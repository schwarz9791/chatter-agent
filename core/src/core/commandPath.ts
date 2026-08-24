/**
 * 外部コマンドの絶対パスを探す。**spawn せずに** `fs.existsSync` だけで判定する。
 *
 * ★ 元は `summarizer/claudeCli.ts` にあった（要約 CLI 専用の探索として書いた）。
 *   [#51] で `server/engineProcess.ts` が合成エンジンの実行パスを解決するのにも要るようになり、
 *   「要約 CLI のファイルからエンジンのパス解決を借りる」形を避けてここへ出した。
 *   **ロジックは移動時に変えていない。**
 *
 * ★ 移植元（cc-mascot の `detect.ts`）は Finder/Dock 起動の Electron アプリ向けに、
 *   ログインシェル PATH の解決（`zsh -ilc`、最大5秒）と `--version` の spawn を検出のたびに
 *   行っていた。ここでは持ち込まない。**ただし PATH が痩せる問題自体は無くなっていない。**
 *   本リポジトリは mise で Node を固定していて、shim が PATH に載るのは対話 rc 経由のみ ——
 *   Finder / Dock から起動した Claude Code はその PATH を継承しない（`plugin/scripts/_lib.sh`
 *   の `chatter_spawn_cli` が同じ前提でログを残す。`core/scripts/verify-phase-a.sh` の ⑫ は
 *   この状態を意図的に再現している）。ログインシェルを起動して補う（`zsh -ilc`）方針は
 *   引き続き持ち込まない — **毎 delta 起動されるプロセスの中で、要約のたびにログインシェルを
 *   立ち上げるコストが見合わないため。**（★ かつてここには「hook の10秒制約に乗せられない」と
 *   書いてあったが誤り。この関数が走るのは hook からデタッチ起動された `chatter-agent-speak`
 *   の中で、hook 自身は spool に1ファイル置いて即 `exit 0` する（`_lib.sh` の
 *   `chatter_spawn_cli` は `nohup ... &`）。同じ経路で既に `execFileSync` を既定で60秒
 *   ブロックしうるので、10秒制約はここには掛かっていない。）代わりに、PATH に
 *   見つからなかったときの保険として mise/asdf/nvm/volta 等の**既知のインストール先**を
 *   `fs.existsSync` だけで（spawn せずに）順に見る軽量な同期探索に絞る。
 *
 * ★ **その保険を使うかは呼び出し側が選ぶ**（`searchKnownBinDirs`）。既知のインストール先は
 *   「バージョンマネージャが入れた CLI」を想定した並びなので、探すコマンド名によっては
 *   まったく別のバイナリを掴む。合成エンジン（`server/engineProcess.ts`）がその実例で、
 *   バイナリ名が literally `run` なため mise / asdf の shim と衝突しうる（→ PR #52 のレビュー）。
 *
 * [#51]: https://github.com/schwarz9791/chatter-agent/issues/51
 */

import * as fs from "fs";
import * as os from "os";
import * as path from "path";

export interface FindCommandPathOptions {
  /** テスト用。既定 `process.env` */
  env?: NodeJS.ProcessEnv;
  /** テスト用。既定 `os.homedir()` */
  homeDir?: string;
  /**
   * PATH で見つからなかったとき、既知のインストール先（`getKnownBinDirs`）も探すか。既定 `true`。
   *
   * ★ **コマンド名が一般的なときは `false` にすること。** あの並びは「バージョンマネージャが
   *   入れた CLI」を拾うためのもので、`run` のようなありふれた名前だと mise / asdf の shim や
   *   `~/.local/bin/run` を掴む。要約 CLI（`claude`）は名前が固有で、かつ Finder / Dock 起動で
   *   PATH が痩せる前提があるので `true` のまま使う。
   */
  searchKnownBinDirs?: boolean;
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
 * - 展開後に絶対パスならそのまま使う。ユーザーが `aiSummaryCommand` / `ttsSpawnCommand` に
 *   明示した値を信頼し、存在確認はしない（間違っていれば実行側が ENOENT を返すだけで、
 *   `no-command` と `error` を厳密に分けることに実利が無い）
 * - そうでなければ `PATH` の各ディレクトリ → 既知の bin ディレクトリ（`searchKnownBinDirs`
 *   が false ならこちらは見ない）の順に探す。
 *   ファイルが存在するだけでなく**実行ビット**（`X_OK`）も見る。0644 の同名ファイル
 *   （インストールの残骸や補完スタブ）が後続の正しい候補を隠さないようにするため
 * - 見つからなければ `undefined`。呼び出し側（`summaryPipeline` は原文へフォールバック、
 *   `engineProcess` は spawn を諦めて 503 運用に落ちる）が決める
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
  if (opts.searchKnownBinDirs ?? true) {
    for (const d of getKnownBinDirs(homeDir)) push(d);
  }

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
