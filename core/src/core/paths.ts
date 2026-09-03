/**
 * ランタイムのパス解決。
 *
 * ★ 規則を単純に保つこと。plugin の bash hook が**同じ spool パスを自力で組み立てる**ため、
 *   `${XDG_CONFIG_HOME:-$HOME/.config}/chatter-agent/spool` の一行で書ける以上のことをしない。
 *   条件分岐や環境変数を増やすと、bash 側と Node 側の実装が静かにズレる。
 */

import * as os from "os";
import * as path from "path";

/**
 * パス解決に必要な環境。テストから偽の環境を注入できるようまとめてある。
 * 既定引数は呼び出し時に評価されるため、モジュール読み込み時に os.homedir() を固定しない。
 */
export interface PathEnv {
  platform: NodeJS.Platform;
  homedir: string;
  env: NodeJS.ProcessEnv;
}

export function currentPathEnv(): PathEnv {
  return { platform: process.platform, homedir: os.homedir(), env: process.env };
}

function xdgConfigHome(e: PathEnv): string {
  return e.env.XDG_CONFIG_HOME || path.join(e.homedir, ".config");
}

function appData(e: PathEnv): string {
  return e.env.APPDATA || path.join(e.homedir, "AppData", "Roaming");
}

/**
 * chatter-agent のランタイムルート。設定・spool・発話ログ・ロックをすべてこの下に置く。
 * 散らばらせないのは、plugin の bash が辿れる場所を1箇所に絞るため。
 */
export function getRuntimeDir(e: PathEnv = currentPathEnv()): string {
  const base = e.platform === "win32" ? appData(e) : xdgConfigHome(e);
  return path.join(base, "chatter-agent");
}

/** 設定ファイル。config より先に必要なので環境変数の解決もここで行う */
export function getConfigFilePath(e: PathEnv = currentPathEnv()): string {
  return e.env.CHATTER_AGENT_CONFIG || path.join(getRuntimeDir(e), "config.json");
}

/** hook が payload を落とす場所。ワーカーが処理し終えたら削除する */
export function getSpoolDir(e: PathEnv = currentPathEnv()): string {
  return path.join(getRuntimeDir(e), "spool");
}

/**
 * 発話の**記録**。1文1行で、消さずに残す。
 *
 * ★ 配信はここを読まない（→ `getSpeechQueueDir`）。誰も tail しないので、
 *   ローテートの正しさが要求されない。
 */
export function getSpeechLogPath(e: PathEnv = currentPathEnv()): string {
  return path.join(getRuntimeDir(e), "speech.jsonl");
}

/** 記録の退避先。`speech.jsonl` → `speech.1.jsonl`。1世代だけ持つ */
export function getSpeechLogBackupPath(basePath: string): string {
  const dir = path.dirname(basePath);
  const ext = path.extname(basePath);
  const stem = path.basename(basePath, ext);
  return path.join(dir, `${stem}.1${ext}`);
}

/**
 * 発話の**配信キュー**。1文1ファイルで、ファイル名が `seq`。
 *
 * CLI が書き、server が読んで配信し、クライアントの ack で消える。
 * 記録と分けてあるのは、配信側は消えてよく、記録側は残したいため。
 */
export function getSpeechQueueDir(e: PathEnv = currentPathEnv()): string {
  return path.join(getRuntimeDir(e), "speech");
}

/**
 * 次に採番する seq を持つ。
 * seq を行番号から導かないのは、ローテートを跨いで連番を維持するため（設計書 §6）。
 */
export function getSpeechStatePath(e: PathEnv = currentPathEnv()): string {
  return path.join(getRuntimeDir(e), "speech.state.json");
}

/** ワーカー側の状態（要約セッションの除外、応答待ちの prompt_id 重複抑制） */
export function getWorkerStatePath(e: PathEnv = currentPathEnv()): string {
  return path.join(getRuntimeDir(e), "speak.state.json");
}

/**
 * 要約 CLI に渡した `--session-id` の共有レジストリ（→ `core/summarizerSessions.ts`）。
 *
 * ★ **書き手は `chatter-agent-server` だけ、読み手は `chatter-agent-speak` だけ。**
 *   CLI 自身の分は `worker.state.json` に入る（書き手を1人に保つための分割）。
 */
export function getSummarizerSessionsPath(e: PathEnv = currentPathEnv()): string {
  return path.join(getRuntimeDir(e), "summarizer-sessions.json");
}

/**
 * 単一ワーカーのロック。**ディレクトリ**として作る（mkdir が原子的なため）。
 * CLI に npm 依存を持たせられないので、ロックライブラリは使わない。
 */
export function getLockDir(e: PathEnv = currentPathEnv()): string {
  return path.join(getRuntimeDir(e), "speak.lock");
}

/**
 * サーバーの単一インスタンスロック。`getLockDir` と同じ書き方（mkdir が原子的なディレクトリ）。
 *
 * 配信キュー（`getSpeechQueueDir`）のパスにはポートもインスタンス識別子も入っていないので、
 * 2台目のサーバーが別ポートで bind に成功すると、起動時の掃除が1台目の未配信キューを消し、
 * 定常状態でも両者が独立に配信して二重再生になる。キューを所有できるサーバーは1台に絞る。
 */
export function getServerLockDir(e: PathEnv = currentPathEnv()): string {
  return path.join(getRuntimeDir(e), "server.lock");
}

/**
 * 発話クライアント（player）の単一インスタンスロック。書き方は `getLockDir` と同じ。
 *
 * ★ 「二重に鳴ってうるさい」ではなく、**2台目が1台目のキューを破壊するから**取る。
 *   ack は累積で、server の `speechQueue.ackUpTo` は `seq <= upTo` を**ファイル名で範囲削除**する。
 *   `dispatcher.ack` のクランプは値を配信済みの範囲に押さえるだけで、削除対象を
 *   「その ack を送ってきたクライアントが受け取った分」には限定しない。
 *   速い player の ack が、遅い player のまだ喋っていない entry を消す。
 *
 * このロックはランタイムルート単位なので、`XDG_CONFIG_HOME` を分けた2台や
 * リモートの server に繋ぐ2台は防げない（→ docs/protocol.md）。
 */
export function getPlayerLockDir(e: PathEnv = currentPathEnv()): string {
  return path.join(getRuntimeDir(e), "player.lock");
}

/**
 * player が合成した WAV を置く場所。再生し終えたら消す。
 *
 * 固定ディレクトリにしてあるのは、`getPlayerLockDir` で1台に絞ってあるため
 * **起動時に丸ごと消して作り直せる**から。`os.tmpdir()` + mkdtemp にすると
 * SIGKILL でゴミが残り、prefix 走査と pid の生死判定が要る。
 */
export function getPlayerTmpDir(e: PathEnv = currentPathEnv()): string {
  return path.join(getRuntimeDir(e), "player-tmp");
}

/**
 * 要約 CLI の cwd。
 *
 * プロジェクトのディレクトリで走らせるとその `CLAUDE.md` が読み込まれてコンテキストが膨らむ
 * （要約に不要なコストと遅延）。`-p`（print モード）では workspace trust ダイアログが skip
 * されるので、見知らぬディレクトリで起動しても止まらない。
 */
export function getSummarizerHomeDir(e: PathEnv = currentPathEnv()): string {
  return path.join(getRuntimeDir(e), "summarizer-home");
}

/**
 * 要約の所要時間を実測するための追記ログ。
 *
 * hook 経路では `console.warn` が `/dev/null` に消えるので、実測の窓がここしかない。
 * **要約が有効なときだけ書かれる**ので、既定 OFF のままなら1バイトも増えない。
 */
export function getSummarizerLogPath(e: PathEnv = currentPathEnv()): string {
  return path.join(getRuntimeDir(e), "summarizer.log");
}
