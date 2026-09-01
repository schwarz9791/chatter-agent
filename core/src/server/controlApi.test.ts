import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { createConfigStore, type ConfigStore } from "../core/config";
import { VERSION } from "../core/version";
import { createControlApi, type ControlApiDeps } from "./controlApi";

let dir: string;
let filePath: string;
let homeDir: string;

beforeEach(() => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "chatter-agent-control-"));
  filePath = path.join(dir, "config.json");
  homeDir = path.join(dir, "home");
  // 不正値の警告でテスト出力が汚れるのを防ぐ
  vi.spyOn(console, "warn").mockImplementation(() => {});
});

afterEach(() => {
  vi.restoreAllMocks();
  fs.rmSync(dir, { recursive: true, force: true });
});

function write(content: unknown): void {
  fs.writeFileSync(filePath, typeof content === "string" ? content : JSON.stringify(content));
}

function readFile(): Record<string, unknown> {
  return JSON.parse(fs.readFileSync(filePath, "utf-8")) as Record<string, unknown>;
}

function store(env: NodeJS.ProcessEnv = {}): ConfigStore {
  return createConfigStore({ filePath, env });
}

function api(overrides: Partial<ControlApiDeps> = {}) {
  return createControlApi({
    config: store(),
    listSpeakers: () => Promise.resolve([{ id: 1, label: "話者（ノーマル）" }]),
    synthesizePreview: () => Promise.resolve(new ArrayBuffer(44)),
    summaryPreview: {
      getCommand: () => "chatter-agent-no-such-command",
      getModel: () => "",
      getTimeoutMs: () => 1000,
      homeDir,
      registerSessionId: () => {},
    },
    ...overrides,
  });
}

/** JSON レスポンスの body を型を付けて取り出す */
function body<T>(response: { kind: string; body: unknown }): T {
  expect(response.kind).toBe("json");
  return response.body as T;
}

describe("GET /v1/health", () => {
  it("version を名乗る", () => {
    const res = api().health();
    expect(res.status).toBe(200);
    expect(body(res)).toEqual({ ok: true, version: VERSION });
  });
});

describe("GET /v1/speakers", () => {
  it("話者一覧を返す", async () => {
    const res = await api().speakers();
    expect(res.status).toBe(200);
    expect(body(res)).toEqual({ speakers: [{ id: 1, label: "話者（ノーマル）" }] });
  });

  /**
   * ★ エンジンが居ないだけ。設定 UI 側は**項目を消さずに**「取得できません」で出す
   *   （消すと「設定が無い」に見える）
   */
  it("★ エンジンに繋がらなければ 503 engine_unreachable", async () => {
    const res = await api({ listSpeakers: () => Promise.reject(new Error("接続できません")) }).speakers();
    expect(res.status).toBe(503);
    expect(body<{ error: string }>(res).error).toBe("engine_unreachable");
  });
});

describe("GET /v1/config", () => {
  it("values / origins / writable を返す", () => {
    write({ ttsSpeakerId: 3 });
    const res = api({ config: store({ CHATTER_AGENT_TTS_SPEED_SCALE: "1.5" }) }).getConfig();
    const value = body<{
      values: Record<string, unknown>;
      origins: Record<string, string>;
      writable: string[];
    }>(res);

    expect(res.status).toBe(200);
    expect(value.values.ttsSpeakerId).toBe(3);
    expect(value.values.ttsSpeedScale).toBe(1.5);
    expect(value.origins.ttsSpeakerId).toBe("file");
    expect(value.origins.ttsSpeedScale).toBe("env");
    expect(value.origins.aiSummaryEnabled).toBe("default");
    expect(value.writable).toContain("ttsSpeakerId");
    expect(value.writable).not.toContain("playerCommand");
  });
});

describe("PATCH /v1/config", () => {
  it("書けて、適用後の値が返る", () => {
    const res = api().patchConfig({ ttsSpeedScale: 1.5 });
    expect(res.status).toBe(200);
    expect(body<{ values: Record<string, unknown> }>(res).values.ttsSpeedScale).toBe(1.5);
    expect(readFile().ttsSpeedScale).toBe(1.5);
  });

  /** ★★ 他バイナリ向けの未知キーが消えないこと */
  it("★★ config.json の未知キーを消さない", () => {
    write({ futureKey: "残す", ttsSpeakerId: 1 });
    api().patchConfig({ ttsSpeakerId: 2 });
    expect(readFile()).toEqual({ futureKey: "残す", ttsSpeakerId: 2 });
  });

  it("ファイルが無くても新規作成できる", () => {
    expect(fs.existsSync(filePath)).toBe(false);
    expect(api().patchConfig({ ttsSpeakerId: 7 }).status).toBe(200);
    expect(readFile()).toEqual({ ttsSpeakerId: 7 });
  });

  /**
   * ★★ 壊れたファイルを丸ごと書き潰さない。`readRawFile` が undefined を返したときに
   *   `{}` で代用すると、ユーザーの設定が消える
   */
  it("★★ config.json が壊れていたら書かずに 500", () => {
    write("{ 壊れている");
    const before = fs.readFileSync(filePath, "utf-8");
    const res = api().patchConfig({ ttsSpeakerId: 2 });
    expect(res.status).toBe(500);
    expect(body<{ error: string }>(res).error).toBe("config_unreadable");
    expect(fs.readFileSync(filePath, "utf-8")).toBe(before);
  });

  it("未知のキーは 400 unknown_key", () => {
    const res = api().patchConfig({ nope: 1 });
    expect(res.status).toBe(400);
    expect(body(res)).toEqual({ error: "unknown_key", key: "nope" });
  });

  it("範囲外の値は 400 invalid_value", () => {
    const res = api().patchConfig({ ttsSpeedScale: 3 });
    expect(res.status).toBe(400);
    expect(body(res)).toEqual({ error: "invalid_value", key: "ttsSpeedScale" });
  });

  /** ★★ ループバック限定でも「設定1行で任意コマンド実行」にはしない */
  it("★★ コマンド実行に繋がるキーは 403 readonly_key", () => {
    const res = api().patchConfig({ playerCommand: "/bin/sh" });
    expect(res.status).toBe(403);
    expect(body(res)).toEqual({ error: "readonly_key", key: "playerCommand" });
  });

  it("再起動まで効かないキーも 403 readonly_key", () => {
    expect(api().patchConfig({ port: 9999 }).status).toBe(403);
  });

  /** ★ 黙って書いて効かないのが最悪。409 で明示的に断る */
  it("★ 環境変数が勝っているキーは 409 env_override（ファイルも書き換わらない）", () => {
    write({ ttsSpeakerId: 1 });
    const res = api({ config: store({ CHATTER_AGENT_TTS_SPEAKER_ID: "5" }) }).patchConfig({ ttsSpeakerId: 9 });
    expect(res.status).toBe(409);
    expect(body(res)).toEqual({ error: "env_override", key: "ttsSpeakerId" });
    expect(readFile().ttsSpeakerId).toBe(1);
  });

  it("オブジェクト以外のボディは 400 invalid_body", () => {
    expect(api().patchConfig(null).status).toBe(400);
    expect(api().patchConfig([1]).status).toBe(400);
    expect(api().patchConfig("x").status).toBe(400);
  });

  /** ★★ 1つでも弾かれたら何も書かない */
  it("★★ 弾かれたキーが混ざっていたら1つも書かない", () => {
    write({ ttsSpeakerId: 1 });
    const res = api().patchConfig({ ttsSpeakerId: 2, playerCommand: "/bin/sh" });
    expect(res.status).toBe(403);
    expect(readFile().ttsSpeakerId).toBe(1);
  });
});

describe("POST /v1/tts/preview", () => {
  it("WAV を返す", async () => {
    const res = await api().ttsPreview();
    expect(res.status).toBe(200);
    expect(res.kind).toBe("wav");
  });

  /** ★ 任意テキストを受けない（無認証の合成 API にしない）ので、外から文を渡す口が無い */
  it("★ 合成する文は固定（呼び出し側からテキストを渡せない）", async () => {
    const seen: string[] = [];
    await api({
      synthesizePreview: (text) => {
        seen.push(text);
        return Promise.resolve(new ArrayBuffer(44));
      },
    }).ttsPreview();
    expect(seen).toHaveLength(1);
    expect(seen[0]).toContain("テスト音声");
  });

  it("合成に失敗したら 503", async () => {
    const res = await api({ synthesizePreview: () => Promise.reject(new Error("エンジンが居ません")) }).ttsPreview();
    expect(res.status).toBe(503);
    expect(body<{ error: string }>(res).error).toBe("synthesis_unavailable");
  });

  /** ★ 押しっぱなしにされたときにエンジンを守る最後の砦（本命はクライアント側のデバウンス） */
  it("★ 最短間隔（1秒）以内の再要求は 429", async () => {
    const control = api({ now: () => 1_000 });
    expect((await control.ttsPreview()).status).toBe(200);
    expect((await control.ttsPreview()).status).toBe(429);
  });

  /** ★ 合成と要約でゲートを分ける（片方を押しただけでもう片方が弾かれない） */
  it("★ テスト音声とテスト要約のゲートは別", async () => {
    const control = api({ now: () => 1_000 });
    expect((await control.ttsPreview()).status).toBe(200);
    expect((await control.summaryPreview()).status).toBe(200);
  });
});

describe("POST /v1/summary/preview", () => {
  /** ★★ 失敗も 200。「あとで取りに来い」ではなく「試した結果、こうだった」 */
  it("★★ 失敗しても 200 で outcome を返す（503 にしない）", async () => {
    const res = await api().summaryPreview();
    expect(res.status).toBe(200);
    const value = body<{ summary: string | null; outcome: string; elapsedMs: number }>(res);
    expect(value.outcome).toBe("no-command");
    expect(value.summary).toBeNull();
    expect(value.elapsedMs).toBeGreaterThanOrEqual(0);
  });

  it("最短間隔以内の再要求は 429", async () => {
    const control = api({ now: () => 1_000 });
    expect((await control.summaryPreview()).status).toBe(200);
    expect((await control.summaryPreview()).status).toBe(429);
  });
});
