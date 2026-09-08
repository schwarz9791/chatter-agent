/**
 * Originally from kazakago/cc-mascot (Apache-2.0, Copyright 2026 kazakago)
 *   electron/services/ruleBasedEmotionClassifier.test.ts @ 46f7def
 * Modified for chatter-agent.
 */

import { describe, it, expect, beforeEach } from "vitest";
import { RuleBasedEmotionClassifier } from "./ruleBasedEmotionClassifier";

describe("RuleBasedEmotionClassifier", () => {
  let classifier: RuleBasedEmotionClassifier;

  beforeEach(() => {
    classifier = new RuleBasedEmotionClassifier();
  });

  describe("Neutral（中立）- コーディング関連の説明", () => {
    it("コードの技術的な説明はneutralと判定される", () => {
      const text =
        "この関数はReactコンポーネントをレンダリングするためのものです。useStateフックを使用して状態を管理しています。";
      expect(classifier.classify(text)).toBe("neutral");
    });

    it("ファイルパスを含む説明はneutralと判定される", () => {
      const text = "src/components/VRMAvatar.tsxファイルの3Dモデル描画ロジックについて説明します。";
      expect(classifier.classify(text)).toBe("neutral");
    });

    it("import文を含むコード例はneutralと判定される", () => {
      const text =
        '次のようにimportします。\n```typescript\nimport { useState } from "react";\n```\nこれで状態管理ができるようになります。';
      expect(classifier.classify(text)).toBe("neutral");
    });

    it("変数・関数の説明はneutralと判定される", () => {
      const text =
        "この変数emotionScoresは各感情のスコアを保持するオブジェクトです。キーワードマッチングの結果を集計します。";
      expect(classifier.classify(text)).toBe("neutral");
    });

    it("クラス・インターフェースの説明はneutralと判定される", () => {
      const text =
        "このクラスはルールベースの感情分類を行います。インターフェースEmotionを実装しており、classify メソッドで感情を判定します。";
      expect(classifier.classify(text)).toBe("neutral");
    });

    it("型定義を含む説明はneutralと判定される", () => {
      const text = 'type Emotion = "neutral" | "happy" | "angry" | "sad" | "relaxed" | "surprised"と定義されています。';
      expect(classifier.classify(text)).toBe("neutral");
    });

    it("配列・オブジェクトの説明はneutralと判定される", () => {
      const text =
        "このプロパティは配列形式で複数のキーワードを保持します。オブジェクトのキーには感情タイプが使用されています。";
      expect(classifier.classify(text)).toBe("neutral");
    });

    it("長文の技術説明はneutralと判定される", () => {
      const text = `まず、VRMモデルをロードする必要があります。次に、アニメーションデータを読み込みます。その後、Three.jsのシーンに追加します。最後に、レンダリングループを開始します。このプロセスは非同期で行われるため、Promiseを使用して処理を制御します。`;
      expect(classifier.classify(text)).toBe("neutral");
    });

    it("句点が多い説明文はneutralと判定される", () => {
      const text =
        "まずファイルを読み込みます。次にデータをパースします。その後、バリデーションを行います。最後に結果を返します。必要に応じて通知を送信します。";
      expect(classifier.classify(text)).toBe("neutral");
    });

    it("コードブロックを含む長文はneutralと判定される", () => {
      const text = `以下のように実装できます。\n\`\`\`typescript\nconst result = await fetchData();\nconsole.log(result);\n\`\`\`\nこれでデータの取得と表示が完了します。`;
      expect(classifier.classify(text)).toBe("neutral");
    });
  });

  describe("Happy（喜び）- 成功・完了の報告", () => {
    it("バグ修正の成功報告はhappyと判定される", () => {
      const text = "バグを修正できました！エラーが解決して正常に動作するようになりました。";
      expect(classifier.classify(text)).toBe("happy");
    });

    it("エラー解決の報告はhappyと判定される", () => {
      const text = "エラーの原因が分かりました！型定義を追加することで解決できました。";
      expect(classifier.classify(text)).toBe("happy");
    });

    it("実装完了の報告はhappyと判定される", () => {
      const text = "実装が完了しました！テストも全て成功しています。";
      expect(classifier.classify(text)).toBe("happy");
    });

    it("問題解決の報告はhappyと判定される", () => {
      const text = "問題を解決しました。型エラーを修正して、ビルドが成功するようになりました。";
      expect(classifier.classify(text)).toBe("happy");
    });

    it("テスト成功の報告はhappyと判定される", () => {
      const text = "テストが全て成功しました！素晴らしい結果です。";
      expect(classifier.classify(text)).toBe("happy");
    });

    it("助かったという表現はhappyと判定される", () => {
      const text = "そのライブラリを使うことで実装が簡単になって助かった！";
      expect(classifier.classify(text)).toBe("happy");
    });
  });

  describe("Sad（悲しい）- エラー・失敗の報告", () => {
    it("エラーでの謝罪はsadと判定される", () => {
      const text = "申し訳ありません…このエラーは現在のバージョンでは修正できません…";
      expect(classifier.classify(text)).toBe("sad");
    });

    it("無理な旨の報告はsadと判定される", () => {
      const text = "その実装は無理です。現在の制約では対応できません...";
      expect(classifier.classify(text)).toBe("sad");
    });

    it("失敗の報告はsadと判定される", () => {
      const text = "ビルドに失敗しました。型エラーが残っています。";
      expect(classifier.classify(text)).toBe("sad");
    });

    it("対応できない旨の表明はsadと判定される", () => {
      const text = "この対応は困難です...制約があります...";
      expect(classifier.classify(text)).toBe("sad");
    });
  });

  describe("Angry（怒り）- 苛立ちの表明", () => {
    it("腹が立つという表現はangryと判定される", () => {
      const text = "同じ間違いを三回も繰り返すなんて、さすがに腹が立ちます。";
      expect(classifier.classify(text)).toBe("angry");
    });

    it("うんざりという表現はangryと判定される", () => {
      const text = "何度言っても直らず、正直うんざりしています。";
      expect(classifier.classify(text)).toBe("angry");
    });

    it("否定形でしか使わない語は、その形を辞書に持つのでangryと判定される", () => {
      expect(classifier.classify("これはもう許せません。")).toBe("angry");
      expect(classifier.classify("許せない。")).toBe("angry");
    });

    it("肯定で使えば感情にならない（語幹だけを辞書に持たない効果）", () => {
      expect(classifier.classify("これは許せる範囲です。")).not.toBe("angry");
    });

    it("複数の感嘆符を含む報告はangryと判定される", () => {
      const text = "トラブルが発生しました！！コンパイルエラーです。";
      expect(classifier.classify(text)).toBe("angry");
    });

    it("エラー・バグ・問題などの技術語だけではangryと判定されない", () => {
      expect(classifier.classify("エラーが発生しました！型定義が間違っています。")).not.toBe("angry");
      expect(classifier.classify("これはバグです！この実装では正しく動作しません。")).not.toBe("angry");
      expect(classifier.classify("問題があります！このコードは動かないはずです。")).not.toBe("angry");
    });
  });

  describe("Relaxed（落ち着き）- 承認・確認", () => {
    it("了解の返答はrelaxedと判定される", () => {
      const text = "了解しました〜";
      expect(classifier.classify(text)).toBe("relaxed");
    });

    it("OK の返答はrelaxedと判定される", () => {
      const text = "その方針でOK〜。";
      expect(classifier.classify(text)).toBe("relaxed");
    });

    it("大丈夫という返答はrelaxedと判定される", () => {
      const text = "大丈夫だよ、問題ない。";
      expect(classifier.classify(text)).toBe("relaxed");
    });

    it("安心の表明はrelaxedと判定される", () => {
      const text = "その実装で安心した〜";
      expect(classifier.classify(text)).toBe("relaxed");
    });
  });

  describe("Surprised（驚き）- 予想外の結果", () => {
    it("驚きの表現はsurprisedと判定される", () => {
      const text = "え！そんな実装方法があったんですか？";
      expect(classifier.classify(text)).toBe("surprised");
    });

    it("意外な発見の報告はsurprisedと判定される", () => {
      const text = "まさか、このバグの原因がそこにあったとは！";
      expect(classifier.classify(text)).toBe("surprised");
    });

    it("予想外の結果報告はsurprisedと判定される", () => {
      const text = "びっくりしました。このライブラリにそんな機能があるとは。";
      expect(classifier.classify(text)).toBe("surprised");
    });

    it("マジという表現はsurprisedと判定される", () => {
      const text = "まじですか、そんな仕様があったとは知りませんでした。";
      expect(classifier.classify(text)).toBe("surprised");
    });
  });

  describe("エッジケース", () => {
    it("空文字はneutralと判定される", () => {
      expect(classifier.classify("")).toBe("neutral");
    });

    it("空白のみはneutralと判定される", () => {
      expect(classifier.classify("   ")).toBe("neutral");
    });

    it("短すぎるテキストはneutralと判定される", () => {
      expect(classifier.classify("a")).toBe("neutral");
    });

    it("混在したキーワードは優先度の高い感情が選ばれる", () => {
      const text = "エラーが発生しましたが、解決できました！";
      expect(classifier.classify(text)).toBe("happy");
    });
  });

  describe("Claude Code実際の返信パターン", () => {
    it("ファイル作成の説明", () => {
      const text = "vitest.config.tsファイルを作成しました。テストの設定を含んでいます。";
      expect(classifier.classify(text)).toBe("neutral");
    });

    it("コマンド実行の案内", () => {
      const text = "npm run testコマンドでテストを実行できます。";
      expect(classifier.classify(text)).toBe("neutral");
    });

    it("エラー修正の完了報告", () => {
      const text = "ESLintエラーを全て修正しました！ビルドが成功するようになりました！";
      expect(classifier.classify(text)).toBe("happy");
    });

    it("型エラーの指摘", () => {
      const text = "型エラーが発生しています。Emotion型の定義を確認してください。";
      expect(classifier.classify(text)).not.toBe("angry");
    });

    it("実装方針の確認", () => {
      const text = "了解〜、その方針で実装を進めるわ。";
      expect(classifier.classify(text)).toBe("relaxed");
    });

    it("長文のコード説明", () => {
      const text = `ruleBasedEmotionClassifier.tsのclassifyメソッドは、以下の手順で感情を判定します。まず、キーワードマッチングを行います。次に、文末パターンをチェックします。その後、ヒューリスティックルールを適用します。最後に、最も高いスコアの感情を返します。このアルゴリズムにより、65-75%の精度を実現しています。`;
      expect(classifier.classify(text)).toBe("neutral");
    });

    it("テスト失敗の報告（謝罪込み）", () => {
      const text = "申し訳ありません。テストが失敗しました。型定義を修正する必要があります。";
      expect(classifier.classify(text)).toBe("sad");
    });

    it("予想外のエラー発見", () => {
      const text = "え？このメソッドにバグがあったんですね。予想外でした。";
      expect(classifier.classify(text)).toBe("surprised");
    });
  });

  describe("リップシンク用のaa表情との連携確認", () => {
    it("どの感情でもaa表情の値は別途設定される前提", () => {
      // このテストは感情分類のみを確認
      // リップシンクのaa表情値は別のシステムで管理される
      const emotions: Array<ReturnType<typeof classifier.classify>> = [
        classifier.classify("嬉しいです！"),
        classifier.classify("エラーです。"),
        classifier.classify("悲しいです..."),
        classifier.classify("了解しました。"),
        classifier.classify("びっくりです！"),
        classifier.classify("関数を定義します。"),
      ];

      // すべての感情が有効な値であることを確認
      emotions.forEach((emotion) => {
        expect(["neutral", "happy", "angry", "sad", "relaxed", "surprised"]).toContain(emotion);
      });
    });
  });

  describe("エッジケース - スコア調整", () => {
    it("技術的な説明の中の軽い言及ではsadが優先されない", () => {
      const text =
        "このクラスの型とインターフェースを説明します。`sample.ts`のコード例を先に示したあと、最後の行だけ見落としがありました。";
      expect(classifier.classify(text)).toBe("neutral");
    });

    it("relaxedの弱いシグナルもneutralに吸収されず判定に反映される", () => {
      const text = "了解〜、その方針で進めるわ。";
      expect(classifier.classify(text)).toBe("relaxed");
    });
  });

  describe("作業状況に応じた表情判定", () => {
    it("完了を待っている状況はrelaxedと判定される", () => {
      const text = "サブエージェントの完了を待っています。";
      expect(classifier.classify(text)).toBe("relaxed");
    });

    it("任せた先が止まって自分でやり直す状況はangryと判定される", () => {
      expect(classifier.classify("サブエージェントが停止したので自分で確認します。")).toBe("angry");
      expect(classifier.classify("全件やり直します。")).toBe("angry");
      expect(classifier.classify("レート制限に引っかかって止まっていました。")).toBe("angry");
    });

    it("手戻りは待機より優先される（待つのと待たされるのは違う）", () => {
      expect(classifier.classify("サブエージェントの完了を待っています。")).toBe("relaxed");
      expect(classifier.classify("止まっているので自分で確認します。")).toBe("angry");
    });

    it("完了の報告はhappyと判定される", () => {
      const text = "実装が完了しました。";
      expect(classifier.classify(text)).toBe("happy");
    });

    it("ブラウザという語だけではangryと判定されない", () => {
      const text = "ブラウザで確認します。";
      expect(classifier.classify(text)).not.toBe("angry");
    });

    it("方針を尋ねる疑問文はsurprisedと判定されない", () => {
      const text = "着手順はどうしますか？";
      expect(classifier.classify(text)).not.toBe("surprised");
    });

    it("未解決件数の報告だけではhappyと判定されない", () => {
      const text = "2件の未解決コメントがあります。";
      expect(classifier.classify(text)).not.toBe("happy");
    });

    it("不安定という語を含んでいてもsadと判定されない", () => {
      const text = "テストが不安定でしたが直りました。";
      expect(classifier.classify(text)).not.toBe("sad");
    });

    it("謝罪と同居する完了報告はhappyと判定されない", () => {
      const text = "申し訳ありません、対応は完了しています。";
      expect(classifier.classify(text)).not.toBe("happy");
    });

    it("見落としの報告はsadと判定される", () => {
      const text = "設計を見落としていました。";
      expect(classifier.classify(text)).toBe("sad");
    });
  });

  describe("否定ガード（キーワード直後の否定形）", () => {
    it("完了していない旨はneutralと判定される", () => {
      expect(classifier.classify("まだ完了していません。")).toBe("neutral");
      expect(classifier.classify("実装は完了していません。")).toBe("neutral");
    });

    it("改善されていない旨はneutralと判定される", () => {
      expect(classifier.classify("改善されていません。")).toBe("neutral");
    });

    it("達成できなかった旨はneutralと判定される", () => {
      expect(classifier.classify("達成できませんでした。")).toBe("neutral");
    });

    it("実現できない旨はneutralと判定される", () => {
      expect(classifier.classify("実現できません。")).toBe("neutral");
    });

    it("「とは言えない」構文でもneutralと判定される", () => {
      expect(classifier.classify("良いとは言えません。")).toBe("neutral");
    });

    it("落ちていない旨はneutralと判定される", () => {
      expect(classifier.classify("一度も落ちていません。")).toBe("neutral");
    });

    it("止まっていない旨はneutralと判定される", () => {
      expect(classifier.classify("止まっていません。")).toBe("neutral");
    });

    it("削除した語の部分一致だった文はneutralと判定される", () => {
      expect(classifier.classify("事実はそうではありません。")).toBe("neutral");
    });

    it("極性を持たない副詞を削除したのでangryと判定されない", () => {
      expect(classifier.classify("相変わらず順調です。")).not.toBe("angry");
      expect(classifier.classify("依然として順調です。")).not.toBe("angry");
    });

    it("意図的な停止はangryと判定されない", () => {
      expect(classifier.classify("サーバーを停止してから起動し直します。")).toBe("neutral");
    });

    it("句点で窓が切れるので後続の否定に引きずられない", () => {
      const text = "完了しました。問題ありません。";
      expect(classifier.classify(text)).toBe("happy");
    });
  });

  describe("タイブレーク（同点時の優先順: angry > sad > relaxed > surprised > happy > neutral）", () => {
    it("待って の重複を落としても、完了報告との同点はrelaxedが勝つ", () => {
      const text = "サブエージェントの完了を待っています。";
      expect(classifier.classify(text)).toBe("relaxed");
    });

    it("sad と relaxed が同点のときはsadが勝つ", () => {
      const text = "残念ですが、順調です。";
      expect(classifier.classify(text)).toBe("sad");
    });

    it("angry と sad が同点のときはangryが勝つ", () => {
      const text = "腹が立ちますが残念です。";
      expect(classifier.classify(text)).toBe("angry");
    });
  });
});
