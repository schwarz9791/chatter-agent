import { describe, it, expect } from "vitest";
import { isLoopbackAddress } from "./loopback";

describe("isLoopbackAddress", () => {
  it("IPv4 / IPv6 のループバックを通す", () => {
    expect(isLoopbackAddress("127.0.0.1")).toBe(true);
    expect(isLoopbackAddress("::1")).toBe(true);
  });

  /** ★ `127.0.0.1` だけではない。`127.0.0.53` を使う環境が実在する */
  it("★ 127.0.0.0/8 全体を通す", () => {
    expect(isLoopbackAddress("127.0.0.53")).toBe(true);
    expect(isLoopbackAddress("127.1.2.3")).toBe(true);
    expect(isLoopbackAddress("127.255.255.255")).toBe(true);
  });

  /** ★ デュアルスタックで listen したときに Node が実際に返す形（実測） */
  it("★ IPv4射影アドレス（::ffff:127.0.0.1）を通す", () => {
    expect(isLoopbackAddress("::ffff:127.0.0.1")).toBe(true);
    expect(isLoopbackAddress("::FFFF:127.0.0.1")).toBe(true);
  });

  it("ゾーン ID が付いていても通す", () => {
    expect(isLoopbackAddress("::1%lo0")).toBe(true);
  });

  it("LAN のアドレスは通さない", () => {
    expect(isLoopbackAddress("192.168.1.10")).toBe(false);
    expect(isLoopbackAddress("10.0.0.1")).toBe(false);
    expect(isLoopbackAddress("fe80::1")).toBe(false);
    expect(isLoopbackAddress("::ffff:192.168.1.10")).toBe(false);
  });

  /**
   * ★★ 判定できないものは false に倒す（fail closed）。ここを寛容にすると
   *   「LAN から設定を書き換えられる」になる
   */
  it("★★ 判定できない入力は false", () => {
    expect(isLoopbackAddress(undefined)).toBe(false);
    expect(isLoopbackAddress(null)).toBe(false);
    expect(isLoopbackAddress("")).toBe(false);
    expect(isLoopbackAddress("   ")).toBe(false);
    expect(isLoopbackAddress("localhost")).toBe(false);
    // 16進表記の射影アドレス。RFC 上は ::ffff:127.0.0.1 と同じだが Node は返さないので通さない
    expect(isLoopbackAddress("::ffff:7f00:1")).toBe(false);
  });

  /**
   * ★ 0 埋めを弾く。実装によって 8進数に読まれるので、
   *   「同じ文字列が別のアドレスを指す」曖昧さを持ち込まない
   */
  it("★ 8進数に読まれうる 0 埋め表記は通さない", () => {
    expect(isLoopbackAddress("0177.0.0.1")).toBe(false);
    expect(isLoopbackAddress("127.00.0.1")).toBe(false);
  });

  it("形が壊れた IPv4 は通さない", () => {
    expect(isLoopbackAddress("127.0.0")).toBe(false);
    expect(isLoopbackAddress("127.0.0.1.1")).toBe(false);
    expect(isLoopbackAddress("127.0.0.256")).toBe(false);
    expect(isLoopbackAddress("127.0.0.x")).toBe(false);
    expect(isLoopbackAddress("127.0.0. 1")).toBe(false);
  });
});
