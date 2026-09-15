/**
 * 非ループバックからの接続に要求する共有トークン。
 *
 * ★ config.json にも環境変数にも置かない。`GET /v1/config` は `snapshot()` を丸ごと返す
 *   （→ `server/controlApi.ts`）ので、config キーにすると漏れる。秘密の置き場はこのファイル
 *   1つに絞る（→ `core/paths.ts` の `getServerTokenPath`）。
 *
 * ★ ループバックはこのトークンを要求されない（→ `server/auth.ts`）。同じマシンの同じユーザーは
 *   このファイルを直接読めるので、要求しても守りは強くならない。
 *
 * 再生成したいときは、ファイルを消してサーバーを再起動する。
 */

import * as crypto from "crypto";
import * as fs from "fs";
import * as path from "path";

/** `crypto.randomBytes` に渡すバイト数。base64url にすると 43 文字になる */
const TOKEN_BYTES = 32;

/** ファイルの権限。同じマシンの他ユーザーから読めないようにする */
const FILE_MODE = 0o600;

/** 生成したトークンの文字集合。既存ファイルがこれ以外を含んでいたら壊れているとみなす */
const TOKEN_PATTERN = /^[A-Za-z0-9_-]+$/;

function generateToken(): string {
  return crypto.randomBytes(TOKEN_BYTES).toString("base64url");
}

/** `readExisting` の結果。既存を読めた場合と、生成し直しが要る2通りを区別する */
type ReadResult = { found: true; token: string } | { found: false; reason: "missing" | "invalid" };

/**
 * 読めて、かつ生成したトークンと同じ形のときだけ「既存」として扱う。
 * ファイルが無い（`ENOENT`）のと、読めない・空・壊れているのを区別する —— 前者だけが
 * 「初回生成」で、後者はすべて「作り直し」（呼び出し側がログの出し分けに使う）。
 */
function readExisting(filePath: string): ReadResult {
  let text: string;
  try {
    text = fs.readFileSync(filePath, "utf-8");
  } catch (err) {
    const reason = (err as NodeJS.ErrnoException)?.code === "ENOENT" ? "missing" : "invalid";
    return { found: false, reason };
  }
  const trimmed = text.trim();
  return TOKEN_PATTERN.test(trimmed) ? { found: true, token: trimmed } : { found: false, reason: "invalid" };
}

/** `ensureServerToken` の戻り値。ログの出し分けに使う（→ `server/index.ts`） */
export interface EnsuredToken {
  token: string;
  /** `null`: 既存をそのまま使った / `"new"`: 無かったので作った / `"replaced"`: あったが壊れていたので作り直した */
  created: null | "new" | "replaced";
}

/**
 * トークンを読む。無い・空・壊れているときは生成して書き、以後はその値を使い続ける。
 *
 * tmp に書いてから rename する（`core/atomicWrite.ts` と同じ理由）。権限は tmp の時点で
 * 0600 にする —— rename はファイルの権限をそのまま引き継ぐので、既定の権限で公開された
 * 状態の窓を作らない。
 */
export function ensureServerToken(filePath: string): EnsuredToken {
  const existing = readExisting(filePath);
  if (existing.found) return { token: existing.token, created: null };

  const token = generateToken();
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  const tmp = `${filePath}.tmp`;
  fs.writeFileSync(tmp, token, { mode: FILE_MODE });
  // ★ `mode` はファイルを新しく作るときにしか効かない。前回の tmp が残っていると
  //   その権限のまま書かれるので、rename の前に明示的に絞る
  fs.chmodSync(tmp, FILE_MODE);
  fs.renameSync(tmp, filePath);
  return { token, created: existing.reason === "missing" ? "new" : "replaced" };
}
