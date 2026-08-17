/**
 * 要約 CLI（`claude`）のコマンド解決・引数組み立て・実行。
 *
 * ★ 移植元（cc-mascot の `detect.ts`）は Finder/Dock 起動の Electron アプリ向けに、
 *   ログインシェル PATH の解決（`zsh -ilc`、最大5秒）と `--version` の spawn を検出のたびに
 *   行っていた。ここでは持ち込まない。この CLI は Claude Code の hook から起動されるので、
 *   Claude Code 自身のプロセスの PATH をそのまま継承しており、そもそも「PATH が痩せる」問題が
 *   存在しない。加えてこの CLI は **hook から毎 delta 起動される**ので、5秒かかりうるサブシェルは
 *   ドレインのたびに払うには重すぎる。`fs.existsSync` だけで探す軽量な同期探索に絞る。
 */

import { execFileSync } from "child_process";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import type { ClaudeCliResult } from "./types";

export interface FindCommandPathOptions {
  /** テスト用。既定 `process.env` */
  env?: NodeJS.ProcessEnv;
  /** テスト用。既定 `os.homedir()` */
  homeDir?: string;
}

/** CLI がよくインストールされる既知のディレクトリ（PATH に含まれないことがある） */
function getKnownBinDirs(homeDir: string): string[] {
  const dirs = [
    path.join(homeDir, ".local", "bin"),
    "/opt/homebrew/bin",
    "/usr/local/bin",
    path.join(homeDir, ".volta", "bin"),
    path.join(homeDir, "bin"),
  ];
  // nvm のバージョン別 bin ディレクトリ。バージョンごとにディレクトリ名が違うので列挙が要る
  try {
    const nvmVersionsDir = path.join(homeDir, ".nvm", "versions", "node");
    if (fs.existsSync(nvmVersionsDir)) {
      for (const version of fs.readdirSync(nvmVersionsDir)) {
        dirs.push(path.join(nvmVersionsDir, version, "bin"));
      }
    }
  } catch {
    // 読めなくても致命ではない。他の候補ディレクトリの探索を続ける
  }
  return dirs;
}

/**
 * コマンドの絶対パスを探す。`fs.existsSync` だけで判定し、**spawn しない**
 * （移植元の `--version` 疎通確認は持ち込まない。上のヘッダ参照）。
 *
 * - 絶対パスならそのまま使う。ユーザーが `aiSummaryCommand` に明示した値を信頼し、
 *   存在確認はしない（間違っていれば `runClaudeCli` が ENOENT で `error` を返すだけで、
 *   `no-command` と `error` を厳密に分けることに実利が無い）
 * - そうでなければ `PATH` の各ディレクトリ → 既知の bin ディレクトリの順に探す
 * - 見つからなければ `undefined`。呼び出し側（`summaryPipeline`）が原文にフォールバックする
 */
export function findCommandPath(command: string, opts: FindCommandPathOptions = {}): string | undefined {
  if (path.isAbsolute(command)) return command;

  const env = opts.env ?? process.env;
  const homeDir = opts.homeDir ?? os.homedir();

  const dirs: string[] = [];
  const seen = new Set<string>();
  const push = (d: string) => {
    if (d && !seen.has(d)) {
      seen.add(d);
      dirs.push(d);
    }
  };
  for (const d of (env.PATH || "").split(path.delimiter)) push(d);
  for (const d of getKnownBinDirs(homeDir)) push(d);

  for (const dir of dirs) {
    const fullPath = path.join(dir, command);
    try {
      if (fs.existsSync(fullPath) && fs.statSync(fullPath).isFile()) return fullPath;
    } catch {
      // stat が権限エラー等で落ちても、他の候補の探索は続ける
    }
  }
  return undefined;
}

export interface BuildSummaryArgsOptions {
  sessionId: string;
  /** 空文字なら `--model` を渡さない（CLI 自身の既定モデルに従う） */
  model: string;
}

/**
 * 要約 CLI の引数を組み立てる純粋関数（`execFileSync` を呼ばずに単体テストできるように分離）。
 *
 * 実機での実測（2026-08-17）を根拠に組んである:
 *
 * - `--session-id` と `--no-session-persistence` は併用できる（exit 0 を確認済み）。付けると
 *   `~/.claude/projects/<cwd のエンコード名>/` に jsonl が残らない（`memory` ディレクトリだけができる）。
 *   要約は一度きりで再開しないので、セッションログを残す意味が無い。★ 移植元の cc-mascot 自身が
 *   これを付け忘れていて、要約セッションの jsonl が 166 件溜まっているのを実測で確認した
 * - `--strict-mcp-config` は `--mcp-config` を渡さなくても単体で使える（exit 0 を確認済み）。
 *   ユーザーの MCP サーバーを起動させない（要約に不要で、起動の分だけ遅くなる）
 * - `--setting-sources ""` は**使わない**。settings.json 由来の stderr 警告
 *   （`Permission allow rule ... is not matched by ...`）は消えるが、実測で速くならない
 *   （10.7秒 → 16.8秒。API のレイテンシが支配的で、設定読み込みは誤差以下）。かつ、
 *   ユーザーが `settings.json` の `apiKeyHelper` / `env.ANTHROPIC_*` で認証している環境を壊す。
 *   無限ループ防止は第1層（`CHATTER_AGENT_DISABLE=1`）と第2層（`--session-id` レジストリ）で
 *   足りているので、設定ソースを切ってまで hooks を読ませない理由が無い
 * - `--bare` は選ばない。hooks を skip できるが `ANTHROPIC_API_KEY` が必須で、OAuth ログイン
 *   運用（実測環境がそう）では使えない
 */
export function buildSummaryArgs(instruction: string, opts: BuildSummaryArgsOptions): string[] {
  const args = [
    "-p",
    instruction,
    // 無限ループ防止の第2層。自前生成のセッションIDでログファイル名を確定させ、呼び出し側の
    // registerSessionId でレジストリに登録してから実行する（→ types.ts の Summarize）
    "--session-id",
    opts.sessionId,
    // セッションの再開が要らないなら jsonl 自体を残さない（上のヘッダの実測根拠を参照）
    "--no-session-persistence",
    // ユーザーの MCP サーバーを起動させない（要約に不要な起動コストを避ける）
    "--strict-mcp-config",
    // プロンプトインジェクション対策: 要約対象のテキストに指示や命令が混ざっていても
    // 副作用ツールを使わせない。★ 現行名 Agent と旧名 Task を両方書いてある。
    // どちらか一方の名前でしか弾けないバージョン差を踏まないための保険
    "--disallowedTools",
    "Agent,Task,Bash,Edit,Write,NotebookEdit,WebFetch,WebSearch",
  ];
  if (opts.model) args.push("--model", opts.model);
  return args;
}

export interface RunClaudeCliDeps {
  commandPath: string;
  args: string[];
  /** 原文。stdin で渡す */
  text: string;
  /**
   * CLI の cwd（隔離ディレクトリ）。プロジェクトの `CLAUDE.md` を読み込ませない
   * （要約に不要なコンテキストと遅延）ための隔離で、呼び出し側が `getSummarizerHomeDir()` を渡す。
   * 無ければここで作る。作れなくても致命ではない（`execFileSync` が ENOENT 等で失敗し、
   * 呼び出し側 `summaryPipeline` が原文にフォールバックする）
   */
  homeDir: string;
  timeoutMs: number;
}

/**
 * `stdout` の全体を要約文とみなす（`.trim()` するだけ）。
 *
 * ★ 実機実測（2026-08-17）: stdout / stderr を分けて確認したところ、`settings.json` に関する
 *   警告（`Permission allow rule (...) is not matched by ...`）は**すべて stderr**に出て、
 *   `stdout` には要約文だけ（227バイト、前置きも改行ノイズも無し）だった。移植元
 *   （cc-mascot の `claudeBackend.extractOutput`）と同じ判断で問題ない。
 *   ★ ただし将来 CLI が stdout に診断や前置きを混ぜるようになったら、その瞬間に
 *   その文言がそのまま読み上げに乗る場所である点は変わらない。
 */
function extractSummary(stdout: string): string {
  return stdout.trim();
}

/**
 * stdout/stderr を合わせて許す上限。要約文自体は 120 文字程度で収まるが、CLI が失敗したときの
 * スタックトレースや警告の集積を打ち切るための保険として、Node の `execFileSync` の既定値
 * （1MiB）をそのまま使う。小さくしすぎると「エラーの詳細が読めない」失敗が増え、
 * 大きくしすぎる意味は無い（毎 delta 起動のプロセス1個がここまで貯め込むことは実運用で無い）。
 */
const MAX_BUFFER_BYTES = 1024 * 1024;

/**
 * 要約 CLI を実行する。
 *
 * ★ タイムアウトの既定 30 秒（`aiSummaryTimeoutMs` の既定値。→ `core/config.ts`）の根拠:
 *   実機実測（2026-08-17、同一マシン・`--model haiku`、227文字 → 96文字）で要約1回が
 *   **10.7〜16.8秒**、うち CLI の起動オーバーヘッド（短いプロンプト・短い出力でもかかる分）が
 *   **約5.2秒**。残りが API 呼び出しと生成。30秒は「実測で遅い方の2倍弱」で妥当だが、
 *   マシンとネットワークで変わるので**秒数を仕様として扱わないこと**（CLAUDE.md と同じ立場）。
 */
export function runClaudeCli(deps: RunClaudeCliDeps): ClaudeCliResult {
  try {
    fs.mkdirSync(deps.homeDir, { recursive: true });
  } catch {
    // 作れなくても致命ではない。この後の execFileSync が ENOENT 等で失敗し、
    // 呼び出し側（summaryPipeline）が原文にフォールバックする
  }

  try {
    const stdout = execFileSync(deps.commandPath, deps.args, {
      // 原文は stdin で渡す。引数で渡さない理由: ARG_MAX の上限に当たりうることと、
      // `ps`/プロセス一覧にユーザーの発話内容がそのまま載ってしまうこと（漏洩経路になる）
      input: deps.text,
      encoding: "utf-8",
      cwd: deps.homeDir,
      // ★ 無限ループ防止の第1層。要約 CLI 自身の Claude Code が MessageDisplay hook を発火させ、
      //   それが spool に積まれ、また要約されて…という再帰を止める唯一の砦。
      //   環境変数は子プロセスの Claude Code とそのフックまで伝播する（→ CLAUDE.md、
      //   plugin/scripts/_lib.sh の chatter_disabled）。**これを外さないこと**
      env: { ...process.env, CHATTER_AGENT_DISABLE: "1" },
      timeout: deps.timeoutMs,
      killSignal: "SIGKILL",
      maxBuffer: MAX_BUFFER_BYTES,
      stdio: ["pipe", "pipe", "pipe"],
    });
    return { ok: true, stdout: extractSummary(stdout) };
  } catch (err) {
    // execFileSync はタイムアウトでも非ゼロ終了でも同じく throw する。
    // タイムアウト到達時は Node が killSignal（上で SIGKILL に固定）を送って子を殺すので、
    // `err.signal` が付いているかどうかで区別できる。CLI 自身が SIGKILL で死ぬことは無いので、
    // signal が付いていれば「Node が timeout で殺した」と判定してよい
    // （非ゼロ終了だけの通常の失敗では signal は null）
    const e = err as NodeJS.ErrnoException & { signal?: string | null; stderr?: string | Buffer };
    if (e.signal) return { ok: false, reason: "timeout" };

    const stderr =
      typeof e.stderr === "string" ? e.stderr : Buffer.isBuffer(e.stderr) ? e.stderr.toString("utf-8") : "";
    const detail = stderr.trim() || e.message || String(err);
    return { ok: false, reason: "error", detail: detail.slice(0, 500) };
  }
}
