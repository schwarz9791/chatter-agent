import { describe, it, expect } from "vitest";
import { truncateAtUnstableTail } from "./unstableTail";
import { cleanTextForSpeech } from "./textFilter";
import { assembleSentences } from "../cli/messageAssembler";

const cut = (text: string) => truncateAtUnstableTail(text);

describe("コードフェンス", () => {
  it("フェンスが無ければそのまま返す", () => {
    const text = "確認します。ログを見ますね。";
    expect(cut(text)).toBe(text);
  });

  it("空文字はそのまま返す", () => {
    expect(cut("")).toBe("");
  });

  it("閉じたフェンスはそのまま返す（除去は cleanTextForSpeech の仕事）", () => {
    const text = "こう書きます。\n```ts\nconst a = 1;\n```\n以上です。";
    expect(cut(text)).toBe(text);
  });

  it("未閉じのフェンスは開始位置より後ろを切り落とす（コードは読み上げない）", () => {
    expect(cut("こう書きます。\n```ts\nconst secret = 1;")).toBe("こう書きます。\n");
  });

  it("言語指定だけ届いた時点でも切り落とす", () => {
    expect(cut("こう書きます。\n```typescript")).toBe("こう書きます。\n");
  });

  it("フェンスが先頭にあれば空文字になる", () => {
    expect(cut("```\nconst a = 1;")).toBe("");
  });

  it("閉じたフェンスが複数あっても、最後の未閉じだけを切る", () => {
    const text = "1つ目。\n```\na\n```\n2つ目。\n```\nb\n```\n3つ目。\n```\nc";
    expect(cut(text)).toBe("1つ目。\n```\na\n```\n2つ目。\n```\nb\n```\n3つ目。\n");
  });

  it("バッククォート4つは3つ+1つとして数える（正規表現と同じ数え方）", () => {
    expect(cut("説明。\n````")).toBe("説明。\n");
    const closedFence = "説明。\n``````";
    expect(cut(closedFence)).toBe(closedFence);
  });

  it("★ [#32] 地の文に混ざった迷子の ``` は開始とみなさない（行頭でないため）", () => {
    // 再現: 閉じたコードブロックの後ろに、``` に言及するだけの地の文が続く。
    // 修正前は出現回数をそのまま数えていたので、この1個で開閉が反転し
    // 「を使います。おわりです。」がまるごと切り落とされていた。
    const text = "説明します。\n```\ncode\n```\n以上です。バッククォート ``` を使います。おわりです。";
    expect(cut(text)).toBe(text);
  });

  it("★ [#32] 迷子フェンスが冒頭近くにあっても全文が消えない", () => {
    // 修正前はここで isOpen が反転し、以降の実質すべて（この例では全文）が切り落とされていた
    const text = "バッククォート ``` の話をします。今日は天気がいいです。";
    expect(cut(text)).toBe(text);
  });
});

describe("書きかけの表の行", () => {
  it("閉じていない行は切る（生の行を読み上げない）", () => {
    expect(cut("手順です。\n| A | B |\n| C | D")).toBe("手順です。\n| A | B |\n");
  });

  it("行が閉じていればそのまま（除去は cleanTextForSpeech の仕事）", () => {
    const text = "手順です。\n| A | B |\n| C | D |\n完了です。";
    expect(cut(text)).toBe(text);
  });

  it("表でない行には反応しない", () => {
    const text = "手順です。\n次に進みます";
    expect(cut(text)).toBe(text);
  });

  it("★ [#32] メッセージが改行で終わると最後の行は空文字になるので、末尾改行があっても検出する", () => {
    // 修正前は「文字列全体の最後の行」しか見ておらず、末尾に改行が付くと
    // その「最後の行」が空文字になって未閉じの表の行を素通りしていた
    // （生の `| C | D` がそのまま読み上げに漏れる実測バグ）。
    expect(cut("手順です。\n| C | D\n")).toBe("手順です。\n");
  });

  it("★ [#32] 未閉じの表の行が最終行でなくても検出する（代償: その先の文は失われる）", () => {
    // 修正前は「最後の行」しか見ないので、表の行の後ろにさらに文が続くケースは
    // そもそも検出の対象外だった（生パイプが1文として読み上げに漏れていた）。
    // 直した後は最初に見つかった不安定行で切るので、「完了です。」自体も失われる。
    // これは意図した代償（ソースコメント参照）: 生パイプの読み上げ事故より軽いと判断した。
    expect(cut("手順です。\n| A | B\n完了です。")).toBe("手順です。\n");
  });

  it("★ 閉じたコードブロックの中の `|` 行には反応しない（scan の前処理）", () => {
    // [#32] で incompleteTableRowAt が全行走査になったので、この scan 依存はもう
    // 「最後の行がたまたまフェンスの外」という偶然に頼っていない。この入力自体、
    // 閉じ ``` の直後に改行を挟まず表の行と同じ行で文字列を終わらせているので、
    // 全行走査で見ても最後の行は `| a | b````（閉じていない表の行）に見える。
    // scan（フェンス内部を空白化する前処理）が無ければここで誤検出して切ってしまう
    // （`node -e` で scan 有無の出力差を確認済み）。
    const text = "説明。\n```\n| a | b```";
    expect(cut(text)).toBe(text);
  });

  it("★ [#32] 全行走査になったので、最終行でなくても閉じたコードブロック内の `|` 行には反応しない", () => {
    // 全行走査への変更で「たまたま最後の行がフェンスの外」という前提が要らなくなった
    // ことを示す新しいケース。表っぽい行はコードブロックの途中（最終行ではない）にあり、
    // その後ろに独立した地の文が続く。scan が無ければ、この途中の行だけで誤検出して
    // 「続きです。」ごと切り落としてしまう。
    const text = "説明。\n```\n| a | b\n```\n続きです。";
    expect(cut(text)).toBe(text);
  });
});

describe("保留しなくなったもの（final を待つので伸びない）", () => {
  it("未閉じの `<` は切らない", () => {
    const text = "条件は a < b です。まず確認します。";
    expect(cut(text)).toBe(text);
  });

  it("未閉じのインラインバッククォートは切らない", () => {
    const text = "実行は `npm run build";
    expect(cut(text)).toBe(text);
  });

  it("末尾の URL は切らない", () => {
    const text = "参考は https://example.com/pa";
    expect(cut(text)).toBe(text);
  });

  it("末尾の16進列は切らない", () => {
    const text = "コミットは abc123";
    expect(cut(text)).toBe(text);
  });
});

describe("cleanTextForSpeech との組み合わせ（この関数が存在する理由）", () => {
  // ★ パイプラインを逐語で複製しない。`assembleSentences`（本体）を直接呼ぶことで、
  //   本体側の実装が変わったときにここが黙って乖離するのを防ぐ。
  const speak = (raw: string) => assembleSentences([raw]);

  it("未閉じのままではコードが読み上げに漏れる", () => {
    expect(cleanTextForSpeech("こう書きます。\n```ts\nconst secret = 1;")).toContain("const secret = 1;");
  });

  it("切り落としてから整形すればコードは漏れない", () => {
    expect(speak("こう書きます。\n```ts\nconst secret = 1;")).toEqual(["こう書きます。"]);
  });

  it("未閉じのままでは表の行が生で読み上げに漏れる", () => {
    expect(cleanTextForSpeech("手順です。\n| C | D")).toContain("| C | D");
  });

  it("切り落としてから整形すれば表の行は漏れない", () => {
    expect(speak("手順です。\n| C | D")).toEqual(["手順です。"]);
  });
});
