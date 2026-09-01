/**
 * peer アドレスがループバックか。**純粋関数。**
 *
 * 制御 API の書き込み口（`PATCH` / `POST`）を「同じマシンから」に絞るのに使う
 * （→ `server/controlApi.ts`）。サーバーの bind は既定 `0.0.0.0` のままにする ——
 * 変えると LAN の Android から繋ぐ [#25] が死ぬので、**絞るのは書き込み口だけ**。
 *
 * ★★ **`X-Forwarded-For` を見ないこと。** あれはクライアントが自由に付けられるヘッダで、
 *   「信頼できるプロキシの後ろ」という前提が無い限り**そのまま偽装できる**。ここで見ると、
 *   絞りが1ヘッダで無効になる。見るのは TCP の peer アドレス（`req.socket.remoteAddress`）だけ。
 *
 * ★★ **判定できないものは false に倒す**（fail closed）。ここを間違えた方向へ寛容にすると
 *   「LAN から設定を書き換えられる」になる。逆に厳しすぎた場合の症状は
 *   「ローカルなのに 404」で、実害は小さく、気付ける。
 *
 * [#25]: https://github.com/schwarz9791/chatter-agent/issues/25
 */

/** `127.0.0.0/8`。**`127.0.0.1` だけではない**（`127.0.0.53` などを実際に使う環境がある） */
function isLoopbackIpv4(text: string): boolean {
  const parts = text.split(".");
  if (parts.length !== 4) return false;
  const octets: number[] = [];
  for (const part of parts) {
    // ★ `Number("")` は 0、`Number(" 1 ")` は 1 になる。数字だけであることを先に見る
    //   （`01` のような 0 埋めも弾く —— 実装によって 8進数に読まれることがあり、
    //   「同じ文字列が別のアドレスを指す」曖昧さを持ち込まない）
    if (!/^(0|[1-9]\d{0,2})$/.test(part)) return false;
    const n = Number(part);
    if (n > 255) return false;
    octets.push(n);
  }
  return octets[0] === 127;
}

export function isLoopbackAddress(address: string | undefined | null): boolean {
  if (typeof address !== "string" || address.length === 0) return false;

  // ゾーン ID（`::1%lo0`）を落とす。付いた形で来ても取りこぼさないため
  const bare = (address.split("%")[0] ?? "").trim().toLowerCase();
  if (bare.length === 0) return false;

  if (bare === "::1") return true;
  if (isLoopbackIpv4(bare)) return true;

  // IPv4射影アドレス。デュアルスタックの listen では Node がこの形で返す
  // （実測: `::ffff:127.0.0.1`）。
  //
  // ★ 16進表記（`::ffff:7f00:1`）は**受けない**。RFC 4291 上は同じアドレスだが、
  //   Node がこの形を返すことは無い。「たぶん同じもの」を自前のパーサで広げるより、
  //   知っている形だけを通して残りは 404 に倒す方が安全（上のヘッダ ★★）。
  if (bare.startsWith("::ffff:")) return isLoopbackIpv4(bare.slice("::ffff:".length));

  return false;
}
