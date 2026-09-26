import { describe, it, expect } from "vitest";
import { SUMMARY_INSTRUCTION, SUMMARY_MAX_CHARS } from "./prompt";

describe("SUMMARY_INSTRUCTION", () => {
  it("SUMMARY_MAX_CHARS の値を実際に含む（プロンプトの文言と定数がズレていないこと）", () => {
    expect(SUMMARY_INSTRUCTION).toContain(`${SUMMARY_MAX_CHARS}文字以内`);
  });

  it("★ 禁止リストの行が「記号」を単独では含まない（！／？を残せという口調ルールと矛盾しないこと）", () => {
    // 感情判定（emotion/ruleBasedEmotionClassifier.ts の sentenceEndPatterns）はほぼ全部が
    // ！ / ？ / … / ♪ / 絵文字。「記号を含めるな」と「！を残せ」が両方書いてあると、
    // モデルがどちらかにしか従えず、句読点扱いの ！ ？ まで削られて成功報告や謝罪が
    // neutral に潰れる（VRM が感情に反応しなくなる）。禁止リストの行から
    // 「記号」という語そのものが消えていることを見る（"Markdown記法" は "記号" の
    // 部分文字列ではないので誤検知しない）。
    const negativeLine = SUMMARY_INSTRUCTION.split("\n").find((line) => line.includes("含めない"));
    expect(negativeLine).toBeDefined();
    if (!negativeLine) return;
    expect(negativeLine).not.toContain("記号");
  });

  it("原文に近い口語調を求め、です・ます調を固定しない", () => {
    expect(SUMMARY_INSTRUCTION).toContain("原文に近い口語調");
    expect(SUMMARY_INSTRUCTION).not.toContain("です・ます調");
  });

  it("発言の主体と依頼の向きを原文のまま保つことを含む", () => {
    expect(SUMMARY_INSTRUCTION).toContain("発言の主体（誰が）と依頼の向き（誰に）を原文のまま保つ");
  });

  it("肯定・否定の反転を禁じる", () => {
    expect(SUMMARY_INSTRUCTION).toContain("原文の肯定・否定を反転させない");
  });

  it("数字はぼかすのは可だが、原文と違う数字の言い切りは禁じる", () => {
    const line = SUMMARY_INSTRUCTION.split("\n").find((l) => l.includes("数字"));
    expect(line).toBeDefined();
    expect(line).toContain("ぼかしてよい");
    expect(line).toContain("原文と違う数字を言い切らない");
  });

  it("原文に無い内容を足すことと、いちばん伝えたい内容を落とすことを禁じる", () => {
    expect(SUMMARY_INSTRUCTION).toContain("原文に書かれていない内容を付け加えないこと");
    expect(SUMMARY_INSTRUCTION).toContain("原文でいちばん伝えたい内容を省略しないこと");
  });
});
