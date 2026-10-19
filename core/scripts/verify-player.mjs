/**
 * 発話 CLI（chatter-agent-player）の受け入れ確認。
 *
 *   cd core && npm run build && npm run verify:player
 *
 * **AivisSpeech もオーディオデバイスも要らない。** 合成エンジンはスタブ HTTP サーバーに、
 * 再生コマンドは `fake-player.mjs` に差し替える。プレイヤーコマンドを config で
 * 差し替えられるようにした決定が、そのまま CI 可能性になっている。
 *
 * ★ #29 で合成がサーバーへ移った。player はエンジンを知らず、`GET /audio/<epoch>-<seq>.wav`
 *   を叩くだけになったので、**シナリオのトリガも「エンジンの応答」から「音声 GET の応答」へ
 *   移してある**（下の `AUDIO_BEHAVIOR`）。スタブのエンジンが要るのは、本物の server を
 *   立てる ⑫ だけ。
 *
 * 使い捨ての XDG_CONFIG_HOME を掘るので、実際の ~/.config/chatter-agent は汚さない。
 * 127.0.0.1 に bind するので macOS のローカルネットワーク許可ダイアログも出ない。
 */

import { spawnSync } from "node:child_process";
import * as fs from "node:fs";
import * as http from "node:http";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { WebSocketServer } from "ws";
import {
  BIN,
  check,
  disposableRoot,
  entryArgv,
  fail,
  killAll,
  makeWav,
  requireBundles,
  show,
  sleep,
  spawnLogged,
  spawned,
  summarize,
  until,
} from "./lib/harness.mjs";

const CORE = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const PLAYER = path.join(CORE, "dist", "chatter-agent-player.mjs");
const SERVER = path.join(CORE, "dist", "chatter-agent-server.mjs");
const CLI = path.join(CORE, "dist", "chatter-agent-speak.mjs");
const FAKE_PLAYER = path.join(CORE, "scripts", "fake-player.mjs");

const SPEAKER_ID = 888753760;
/** スタブが返す WAV の長さ。再生タイムアウトの導出に効く */
const WAV_SECONDS = 0.2;

requireBundles([
  ["player", PLAYER],
  ["server", SERVER],
  ["CLI", CLI],
]);

// ── 使い捨てのランタイムルート ─────────────────────────────────────────────

const { root, runtime } = disposableRoot("player");
const tmpDir = path.join(runtime, "player-tmp");
const spoolDir = path.join(runtime, "spool");
const queueDir = path.join(runtime, "speech");
fs.mkdirSync(spoolDir, { recursive: true });

// ── スタブの合成エンジン ───────────────────────────────────────────────────

const WAV = makeWav(WAV_SECONDS);

/**
 * スタブの合成エンジン。**⑫（本物の server を通す end-to-end）でしか使わない。**
 *
 * ★ text の中身で挙動を変える分岐（`FAIL` / `HANG` / `SLOW`）は #29 で
 *   スタブ**サーバー**の音声 HTTP 段（`createStubServer`）へ移した。ここに残しておくと
 *   到達しないコードが2箇所に並ぶので置かない。
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
      synthesized.push(url.searchParams.get("text") ?? "");
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ accent_phrases: [] }));
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
 *
 * ★ **WebSocket と音声の HTTP を同じポートに載せる。** 本物と同じ形にしておかないと、
 *   クライアントが `ws://host:port` から `http://host:port` を導いている経路
 *   （`player/audioFetcher.ts` の `deriveAudioBaseUrl`）が検証の外に出る。
 *
 * ★ 構築の順序も本物と同じ（http を作る → ws を載せる → listen）。逆順にすると
 *   `listening` が既に発火済みで、ws がそれを転送できない。
 */
function createStubServer() {
  const state = { acks: [], connections: 0, sockets: [], onConnect: null, audioRequests: [] };
  /** `${epoch}:${seq}` → 送ったレコード。音声 GET の応答を text で決めるため */
  const sent = new Map();

  const httpServer = http.createServer((req, res) => {
    const pathname = (req.url ?? "").split("?")[0];
    const matched = /^\/audio\/(.+)-(\d{12})\.wav$/.exec(pathname);
    if (!matched) {
      res.writeHead(404).end();
      return;
    }

    const key = `${matched[1]}:${Number(matched[2])}`;
    state.audioRequests.push(key);
    const record = sent.get(key);
    if (!record) {
      res.writeHead(404).end();
      return;
    }

    // ★ トリガは text の中身。#29 より前は同じ規則をスタブ**エンジン**に置いていた
    const text = record.text;
    if (text.includes("FAIL")) return void res.writeHead(500).end();
    if (text.includes("GONE")) return void res.writeHead(404).end();
    if (text.includes("BUSY")) return void res.writeHead(503, { "retry-after": "1" }).end();
    if (text.includes("HANG")) return; // 応答しない
    setTimeout(
      () => {
        if (res.writableEnded) return;
        res.writeHead(200, { "content-type": "audio/wav", "content-length": String(WAV.byteLength) });
        res.end(WAV);
      },
      text.includes("SLOW") ? 400 : 0,
    );
  });

  const wss = new WebSocketServer({ server: httpServer });

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

  const ready = new Promise((done) => httpServer.once("listening", done));
  httpServer.listen(0, "127.0.0.1");

  return {
    ready,
    url: () => `ws://127.0.0.1:${httpServer.address().port}`,
    state,
    send: (record) => {
      sent.set(`${record.epoch}:${record.seq}`, record);
      for (const socket of wss.clients) socket.send(JSON.stringify(record));
    },
    lastAck: () => (state.acks.length === 0 ? null : state.acks[state.acks.length - 1].seq),
    close: () =>
      new Promise((done) => {
        for (const socket of wss.clients) socket.terminate();
        wss.close(() => {
          httpServer.closeAllConnections();
          httpServer.close(() => done());
        });
      }),
  };
}

let seqCounter = 0;
/**
 * 採番の世代（#29）。**サーバー由来**なので、ここを変えることが「ランタイムルートが
 * 作り直されて採番が 1 に戻った」の再現になる（→ ⑤）。
 */
let epoch = "stub-epoch-1";
function record(text, overrides = {}) {
  seqCounter++;
  const merged = {
    epoch,
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
  // ★ `overrides` を2度 spread しないこと。2度目は同じキーを上書きし直すだけで、
  //   読み手は2つのオブジェクトを見比べないとそれに気づけない
  return {
    ...merged,
    audio: overrides.audio ?? {
      path: `/audio/${merged.epoch}-${String(merged.seq).padStart(12, "0")}.wav`,
      format: "wav",
    },
  };
}

// ── player の起動 ──────────────────────────────────────────────────────────

const playLog = path.join(root, "played.txt");

function playerEnv(overrides = {}) {
  return {
    ...process.env,
    XDG_CONFIG_HOME: root,
    CHATTER_AGENT_PLAYER_COMMAND: process.execPath,
    CHATTER_AGENT_PLAYER_ARGS: `${FAKE_PLAYER},{file}`,
    // ★ player はもうエンジンを知らない。音声は WebSocket と同じ authority から取る
    CHATTER_AGENT_AUDIO_FETCH_TIMEOUT_MS: "1000",
    FAKE_PLAYER_LOG: playLog,
    ...overrides,
  };
}

function startPlayer(env) {
  return spawnLogged(entryArgv("player", PLAYER), { env, label: "player" });
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
  await handle.waitFor("[Player] Connected");
  return handle;
}

async function stopPlayer(handle) {
  await handle?.stop();
}

/** 鳴った順。ファイル名は `<epoch>-<ゼロ埋めした seq>.wav` */
function played() {
  return playedFiles().map((name) => Number(name.replace(".wav", "").split("-").pop()));
}

/** 鳴ったファイル名そのもの。一時ファイル名の衝突を見るときに使う */
function playedFiles() {
  if (!fs.existsSync(playLog)) return [];
  return fs.readFileSync(playLog, "utf-8").split("\n").filter(Boolean);
}

function cleanup() {
  killAll();
  engine?.close();
  fs.rmSync(root, { recursive: true, force: true });
  // ★ 孫（fake-player）は `running` に居ない。player を SIGKILL すると
  //   `audio.stopAll()` が飛ぶので、hang モードの偽プレイヤーが残りうる。
  //   fake-player 側に自死のタイマーを持たせてあるが、ここでも掃除する
  spawnSync("/usr/bin/pkill", ["-f", FAKE_PLAYER], { stdio: "ignore" });
}

// ── 検証 ───────────────────────────────────────────────────────────────────

try {
  await startEngine();

  {
    show("① Plays in order and acks in seq order");
    const stub = createStubServer();
    await stub.ready;
    const player = await startReadyPlayer(playerEnv({ CHATTER_AGENT_PLAYER_SERVER_URL: stub.url() }));

    for (const text of ["ひとつめ。", "ふたつめ。", "みっつめ。"]) stub.send(record(text));
    const ok = await until(() => played().length === 3);
    check("all 3 sentences played", ok, `played: ${JSON.stringify(played())}`);
    check(
      "played in ascending seq order",
      JSON.stringify(played()) === JSON.stringify([1, 2, 3]),
      JSON.stringify(played()),
    );

    await until(() => stub.lastAck() === 3);
    check("ack advanced to the end", stub.lastAck() === 3, `ack=${JSON.stringify(stub.state.acks)}`);
    check(
      "ack advances monotonically",
      stub.state.acks.every((a, i, all) => i === 0 || a.seq >= all[i - 1].seq),
      JSON.stringify(stub.state.acks),
    );

    await stopPlayer(player);
    await stub.close();
  }

  {
    show("② A later synthesis finishing first does not overtake the head");
    const stub = createStubServer();
    await stub.ready;
    const player = await startReadyPlayer(playerEnv({ CHATTER_AGENT_PLAYER_SERVER_URL: stub.url() }));

    seqCounter = 0;
    // 1文目だけ合成が遅い。先読みで 2, 3 の合成は先に終わる
    stub.send(record("SLOW おそい。"));
    stub.send(record("はやい1。"));
    stub.send(record("はやい2。"));

    const ok = await until(() => played().length === 3);
    check("all 3 sentences played", ok, JSON.stringify(played()));
    check(
      "★ played in seq order without overtaking",
      JSON.stringify(played()) === JSON.stringify([1, 2, 3]),
      JSON.stringify(played()),
    );

    await stopPlayer(player);
    await stub.close();
  }

  {
    show("③ A seq whose synthesis failed is acked at the head (without dragging in unplayed ones)");
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
    check("★ has not acked yet even though the failure is settled", early.length === 0, JSON.stringify(early));

    const ok = await until(() => stub.lastAck() === 3);
    check("ack advanced to the end", ok, JSON.stringify(stub.state.acks));
    check("the failed seq does not play", !played().includes(2), JSON.stringify(played()));
    check("the seqs before and after it play", played().includes(1) && played().includes(3), JSON.stringify(played()));

    await stopPlayer(player);
    await stub.close();
  }

  {
    show("④ Does not speak twice even if resent after reconnecting");
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
    check("reconnects after a disconnect", reconnected, `connections=${stub.state.connections}\n${player.log}`);

    await sleep(500);
    check("★ does not speak the same seq twice", played().length === 1, JSON.stringify(played()));
    check("★ re-sends the ack for an already consumed resend", stub.lastAck() === 1, JSON.stringify(stub.state.acks));

    await stopPlayer(player);
    await stub.close();
  }

  {
    show("⑤ Does not fall silent when numbering restarts");
    const stub = createStubServer();
    await stub.ready;
    const player = await startReadyPlayer(playerEnv({ CHATTER_AGENT_PLAYER_SERVER_URL: stub.url() }));

    seqCounter = 0;
    epoch = "stub-epoch-1";
    for (const text of ["ふるい1。", "ふるい2。", "ふるい3。"]) stub.send(record(text));
    await until(() => played().length === 3);

    // ~/.config/chatter-agent を消したのと同じ状態。seq が 1 に戻り、**epoch も変わる**（#29）
    seqCounter = 0;
    epoch = "stub-epoch-2";
    stub.send(record("あたらしい1。"));

    const ok = await until(() => played().length === 4, 6000);
    check(
      "★ speaks after numbering restarts (remembering by seq alone would stay silent forever)",
      ok,
      JSON.stringify(played()),
    );

    // ★ 旧世代の音声を消し忘れると、孤児の afplay が読んでいる最中のファイルを truncate する。
    //   一時ファイル名は `<内部エポック>-<seq>.wav` なので、やり直しを跨いだ 2 つの seq=1 は
    //   別のファイル名で鳴っていなければならない
    check(
      "★ the same seq across a restart plays as a different temporary file",
      new Set(playedFiles()).size === playedFiles().length,
      JSON.stringify(playedFiles()),
    );

    await stopPlayer(player);
    await stub.close();
  }

  {
    show("⑥ Does not freeze when synthesis never returns");
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
      "★ times out and moves on (a stuck head would silence everything after it)",
      ok,
      `${JSON.stringify(played())}\n${player.log}`,
    );
    check("the stuck seq does not play", !played().includes(1), JSON.stringify(played()));
    await until(() => stub.lastAck() === 2, 5000);
    check("ack catches up too", stub.lastAck() === 2, `${JSON.stringify(stub.state.acks)}\n${player.log}`);

    await stopPlayer(player);
    await stub.close();
  }

  {
    show("⑦ Times out and moves on even if playback never returns");
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
    check("★ gives up on the hung playback and moves on", ok, JSON.stringify(played()));

    await stopPlayer(player);
    await stub.close();
  }

  {
    // ★ ここが無いと `playbackFailed → ack → サーバーのキューから削除`（player で最も
    //   破壊的な経路）の end-to-end カバレッジがゼロになる。`played()` が証明しているのは
    //   「偽プレイヤーが起動した」ことだけで、「聞こえた」ことではない
    show("⑧ Does not stop when the playback command fails, and ack advances");
    const stub = createStubServer();
    await stub.ready;
    const player = await startReadyPlayer(
      playerEnv({ CHATTER_AGENT_PLAYER_SERVER_URL: stub.url(), FAKE_PLAYER_MODE: "fail" }),
    );

    seqCounter = 0;
    for (const text of ["いち。", "に。", "さん。"]) stub.send(record(text));

    // 3文とも再生に失敗するが、head で ack して先へ進む
    const drained = await until(() => stub.lastAck() === 3, 15_000);
    check("★ ack advances to the end even if every sentence fails to play", drained, JSON.stringify(stub.state.acks));
    check("playback was attempted for all 3 sentences", played().length === 3, JSON.stringify(played()));
    check("does not swallow failures silently", player.log.includes("Playback of seq="), player.log);

    await stopPlayer(player);
    await stub.close();
  }

  {
    show("⑨ A second instance fails to start");
    const stub = createStubServer();
    await stub.ready;
    const env = playerEnv({ CHATTER_AGENT_PLAYER_SERVER_URL: stub.url() });
    const first = await startReadyPlayer(env);

    const second = startPlayer(env);
    const exited = await until(() => second.exited !== null, 5000);
    check(
      "★ the second instance fails to start (its ack would erase the first one's queue)",
      exited && second.exited === 1,
      `exit=${second.exited}`,
    );
    check(
      "prints a message that explains the reason",
      second.log.includes("Another chatter-agent-player is already running"),
      second.log,
    );

    await stopPlayer(first);
    await stub.close();
  }

  {
    show("⑩ Leaves no temporary files behind");
    const stub = createStubServer();
    await stub.ready;
    const player = await startReadyPlayer(playerEnv({ CHATTER_AGENT_PLAYER_SERVER_URL: stub.url() }));

    seqCounter = 0;
    for (const text of ["あ。", "FAIL い。", "う。"]) stub.send(record(text));
    await until(() => stub.lastAck() === 3, 8000);
    await sleep(200);

    const left = fs.existsSync(tmpDir) ? fs.readdirSync(tmpDir) : [];
    check("WAV files that finished playing are removed (failed ones too)", left.length === 0, JSON.stringify(left));

    await stopPlayer(player);
    check("the temporary directory is removed on exit", !fs.existsSync(tmpDir), tmpDir);
    await stub.close();
  }

  {
    show("⑪ ★ Does not burn through the backlog while 503 keeps coming back (#29)");

    // ★ #29 より前、ここは「エンジンが落ちていたら WebSocket に繋がない」だった。
    //   合成の失敗が「1回リトライして捨てて ack」に流れるので、AivisSpeech を起動し
    //   忘れたまま繋ぐと**溜まっていたキューが数百 ms で全部捨てられた**ためで、
    //   接続そのものを止めるのが唯一の歯止めだった。
    //
    //   合成がサーバーへ移った今、player はエンジンの生死を知らない。歯止めは
    //   **503（あとで取りに来い）を試行回数に数えない**ことへ移してある。
    //   ここで ack が1つでも出たら、その entry はサーバー側のキューから消える。
    const stub = createStubServer();
    await stub.ready;
    const player = await startReadyPlayer(playerEnv({ CHATTER_AGENT_PLAYER_SERVER_URL: stub.url() }));

    seqCounter = 0;
    for (const text of ["BUSY いち。", "BUSY にい。", "BUSY さん。"]) stub.send(record(text));

    // ★ **アサートする条件そのものを待つこと。** 以前は `audioRequests.length >= 4` で
    //   待っていたが、`unavailableWarnAfter` は 5 で BUSY は3件なので、
    //   その時点で警告が出ているとは限らなかった（ローカルでは 20ms のポーリング粒度の
    //   おかげでたまたま通っていた）
    const hinted = await until(() => player.log.includes("ttsSpeakerId"), 20_000);
    check(
      "★ keeps retrying during 503 and prints a hint to suspect the settings",
      hinted,
      `requests=${stub.state.audioRequests.length}\n${player.log}`,
    );
    check(
      "★ sends no ack at all (an ack would remove it from the server queue)",
      stub.state.acks.length === 0,
      JSON.stringify(stub.state.acks),
    );
    check("nothing plays", played().length === 0, JSON.stringify(played()));

    await stopPlayer(player);
    await stub.close();
  }

  {
    show(
      "⑫ ★ When connected to a server that carries no audio, leaves one line explaining the silence (#49 review B-1)",
    );

    // ★ **`ttsEnabled: false`（`"audio": null` が明示的に載る）と、#29 より前のサーバー
    //   （`audio` キーが無い）を言い分ける。** 潰したままだと、後者は前者と区別なく
    //   全文が無言で ack され、どちらの側にも1行も出ない。
    //
    // ★ **player は接続ごとに最初のフレームだけを見る**ので、2つのケースは
    //   **別々の接続で**確かめること。1つの接続に並べると、2件目はラッチで
    //   素通りするだけになり「警告しない」が何も証明しなくなる。
    const stub = createStubServer();
    await stub.ready;
    seqCounter = 0;

    // (1) 正常な設定（ttsEnabled: false）。**警告してはいけない** — 消す手段が無いため
    const quiet = await startReadyPlayer(playerEnv({ CHATTER_AGENT_PLAYER_SERVER_URL: stub.url() }));
    const silent = record("音声を用意しない設定の発言です。", { audio: null });
    stub.send(silent);
    check(
      "acks and moves on even when audio is not provided",
      await until(() => stub.lastAck() === silent.seq, 5000),
      JSON.stringify(stub.state.acks),
    );
    check(
      "★ no warning for an explicit audio: null (ttsEnabled: false is a valid setting)",
      !quiet.log.includes("has no audio"),
      quiet.log,
    );
    await stopPlayer(quiet);

    // (2) #29 より前のサーバー。同じ「無音」だが、こちらは理由を出す
    const legacy = await startReadyPlayer(playerEnv({ CHATTER_AGENT_PLAYER_SERVER_URL: stub.url() }));
    const undeclared = record("音声キーの無いフレームです。");
    delete undeclared.audio; // JSON.stringify がキーごと落とす（＝ #29 より前のサーバーの形）
    stub.send(undeclared);

    check(
      "★ the reason for the silence appears in the log",
      await until(() => legacy.log.includes("has no audio"), 10_000),
      legacy.log,
    );
    check(
      "★ behavior is unchanged (still acks and moves on)",
      await until(() => stub.lastAck() === undeclared.seq, 5000),
      JSON.stringify(stub.state.acks),
    );
    check("neither plays any sound", played().length === 0, JSON.stringify(played()));

    await stopPlayer(legacy);
    await stub.close();
  }

  {
    show("⑬ End to end through the real server and CLI");
    const PORT = 18571;
    const chainEnv = {
      ...playerEnv(),
      CHATTER_AGENT_HOST: "127.0.0.1",
      CHATTER_AGENT_PORT: String(PORT),
      // ★ #29 でエンジンを叩くのは **server**。ここだけスタブのエンジンが要る
      CHATTER_AGENT_TTS_URL: engineUrl,
      CHATTER_AGENT_TTS_SPEAKER_ID: String(SPEAKER_ID),
      CHATTER_AGENT_SYNTHESIS_TIMEOUT_MS: "2000",
      // ★ 感情判定は見ないシナリオなので固定する。既定（ollaya・spawn=true）のままだと、
      //   開発機に ollaya が入っていれば本物の `ollaya serve` が起動されうる
      CHATTER_AGENT_EMOTION_CLASSIFIER: "dictionary",
    };
    // playerServerUrl は空のまま。host / port からの導出も一緒に確かめる
    delete chainEnv.CHATTER_AGENT_PLAYER_SERVER_URL;

    // ★ `spawnLogged` で起動すること。自前で spawn すると、失敗時の一括出力
    //   （`spawned()` を回る）に出てこず、唯一の end-to-end シナリオが落ちたときに
    //   空のログしか読めない
    const server = spawnLogged(entryArgv("server", SERVER), { env: chainEnv, label: "server" });
    await server.waitFor("[Server] Ready");

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
    const [speakCmd, ...speakArgs] = entryArgv("speak", CLI);
    const cli = spawnSync(speakCmd, speakArgs, { env: chainEnv, stdio: "ignore" });
    check("the CLI exits normally", cli.status === 0, `status=${cli.status}`);

    const heard = await until(() => played().length === 2, 10_000);
    check("★ sound plays through hook → CLI → server → player", heard, JSON.stringify(played()));

    const drained = await until(() => fs.readdirSync(queueDir).filter((f) => f.endsWith(".json")).length === 0, 5000);
    check(
      "★ an ack is sent after each utterance and the delivery queue empties",
      drained,
      JSON.stringify(fs.readdirSync(queueDir)),
    );
    // ★ **厳密一致にすること。** `>= 2` だと player が別に叩いていても通る。
    //   ⑫ に入る時点で `synthesized` は空（①〜⑪ はスタブサーバーしか使わない）で、
    //   期待は「1文につき1回」。player が独自に合成すれば 4 になる
    check(
      "★ synthesis happens once per sentence, on the server side",
      synthesized.length === 2,
      `synthesized=${JSON.stringify(synthesized)}`,
    );
    // ★ ログ（`[Player] engine: …`）を見るアサートは #49 で行ごと消えたので常に真だった。
    //   バンドルの中身を見る。**`/audio_query` のような URL 断片は使えない** —
    //   `config.ts` の docstring がその語を含み、`minify: false` なのでコメントごと載る
    //   単一バイナリのときは player のバンドルを実行していないので当てはまらない
    if (!BIN) {
      const playerBundle = fs.readFileSync(PLAYER, "utf-8");
      check(
        "★ the player bundle does not contain a TTS engine client",
        !playerBundle.includes("createVoicevoxEngine") && !playerBundle.includes("createOpenAiEngine"),
        "the bundle contains a TTS engine client",
      );
    }

    await stopPlayer(player);
    await server.stop();
  }
} catch (err) {
  console.error(err);
  fail("runtime error");
} finally {
  cleanup();
}

await summarize(() =>
  spawned()
    .filter((handle) => handle.log)
    .map((handle) => `${handle.label} log:\n${handle.log}`)
    .join("\n\n"),
);
