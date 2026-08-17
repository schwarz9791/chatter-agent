/**
 * 要約の判定とフォールバック。cc-mascot の `createSummaryPipeline` を同期化したもの。
 *
 * ★ 移植元との最大の違いは同期実行であること。呼び出し元の `drainSpool`（`core/src/cli/worker.ts`）
 *   は完全に同期で、**単一ワーカーのロックが直列化を担っている**ので、移植元にあった
 *   セマフォ（同時実行数の制限）はここでは不要。CLI は hook からデタッチ起動される単発プロセス
 *   なので、`execFileSync` でブロックして構わない（`worker.ts` の `acquireLockWithRetry` も
 *   既に `Atomics.wait` で同期スリープする設計になっている）。
 *
 * ★ 移植元の「滞留ガード」（`semaphore.waiting >= maxWaiting` でスキップ）は、同時実行の概念が
 *   無くなったのでそのままは持ち込めない。ここでは「**1回のドレインで要約してよい回数の上限**」
 *   （`getMaxPerDrain`）に読み替えてある。CLI が長時間動かなかった後のドレインでは長文が
 *   複数溜まっていることがあり、全部要約すると `timeoutMs × N` だけ発話が遅れるため。
 */

import { randomUUID } from "crypto";
import * as fs from "fs";
import { cleanTextForSpeech } from "../text/textFilter";
import { buildSummaryArgs, findCommandPath, runClaudeCli } from "./claudeCli";
import { SUMMARY_INSTRUCTION } from "./prompt";
import type { Summarize, SummaryOutcome } from "./types";

export interface SummaryPipelineDeps {
  /** 機能が有効か */
  isEnabled: () => boolean;
  /** 長文判定の閾値（文字数） */
  getThreshold: () => number;
  /** 要約1回の上限。超えたら要約を諦めて原文を返す */
  getTimeoutMs: () => number;
  /** 1回のドレインで要約してよい回数の上限（上のヘッダ参照） */
  getMaxPerDrain: () => number;
  /** 要約に使う CLI。絶対パスも可 */
  getCommand: () => string;
  /** `--model` に渡す値。空文字なら渡さない */
  getModel: () => string;
  /**
   * 要約 CLI の cwd（隔離ディレクトリ）。
   * ★ `config.ts` は参照のたびに mtime を見て読み直す作りなので、上の6つは**値ではなく
   *   getter で受ける**。ここだけ値で受けているのは、隔離ディレクトリのパス自体は設定変更で
   *   動かないランタイム上のパス（`core/paths.ts` の `getSummarizerHomeDir()`）で、
   *   `config.json` 由来ではないため
   */
  homeDir: string;
  /** 実測ログの追記先（下の `log` 参照）。同じ理由で値で受ける */
  logPath: string;
  /** テスト用。既定 `Date.now` */
  now?: () => number;
}

/**
 * `Summarize` を作るファクトリ。
 *
 * ★ `maxPerDrain` のカウンタはこの関数のクロージャ内（インスタンス変数）で持つ。
 *   `chatter-agent-speak` は hook から毎 delta 起動される単発プロセスで、1回の起動が
 *   1回の `drainSpool` 呼び出しに対応する。このファクトリもプロセスごとに1回だけ呼ばれるので、
 *   インスタンス変数で持つだけで自然に「1ドレインあたり」の意味になる
 *   （プロセスをまたいで永続化する必要が無い＝次のドレイン＝次のプロセス＝カウンタは0から）。
 */
export function createSummaryPipeline(deps: SummaryPipelineDeps): Summarize {
  const now = deps.now ?? Date.now;
  let summarizedCount = 0;

  /**
   * 実測ログへの1行追記。`<ISO時刻>\t<結果>\t<所要ms>\t<原文長>\t<要約長>`。
   *
   * ★ ログの書き込み失敗で発話を止めないこと。要約の判定結果は既に確定しているので、
   *   実測用の窓が壊れているだけで発話そのものには影響させない。
   * ★ ここに来る（＝呼ばれる）のは `isEnabled()` が true かつ閾値を超えたときだけなので、
   *   要約が既定 OFF のままなら `logPath` は1バイトも増えない。
   * ★ このログは issue #31 の完了条件（要約 ON のときの実際の遅延を実測して記録する）のための
   *   窓であり、hook 経路では `console.warn` が `/dev/null` に消えるのでここしか実測の術が無い。
   *   実測が終わったら消してよい（ローテートは持たせていない）。
   */
  function log(outcome: SummaryOutcome, startedAt: number, textLength: number, summaryLength: number): void {
    try {
      const elapsedMs = now() - startedAt;
      const line = `${new Date(now()).toISOString()}\t${outcome}\t${elapsedMs}\t${textLength}\t${summaryLength}\n`;
      fs.appendFileSync(deps.logPath, line);
    } catch {
      // 上のヘッダ参照。書けなくても発話は止めない
    }
  }

  return (text, registerSessionId) => {
    const startedAt = now();

    // ★ 本体を丸ごと try で包む。個々の経路は例外を投げないように書いてあるが、
    //   「throw しない」は型（types.ts の Summarize）で宣言した**契約**なので、実装側で
    //   保証しておく（移植元の `process()` も同じ形で全体を包んでいた）。ここが漏れると
    //   要約の失敗ごときで `processMessage` が publish の手前で抜け、spool も消えず
    //   tombstone も付かないまま、そのメッセージが二度と発話されない状態になりうる。
    try {
      // 1. 無効 → 原文
      if (!deps.isEnabled()) return text;

      // 2. 閾値以下 → 原文
      if (text.length <= deps.getThreshold()) return text;

      // 3. このインスタンス（＝このドレイン）で要約した回数が上限に達している → 原文
      if (summarizedCount >= deps.getMaxPerDrain()) {
        log("skipped-limit", startedAt, text.length, 0);
        return text;
      }

      // 4. コマンドが見つからない → 原文
      const commandPath = findCommandPath(deps.getCommand());
      if (!commandPath) {
        log("no-command", startedAt, text.length, 0);
        return text;
      }

      // ここから実際に CLI を起動する。起動を決めた時点でカウントする
      // （タイムアウトや失敗に終わっても、時間を消費した実行として上限に数える）
      summarizedCount++;

      // ★ execFileSync する前に呼ぶこと（types.ts の Summarize の契約）。要約中に親プロセスが
      //   落ちても、このセッションIDは既に呼び出し側（workerState）へ永続化されている
      const sessionId = randomUUID();
      registerSessionId(sessionId);

      const args = buildSummaryArgs(SUMMARY_INSTRUCTION, { sessionId, model: deps.getModel() });
      const result = runClaudeCli({
        commandPath,
        args,
        text,
        homeDir: deps.homeDir,
        timeoutMs: deps.getTimeoutMs(),
      });

      // 5. 実行 → 失敗/タイムアウト → 原文
      if (!result.ok) {
        log(result.reason, startedAt, text.length, 0);
        return text;
      }

      // 6. 成功 → 再クリーニング（★ CLI が Markdown 等を返した場合の保険。移植元と同じ）
      const summary = cleanTextForSpeech(result.stdout).trim();

      // 7. 空 or 原文以上の長さ → 不採用（「短くならなかった要約を採用しない」妥当性検証）
      if (!summary || summary.length >= text.length) {
        log("invalid", startedAt, text.length, summary.length);
        return text;
      }

      // 8. 採用
      log("ok", startedAt, text.length, summary.length);
      return summary;
    } catch {
      // 想定していない例外（getter の実装ミス、fs の権限、CLI 名の異常値など）。
      // 原文を返して発話を続ける。`log` 自身も内部で例外を握るので、ここから再度漏れない
      log("error", startedAt, text.length, 0);
      return text;
    }
  };
}
