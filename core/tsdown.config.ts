import { defineConfig } from "tsdown";

/**
 * 配布物に載せる帰属表示と改変告知。
 *
 * ★ バンドラはソースのヘッダコメントを落とす。`/plugin install` が実際に配るのは
 *   `plugin/bin/*.mjs` なので、そこに残っていないと Apache-2.0 §4(b)（改変の告知）と
 *   §4(d)（帰属表示）を満たさない。`ruleBasedEmotionClassifier` は丸ごとインライン展開
 *   されているのに、ヘッダだけが消えている状態だった。
 *
 * 内容は `plugin/NOTICE` と揃えること。対象ファイルの一覧は docs/origin.md。
 */
const LICENSE_BANNER = `/**
 * chatter-agent
 * Copyright 2026 Masaki Matsumura
 * Licensed under the Apache License, Version 2.0.
 *
 * This bundle includes software developed as part of CC Mascot
 * (https://github.com/kazakago/cc-mascot), Copyright 2026 kazakago,
 * licensed under the Apache License, Version 2.0:
 *
 *   electron/filters/textFilter.ts @ 46f7def
 *   electron/services/ruleBasedEmotionClassifier.ts @ 46f7def
 *
 * Modified for chatter-agent. See NOTICE and docs/origin.md.
 */`;

const shared = {
  format: "esm",
  platform: "node",
  target: "node24",
  dts: false,
  sourcemap: false,
  minify: false,
  outputOptions: { banner: LICENSE_BANNER },
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
    outputOptions: { ...shared.outputOptions, entryFileNames: "chatter-agent-speak.mjs" },
  },

  /**
   * server は常駐プロセスで、プラグインに同梱しない。`ws` は
   * package.json の依存として実行時に解決させる（既定で external）。
   */
  {
    ...shared,
    entry: "src/server/index.ts",
    outDir: "dist",
    clean: true,
    outputOptions: { ...shared.outputOptions, entryFileNames: "chatter-agent-server.mjs" },
  },

  /**
   * player も常駐プロセス。server と同じ扱い（`dist` へ出し、`ws` は external）。
   *
   * ★ `clean` は `dist` に出すエントリのうち1つだけが持つこと。両方が true だと、
   *   実行順によって先に出た方の成果物が消える。ここは server 側に任せる。
   */
  {
    ...shared,
    entry: "src/player/index.ts",
    outDir: "dist",
    clean: false,
    outputOptions: { ...shared.outputOptions, entryFileNames: "chatter-agent-player.mjs" },
  },
]);
