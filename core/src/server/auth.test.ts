import { describe, it, expect } from "vitest";
import { isAuthorized } from "./auth";

const TOKEN = "s3cr3t-token-value";

describe("isAuthorized", () => {
  it("ループバックはトークン無しでも通る", () => {
    expect(isAuthorized("127.0.0.1", undefined, TOKEN)).toBe(true);
    expect(isAuthorized("::1", null, TOKEN)).toBe(true);
  });

  it("★ ループバックは誤ったトークンでも通る（免除。同じマシンの同じユーザーはトークンファイルを直接読める）", () => {
    expect(isAuthorized("127.0.0.1", "Bearer wrong", TOKEN)).toBe(true);
  });

  it("非ループバックは正しい Bearer トークンで通る", () => {
    expect(isAuthorized("192.168.1.5", `Bearer ${TOKEN}`, TOKEN)).toBe(true);
  });

  it("スキーム名の大小文字は無視する（RFC 6750）", () => {
    expect(isAuthorized("192.168.1.5", `bearer ${TOKEN}`, TOKEN)).toBe(true);
    expect(isAuthorized("192.168.1.5", `BEARER ${TOKEN}`, TOKEN)).toBe(true);
  });

  it("非ループバックはトークンが無ければ拒否", () => {
    expect(isAuthorized("192.168.1.5", undefined, TOKEN)).toBe(false);
    expect(isAuthorized("192.168.1.5", null, TOKEN)).toBe(false);
    expect(isAuthorized("192.168.1.5", "", TOKEN)).toBe(false);
  });

  it("非ループバックは誤ったトークンを拒否", () => {
    expect(isAuthorized("192.168.1.5", "Bearer wrong-token", TOKEN)).toBe(false);
  });

  it("★ 非ループバックは長さの違うトークンを拒否する（timingSafeEqual の RangeError を踏まない）", () => {
    expect(isAuthorized("192.168.1.5", "Bearer short", TOKEN)).toBe(false);
    expect(isAuthorized("192.168.1.5", `Bearer ${TOKEN}-extra`, TOKEN)).toBe(false);
  });

  it("Bearer 以外のスキームは拒否", () => {
    expect(isAuthorized("192.168.1.5", `Basic ${TOKEN}`, TOKEN)).toBe(false);
  });

  it("形が壊れたヘッダは拒否", () => {
    expect(isAuthorized("192.168.1.5", "Bearer", TOKEN)).toBe(false);
    expect(isAuthorized("192.168.1.5", TOKEN, TOKEN)).toBe(false);
  });

  it("判定できないアドレス（remoteAddress 無し）はループバックに倒さない", () => {
    expect(isAuthorized(undefined, `Bearer ${TOKEN}`, TOKEN)).toBe(true);
    expect(isAuthorized(undefined, undefined, TOKEN)).toBe(false);
  });
});
