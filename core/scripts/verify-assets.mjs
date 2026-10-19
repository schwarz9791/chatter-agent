/**
 * 素材配布（#117）の受け入れ確認。
 *
 *   cd core && npm run build && npm run verify:assets
 *
 * 見るのは「本物の `chatter-agent-server` が `/v1/assets` のマニフェストと
 * `/v1/assets/<path>` を実際に配れるか」。**合成エンジンは要らない**
 * （`CHATTER_AGENT_TTS_SPAWN=0` で止める。渡さないと開発機で本物の AivisSpeech が起きる）。
 *
 * 使い捨ての XDG_CONFIG_HOME を掘るので、実際の ~/.config/chatter-agent は汚さない。
 * 127.0.0.1 に bind するので macOS のローカルネットワーク許可ダイアログも出ない。
 */

import * as crypto from "node:crypto";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import {
  check,
  disposableRoot,
  entryArgv,
  fail,
  killAll,
  requireBundles,
  show,
  spawnLogged,
  summarize,
} from "./lib/harness.mjs";

const CORE = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const SERVER = path.join(CORE, "dist", "chatter-agent-server.mjs");
const PORT = 18573;

requireBundles([["server", SERVER]]);

// ── 素材を仕込む ────────────────────────────────────────────────────────────

const { root, runtime } = disposableRoot("assets");
const modelsDir = path.join(runtime, "models");
const happyDir = path.join(runtime, "animations", "happy");
fs.mkdirSync(modelsDir, { recursive: true });
fs.mkdirSync(happyDir, { recursive: true });

const sha256 = (buf) => crypto.createHash("sha256").update(buf).digest("hex");

// モデルは実物と同様に数百KBある想定。Range で途中から取り直せることを見るため十分な大きさにする
const modelBytes = crypto.randomBytes(200_000);
const happyBytes = crypto.randomBytes(20_000);
fs.writeFileSync(path.join(modelsDir, "mascot.vrm"), modelBytes);
fs.writeFileSync(path.join(runtime, "animations", "idle.vrma"), crypto.randomBytes(1_000));
fs.writeFileSync(path.join(happyDir, "wave.vrma"), happyBytes);

const env = {
  ...process.env,
  XDG_CONFIG_HOME: root,
  CHATTER_AGENT_HOST: "127.0.0.1",
  CHATTER_AGENT_PORT: String(PORT),
  CHATTER_AGENT_TTS_SPAWN: "0",
  // ★ 感情判定は見ないシナリオなので固定する。既定（ollaya・spawn=true）のままだと、
  //   開発機に ollaya が入っていれば本物の `ollaya serve` が起動されうる
  CHATTER_AGENT_EMOTION_CLASSIFIER: "dictionary",
};

let server = null;
function cleanup() {
  killAll();
  fs.rmSync(root, { recursive: true, force: true });
}
process.on("exit", cleanup);

const base = `http://127.0.0.1:${PORT}`;

try {
  server = spawnLogged(entryArgv("server", SERVER), { env, label: "server" });
  await server.waitFor("[Server] Ready");

  show("① Fetch the manifest");
  const manifestRes = await fetch(`${base}/v1/assets`);
  check("returns 200", manifestRes.status === 200, `status=${manifestRes.status}`);
  const { files } = await manifestRes.json();
  console.log(JSON.stringify(files, null, 2));

  const model = files.find((f) => f.path === "models/mascot.vrm");
  check("★ mascot.vrm is listed", model !== undefined, JSON.stringify(files));
  check("size matches the actual file", model?.size === modelBytes.length, `${model?.size} vs ${modelBytes.length}`);
  check("sha256 matches the actual file", model?.sha256 === sha256(modelBytes), model?.sha256);
  check(
    "★ sorted by path in Ordinal ascending order",
    JSON.stringify(files.map((f) => f.path)) ===
      JSON.stringify(files.map((f) => f.path).sort((a, b) => (a < b ? -1 : a > b ? 1 : 0))),
    JSON.stringify(files.map((f) => f.path)),
  );

  show("② Fetch the whole file");
  const full = await fetch(`${base}/v1/assets/models/mascot.vrm`);
  const fullBody = Buffer.from(await full.arrayBuffer());
  check("returns 200", full.status === 200, `status=${full.status}`);
  check(
    "content-type is application/octet-stream",
    full.headers.get("content-type") === "application/octet-stream",
    full.headers.get("content-type"),
  );
  check(
    "★ accept-ranges is bytes (asset routes only; /audio/ stays none)",
    full.headers.get("accept-ranges") === "bytes",
  );
  check("body matches the actual file", fullBody.equals(modelBytes));

  show(
    "③ ★ Pretend the download stopped midway, refetch the rest with Range, and the hash of the concatenation matches",
  );
  // 前半は②で取得済みの本体から切り出す（＝「ここまでは既に手元にある」状態を再現）。
  // 後半は実際に Range 付きで GET し直す ——ここが見たい本体
  const cutAt = Math.floor(modelBytes.length / 3);
  const rest = await fetch(`${base}/v1/assets/models/mascot.vrm`, { headers: { range: `bytes=${cutAt}-` } });
  check("returns 206", rest.status === 206, `status=${rest.status}`);
  check(
    "Content-Range is correct",
    rest.headers.get("content-range") === `bytes ${cutAt}-${modelBytes.length - 1}/${modelBytes.length}`,
    rest.headers.get("content-range"),
  );
  const restBody = Buffer.from(await rest.arrayBuffer());
  const reassembled = Buffer.concat([fullBody.subarray(0, cutAt), restBody]);
  check("concatenated length matches the body", reassembled.length === modelBytes.length, `${reassembled.length}`);
  check(
    "★ sha256 of the concatenation matches the manifest",
    sha256(reassembled) === model?.sha256,
    sha256(reassembled),
  );

  show("④ An out-of-range Range returns 416");
  const oob = await fetch(`${base}/v1/assets/models/mascot.vrm`, { headers: { range: `bytes=${modelBytes.length}-` } });
  check("returns 416", oob.status === 416, `status=${oob.status}`);
  check(
    "Content-Range is bytes */size",
    oob.headers.get("content-range") === `bytes */${modelBytes.length}`,
    oob.headers.get("content-range"),
  );

  show("⑤ A path not in the manifest returns 404");
  check("returns 404", (await fetch(`${base}/v1/assets/models/other.vrm`)).status === 404);
  // ★ `..` を生で書かないこと。`fetch` の URL パーサが送信前に畳んでしまうので、
  //   サーバーには別のパスが届き、検査したつもりのものを検査していないものになる。
  //   エンコードした形はそのまま送られる（サーバー側がデコードしないことが効いているのを見る）
  for (const bad of [
    "/v1/assets/models/%2e%2e%2f%2e%2e%2fetc%2fpasswd",
    "/v1/assets/animations/%2e%2e/wave.vrma",
    "/v1/assets/animations/neutral/x.vrma",
  ]) {
    check(`★ anything other than the three shapes returns 404: ${bad}`, (await fetch(`${base}${bad}`)).status === 404);
  }

  show("⑥ ★ The server stays alive when a download is cut midway");
  // 帯域の細い経路では普通に起きる。読み出し側を閉じ損ねていると、
  // 症状は「しばらく使うと配れなくなる」という見分けにくい壊れ方になる
  for (let i = 0; i < 20; i++) {
    const controller = new AbortController();
    try {
      const aborted = await fetch(`${base}/v1/assets/models/mascot.vrm`, { signal: controller.signal });
      const reader = aborted.body.getReader();
      await reader.read();
      controller.abort();
      await reader.cancel().catch(() => {});
    } catch {
      // 切った側が見る例外はここでは問わない
    }
  }
  const after = await fetch(`${base}/v1/assets/models/mascot.vrm`);
  check("★ still returns 200 after repeated aborts", after.status === 200, `status=${after.status}`);
  check("body matches the actual file", Buffer.from(await after.arrayBuffer()).equals(modelBytes));
} catch (err) {
  console.error("\n\x1b[31mAn exception occurred during verification\x1b[0m");
  console.error(err);
  fail("exception");
}

await summarize(() => (server?.log ? `server log:\n${server.log}` : ""));
