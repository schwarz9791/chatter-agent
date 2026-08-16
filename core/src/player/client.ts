/**
 * `chatter-agent-server` への接続。**フレームの解釈も判断もしない**（→ `playbackQueue.ts`）。
 *
 * `server/wsServer.ts` の裏返しにあたる部品で、繋ぐ・繋ぎ直す・ack を送る、だけを持つ。
 */

import { WebSocket } from "ws";

/** 初回の再接続待ち。ここから倍々に伸ばす */
const BACKOFF_MIN_MS = 500;
const BACKOFF_MAX_MS = 30_000;

/**
 * これだけ繋がっていられたらバックオフをリセットする。
 *
 * ★ `open` した瞬間にリセットしないこと。バックプレッシャ（1013）で切られ続ける相手だと
 *   「繋がる → すぐ切れる」が最短間隔で回り続ける。
 */
const BACKOFF_RESET_AFTER_MS = 10_000;

/**
 * サーバーの ping がこれだけ来なければ、繋がっているとみなさず切り直す。
 *
 * ★ サーバー側は 30 秒ごとに ping を打ち、pong が返らない接続を terminate する
 *   （`wsServer.ts` の heartbeat）。**その対称の仕組みがクライアントには無い。**
 *   スリープ復帰や NAT テーブル切れで half-open になると、player は「接続中」のまま
 *   永久に無音になる。症状が無音なので、これが無いと原因に辿り着けない。
 */
const PING_WATCHDOG_MS = 90_000;

/** ack をまとめて送るまでの猶予。累積 ack なので最大値の1回で足りる */
const ACK_FLUSH_MS = 20;

export interface SpeechClientOptions {
  url: string;
  /** 受け取った生フレーム。パースは呼び出し側 */
  onFrame: (raw: string) => void;
  onConnected: () => void;
  onDisconnected: () => void;
  /** テスト用。既定は 90 秒。0 で無効（`wsServer` の `heartbeatIntervalMs` と同じ流儀） */
  pingWatchdogMs?: number;
  /** テスト用。既定は 500ms */
  backoffMinMs?: number;
}

export interface SpeechClient {
  start(): void;
  /** 累積 ack。短時間に何度呼んでも、最大値が1回だけ飛ぶ */
  ack(seq: number): void;
  /**
   * まだ送っていない ack を捨てる。
   *
   * ★ 採番のやり直しを検出したら必ず呼ぶこと。間引きバッファ（20ms）に旧エポックの ack が
   *   残っていると、**切断を挟まなくても**それが新しいサーバーへ飛び、`ackUpTo` が
   *   まだ喋っていない entry を消す。reducer 側の `pendingAck` を消すだけでは足りない
   */
  dropPendingAck(): void;
  /** 再接続をやめて閉じる */
  close(): Promise<void>;
}

/**
 * `playerServerUrl` が空のときの接続先。
 *
 * ★ `host` をそのまま使えない。既定の `0.0.0.0` は **bind アドレスであって接続先ではない**。
 *   macOS では偶然 localhost に繋がるが、意図した挙動ではないし `::` では破綻する。
 */
export function deriveServerUrl(host: string, port: number): string {
  const target = host === "0.0.0.0" || host === "::" || host === "" ? "127.0.0.1" : host;
  // IPv6 リテラルは角括弧で囲む必要がある
  const authority = target.includes(":") ? `[${target}]` : target;
  return `ws://${authority}:${port}`;
}

export function createSpeechClient(options: SpeechClientOptions): SpeechClient {
  const watchdogMs = options.pingWatchdogMs ?? PING_WATCHDOG_MS;
  const backoffMin = options.backoffMinMs ?? BACKOFF_MIN_MS;

  let socket: WebSocket | null = null;
  let closed = false;
  let attempt = 0;
  let reconnectTimer: NodeJS.Timeout | null = null;
  let watchdog: NodeJS.Timeout | null = null;
  let ackTimer: NodeJS.Timeout | null = null;
  let pendingAck: number | null = null;
  let openedAt = 0;

  function backoffMs(): number {
    const base = Math.min(backoffMin * 2 ** attempt, BACKOFF_MAX_MS);
    // ジッタを入れて、複数プロセスが同じ間隔で叩き続けるのを避ける
    return base / 2 + Math.random() * (base / 2);
  }

  function clearWatchdog(): void {
    if (watchdog) clearTimeout(watchdog);
    watchdog = null;
  }

  function armWatchdog(): void {
    clearWatchdog();
    if (watchdogMs <= 0) return;
    watchdog = setTimeout(() => {
      console.warn("[Player] サーバーからの ping が途切れました。接続し直します");
      // close ではなく terminate。half-open では close ハンドシェイクが返ってこない
      socket?.terminate();
    }, watchdogMs);
    watchdog.unref();
  }

  function flushAck(): void {
    if (ackTimer) clearTimeout(ackTimer);
    ackTimer = null;
    if (pendingAck === null) return;

    // ★ 送れると分かってから消費すること。先に消すと、reducer が `connected` のまま ack を出した
    //   直後にソケットが CLOSING（`close` イベントがまだ届いていない）だったケースで、
    //   ack が client 側からも reducer 側からも消えて復旧手段が無くなる
    if (socket?.readyState !== WebSocket.OPEN) return;
    const seq = pendingAck;
    pendingAck = null;
    socket.send(JSON.stringify({ type: "spoken", seq }));
  }

  function scheduleReconnect(): void {
    if (closed || reconnectTimer) return;
    const delay = backoffMs();
    attempt++;
    // ★ このタイマーを unref しないこと。切断中は socket も無く、他のタイマーも unref 済みなので、
    //   ここまで unref するとイベントループが空になって**プロセスがそのまま終了する**。
    //   `close()` で必ず clearTimeout するので、終了処理が妨げられることはない
    reconnectTimer = setTimeout(() => {
      reconnectTimer = null;
      connect();
    }, delay);
  }

  function connect(): void {
    if (closed) return;

    // ★ Origin ヘッダを付けないこと。`allowedOrigins` の既定は空で、Origin 付きの接続は
    //   すべて拒否される。Node の ws クライアントは既定で付けないので、足さなければよい
    const next = new WebSocket(options.url);
    socket = next;

    // ★ message ハンドラは open を待たずにここで張る。接続直後の追いつきは
    //   サーバーの connection ハンドラから**同期で**流れるので、open を待つと取りこぼす
    //   （docs/protocol.md の唯一の★警告）
    next.on("message", (data) => options.onFrame(String(data)));

    next.on("open", () => {
      openedAt = Date.now();
      console.log(`[Player] 接続しました: ${options.url}`);
      armWatchdog();
      options.onConnected();
      flushAck();
    });

    // サーバーの heartbeat。ws は pong を自動で返すので、ここでは生存の証拠として使う
    next.on("ping", () => armWatchdog());

    next.on("unexpected-response", (_req, res) => {
      // 401 は Origin 検査。ネイティブから張っている限り起きないが、理由が分からないと詰まる
      const hint = res.statusCode === 401 ? "（allowedOrigins に弾かれた可能性があります）" : "";
      console.error(`[Player] ハンドシェイクに失敗しました: ${res.statusCode}${hint}`);

      // ★ このハンドラを張ったら、自分で接続を畳むこと。
      //   ws は `emit("unexpected-response", …)` が **false を返したときだけ**（＝リスナが1つも
      //   無いときだけ）`abortHandshake` を呼ぶ（ws@8.21.3 websocket.js:928）。ログを出すだけだと
      //   emit が true を返すので `error` も `close` も発火せず、readyState は CONNECTING のまま
      //   固着する。`close` が `scheduleReconnect()` を呼ぶ唯一の経路なので、**再起動するまで
      //   永久に無音**になる（watchdog も `open` でしか張らないので救済されない）。
      //   → docs/protocol.md のクライアント責務5に反する
      next.terminate();
    });

    next.on("error", (err) => {
      // 起動直後にサーバーが居ないのは通常のこと。毎回スタックを出さない
      console.warn(`[Player] 接続エラー: ${err.message}`);
    });

    next.on("close", (code, reason) => {
      clearWatchdog();
      if (socket === next) socket = null;
      if (closed) return;

      // 安定して繋がっていられたなら、次の切断は「たまたま」として最短から試す
      if (openedAt && Date.now() - openedAt >= BACKOFF_RESET_AFTER_MS) attempt = 0;
      openedAt = 0;

      const detail = reason.length > 0 ? ` ${String(reason)}` : "";
      console.warn(`[Player] 切断されました (code=${code})${detail}。繋ぎ直します`);
      options.onDisconnected();
      scheduleReconnect();
    });
  }

  return {
    start() {
      connect();
    },

    ack(seq) {
      pendingAck = pendingAck === null ? seq : Math.max(pendingAck, seq);
      if (ackTimer) return;
      // 接続直後の追いつきでは、消費済みの再送が最大 500 件まとめて届く。
      // 累積 ack なので、その塊に対して打つべき ack は最大値の1回だけ
      ackTimer = setTimeout(flushAck, ACK_FLUSH_MS);
      ackTimer.unref();
    },

    dropPendingAck() {
      if (ackTimer) clearTimeout(ackTimer);
      ackTimer = null;
      pendingAck = null;
    },

    close() {
      closed = true;
      // 喋り終えた直後に Ctrl-C しても、間引き中の ack は投げてから閉じる。
      // 落とすと次回起動でその文がもう一度鳴る
      flushAck();
      if (reconnectTimer) clearTimeout(reconnectTimer);
      reconnectTimer = null;
      if (ackTimer) clearTimeout(ackTimer);
      ackTimer = null;
      clearWatchdog();

      const target = socket;
      socket = null;
      if (!target || target.readyState === WebSocket.CLOSED) return Promise.resolve();

      return new Promise<void>((resolve) => {
        target.once("close", () => resolve());
        target.close();
        // 相手が応答しないときのため。close ハンドシェイクを待ち続けない
        setTimeout(() => {
          target.terminate();
          resolve();
        }, 1000).unref();
      });
    },
  };
}
