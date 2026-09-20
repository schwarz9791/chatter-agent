# chatter-agent

[English](./README.md) | **日本語**

**Claude Code の発言を、VRM キャラクターがリアルタイムで読み上げるシステム。**

Claude Code の `MessageDisplay` hook から発言テキストを直接受け取り、サーバーが整形して音声に合成します。表示側アプリ（macOS のデスクトップ常駐 / Android XR グラス）はそれを受け取って鳴らし、VRM キャラクターの表情・モーション・リップシンクに反映します。

**対象は Claude Code のみです。** hook を持たない他のコーディングエージェントには対応していません。

## 仕組み

server / client 構成です。**音声の合成はサーバー側で行い、クライアントは音声合成エンジンを持ちません。** Android XR グラスに合成エンジン（AivisSpeech）を置けないためこの形にしています。

```
Claude Code
  │ hook（MessageDisplay / PreToolUse / Notification）
  ▼
┌─────────────── server ───────────────┐
│ plugin              payload を置くだけ │
│   ▼                                  │
│ chatter-agent-speak  整形・文分割・     │
│   ▼                  感情判定・採番     │
│ 配信キュー                             │
│   ▼                                  │
│ chatter-agent-server                 │
│   ├─ WebSocket   発話テキストを配信      │
│   ├─ GET /audio/ 取りに来られたら合成    │
│   └─ /v1/*       設定の読み書き         │
└───────────────────────────────────────┘
  ▲ ack           │ テキスト / 音声
  │               ▼
┌─────────────── client ───────────────┐
│ chatter-agent-player  CLI。音を鳴らす  │
│ chatter-mascot        Unity。VRM 表示 │
└──────────────────────────────────────┘
```

| | 担当 |
|---|---|
| **server** | 発言の受け取り、テキストの整形（Markdown 除去・文分割・感情判定・AI要約）、発話キューの管理と配信、音声合成（合成エンジンの起動まで面倒を見る）、設定の保持 |
| **client** | 発話の受信、音声の取得と再生、再生し終わった通知（ack）、VRM の描画・表情・モーション・リップシンク、（デスクトップ版のみ）ウィンドウ・常駐・設定 UI |

発話の契約は [`docs/protocol.md`](./docs/protocol.md) にあります。クライアントを書くならこれだけで足ります。

## 構成

| ディレクトリ | 内容 |
|---|---|
| `plugin/` | Claude Code プラグイン。bash の hook が payload を置くだけ |
| `core/` | サーバーと CLI（TypeScript / Node）。`src/server/` `src/cli/` `src/player/` |
| `apps/chatter-mascot/` | 表示側アプリ（Unity + UniVRM）。macOS と Android XR を1プロジェクトから |
| `docs/` | 基本設計・ファイル構成・コマンド。実装で踏んだことは `docs/knowledge/` |

## 現在の状態

4つの成果物があります。

| | 状態 |
|---|---|
| **サーバー**（`chatter-agent-server`） | 動きます |
| **CLI プレーヤー**（`chatter-agent-player`） | 動きます。プロトコルの参照実装も兼ねています |
| **macOS クライアント**（`chatter-mascot`） | 動きます。透過ウィンドウで常駐し、VRM が表情・モーション・リップシンク付きで読み上げます。設定はアプリから変えられます |
| **Android XR クライアント**（同じ Unity プロジェクト。XREAL Aura 想定） | エミュレータまで。OpenXR の Full Space で空間に立ち、LAN 越しにサーバーへ繋がります。**実機は未確認** |

## ビルド

### 前提

- **Node 24.11 以上**（`mise.toml` で 24.19.0 に固定しています）
- **Unity 6000.3.14f1** —— macOS / Android のビルドサポートモジュールが必要です
- **Xcode コマンドラインツール** —— macOS 常駐用のネイティブプラグインをビルドするのに使います（`xcode-select --install`）
- **Android SDK の platform-tools**（`adb`）—— Android 版を端末へ入れるとき

Unity プロジェクトのセットアップ手順は [`docs/mascot.md`](./docs/mascot.md) にあります。

### サーバーと CLI

```bash
cd core
npm install
npm run build
```

`plugin/bin/chatter-agent-speak.mjs`（プラグイン同梱の CLI）と `core/dist/` にサーバー・プレーヤーが出ます。

### macOS クライアント

```bash
cd apps/chatter-mascot
./scripts/build.sh           # → Build/ChatterMascot.app
```

常駐用のネイティブプラグイン（Objective-C）は `build.sh` が先にビルドします。**クローンした直後は
Unity を開く前に [`docs/mascot.md`](./docs/mascot.md) のセットアップを済ませてください。**

### Android XR クライアント

```bash
cd apps/chatter-mascot
./scripts/build-android.sh   # → Build/ChatterMascot.apk
```

## 実行

### 前提

- **Claude Code**
- **[AivisSpeech](https://aivis-project.com/)** —— **インストールだけしておけば十分です。** 合成エンジンが動いていなければサーバーが起こし、サーバーを止めれば一緒に落ちます。手で起こす必要があるのは、別ホストのエンジンに繋ぐときだけです

### Claude Code プラグインの導入

```bash
claude plugin marketplace add ./
claude plugin install chatter-agent@chatter-agent
```

**セッションを再起動してください。** 入ったかどうかは発話の記録で確かめられます。

```bash
tail -f "${XDG_CONFIG_HOME:-$HOME/.config}/chatter-agent/speech.jsonl"
```

黙らせたいときは `CHATTER_AGENT_DISABLE=1` を付けて Claude Code を起動します。

### サーバー

```bash
cd core && npm run start:server
```

### クライアント

```bash
# CLI プレーヤー（耳で聞くだけ。Unity を待たずに音が出ます）
cd core && npm run start:player

# macOS
open apps/chatter-mascot/Build/ChatterMascot.app

# Android XR（端末に接続先を書いてから入れる）
cd apps/chatter-mascot
./scripts/configure-android.sh          # LAN の IP を自動で組み立てます
./scripts/run-android.sh                # install して起動、logcat を流します
```

## モデルとモーション

**主な設定は macOS クライアントから変えられます。** キャラクターを右クリック、またはメニューバーのアイコンから設定パネルが開きます —— モデル・大きさ・音量・話す速さ・音声スタイル・モーション・要約・ショートカット。

設定と素材は `~/.config/chatter-agent/` に入ります（`XDG_CONFIG_HOME` を設定していればそちら）。

```
~/.config/chatter-agent/
├── config.json               サーバーの設定
├── emotion-keywords.json     感情判定のキーワード（消せば既定に戻ります）
├── mascot/settings.json      クライアントの設定
├── mascot/window.json        ウィンドウの位置と大きさ
├── models/mascot.vrm         設定パネルで選んだモデル
└── animations/               モーション（手で置きます）
```

**同梱しているのは CC0 のモデル1体と、待機ループのモーション1本だけです。** 感情モーションは再配布できる素材が無いので同梱していません —— **`.vrma` は手で置いてください。**

| 置き場所 | 何が再生されるか |
|---|---|
| `animations/*.vrma`（直下） | 待機ループの差し替え（同梱のものの代わり） |
| `animations/idle/` | 待機中の小ネタ（30〜60 秒ごとに1本） |
| `animations/happy/` `angry/` `sad/` `relaxed/` `surprised/` | その感情の発言でワンショット再生 → 待機へ戻る |

モデルの差し替えは設定パネルからできますが、**モーションに UI はありません。** ディレクトリを掘って `.vrma` を置くと、次の起動で拾います。`neutral` に対応するディレクトリはありません（感情モーションを出さない、が既定の振る舞いです）。

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
```

`verify:tts` と `verify:player` は合成エンジンと再生コマンドをスタブに差し替えるので、AivisSpeech もオーディオデバイスも要りません。

```bash
cd apps/chatter-mascot
./scripts/test.sh        # EditMode テスト
```

| 文書 | 読むとき |
|---|---|
| [`CLAUDE.md`](./CLAUDE.md) | 全体の設計と、壊してはいけない不変条件 |
| [`docs/protocol.md`](./docs/protocol.md) | 発話の契約。クライアントを書くとき |
| [`docs/core.md`](./docs/core.md) | `core/` を触るとき |
| [`docs/plugin.md`](./docs/plugin.md) | `plugin/` を触るとき |
| [`docs/mascot.md`](./docs/mascot.md) | 表示側アプリを触るとき |
| [`docs/origin.md`](./docs/origin.md) | cc-mascot 由来のコードを触るとき |
| [`docs/knowledge/`](./docs/knowledge) | 実装で踏んだこと・なぜそうしたか・実測値 |

## ロードマップ

- サーバー側からのモデル・モーションの配信
- 多言語対応 —— TTS を差し替えられるようにする / 辞書ベースの感情判定をやめる
- Claude 以外のコーディングエージェントへの対応
- XR 空間である程度動き回る対応、空間アンカー対応
- CI 整備と各種アプリストアへの配信
- Galaxy XR 対応
- Android XR の Home Scene でも表示できるようにする
- Android グラス（INAIR Pod / Viture Neckband など）で動くようにする

## ライセンス

[Apache-2.0](./LICENSE)。[cc-mascot](https://github.com/kazakago/cc-mascot)（Apache-2.0, Copyright 2026 kazakago）の派生物です。帰属表示は [`NOTICE`](./NOTICE)、移植の詳細は [`docs/origin.md`](./docs/origin.md) にあります。
