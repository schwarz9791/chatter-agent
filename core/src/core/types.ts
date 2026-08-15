/**
 * chatter-agent 全体の契約。
 *
 * `speech.jsonl` の1行がそのまま WebSocket で配信されるので、ログ形式と配信形式は同一。
 * 設計書 §5 が一次情報。
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
   * 単一ワーカーがロック下で採番する。再接続時の欠落検出に使う。
   * 次の値は `speech.state.json` に持つのでローテートしても連続する。
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
