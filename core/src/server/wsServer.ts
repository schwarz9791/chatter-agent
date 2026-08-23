/**
 * WebSocket サーバー。
 *
 * 以下はいずれも実際に踏んだ問題への対処なので、消さないこと:
 *
 * - `listening` するまで resolve しない（呼び出し側が監視を始める前に失敗を検知できる）
 * - 外部の `http.Server` を渡されたら、**`listen()` はこちらで、`ws` を載せた後に呼ぶ**
 *   （先に listen すると `listening` が既に発火済みで、Promise が永久に resolve しない）
 * - ping-pong で半開接続を落とす（Android が圏外に出ると TCP が半開のまま残る）
 * - タイマーは `unref()`（しないとイベントループが終了できない）
 * - `bufferedAmount` 超過はその接続を切る（フレームだけ捨てて繋ぎっぱなしにしない。詳細は sendTo 参照）
 * - `boundAddress` を保持（`wss.address()` は close 後に null を返す）
 * - socket に error ハンドラを必ず付ける（付けないと uncaught でプロセスごと落ちる）
 */

import type * as http from "http";
import { WebSocketServer } from "ws";
import type { WebSocket } from "ws";
import { isValidEpoch, type SpeechEpoch } from "../core/types";

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
  /**
   * 同じポートで HTTP も受けたいときに渡す（音声の `GET /audio/…`）。
   *
   * ★ **まだ `listen()` していないものを渡すこと。** `ws` は外部 server の
   *   `listening` / `error` を転送するだけなので、先に listen されていると
   *   この関数の Promise が**永久に resolve せず、エラーも出ずに起動が固まる**。
   *
   * ★ 渡した server の後始末も `close()` が行う。`ws` は `options.server` 指定時に
   *   **http server を閉じない**（`_removeListeners()` するだけ）ので、
   *   ここで閉じないとポートが解放されない。
   */
  server?: http.Server;
  /** 0 で無効化（テスト用） */
  heartbeatIntervalMs?: number;
  maxBufferedBytes?: number;
  /**
   * `Origin` ヘッダの完全一致許可リスト。既定は `[]`（＝ `Origin` 付き接続を全拒否、今までの挙動）。
   * 前方一致やワイルドカードは入れない。`verifyClient` 参照
   */
  allowedOrigins?: string[];
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
   *
   * `epoch` は**任意フィールド**。名乗らなければ `null` が渡る（→ docs/protocol.md）。
   * どの世代の ack かを見るのは呼び出し側（`dispatcher.ack`）の仕事。
   */
  onAck?: (seq: number, epoch: SpeechEpoch | null) => void;
}

const DEFAULT_HEARTBEAT_MS = 30_000;
const DEFAULT_MAX_BUFFERED_BYTES = 1024 * 1024;
const CLOSE_GRACE_MS = 2_000;

/**
 * クライアントから受け取る唯一のメッセージ。
 *
 * ★ 上限を絞ること。ここが開いた時点で、繋げる相手はサーバーにデータを送れるようになる。
 *
 * ★ 超過したフレームは「黙って捨てられる」のではなく、接続そのものが 1009 で切られる
 *   （ws@8.21.3: receiver.js が statusCode 1009 を載せた RangeError を投げ、
 *   websocket.js の receiverOnError が websocket.close() を呼ぶ）。message イベント自体が
 *   発火しないので、下の `if (seq === null) return` は無関係（あちらはパースに失敗した
 *   *正常なサイズの* メッセージを捨てているだけ）。送信側（broadcast）の上限には効かない
 *   — これは受信専用の制限
 */
const MAX_PAYLOAD_BYTES = 4 * 1024;

/**
 * `{"type":"spoken","seq":N,"epoch":"…"}` から `seq` と `epoch` を読む。読めなければ null。
 *
 * ★ ここはクライアント由来の入力。**通す値を絞ること。** 受け取った `seq` は
 *   ファイル名から読んだ値との比較にしか使わず、パスの組み立てには使わない。
 *
 * ★ `epoch` は**任意**。省略は「世代を名乗らない」で、`epoch: null` として通す
 *   （契約に無かった頃のクライアントを黙って無音にしないため）。ただし**載っているのに
 *   形が違うものは通さない** — 世代の判定に使う値なので、緩めると旧世代の ack が
 *   すり抜けて、まだ喋っていない entry がキューから消える。
 *
 * ★ **JSON の `null` は「省略」と同じ扱いにすること。** 未設定の optional を明示的な
 *   `null` にするのは Unity の `JsonUtility`、多くの C# シリアライザ、Go の
 *   `encoding/json`（ポインタ）、Python の `json.dumps(None)` の既定で、
 *   **この契約が想定している Unity クライアント（#12）そのもの**が踏む。
 *   ここで弾くと ack ごと捨てられ、症状は「キューが上限に張り付いて、
 *   どちらの側にもエラーが出ない」になる。
 */
export function parseAck(raw: string): { seq: number; epoch: SpeechEpoch | null } | null {
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }

  if (typeof parsed !== "object" || parsed === null) return null;
  const { type, seq, epoch } = parsed as { type?: unknown; seq?: unknown; epoch?: unknown };

  if (type !== "spoken") return null;
  if (typeof seq !== "number" || !Number.isSafeInteger(seq) || seq < 0) return null;

  // `undefined`（省略）と `null`（明示的に「無い」）を同じに扱う
  if (epoch === undefined || epoch === null) return { seq, epoch: null };
  if (!isValidEpoch(epoch)) return null;

  return { seq, epoch };
}

/**
 * ブラウザからの接続を弾く。
 *
 * WebSocket は CORS の対象外なので、これが無いと**ユーザーが開いた任意の Web ページ**が
 * `new WebSocket("ws://127.0.0.1:8570")` で会話を読め、ack を投げてマスコットを黙らせられる。
 * `host` を 127.0.0.1 にしても塞がらない。
 *
 * `allowedOrigins`（完全一致・既定 `[]`）に載っている `Origin` だけを通す。
 * Electron の `chatter-mascot` や Unity WebGL ビルドのように、`Origin` を送るが正規のクライアント
 * である相手を通す唯一の経路がこれ。前方一致やワイルドカードは入れない（緩めるほど上の脅威に近づく）。
 *
 * Unity（WebGL 以外）/ ネイティブクライアントは `Origin` を送らないので、リストの中身に関わらず通る。
 * LAN 上の他端末に対する認証は別途必要（Issue #3）。
 */
function createVerifyClient(
  allowedOrigins: string[],
): (info: { origin?: string; req: { headers: Record<string, unknown> } }) => boolean {
  const allowed = new Set(allowedOrigins);
  return (info) => {
    const origin = info.origin ?? info.req.headers.origin;
    if (typeof origin !== "string" || origin.length === 0) return true;
    if (allowed.has(origin)) return true;

    // 拒否理由を分けて出す。空リストなら「そもそも許可リストが無い」、そうでなければ「リストに無い」
    console.warn(
      allowed.size === 0
        ? `[WS] Rejected origin (allowedOrigins is empty): ${origin}`
        : `[WS] Rejected origin (not in allowedOrigins): ${origin}`,
    );
    return false;
  };
}

export function createWsServer(options: WsServerOptions): Promise<WsServer> {
  const heartbeatMs = options.heartbeatIntervalMs ?? DEFAULT_HEARTBEAT_MS;
  const maxBuffered = options.maxBufferedBytes ?? DEFAULT_MAX_BUFFERED_BYTES;

  return new Promise<WsServer>((resolve, reject) => {
    const external = options.server;
    const wss = new WebSocketServer({
      // 外部 server を渡すときは host/port を同時に渡せない（ws がバリデーションで弾く）。
      // listen は下で、この構築が終わってから行う
      ...(external ? { server: external } : { host: options.host, port: options.port }),
      maxPayload: MAX_PAYLOAD_BYTES,
      verifyClient: createVerifyClient(options.allowedOrigins ?? []),
    });
    const alive = new WeakSet<WebSocket>();
    let boundAddress = { host: options.host, port: options.port };

    /** 送れたら true。捨てた（バックプレッシャ / 未接続）なら false */
    const sendTo = (socket: WebSocket, message: string): boolean => {
      if (socket.readyState !== socket.OPEN) return false;
      if (socket.bufferedAmount > maxBuffered) {
        // ★ フレームを捨てて接続を維持すると、キューは「配信済み」として先へ進むのに
        //   その1文だけが永久に届かない。切ってしまえば、繋ぎ直したときに未 ack 分が
        //   接続直後の追いつきで再送される（docs/protocol.md）。
        //   キューの上限 500 件 × 数百バイトは 1MB に遠く届かないので、
        //   ここに来る時点でクライアントは実質停止している
        console.warn(`[WS] Backpressure (${socket.bufferedAmount}B buffered), closing`);
        socket.close(1013, "too slow");
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
        // ★ 読めない ack を**無言で捨てないこと。** 捨てられた ack はキューを減らさないので、
        //   症状は「上限まで溜まって古い方から捨てられ続ける」になり、
        //   契約どおりのつもりのクライアントが弾かれても原因に辿り着く手がかりが残らない。
        //   ただし1接続につき1回で足りる（壊れたクライアントは同じ形を送り続ける）
        let warnedBadAck = false;

        socket.on("message", (data) => {
          const ack = parseAck(String(data));
          // 知らない形は捨てる。maxPayload 超過はそもそもここに来ない（MAX_PAYLOAD_BYTES 参照）
          if (ack === null) {
            if (!warnedBadAck) {
              warnedBadAck = true;
              console.warn(`[WS] 読めない ack を捨てました (${peer}): ${String(data).slice(0, 120)}`);
            }
            return;
          }
          // 隣の onConnect と同じ理由。ここで投げると message イベント経由の uncaught になり
          // サーバーごと落ちる
          try {
            options.onAck?.(ack.seq, ack.epoch);
          } catch (err) {
            console.error("[WS] onAck failed:", err);
          }
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
            if (!external) return done();
            // ★ `options.server` 指定時、ws は http server を閉じない。
            //   音声を配っている最中の keep-alive 接続が残るので、明示的に切る
            external.closeAllConnections();
            external.close(() => done());
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

    // ★ 構築の**後**に listen する（このファイルのヘッダ参照）。内部 server のときは
    //   WebSocketServer のコンストラクタが既に listen している
    if (external) external.listen(options.port, options.host);
  });
}
