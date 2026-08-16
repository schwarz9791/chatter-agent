/**
 * 発話 CLI（chatter-agent-player）の受け入れ確認。
 *
 *   cd core && npm run build && npm run verify:player
 *
 * **AivisSpeech もオーディオデバイスも要らない。** 合成エンジンはスタブ HTTP サーバーに、
 * 再生コマンドは `fake-player.mjs` に差し替える。プレイヤーコマンドを config で
 * 差し替えられるようにした決定が、そのまま CI 可能性になっている。
 *
 * 使い捨ての XDG_CONFIG_HOME を掘るので、実際の ~/.config/chatter-agent は汚さない。
 * 127.0.0.1 に bind するので macOS のローカルネットワーク許可ダイアログも出ない。
 */

import { spawn, spawnSync } from "node:child_process";
import * as fs from "node:fs";
import * as http from "node:http";
import * as os from "node:os";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { WebSocketServer } from "ws";

const CORE = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const REPO = path.resolve(CORE, "..");
const PLAYER = path.join(CORE, "dist", "chatter-agent-player.mjs");
const SERVER = path.join(CORE, "dist", "chatter-agent-server.mjs");
const CLI = path.join(REPO, "plugin", "bin", "chatter-agent-speak.mjs");
const FAKE_PLAYER = path.join(CORE, "scripts", "fake-player.mjs");

const SPEAKER_ID = 888753760;
/** スタブが返す WAV の長さ。再生タイムアウトの導出に効く */
const WAV_SECONDS = 0.2;

for (const [label, file] of [
  ["player", PLAYER],
  ["server", SERVER],
  ["CLI", CLI],
]) {
  if (!fs.existsSync(file)) {
    console.error(`${label} のバンドルがありません: ${file}\n先に core/ で npm run build を実行してください。`);
    process.exit(1);
  }
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

// 既定は広めに取る。CI（ubuntu）は手元より遅く、Node の初回起動と合成 2 往復が乗る
async function until(predicate, timeoutMs = 10_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (predicate()) return true;
    await sleep(20);
  }
  return false;
}

// ── 使い捨てのランタイムルート ─────────────────────────────────────────────

const root = fs.mkdtempSync(path.join(os.tmpdir(), "chatter-agent-player-"));
const runtime = path.join(root, "chatter-agent");
const tmpDir = path.join(runtime, "player-tmp");
const spoolDir = path.join(runtime, "spool");
const queueDir = path.join(runtime, "speech");
fs.mkdirSync(spoolDir, { recursive: true });

// ── スタブの合成エンジン ───────────────────────────────────────────────────

/** 長さだけ正しい RIFF/PCM。player はここから再生タイムアウトを導く */
function makeWav(seconds) {
  const sampleRate = 24000;
  const byteRate = sampleRate * 2;
  const dataBytes = Math.round(byteRate * seconds);
  const buf = Buffer.alloc(44 + dataBytes);
  buf.write("RIFF", 0);
  buf.writeUInt32LE(36 + dataBytes, 4);
  buf.write("WAVE", 8);
  buf.write("fmt ", 12);
  buf.writeUInt32LE(16, 16);
  buf.writeUInt16LE(1, 20);
  buf.writeUInt16LE(1, 22);
  buf.writeUInt32LE(sampleRate, 24);
  buf.writeUInt32LE(byteRate, 28);
  buf.writeUInt16LE(2, 32);
  buf.writeUInt16LE(16, 34);
  buf.write("data", 36);
  buf.writeUInt32LE(dataBytes, 40);
  return buf;
}

const WAV = makeWav(WAV_SECONDS);

/**
 * text の中身で挙動を変える:
 *   FAIL を含む → 500（item 固有の失敗）
 *   HANG を含む → 応答を返さない（合成タイムアウト）
 *   SLOW を含む → 400ms 遅らせる（先読みの追い越しを作る）
 */
let engine;
let engineUrl = "";
const synthesized = [];

async function startEngine() {
  engine = http.createServer((req, res) => {
    const url = new URL(req.url, "http://x");

    if (url.pathname === "/speakers") {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify([{ name: "スタブ", speaker_uuid: "u", styles: [{ id: SPEAKER_ID, name: "ノーマル" }] }]));
      return;
    }

    if (url.pathname === "/audio_query") {
      const text = url.searchParams.get("text") ?? "";
      synthesized.push(text);
      if (text.includes("FAIL")) {
        res.writeHead(500);
        res.end();
        return;
      }
      if (text.includes("HANG")) return; // 応答しない
      const delay = text.includes("SLOW") ? 400 : 0;
      setTimeout(() => {
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ accent_phrases: [] }));
      }, delay);
      return;
    }

    if (url.pathname === "/synthesis") {
      // ボディを読み切ってから返す（読まないと接続が滞る）
      req.on("data", () => {});
      req.on("end", () => {
        res.writeHead(200, { "Content-Type": "audio/wav" });
        res.end(WAV);
      });
      return;
    }

    res.writeHead(404);
    res.end();
  });

  await new Promise((done) => engine.listen(0, "127.0.0.1", done));
  engineUrl = `http://127.0.0.1:${engine.address().port}`;
}

// ── スタブの配信サーバー ───────────────────────────────────────────────────

/**
 * 本物の `chatter-agent-server` の代わり。ack を直接観測でき、
 * 接続直後の同期送出や採番のやり直しを狙って作れる。
 */
function createStubServer() {
  const wss = new WebSocketServer({ host: "127.0.0.1", port: 0 });
  const state = { acks: [], connections: 0, sockets: [], onConnect: null };

  wss.on("connection", (socket) => {
    state.connections++;
    state.sockets.push(socket);
    socket.on("message", (data) => {
      try {
        state.acks.push(JSON.parse(String(data)));
      } catch {
        state.acks.push({ raw: String(data) });
      }
    });
    // server の catchUp と同じく、connection ハンドラから同期で流す
    state.onConnect?.(socket, state.connections);
  });

  return {
    ready: new Promise((done) => wss.once("listening", done)),
    url: () => `ws://127.0.0.1:${wss.address().port}`,
    state,
    send: (record) => {
      for (const socket of wss.clients) socket.send(JSON.stringify(record));
    },
    lastAck: () => (state.acks.length === 0 ? null : state.acks[state.acks.length - 1].seq),
    close: () =>
      new Promise((done) => {
        for (const socket of wss.clients) socket.terminate();
        wss.close(done);
      }),
  };
}

let seqCounter = 0;
function record(text, overrides = {}) {
  seqCounter++;
  return {
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
}

// ── player の起動 ──────────────────────────────────────────────────────────

const playLog = path.join(root, "played.txt");

function playerEnv(overrides = {}) {
  return {
    ...process.env,
    XDG_CONFIG_HOME: root,
    CHATTER_AGENT_TTS_URL: engineUrl,
    CHATTER_AGENT_TTS_SPEAKER_ID: String(SPEAKER_ID),
    CHATTER_AGENT_PLAYER_COMMAND: process.execPath,
    CHATTER_AGENT_PLAYER_ARGS: `${FAKE_PLAYER},{file}`,
    CHATTER_AGENT_SYNTHESIS_TIMEOUT_MS: "1000",
    FAKE_PLAYER_LOG: playLog,
    ...overrides,
  };
}

const running = [];

function startPlayer(env) {
  const child = spawn(process.execPath, [PLAYER], { env, stdio: ["ignore", "pipe", "pipe"] });
  const handle = { child, log: "", exited: null };
  child.stdout.on("data", (d) => (handle.log += d));
  child.stderr.on("data", (d) => (handle.log += d));
  child.on("exit", (code) => (handle.exited = code ?? -1));
  running.push(handle);
  return handle;
}

/**
 * ★ `[Player] Ready` ではなく**接続が張れたところまで**待つこと。
 *   Ready は `client.start()` の直後に出るので、その時点ではまだハンドシェイクの途中でありうる。
 *   接続前に `stub.send()` すると `wss.clients` が空で誰にも届かず、そのシナリオが丸ごと空振りする。
 *   手元（macOS）では接続が間に合っていたが、CI の初回起動では間に合わずに落ちた。
 */
async function startReadyPlayer(env = playerEnv()) {
  fs.writeFileSync(playLog, "");
  const handle = startPlayer(env);
  const ok = await until(() => handle.log.includes("[Player] 接続しました") || handle.exited !== null, 15_000);
  if (!ok || handle.exited !== null) throw new Error(`player が接続しませんでした:\n${handle.log}`);
  return handle;
}

async function stopPlayer(handle) {
  if (!handle || handle.exited !== null) return;
  const dead = new Promise((done) => handle.child.once("exit", done));
  handle.child.kill("SIGTERM");
  await Promise.race([dead, sleep(4000)]);
  if (handle.exited === null) handle.child.kill("SIGKILL");
}

/** 鳴った順（ファイル名 = ゼロ埋めした seq） */
function played() {
  if (!fs.existsSync(playLog)) return [];
  return fs
    .readFileSync(playLog, "utf-8")
    .split("\n")
    .filter(Boolean)
    .map((name) => Number(name.replace(".wav", "")));
}

function cleanup() {
  for (const handle of running) handle.child.kill("SIGKILL");
  engine?.close();
  fs.rmSync(root, { recursive: true, force: true });
}

// ── 検証 ───────────────────────────────────────────────────────────────────

try {
  await startEngine();

  {
    show("① 順序どおりに再生し、seq 順に ack する");
    const stub = createStubServer();
    await stub.ready;
    const player = await startReadyPlayer(playerEnv({ CHATTER_AGENT_PLAYER_SERVER_URL: stub.url() }));

    for (const text of ["ひとつめ。", "ふたつめ。", "みっつめ。"]) stub.send(record(text));
    const ok = await until(() => played().length === 3);
    check("3文とも鳴った", ok, `鳴ったのは ${JSON.stringify(played())}`);
    check("seq 昇順に鳴った", JSON.stringify(played()) === JSON.stringify([1, 2, 3]), JSON.stringify(played()));

    await until(() => stub.lastAck() === 3);
    check("最後まで ack が進んだ", stub.lastAck() === 3, `ack=${JSON.stringify(stub.state.acks)}`);
    check(
      "ack は単調に進む",
      stub.state.acks.every((a, i, all) => i === 0 || a.seq >= all[i - 1].seq),
      JSON.stringify(stub.state.acks),
    );

    await stopPlayer(player);
    await stub.close();
  }

  {
    show("② 後ろの合成が先に終わっても head を追い越さない");
    const stub = createStubServer();
    await stub.ready;
    const player = await startReadyPlayer(playerEnv({ CHATTER_AGENT_PLAYER_SERVER_URL: stub.url() }));

    seqCounter = 0;
    // 1文目だけ合成が遅い。先読みで 2, 3 の合成は先に終わる
    stub.send(record("SLOW おそい。"));
    stub.send(record("はやい1。"));
    stub.send(record("はやい2。"));

    const ok = await until(() => played().length === 3);
    check("3文とも鳴った", ok, JSON.stringify(played()));
    check(
      "★ 追い越さずに seq 順で鳴った",
      JSON.stringify(played()) === JSON.stringify([1, 2, 3]),
      JSON.stringify(played()),
    );

    await stopPlayer(player);
    await stub.close();
  }

  {
    show("③ 合成に失敗した seq は head で ack する（未再生分を巻き込まない）");
    const stub = createStubServer();
    await stub.ready;
    const player = await startReadyPlayer(playerEnv({ CHATTER_AGENT_PLAYER_SERVER_URL: stub.url() }));

    seqCounter = 0;
    stub.send(record("SLOW さいしょ。")); // seq 1: 合成が遅い
    stub.send(record("FAIL だめ。")); // seq 2: 合成に失敗する
    stub.send(record("さいご。")); // seq 3

    // seq 2 の失敗が確定するのは seq 1 の再生前。ここで ack(2) が飛ぶと seq 1 が道連れになる
    await sleep(200);
    const early = stub.state.acks.map((a) => a.seq);
    check("★ 失敗が確定しても、まだ ack を出していない", early.length === 0, JSON.stringify(early));

    const ok = await until(() => stub.lastAck() === 3);
    check("最後まで ack が進んだ", ok, JSON.stringify(stub.state.acks));
    check("失敗した seq は鳴らない", !played().includes(2), JSON.stringify(played()));
    check("前後の seq は鳴る", played().includes(1) && played().includes(3), JSON.stringify(played()));

    await stopPlayer(player);
    await stub.close();
  }

  {
    show("④ 再接続で再送されても二度読み上げない");
    const stub = createStubServer();
    await stub.ready;
    const player = await startReadyPlayer(playerEnv({ CHATTER_AGENT_PLAYER_SERVER_URL: stub.url() }));

    seqCounter = 0;
    const first = record("いちど。");
    // 2回目の接続で、ack 済みのものをもう一度流す（ack が届く前に切れたときの再現）
    stub.state.onConnect = (socket, n) => {
      if (n === 2) socket.send(JSON.stringify(first));
    };

    stub.send(first);
    await until(() => played().length === 1);

    stub.state.sockets[0].close();
    const reconnected = await until(() => stub.state.connections === 2, 15_000);
    check("切断されたら繋ぎ直す", reconnected, `connections=${stub.state.connections}\n${player.log}`);

    await sleep(500);
    check("★ 同じ seq を二度読み上げない", played().length === 1, JSON.stringify(played()));
    check("★ 消費済みの再送には ack を打ち直す", stub.lastAck() === 1, JSON.stringify(stub.state.acks));

    await stopPlayer(player);
    await stub.close();
  }

  {
    show("⑤ 採番がやり直されても沈黙しない");
    const stub = createStubServer();
    await stub.ready;
    const player = await startReadyPlayer(playerEnv({ CHATTER_AGENT_PLAYER_SERVER_URL: stub.url() }));

    seqCounter = 0;
    for (const text of ["ふるい1。", "ふるい2。", "ふるい3。"]) stub.send(record(text));
    await until(() => played().length === 3);

    // ~/.config/chatter-agent を消したのと同じ状態。seq が 1 に戻り、ts も新しくなる
    seqCounter = 0;
    stub.send(record("あたらしい1。"));

    const ok = await until(() => played().length === 4, 6000);
    check("★ 採番のやり直し後も喋る（seq だけで覚えていると永久に黙る）", ok, JSON.stringify(played()));

    await stopPlayer(player);
    await stub.close();
  }

  {
    show("⑥ 合成が返ってこなくても固まらない");
    const stub = createStubServer();
    await stub.ready;
    const player = await startReadyPlayer(playerEnv({ CHATTER_AGENT_PLAYER_SERVER_URL: stub.url() }));

    seqCounter = 0;
    stub.send(record("HANG だんまり。")); // 応答が返らない
    stub.send(record("そのつぎ。"));

    // 合成タイムアウト（1000ms）× 2回試行が終わってから seq 2 が鳴る。
    // ★ 余裕を多めに取ること。応答を返さないリクエストは keep-alive のソケットを
    //   掴んだままになるので、後続の合成が新しい接続を開くまでの間が読めない。
    //   12 秒だと数回に1回取りこぼした
    const ok = await until(() => played().includes(2), 25_000);
    check(
      "★ タイムアウトして次へ進む（head が固まると以後すべて無音）",
      ok,
      `${JSON.stringify(played())}\n${player.log}`,
    );
    check("固まった seq は鳴らない", !played().includes(1), JSON.stringify(played()));
    await until(() => stub.lastAck() === 2, 5000);
    check("ack も追いつく", stub.lastAck() === 2, `${JSON.stringify(stub.state.acks)}\n${player.log}`);

    await stopPlayer(player);
    await stub.close();
  }

  {
    show("⑦ 再生が返ってこなくてもタイムアウトして次へ進む");
    const stub = createStubServer();
    await stub.ready;
    // 1文目だけハングさせたいが、コマンドは1つしか指定できない。
    // WAV は 0.2 秒なので、再生タイムアウトは 0.2*2+5 = 5.4 秒
    const player = await startReadyPlayer(
      playerEnv({ CHATTER_AGENT_PLAYER_SERVER_URL: stub.url(), FAKE_PLAYER_MODE: "hang" }),
    );

    seqCounter = 0;
    stub.send(record("とまる。"));
    stub.send(record("つぎ。"));

    const ok = await until(() => played().length === 2, 15_000);
    check("★ ハングした再生を諦めて次へ進む", ok, JSON.stringify(played()));

    await stopPlayer(player);
    await stub.close();
  }

  {
    show("⑧ 二重起動は失敗する");
    const stub = createStubServer();
    await stub.ready;
    const env = playerEnv({ CHATTER_AGENT_PLAYER_SERVER_URL: stub.url() });
    const first = await startReadyPlayer(env);

    const second = startPlayer(env);
    const exited = await until(() => second.exited !== null, 5000);
    check(
      "★ 2台目は起動に失敗する（ack が1台目のキューを消すため）",
      exited && second.exited === 1,
      `exit=${second.exited}`,
    );
    check("理由が分かるメッセージを出す", second.log.includes("既に別の chatter-agent-player"), second.log);

    await stopPlayer(first);
    await stub.close();
  }

  {
    show("⑨ 一時ファイルを残さない");
    const stub = createStubServer();
    await stub.ready;
    const player = await startReadyPlayer(playerEnv({ CHATTER_AGENT_PLAYER_SERVER_URL: stub.url() }));

    seqCounter = 0;
    for (const text of ["あ。", "FAIL い。", "う。"]) stub.send(record(text));
    await until(() => stub.lastAck() === 3, 8000);
    await sleep(200);

    const left = fs.existsSync(tmpDir) ? fs.readdirSync(tmpDir) : [];
    check("再生し終えた WAV は消える（失敗した分も）", left.length === 0, JSON.stringify(left));

    await stopPlayer(player);
    check("終了時に一時ディレクトリごと消す", !fs.existsSync(tmpDir), tmpDir);
    await stub.close();
  }

  {
    show("⑩ エンジンが落ちていたら接続しない（バックログを消さない）");
    const stub = createStubServer();
    await stub.ready;
    // 誰も listen していないポートをエンジンに指定する
    const player = startPlayer(
      playerEnv({ CHATTER_AGENT_PLAYER_SERVER_URL: stub.url(), CHATTER_AGENT_TTS_URL: "http://127.0.0.1:1" }),
    );

    await sleep(1500);
    check("★ Ready にならない", !player.log.includes("[Player] Ready"), player.log);
    check(
      "★ WebSocket に繋がない（繋げば発話が届いて捨てられる）",
      stub.state.connections === 0,
      `connections=${stub.state.connections}`,
    );
    check("待っている理由を出す", player.log.includes("音声合成エンジンに繋がりません"), player.log);

    await stopPlayer(player);
    await stub.close();
  }

  {
    show("⑪ 本物の server と CLI を通したエンドツーエンド");
    const PORT = 18571;
    const chainEnv = {
      ...playerEnv(),
      CHATTER_AGENT_HOST: "127.0.0.1",
      CHATTER_AGENT_PORT: String(PORT),
    };
    // playerServerUrl は空のまま。host / port からの導出も一緒に確かめる
    delete chainEnv.CHATTER_AGENT_PLAYER_SERVER_URL;

    const server = spawn(process.execPath, [SERVER], { env: chainEnv, stdio: ["ignore", "pipe", "pipe"] });
    running.push({ child: server, log: "", exited: null });
    let serverLog = "";
    server.stdout.on("data", (d) => (serverLog += d));
    server.stderr.on("data", (d) => (serverLog += d));
    const serverUp = await until(() => serverLog.includes("[Server] Ready"));
    if (!serverUp) throw new Error(`server が起動しませんでした:\n${serverLog}`);

    const player = await startReadyPlayer(chainEnv);

    // plugin の hook が置く形の payload を spool に置いて CLI を起動する
    const payload = {
      session_id: "sess-1",
      hook_event_name: "MessageDisplay",
      turn_id: "turn-1",
      message_id: "m-e2e",
      index: 0,
      final: true,
      delta: "配管がつながりました。ふたつめの文です。",
    };
    fs.writeFileSync(path.join(spoolDir, "m-e2e.0.json"), JSON.stringify(payload));
    const cli = spawnSync(process.execPath, [CLI], { env: chainEnv, stdio: "ignore" });
    check("CLI が正常終了する", cli.status === 0, `status=${cli.status}`);

    const heard = await until(() => played().length === 2, 10_000);
    check("★ hook → CLI → server → player で音が鳴る", heard, JSON.stringify(played()));

    const drained = await until(() => fs.readdirSync(queueDir).filter((f) => f.endsWith(".json")).length === 0, 5000);
    check("★ 喋り終えるたびに ack が飛び、配信キューが空になる", drained, JSON.stringify(fs.readdirSync(queueDir)));

    await stopPlayer(player);
    server.kill("SIGTERM");
    await sleep(300);
  }

  show("結果");
  if (failures.length > 0) {
    console.log(`\x1b[31m${failures.length} 件失敗\x1b[0m`);
    for (const handle of running) {
      if (handle.log) console.log(`\nプロセスのログ:\n${handle.log}`);
    }
  } else {
    console.log("\x1b[32mすべて PASS\x1b[0m");
  }
} catch (err) {
  console.error(err);
  for (const handle of running) {
    if (handle.log) console.error(`\nプロセスのログ:\n${handle.log}`);
  }
  failures.push("実行エラー");
} finally {
  cleanup();
}

process.exit(failures.length === 0 ? 0 : 1);
