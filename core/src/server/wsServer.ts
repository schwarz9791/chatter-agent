/**
 * WebSocket サーバー。
 *
 * 以下はいずれも実際に踏んだ問題への対処なので、消さないこと:
 *
 * - `listening` するまで resolve しない（呼び出し側が監視を始める前に失敗を検知できる）
 * - ping-pong で半開接続を落とす（Android が圏外に出ると TCP が半開のまま残る）
 * - タイマーは `unref()`（しないとイベントループが終了できない）
 * - `bufferedAmount` 超過はそのメッセージだけ捨てる（接続は維持）
 * - `boundAddress` を保持（`wss.address()` は close 後に null を返す）
 * - socket に error ハンドラを必ず付ける（付けないと uncaught でプロセスごと落ちる）
 */

import { WebSocketServer } from "ws";
import type { WebSocket } from "ws";

export interface WsServer {
  /** 接続中の全クライアントへ送る */
  broadcast: (message: string) => void;
  clientCount: () => number;
  address: () => { host: string; port: number };
  close: () => Promise<void>;
}

export interface WsServerOptions {
  host: string;
  port: number;
  /** 0 で無効化（テスト用） */
  heartbeatIntervalMs?: number;
  maxBufferedBytes?: number;
  /**
   * `?since=<seq>` で接続してきたクライアントへ、接続直後に送り直す行を返す。
   *
   * `since` は「ここまでは受け取った」の意味なので、**`seq > since` の行**を返すこと。
   * 遡れる範囲は現世代のファイルまで（設計書 §4-4）。それより古い分は返せないが、
   * 各行が `seq` を持つので、クライアントは欠落を自分で検出できる。
   *
   * ★ 送り直しは connection ハンドラで**同期に**流れる。クライアントは接続の**前**に
   *   message ハンドラを張ること。open を待ってから張ると取りこぼす。
   */
  backfill?: (since: number) => string[];
}

const DEFAULT_HEARTBEAT_MS = 30_000;
const DEFAULT_MAX_BUFFERED_BYTES = 1024 * 1024;
const CLOSE_GRACE_MS = 2_000;

/**
 * `?since=12` を読む。無い・10進数字以外・負なら null（取りこぼし埋めをしない）。
 *
 * ★ `Number()` に丸投げしないこと。`Number("")` は `0` なので、クライアントが
 *   「まだ何も受け取っていない」つもりで送る `?since=` が**全履歴のリプレイ**になる。
 *   `0x10` → 16、`1e3` → 1000 も通ってしまう。
 */
export function parseSince(url: string | undefined): number | null {
  if (!url) return null;
  const query = url.indexOf("?");
  if (query === -1) return null;

  const raw = new URLSearchParams(url.slice(query + 1)).get("since");
  if (raw === null || !/^\d+$/.test(raw)) return null;

  const since = Number(raw);
  return Number.isSafeInteger(since) ? since : null;
}

export function createWsServer(options: WsServerOptions): Promise<WsServer> {
  const heartbeatMs = options.heartbeatIntervalMs ?? DEFAULT_HEARTBEAT_MS;
  const maxBuffered = options.maxBufferedBytes ?? DEFAULT_MAX_BUFFERED_BYTES;

  return new Promise<WsServer>((resolve, reject) => {
    const wss = new WebSocketServer({ host: options.host, port: options.port });
    const alive = new WeakSet<WebSocket>();
    let boundAddress = { host: options.host, port: options.port };

    /** 送れたら true。捨てた（バックプレッシャ / 未接続）なら false */
    const sendTo = (socket: WebSocket, message: string): boolean => {
      if (socket.readyState !== socket.OPEN) return false;
      if (socket.bufferedAmount > maxBuffered) {
        console.warn(`[WS] Backpressure (${socket.bufferedAmount}B buffered), dropping message`);
        return false;
      }
      socket.send(message, (err) => {
        if (err) console.error("[WS] Send failed:", err);
      });
      return true;
    };

    wss.on("connection", (socket, req) => {
      const peer = `${req.socket.remoteAddress}:${req.socket.remotePort}`;
      alive.add(socket);
      console.log(`[WS] Connected: ${peer} (clients: ${wss.clients.size})`);

      socket.on("pong", () => alive.add(socket));
      // ハンドラを付けないと socket の error が uncaught になりサーバーごと落ちる
      socket.on("error", (err) => console.error(`[WS] Client error (${peer}):`, err));
      socket.on("close", (code) => console.log(`[WS] Disconnected: ${peer} code=${code}`));

      const since = parseSince(req.url);
      if (since === null || !options.backfill) return;

      // 以後のブロードキャストより先に流す。ここは同期なので順序が入れ替わらない
      let sent = 0;
      let truncated = false;
      try {
        const lines = options.backfill(since);
        for (const line of lines) {
          // ★ 捨てられたら打ち切る。数えるのは**実際に送れた分**だけにすること。
          //   同期ループの中ではソケットを drain できないので、大きな backfill は
          //   途中で bufferedAmount 上限に当たる。無条件に数えると、届いていないのに
          //   「送り終えた」とログに出て、欠落の手がかりが消える
          if (!sendTo(socket, line)) {
            truncated = true;
            break;
          }
          sent++;
        }
        if (truncated) {
          console.warn(
            `[WS] Backfill truncated for ${peer}: ${sent}/${lines.length} lines sent since seq=${since}. ` +
              "クライアントは受け取れた最後の seq から接続し直すこと",
          );
        }
      } catch (err) {
        console.error("[WS] Backfill failed:", err);
      }
      if (!truncated) console.log(`[WS] Backfilled ${sent} lines since seq=${since} for ${peer}`);
    });

    // ping に応答しない接続を落とす。Android が圏外に出るとTCPが半開のまま残るため
    const timer =
      heartbeatMs > 0
        ? setInterval(() => {
            for (const socket of wss.clients) {
              if (!alive.has(socket)) {
                console.warn("[WS] No pong, terminating dead connection");
                socket.terminate();
                continue;
              }
              alive.delete(socket);
              socket.ping();
            }
          }, heartbeatMs)
        : null;
    // unref しないとイベントループが終了できない
    timer?.unref();

    const server: WsServer = {
      broadcast(message) {
        for (const socket of wss.clients) sendTo(socket, message);
      },

      clientCount: () => wss.clients.size,

      // listening 時に確定させた値。wss.address() は close 後に null を返すため保持しておく
      address: () => ({ ...boundAddress }),

      close() {
        return new Promise<void>((done) => {
          if (timer) clearInterval(timer);
          for (const socket of wss.clients) socket.close(1001, "server shutting down");

          let settled = false;
          const finish = () => {
            if (settled) return;
            settled = true;
            clearTimeout(hard);
            done();
          };

          // close ハンドシェイクに応じないクライアントや、切断直後で OS がまだ掴んでいる
          // ソケットがあると wss.close() のコールバックが返らないことがある。
          // 猶予を過ぎたら強制的に切ったうえで、返ってこなくても先へ進む
          const hard = setTimeout(() => {
            for (const socket of wss.clients) socket.terminate();
            finish();
          }, CLOSE_GRACE_MS);

          wss.close(finish);
        });
      },
    };

    const onStartupError = (err: Error) => {
      wss.off("listening", onListening);
      reject(err);
    };
    const onListening = () => {
      const addr = wss.address();
      if (addr && typeof addr !== "string") boundAddress = { host: addr.address, port: addr.port };
      wss.off("error", onStartupError);
      // 起動後のエラーはログに留める。常駐プロセスを1件のエラーで落とさない
      wss.on("error", (err) => console.error("[WS] Server error:", err));
      resolve(server);
    };
    wss.once("error", onStartupError);
    wss.once("listening", onListening);
  });
}
