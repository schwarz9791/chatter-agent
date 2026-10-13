# リリース手順

配るもの（今はマスコットの APK）を作って版を確定させる手順。

## 前提（この Mac に一度だけ）

- release keystore がリポジトリの外にあり、パスワードがキーチェーン（サービス名 `chatter-mascot-keystore`）に
  入っていること。作り方とバックアップは [`mascot.md`](./mascot.md)「署名と版」
- Unity のビルド環境（[`mascot.md`](./mascot.md)「環境」）
- ネットワーク。release は Gradle の依存を毎回ダウンロードし直す（→ [`mascot.md`](./mascot.md)「署名と版」）

## 版を決める

セマンティックバージョニング。互換を壊す変更は MAJOR、機能の追加は MINOR、修正だけなら PATCH を上げる。
**1.0.0 になるまでは、互換を壊す変更も MINOR で上げる。**

版は前回より必ず上げる（versionCode が下がると上書きインストールが拒まれる）。タグが `mascot-v*` の
最大でなければ `build-android.sh` が止まる。タグの形と、版・versionCode の
決まり方は [`mascot.md`](./mascot.md)「署名と版」。

## マスコット（APK）

```bash
git switch main && git pull          # origin と同じ main にする
git status                           # 未追跡ファイルも含めて空であること
git tag mascot-v0.2.0                # まだ push しない
cd chatter-mascot
CHATTER_MASCOT_KEYSTORE=<keystore の絶対パス> ./scripts/build-android.sh
#   → [Build] version=0.2.0 versionCode=200 signing=release … が出る
#   → Build/ChatterMascot.apk
adb install -r Build/ChatterMascot.apk   # 端末で確かめる（手元ビルドが入っているなら先に下の注記）
git push origin mascot-v0.2.0        # 確かめてから
```

- ★ **タグは origin の main に入っているコミットに付ける。** `build-android.sh` が確かめるのは
  「作業ツリーがきれいで、HEAD に `mascot-v*` タグがちょうど1つ」だけで、main かどうかは見ない。
  push していないコミットに付けたタグを push すると、main に無いコミットの版ができる
- 端末に手元ビルド（debug 鍵）が入っていると、release の APK は鍵が違うので `adb install -r` が通らない。
  先に `adb uninstall tech.sukima.chattermascot` を行う（ペアリングはやり直し）
- Unity Editor でこのプロジェクトを開いているとビルドは失敗する。閉じてから走らせる
- 未追跡ファイルも「汚れ」として止まる（`Build/` や `Library/` は `.gitignore` 済みなので数えない）
- 版と署名は `apksigner verify --print-certs` / `aapt2 dump badging` でも確かめられる（→ [`mascot.md`](./mascot.md)「署名と版」の「確かめ方」）

## やり直すとき

- **push 前**: `git tag -d mascot-v0.2.0` でタグを消し、直す変更を main に入れてから、pull してタグを付け直す
- ★ **push した後**: タグを付け直さない。同じ versionCode で中身の違う APK ができる。直したものは次の版
  （例: `mascot-v0.2.1`）として出す
