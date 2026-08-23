/**
 * chatter-agent 全体の契約。
 *
 * 記録（`speech.jsonl`）と配信キュー（`speech/<seq>.json`）は分かれているが、どちらも
 * この型のオブジェクト1件を1行 / 1ファイルとして持つ。配信キューの1ファイルがそのまま
 * WebSocket の1フレームになるので、記録・キュー・配信の3箇所で変換は要らない。
 *
 * 契約の詳細（キューの形、WebSocket、ack）は docs/protocol.md を正とする。
 * 設計書 §5 は元の一次情報だが、記録と配信を分けた形には追従していない。
 */

/**
 * VRM の標準 expression 名と一対一で対応する。
 *
 * cc-mascot 由来の `emotion/ruleBasedEmotionClassifier.ts` にも同じ union が定義されているが、
 * 契約はこちらを正とし、あちらはここから import する。
 */
export type Emotion = "neutral" | "happy" | "angry" | "sad" | "relaxed" | "surprised";

/**
 * 発話の種別。
 * - `assistant`: 通常の発言
 * - `prompt`: 応答待ち通知（質問・計画承認・許可プロンプト）
 *
 * 受信側は**未知の kind を `assistant` として扱う**こと。
 */
export type SpeechKind = "assistant" | "prompt";

/** 発話を生んだプロデューサー。将来 Codex 等が同じログに書ける余地を残してある */
export type SpeechSource = "claude-code";

/**
 * 採番の世代。`seq` は**この中でしか一意でない**。
 *
 * ランタイムルート（または `speech.state.json` と `speech.jsonl` の両方）が消えると
 * CLI の採番は 1 に戻る。`seq` だけを覚えている受信側は、そこで「もう喋った」と誤判定して
 * **何百文でも一切喋らなくなる**（エラーも出ない）。以前はこれを `(seq, ts)` の組から
 * 推論させていたが、推論はクライアントごとに約40行の状態機械を要求した（→ #29）。
 *
 * **採番のやり直しと一対一。** 採番が続くなら同じ値のまま、やり直されたら別の値になる。
 * 比較は**等値だけ**で、順序も暗号学的な性質も要求しない。
 *
 * ★ **URL の一部になる**（`/audio/<epoch>-<seq>.wav`）。`isValidEpoch` の charset から
 *   外れる値を作らないこと。
 */
export type SpeechEpoch = string;

/** `epoch` として通す形。**パスと URL に載るので、ここを緩めないこと** */
const EPOCH_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;

export function isValidEpoch(value: unknown): value is SpeechEpoch {
  return typeof value === "string" && EPOCH_PATTERN.test(value);
}

/**
 * `epoch` がまだ無かった頃に書かれた記録・配信キュー entry に与える値。
 *
 * ★ **`epoch` が読めないからといってランダムな値を生成しないこと。** 生成すると
 *   **アップグレードした瞬間に「採番がやり直された」と読まれる**。サーバーは旧 epoch の
 *   entry を配信しなくなり、CLI は次の publish でキューを空にするので、in-flight の発話が
 *   丸ごと消える。採番が続いている以上、epoch も続いていると見なすのが正しい。
 *
 * ★ **1箇所で定義すること。** 記録側（`core/speechLog.ts` の `reconcile`）と
 *   キュー側（`core/speechQueue.ts` の `read`）が別々の値を使うと、
 *   「ログ由来の legacy」と「キュー由来の legacy」が別世代として扱われ、
 *   ここで防ごうとしているバグをそのまま再生産する。
 */
export const LEGACY_EPOCH: SpeechEpoch = "legacy";

/** `speech.jsonl` の1行。1文1行。 */
export interface SpeechRecord {
  /** 採番の世代。→ `SpeechEpoch` */
  epoch: SpeechEpoch;
  /**
   * 単一ワーカーがロック下で採番する。**配信キューのファイル名であり、ack のキー**。
   * 次の値は `speech.state.json` に持つのでローテートしても連続する。
   *
   * ★ **`epoch` を跨いで一意ではない。** 単独でキーにしないこと（→ `SpeechEpoch`）。
   *
   * ファイル名の seq と payload の seq は必ず一致するものとしてよい。
   * `speechQueue.read()` がこれを照合し、食い違ったら配信しない（docs/protocol.md）。
   */
  seq: number;
  /** ISO8601 */
  ts: string;
  source: SpeechSource;
  sessionId: string | null;
  turnId: string | null;
  messageId: string | null;
  kind: SpeechKind;
  /** 1文。Markdown 除去済み */
  text: string;
  emotion: Emotion;
}

/**
 * cc-mascot 由来コードが受け渡しに使う発話メッセージ。
 *
 * 上流では `electron/adapters/harnessAdapter.ts` に定義されているが、`adapters/` は
 * 移植しない（対象が Claude Code のみでログ形式の抽象化が不要）ため、ここで定義し直す。
 */
export interface SpeakMessage {
  type: "speak";
  text: string;
  emotion?: Emotion;
  kind?: SpeechKind;
}
