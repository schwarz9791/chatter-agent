/**
 * VRM モデル / VRMA モーションの URL パス。
 *
 * ```
 * /v1/assets/models/mascot.vrm
 * /v1/assets/animations/idle.vrma
 * /v1/assets/animations/<category>/<name>.vrma
 * ```
 *
 * ★ **URL デコードしない。** 受け取った文字列をそのまま正規表現に通す。デコードすると
 *   `%2e%2e` が `..` に化けて通り道ができる —— 生のままなら `%` が charset に無いので
 *   そのまま弾かれる（`core/audioPath.ts` と同じ論法）。
 *
 * ★ category の正は `chatter-mascot` 側 `Vrm/MotionCategory.cs` の `DirectoryName`
 *   （`idle`/`happy`/`angry`/`sad`/`relaxed`/`surprised`/`walk`）。core の `Emotion` 型を
 *   流用しないこと —— `idle` / `walk` は感情由来ではないカテゴリで、`Emotion` には無い。
 */

/** `animations/<ここ>/` のディレクトリ名。`assetCatalog.ts` の走査もこの並びに従う */
export const ASSET_CATEGORIES = ["idle", "happy", "angry", "sad", "relaxed", "surprised", "walk"] as const;

export type AssetCategory = (typeof ASSET_CATEGORIES)[number];

/** `animations/<category>/` 配下のファイル名（拡張子を除く） */
const NAME_PATTERN = "[A-Za-z0-9][A-Za-z0-9._-]{0,127}";

const MODEL_REL = /^models\/mascot\.vrm$/;
const IDLE_ANIMATION_REL = /^animations\/idle\.vrma$/;
const CATEGORY_ANIMATION_REL = new RegExp(`^animations/(?:${ASSET_CATEGORIES.join("|")})/${NAME_PATTERN}\\.vrma$`);

/** URL パスの `/v1/assets/` から先。呼び出し側はこの相対パス文字列だけを持ち回れば足りる */
export interface AssetRef {
  rel: string;
}

const PREFIX = "/v1/assets/";

/** 読めたら `AssetRef`、読めなければ null。**受け取った文字列をパスの組み立てに使わない** */
export function parseAssetPath(pathname: string): AssetRef | null {
  if (!pathname.startsWith(PREFIX)) return null;
  const rel = pathname.slice(PREFIX.length);
  return isAssetRelPath(rel) ? { rel } : null;
}

export function buildAssetPath(rel: string): string {
  return `${PREFIX}${rel}`;
}

/** 3つの形のいずれかに一致するか。`assetCatalog.ts` が走査結果を載せてよいか判定するのにも使う */
export function isAssetRelPath(value: unknown): value is string {
  if (typeof value !== "string") return false;
  return MODEL_REL.test(value) || IDLE_ANIMATION_REL.test(value) || CATEGORY_ANIMATION_REL.test(value);
}
