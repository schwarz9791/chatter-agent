/**
 * 合成エンジン（AivisSpeech-Engine の `run`）を起こして、サーバーと道連れで落とす（[#51]）。
 *
 * **`server/index.ts` は配線と終了処理しか置かない**方針なので、判断はここに出す
 * （`dispatcher.ts` / `audioStore.ts` と同じ理由 → docs/core.md）。`index.ts` には
 * テストが無いため、直書きすると条件の網羅を確かめる手段が無くなる。
 *
 * ★ **起こすだけで、起動を待たない。** 合成は今までどおり `GET /audio/…` が来たときに走り、
 *   間に合わなければ `503`。テキストの WebSocket 配信は一切ブロックしない
 *   （→ CLAUDE.md「絶対に守ること」7）。
 *
 * ★ **エンジンを他と共有しない前提で書いてある。** 「サーバーが落ちたらエンジンも落ちる」で
 *   構わないのは、このエンジンを chatter-agent 以外が使わないため。GUI（AivisSpeech.app）が
 *   既に上げている場合は**そもそも起こさない**（呼び出し側の疎通確認で弾く）ので、
 *   他人が起こしたエンジンを殺すことはない。
 *
 * ★ **POSIX 前提。** `process.kill(-pid, …)` によるプロセスグループへの送信は Windows に無い。
 *   対応プラットフォームは macOS / Linux（CI は ubuntu）。
 *
 * [#51]: https://github.com/schwarz9791/chatter-agent/issues/51
 */

import { spawn as nodeSpawn } from "child_process";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { findCommandPath } from "../core/commandPath";

/** 起こせると判断した結果。そのまま `startEngine` に渡す */
export interface EngineSpawnPlan {
  command: string;
  args: string[];
  /**
   * `ttsSpawnCommand` に**非絶対パス**が書かれていて、名前から解決したときだけ入る（元の値）。
   *
   * ★ **解決したことを呼び出し側に名指しさせるためのもの。** PATH には `~/.local/bin` や
   *   mise/asdf の shims が普通に載っている（実測で 7/7）ので、`run` のようなありふれた名前だと
   *   まったく別のバイナリに当たりうる。禁止はしない —— `ttsSpawnCommand: "docker"` で
   *   コンテナのエンジンを起こす運用が潰れるため —— 代わりに**黙って読み替えない**
   *   （→ PR #52 のレビュー）。
   */
  resolvedFrom?: string;
}

/**
 * 起こさないと判断した結果。**理由を持たせること。**
 * 呼び出し側（`index.ts`）が「どこを探したか」を組み立て直さずにログへ出せる形にしておく
 * ——そうしないと既知候補の定義が2箇所に分裂する。
 */
export type EngineSpawnSkip =
  | { skip: "not-loopback"; host: string }
  | { skip: "not-http"; protocol: string }
  | { skip: "not-found"; tried: string[] };

export type EngineSpawnResolution = EngineSpawnPlan | EngineSpawnSkip;

/**
 * ループバックのホスト名。**`[::1]` は角括弧付きで持つこと。**
 * `new URL("http://[::1]:10101").hostname` が返すのは `"[::1]"` であって `"::1"` ではない
 * （Node 24 実測）。`"::1"` と書くと IPv6 のループバックが `not-loopback` に落ちて起こさない。
 */
const LOOPBACK_HOSTNAMES = new Set(["localhost", "[::1]"]);

/**
 * `127.0.0.0/8` 全域を受ける。`127.0.0.2` も RFC 1122 上まぎれもなくループバックなので、
 * `127.0.0.1` だけに絞ると `not-loopback` という skip 理由が嘘になる。
 * /8 の外へは届かないので、広く取ることによる危険は無い。
 */
const IPV4_LOOPBACK = /^127\.\d{1,3}\.\d{1,3}\.\d{1,3}$/;

/** `http:` の既定ポート。`ttsBaseUrl` はポートを省略できる（`makeUrlParser` が通す） */
const DEFAULT_HTTP_PORT = "80";

function isLoopback(hostname: string): boolean {
  return LOOPBACK_HOSTNAMES.has(hostname) || IPV4_LOOPBACK.test(hostname);
}

/**
 * AivisSpeech.app に同梱されたエンジンの既知の場所。
 *
 * ★ `getKnownBinDirs`（`core/commandPath.ts`）は流用しない。あれは Node のバージョン
 *   マネージャ向けの bin ディレクトリ列挙で、`.app` バンドル内のバイナリには当たらない。
 */
export function knownEnginePaths(homeDir: string): string[] {
  const suffix = path.join("AivisSpeech.app", "Contents", "Resources", "AivisSpeech-Engine", "run");
  return [path.join("/Applications", suffix), path.join(homeDir, "Applications", suffix)];
}

export interface ResolveEngineSpawnDeps {
  /** `ttsBaseUrl`。`makeUrlParser` を通っているので必ず妥当な絶対 URL */
  baseUrl: string;
  /** `ttsSpawnCommand`。空文字なら既知候補にフォールバックする */
  command: string;
  /** `ttsSpawnArgs`。空配列なら `baseUrl` から組む */
  args: readonly string[];
  /** テスト用。既定 `fs.existsSync` */
  exists?: (filePath: string) => boolean;
  /** テスト用。既定 `os.homedir()` */
  homeDir?: string;
  /**
   * テスト用。既定 `process.env`。
   *
   * ★ **これが無いと明示 `ttsSpawnCommand` の分岐だけ実環境の PATH に依存する。**
   *   `exists` は既知候補の分岐にしか効かず、`findCommandPath` は実 `process.env.PATH` を読む
   *   ——「純関数」と書いてあるのにそこだけシームが空いていた（→ PR #52 のレビュー）。
   */
  env?: NodeJS.ProcessEnv;
}

/**
 * 起こすべきコマンドと引数を決める**純関数**（fs の参照は `exists` で差し替えられる）。
 *
 * 条件4（ループバック限定）と条件5（コマンドが解決できた）をここで見る。
 * 条件1〜3（`ttsEnabled` / `ttsSpawn` / 起動時の疎通）は config と実際の通信に依存するので
 * 呼び出し側（`index.ts`）が持つ。
 */
export function resolveEngineSpawn(deps: ResolveEngineSpawnDeps): EngineSpawnResolution {
  const exists = deps.exists ?? fs.existsSync;
  const homeDir = deps.homeDir ?? os.homedir();
  const env = deps.env ?? process.env;

  const url = new URL(deps.baseUrl);
  // ★ リモートのエンジンは起こせない。ここで返すので、この先 fs には一切触らない
  if (!isLoopback(url.hostname)) return { skip: "not-loopback", host: url.hostname };
  // ★ `makeUrlParser` は `https:` も通すが、起こせるのは平文のエンジンだけ。
  //   受理すると `--port 443` で平文サーバーが立ち（非 root なら bind に失敗し）、
  //   ユーザーは「起動したことすら知らないプロセス」の stderr を読む羽目になる
  if (url.protocol !== "http:") return { skip: "not-http", protocol: url.protocol };

  if (deps.command) {
    // ★ 明示指定が見つからなくても既知候補へ**フォールバックしない**。
    //   指定を黙って別のバイナリに読み替えるのは、最も気づきにくい失敗の仕方になる
    const resolved = findCommandPath(deps.command, { homeDir, env });
    if (resolved === undefined) return { skip: "not-found", tried: searchedPaths(deps.command, env) };
    // ★ 名前から引いたなら、それを呼び出し側に伝える（上の `resolvedFrom`）。
    //   `path.isAbsolute` で見るのは**展開後**の値 —— `~/bin/run` は絶対パスに展開される
    const namedLookup = resolved !== deps.command && !path.isAbsolute(deps.command) && !deps.command.startsWith("~/");
    return {
      command: resolved,
      args: buildArgs(deps.args, url),
      ...(namedLookup ? { resolvedFrom: deps.command } : {}),
    };
  }

  const candidates = knownEnginePaths(homeDir);
  const found = candidates.find((candidate) => exists(candidate));
  if (found === undefined) return { skip: "not-found", tried: candidates };
  return { command: found, args: buildArgs(deps.args, url) };
}

/**
 * `not-found` のときに「どこを探したか」を返す。
 *
 * ★ **生のコマンド名だけを返さないこと。** `合成エンジンが見つかりません: aivis-run` だけでは、
 *   PATH を直すのか・ファイル名を直すのか・実行ビットを立てるのか判断できない。
 *   既知候補の経路がフルパスを全部載せているのに対して非対称でもある（→ PR #52 のレビュー）。
 */
function searchedPaths(command: string, env: NodeJS.ProcessEnv): string[] {
  // 絶対パスは `findCommandPath` がそのまま返すので、ここには来ない（= 非絶対パスだけ）
  const dirs = (env.PATH || "").split(path.delimiter).filter(Boolean);
  if (dirs.length === 0) return [command];
  return dirs.map((dir) => path.join(dir, command));
}

function buildArgs(args: readonly string[], url: URL): string[] {
  // ★ 指定があるなら**置換**（`--host` / `--port` を足さない）。「足りない分だけ補う」形は
  //   賢すぎて挙動が読めない。上書きするなら全部自分で書いてもらう
  if (args.length > 0) return [...args];

  // ★ 角括弧を必ず外す。`hostname` は IPv6 を `[::1]` で返すが、uvicorn 系の `--host` は
  //   角括弧なしを期待し、`[::1]` を渡すと bind に失敗する
  const host = url.hostname.replace(/^\[|\]$/g, "");
  // ★ ポート省略（`http://127.0.0.1`）だと `url.port` は空文字。そのまま渡すと壊れるので
  //   `http:` の既定に落とす（ここに来る時点でスキームは `http:` に絞ってある）。
  //   80 は非 root で bind できないが、その失敗は exit コードと stderr の末尾としてログに出る
  //   （下の `startEngine`）ので、原因が症状に出る
  const port = url.port || DEFAULT_HTTP_PORT;
  return ["--host", host, "--port", port];
}

/** ログの主語。エンジンのプロセスそのものの行は `[Engine]`、判断の行は `[Server]`（docs/core.md） */
const LOG_PREFIX = "[Server]";
/** 候補を1行1件で出すときのインデント。話者候補（`index.ts` の `SPEAKER_HINT_LIMIT` ループ）と同じ形 */
const ITEM_INDENT = "  ";

/**
 * `tried` に載せる探索先の上限。PATH は10個を超えるのが普通なので全部は出さない。
 * ★ 切ったら残件数を出すこと（黙って truncate すると「全部探した」と読まれる）。
 */
const TRIED_HINT_LIMIT = 12;

/**
 * 起こさなかった理由を**そのままログに出せる行**へ落とす。
 *
 * ★ **`index.ts` に文字列を組ませない。** 既知候補の一覧を持っているのはこのファイルなので、
 *   組み立てを外に置くと定義が2箇所に分裂する（`EngineSpawnSkip` の註記）。`index.ts` は
 *   「配線と終了処理しか置かない」方針でテストも無いので、分岐をあちらに残すと検査もされない。
 *
 * ★ **`join(" / ")` で1行にまとめない。** エンジンのフルパスは1本で 80 文字を超えるので、
 *   2本並べただけで端末が折り返し、どこまでが1つのパスか読めなくなる。
 *
 * ★ **帰結（音声が 503 になる）はここに書かない。** それを知っているのは呼び出し側だけ
 *   （→ `index.ts` の `warnAudioUnavailable`）。
 */
export function describeEngineSkip(skip: EngineSpawnSkip): string[] {
  switch (skip.skip) {
    case "not-loopback":
      return [`${LOG_PREFIX} ${skip.host} はループバックではないので合成エンジンを起こせません`];

    case "not-http":
      return [`${LOG_PREFIX} ${skip.protocol} のエンジンは起こせません（起こせるのは平文の http: だけ）`];

    case "not-found": {
      const shown = skip.tried.slice(0, TRIED_HINT_LIMIT);
      const rest = skip.tried.length - shown.length;
      return [
        `${LOG_PREFIX} 合成エンジンが見つかりません。探した場所:`,
        ...shown.map((candidate) => `${LOG_PREFIX}${ITEM_INDENT}${candidate}`),
        ...(rest > 0 ? [`${LOG_PREFIX}${ITEM_INDENT}…ほか ${rest} 件`] : []),
        // ★ 実行ビットまで見ていることを言う。`ls` で見えるファイルが「探した場所」に並ぶので、
        //   これが無いと「在るのに見つからない」の原因（0644）に辿り着けない
        `${LOG_PREFIX} ファイルが在っても実行ビット（chmod +x）が無いと候補から外れます`,
      ];
    }
  }
}

/**
 * 起こした子プロセスのハンドル。
 *
 * `stop()` は**冪等**で、**reject しない**（終了処理の途中で throw させない）。
 */
export interface EngineProcess {
  /**
   * ★ **`pid` と `exited()` に本番の呼び出し元は無い**（`index.ts` は `stop()` しか呼ばない）。
   *   テストの観測点として意図的に残してある。**再起動しない方針が確定している**ので
   *   ヘルスチェック等の用途が生まれる見込みも無い —— 次に読む人が用途を探さなくて済むように
   *   ここに書いておく（→ PR #52 のレビュー）。
   */
  readonly pid: number | undefined;
  /** 既に終わっているか（テストの観測点。上記参照） */
  exited: () => boolean;
  /** プロセスグループごと止める */
  stop: () => Promise<void>;
}

export interface StartEngineDeps {
  /** テスト用。既定 `child_process.spawn` */
  spawn?: typeof nodeSpawn;
  /** テスト用。既定 `process.kill` */
  kill?: (pid: number, signal: NodeJS.Signals) => void;
  /** テスト用。SIGTERM を送ってから SIGKILL に切り替えるまで */
  termGraceMs?: number;
  /** テスト用。SIGKILL の後に exit を待つ上限 */
  killWaitMs?: number;
  log?: (message: string) => void;
  warn?: (message: string) => void;
}

/** 落ちた理由を残すのに要る量。数 KB あれば足りる。stdout / stderr がそれぞれ持つ */
const OUTPUT_TAIL_CHARS = 2048;

/**
 * 終了処理の予算。
 *
 * ★ **`index.ts` の `SHUTDOWN_STEP_TIMEOUT_MS`（2500ms）より小さく保つこと。**
 *   超えると `step()` の watchdog が先に諦め、SIGKILL の経路に到達できない。
 *   合計 1500ms なので 1000ms の余裕がある。
 */
const TERM_GRACE_MS = 1_200;
const KILL_WAIT_MS = 300;

export function startEngine(plan: EngineSpawnPlan, deps: StartEngineDeps = {}): EngineProcess {
  const spawnFn = deps.spawn ?? nodeSpawn;
  const kill = deps.kill ?? ((pid: number, signal: NodeJS.Signals) => void process.kill(pid, signal));
  const termGraceMs = deps.termGraceMs ?? TERM_GRACE_MS;
  const killWaitMs = deps.killWaitMs ?? KILL_WAIT_MS;
  const log = deps.log ?? ((m: string) => console.log(m));
  const warn = deps.warn ?? ((m: string) => console.warn(m));

  const child = spawnFn(plan.command, plan.args, {
    // ★ shell は噛ませない。パスに空白が入るだけで壊れるし、設定ファイル経由の
    //   コマンド実行経路をわざわざ作る理由が無い（→ `player/audioPlayer.ts`）
    shell: false,
    // ★ stdout も拾う。エンジンが listen した瞬間を知る手段は観測しかないのに、捨てていると
    //   「起こしたのに 503 が続く」を調べる人が、エンジンが死ぬまで一切の出力を見られない
    //   （→ PR #52 のレビュー）。tail は stderr と共有する —— 順序は保証しないが、
    //   落ちた理由を追うには足りる
    stdio: ["ignore", "pipe", "pipe"],
    // ★ POSIX では setsid される。これが「`-pid` でプロセスグループごと撃てる」の前提。
    //   `run` は PyInstaller のバイナリで**自分の子を持つ**ので、`child.kill()`（自分だけ）では
    //   孫が残り、ポートを掴んだままになる
    detached: true,
  });

  let exited = false;
  let stopRequested = false;

  // ★ **末尾**を保つ（`audioPlayer.ts` は先頭を保つが、あちらは短命なコマンドの第一報が
  //   欲しいのに対し、こちらは常駐プロセスが**落ちた理由**が欲しいので逆にする）
  // ★ **stdout と stderr で窓を分けること。** 混ぜると、エンジンが stdout に出すアクセスログ
  //   （uvicorn は合成のたびに1行出す）が窓を埋め、**落ちた瞬間の stderr を押し出す**。
  //   出すときは stderr を先に、空なら stdout を使う
  let stderrTail = "";
  let stdoutTail = "";
  const collect = (stream: NodeJS.ReadableStream | null, onChunk: (chunk: string) => void): void => {
    if (!stream) return;
    // ★ setEncoding すること。付けないとチャンク境界で UTF-8 が割れ、日本語のエラーが
    //   U+FFFD に化ける（`String(chunk)` を毎回するのと同じ罠）
    stream.setEncoding("utf-8");
    stream.on("data", onChunk);
  };
  collect(child.stdout, (chunk) => {
    stdoutTail = (stdoutTail + chunk).slice(-OUTPUT_TAIL_CHARS);
  });
  collect(child.stderr, (chunk) => {
    stderrTail = (stderrTail + chunk).slice(-OUTPUT_TAIL_CHARS);
  });

  log(`[Engine] 起動しました (pid=${child.pid ?? "?"}): ${plan.command} ${plan.args.join(" ")}`);

  // ★ spawn の失敗（ENOENT / EACCES）は `exit` ではなく `error` で来る。`exit` だけを見る実装は
  //   「起動したつもりで永久に繋がらない」状態になる（→ `player/audioPlayer.ts`）
  child.on("error", (err) => {
    exited = true;
    // ★ 帰結までここで言い切る。`index.ts` の `warnAudioUnavailable` は「起こさないと決めた」
    //   経路の行なので、**起こしてから失敗した**この経路には届かない。
    //   ENOENT / EACCES は `ttsSpawnCommand` を書き間違えた人が最も踏む経路
    warn(`[Engine] 起動できません (${plan.command}): ${String(err)}。音声は 503 になります`);
  });

  child.on("exit", (code, signal) => {
    exited = true;
    // ★ 自分で止めたときに stderr を出さないこと。SIGTERM で殺すと `code` は `null` になり、
    //   `code !== 0` の条件に引っかかるので、**終了のたびに 2KB のログが落ちる**
    if (stopRequested) {
      log(`[Engine] 停止しました (signal=${signal})`);
      return;
    }
    warn(`[Engine] 終了しました (code=${code} signal=${signal})。再起動はしません（音声は 503 になります）`);
    // ★ これが無いと「起動したはずなのに繋がらない」の原因が1文字も残らない
    if (code === 0) return;
    // stderr を優先。エンジンによっては全部 stdout に出す（uvicorn がそう）ので、その場合は stdout
    const tail = stderrTail.trim() || stdoutTail.trim();
    if (tail) warn(`[Engine] 出力(末尾):\n${tail}`);
  });

  const waitExit = (ms: number): Promise<boolean> =>
    new Promise((resolve) => {
      if (exited) return resolve(true);
      const timer = setTimeout(() => resolve(exited), ms);
      timer.unref();
      child.once("exit", () => {
        clearTimeout(timer);
        resolve(true);
      });
    });

  /**
   * ★ **「送れた」と「もう居ない」と「送れなかった」を混ぜないこと。**
   *   以前はどれも `false` を返していたので、EPERM のときに `doStop` が「完了」と見なして
   *   SIGKILL へ進まず、**グループが生きたまま**終了処理が成功扱いになっていた
   *   （→ PR #52 のレビュー）。短絡してよいのは `gone` だけ。
   */
  const signalGroup = (signal: NodeJS.Signals): "sent" | "gone" | "failed" => {
    const pid = child.pid;
    if (pid === undefined) return "gone";
    try {
      // ★ 負の pid で**プロセスグループ全体**へ送る（`detached: true` が前提）
      kill(-pid, signal);
      return "sent";
    } catch (err) {
      // ESRCH = そのグループはもう居ない。止めるという目的は達している
      if ((err as NodeJS.ErrnoException).code === "ESRCH") return "gone";
      warn(`[Engine] ${signal} を送れませんでした (pid=${pid}): ${String(err)}`);
      return "failed";
    }
  };

  let stopping: Promise<void> | null = null;

  const doStop = async (): Promise<void> => {
    stopRequested = true;
    // ★ **既に exit を見ているなら何も送らない。** pid は OS に再利用されるので、死んだ子の pid で
    //   グループを撃つと無関係なプロセス群を巻き添えにしうる。
    //   代償は「子が先に落ちて孫だけ残った場合に取り逃す」こと —— これはサーバーが SIGKILL された
    //   ときと同じ状態で、次回起動時の疎通確認（条件3）が残ったエンジンを再利用する
    if (exited || child.pid === undefined) return;
    if (signalGroup("SIGTERM") === "gone") return;
    if (await waitExit(termGraceMs)) return;

    // ★ **昇格の直前にも入口と同じガードを置くこと。** 猶予の終わり際に子が死んで、Node が
    //   まだ `exit` を配送していないと `waitExit` は `false` を返す。そのまま撃つと、
    //   reap 済みの pid を持つ**無関係なプロセスグループ**に SIGKILL が飛びうる
    //   （→ PR #52 のレビュー）。
    //
    //   ★ **これで窓が閉じきるわけではない。** `exited` が立つのは `exit` の配送時なので、
    //     reap から配送までの間は「実際は死んでいるがフラグは false」になる。狭めるだけ。
    //     **既存の入口ガードと区別できる単体テストは書けない**ので、意図をここに残す
    if (exited) return;

    warn(`[Engine] SIGTERM で終わらないので SIGKILL します (pid=${child.pid})`);
    signalGroup("SIGKILL");
    await waitExit(killWaitMs);
  };

  return {
    get pid() {
      return child.pid;
    },
    exited: () => exited,
    stop: () => (stopping ??= doStop()),
  };
}
