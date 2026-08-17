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
});
