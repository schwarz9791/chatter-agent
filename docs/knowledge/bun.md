# core を Bun で動かして踏んだこと

core を `bun build --compile` で1本の実行ファイルにし、ChatterAgent に同梱する計画（#169）がある。
その前に、コードを書き換えずに Bun でどこまで動くかを試した。ここは**その結果と実測だけ**を残す。

**測定条件**: bun 1.4.3 / node 24.19.0 / Apple M1 Max / macOS 27.0.1（2026-10-18）。
`npm run build` で作った `dist/` と `plugin/bin/` を、そのまま bun で動かした。compile もした。

## Node と同じに動いたもの

- **`verify:*` がすべて通った**（phase-a / b / tts / player / assets）。
  - harness は `process.execPath` で子を起こすので、`bun scripts/verify-*.mjs` で回すと server・CLI・player もすべて Bun で動く。
  - phase-a は hook が PATH の `node` を呼ぶので、bun を exec する `node` を PATH の先頭に置いて回した。
- **compile したバイナリで、hook → CLI → 配信キュー → WebSocket → 音声の取得 → 再生 → ack の1本が通った。**
- **エンジンの停止**: server に SIGTERM を1回送ると、偽エンジンとその孫が消えた。`detached` + `process.kill(-pid)` が効いている。
- **mDNS**: ciao の広告が `dns-sd` で見えた。ciao は compile のバンドルに入る。
- **`birthtimeNs`**（spool の到着順を決める値）が、node と同じ値になった。
- **`execFile` / `execFileSync` のエラー**: `code` / `signal` / `killed` / `status` は同じ値だった。
  - 違うのは `name` と message の文字列だけ。
  - `summarizer/claudeCli.ts` の失敗の見分け方は、そのまま成り立つ。
- **`verifyClient`**: Origin とトークンでの拒否が効く。どちらのランタイムでも 401 を返す。

## ★ Bun は `ws` を自前の実装に差し替え、`maxPayload` を見ない

- `import … from "ws"` は、Bun で走らせると `node_modules` の ws ではなく、Bun に組み込みの実装になる。
  - compile 版でも同じで、ws はバンドルに入らない。
  - 差し替えは bare specifier のときだけで、ws を絶対パスで import すると本物になる（これで原因を切り分けた）。
- `maxPayload: 4096` の server に 5KB のフレームを送ったときの違い:
  - Node は 1009 で切る。
  - Bun はそのまま `message` に渡す。
  - クライアントが Node か Bun かは関係ない。
- `wsServer.ts` は「超過したフレームは `message` に来ない」ことを前提にしている。
  → Bun で動かすなら、上限は `message` ハンドラで自分で見る（#211）。

## Bun の fetch の接続エラーには `cause` が無い

- ECONNREFUSED は `TypeError { code: "ECONNREFUSED" }` で届き、`cause` を持たない。
  - Node では `cause` に `connect ECONNREFUSED 127.0.0.1:<port>` が載る。
- そのため、`tts/voicevoxClient.ts` の診断メッセージから接続先が消える。判定は `code` で取れる。

## Bun の WebSocket クライアントは、拒否された upgrade の後に `error` を出す

- 401 で拒否された後に、`error`（`Expected 101 status code`）と close 1002 が来る。Node の ws ではこれが来ない。
- `wsServer.test.ts` の Origin 拒否のテストでは、リスナーを外した後に `error` が来て unhandled error になり、
  vitest が exit 1 で終わる。テスト自体は通っている。

## vitest は Bun でほぼ回る

`bunx --bun vitest run` で、Bun でだけ落ちたもの:
- 上の3つから来るテスト3件（`maxPayload` で1件、fetch の `cause` で2件）
- unhandled error 2件

モックやタイマーなど、vitest を Bun で動かすこと自体が原因の失敗は無かった。

## 大きさ・起動・署名

- compile したバイナリは、1本あたり約 70MB。server・player・CLI を別々に作ると、それぞれがこの大きさになる。
- 署名は ad-hoc（linker-signed）で、`codesign --verify` は通る。
- **CLI の空振り起動**（spool が空）を 20 回 × 2 セット測った中央値:
  - node: 約 40ms
  - compile 版: 約 16ms

## 確かめていないこと

- **実 LAN 経由のトークン検査。** テストは、非ループバックを Unix ソケットで模擬している。
  Bun では `req.socket.remoteAddress` の実際の値が効くので、XR 実機で確かめる（#212）。
- Tauri への同梱、Claude Code 実機の hook からの起動、Developer ID での署名と公証。
