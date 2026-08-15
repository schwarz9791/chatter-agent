import { defineConfig } from "tsdown";

const shared = {
  format: "esm",
  platform: "node",
  target: "node24",
  dts: false,
  sourcemap: false,
  minify: false,
} as const;

export default defineConfig([
  /**
   * CLI は hook から**毎 delta 呼ばれる**ので `tsx`（起動 ~300ms）では使えない。バンドルする。
   *
   * 出力先が `plugin/bin/` なのは、`/plugin install` でプラグインが複製されるため
   * `${CLAUDE_PLUGIN_ROOT}` から `core/dist` が見える保証がないから（docs/core.md）。
   * 成果物は git にコミットし、CI でソースとの一致を検証して腐敗を防ぐ。
   *
   * ★ 拡張子は `.mjs` でなければならない。`plugin/bin/` に package.json を置かないので、
   *   `.js` だと Node が CJS として読んで壊れる。
   */
  {
    ...shared,
    entry: "src/cli/index.ts",
    outDir: "../plugin/bin",
    // CLI は npm 依存を持たない。万一入り込んだらバンドルに含めて実行時解決を発生させない
    deps: { alwaysBundle: [/.*/] },
    // plugin/bin/ は git 管理下。他のファイルを消しに行かせない
    clean: false,
    outputOptions: { entryFileNames: "chatter-agent-speak.mjs" },
  },

  /**
   * server は常駐プロセスで、プラグインに同梱しない。`ws` / `chokidar` は
   * package.json の依存として実行時に解決させる（既定で external）。
   */
  {
    ...shared,
    entry: "src/server/index.ts",
    outDir: "dist",
    clean: true,
    outputOptions: { entryFileNames: "chatter-agent-server.mjs" },
  },
]);
