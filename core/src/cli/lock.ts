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
  /** 所有権の印。既定はプロセスごとに一意な値 */
  token?: string;
}

const DEFAULT_STALE_MS = 60_000;

interface Owner {
  pid: number;
  token: string;
}

function ownerFilePath(lockDir: string): string {
  return path.join(lockDir, "owner.json");
}

function readOwner(lockDir: string): Owner | null {
  try {
    const parsed: unknown = JSON.parse(fs.readFileSync(ownerFilePath(lockDir), "utf-8"));
    if (typeof parsed === "object" && parsed !== null) {
      const { pid, token } = parsed as Partial<Owner>;
      if (typeof pid === "number" && Number.isInteger(pid) && typeof token === "string") return { pid, token };
    }
  } catch {
    // 作成途中 / 壊れている
  }
  return null;
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
 * ★ pid の生存確認を経過時間より**先に**行うこと。逆にすると、SIGKILL / SIGHUP / OOM で
 *   死んだプロセスのロックが残ったとき、hook が起動する CLI が丸ごと staleMs のあいだ
 *   無言で return し続ける（その間の発話がすべて遅延する）。
 *
 * 古さによる判定も残す。所有者が読めない（作成途中 / 壊れている）場合に加えて、
 * pid が再利用されて別のプロセスが生きて見えるケースの backstop になる。
 */
function isStale(lockDir: string, staleMs: number, now: number): boolean {
  const owner = readOwner(lockDir);
  if (owner !== null && !isProcessAlive(owner.pid)) return true;

  try {
    return now - fs.statSync(lockDir).mtimeMs >= staleMs;
  } catch {
    return false; // 消えていた。奪うまでもない
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
  const token = options.token ?? `${pid}-${process.hrtime.bigint()}`;

  fs.mkdirSync(path.dirname(lockDir), { recursive: true });

  const tryCreate = (): Lock | null => {
    try {
      fs.mkdirSync(lockDir);
    } catch (err) {
      if ((err as NodeJS.ErrnoException).code === "EEXIST") return null;
      throw err;
    }
    // 取得後に置く。読めない owner は「作成途中」なので isStale が古さで判定する
    try {
      fs.writeFileSync(ownerFilePath(lockDir), `${JSON.stringify({ pid, token })}\n`);
    } catch {
      // owner が書けなくてもロック自体は有効
    }

    // ★ 書いた印を読み直して、自分のものか確かめること。
    //   `isStale()` が true を返してから下の renameSync までの間に別プロセスが奪取を
    //   完了していると、rename が**その生きたロック**に当たって成功してしまう。
    //   そのとき負けた側はここで自分以外の印を見るので、諦められる。
    //
    //   キュー化で失敗モードが悪化したため塞いでいる。以前は seq の重複が
    //   speech.jsonl の重複行（＝マスコットが言い直すので気づける）で済んだが、
    //   今は seq がファイル名なので**同じ名前への上書き＝片方が無言で消える**。
    const owner = readOwner(lockDir);
    if (owner !== null && owner.token !== token) return null;

    return makeLock(lockDir, token);
  };

  const first = tryCreate();
  if (first) return first;

  if (!isStale(lockDir, staleMs, now())) return null;

  // ★ 奪取は rename で行うこと。rmSync → mkdir の順にすると mkdir の原子性が無効になり、
  //   2プロセスが同時に「放置ロックを消して作り直す」ことができてしまう
  //   （後から来た側の rmSync が、先に取った側の**生きたロック**を消す）。
  //   rename は成功するのが1プロセスだけなので、そこで勝者が決まる。
  const evicted = `${lockDir}.evicted-${token}`;
  try {
    fs.renameSync(lockDir, evicted);
  } catch {
    // 別プロセスに先を越された。素直に諦める
    return null;
  }
  fs.rmSync(evicted, { recursive: true, force: true });

  return tryCreate();
}

function makeLock(lockDir: string, token: string): Lock {
  let released = false;
  return {
    release() {
      if (released) return;
      released = true;

      // ★ 所有権を確認してから消すこと。確認せずに消すと、放置ロックとして奪われた側が
      //   終了時に**新しい保持者のロック**を消してしまう
      const owner = readOwner(lockDir);
      if (owner !== null && owner.token !== token) return;

      fs.rmSync(lockDir, { recursive: true, force: true });
    },
  };
}
