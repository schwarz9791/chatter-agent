import { describe, it, expect } from "vitest";
import { buildAssetPath, isAssetRelPath, parseAssetPath } from "./assetPath";

const VALID_RELS = ["models/mascot.vrm", "animations/idle.vrma", "animations/happy/wave.vrma"];

describe("parseAssetPath / buildAssetPath", () => {
  it("3つの形が通る", () => {
    for (const rel of VALID_RELS) {
      expect(parseAssetPath(buildAssetPath(rel)), rel).toEqual({ rel });
    }
  });

  it("すべてのカテゴリで通る", () => {
    for (const category of ["idle", "happy", "angry", "sad", "relaxed", "surprised", "walk"]) {
      const rel = `animations/${category}/wave.vrma`;
      expect(parseAssetPath(buildAssetPath(rel)), rel).toEqual({ rel });
    }
  });

  it("★ 知らない形は通さない（受け取った文字列をパスの組み立てに使わないための最初の関門）", () => {
    for (const raw of [
      "",
      "/",
      "/v1/assets/",
      "/v1/assets/models/other.vrm", // 固定名以外は通さない（URL 空間は3つの形だけ）
      "/v1/assets/animations/idle.vrm", // 拡張子違い
      "/v1/assets/animations/happy/wave.vrm", // 拡張子違い
      "/v1/assets/models/../mascot.vrm",
      "/v1/assets/animations/../../etc/passwd",
      "/v1/assets/animations/%2e%2e/wave.vrma",
      "/v1/assets/animations/happy/%2e%2e%2fmascot.vrm",
      "/v1/assets/animations/happy/sub/dir.vrma", // name にスラッシュを含められない
      "/v1/assets/animations/neutral/wave.vrma", // 未知のカテゴリ（core の Emotion にはあるが chatter-mascot のディレクトリ名ではない）
      "/v1/assets/animations/happy/.hidden.vrma", // name の先頭はドット不可
      "/V1/ASSETS/models/mascot.vrm", // 大文字小文字
      "models/mascot.vrm", // プレフィックス無し
      "/v1/assets/models/mascot.vrm\n", // ★ 末尾の改行。.NET の `$` はこれを通すので、実装を移すときに差が出る
    ]) {
      expect(parseAssetPath(raw), raw).toBeNull();
    }
  });
});

describe("isAssetRelPath", () => {
  it("3つの形の相対パスを通す", () => {
    for (const rel of VALID_RELS) expect(isAssetRelPath(rel), rel).toBe(true);
  });

  it("★ 不正な相対パスを弾く", () => {
    for (const raw of [
      "../models/mascot.vrm",
      "models/../mascot.vrm",
      "animations/happy/../../etc/passwd",
      "animations/happy/sub/dir.vrma",
      "animations/neutral/wave.vrma",
      "animations/happy/.hidden.vrma",
      "animations/happy/wave.vrm",
      "models/mascot.glb",
      42,
      null,
      undefined,
    ]) {
      expect(isAssetRelPath(raw), String(raw)).toBe(false);
    }
  });
});
