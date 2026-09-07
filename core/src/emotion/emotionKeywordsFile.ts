/**
 * 感情キーワード辞書ファイル（`~/.config/chatter-agent/emotion-keywords.json`）の
 * 読み書き。hook 経路（毎 delta 起動）から呼ばれるので throw しない・部分採用しない。
 */

import * as fs from "fs";
import * as path from "path";
import { writeFileAtomic } from "../core/atomicWrite";
import { DEFAULT_EMOTION_KEYWORDS, type EmotionKeywords } from "./defaultEmotionKeywords";

const MAX_KEYWORDS_PER_EMOTION = 1000;

function isEmotionKey(key: string): key is keyof EmotionKeywords {
  return Object.hasOwn(DEFAULT_EMOTION_KEYWORDS, key);
}

/**
 * 感情ごとに独立して検証する。1感情の不正が他の感情や全体を巻き添えにしない。
 * 常に完全な `EmotionKeywords`（既定との合成済み）を返す。
 */
export function parseEmotionKeywords(raw: unknown, warn: (message: string) => void): EmotionKeywords {
  if (typeof raw !== "object" || raw === null || Array.isArray(raw)) {
    warn("[EmotionKeywords] トップレベルがオブジェクトではありません。既定値を使います");
    return { ...DEFAULT_EMOTION_KEYWORDS };
  }

  const record = raw as Record<string, unknown>;
  const result: EmotionKeywords = { ...DEFAULT_EMOTION_KEYWORDS };

  for (const key of Object.keys(record)) {
    if (key === "neutral") {
      warn('[EmotionKeywords] "neutral" は指定できません。無視します');
      continue;
    }
    if (!isEmotionKey(key)) {
      warn(`[EmotionKeywords] 未知のキー "${key}" は無視されます`);
      continue;
    }

    const value = record[key];
    if (!Array.isArray(value)) {
      warn(`[EmotionKeywords] ${key} は配列である必要があります。既定値を使います`);
      continue;
    }
    if (value.some((item) => typeof item !== "string")) {
      warn(`[EmotionKeywords] ${key} の要素に文字列以外が含まれています。既定値を使います`);
      continue;
    }

    const deduped: string[] = [];
    const seen = new Set<string>();
    for (const item of value as string[]) {
      const trimmed = item.trim();
      if (!trimmed || seen.has(trimmed)) continue;
      seen.add(trimmed);
      deduped.push(trimmed);
    }

    if (deduped.length > MAX_KEYWORDS_PER_EMOTION) {
      warn(`[EmotionKeywords] ${key} の語数が上限（${MAX_KEYWORDS_PER_EMOTION}）を超えています。既定値を使います`);
      continue;
    }

    result[key] = deduped;
  }

  return result;
}

/** ファイルが無いのは正常（初回起動前）なので警告しない */
export function readEmotionKeywords(filePath: string, warn: (message: string) => void = console.warn): EmotionKeywords {
  let text: string;
  try {
    text = fs.readFileSync(filePath, "utf-8");
  } catch (err) {
    if ((err as NodeJS.ErrnoException).code === "ENOENT") {
      return { ...DEFAULT_EMOTION_KEYWORDS };
    }
    warn(`[EmotionKeywords] ${filePath} を読めませんでした: ${String(err)}。既定値を使います`);
    return { ...DEFAULT_EMOTION_KEYWORDS };
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch (err) {
    warn(`[EmotionKeywords] ${filePath} のJSONが壊れています: ${String(err)}。既定値を使います`);
    return { ...DEFAULT_EMOTION_KEYWORDS };
  }

  return parseEmotionKeywords(parsed, warn);
}

/** 既にあれば何もしない。失敗は握り潰し、読み取り専用の配置でも発話を止めない */
export function writeDefaultEmotionKeywordsIfAbsent(filePath: string): void {
  try {
    if (fs.existsSync(filePath)) return;
    fs.mkdirSync(path.dirname(filePath), { recursive: true });
    writeFileAtomic(filePath, `${JSON.stringify(DEFAULT_EMOTION_KEYWORDS, null, 2)}\n`);
  } catch {
    // 失敗しても発話は既定値のまま続く
  }
}
