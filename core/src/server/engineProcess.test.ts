/**
 * `resolveEngineSpawn` は純関数なので網羅する。`startEngine` は
 * **`spawn` をモックせず実際にコマンドを起動する**（`audioPlayer.test.ts` / `wsServer.test.ts` と
 * 同じ方針）。`/bin/sh` は macOS と Linux の両方にある。
 *
 * ★ プロセスグループごとの kill は実測でしか確かめられない。`sh` は SIGTERM を背景ジョブへ
 *   転送しないので、`child.kill()` 相当なら孫（`sleep`）が生き残り、`process.kill(-pid)` なら
 *   道連れになる —— この2つは外から区別できる。
 */

import { describe, it, expect, afterEach, beforeEach, vi } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import {
  describeEngineSkip,
  knownEnginePaths,
  resolveEngineSpawn,
  startEngine,
  type EngineProcess,
  type EngineSpawnSkip,
} from "./engineProcess";

let dir: string;

beforeEach(() => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "chatter-agent-engine-"));
});

const HOME = "/Users/tester";
const APP = path.join("/Applications", "AivisSpeech.app", "Contents", "Resources", "AivisSpeech-Engine", "run");
const HOME_APP = path.join(
  HOME,
  "Applications",
  "AivisSpeech.app",
  "Contents",
  "Resources",
  "AivisSpeech-Engine",
  "run",
);

/** 既定は「どこにも無い」。見つけたいものだけ渡す */
function resolve(
  overrides: Partial<Parameters<typeof resolveEngineSpawn>[0]> = {},
  present: string[] = [],
): ReturnType<typeof resolveEngineSpawn> {
  return resolveEngineSpawn({
    baseUrl: "http://127.0.0.1:10101",
    command: "",
    args: [],
    homeDir: HOME,
    // ★ 既定で PATH を空にする。渡す口が無かった頃は、明示 `ttsSpawnCommand` の分岐だけ
    //   実環境の PATH とホームの中身に依存していた（→ PR #52 のレビュー）
    env: { PATH: "" },
    exists: (p) => present.includes(p),
    ...overrides,
  });
}

describe("resolveEngineSpawn", () => {
  it("ループバックなら既知候補（/Applications）を使い、baseUrl から --host/--port を組む", () => {
    expect(resolve({}, [APP])).toEqual({
      command: APP,
      args: ["--host", "127.0.0.1", "--port", "10101"],
    });
  });

  it("/Applications に無ければ ~/Applications を見る", () => {
    const plan = resolve({}, [HOME_APP]);
    expect(plan).toEqual({ command: HOME_APP, args: ["--host", "127.0.0.1", "--port", "10101"] });
  });

  it("どちらにも無ければ not-found（探した場所を返す）", () => {
    expect(resolve({}, [])).toEqual({ skip: "not-found", tried: knownEnginePaths(HOME) });
  });

  it("localhost はそのまま --host localhost に渡す（127.0.0.1 へ読み替えない）", () => {
    expect(resolve({ baseUrl: "http://localhost:10101" }, [APP])).toMatchObject({
      args: ["--host", "localhost", "--port", "10101"],
    });
  });

  /**
   * ★ 要のケース。`new URL("http://[::1]:10101").hostname` は `"[::1]"`（角括弧付き）を返す。
   *   判定側で `"::1"` と書くと spawn せず、`--host` に `[::1]` を渡すと bind に失敗する。
   */
  it("★ IPv6 のループバックを受け、--host からは角括弧が外れる", () => {
    expect(resolve({ baseUrl: "http://[::1]:10101" }, [APP])).toEqual({
      command: APP,
      args: ["--host", "::1", "--port", "10101"],
    });
  });

  it("IPv6 の非圧縮表記も URL の正規化に乗る", () => {
    expect(resolve({ baseUrl: "http://[0:0:0:0:0:0:0:1]:10101" }, [APP])).toMatchObject({
      args: ["--host", "::1", "--port", "10101"],
    });
  });

  it("127.0.0.0/8 はすべてループバックとして扱う", () => {
    expect(resolve({ baseUrl: "http://127.0.0.2:10101" }, [APP])).toMatchObject({
      args: ["--host", "127.0.0.2", "--port", "10101"],
    });
  });

  it("★ ループバックでなければ not-loopback。fs には一度も触らない", () => {
    const exists = vi.fn(() => true);
    for (const baseUrl of ["http://192.168.1.5:10101", "http://example.com:10101"]) {
      const result = resolveEngineSpawn({ baseUrl, command: "", args: [], homeDir: HOME, exists });
      expect(result).toEqual({ skip: "not-loopback", host: new URL(baseUrl).hostname });
    }
    expect(exists).not.toHaveBeenCalled();
  });

  it("ポートを省略した baseUrl は http の既定（80）に落とす", () => {
    expect(resolve({ baseUrl: "http://127.0.0.1" }, [APP])).toMatchObject({
      args: ["--host", "127.0.0.1", "--port", "80"],
    });
  });

  /**
   * ★ `makeUrlParser` は `https:` も通すので、ここで弾かないと `--port 443` で
   *   **平文のエンジン**が立ち上がる（非 root なら bind に失敗する）。
   */
  it("★ https のループバックは起こさない（起こせるのは平文の http だけ）", () => {
    expect(resolve({ baseUrl: "https://127.0.0.1:10101" }, [APP])).toEqual({
      skip: "not-http",
      protocol: "https:",
    });
  });

  it("ttsSpawnCommand が絶対パスなら、既知候補を探さずにそれを使う", () => {
    const exists = vi.fn(() => true);
    const result = resolveEngineSpawn({
      baseUrl: "http://127.0.0.1:10101",
      command: "/opt/aivis/run",
      args: [],
      homeDir: HOME,
      exists,
    });
    expect(result).toMatchObject({ command: "/opt/aivis/run" });
    expect(exists).not.toHaveBeenCalled();
  });

  it("ttsSpawnCommand の ~/ は homeDir に展開される", () => {
    expect(resolve({ command: "~/bin/run" })).toMatchObject({ command: path.join(HOME, "bin", "run") });
  });

  it("★ ttsSpawnCommand が見つからないとき、既知候補にフォールバックしない", () => {
    const exists = vi.fn(() => true);
    const result = resolveEngineSpawn({
      baseUrl: "http://127.0.0.1:10101",
      command: "chatter-agent-no-such-engine-xyz",
      args: [],
      homeDir: HOME,
      env: { PATH: "" },
      exists,
    });
    expect(result).toMatchObject({ skip: "not-found" });
    expect(exists).not.toHaveBeenCalled();
  });

  it("★ not-found は探した場所をフルパスで返す（生のコマンド名だけにしない）", () => {
    expect(resolve({ command: "aivis-run", env: { PATH: "/opt/bin:/usr/bin" } })).toEqual({
      skip: "not-found",
      tried: ["/opt/bin/aivis-run", "/usr/bin/aivis-run"],
    });
  });

  /**
   * ★ **名前からの解決は禁止しない**（`ttsSpawnCommand: "docker"` でコンテナのエンジンを
   *   起こす運用が潰れるため）。代わりに**黙って読み替えない** —— 元の値を `resolvedFrom` で
   *   返し、呼び出し側が名指しでログに出す（→ PR #52 のレビュー）。
   *
   * ★ 一度は「既知の bin ディレクトリを探さない」で塞ごうとしたが、**実測すると
   *   `getKnownBinDirs` の 7 件は 7/7 とも PATH に載っていた**ので穴が1つも塞がらなかった。
   */
  it("★ 名前から解決したときは resolvedFrom に元の値を残す", () => {
    const binDir = path.join(dir, "path-bin");
    fs.mkdirSync(binDir, { recursive: true });
    const bin = path.join(binDir, "run");
    fs.writeFileSync(bin, "#!/bin/sh\n");
    fs.chmodSync(bin, 0o755);

    expect(resolve({ command: "run", env: { PATH: binDir } })).toMatchObject({
      command: bin,
      resolvedFrom: "run",
    });
  });

  it("絶対パスで指定したときは resolvedFrom を付けない（読み替えが起きていない）", () => {
    expect(resolve({ command: "/opt/aivis/run" })).toEqual({
      command: "/opt/aivis/run",
      args: ["--host", "127.0.0.1", "--port", "10101"],
    });
  });

  it("~/ 指定も読み替えではないので resolvedFrom を付けない", () => {
    expect(resolve({ command: "~/bin/run" })).not.toHaveProperty("resolvedFrom");
  });

  it("既知候補から見つけたときも resolvedFrom は付かない", () => {
    expect(resolve({}, [APP])).not.toHaveProperty("resolvedFrom");
  });

  it("★ ttsSpawnArgs を指定したら --host/--port は足さない（追加ではなく置換）", () => {
    expect(resolve({ args: ["--use_gpu"] }, [APP])).toEqual({ command: APP, args: ["--use_gpu"] });
  });
});

describe("describeEngineSkip", () => {
  it("ループバックでない理由はホスト名を名指しする", () => {
    expect(describeEngineSkip({ skip: "not-loopback", host: "example.com" })).toEqual([
      "[Server] example.com はループバックではないので合成エンジンを起こせません",
    ]);
  });

  it("http 以外はスキームを名指しする", () => {
    expect(describeEngineSkip({ skip: "not-http", protocol: "https:" })[0]).toContain("https:");
  });

  /** ★ フルパスは1本で 80 文字を超えるので、`/` で連結すると端末の折り返しで読めなくなる */
  it("★ not-found は1行1件で出す（連結しない）", () => {
    const lines = describeEngineSkip({ skip: "not-found", tried: ["/a/run", "/b/run"] });
    expect(lines.some((l) => l.includes("/a/run") && l.includes("/b/run"))).toBe(false);
    expect(lines.filter((l) => l.includes("/a/run"))).toHaveLength(1);
    expect(lines.filter((l) => l.includes("/b/run"))).toHaveLength(1);
  });

  /** ★ `ls` で見えるファイルが「探した場所」に並ぶので、これが無いと 0644 に辿り着けない */
  it("★ 実行ビットを見ていることを伝える", () => {
    const lines = describeEngineSkip({ skip: "not-found", tried: ["/a/run"] });
    expect(lines.some((l) => l.includes("実行ビット"))).toBe(true);
  });

  it("★ 上限で切ったら残件数を出す（黙って truncate しない）", () => {
    const tried = Array.from({ length: 30 }, (_, i) => `/dir${i}/run`);
    const lines = describeEngineSkip({ skip: "not-found", tried });
    expect(lines.some((l) => l.includes("ほか 18 件"))).toBe(true);
    expect(lines.some((l) => l.includes("/dir29/run"))).toBe(false);
  });

  it("上限以内なら残件数の行を出さない", () => {
    const lines = describeEngineSkip({ skip: "not-found", tried: ["/a/run", "/b/run"] });
    expect(lines.some((l) => l.includes("ほか"))).toBe(false);
  });

  /** ★ 帰結（503）を知っているのは呼び出し側だけ。ここで言うと spawn する経路で嘘になる */
  it("★ 帰結（503）は書かない", () => {
    const skips: EngineSpawnSkip[] = [
      { skip: "not-loopback", host: "h" },
      { skip: "not-http", protocol: "https:" },
      { skip: "not-found", tried: ["/a/run"] },
    ];
    for (const skip of skips) {
      expect(describeEngineSkip(skip).some((l) => l.includes("503"))).toBe(false);
    }
  });
});

// ── startEngine ────────────────────────────────────────────────────────────

const started: EngineProcess[] = [];

/** テストが落ちても `sleep 30` を残さない */
afterEach(async () => {
  for (const engine of started.splice(0)) {
    try {
      await engine.stop();
    } catch {
      // 後始末なので失敗しても続ける
    }
  }
  vi.restoreAllMocks();
  fs.rmSync(dir, { recursive: true, force: true });
});

function start(command: string, args: string[], deps: Parameters<typeof startEngine>[1] = {}): EngineProcess {
  const engine = startEngine({ command, args }, { log: () => {}, warn: () => {}, ...deps });
  started.push(engine);
  return engine;
}

/** 条件が満たされるまで短く待つ（実プロセスの生死はイベントループを跨ぐ） */
async function until(predicate: () => boolean, timeoutMs = 3_000): Promise<boolean> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (predicate()) return true;
    await new Promise((done) => setTimeout(done, 20));
  }
  return predicate();
}

function alive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch (err) {
    return (err as NodeJS.ErrnoException).code === "EPERM";
  }
}

describe("startEngine", () => {
  /**
   * ★ **孫が生まれるのを待ってから止めること。** `until(() => alive(childPid))` は spawn 直後に
   *   true を返すので、そこで `stop()` すると `sh` が `sleep` を fork する前に殺してしまい、
   *   「孫が残らなかった」が**プロセスグループとは無関係の理由で**成立する。実際それで
   *   `kill(pid)` への退行を検出できていなかった。孫の pid をファイル経由で受け取る。
   */
  it("★ stop() が孫プロセスまで道連れにする（プロセスグループごと撃っている）", async () => {
    const marker = path.join(dir, "grandchild.pid");
    const engine = start("/bin/sh", ["-c", `sleep 30 & echo $! > ${marker}; wait`]);

    expect(await until(() => fs.existsSync(marker) && fs.readFileSync(marker, "utf-8").trim() !== "")).toBe(true);
    const grandPid = Number(fs.readFileSync(marker, "utf-8").trim());
    expect(Number.isInteger(grandPid)).toBe(true);
    expect(await until(() => alive(grandPid))).toBe(true);

    await engine.stop();

    // ★ ここが本題。`child.kill()`（自分だけ）なら sh は死ぬが sleep は生き残る。
    //   `process.kill(-pid)` でグループごと撃って初めて孫も消える
    expect(await until(() => !alive(grandPid))).toBe(true);
  });

  it("SIGTERM を無視する相手には SIGKILL まで進む", async () => {
    const engine = start("/bin/sh", ["-c", 'trap "" TERM; sleep 30'], { termGraceMs: 100, killWaitMs: 1_000 });
    const pid = engine.pid!;
    expect(await until(() => alive(pid))).toBe(true);

    await engine.stop();
    expect(await until(() => !alive(pid))).toBe(true);
  });

  it("異常終了すると stderr の末尾を添えて warn する", async () => {
    const warnings: string[] = [];
    const engine = start("/bin/sh", ["-c", 'echo "ここが原因です" >&2; exit 3'], {
      warn: (m) => void warnings.push(m),
    });

    expect(await until(() => engine.exited())).toBe(true);
    expect(warnings.some((m) => m.includes("code=3"))).toBe(true);
    expect(warnings.some((m) => m.includes("ここが原因です"))).toBe(true);
  });

  it("★ 出力は末尾を残す（落ちた理由が欲しい）", async () => {
    const warnings: string[] = [];
    // 2048 文字を超えて流し、先頭が消えて末尾が残ることを見る
    const engine = start("/bin/sh", ["-c", 'printf "A%.0s" $(seq 1 3000) >&2; echo "-LAST-" >&2; exit 1'], {
      warn: (m) => void warnings.push(m),
    });

    expect(await until(() => engine.exited())).toBe(true);
    const dump = warnings.find((m) => m.includes("出力(末尾)")) ?? "";
    expect(dump).toContain("-LAST-");
    expect(dump.length).toBeLessThan(2_200);
  });

  /**
   * ★ stdout を捨てていると「起こしたのに 503 が続く」を調べる人が、エンジンが死ぬまで
   *   一切の出力を見られない（→ PR #52 のレビュー）。uvicorn の起動ログは stdout に出る。
   */
  it("★ stdout しか出さないエンジンでも手がかりが残る", async () => {
    const warnings: string[] = [];
    const engine = start("/bin/sh", ["-c", 'echo "stdout に出た手がかり"; exit 2'], {
      warn: (m) => void warnings.push(m),
    });

    expect(await until(() => engine.exited())).toBe(true);
    expect(warnings.some((m) => m.includes("stdout に出た手がかり"))).toBe(true);
  });

  /**
   * ★ 混ぜると、エンジンが stdout に出すアクセスログ（uvicorn は合成のたびに1行出す）が
   *   窓を埋め、**落ちた瞬間の stderr を押し出す**。窓は分けて、出すときは stderr を優先する。
   */
  it("★ stdout の洪水が stderr を押し出さない（窓が分かれている）", async () => {
    const warnings: string[] = [];
    const engine = start("/bin/sh", ["-c", 'echo "本当の原因" >&2; printf "A%.0s" $(seq 1 4000); exit 1'], {
      warn: (m) => void warnings.push(m),
    });

    expect(await until(() => engine.exited())).toBe(true);
    const dump = warnings.find((m) => m.includes("出力(末尾)")) ?? "";
    expect(dump).toContain("本当の原因");
  });

  it("★ 自分で止めたときは stderr を出さない（SIGTERM だと code は null になる）", async () => {
    const warnings: string[] = [];
    const engine = start("/bin/sh", ["-c", 'echo "起動時の注意書き" >&2; sleep 30'], {
      warn: (m) => void warnings.push(m),
    });
    await until(() => engine.pid !== undefined);

    await engine.stop();
    expect(await until(() => engine.exited())).toBe(true);
    expect(warnings.some((m) => m.includes("stderr"))).toBe(false);
    expect(warnings.some((m) => m.includes("終了しました"))).toBe(false);
  });

  it("起動できないコマンドは error 経路で warn し、exited() が true になる", async () => {
    const warnings: string[] = [];
    const engine = start("/definitely/not/a/real/binary", [], { warn: (m) => void warnings.push(m) });

    expect(await until(() => engine.exited())).toBe(true);
    expect(warnings.some((m) => m.includes("起動できません"))).toBe(true);
  });

  it("★ 既に終わっている相手にはシグナルを送らない（pid の再利用を巻き添えにしない）", async () => {
    const sent: [number, string][] = [];
    const engine = start("/usr/bin/true", [], { kill: (pid, signal) => void sent.push([pid, signal]) });

    expect(await until(() => engine.exited())).toBe(true);
    await engine.stop();
    expect(sent).toEqual([]);
  });

  it("stop() は冪等（2回呼んでもシグナルは1組だけ）", async () => {
    const sent: [number, string][] = [];
    const engine = start("/bin/sh", ["-c", "sleep 30"], {
      kill: (pid, signal) => {
        sent.push([pid, signal]);
        process.kill(pid, signal);
      },
      termGraceMs: 500,
    });
    await until(() => engine.pid !== undefined);
    const pid = engine.pid!;

    await Promise.all([engine.stop(), engine.stop()]);
    // 負の pid（プロセスグループ）へ SIGTERM が1回だけ
    expect(sent).toEqual([[-pid, "SIGTERM"]]);
  });

  it("spawn には detached / shell:false / stdout と stderr のパイプを渡す", () => {
    const calls: unknown[][] = [];
    startEngine(
      { command: "/bin/echo", args: ["x"] },
      {
        log: () => {},
        warn: () => {},
        spawn: ((...callArgs: unknown[]) => {
          calls.push(callArgs);
          // exit も error も出さない最小のダミー
          return { pid: 1234, stdout: null, stderr: null, on: () => {}, once: () => {} } as never;
        }) as never,
      },
    );
    expect(calls[0]?.[2]).toMatchObject({ detached: true, shell: false, stdio: ["ignore", "pipe", "pipe"] });
  });

  /**
   * ★ 以前は ESRCH（もう居ない）も EPERM（送れなかった）も同じ `false` を返していたので、
   *   **グループが生きているのに昇格せず**、終了処理が成功扱いになっていた
   *   （→ PR #52 のレビュー）。
   */
  it("★ ESRCH 以外の kill 失敗では SIGKILL まで進む", async () => {
    const sent: string[] = [];
    const engine = start("/bin/sh", ["-c", "sleep 30"], {
      kill: (_pid, signal) => {
        sent.push(signal);
        const err = new Error("operation not permitted") as NodeJS.ErrnoException;
        err.code = "EPERM";
        throw err;
      },
      termGraceMs: 50,
      killWaitMs: 50,
    });
    await until(() => engine.pid !== undefined);

    await engine.stop();
    expect(sent).toEqual(["SIGTERM", "SIGKILL"]);

    // 実際には送れていないので、後始末は自前で
    if (engine.pid !== undefined) {
      try {
        process.kill(-engine.pid, "SIGKILL");
      } catch {
        // 既に居ないなら何もしない
      }
    }
  });

  it("ESRCH（もう居ない）なら SIGKILL へ進まない", async () => {
    const sent: string[] = [];
    const engine = start("/bin/sh", ["-c", "sleep 30"], {
      kill: (_pid, signal) => {
        sent.push(signal);
        const err = new Error("no such process") as NodeJS.ErrnoException;
        err.code = "ESRCH";
        throw err;
      },
      termGraceMs: 50,
    });
    await until(() => engine.pid !== undefined);

    await engine.stop();
    expect(sent).toEqual(["SIGTERM"]);

    if (engine.pid !== undefined) {
      try {
        process.kill(-engine.pid, "SIGKILL");
      } catch {
        // 既に居ないなら何もしない
      }
    }
  });
});
