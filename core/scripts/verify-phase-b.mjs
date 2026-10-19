/**
 * Phase B の受け入れ確認。
 *
 * server を実際に起動し、WebSocket クライアントを繋いで配信・ack・上限・Origin 検査を見る。
 *
 *   cd core && npm run build && npm run verify:phase-b
 *
 * 使い捨ての XDG_CONFIG_HOME を掘るので、実際の ~/.config/chatter-agent は汚さない。
 * 127.0.0.1 に bind するので macOS のローカルネットワーク許可ダイアログも出ない。
 */

import { spawnSync } from "node:child_process";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { WebSocket } from "ws";
import {
  check,
  disposableRoot,
  entryArgv,
  fail,
  killAll,
  requireBundles,
  show,
  sleep,
  spawnLogged,
  summarize,
} from "./lib/harness.mjs";

const CORE = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const CLI = path.join(CORE, "dist", "chatter-agent-speak.mjs");
const SERVER = path.join(CORE, "dist", "chatter-agent-server.mjs");

const PORT = 18570;
/** 上限で古い方から捨てるのを見たいので、わざと小さくする */
const QUEUE_MAX = 6;

requireBundles([
  ["CLI", CLI],
  ["server", SERVER],
]);

const { root, runtime } = disposableRoot("phase-b");
const spoolDir = path.join(runtime, "spool");
const queueDir = path.join(runtime, "speech");
fs.mkdirSync(spoolDir, { recursive: true });

const env = {
  ...process.env,
  XDG_CONFIG_HOME: root,
  CHATTER_AGENT_HOST: "127.0.0.1",
  CHATTER_AGENT_PORT: String(PORT),
  CHATTER_AGENT_SPEECH_QUEUE_MAX_ENTRIES: String(QUEUE_MAX),
  // ★ このスクリプトだけ TTS 系の env を渡していないので、サーバーは既定の
  //   127.0.0.1:10101 に繋ぎに行って失敗する。#51 以降はそこで**本物の AivisSpeech を
  //   起こしてしまう**（開発機には実体がある）ので、明示的に切る。
  //   `CHATTER_AGENT_TTS_ENABLED=false` では逃げられない —— 下の「音声の相対パスが
  //   載っている」がフレームの audio を検査しているため
  CHATTER_AGENT_TTS_SPAWN: "0",
  // ★ 感情判定は見ないシナリオなので固定する。既定（ollaya・spawn=true）のままだと、
  //   開発機に ollaya が入っていれば本物の `ollaya serve` が起動されうる
  CHATTER_AGENT_EMOTION_CLASSIFIER: "dictionary",
};

const queueSize = () => fs.readdirSync(queueDir).filter((f) => f.endsWith(".json")).length;

/**
 * spool に delta を1本置いて CLI を起動する（plugin の bash hook の代わり）。
 *
 * hook は `<message_id>.<index>.json` を tmp + rename で置く（追記はしない）。ここでは
 * 1メッセージ1 delta（index 0 が即 final）で十分なので、そのまま writeFileSync でよい。
 */
function speak(messageId, text) {
  const payload = {
    session_id: "sess-1",
    hook_event_name: "MessageDisplay",
    turn_id: "turn-1",
    message_id: messageId,
    index: 0,
    final: true,
    delta: text,
  };
  fs.writeFileSync(path.join(spoolDir, `${messageId}.0.json`), JSON.stringify(payload));
  const [speakCmd, ...speakArgs] = entryArgv("speak", CLI);
  const { status } = spawnSync(speakCmd, speakArgs, { env, stdio: "ignore" });
  if (status !== 0) throw new Error(`CLI exited abnormally (status=${status})`);
}

/** 接続の前に message ハンドラを張る（接続直後の追いつきは同期で来る） */
function connect(options = {}) {
  const socket = new WebSocket(`ws://127.0.0.1:${PORT}`, { headers: options.headers });
  const received = [];
  socket.on("message", (data) => received.push(JSON.parse(String(data))));
  return new Promise((resolve, reject) => {
    socket.once("open", () =>
      resolve({
        socket,
        received,
        texts: () => received.map((r) => r.text),
        seqs: () => received.map((r) => r.seq),
        frames: () => received,
        /**
         * 「seq N まで喋った」。
         *
         * ★ **直近に受け取ったフレームの `epoch` を名乗る**（#29）。名乗らないと
         *   サーバーは「世代を名乗らない ack」として現世代のものに扱うので、
         *   採番のやり直しを跨いだ ack が通ってしまい、⑧ の検査が空振りする
         */
        ack: (seq) => {
          const epoch = received[received.length - 1]?.epoch;
          socket.send(JSON.stringify({ type: "spoken", seq, ...(epoch === undefined ? {} : { epoch }) }));
        },
        async settle(ms = 400) {
          await sleep(ms);
          return received;
        },
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

/** 走っている server。止めたら null に戻す */
let server = null;
/** **止めた**分のログ。この検証は途中で2回 server を入れ替える */
let stoppedLog = "";
const serverLogs = () => stoppedLog + (server?.log ?? "");

/**
 * ★ 起動ごとにログを分けること。1本に溜めると「今回の起動で Ready が出たか」を
 *   `lastIndexOf` の比較で判定する羽目になる（`spawnLogged` はハンドルごとに持つ）。
 */
async function startServer() {
  server = spawnLogged(entryArgv("server", SERVER), { env, label: "server" });
  await server.waitFor("[Server] Ready");
}

async function stopServer() {
  if (server === null) return;
  await server.stop(3000);
  stoppedLog += server.log;
  server = null;
}

function cleanup() {
  killAll();
  fs.rmSync(root, { recursive: true, force: true });
}

try {
  await startServer();

  show("① Sentences are delivered one by one to a connected client");
  const client = await connect();
  speak("m-1", "確認します。ログを見ます。以上です。");
  await client.settle();
  console.log(client.received.map((r) => `seq=${r.seq} ${r.kind} ${JSON.stringify(r.text)}`).join("\n"));
  check(
    "arrives one sentence per line",
    JSON.stringify(client.texts()) === JSON.stringify(["確認します。", "ログを見ます。", "以上です。"]),
    JSON.stringify(client.texts()),
  );
  check("seq is consecutive", JSON.stringify(client.seqs()) === JSON.stringify([1, 2, 3]));
  // ★ #29: 配信フレームには音声の**相対パス**が載る。絶対 URL にしないこと
  //   （bind アドレスは接続先ではないので、絶対 URL だと LAN 越しのクライアントから繋がらない）
  check(
    "carries the relative audio path",
    client.frames().every((f) => typeof f.audio?.path === "string" && f.audio.path.startsWith(`/audio/${f.epoch}-`)),
    JSON.stringify(client.frames().map((f) => f.audio)),
  );

  show("② ack shrinks the queue");
  console.log(`queue before ack: ${queueSize()} entries`);
  check("entries remain in the queue before they are spoken", queueSize() === 3, `${queueSize()} entries`);

  client.ack(2);
  await sleep(300);
  console.log(`after acking seq<=2: ${queueSize()} entries`);
  check("a cumulative ack removes seq<=2", queueSize() === 1, `${queueSize()} entries`);

  client.ack(3);
  await sleep(300);
  check("acking everything empties the queue", queueSize() === 0, `${queueSize()} entries`);

  show("③ Entries written while disconnected arrive on connect (instead of ?since=)");
  await client.close();
  speak("m-2", "切断中の一文目。切断中の二文目。");
  await sleep(300); // server がキューを読んで配信済みと判定するまで待つ

  const rejoined = await connect();
  await rejoined.settle();
  console.log(rejoined.received.map((r) => `seq=${r.seq} ${JSON.stringify(r.text)}`).join("\n"));
  check(
    "unacked entries arrive",
    JSON.stringify(rejoined.texts()) === JSON.stringify(["切断中の一文目。", "切断中の二文目。"]),
    JSON.stringify(rejoined.texts()),
  );

  show("④ Overlapping catch-up and broadcast causes neither duplicates nor drops");
  // ★ speak() と connect() の間で待たないこと。poll() はクライアントの接続の有無に
  //   関係なく50msごとに動き、キューにある entry を見つけ次第 delivered 済みにする。
  //   ここで待つと m-3a は接続前に確実に delivered 済みになり、「配信済みのものだけを
  //   追いつきで送る」判定があってもなくても catchUp の送出内容が変わらなくなる
  //   （どちらの実装でも1回ずつ届く）。判定を外したときに違いが出るのは、
  //   catchUp が動く瞬間に「キューにはあるが、まだ delivered 済みではない」entry が
  //   残っている場合だけ。待たずに speak 直後に connect することで、その窓を狙う
  speak("m-3a", "追いつき対象の一文目。追いつき対象の二文目。");
  const racer = await connect();
  speak("m-3b", "重ねて配信される一文目。重ねて配信される二文目。");
  await racer.settle(600);
  console.log(`racer: ${racer.seqs().join(",")}`);
  check(
    "never receives the same seq twice",
    new Set(racer.seqs()).size === racer.seqs().length,
    racer.seqs().join(","),
  );
  check(
    "both the catch-up and the overlapped entries arrive exactly once, with no drops",
    ["追いつき対象の一文目。", "追いつき対象の二文目。", "重ねて配信される一文目。", "重ねて配信される二文目。"].every(
      (t) => racer.texts().includes(t),
    ),
    racer.texts().join(" / "),
  );

  await racer.close();
  await rejoined.close();

  show("⑤ Beyond the cap, the oldest entries are dropped first");
  const before = queueSize();
  const queueSeqs = () =>
    new Set(
      fs
        .readdirSync(queueDir)
        .filter((f) => f.endsWith(".json"))
        .map((f) => Number(f.slice(0, -5))),
    );

  // trim される前に、実際に積んだ seq を積んだ順で控える。「残っているのが末尾か」を
  // 見るには、後から remaining を並べ替えて自分自身と比べる（＝常に真になる）のではなく、
  // 外から見た「本当に積んだ列」と突き合わせる必要がある
  const pushed = [];
  for (let i = 0; i < 6; i++) {
    const before_ = queueSeqs();
    speak(`m-cap${i}`, `上限${i}の一文目。上限${i}の二文目。`);
    for (const s of queueSeqs()) if (!before_.has(s)) pushed.push(s);
  }
  pushed.sort((a, b) => a - b);

  const after = queueSize();
  console.log(`queue: ${before} entries → ${after} entries (cap ${QUEUE_MAX})`);
  check("does not exceed the cap", after <= QUEUE_MAX, `${after} entries`);

  const remaining = [...queueSeqs()].sort((a, b) => a - b);
  const expectedTail = pushed.slice(-QUEUE_MAX); // 新しい方から数えて上限件数ぶん
  check(
    "what remains is the tail of the pushed order (oldest dropped first)",
    JSON.stringify(remaining) === JSON.stringify(expectedTail),
    `remaining=${JSON.stringify(remaining)} expected=${JSON.stringify(expectedTail)}`,
  );

  show("⑥ A connection with an Origin header is rejected (#3)");
  let rejected = false;
  try {
    await connect({ headers: { Origin: "https://evil.example.com" } });
  } catch {
    rejected = true;
  }
  check("browsers cannot connect", rejected);

  show("⑦ Startup cleanup drops only old entries and keeps the latest one");
  // 全消し clear() をやめて dropOlderThan(STARTUP_KEEP_MS) にしたのは、CLI が delta
  // ごとに起動されるため。1つのメッセージの文は複数回のドレインに分かれて publish
  // されるので、全消しだと配信中メッセージの前半だけが消え、マスコットが段落の
  // 途中から喋り出す。「落ちている間に書かれた古い entry だけを捨て、直前に
  // 書かれたものは残す」ことを、両側（古い方・新しい方）を見て確かめる
  await stopServer();

  speak("m-offline-old", "落ちている間に書かれた古い発話です。");
  // 直前の speak() で新規に作られた1件（seq は単調増加なので、名前順で最後に来る）
  const offlineOldFile = fs
    .readdirSync(queueDir)
    .filter((f) => f.endsWith(".json"))
    .sort()
    .at(-1);
  // STARTUP_KEEP_MS（10秒）を確実に超える古さへ戻す
  const past = new Date(Date.now() - 20_000);
  fs.utimesSync(path.join(queueDir, offlineOldFile), past, past);

  speak("m-offline-new", "落ちている間に書かれた新しい発話です。");
  const offlineNewFile = fs
    .readdirSync(queueDir)
    .filter((f) => f.endsWith(".json"))
    .sort()
    .at(-1);

  await startServer();
  const afterRestart = await connect();
  await afterRestart.settle(600);

  check("the old entry is removed along with its file", !fs.existsSync(path.join(queueDir, offlineOldFile)));
  check(
    "the old utterance is not delivered",
    !afterRestart.texts().includes("落ちている間に書かれた古い発話です。"),
    afterRestart.texts().join(" / "),
  );
  check("the entry written last is kept", fs.existsSync(path.join(queueDir, offlineNewFile)));
  check(
    "the new utterance is delivered",
    afterRestart.texts().includes("落ちている間に書かれた新しい発話です。"),
    afterRestart.texts().join(" / "),
  );

  show("⑧ ★ [#29/A-1] The new generation is delivered even if numbering restarts while the server is running");

  // ★ この検証がユニットテストと別に要る理由。dispatcher の `delivered` は seq 単独キーで、
  //   CLI の `clear()` → `enqueue()` は**同一プロセス内で同期的に**走るので、
  //   50ms のポーリングが空のディレクトリを観測することはまず無い。
  //   「seq の集合は変わっていないのに中身が別世代に入れ替わった」状態が、
  //   実時間のポーリングの下で作られる。
  //
  // ★ **新世代の seq を旧世代より少なくすること。** 新しい seq が1つでも
  //   `delivered` の外に現れると、ループ側がそれを読んで世代違いに気づいてしまい、
  //   **先頭を読むプローブを消しても通ってしまう**（＝検査として成立しない）。
  //   1メッセージを複数文にして、1回の enqueue で旧世代の seq に収まる形にする。
  //
  // ★ クライアントを繋がない状態で溜めるのも肝。ack が一度でも来ていると
  //   prune で `delivered` が空になり、症状が出ない。
  await stopServer();
  fs.rmSync(queueDir, { recursive: true, force: true });
  fs.rmSync(path.join(runtime, "speech.jsonl"), { force: true });
  fs.rmSync(path.join(runtime, "speech.1.jsonl"), { force: true });
  fs.rmSync(path.join(runtime, "speech.state.json"), { force: true });

  speak("m-gen-a", "旧世代の一文目です。旧世代の二文目です。旧世代の三文目です。");
  const oldEpoch = JSON.parse(fs.readFileSync(path.join(runtime, "speech.state.json"), "utf8")).epoch;
  check("3 entries of the old generation are queued", queueSize() === 3, `${queueSize()} entries`);

  await startServer();
  // ★ 誰も繋がないまま poll させる。ここで全 entry が `delivered` に入る
  await sleep(400);

  // ランタイムルートを消したのと同じ状態にして、CLI に採番をやり直させる
  fs.rmSync(path.join(runtime, "speech.jsonl"), { force: true });
  fs.rmSync(path.join(runtime, "speech.state.json"), { force: true });
  speak("m-gen-b", "新世代の一文目です。新世代の二文目です。");
  const newEpoch = JSON.parse(fs.readFileSync(path.join(runtime, "speech.state.json"), "utf8")).epoch;
  check("numbering has restarted (epoch changes)", newEpoch !== oldEpoch, `${oldEpoch} → ${newEpoch}`);
  check("the new generation seq falls within the old generation range", queueSize() === 2, `${queueSize()} entries`);

  const afterRenumber = await connect();
  await afterRenumber.settle(800);

  check(
    "★ the new generation utterances arrive (not skipped by treating seq as a sole key)",
    afterRenumber.texts().includes("新世代の一文目です。") && afterRenumber.texts().includes("新世代の二文目です。"),
    afterRenumber.texts().join(" / "),
  );
  check(
    "the old generation utterances do not arrive",
    !afterRenumber.texts().some((t) => t.startsWith("旧世代")),
    afterRenumber.texts().join(" / "),
  );

  // ★ ここが本丸。世代が乗り換わっていないと、新 epoch の ack が拒否されてキューが減らない
  afterRenumber.ack(Math.max(...afterRenumber.seqs()));
  await sleep(400);
  check(
    "★ the new generation ack goes through and the queue empties (not stuck)",
    queueSize() === 0,
    `${queueSize()} entries / ${JSON.stringify(fs.readdirSync(queueDir))}`,
  );

  await afterRenumber.close();
} catch (err) {
  console.error(err);
  fail("runtime error");
} finally {
  cleanup();
}

await summarize(() => `server log:\n${serverLogs()}`);
