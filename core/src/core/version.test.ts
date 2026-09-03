import { describe, it, expect } from "vitest";
import * as fs from "fs";
import * as path from "path";
import { VERSION } from "./version";

describe("VERSION", () => {
  /**
   * ★ これが `version.ts` を手で二重管理してよい唯一の根拠。落ちたら
   *   `package.json` に合わせて `version.ts` を直す（逆ではない）。
   */
  it("package.json の version と一致している", () => {
    const pkgPath = path.join(import.meta.dirname, "..", "..", "package.json");
    const pkg = JSON.parse(fs.readFileSync(pkgPath, "utf-8")) as { version?: unknown };
    expect(pkg.version).toBe(VERSION);
  });
});
