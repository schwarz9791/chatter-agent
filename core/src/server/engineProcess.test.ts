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
import { knownEnginePaths, resolveEngineSpawn, startEngine, type EngineProcess } from "./engineProcess";

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

  it("ポートを省略した baseUrl はスキームの既定に落とす", () => {
    expect(resolve({ baseUrl: "http://127.0.0.1" }, [APP])).toMatchObject({
      args: ["--host", "127.0.0.1", "--port", "80"],
    });
    expect(resolve({ baseUrl: "https://127.0.0.1" }, [APP])).toMatchObject({
      args: ["--host", "127.0.0.1", "--port", "443"],
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
      exists,
      // PATH を空にして、実環境の PATH を引かないようにする
    });
    expect(result).toEqual({ skip: "not-found", tried: ["chatter-agent-no-such-engine-xyz"] });
    expect(exists).not.toHaveBeenCalled();
  });

  it("★ ttsSpawnArgs を指定したら --host/--port は足さない（追加ではなく置換）", () => {
    expect(resolve({ args: ["--use_gpu"] }, [APP])).toEqual({ command: APP, args: ["--use_gpu"] });
  });
});

// ── startEngine ────────────────────────────────────────────────────────────

const started: EngineProcess[] = [];
let dir: string;

beforeEach(() => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "chatter-agent-engine-"));
});

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

  it("★ stderr は末尾を残す（落ちた理由が欲しい）", async () => {
    const warnings: string[] = [];
    // 2048 文字を超えて流し、先頭が消えて末尾が残ることを見る
    const engine = start("/bin/sh", ["-c", 'printf "A%.0s" $(seq 1 3000) >&2; echo "-LAST-" >&2; exit 1'], {
      warn: (m) => void warnings.push(m),
    });

    expect(await until(() => engine.exited())).toBe(true);
    const dump = warnings.find((m) => m.includes("stderr")) ?? "";
    expect(dump).toContain("-LAST-");
    expect(dump.length).toBeLessThan(2_200);
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

  it("spawn には detached / shell:false / stderr のパイプを渡す", () => {
    const calls: unknown[][] = [];
    startEngine(
      { command: "/bin/echo", args: ["x"] },
      {
        log: () => {},
        warn: () => {},
        spawn: ((...callArgs: unknown[]) => {
          calls.push(callArgs);
          // exit も error も出さない最小のダミー
          return { pid: 1234, stderr: null, on: () => {}, once: () => {} } as never;
        }) as never,
      },
    );
    expect(calls[0]?.[2]).toMatchObject({ detached: true, shell: false, stdio: ["ignore", "ignore", "pipe"] });
  });
});
