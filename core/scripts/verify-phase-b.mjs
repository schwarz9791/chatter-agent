/**
 * Phase B の受け入れ確認。
 *
 * server を実際に起動し、WebSocket クライアントを繋いで
 * 「1文ずつ流れる」「?since= で欠落を埋められる」「ローテートを跨いでも配信が続く」を見る。
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
/** ローテートを跨ぐ確認のため、上限をわざと小さくする */
const MAX_BYTES = 500;

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
fs.mkdirSync(spoolDir, { recursive: true });

const env = {
  ...process.env,
  XDG_CONFIG_HOME: root,
  CHATTER_AGENT_HOST: "127.0.0.1",
  CHATTER_AGENT_PORT: String(PORT),
  CHATTER_AGENT_SPEECH_LOG_MAX_BYTES: String(MAX_BYTES),
  CHATTER_AGENT_SPEECH_LOG_GENERATIONS: "3",
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

/** spool に delta を1本置いて CLI を起動する（plugin の bash hook の代わり） */
function speak(messageId, index, text, final = false) {
  const payload = {
    session_id: "sess-1",
    hook_event_name: "MessageDisplay",
    turn_id: "turn-1",
    message_id: messageId,
    index,
    final,
    delta: text,
  };
  fs.appendFileSync(path.join(spoolDir, `${messageId}.jsonl`), JSON.stringify(payload) + "\n");
  const { status } = spawnSync(process.execPath, [CLI], { env, stdio: "ignore" });
  if (status !== 0) throw new Error(`CLI が異常終了しました (status=${status})`);
}

/** 接続の前に message ハンドラを張る（?since= の送り直しは接続直後に同期で来る） */
function connect(query = "") {
  const socket = new WebSocket(`ws://127.0.0.1:${PORT}${query}`);
  const received = [];
  socket.on("message", (data) => received.push(JSON.parse(String(data))));
  return new Promise((resolve, reject) => {
    socket.once("open", () =>
      resolve({
        socket,
        received,
        texts: () => received.map((r) => r.text),
        seqs: () => received.map((r) => r.seq),
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

const server = spawn(process.execPath, [SERVER], { env, stdio: ["ignore", "pipe", "pipe"] });
let serverLog = "";
server.stdout.on("data", (d) => (serverLog += d));
server.stderr.on("data", (d) => (serverLog += d));

function cleanup() {
  server.kill("SIGTERM");
  fs.rmSync(root, { recursive: true, force: true });
}

try {
  // "Ready" が出るまで待つ
  for (let i = 0; i < 100 && !serverLog.includes("[Server] Ready"); i++) await sleep(50);
  if (!serverLog.includes("[Server] Ready")) throw new Error(`server が起動しませんでした:\n${serverLog}`);

  show("① 接続したクライアントに1文ずつ流れる");
  const client = await connect();
  speak("m-1", 0, "確認します。ログを見ます。");
  speak("m-1", 1, "以上です。", true);
  await client.settle();
  console.log(client.received.map((r) => `seq=${r.seq} ${r.kind} ${JSON.stringify(r.text)}`).join("\n"));
  check(
    "1文1行で届く",
    JSON.stringify(client.texts()) === JSON.stringify(["確認します。", "ログを見ます。", "以上です。"]),
  );
  check("seq が連続している", JSON.stringify(client.seqs()) === JSON.stringify([1, 2, 3]));

  show("② 切断中の分を ?since= で埋められる");
  await client.close();
  speak("m-2", 0, "切断中の一文目。切断中の二文目。", true);
  await sleep(200);

  const rejoined = await connect(`/?since=${client.seqs().at(-1)}`);
  await rejoined.settle();
  console.log(rejoined.received.map((r) => `seq=${r.seq} ${JSON.stringify(r.text)}`).join("\n"));
  check(
    "切断中に流れた分だけが送り直される",
    JSON.stringify(rejoined.texts()) === JSON.stringify(["切断中の一文目。", "切断中の二文目。"]),
    JSON.stringify(rejoined.texts()),
  );
  check("重複して送られない", new Set(rejoined.seqs()).size === rejoined.seqs().length);

  show("③ 追いついているクライアントには何も送り直さない");
  const uptodate = await connect(`/?since=${rejoined.seqs().at(-1)}`);
  await uptodate.settle(300);
  check("送り直しは0件", uptodate.received.length === 0, `${uptodate.received.length} 件届いた`);

  show("④ ローテートを跨いでも配信が続く");
  // 実運用の条件（世代 5MB / 監視はサブ秒で反応）に合わせ、書き込みの合間に server が
  // 読める余地を与える。ローテートは跨ぐが、1回の読み取りの間に流れる世代は1つまで。
  const before = fs.readdirSync(runtime).filter((f) => f.startsWith("speech."));
  for (let i = 0; i < 6; i++) {
    speak(`m-r${i}`, 0, `ローテート${i}です。おわり${i}。`, true);
    await sleep(150);
  }
  await uptodate.settle(600);
  const after = fs.readdirSync(runtime).filter((f) => f.startsWith("speech."));
  console.log(`speech ファイル: ${before.join(", ")}  →  ${after.join(", ")}`);
  check("実際にローテートが起きた", after.includes("speech.1.jsonl"));
  check(
    "ローテートを跨いだ分も全部届いた",
    [...Array(6).keys()].every((i) => uptodate.texts().includes(`ローテート${i}です。`)),
    uptodate.texts().join(" / "),
  );
  check(
    "配信された seq が連続している（取りこぼしも重複も無い）",
    uptodate.seqs().every((s, i, a) => i === 0 || s === a[i - 1] + 1),
    uptodate.seqs().join(","),
  );

  show("⑤ 監視が追いつかないほどの連投（既知の限界。壊れないことだけを見る）");
  // 1回の読み取りの間に2世代以上が流れると、中間世代は位置を当てにできないので配信されない。
  // 実運用でここに至るには、読み取りの合間に上限サイズの2倍（既定なら 10MB）が書かれる必要がある。
  // 欠落しても「順序が入れ替わらない」「重複しない」ことは保たれる、というのがここでの保証。
  const burstStart = uptodate.seqs().at(-1) ?? 0;
  for (let i = 0; i < 8; i++) speak(`m-b${i}`, 0, `連投${i}です。おわり${i}。`, true);
  await uptodate.settle(800);

  const burst = uptodate.seqs().filter((s) => s > burstStart);
  const dropped = burst.length === 0 ? 0 : burst.at(-1) - burstStart - burst.length;
  console.log(`連投 16 行のうち ${burst.length} 行が配信され、${dropped} 行が欠落しました`);
  check(
    "配信された分の順序は保たれている",
    burst.every((s, i, a) => i === 0 || s > a[i - 1]),
    burst.join(","),
  );
  check("重複配信は無い", new Set(burst).size === burst.length, burst.join(","));
  check("欠落は seq の飛びとして見える（クライアントが検出できる）", dropped >= 0);

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
