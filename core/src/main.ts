#!/usr/bin/env node
/**
 * `chatter-agent` — server / player / speak を1本の実行ファイルにまとめる入口。
 *
 * 各エントリはロード時に main() を走らせるので、`import` がそのまま起動になる。
 * 動的 import にしているのは、選ばれなかった側の依存（ws / ciao。ciao はロード時に
 * source-map-support を読む）を評価させないため。`speak` は hook から毎 delta 起動される。
 */

const entries: Record<string, () => Promise<unknown>> = {
  server: () => import("./server/index"),
  player: () => import("./player/index"),
  speak: () => import("./cli/index"),
};

const role = process.argv[2] ?? "";
const load = Object.hasOwn(entries, role) ? entries[role] : undefined;
if (!load) {
  process.stderr.write("usage: chatter-agent <server|player|speak>\n");
  process.exit(2);
}
await load();
