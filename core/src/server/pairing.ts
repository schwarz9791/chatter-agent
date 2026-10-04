/**
 * 4桁 PIN によるペアリング。LAN の端末がトークンファイルに触れずに、server のトークンを受け取る。
 *
 * ★ **タイマーを持たない。** 期限は呼ばれた時点の `now()` で判定する（常駐プロセスに
 *   後始末の手を増やさない）。
 *
 * ★ **PIN は 10^4 通りしかない。** 守りは「試行回数の上限（`MAX_FAILURES`）」と「短い期限」で、
 *   上限に達したら PIN ごと捨てる。発行し直せば回数は戻るが、発行できるのはループバックだけ
 *   （→ `server/httpServer.ts`）なので、LAN の相手は回数を戻せない。
 */

import * as crypto from "crypto";
import { timingSafeEqualString } from "./auth";

/** PIN の有効期間 */
export const TTL_MS = 300_000;
/** 間違えてよい回数。この回数に達したら PIN を無効化する */
export const MAX_FAILURES = 5;

export type PairingState = "none" | "pending" | "paired" | "expired" | "locked";

export type ClaimResult =
  | { ok: true; token: string }
  | { ok: false; reason: "invalid" | "no_pairing" | "expired" | "locked" | "wrong_pin"; remaining?: number };

export interface PairingStatus {
  state: PairingState;
  /** `pending` のときだけ。★ PIN は載せない（→ `server/controlApi.ts`） */
  expiresAt?: number;
}

export interface Pairing {
  /** PIN を発行する。既存の PIN は置き換わり、失敗回数は 0 に戻る */
  issue(): { pin: string; expiresAt: number };
  status(): PairingStatus;
  claim(pin: unknown): ClaimResult;
}

export interface PairingDeps {
  token: string;
  now?: () => number;
  randomPin?: () => string;
}

const PIN_PATTERN = /^\d{4}$/;

function defaultRandomPin(): string {
  return String(crypto.randomInt(0, 10_000)).padStart(4, "0");
}

export function createPairing(deps: PairingDeps): Pairing {
  const now = deps.now ?? Date.now;
  const randomPin = deps.randomPin ?? defaultRandomPin;

  let state: "none" | "pending" | "paired" | "locked" = "none";
  let pin = "";
  let expiresAt = 0;
  let failures = 0;

  const isExpired = () => state === "pending" && now() >= expiresAt;

  return {
    issue() {
      pin = randomPin();
      expiresAt = now() + TTL_MS;
      failures = 0;
      state = "pending";
      return { pin, expiresAt };
    },

    status() {
      if (isExpired()) return { state: "expired" };
      if (state === "pending") return { state, expiresAt };
      return { state };
    },

    claim(input) {
      // ★ 形式の誤りは回数に数えない（クライアントのバグで正規の PIN が使えなくなるのを避ける）
      if (typeof input !== "string" || !PIN_PATTERN.test(input)) return { ok: false, reason: "invalid" };
      if (state === "none" || state === "paired") return { ok: false, reason: "no_pairing" };
      if (state === "locked") return { ok: false, reason: "locked" };
      if (isExpired()) return { ok: false, reason: "expired" };

      if (!timingSafeEqualString(input, pin)) {
        failures += 1;
        if (failures >= MAX_FAILURES) {
          state = "locked";
          pin = "";
        }
        return { ok: false, reason: "wrong_pin", remaining: Math.max(0, MAX_FAILURES - failures) };
      }

      // ★ 使い切り。再利用できると、PIN を覗かれた時点で恒久的な鍵になる
      state = "paired";
      pin = "";
      return { ok: true, token: deps.token };
    },
  };
}
