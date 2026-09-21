/**
 * 配布する素材（VRM モデル / VRMA モーション）のカタログ。HTTP を知らない層。
 *
 * ```
 * {root}/models/mascot.vrm             → models/mascot.vrm
 * {root}/models/*.vrm                  → models/mascot.vrm（固定名が無いときだけ）
 * {root}/animations/idle.vrma          → animations/idle.vrma
 * {root}/animations/*.vrma             → animations/idle.vrma（固定名が無いときだけ）
 * {root}/animations/<category>/*.vrma  → animations/<category>/<name>.vrma
 * ```
 *
 * ★ **固定名優先 → 無ければ Ordinal 昇順の先頭。** これは `chatter-mascot` 側がデスクトップで
 *   実際に読む1本と同じ規則。走査結果とデスクトップの表示が食い違うと、
 *   「配ったファイルと違うモデルが動いている」という見分けにくいズレになる。
 *
 * ★ **`manifest()` に載ったものしか `resolve()` しない。** 配布可能なパスの唯一の
 *   出どころをここに絞る（→ `resolve` の ★）。
 */

import * as crypto from "crypto";
import * as fs from "fs";
import * as path from "path";
import { ASSET_CATEGORIES, isAssetRelPath } from "../core/assetPath";

export interface AssetEntry {
  path: string;
  size: number;
  sha256: string;
}

export interface AssetCatalog {
  /** `path` の Ordinal 昇順（サーバー間・実行間で並びが揺れない） */
  manifest(): AssetEntry[];
  /**
   * `path` の実体。マニフェストに無いものは null。
   *
   * ★ **これが唯一の解決口。** `assetPath.ts` の正規表現を通っていても、
   *   ファイルが無ければ配らない。
   */
  resolve(requestedPath: string): { absolute: string; size: number } | null;
}

interface ResolvedEntry extends AssetEntry {
  absolute: string;
}

/** ディレクトリ直下のファイル名。シンボリックリンクは追わない（配布ディレクトリの外へ抜けさせない） */
function listFiles(dir: string): string[] {
  let dirents: fs.Dirent[];
  try {
    dirents = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return [];
  }
  return dirents.filter((d) => d.isFile()).map((d) => d.name);
}

/** 固定名があればそれ、無ければ `pattern` に合う名前を Ordinal 昇順に並べた先頭 */
function pickPrimary(dir: string, fixedName: string, pattern: RegExp): string | null {
  const names = listFiles(dir);
  if (names.includes(fixedName)) return fixedName;
  const matched = names.filter((n) => pattern.test(n)).sort();
  return matched[0] ?? null;
}

export function createAssetCatalog(modelsDir: string, animationsDir: string): AssetCatalog {
  /**
   * sha256 のキャッシュ。キーは `absolute|mtimeMs|size`。
   *
   * ★ mtime か size が変われば別のキーになるので、古い方は自然に引かれなくなる。
   *   `manifest()` の終わりで**今回見たキーだけ**残すので、削除やリネームで
   *   キャッシュが際限なく伸びることもない。
   */
  const hashCache = new Map<string, string>();

  function hashOf(absolute: string, mtimeMs: number, size: number, seenKeys: Set<string>): string {
    const key = `${absolute}|${mtimeMs}|${size}`;
    seenKeys.add(key);
    const cached = hashCache.get(key);
    if (cached !== undefined) return cached;
    const sha256 = crypto.createHash("sha256").update(fs.readFileSync(absolute)).digest("hex");
    hashCache.set(key, sha256);
    return sha256;
  }

  function addEntry(into: ResolvedEntry[], seenKeys: Set<string>, relPath: string, absolute: string): void {
    let stat: fs.Stats;
    try {
      stat = fs.statSync(absolute);
    } catch {
      return; // 列挙後に消えた等の競合。無ければ載せないだけでよい
    }
    into.push({
      path: relPath,
      size: stat.size,
      sha256: hashOf(absolute, stat.mtimeMs, stat.size, seenKeys),
      absolute,
    });
  }

  function buildEntries(): ResolvedEntry[] {
    const entries: ResolvedEntry[] = [];
    const seenKeys = new Set<string>();

    const model = pickPrimary(modelsDir, "mascot.vrm", /\.vrm$/);
    if (model !== null) addEntry(entries, seenKeys, "models/mascot.vrm", path.join(modelsDir, model));

    const idle = pickPrimary(animationsDir, "idle.vrma", /\.vrma$/);
    if (idle !== null) addEntry(entries, seenKeys, "animations/idle.vrma", path.join(animationsDir, idle));

    for (const category of ASSET_CATEGORIES) {
      const categoryDir = path.join(animationsDir, category);
      for (const name of listFiles(categoryDir)) {
        const relPath = `animations/${category}/${name}`;
        // ★ name パターンに合わない名前は載せない（配れない URL を載せてもクライアントが 404 を踏むだけ）
        if (!isAssetRelPath(relPath)) continue;
        addEntry(entries, seenKeys, relPath, path.join(categoryDir, name));
      }
    }

    entries.sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0));

    for (const key of hashCache.keys()) {
      if (!seenKeys.has(key)) hashCache.delete(key);
    }
    return entries;
  }

  return {
    manifest: () => buildEntries().map(({ absolute: _absolute, ...entry }) => entry),
    resolve: (requestedPath) => {
      const found = buildEntries().find((e) => e.path === requestedPath);
      return found === undefined ? null : { absolute: found.absolute, size: found.size };
    },
  };
}
