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

export interface AudioHttpDeps {
  store: AudioStore;
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
function checkOrigin(req: http.IncomingMessage, res: http.ServerResponse, allowed: Set<string>): boolean {
  const origin = req.headers.origin;
  if (typeof origin !== "string" || origin.length === 0) return true; // ネイティブクライアント
  if (!allowed.has(origin)) {
    console.warn(`[HTTP] Rejected origin: ${origin}`);
    return false;
  }
  res.setHeader("access-control-allow-origin", origin);
  res.setHeader("vary", "Origin");
  return true;
}

export function createAudioHttpServer(deps: AudioHttpDeps): http.Server {
  const allowed = new Set(deps.allowedOrigins);

  return http.createServer((req, res) => {
    // ★ ハンドラ全体を握ること。ここで throw すると `uncaughtException` ガードが
    //   プロセスは守るが、**レスポンスが終わらずクライアントがハングする**
    void handle(req, res).catch((err: unknown) => {
      console.error("[HTTP] リクエストの処理に失敗しました:", err);
      endWith(res, 500, "internal error\n");
    });
  });

  async function handle(req: http.IncomingMessage, res: http.ServerResponse): Promise<void> {
    if (!checkOrigin(req, res, allowed)) return endWith(res, 403, "forbidden\n");

    if (req.method !== "GET" && req.method !== "HEAD") return endWith(res, 405, "method not allowed\n");

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

    let wav: ArrayBuffer;
    try {
      wav = await deps.store.get(key.epoch, key.seq, record.text);
    } catch (err) {
      if (err instanceof SynthesisUnavailableError) {
        console.warn(`[HTTP] seq=${key.seq} の合成に失敗しました（あとで取りに来てもらいます）: ${err.message}`);
        return endWith(res, 503, "synthesis unavailable\n");
      }
      throw err;
    }

    if (res.writableEnded) return; // 合成を待っている間に切られた
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
