import { describe, it, expect, beforeEach, afterEach } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { DEFAULT_EMOTION_KEYWORDS } from "./defaultEmotionKeywords";
import { parseEmotionKeywords, readEmotionKeywords, writeDefaultEmotionKeywordsIfAbsent } from "./emotionKeywordsFile";

let dir: string;
let filePath: string;

beforeEach(() => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "chatter-agent-emotion-"));
  filePath = path.join(dir, "emotion-keywords.json");
});

afterEach(() => {
  fs.rmSync(dir, { recursive: true, force: true });
});

function collectWarnings(): { warn: (message: string) => void; messages: string[] } {
  const messages: string[] = [];
  return { warn: (message) => messages.push(message), messages };
}

describe("parseEmotionKeywords", () => {
  it("トップレベルが非オブジェクトなら警告して全部既定になる", () => {
    const { warn, messages } = collectWarnings();
    expect(parseEmotionKeywords("not an object", warn)).toEqual(DEFAULT_EMOTION_KEYWORDS);
    expect(messages).toHaveLength(1);
  });

  it("トップレベルが配列なら警告して全部既定になる", () => {
    const { warn, messages } = collectWarnings();
    expect(parseEmotionKeywords(["happy"], warn)).toEqual(DEFAULT_EMOTION_KEYWORDS);
    expect(messages).toHaveLength(1);
  });

  it("トップレベルが null なら警告して全部既定になる", () => {
    const { warn, messages } = collectWarnings();
    expect(parseEmotionKeywords(null, warn)).toEqual(DEFAULT_EMOTION_KEYWORDS);
    expect(messages).toHaveLength(1);
  });

  it("未知のキーは警告して無視し、他のキーは採用する", () => {
    const { warn, messages } = collectWarnings();
    const result = parseEmotionKeywords({ happy: ["わーい"], unknownEmotion: ["x"] }, warn);
    expect(result.happy).toEqual(["わーい"]);
    expect(result).not.toHaveProperty("unknownEmotion");
    expect(messages.some((m) => m.includes("unknownEmotion"))).toBe(true);
  });

  it('"neutral" は警告して無視する（未知キーとは別のメッセージ）', () => {
    const unknown = collectWarnings();
    parseEmotionKeywords({ someUnknownKey: ["x"] }, unknown.warn);

    const { warn, messages } = collectWarnings();
    const result = parseEmotionKeywords({ neutral: ["x"] }, warn);
    expect(result).toEqual(DEFAULT_EMOTION_KEYWORDS);
    expect(messages).toHaveLength(1);
    expect(messages[0]).not.toEqual(unknown.messages[0]);
  });

  it("値が配列でなければ警告してその感情だけ既定になる", () => {
    const { warn, messages } = collectWarnings();
    const result = parseEmotionKeywords({ happy: "not an array", angry: ["むかつく"] }, warn);
    expect(result.happy).toEqual(DEFAULT_EMOTION_KEYWORDS.happy);
    expect(result.angry).toEqual(["むかつく"]);
    expect(messages).toHaveLength(1);
  });

  it("要素に文字列以外が1つでもあれば警告してその感情だけ既定になる", () => {
    const { warn, messages } = collectWarnings();
    const result = parseEmotionKeywords({ sad: ["悲しい", 1, "つらい"] }, warn);
    expect(result.sad).toEqual(DEFAULT_EMOTION_KEYWORDS.sad);
    expect(messages).toHaveLength(1);
  });

  it("語数が上限を超えたら警告してその感情だけ既定になる", () => {
    const { warn, messages } = collectWarnings();
    const tooMany = Array.from({ length: 1001 }, (_, i) => `word${i}`);
    const result = parseEmotionKeywords({ relaxed: tooMany }, warn);
    expect(result.relaxed).toEqual(DEFAULT_EMOTION_KEYWORDS.relaxed);
    expect(messages).toHaveLength(1);
  });

  it("上限ちょうどなら警告なしで採用する", () => {
    const { warn, messages } = collectWarnings();
    const exactly = Array.from({ length: 1000 }, (_, i) => `word${i}`);
    const result = parseEmotionKeywords({ relaxed: exactly }, warn);
    expect(result.relaxed).toEqual(exactly);
    expect(messages).toHaveLength(0);
  });

  it("空文字・空白だけの要素は trim して落とす", () => {
    const { warn, messages } = collectWarnings();
    const result = parseEmotionKeywords({ happy: ["わーい", "", "   ", "やった"] }, warn);
    expect(result.happy).toEqual(["わーい", "やった"]);
    expect(messages).toHaveLength(0);
  });

  it("trim 後に空になる配列（[]）はそのまま採用する（既定とは区別する）", () => {
    const { warn, messages } = collectWarnings();
    const result = parseEmotionKeywords({ surprised: ["", "  "] }, warn);
    expect(result.surprised).toEqual([]);
    expect(messages).toHaveLength(0);
  });

  it("[] を直接指定してもそのまま採用する", () => {
    const { warn, messages } = collectWarnings();
    const result = parseEmotionKeywords({ surprised: [] }, warn);
    expect(result.surprised).toEqual([]);
    expect(messages).toHaveLength(0);
  });

  it("重複した要素は排除する", () => {
    const { warn, messages } = collectWarnings();
    const result = parseEmotionKeywords({ angry: ["むかつく", "むかつく", "怒"] }, warn);
    expect(result.angry).toEqual(["むかつく", "怒"]);
    expect(messages).toHaveLength(0);
  });

  it("キーが無ければ既定のまま", () => {
    const { warn, messages } = collectWarnings();
    const result = parseEmotionKeywords({ happy: ["わーい"] }, warn);
    expect(result.angry).toEqual(DEFAULT_EMOTION_KEYWORDS.angry);
    expect(result.sad).toEqual(DEFAULT_EMOTION_KEYWORDS.sad);
    expect(result.relaxed).toEqual(DEFAULT_EMOTION_KEYWORDS.relaxed);
    expect(result.surprised).toEqual(DEFAULT_EMOTION_KEYWORDS.surprised);
    expect(messages).toHaveLength(0);
  });

  it("置き換えの合成: angry だけ書けば angry は置き換わり他の4感情は既定のまま", () => {
    const { warn } = collectWarnings();
    const result = parseEmotionKeywords({ angry: ["ぷんぷん"] }, warn);
    expect(result.angry).toEqual(["ぷんぷん"]);
    expect(result.happy).toEqual(DEFAULT_EMOTION_KEYWORDS.happy);
    expect(result.sad).toEqual(DEFAULT_EMOTION_KEYWORDS.sad);
    expect(result.relaxed).toEqual(DEFAULT_EMOTION_KEYWORDS.relaxed);
    expect(result.surprised).toEqual(DEFAULT_EMOTION_KEYWORDS.surprised);
  });
});

describe("readEmotionKeywords", () => {
  it("ファイルが無ければ既定を返す（警告なし）", () => {
    const { warn, messages } = collectWarnings();
    expect(readEmotionKeywords(filePath, warn)).toEqual(DEFAULT_EMOTION_KEYWORDS);
    expect(messages).toHaveLength(0);
  });

  it("書き出したファイルを読み戻すと既定と一致する（ラウンドトリップ）", () => {
    writeDefaultEmotionKeywordsIfAbsent(filePath);
    const { warn, messages } = collectWarnings();
    expect(readEmotionKeywords(filePath, warn)).toEqual(DEFAULT_EMOTION_KEYWORDS);
    expect(messages).toHaveLength(0);
  });

  it("JSON が壊れていれば警告して既定を返す", () => {
    fs.writeFileSync(filePath, "{ 壊れている");
    const { warn, messages } = collectWarnings();
    expect(readEmotionKeywords(filePath, warn)).toEqual(DEFAULT_EMOTION_KEYWORDS);
    expect(messages).toHaveLength(1);
  });

  it("トップレベルが配列なら警告して既定を返す", () => {
    fs.writeFileSync(filePath, JSON.stringify(["happy"]));
    const { warn, messages } = collectWarnings();
    expect(readEmotionKeywords(filePath, warn)).toEqual(DEFAULT_EMOTION_KEYWORDS);
    expect(messages).toHaveLength(1);
  });

  it("angry だけ書いたファイルは angry だけ置き換わり他の4感情は既定のまま", () => {
    fs.writeFileSync(filePath, JSON.stringify({ angry: ["ぷんぷん"] }));
    const { warn, messages } = collectWarnings();
    const result = readEmotionKeywords(filePath, warn);
    expect(result.angry).toEqual(["ぷんぷん"]);
    expect(result.happy).toEqual(DEFAULT_EMOTION_KEYWORDS.happy);
    expect(result.sad).toEqual(DEFAULT_EMOTION_KEYWORDS.sad);
    expect(result.relaxed).toEqual(DEFAULT_EMOTION_KEYWORDS.relaxed);
    expect(result.surprised).toEqual(DEFAULT_EMOTION_KEYWORDS.surprised);
    expect(messages).toHaveLength(0);
  });
});

describe("writeDefaultEmotionKeywordsIfAbsent", () => {
  it("親ディレクトリが無くても作って書き出す", () => {
    const nested = path.join(dir, "nested", "emotion-keywords.json");
    writeDefaultEmotionKeywordsIfAbsent(nested);
    expect(fs.existsSync(nested)).toBe(true);
  });

  it("既存ファイルは上書きされない（内容が変わらない）", () => {
    const custom = JSON.stringify({ happy: ["カスタム"] });
    fs.writeFileSync(filePath, custom);
    writeDefaultEmotionKeywordsIfAbsent(filePath);
    expect(fs.readFileSync(filePath, "utf-8")).toBe(custom);
  });

  it("書き出した JSON のキー順が happy, angry, sad, relaxed, surprised である", () => {
    writeDefaultEmotionKeywordsIfAbsent(filePath);
    const parsed = JSON.parse(fs.readFileSync(filePath, "utf-8"));
    expect(Object.keys(parsed)).toEqual(["happy", "angry", "sad", "relaxed", "surprised"]);
  });

  it("末尾に改行を付ける", () => {
    writeDefaultEmotionKeywordsIfAbsent(filePath);
    expect(fs.readFileSync(filePath, "utf-8").endsWith("\n")).toBe(true);
  });

  it("書き込み先が読み取り専用でも失敗を握り潰す", () => {
    const readonlyDir = path.join(dir, "readonly");
    fs.mkdirSync(readonlyDir);
    fs.chmodSync(readonlyDir, 0o500);
    const target = path.join(readonlyDir, "sub", "emotion-keywords.json");
    try {
      expect(() => writeDefaultEmotionKeywordsIfAbsent(target)).not.toThrow();
      expect(fs.existsSync(target)).toBe(false);
    } finally {
      fs.chmodSync(readonlyDir, 0o700);
    }
  });
});
