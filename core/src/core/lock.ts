/**
 * 単一ワーカーのロック。
 *
 * CLI は hook から**毎 delta 起動される**が、spool を処理してよいのは
 * ロックを取れた1プロセスだけ（CLAUDE.md「絶対に守ること」4）。`seq` の採番も
 * このロック下で行う。取れなかったプロセスは何もせず即終了する（先行ワーカーが拾う）。
 *
 * `src/cli/` と `src/core/` はどちらも npm 依存を持てない（docs/core.md）ので、
 * ロックライブラリは使わず**`mkdir` の原子性**で実装する。同名ディレクトリの
 * 作成は、成功するのが必ず1プロセスだけ。
 */

import * as fs from "fs";
import * as path from "path";

export interface Lock {
  release(): void;
}

export interface AcquireLockOptions {
  /** 所有印（owner.json）が読めないときだけ使う経過時間のしきい値。読めるなら pid の生死だけで判定する（isStale() 参照） */
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
 * 所有者が読めるなら pid の生死**だけ**で決める。古さも条件に加えると、
 * ドレインが staleMs を超えて長引いた・ラップトップがサスペンドから復帰しただけの
 * **生きた保持者**から奪ってしまう（実測: mtime だけで60秒経過扱いにすると
 * 生きたロックが奪われる）。
 *
 * ★ **AI要約（[#31]）で「長引くドレイン」が正常系になった。** 要約は `claude -p` を
 *   `execFileSync` で同期実行するので、1メッセージあたり数十秒（上限は `aiSummaryTimeoutMs`。
 *   既定60秒）ロックを保持したまま止まる。1ドレインで既定3件・設定の上限なら8件まで
 *   要約しうるので、保持時間が `DEFAULT_STALE_MS`（60秒）を**1メッセージでも超えうる**。
 *   ここに古さの条件を足すと、
 *   **要約中のワーカーからロックが奪われて2プロセスが同時に spool を処理し、発話順が壊れる**
 *   （CLAUDE.md「絶対に守ること」4）。時間で判定したくなったら、まずこの事実を思い出すこと。
 *
 * [#31]: https://github.com/schwarz9791/chatter-agent/issues/31
 *
 * ★ この結果、pid が再利用されると恒久的なロックになる穴が残る（死んだプロセスの
 *   pid を別の生きたプロセスが引き継ぐと、owner は永遠に「生きている」に見える）。
 *   意図的な判断: 「生きた保持者を60秒で奪う」方が実害が大きい。踏んだら
 *   `speak.lock` を手で消せば済む。
 *
 * 所有者が読めない場合（mkdir 直後で owner.json を書く前 / 壊れている）だけ、
 * 経過時間で判定する。読めないまま staleMs 続いたなら owner.json を書けずに死んだとみなす。
 */
function isStale(lockDir: string, staleMs: number, now: number): boolean {
  const owner = readOwner(lockDir);
  if (owner !== null) return !isProcessAlive(owner.pid);

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
    // ★ owner.json は排他作成（"wx"）で書くこと。mkdir 自体はこのプロセスのものでも、
    //   奪取（下の renameSync）と競合する経路がある限り「owner.json が既に存在する」
    //   可能性はゼロと言い切れない。"w"（上書き）だと他人の印を書き換えてしまう。
    //   書けなかったら諦めて null を返す。**lockDir を消してはいけない** —
    //   消すと release() と同じ「他人のロックを消す」経路になる。印の無いロックは
    //   isStale() の古さ判定（mkdir 直後の猶予）が回収する
    try {
      fs.writeFileSync(ownerFilePath(lockDir), `${JSON.stringify({ pid, token })}\n`, { flag: "wx" });
    } catch {
      return null;
    }

    // ★ 書いた印を読み直して、自分のものだと確認できたときだけ進めること。
    //   readOwner は ENOENT も壊れた JSON も同じ null に潰すので、null を「自分のもの」と
    //   みなして素通りすると所有権が無いのに進んでしまう。ここは奪取（下のブロック）が
    //   失敗して元に戻した直後に別プロセスが割り込んだ場合の backstop になる
    const owner = readOwner(lockDir);
    if (owner === null || owner.token !== token) return null;

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

  // ★ rename は lockDir が在りさえすれば成功する。isStale() が true を返してから
  //   この rename に着くまでの間に別プロセスが放置ロックを奪って取り直していると、
  //   rename がその**生きたロック**に当たって成功してしまう（実測: P2 holds a Lock: true /
  //   P3 holds a Lock: true — 両方が取れたと思い込む）。奪った現物を rename の後に
  //   もう一度見て、本当に放置ロックだったか確かめる。
  //   rename(2) はディレクトリ自身の mtime を変えないので、isStale(evicted) は
  //   奪う前と同じものを見られる
  if (!isStale(evicted, staleMs, now())) {
    // 生きたロックだった。戻して諦める（戻せないなら残骸を残さない）
    try {
      fs.renameSync(evicted, lockDir);
    } catch {
      fs.rmSync(evicted, { recursive: true, force: true });
    }
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

      // ★ 自分のものだと確認できたときだけ消すこと。readOwner は ENOENT も壊れた
      //   JSON も同じ null に潰すので、null を「自分のもの」とみなして素通りすると、
      //   放置ロックとして奪われた側が終了時に**新しい保持者のロック**を消してしまう
      //   （相互排除そのものが消える。実測: lockDir actually exists: false）。
      //   owner.json が消えている/壊れているときは「自分のものだと確認できない」ので
      //   何もしない。残骸は isStale() の古さ判定（owner null → mkdir 直後の猶予）が回収する
      const owner = readOwner(lockDir);
      if (owner === null || owner.token !== token) return;

      fs.rmSync(lockDir, { recursive: true, force: true });
    },
  };
}
