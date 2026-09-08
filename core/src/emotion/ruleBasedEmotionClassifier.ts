/**
 * Originally from kazakago/cc-mascot (Apache-2.0, Copyright 2026 kazakago)
 *   electron/services/ruleBasedEmotionClassifier.ts @ 46f7def
 * Modified for chatter-agent.
 */

/**
 * ルールベース感情分類器
 *
 * Claude Codeの日本語テキストから感情を自動分類する。
 * キーワードマッチング + 文末パターン + ヒューリスティックで65-75%の精度を実現。
 */

// 改変: Emotion をここで定義せず、契約側（core/types）から取り込む。
// VRM の expression 名と一対一である以上、正は speech.jsonl の契約側にある。
import type { Emotion } from "../core/types";
import { DEFAULT_EMOTION_KEYWORDS, type EmotionKeywords } from "./defaultEmotionKeywords";

export type { Emotion };

// キーワード直後の否定形を弾く共通ガード。活用形を辞書に並べる代わりに、
// 当たった位置の直後だけを見て判定する。
const SENTENCE_BOUNDARY = /[。！？!?\n、]/;
const NEGATION_LINK = "[はがもをにでとしてられさきりえけいうつっまなわ]{0,6}";
const NEGATION_ENDING = "(ない|ないで|ません|ませんで|なかっ|ず|ぬ)";
const NEGATION_PATTERN = new RegExp("^(?:" + NEGATION_LINK + "|とは言え|とはいえ|とは思え)" + NEGATION_ENDING);

/** キーワード直後から文の区切りまでの短い窓を切り出す */
function tailAfterKeyword(text: string, at: number, keywordLength: number): string {
  const window = text.slice(at + keywordLength, at + keywordLength + 12);
  const boundary = window.search(SENTENCE_BOUNDARY);
  return boundary >= 0 ? window.slice(0, boundary) : window;
}

/**
 * キーワードが1箇所でも肯定形で出現していれば真。
 * 同じ語が複数回出るときは、すべての出現が否定形のときだけ偽になる。
 */
function hasAffirmativeMatch(text: string, keyword: string): boolean {
  let index = text.indexOf(keyword);
  while (index !== -1) {
    if (!NEGATION_PATTERN.test(tailAfterKeyword(text, index, keyword.length))) {
      return true;
    }
    index = text.indexOf(keyword, index + 1);
  }
  return false;
}

// 最高スコアが同点のときの優先順。scores リテラルのキー順に判定を委ねない。
// 現在の状態や未解決の情報は、報告の喜びより優先する。
const TIE_BREAK_ORDER: Emotion[] = ["angry", "sad", "relaxed", "surprised", "happy", "neutral"];

export class RuleBasedEmotionClassifier {
  private emotionKeywords: EmotionKeywords;

  constructor(emotionKeywords: EmotionKeywords = DEFAULT_EMOTION_KEYWORDS) {
    this.emotionKeywords = emotionKeywords;
  }

  /**
   * 文末パターン（正規表現）
   * 女性言葉・中性的・丁寧・男性的な言葉すべてに対応
   */
  private sentenceEndPatterns: Record<Exclude<Emotion, "neutral">, RegExp[]> = {
    happy: [
      /[！!]{2,}/, // 複数の感嘆符
      // 女性言葉
      /わ[ね〜～！!♪]+$/, // わね！！、わ〜♪など
      /わよ[！!♪]+$/, // わよ！、わよ♪
      // 中性的・丁寧
      /です[！!♪]+$/, // です！
      /ます[！!♪]+$/, // ます！
      /ました[！!♪]+$/, // ました！
      /ね[！!♪]+$/, // ね！
      /よ[！!♪]+$/, // よ！
      // 男性的
      /ぜ[！!]+$/, // ぜ！
      /ぞ[！!]+$/, // ぞ！
      /だ[！!]+$/, // だ！
      /った[！!]+$/, // やった！、できた！
      // 共通
      /[♪♫]+/, // 音符記号
      /[✨🎉🎊😊😄🎊👍]+/u, // 喜びの絵文字
    ],
    angry: [
      /[！!？?]{2,}/, // 複数の感嘆符・疑問符
      // 女性言葉
      /わよ[！!]{2,}$/, // わよ！！（強い）
      /のよ[！!]+$/, // のよ！
      // 中性的・丁寧
      /です[！!]{2,}$/, // です！！
      /ません[！!]+$/, // ません！
      // 男性的
      /だ[！!]{2,}$/, // だ！！
      /だろ[！!？?]+$/, // だろ！
      /のか[！!？?]+$/, // のか！
      // 共通
      /[💢😠😡]+/u, // 怒りの絵文字
    ],
    sad: [
      // 女性言葉
      /わ…+$/, // 悲しいわ…
      /のね…+$/, // 残念なのね…
      // 中性的・丁寧（三点リーダーがある場合のみ）
      /です…+$/, // 残念です…
      /ます…+$/, // できます…
      /ません…+$/, // できません…
      // 男性的
      /だ…+$/, // 無理だ…
      /な…+$/, // ダメだな…
      // 共通
      /[。.]{2,}$/, // 句点の連続（..、。。）
      /…+$/, // 三点リーダー
      /[😢😭💔]+/u, // 悲しみの絵文字
    ],
    surprised: [
      /[！!]{2,}[？?]?$/, // 感嘆符（連続）
      // 女性言葉
      /え[っ〜～！!？?]+/, // えっ！、え〜？など
      /まさか[！!？?]/, // まさか！
      // 男性的
      /だと[！!？?]$/, // マジだと！？
      // 共通
      /マジ[！!？?]/, // マジ！？
      /ほんと[！!？?]/, // ほんと！？
      /本当[！!？?]/, // 本当！？
      /[😮😲🤯]+/u, // 驚きの絵文字
    ],
    relaxed: [
      // 女性言葉（波線がある場合のみ）
      /わ[ね〜～]+$/, // わね〜
      /ですわ[〜～]+$/, // ですわ〜
      // 中性的・丁寧（波線がある場合のみ）
      /です[〜～]+$/, // です〜
      /ます[〜～]+$/, // ます〜
      /ました[〜～]+$/, // ました〜
      /ね[〜～]+$/, // ね〜
      // 共通（明確なrelaxed表現のみ）
      /OK[。.〜～]+$/, // OK.、OK〜
      /了解[。.〜～]+$/, // 了解。、了解〜
    ],
  };

  /**
   * テキストから感情を分類する
   * @param text 分類対象のテキスト
   * @returns 分類された感情
   */
  classify(text: string): Emotion {
    // 空文字・短すぎる場合はneutral
    if (!text || text.trim().length < 2) {
      return "neutral";
    }

    const normalizedText = text.trim();
    const textLength = normalizedText.length;
    const isLongText = textLength > 100; // 長文判定

    // 1. スコア初期化
    const scores: Record<Emotion, number> = {
      neutral: 0,
      happy: 0,
      angry: 0,
      sad: 0,
      relaxed: 0,
      surprised: 0,
    };

    // 2. キーワードマッチング（長文では重みを増加）
    const keywordWeight = isLongText ? 3 : 2;
    for (const [emotion, keywords] of Object.entries(this.emotionKeywords)) {
      for (const keyword of keywords) {
        if (hasAffirmativeMatch(normalizedText, keyword)) {
          scores[emotion as Emotion] += keywordWeight;
        }
      }
    }

    // 3. 文末パターンチェック（長文では重要度を上げる）
    const patternWeight = isLongText ? 4 : 2;
    for (const [emotion, patterns] of Object.entries(this.sentenceEndPatterns)) {
      for (const pattern of patterns) {
        if (pattern.test(normalizedText)) {
          scores[emotion as Emotion] += patternWeight;
        }
      }
    }

    // 4. 文頭の感情表現を強化（最初の50文字以内）
    const firstPart = normalizedText.substring(0, 50);
    for (const [emotion, keywords] of Object.entries(this.emotionKeywords)) {
      for (const keyword of keywords) {
        if (hasAffirmativeMatch(firstPart, keyword)) {
          scores[emotion as Emotion] += 2; // 文頭の感情は重視
        }
      }
    }

    // 5. ヒューリスティックルール
    this.applyHeuristics(normalizedText, scores);

    // 6. ネガティブ感情の優先処理
    // angry/sad のキーワードがあれば、happy の文末スコアを抑制
    if (scores.angry > 0 || scores.sad > 0) {
      // happy の文末パターンによるスコアを半減
      const hasHappyEndPattern = this.sentenceEndPatterns.happy.some((p) => p.test(normalizedText));
      if (hasHappyEndPattern && (scores.angry > 0 || scores.sad > 0)) {
        scores.happy = Math.floor(scores.happy * 0.5);
      }
    }

    // 7. 長文の場合、感情スコアがあればneutralを抑制
    if (isLongText) {
      const emotionScoreSum = scores.happy + scores.angry + scores.sad + scores.surprised + scores.relaxed;
      // 強い感情表現（スコア10以上）がある場合のみneutralを抑制
      if (emotionScoreSum >= 10) {
        scores.neutral = Math.max(0, scores.neutral - 3);
      }
    }

    // 8. 最高スコアの感情を返す（デフォルトはneutral、同点は TIE_BREAK_ORDER で決める）
    let maxEmotion: Emotion = "neutral";
    let maxScore = 0;

    for (const emotion of TIE_BREAK_ORDER) {
      const score = scores[emotion];
      if (score > maxScore) {
        maxScore = score;
        maxEmotion = emotion;
      }
    }

    // デバッグログ（開発時に有効）
    if (process.env.NODE_ENV === "development" && maxEmotion !== "neutral") {
      console.log(
        `[EmotionClassifier] Text: "${normalizedText.substring(0, 50)}${normalizedText.length > 50 ? "..." : ""}"`,
      );
      console.log(`[EmotionClassifier] Scores:`, scores);
      console.log(`[EmotionClassifier] Result: ${maxEmotion}`);
    }

    return maxEmotion;
  }

  /**
   * ヒューリスティックルールを適用
   */
  private applyHeuristics(text: string, scores: Record<Emotion, number>): void {
    // 感情スコアの合計を計算
    const emotionScoreSum = scores.happy + scores.angry + scores.sad + scores.surprised + scores.relaxed;
    const hasEmotion = emotionScoreSum > 0;

    // 短い返事（明確なrelaxed表現のみ）
    if (text.length < 10) {
      if (/^(OK|了解|わかった)/.test(text)) {
        scores.relaxed += 2;
      }
    }

    // コードブロックやバッククォート → neutral（ただし感情がある場合は抑制）
    if (/```|`[^`]+`/.test(text)) {
      scores.neutral += hasEmotion ? 2 : 4;
      // relaxedを抑制
      scores.relaxed = Math.max(0, scores.relaxed - 2);
    }

    // import/export/function などのキーワード → neutral（ただし感情がある場合は抑制）
    if (/(import|export|function|const|let|var|class|interface|type)/.test(text)) {
      scores.neutral += hasEmotion ? 2 : 4;
      scores.relaxed = Math.max(0, scores.relaxed - 2);
    }

    // ファイルパス → neutral（軽く）
    if (/[/\\][a-zA-Z0-9_\-./\\]+/.test(text)) {
      scores.neutral += hasEmotion ? 0 : 1;
    }

    // 技術用語 → neutral（ただし感情がある場合は抑制）
    if (/(コード|関数|メソッド|変数|クラス|インターフェース|型|配列|オブジェクト|プロパティ)/.test(text)) {
      scores.neutral += hasEmotion ? 1 : 3;
      scores.relaxed = Math.max(0, scores.relaxed - 1);
    }

    // 「次に」「まず」「それから」などの説明的な接続詞 → neutral傾向（感情がある場合は無視）
    if (!hasEmotion && /(次に|まず|それから|その後|最後に|ここで|この|その)/.test(text)) {
      scores.neutral += 1;
    }

    // 長文（100文字以上）で句点が多い → neutral（説明文）（感情がある場合は抑制）
    if (text.length > 100) {
      const periodCount = (text.match(/[。.]/g) || []).length;
      if (periodCount >= 3) {
        scores.neutral += hasEmotion ? 1 : 2;
      }
    }

    // 謝罪・自責の語が同居する文では happy を持ち上げない
    const hasApology = /(申し訳|すみま|ごめん|すまな|見落と)/.test(text);
    if (hasApology) {
      scores.happy = Math.max(0, scores.happy - 4);
    } else if (/(エラー|バグ|問題|失敗)/.test(text) && /(修正|解決|できた|成功|完了)/.test(text)) {
      // ネガティブワード + 肯定 → happy（問題解決）
      scores.happy += 4;
      scores.angry = Math.max(0, scores.angry - 2);
    }
  }
}
