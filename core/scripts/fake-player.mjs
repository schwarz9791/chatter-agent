/**
 * `verify-player.mjs` が `playerCommand` に差し込む偽の再生コマンド。
 *
 * 受け取ったファイル名を追記ログに書くので、**実際に何がどの順で鳴ったか**が観測できる。
 * `afplay` の代わりにこれを使えるのは、プレイヤーコマンドを config で差し替えられるように
 * したから（→ core/src/player/audioPlayer.ts）。オーディオデバイスの無い CI で回せる。
 *
 * 環境変数:
 *   FAKE_PLAYER_LOG   追記先（必須）
 *   FAKE_PLAYER_MODE  ok（既定）/ fail（異常終了）/ hang（返ってこない）
 *   FAKE_PLAYER_MS    ok のときの「再生時間」。既定 20ms
 */

import * as fs from "node:fs";
import * as path from "node:path";

const file = process.argv[2];
const log = process.env.FAKE_PLAYER_LOG;
if (!log) {
  console.error("FAKE_PLAYER_LOG がありません");
  process.exit(2);
}

fs.appendFileSync(log, `${path.basename(file ?? "?")}\n`);

const mode = process.env.FAKE_PLAYER_MODE ?? "ok";
if (mode === "fail") process.exit(1);
if (mode === "hang") {
  // SIGTERM を受け取れる状態のまま返らない。タイムアウトの検証用。
  // ★ 上限を入れること。親を SIGKILL されると孤児になり、遅い CI では
  //   ハーネスが exit 0 した後もプロセスが残る
  setInterval(() => {}, 1000);
  setTimeout(() => process.exit(3), 120_000);
} else {
  setTimeout(() => process.exit(0), Number(process.env.FAKE_PLAYER_MS ?? 20));
}
