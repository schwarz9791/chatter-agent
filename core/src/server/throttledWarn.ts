/**
 * 同じ警告を間引きつつ、抑制した件数を次の1行に持ち越す。
 *
 * ★ **`warnOnce`（生涯1回）にしないこと。** エンジンが落ちている間は失敗が続くのが
 *   正常なので、一度きりにすると「恒久的に詰まった状態のログが数行だけ」になる。
 *   逆に毎回出すと、先読み窓ぶん（既定4件）× 1秒で **1日 34 万行**になる。
 *
 * ★ **間引きのキーはメッセージ本文にすること。** 固定キーにすると
 *   `ECONNREFUSED` → `422`（エンジンを起動した直後に話者 ID 違いが分かる）という
 *   **いちばん見たい遷移**が窓に飲まれる。
 *
 * ★ **キーの数に上限を置くこと。** `origin` のようにクライアントが決める文字列を
 *   キーにすると、無制限に増える Map になる（`server/dispatcher.ts` の
 *   `WARNED_EPOCH_LIMIT` が同じ罠への前例）。
 */

/** 同じ本文を出し直すまでの間隔 */
const DEFAULT_INTERVAL_MS = 30_000;

/** 覚えておくキーの上限 */
const KEY_LIMIT = 64;

export function createThrottledWarn(intervalMs = DEFAULT_INTERVAL_MS): (message: string) => void {
  const seen = new Map<string, { at: number; suppressed: number }>();

  return (message) => {
    const now = Date.now();
    const last = seen.get(message);

    if (last !== undefined && now - last.at < intervalMs) {
      last.suppressed++;
      return;
    }
    if (seen.size >= KEY_LIMIT) seen.clear();

    const suppressed = last?.suppressed ?? 0;
    seen.set(message, { at: now, suppressed: 0 });
    console.warn(suppressed > 0 ? `${message}（同じ警告を ${suppressed} 件省略しました）` : message);
  };
}
