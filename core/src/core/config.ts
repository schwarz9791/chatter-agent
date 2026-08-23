/**
 * 設定ストア。環境変数 > config.json > 既定値。
 *
 * 守る性質:
 *
 * - `SPECS` の `satisfies` で全キーの網羅を型で担保する
 * - `mtime`+`size` のスタンプが変わったときだけ読み直す（参照時に stat するだけ。watch はしない）
 * - 壊れた JSON では直前の値を維持する（書き込み途中を読んだ瞬間に挙動が飛ばないように）
 * - 不正値・未知キーは警告して既定値で動き続ける（**throw しない**）
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

  // ── 以下は server（音声合成）だけが読む ─────────────────────────────
  // ★ ここに置くこと。config.ts は全バイナリが同じ SPECS を共有していて、
  //   載っていないキーは「未知のキー」として警告される。別ファイルに分けると
  //   chatter-agent-speak が毎 delta の起動ごとに警告を吐く。
  //
  // ★ #29 で読み手が player → server に移った。**キー名も意味も変えていない**
  //   （改名すると、既存の config.json に残った旧キーが全バイナリで未知キー警告を出す。
  //   #11 で `speechLogGenerations` を廃止したときに実際に踏んだ）。

  /**
   * 音声を合成してクライアントへ配るか。
   *
   * `false` にすると配信フレームの `audio` が常に `null` になり、`GET /audio/…` も
   * 404 を返す。クライアントは何も鳴らさずに ack する。**テキストの配信は止まらない**ので、
   * 自前で合成するクライアントや、字幕・表情だけを使うクライアントの逃げ道になる。
   */
  ttsEnabled: boolean;
  /**
   * 音声合成エンジンの baseUrl。AivisSpeech / VOICEVOX の互換 API を叩く。
   * 既定は AivisSpeech.app を単体起動したときの標準ポート。
   * （cc-mascot はエンジンを自分で spawn して 8564 を使うので、そちらとは別物）
   */
  ttsBaseUrl: string;
  /** 話者のスタイル ID。既定は AivisSpeech 標準同梱の Anneli（ノーマル）。VOICEVOX は 0 始まりの小さい整数 */
  ttsSpeakerId: number;
  /** `/audio_query` と `/synthesis` の1リクエストあたりの上限。Node の fetch に既定タイムアウトは無い */
  synthesisTimeoutMs: number;

  // ── 以下は発話クライアント（player）だけが読む ─────────────────────────

  /**
   * 再生中の1件を含めて、いくつ先まで**音声を取りに行く**か。0 なら完全直列。
   *
   * ★ #29 で合成がサーバーへ移ったが、**このキーの意味は変わっていない**。
   *   サーバーは投機的な先読みを持たず、GET が来たときに合成するので、
   *   この窓がそのまま合成の需要信号になる。
   *
   * 大きくすると合成待ちは減るが、`/synthesis` は CPU 律速なので
   * 同時に投げすぎると**先頭の合成が遅くなる**＝1文目の発話開始が遅れる。
   */
  synthesisLookahead: number;
  /**
   * `GET /audio/…` の1リクエストあたりの上限。
   *
   * ★ **サーバー側の `synthesisTimeoutMs` より長くすること。** この GET は
   *   「合成が終わるまで待つ」ので、短いとクライアント側のタイムアウトが先に効いて
   *   503（あとで取りに来い）ではなく転送エラーとして扱われ、試行回数を消費する。
   * ★ 省略できない。Node の fetch に既定のタイムアウトは無く、返らない相手を掴むと
   *   head-of-line blocking で**以後すべてが無音になり、エラーも出ない**。
   */
  audioFetchTimeoutMs: number;
  /** WAV を再生するコマンド。macOS の afplay 以外にも差し替えられる（検証では /usr/bin/true 等を使う） */
  playerCommand: string;
  /** `playerCommand` に渡す引数。`{file}` が WAV のパスに置換される。シェルは噛ませない */
  playerArgs: string[];
  /**
   * player の接続先。空なら `port` と `host` から導出する。
   *
   * ★ `host` をそのまま使えない。既定の `0.0.0.0` は **bind アドレスであって接続先ではない**。
   *   導出では `0.0.0.0` / `::` を `127.0.0.1` に読み替える。
   */
  playerServerUrl: string;
  /**
   * これより古い発話は音を出さずに ack だけして飛ばす。0 なら無効。
   *
   * TTS + 再生は生成よりずっと遅いので、バックログを抱えると数分前の発言を今喋ることになる。
   * server 側の起動時の掃除（10秒）と同じ判断をクライアント側にも置けるようにしてあるが、
   * 既定は無効。実機で遅れを測ってから決める。
   */
  speechMaxAgeMs: number;

  // ── 以下は AI要約（summarizer）だけが読む ─────────────────────────
  // ★ ここに置くこと。理由は player のキーと同じ（→上の註記）だが、**警告を吐く側が逆になる**。
  //   これは chatter-agent-speak だけが読むキーなので、別ファイルに分けると
  //   server と player が起動のたびに未知キー警告を吐く。

  /**
   * 長いメッセージを CLI エージェントで要約してから読み上げるか。
   *
   * ★ 既定 OFF。有効にすると `aiSummaryThreshold` を超えたメッセージのたびに `claude -p` が
   *   走るので、ユーザーの課金を消費する。
   * ★ **代償は遅延の方が大きい。** 要約1回の所要時間は**入力の長さから予測できない**
   *   （実測10件で相関が見られず、短い入力がタイムアウトし長い入力が10秒台で返ることがあった。
   *   → `aiSummaryTimeoutMs`）。ばらつきの支配要因は AI の生成時間で、環境（マシン・
   *   ネットワーク・モデル）でも変わる。`final` の待ちが中央値0秒（→ `CLAUDE.md`）なのに対し、
   *   要約に掛かった秒数は丸ごと発話の遅れとして乗る。**秒数を仕様として扱わないこと**。
   */
  aiSummaryEnabled: boolean;
  /**
   * この文字数を超えたメッセージだけ要約する。
   * 実測で1メッセージ平均 184.5 文字（`speech.jsonl` 229件）なので、200 だと「長い方だけ」が対象になる。
   */
  aiSummaryThreshold: number;
  /**
   * 要約に使う CLI。絶対パスも可。
   * 差し替えられることが、そのまま検証の自動化になっている（`playerCommand` / `playerArgs` と同じ論法。
   * → `docs/core.md`）。
   */
  aiSummaryCommand: string;
  /**
   * `--model` に渡す値。**空文字なら `--model` を渡さず CLI の既定に従う。**
   * 既定が `haiku` なのは、2〜3文120文字の要約に上位モデルは過剰で、コストと遅延の両方が乗るため。
   */
  aiSummaryModel: string;
  /**
   * 要約1回の上限。超えたら要約を諦めて原文をそのまま読み上げる（発話は止めない）。
   *
   * ★ 既定 60 秒。実測10件（`summarizer.log`）では入力の長さと所要時間が相関せず
   *   （→ `aiSummaryEnabled`）、旧既定の30秒では3/10（30%）がタイムアウトしていた。
   *   タイムアウトすると、要約がいちばん効くはずの長い発言ほど、待たされた末に原文が
   *   そのまま全文読み上げられるという逆転が起きる。**「実測値の N 倍」という決め方はしていない**
   *   （相関しないものに倍率を掛けても意味が無いため）。60秒は「実測でタイムアウトが
   *   3割起きた30秒では短すぎた」という根拠だけを持つ値で、秒数自体は仕様ではない。
   */
  aiSummaryTimeoutMs: number;
  /**
   * 1回のドレインで要約してよいメッセージ数の上限。既定は3、上限は8（`parseAiSummaryMaxPerDrain`）。
   *
   * ★ 上限が要る理由: CLI が長時間動かなかった後のドレインでは長文が複数溜まっていることがあり、
   *   全部要約すると `aiSummaryTimeoutMs × N` だけ発話が遅れる。超えた分は要約せず原文で出す。
   * ★ **上限8は `aiSummaryTimeoutMs` の既定と連動している。** 1回のドレインは最悪
   *   `aiSummaryMaxPerDrain × aiSummaryTimeoutMs` の間ロックを保持しうるので、既定60秒と
   *   掛けると上限8でも最悪480秒。
   * ★ **上限8は `workerState.ts` の `SUMMARIZER_SESSION_LIMIT`（64）とも連動している。**
   *   64 ÷ 8 = 8ドレイン分の要約セッションIDを覚えられる計算になっている。上限をこれより
   *   大きくすると、リング（`summarizerSessionIds`）が覚えていられるドレイン数が減り、
   *   要約 CLI の出力が遅れて spool に着いたときに、無限ループ防止の第2層
   *   （`isSummarizerSession`）が既に忘れている確率が上がる。
   * ★ **「1ドレイン内で自分のセッションIDが押し出される」わけではない**（かつてここにそう
   *   書いてあったが誤り）。1ドレインで追加される ID は高々 `aiSummaryMaxPerDrain` 個なので、
   *   自分を押し出すには 64 を超える必要がある。上限8の実効的な根拠はロック保持時間
   *   （8 × `aiSummaryTimeoutMs` = 既定60秒なら480秒）の方。
   */
  aiSummaryMaxPerDrain: number;
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

    ttsEnabled: true,
    ttsBaseUrl: "http://127.0.0.1:10101",
    ttsSpeakerId: 888753760,
    synthesisTimeoutMs: 30_000,
    synthesisLookahead: 3,
    audioFetchTimeoutMs: 45_000,
    playerCommand: "afplay",
    playerArgs: ["{file}"],
    playerServerUrl: "",
    speechMaxAgeMs: 0,

    aiSummaryEnabled: false,
    aiSummaryThreshold: 200,
    aiSummaryCommand: "claude",
    aiSummaryModel: "haiku",
    aiSummaryTimeoutMs: 60_000,
    aiSummaryMaxPerDrain: 3,
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

/**
 * `CHATTER_AGENT_DISABLE` — hook と CLI をまとめて黙らせる。無限ループ防止の第1層で、
 * 要約プロセスはこれを付けて spawn される（設計書 §4-3）。config のキーではないので
 * `SPECS` には載せず、環境変数だけを見る。
 *
 * ★ 「設定されていれば無効」にしないこと。`CHATTER_AGENT_DISABLE=0` を「無効化の解除」の
 *   つもりで書くと、逆に全発話が止まる。しかも診断が出ないので原因に辿り着けない。→ #4
 *
 * ★ 判定は `plugin/scripts/_lib.sh` の `chatter_disabled` と同じトークン集合に揃える。
 *   hook 側だけが黙る / CLI 側だけが黙るという半端な状態を作らないため。
 *   未知の値は「無効化しない」に倒す（`parseBoolean` が `undefined` を返すため）。
 */
export function isSpeakDisabled(env: NodeJS.ProcessEnv = process.env): boolean {
  return parseBoolean(env.CHATTER_AGENT_DISABLE) === true;
}

function toInt(raw: unknown): number | undefined {
  const n = typeof raw === "number" ? raw : typeof raw === "string" ? Number(raw.trim()) : NaN;
  return Number.isInteger(n) ? n : undefined;
}

const parsePort: Parser<number> = (raw) => {
  const n = toInt(raw);
  return n !== undefined && n >= 1 && n <= 65535 ? n : undefined;
};

/**
 * `aiSummaryMaxPerDrain` 専用。`parsePositiveInt` は `n >= 1` しか見ないので、
 * `aiSummaryMaxPerDrain: 1000000` のような値がそのまま素通りしてしまう（issue #38 レビュー G1-b）。
 * `parsePort`（1〜65535）と同じ「上限付きパーサ」の形。
 *
 * ★ 上限 8 の根拠は2つ、いずれも `aiSummaryTimeoutMs` / `workerState.ts` の
 *   `SUMMARIZER_SESSION_LIMIT` と連動しているので、上限だけを単独で動かさないこと。
 *   実効的な根拠は1のロック保持時間の方（2はドレインをまたぐ履歴の深さの話であり、
 *   1ドレイン内で押し出しが起きるわけではない）:
 *
 * 1. 1回のドレインで要約する件数 × `aiSummaryTimeoutMs` の間ロックを保持する。
 *    `aiSummaryTimeoutMs` 自体には実質的な上限が無い（`parseTimeoutMs` は `MAX_TIMER_MS`
 *    ≒ 24.8日でしか縛らない）ので、480秒（既定60秒 × 上限8）はタイムアウトが既定値のときの
 *    数字であって、強制された天井ではない（→ `aiSummaryTimeoutMs` の docstring）。
 * 2. `workerState.ts` の `SUMMARIZER_SESSION_LIMIT`（64）は「64 ÷ 8 = 8ドレイン分の
 *    要約セッションIDを覚えられる」という計算で決めてある。上限をこれより緩めると、
 *    ドレインをまたいで覚えていられる履歴が浅くなり、要約 CLI の出力が遅れて spool に
 *    着いたときに無限ループ防止の第2層（`isSummarizerSession`）が既に忘れている確率が上がる。
 */
const parseAiSummaryMaxPerDrain: Parser<number> = (raw) => {
  const n = toInt(raw);
  return n !== undefined && n >= 1 && n <= 8 ? n : undefined;
};

const parsePositiveInt: Parser<number> = (raw) => {
  const n = toInt(raw);
  return n !== undefined && n >= 1 ? n : undefined;
};

// 0 を「無効」「直列」として意味づけているキー用（synthesisLookahead / speechMaxAgeMs）。
// VOICEVOX の話者 ID も 0 から始まるのでこちらを使う
const parseNonNegativeInt: Parser<number> = (raw) => {
  const n = toInt(raw);
  return n !== undefined && n >= 0 ? n : undefined;
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
 * `setTimeout` / `AbortSignal.timeout` が受け付ける上限（2^31-1）。
 *
 * ★ これを超えると静かに壊れる。Node 24 実測: `AbortSignal.timeout(4294967295)` は
 *   `TimeoutOverflowWarning` を出して **1ms に化け**（全リクエストが即 abort し、
 *   「エンジンがタイムアウトしました」という**設定ではなくエンジンを指すメッセージ**が出る）、
 *   `AbortSignal.timeout(99999999999)` は `RangeError` を投げる（`waitForEngine` が
 *   永久にループして接続しない）。「実質無制限」のつもりで大きい数を書くと踏む
 */
const MAX_TIMER_MS = 2_147_483_647;

const parseTimeoutMs: Parser<number> = (raw) => {
  const n = toInt(raw);
  return n !== undefined && n >= 1 && n <= MAX_TIMER_MS ? n : undefined;
};

/**
 * 再生コマンドの引数。
 *
 * ★ `parseStringList` を流用しないこと。あれは**集合**（`allowedOrigins`）用で、空入力に対して
 *   `undefined` ではなく `[]` を返す。`collect()` は `undefined` のときだけ既定値へ落とすので、
 *   `CHATTER_AGENT_PLAYER_ARGS=`（ラッパーや CI で普通に起きる）が既定の `["{file}"]` を
 *   上書きし、`afplay` が引数なしで起動して**全文が再生に失敗し、ack されてキューから消える**。
 *   位置引数として意味を成すかどうかをここで検証する。
 */
const parsePlayerArgs: Parser<string[]> = (raw) => {
  let items: unknown[];
  if (typeof raw === "string") items = raw.split(",");
  else if (Array.isArray(raw)) items = raw;
  else return undefined;

  const out: string[] = [];
  for (const item of items) {
    if (typeof item !== "string") return undefined;
    const trimmed = item.trim();
    if (trimmed) out.push(trimmed);
  }

  // 引数ゼロ、あるいは WAV のパスを渡す先が無い並びは設定ミス。既定値に倒して警告を出す
  if (out.length === 0) return undefined;
  if (!out.some((arg) => arg.includes("{file}"))) return undefined;
  return out;
};

/**
 * スキームを絞った URL のパーサを作る。
 *
 * 素通しにすると、`localhost:10101`（スキーム忘れ）や末尾スラッシュ付きが
 * そのまま `${baseUrl}/audio_query` に連結されて、症状が「無音」の設定ミスになる。
 * ここで弾けば「不正です。既定値を使います」の警告が出る。
 */
function makeUrlParser(protocols: string[]): Parser<string> {
  return (raw) => {
    const text = parseNonEmptyString(raw);
    if (text === undefined) return undefined;
    let url: URL;
    try {
      url = new URL(text);
    } catch {
      return undefined;
    }
    if (!protocols.includes(url.protocol)) return undefined;
    // 末尾スラッシュを落として連結の形を1つに揃える。`new URL("http://h:1")` は
    // toString() が "http://h:1/" を返すので、生の文字列側で処理する
    return text.replace(/\/+$/, "");
  };
}

/**
 * `aiSummaryModel` 専用。**空文字を「指定なし」として通す唯一のパーサ。**
 *
 * ★ `parseNonEmptyString` に統一したくなるが、ここだけは空に意味がある
 *   （`--model` を渡さず要約 CLI 自身の既定モデルに従う、という指定）。`parsePlayerArgs` が
 *   空入力を弾いているのは逆に「空だと `afplay` が引数なしで起動し全文が無音になる」事故を
 *   防ぐためで、両者は「空を落とし穴として扱う」点で対称なだけで、結論（空を通すか弾くか）は
 *   キーごとの意味に従って逆になる。
 *   trim だけはする（`CHATTER_AGENT_AI_SUMMARY_MODEL=" haiku "` のような値がそのまま
 *   `--model` の引数に渡らないように。`parseNonEmptyString` と同じ理由）。
 */
const parseAiSummaryModel: Parser<string> = (raw) => (typeof raw === "string" ? raw.trim() : undefined);

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

  ttsEnabled: { env: "CHATTER_AGENT_TTS_ENABLED", parse: parseBoolean },
  ttsBaseUrl: { env: "CHATTER_AGENT_TTS_URL", parse: makeUrlParser(["http:", "https:"]) },
  ttsSpeakerId: { env: "CHATTER_AGENT_TTS_SPEAKER_ID", parse: parseNonNegativeInt },
  synthesisTimeoutMs: { env: "CHATTER_AGENT_SYNTHESIS_TIMEOUT_MS", parse: parseTimeoutMs },
  synthesisLookahead: { env: "CHATTER_AGENT_SYNTHESIS_LOOKAHEAD", parse: parseNonNegativeInt },
  audioFetchTimeoutMs: { env: "CHATTER_AGENT_AUDIO_FETCH_TIMEOUT_MS", parse: parseTimeoutMs },
  playerCommand: { env: "CHATTER_AGENT_PLAYER_COMMAND", parse: parseNonEmptyString },
  playerArgs: { env: "CHATTER_AGENT_PLAYER_ARGS", parse: parsePlayerArgs },
  playerServerUrl: { env: "CHATTER_AGENT_PLAYER_SERVER_URL", parse: makeUrlParser(["ws:", "wss:"]) },
  speechMaxAgeMs: { env: "CHATTER_AGENT_SPEECH_MAX_AGE_MS", parse: parseNonNegativeInt },

  aiSummaryEnabled: { env: "CHATTER_AGENT_AI_SUMMARY_ENABLED", parse: parseBoolean },
  aiSummaryThreshold: { env: "CHATTER_AGENT_AI_SUMMARY_THRESHOLD", parse: parsePositiveInt },
  aiSummaryCommand: { env: "CHATTER_AGENT_AI_SUMMARY_COMMAND", parse: parseNonEmptyString },
  aiSummaryModel: { env: "CHATTER_AGENT_AI_SUMMARY_MODEL", parse: parseAiSummaryModel },
  aiSummaryTimeoutMs: { env: "CHATTER_AGENT_AI_SUMMARY_TIMEOUT_MS", parse: parseTimeoutMs },
  aiSummaryMaxPerDrain: { env: "CHATTER_AGENT_AI_SUMMARY_MAX_PER_DRAIN", parse: parseAiSummaryMaxPerDrain },
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
