// 設定パネルの文言。
// `Text` に足したら JA / EN の両方を直さないとコンパイルが通らない。

export type Text = {
  title: string;
  coreUnreachable: string;
  unsupportedHost: string;
  panelEmpty: string;

  sectionCharacter: string;
  chooseVrm: string;
  bundledModelNote: string;
  appliesFromNextLaunch: string;
  characterSize: string;
  resetWindow: string;
  mascotNotRunning: string;
  notVrm: string;
  copyFailed(reason: string): string;

  sectionAudio: string;
  ttsEngine: string;
  voiceStyle: string;
  volume: string;
  speakingSpeed: string;
  playTestVoice: string;
  speakerListUnavailable: string;
  appliesFromNextSentence: string;
  mutedNoSound: string;
  envOverridden(envName: string): string;

  sectionMotion: string;
  idleMotion: string;
  cursorGaze: string;
  blink: string;
  frameRate: string;
  frameRateNote: string;
  previewMotion: string;
  playMotion: string;
  previewIdleOff: string;
  previewLoading: string;
  previewEmpty: string;
  previewNote: string;

  sectionAiSummary: string;
  summarizeLongMessages: string;
  summarizeNote: string;
  summaryEngine: string;
  summaryEngineNote: string;
  summaryClaude: string;

  sectionEmotion: string;
  emotionEngine: string;
  emotionEngineNote: string;
  emotionDictionary: string;

  sectionShortcuts: string;
  hotKeyMute: string;
  hotKeyHide: string;
  hotKeyNeedsModifier: string;
  hotKeyClashRejected(label: string): string;

  sectionReset: string;
  resetAll: string;
  resetAllNote: string;
  confirmResetTitle: string;
  confirmResetMessage: string;
  confirmResetOk: string;
  confirmResetCancel: string;
  resetDone(removedModels: number): string;
  resetMascotFailed(reason: string): string;
  resetCoreDefaultsUnavailable: string;
  resetKeyFailed(key: string, reason: string): string;

  settingsUnreadable(reason: string): string;

  errorResponseUnreadable(reason: string): string;
  errorHttp(status: number): string;
  errorEnvOverride(key: string): string;
  errorReadonlyKey(key: string): string;
  errorInvalidValue(key: string): string;
  errorEngineUnreachable: string;
  errorSynthesisUnavailable: string;
  errorTtsDisabled: string;
  errorConfigUnreadable: string;
  errorConfigUnwritable: string;
  errorTooManyRequests: string;
  errorUnknown(error: string, key: string): string;
};

export const JA: Text = {
  title: "Chatter Agent 設定",
  coreUnreachable: "サーバーに繋がりません",
  unsupportedHost: "host が LAN の特定アドレスのため設定できません",
  panelEmpty: "（取得できません）",

  sectionCharacter: "キャラクター",
  chooseVrm: "VRM モデルを選ぶ…",
  bundledModelNote: "同梱のモデルを使っています",
  appliesFromNextLaunch: "次回の起動から反映されます",
  characterSize: "大きさ",
  resetWindow: "位置と大きさをリセット",
  mascotNotRunning: "Chatter Mascot が起動していません",
  notVrm: "VRM ファイル（.vrm）を選んでください",
  copyFailed: (reason) => `コピーできませんでした: ${reason}`,

  sectionAudio: "オーディオ",
  ttsEngine: "合成エンジン",
  voiceStyle: "音声スタイル",
  volume: "音量",
  speakingSpeed: "話す速さ",
  playTestVoice: "テスト音声を再生",
  speakerListUnavailable: "話者の一覧を取得できませんでした",
  appliesFromNextSentence: "次に喋る文から変わります",
  mutedNoSound: "ミュート中なので鳴りません",
  envOverridden: (envName) => `環境変数（${envName}）で固定されています`,

  sectionMotion: "モーション",
  idleMotion: "待機モーション",
  cursorGaze: "カーソルを目で追う",
  blink: "まばたき",
  frameRate: "フレームレート",
  frameRateNote: "60 fps は CPU 使用率が上がります",
  previewMotion: "モーションを確認",
  playMotion: "再生",
  previewIdleOff: "待機モーションが OFF の間は再生できません",
  previewLoading: "モーションを読み込み中です",
  previewEmpty:
    "モーションがありません。~/.config/chatter-agent/animations/<カテゴリ>/ に .vrma を置いてください",
  previewNote: "感情のモーションを再生中は始まらないことがあります",

  sectionAiSummary: "AI要約",
  summarizeLongMessages: "長いメッセージを要約してから読み上げる",
  summarizeNote: "要約には時間がかかります（間に合わなければ原文を読み上げます）",
  summaryEngine: "要約エンジン",
  summaryEngineNote: "fm は macOS 27 以降で使えます。使えないときは原文を読み上げます",
  summaryClaude: "Claude Haiku",

  sectionEmotion: "感情判定",
  emotionEngine: "感情判定エンジン",
  emotionEngineNote: "fm は macOS 27 以降で使えます。使えないときは辞書式に戻ります",
  emotionDictionary: "辞書式",

  sectionShortcuts: "ショートカット",
  hotKeyMute: "ミュートの切り替え",
  hotKeyHide: "キャラクターの表示切り替え",
  hotKeyNeedsModifier: "修飾キー（⌃ ⌥ ⇧ ⌘）を1つ以上選んでください",
  hotKeyClashRejected: (label) => `「${label}」と同じ組み合わせです`,

  sectionReset: "リセット",
  resetAll: "すべての設定をリセット…",
  resetAllNote: "選んだモデルのファイルも消します。合成エンジンと、キャラクターの位置・大きさはそのままです",
  confirmResetTitle: "すべての設定をリセットしますか？",
  confirmResetMessage:
    "音量・モーション・ショートカット・音声スタイル・話す速さ・要約・要約エンジン・感情判定の設定が既定に戻り、選んだ VRM モデルのファイルも削除されます。この操作は取り消せません。",
  confirmResetOk: "リセットする",
  confirmResetCancel: "やめる",
  resetDone: (n) => (n > 0 ? `既定に戻しました（モデル ${n} 件を削除）` : "既定に戻しました"),
  resetMascotFailed: (reason) => `マスコットの設定を戻せませんでした: ${reason}`,
  resetCoreDefaultsUnavailable: "既定値を取れません",
  resetKeyFailed: (key, reason) => `${key} を戻せませんでした（${reason}）`,

  settingsUnreadable: (reason) => `settings.json を読めません（${reason}）`,

  errorResponseUnreadable: (reason) => `応答を読めませんでした: ${reason}`,
  errorHttp: (status) => `エラーが返りました（HTTP ${status}）`,
  errorEnvOverride: (key) => `環境変数で固定されているので変えられません（${key}）`,
  errorReadonlyKey: (key) => `この設定は変更できません（${key}）`,
  errorInvalidValue: (key) => `値が範囲外です（${key}）`,
  errorEngineUnreachable: "音声合成エンジンに繋がりません",
  errorSynthesisUnavailable: "音声を合成できませんでした",
  errorTtsDisabled: "サーバー側で音声が無効になっています（ttsEnabled）",
  errorConfigUnreadable: "config.json を読めないので書き込みませんでした",
  errorConfigUnwritable: "config.json に書けませんでした",
  errorTooManyRequests: "続けて押しすぎです。少し待ってください",
  errorUnknown: (error, key) => `${error}（${key}）`,
};

export const EN: Text = {
  title: "Chatter Agent Settings",
  coreUnreachable: "Can't reach the server.",
  unsupportedHost: "Can't configure: host is set to a specific LAN address.",
  panelEmpty: "(Unavailable)",

  sectionCharacter: "Character",
  chooseVrm: "Choose VRM Model…",
  bundledModelNote: "Using the bundled model.",
  appliesFromNextLaunch: "Takes effect on next launch.",
  characterSize: "Size",
  resetWindow: "Reset position and size",
  mascotNotRunning: "Chatter Mascot is not running.",
  notVrm: "Choose a VRM file (.vrm).",
  copyFailed: (reason) => `Couldn't copy the file: ${reason}`,

  sectionAudio: "Audio",
  ttsEngine: "Speech engine",
  voiceStyle: "Voice style",
  volume: "Volume",
  speakingSpeed: "Speaking speed",
  playTestVoice: "Play Test Voice",
  speakerListUnavailable: "Couldn't load the voice list.",
  appliesFromNextSentence: "Takes effect from the next sentence.",
  mutedNoSound: "Muted, so nothing will play.",
  envOverridden: (envName) => `Locked by environment variable (${envName}).`,

  sectionMotion: "Motion",
  idleMotion: "Idle motion",
  cursorGaze: "Eyes follow cursor",
  blink: "Blink",
  frameRate: "Frame rate",
  frameRateNote: "60 fps uses more CPU.",
  previewMotion: "Preview motion",
  playMotion: "Play",
  previewIdleOff: "Can't play while Idle motion is off.",
  previewLoading: "Loading motions…",
  previewEmpty: "No motions found. Put .vrma files in ~/.config/chatter-agent/animations/<category>/.",
  previewNote: "May not start while an emotion motion is playing.",

  sectionAiSummary: "AI Summary",
  summarizeLongMessages: "Summarize long messages before reading aloud",
  summarizeNote: "Summarizing takes time. If it isn't ready in time, the original is read.",
  summaryEngine: "Summary engine",
  summaryEngineNote: "fm requires macOS 27 or later. Otherwise the original is read.",
  summaryClaude: "Claude Haiku",

  sectionEmotion: "Emotion",
  emotionEngine: "Emotion engine",
  emotionEngineNote: "fm requires macOS 27 or later. Otherwise falls back to Dictionary.",
  emotionDictionary: "Dictionary",

  sectionShortcuts: "Shortcuts",
  hotKeyMute: "Toggle mute",
  hotKeyHide: "Show/hide character",
  hotKeyNeedsModifier: "Choose at least one modifier (⌃ ⌥ ⇧ ⌘).",
  hotKeyClashRejected: (label) => `Already used by "${label}".`,

  sectionReset: "Reset",
  resetAll: "Reset All Settings…",
  resetAllNote: "Also deletes the chosen model file. The speech engine and the character's position and size are kept.",
  confirmResetTitle: "Reset all settings?",
  confirmResetMessage:
    "Volume, motion, shortcuts, voice style, speaking speed, summary, summary engine, and emotion engine settings return to their defaults, and the chosen VRM model file is deleted. This can't be undone.",
  confirmResetOk: "Reset",
  confirmResetCancel: "Cancel",
  resetDone: (n) => (n > 0 ? `Restored defaults and deleted ${n} model file(s).` : "Restored defaults."),
  resetMascotFailed: (reason) => `Couldn't reset the mascot settings: ${reason}`,
  resetCoreDefaultsUnavailable: "Couldn't get the defaults.",
  resetKeyFailed: (key, reason) => `Couldn't reset ${key}: ${reason}`,

  settingsUnreadable: (reason) => `Couldn't read settings.json (${reason}).`,

  errorResponseUnreadable: (reason) => `Couldn't read the response: ${reason}`,
  errorHttp: (status) => `The server returned an error (HTTP ${status}).`,
  errorEnvOverride: (key) => `Can't change: locked by an environment variable (${key}).`,
  errorReadonlyKey: (key) => `This setting can't be changed (${key}).`,
  errorInvalidValue: (key) => `Value out of range (${key}).`,
  errorEngineUnreachable: "Can't reach the speech engine.",
  errorSynthesisUnavailable: "Couldn't synthesize speech.",
  errorTtsDisabled: "Speech is disabled on the server (ttsEnabled).",
  errorConfigUnreadable: "Couldn't read config.json, so nothing was written.",
  errorConfigUnwritable: "Couldn't write to config.json.",
  errorTooManyRequests: "Too many requests. Please wait a moment.",
  errorUnknown: (error, key) => `${error} (${key})`,
};
