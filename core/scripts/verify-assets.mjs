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
import { check, disposableRoot, fail, killAll, requireBundles, show, spawnLogged, summarize } from "./lib/harness.mjs";

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
};

let server = null;
function cleanup() {
  killAll();
  fs.rmSync(root, { recursive: true, force: true });
}
process.on("exit", cleanup);

const base = `http://127.0.0.1:${PORT}`;

try {
  server = spawnLogged([SERVER], { env, label: "server" });
  await server.waitFor("[Server] Ready");

  show("① マニフェスト取得");
  const manifestRes = await fetch(`${base}/v1/assets`);
  check("200 が返る", manifestRes.status === 200, `status=${manifestRes.status}`);
  const { files } = await manifestRes.json();
  console.log(JSON.stringify(files, null, 2));

  const model = files.find((f) => f.path === "models/mascot.vrm");
  check("★ mascot.vrm が載っている", model !== undefined, JSON.stringify(files));
  check("size が実体と一致する", model?.size === modelBytes.length, `${model?.size} vs ${modelBytes.length}`);
  check("sha256 が実体と一致する", model?.sha256 === sha256(modelBytes), model?.sha256);
  check(
    "★ path の Ordinal 昇順で並ぶ",
    JSON.stringify(files.map((f) => f.path)) ===
      JSON.stringify(files.map((f) => f.path).sort((a, b) => (a < b ? -1 : a > b ? 1 : 0))),
    JSON.stringify(files.map((f) => f.path)),
  );

  show("② 全体取得");
  const full = await fetch(`${base}/v1/assets/models/mascot.vrm`);
  const fullBody = Buffer.from(await full.arrayBuffer());
  check("200 が返る", full.status === 200, `status=${full.status}`);
  check(
    "content-type は application/octet-stream",
    full.headers.get("content-type") === "application/octet-stream",
    full.headers.get("content-type"),
  );
  check(
    "★ accept-ranges は bytes（資産ルートだけ。/audio/ は none のまま）",
    full.headers.get("accept-ranges") === "bytes",
  );
  check("本体が実体と一致する", fullBody.equals(modelBytes));

  show("③ ★ 途中まで取ったことにして、Range で残りを取り直し、連結結果のハッシュが一致する");
  // 前半は②で取得済みの本体から切り出す（＝「ここまでは既に手元にある」状態を再現）。
  // 後半は実際に Range 付きで GET し直す ——ここが見たい本体
  const cutAt = Math.floor(modelBytes.length / 3);
  const rest = await fetch(`${base}/v1/assets/models/mascot.vrm`, { headers: { range: `bytes=${cutAt}-` } });
  check("206 が返る", rest.status === 206, `status=${rest.status}`);
  check(
    "Content-Range が正しい",
    rest.headers.get("content-range") === `bytes ${cutAt}-${modelBytes.length - 1}/${modelBytes.length}`,
    rest.headers.get("content-range"),
  );
  const restBody = Buffer.from(await rest.arrayBuffer());
  const reassembled = Buffer.concat([fullBody.subarray(0, cutAt), restBody]);
  check("連結した長さが本体と一致する", reassembled.length === modelBytes.length, `${reassembled.length}`);
  check("★ 連結結果の sha256 がマニフェストと一致する", sha256(reassembled) === model?.sha256, sha256(reassembled));

  show("④ 範囲外の Range は 416");
  const oob = await fetch(`${base}/v1/assets/models/mascot.vrm`, { headers: { range: `bytes=${modelBytes.length}-` } });
  check("416 が返る", oob.status === 416, `status=${oob.status}`);
  check(
    "Content-Range は bytes */size",
    oob.headers.get("content-range") === `bytes */${modelBytes.length}`,
    oob.headers.get("content-range"),
  );

  show("⑤ マニフェストに無いパスは 404");
  check("404 が返る", (await fetch(`${base}/v1/assets/models/other.vrm`)).status === 404);
  // ★ `..` を生で書かないこと。`fetch` の URL パーサが送信前に畳んでしまうので、
  //   サーバーには別のパスが届き、検査したつもりのものを検査していないものになる。
  //   エンコードした形はそのまま送られる（サーバー側がデコードしないことが効いているのを見る）
  for (const bad of [
    "/v1/assets/models/%2e%2e%2f%2e%2e%2fetc%2fpasswd",
    "/v1/assets/animations/%2e%2e/wave.vrma",
    "/v1/assets/animations/neutral/x.vrma",
  ]) {
    check(`★ 3つの形以外は 404: ${bad}`, (await fetch(`${base}${bad}`)).status === 404);
  }

  show("⑥ ★ 途中で切られてもサーバーが生きている");
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
  check("★ 中断を繰り返した後でも 200 を返す", after.status === 200, `status=${after.status}`);
  check("本体が実体と一致する", Buffer.from(await after.arrayBuffer()).equals(modelBytes));
} catch (err) {
  console.error("\n\x1b[31m検証中に例外が発生しました\x1b[0m");
  console.error(err);
  fail("例外");
}

await summarize(() => (server?.log ? `server のログ:\n${server.log}` : ""));
