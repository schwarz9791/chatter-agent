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

/** `speech.jsonl` の1行。1文1行。 */
export interface SpeechRecord {
  /**
   * 単一ワーカーがロック下で採番する。**配信キューのファイル名であり、ack のキー**。
   * 次の値は `speech.state.json` に持つのでローテートしても連続する。
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
