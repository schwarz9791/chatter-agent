// ★ ChatterAgent の WebView からも値 import される。node の API や実行時 import を持たないこと
//   （`import type` のみ可）。

/** 合成エンジンごとの接続先・声の設定キー。エンジンとキーの対応はここ1か所だけ */
export function engineKeys(engine: string) {
  return engine === "openai"
    ? ({ baseUrl: "kokoroBaseUrl", voice: "kokoroVoiceId" } as const)
    : ({ baseUrl: "ttsBaseUrl", voice: "ttsSpeakerId" } as const);
}

export type VoiceLanguage = "ja" | "en";

/**
 * 読み上げる声が読める言語。Kokoro は声の ID の先頭で読みの言語が決まるので、それに合わせる。
 * 日本語の声以外は英語として扱う
 */
export function voiceLanguage(engine: string, kokoroVoiceId: string): VoiceLanguage {
  if (engine !== "openai") return "ja";
  return kokoroVoiceId.charAt(0).toLowerCase() === "j" ? "ja" : "en";
}
