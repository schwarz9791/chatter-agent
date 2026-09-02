/**
 * 制御 API（`PATCH /v1/config`）が受けた変更を検証し、書き戻す JSON を組み立てる。**純粋関数。**
 *
 * ★ **パーサを複製しない。** 検証は `parseConfigValue`（＝ `SPECS` のパーサそのもの）だけを
 *   通す。複製した瞬間に「HTTP からは通るがファイルからは通らない値」が生まれ、
 *   どちらが正しいのかコードから読めなくなる（→ `core/config.ts`）。
 *
 * ★ **生の JSON をベースにする。** `snapshot()` の値で全体を上書きすると、
 *   **他バイナリ向けの未知キーが消える**（`collect()` は `SPECS` に無いキーを捨てるため）。
 */

import { isConfigKey, parseConfigValue, type ConfigKey, type ConfigOrigin } from "./config";

/**
 * 制御 API から書けないキー。**理由が3種類あるので分けて持つ。**
 *
 * ★ 1つの配列に混ぜないこと。「なぜ書けないのか」で対応が変わる —— (a) と (c) は緩めては
 *   いけないセキュリティ境界、(b) は**将来サーバーが再起動できるようになれば書けるように
 *   なりうる**。
 */

/**
 * (a) **コマンド実行に繋がる。**
 *
 * ループバック限定にしてあっても、「設定を1行書き換えるだけで任意コマンド実行」は
 * 別格の壊れ方をする（ブラウザ経由の CSRF がもし1つでも通れば、それがそのまま RCE になる）。
 * 絞りを3重にしてある（→ `server/controlApi.ts`）のは**この行を守るため**でもあるので、
 * 「ループバックなんだから良いだろう」で緩めないこと。
 *
 * ★ `aiSummaryModel` は載せていない。`execFile` にシェルを噛ませていないので
 *   `--model <値>` の**値**にしかならず、新しいコマンドにも別のフラグにもならない。
 */
const READONLY_EXECUTABLE: readonly ConfigKey[] = [
  "ttsSpawnCommand",
  "ttsSpawnArgs",
  "playerCommand",
  "playerArgs",
  "aiSummaryCommand",
];

/**
 * (b) **書いても効かない。**
 *
 * `host` / `port` は `listen()` 済み、`allowedOrigins` は `createAudioHttpServer` の冒頭で
 * `Set` に焼かれている（→ `server/httpServer.ts`）。どれも再起動まで反映されない。
 *
 * ★ **「効かないだけなら書かせてもよい」ではない。** 設定 UI に出た項目が、変えても何も
 *   起きないというのは**いちばん悪い見え方**（壊れているのか自分の操作が悪いのか分からない）。
 *   403 で明示的に断る方が親切。
 */
const READONLY_UNTIL_RESTART: readonly ConfigKey[] = ["host", "port", "allowedOrigins"];

/**
 * (c) **本文の外部送信路になる。**
 *
 * `ttsBaseUrl` を書き換えると、以後 `ttsFor(currentVoice())` は Claude Code の
 * **全メッセージ本文**をそのホストの `/audio_query` へ POST する。`currentVoice()` は
 * 毎回 `config.get` するので（→ `server/index.ts`）**再起動も要らず、次の1文から**そうなる。
 * しかも音が鳴らなくなるだけなので、**利用者から見た症状は「無音」だけ**で、本文が
 * 出ていることには気付けない。(a) と同じ「設定を1行書き換えるだけ」の壊れ方。
 *
 * ★ 設定パネルはこのキーを一度も書かない（書くのは `ttsSpeakerId` / `ttsSpeedScale` /
 *   `aiSummaryEnabled` の3つだけ）。塞いでも UI は何も失わない。
 *
 * ★ `playerServerUrl` は載せない。player は**受け手**なので、向き先を変えても
 *   本文が外へ出ることはない（別のサーバーの音声を再生させられるだけ）。
 */
const READONLY_EXFILTRATION: readonly ConfigKey[] = ["ttsBaseUrl"];

const READONLY_KEYS: readonly ConfigKey[] = [
  ...READONLY_EXECUTABLE,
  ...READONLY_UNTIL_RESTART,
  ...READONLY_EXFILTRATION,
];

export function isWritableConfigKey(key: ConfigKey): boolean {
  return !READONLY_KEYS.includes(key);
}

/** `GET /v1/config` の `writable`。**並び順は `SPECS` の宣言順**（呼び出し側が絞り込む） */
export function writableConfigKeys(keys: readonly ConfigKey[]): ConfigKey[] {
  return keys.filter(isWritableConfigKey);
}

export type ConfigPatchFailure =
  /** `SPECS` に無いキー */
  | { reason: "unknown_key"; key: string }
  /** 上の READONLY_KEYS */
  | { reason: "readonly_key"; key: string }
  /** 環境変数が勝っているので、ファイルに書いても効かない */
  | { reason: "env_override"; key: string }
  /** `SPECS` のパーサが弾いた */
  | { reason: "invalid_value"; key: string };

export type ConfigPatchResult =
  | { ok: true; next: Record<string, unknown>; changed: ConfigKey[] }
  | { ok: false; failure: ConfigPatchFailure };

/**
 * `base`（生の `config.json`）に `patch` を当てた結果を返す。
 *
 * ★★ **all-or-nothing。** 1つでも弾かれたら**何も書かない**。部分適用にすると
 *   「400 が返ったのに半分は書き換わっている」という、呼び出し側から復元できない状態になる。
 *
 * ★ 値は**パーサが正規化したもの**を書く（`" 127.0.0.1 "` → `"127.0.0.1"` など）。
 *   生値を書くと、ファイルから読み直したときに初めて正規化されることになり、
 *   レスポンスとファイルの中身がズレる。
 */
export function buildConfigPatch(
  base: Record<string, unknown>,
  patch: Record<string, unknown>,
  originOf: (key: ConfigKey) => ConfigOrigin,
): ConfigPatchResult {
  const next: Record<string, unknown> = { ...base };
  const changed: ConfigKey[] = [];

  for (const key of Object.keys(patch)) {
    if (!isConfigKey(key)) return { ok: false, failure: { reason: "unknown_key", key } };
    if (!isWritableConfigKey(key)) return { ok: false, failure: { reason: "readonly_key", key } };
    // ★ 優先順位は「環境変数 > ファイル > 既定」。環境変数が勝っているキーは、
    //   ファイルに書いても効かない。**黙って書いて効かないのがいちばん悪い**
    if (originOf(key) === "env") return { ok: false, failure: { reason: "env_override", key } };

    const parsed = parseConfigValue(key, patch[key]);
    if (parsed === undefined) return { ok: false, failure: { reason: "invalid_value", key } };

    next[key] = parsed;
    changed.push(key);
  }

  return { ok: true, next, changed };
}
