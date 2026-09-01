import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import {
  configKeys,
  createConfigStore,
  createDefaultConfig,
  isConfigKey,
  isSpeakDisabled,
  parseConfigValue,
} from "./config";
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

  ttsEnabled: true,
  ttsBaseUrl: "http://127.0.0.1:10101",
  ttsSpeakerId: 888753760,
  ttsSpeedScale: 1.0,
  synthesisTimeoutMs: 30_000,
  ttsSpawn: true,
  ttsSpawnCommand: "",
  ttsSpawnArgs: [],

  synthesisLookahead: 3,
  audioFetchTimeoutMs: 45_000,
  playerCommand: "afplay",
  playerArgs: ["{file}"],
  playerServerUrl: "",
  speechMaxAgeMs: 0,

  aiSummaryEnabled: false,
  aiSummaryThreshold: 200,
  aiSummaryCommand: "claude",
  aiSummaryModel: "haiku",
  aiSummaryTimeoutMs: 60_000,
  aiSummaryMaxPerDrain: 3,
};

function store(env: NodeJS.ProcessEnv = {}) {
  return createConfigStore({ filePath, env, defaults: DEFAULTS });
}

/**
 * ★ `DEFAULTS` は `store()` に注入している値なので、それとの比較は同語反復になる。
 *   本番の既定値が動いたことを検出できるのはこのテストだけ。
 *   ここが緑なら `docs/core.md` の表とコミット済みバンドルも同じ値である、という関係にする
 */
it("テストの DEFAULTS が本番の既定値と一致している", () => {
  expect(createDefaultConfig()).toEqual(DEFAULTS);
});

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

  it("ttsSpawn は 0 / false / off で切れる（verify-phase-b が渡す形）", () => {
    expect(store().get("ttsSpawn")).toBe(true);
    expect(store({ CHATTER_AGENT_TTS_SPAWN: "0" }).get("ttsSpawn")).toBe(false);
    expect(store({ CHATTER_AGENT_TTS_SPAWN: "false" }).get("ttsSpawn")).toBe(false);
    expect(store({ CHATTER_AGENT_TTS_SPAWN: "off" }).get("ttsSpawn")).toBe(false);
  });

  it("ttsSpawnCommand は環境変数と config.json の両方から読める", () => {
    expect(store({ CHATTER_AGENT_TTS_SPAWN_COMMAND: "/opt/aivis/run" }).get("ttsSpawnCommand")).toBe("/opt/aivis/run");
    write({ ttsSpawnCommand: "~/bin/run" });
    expect(store().get("ttsSpawnCommand")).toBe("~/bin/run");
  });

  /**
   * ★ `parsePlayerArgs` に差し替えられたら落ちる回帰テスト。あれは `{file}` を含まない列と
   *   空入力を弾くが、`ttsSpawnArgs` では**空に「ttsBaseUrl から導出する」意味がある**。
   */
  it("ttsSpawnArgs は {file} を含まない引数列を受け、空なら [] になる", () => {
    expect(store({ CHATTER_AGENT_TTS_SPAWN_ARGS: "--use_gpu,--load_all_models" }).get("ttsSpawnArgs")).toEqual([
      "--use_gpu",
      "--load_all_models",
    ]);
    expect(store({ CHATTER_AGENT_TTS_SPAWN_ARGS: "" }).get("ttsSpawnArgs")).toEqual([]);
    write({ ttsSpawnArgs: ["--host", "127.0.0.1", "--port", "10101"] });
    expect(store().get("ttsSpawnArgs")).toEqual(["--host", "127.0.0.1", "--port", "10101"]);
  });

  it("未知のキーは無視して警告する", () => {
    write({ port: 9000, totallyUnknownKey: true });
    const s = store();
    expect(s.get("port")).toBe(9000);
    expect(console.warn).toHaveBeenCalledWith(expect.stringContaining("totallyUnknownKey"));
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

describe("aiSummary*（#31）", () => {
  it("既定はOFF・閾値200・claude/haiku・タイムアウト60秒・1ドレイン3件まで", () => {
    const c = createDefaultConfig();
    expect(c.aiSummaryEnabled).toBe(false);
    expect(c.aiSummaryThreshold).toBe(200);
    expect(c.aiSummaryCommand).toBe("claude");
    expect(c.aiSummaryModel).toBe("haiku");
    expect(c.aiSummaryTimeoutMs).toBe(60_000);
    expect(c.aiSummaryMaxPerDrain).toBe(3);
  });

  it("config.json から読める", () => {
    write({
      aiSummaryEnabled: true,
      aiSummaryThreshold: 100,
      aiSummaryCommand: "/usr/local/bin/claude",
      aiSummaryModel: "sonnet",
      aiSummaryTimeoutMs: 5000,
      aiSummaryMaxPerDrain: 1,
    });
    const s = store();
    expect(s.get("aiSummaryEnabled")).toBe(true);
    expect(s.get("aiSummaryThreshold")).toBe(100);
    expect(s.get("aiSummaryCommand")).toBe("/usr/local/bin/claude");
    expect(s.get("aiSummaryModel")).toBe("sonnet");
    expect(s.get("aiSummaryTimeoutMs")).toBe(5000);
    expect(s.get("aiSummaryMaxPerDrain")).toBe(1);
  });

  it("環境変数からも読める", () => {
    const s = store({
      CHATTER_AGENT_AI_SUMMARY_ENABLED: "true",
      CHATTER_AGENT_AI_SUMMARY_THRESHOLD: "150",
      CHATTER_AGENT_AI_SUMMARY_COMMAND: "/opt/claude",
      CHATTER_AGENT_AI_SUMMARY_MODEL: "opus",
      CHATTER_AGENT_AI_SUMMARY_TIMEOUT_MS: "10000",
      CHATTER_AGENT_AI_SUMMARY_MAX_PER_DRAIN: "5",
    });
    expect(s.get("aiSummaryEnabled")).toBe(true);
    expect(s.get("aiSummaryThreshold")).toBe(150);
    expect(s.get("aiSummaryCommand")).toBe("/opt/claude");
    expect(s.get("aiSummaryModel")).toBe("opus");
    expect(s.get("aiSummaryTimeoutMs")).toBe(10_000);
    expect(s.get("aiSummaryMaxPerDrain")).toBe(5);
  });

  // ★ ここが回帰すると `--model ""` がそのまま要約 CLI に渡って壊れる。空文字は
  //   「--model を渡さず CLI 自身の既定モデルに従う」という意味を持つ有効値であって、
  //   不正値ではないので既定値の "haiku" へ倒してはいけない（parseNonEmptyString と混同しないこと）
  it("★ aiSummaryModel に空文字を渡すと空文字のまま通る（既定値 haiku に倒れない）", () => {
    write({ aiSummaryModel: "" });
    expect(store().get("aiSummaryModel")).toBe("");
    expect(store({ CHATTER_AGENT_AI_SUMMARY_MODEL: "" }).get("aiSummaryModel")).toBe("");
  });

  it("aiSummaryModel は前後の空白を trim する", () => {
    expect(store({ CHATTER_AGENT_AI_SUMMARY_MODEL: " sonnet " }).get("aiSummaryModel")).toBe("sonnet");
  });

  it("aiSummaryThreshold が0や負値なら既定値に倒れる", () => {
    write({ aiSummaryThreshold: 0 });
    expect(store().get("aiSummaryThreshold")).toBe(200);
    write({ aiSummaryThreshold: -1 });
    expect(store().get("aiSummaryThreshold")).toBe(200);
  });

  it("aiSummaryMaxPerDrain が0や負値なら既定値に倒れる", () => {
    write({ aiSummaryMaxPerDrain: 0 });
    expect(store().get("aiSummaryMaxPerDrain")).toBe(3);
    write({ aiSummaryMaxPerDrain: -1 });
    expect(store().get("aiSummaryMaxPerDrain")).toBe(3);
  });

  // ★ issue #38 レビュー G1-b。上限が無いと aiSummaryMaxPerDrain: 1000000 が素通りし、
  //   aiSummaryTimeoutMs（既定60秒）× N でロック保持時間が際限なく伸びる。
  //   上限8は workerState.ts の SUMMARIZER_SESSION_LIMIT（64）とも連動している値なので、
  //   ここが緑である限り両者の対応が崩れていないことも見ている
  it("aiSummaryMaxPerDrain の上限は8。超えたら既定値に倒れ、8はそのまま通る", () => {
    write({ aiSummaryMaxPerDrain: 9 });
    expect(store().get("aiSummaryMaxPerDrain")).toBe(3);
    write({ aiSummaryMaxPerDrain: 1_000_000 });
    expect(store().get("aiSummaryMaxPerDrain")).toBe(3);
    write({ aiSummaryMaxPerDrain: 8 });
    expect(store().get("aiSummaryMaxPerDrain")).toBe(8);
  });

  it("aiSummaryTimeoutMs が MAX_TIMER_MS（2^31-1）を超えると既定値に倒れる", () => {
    // setTimeout / AbortSignal.timeout の上限を超えると静かに壊れる（→ parseTimeoutMs のコメント）
    write({ aiSummaryTimeoutMs: 2_147_483_648 });
    expect(store().get("aiSummaryTimeoutMs")).toBe(60_000);
  });

  it("aiSummaryEnabled は既存の真偽値パーサと同じトークンを受ける", () => {
    expect(store({ CHATTER_AGENT_AI_SUMMARY_ENABLED: "1" }).get("aiSummaryEnabled")).toBe(true);
    expect(store({ CHATTER_AGENT_AI_SUMMARY_ENABLED: "off" }).get("aiSummaryEnabled")).toBe(false);
  });
});

describe("ttsSpeedScale（#76）", () => {
  /**
   * ★ このファイルで**小数を受ける最初のキー**。`toInt` は `Number.isInteger` 縛りなので
   *   流用できず、専用のパーサ（`makeRangeParser`）が要った
   */
  it("★ 小数を受ける", () => {
    write({ ttsSpeedScale: 1.5 });
    expect(store().get("ttsSpeedScale")).toBe(1.5);
    expect(store({ CHATTER_AGENT_TTS_SPEED_SCALE: "0.7" }).get("ttsSpeedScale")).toBe(0.7);
  });

  it("範囲は 0.5〜2.0。外れたら既定値（1.0）に倒れる", () => {
    write({ ttsSpeedScale: 0.5 });
    expect(store().get("ttsSpeedScale")).toBe(0.5);
    touchFuture();
    write({ ttsSpeedScale: 2.0 });
    expect(store().get("ttsSpeedScale")).toBe(2.0);
    write({ ttsSpeedScale: 0.4 });
    expect(store().get("ttsSpeedScale")).toBe(1.0);
    write({ ttsSpeedScale: 2.1 });
    expect(store().get("ttsSpeedScale")).toBe(1.0);
  });

  /**
   * ★ `Number("")` も `Number(" ")` も `Number(null)` も `0` になる。`toInt` では
   *   `Number.isInteger(NaN)` が弾いていた分を、こちらは明示的に落とす必要がある
   */
  it("★ 空文字・空白・非数は既定値に倒れる（0 として通さない）", () => {
    expect(store({ CHATTER_AGENT_TTS_SPEED_SCALE: "" }).get("ttsSpeedScale")).toBe(1.0);
    expect(store({ CHATTER_AGENT_TTS_SPEED_SCALE: " " }).get("ttsSpeedScale")).toBe(1.0);
    expect(store({ CHATTER_AGENT_TTS_SPEED_SCALE: "はやい" }).get("ttsSpeedScale")).toBe(1.0);
    write({ ttsSpeedScale: null });
    expect(store().get("ttsSpeedScale")).toBe(1.0);
  });
});

describe("制御 API 用の口（#76）", () => {
  it("isConfigKey は SPECS のキーだけを通す", () => {
    expect(isConfigKey("ttsSpeedScale")).toBe(true);
    expect(isConfigKey("nope")).toBe(false);
    // ★ Object.prototype 由来の名前を通さない
    expect(isConfigKey("constructor")).toBe(false);
    expect(isConfigKey("__proto__")).toBe(false);
  });

  it("configKeys は ChatterAgentConfig の全キーを返す", () => {
    expect([...configKeys()].sort()).toEqual(Object.keys(createDefaultConfig()).sort());
  });

  /** ★ HTTP 側にパーサを複製しないための口。`SPECS` のパーサそのものを通す */
  it("★ parseConfigValue は SPECS のパーサそのもの", () => {
    expect(parseConfigValue("ttsSpeedScale", "1.5")).toBe(1.5);
    expect(parseConfigValue("ttsSpeedScale", 9)).toBeUndefined();
    expect(parseConfigValue("ttsBaseUrl", "http://h:1/")).toBe("http://h:1");
    expect(parseConfigValue("ttsBaseUrl", "localhost:10101")).toBeUndefined();
  });

  describe("originOf", () => {
    it("既定値のままなら default", () => {
      expect(store().originOf("ttsSpeakerId")).toBe("default");
    });

    it("ファイルに書かれていれば file", () => {
      write({ ttsSpeakerId: 3 });
      expect(store().originOf("ttsSpeakerId")).toBe("file");
    });

    it("環境変数が勝っていれば env", () => {
      write({ ttsSpeakerId: 3 });
      expect(store({ CHATTER_AGENT_TTS_SPEAKER_ID: "5" }).originOf("ttsSpeakerId")).toBe("env");
    });

    /**
     * ★ 「値が既定値と同じかどうか」では判定できない。既定値と同じ値が明示的に
     *   書かれていることは普通にある
     */
    it("★ 既定値と同じ値が書かれていても file", () => {
      write({ ttsSpeakerId: 888753760 });
      expect(store().originOf("ttsSpeakerId")).toBe("file");
    });

    /** 不正値は採用されないので、出どころも既定に戻る */
    it("不正な値なら default（採用されていないので）", () => {
      write({ ttsSpeakerId: -1 });
      expect(store().originOf("ttsSpeakerId")).toBe("default");
    });
  });

  describe("readRawFile", () => {
    it("ファイルが無ければ空のベースを返す", () => {
      expect(store().readRawFile()).toEqual({});
    });

    /** ★★ `collect()` を通さない。未知キーが残ることがこの関数の存在理由 */
    it("★★ 未知のキーもそのまま返す", () => {
      write({ ttsSpeakerId: 3, futureKey: "残す" });
      expect(store().readRawFile()).toEqual({ ttsSpeakerId: 3, futureKey: "残す" });
    });

    /**
     * ★★ 壊れているときに `{}` を返さないこと。返すと `PATCH` が
     *   ユーザーのファイルを丸ごと書き潰す
     */
    it("★★ 壊れた JSON では undefined（空オブジェクトではない）", () => {
      write("{ 壊れている");
      expect(store().readRawFile()).toBeUndefined();
    });

    it("トップレベルが配列なら undefined", () => {
      write([1, 2, 3]);
      expect(store().readRawFile()).toBeUndefined();
    });
  });
});
