import { describe, expect, it } from "vitest";
import { MAX_FAILURES, TTL_MS, createPairing } from "./pairing";

function setup() {
  let t = 1_000;
  const pairing = createPairing({ token: "secret-token", now: () => t, randomPin: () => "0427" });
  return { pairing, advance: (ms: number) => (t += ms) };
}

describe("createPairing", () => {
  it("発行前は none で、claim は no_pairing", () => {
    const { pairing } = setup();
    expect(pairing.status()).toEqual({ state: "none" });
    expect(pairing.claim("0427")).toEqual({ ok: false, reason: "no_pairing" });
  });

  it("正しい PIN で token が返り、同じ PIN の再利用は no_pairing", () => {
    const { pairing } = setup();
    pairing.issue();
    expect(pairing.claim("0427")).toEqual({ ok: true, token: "secret-token" });
    expect(pairing.status()).toEqual({ state: "paired" });
    expect(pairing.claim("0427")).toEqual({ ok: false, reason: "no_pairing" });
  });

  it("間違いは残り回数を返し、上限で locked。以降は正しい PIN でも locked", () => {
    const { pairing } = setup();
    pairing.issue();
    for (let i = 1; i <= MAX_FAILURES; i++) {
      expect(pairing.claim("1111")).toEqual({ ok: false, reason: "wrong_pin", remaining: MAX_FAILURES - i });
    }
    expect(pairing.status()).toEqual({ state: "locked" });
    expect(pairing.claim("0427")).toEqual({ ok: false, reason: "locked" });
  });

  it("期限を過ぎると expired", () => {
    const { pairing, advance } = setup();
    pairing.issue();
    advance(TTL_MS - 1);
    expect(pairing.status().state).toBe("pending");
    advance(1);
    expect(pairing.status()).toEqual({ state: "expired" });
    expect(pairing.claim("0427")).toEqual({ ok: false, reason: "expired" });
  });

  it("発行し直すと古い PIN は wrong_pin になり、回数も戻る", () => {
    let n = 0;
    const pairing = createPairing({ token: "t", randomPin: () => (n++ === 0 ? "1111" : "2222") });
    pairing.issue();
    pairing.claim("0000");
    pairing.claim("0000");
    pairing.issue();
    expect(pairing.claim("1111")).toEqual({ ok: false, reason: "wrong_pin", remaining: MAX_FAILURES - 1 });
    expect(pairing.claim("2222")).toEqual({ ok: true, token: "t" });
  });

  it("形式の誤りは invalid で、回数に数えない", () => {
    const { pairing } = setup();
    pairing.issue();
    for (const bad of ["", "42", "04270", "abcd", 427, null, undefined]) {
      expect(pairing.claim(bad)).toEqual({ ok: false, reason: "invalid" });
    }
    expect(pairing.claim("1111")).toEqual({ ok: false, reason: "wrong_pin", remaining: MAX_FAILURES - 1 });
  });

  it("status に PIN は載らず、expiresAt は pending のときだけ", () => {
    const { pairing } = setup();
    expect(pairing.status()).not.toHaveProperty("expiresAt");
    pairing.issue();
    const pending = pairing.status();
    expect(pending).toEqual({ state: "pending", expiresAt: 1_000 + TTL_MS });
    expect(JSON.stringify(pending)).not.toContain("0427");
    pairing.claim("0427");
    expect(pairing.status()).not.toHaveProperty("expiresAt");
  });
});
