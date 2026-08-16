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

import { spawn, spawnSync } from "node:child_process";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { WebSocket } from "ws";

const CORE = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const REPO = path.resolve(CORE, "..");
const CLI = path.join(REPO, "plugin", "bin", "chatter-agent-speak.mjs");
const SERVER = path.join(CORE, "dist", "chatter-agent-server.mjs");

const PORT = 18570;
/** 上限で古い方から捨てるのを見たいので、わざと小さくする */
const QUEUE_MAX = 6;

for (const [label, file] of [
  ["CLI", CLI],
  ["server", SERVER],
]) {
  if (!fs.existsSync(file)) {
    console.error(`${label} のバンドルがありません: ${file}\n先に core/ で npm run build を実行してください。`);
    process.exit(1);
  }
}

const root = fs.mkdtempSync(path.join(os.tmpdir(), "chatter-agent-phase-b-"));
const runtime = path.join(root, "chatter-agent");
const spoolDir = path.join(runtime, "spool");
const queueDir = path.join(runtime, "speech");
fs.mkdirSync(spoolDir, { recursive: true });

const env = {
  ...process.env,
  XDG_CONFIG_HOME: root,
  CHATTER_AGENT_HOST: "127.0.0.1",
  CHATTER_AGENT_PORT: String(PORT),
  CHATTER_AGENT_SPEECH_QUEUE_MAX_ENTRIES: String(QUEUE_MAX),
};

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
  const { status } = spawnSync(process.execPath, [CLI], { env, stdio: "ignore" });
  if (status !== 0) throw new Error(`CLI が異常終了しました (status=${status})`);
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
        /** 「seq N まで喋った」 */
        ack: (seq) => socket.send(JSON.stringify({ type: "spoken", seq })),
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

let server;
let serverLog = "";

function startServer() {
  server = spawn(process.execPath, [SERVER], { env, stdio: ["ignore", "pipe", "pipe"] });
  server.stdout.on("data", (d) => (serverLog += d));
  server.stderr.on("data", (d) => (serverLog += d));
}

async function waitForReady() {
  const mark = "[Server] Ready";
  const seen = serverLog.lastIndexOf(mark);
  for (let i = 0; i < 100; i++) {
    if (serverLog.lastIndexOf(mark) > seen || (seen === -1 && serverLog.includes(mark))) return;
    await sleep(50);
  }
  throw new Error(`server が起動しませんでした:\n${serverLog}`);
}

async function stopServer() {
  if (!server) return;
  const dead = new Promise((done) => server.once("exit", done));
  server.kill("SIGTERM");
  await Promise.race([dead, sleep(3000)]);
}

function cleanup() {
  server?.kill("SIGKILL");
  fs.rmSync(root, { recursive: true, force: true });
}

try {
  startServer();
  await waitForReady();

  show("① 接続したクライアントに1文ずつ流れる");
  const client = await connect();
  speak("m-1", "確認します。ログを見ます。以上です。");
  await client.settle();
  console.log(client.received.map((r) => `seq=${r.seq} ${r.kind} ${JSON.stringify(r.text)}`).join("\n"));
  check(
    "1文1行で届く",
    JSON.stringify(client.texts()) === JSON.stringify(["確認します。", "ログを見ます。", "以上です。"]),
    JSON.stringify(client.texts()),
  );
  check("seq が連続している", JSON.stringify(client.seqs()) === JSON.stringify([1, 2, 3]));

  show("② ack でキューが減る");
  console.log(`ack 前のキュー: ${queueSize()} 件`);
  check("喋る前はキューに残っている", queueSize() === 3, `${queueSize()} 件`);

  client.ack(2);
  await sleep(300);
  console.log(`seq<=2 を ack した後: ${queueSize()} 件`);
  check("累積 ack で seq<=2 が消える", queueSize() === 1, `${queueSize()} 件`);

  client.ack(3);
  await sleep(300);
  check("全部 ack すれば空になる", queueSize() === 0, `${queueSize()} 件`);

  show("③ 切断中に書かれた分が、接続時に届く（?since= の代わり）");
  await client.close();
  speak("m-2", "切断中の一文目。切断中の二文目。");
  await sleep(300); // server がキューを読んで配信済みと判定するまで待つ

  const rejoined = await connect();
  await rejoined.settle();
  console.log(rejoined.received.map((r) => `seq=${r.seq} ${JSON.stringify(r.text)}`).join("\n"));
  check(
    "未 ack の分が届く",
    JSON.stringify(rejoined.texts()) === JSON.stringify(["切断中の一文目。", "切断中の二文目。"]),
    JSON.stringify(rejoined.texts()),
  );

  show("④ 追いつき送出とブロードキャストが重なっても二重配信・取りこぼしにならない");
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
  check("同じ seq を2度受け取らない", new Set(racer.seqs()).size === racer.seqs().length, racer.seqs().join(","));
  check(
    "追いつき分・重ねた分の両方が、一度ずつ取りこぼさず届く",
    ["追いつき対象の一文目。", "追いつき対象の二文目。", "重ねて配信される一文目。", "重ねて配信される二文目。"].every(
      (t) => racer.texts().includes(t),
    ),
    racer.texts().join(" / "),
  );

  await racer.close();
  await rejoined.close();

  show("⑤ 上限を超えたら古い方から捨てる");
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
  console.log(`キュー: ${before} 件 → ${after} 件（上限 ${QUEUE_MAX}）`);
  check("上限を超えない", after <= QUEUE_MAX, `${after} 件`);

  const remaining = [...queueSeqs()].sort((a, b) => a - b);
  const expectedTail = pushed.slice(-QUEUE_MAX); // 新しい方から数えて上限件数ぶん
  check(
    "残っているのは、積んだ順の末尾（古い方から捨てられている）",
    JSON.stringify(remaining) === JSON.stringify(expectedTail),
    `remaining=${JSON.stringify(remaining)} expected=${JSON.stringify(expectedTail)}`,
  );

  show("⑥ Origin 付きの接続は拒否される（#3）");
  let rejected = false;
  try {
    await connect({ headers: { Origin: "https://evil.example.com" } });
  } catch {
    rejected = true;
  }
  check("ブラウザからは繋がらない", rejected);

  show("⑦ 起動時の掃除は、古い entry だけを捨てて直前の entry は残す");
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

  startServer();
  await waitForReady();
  const afterRestart = await connect();
  await afterRestart.settle(600);

  check("古い entry はファイルごと捨てられる", !fs.existsSync(path.join(queueDir, offlineOldFile)));
  check(
    "古い発話は配信されない",
    !afterRestart.texts().includes("落ちている間に書かれた古い発話です。"),
    afterRestart.texts().join(" / "),
  );
  check("直前に書かれた entry は残る", fs.existsSync(path.join(queueDir, offlineNewFile)));
  check(
    "新しい発話は配信される",
    afterRestart.texts().includes("落ちている間に書かれた新しい発話です。"),
    afterRestart.texts().join(" / "),
  );

  show("結果");
  if (failures.length > 0) {
    console.log(`\x1b[31m${failures.length} 件失敗\x1b[0m`);
    console.log(`\nserver のログ:\n${serverLog}`);
  } else {
    console.log("\x1b[32mすべて PASS\x1b[0m");
  }
} catch (err) {
  console.error(err);
  console.error(`\nserver のログ:\n${serverLog}`);
  failures.push("実行エラー");
} finally {
  cleanup();
}

process.exit(failures.length === 0 ? 0 : 1);
