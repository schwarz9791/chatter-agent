/**
 * バンドルに焼き込むバージョン。`GET /v1/health` と、将来の診断ログが名乗る値。
 *
 * ★ **`package.json` を実行時に読まないこと。** CLI（`plugin/bin/chatter-agent-speak.mjs`）は
 *   `/plugin install` でコピーされた先から動くので、隣に `package.json` がある保証が無い。
 *   バンドラの `define` にもしない —— 型が付かず、テストからも参照できなくなる。
 *
 * ★ **`package.json` との一致は `version.test.ts` が固定する。** 手で二重管理する形は
 *   必ずズレるので、ズレたらテストが落ちるようにしてある。
 */
export const VERSION = "0.1.0";
