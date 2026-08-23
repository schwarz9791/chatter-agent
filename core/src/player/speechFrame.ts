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

import { isAudioPath } from "../core/audioPath";
import { isValidEpoch, type AudioRef, type Emotion, type SpeechFrame, type SpeechKind } from "../core/types";

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
 * 読めたら `SpeechFrame`、読めなければ null。
 *
 * 必須なのは `epoch` / `seq`（非負の安全整数）/ `text`（文字列）/ `ts`（文字列）の4つ。
 * `epoch` を必須にしているのは、重複排除のキーが `seq` 単独では足りないため
 * （採番はランタイムルートの作り直しで 1 に戻る → `core/types.ts` の `SpeechEpoch`）。
 *
 * `audio` は**読めなければ null に倒す**。音声が無いフレームは「鳴らさずに ack する」
 * という正常な経路があるので、フレームごと捨てる理由が無い。
 */
export function parseSpeechFrame(raw: string): SpeechFrame | null {
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }

  if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) return null;
  const r = parsed as Record<string, unknown>;

  // ★ `isValidEpoch` の charset から外れる値を通さないこと。この値は
  //   一時ファイル名と音声の URL の材料になる（→ core/types.ts の SpeechEpoch）
  const epoch = r.epoch;
  if (!isValidEpoch(epoch)) return null;

  // ★ 1 始まり。0 を通さないこと。`speechLog` の採番は 1 からで、0 は
  //   `playbackQueue` の水位の初期値（未受信）と衝突する。seq 0 のフレームは
  //   初期状態で「seq が戻った」と読まれて余計な resetEpoch を起こすうえ、
  //   `dispatcher.ack` が 0 を no-op として扱うのでそのキューは永久に消えない
  const seq = r.seq;
  if (typeof seq !== "number" || !Number.isSafeInteger(seq) || seq < 1) return null;

  const text = r.text;
  if (typeof text !== "string") return null;

  const ts = r.ts;
  if (typeof ts !== "string" || ts === "") return null;

  const kind = typeof r.kind === "string" && KNOWN_KINDS.has(r.kind) ? (r.kind as SpeechKind) : "assistant";
  const emotion = typeof r.emotion === "string" && KNOWN_EMOTIONS.has(r.emotion) ? (r.emotion as Emotion) : "neutral";

  return {
    epoch,
    seq,
    ts,
    audio: parseAudio(r.audio),
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
 * フレームに `audio` キーが**載っていない**（＝ #29 より前のサーバー）。
 *
 * ★ **`"audio": null` と別物として扱うこと。** `parseAudio` はどちらも null に潰すが、
 *   ワイヤ上では区別できる:
 *
 *   - `ttsEnabled: false` / 読み上げる中身が無い文 → サーバーは `"audio": null` を**明示的に載せる**
 *     （`server/dispatcher.ts` の `buildFrame` は `{ ...record, audio }` を stringify する）
 *   - #29 より前のサーバー → `audio` キーが**存在しない**
 *
 *   潰したままだと、後者は前者と区別なく**全文が無言で ack され、どちらの側にも1行も出ない**。
 *   キーの欠落だけを見るので、**`ttsEnabled: false` では原理的に発火しない**
 *   （そこで発火すると、正常な設定に対して消す手段の無い警告が出続ける）。
 *
 * 読めない JSON は false。フレームごと捨てる経路が別に警告するので、ここで二重に出さない。
 */
export function isAudioUndeclared(raw: string): boolean {
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return false;
  }
  return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed) && !("audio" in parsed);
}

/**
 * 音声の参照。読めなければ null（＝鳴らさずに ack する）。
 *
 * ★ **絶対 URL を通さないこと。** 任意の URL を受け入れると、サーバーが
 *   クライアントを任意の外部ホストへ向かわせられる。`/audio/<epoch>-<seq>.wav` の形だけを通す。
 */
function parseAudio(raw: unknown): AudioRef | null {
  if (typeof raw !== "object" || raw === null) return null;
  const { path, format } = raw as { path?: unknown; format?: unknown };
  if (!isAudioPath(path)) return null;
  if (format !== "wav") return null;
  return { path, format };
}
