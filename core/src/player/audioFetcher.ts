/**
 * 合成済み音声を取りに行く。
 *
 * #29 で合成がサーバーへ移り、player 側の `audio_query` → `synthesis` の2往復が
 * **`GET /audio/<epoch>-<seq>.wav` の1往復**になった。サーバーは合成が終わるまで
 * レスポンスを保留するので、待ち時間の性質は前と変わらない。
 *
 * ★ **結果を3つに分けること。** ここが2値（成功 / 失敗）だと、`playbackQueue` の
 *   `synthesisAttempts`（既定2）が数 ms で燃え尽きる。AivisSpeech を起動し忘れたまま
 *   繋ぐと**溜まっていたキューが数百 ms で全部捨てられる**という、
 *   `player/index.ts` の `waitForEngine` が守っていたのと同じ事故になる。
 */

export type AudioFetchResult =
  /** 200。WAV が取れた */
  | { kind: "ready"; wav: ArrayBuffer }
  /**
   * 503。サーバーはいるが音声を用意できない（エンジンが落ちている / 合成が返らない）。
   * **あとで取りに来い**という意味なので、試行回数を消費せずに待つ。
   */
  | { kind: "unavailable"; reason: string }
  /**
   * 404。永久に用意できない（キューから消えた / 世代違い / 読み上げる中身が無い /
   * `ttsEnabled: false`）。諦めて ack する。
   */
  | { kind: "gone"; reason: string }
  /** 転送そのものの失敗（接続不可・タイムアウト・想定外のステータス）。試行回数を消費する */
  | { kind: "failed"; reason: string };

export interface AudioFetcherOptions {
  /**
   * 音声の取得元。WebSocket の接続先から導出する（`ws://host:port` → `http://host:port`）。
   *
   * ★ サーバーは自分の到達アドレスを知らないので、フレームに載るのは**相対パス**だけ。
   *   authority を補うのはクライアントの責務（→ `core/audioPath.ts`）。
   */
  baseUrl: string;
  /**
   * 1リクエストの上限。**省略できない。** Node の fetch に既定のタイムアウトは無く、
   * 返らない相手を掴むと head-of-line blocking で以後すべてが無音になる。
   */
  timeoutMs: number;
}

export interface AudioFetcher {
  fetchAudio(audioPath: string): Promise<AudioFetchResult>;
  readonly baseUrl: string;
}

/** `ws://host:port` / `wss://host:port` を音声の取得元に読み替える */
export function deriveAudioBaseUrl(serverUrl: string): string {
  const url = new URL(serverUrl);
  url.protocol = url.protocol === "wss:" ? "https:" : "http:";
  url.pathname = "";
  url.search = "";
  url.hash = "";
  return url.toString().replace(/\/$/, "");
}

/** サーバーが本文に載せた理由。読めなければ既定の文言のまま */
const REASON_MAX_CHARS = 300;

async function reason(res: Response, fallback: string): Promise<string> {
  const body = await res.text().catch(() => "");
  const trimmed = body.trim().slice(0, REASON_MAX_CHARS);
  return trimmed.length > 0 ? trimmed : fallback;
}

export function createAudioFetcher(options: AudioFetcherOptions): AudioFetcher {
  const { baseUrl, timeoutMs } = options;

  return {
    baseUrl,

    async fetchAudio(audioPath) {
      let res: Response;
      try {
        res = await fetch(`${baseUrl}${audioPath}`, { signal: AbortSignal.timeout(timeoutMs) });
      } catch (err) {
        const timedOut = err instanceof Error && (err.name === "TimeoutError" || err.name === "AbortError");
        return { kind: "failed", reason: timedOut ? `${timeoutMs}ms で返りませんでした` : String(err) };
      }

      // ★ **ボディを読むこと。** 目的は接続の再利用ではなく**診断**。
      //   サーバーは 503 / 404 の本文に理由（`ECONNREFUSED …` / `speaker not found …`）を
      //   載せてくるので、ここで捨てると無音の原因がクライアント側のログから消える。
      //
      //   ★ 以前は `cancel()` で「undici が接続を再利用できないから」と書いていたが、
      //     Node 24.19.0 で測ると**短いボディでは `cancel()` でも未読でも 30 リクエストで
      //     2 接続**だった（58〜59 本になるのは 2MB のボディに `cancel()` したとき）。
      //     接続数を理由にしない。
      if (res.status === 503) {
        return { kind: "unavailable", reason: await reason(res, "サーバーが音声を用意できていません (503)") };
      }
      if (res.status === 404) {
        return { kind: "gone", reason: await reason(res, "音声がありません (404)") };
      }
      if (!res.ok) {
        return { kind: "failed", reason: await reason(res, `想定外のステータス ${res.status}`) };
      }

      try {
        return { kind: "ready", wav: await res.arrayBuffer() };
      } catch (err) {
        return { kind: "failed", reason: `本体の読み取りに失敗しました: ${String(err)}` };
      }
    },
  };
}
