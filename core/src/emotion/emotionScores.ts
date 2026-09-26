/**
 * 6感情のスコア（`Record<Emotion, number>` 相当）からラベルを1つ選ぶ。fm・Ollaya 共通。
 */

import type { Emotion } from "../core/types";

export const EMOTION_KEYS: readonly Emotion[] = ["happy", "relaxed", "surprised", "sad", "angry", "neutral"];

/**
 * 最大値のラベルを返す。壊れていれば `null`（呼び出し側が fallback する）。
 * **最大値が0以下、または同点なら `"neutral"`。** 同点を先頭キー（happy）に倒すと、
 * 全部0点や sad/angry と同値の文まで笑顔になる。
 */
export function pickEmotion(scores: Record<string, number> | null | undefined): Emotion | null {
  if (typeof scores !== "object" || scores === null) return null;

  let best: Emotion | null = null;
  let bestValue = Number.NEGATIVE_INFINITY;
  let tie = false;

  for (const key of EMOTION_KEYS) {
    const v = scores[key];
    if (typeof v !== "number" || !Number.isFinite(v)) continue;
    if (v > bestValue) {
      bestValue = v;
      best = key;
      tie = false;
    } else if (v === bestValue) {
      tie = true;
    }
  }

  if (best === null) return null;
  return tie || bestValue <= 0 ? "neutral" : best;
}
