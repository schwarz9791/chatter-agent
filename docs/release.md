# リリース手順

配るもの（マスコットの APK と macOS 版、ChatterAgent）を作って版を確定させ、GitHub Release に載せる手順。

## 前提（この Mac に一度だけ）

- release keystore がリポジトリの外にあり、パスワードがキーチェーン（サービス名 `chatter-mascot-keystore`）に
  入っていること。作り方とバックアップは [`mascot.md`](./mascot.md)「署名と版」
- updater の秘密鍵がリポジトリの外にあり、パスワードがキーチェーン（サービス名 `chatter-agent-updater-key`）に
  入っていること。ChatterAgent の更新（ChatterAgent 自身とマスコットの macOS 版）は、この鍵で署名したものだけを受け付ける。
  作り方は下の「updater の鍵」
- Unity のビルド環境（[`mascot.md`](./mascot.md)「環境」）
- ネットワーク。release は Gradle の依存を毎回ダウンロードし直す（→ [`mascot.md`](./mascot.md)「署名と版」）

## updater の鍵（一度だけ）

```bash
npx --yes @tauri-apps/cli@2.12.1 signer generate -w ~/.tauri/chatter-agent-updater.key
#   → パスワードはプロンプトで入れる。標準出力に公開鍵が出る（~/.tauri/chatter-agent-updater.key.pub にも書かれる）

# キーチェーンへ登録（-w だけ渡すとプロンプトで聞かれるので、値が argv に出ない）
security add-generic-password -a "$USER" -s chatter-agent-updater-key -w
```

- 公開鍵は `chatter-agent-app/src-tauri/tauri.conf.json` の `plugins.updater.pubkey` に入っている。鍵を作り直したら、ここも更新する
- 秘密鍵はリポジトリの外に置く。`CHATTER_AGENT_UPDATER_KEY=<秘密鍵の絶対パス>` で `build-release.sh` と
  `chatter-mascot/scripts/build.sh`（release）に渡す。パスワードは `TAURI_SIGNING_PRIVATE_KEY_PASSWORD`、無ければキーチェーンから読む。
  どちらも Unity やビルドを始める前に確かめて、足りなければ止まる
- パスワードはシェル変数に持ち、署名のコマンド（`tauri signer sign`）にだけ環境変数で渡す。ビルドや依存の `npm ci` の子プロセスには見せない
  （環境に `TAURI_SIGNING_PRIVATE_KEY_PASSWORD` / `TAURI_SIGNING_PRIVATE_KEY` があっても、関門で読んで `unset` する）
- ★ **秘密鍵とパスワードの両方をバックアップする（APK の keystore と同じ考え方。→ [`mascot.md`](./mascot.md)「署名と版」）。
  失うと、その公開鍵を入れた版の利用者に更新を配れなくなる**（以後は手で入れ替えてもらう）。パスワードは鍵と同じ場所に置かない

## 版を決める

セマンティックバージョニング。互換を壊す変更は MAJOR、機能の追加は MINOR、修正だけなら PATCH を上げる。
**1.0.0 になるまでは、互換を壊す変更も MINOR で上げる。**

タグは `mascot-v*`（マスコット）と `agent-v*`（ChatterAgent）の2系統で、版はそれぞれ独立に上げる。

版は前回より必ず上げる（versionCode が下がると上書きインストールが拒まれる）。タグが `mascot-v*` の
最大でなければ `build-android.sh` と `build.sh`（release）が止まる。タグの形と、版・versionCode の
決まり方は [`mascot.md`](./mascot.md)「署名と版」。

`agent-v*` は `agent-v<MAJOR>.<MINOR>.<PATCH>`（先頭ゼロ不可、接尾辞不可、`agent-v0.0.0` 不可）。
最大の `agent-v*` でなければ `build-release.sh` が止まる。コミットされている版（`tauri.conf.json` /
`Cargo.toml` / `package.json`）は手元ビルドの印 `0.0.0-dev` で、配る版はタグからビルド時に与える。

## マスコット（APK と macOS 版）

```bash
git switch main && git pull          # origin と同じ main にする
git status                           # 未追跡ファイルも含めて空であること
git tag mascot-v0.2.0                # まだ push しない
(cd chatter-agent-app && npm ci)     # macOS 版の zip の署名に tauri CLI を使う
cd chatter-mascot
CHATTER_MASCOT_KEYSTORE=<keystore の絶対パス> ./scripts/build-android.sh
#   → [Build] version=0.2.0 versionCode=200 signing=release … が出る
#   → Build/ChatterMascot.apk
adb install -r Build/ChatterMascot.apk   # 端末で確かめる（手元ビルドが入っているなら先に下の注記）
CHATTER_AGENT_UPDATER_KEY=<updater の秘密鍵の絶対パス> CHATTER_MASCOT_RELEASE=1 ./scripts/build.sh
#   → [Build] version=0.2.0 → Build/ChatterMascot-macOS.zip と .zip.sig が出る（版は .app から読んでタグと比べてある）
git push origin mascot-v0.2.0        # 確かめてから
```

- ★ **タグは origin の main に入っているコミットに付ける。** `build-android.sh` と `build.sh`（release）が確かめるのは
  「作業ツリーがきれいで、HEAD に `mascot-v*` タグがちょうど1つ」だけで、main かどうかは見ない。
  push していないコミットに付けたタグを push すると、main に無いコミットの版ができる
- 端末に手元ビルド（debug 鍵）が入っていると、release の APK は鍵が違うので `adb install -r` が通らない。
  先に `adb uninstall tech.sukima.chattermascot` を行う（ペアリングはやり直し）
- Unity Editor でこのプロジェクトを開いているとビルドは失敗する。閉じてから走らせる
- 未追跡ファイルも「汚れ」として止まる（`Build/` や `Library/` は `.gitignore` 済みなので数えない）
- macOS 版は ad-hoc 署名で、公証しない。署名が検証（`codesign --verify --deep --strict`）を通らないとき、
  `.app` の版がタグと合わないとき、`LSUIElement` が無いときは release ビルドが止まる
- release ビルドは、Unity のビルドにエラーが1つでもあれば止まる（`[Build] result=Succeeded errors=0` で通る）。
  シェーダーのコンパイルに失敗しても Unity は成功を返すので、結果だけでは見た目の欠けたビルドに気づけない
- 版と署名は `apksigner verify --print-certs` / `aapt2 dump badging` でも確かめられる（→ [`mascot.md`](./mascot.md)「署名と版」の「確かめ方」）

## ChatterAgent

```bash
cd "$(git rev-parse --show-toplevel)"   # マスコットの節から続けると chatter-mascot/ に居るので戻る
git switch main && git pull          # origin と同じ main にする
git status                           # 未追跡ファイルも含めて空であること
git tag agent-v0.2.0                 # まだ push しない
cd chatter-agent-app && npm ci
CHATTER_AGENT_UPDATER_KEY=<updater の秘密鍵の絶対パス> npm run build:release
#   → build/ChatterAgent.zip、更新用の build/ChatterAgent.app.tar.gz と build/latest.json
git push origin agent-v0.2.0         # 版を確かめてから（下）
```

- `build-release.sh` は作業ツリーがきれいで HEAD に `agent-v*` タグがちょうど1つ、かつ最大のときだけ通る。
  main かどうかは見ないので、マスコットと同じく origin と同じ main でタグを付ける
- `build-release.sh` は core の `npm ci` と `build:bin` を先に走らせ、できた実行ファイルを `.app` に同梱する
  （→ [`agent.md`](./agent.md)「ビルドと起動」）。bun はルートの `mise.toml` で固定している
- 版は `defaults read "$PWD/build/ChatterAgent.app/Contents/Info.plist" CFBundleShortVersionString` か、
  起動して設定窓の情報欄で確かめる
- arm64 だけのビルドになる（Intel Mac 向けは作らない）。`build-release.sh` は `uname -m` が `arm64` でなければ止まる
- `ChatterAgent.app.tar.gz` は `tauri build` の後で `build-release.sh` が自分で固めて `tauri signer sign` で署名する
  （`createUpdaterArtifacts` は使わない。鍵を `tauri build` に渡さないため）
- 署名は ad-hoc で、公証しない。開き方は Release ノート（`.github/release-notes/agent.md`）に書いてある

## GitHub Release

タグを push してから、draft で作る。ノートの雛形は `.github/release-notes/` にあり、版番号を含まないのでそのまま渡せる。

```bash
cd "$(git rev-parse --show-toplevel)"   # 上の節で chatter-mascot/ や chatter-agent-app/ に居るので戻る
gh release create mascot-v0.2.0 --draft --verify-tag --title "ChatterMascot 0.2.0" \
  --notes-file .github/release-notes/mascot.md \
  chatter-mascot/Build/ChatterMascot.apk chatter-mascot/Build/ChatterMascot-macOS.zip \
  chatter-mascot/Build/ChatterMascot-macOS.zip.sig
gh release create agent-v0.2.0 --draft --verify-tag --title "ChatterAgent 0.2.0" \
  --notes-file .github/release-notes/agent.md chatter-agent-app/build/ChatterAgent.zip \
  chatter-agent-app/build/ChatterAgent.app.tar.gz chatter-agent-app/build/latest.json
```

- ★ ChatterAgent はこれらのアセットを Releases の API で名前を指して引く。**`ChatterMascot-macOS.zip` / `.zip.sig` /
  `.app.tar.gz` / `latest.json` の名前を変えない**
- draft に今回の変更点を書き足してから publish する
- ★ **ChatterAgent とマスコットの macOS 版は publish の前に、別の Mac（無ければ同じ Mac の別ユーザー）でブラウザから
  draft の zip をダウンロードして**（quarantine が付く経路で）、ノートの開き方で開けることを確かめる。マスコットは
  開いたあと、ChatterAgent から表示できることも確かめる。確かめたら publish
  （→ [`knowledge/agent.md`](./knowledge/agent.md)「配った zip は Gatekeeper の「このまま開く」で開ける」）
- core は `ChatterAgent.app` に同梱される（`Contents/MacOS/chatter-agent`）ので、別に添付しない

## やり直すとき

- **push 前**: `git tag -d mascot-v0.2.0` でタグを消し（`agent-v*` も同じ）、直す変更を main に入れてから、pull してタグを付け直す
- ★ **push した後**: タグを付け直さない。同じ versionCode で中身の違う APK ができる。直したものは次の版
  （例: `mascot-v0.2.1`）として出す。`agent-v*` も同じ考え方
- ★ **publish した後は添付を差し替えない。** 同じ版で中身の違うものができる。次の版で出す
- draft のうちは `gh release delete <tag>` で消して、同じタグで作り直せる（タグは消えない。消さない）
