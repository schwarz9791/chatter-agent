// DOM に触らない純粋な判定。テストできるよう main.ts から分けてある。

import type {
  ChatterAgentConfig,
  ConfigEnvNames,
  ConfigKey,
  ConfigOrigin,
} from "../../core/src/core/config.ts";
import type { ServerError } from "./tauri.ts";
import type { Text } from "./text.ts";

export type PanelKey =
  | "ttsEngine"
  | "ttsSpeakerId"
  | "kokoroVoiceId"
  | "ttsSpeedScale"
  | "aiSummaryEnabled"
  | "aiSummaryBackend"
  | "emotionClassifier";

/** 環境変数名の写し。core の SPECS とずれたらコンパイルが通らない */
export const ENV_NAMES = {
  ttsEngine: "CHATTER_AGENT_TTS_ENGINE",
  ttsSpeakerId: "CHATTER_AGENT_TTS_SPEAKER_ID",
  kokoroVoiceId: "CHATTER_AGENT_KOKORO_VOICE_ID",
  ttsSpeedScale: "CHATTER_AGENT_TTS_SPEED_SCALE",
  aiSummaryEnabled: "CHATTER_AGENT_AI_SUMMARY_ENABLED",
  aiSummaryBackend: "CHATTER_AGENT_AI_SUMMARY_BACKEND",
  emotionClassifier: "CHATTER_AGENT_EMOTION_CLASSIFIER",
} as const satisfies { [K in PanelKey]: ConfigEnvNames[K] };

export interface ConfigSnapshot {
  values: ChatterAgentConfig;
  origins: Record<ConfigKey, ConfigOrigin>;
  writable: ConfigKey[];
  defaults: ChatterAgentConfig;
}

/** リセットで server 側へ戻すキー。`ttsEngine` は戻さない */
export const SERVER_RESET_KEYS = [
  "ttsSpeakerId",
  "kokoroVoiceId",
  "ttsSpeedScale",
  "aiSummaryEnabled",
  "aiSummaryBackend",
  "emotionClassifier",
] as const satisfies readonly PanelKey[];

/** 環境変数で固定されているときの注記。固定でなければ null */
export function envNote(
  key: PanelKey,
  origins: Partial<Record<ConfigKey, ConfigOrigin>> | undefined,
  t: Text,
): string | null {
  return origins?.[key] === "env" ? t.envOverridden(ENV_NAMES[key]) : null;
}

// ── ショートカット（マスコット設定に書く表記の規則） ──

export const HOTKEY_KEYS = [
  ..."abcdefghijklmnopqrstuvwxyz0123456789",
  "space",
  "return",
  "tab",
  "escape",
  ...Array.from({ length: 12 }, (_, i) => `f${i + 1}`),
] as const;

export interface HotKey {
  ctrl: boolean;
  opt: boolean;
  shift: boolean;
  cmd: boolean;
  key: string;
}

const MODIFIERS: Record<string, "ctrl" | "opt" | "shift" | "cmd"> = {
  cmd: "cmd",
  command: "cmd",
  meta: "cmd",
  opt: "opt",
  option: "opt",
  alt: "opt",
  ctrl: "ctrl",
  control: "ctrl",
  shift: "shift",
};

/** 不正（空・空の要素・キーが0または2つ以上・知らないキー・修飾キー無し）なら null */
export function parseHotKey(text: unknown): HotKey | null {
  if (typeof text !== "string" || text === "") return null;
  const out: HotKey = { ctrl: false, opt: false, shift: false, cmd: false, key: "" };
  for (const raw of text.split("+")) {
    const token = raw.trim().toLowerCase();
    if (token === "") return null;
    const mod = Object.hasOwn(MODIFIERS, token) ? MODIFIERS[token] : undefined;
    if (mod) {
      out[mod] = true;
      continue;
    }
    if (out.key !== "" || !(HOTKEY_KEYS as readonly string[]).includes(token)) return null;
    out.key = token;
  }
  if (out.key === "" || !(out.ctrl || out.opt || out.shift || out.cmd)) return null;
  return out;
}

/** `ctrl+opt+shift+cmd+<key>` の順。修飾キー無し・キー無しは空文字 */
export function formatHotKey(h: HotKey): string {
  if (h.key === "" || !(h.ctrl || h.opt || h.shift || h.cmd)) return "";
  return [h.ctrl && "ctrl", h.opt && "opt", h.shift && "shift", h.cmd && "cmd", h.key]
    .filter(Boolean)
    .join("+");
}

export function sameCombination(a: unknown, b: unknown): boolean {
  const l = parseHotKey(a);
  const r = parseHotKey(b);
  return l !== null && r !== null && formatHotKey(l) === formatHotKey(r);
}

export type HotKeyCheck = "ok" | "needsModifier" | "clash";

/** 書いてよいか。もう一方の設定値と同じ組み合わせなら "clash" */
export function checkHotKey(h: HotKey, other: unknown): HotKeyCheck {
  const text = formatHotKey(h);
  if (text === "") return "needsModifier";
  return sameCombination(text, other) ? "clash" : "ok";
}

// ── マスコット設定（キーが無いときの表示用の既定値） ──

export const MASCOT_DEFAULTS = {
  "audio.volume": 1.0,
  "audio.mute": false,
  "audio.muteHotKey": "ctrl+opt+m",
  "ui.hideHotKey": "ctrl+opt+h",
  "character.idleMotion": true,
  "character.cursorGaze": true,
  "character.blink": true,
  "character.vrm": "",
  // 範囲 0.5〜2.0・刻み 0.1 はマスコット（SettingsMapping）の写し
  "character.scale": 1.0,
  "display.frameRate": 30,
} as const;

export type MascotKey = keyof typeof MASCOT_DEFAULTS;
type Widen<T> = T extends number ? number : T extends boolean ? boolean : string;

/** 型が合わない値は既定値として扱う */
export function mascotGet<K extends MascotKey>(settings: unknown, key: K): Widen<(typeof MASCOT_DEFAULTS)[K]> {
  const fallback = MASCOT_DEFAULTS[key];
  let cur: unknown = settings;
  for (const part of key.split(".")) {
    if (typeof cur !== "object" || cur === null) return fallback as never;
    cur = (cur as Record<string, unknown>)[part];
  }
  return (typeof cur === typeof fallback ? cur : fallback) as never;
}

// ── モーションの確認 ──

export type MotionPreviewState = "notRunning" | "idleOff" | "loading" | "empty" | "ready";

/** 再生できない理由。`motions` は null が読み込み中、空配列が空 */
export function motionPreviewState(s: {
  running: boolean;
  idleMotion: boolean;
  motions: string[] | null;
}): MotionPreviewState {
  if (!s.running) return "notRunning";
  if (!s.idleMotion) return "idleOff";
  if (s.motions === null) return "loading";
  return s.motions.length === 0 ? "empty" : "ready";
}

// ── エラー文言 ──

function parseBody(body: string): { error?: string; key?: string } {
  try {
    const v: unknown = JSON.parse(body);
    return typeof v === "object" && v !== null ? (v as { error?: string; key?: string }) : {};
  } catch {
    return {};
  }
}

/** 失敗の値（`{status, body}` かそれ以外）から表示する文言を選ぶ */
export function errorMessage(err: ServerError | null, raw: unknown, key: string, t: Text): string {
  if (err === null) return String(raw);
  if (err.status === 0) {
    if (err.body === "unreachable") return t.coreUnreachable;
    if (err.body === "unsupported_host") return t.unsupportedHost;
    return t.errorResponseUnreadable(err.body);
  }
  const { error, key: k } = parseBody(err.body);
  const target = k ?? key;
  switch (error) {
    case "env_override":
      return t.errorEnvOverride(target);
    case "readonly_key":
      return t.errorReadonlyKey(target);
    case "invalid_value":
      return t.errorInvalidValue(target);
    case "engine_unreachable":
      return t.errorEngineUnreachable;
    case "synthesis_unavailable":
      return t.errorSynthesisUnavailable;
    case "tts_disabled":
      return t.errorTtsDisabled;
    case "config_unreadable":
      return t.errorConfigUnreadable;
    case "config_unwritable":
      return t.errorConfigUnwritable;
  }
  if (err.status === 409 && error === "not_lan") return t.pairingNotLan;
  if (err.status === 429) return t.errorTooManyRequests;
  return error ? t.errorUnknown(error, target) : t.errorHttp(err.status);
}

// ── ペアリング ──

export type PairingState = "none" | "pending" | "paired" | "expired" | "locked";

/** `m:ss`。負は `0:00` */
export function formatRemaining(ms: number): string {
  const s = Math.max(0, Math.ceil(ms / 1000));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
}

export interface PairingView {
  message: "pending" | "paired" | "expired" | "locked";
  showPin: boolean;
  showReissue: boolean;
  keepPolling: boolean;
}

/** 発行済みの PIN の状態から表示を決める。`none`（server が忘れた）は期限切れと同じ扱い */
export function pairingView(state: PairingState): PairingView {
  switch (state) {
    case "pending":
      return { message: "pending", showPin: true, showReissue: false, keepPolling: true };
    case "paired":
      return { message: "paired", showPin: false, showReissue: true, keepPolling: false };
    case "locked":
      return { message: "locked", showPin: false, showReissue: true, keepPolling: false };
    case "expired":
    case "none":
      return { message: "expired", showPin: false, showReissue: true, keepPolling: false };
  }
}
