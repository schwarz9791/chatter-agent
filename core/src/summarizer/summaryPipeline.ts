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
import { toSpeechSentences } from "../text/speechText";
import { findCommandPath } from "../core/commandPath";
import { buildSummaryArgs, runClaudeCli } from "./claudeCli";
import { SUMMARY_INSTRUCTION, SUMMARY_MAX_CHARS } from "./prompt";
import type { Summarize, SummaryOutcome } from "./types";

/**
 * 要約として採用してよいか。**純粋関数。**
 *
 * ★ 同期の pipeline（ここ）と非同期のプレビュー（`summaryPreview.ts`）が
 *   **同じ規則を見る**ために切り出してある。片方だけ直すと、設定パネルの
 *   「テスト要約」が通るのに本番では原文が読み上げられる（またはその逆）という、
 *   いちばん切り分けにくいズレになる。
 *
 * ★ 上限を `SUMMARY_MAX_CHARS`（120）の2倍にしている根拠は下の判定箇所のコメント参照
 *   （`claude -p` が exit 0 のままレート制限の通知を stdout に出す事故を実測で踏んでいる）。
 *
 * @param spoken 実際に読み上げる形（`toSpeechSentences` を通した後）
 * @param originalLength 比較相手の原文の長さ。**整形済みの長さで比べること**
 */
export function isAcceptableSummary(spoken: string, originalLength: number): boolean {
  return spoken.length > 0 && spoken.length < originalLength && spoken.length <= SUMMARY_MAX_CHARS * 2;
}

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
   * 実測ログへの1行追記。`<ISO時刻>\t<結果>\t<所要ms>\t<原文長>\t<要約長>\t<detail>`。
   *
   * ★ ログの書き込み失敗で発話を止めないこと。要約の判定結果は既に確定しているので、
   *   実測用の窓が壊れているだけで発話そのものには影響させない。
   * ★ ここに来る（＝呼ばれる）のは `isEnabled()` が true かつ閾値を超えたときだけなので、
   *   要約が既定 OFF のままなら `logPath` は1バイトも増えない。
   * ★ このログは issue #31 の完了条件（要約 ON のときの実際の遅延を実測して記録する）のための
   *   窓であり、hook 経路では `console.warn` が `/dev/null` に消えるのでここしか実測の術が無い。
   *   実測が終わったら消してよい（ローテートは持たせていない）。
   * ★ D1(b)（issue #38 レビュー）: 6列目 `detail` を追加した。`claudeCli.ts` が拾った stderr の
   *   抜粋（timeout/overflow/error のときだけ持つ）を渡す。ここが空のままだと、本物の CLI 失敗
   *   （OAuth トークン切れ、フラグ拒否）の原因が「1行のログ」から追えなくなる（上の★のとおり、
   *   hook 経路ではこのログ以外に手がかりが残らない）。改行・タブは1行に収まるよう潰す。
   * ★★ 既存の列（1〜5列目）は動かさないこと。`scripts/verify-phase-a.sh` が `split("\t")[1]`
   *   で2列目（outcome）を見ている。6列目の追加は影響しないが、変更したら
   *   `npm run verify:phase-a` で確認すること。
   */
  function log(
    outcome: SummaryOutcome,
    startedAt: number,
    textLength: number,
    summaryLength: number,
    detail = "",
  ): void {
    try {
      const elapsedMs = now() - startedAt;
      const safeDetail = detail.replace(/\s+/g, " ");
      const line = `${new Date(now()).toISOString()}\t${outcome}\t${elapsedMs}\t${textLength}\t${summaryLength}\t${safeDetail}\n`;
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
        log(result.reason, startedAt, text.length, 0, result.detail ?? "");
        return text;
      }

      // 6. 成功。★ A2（issue #38 レビュー）: 返すのは素の stdout。ここで整形して返すと、
      //   worker.ts 側でもう一度整形されることになり、cleanTextForSpeech の非冪等性
      //   （バッククォートで囲まれた見出し・リスト・表が2パス目で消える）を踏む。
      //   整形は worker.ts の summarizeSentences（toSpeechSentences 呼び出し。＝通常の
      //   発話経路と同じ1箇所）に集約する
      const summary = result.stdout.trim();

      // 7. 妥当性の判定は「実際に読み上げる文字列」で行う。比較相手の text は整形済み
      //   （worker.ts の sentences.join("\n")）なので、素の Markdown 長で比べると不公平になる。
      //   ★ ここで作った spoken は**捨てる**（計測用）。発話に載るのは上の summary で、
      //   整形は worker.ts の summarizeSentences の toSpeechSentences（＝通常の発話経路と
      //   同じ1箇所）が行う
      const spoken = toSpeechSentences(summary).join("\n");

      // ★ A1（issue #38 レビュー）: 「空でない かつ 原文より短い」だけでなく、上限文字数も見る。
      //   `claude -p` が exit 0 のままレート制限の通知（`Claude usage limit reached...`）や
      //   拒否文を stdout に出すと、これまではそれがそのまま要約として採用されていた
      //   （原文はどこにも残らない。実測で 744文字 → 335文字が ok で通り、そのまま読み上げられた）。
      //   SUMMARY_MAX_CHARS（120）の2倍（240文字）にする根拠: 実測の ok 7件の要約長は
      //   65 / 80 / 82 / 102 / 116 / 118 / 335 で、240 で切ると外れ値の335だけが弾かれ他は
      //   全部通る。これより厳しくすると、数十秒待った末に invalid で原文フォールバックする
      //   回数が増え、遅延だけ払って効果ゼロになる。
      if (!isAcceptableSummary(spoken, text.length)) {
        log("invalid", startedAt, text.length, spoken.length);
        return text;
      }

      // 8. 採用
      log("ok", startedAt, text.length, spoken.length);
      return summary;
    } catch (err) {
      // ★ D1(a)（issue #38 レビュー）: ここは "error"（CLI が非ゼロ終了、または起動自体に
      //   失敗した）とは別の outcome にする。ここで拾うのは execFileSync に到達すらしていない
      //   例外（getter の実装ミス、registerSessionId の失敗＝ディスクフルや読み取り専用 FS など、
      //   pipeline 内部の例外）。"error" のまま記録すると、運用者は「claude CLI が壊れている」と
      //   結論し、真の障害（無限ループ防止の第2層が黙って無効になっている等）に辿り着けない。
      const detail = err instanceof Error ? err.message : String(err);
      log("internal", startedAt, text.length, 0, detail);
      return text;
    }
  };
}
