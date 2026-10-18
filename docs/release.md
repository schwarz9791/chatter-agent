# リリース手順

配るもの（マスコットの APK と macOS 版、ChatterAgent）を作って版を確定させ、GitHub Release に載せる手順。

## 前提（この Mac に一度だけ）

- release keystore がリポジトリの外にあり、パスワードがキーチェーン（サービス名 `chatter-mascot-keystore`）に
  入っていること。作り方とバックアップは [`mascot.md`](./mascot.md)「署名と版」
- Unity のビルド環境（[`mascot.md`](./mascot.md)「環境」）
- ネットワーク。release は Gradle の依存を毎回ダウンロードし直す（→ [`mascot.md`](./mascot.md)「署名と版」）

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
cd chatter-mascot
CHATTER_MASCOT_KEYSTORE=<keystore の絶対パス> ./scripts/build-android.sh
#   → [Build] version=0.2.0 versionCode=200 signing=release … が出る
#   → Build/ChatterMascot.apk
adb install -r Build/ChatterMascot.apk   # 端末で確かめる（手元ビルドが入っているなら先に下の注記）
CHATTER_MASCOT_RELEASE=1 ./scripts/build.sh
#   → [Build] version=0.2.0 → Build/ChatterMascot-macOS.zip が出る（版は .app から読んでタグと比べてある）
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
npm run build:release                # → build/ChatterAgent.zip
git push origin agent-v0.2.0         # 版を確かめてから（下）
```

- `build-release.sh` は作業ツリーがきれいで HEAD に `agent-v*` タグがちょうど1つ、かつ最大のときだけ通る。
  main かどうかは見ないので、マスコットと同じく origin と同じ main でタグを付ける
- `build-release.sh` は core の `npm ci` と `build:bin` を先に走らせ、できた実行ファイルを `.app` に同梱する
  （→ [`agent.md`](./agent.md)「ビルドと起動」）。bun はルートの `mise.toml` で固定している
- 版は `defaults read "$PWD/build/ChatterAgent.app/Contents/Info.plist" CFBundleShortVersionString` か、
  起動して設定窓の情報欄で確かめる
- arm64 だけのビルドになる（Intel Mac 向けは作らない）
- 署名は ad-hoc で、公証しない。開き方は Release ノート（`.github/release-notes/agent.md`）に書いてある

## GitHub Release

タグを push してから、draft で作る。ノートの雛形は `.github/release-notes/` にあり、版番号を含まないのでそのまま渡せる。

```bash
cd "$(git rev-parse --show-toplevel)"   # 上の節で chatter-mascot/ や chatter-agent-app/ に居るので戻る
gh release create mascot-v0.2.0 --draft --verify-tag --title "ChatterMascot 0.2.0" \
  --notes-file .github/release-notes/mascot.md \
  chatter-mascot/Build/ChatterMascot.apk chatter-mascot/Build/ChatterMascot-macOS.zip
gh release create agent-v0.2.0 --draft --verify-tag --title "ChatterAgent 0.2.0" \
  --notes-file .github/release-notes/agent.md chatter-agent-app/build/ChatterAgent.zip
```

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
