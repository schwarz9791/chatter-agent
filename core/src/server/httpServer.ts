/**
 * 音声を配る HTTP の口。WebSocket と**同じポート**に相乗りする。
 *
 * ```
 * GET /audio/<epoch>-<seq>.wav
 *   200  合成済み、または今から合成して返した（完了までレスポンスを保留する）
 *   403  Origin が allowedOrigins に無い
 *   404  永久に用意できない（キューから消えた / epoch 違い / 読み上げる中身が無い）
 *   503  エンジンに繋がらない・合成が返らない。**あとで取りに来い**
 * ```
 *
 * ★ **404 と 503 を混ぜないこと。** クライアントは 503 では試行回数を減らさずに
 *   待ち、404 では諦めて ack する。混ぜると、エンジンを起動し忘れているだけで
 *   **溜まっていたキューが数百 ms で全部捨てられる**（`player/index.ts` の
 *   `waitForEngine` が守っていた不変条件と同じもの）。
 *
 * ★ **合成の失敗を 404 に落とさないこと。** 「エンジンが 4xx を返したなら恒久的だから
 *   諦めさせる」は一見筋が通るが、404 は `ack → ackUpTo` まで通って**キューのファイルを
 *   物理削除する**ので、設定を直しても復元できない（→ `server/audioStore.ts`）。
 *
 * ★ **`ws` より先に作り、`listen` は `ws` を載せてから。** `new WebSocketServer({ server })`
 *   は `listening` を転送するだけなので、先に `listen()` してしまうと
 *   `createWsServer` の Promise が**永久に resolve せず、エラーも出ずに起動が固まる**。
 *   順序は `wsServer.ts` の `createWsServer` が握っている。
 */

import * as http from "http";
import { parseAudioPath } from "../core/audioPath";
import type { SpeechRecord } from "../core/types";
import { hasSpeakableText } from "../text/speakable";
import { SynthesisUnavailableError, type AudioStore } from "./audioStore";
import { createThrottledWarn } from "./throttledWarn";

export interface AudioHttpDeps {
  store: AudioStore;
  /**
   * 1回の GET を保留する上限。超えたら 503 を返す。
   *
   * ★ **合成そのものは打ち切らない。** `audioStore` は single-flight なので、走らせたままに
   *   しておけば終わった時点でキャッシュに入り、クライアントの取り直しが即 200 になる。
   *   ここで打ち切ることで、「クライアントの `audioFetchTimeoutMs` はサーバーの
   *   `synthesisTimeoutMs` より長くしなければならない」という**設定間の暗黙の順序制約が
   *   要らなくなる**（守られなかったときの症状は「試行回数を消費して発話が捨てられる」で、
   *   設定からは読み取れない）。
   */
  responseTimeoutMs: number;
  /** 合成に失敗したときに呼ぶ。診断（話者一覧など）の再実行に使う */
  onSynthesisFailed?: () => void;
  /**
   * その `seq` の配信キュー entry。無ければ null。
   *
   * ★ **本文の権威はキュー。** ここを `audioStore` に持たせると、キューから消えた
   *   （＝ ack 済み・trim 済み）文の音声を配り続ける経路ができる。
   */
  lookup: (seq: number) => SpeechRecord | null;
  /** `Origin` の完全一致許可リスト。`wsServer` と同じものを渡す */
  allowedOrigins: string[];
  /**
   * 音声を配らない設定（`ttsEnabled: false`）なら true。
   * 関数なのは config が実行中に読み直されるため（→ `dispatcher.ts` の `audioEnabled`）
   */
  disabled: () => boolean;
}

/** 503 のときにクライアントへ渡す再試行の目安 */
const RETRY_AFTER_SECONDS = 1;

/** `Allow` と `Access-Control-Allow-Methods` に出す値 */
const ALLOWED_METHODS = "GET, HEAD, OPTIONS";

/** 応答の期限切れ。合成の失敗（`SynthesisUnavailableError`）とは別物 */
class ResponseDeadlineError extends Error {
  constructor() {
    super("response deadline");
    this.name = "ResponseDeadlineError";
  }
}

/**
 * `work` を待つが、`ms` を超えたら諦める。**`work` は止めない。**
 *
 * ★ 捨てた promise に `catch` を付けておくこと。付けないと、後から失敗したときに
 *   `unhandledRejection` になる（常駐プロセスのガードには引っかかるが、ログが濁る）。
 */
function withDeadline<T>(work: Promise<T>, ms: number): Promise<T> {
  work.catch(() => {});
  return new Promise<T>((resolve, reject) => {
    const timer = setTimeout(() => reject(new ResponseDeadlineError()), ms);
    timer.unref();
    work.then(
      (value) => {
        clearTimeout(timer);
        resolve(value);
      },
      (err: unknown) => {
        clearTimeout(timer);
        reject(err instanceof Error ? err : new Error(String(err)));
      },
    );
  });
}

function endWith(res: http.ServerResponse, status: number, body: string): void {
  if (res.writableEnded) return;
  res.writeHead(status, {
    "content-type": "text/plain; charset=utf-8",
    "cache-control": "no-store",
    ...(status === 503 ? { "retry-after": String(RETRY_AFTER_SECONDS) } : {}),
  });
  res.end(body);
}

/**
 * `Origin` を検査し、許可するなら CORS ヘッダを返す。拒否するなら false。
 *
 * ★ **403 を返すだけでは足りない。** WebSocket は CORS の対象外なので `verifyClient` で
 *   足りたが、`fetch("/audio/…")` は対象。許可した Origin に
 *   `Access-Control-Allow-Origin` を返さないと、サーバーが許可していても
 *   ブラウザ側で音声だけブロックされる（WebView クライアント: Tauri / Electron の
 *   renderer / Unity WebGL）。
 */
function checkOrigin(
  req: http.IncomingMessage,
  res: http.ServerResponse,
  allowed: Set<string>,
  warn: (message: string) => void,
): boolean {
  const origin = req.headers.origin;
  if (typeof origin !== "string" || origin.length === 0) return true; // ネイティブクライアント
  if (!allowed.has(origin)) {
    warn(`[HTTP] Rejected origin: ${origin}`);
    return false;
  }
  res.setHeader("access-control-allow-origin", origin);
  res.setHeader("vary", "Origin");
  // ★ `Retry-After` は CORS の safelist に入っていない。expose しないと
  //   **ブラウザ / WebView の JS からは読めない** — #29 が主目的にしている
  //   XR / Unity WebGL / Electron renderer で、503 のバックオフ情報が届かなくなる
  res.setHeader("access-control-expose-headers", "retry-after");
  return true;
}

export function createAudioHttpServer(deps: AudioHttpDeps): http.Server {
  const allowed = new Set(deps.allowedOrigins);
  const warn = createThrottledWarn();

  return http.createServer((req, res) => {
    // ★ ハンドラ全体を握ること。ここで throw すると `uncaughtException` ガードが
    //   プロセスは守るが、**レスポンスが終わらずクライアントがハングする**
    void handle(req, res).catch((err: unknown) => {
      console.error("[HTTP] リクエストの処理に失敗しました:", err);
      endWith(res, 500, "internal error\n");
    });
  });

  async function handle(req: http.IncomingMessage, res: http.ServerResponse): Promise<void> {
    if (!checkOrigin(req, res, allowed, warn)) return endWith(res, 403, "forbidden\n");

    // ★ プリフライトは `checkOrigin` の**後**。405 で切ると `Access-Control-Allow-Methods` が
    //   返らず、safelist 外のヘッダを付けるブラウザ系クライアントの本リクエストが
    //   ブロックされる（許可 Origin に ACAO を返した手当てもそこへ到達しない）。
    //
    // ★ `Access-Control-Allow-Headers` に `range` を並べないこと。下で
    //   `Accept-Ranges: none` と宣言している以上、**対応していないものを対応していると
    //   言う**ことになる
    if (req.method === "OPTIONS") {
      res.writeHead(204, { allow: ALLOWED_METHODS, "access-control-allow-methods": ALLOWED_METHODS });
      return void res.end();
    }

    // RFC 9110 §15.5.6: 405 は `Allow` を返さなければならない
    if (req.method !== "GET" && req.method !== "HEAD") {
      res.setHeader("allow", ALLOWED_METHODS);
      return endWith(res, 405, "method not allowed\n");
    }

    // ★ 受け取った文字列をパスの組み立てに使わない。正規表現で `(epoch, seq)` に
    //   分解してから、Map とキューを引く（`speechQueue.read` と同じ論法）
    const pathname = (req.url ?? "").split("?")[0] ?? "";
    const key = parseAudioPath(pathname);
    if (key === null) return endWith(res, 404, "not found\n");

    if (deps.disabled()) return endWith(res, 404, "audio is disabled\n");

    const record = deps.lookup(key.seq);
    // キューから消えた（ack / trim / 起動時の掃除）か、世代違いの古い URL。
    // どちらも**永久に用意できない**ので 404
    if (record === null || record.epoch !== key.epoch) return endWith(res, 404, "not found\n");
    if (!hasSpeakableText(record.text)) return endWith(res, 404, "nothing to speak\n");

    // ★ **応答だけを打ち切る。合成は走らせたまま。** `audioStore` は single-flight なので、
    //   終われば同じキーでキャッシュに入り、クライアントの取り直しが即 200 になる。
    //   合成そのものを短く切ると、モデルロード中の1文目が永久に完成しない
    let wav: ArrayBuffer;
    try {
      wav = await withDeadline(deps.store.get(key.epoch, key.seq, record.text), deps.responseTimeoutMs);
    } catch (err) {
      if (err instanceof SynthesisUnavailableError) {
        deps.onSynthesisFailed?.();
        warn(`[HTTP] seq=${key.seq} の合成に失敗しました（あとで取りに来てもらいます）: ${err.message}`);
        return endWith(res, 503, `synthesis unavailable: ${err.message}\n`);
      }
      if (err instanceof ResponseDeadlineError) {
        warn(`[HTTP] 合成が ${deps.responseTimeoutMs}ms で終わらないので一旦返します（合成は続行中）`);
        return endWith(res, 503, "synthesis in progress\n");
      }
      throw err;
    }

    // ★ `writableEnded` は**自分が end() を呼んだ後にしか真にならない**。
    //   合成を待っている間にクライアントが切ったかどうかは `destroyed` で見る
    if (res.writableEnded || res.destroyed) return;
    res.writeHead(200, {
      "content-type": "audio/wav",
      "content-length": String(wav.byteLength),
      "cache-control": "no-store",
      // ★ Range は実装しない。Unity の UnityWebRequestMultimedia / ExoPlayer は
      //   Range を投げてくるが、1文ぶんの WAV は数百KB なので分割で得るものが無い。
      //   Range を無視して 200 に全体を返すのは仕様上正しい
      "accept-ranges": "none",
    });
    if (req.method === "HEAD") return void res.end();
    res.end(Buffer.from(wav));
  }
}
