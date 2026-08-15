import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { createConfigStore, createDefaultConfig } from "./config";
import type { ChatterAgentConfig } from "./config";

let dir: string;
let filePath: string;

const DEFAULTS: ChatterAgentConfig = {
  port: 8570,
  host: "0.0.0.0",
  speakPrompts: true,
  speechLogMaxBytes: 5 * 1024 * 1024,
  speechLogGenerations: 3,
  spoolMaxAgeHours: 6,
};

function store(env: NodeJS.ProcessEnv = {}) {
  return createConfigStore({ filePath, env, defaults: DEFAULTS });
}

function write(content: unknown): void {
  fs.writeFileSync(filePath, typeof content === "string" ? content : JSON.stringify(content));
}

/** mtime の解像度が粗い環境でも size が変わらないケースを踏まないよう明示的にずらす */
function touchFuture(): void {
  const future = new Date(Date.now() + 2000);
  fs.utimesSync(filePath, future, future);
}

beforeEach(() => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "chatter-agent-config-"));
  filePath = path.join(dir, "config.json");
  // 不正値の警告でテスト出力が汚れるのを防ぐ
  vi.spyOn(console, "warn").mockImplementation(() => {});
});

afterEach(() => {
  vi.restoreAllMocks();
  fs.rmSync(dir, { recursive: true, force: true });
});

describe("createDefaultConfig", () => {
  it("WebSocketの既定はポート8570・0.0.0.0バインド", () => {
    const c = createDefaultConfig();
    expect(c.port).toBe(8570);
    expect(c.host).toBe("0.0.0.0");
  });

  it("発話ログとspoolの既定", () => {
    const c = createDefaultConfig();
    expect(c.speechLogMaxBytes).toBe(5 * 1024 * 1024);
    expect(c.speechLogGenerations).toBe(3);
    expect(c.spoolMaxAgeHours).toBe(6);
    expect(c.speakPrompts).toBe(true);
  });
});

describe("createConfigStore", () => {
  it("設定ファイルが無ければ既定値を返す", () => {
    expect(store().snapshot()).toEqual(DEFAULTS);
  });

  it("設定ファイルの値が既定値を上書きする", () => {
    write({ port: 9000, speakPrompts: false, speechLogGenerations: 10 });
    const s = store();
    expect(s.get("port")).toBe(9000);
    expect(s.get("speakPrompts")).toBe(false);
    expect(s.get("speechLogGenerations")).toBe(10);
    expect(s.get("host")).toBe("0.0.0.0"); // 未指定は既定のまま
  });

  it("環境変数が設定ファイルより優先される", () => {
    write({ port: 9000, speakPrompts: false });
    const s = store({ CHATTER_AGENT_PORT: "9999", CHATTER_AGENT_SPEAK_PROMPTS: "true" });
    expect(s.get("port")).toBe(9999);
    expect(s.get("speakPrompts")).toBe(true);
  });

  it("環境変数の真偽値は1/true/yes/onと0/false/no/offを受ける", () => {
    expect(store({ CHATTER_AGENT_SPEAK_PROMPTS: "off" }).get("speakPrompts")).toBe(false);
    expect(store({ CHATTER_AGENT_SPEAK_PROMPTS: "0" }).get("speakPrompts")).toBe(false);
    expect(store({ CHATTER_AGENT_SPEAK_PROMPTS: "YES" }).get("speakPrompts")).toBe(true);
  });

  it("host は 127.0.0.1 に絞れる（信頼できないLAN向け）", () => {
    expect(store({ CHATTER_AGENT_HOST: "127.0.0.1" }).get("host")).toBe("127.0.0.1");
  });

  it("型が不正な値は既定値にフォールバックする", () => {
    write({ port: "abc", speakPrompts: "maybe", speechLogMaxBytes: -5 });
    const s = store();
    expect(s.get("port")).toBe(8570);
    expect(s.get("speakPrompts")).toBe(true);
    expect(s.get("speechLogMaxBytes")).toBe(5 * 1024 * 1024);
  });

  it("範囲外のポートは既定値にフォールバックする", () => {
    write({ port: 70000 });
    expect(store().get("port")).toBe(8570);
  });

  it("未知のキーは無視して警告する", () => {
    write({ port: 9000, aiSummaryEnabled: true });
    const s = store();
    expect(s.get("port")).toBe(9000);
    expect(console.warn).toHaveBeenCalledWith(expect.stringContaining("aiSummaryEnabled"));
  });

  it("トップレベルがオブジェクトでなければ既定値を使う", () => {
    write("[1, 2, 3]");
    expect(store().get("port")).toBe(8570);
  });

  it("設定ファイルを書き換えたら次のgetで反映される", () => {
    write({ port: 9000 });
    const s = store();
    expect(s.get("port")).toBe(9000);

    write({ port: 9100 });
    touchFuture();
    expect(s.get("port")).toBe(9100);
  });

  it("JSONが壊れたら直前の値を維持する", () => {
    write({ port: 9000 });
    const s = store();
    expect(s.get("port")).toBe(9000);

    write("{ port: 91"); // 書き込み途中を模した壊れたJSON
    touchFuture();
    expect(s.get("port")).toBe(9000);
  });

  it("同じ不正値で警告を繰り返さない", () => {
    write({ port: "abc" });
    const s = store();
    s.get("port");
    s.get("port");
    s.get("port");
    expect(console.warn).toHaveBeenCalledTimes(1);
  });

  it("filePath を公開する（起動ログ用）", () => {
    expect(store().filePath).toBe(filePath);
  });
});
