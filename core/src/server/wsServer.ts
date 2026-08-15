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
  /** そのソケットにだけ送る。接続直後の追いつき用 */
  sendTo: (socket: WebSocket, message: string) => boolean;
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
   * 接続してきたクライアントに、まず何を送るか。
   *
   * ★ ここで**まだ broadcast していない分を送らないこと。** `ws` は connection を
   *   発火する**前に**クライアントを `wss.clients` へ入れるので、直後の broadcast と
   *   合わせて新規クライアントだけが二重に受け取る。
   */
  onConnect?: (send: (message: string) => boolean) => void;
  /**
   * クライアントが「seq N まで喋った」と言ってきたときに呼ぶ。
   *
   * 累積 ack なので、1つ落ちても次で自己修復する。`seq` は**非負の安全整数**しか渡らない。
   */
  onAck?: (seq: number) => void;
}

const DEFAULT_HEARTBEAT_MS = 30_000;
const DEFAULT_MAX_BUFFERED_BYTES = 1024 * 1024;
const CLOSE_GRACE_MS = 2_000;

/**
 * クライアントから受け取る唯一のメッセージ。
 *
 * ★ 上限を絞ること。ここが開いた時点で、繋げる相手はサーバーにデータを送れるようになる。
 */
const MAX_PAYLOAD_BYTES = 4 * 1024;

/**
 * `{"type":"spoken","seq":N}` から `seq` を読む。読めなければ null。
 *
 * ★ ここはクライアント由来の入力。**通す値を絞ること。** 受け取った `seq` は
 *   ファイル名から読んだ値との比較にしか使わず、パスの組み立てには使わない。
 */
export function parseAck(raw: string): number | null {
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }

  if (typeof parsed !== "object" || parsed === null) return null;
  const { type, seq } = parsed as { type?: unknown; seq?: unknown };

  if (type !== "spoken") return null;
  if (typeof seq !== "number" || !Number.isSafeInteger(seq) || seq < 0) return null;

  return seq;
}

/**
 * ブラウザからの接続を弾く。
 *
 * WebSocket は CORS の対象外なので、これが無いと**ユーザーが開いた任意の Web ページ**が
 * `new WebSocket("ws://127.0.0.1:8570")` で会話を読め、ack を投げてマスコットを黙らせられる。
 * `host` を 127.0.0.1 にしても塞がらない。
 *
 * Unity / ネイティブクライアントは `Origin` を送らないので影響しない。
 * LAN 上の他端末に対する認証は別途必要（Issue #3）。
 */
function verifyClient(info: { origin?: string; req: { headers: Record<string, unknown> } }): boolean {
  const origin = info.origin ?? info.req.headers.origin;
  if (typeof origin === "string" && origin.length > 0) {
    console.warn(`[WS] Rejected browser origin: ${origin}`);
    return false;
  }
  return true;
}

export function createWsServer(options: WsServerOptions): Promise<WsServer> {
  const heartbeatMs = options.heartbeatIntervalMs ?? DEFAULT_HEARTBEAT_MS;
  const maxBuffered = options.maxBufferedBytes ?? DEFAULT_MAX_BUFFERED_BYTES;

  return new Promise<WsServer>((resolve, reject) => {
    const wss = new WebSocketServer({
      host: options.host,
      port: options.port,
      maxPayload: MAX_PAYLOAD_BYTES,
      verifyClient,
    });
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

      if (options.onAck) {
        socket.on("message", (data) => {
          const seq = parseAck(String(data));
          if (seq === null) return; // 知らない形は黙って捨てる
          options.onAck?.(seq);
        });
      }

      if (!options.onConnect) return;

      // 以後のブロードキャストより先に流す。ここは同期なので順序が入れ替わらない
      try {
        options.onConnect((message) => sendTo(socket, message));
      } catch (err) {
        console.error("[WS] onConnect failed:", err);
      }
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

      sendTo,

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
