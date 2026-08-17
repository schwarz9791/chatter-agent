import { describe, it, expect } from "vitest";
import { toSpeechSentences } from "./speechText";

/**
 * `cli/messageAssembler.test.ts` が `assembleSentences`（1本の adapter）経由でこの関数の
 * 挙動を厚くカバーしているので、ここでは `toSpeechSentences` 直呼びとして最低限だけ見る。
 */
describe("toSpeechSentences", () => {
  it("全文を1文ずつに割って返す", () => {
    expect(toSpeechSentences("確認します。ログを見ます。")).toEqual(["確認します。", "ログを見ます。"]);
  });

  it("空文字では何も返さない", () => {
    expect(toSpeechSentences("")).toEqual([]);
  });

  it("dropUnterminatedTail が true なら、文として閉じていない末尾を落とす", () => {
    expect(toSpeechSentences("Aの一文目。Aの途中", { dropUnterminatedTail: true })).toEqual(["Aの一文目。"]);
  });

  it("dropUnterminatedTail を渡さなければ、閉じていない末尾もそのまま出す", () => {
    expect(toSpeechSentences("Aの一文目。Aの途中")).toEqual(["Aの一文目。", "Aの途中"]);
  });

  it("未閉じのコードフェンスは読み上げに漏れない（truncateAtUnstableTail 経由）", () => {
    expect(toSpeechSentences("説明します。\n```ts\nconst secret = 1;")).toEqual(["説明します。"]);
  });

  // ★ この関数を発話に載る値に2回通してはいけない、という docstring の根拠を固定するテスト。
  //   PR #38 レビューの実測（`|a|` は1パス目で `|a|` のまま受理され、2パス目で空文字列になる）
  //   をそのまま再現する
  it("★ 冪等ではない。バッククォートで囲まれた表構文は2パス目で消える（発話に載る値には1回だけ適用すること）", () => {
    const once = toSpeechSentences("`|a|`");
    expect(once).toEqual(["|a|"]);

    const twice = toSpeechSentences(once.join("\n"));
    expect(twice).toEqual([]);
  });
});
