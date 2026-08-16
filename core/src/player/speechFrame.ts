/**
 * サーバーから届いたテキストフレームを `SpeechRecord` として読む。
 *
 * ★ `server/wsServer.ts` の `parseAck` と対称の役目。サーバーは「`SpeechRecord` の JSON 以外は
 *   送らない」契約だが、**受け取る側でも通す値を絞る**。`seq` は Map のキーであり ack の値であり
 *   一時ファイル名の材料でもあるので、ここが緩いと `speechQueue.read()` がファイル名と payload の
 *   seq を照合しているのと同じ穴が player 側に開く。
 *
 * ★ 読めないフレームは**警告して捨てる。接続は切らない**（parseAck が知らない形を黙って
 *   捨てるのと同じ扱い）。1フレームの不整合でストリーム全体を落とす理由が無い。
 */

import type { Emotion, SpeechKind, SpeechRecord } from "../core/types";

/** `docs/protocol.md`: 未知の `kind` は `assistant` として扱う */
const KNOWN_KINDS = new Set<string>(["assistant", "prompt"] satisfies SpeechKind[]);

/** VRM の標準 expression 名と一対一。未知なら中立に倒す */
const KNOWN_EMOTIONS = new Set<string>([
  "neutral",
  "happy",
  "angry",
  "sad",
  "relaxed",
  "surprised",
] satisfies Emotion[]);

/** 取れなかった識別子は null（`SpeechRecord` の契約どおり）。文字列以外も null に丸める */
function optionalString(raw: unknown): string | null {
  return typeof raw === "string" ? raw : null;
}

/**
 * 読めたら `SpeechRecord`、読めなければ null。
 *
 * 必須なのは `seq`（非負の安全整数）/ `text`（文字列）/ `ts`（文字列）の3つだけ。
 * `ts` を必須にしているのは、重複排除のキーが `seq` 単独では足りないため
 * （→ `playbackQueue.ts` の「エポック」の項）。
 */
export function parseSpeechFrame(raw: string): SpeechRecord | null {
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }

  if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) return null;
  const r = parsed as Record<string, unknown>;

  const seq = r.seq;
  if (typeof seq !== "number" || !Number.isSafeInteger(seq) || seq < 0) return null;

  const text = r.text;
  if (typeof text !== "string") return null;

  const ts = r.ts;
  if (typeof ts !== "string" || ts === "") return null;

  const kind = typeof r.kind === "string" && KNOWN_KINDS.has(r.kind) ? (r.kind as SpeechKind) : "assistant";
  const emotion = typeof r.emotion === "string" && KNOWN_EMOTIONS.has(r.emotion) ? (r.emotion as Emotion) : "neutral";

  return {
    seq,
    ts,
    // 将来 別のプロデューサーが増えても、player は誰が書いたかで挙動を変えない
    source: "claude-code",
    sessionId: optionalString(r.sessionId),
    turnId: optionalString(r.turnId),
    messageId: optionalString(r.messageId),
    kind,
    text,
    emotion,
  };
}

/**
 * 合成に出す意味のあるテキストか。
 *
 * `docs/core.md`「既知の欠落」にあるとおり、文分割は約物だけの発話（`すごい！！` →
 * `["すごい！", "！"]` の後半）を作ることがある。これを `/audio_query` に投げると
 * 空の WAV か 4xx が返るので、合成を試みる前にここで落とす。
 *
 * 判定は「音になる文字が1つでもあるか」。約物・空白・制御文字しか無ければ false。
 */
export function hasSpeakableText(text: string): boolean {
  // \p{L} 文字 / \p{N} 数字 / \p{S} 記号（℃ や € など読まれうるもの）
  return /[\p{L}\p{N}\p{S}]/u.test(text);
}
