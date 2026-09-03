/**
 * 音声合成エンジンのクライアント。AivisSpeech / VOICEVOX の互換 API を叩く。
 *
 * `POST /audio_query?text=…&speaker=<id>` で読み仮名とアクセントの入ったクエリを作り、
 * それをそのままボディに載せて `POST /synthesis?speaker=<id>` に投げると WAV が返る。
 * cc-mascot も同じ2段構えだが、こちらは公開されている API 仕様を見て書いてある
 * （そのため cc-mascot 由来の帰属表示は付かない → docs/origin.md）。
 *
 * ★ `AudioQuery` の中身は解釈しない。エンジンが返した JSON をそのまま返送するのが
 *   最も安全（エンジンのバージョン差に強い）。
 *
 * ★★ **例外は `speedScale` だけ**（#76 で入った `ttsSpeedScale`。→ `applySpeedScale`）。
 *   合成し直さないと話速は変えられず、再生側で伸縮するとリップシンク（#58）が
 *   WAV から作ったエンベロープとズレるため、ここで触る以外の道が無い。
 *   **例外をここ1つに留めること** —— `pitchScale` / `intonationScale` を同じ理屈で
 *   足していくと、「そのまま返送する」という一番安全な形が失われる。
 */

/** エンジンが返す読み仮名クエリ。中身は解釈せずそのまま返送する（例外は `applySpeedScale`） */
export type AudioQuery = Record<string, unknown>;

export interface SpeakerStyle {
  id: number;
  name: string;
}

export interface Speaker {
  name: string;
  speaker_uuid: string;
  styles: SpeakerStyle[];
}

export interface VoicevoxClientOptions {
  baseUrl: string;
  speakerId: number;
  /**
   * **1リクエストあたり**の上限。`audio_query` と `synthesis` にそれぞれ効く。
   *
   * ★ 省略できない。Node の `fetch` に既定のタイムアウトは無いので、エンジンが応答を返さず
   *   TCP を保持し続けると、その文が固まる。head-of-line blocking なので
   *   **以後すべてが無音になり、しかもエラーが1行も出ない**。
   *
   * ★ **2往復で1つの予算にしないこと。** 一見「合計を抑えられて良い」に見えるが、
   *   AivisSpeech は初回にモデルをロードするので `/audio_query` が予算をほぼ食い切ることがあり、
   *   そうすると **CPU 律速の `/synthesis` に残り0が渡って落ちる**（往復ごとなら通っていた）。
   *   「クライアントが待たされ過ぎる」side は、サーバーが `GET /audio/…` の**応答**を
   *   打ち切ることで抑える（→ `server/httpServer.ts`）。エンジンへの上限とは別の話。
   */
  timeoutMs: number;
  /**
   * 話速。`audio_query` の応答の `speedScale` を書き換える（→ `applySpeedScale`）。
   *
   * ★ **省略は「触らない」。** エンジンの疎通確認（`listSpeakers`）しかしない呼び出し側に
   *   意味の無い速度を書かせないための省略可で、「既定は 1.0」ではない ——
   *   等倍にしたいなら `1.0` を明示して渡すこと（エンジン側の既定が 1.0 とは限らない）。
   */
  speedScale?: number;
}

/**
 * エンジンが**応答した**うえでのエラー。`status` と応答本文の先頭を持つ。
 *
 * ★ **本文を捨てないこと。** `ttsSpeakerId` を間違えたときにエンジンが返す 422 の本文には、
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
const DETAIL_MAX_CHARS = 512;

export interface VoicevoxClient {
  /** 1文ぶんの WAV。`audio_query` → `synthesis` の2往復 */
  synthesize(text: string): Promise<ArrayBuffer>;
  /** 話者の一覧。起動時の疎通確認と、話者 ID の検査に使う */
  listSpeakers(): Promise<Speaker[]>;
  readonly baseUrl: string;
  readonly speakerId: number;
}

/**
 * どの段階で落ちたかを残す。ログの1行から原因に辿り着けるように。
 *
 * ★ **`cause` を辿ること。** undici の接続失敗は `TypeError: fetch failed` としか名乗らず、
 *   `ECONNREFUSED` / アドレス / ポートは `err.cause` にしか入っていない。
 *   ここを `String(err)` で潰すと、ログが「fetch failed」だけになり
 *   **どこへ繋ごうとして何が起きたのかが1文字も出ない**。
 */
function describe(op: string, err: unknown): TtsTransportError {
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
function explain(err: unknown): string {
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

function describeOne(err: Error): string {
  const { code, address, port } = err as { code?: unknown; address?: unknown; port?: unknown };
  const where = typeof address === "string" ? ` (${address}${typeof port === "number" ? `:${port}` : ""})` : "";
  const head = typeof code === "string" ? `${code} ` : "";
  return `${head}${err.message}${where}`.trim();
}

/** RIFF/WAVE のマジックだけ見る。中身の妥当性までは見ない */
function looksLikeWav(buffer: ArrayBuffer): boolean {
  if (buffer.byteLength < 12) return false;
  const head = new Uint8Array(buffer, 0, 12);
  const tag = (offset: number) =>
    String.fromCharCode(head[offset]!, head[offset + 1]!, head[offset + 2]!, head[offset + 3]!);
  return tag(0) === "RIFF" && tag(8) === "WAVE";
}

/**
 * `audio_query` が返した JSON の `speedScale` **だけ**を書き換える。**純粋関数。**
 *
 * ★ **キーを持たないエンジンには何もしない。** 無いところに作ると、そのエンジンが
 *   知らないフィールドを載せた JSON を返送することになる。「触る」と「生やす」は別。
 *
 * ★ **1.0 でも、キーがあれば書く。** 「既定値なら触らない」形にすると、
 *   エンジン側の既定が 1.0 でないときに**等倍へ戻せなくなる**。
 *
 * ★ **元のオブジェクトを破壊しない。** `audioStore` は single-flight で1つの
 *   `synthesize` を共有するが、`AudioQuery` はその中で作られて捨てられるので現状は
 *   共有されない —— それは今の実装の都合であって契約ではない。
 */
export function applySpeedScale(query: AudioQuery, speedScale: number | undefined): AudioQuery {
  if (speedScale === undefined) return query;
  if (!Object.hasOwn(query, "speedScale")) return query;
  return { ...query, speedScale };
}

export function createVoicevoxClient(options: VoicevoxClientOptions): VoicevoxClient {
  const { baseUrl, speakerId, timeoutMs, speedScale } = options;

  async function request(op: string, url: string, init: RequestInit): Promise<Response> {
    let res: Response;
    try {
      res = await fetch(url, { ...init, signal: AbortSignal.timeout(timeoutMs) });
    } catch (err) {
      throw describe(op, err);
    }
    if (!res.ok) {
      // ★ **ボディを読むこと。** 目的は接続の再利用ではなく**診断**。
      //   `ttsSpeakerId` を間違えたときの 422 には、何が悪いかが本文に入っている。
      //
      //   ★ 以前ここには「読み切らないと undici が接続を再利用できない（422 を 30 回で
      //     58 本開いた）」というコメントがあったが、Node 24.19.0 で測り直したところ
      //     **小さいボディでは `cancel()` でも未読でも 30 リクエストで 2 接続**だった
      //     （58〜59 本になるのは 2MB のボディに `cancel()` したときで、
      //     記録されていた数字はむしろその形と一致する）。接続数を理由にしない。
      //
      //   読み取りにも `AbortSignal.timeout` が効くので、止まった本文で固まることはない
      const detail = await res
        .text()
        .then((body) => body.trim().slice(0, DETAIL_MAX_CHARS))
        .catch(() => "");
      throw new TtsHttpError(op, res.status, detail);
    }
    return res;
  }

  return {
    baseUrl,
    speakerId,

    async synthesize(text) {
      // text はクエリ文字列に載る。長文だと 414 になりうるが、その1文が捨てられるだけ
      const queryUrl = `${baseUrl}/audio_query?text=${encodeURIComponent(text)}&speaker=${speakerId}`;
      const queryRes = await request("audio_query", queryUrl, { method: "POST" });

      let query: AudioQuery;
      try {
        query = (await queryRes.json()) as AudioQuery;
      } catch (err) {
        throw describe("audio_query の読み取り", err);
      }

      const synthRes = await request("synthesis", `${baseUrl}/synthesis?speaker=${speakerId}`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(applySpeedScale(query, speedScale)),
      });

      let wav: ArrayBuffer;
      try {
        wav = await synthRes.arrayBuffer();
      } catch (err) {
        throw describe("synthesis の読み取り", err);
      }

      // ★ 200 でも WAV とは限らない。`ttsBaseUrl` が別サービス（開発サーバー、
      //   キャプティブポータル）を指していると 200 + HTML が返り、そのまま再生に回って
      //   「再生に失敗しました」で ack される。**症状が原因からいちばん遠いところに出る**ので、
      //   ここで名指ししておく
      if (!looksLikeWav(wav)) {
        throw new TtsHttpError(
          "synthesis",
          synthRes.status,
          `WAV ではない応答が返りました（${wav.byteLength} バイト）`,
        );
      }
      return wav;
    },

    async listSpeakers() {
      const res = await request("speakers", `${baseUrl}/speakers`, { method: "GET" });
      let parsed: unknown;
      try {
        parsed = await res.json();
      } catch (err) {
        throw describe("speakers の読み取り", err);
      }
      if (!Array.isArray(parsed)) throw new Error("speakers が配列を返しませんでした");
      return parsed as Speaker[];
    },
  };
}

/** `<話者名>（<スタイル名>）` の一覧。話者 ID が見つからないときの案内に使う */
export function flattenStyles(speakers: Speaker[]): { id: number; label: string }[] {
  const out: { id: number; label: string }[] = [];
  for (const speaker of speakers) {
    if (!Array.isArray(speaker?.styles)) continue;
    for (const style of speaker.styles) {
      if (typeof style?.id !== "number") continue;
      out.push({ id: style.id, label: `${speaker.name}（${style.name}）` });
    }
  }
  return out;
}

export function hasStyle(speakers: Speaker[], speakerId: number): boolean {
  return flattenStyles(speakers).some((style) => style.id === speakerId);
}
