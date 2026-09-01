import { describe, it, expect } from "vitest";
import { buildConfigPatch, isWritableConfigKey, writableConfigKeys } from "./configPatch";
import { configKeys, type ConfigKey, type ConfigOrigin } from "./config";

/** 既定では「どのキーも既定値のまま」＝ env も file も勝っていない */
const allDefault = (): ConfigOrigin => "default";

describe("isWritableConfigKey", () => {
  /**
   * ★★ (a) コマンド実行に繋がるキー。ループバック限定でも、ここが緩むと
   *   「設定を1行書き換えるだけで任意コマンド実行」になる
   */
  it("★★ コマンド実行に繋がるキーは書けない", () => {
    for (const key of ["ttsSpawnCommand", "ttsSpawnArgs", "playerCommand", "playerArgs", "aiSummaryCommand"] as const) {
      expect(isWritableConfigKey(key)).toBe(false);
    }
  });

  /** (b) 再起動まで反映されないキー。「効かない設定」をパネルに出さないため */
  it("再起動まで効かないキーは書けない", () => {
    for (const key of ["host", "port", "allowedOrigins"] as const) {
      expect(isWritableConfigKey(key)).toBe(false);
    }
  });

  it("設定 UI が触るキーは書ける", () => {
    for (const key of ["ttsSpeakerId", "ttsSpeedScale", "aiSummaryEnabled"] as const) {
      expect(isWritableConfigKey(key)).toBe(true);
    }
  });

  /**
   * ★ `aiSummaryModel` は `--model <値>` の**値**にしかならない（`execFile` に
   *   シェルを噛ませていないので、新しいコマンドにも別のフラグにもならない）
   */
  it("★ aiSummaryModel は書ける（値であってコマンドではない）", () => {
    expect(isWritableConfigKey("aiSummaryModel")).toBe(true);
  });

  it("writableConfigKeys は SPECS の宣言順を保つ", () => {
    const all = configKeys();
    const writable = writableConfigKeys(all);
    expect(writable).toEqual(all.filter(isWritableConfigKey));
    // 部分列であること（並び替えていない）
    expect(all.filter((k) => writable.includes(k))).toEqual(writable);
  });
});

describe("buildConfigPatch", () => {
  it("変更キーだけを差し替える", () => {
    const result = buildConfigPatch({ ttsSpeakerId: 1 }, { ttsSpeedScale: 1.5 }, allDefault);
    expect(result).toEqual({ ok: true, next: { ttsSpeakerId: 1, ttsSpeedScale: 1.5 }, changed: ["ttsSpeedScale"] });
  });

  /**
   * ★★ 他バイナリ向けの未知キーが消えないこと。`collect()` を通した値で全体を
   *   上書きするとここが落ちる
   */
  it("★★ 未知のキーを消さない", () => {
    const base = { ttsSpeakerId: 1, somethingElse: { deep: true }, futureKey: "残す" };
    const result = buildConfigPatch(base, { ttsSpeakerId: 2 }, allDefault);
    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.next).toEqual({ ttsSpeakerId: 2, somethingElse: { deep: true }, futureKey: "残す" });
  });

  it("元のオブジェクトを書き換えない", () => {
    const base = { ttsSpeakerId: 1 };
    buildConfigPatch(base, { ttsSpeakerId: 2 }, allDefault);
    expect(base).toEqual({ ttsSpeakerId: 1 });
  });

  /** ★ 値はパーサが正規化したものを書く（生値だとファイルとレスポンスがズレる） */
  it("★ パーサが正規化した値を書く", () => {
    const result = buildConfigPatch({}, { ttsBaseUrl: " http://127.0.0.1:10101/ " }, allDefault);
    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.next.ttsBaseUrl).toBe("http://127.0.0.1:10101");
  });

  it("SPECS に無いキーは unknown_key", () => {
    expect(buildConfigPatch({}, { nope: 1 }, allDefault)).toEqual({
      ok: false,
      failure: { reason: "unknown_key", key: "nope" },
    });
  });

  it("書けないキーは readonly_key", () => {
    expect(buildConfigPatch({}, { playerCommand: "/bin/sh" }, allDefault)).toEqual({
      ok: false,
      failure: { reason: "readonly_key", key: "playerCommand" },
    });
  });

  /** ★ 環境変数が勝っているキーは書いても効かない。**黙って書いて効かないのが最悪** */
  it("★ 環境変数が勝っているキーは env_override", () => {
    const originOf = (key: ConfigKey): ConfigOrigin => (key === "ttsSpeakerId" ? "env" : "default");
    expect(buildConfigPatch({}, { ttsSpeakerId: 3 }, originOf)).toEqual({
      ok: false,
      failure: { reason: "env_override", key: "ttsSpeakerId" },
    });
  });

  it("ファイルが勝っているキーは書ける（上書きするだけ）", () => {
    const originOf = (key: ConfigKey): ConfigOrigin => (key === "ttsSpeakerId" ? "file" : "default");
    const result = buildConfigPatch({ ttsSpeakerId: 1 }, { ttsSpeakerId: 3 }, originOf);
    expect(result.ok).toBe(true);
  });

  it("範囲外の値は invalid_value", () => {
    expect(buildConfigPatch({}, { ttsSpeedScale: 3 }, allDefault)).toEqual({
      ok: false,
      failure: { reason: "invalid_value", key: "ttsSpeedScale" },
    });
  });

  /**
   * ★★ all-or-nothing。部分適用にすると「400 が返ったのに半分は書き換わっている」という、
   *   呼び出し側から復元できない状態になる
   */
  it("★★ 1つでも弾かれたら何も書かない", () => {
    const result = buildConfigPatch({}, { ttsSpeakerId: 5, ttsSpeedScale: 99 }, allDefault);
    expect(result.ok).toBe(false);
    if (result.ok) return;
    expect(result.failure.reason).toBe("invalid_value");
  });

  it("空のパッチはベースをそのまま返す", () => {
    expect(buildConfigPatch({ a: 1 }, {}, allDefault)).toEqual({ ok: true, next: { a: 1 }, changed: [] });
  });
});
