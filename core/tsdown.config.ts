import { defineConfig } from "tsdown";

/**
 * node で動かすバンドルに載せる帰属表示と改変告知。
 *
 * ★ バンドラはソースのヘッダコメントを落とす。node で動かすバンドル（開発と `CHATTER_AGENT_CLI` の
 *   経路）にも帰属表示と改変告知を残す。配る `.app` の単一バイナリには banner が付かず、
 *   同梱の `NOTICE`（`tauri.bundle.conf.json` の `resources`）が担う。
 *
 * 内容はルートの `NOTICE` と揃えること。対象ファイルの一覧は docs/origin.md。
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
   * CLI を node で動かすのは `CHATTER_AGENT_CLI` の経路（開発と、ChatterAgent を使わない運用）。
   * hook から**毎 delta 起こされる**ので `tsx` は起動が重くて使えない。バンドルする。
   */
  {
    ...shared,
    entry: "src/cli/index.ts",
    outDir: "dist",
    // CLI は npm 依存を持たない。万一入り込んだらバンドルに含めて実行時解決を発生させない
    deps: { alwaysBundle: [/.*/] },
    // dist の clean は server のエントリだけが持つ
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
