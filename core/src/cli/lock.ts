/**
 * 単一ワーカーのロック。
 *
 * CLI は hook から**毎 delta 起動される**が、spool を処理してよいのは
 * ロックを取れた1プロセスだけ（CLAUDE.md「絶対に守ること」4）。`seq` の採番も
 * このロック下で行う。取れなかったプロセスは何もせず即終了する（先行ワーカーが拾う）。
 *
 * `src/cli/` は npm 依存を持てない（docs/core.md）ので、ロックライブラリは使わず
 * **`mkdir` の原子性**で実装する。同名ディレクトリの作成は、成功するのが必ず1プロセスだけ。
 */

import * as fs from "fs";
import * as path from "path";

export interface Lock {
  release(): void;
}

export interface AcquireLockOptions {
  /** この時間より古く、かつプロセスが生きていないロックは奪う */
  staleMs?: number;
  /** テスト用 */
  now?: () => number;
  pid?: number;
}

const DEFAULT_STALE_MS = 60_000;

function pidFilePath(lockDir: string): string {
  return path.join(lockDir, "pid");
}

/** そのプロセスが生きているか。権限が無くて確認できない場合は「生きている」に倒す */
function isProcessAlive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch (err) {
    return (err as NodeJS.ErrnoException).code === "EPERM";
  }
}

/**
 * 放置されたロックか判定する。
 *
 * 「古い」だけでは足りない（要約などで長く走っているワーカーを殺してしまう）ので、
 * **プロセスが死んでいること**と**十分に古いこと**の両方を要求する。
 * pid が読めない（作成途中 / 壊れている）ロックは、古い場合にだけ奪う。
 */
function isStale(lockDir: string, staleMs: number, now: number): boolean {
  let age: number;
  try {
    age = now - fs.statSync(lockDir).mtimeMs;
  } catch {
    return false; // 消えていた。奪うまでもない
  }
  if (age < staleMs) return false;

  try {
    const pid = Number.parseInt(fs.readFileSync(pidFilePath(lockDir), "utf-8").trim(), 10);
    if (!Number.isInteger(pid) || pid <= 0) return true;
    return !isProcessAlive(pid);
  } catch {
    return true;
  }
}

/**
 * ロックを取る。取れなければ `null`（呼び出し側は即終了すること）。
 * 放置ロックを見つけた場合だけ、1回だけ奪って取り直す。
 */
export function acquireLock(lockDir: string, options: AcquireLockOptions = {}): Lock | null {
  const staleMs = options.staleMs ?? DEFAULT_STALE_MS;
  const now = options.now ?? Date.now;
  const pid = options.pid ?? process.pid;

  fs.mkdirSync(path.dirname(lockDir), { recursive: true });

  const tryCreate = (): boolean => {
    try {
      fs.mkdirSync(lockDir);
    } catch (err) {
      if ((err as NodeJS.ErrnoException).code === "EEXIST") return false;
      throw err;
    }
    // 取得後に置く。読めない pid は「作成途中」なので isStale が古さで判定する
    try {
      fs.writeFileSync(pidFilePath(lockDir), `${pid}\n`);
    } catch {
      // pid が書けなくてもロック自体は有効
    }
    return true;
  };

  if (tryCreate()) return makeLock(lockDir);

  if (!isStale(lockDir, staleMs, now())) return null;

  // 放置ロックを片付けて取り直す。ここで別プロセスに先を越されたら素直に諦める
  fs.rmSync(lockDir, { recursive: true, force: true });
  return tryCreate() ? makeLock(lockDir) : null;
}

function makeLock(lockDir: string): Lock {
  let released = false;
  return {
    release() {
      if (released) return;
      released = true;
      fs.rmSync(lockDir, { recursive: true, force: true });
    },
  };
}
