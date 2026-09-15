/**
 * `Authorization: Bearer <token>` の検証。**純粋関数。**
 *
 * 非ループバックの相手にだけ要求する（→ `server/loopback.ts`）。同じマシンの同じユーザーは
 * トークンファイルを直接読めるので、ループバックへ要求しても守りは強くならない。
 */

import * as crypto from "crypto";
import { isLoopbackAddress } from "./loopback";

const BEARER_PATTERN = /^bearer\s+(\S+)$/i;

/** `Authorization` ヘッダから token68 部分を取り出す。読めなければ null。スキーム名の大小文字は無視する（RFC 6750） */
function extractBearerToken(header: string | undefined | null): string | null {
  if (typeof header !== "string") return null;
  const matched = BEARER_PATTERN.exec(header.trim());
  return matched ? matched[1]! : null;
}

/**
 * ★ `crypto.timingSafeEqual` は長さが違うバッファを渡すと例外を投げる。
 *   長さの比較は**内容の比較より前に**行い、違えば即 false にする。
 */
function timingSafeEqualString(a: string, b: string): boolean {
  const bufA = Buffer.from(a, "utf-8");
  const bufB = Buffer.from(b, "utf-8");
  if (bufA.length !== bufB.length) return false;
  return crypto.timingSafeEqual(bufA, bufB);
}

/**
 * その接続 / リクエストを通してよいか。
 *
 * ループバックは無条件で許可する。それ以外は `Authorization: Bearer <token>` が
 * 定数時間比較で一致したときだけ許可する。
 */
export function isAuthorized(
  remoteAddress: string | undefined | null,
  authorizationHeader: string | undefined | null,
  token: string,
): boolean {
  if (isLoopbackAddress(remoteAddress)) return true;

  const provided = extractBearerToken(authorizationHeader);
  if (provided === null) return false;
  return timingSafeEqualString(provided, token);
}
