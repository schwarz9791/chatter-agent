import { describe, it, expect, afterEach, vi } from "vitest";
import * as http from "http";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { createAudioStore } from "./audioStore";
import { TtsHttpError } from "../tts/voicevoxClient";
import { createHttpServer, type HttpServerDeps } from "./httpServer";
import type { ControlApi, ControlResponse } from "./controlApi";
import type { SpeechRecord } from "../core/types";

const HOST = "127.0.0.1";
const EPOCH = "gen-1";

const servers: http.Server[] = [];
const tmpDirs: string[] = [];

afterEach(async () => {
  vi.restoreAllMocks();
  for (const d of tmpDirs.splice(0)) fs.rmSync(d, { recursive: true, force: true });
  await Promise.all(
    servers.splice(0).map(
      (server) =>
        new Promise<void>((done) => {
          server.closeAllConnections();
          server.close(() => done());
        }),
    ),
  );
});

function record(seq: number, text = `文${seq}。`, epoch = EPOCH): SpeechRecord {
  return {
    epoch,
    seq,
    ts: "2026-08-15T00:00:00.000Z",
    source: "claude-code",
    sessionId: null,
    turnId: null,
    messageId: null,
    kind: "assistant",
    text,
    emotion: "neutral",
  };
}

const VOICE = { baseUrl: "http://127.0.0.1:10101", speakerId: 888753760, speedScale: 1.0 };

function wavOf(bytes: number): ArrayBuffer {
  return new ArrayBuffer(bytes);
}

/**
 * 制御 API のスタブ。**このファイルが見るのはルーティングと絞りだけ**なので、
 * 中身は「呼ばれたことが分かる 200」で足りる（制御 API の中身は `controlApi.test.ts`）。
 */
function stubControl(): ControlApi {
  const ok = (name: string): ControlResponse => ({ status: 200, kind: "json", body: { called: name } });
  return {
    health: () => ok("health"),
    speakers: () => Promise.resolve(ok("speakers")),
    getConfig: () => ok("getConfig"),
    patchConfig: (body) => ({ status: 200, kind: "json", body: { called: "patchConfig", body } }),
    ttsPreview: () => Promise.resolve({ status: 200, kind: "wav", body: wavOf(44) }),
    summaryPreview: () => Promise.resolve(ok("summaryPreview")),
  };
}

async function start(overrides: Partial<HttpServerDeps> = {}): Promise<string> {
  const server = createHttpServer({
    store: createAudioStore({ currentVoice: () => VOICE, synthesize: () => Promise.resolve(wavOf(12)) }),
    lookup: (seq: number) => (seq === 1 ? record(1) : null),
    allowedOrigins: [],
    disabled: () => false,
    responseTimeoutMs: () => 5_000,
    control: stubControl(),
    ...overrides,
  });
  servers.push(server);
  await new Promise<void>((done) => server.listen(0, HOST, done));
  const address = server.address();
  if (address === null || typeof address === "string") throw new Error("bind に失敗しました");
  return `http://${HOST}:${address.port}`;
}

describe("GET /audio/<epoch>-<seq>.wav", () => {
  it("合成して WAV を返す", async () => {
    const base = await start();
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`);

    expect(res.status).toBe(200);
    expect(res.headers.get("content-type")).toBe("audio/wav");
    expect((await res.arrayBuffer()).byteLength).toBe(12);
  });

  it("Range は実装しない（Unity / ExoPlayer が投げてきても全体を返す）", async () => {
    const base = await start();
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`, { headers: { range: "bytes=0-3" } });

    expect(res.status).toBe(200);
    expect(res.headers.get("accept-ranges")).toBe("none");
    expect((await res.arrayBuffer()).byteLength).toBe(12);
  });

  it("★ キューから消えた entry は 404（永久に用意できない）", async () => {
    const base = await start({ lookup: () => null });
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`);
    expect(res.status).toBe(404);
  });

  it("★ 世代違いの URL は 404（採番のやり直しを跨いだ古い URL）", async () => {
    const base = await start();
    const res = await fetch(`${base}/audio/gen-9-000000000001.wav`);
    expect(res.status).toBe(404);
  });

  it("読み上げる中身が無い文は 404（約物だけの断片）", async () => {
    const base = await start({ lookup: () => record(1, "！") });
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`);
    expect(res.status).toBe(404);
  });

  it("ttsEnabled=false なら 404", async () => {
    const base = await start({ disabled: () => true });
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`);
    expect(res.status).toBe(404);
  });

  it("★ 合成できないときは 503 + Retry-After（404 と混ぜない）", async () => {
    vi.spyOn(console, "warn").mockImplementation(() => {});
    const base = await start({
      store: createAudioStore({
        currentVoice: () => VOICE,
        synthesize: () => Promise.reject(new Error("ECONNREFUSED 127.0.0.1:10101")),
      }),
    });
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`);

    expect(res.status).toBe(503);
    expect(res.headers.get("retry-after")).toBe("1");
    // ★ 理由を本文にも載せる。無音の原因はログにしか出ないので、クライアント側にも渡す
    expect(await res.text()).toContain("ECONNREFUSED");
  });

  it("★ エンジンが 4xx を返しても 503（404 にすると本文が物理削除される）", async () => {
    vi.spyOn(console, "warn").mockImplementation(() => {});
    const base = await start({
      store: createAudioStore({
        currentVoice: () => VOICE,
        synthesize: () => Promise.reject(new TtsHttpError("audio_query", 422, "speaker not found")),
      }),
    });
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`);

    expect(res.status).toBe(503);
    expect(await res.text()).toContain("speaker not found");
  });

  it("合成が失敗したら onSynthesisFailed が呼ばれる（診断の再実行）", async () => {
    vi.spyOn(console, "warn").mockImplementation(() => {});
    const onSynthesisFailed = vi.fn();
    const base = await start({
      store: createAudioStore({ currentVoice: () => VOICE, synthesize: () => Promise.reject(new Error("down")) }),
      onSynthesisFailed,
    });
    await (await fetch(`${base}/audio/${EPOCH}-000000000001.wav`)).text();

    expect(onSynthesisFailed).toHaveBeenCalled();
  });

  it("★ 応答だけ期限で打ち切り、合成は走らせたままにする（次の GET はキャッシュに当たる）", async () => {
    const warnSpy = vi.spyOn(console, "warn").mockImplementation(() => {});
    let resolveSynth: ((wav: ArrayBuffer) => void) | undefined;
    const synthesize = vi.fn(
      () =>
        new Promise<ArrayBuffer>((resolve) => {
          resolveSynth = resolve;
        }),
    );
    const base = await start({
      store: createAudioStore({ currentVoice: () => VOICE, synthesize }),
      responseTimeoutMs: () => 150,
    });

    const first = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`);
    expect(first.status).toBe(503);
    expect(await first.text()).toContain("in progress");
    expect(warnSpy).toHaveBeenCalled();

    // 合成は打ち切られていないので、終わればキャッシュに入る
    resolveSynth?.(wavOf(9));
    await new Promise((r) => setTimeout(r, 20));

    const second = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`);
    expect(second.status).toBe(200);
    expect((await second.arrayBuffer()).byteLength).toBe(9);
    // 打ち切っても合成をやり直していない
    expect(synthesize).toHaveBeenCalledTimes(1);
  });

  /**
   * ★★ #76 のレビュー A-4。値で受けていた頃は、`PATCH /v1/config` で
   *   `synthesisTimeoutMs` を変えても**エンジンへのリクエスト期限しか**変わらず、
   *   ここの打ち切りは再起動まで旧値のままだった（**半分だけ効く**）。
   */
  it("★★ 応答の期限はリクエストごとに読み直す", async () => {
    vi.spyOn(console, "warn").mockImplementation(() => {});
    let timeoutMs = 50;
    const base = await start({
      lookup: (seq: number) => record(seq),
      store: createAudioStore({
        currentVoice: () => VOICE,
        synthesize: () => new Promise<ArrayBuffer>((resolve) => setTimeout(() => resolve(wavOf(9)), 200)),
      }),
      responseTimeoutMs: () => timeoutMs,
    });

    expect((await fetch(`${base}/audio/${EPOCH}-000000000001.wav`)).status).toBe(503);

    // 設定を変えた（サーバーは再起動していない）。**別の seq** で確かめること ——
    // 同じ seq は打ち切った合成がキャッシュに入るので、期限を見ずに 200 になる
    timeoutMs = 5_000;
    expect((await fetch(`${base}/audio/${EPOCH}-000000000002.wav`)).status).toBe(200);
  });

  it("★ 503 の warn は間引かれる（先読み窓 × 1秒で1日34万行になる）", async () => {
    const warnSpy = vi.spyOn(console, "warn").mockImplementation(() => {});
    const base = await start({
      store: createAudioStore({ currentVoice: () => VOICE, synthesize: () => Promise.reject(new Error("down")) }),
    });

    for (let i = 0; i < 10; i++) {
      await (await fetch(`${base}/audio/${EPOCH}-000000000001.wav`)).text();
    }

    expect(warnSpy.mock.calls.length).toBe(1);
  });

  it("★ パストラバーサルを弾く", async () => {
    const base = await start({ lookup: () => record(1) });
    for (const path of ["/audio/../../etc/passwd", "/audio/%2e%2e%2f%2e%2e%2fetc%2fpasswd", "/etc/passwd", "/"]) {
      expect((await fetch(`${base}${path}`)).status, path).toBe(404);
    }
  });

  it("GET / HEAD / OPTIONS 以外は 405 で、Allow を返す（RFC 9110 §15.5.6）", async () => {
    const base = await start();
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`, { method: "POST" });
    expect(res.status).toBe(405);
    expect(res.headers.get("allow")).toBe("GET, HEAD, OPTIONS");
  });

  it("★ OPTIONS は 204 + Allow-Methods（405 で切るとプリフライトが通らない）", async () => {
    const base = await start({ allowedOrigins: ["tauri://localhost"] });
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`, {
      method: "OPTIONS",
      headers: { origin: "tauri://localhost" },
    });

    expect(res.status).toBe(204);
    expect(res.headers.get("access-control-allow-methods")).toBe("GET, HEAD, OPTIONS");
    expect(res.headers.get("access-control-allow-origin")).toBe("tauri://localhost");
    // ★ Range は実装しないので、対応していると言わない
    expect(res.headers.get("access-control-allow-headers")).toBeNull();
  });

  it("プリフライトも Origin 検査を通る", async () => {
    vi.spyOn(console, "warn").mockImplementation(() => {});
    const base = await start();
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`, {
      method: "OPTIONS",
      headers: { origin: "http://evil.example.com" },
    });
    expect(res.status).toBe(403);
  });

  it("HEAD は本体を返さずヘッダだけ返す", async () => {
    const base = await start();
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`, { method: "HEAD" });
    expect(res.status).toBe(200);
    expect(res.headers.get("content-length")).toBe("12");
  });
});

describe("Origin（WebSocket と同じ規則）", () => {
  it("許可リストに無い Origin は 403", async () => {
    vi.spyOn(console, "warn").mockImplementation(() => {});
    const base = await start();
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`, {
      headers: { origin: "http://evil.example.com" },
    });
    expect(res.status).toBe(403);
  });

  it("★ 許可した Origin には Access-Control-Allow-Origin を返す（403 を返さないだけでは足りない）", async () => {
    const base = await start({ allowedOrigins: ["tauri://localhost"] });
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`, { headers: { origin: "tauri://localhost" } });

    expect(res.status).toBe(200);
    // これが無いと、サーバーが許可していてもブラウザ側で音声だけブロックされる
    expect(res.headers.get("access-control-allow-origin")).toBe("tauri://localhost");
    expect(res.headers.get("vary")).toBe("Origin");
  });

  it("★ Retry-After を expose する（safelist に無いのでブラウザ / WebView の JS から読めない）", async () => {
    vi.spyOn(console, "warn").mockImplementation(() => {});
    const base = await start({
      allowedOrigins: ["tauri://localhost"],
      store: createAudioStore({
        currentVoice: () => VOICE,
        synthesize: () => Promise.reject(new Error("ECONNREFUSED")),
      }),
    });
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`, { headers: { origin: "tauri://localhost" } });
    await res.text();

    // #29 が主目的にしている XR / Unity WebGL / Electron renderer は、これが無いと
    // 503 のバックオフ情報を受け取れない
    expect(res.status).toBe(503);
    expect(res.headers.get("access-control-expose-headers")).toBe("retry-after");
  });

  it("Origin を送らないクライアント（Unity ネイティブなど）は素通り", async () => {
    const base = await start();
    expect((await fetch(`${base}/audio/${EPOCH}-000000000001.wav`)).status).toBe(200);
  });
});

// ───────────────────────────────────────────────────────────────────────────
// 制御 API（/v1/*）のルーティングと、書き込み口の3重の絞り（#76）
//
// ★ ここで見るのは**ルーティングと絞りだけ**。制御 API の中身は `controlApi.test.ts`。
// ───────────────────────────────────────────────────────────────────────────

/**
 * **ループバックでない相手**を再現する。
 *
 * ★ TCP で LAN のアドレスから繋ぐテストは環境依存になる（CI にそのインターフェースがあるとは
 *   限らない）。Unix ドメインソケットなら `req.socket.remoteAddress` が `undefined` になり、
 *   `isLoopbackAddress` の「判定できないものは false に倒す」経路をそのまま通る。
 *   **判定関数を注入で差し替えていない**ので、本番と同じコードが動いている。
 */
async function startUnix(overrides: Partial<HttpServerDeps> = {}): Promise<string> {
  const server = createHttpServer({
    store: createAudioStore({ currentVoice: () => VOICE, synthesize: () => Promise.resolve(wavOf(12)) }),
    lookup: (seq: number) => (seq === 1 ? record(1) : null),
    allowedOrigins: [],
    disabled: () => false,
    responseTimeoutMs: () => 5_000,
    control: stubControl(),
    ...overrides,
  });
  servers.push(server);
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "cm-"));
  tmpDirs.push(dir);
  const socketPath = path.join(dir, "s.sock");
  await new Promise<void>((done) => server.listen(socketPath, done));
  return socketPath;
}

interface RawResponse {
  status: number;
  headers: http.IncomingHttpHeaders;
  text: string;
}

function requestUnix(socketPath: string, options: http.RequestOptions & { body?: string }): Promise<RawResponse> {
  return new Promise((resolve, reject) => {
    const req = http.request({ socketPath, ...options }, (res) => {
      const chunks: Buffer[] = [];
      res.on("data", (c: Buffer) => chunks.push(c));
      res.on("end", () =>
        resolve({ status: res.statusCode ?? 0, headers: res.headers, text: Buffer.concat(chunks).toString("utf-8") }),
      );
    });
    req.on("error", reject);
    req.end(options.body);
  });
}

const JSON_HEADERS = { "content-type": "application/json" };

describe("制御 API のルーティング（#76）", () => {
  it("GET /v1/health は制御 API に届く", async () => {
    const base = await start();
    const res = await fetch(`${base}/v1/health`);
    expect(res.status).toBe(200);
    expect(await res.json()).toEqual({ called: "health" });
  });

  it("GET /v1/config と PATCH /v1/config は同じパスで振り分けられる", async () => {
    const base = await start();
    expect(await (await fetch(`${base}/v1/config`)).json()).toEqual({ called: "getConfig" });

    const patched = await fetch(`${base}/v1/config`, {
      method: "PATCH",
      headers: JSON_HEADERS,
      body: JSON.stringify({ ttsSpeedScale: 1.5 }),
    });
    expect(await patched.json()).toEqual({ called: "patchConfig", body: { ttsSpeedScale: 1.5 } });
  });

  it("POST /v1/tts/preview は WAV を返す", async () => {
    const base = await start();
    const res = await fetch(`${base}/v1/tts/preview`, { method: "POST", headers: JSON_HEADERS, body: "{}" });
    expect(res.status).toBe(200);
    expect(res.headers.get("content-type")).toBe("audio/wav");
  });

  it("表に無いパスは 404", async () => {
    const base = await start();
    expect((await fetch(`${base}/v1/nope`)).status).toBe(404);
    expect((await fetch(`${base}/v1/`)).status).toBe(404);
  });

  /**
   * ★★ #76 まではメソッド判定が先で、`GET / HEAD / OPTIONS` 以外はルーティングに
   *   到達する前に 405 で切られていた。順序を入れ替えた証拠
   */
  it("★★ PATCH がルーティングまで届く（メソッドで先に切られない）", async () => {
    const base = await start();
    const res = await fetch(`${base}/v1/config`, { method: "PATCH", headers: JSON_HEADERS, body: "{}" });
    expect(res.status).toBe(200);
  });

  /** ★ 405 の `Allow` は**リソースごと**（RFC 9110 §15.5.6） */
  it("★ 405 の Allow はリソースごと", async () => {
    const base = await start();

    const audio = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`, { method: "PATCH" });
    expect(audio.status).toBe(405);
    expect(audio.headers.get("allow")).toBe("GET, HEAD, OPTIONS");

    const health = await fetch(`${base}/v1/health`, { method: "POST", headers: JSON_HEADERS, body: "{}" });
    expect(health.status).toBe(405);
    expect(health.headers.get("allow")).toBe("GET, HEAD, OPTIONS");

    const config = await fetch(`${base}/v1/config`, { method: "DELETE" });
    expect(config.status).toBe(405);
    expect(config.headers.get("allow")).toBe("GET, HEAD, PATCH, OPTIONS");

    const preview = await fetch(`${base}/v1/tts/preview`, { method: "GET" });
    expect(preview.status).toBe(405);
    expect(preview.headers.get("allow")).toBe("POST, OPTIONS");
  });

  it("OPTIONS は 204 + リソースごとの Allow。制御 API だけ content-type を許す", async () => {
    const base = await start();

    const config = await fetch(`${base}/v1/config`, { method: "OPTIONS" });
    expect(config.status).toBe(204);
    expect(config.headers.get("allow")).toBe("GET, HEAD, PATCH, OPTIONS");
    expect(config.headers.get("access-control-allow-headers")).toBe("content-type");

    // ★ /audio/… では返さない（Range を実装していないのと同じ理由で、対応していないと言う）
    const audio = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`, { method: "OPTIONS" });
    expect(audio.headers.get("access-control-allow-headers")).toBeNull();
  });

  it("ボディが上限を超えたら 413", async () => {
    const base = await start();
    const res = await fetch(`${base}/v1/config`, {
      method: "PATCH",
      headers: JSON_HEADERS,
      body: JSON.stringify({ pad: "x".repeat(80 * 1024) }),
    });
    expect(res.status).toBe(413);
  });

  /**
   * ★★ #76 のレビュー A-3。`close` を見ていなかった頃は、送信途中で切られると
   *   `readBody` の Promise が settle せず `handleControl` の `await` が**永久に返らなかった**
   *   （バッファも `req` / `res` もクロージャも、プロセスが生きている限り残る）。
   *   ★ 漏れそのものは外から観測できないので、ここで固定するのは
   *   「切られても応答を書かず、サーバーは生き続ける」ところまで。
   */
  it("★ 送信途中で切られてもサーバーは生き続ける", async () => {
    const base = await start();
    const url = new URL(base);

    await new Promise<void>((done) => {
      const req = http.request({
        host: url.hostname,
        port: Number(url.port),
        path: "/v1/config",
        method: "PATCH",
        headers: { "content-type": "application/json", "content-length": "100" },
      });
      req.on("error", () => {});
      // content-length ぶん送らずに切る
      req.write("{");
      setTimeout(() => {
        req.destroy();
        done();
      }, 20);
    });

    expect((await fetch(`${base}/v1/health`)).status).toBe(200);
  });

  it("JSON として読めないボディは 400", async () => {
    const base = await start();
    const res = await fetch(`${base}/v1/config`, { method: "PATCH", headers: JSON_HEADERS, body: "{ 壊れている" });
    expect(res.status).toBe(400);
    expect(await res.json()).toEqual({ error: "invalid_json" });
  });
});

describe("書き込み口の3重の絞り（#76）", () => {
  /**
   * ★★ 絞り1。**403 ではなく 404** —— 403 は「口はあるが権限が無い」と教えることになる。
   *   存在そのものを見せない
   */
  it("★★ ループバックでない相手の PATCH は 404（403 ではない）", async () => {
    const socketPath = await startUnix();
    const res = await requestUnix(socketPath, {
      method: "PATCH",
      path: "/v1/config",
      headers: JSON_HEADERS,
      body: "{}",
    });
    expect(res.status).toBe(404);
  });

  it("★★ ループバックでない相手の POST も 404", async () => {
    const socketPath = await startUnix();
    const res = await requestUnix(socketPath, {
      method: "POST",
      path: "/v1/tts/preview",
      headers: JSON_HEADERS,
      body: "{}",
    });
    expect(res.status).toBe(404);
  });

  /** ★ 読みは絞らない（LAN の XR クライアントが話者一覧を引けなくなる） */
  it("★ ループバックでない相手でも GET は通る", async () => {
    const socketPath = await startUnix();
    const res = await requestUnix(socketPath, { method: "GET", path: "/v1/config" });
    expect(res.status).toBe(200);
    expect(JSON.parse(res.text)).toEqual({ called: "getConfig" });
  });

  /** ★ `Allow` に載せるのは「その相手が使えるメソッド」。404 になるものを名乗らない */
  it("★ ループバックでない相手には書き込みメソッドを名乗らない", async () => {
    const socketPath = await startUnix();
    const res = await requestUnix(socketPath, { method: "OPTIONS", path: "/v1/config" });
    expect(res.status).toBe(204);
    expect(res.headers.allow).toBe("GET, HEAD, OPTIONS");
  });

  /**
   * ★★ 絞り2。`Origin` が付く＝ WebView / ブラウザから張られた。`allowedOrigins` に
   *   開発サーバーのポートを足していた人が、そのポートを踏んだ Web ページに
   *   設定を書き換えられる穴を塞ぐ
   */
  it("★★ Origin が付いた書き込みは、許可済みの Origin でも 403", async () => {
    const base = await start({ allowedOrigins: ["http://localhost:3000"] });
    const res = await fetch(`${base}/v1/config`, {
      method: "PATCH",
      headers: { ...JSON_HEADERS, origin: "http://localhost:3000" },
      body: "{}",
    });
    expect(res.status).toBe(403);
  });

  it("Origin が付いていない書き込みは通る（ネイティブクライアント）", async () => {
    const base = await start({ allowedOrigins: ["http://localhost:3000"] });
    const res = await fetch(`${base}/v1/config`, { method: "PATCH", headers: JSON_HEADERS, body: "{}" });
    expect(res.status).toBe(200);
  });

  /**
   * ★★ 絞り3。simple request では JSON の Content-Type を付けられないので、
   *   **プリフライトが必ず走って絞り2に掛かる**（CSRF 対策の本体はこの連鎖）
   */
  it("★★ Content-Type が application/json でなければ 415", async () => {
    const base = await start();
    expect((await fetch(`${base}/v1/config`, { method: "PATCH", body: "{}" })).status).toBe(415);
    expect(
      (
        await fetch(`${base}/v1/config`, {
          method: "PATCH",
          headers: { "content-type": "text/plain" },
          body: "{}",
        })
      ).status,
    ).toBe(415);
  });

  it("charset 付きの application/json は通る", async () => {
    const base = await start();
    const res = await fetch(`${base}/v1/config`, {
      method: "PATCH",
      headers: { "content-type": "application/json; charset=utf-8" },
      body: "{}",
    });
    expect(res.status).toBe(200);
  });

  /** 読み（GET）には Content-Type を要求しない */
  it("GET には Content-Type を要求しない", async () => {
    const base = await start();
    expect((await fetch(`${base}/v1/config`)).status).toBe(200);
  });
});
