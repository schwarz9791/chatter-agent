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

/** 発話ログの現世代。全体の契約（設計書 §5） */
export function getSpeechLogPath(e: PathEnv = currentPathEnv()): string {
  return path.join(getRuntimeDir(e), "speech.jsonl");
}

/**
 * ローテート後の世代パス。`speech.jsonl` → `speech.1.jsonl`。
 * 世代番号は 1 始まり（0 は現世代 = `basePath` そのもの）。
 */
export function getSpeechLogGenerationPath(basePath: string, generation: number): string {
  if (generation <= 0) return basePath;
  const dir = path.dirname(basePath);
  const ext = path.extname(basePath);
  const stem = path.basename(basePath, ext);
  return path.join(dir, `${stem}.${generation}${ext}`);
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
 * 単一ワーカーのロック。**ディレクトリ**として作る（mkdir が原子的なため）。
 * CLI に npm 依存を持たせられないので、ロックライブラリは使わない。
 */
export function getLockDir(e: PathEnv = currentPathEnv()): string {
  return path.join(getRuntimeDir(e), "speak.lock");
}
