/**
 * 要約 CLI（`claude`）のコマンド解決・引数組み立て・実行。
 *
 * ★ 移植元（cc-mascot の `detect.ts`）は Finder/Dock 起動の Electron アプリ向けに、
 *   ログインシェル PATH の解決（`zsh -ilc`、最大5秒）と `--version` の spawn を検出のたびに
 *   行っていた。ここでは持ち込まない。**ただし PATH が痩せる問題自体は無くなっていない。**
 *   本リポジトリは mise で Node を固定していて、shim が PATH に載るのは対話 rc 経由のみ ——
 *   Finder / Dock から起動した Claude Code はその PATH を継承しない（`plugin/scripts/_lib.sh`
 *   の `chatter_spawn_cli` が同じ前提でログを残す。`core/scripts/verify-phase-a.sh` の ⑫ は
 *   この状態を意図的に再現している）。ログインシェルを起動して補う（`zsh -ilc`）方針は
 *   引き続き持ち込まない — 重く、hook の10秒制約に乗せられないため。代わりに、PATH に
 *   見つからなかったときの保険として mise/asdf/nvm/volta 等の**既知のインストール先**を
 *   `fs.existsSync` だけで（spawn せずに）順に見る軽量な同期探索に絞る。
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
    // mise の shim ディレクトリ。本リポジトリが Node を固定しているのがこれで、
    // 対話 rc を経由しない起動（Finder / Dock）では PATH に載らない（→ 上のヘッダ）
    path.join(homeDir, ".local", "share", "mise", "shims"),
    // asdf も同じ理由（shim 方式のバージョンマネージャ）
    path.join(homeDir, ".asdf", "shims"),
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
  // ★ fnm は意図的に対象外。shim を持たず、`fnm env` がシェルごとの一時ディレクトリ
  //   （`fnm_multishells/<pid>_<timestamp>/bin`）を PATH に挿す方式なので、**プロセスの外から
  //   当てられる固定の場所が無い**。インストール先（`FNM_DIR`、既定は XDG か macOS の
  //   Application Support）もバージョン配置（`node-versions/<v>/installation/bin`）も環境で
  //   変わるため、推測でパスを並べても外れる。PATH に載っていれば上のループが拾う
  return dirs;
}

/**
 * コマンドの絶対パスを探す。`fs.existsSync` だけで判定し、**spawn しない**
 * （移植元の `--version` 疎通確認は持ち込まない。上のヘッダ参照）。
 *
 * - `~/` で始まるなら `os.homedir()` に展開する。`aiSummaryCommand: "~/.local/bin/claude"` は
 *   `parseNonEmptyString`（config.ts）がそのまま受理するが、展開しないと下の絶対パス判定に
 *   当たらず、PATH の各ディレクトリと結合されて絶対に見つからないパスになる
 *   （`~user/` のような他ユーザーのホーム形式は対応不要）
 * - 展開後に絶対パスならそのまま使う。ユーザーが `aiSummaryCommand` に明示した値を信頼し、
 *   存在確認はしない（間違っていれば `runClaudeCli` が ENOENT で `error` を返すだけで、
 *   `no-command` と `error` を厳密に分けることに実利が無い）
 * - そうでなければ `PATH` の各ディレクトリ → 既知の bin ディレクトリの順に探す。
 *   ファイルが存在するだけでなく**実行ビット**（`X_OK`）も見る。0644 の同名ファイル
 *   （インストールの残骸や補完スタブ）が後続の正しい候補を隠さないようにするため
 * - 見つからなければ `undefined`。呼び出し側（`summaryPipeline`）が原文にフォールバックする
 */
export function findCommandPath(command: string, opts: FindCommandPathOptions = {}): string | undefined {
  const homeDir = opts.homeDir ?? os.homedir();
  const expanded = command.startsWith("~/") ? path.join(homeDir, command.slice("~/".length)) : command;

  if (path.isAbsolute(expanded)) return expanded;

  const env = opts.env ?? process.env;

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
    const fullPath = path.join(dir, expanded);
    try {
      if (fs.existsSync(fullPath) && fs.statSync(fullPath).isFile()) {
        // 実行ビットが無ければこの候補を諦め、次の候補の探索を続ける（return しない）。
        // execFileSync は EACCES で throw し、それをそのまま「error」と記録すると、
        // 同じ PATH で `which` が本物を見つけられる状況でも原因に辿り着けない
        fs.accessSync(fullPath, fs.constants.X_OK);
        return fullPath;
      }
    } catch {
      // stat/access が権限エラーや実行ビット無しで落ちても、他の候補の探索は続ける
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
    // プロンプトインジェクション対策: 要約対象のテキストは信頼できない引用（取得した Web
    // ページ、issue 本文など）を含みうる。そこに仕込まれた指示で秘密を読ませ、それが
    // stdout（＝要約結果。そのまま読み上げられ speech.jsonl に永続化され全 WebSocket
    // クライアントに配信される）に出力されると音になって漏洩する。
    // ★ `--allowedTools ""` は使わない。`<tools...>` は可変長引数なので空文字1個が渡るだけで
    //   「全ツール禁止」にはならない。deny 側が allow に優先する仕様に頼るほうが確実
    // ★ `--setting-sources` を渡さない設計（上のヘッダ参照）なのでユーザーの settings.json の
    //   allow ルールは載る。だからここで明示的に塞ぐ必要がある
    // ★ 現行名 Agent と旧名 Task を両方書いてある。どちらか一方の名前でしか弾けない
    //   バージョン差を踏まないための保険。BashOutput / KillShell も同じ理由（Bash の従属ツール）
    // - Read / Glob / Grep: 要約への入力は stdin だけで足りる。ファイルシステムを読ませる
    //   必要が無く、読ませた内容がそのまま出力に混ざりうる
    // - SlashCommand: プロジェクトのスラッシュコマンドは自前の `allowed-tools: Bash(...)` を
    //   持てるので、Bash を止めても SlashCommand 経由でそれを迂回できる
    "--disallowedTools",
    "Agent,Task,Bash,BashOutput,KillShell,Edit,Write,NotebookEdit,WebFetch,WebSearch,Read,Glob,Grep,SlashCommand",
  ];
  if (opts.model) args.push("--model", opts.model);
  return args;
}

/**
 * 子（要約 CLI）に渡さない環境変数の denylist。**完全一致のみ**（プレフィックス一括除去はしない）。
 *
 * ★ denylist を選んだ理由: allowlist にすると、こちらが知らない認証構成
 *   （`settings.json` の `apiKeyHelper`、独自の `ANTHROPIC_*` 派生変数など）を巻き添えにして
 *   壊しうる。denylist なら、存在しないキーを列挙しても無害 —— 「漏れても壊れないが、
 *   allowlist は知らない構成を壊す」という非対称性がある。
 *
 * ★ **プレフィックス一括除去（`CLAUDE_*` や `CLAUDE_CODE_*`）にしないこと。**
 *   `CLAUDE_CONFIG_DIR`（認証情報の置き場所）、`CLAUDE_CODE_OAUTH_TOKEN`、
 *   `CLAUDE_CODE_USE_BEDROCK` / `USE_VERTEX`、`CLAUDE_CODE_API_KEY_HELPER_TTL_MS` を
 *   巻き込んで認証が壊れる。しかも壊れても原文で発話されるので気付けない。
 *
 * 絶対に落とさないもの（denylist に載せない）: `ANTHROPIC_*` 全部、`CLAUDE_CONFIG_DIR`、
 * `CLAUDE_CODE_OAUTH_TOKEN`、`CLAUDE_CODE_USE_BEDROCK` / `USE_VERTEX` と
 * `AWS_*` / `GOOGLE_*` / `CLOUD_ML_REGION`、`CLAUDE_CODE_API_KEY_HELPER_TTL_MS`、
 * `CLAUDE_CODE_EXECPATH`、`PATH` / `HOME` / プロキシ・証明書系、そして
 * `CHATTER_AGENT_DISABLE=1`（無限ループ防止の第1層。→ `buildSummaryEnv` 末尾）と `CHATTER_AGENT_*`。
 */
const ENV_DENYLIST: readonly string[] = [
  // 無限ループ防止の第2層（--session-id レジストリ）が素通しになる。最重要
  "CLAUDE_CODE_SESSION_ID",
  // 親セッションの IPC 認証トークンとソケットパス
  "CLAUDE_CODE_MESSAGING_TOKEN",
  "CLAUDE_CODE_MESSAGING_SOCKET",
  // 「Claude Code の中から起動された」文脈
  "CLAUDECODE",
  "CLAUDE_CODE_ENTRYPOINT",
  // 親セッションの同一性・親子関係
  "CLAUDE_CODE_BRIDGE_SESSION_ID",
  "CLAUDE_CODE_CHILD_SESSION",
  "CLAUDE_PID",
  // 親の effort 継承は --model haiku の意図（コストと遅延の最小化）を打ち消す
  "CLAUDE_EFFORT",
  // ユーザーのプロジェクトパス。cwd 隔離（プロジェクトの CLAUDE.md を読ませない）と矛盾する
  "CLAUDE_PROJECT_DIR",
  "CLAUDE_PLUGIN_ROOT",
  // IDE 連携ポート。不要な接続を作らせない
  "CLAUDE_CODE_SSE_PORT",
];

/**
 * 要約 CLI に渡す環境変数を組み立てる純粋関数（`execFileSync` を呼ばずに単体テストできるように分離）。
 *
 * 親（`process.env`）をそのまま継承すると、`CLAUDE_CODE_SESSION_ID` 等の親セッションを
 * 指す変数まで子（要約 CLI が起動する Claude Code）に伝播し、そこで発火した hook の
 * payload が親の session_id を名乗る可能性がある。それが無限ループ防止の第2層
 * （`--session-id` レジストリ）を素通しにする。`MESSAGING_SOCKET` / `MESSAGING_TOKEN` は
 * 親の生きたセッションを指すので、`cwd: getSummarizerHomeDir()` による隔離も部分的に無効化する。
 */
export function buildSummaryEnv(parent: NodeJS.ProcessEnv = process.env): NodeJS.ProcessEnv {
  const env = { ...parent };
  for (const key of ENV_DENYLIST) delete env[key];
  // ★ 無限ループ防止の第1層。要約 CLI 自身の Claude Code が MessageDisplay hook を発火させ、
  //   それが spool に積まれ、また要約されて…という再帰を止める唯一の砦。denylist で他のキーを
  //   落としても、これだけは必ず立てる（→ plugin/scripts/_lib.sh の chatter_disabled）
  env.CHATTER_AGENT_DISABLE = "1";
  return env;
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
 * ★ タイムアウトの既定（`aiSummaryTimeoutMs`。→ `core/config.ts`）について:
 *   所要時間は**入力の長さから予測できない**。実機実測10件では相関が見られず、短い入力が
 *   タイムアウトする一方で長い入力が10秒台で返ることがあった。ばらつきの支配要因は AI の
 *   生成時間で、マシン・ネットワーク・モデルでも変わる。**秒数を仕様として扱わないこと**
 *   （CLAUDE.md と同じ立場）。既定を60秒にしたのは「30秒では実測10件中3割がタイムアウトした」
 *   という一点が根拠で、**「実測値の N 倍」という決め方はしていない**（相関しないものに
 *   倍率を掛けても意味が無いため）。
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
      // 無限ループ防止の第1層（CHATTER_AGENT_DISABLE=1）を立てつつ、親セッションの認証・IPC
      // 変数（CLAUDE_CODE_SESSION_ID 等）を子に渡さない denylist フィルタ
      // （→ buildSummaryEnv のコメント。ENV_DENYLIST の一覧と絶対に落とさないものの根拠）
      env: buildSummaryEnv(),
      timeout: deps.timeoutMs,
      killSignal: "SIGKILL",
      maxBuffer: MAX_BUFFER_BYTES,
      stdio: ["pipe", "pipe", "pipe"],
    });
    return { ok: true, stdout: extractSummary(stdout) };
  } catch (err) {
    // execFileSync はタイムアウトでも maxBuffer 超過でも非ゼロ終了でも同じく throw する。
    // ★ `err.signal` の有無では区別できない（かつては「signal が付いていれば timeout」と
    //   判定していたが、これは誤り）。Node 24 実機実測では:
    //   - maxBuffer 超過 → `{ code: "ENOBUFS", status: null, signal: "SIGKILL" }`
    //   - 子プロセスの自死や外部からの kill（hook は `nohup node ... &` で起動し setsid を
    //     使えないため、要約 CLI は親 Claude Code のプロセスグループに残り、ターミナルの
    //     Ctrl-C を受ける。→ plugin/scripts/_lib.sh の chatter_spawn_cli）→ `signal: "SIGTERM"`
    //   どちらも signal が付くので、signal の有無だけでは timeout と誤報しうる。
    //   `err.code === "ETIMEDOUT"` は Node がタイムアウトで子を殺したときだけ立つので、
    //   これで判定する（`status` は ENOENT / ETIMEDOUT / ENOBUFS のいずれでも null になるので使えない）
    const e = err as NodeJS.ErrnoException & { signal?: string | null; stderr?: string | Buffer };
    const stderr =
      typeof e.stderr === "string" ? e.stderr : Buffer.isBuffer(e.stderr) ? e.stderr.toString("utf-8") : "";
    const detail = (stderr.trim() || e.message || String(err)).slice(0, 500);

    if (e.code === "ETIMEDOUT") return { ok: false, reason: "timeout", detail };
    if (e.code === "ENOBUFS") return { ok: false, reason: "overflow", detail };
    return { ok: false, reason: "error", detail };
  }
}
