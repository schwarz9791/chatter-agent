/**
 * 音声合成エンジンの抽象。`voicevoxClient.ts` / `openaiClient.ts` はどちらもこの形を実装する
 * （→ `server/index.ts` の `ttsFor` がエンジン種別で差し替える）。
 *
 * ★ **診断まわりのヘルパーをここへ集める。** タイムアウト・接続失敗の言い換え（`describe` /
 *   `explain`）・WAV かどうかの検査（`looksLikeWav`）・エラー型はエンジンの種類に関わらず同じ形で
 *   要る。エンジンごとに複製すると、片方だけ直して片方が古いままという分裂が起きる。
 */

export interface TtsEngine {
  readonly baseUrl: string;
  /** 1文ぶんの WAV */
  synthesize(text: string): Promise<ArrayBuffer>;
  /** 声の一覧。起動時の疎通確認と、`ttsSpeakerId` の検査に使う */
  listVoices(): Promise<{ id: string; label: string }[]>;
  /**
   * `ttsSpeakerId` が `voices` の中に実在するか。**エンジンごとに指定の書き方が違うので、
   * 一致の取り方はエンジン自身が知っている。** `voicevoxClient` は完全一致だが、
   * `openaiClient`（Kokoro-FastAPI）は `af_bella+af_sky` のような合成指定を受け付けるため、
   * `index.ts` 側で共通の完全一致を書くと存在するのに「無い」と誤診断する。
   */
  hasVoice(voices: { id: string; label: string }[], id: string): boolean;
}

export interface TtsEngineOptions {
  baseUrl: string;
  voiceId: string;
  /**
   * 話速。省略の扱いはエンジンごとに決める（→各クライアントの docstring）。
   *
   * ★ 範囲 0.5〜2.0 は**エンジンの受理範囲ではなく実用の範囲**（→ `core/config.ts` の
   *   `ttsSpeedScale`）。
   */
  speedScale?: number;
  /**
   * **1リクエストあたり**の上限。
   *
   * ★ 省略できない。Node の `fetch` に既定のタイムアウトは無いので、エンジンが応答を返さず
   *   TCP を保持し続けると、その文が固まる。head-of-line blocking なので
   *   **以後すべてが無音になり、しかもエラーが1行も出ない**。
   */
  timeoutMs: number;
}

/**
 * エンジンが**応答した**うえでのエラー。`status` と応答本文の先頭を持つ。
 *
 * ★ **本文を捨てないこと。** `ttsSpeakerId` を間違えたときにエンジンが返す 4xx の本文には、
 *   何が悪いかが `detail` として入っている。ここを落とすと、症状（無音）から原因へ
 *   辿る手がかりがサーバーのログに1つも残らない。
 *
 * ★ これを見て「恒久的だから諦める」と決めないこと。線引きは実質不可能で、
 *   モデルロード中の 4xx・`ttsBaseUrl` のパス違いで別サービスが返す 404/405・
 *   プロキシの 407 まで巻き込む。諦めると `ackUpTo` が本文を**物理削除**するので、
 *   設定を直しても復元できない（→ `server/audioStore.ts`）。
 */
export class TtsHttpError extends Error {
  readonly status: number;
  readonly detail: string;
  constructor(op: string, status: number, detail: string) {
    super(detail ? `${op} が ${status} を返しました: ${detail}` : `${op} が ${status} を返しました`);
    this.name = "TtsHttpError";
    this.status = status;
    this.detail = detail;
  }
}

/** エンジンに**届かなかった / 応答が返らなかった**エラー */
export class TtsTransportError extends Error {
  constructor(message: string, options?: { cause?: unknown }) {
    super(message, options);
    this.name = "TtsTransportError";
  }
}

/** `TtsHttpError.detail` に載せる本文の上限。原因が分かれば足りる */
export const DETAIL_MAX_CHARS = 512;

/**
 * どの段階で落ちたかを残す。ログの1行から原因に辿り着けるように。
 *
 * ★ **`cause` を辿ること。** undici の接続失敗は `TypeError: fetch failed` としか名乗らず、
 *   `ECONNREFUSED` / アドレス / ポートは `err.cause` にしか入っていない。
 *   ここを `String(err)` で潰すと、ログが「fetch failed」だけになり
 *   **どこへ繋ごうとして何が起きたのかが1文字も出ない**。
 */
export function describe(op: string, err: unknown): TtsTransportError {
  if (err instanceof Error && (err.name === "TimeoutError" || err.name === "AbortError")) {
    return new TtsTransportError(`${op} がタイムアウトしました`, { cause: err });
  }
  return new TtsTransportError(`${op} に失敗しました: ${explain(err)}`, { cause: err });
}

/**
 * `TypeError: fetch failed` の下にある本当の理由（`ECONNREFUSED` など）まで降りる。
 *
 * ★ `AggregateError` も開くこと。ホスト名が複数のアドレスに解決されると
 *   （`localhost` → `::1` と `127.0.0.1`）、undici は**メッセージが空の** `AggregateError` を
 *   cause に置き、実際のアドレスとポートは `errors[]` の中にしか無い。
 */
export function explain(err: unknown): string {
  const parts: string[] = [];
  let current: unknown = err;

  for (let depth = 0; depth < 4 && current instanceof Error; depth++) {
    const label = describeOne(current);
    if (label) parts.push(label);

    const { errors } = current as { errors?: unknown };
    if (Array.isArray(errors)) {
      for (const sub of errors.slice(0, 2)) {
        if (sub instanceof Error) parts.push(describeOne(sub));
      }
    }
    current = (current as { cause?: unknown }).cause;
  }
  return parts.filter(Boolean).join(" ← ");
}

export function describeOne(err: Error): string {
  const { code, address, port } = err as { code?: unknown; address?: unknown; port?: unknown };
  const where = typeof address === "string" ? ` (${address}${typeof port === "number" ? `:${port}` : ""})` : "";
  const head = typeof code === "string" ? `${code} ` : "";
  return `${head}${err.message}${where}`.trim();
}

/** RIFF/WAVE のマジックだけ見る。中身の妥当性までは見ない */
export function looksLikeWav(buffer: ArrayBuffer): boolean {
  if (buffer.byteLength < 12) return false;
  const head = new Uint8Array(buffer, 0, 12);
  const tag = (offset: number) =>
    String.fromCharCode(head[offset]!, head[offset + 1]!, head[offset + 2]!, head[offset + 3]!);
  return tag(0) === "RIFF" && tag(8) === "WAVE";
}

/**
 * `timeoutMs` を1つ握った `request()` を作る。**エンジンクライアントの共通ヘルパー。**
 *
 * ★ **ボディを読むこと。** 目的は接続の再利用ではなく**診断**。`ttsSpeakerId` を
 *   間違えたときの 4xx の本文には、何が悪いかが入っている。ここを落とすと、
 *   症状（無音）から原因へ辿る手がかりがログに1つも残らない。
 *
 * ★ 読み取りにも `AbortSignal.timeout` が効くので、止まった本文で固まることはない。
 */
export function createRequester(timeoutMs: number) {
  return async function request(op: string, url: string, init: RequestInit): Promise<Response> {
    let res: Response;
    try {
      res = await fetch(url, { ...init, signal: AbortSignal.timeout(timeoutMs) });
    } catch (err) {
      throw describe(op, err);
    }
    if (!res.ok) {
      const detail = await res
        .text()
        .then((body) => body.trim().slice(0, DETAIL_MAX_CHARS))
        .catch(() => "");
      throw new TtsHttpError(op, res.status, detail);
    }
    return res;
  };
}
