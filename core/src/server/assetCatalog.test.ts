import { describe, it, expect, afterEach } from "vitest";
import * as crypto from "crypto";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { createAssetCatalog } from "./assetCatalog";

const tmpDirs: string[] = [];

afterEach(() => {
  for (const d of tmpDirs.splice(0)) fs.rmSync(d, { recursive: true, force: true });
});

function tmpRoot(): { root: string; modelsDir: string; animationsDir: string } {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "chatter-agent-assets-"));
  tmpDirs.push(root);
  const modelsDir = path.join(root, "models");
  const animationsDir = path.join(root, "animations");
  return { root, modelsDir, animationsDir };
}

function write(dir: string, name: string, content = "x"): string {
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, name);
  fs.writeFileSync(file, content);
  return file;
}

function sha256Of(content: string): string {
  return crypto.createHash("sha256").update(content).digest("hex");
}

function byteLength(content: string): number {
  return Buffer.byteLength(content);
}

describe("manifest", () => {
  it("ディレクトリが無くても落ちない（空を返す）", () => {
    const { modelsDir, animationsDir } = tmpRoot();
    const catalog = createAssetCatalog(modelsDir, animationsDir);
    expect(catalog.manifest()).toEqual([]);
  });

  it("models/mascot.vrm を優先する", () => {
    const { modelsDir, animationsDir } = tmpRoot();
    write(modelsDir, "aaa.vrm", "先頭のはず");
    write(modelsDir, "mascot.vrm", "選ばれるべき");
    const catalog = createAssetCatalog(modelsDir, animationsDir);

    const entries = catalog.manifest();
    expect(entries).toHaveLength(1);
    expect(entries[0]).toMatchObject({ path: "models/mascot.vrm", size: byteLength("選ばれるべき") });
    expect(entries[0]?.sha256).toBe(sha256Of("選ばれるべき"));
  });

  it("mascot.vrm が無ければ Ordinal 昇順の先頭", () => {
    const { modelsDir, animationsDir } = tmpRoot();
    write(modelsDir, "zzz.vrm", "後ろ");
    write(modelsDir, "aaa.vrm", "先頭");
    const catalog = createAssetCatalog(modelsDir, animationsDir);

    const entries = catalog.manifest();
    expect(entries).toHaveLength(1);
    expect(entries[0]?.path).toBe("models/mascot.vrm");
    expect(entries[0]?.sha256).toBe(sha256Of("先頭"));
  });

  it("animations/idle.vrma も同じ規則（固定名優先 → 無ければ Ordinal 先頭）", () => {
    const { modelsDir, animationsDir } = tmpRoot();
    write(animationsDir, "zzz.vrma", "後ろ");
    write(animationsDir, "idle.vrma", "固定名");
    const catalog = createAssetCatalog(modelsDir, animationsDir);

    expect(catalog.manifest()).toEqual([
      { path: "animations/idle.vrma", size: byteLength("固定名"), sha256: sha256Of("固定名") },
    ]);
  });

  it("カテゴリ別ディレクトリはそのまま名前を載せる", () => {
    const { modelsDir, animationsDir } = tmpRoot();
    write(path.join(animationsDir, "happy"), "wave.vrma", "嬉しい");
    write(path.join(animationsDir, "sad"), "cry.vrma", "悲しい");
    const catalog = createAssetCatalog(modelsDir, animationsDir);

    const paths = catalog.manifest().map((e) => e.path);
    expect(paths).toEqual(["animations/happy/wave.vrma", "animations/sad/cry.vrma"]);
  });

  it("★ name パターンに合わない名前は載せない（配れない URL を載せない）", () => {
    const { modelsDir, animationsDir } = tmpRoot();
    const happyDir = path.join(animationsDir, "happy");
    write(happyDir, "ok.vrma", "ok");
    write(happyDir, ".hidden.vrma", "hidden"); // 先頭がドット
    write(happyDir, "sub.dir.vrma.bak", "bak"); // 拡張子違い
    const catalog = createAssetCatalog(modelsDir, animationsDir);

    expect(catalog.manifest().map((e) => e.path)).toEqual(["animations/happy/ok.vrma"]);
  });

  it("未知のカテゴリ名のディレクトリは走査しない", () => {
    const { modelsDir, animationsDir } = tmpRoot();
    write(path.join(animationsDir, "neutral"), "wave.vrma", "x");
    const catalog = createAssetCatalog(modelsDir, animationsDir);
    expect(catalog.manifest()).toEqual([]);
  });

  it("★ シンボリックリンクを拾わない（配布ディレクトリの外へ抜ける経路を塞ぐ）", () => {
    const { root, modelsDir, animationsDir } = tmpRoot();
    const outside = path.join(root, "outside.vrm");
    fs.writeFileSync(outside, "外側のファイル");
    fs.mkdirSync(modelsDir, { recursive: true });
    fs.symlinkSync(outside, path.join(modelsDir, "mascot.vrm"));
    const catalog = createAssetCatalog(modelsDir, animationsDir);

    expect(catalog.manifest()).toEqual([]);
  });

  it("path の Ordinal 昇順で並ぶ", () => {
    const { modelsDir, animationsDir } = tmpRoot();
    write(modelsDir, "mascot.vrm", "m");
    write(animationsDir, "idle.vrma", "i");
    write(path.join(animationsDir, "angry"), "shout.vrma", "a");
    const catalog = createAssetCatalog(modelsDir, animationsDir);

    expect(catalog.manifest().map((e) => e.path)).toEqual([
      "animations/angry/shout.vrma",
      "animations/idle.vrma",
      "models/mascot.vrm",
    ]);
  });

  it("★ mtime が変わるとハッシュを取り直す", () => {
    const { modelsDir, animationsDir } = tmpRoot();
    const file = write(modelsDir, "mascot.vrm", "v1");
    const catalog = createAssetCatalog(modelsDir, animationsDir);
    expect(catalog.manifest()[0]?.sha256).toBe(sha256Of("v1"));

    // 中身とサイズを変えずに mtime だけ進める場合でもキーが変わるよう、内容ごと変える
    fs.writeFileSync(file, "v2");
    const future = new Date(Date.now() + 60_000);
    fs.utimesSync(file, future, future);

    expect(catalog.manifest()[0]?.sha256).toBe(sha256Of("v2"));
  });
});

describe("resolve", () => {
  it("マニフェストに載っているものは実体を返す", () => {
    const { modelsDir, animationsDir } = tmpRoot();
    const file = write(modelsDir, "mascot.vrm", "hello");
    const catalog = createAssetCatalog(modelsDir, animationsDir);

    expect(catalog.resolve("models/mascot.vrm")).toEqual({ absolute: file, size: 5 });
  });

  it("★ マニフェスト外は返さない", () => {
    const { modelsDir, animationsDir } = tmpRoot();
    write(modelsDir, "mascot.vrm", "hello");
    const catalog = createAssetCatalog(modelsDir, animationsDir);

    expect(catalog.resolve("models/other.vrm")).toBeNull();
    expect(catalog.resolve("animations/idle.vrma")).toBeNull();
    expect(catalog.resolve("../../etc/passwd")).toBeNull();
  });
});
