/**
 * 設定パネル（[#76]）のための制御 API。`/v1/*`。
 *
 * ```
 * GET   /v1/health           200 {"ok":true,"version":"…"}
 * GET   /v1/speakers         200 {"speakers":[{"id":…,"label":"…"}]} / 503 engine_unreachable
 * GET   /v1/config           200 {"values":…,"origins":…,"writable":[…],"defaults":…}
 * PATCH /v1/config           200 {"values":…}（**適用後に読み直した値**）
 * POST  /v1/tts/preview      200 audio/wav（★ 固定文）
 * POST  /v1/summary/preview  200 {"summary":…,"outcome":…,"elapsedMs":…}（★ 失敗も 200）
 * ```
 *
 * ★ **HTTP を知らない層にしてある。** `req` / `res` は `httpServer.ts` が扱い、ここは
 *   「入力 → `ControlResponse`」だけを返す。`server/index.ts` と `wsServer.ts` が配線に
 *   徹して判断を `dispatcher` / `audioStore` / `engineProcess` に切り出しているのと同じ形
 *   （→ `docs/core.md`）。テストも実サーバーを立てずに書ける。
 *
 * ★ **書き込み口の絞り（ループバック限定 / `Origin` 禁止 / `Content-Type` 必須）はここに無い。**
 *   あれはルーティングの手前で効かせるものなので `httpServer.ts` が持つ。ここに置くと
 *   「ハンドラを1つ足したときに絞りを付け忘れる」形になる。
 *
 * [#76]: https://github.com/schwarz9791/chatter-agent/issues/76
 */

import * as fs from "fs";
import * as path from "path";
import { buildConfigPatch, writableConfigKeys } from "../core/configPatch";
import { configKeys, createDefaultConfig, type ConfigKey, type ConfigStore } from "../core/config";
import { writeFileAtomic } from "../core/atomicWrite";
import { VERSION } from "../core/version";
import { runSummaryPreview, type SummaryPreviewDeps } from "../summarizer/summaryPreview";

/**
 * テスト音声の固定文。
 *
 * ★★ **任意テキストを受けないこと。** 受けた瞬間、無認証（LAN 露出）の合成 API になる。
 *   `/synthesis` は CPU 律速なので、長文を並べるだけで実質 DoS になる
 *   （`audioStore` の `maxInFlight` が守っているのはキュー経由の合成だけ）。
 *
 * ★ 話者と話速の両方が耳で分かる長さにしてある。短すぎると速度の違いが分からない。
 */
export const TTS_PREVIEW_TEXT = "テスト音声です。この声と速さで読み上げます。";

/**
 * テスト要約の固定文。
 *
 * ★ **こちらも任意テキストを受けない。** 理由は上と別で、要約は `claude -p` を起こす＝
 *   **ユーザーの課金を消費する**。押した回数ぶんだけ、が守れる形にしておく。
 *
 * ★ 要約して意味のある長さ（`aiSummaryThreshold` の既定 200 文字より長い）にしてある。
 *   短いと「要約が原文より短くならない」で `invalid` になり、CLI は正常なのに
 *   失敗したように見える。
 */
export const SUMMARY_PREVIEW_TEXT = [
  "設定パネルのテスト要約です。",
  "この文章は、要約に使う CLI が正しく起動し、指示どおりに短い文章を返せるかを確かめるためだけに用意した固定のサンプルです。",
  "実際の読み上げでは、Claude Code が話した内容がそのままここに入ります。",
  "長いメッセージほど読み上げに時間がかかるため、一定の文字数を超えたものだけを要約してから読み上げる、という設計になっています。",
  "要約が間に合わなかった場合は、原文がそのまま読み上げられます。",
].join("");

/** レスポンス。`httpServer.ts` がこれを HTTP に写す */
export type ControlResponse =
  | { status: number; kind: "json"; body: unknown; headers?: Record<string, string> }
  | { status: number; kind: "wav"; body: ArrayBuffer; headers?: Record<string, string> };

function json(status: number, body: unknown, headers?: Record<string, string>): ControlResponse {
  return { status, kind: "json", body, headers };
}

function fail(status: number, error: string, extra: Record<string, unknown> = {}): ControlResponse {
  return json(status, { error, ...extra });
}

/**
 * プレビューの間引き。**同時1本 + 最短間隔1秒。**
 *
 * ★ **本命の間引きはクライアント側**（スライダーを 300ms デバウンスする）。ここは
 *   「押しっぱなしにされたときにエンジンと CLI を守る」ための最後の砦で、
 *   まともなクライアントは一度も引っ掛からない。
 *
 * ★ **合成と要約で別々のゲートを持つこと。** 1つにすると、テスト音声を鳴らした直後に
 *   テスト要約が弾かれる（利用者から見れば無関係な操作）。
 */
function createPreviewGate(minIntervalMs: number, now: () => number) {
  let busy = false;
  let lastAt = Number.NEGATIVE_INFINITY;

  return {
    /** 通せたら `true`。通した場合は必ず `release()` すること */
    tryEnter(): boolean {
      if (busy) return false;
      if (now() - lastAt < minIntervalMs) return false;
      busy = true;
      return true;
    },
    release(): void {
      busy = false;
      // ★ 「入った時刻」ではなく「終わった時刻」を記録する。入った時刻にすると、
      //   1分かかる要約の直後に次の要求が素通りする
      lastAt = now();
    },
  };
}

const PREVIEW_MIN_INTERVAL_MS = 1_000;

export interface ControlApiDeps {
  config: ConfigStore;
  /**
   * 話者一覧（`voicevoxClient.listSpeakers()` + `flattenStyles()`）。
   * エンジンに繋がらなければ reject する。
   */
  listSpeakers: () => Promise<{ id: number; label: string }[]>;
  /** 固定文を今の声で合成する。`audioStore` は通さない（キューに無い文なので `lookup` が引けない） */
  synthesizePreview: (text: string) => Promise<ArrayBuffer>;
  /** テスト要約（→ `summarizer/summaryPreview.ts`）。**同期の pipeline を呼ばないこと** */
  summaryPreview: Omit<SummaryPreviewDeps, "now">;
  now?: () => number;
}

export interface ControlApi {
  health(): ControlResponse;
  speakers(): Promise<ControlResponse>;
  getConfig(): ControlResponse;
  patchConfig(body: unknown): ControlResponse;
  ttsPreview(): Promise<ControlResponse>;
  summaryPreview(): Promise<ControlResponse>;
}

export function createControlApi(deps: ControlApiDeps): ControlApi {
  const now = deps.now ?? Date.now;
  const ttsGate = createPreviewGate(PREVIEW_MIN_INTERVAL_MS, now);
  const summaryGate = createPreviewGate(PREVIEW_MIN_INTERVAL_MS, now);

  function configBody(): { values: Record<string, unknown>; origins: Record<string, string> } {
    const values = deps.config.snapshot() as unknown as Record<string, unknown>;
    const origins: Record<string, string> = {};
    for (const key of configKeys()) origins[key] = deps.config.originOf(key);
    return { values: { ...values }, origins };
  }

  return {
    health() {
      return json(200, { ok: true, version: VERSION });
    },

    async speakers() {
      try {
        return json(200, { speakers: await deps.listSpeakers() });
      } catch (err) {
        // ★ 話者一覧が取れないのは「エンジンが居ない」だけ。設定 UI 側は
        //   **項目を消さずに「取得できません」で出す**（消すと「設定が無い」に見える）
        return fail(503, "engine_unreachable", { detail: err instanceof Error ? err.message : String(err) });
      }
    },

    getConfig() {
      return json(200, {
        ...configBody(),
        writable: writableConfigKeys(configKeys()),
        // ★★ **既定値をクライアントに書き写させないこと。** `SPECS` が権威なので、
        //   写した瞬間に「core を直したのにクライアントだけ古い既定に戻す」がありうる。
        //   設定パネルの「すべての設定をリセット」がこれを使う（#76）
        defaults: createDefaultConfig(),
      });
    },

    patchConfig(body) {
      if (typeof body !== "object" || body === null || Array.isArray(body)) {
        return fail(400, "invalid_body");
      }

      // ★★ **生の JSON をベースにする。** `snapshot()` の値で全体を上書きすると、
      //   他バイナリ向けの未知キーが消える（→ `core/configPatch.ts`）
      const base = deps.config.readRawFile();
      if (base === undefined) {
        // ★ ここで `{}` を代わりに使わないこと。壊れた（あるいは読めない）ファイルを
        //   丸ごと書き潰すことになる
        return fail(500, "config_unreadable", { path: deps.config.filePath });
      }

      const patch = body as Record<string, unknown>;
      const result = buildConfigPatch(base, patch, (key: ConfigKey) => deps.config.originOf(key));
      if (!result.ok) {
        const { reason, key } = result.failure;
        const status = reason === "readonly_key" ? 403 : reason === "env_override" ? 409 : 400;
        return fail(status, reason, { key });
      }

      try {
        // ★ 親ディレクトリを作ってから書く。`CHATTER_AGENT_CONFIG` が未作成の
        //   ディレクトリを指していると tmp の書き込みが ENOENT で落ち、**すべての PATCH が
        //   500 config_unwritable** になる（パネルには「保存できません」としか出ず、
        //   `mkdir` すれば直ることは分からない）。`readRawFile` が ENOENT を `{}` に落として
        //   「書き手が新規作成できる」ようにしているのと対にする。
        //   ★ `writeFileAtomic` の側には入れない。あちらは tmp + rename の**機構だけ**を持ち、
        //     ディレクトリを誰が保証するかは呼び出し側の話（→ `core/atomicWrite.ts`）
        fs.mkdirSync(path.dirname(deps.config.filePath), { recursive: true });
        writeFileAtomic(deps.config.filePath, `${JSON.stringify(result.next, null, 2)}\n`);
        // ★★ **書いた直後にスタンプを捨てる。** これが無いと、バイト長が変わらない書き換えを
        //   粒度の粗い FS で行ったときに読み飛ばされる（→ `core/config.ts` の `invalidate`）
        deps.config.invalidate();
      } catch (err) {
        return fail(500, "config_unwritable", { detail: err instanceof Error ? err.message : String(err) });
      }

      // ★ **書いた後に読み直した値を返す。** これが「本当に効いた値」になる
      //   （上で `invalidate()` しているので、必ず今書いたファイルを読む）
      return json(200, configBody());
    },

    async ttsPreview() {
      if (!ttsGate.tryEnter()) return fail(429, "too_many_requests");
      try {
        const wav = await deps.synthesizePreview(TTS_PREVIEW_TEXT);
        return { status: 200, kind: "wav", body: wav };
      } catch (err) {
        return fail(503, "synthesis_unavailable", { detail: err instanceof Error ? err.message : String(err) });
      } finally {
        ttsGate.release();
      }
    },

    async summaryPreview() {
      if (!summaryGate.tryEnter()) return fail(429, "too_many_requests");
      try {
        const result = await runSummaryPreview(SUMMARY_PREVIEW_TEXT, { ...deps.summaryPreview, now });
        // ★★ **失敗も 200 で返す。** これは「あとで取りに来い」ではなく
        //   「試した結果、こうだった」。503 にすると、クライアントの再試行ロジック
        //   （→ `docs/protocol.md` の責務8）が噛んで押していないのに何度も `claude -p` が走る
        return json(200, {
          summary: result.summary,
          outcome: result.outcome,
          elapsedMs: result.elapsedMs,
          ...(result.detail ? { detail: result.detail } : {}),
        });
      } finally {
        summaryGate.release();
      }
    },
  };
}
