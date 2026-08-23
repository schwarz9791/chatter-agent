/**
 * `verify:*` スクリプトの共通部分。
 *
 * 3本（`verify-tts` / `verify-player` / `verify-phase-b`）が同じものを持っていた:
 * 結果の記録、ポーリング待ち、使い捨ての `XDG_CONFIG_HOME`、スタブ用の WAV、
 * 子プロセスの起動と「この行が出るまで待つ」。
 *
 * ★ **判定そのものはここに置かない。** ここに入れてよいのは「どの検証でも同じ形になるもの」
 *   だけで、シナリオ固有のスタブ（合成エンジン / WebSocket サーバー）は各スクリプトに残す。
 *   共通化すると、落ちたときに「スタブの挙動」と「本物の挙動」のどちらを疑うかが増える。
 *
 * ★ **`vitest` からは使わない。** ここは「ビルド済みバンドルを実際に起動して外から見る」
 *   ための道具で、単体テストとは目的が違う。
 */

import { spawn } from "node:child_process";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";

// ── 待つ ───────────────────────────────────────────────────────────────────

export const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * `predicate` が真になるまで待つ。真になったら true、時間切れなら false。
 *
 * 既定は広めに取る。CI（ubuntu）は手元より遅く、Node の初回起動と合成 2 往復が乗る。
 */
export async function until(predicate, timeoutMs = 10_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (predicate()) return true;
    await sleep(20);
  }
  return false;
}

// ── 結果の記録 ─────────────────────────────────────────────────────────────

const failures = [];

export function check(label, ok, detail) {
  if (!ok) failures.push(label);
  const mark = ok ? "\x1b[32mPASS\x1b[0m" : "\x1b[31mFAIL\x1b[0m";
  console.log(`${mark}  ${label}${detail && !ok ? `\n      ${detail}` : ""}`);
}

export function show(title) {
  console.log(`\n\x1b[1m--- ${title} ---\x1b[0m`);
}

/** 判定ではなく実行そのものが失敗したとき（catch 経路） */
export function fail(label) {
  failures.push(label);
}

/**
 * 結果を出して終わる。**`process.exit` まで行うので、呼んだら後続は走らない。**
 *
 * @param diagnostics 失敗したときだけ呼ばれる、ログのダンプを返す関数
 *
 * ★ **`process.exitCode` に倒さないこと。** 呼び出し側の catch 経路では子プロセスや
 *   WebSocket サーバーが残ってイベントループが空にならないので、自然終了に任せると
 *   プロセスがハングする。
 *
 * ★ **exit の前に書き込みの排出を待つこと。** 診断ダンプは数百KBになりうる。macOS では
 *   パイプへの書き込みが非同期なので、待たずに exit すると 64KiB で切れる
 *   （Linux と TTY は同期なので CI では起きない ＝ 手元でだけ再現する）。
 */
export async function summarize(diagnostics) {
  show("結果");
  if (failures.length === 0) {
    console.log("\x1b[32mすべて PASS\x1b[0m");
  } else {
    console.log(`\x1b[31m${failures.length} 件 FAIL\x1b[0m`);
    for (const label of failures) console.log(`  - ${label}`);
    const dump = diagnostics?.();
    if (dump) console.log(`\n${dump}`);
  }

  const exitCode = failures.length === 0 ? 0 : 1;
  await new Promise((resolve) => process.stdout.write("", resolve));
  await new Promise((resolve) => process.stderr.write("", resolve));
  process.exit(exitCode);
}

// ── 使い捨てのランタイムルート ─────────────────────────────────────────────

/**
 * `XDG_CONFIG_HOME` に渡す使い捨てのディレクトリ。実際の `~/.config/chatter-agent` は汚さない。
 *
 * `runtime` は `chatter-agent` を掘った先で、`spool/` や `speech/` の親になる。
 */
export function disposableRoot(prefix) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `chatter-agent-${prefix}-`));
  return { root, runtime: path.join(root, "chatter-agent") };
}

/** ビルド済みバンドルが揃っているか。無ければその場で終わる */
export function requireBundles(entries) {
  for (const [label, file] of entries) {
    if (fs.existsSync(file)) continue;
    console.error(`${label} のバンドルがありません: ${file}\n先に core/ で npm run build を実行してください。`);
    process.exit(1);
  }
}

// ── スタブが返す WAV ───────────────────────────────────────────────────────

/**
 * 長さだけ正しい RIFF/PCM。player はここから再生タイムアウトを導く。
 *
 * ★ 中身は無音でよいが、**ヘッダは本物にすること**。`voicevoxClient` が RIFF/WAVE の
 *   マジックを見るので、`Buffer.from("RIFF")` だけでは弾かれる。
 */
export function makeWav(seconds) {
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

// ── 子プロセス ─────────────────────────────────────────────────────────────

const running = [];

/**
 * ビルド済みバンドルを Node で起動し、stdout / stderr を1本のログに溜める。
 *
 * ★ **stdout と stderr を分けないこと。** 見たいのは時系列で、どちらに出たかではない。
 *   分けると「警告の後に Ready が出たか」のような順序の検査が書けなくなる。
 *
 * ★ **ログはハンドルごとに持つ。** プロセスをまたいで1本に溜めると、再起動を挟む検証で
 *   「今回の起動で出た行か」を判定するために `lastIndexOf` の比較が要る。
 */
export function spawnLogged(argv, options = {}) {
  const { env, label = "プロセス" } = options;
  const child = spawn(process.execPath, argv, { env, stdio: ["ignore", "pipe", "pipe"] });
  const handle = {
    label,
    child,
    log: "",
    /** 終了コード。動いている間は null */
    exited: null,

    /** ログに `mark` が出るまで待つ。出ないまま死んだら例外 */
    async waitFor(mark, timeoutMs = 15_000) {
      const ok = await until(() => handle.log.includes(mark) || handle.exited !== null, timeoutMs);
      if (!ok || handle.exited !== null) {
        throw new Error(`${label} が「${mark}」を出しませんでした（exited=${handle.exited}）:\n${handle.log}`);
      }
      return handle;
    },

    /** SIGTERM で止め、`graceMs` 待っても死ななければ SIGKILL */
    async stop(graceMs = 4000) {
      if (handle.exited !== null) return handle;
      const dead = new Promise((done) => child.once("exit", done));
      child.kill("SIGTERM");
      await Promise.race([dead, sleep(graceMs)]);
      if (handle.exited === null) child.kill("SIGKILL");
      return handle;
    },
  };

  child.stdout.on("data", (chunk) => (handle.log += chunk));
  child.stderr.on("data", (chunk) => (handle.log += chunk));
  child.on("exit", (code) => (handle.exited = code ?? -1));
  running.push(handle);
  return handle;
}

/** これまでに `spawnLogged` で起動したもの全部（診断ダンプと後片付けに使う） */
export function spawned() {
  return running;
}

/** 後片付け。`process.on("exit")` から呼ぶので**同期**であること */
export function killAll() {
  for (const handle of running) handle.child.kill("SIGKILL");
}
