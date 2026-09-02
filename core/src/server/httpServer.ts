/**
 * HTTP の口。WebSocket と**同じポート**に相乗りする。
 *
 * ```
 * GET /audio/<epoch>-<seq>.wav
 *   200  合成済み、または今から合成して返した（完了までレスポンスを保留する）
 *   403  Origin が allowedOrigins に無い
 *   404  永久に用意できない（キューから消えた / epoch 違い / 読み上げる中身が無い）
 *   503  エンジンに繋がらない・合成が返らない。**あとで取りに来い**
 *
 * /v1/*   設定パネル（#76）の制御 API。中身は `server/controlApi.ts`
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
 *
 * ★★ **判定の順序は「パス → メソッド」。** #76 まではメソッドが先で、
 *   `GET / HEAD / OPTIONS` 以外はルーティングに到達する前に 405 で切られていた。
 *   `PATCH` / `POST` を足すには入れ替えるしかない。副作用として **405 の `Allow` が
 *   リソースごと**になった（RFC 9110 §15.5.6 が要求しているのは元々こちらの形）。
 */

import * as http from "http";
import { parseAudioPath } from "../core/audioPath";
import type { SpeechRecord } from "../core/types";
import { hasSpeakableText } from "../text/speakable";
import { SynthesisUnavailableError, type AudioStore } from "./audioStore";
import type { ControlApi, ControlResponse } from "./controlApi";
import { isLoopbackAddress } from "./loopback";
import { createThrottledWarn } from "./throttledWarn";

export interface HttpServerDeps {
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
   *
   * ★ **関数であること**（`disabled` と同じ理由。config は実行中に読み直される）。値で渡すと、
   *   `PATCH /v1/config` で `synthesisTimeoutMs` を変えたときに**エンジンへのリクエスト期限だけ**が
   *   変わり、ここの打ち切りは再起動まで旧値のまま＝**半分だけ効く**。200 が返って新しい値が
   *   エコーされるぶん、「変えても何も起きない」より見え方が悪い。
   */
  responseTimeoutMs: () => number;
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
  /**
   * 設定パネルの制御 API（→ `server/controlApi.ts`）。
   *
   * ★ 省略可にしていない。渡し忘れると `/v1/*` が黙って 404 になり、
   *   症状は「設定パネルが何も表示しない」で、原因の見当が付かない。
   */
  control: ControlApi;
}

/** 503 のときにクライアントへ渡す再試行の目安 */
const RETRY_AFTER_SECONDS = 1;

/** リクエストボディの上限。設定の差分しか来ないので十分すぎるほど大きい */
const MAX_BODY_BYTES = 64 * 1024;

/**
 * `Allow` に出すときの並び。**リソースごとにここから絞り込む**ので、
 * 表記ゆれ（`GET, HEAD` と `HEAD, GET`）が出ない。
 */
const METHOD_ORDER = ["GET", "HEAD", "POST", "PATCH", "OPTIONS"] as const;

/**
 * 書き込みのメソッド。**3重の絞り**（ループバック限定 / `Origin` 禁止 / `Content-Type` 必須）が
 * 掛かるのはこれだけ。
 */
function isWriteMethod(method: string): boolean {
  return method === "PATCH" || method === "POST";
}

/**
 * ルート表の1件。`methods` は**そのリソースが本来受けるもの**で、`Allow` の素になる。
 */
interface Route {
  readonly methods: readonly string[];
  readonly kind: "audio" | "control";
}

const AUDIO_ROUTE: Route = { methods: ["GET"], kind: "audio" };

/** `/v1/*` のルート表。**パスは完全一致**（正規表現の羅列にしない） */
const CONTROL_ROUTES = new Map<string, Route>([
  ["/v1/health", { methods: ["GET"], kind: "control" }],
  ["/v1/speakers", { methods: ["GET"], kind: "control" }],
  ["/v1/config", { methods: ["GET", "PATCH"], kind: "control" }],
  ["/v1/tts/preview", { methods: ["POST"], kind: "control" }],
  ["/v1/summary/preview", { methods: ["POST"], kind: "control" }],
]);

function resolveRoute(pathname: string): Route | null {
  const control = CONTROL_ROUTES.get(pathname);
  if (control !== undefined) return control;
  // ★ 受け取った文字列をパスの組み立てに使わない。正規表現で `(epoch, seq)` に
  //   分解してから、Map とキューを引く（`speechQueue.read` と同じ論法）
  return parseAudioPath(pathname) === null ? null : AUDIO_ROUTE;
}

/**
 * そのリソース・その相手に許すメソッド。
 *
 * ★ **`GET` を受けるなら `HEAD` も受ける**。`OPTIONS` は常に受ける（プリフライト）。
 *
 * ★ **ループバックでない相手には書き込みメソッドを名乗らない。** どうせ 404 を返すので
 *   `Allow` に載せるのは嘘になるし、「口の存在を見せない」（下の絞り1）とも揃わない。
 */
function allowFor(route: Route, loopback: boolean): string[] {
  const set = new Set<string>(route.methods);
  if (set.has("GET")) set.add("HEAD");
  set.add("OPTIONS");
  return METHOD_ORDER.filter((m) => set.has(m) && (loopback || !isWriteMethod(m)));
}

/** `application/json`（`; charset=utf-8` 付きも通す） */
function isJsonContentType(value: string | undefined): boolean {
  if (typeof value !== "string") return false;
  return (value.split(";")[0] ?? "").trim().toLowerCase() === "application/json";
}

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

/**
 * text/plain で返す。
 *
 * ★ **`destroyed` も見ること。** `writableEnded` は**自分が `end()` を呼んだ後にしか
 *   真にならない**ので、合成を待っている間にクライアントが切ったかどうかは分からない。
 *   破棄済みの応答に `writeHead()` すると、リスナの付いていない `ServerResponse` に
 *   `ERR_STREAM_DESTROYED` が上がり、ただのキャンセルでプロセスのガードまで届く。
 */
function endWith(res: http.ServerResponse, status: number, body: string): void {
  if (res.writableEnded || res.destroyed) return;
  res.writeHead(status, {
    "content-type": "text/plain; charset=utf-8",
    "cache-control": "no-store",
    ...(status === 503 ? { "retry-after": String(RETRY_AFTER_SECONDS) } : {}),
  });
  res.end(body);
}

/**
 * JSON を返す。
 *
 * ★ `HEAD` でも `content-length` は本来の長さを返すこと（RFC 9110 §9.3.2）。
 *   0 にすると、長さを見てから取りに来るクライアントが「中身が無い」と判断する。
 *
 * ★ **`destroyed` も見ること**（理由は `endWith` の ★）。`POST /v1/summary/preview` は
 *   `claude -p` を最大 `aiSummaryTimeoutMs`（既定60秒）待つので、いちばん切られやすい。
 */
function endWithJson(
  res: http.ServerResponse,
  status: number,
  body: unknown,
  headers: Record<string, string>,
  head: boolean,
): void {
  if (res.writableEnded || res.destroyed) return;
  const text = `${JSON.stringify(body)}\n`;
  res.writeHead(status, {
    "content-type": "application/json; charset=utf-8",
    "content-length": String(Buffer.byteLength(text)),
    "cache-control": "no-store",
    ...headers,
  });
  if (head) return void res.end();
  res.end(text);
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

/**
 * ボディの決着。**「大きすぎる」と「切られた」を同じ値にしないこと。**
 *
 * ★ 1つの `null` に畳むと、40バイトのボディが接続リセットで落ちただけで
 *   **413 payload_too_large** として報告される。413 は恒久的な拒否なので、
 *   クライアントは再送しない —— 一時的な転送エラーの正しい復旧ができなくなる。
 */
type BodyResult = { ok: true; text: string } | { ok: false; reason: "too_large" | "aborted" };

/**
 * ボディを上限つきで読む。
 *
 * ★★ **`close` を必ず見ること。** クライアントが送信途中で切ると Node は
 *   `IncomingMessage` に `close` を出すが、**`end` は出さず `error` も保証されない**。
 *   `data` / `end` / `error` の3本だけだと Promise が settle せず、`handleControl` の
 *   `await readBody(...)` が**永久に返らない** —— バッファも `req` / `res` も
 *   ハンドラのクロージャも、プロセスが生きている限り到達可能なまま残る
 *   （設定パネルがキャンセルするたびに1件ずつ増える）。
 */
function readBody(req: http.IncomingMessage, limit: number): Promise<BodyResult> {
  return new Promise((resolve) => {
    const chunks: Buffer[] = [];
    let size = 0;
    let settled = false;
    const finish = (value: BodyResult) => {
      if (settled) return;
      settled = true;
      resolve(value);
    };
    req.on("data", (chunk: Buffer) => {
      size += chunk.length;
      if (size > limit) {
        // ★ 読み捨てを続けること。ここで destroy すると、クライアントは
        //   413 のレスポンスを読む前に接続を切られる
        req.resume();
        finish({ ok: false, reason: "too_large" });
        return;
      }
      chunks.push(chunk);
    });
    req.on("end", () => finish({ ok: true, text: Buffer.concat(chunks).toString("utf-8") }));
    req.on("error", () => finish({ ok: false, reason: "aborted" }));
    // ★ 正常終了の後にも来るが、`settled` で畳まれるので無害
    req.on("close", () => finish({ ok: false, reason: "aborted" }));
  });
}

export function createHttpServer(deps: HttpServerDeps): http.Server {
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
    const pathname = (req.url ?? "").split("?")[0] ?? "";
    const method = req.method ?? "";
    const route = resolveRoute(pathname);
    if (route === null) return endWith(res, 404, "not found\n");

    const loopback = isLoopbackAddress(req.socket.remoteAddress);

    // ★★ 絞り1: 書き込み口はループバックからだけ。**403 ではなく 404。**
    //   403 は「口はあるが権限が無い」と教えることになる。存在そのものを見せない。
    //
    // ★★ **`X-Forwarded-For` を見ないこと。** クライアントが自由に付けられるヘッダなので、
    //   見た瞬間にこの絞りが1行で無効になる（→ `server/loopback.ts`）。
    //
    // ★ **`checkOrigin` より前に置くこと。** 後ろに置くと、許可外 Origin のときだけ
    //   403 が返る＝**口の存在が Origin の違いで漏れる**。
    if (isWriteMethod(method) && !loopback) return endWith(res, 404, "not found\n");

    if (!checkOrigin(req, res, allowed, warn)) return endWith(res, 403, "forbidden\n");

    const allowList = allowFor(route, loopback);
    const allow = allowList.join(", ");

    // ★ プリフライトは `checkOrigin` の**後**。405 で切ると `Access-Control-Allow-Methods` が
    //   返らず、safelist 外のヘッダを付けるブラウザ系クライアントの本リクエストが
    //   ブロックされる（許可 Origin に ACAO を返した手当てもそこへ到達しない）。
    //
    // ★ `Access-Control-Allow-Headers` に `range` を並べないこと。下で
    //   `Accept-Ranges: none` と宣言している以上、**対応していないものを対応していると
    //   言う**ことになる。
    //   ★ `content-type` を返すのは**制御 API のときだけ**。あちらは
    //   `application/json` を必須にしている（下の絞り3）ので、返さないと
    //   ブラウザ系クライアントの `PATCH` がプリフライトで止まる。逆に `/audio/…` で返すと
    //   「対応していないものを対応していると言う」側に倒れる
    if (method === "OPTIONS") {
      res.writeHead(204, {
        allow,
        "access-control-allow-methods": allow,
        ...(route.kind === "control" ? { "access-control-allow-headers": "content-type" } : {}),
      });
      return void res.end();
    }

    // RFC 9110 §15.5.6: 405 は `Allow` を返さなければならない。**リソースごとの値**を返す
    if (!allowList.includes(method)) {
      res.setHeader("allow", allow);
      return endWith(res, 405, "method not allowed\n");
    }

    if (isWriteMethod(method)) {
      // ★★ 絞り2: `Origin` が付いた書き込みは拒否。**付いている＝ WebView / ブラウザから来た。**
      //   `allowedOrigins` に `http://localhost:3000` を足していた人が、そのポートを踏んだ
      //   Web ページに設定を書き換えられる穴を塞ぐ。ネイティブクライアント
      //   （Unity の `UnityWebRequest`）は `Origin` を送らない
      const origin = req.headers.origin;
      if (typeof origin === "string" && origin.length > 0) return endWith(res, 403, "forbidden\n");

      // ★★ 絞り3: `Content-Type: application/json` を必須にする。
      //   simple request では JSON の Content-Type を付けられないので、**プリフライトが必ず走り、
      //   絞り2に掛かる**（CSRF 対策の本体はこの連鎖）
      if (!isJsonContentType(req.headers["content-type"])) {
        return endWith(res, 415, "unsupported media type\n");
      }
    }

    if (route.kind === "control") return handleControl(req, res, pathname, method);
    return handleAudio(res, pathname, method);
  }

  async function handleControl(
    req: http.IncomingMessage,
    res: http.ServerResponse,
    pathname: string,
    method: string,
  ): Promise<void> {
    let body: unknown = undefined;
    if (isWriteMethod(method)) {
      // ★ 使わない場合でもボディは読み切ること。放置すると keep-alive の次の
      //   リクエストがパースできなくなる
      const raw = await readBody(req, MAX_BODY_BYTES);
      if (!raw.ok) {
        // ★ 切られたときは**何も返さない**。相手はもう居ないので、書けば
        //   破棄済みの応答に `writeHead()` することになる（→ `endWith` の ★）
        if (raw.reason === "aborted") return;
        return endWithJson(res, 413, { error: "payload_too_large" }, {}, false);
      }
      if (raw.text.trim().length > 0) {
        try {
          body = JSON.parse(raw.text);
        } catch {
          return endWithJson(res, 400, { error: "invalid_json" }, {}, false);
        }
      }
    }

    const head = method === "HEAD";
    const response = await runControl(pathname, method, body);
    const headers = { ...response.headers, ...(response.status === 429 ? { "retry-after": "1" } : {}) };

    if (response.kind === "wav") {
      if (res.writableEnded || res.destroyed) return;
      res.writeHead(response.status, {
        "content-type": "audio/wav",
        "content-length": String(response.body.byteLength),
        "cache-control": "no-store",
        "accept-ranges": "none",
        ...headers,
      });
      if (head) return void res.end();
      return void res.end(Buffer.from(response.body));
    }
    return endWithJson(res, response.status, response.body, headers, head);
  }

  function runControl(pathname: string, method: string, body: unknown): Promise<ControlResponse> | ControlResponse {
    switch (pathname) {
      case "/v1/health":
        return deps.control.health();
      case "/v1/speakers":
        return deps.control.speakers();
      case "/v1/config":
        return method === "PATCH" ? deps.control.patchConfig(body ?? null) : deps.control.getConfig();
      case "/v1/tts/preview":
        return deps.control.ttsPreview();
      case "/v1/summary/preview":
        return deps.control.summaryPreview();
      default:
        // resolveRoute を通っている以上ここには来ない。来たら表とこの switch がズレている
        return { status: 404, kind: "json", body: { error: "not_found" } };
    }
  }

  async function handleAudio(res: http.ServerResponse, pathname: string, method: string): Promise<void> {
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
    // ★ リクエストごとに読む（→ deps の ★）。catch からも参照するので try の外で取る
    const deadlineMs = deps.responseTimeoutMs();
    let wav: ArrayBuffer;
    try {
      wav = await withDeadline(deps.store.get(key.epoch, key.seq, record.text), deadlineMs);
    } catch (err) {
      if (err instanceof SynthesisUnavailableError) {
        deps.onSynthesisFailed?.();
        warn(`[HTTP] seq=${key.seq} の合成に失敗しました（あとで取りに来てもらいます）: ${err.message}`);
        return endWith(res, 503, `synthesis unavailable: ${err.message}\n`);
      }
      if (err instanceof ResponseDeadlineError) {
        warn(`[HTTP] 合成が ${deadlineMs}ms で終わらないので一旦返します（合成は続行中）`);
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
    if (method === "HEAD") return void res.end();
    res.end(Buffer.from(wav));
  }
}
