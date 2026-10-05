# chatter-agent

[English](./README.md) | **日本語**

**Claude Code の発言を、VRM キャラクターがリアルタイムで読み上げるシステム。**

Claude Code の `MessageDisplay` hook から発言テキストを直接受け取り、サーバーが整形して音声に合成します。表示側アプリ（macOS のデスクトップ常駐 / Android XR のグラス・ヘッドセット）はそれを受け取って鳴らし、VRM キャラクターの表情・モーション・リップシンクに反映します。

**いまは Claude Code のみです。** 発言の捕捉を `MessageDisplay` hook に頼っているので、同等の hook を持たないツールでは捕まえる先がありません（対応はロードマップに入れています）。

## 仕組み

server / client 構成です。**音声の合成はサーバー側で行い、クライアントは音声合成エンジンを持ちません。** Android XR グラスに合成エンジン（AivisSpeech）を置けないためこの形にしています。

```
Claude Code
  │ hook（MessageDisplay / PreToolUse / Notification）
  ▼
┌──────────────── server ────────────────┐
│ plugin              payload を置くだけ │
│   ▼                                    │
│ chatter-agent-speak  整形・文分割・    │
│   ▼                  感情判定・採番    │
│ 配信キュー                             │
│   ▼                                    │
│ chatter-agent-server                   │
│   ├─ WebSocket   発話テキストを配信    │
│   ├─ GET /audio/ 取りに来られたら合成  │
│   └─ /v1/*       設定の読み書き        │
└────────────────────────────────────────┘
  ▲ ack           │ テキスト / 音声
  │               ▼
┌──────────────── client ────────────────┐
│ chatter-agent-player  CLI。音を鳴らす  │
│ chatter-mascot        Unity。VRM 表示  │
└────────────────────────────────────────┘
```

| | 担当 |
|---|---|
| **server** | 発言の受け取り、テキストの整形（Markdown 除去・文分割・感情判定・AI要約）、発話キューの管理と配信、音声合成（合成エンジンの起動まで面倒を見る）、設定の保持 |
| **client** | 発話の受信、音声の取得と再生、再生し終わった通知（ack）、VRM の描画・表情・モーション・リップシンク、（Android XR のみ）空間に浮かぶ設定パネル、（デスクトップ版のみ）ウィンドウ・常駐。デスクトップの設定 UI は ChatterAgent が持ちます |

発話の契約は [`docs/protocol.md`](./docs/protocol.md) にあります。クライアントを書くならこれだけで足ります。

## 構成

| ディレクトリ | 内容 |
|---|---|
| `plugin/` | Claude Code プラグイン。bash の hook が payload を置くだけ |
| `core/` | サーバーと CLI（TypeScript / Node）。`src/server/` `src/cli/` `src/player/` |
| `chatter-mascot/` | 表示側アプリ（Unity + UniVRM）。macOS と Android XR を1プロジェクトから |
| `chatter-agent-app/` | ChatterAgent（Tauri）。メニューバー常駐でサーバーを起動・停止し、設定パネルを持ち、マスコットの表示・ミュート・大きさ・モーションの確認を操作する |
| `docs/` | 基本設計・ファイル構成・コマンド。実装で踏んだことは `docs/knowledge/` |

## 現在の状態

4つの成果物があります。

| | 状態 |
|---|---|
| **サーバー**（`chatter-agent-server`） | 動きます |
| **CLI プレーヤー**（`chatter-agent-player`） | 動きます。プロトコルの参照実装も兼ねています |
| **macOS クライアント**（`chatter-mascot`） | 動きます。透過ウィンドウで常駐し、VRM が表情・モーション・リップシンク付きで読み上げます。設定はアプリから変えられます |
| **Android XR クライアント**（同じ Unity プロジェクト。XREAL Aura / Galaxy XR 想定） | エミュレータまで。OpenXR の Full Space で空間に立ち、グラスでもヘッドセットでも背景に部屋が透けます。LAN 越しにサーバーへ繋がります。**実機は未確認** |

## ビルド

**コマンドはリポジトリのルートから実行します。**

### 前提

- **Node 24.11 以上**（`mise.toml` で 24.19.0 に固定しています）
- **Rust**（`chatter-agent-app/mise.toml` で固定しています）—— ChatterAgent をビルドするとき
- **Unity 6000.3.14f1** —— macOS / Android のビルドサポートモジュールが必要です
- **Unity CLI**（`unity` コマンド） —— `chatter-mascot/scripts/*.sh` がこれ経由で Editor を動かします
- **Git LFS** —— マスコットの同梱モデル・モーション・アイコンは LFS 管理です。無しで clone するとポインタのままビルドされ、同梱モデルに頼っていれば Cube だけが出ます（clone 済みなら `git lfs install` → `git lfs pull` のあと、ビルドし直す）
- **Android SDK の platform-tools**（`adb`）—— Android 版を端末へ入れるとき

Unity プロジェクトのセットアップ手順は [`docs/mascot.md`](./docs/mascot.md) にあります。

### サーバーと CLI

```bash
cd core
npm install
npm run build
```

`plugin/bin/chatter-agent-speak.mjs`（プラグイン同梱の CLI）と `core/dist/` にサーバー・プレーヤーが出ます。

### ChatterAgent（macOS）

```bash
cd chatter-agent-app
npm install
npm run build      # → build/ChatterAgent.app
```

サーバーの起動・停止と設定パネルを受け持つメニューバー常駐アプリです。使い方は「実行」の「サーバー」にあります。

### macOS クライアント

```bash
cd chatter-mascot
./scripts/build.sh           # → Build/ChatterMascot.app
```

**クローンした直後は Unity を開く前に [`docs/mascot.md`](./docs/mascot.md) のセットアップを済ませてください。**
マスコットは描画と再生だけで、設定パネル・メニューバー・ショートカットは持ちません（Dock にも出ません）。
キャラクターを右クリックすると ChatterAgent の設定パネルが開きます（ChatterAgent が動いていなければ起動します）。

### Android XR クライアント

```bash
cd chatter-mascot
./scripts/build-android.sh   # → Build/ChatterMascot.apk
```

## 実行

**コマンドはリポジトリのルートから実行します。**

### 前提

- **Claude Code**
- **[AivisSpeech](https://aivis-project.com/)** —— **インストールだけしておけば十分です。** 合成エンジンが動いていなければサーバーが起こし、サーバーを止めれば一緒に落ちます。手で起こすのは、別ホストのエンジンに繋ぐときと、**話者を増やすとき**（音声モデルの追加には GUI が要ります）。自動起動そのものを止めるなら `CHATTER_AGENT_TTS_SPAWN=0`
- **音声合成エンジンは差し替え可能です**（`ttsEngine`）。既定は AivisSpeech（VOICEVOX 互換）ですが、日本語以外の言語には **[Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI)**（OpenAI 互換）も使えます。導入手順は [`docs/kokoro.md`](./docs/kokoro.md)
- **macOS** —— 表示側アプリは macOS と Android XR 向けで、再生コマンドの既定は `afplay`、合成エンジンの自動探索も macOS のパスしか見ません。Linux / Windows では、CLI プレーヤーに `CHATTER_AGENT_PLAYER_COMMAND` で再生コマンドを指定してください
- **要約（任意）** —— 長い発言を短く整えてから読み上げます。既定は `fm`（macOS 27 以降に入っている Apple Foundation Models CLI）。使えない環境（macOS 27 未満・Linux・Windows）では原文がそのまま読み上げられます。`aiSummaryBackend` を `claude`（`claude -p`）に切り替えることもできます
- **感情判定（任意）** —— 発話ごとに表情を選びます。既定は **[Ollaya](https://ollaya.dev/)**（ローカルで動く decision model）で、`ollaya pull laya:multilingual` で一度モデルを取得しておけば、以後は AivisSpeech と同じく居なければサーバーが起こします。導入していない・繋がらないときは辞書式の判定にフォールバックするので、無くても発話は止まりません。`emotionClassifier` で `fm` や `dictionary`（従来の辞書式）にも切り替えられます

### Claude Code プラグインの導入

**GitHub から直接入れる**（`--sparse` でプラグインの部分だけを取ってきます）:

```bash
claude plugin marketplace add schwarz9791/chatter-agent --sparse .claude-plugin plugin
claude plugin install chatter-agent@chatter-agent
```

更新するときは、marketplace を読み直してからプラグインを更新します。更新が届くのは、プラグインの
`version` が上がったときです。

```bash
claude plugin marketplace update chatter-agent
claude plugin update chatter-agent@chatter-agent
```

**クローンから入れる**（開発向け）:

```bash
claude plugin marketplace add ./
claude plugin install chatter-agent@chatter-agent
```

この場合はクローンがそのまま使われるので、pull してセッションを開き直せば新しい中身になります。

**セッションを再起動してください。** 入ったかどうかは発話の記録で確かめられます。

```bash
tail -f "${XDG_CONFIG_HOME:-$HOME/.config}/chatter-agent/speech.jsonl"
```

黙らせたいときは `CHATTER_AGENT_DISABLE=1` を付けて Claude Code を起動します。

### サーバー

```bash
cd core && npm run start:server
```

macOS では、メニューバー常駐アプリ **ChatterAgent** にサーバーの起動・停止・再起動を任せることもできます。
`core/` と ChatterAgent をビルドしたら（→「ビルド」）、`chatter-agent-app/build/ChatterAgent.app` を開き、メニューの「core の場所を選ぶ…」で `core/` を一度選ぶと、ログインシェルと同じ環境で
サーバーを起こします。上の手動起動もこれまでどおり使えます（ChatterAgent は「外で動いている」と表示するだけで、
手を出しません）。メニューの「設定…」から、サーバーの設定とマスコットの設定（モデル・大きさ・音量・モーションなど）を変えられます。マスコットを右クリックするか、Finder などから ChatterAgent を開き直しても、設定窓が前面に出ます。

メニューの「マスコットを表示 / 隠す」と「ミュート」で、繋ぐクライアントも切り替わります。表示中は macOS アプリ、
隠している間は CLI プレーヤーで声だけを鳴らし、隠してミュートしているときは何も繋ぎません（2つを同時に繋ぐと
二重に鳴るので、ChatterAgent がどちらか一方だけを動かします）。macOS アプリは `/Applications` →
`~/Applications` → `chatter-mascot/Build` の順に `ChatterMascot.app` を探します。ショートカット
（既定は ⌃⌥M がミュート、⌃⌥H が表示切替。登録するのは ChatterAgent だけです）でも操作できます。詳細は [`docs/agent.md`](./docs/agent.md)。

### クライアント

サーバーとクライアントの組み合わせ方はケースで変わります。詳しくは
[`docs/mascot.md`](./docs/mascot.md)「接続」。

| ケース | サーバー | クライアント |
|---|---|---|
| A. Mac だけ | `cd core && npm run start:server` | macOS アプリ / CLI プレーヤー |
| B. エミュレータ・USB 接続の実機を Mac のサーバーへ | そのまま起動（既定でループバックのみ listen） | `./scripts/run-android.sh`（`adb reverse` 経由。トークンも LAN 公開も要りません。端末の `connection` は空にしておきます —— `./scripts/configure-android.sh --clear`） |
| C. LAN 越し（Wi-Fi の実機） | `CHATTER_AGENT_HOST=0.0.0.0 npm run start:server` | XR: 端末の設定パネルの「ペアリング…」に、XR なし: 繋がらないときに開くダイアログに、ChatterAgent の「Android とペアリング…」に出る4桁の PIN を入れます。mDNS が届かないとき: `./scripts/configure-android.sh` → `./scripts/run-android.sh` |
| D. Mac と Android を同時に動かす | 別のランタイムルート・別ポートでもう1本 | B か C をそのポートで |

```bash
# CLI プレーヤー（耳で聞くだけ。Unity を待たずに音が出ます）
cd core && npm run start:player
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

Android の C は、アプリを入れたあと adb なしで繋げます。サーバーを `0.0.0.0` で起動し、端末と Mac を同じ Wi-Fi に置いて、次の順に進めます。

1. ChatterAgent のメニュー「Android とペアリング…」を開く（4桁の PIN が出ます。有効 5 分、間違えられるのは 5 回まで）
2. 端末の設定パネルで「ペアリング…」を開き、PIN を入れて「ペアリング」（XR なしの端末は、繋がらないときに開くダイアログに PIN を入れます）
3. 成功するとトークンが端末に保存され、その場で繋ぎ直します（再起動は要りません。端末は mDNS で Mac を探します）

```bash
# Android（C: LAN 越し。adb で済ませたいとき、または mDNS が届かないとき）
cd chatter-mascot
./scripts/configure-android.sh          # 端末が LAN の Mac を自動で探します（mDNS）
./scripts/run-android.sh                # install して起動、logcat を流します
```

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

**Android XR もディレクトリは同じです**（端末の `Android/data/tech.sukima.chattermascot/files/` の下）が、
**`adb push` は要りません。** 接続先とトークンを設定すれば（ペアリング、または `configure-android.sh`）、起動のたびに
サーバーの `GET /v1/assets` から自動で取りに行きます（既定 `auto`。反映は次回の起動から）。
手置きもこれまでどおり使え、同期より優先されます。ただし**直下の2本は固定名で、
`models/mascot.vrm` と `animations/idle.vrma` しか読みません** —— 上の表の任意名
（`animations/*.vrma`）はここには効きません。**カテゴリ別（`animations/happy/` など）は
同期・手置きのどちらでも任意名のままで効きます。** 手順は
[`docs/mascot.md`](./docs/mascot.md)「モデルとモーションを入れる」。

## 開発

```bash
cd core
npm run typecheck
npm run lint
npm run format
npm run test:run

npm run verify:phase-a   # hook → 記録 + 配信キュー
npm run verify:phase-b   # 配信キュー → WebSocket
npm run verify:tts       # 合成と GET /audio/
npm run verify:player    # WebSocket → 音声取得 → 再生 → ack
npm run verify:assets    # マニフェスト → GET /v1/assets/ → Range で再開
```

`verify:tts` と `verify:player` は合成エンジンと再生コマンドをスタブに差し替えるので、AivisSpeech もオーディオデバイスも要りません。

```bash
cd chatter-mascot
./scripts/test.sh        # EditMode テスト
```

| 文書 | 読むとき |
|---|---|
| [`CLAUDE.md`](./CLAUDE.md) | 全体の設計と、壊してはいけない不変条件 |
| [`docs/protocol.md`](./docs/protocol.md) | 発話の契約。クライアントを書くとき |
| [`docs/core.md`](./docs/core.md) | `core/` を触るとき |
| [`docs/plugin.md`](./docs/plugin.md) | `plugin/` を触るとき |
| [`docs/mascot.md`](./docs/mascot.md) | 表示側アプリを触るとき |
| [`docs/agent.md`](./docs/agent.md) | ChatterAgent（サーバーとマスコットの面倒を見るメニューバーアプリ）を触るとき |
| [`docs/origin.md`](./docs/origin.md) | cc-mascot 由来のコードを触るとき |
| [`docs/kokoro.md`](./docs/kokoro.md) | Kokoro-FastAPI（日本語以外向けの TTS）を使うとき |
| [`docs/knowledge/`](./docs/knowledge) | 実装で踏んだこと・なぜそうしたか・実測値 |

## ロードマップ

- Claude 以外のコーディングエージェントへの対応
- 空間アンカー対応（置き直した位置を再起動しても保つ）
- CI 整備と各種アプリストアへの配信
- Android XR で Home Scene アプリと同居できるようにする
- Android グラス（INAIR Pod / Viture Neckband など）で動くようにする

## ライセンス

[Apache-2.0](./LICENSE)。[cc-mascot](https://github.com/kazakago/cc-mascot)（Apache-2.0, Copyright 2026 kazakago）の派生物です。帰属表示は [`NOTICE`](./NOTICE)、移植の詳細は [`docs/origin.md`](./docs/origin.md) にあります。
