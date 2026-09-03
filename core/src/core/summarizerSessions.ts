/**
 * 要約 CLI に渡した `--session-id` の**共有**レジストリ（無限ループ防止の第2層）。
 *
 * 第2層そのものの説明は `cli/workerState.ts` の `summarizerSessionIds` にある。ここは
 * **CLI 以外のプロセスが要約を起こすようになったこと**（#76 の `POST /v1/summary/preview`）で
 * 必要になった、その置き場所の話。
 *
 * ★★ **`worker.state.json` に相乗りさせないこと。** あのファイルは CLI が
 *   「ドレインの先頭で読み、途中と末尾で全体を書き戻す」形で使っている。ロックの外に居る
 *   サーバーが同じファイルを read-modify-write すると、**CLI の tombstone
 *   （`publishedMessageIds`）を巻き添えで消しうる** —— 症状は「同じメッセージを2回喋る」で、
 *   要約のテストボタンを押しただけで起きる。しかも再現条件がタイミングなので追えない。
 *
 * ★ **CLI のロックを取りに行くのも駄目。** あのロックはドレイン全体（要約中は数十秒、
 *   上限は `aiSummaryTimeoutMs × aiSummaryMaxPerDrain`）保持される。テストボタン1つのために
 *   そこまで待つか、待たずに諦めるかの二択になる。
 *
 * → **ファイルを分けて「書き手を1人だけ」にする。** このファイルを書くのは
 *   `chatter-agent-server` だけ、`worker.state.json` を書くのは `chatter-agent-speak` だけ。
 *   CLI は両方を**読んで** or で判定する（`worker.ts` の第2層）。
 *   書き手が1人なら read-modify-write の競合が原理的に起きない。
 */

import * as fs from "fs";
import { writeFileAtomic } from "./atomicWrite";

/**
 * 覚えておく件数。
 *
 * ★ `workerState.ts` の `SUMMARIZER_SESSION_LIMIT`（64）と揃える必要は無い。あちらは
 *   「1ドレインで最大 `aiSummaryMaxPerDrain` 件 × 何ドレイン分」という計算で決まっているが、
 *   こちらの書き手は**人がボタンを押したとき**だけで、しかもレート制限（同時1本 + 最短1秒）が
 *   掛かっている。16 あれば、押し続けても直近の分は必ず残る。
 */
const LIMIT = 16;

/** 記録されている session_id。読めなければ空（抑制が1回効かないだけ） */
export function readSummarizerSessions(filePath: string): string[] {
  try {
    const parsed: unknown = JSON.parse(fs.readFileSync(filePath, "utf-8"));
    if (!Array.isArray(parsed)) return [];
    // ★ 途中に非文字列が混ざっていても配列ごと捨てないこと。ここは許可リストではなく
    //   抑制リストなので、読めた分だけでも効かせた方が安全側に倒れる
    return parsed.filter((item): item is string => typeof item === "string").slice(-LIMIT);
  } catch {
    // 無い・壊れているのは異常ではない
    return [];
  }
}

/**
 * `sessionId` を記録する。**要約 CLI を起動する前に呼ぶ契約**（→ `summarizer/types.ts`）。
 *
 * ★ **throw を握らないこと。** 呼び出し側（`summaryPipeline` が返す関数を包む catch）が
 *   拾って原文フォールバックになり、要約 CLI が**起動しない**。「登録できなければ起こさない」が
 *   第2層の安全側の挙動で、`cli/worker.ts` の `registerSessionId` が意図的に
 *   try/catch を付けていないのと同じ理由。
 */
export function registerSummarizerSession(filePath: string, sessionId: string): void {
  const sessions = readSummarizerSessions(filePath);
  sessions.push(sessionId);
  writeFileAtomic(filePath, `${JSON.stringify(sessions.slice(-LIMIT))}\n`);
}
