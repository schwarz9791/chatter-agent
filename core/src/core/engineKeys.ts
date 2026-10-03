// ★ ChatterAgent の WebView からも値 import される。node の API や実行時 import を持たないこと
//   （`import type` のみ可）。

/** 合成エンジンごとの接続先・声の設定キー。エンジンとキーの対応はここ1か所だけ */
export function engineKeys(engine: string) {
  return engine === "openai"
    ? ({ baseUrl: "kokoroBaseUrl", voice: "kokoroVoiceId" } as const)
    : ({ baseUrl: "ttsBaseUrl", voice: "ttsSpeakerId" } as const);
}
