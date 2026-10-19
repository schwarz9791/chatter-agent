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

import * as fs from "node:fs";
import * as http from "node:http";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { WebSocket } from "ws";
import {
  check,
  disposableRoot,
  entryArgv,
  fail,
  makeWav,
  requireBundles,
  show,
  sleep,
  killAll,
  spawnLogged,
  summarize,
  until,
} from "./lib/harness.mjs";

const CORE = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const SERVER = path.join(CORE, "dist", "chatter-agent-server.mjs");
const PORT = 18572;
const SPEAKER_ID = 888753760;
/** openai 互換エンジン（Kokoro-FastAPI）の声 ID。VOICEVOX 系と違い英字 */
const OPENAI_VOICE_ID = "af_heart";
const EPOCH = "verify-tts-1";

requireBundles([["server", SERVER]]);

// ── 使い捨てのランタイムルート ─────────────────────────────────────────────

const { root, runtime } = disposableRoot("tts");
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

const WAV = makeWav(0.2);

/** 合成に来たテキスト。「いつ・何回」合成したかを見る唯一の窓 */
const synthesized = [];
/** openai 互換の口（`/v1/audio/speech`）に来た `{voice, speed}`。openai 版の唯一の窓 */
const openaiRequests = [];
/** true の間、エンジンは落ちているものとして振る舞う */
let engineDown = false;
/** 合成を遅らせる（同時要求のまとめを観測するため） */
let synthesisDelayMs = 0;
/**
 * #149: 声は正しいのに、この本文だけをエンジンが拒む状況を再現する（実機で踏んだ
 * Kokoro の `400 Input contains no speakable text` が典型）。
 */
const rejectedTexts = new Set();
/**
 * #149: `ttsEngine=openai` なのに `ttsBaseUrl` が openai 互換ではない何か（VOICEVOX 系や
 * 無関係な HTTP サーバー）を指している状況を再現する。true の間は `/v1/audio/voices` も
 * 404 を返す —— このスタブは同じポートで両方の口を持っているので、素の 404（下の
 * catch-all）だけでは「エンジンには繋がるが openai の口を持たない」を表せない。
 */
let openaiVoicesUnavailable = false;

const engine = http.createServer((req, res) => {
  const url = new URL(req.url, "http://x");
  if (engineDown) return void res.destroy();

  if (url.pathname === "/speakers") {
    res.writeHead(200, { "content-type": "application/json" });
    res.end(JSON.stringify([{ name: "スタブ", speaker_uuid: "u", styles: [{ id: SPEAKER_ID, name: "ノーマル" }] }]));
    return;
  }
  if (url.pathname === "/audio_query") {
    // 本物のエンジンと同じで、存在しない話者 ID は 422 + detail
    if (url.searchParams.get("speaker") !== String(SPEAKER_ID)) {
      res.writeHead(422, { "content-type": "application/json" });
      res.end(JSON.stringify({ detail: `speaker not found: ${url.searchParams.get("speaker")}` }));
      return;
    }
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
  if (url.pathname === "/v1/audio/voices") {
    if (openaiVoicesUnavailable) {
      res.writeHead(404).end();
      return;
    }
    res.writeHead(200, { "content-type": "application/json" });
    res.end(JSON.stringify({ voices: [{ id: OPENAI_VOICE_ID, name: "Heart" }] }));
    return;
  }
  if (url.pathname === "/v1/audio/speech") {
    let body = "";
    req.on("data", (chunk) => (body += chunk));
    req.on("end", () => {
      const { voice, speed, input } = JSON.parse(body);
      // 本物の Kokoro-FastAPI と同じで、存在しない声は 400 + detail
      if (voice !== OPENAI_VOICE_ID) {
        res.writeHead(400, { "content-type": "application/json" });
        res.end(JSON.stringify({ detail: `voice not found: ${voice}` }));
        return;
      }
      // ★ #149: 声は合っているのに、この本文だけをエンジンが拒む状況を再現する
      if (rejectedTexts.has(input)) {
        res.writeHead(400, { "content-type": "application/json" });
        res.end(JSON.stringify({ detail: "Input contains no speakable text" }));
        return;
      }
      openaiRequests.push({ voice, speed });
      setTimeout(() => {
        if (res.writableEnded) return;
        res.writeHead(200, { "content-type": "audio/wav" });
        res.end(WAV);
      }, synthesisDelayMs);
    });
    return;
  }
  res.writeHead(404).end();
});

// ── server の起動 ──────────────────────────────────────────────────────────

const base = `http://127.0.0.1:${PORT}`;
/** 走っている server。止めたら null に戻す */
let server = null;
/** **止めた**分のログ。⑪ で再起動するので、生きているぶんは `server.log` にある */
let stoppedLog = "";
const serverLogs = () => stoppedLog + (server?.log ?? "");

function serverEnv(overrides = {}) {
  return {
    ...process.env,
    XDG_CONFIG_HOME: root,
    CHATTER_AGENT_HOST: "127.0.0.1",
    CHATTER_AGENT_PORT: String(PORT),
    CHATTER_AGENT_TTS_URL: `http://127.0.0.1:${engine.address().port}`,
    CHATTER_AGENT_TTS_SPEAKER_ID: String(SPEAKER_ID),
    // スタブは voicevox と openai の口を同じポートで持つので、Kokoro の宛先も同じにする
    CHATTER_AGENT_KOKORO_URL: `http://127.0.0.1:${engine.address().port}`,
    CHATTER_AGENT_KOKORO_VOICE_ID: OPENAI_VOICE_ID,
    CHATTER_AGENT_SYNTHESIS_TIMEOUT_MS: "3000",
    // ★ 感情判定は見ないシナリオなので固定する。既定（ollaya・spawn=true）のままだと、
    //   開発機に ollaya が入っていれば本物の `ollaya serve` が起動され、[Engine] 起動ログの
    //   検査（「1度も出ていない」）や pid の正規表現に Ollaya 側の pid が混ざりうる
    CHATTER_AGENT_EMOTION_CLASSIFIER: "dictionary",
    ...overrides,
  };
}

async function startServer(env) {
  server = spawnLogged(entryArgv("server", SERVER), { env, label: "server" });
  await server.waitFor("[Server] Ready");
}

async function stopServer() {
  if (server === null) return;
  await server.stop();
  stoppedLog += server.log;
  server = null;
  // ⑫ は「誰も listen していない」を見る。exit の後、実際に解放されるまでを少しだけ待つ
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
  killAll();
  engine.close();
  fs.rmSync(root, { recursive: true, force: true });
}
process.on("exit", cleanup);

// ── シナリオ ───────────────────────────────────────────────────────────────

try {
  await new Promise((done) => engine.listen(0, "127.0.0.1", done));
  await startServer(serverEnv());

  {
    show("① Does not synthesize while nobody is connected");
    const record = enqueue("だれも聞いていません。");
    await sleep(500);
    check(
      "★ never calls the engine (synthesizes only once a GET arrives)",
      synthesized.length === 0,
      JSON.stringify(synthesized),
    );

    show("② Frames carry the relative audio path");
    const client = await connect();
    const arrived = await until(() => client.frames.length === 1);
    check("was delivered", arrived, JSON.stringify(client.frames));
    check(
      "★ relative path (the server does not know its own reachable address)",
      client.frames[0]?.audio?.path === audioPath(record),
      JSON.stringify(client.frames[0]?.audio),
    );
    check("delivery alone does not synthesize", synthesized.length === 0, JSON.stringify(synthesized));

    show("③ Synthesizes only on GET and returns a WAV");
    const res = await fetch(`${base}${audioPath(record)}`);
    const body = await res.arrayBuffer();
    check("returns 200", res.status === 200, `status=${res.status}`);
    check(
      "content-type is audio/wav",
      res.headers.get("content-type") === "audio/wav",
      res.headers.get("content-type"),
    );
    check("returns the WAV body", body.byteLength === WAV.byteLength, `${body.byteLength} vs ${WAV.byteLength}`);
    check("called the engine exactly once", synthesized.length === 1, JSON.stringify(synthesized));

    show("④ ★ Synthesis happens once even when several clients fetch the same sentence at the same time");
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
      "all 3 return 200",
      [a, b, c].every((r) => r.status === 200),
      [a, b, c].map((r) => r.status).join(","),
    );
    check("★ synthesized only once", synthesized.length === before + 1, `${before} → ${synthesized.length}`);
    synthesisDelayMs = 0;

    show("⑤ The second GET is served from the cache");
    const cachedBefore = synthesized.length;
    await (await fetch(`${base}${audioPath(record)}`)).arrayBuffer();
    check(
      "does not call the engine",
      synthesized.length === cachedBefore,
      JSON.stringify(synthesized.slice(cachedBefore)),
    );

    show("⑥ Whatever cannot be provided returns 404 (it will never exist)");
    const wrongEpoch = `/audio/other-epoch-${String(record.seq).padStart(12, "0")}.wav`;
    check("★ a URL of a different generation", (await fetch(`${base}${wrongEpoch}`)).status === 404, wrongEpoch);
    check("a seq not in the queue", (await fetch(`${base}/audio/${EPOCH}-000000009999.wav`)).status === 404);
    for (const bad of ["/audio/../../etc/passwd", "/audio/%2e%2e%2f%2e%2e%2fetc%2fpasswd", "/etc/passwd", "/"]) {
      check(`★ rejects path traversal: ${bad}`, (await fetch(`${base}${bad}`)).status === 404);
    }

    show("⑦ Origin (same rule as WebSocket)");
    const rejected = await fetch(`${base}${audioPath(record)}`, { headers: { origin: "http://evil.example.com" } });
    check("an Origin outside the allow list returns 403", rejected.status === 403, `status=${rejected.status}`);

    show("⑧ Once ack removes it from the queue, it returns 404");
    client.ack(record.seq);
    // until は同期の述語しか扱えないので、ここは自前で回す（削除は server の 50ms ポーリング外）
    let status = 0;
    for (let i = 0; i < 50 && status !== 404; i++) {
      status = (await fetch(`${base}${audioPath(record)}`)).status;
      if (status !== 404) await sleep(100);
    }
    check(
      "★ does not serve audio that has been acked (the queue is the authority for the text)",
      status === 404,
      `status=${status}`,
    );

    await client.close();
  }

  {
    show("⑨ ★ Returns 503 when the engine is down (text delivery does not stop)");
    engineDown = true;
    const client = await connect();
    const record = enqueue("エンジンが落ちている間の発言です。");
    const arrived = await until(() => client.frames.some((f) => f.seq === record.seq), 5000);
    check(
      "★ the text arrives (the cause of the silence can be diagnosed)",
      arrived,
      JSON.stringify(client.frames.map((f) => f.seq)),
    );

    const res = await fetch(`${base}${audioPath(record)}`);
    await res.arrayBuffer();
    check("★ 503 rather than 404 (come back later)", res.status === 503, `status=${res.status}`);
    check("returns Retry-After", res.headers.get("retry-after") === "1", res.headers.get("retry-after"));

    show("⑩ Once the engine is back, the same URL can be fetched again");
    engineDown = false;
    const recovered = await fetch(`${base}${audioPath(record)}`);
    const body = await recovered.arrayBuffer();
    check(
      "★ returns 200 (backs the design of not making clients give up on 503)",
      recovered.status === 200,
      `status=${recovered.status}`,
    );
    check("returns the WAV body", body.byteLength === WAV.byteLength);

    await client.close();
  }

  {
    show("⑪ With ttsEnabled=false, no audio is served (text still is)");
    await stopServer();
    await startServer(serverEnv({ CHATTER_AGENT_TTS_ENABLED: "false" }));

    const client = await connect();
    const record = enqueue("音声なしで配られる発言です。");
    const arrived = await until(() => client.frames.some((f) => f.seq === record.seq), 5000);
    check("the text arrives", arrived, JSON.stringify(client.frames.map((f) => f.seq)));
    check(
      "★ audio is null",
      client.frames.find((f) => f.seq === record.seq)?.audio === null,
      JSON.stringify(client.frames.find((f) => f.seq === record.seq)),
    );
    check("GET returns 404", (await fetch(`${base}${audioPath(record)}`)).status === 404);
    check("the startup log states the reason", serverLogs().includes("ttsEnabled=false"), serverLogs());

    await client.close();
  }

  {
    show("⑫ ★ When ttsSpeakerId does not exist, the cause of the silence appears in the log");
    // 「音が出ない」以外の症状が無い設定ミス。起動時の診断が候補を並べないと、
    // ユーザーからは「壊れている」としか見えない
    await stopServer();
    await startServer(serverEnv({ CHATTER_AGENT_TTS_SPEAKER_ID: "999999" }));

    const client = await connect();
    const record = enqueue("話者 ID を間違えたときの発言です。");
    await until(() => client.frames.some((f) => f.seq === record.seq), 5000);

    check("★ the startup diagnosis names the value", serverLogs().includes("Voice ID 999999 ("), serverLogs());
    check(
      "★ lists the existing speaker IDs as candidates (in a form that can be fixed)",
      serverLogs().includes(String(SPEAKER_ID)),
      serverLogs(),
    );

    const res = await fetch(`${base}${audioPath(record)}`);
    const body = await res.text();
    // ★ 4xx を 404 に落とさないこと。404 はクライアント側で ack まで通り、
    //   本文が物理削除される（設定を直しても復元できない）
    check(
      "★ 503 even on an engine 422 (a 404 would physically delete the text)",
      res.status === 503,
      `status=${res.status}`,
    );
    check("the response body also carries the engine reason", body.includes("speaker not found"), body);

    await client.close();
  }

  {
    show("⑬ The port is released on shutdown (ws does not close an external http server)");
    await stopServer();
    let reachable = true;
    try {
      await fetch(base, { signal: AbortSignal.timeout(1000) });
    } catch {
      reachable = false;
    }
    check("★ nobody is listening after SIGTERM", !reachable);
  }

  {
    show("⑭ ★ Does not start the TTS engine because it is reachable (#51 condition 3)");
    // ここまでの全シナリオでスタブのエンジンが listen している。条件3が壊れると、
    // 開発機（macOS）では代わりに**本物の AivisSpeech** が黙って起動する。
    // ★ このスクリプトに CHATTER_AGENT_TTS_SPAWN=0 を入れないこと —— 入れると
    //   条件3が壊れても素通りしてしまい、この検査の意味が無くなる
    check("★ [Engine] Started never appears", !serverLogs().includes("[Engine] Started"), serverLogs());
    // ★ スキップ理由が増えるたびにここを足さないと、否定検査は落ちるのではなく
    //   **黙って空振りする**（vacuous pass）。全変種が「cannot be started」か「Could not find」を
    //   含むことは describeEngineSkip の単体テスト側で固定してある
    check(
      "★ even with a wrong ttsSpeakerId (⑫) the engine is reachable, so it is not started",
      !serverLogs().includes("Could not find") && !serverLogs().includes("cannot be started"),
      serverLogs(),
    );
  }
  {
    show("⑮ ★ On the path that starts the engine, does not print the 503 warning first (#52 review)");
    // ★ **⑭ より後ろに置くこと。** ⑭ は累積ログに対して「[Engine] Started が1度も
    //   出ていない」を検査しているので、前に置くと必ず落ちる。
    // ★ **CHATTER_AGENT_TTS_SPAWN_COMMAND を必ず渡すこと。** 渡さないと開発機（macOS）で
    //   **本物の AivisSpeech** が起きる。起こす相手はここで作る使い捨ての実行ファイルに限る
    await stopServer();
    const fakeEngine = path.join(root, "fake-engine");
    fs.writeFileSync(fakeEngine, "#!/bin/sh\nsleep 600\n");
    fs.chmodSync(fakeEngine, 0o755);

    // 誰も listen していないループバックのポート（条件3を落として spawn 経路へ入れる）
    await startServer(
      serverEnv({
        CHATTER_AGENT_TTS_URL: `http://127.0.0.1:${PORT + 1}`,
        CHATTER_AGENT_TTS_SPAWN_COMMAND: fakeEngine,
      }),
    );
    await until(() => (server?.log ?? "").includes("[Engine] Started"), 5000);

    // ★ **累積ログ（serverLogs()）で判定しないこと。** 前のシナリオが出した行を拾ってしまう
    const spawnLog = server?.log ?? "";
    check(
      "★ the observation made before starting remains (the cause is not erased)",
      spawnLog.includes("Cannot connect to the TTS engine"),
      spawnLog,
    );
    check("★ started the engine", spawnLog.includes("[Engine] Started"), spawnLog);
    check(
      "★ does not print a warning that the next line would contradict (only the side that decided not to start prints the consequence)",
      !spawnLog.includes("Audio GET requests will return 503"),
      spawnLog,
    );

    const enginePid = Number(/\[Engine\] Started \(pid=(\d+)\)/.exec(spawnLog)?.[1]);
    check("the startup log carries the pid", Number.isInteger(enginePid) && enginePid > 0, String(enginePid));

    await stopServer();
    // ★ CI で「サーバーを止めるとエンジンも落ちる」を見る唯一の場所（今までは実機確認だけ）
    const gone = await until(() => {
      try {
        process.kill(enginePid, 0);
        return false;
      } catch {
        return true;
      }
    }, 5000);
    check("★ stopping the server also stops the engine (the whole process group)", gone, `pid=${enginePid}`);
  }

  {
    show("⑯ ★ When resolved from a name, does not silently reinterpret it and names it instead (#52 review)");
    await stopServer();
    // ★ **`run` を渡さないこと。** 退行していた場合に verify 自身が無関係なバイナリを起動する。
    //   ここで見たいのは「名前から引いたら名指しする」ことだけなので、実在しない名前で足りる
    await startServer(
      serverEnv({
        CHATTER_AGENT_TTS_URL: `http://127.0.0.1:${PORT + 2}`,
        CHATTER_AGENT_TTS_SPAWN_COMMAND: "chatter-agent-no-such-engine-xyz",
      }),
    );
    // ★ 子プロセスの stdout/stderr は POSIX ではパイプ＝非同期フラッシュ（Node の仕様）。
    //   `describeEngineSkip` の複数行 console.warn は同一 tick の同期呼び出しでも、
    //   親側には別々のタイミングで届きうる。**最後の行**（帰結の503）が届くのを待つことで、
    //   同じストリーム（stderr）上で先に書かれた行がすべて届いていることを保証する
    //   （単一の書き手の単一パイプは順序を保つ）。先頭の行で待つと、サーバー起動時に走る
    //   他の非同期処理と競合して手前で止まったスナップショットを掴みうる。
    await until(() => (server?.log ?? "").includes("Audio GET requests will return 503"), 5000);

    const missLog = server?.log ?? "";
    check(
      "★ lists the searched locations as full paths",
      /\n?\[Server\] {2}\/.*chatter-agent-no-such-engine-xyz/.test(missLog),
      missLog,
    );
    check("★ also warns about the executable bit", missLog.includes("executable bit"), missLog);
    check("the engine was not started", !missLog.includes("[Engine] Started"), missLog);
    check(
      "★ here it also states the consequence (503)",
      missLog.includes("Audio GET requests will return 503"),
      missLog,
    );
  }

  {
    show("⑰ ★ ttsEngine=openai: GET calls the openai endpoint, and voice/speed are sent as configured");
    await stopServer();
    await startServer(
      serverEnv({
        CHATTER_AGENT_TTS_ENGINE: "openai",
        CHATTER_AGENT_KOKORO_VOICE_ID: OPENAI_VOICE_ID,
        CHATTER_AGENT_TTS_SPEED_SCALE: "1.5",
      }),
    );

    const client = await connect();
    const record = enqueue("openai エンジンで読み上げる発言です。");
    await until(() => client.frames.some((f) => f.seq === record.seq), 5000);

    const before = openaiRequests.length;
    const res = await fetch(`${base}${audioPath(record)}`);
    const body = await res.arrayBuffer();
    check("returns 200", res.status === 200, `status=${res.status}`);
    check("returns the WAV body", body.byteLength === WAV.byteLength, `${body.byteLength} vs ${WAV.byteLength}`);
    check(
      "★ the endpoint called is openai /v1/audio/speech",
      openaiRequests.length === before + 1,
      JSON.stringify(openaiRequests),
    );
    const seen = openaiRequests.at(-1);
    check("★ voice follows kokoroVoiceId", seen?.voice === OPENAI_VOICE_ID, JSON.stringify(seen));
    check("★ speed follows ttsSpeedScale", seen?.speed === 1.5, JSON.stringify(seen));

    show("⑱ ★ ttsEngine=openai: returns 503 when the engine is down (text delivery does not stop)");
    engineDown = true;
    const downRecord = enqueue("openai エンジンが落ちている間の発言です。");
    const arrived = await until(() => client.frames.some((f) => f.seq === downRecord.seq), 5000);
    check("★ the text arrives", arrived, JSON.stringify(client.frames.map((f) => f.seq)));
    const downRes = await fetch(`${base}${audioPath(downRecord)}`);
    await downRes.arrayBuffer();
    check("★ 503 rather than 404", downRes.status === 503, `status=${downRes.status}`);
    engineDown = false;

    await client.close();
  }

  {
    show("⑲ ★ ttsEngine=openai: when kokoroVoiceId does not exist, the startup diagnosis lists candidates");
    await stopServer();
    await startServer(
      serverEnv({
        CHATTER_AGENT_TTS_ENGINE: "openai",
        CHATTER_AGENT_KOKORO_VOICE_ID: "no-such-voice",
      }),
    );

    const client = await connect();
    const record = enqueue("声 ID を間違えたときの発言です（openai）。");
    await until(() => client.frames.some((f) => f.seq === record.seq), 5000);

    const diagLog = server?.log ?? "";
    check("★ the startup diagnosis names the value", diagLog.includes("Voice ID no-such-voice ("), diagLog);
    check("★ lists the existing voice IDs as candidates", diagLog.includes(OPENAI_VOICE_ID), diagLog);

    const res = await fetch(`${base}${audioPath(record)}`);
    await res.arrayBuffer();
    check("★ 503 even on an engine 400", res.status === 503, `status=${res.status}`);

    await client.close();
  }

  {
    show("⑳ ★ ttsEngine=openai: does not start the TTS engine because it is reachable");
    await stopServer();
    await startServer(
      serverEnv({
        CHATTER_AGENT_TTS_ENGINE: "openai",
        CHATTER_AGENT_KOKORO_VOICE_ID: OPENAI_VOICE_ID,
      }),
    );
    // ★ 疎通の確認は Ready の後に走る。結論が出るまで待たないと、否定検査が空振りする
    const probed = await until(() => (server?.log ?? "").includes("Connected to the TTS engine"), 5000);
    check("the reachability check finished", probed, server?.log ?? "");
    // ★ この起動ぶんのログだけで見る（累積だと ⑮ が出した「[Engine] Started」を拾う）
    const freshLog = server?.log ?? "";
    check("★ [Engine] Started does not appear", !freshLog.includes("[Engine] Started"), freshLog);

    show("㉑ ★ Control API: GET /v1/speakers returns the openai voice list");
    const speakersRes = await fetch(`${base}/v1/speakers`);
    const speakersBody = await speakersRes.json();
    check("returns 200", speakersRes.status === 200, `status=${speakersRes.status}`);
    check(
      '★ {speakers:[{id:"af_heart", ...}]}',
      Array.isArray(speakersBody.speakers) && speakersBody.speakers.some((s) => s.id === OPENAI_VOICE_ID),
      JSON.stringify(speakersBody),
    );
  }

  {
    show("㉒ ★ #149: when a later seq synthesizes fine but only this one sentence is rejected, it falls to 404");
    // ここまでの ⑳ でサーバーは ttsEngine=openai のまま動いている
    const client = await connect();

    const rejectedText = "この文だけ拒まれます。";
    rejectedTexts.add(rejectedText);
    const rejected = enqueue(rejectedText);
    const okAfter = enqueue("後ろの文は通ります。");
    await until(() => client.frames.some((f) => f.seq === okAfter.seq), 5000);

    const first = await fetch(`${base}${audioPath(rejected)}`);
    await first.arrayBuffer();
    check(
      "★ 503 while the later one is not yet synthesized (it is not deleted)",
      first.status === 503,
      `status=${first.status}`,
    );

    const second = await fetch(`${base}${audioPath(okAfter)}`);
    await second.arrayBuffer();
    check("the later sentence returns 200", second.status === 200, `status=${second.status}`);

    const third = await fetch(`${base}${audioPath(rejected)}`);
    const thirdBody = await third.text();
    check(
      "★ once the later one succeeds, only the rejected sentence falls to 404",
      third.status === 404,
      `status=${third.status}`,
    );
    check("the reason is in the body", thirdBody.includes("rejected by engine"), thirdBody);

    rejectedTexts.delete(rejectedText);
    await client.close();
  }

  {
    show("㉓ ★ #149: when every sentence is rejected, it stays 503 instead of becoming 404 (the form rule 7 protects)");
    const client = await connect();

    const text = "誰にも通りません。";
    rejectedTexts.add(text);
    const record = enqueue(text);
    await until(() => client.frames.some((f) => f.seq === record.seq), 5000);

    for (let i = 0; i < 3; i++) {
      const res = await fetch(`${base}${audioPath(record)}`);
      await res.arrayBuffer();
      check(
        `★ GET #${i + 1} also stays 503 (no later sentence, so no 404)`,
        res.status === 503,
        `status=${res.status}`,
      );
    }

    rejectedTexts.delete(text);
    await client.close();
  }

  {
    show(
      "㉔ ★ #149: even if kokoroBaseUrl on openai points at a non-openai-compatible peer (404), warns without starting the engine",
    );
    await stopServer();
    openaiVoicesUnavailable = true;
    await startServer(
      serverEnv({
        CHATTER_AGENT_TTS_ENGINE: "openai",
        CHATTER_AGENT_KOKORO_VOICE_ID: OPENAI_VOICE_ID,
      }),
    );

    const warned = await until(() => (server?.log ?? "").includes("The TTS engine responded but"), 5000);
    check("★ a warning that a response was received appears", warned, server?.log ?? "");
    const freshLog = server?.log ?? "";
    check("★ the warning carries the status (404)", freshLog.includes("status=404"), freshLog);
    check(
      "★ a response exists, so it is not treated as unreachable and the engine is not started",
      !freshLog.includes("[Engine] Started"),
      freshLog,
    );

    openaiVoicesUnavailable = false;
  }

  {
    show("㉕ ★ Switching ttsEngine at runtime: the start decision is redone and only the AivisSpeech we started stops");
    await stopServer();
    // ★ `ttsEngine` を env で固定しないこと（固定すると PATCH が 409 になる）。既定の voicevox で始める。
    //   起こす相手は使い捨てのスタブに限る（`CHATTER_AGENT_TTS_SPAWN_COMMAND` を必ず渡す）。
    //   `ttsSpawnCommand` は AivisSpeech 専用なので、Kokoro 側は常駐のスタブ（openai の口）へ向ける
    const voicevoxPort = PORT + 3;
    const SPAWNED_WAV_SECONDS = 0.5;
    const stubVoicevox = path.join(root, "stub-voicevox");
    fs.writeFileSync(
      stubVoicevox,
      `#!/usr/bin/env node
const http = require("node:http");
const args = process.argv.slice(2);
const host = args[args.indexOf("--host") + 1];
const port = Number(args[args.indexOf("--port") + 1]);
const wav = Buffer.from(${JSON.stringify(makeWav(SPAWNED_WAV_SECONDS).toString("base64"))}, "base64");
http
  .createServer((req, res) => {
    if (req.url.startsWith("/speakers")) {
      res.writeHead(200, { "content-type": "application/json" });
      res.end(JSON.stringify([{ name: "スタブ", speaker_uuid: "u", styles: [{ id: ${SPEAKER_ID}, name: "ノーマル" }] }]));
      return;
    }
    if (req.url.startsWith("/audio_query")) {
      req.resume();
      req.on("end", () => {
        res.writeHead(200, { "content-type": "application/json" });
        res.end(JSON.stringify({ accent_phrases: [] }));
      });
      return;
    }
    if (req.url.startsWith("/synthesis")) {
      req.resume();
      req.on("end", () => {
        res.writeHead(200, { "content-type": "audio/wav" });
        res.end(wav);
      });
      return;
    }
    res.writeHead(404).end();
  })
  .listen(port, host);
`,
    );
    fs.chmodSync(stubVoicevox, 0o755);

    await startServer(
      serverEnv({
        CHATTER_AGENT_TTS_ENGINE: undefined,
        CHATTER_AGENT_TTS_URL: `http://127.0.0.1:${voicevoxPort}`,
        CHATTER_AGENT_TTS_SPAWN_COMMAND: stubVoicevox,
      }),
    );
    const spawnedAtStart = await until(() => (server?.log ?? "").includes("[Engine] Started"), 10_000);
    check("★ started the AivisSpeech stub at startup", spawnedAtStart, server?.log ?? "");
    const pidOf = (log) => Number(/\[Engine\] Started \(pid=(\d+)\)/.exec(log)?.[1]);
    const pidA = pidOf(server?.log ?? "");
    check("the pid of the started engine appears in the log", Number.isInteger(pidA), server?.log ?? "");
    const spawnCount = () => (server?.log ?? "").split("[Engine] Started").length - 1;
    const alive = (pid) => {
      try {
        process.kill(pid, 0);
        return true;
      } catch {
        return false;
      }
    };

    const patchEngine = (value) =>
      fetch(`${base}/v1/config`, {
        method: "PATCH",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ ttsEngine: value }),
      });
    /** 200 か 503 以外になるまで取り直す（起こした直後のエンジンはまだ上がっていない） */
    const fetchAudio = async (record) => {
      for (let i = 0; i < 100; i++) {
        const res = await fetch(`${base}${audioPath(record)}`);
        const buf = await res.arrayBuffer();
        if (res.status !== 503) return { status: res.status, bytes: buf.byteLength };
        await sleep(100);
      }
      return { status: 503, bytes: 0 };
    };

    const client = await connect();

    const toOpenai = await patchEngine("openai");
    check("★ PATCH {ttsEngine:openai} succeeds", toOpenai.status === 200, `status=${toOpenai.status}`);
    const closedA = Number.isInteger(pidA) && (await until(() => !alive(pidA), 10_000));
    check("★ the AivisSpeech we started stops", closedA, `pid=${pidA}`);
    check("★ switching to openai starts nothing", spawnCount() === 1, server?.log ?? "");

    const recordA = enqueue("openai に切り替えた後の発言です。");
    await until(() => client.frames.some((f) => f.seq === recordA.seq), 5000);
    const openaiBefore = openaiRequests.length;
    const audioA = await fetchAudio(recordA);
    check(
      "★ the next GET goes to the openai endpoint (Kokoro)",
      audioA.status === 200 && audioA.bytes === WAV.byteLength,
      JSON.stringify(audioA),
    );
    check("the openai endpoint was called", openaiRequests.length === openaiBefore + 1, JSON.stringify(openaiRequests));

    const toVoicevox = await patchEngine("voicevox");
    check("★ PATCH {ttsEngine:voicevox} succeeds", toVoicevox.status === 200, `status=${toVoicevox.status}`);
    const respawned = await until(() => spawnCount() === 2, 10_000);
    check("★ switching restarts AivisSpeech", respawned, server?.log ?? "");
    const pidB = pidOf((server?.log ?? "").slice((server?.log ?? "").lastIndexOf("[Engine] Started") - 1));
    check("the new pid differs from the previous one", Number.isInteger(pidB) && pidB !== pidA, `A=${pidA} B=${pidB}`);

    const recordB = enqueue("voicevox に戻した後の発言です。");
    await until(() => client.frames.some((f) => f.seq === recordB.seq), 5000);
    const openaiMid = openaiRequests.length;
    const audioB = await fetchAudio(recordB);
    check(
      "★ the next GET goes to the AivisSpeech stub we started",
      audioB.status === 200 && audioB.bytes === makeWav(SPAWNED_WAV_SECONDS).byteLength,
      JSON.stringify(audioB),
    );
    check("the openai endpoint was not called", openaiRequests.length === openaiMid, JSON.stringify(openaiRequests));

    const same = await patchEngine("voicevox");
    check("a PATCH with the same value succeeds too", same.status === 200, `status=${same.status}`);
    await sleep(500);
    check("★ a PATCH with the same value does not restart", spawnCount() === 2, server?.log ?? "");
    check("★ the started engine stays alive", alive(pidB), `pid=${pidB}`);

    // ★ 生きているエンジンでは修正前の条件でも素通りする。**落ちた**エンジンで確かめること
    process.kill(-pidB, "SIGTERM"); // detached で起こしているのでプロセスグループごと
    check("stopped the started engine from outside", await until(() => !alive(pidB), 5000), `pid=${pidB}`);
    const sameAfterCrash = await patchEngine("voicevox");
    check(
      "a same-value PATCH after the crash succeeds too",
      sameAfterCrash.status === 200,
      `status=${sameAfterCrash.status}`,
    );
    await sleep(500);
    check("★ a crashed engine is not restarted by a same-value PATCH", spawnCount() === 2, server?.log ?? "");

    // サーバーの終了で道連れにできるかは、切り替えで起こし直した別のエンジンで確かめる
    await patchEngine("openai");
    await patchEngine("voicevox");
    check("★ switching again restarts it", await until(() => spawnCount() === 3, 10_000), server?.log ?? "");
    const pidC = pidOf((server?.log ?? "").slice((server?.log ?? "").lastIndexOf("[Engine] Started") - 1));

    await client.close();
    await stopServer();
    const goneC = Number.isInteger(pidC) && (await until(() => !alive(pidC), 5000));
    check("★ stopping the server also stops the started engine", goneC, `pid=${pidC}`);
  }
} catch (err) {
  console.error("\n\x1b[31mAn exception occurred during verification\x1b[0m");
  console.error(err);
  fail("exception");
}

await summarize(() => (serverLogs() ? `server log:\n${serverLogs()}` : ""));
