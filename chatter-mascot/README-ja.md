# chatter-mascot

[English](./README.md) | **日本語**

表示側アプリ（Unity + UniVRM）。macOS と Android XR を1プロジェクトから。

全体像（仕組み・現在の状態・導入）は[ルートの README](../README-ja.md)を見てください。

## ビルド

**コマンドはリポジトリのルートから実行します。**

### 前提

- **Unity 6000.3.14f1** —— macOS / Android のビルドサポートモジュールが必要です
- **Unity CLI**（`unity` コマンド） —— `chatter-mascot/scripts/*.sh` がこれ経由で Editor を動かします
- **Git LFS** —— マスコットの同梱モデル・モーション・アイコンは LFS 管理です。無しで clone するとポインタのままビルドされ、同梱モデルに頼っていれば Cube だけが出ます（clone 済みなら `git lfs install` → `git lfs pull` のあと、ビルドし直す）
- **Android SDK の platform-tools**（`adb`）—— Android 版を端末へ入れるとき

Unity プロジェクトのセットアップ手順は [`docs/mascot.md`](../docs/mascot.md) にあります。

### macOS クライアント

```bash
cd chatter-mascot
./scripts/build.sh           # → Build/ChatterMascot.app
```

ビルド済みのものは [Releases](https://github.com/schwarz9791/chatter-agent/releases) にあります。

**クローンした直後は Unity を開く前に [`docs/mascot.md`](../docs/mascot.md) のセットアップを済ませてください。**
マスコットは描画と再生だけで、設定パネル・メニューバー・ショートカットは持ちません（Dock にも出ません）。
キャラクターを右クリックすると ChatterAgent の設定パネルが開きます（ChatterAgent が動いていなければ起動します）。

### Android XR クライアント

```bash
cd chatter-mascot
./scripts/build-android.sh   # → Build/ChatterMascot.apk
```

## 実行

**コマンドはリポジトリのルートから実行します。**

### クライアント

サーバーとクライアントの組み合わせ方はケースで変わります。詳しくは
[`docs/mascot.md`](../docs/mascot.md)「接続」。

| ケース | サーバー | クライアント |
|---|---|---|
| A. Mac だけ | ChatterAgent を開く | macOS アプリ / CLI プレーヤー |
| B. エミュレータ・USB 接続の実機を Mac のサーバーへ | そのまま起動（既定でループバックのみ listen） | `./scripts/run-android.sh`（`adb reverse` 経由。トークンも LAN 公開も要りません。端末の `connection` は空にしておきます —— `./scripts/configure-android.sh --clear`） |
| C. LAN 越し（Wi-Fi の実機） | `config.json` に `"host": "0.0.0.0"` を足して、ChatterAgent の「サーバーを再起動」（→ [サーバーを LAN に公開する](../README-ja.md#3-サーバーを-lan-に公開する)） | XR: 端末の設定パネルの「ペアリング…」に、XR なし: 繋がらないときに開くダイアログに、ChatterAgent の「Android とペアリング…」に出る4桁の PIN を入れます。mDNS が届かないとき: [繋がらないとき](../README-ja.md#5-繋がらないとき)を見る |
| D. Mac と Android を同時に動かす | 別のランタイムルート・別ポートでもう1本 | B か C をそのポートで |

```bash
# CLI プレーヤー（耳で聞くだけ。Unity を待たずに音が出ます）
# ChatterAgent はマスコットを隠しているあいだ自分で起こします。手で起こすのは ChatterAgent を動かしていないときだけです（2つ繋ぐと文を取り合います）
/Applications/ChatterAgent.app/Contents/MacOS/chatter-agent player
```

```bash
# macOS
open chatter-mascot/Build/ChatterMascot.app
```

```bash
# Android（B: エミュレータ・USB 接続の実機を Mac のサーバーへ）
cd chatter-mascot
./scripts/run-android.sh
```

Android の C は、[XREAL Aura で試す](../README-ja.md#xreal-aura-で試すandroid-xr)に手順をまとめています。

## モデルとモーション

**主な設定は ChatterAgent の設定パネルから変えられます。** キャラクターを右クリック、または ChatterAgent のメニューバーのアイコンから開きます —— モデル・大きさ・音量・話す速さ・音声スタイル・モーションの確認・要約・ショートカット。マスコット自身は設定を書かず、読むだけです。

設定と素材は `~/.config/chatter-agent/` に入ります（`XDG_CONFIG_HOME` を設定していればそちら）。

```
~/.config/chatter-agent/
├── config.json               サーバーの設定
├── emotion-keywords.json     感情判定のキーワード（消せば既定に戻ります）
├── mascot/settings.json      クライアントの設定（大きさは `character.scale`。書くのは ChatterAgent だけ）
├── mascot/window.json        ウィンドウの位置
├── models/mascot.vrm         ChatterAgent の設定パネルで選んだモデル
└── animations/               モーション（手で置きます）
```

**同梱しているのは CC0 のモデル1体と、待機ループのモーション1本だけです。** 感情モーションは再配布できる素材が無いので同梱していません —— **`.vrma` は手で置いてください。**

| 置き場所 | 何が再生されるか |
|---|---|
| `animations/*.vrma`（直下） | 待機ループの差し替え（同梱のものの代わり） |
| `animations/idle/` | 待機中の小ネタ（30〜60 秒ごとに1本） |
| `animations/happy/` `angry/` `sad/` `relaxed/` `surprised/` | その感情の発言でワンショット再生 → 待機へ戻る |
| `animations/walk/` | XR で歩き回るときの歩行モーション（ループ再生。macOS では使わない） |

モデルの差し替えは ChatterAgent の設定パネルからでき、モーションは「モーション」節で再生して確認できますが、**モーションを置く UI はありません。** ディレクトリを掘って `.vrma` を置くと、次の起動で拾います。`neutral` に対応するディレクトリはありません（感情モーションを出さない、が既定の振る舞いです）。

**モデルは VRM 1.0 だけに対応しています。** VRM 0.x のモデルは読めないので、代わりに同梱のモデルが出ます。理由はログに出ます（`Failed to load as VRM 1.0`）。

**Android XR もディレクトリは同じです**（端末の `Android/data/tech.sukima.chattermascot/files/` の下）が、
**`adb push` は要りません。** 接続先とトークンを設定すれば（ペアリング、または `configure-android.sh`）、起動のたびに
サーバーの `GET /v1/assets` から自動で取りに行きます（既定 `auto`。反映は次回の起動から）。
手置きもこれまでどおり使え、同期より優先されます。ただし**直下の2本は固定名で、
`models/mascot.vrm` と `animations/idle.vrma` しか読みません** —— 上の表の任意名
（`animations/*.vrma`）はここには効きません。**カテゴリ別（`animations/happy/` など）は
同期・手置きのどちらでも任意名のままで効きます。** 手順は
[`docs/mascot.md`](../docs/mascot.md)「モデルとモーションを入れる」。

## 開発

```bash
cd chatter-mascot
./scripts/test.sh        # EditMode テスト
```
