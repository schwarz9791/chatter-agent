/**
 * 音声合成エンジンのクライアント。AivisSpeech / VOICEVOX の互換 API を叩く。
 *
 * `POST /audio_query?text=…&speaker=<id>` で読み仮名とアクセントの入ったクエリを作り、
 * それをそのままボディに載せて `POST /synthesis?speaker=<id>` に投げると WAV が返る。
 * cc-mascot も同じ2段構えだが、こちらは公開されている API 仕様を見て書いてある
 * （そのため cc-mascot 由来の帰属表示は付かない → docs/origin.md）。
 *
 * ★ `AudioQuery` の中身は解釈しない。`speedScale` などを触る予定が無いうちは、
 *   エンジンが返した JSON をそのまま返送するのが最も安全（エンジンのバージョン差に強い）。
 */

/** エンジンが返す読み仮名クエリ。中身は解釈せずそのまま返送する */
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
   * 1リクエストあたりの上限。
   *
   * ★ 省略できない。Node の `fetch` に既定のタイムアウトは無いので、エンジンが応答を返さず
   *   TCP を保持し続けると、その文が `synthesizing` のまま固まる。head-of-line blocking なので
   *   **以後すべてが無音になり、しかもエラーが1行も出ない**。
   */
  timeoutMs: number;
  /** テストから差し替える。既定はグローバルの fetch */
  fetchImpl?: typeof fetch;
}

export interface VoicevoxClient {
  /** 1文ぶんの WAV。`audio_query` → `synthesis` の2往復 */
  synthesize(text: string): Promise<ArrayBuffer>;
  /** 話者の一覧。起動時の疎通確認と、話者 ID の検査に使う */
  listSpeakers(): Promise<Speaker[]>;
  readonly baseUrl: string;
  readonly speakerId: number;
}

/** どの段階で落ちたかを残す。ログの1行から原因に辿り着けるように */
function describe(op: string, err: unknown): Error {
  if (err instanceof Error && (err.name === "TimeoutError" || err.name === "AbortError")) {
    return new Error(`${op} がタイムアウトしました`);
  }
  return new Error(`${op} に失敗しました: ${String(err)}`);
}

export function createVoicevoxClient(options: VoicevoxClientOptions): VoicevoxClient {
  const { baseUrl, speakerId, timeoutMs } = options;
  const doFetch = options.fetchImpl ?? fetch;

  async function request(op: string, url: string, init: RequestInit): Promise<Response> {
    let res: Response;
    try {
      res = await doFetch(url, { ...init, signal: AbortSignal.timeout(timeoutMs) });
    } catch (err) {
      throw describe(op, err);
    }
    if (!res.ok) throw new Error(`${op} が ${res.status} を返しました`);
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
        body: JSON.stringify(query),
      });

      try {
        return await synthRes.arrayBuffer();
      } catch (err) {
        throw describe("synthesis の読み取り", err);
      }
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
