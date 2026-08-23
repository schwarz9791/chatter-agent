/**
 * サーバー合成（#29）の受け入れ確認。
 *
 *   cd core && npm run build && npm run verify:tts
 *
 * **AivisSpeech は要らない。** 合成エンジンはスタブ HTTP サーバーに差し替える。
 * 見るのは「本物の `chatter-agent-server` が、1つのポートで WebSocket と音声の HTTP を
 * 同時に受け、合成をいつ・何回走らせるか」。
 *
 * ★ ここが `verify:player` と分かれているのは、**player を挟まずに**サーバーの契約だけを
 *   見たいから。player を通すと「合成が1回だったのは先読み窓が小さいからでは？」のように、
 *   落ちたときの原因がサーバー側かクライアント側か切り分けられなくなる。
 *
 * 使い捨ての XDG_CONFIG_HOME を掘るので、実際の ~/.config/chatter-agent は汚さない。
 * 127.0.0.1 に bind するので macOS のローカルネットワーク許可ダイアログも出ない。
 */

import { spawn } from "node:child_process";
import * as fs from "node:fs";
import * as http from "node:http";
import * as os from "node:os";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { WebSocket } from "ws";

const CORE = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const SERVER = path.join(CORE, "dist", "chatter-agent-server.mjs");
const PORT = 18572;
const SPEAKER_ID = 888753760;
const EPOCH = "verify-tts-1";

if (!fs.existsSync(SERVER)) {
  console.error(`server のバンドルがありません: ${SERVER}\n先に core/ で npm run build を実行してください。`);
  process.exit(1);
}

const failures = [];
function check(label, ok, detail) {
  if (!ok) failures.push(label);
  const mark = ok ? "\x1b[32mPASS\x1b[0m" : "\x1b[31mFAIL\x1b[0m";
  console.log(`${mark}  ${label}${detail && !ok ? `\n      ${detail}` : ""}`);
}
function show(title) {
  console.log(`\n\x1b[1m--- ${title} ---\x1b[0m`);
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function until(predicate, timeoutMs = 10_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (predicate()) return true;
    await sleep(20);
  }
  return false;
}

// ── 使い捨てのランタイムルート ─────────────────────────────────────────────

const root = fs.mkdtempSync(path.join(os.tmpdir(), "chatter-agent-tts-"));
const runtime = path.join(root, "chatter-agent");
const queueDir = path.join(runtime, "speech");
fs.mkdirSync(queueDir, { recursive: true });

let seqCounter = 0;
function enqueue(text, overrides = {}) {
  seqCounter++;
  const record = {
    epoch: EPOCH,
    seq: seqCounter,
    ts: new Date().toISOString(),
    source: "claude-code",
    sessionId: "sess-1",
    turnId: "turn-1",
    messageId: "m1",
    kind: "assistant",
    text,
    emotion: "neutral",
    ...overrides,
  };
  const name = `${String(record.seq).padStart(12, "0")}.json`;
  fs.writeFileSync(path.join(queueDir, name), `${JSON.stringify(record)}\n`);
  return record;
}

function audioPath(record) {
  return `/audio/${record.epoch}-${String(record.seq).padStart(12, "0")}.wav`;
}

// ── スタブの合成エンジン ───────────────────────────────────────────────────

/** 長さだけ正しい RIFF/PCM */
function makeWav(seconds) {
  const sampleRate = 24000;
  const dataBytes = Math.round(sampleRate * 2 * seconds);
  const buf = Buffer.alloc(44 + dataBytes);
  buf.write("RIFF", 0);
  buf.writeUInt32LE(36 + dataBytes, 4);
  buf.write("WAVE", 8);
  buf.write("fmt ", 12);
  buf.writeUInt32LE(16, 16);
  buf.writeUInt16LE(1, 20);
  buf.writeUInt16LE(1, 22);
  buf.writeUInt32LE(sampleRate, 24);
  buf.writeUInt32LE(sampleRate * 2, 28);
  buf.writeUInt16LE(2, 32);
  buf.writeUInt16LE(16, 34);
  buf.write("data", 36);
  buf.writeUInt32LE(dataBytes, 40);
  return buf;
}
const WAV = makeWav(0.2);

/** 合成に来たテキスト。「いつ・何回」合成したかを見る唯一の窓 */
const synthesized = [];
/** true の間、エンジンは落ちているものとして振る舞う */
let engineDown = false;
/** 合成を遅らせる（同時要求のまとめを観測するため） */
let synthesisDelayMs = 0;

const engine = http.createServer((req, res) => {
  const url = new URL(req.url, "http://x");
  if (engineDown) return void res.destroy();

  if (url.pathname === "/speakers") {
    res.writeHead(200, { "content-type": "application/json" });
    res.end(JSON.stringify([{ name: "スタブ", speaker_uuid: "u", styles: [{ id: SPEAKER_ID, name: "ノーマル" }] }]));
    return;
  }
  if (url.pathname === "/audio_query") {
    synthesized.push(url.searchParams.get("text") ?? "");
    setTimeout(() => {
      if (res.writableEnded) return;
      res.writeHead(200, { "content-type": "application/json" });
      res.end(JSON.stringify({ accent_phrases: [] }));
    }, synthesisDelayMs);
    return;
  }
  if (url.pathname === "/synthesis") {
    req.on("data", () => {});
    req.on("end", () => {
      res.writeHead(200, { "content-type": "audio/wav" });
      res.end(WAV);
    });
    return;
  }
  res.writeHead(404).end();
});

// ── server の起動 ──────────────────────────────────────────────────────────

const base = `http://127.0.0.1:${PORT}`;
let server;
let serverLog = "";

function serverEnv(overrides = {}) {
  return {
    ...process.env,
    XDG_CONFIG_HOME: root,
    CHATTER_AGENT_HOST: "127.0.0.1",
    CHATTER_AGENT_PORT: String(PORT),
    CHATTER_AGENT_TTS_URL: `http://127.0.0.1:${engine.address().port}`,
    CHATTER_AGENT_TTS_SPEAKER_ID: String(SPEAKER_ID),
    CHATTER_AGENT_SYNTHESIS_TIMEOUT_MS: "3000",
    ...overrides,
  };
}

async function startServer(env) {
  serverLog = "";
  server = spawn(process.execPath, [SERVER], { env, stdio: ["ignore", "pipe", "pipe"] });
  server.stdout.on("data", (d) => (serverLog += d));
  server.stderr.on("data", (d) => (serverLog += d));
  const up = await until(() => serverLog.includes("[Server] Ready"), 15_000);
  if (!up) throw new Error(`server が起動しませんでした:\n${serverLog}`);
}

async function stopServer() {
  if (!server || server.exitCode !== null) return;
  const dead = new Promise((done) => server.once("exit", done));
  server.kill("SIGTERM");
  await Promise.race([dead, sleep(4000)]);
  if (server.exitCode === null) server.kill("SIGKILL");
  await sleep(200);
}

/** 接続の前に message ハンドラを張る（接続直後の追いつきは同期で来る） */
function connect() {
  const socket = new WebSocket(`ws://127.0.0.1:${PORT}`);
  const frames = [];
  socket.on("message", (data) => frames.push(JSON.parse(String(data))));
  return new Promise((resolve, reject) => {
    socket.once("open", () =>
      resolve({
        frames,
        ack: (seq) => socket.send(JSON.stringify({ type: "spoken", seq, epoch: EPOCH })),
        close: () =>
          new Promise((done) => {
            socket.once("close", done);
            socket.close();
          }),
      }),
    );
    socket.once("error", reject);
  });
}

function cleanup() {
  server?.kill("SIGKILL");
  engine.close();
  fs.rmSync(root, { recursive: true, force: true });
}
process.on("exit", cleanup);

// ── シナリオ ───────────────────────────────────────────────────────────────

try {
  await new Promise((done) => engine.listen(0, "127.0.0.1", done));
  await startServer(serverEnv());

  {
    show("① 誰も繋いでいない間は合成しない");
    const record = enqueue("だれも聞いていません。");
    await sleep(500);
    check(
      "★ エンジンを一度も叩かない（GET が来て初めて合成する）",
      synthesized.length === 0,
      JSON.stringify(synthesized),
    );

    show("② フレームには音声の相対パスが載る");
    const client = await connect();
    const arrived = await until(() => client.frames.length === 1);
    check("配信された", arrived, JSON.stringify(client.frames));
    check(
      "★ 相対パス（サーバーは自分の到達アドレスを知らない）",
      client.frames[0]?.audio?.path === audioPath(record),
      JSON.stringify(client.frames[0]?.audio),
    );
    check("配信しただけでは合成しない", synthesized.length === 0, JSON.stringify(synthesized));

    show("③ GET で初めて合成し、WAV が返る");
    const res = await fetch(`${base}${audioPath(record)}`);
    const body = await res.arrayBuffer();
    check("200 が返る", res.status === 200, `status=${res.status}`);
    check(
      "content-type は audio/wav",
      res.headers.get("content-type") === "audio/wav",
      res.headers.get("content-type"),
    );
    check("WAV の中身が返る", body.byteLength === WAV.byteLength, `${body.byteLength} vs ${WAV.byteLength}`);
    check("エンジンを1回だけ叩いた", synthesized.length === 1, JSON.stringify(synthesized));

    show("④ ★ 同じ文を複数クライアントが同時に取りに来ても、合成は1回");
    synthesisDelayMs = 300;
    const shared = enqueue("ふたりが同時に取りに来ます。");
    await until(() => client.frames.length === 2);
    const before = synthesized.length;
    const [a, b, c] = await Promise.all([
      fetch(`${base}${audioPath(shared)}`),
      fetch(`${base}${audioPath(shared)}`),
      fetch(`${base}${audioPath(shared)}`),
    ]);
    await Promise.all([a.arrayBuffer(), b.arrayBuffer(), c.arrayBuffer()]);
    check(
      "3件とも 200",
      [a, b, c].every((r) => r.status === 200),
      [a, b, c].map((r) => r.status).join(","),
    );
    check("★ 合成は1回だけ", synthesized.length === before + 1, `${before} → ${synthesized.length}`);
    synthesisDelayMs = 0;

    show("⑤ 2度目の GET はキャッシュから返る");
    const cachedBefore = synthesized.length;
    await (await fetch(`${base}${audioPath(record)}`)).arrayBuffer();
    check("エンジンを叩かない", synthesized.length === cachedBefore, JSON.stringify(synthesized.slice(cachedBefore)));

    show("⑥ 用意できないものは 404（永久に無い）");
    const wrongEpoch = `/audio/other-epoch-${String(record.seq).padStart(12, "0")}.wav`;
    check("★ 世代違いの URL", (await fetch(`${base}${wrongEpoch}`)).status === 404, wrongEpoch);
    check("キューに無い seq", (await fetch(`${base}/audio/${EPOCH}-000000009999.wav`)).status === 404);
    for (const bad of ["/audio/../../etc/passwd", "/audio/%2e%2e%2f%2e%2e%2fetc%2fpasswd", "/etc/passwd", "/"]) {
      check(`★ パストラバーサルを弾く: ${bad}`, (await fetch(`${base}${bad}`)).status === 404);
    }

    show("⑦ Origin（WebSocket と同じ規則）");
    const rejected = await fetch(`${base}${audioPath(record)}`, { headers: { origin: "http://evil.example.com" } });
    check("許可リストに無い Origin は 403", rejected.status === 403, `status=${rejected.status}`);

    show("⑧ ack でキューから消えたら 404 になる");
    client.ack(record.seq);
    // until は同期の述語しか扱えないので、ここは自前で回す（削除は server の 50ms ポーリング外）
    let status = 0;
    for (let i = 0; i < 50 && status !== 404; i++) {
      status = (await fetch(`${base}${audioPath(record)}`)).status;
      if (status !== 404) await sleep(100);
    }
    check("★ ack 済みの音声は配らない（本文の権威はキュー）", status === 404, `status=${status}`);

    await client.close();
  }

  {
    show("⑨ ★ エンジンが落ちていたら 503（テキストの配信は止まらない）");
    engineDown = true;
    const client = await connect();
    const record = enqueue("エンジンが落ちている間の発言です。");
    const arrived = await until(() => client.frames.some((f) => f.seq === record.seq), 5000);
    check("★ テキストは届く（無音の原因を診断できる）", arrived, JSON.stringify(client.frames.map((f) => f.seq)));

    const res = await fetch(`${base}${audioPath(record)}`);
    await res.arrayBuffer();
    check("★ 404 ではなく 503（あとで取りに来い）", res.status === 503, `status=${res.status}`);
    check("Retry-After を返す", res.headers.get("retry-after") === "1", res.headers.get("retry-after"));

    show("⑩ エンジンが戻れば、同じ URL で取り直せる");
    engineDown = false;
    const recovered = await fetch(`${base}${audioPath(record)}`);
    const body = await recovered.arrayBuffer();
    check("★ 200 が返る（503 で諦めさせない設計の裏付け）", recovered.status === 200, `status=${recovered.status}`);
    check("WAV の中身が返る", body.byteLength === WAV.byteLength);

    await client.close();
  }

  {
    show("⑪ ttsEnabled=false なら音声を配らない（テキストは配る）");
    await stopServer();
    await startServer(serverEnv({ CHATTER_AGENT_TTS_ENABLED: "false" }));

    const client = await connect();
    const record = enqueue("音声なしで配られる発言です。");
    const arrived = await until(() => client.frames.some((f) => f.seq === record.seq), 5000);
    check("テキストは届く", arrived, JSON.stringify(client.frames.map((f) => f.seq)));
    check(
      "★ audio は null",
      client.frames.find((f) => f.seq === record.seq)?.audio === null,
      JSON.stringify(client.frames.find((f) => f.seq === record.seq)),
    );
    check("GET は 404", (await fetch(`${base}${audioPath(record)}`)).status === 404);
    check("起動ログに理由が出る", serverLog.includes("ttsEnabled=false"), serverLog);

    await client.close();
  }

  {
    show("⑫ 終了処理でポートが解放される（ws は外部 http server を閉じない）");
    await stopServer();
    let reachable = true;
    try {
      await fetch(base, { signal: AbortSignal.timeout(1000) });
    } catch {
      reachable = false;
    }
    check("★ SIGTERM の後、誰も listen していない", !reachable);
  }
} catch (err) {
  console.error("\n\x1b[31m検証中に例外が発生しました\x1b[0m");
  console.error(err);
  if (serverLog) console.error(`\nserver のログ:\n${serverLog}`);
  failures.push("例外");
}

show("結果");
if (failures.length === 0) {
  console.log("\x1b[32mすべて PASS\x1b[0m");
  process.exit(0);
}
console.log(`\x1b[31m${failures.length} 件 FAIL\x1b[0m`);
for (const label of failures) console.log(`  - ${label}`);
if (serverLog) console.log(`\nserver のログ:\n${serverLog}`);
process.exit(1);
