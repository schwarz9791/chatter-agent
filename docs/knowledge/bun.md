# core を Bun で動かして踏んだこと

core を `bun build --compile` で1本の実行ファイルにし、ChatterAgent に同梱した（#169 / #212）。
その前に、コードを書き換えずに Bun でどこまで動くかを試した。ここは**その結果と実測**と、
それを受けた手当て（`→` の行）を残す。同梱で踏んだことは「1本にして同梱する」の節。

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
- `wsServer.ts` は「超過したフレームは `message` に来ない」ことを前提にしていた。
  → `message` ハンドラでも同じ上限を見て 1009 で切る（#211）。`maxPayload` は残す（Node ではフレームを溜め込む前に切れる）。
- ただし Bun では、この判定はメッセージを受け切った後に走る。Node のように溜め込む前には切れない。
  - 1フレームで送ると、5KB・8MB・15MB は受け切ってから 1009、17MB・20MB は Bun 自身が 1006 で切った。
    実効の上限は1接続あたり約 16MB。
  - 繋げるのはループバックかトークンを持つ相手だけなので、同梱しても手当てしない（#212）。

## Bun の fetch の接続エラーには `cause` が無い

- ECONNREFUSED は `TypeError { code: "ECONNREFUSED" }` で届き、`cause` を持たない。
  - Node では `cause` に `connect ECONNREFUSED 127.0.0.1:<port>` が載る。
- そのため、TTS の診断メッセージ（`tts/ttsEngine.ts` の `explain`）から接続先が消える。判定は `code` で取れる。
  → 接続先は `createRequester` が `op` に添える（`audio_query (127.0.0.1:10101) に失敗しました: …`）。

## Bun の WebSocket クライアントは、拒否された upgrade の後に `error` を出す

- 401 で拒否された後に、`error`（`Expected 101 status code`）と close 1002 が来る。Node の ws ではこれが来ない。
- `wsServer.test.ts` の Origin 拒否のテストでは、リスナーを外した後に `error` が来て unhandled error になり、
  vitest が exit 1 で終わる。テスト自体は通っている。
  → テストの `connect()` は `error` を `once` ではなく `on` で受ける。

## vitest は Bun でほぼ回る

`bunx --bun vitest run` で、Bun でだけ落ちたもの:
- 上の3つから来るテスト3件（`maxPayload` で1件、fetch の `cause` で2件）
- unhandled error 2件

モックやタイマーなど、vitest を Bun で動かすこと自体が原因の失敗は無かった。
上の手当てで、どれも Node と同じに通るようになった（CI の `bun` ジョブで回している）。

## `bun --bun run` が差し替える `node` は、`node -e` の後ろの1つ目の引数を落とす

- `node -e '<code>' a b` の `process.argv.slice(1)`:
  - node と `bun -e`: `["a","b"]`
  - `bun --bun run` の script の中の `node`: `["b"]`
- `verify-phase-a.sh` はこの形で引数を渡すので、`bun --bun run verify:phase-a` は落ちる。
  → phase-a だけは、`exec bun "$@"` だけの `node` を PATH の先頭に置いて `bash scripts/verify-phase-a.sh` で回す。
  他の `verify:*` は `bun --bun run` で足りる。

## 大きさ・起動・署名

- compile したバイナリは、1本あたり約 70MB。server・player・CLI を別々に作ると、それぞれがこの大きさになる。
- 署名は ad-hoc（linker-signed）で、`codesign --verify` は通る。
- **CLI の空振り起動**（spool が空）を 20 回 × 2 セット測った中央値:
  - node: 約 40ms
  - compile 版: 約 16ms

## 1本にして同梱する（#212）

測定条件は冒頭と同じ。server・player・CLI を `src/main.ts` からサブコマンドで振り分ける1本にし、
Tauri の `externalBin` で `.app` に入れた。

- **大きさ**: 1本で約 71MB。CLI だけを compile しても約 71MB なので、ほぼ Bun のランタイムの大きさ。`.app` は約 80MB。
- **CLI の空振り起動**（spool が空）を、3つを交互に 30 回ずつ起こした中央値:
  - node（`plugin/bin/chatter-agent-speak.mjs`）: 42.2ms
  - 1本の `chatter-agent speak`: 25.3ms
  - CLI だけを compile したもの: 17.0ms
  - 1本の方が遅いのは、使わない server / player のコードもパースするため。評価はしていない
    （`bun build --target=bun` の出力で、ciao の `source-map-support/register` は遅延の `init_*` の中にだけある）。
    hook を同梱の実行ファイルに切り替える（#213）ときの判断材料
- **`--compile` は minify しない。** 関数名が残るので、`server.log` のスタックトレースは読める。
- **compile 版は既定で、cwd の `.env` と `bunfig.toml` を読む。** `speak` は hook の cwd（利用者のプロジェクト）で走るので、
  → `--no-compile-autoload-dotenv --no-compile-autoload-bunfig` で切った。
- **`process.execPath` が実行ファイル自身になる。** `emotion/ollayaClassifier.ts` は `process.execPath -e <script>` で
  子を起こすので、`-e` が振り分けに渡り、黙って辞書式の判定へ落ちる。
  → 子の env に `BUN_BE_BUN=1` を付けて、実行ファイルを bun として振る舞わせる（Node では無視される）。
- **Tauri の bundler は sidecar も ad-hoc で署名し直す**（`Signing …/Contents/MacOS/chatter-agent`）。
  - Identifier は `chatter-agent-<ハッシュ>` になる。
  - `.app` の `codesign --verify --deep --strict` は通り、署名し直した後も実行ファイルは動く。
  - hardened runtime は付かないので、JIT の entitlements は要らない。
- **quarantine が残ったままでも、同梱の実行ファイルは起動できる。** ダウンロード相当（zip に `com.apple.quarantine` を付けて
  Archive Utility で展開。中の `chatter-agent` にも付く）の `.app` を、`xattr` を外さずに「このまま開く」で承認した:
  - 承認でフラグが `0081` → `00c1` に変わる。バンドルにも、中の `chatter-agent` にも
  - ChatterAgent が spawn した `chatter-agent server` / `player` は Gatekeeper に止められず、`[Server] Ready` まで上がった
  - Finder ではなく `mv` で `/Applications` へ移すと App Translocation で読み取り専用の別パスから動くが、
    同梱の実行ファイルは `current_exe()` の隣で見つかる

- **実 LAN 経由のトークン検査**（同梱の server に、同じ Mac から自分の LAN の IP 宛てで `GET /v1/health`）:
  - トークン無しは 401 になり、ログに `Rejected unauthorized request from <LAN の IP>` が出る。Bun でも `req.socket.remoteAddress` に実際の相手が入る
  - トークン有りは 200、ループバックは 200
  - テストは非ループバックを Unix ソケットで模擬しているので、ここは実物でしか見られない
- **XR エミュレータ（`XR_Glasses`）から同梱の server へ**: `configure-android.sh` で `ws://<LAN の IP>:<port>` と
  トークンを書き込むと、非ループバック（Mac の LAN の IP）として繋がった。同梱の `speak` で流した2文が、
  音声の取得・再生・ack まで通った。`speak` は Ollaya の子（`BUN_BE_BUN=1`）を起こして約 1.2 秒かかった
  （辞書式へ落ちるなら子はすぐ終わる）

## 確かめていないこと

- XR 実機（Aura）から同梱の server への接続（エミュレータでは確かめた）。
- Claude Code 実機の hook から同梱の実行ファイルを起こすこと（#213）、Developer ID での署名と公証。
