/**
 * 設定ストア。環境変数 > config.json > 既定値。
 *
 * 守る性質:
 *
 * - `SPECS` の `satisfies` で全キーの網羅を型で担保する
 * - `mtime`+`size` のスタンプが変わったときだけ読み直す（参照時に stat するだけ。watch はしない）
 * - 壊れた JSON では直前の値を維持する（書き込み途中を読んだ瞬間に挙動が飛ばないように）
 * - 不正値・未知キーは警告して既定値で動き続ける（**throw しない**）
 *
 * 上流にあった要約関連のキー（aiSummary*）は summarizer を移植していないので落としてある。
 * summarizer を入れるときに戻す。
 */

import * as fs from "fs";
import { currentPathEnv, getConfigFilePath } from "./paths";
import type { PathEnv } from "./paths";

export interface ChatterAgentConfig {
  /** WebSocket の待受ポート。8563 / 8564 は AivisSpeech 等が使うので避けてある */
  port: number;
  /**
   * WebSocket の bind アドレス。LAN の Android から繋ぐため既定は 0.0.0.0。
   * ★ 無認証で LAN 全体に露出する。信頼できないネットワークでは 127.0.0.1 にすること。
   */
  host: string;
  /** 応答待ち通知（kind: "prompt"）を読み上げるか */
  speakPrompts: boolean;
  /** 記録（speech.jsonl）がこのサイズを超えたら speech.1.jsonl に退避する */
  speechLogMaxBytes: number;
  /**
   * 配信キューに溜めておく上限。超えたら古い方から捨てる。
   *
   * クライアントが繋がっていなければ ack は来ないので、これが唯一の歯止めになる。
   * 古い発話は無価値（起動時にも全部捨てる）なので、数百あれば足りる。
   */
  speechQueueMaxEntries: number;
  /** CLI が起動しないまま終わった spool の孤児を、この時間より古ければ掃除する */
  spoolMaxAgeHours: number;
  /**
   * WebSocket 接続を許可する `Origin` の完全一致リスト（前方一致・ワイルドカードは無し）。
   * 既定は空＝`Origin` 付きの接続はすべて拒否（今までの挙動のまま）。
   * Electron の renderer（`file://` なら `null`、`http://localhost:*` ならそのオリジン）や
   * Unity WebGL ビルドを繋ぐときに、必要な分だけ足す。
   */
  allowedOrigins: string[];
}

export function createDefaultConfig(): ChatterAgentConfig {
  return {
    port: 8570,
    host: "0.0.0.0",
    speakPrompts: true,
    speechLogMaxBytes: 5 * 1024 * 1024,
    speechQueueMaxEntries: 500,
    spoolMaxAgeHours: 6,
    allowedOrigins: [],
  };
}

type ConfigKey = keyof ChatterAgentConfig;

/** JSON 由来の値と環境変数の文字列の両方を受けるので引数は unknown。不正なら undefined */
type Parser<T> = (raw: unknown) => T | undefined;

const TRUTHY = ["1", "true", "yes", "on"];
const FALSY = ["0", "false", "no", "off"];

const parseBoolean: Parser<boolean> = (raw) => {
  if (typeof raw === "boolean") return raw;
  if (typeof raw !== "string") return undefined;
  const v = raw.trim().toLowerCase();
  if (TRUTHY.includes(v)) return true;
  if (FALSY.includes(v)) return false;
  return undefined;
};

function toInt(raw: unknown): number | undefined {
  const n = typeof raw === "number" ? raw : typeof raw === "string" ? Number(raw.trim()) : NaN;
  return Number.isInteger(n) ? n : undefined;
}

const parsePort: Parser<number> = (raw) => {
  const n = toInt(raw);
  return n !== undefined && n >= 1 && n <= 65535 ? n : undefined;
};

const parsePositiveInt: Parser<number> = (raw) => {
  const n = toInt(raw);
  return n !== undefined && n >= 1 ? n : undefined;
};

// trim した値を返すこと。判定にだけ使って生値を返すと、CHATTER_AGENT_HOST=" 127.0.0.1 "
// のような値がそのまま listen() へ渡る
const parseNonEmptyString: Parser<string> = (raw) => (typeof raw === "string" && raw.trim() ? raw.trim() : undefined);

// 環境変数（カンマ区切りの文字列）と config.json（配列）の両方を受ける。
// 要素が1つでも文字列以外なら、部分的に取り込まず配列ごと undefined にする
// （「一部だけ有効な許可リスト」は事故ると気づきにくいので、丸ごと既定値に倒す）
const parseStringList: Parser<string[]> = (raw) => {
  let items: unknown[];
  if (typeof raw === "string") {
    items = raw.split(",");
  } else if (Array.isArray(raw)) {
    items = raw;
  } else {
    return undefined;
  }

  const out: string[] = [];
  for (const item of items) {
    if (typeof item !== "string") return undefined;
    const trimmed = item.trim();
    if (trimmed) out.push(trimmed);
  }
  return out;
};

/**
 * キーの定義。satisfies で ChatterAgentConfig の全キーを網羅していることを型で担保する
 * （satisfies は型のみなので erasableSyntaxOnly に抵触しない）。
 * キーを増やすときは ChatterAgentConfig と SPECS の両方を直さないとコンパイルが通らない。
 */
const SPECS = {
  port: { env: "CHATTER_AGENT_PORT", parse: parsePort },
  host: { env: "CHATTER_AGENT_HOST", parse: parseNonEmptyString },
  speakPrompts: { env: "CHATTER_AGENT_SPEAK_PROMPTS", parse: parseBoolean },
  speechLogMaxBytes: { env: "CHATTER_AGENT_SPEECH_LOG_MAX_BYTES", parse: parsePositiveInt },
  speechQueueMaxEntries: { env: "CHATTER_AGENT_SPEECH_QUEUE_MAX_ENTRIES", parse: parsePositiveInt },
  spoolMaxAgeHours: { env: "CHATTER_AGENT_SPOOL_MAX_AGE_HOURS", parse: parsePositiveInt },
  allowedOrigins: { env: "CHATTER_AGENT_ALLOWED_ORIGINS", parse: parseStringList },
} as const satisfies { [K in ConfigKey]: { env: string; parse: Parser<ChatterAgentConfig[K]> } };

const CONFIG_KEYS = Object.keys(SPECS) as ConfigKey[];

export interface ConfigStore {
  get<K extends ConfigKey>(key: K): ChatterAgentConfig[K];
  /** 起動ログ用のスナップショット */
  snapshot(): Readonly<ChatterAgentConfig>;
  readonly filePath: string;
}

export interface ConfigStoreDeps {
  filePath?: string;
  env?: NodeJS.ProcessEnv;
  defaults?: ChatterAgentConfig;
  pathEnv?: PathEnv;
}

export function createConfigStore(deps: ConfigStoreDeps = {}): ConfigStore {
  const filePath = deps.filePath ?? getConfigFilePath(deps.pathEnv ?? currentPathEnv());
  const defaults = deps.defaults ?? createDefaultConfig();

  // 同じ問題を毎回警告しないようにする（get はメッセージごとに呼ばれる）
  const warned = new Set<string>();
  const warnOnce = (key: string, message: string) => {
    if (warned.has(key)) return;
    warned.add(key);
    console.warn(message);
  };

  function collect(source: Record<string, unknown>, origin: string): Partial<ChatterAgentConfig> {
    const out: Partial<ChatterAgentConfig> = {};
    for (const key of CONFIG_KEYS) {
      const raw = source[key];
      if (raw === undefined) continue;
      const parsed = SPECS[key].parse(raw);
      if (parsed === undefined) {
        warnOnce(
          `${origin}:${key}`,
          `[Config] ${origin} の ${key} が不正です (${JSON.stringify(raw)})。既定値を使います`,
        );
        continue;
      }
      // key がユニオン型なので out[key] の型が絞り込めない。パーサの戻り値が
      // ChatterAgentConfig[K] に一致することは SPECS の satisfies で保証済み。
      (out as Record<ConfigKey, unknown>)[key] = parsed;
    }
    return out;
  }

  // 環境変数はプロセス実行中に変わらないので起動時に1回だけ読む
  const envSource = deps.env ?? process.env;
  const envValues: Record<string, unknown> = {};
  for (const key of CONFIG_KEYS) {
    const raw = envSource[SPECS[key].env];
    if (raw !== undefined) envValues[key] = raw;
  }
  const overrides = collect(envValues, "環境変数");

  let fileValues: Partial<ChatterAgentConfig> = {};
  let merged: ChatterAgentConfig = { ...defaults, ...overrides };
  /** `${mtimeMs}:${size}`。ファイルが無いときは null */
  let stamp: string | null = null;
  let loaded = false;

  function readFileValues(): Partial<ChatterAgentConfig> | undefined {
    let text: string;
    try {
      text = fs.readFileSync(filePath, "utf-8");
    } catch (err) {
      warnOnce("file:read", `[Config] ${filePath} を読めませんでした: ${String(err)}`);
      return undefined;
    }

    let parsed: unknown;
    try {
      parsed = JSON.parse(text);
    } catch (err) {
      warnOnce("file:json", `[Config] ${filePath} のJSONが壊れています: ${String(err)}。直前の値を使い続けます`);
      return undefined;
    }

    if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) {
      // undefined を返すと呼び出し側は fileValues を触らない＝直前の値が残る。
      // メッセージも実態に合わせる（「既定値を使います」ではない）
      warnOnce("file:shape", `[Config] ${filePath} のトップレベルがオブジェクトではありません。直前の値を使い続けます`);
      return undefined;
    }

    const record = parsed as Record<string, unknown>;
    for (const key of Object.keys(record)) {
      if (!CONFIG_KEYS.includes(key as ConfigKey)) {
        warnOnce(`file:unknown:${key}`, `[Config] ${filePath} の未知のキー "${key}" は無視されます`);
      }
    }
    return collect(record, "config.json");
  }

  function refresh(): void {
    let next: string | null = null;
    try {
      const st = fs.statSync(filePath);
      next = `${st.mtimeMs}:${st.size}`;
    } catch {
      // 設定ファイルが無いのは正常（すべて既定値と環境変数で決まる）
    }
    if (loaded && next === stamp) return;
    loaded = true;
    stamp = next;

    if (next === null) {
      fileValues = {};
    } else {
      // パースに失敗したときは直前の値を維持する
      // （書き込み途中の壊れたJSONを読んだ瞬間に挙動が飛ばないように）
      const parsed = readFileValues();
      if (parsed) fileValues = parsed;
    }
    merged = { ...defaults, ...fileValues, ...overrides };
  }

  return {
    filePath,
    get(key) {
      refresh();
      return merged[key];
    },
    snapshot() {
      refresh();
      return { ...merged };
    },
  };
}
