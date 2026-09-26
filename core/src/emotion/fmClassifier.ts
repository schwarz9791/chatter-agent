/**
 * fm（Apple Foundation Models CLI、macOS 27 以降）へメッセージ全体を1回だけ投げ、
 * 判定結果を全部の文に適用する感情分類器。
 *
 * ★ 1文ずつ判定すると重すぎるので、メッセージ単位で1回にまとめる。
 *   `--schema` で構造化出力を強制し、6感情のスコア（0.0〜1.0、独立）から最大値のラベルを採る。
 * ★ 実行は `summarizer/claudeCli.ts` の `runClaudeCli`（execFileSync + タイムアウト + 失敗分類）を
 *   そのまま流用する。要約 CLI 専用ではなく「同期で外部 CLI を1つ叩く」汎用の形として使う。
 * ★ どの失敗（コマンドが無い・タイムアウト・非ゼロ終了・壊れた JSON）でも例外を投げず、
 *   呼び出し側から渡された `fallback`（辞書式）に委ねる。
 */

import * as fs from "fs";
import * as path from "path";
import { writeFileAtomic } from "../core/atomicWrite";
import { resolveFmCommandPath, runClaudeCli } from "../summarizer/claudeCli";
import { EMOTION_KEYS, pickEmotion } from "./emotionScores";
import type { Emotion } from "../core/types";

/** `fm respond --schema` に渡す構造化出力のスキーマ。内容が変わったときだけランタイムディレクトリへ書き直す */
export const FM_EMOTION_SCHEMA = {
  additionalProperties: false,
  type: "object",
  title: "EmotionScores",
  properties: Object.fromEntries(EMOTION_KEYS.map((k) => [k, { description: "0.0-1.0", type: "number" }])),
  "x-order": EMOTION_KEYS,
  required: EMOTION_KEYS,
};

/**
 * 判定基準の指示文。sad / angry はそのままだと値が付きにくい傾向があるので、
 * 明示的に「遠慮せず付ける」よう促す。
 */
export const FM_EMOTION_INSTRUCTION = [
  "あなたはAIコーディングアシスタントの発言を読み取り、そこに乗っている感情を6種類のスコアとして判定します。",
  "",
  "出力は次の形式のJSONのみ。前置き・説明・コードブロック記法は一切付けないこと。",
  '{"happy": 0.0, "relaxed": 0.0, "surprised": 0.0, "sad": 0.0, "angry": 0.0, "neutral": 0.0}',
  "",
  "各キーは0.0〜1.0の値。感情ごとに独立した強さなので合計が1になる必要はない。読み上げキャラクターの",
  "表情に使うため、はっきりした感情ならやや誇張して高い値を付けてよい（何でもneutralに寄せない）。",
  "",
  "判定基準（コーディングエージェント自身の状況に当てはめること）:",
  "- happy（達成）: 完了報告、テスト通過、レビュー対応完了など、うまくいったことを伝える文。",
  "- relaxed（一段落・待機）: 何かの結果を待っている、確認作業中、落ち着いて状況を説明している文。",
  "- surprised（予想外の発見）: 想定していなかった事実やバグが見つかった、驚きを伴う発見を伝える文。",
  "- sad（残念）: 思いどおりにならなかった、期待が外れたときの文。例: サブエージェントが完了を返して",
  "  こない、成果物を差し戻す必要があった、謝罪の言葉（「申し訳ありません」「すみません」）、見落と",
  "  しに気づいた。",
  "- angry（失敗への悔しさ・苛立ち）: 自分側の失敗や想定外の破綻を伝える文。例: テストが想定外に落ち",
  "  た、自分の修正で回帰バグを生んでしまった、同じ罠を二度踏んだ。",
  "- neutral（淡々とした報告）: 感情の起伏がない、事実だけを述べる文。",
  "",
  "sad と angry は、そのまま判定すると値が付きにくい傾向がある。上の基準に当てはまる文なら、",
  "遠慮せず0.5以上の値を付けること。",
].join("\n");

/**
 * 内容が変わっていなければ何もしない。変わっていれば tmp + rename で書き直す
 * （`FM_EMOTION_SCHEMA` を変えた将来のアップグレードでも、既存環境に古いスキーマが残り続けない
 * ようにするため）。失敗は握り潰し、読み取り専用の配置でも発話を止めない。
 *
 * ★ ユーザーが手で編集するファイルではない（`emotionKeywordsFile.ts` とは違う）ので、
 *   内容が違えば無条件に上書きしてよい。
 */
export function writeFmEmotionSchemaIfChanged(filePath: string): void {
  try {
    const desired = `${JSON.stringify(FM_EMOTION_SCHEMA, null, 2)}\n`;
    let current: string | undefined;
    try {
      current = fs.readFileSync(filePath, "utf-8");
    } catch {
      current = undefined;
    }
    if (current === desired) return;

    fs.mkdirSync(path.dirname(filePath), { recursive: true });
    writeFileAtomic(filePath, desired);
  } catch {
    // 失敗しても classify 側は毎回 --schema にこのパスを渡すだけなので、書けていなくても
    // fm がエラーになり fallback に落ちるだけで発話は止まらない
  }
}

/** ```json フェンス付きで返ってきた場合の保険（--schema があれば通常は素の JSON になる） */
function parseScores(stdout: string): unknown {
  const trimmed = stdout.trim();
  try {
    return JSON.parse(trimmed);
  } catch {
    const stripped = trimmed.replace(/^```[a-zA-Z]*\n?/, "").replace(/\n?```$/, "");
    try {
      return JSON.parse(stripped);
    } catch {
      return null;
    }
  }
}

export interface FmEmotionClassifierDeps {
  /** スキーマ JSON の置き場所（`core/paths.ts` の `getEmotionSchemaPath()`） */
  schemaPath: string;
  /** fm CLI の cwd */
  homeDir: string;
  /** 判定1回（メッセージ単位）の上限。超えたら fallback */
  getTimeoutMs: () => number;
  /** 接続不可・タイムアウト・壊れた応答のときのフォールバック（辞書式） */
  fallback: (texts: string[]) => Emotion[];
  /** テスト用。既定 `FM_COMMAND_PATH`（`/usr/bin/fm`） */
  commandPath?: string;
}

/**
 * `(texts: string[]) => Emotion[]` を作る。**メッセージ単位で1回だけ fm を呼び**、
 * 同じ感情を `texts` の全要素に付ける。throw しない（内部で必ず fallback に落とす）。
 */
export function createFmEmotionClassifier(deps: FmEmotionClassifierDeps): (texts: string[]) => Emotion[] {
  return (texts) => {
    if (texts.length === 0) return [];

    try {
      writeFmEmotionSchemaIfChanged(deps.schemaPath);

      const commandPath = resolveFmCommandPath(deps.commandPath);
      if (!commandPath) return deps.fallback(texts);

      const args = [
        "respond",
        "-i",
        FM_EMOTION_INSTRUCTION,
        "--no-stream",
        "--guardrails",
        "permissive-content-transformations",
        "--schema",
        deps.schemaPath,
      ];
      const result = runClaudeCli({
        commandPath,
        args,
        text: texts.join("\n"),
        homeDir: deps.homeDir,
        timeoutMs: deps.getTimeoutMs(),
      });
      if (!result.ok) return deps.fallback(texts);

      const emotion = pickEmotion(parseScores(result.stdout) as Record<string, number> | null);
      if (emotion === null) return deps.fallback(texts);

      return texts.map(() => emotion);
    } catch {
      return deps.fallback(texts);
    }
  };
}
