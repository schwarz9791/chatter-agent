import { describe, it, expect } from "vitest";
import { assembleSentences } from "./messageAssembler";

/** `final:true` を見たワーカーが渡すのと同じ形（メッセージ全文の delta 列） */
const speak = (deltas: string[]) => assembleSentences(deltas);

describe("assembleSentences", () => {
  it("メッセージ全文を1文ずつに割って返す", () => {
    expect(speak(["確認します。ログを見ます。"])).toEqual(["確認します。", "ログを見ます。"]);
  });

  it("文が1つしか無くても出す（保留はしない）", () => {
    expect(speak(["確認します。"])).toEqual(["確認します。"]);
  });

  it("句点で終わっていなくても出す（もう続きは来ない）", () => {
    expect(speak(["Aの一文目。Aの途中"])).toEqual(["Aの一文目。", "Aの途中"]);
  });

  it("空の delta では何も出さない", () => {
    expect(speak([])).toEqual([]);
    expect(speak([""])).toEqual([]);
  });
});

describe("チャンク境界が文の途中で切れても壊れない（設計書 §2-4）", () => {
  it("文の途中で切れた delta を跨いで1文にまとめる", () => {
    expect(speak(["確認し", "ます。ログを", "見ます。おわり。"])).toEqual([
      "確認します。",
      "ログを見ます。",
      "おわり。",
    ]);
  });

  it("改行だけの delta が混ざっても文が割れない", () => {
    expect(speak(["1つ目。\n", "\n2つ目。\n", "\n3つ目。"])).toEqual(["1つ目。", "2つ目。", "3つ目。"]);
  });

  it("final の delta が空でも、それまでの全文が出る", () => {
    // メッセージが改行で終わると final の delta は空で届く（docs/plugin.md）
    expect(speak(["確認します。\n", "ログを見ます。\n", ""])).toEqual(["確認します。", "ログを見ます。"]);
  });
});

describe("読み上げたくない末尾は捨てる", () => {
  it("閉じたコードブロックは中身ごと消える", () => {
    const total = speak(["説明します。\n```ts\n", "const a = 1;\n", "```\n以上です。"]);
    expect(total).toEqual(["説明します。", "以上です。"]);
    expect(total.join("")).not.toContain("const a");
  });

  it("★ フェンスが閉じないまま終わったら、コードは出さない", () => {
    expect(speak(["説明します。\n```ts\n", "const secret = 1;"])).toEqual(["説明します。"]);
  });

  it("閉じた表の行は消え、手前と後ろの文は残る", () => {
    const total = speak(["手順です。\n| A | B |\n| C | D", " |\n完了です。"]);
    expect(total).toEqual(["手順です。", "完了です。"]);
    expect(total.join("")).not.toContain("|");
  });

  it("★ 表の行が閉じないまま終わったら、生の行を読み上げない", () => {
    expect(speak(["手順です。\n| A | B |\n| C | D"])).toEqual(["手順です。"]);
  });
});

/**
 * ストリーミング中だけ効いていた保留（未閉じの `<` / インラインコード / URL / 16進）は
 * [#30] で落とした。`final` を待つので「後から届いて既出範囲を変える」ことが起きえない。
 *
 * 残っているのは `cleanTextForSpeech` 側の未対応（下の「既知の欠落」）だけで、
 * 挙動としては旧実装の `final:true` 経路と同じ。
 *
 * [#30]: https://github.com/schwarz9791/chatter-agent/issues/30
 */
describe("未閉じの `<` やインラインコードは保留しない（final 待ちなので伸びない）", () => {
  it("`>` が無い `<` はそのまま読み上げる", () => {
    expect(speak(["条件は a < b です。まず確認します。"])).toEqual(["条件は a < b です。", "まず確認します。"]);
  });

  it("閉じていないインラインコードもそのまま読み上げる", () => {
    expect(speak(["実行は `npm run build"])).toEqual(["実行は `npm run build"]);
  });
});

describe("実測ログに近い流れ", () => {
  it("大きく遅れて届く final で、メッセージ全文が一括で出る", () => {
    // 設計書 §2-4 の 7e2d4582 に近い形（最終チャンクだけ大きく遅れる）
    const deltas = [
      "完全に判明しました。ストリーミングでテキストが流れてきます。\n\n",
      "対照の `PostToolUse` も2件出たので、設定が読み込まれたことは確定です。\n\n",
      "3点わかりました。1つの `message_id` に対し index が振られます。",
      "これは実装方針に関わる分岐があるので確認させてください。",
    ];

    expect(speak(deltas)).toEqual([
      "完全に判明しました。",
      "ストリーミングでテキストが流れてきます。",
      "対照の PostToolUse も2件出たので、設定が読み込まれたことは確定です。",
      "3点わかりました。",
      "1つの message_id に対し index が振られます。",
      "これは実装方針に関わる分岐があるので確認させてください。",
    ]);
  });
});

describe("防御", () => {
  it("見出し・リストマーカー・URL は除去済みの文になる", () => {
    expect(speak(["## 見出し\n- 箇条書きです。詳細は https://example.com を見てください。"])).toEqual([
      "見出し",
      "箇条書きです。",
      "詳細は  を見てください。",
    ]);
  });
});

/**
 * 移植した `cleanTextForSpeech`（上流 cc-mascot の10段の正規表現）が**扱っていない**記法。
 * 上流にもこれを保持する意図のテストは無く、単なる未対応。
 *
 * 実機で頻度を見てから整形規則をまとめて見直す方針にしたので、ここでは
 * **現状の挙動を固定して欠落を可視化するだけ**にしてある。
 * 詳細と直し方は docs/core.md の「既知の欠落」を参照。
 */
describe("既知の欠落（移植した cleanTextForSpeech の未対応記法）", () => {
  const speakOne = (text: string) => speak([text]);

  it("強調の ** が残る", () => {
    expect(speakOne("**最重要** の点を確認します。")).toEqual(["**最重要** の点を確認します。"]);
  });

  it("句点をまたぐ強調は、閉じの ** だけが1発話になってしまう", () => {
    expect(speakOne("**確認します。**")).toEqual(["**確認します。", "**"]);
  });

  it("斜体・下線強調・取り消し線も残る", () => {
    expect(speakOne("*斜体* と __太字__ と ~~取り消し~~ です。")).toEqual([
      "*斜体* と __太字__ と ~~取り消し~~ です。",
    ]);
  });

  it("リンクは URL だけ消えて壊れた残骸になる", () => {
    // 段8（URL除去）がリンク記法を知らないため。直すなら段8より前に処理が要る
    expect(speakOne("詳細は [ドキュメント](https://example.com) を参照。")).toEqual([
      "詳細は [ドキュメント]( を参照。",
    ]);
  });

  it("★ URL 直後に句点を書くと、その先が段落ごと消える", () => {
    expect(speakOne("参考は https://example.com。まず設計書を読みます。次に実装します。")).toEqual(["参考は"]);
  });

  it("★ `<` と `>` に挟まれた文が丸ごと消える", () => {
    expect(speakOne("1 < 2 なので先に進みます。確認しました。3 > 2 です。")).toEqual(["1  2 です。"]);
  });

  it("★ commit hash の正規表現が数値や英単語まで消す", () => {
    expect(speakOne("ファイルは 5242880 バイトです。")).toEqual(["ファイルは  バイトです。"]);
    expect(speakOne("The word defaced was removed.")).toEqual(["The word  was removed."]);
  });

  it("約物だけの発話が seq を消費する", () => {
    expect(speakOne("すごい！！")).toEqual(["すごい！", "！"]);
  });
});
