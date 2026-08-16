import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { createConfigStore, createDefaultConfig, isSpeakDisabled } from "./config";
import type { ChatterAgentConfig } from "./config";

let dir: string;
let filePath: string;

const DEFAULTS: ChatterAgentConfig = {
  port: 8570,
  host: "0.0.0.0",
  speakPrompts: true,
  speechLogMaxBytes: 5 * 1024 * 1024,
  speechQueueMaxEntries: 500,
  spoolMaxAgeHours: 6,
  allowedOrigins: [],

  ttsBaseUrl: "http://127.0.0.1:10101",
  ttsSpeakerId: 888753760,
  synthesisLookahead: 3,
  synthesisTimeoutMs: 30_000,
  playerCommand: "afplay",
  playerArgs: ["{file}"],
  playerServerUrl: "",
  speechMaxAgeMs: 0,
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
    expect(c.speechQueueMaxEntries).toBe(500);
    expect(c.spoolMaxAgeHours).toBe(6);
    expect(c.speakPrompts).toBe(true);
  });

  it("player の既定は AivisSpeech 単体起動のポートと afplay", () => {
    const c = createDefaultConfig();
    expect(c.ttsBaseUrl).toBe("http://127.0.0.1:10101");
    expect(c.ttsSpeakerId).toBe(888753760);
    expect(c.synthesisLookahead).toBe(3);
    expect(c.playerCommand).toBe("afplay");
    expect(c.playerArgs).toEqual(["{file}"]);
    // 空なら port / host から導出する。速度と古さの既定は無効
    expect(c.playerServerUrl).toBe("");
    expect(c.speechMaxAgeMs).toBe(0);
  });
});

describe("createConfigStore", () => {
  it("設定ファイルが無ければ既定値を返す", () => {
    expect(store().snapshot()).toEqual(DEFAULTS);
  });

  it("設定ファイルの値が既定値を上書きする", () => {
    write({ port: 9000, speakPrompts: false, speechQueueMaxEntries: 10 });
    const s = store();
    expect(s.get("port")).toBe(9000);
    expect(s.get("speakPrompts")).toBe(false);
    expect(s.get("speechQueueMaxEntries")).toBe(10);
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

  it("ttsBaseUrl はスキームを検査し、末尾スラッシュを落とす", () => {
    expect(store({ CHATTER_AGENT_TTS_URL: "http://127.0.0.1:8564/" }).get("ttsBaseUrl")).toBe("http://127.0.0.1:8564");
    expect(store({ CHATTER_AGENT_TTS_URL: "https://tts.example/api//" }).get("ttsBaseUrl")).toBe(
      "https://tts.example/api",
    );
  });

  it("スキームの無い ttsBaseUrl は既定値にフォールバックする", () => {
    // ★ 素通しにすると `localhost:10101/audio_query` を fetch して、症状が「無音」の設定ミスになる
    write({ ttsBaseUrl: "localhost:10101" });
    expect(store().get("ttsBaseUrl")).toBe("http://127.0.0.1:10101");
  });

  it("playerServerUrl は ws / wss だけを受ける", () => {
    expect(store({ CHATTER_AGENT_PLAYER_SERVER_URL: "ws://127.0.0.1:9999" }).get("playerServerUrl")).toBe(
      "ws://127.0.0.1:9999",
    );
    // http:// を書いてしまったら既定（空＝port/host から導出）へ倒す
    expect(store({ CHATTER_AGENT_PLAYER_SERVER_URL: "http://127.0.0.1:9999" }).get("playerServerUrl")).toBe("");
  });

  it("0 を意味のある値として受けるキーがある", () => {
    // synthesisLookahead: 0 = 完全直列、speechMaxAgeMs: 0 = 古さで飛ばさない、
    // ttsSpeakerId: 0 = VOICEVOX の先頭スタイル。parsePositiveInt だと全部弾かれる
    const s = store({
      CHATTER_AGENT_SYNTHESIS_LOOKAHEAD: "0",
      CHATTER_AGENT_SPEECH_MAX_AGE_MS: "0",
      CHATTER_AGENT_TTS_SPEAKER_ID: "0",
    });
    expect(s.get("synthesisLookahead")).toBe(0);
    expect(s.get("speechMaxAgeMs")).toBe(0);
    expect(s.get("ttsSpeakerId")).toBe(0);
  });

  it("負の値は既定値にフォールバックする", () => {
    write({ synthesisLookahead: -1, ttsSpeakerId: -1 });
    const s = store();
    expect(s.get("synthesisLookahead")).toBe(3);
    expect(s.get("ttsSpeakerId")).toBe(888753760);
  });

  it("playerArgs は環境変数のカンマ区切りと config.json の配列の両方を受ける", () => {
    expect(store({ CHATTER_AGENT_PLAYER_ARGS: "-q,1,{file}" }).get("playerArgs")).toEqual(["-q", "1", "{file}"]);
    write({ playerArgs: ["{file}", "--volume", "0.5"] });
    expect(store().get("playerArgs")).toEqual(["{file}", "--volume", "0.5"]);
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

describe("allowedOrigins（#E-1）", () => {
  it("既定は空配列（Origin付きの接続は全拒否のまま）", () => {
    expect(store().get("allowedOrigins")).toEqual([]);
  });

  it("config.json の配列を受け、要素をtrimして空要素を落とす", () => {
    write({ allowedOrigins: [" http://localhost:5173 ", "", "app://renderer"] });
    expect(store().get("allowedOrigins")).toEqual(["http://localhost:5173", "app://renderer"]);
  });

  it("環境変数はカンマ区切りで受け、要素をtrimして空要素を落とす", () => {
    const s = store({ CHATTER_AGENT_ALLOWED_ORIGINS: " http://localhost:5173 ,,app://renderer " });
    expect(s.get("allowedOrigins")).toEqual(["http://localhost:5173", "app://renderer"]);
  });

  it("文字列以外の要素が混じったら配列ごと既定値にフォールバックする", () => {
    write({ allowedOrigins: ["http://localhost:5173", 42] });
    expect(store().get("allowedOrigins")).toEqual([]);
  });

  it("配列でも文字列でもない値は既定値にフォールバックする", () => {
    write({ allowedOrigins: 42 });
    expect(store().get("allowedOrigins")).toEqual([]);
  });
});

describe("isSpeakDisabled（#4）", () => {
  it("1/true/yes/on で無効化する（大小文字と前後の空白は無視）", () => {
    for (const raw of ["1", "true", "TRUE", "yes", "on", " 1 ", "  True  "]) {
      expect(isSpeakDisabled({ CHATTER_AGENT_DISABLE: raw })).toBe(true);
    }
  });

  // ★ ここが #4 の本体。presence 判定のままだと「無効化の解除」のつもりで書いた 0 / false が
  //   逆に全発話を止める。診断も出ないので原因に辿り着けない
  it("0/false/no/off では無効化しない", () => {
    for (const raw of ["0", "false", "FALSE", "no", "off"]) {
      expect(isSpeakDisabled({ CHATTER_AGENT_DISABLE: raw })).toBe(false);
    }
  });

  it("未設定・空文字・未知の値では無効化しない", () => {
    expect(isSpeakDisabled({})).toBe(false);
    expect(isSpeakDisabled({ CHATTER_AGENT_DISABLE: "" })).toBe(false);
    expect(isSpeakDisabled({ CHATTER_AGENT_DISABLE: "maybe" })).toBe(false);
  });
});
