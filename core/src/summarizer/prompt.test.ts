import { describe, it, expect } from "vitest";
import {
  SUMMARY_CHARS_MAX,
  SUMMARY_CHARS_MIN,
  SUMMARY_RATIO,
  SUMMARY_SENTENCES_MAX,
  SUMMARY_SENTENCES_MIN,
  SUMMARY_WORDS_MAX,
  SUMMARY_WORDS_MIN,
  SUMMARY_WORDS_PER_SENTENCE,
  buildSummaryInstruction,
  countWords,
  summaryLengthLimit,
  wrapSummaryInput,
} from "./prompt";

describe("summaryLengthLimit", () => {
  it("仮名を含む原文は文字数（chars）で数える", () => {
    expect(summaryLengthLimit("これはひらがなを含む文章です").unit).toBe("chars");
    expect(summaryLengthLimit("カタカナだけの文章").unit).toBe("chars");
  });

  it("仮名を含まない英語の原文は語数（words）で数える", () => {
    expect(summaryLengthLimit("This is an English sentence.").unit).toBe("words");
  });

  it("漢字だけの中国語相当の原文は仮名が無いので語数（words）で数える（漢字は判定に使わない）", () => {
    expect(summaryLengthLimit("这是一个中文句子没有假名").unit).toBe("words");
  });

  it("英単語が混じっていても仮名があれば文字数（chars）で数える", () => {
    expect(summaryLengthLimit("これは API を呼び出す処理です").unit).toBe("chars");
  });

  it("chars: 下限を下回る原文長は下限にクランプする", () => {
    expect(summaryLengthLimit("あ").max).toBe(SUMMARY_CHARS_MIN);
  });

  it("chars: 下限と上限の間では比率どおりに計算する", () => {
    const text = "あ".repeat(1000);
    expect(summaryLengthLimit(text).max).toBe(Math.round(1000 * SUMMARY_RATIO));
  });

  it("chars: 上限を超える原文長は上限にクランプする", () => {
    const text = "あ".repeat(100_000);
    expect(summaryLengthLimit(text).max).toBe(SUMMARY_CHARS_MAX);
  });

  it("words: 下限を下回る語数は下限にクランプする", () => {
    expect(summaryLengthLimit("one word").max).toBe(SUMMARY_WORDS_MIN);
    expect(summaryLengthLimit("").max).toBe(SUMMARY_WORDS_MIN);
  });

  it("words: 下限と上限の間では比率どおりに計算する", () => {
    const wordCount = 200;
    const text = Array.from({ length: wordCount }, (_, i) => `word${i}`).join(" ");
    expect(summaryLengthLimit(text).max).toBe(Math.round(wordCount * SUMMARY_RATIO));
  });

  it("words: 上限を超える語数は上限にクランプする", () => {
    const text = Array.from({ length: 10_000 }, (_, i) => `word${i}`).join(" ");
    expect(summaryLengthLimit(text).max).toBe(SUMMARY_WORDS_MAX);
  });

  it("sentences は下限を下回らない（chars）", () => {
    expect(summaryLengthLimit("あ").sentences).toBe(SUMMARY_SENTENCES_MIN);
  });

  it("sentences は上限を超えない（chars、上限文字数のとき）", () => {
    expect(summaryLengthLimit("あ".repeat(100_000)).sentences).toBe(SUMMARY_SENTENCES_MAX);
  });

  it("sentences は下限を下回らない（words）", () => {
    expect(summaryLengthLimit("one word").sentences).toBe(SUMMARY_SENTENCES_MIN);
  });

  it("sentences は上限語数を1文あたりの語数で割った数になり、上限を超えない（words）", () => {
    const text = Array.from({ length: 10_000 }, (_, i) => `word${i}`).join(" ");
    const { sentences } = summaryLengthLimit(text);
    expect(sentences).toBe(Math.round(SUMMARY_WORDS_MAX / SUMMARY_WORDS_PER_SENTENCE));
    expect(sentences).toBeLessThanOrEqual(SUMMARY_SENTENCES_MAX);
  });
});

describe("countWords", () => {
  it("空白区切りで語数を数える", () => {
    expect(countWords("one two three")).toBe(3);
  });

  it("空文字は0語", () => {
    expect(countWords("")).toBe(0);
    expect(countWords("   ")).toBe(0);
  });
});

describe("buildSummaryInstruction", () => {
  const charsLimit = { unit: "chars" as const, max: 150, sentences: 3 };
  const wordsLimit = { unit: "words" as const, max: 40, sentences: 4 };

  it("chars のときは上限値と characters を含む", () => {
    const instruction = buildSummaryInstruction(charsLimit);
    expect(instruction).toContain("150 characters");
    expect(instruction).toContain("at most 3 sentences");
  });

  it("words のときは上限値と words を含む", () => {
    const instruction = buildSummaryInstruction(wordsLimit);
    expect(instruction).toContain("40 words");
    expect(instruction).toContain("at most 4 sentences");
  });

  it("原文と同じ言語で要約させる指示を含み、日本語に固定しない", () => {
    expect(buildSummaryInstruction(charsLimit)).toContain("same language as the original");
    expect(buildSummaryInstruction(charsLimit)).not.toContain("日本語");
  });

  it("一人称で書き手の声のまま話すことを求める", () => {
    expect(buildSummaryInstruction(charsLimit)).toContain("first person");
  });

  /**
   * ★ Must remove 側は「Markdown decoration」「quotation marks」「emoji」とだけ書き、句読点扱いの `!` `?` を
   *   含めない。感情判定（`emotion/ruleBasedEmotionClassifier.ts` の `sentenceEndPatterns`）は
   *   ほぼ全部が ！ / ？ / … / ♪ / 絵文字なので、句読点の `!` `?` まで除去対象にすると、
   *   モデルが指示に従うほど長い成功報告や謝罪が neutral に潰れる。
   */
  it("★ Must remove の行が「!」「?」を対象に含まない（句読点として使ってよい旨と矛盾しないこと）", () => {
    const instruction = buildSummaryInstruction(charsLimit);
    const mustRemoveSection = instruction.split("## Must remove")[1]!.split("## Formatting")[0]!;
    expect(mustRemoveSection).not.toContain("!");
    expect(mustRemoveSection).not.toContain("?");
    expect(instruction).toContain("`!` and `?` are punctuation");
  });

  it("原文より長くしないことを求める", () => {
    expect(buildSummaryInstruction(charsLimit)).toContain("Never make it longer than the original");
  });

  it("肯定・否定の反転や疑問文と平叙文の入れ替えを禁じる", () => {
    const instruction = buildSummaryInstruction(charsLimit);
    expect(instruction).toContain("do not flip positive/negative statements");
    expect(instruction).toContain("do not turn questions into statements");
  });

  it("感情とトーンのニュアンスを保つことを求める", () => {
    expect(buildSummaryInstruction(charsLimit)).toContain("emotional nuance and tone");
  });

  it("数字はぼかしてよいが、原文と違う数字は言い切らないことを求める", () => {
    expect(buildSummaryInstruction(charsLimit)).toContain("Numbers may be rounded off");
    expect(buildSummaryInstruction(charsLimit)).toContain("never state a different number");
  });

  it("原文に無い情報を足さないことを求める", () => {
    expect(buildSummaryInstruction(charsLimit)).toContain("Do not add information that isn't in the original");
  });

  it("原文が AI コーディングアシスタントの発言であるという前提を含む", () => {
    expect(buildSummaryInstruction(charsLimit)).toContain("AI coding assistant");
  });

  it("原文は <text> タグの中で渡す旨を含み、原文自体は埋め込まない", () => {
    expect(buildSummaryInstruction(charsLimit)).toContain("<text>");
  });
});

describe("wrapSummaryInput", () => {
  it("原文を <text> タグで囲む", () => {
    expect(wrapSummaryInput("こんにちは")).toBe("<text>\nこんにちは\n</text>");
  });
});
