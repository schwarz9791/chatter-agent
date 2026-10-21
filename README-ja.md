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
| `core/` | サーバーと CLI（TypeScript）。Bun で1本の実行ファイルにして ChatterAgent に同梱します。`src/server/` `src/cli/` `src/player/` |
| [`chatter-mascot/`](./chatter-mascot/README-ja.md) | 表示側アプリ（Unity + UniVRM）。macOS と Android XR を1プロジェクトから |
| [`chatter-agent-app/`](./chatter-agent-app/README-ja.md) | ChatterAgent（Tauri）。メニューバー常駐でサーバーを起動・停止し、設定パネルを持ち、マスコットの表示・ミュート・大きさ・モーションの確認を操作する |
| `docs/` | 基本設計・ファイル構成・コマンド。実装で踏んだことは `docs/knowledge/` |

## 現在の状態

4つの成果物があります。

| | 状態 |
|---|---|
| **サーバー**（`chatter-agent-server`） | 動きます |
| **CLI プレーヤー**（`chatter-agent-player`） | 動きます。プロトコルの参照実装も兼ねています |
| **macOS クライアント**（`chatter-mascot`） | 動きます。透過ウィンドウで常駐し、VRM が表情・モーション・リップシンク付きで読み上げます。設定はアプリから変えられます |
| **Android XR クライアント**（同じ Unity プロジェクト。XREAL Aura / Galaxy XR 想定） | エミュレータまで。OpenXR の Full Space で空間に立ち、グラスでもヘッドセットでも背景に部屋が透けます。LAN 越しにサーバーへ繋がります。**実機は未確認**。[XREAL Aura で試す](#xreal-aura-で試すandroid-xr)を見てください |

## XREAL Aura で試す（Android XR）

XR 版はまだエミュレータでしか動かしていません。**実機に繋ぐのはこれが初めてなので、不具合の報告を歓迎します。** 上から順に進めてください。

要るものは次のとおりです。

- Apple Silicon の Mac と Claude Code
- `adb`
- XREAL Aura
- 同じ Wi-Fi に置いた Mac と Aura

**Unity と Rust は要りません。**

### 1. インストール

1. **Aura に入れるアプリ。** [Releases](https://github.com/schwarz9791/chatter-agent/releases) の `mascot-v*` から `ChatterMascot.apk` を取り、入れます。更新は `adb install -r` です。

   入れる前に、Aura で開発者向けオプションと USB デバッグを ON にし、Mac の `adb devices` に Aura が出ることを確かめます。自分でビルドした版が入っていると署名が違うので、先に `adb uninstall tech.sukima.chattermascot` します。

   ```bash
   adb install ChatterMascot.apk
   ```

2. **Mac に入れる ChatterAgent。** `agent-v*` から `ChatterAgent.zip` を取って展開し、`ChatterAgent.app` を `/Applications` へ移します。ad-hoc 署名で公証していないので、ダウンロードすると Gatekeeper に止められます。次のどちらかで開けます。
   - `xattr -dr com.apple.quarantine /Applications/ChatterAgent.app` を実行する
   - 一度開いてから、システム設定 → プライバシーとセキュリティで「このまま開く」を押す。続くダイアログでもう一度「このまま開く」を押し、Touch ID か管理者パスワードで認証する

   サーバーは同梱されていて、ChatterAgent を開くと起動します。その直後、ChatterAgent が「マスコットを表示できません」という警告を出しますが、この手順では macOS のマスコットを入れないので想定どおりです。そのあと Mac の CLI プレーヤーが声を出し始めますが、「3. サーバーを LAN に公開する」でミュートします。

3. **Claude Code プラグイン。** [Claude Code プラグインの導入](#claude-code-プラグインの導入)に従って入れ、Claude Code のセッションを再起動します。

### 2. 英語の声を選ぶ

既定の合成エンジン AivisSpeech は日本語の声です。**AivisSpeech が入っていないまま既定で使うと、テキストは Aura に届きますが音は鳴りません。**

英語の声には [Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI) を使います。導入は [`docs/kokoro.md`](./docs/kokoro.md)（英語）にあります。入れたら `~/.config/chatter-agent/config.json` で向けます。無ければ作ってください。

```json
{
  "ttsEngine": "openai",
  "kokoroDir": "/absolute/path/to/Kokoro-FastAPI"
}
```

声は `kokoroVoiceId`（既定 `af_heart`）で変えられます。

### 3. サーバーを LAN に公開する

サーバーは既定で `127.0.0.1` しか listen しないので、Aura から繋げません。同じ `config.json` に `"host": "0.0.0.0"` を足し、ChatterAgent のメニュー「サーバーを再起動」を選びます。

```json
{
  "ttsEngine": "openai",
  "kokoroDir": "/absolute/path/to/Kokoro-FastAPI",
  "host": "0.0.0.0"
}
```

- **macOS がローカルネットワークの許可を求めたら許可し、もう一度「サーバーを再起動」を選びます。** 許可より前に起動したサーバーは、Aura から見つかりません。
- ターミナルから起動するなら、先に ChatterAgent のメニュー「終了」を選び（サーバーは同時に1つしか動きません）、同梱の実行ファイルを `CHATTER_AGENT_HOST=0.0.0.0 /Applications/ChatterAgent.app/Contents/MacOS/chatter-agent server` で起こします。環境変数は `config.json` より優先されます。**この場合、許可が付くのはターミナルのアプリで、再起動もターミナル側で行います**（ChatterAgent の「サーバーを再起動」は、外で動いているサーバーには効きません）。
- **ChatterAgent のメニューで「ミュート」を ON にします**（macOS のマスコットが出ていれば「マスコットを隠す」も）。同時に繋げるクライアントは1つだけです。両方繋ぐと Mac と Aura が文を取り合い、Aura が文を飛ばします。

### 4. ペアリング

1. Aura で「Chatter Mascot」を起動します。
2. ChatterAgent のメニュー「Android とペアリング…」を開きます。4桁の PIN が出ます。有効 5 分、間違えられるのは 5 回までです。期限が切れるか 5 回間違えたら、窓の「発行し直す」で出し直せます。
3. Aura で設定パネルを開きます。視線の正面に浮かぶパネルで、開き方は2通りあります。
   - キャラクターをつまんで離す。頭上に歯車が少しの間出るので、それをつまむ
   - 手のひらを自分の頭の方へ向ける。手のひらの上にボタンが出るので、もう片方の手でつまむ
4. パネルの「接続」の下にある「ペアリング…」を押し、PIN を入れて「ペアリング」を押します。

成功するとトークンが Aura に保存され、その場で繋ぎ直します。再起動は要りません。Aura は mDNS で Mac を探します。

ChatterAgent を使わない場合は、PIN を自分で発行できます。Mac 自身から（ループバックから）叩きます。`Content-Type` ヘッダが無いと `415` が返ります。

```bash
curl -H 'Content-Type: application/json' -d '{}' http://127.0.0.1:8570/v1/pairing
# {"pin":"2458","expiresAt":1792136026504}
```

### 5. 繋がらないとき

次の順に確かめます。

1. **`host` が `127.0.0.1` のまま。** ペアリング窓に「サーバーが LAN に公開されていません。config.json の host か環境変数 CHATTER_AGENT_HOST を 0.0.0.0 にして、サーバーを再起動してください」と出ます。curl なら `409` `{"error":"not_lan"}` です。3 をやり直します。
2. **macOS のローカルネットワークの許可が拒否されている。** 許可が付くのは、サーバーを起動したアプリ（ChatterAgent か、ターミナルのアプリ）です。一度「許可しない」を選んでいると、ダイアログは二度と出ず、Mac 自身からは繋がるのに LAN からだけ繋がらなくなります。システム設定 → プライバシーとセキュリティ → ローカルネットワークでそのアプリをオンにし、「サーバーを再起動」を選びます（ターミナルで起動したなら、ターミナル側で再起動します）。
3. **mDNS が Aura に届かない。** ルーターがマルチキャスト（mDNS）を通さないときや、Mac と Aura が別のサブネット・VLAN にいるときに起きます。設定パネルには「サーバーが見つかりません」と出ます。リポジトリを clone して、接続先を明示して Aura に書き込みます。

   ```bash
   git clone https://github.com/schwarz9791/chatter-agent.git && cd chatter-agent
   ADB="$(command -v adb)" ./chatter-mascot/scripts/configure-android.sh ws://<mac-ip>:8570
   ```

   - スクリプトは `adb` を自動では探しません（既定は Android Studio の SDK のパス）。`ADB=` で渡します。
   - トークンを読むので、サーバーが動いている必要があります。トークンも書き込むので、ペアリングは要りません。
   - 繋ぐ先の候補は、サーバーのログ（「ログを開く」）に `ws://<ip>:<port>` の行で出ます。Aura と同じネットワークのものを使います。
   - 書き込んだあと、スクリプトがアプリを再起動します。

   それでも繋がらなければ、Wi-Fi がクライアント分離（ゲスト用ネットワークに多い）をしているかもしれません。分離は端末どうしの通信をすべて止めるので、接続先を明示しても繋がりません。分離の無いネットワークを使ってください。

### 6. 不具合報告のためのログ

報告は [XREAL Aura test report](https://github.com/schwarz9791/chatter-agent/issues/new?template=xr-test-report.yml) のフォームからどうぞ（英語のフォームです）。必須は環境だけで、「動かなかった」報告も歓迎します。次のものを添えてください。

- **Aura:** `adb logcat -s Unity ChatterMascot > aura.log`。アプリを起動する前に始め、症状を再現してから Ctrl-C で止めます。`ChatterMascot` は Java 側のタグで、mDNS の探索はこちらに出るので、両方要ります。
- **Mac:** ChatterAgent のメニュー「ログを開く」（サーバーのログ）。
- **どのビルドか:** 起動ログの `[Mascot] version:` の行、または XR 設定パネルの末尾。

## ビルド

ビルド済みの APK・macOS 版マスコット・ChatterAgent（macOS）は [Releases](https://github.com/schwarz9791/chatter-agent/releases) にあります。

**コマンドはリポジトリのルートから実行します。**

### 前提

- **Node 24.11 以上**（`mise.toml` で 24.19.0 に固定しています）

### サーバーと CLI

```bash
cd core
npm install
npm run build
```

`core/dist/` に CLI・サーバー・プレーヤーが出ます。**開発用です**（ChatterAgent を使うなら要りません）。ブランチの core を試すときに、ChatterAgent の `devCoreDir` で向けたり、プラグインの `CHATTER_AGENT_CLI` で指したりして使います。

### ChatterAgent（macOS）

ChatterAgent のビルド手順は [`chatter-agent-app/README-ja.md`](./chatter-agent-app/README-ja.md#ビルド) にあります。

### クライアント（macOS / Android XR）

マスコット（macOS クライアント・Android XR クライアント）のビルド手順と、Unity などの前提は [`chatter-mascot/README-ja.md`](./chatter-mascot/README-ja.md#ビルド) にあります。

## 実行

**コマンドはリポジトリのルートから実行します。**

### 前提

- **Claude Code**
- **Node 24.11 以上** —— clone からビルドするときだけ要ります（プラグイン自体は Node を使いません）
- **[AivisSpeech](https://aivis-project.com/)** —— **インストールだけしておけば十分です。** 合成エンジンが動いていなければサーバーが起こし、サーバーを止めれば一緒に落ちます。手で起こすのは、別ホストのエンジンに繋ぐときと、**話者を増やすとき**（音声モデルの追加には GUI が要ります）。自動起動そのものを止めるなら `CHATTER_AGENT_TTS_SPAWN=0`
- **音声合成エンジンは差し替え可能です**（`ttsEngine`）。既定は AivisSpeech（VOICEVOX 互換）ですが、日本語以外の言語には **[Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI)**（OpenAI 互換）も使えます。導入手順は [`docs/kokoro.md`](./docs/kokoro.md)
- **macOS** —— 表示側アプリは macOS と Android XR 向けで、再生コマンドの既定は `afplay`、合成エンジンの自動探索も macOS のパスしか見ません
- **要約（任意）** —— 長い発言を短く整えてから読み上げます。既定は `fm`（macOS 27 以降に入っている Apple Foundation Models CLI）。使えない環境（macOS 27 未満）では原文がそのまま読み上げられます。`aiSummaryBackend` を `claude`（`claude -p`）に切り替えることもできます
- **感情判定（任意）** —— 発話ごとに表情を選びます。既定は **[Ollaya](https://ollaya.dev/)**（ローカルで動く decision model）で、`ollaya pull laya:multilingual` で一度モデルを取得しておけば、以後は AivisSpeech と同じく居なければサーバーが起こします。導入していない・繋がらないときは辞書式の判定にフォールバックするので、無くても発話は止まりません。`emotionClassifier` で `fm` や `dictionary`（従来の辞書式）にも切り替えられます

### Claude Code プラグインの導入

**GitHub から直接入れる**（`--sparse` でプラグインの部分だけを取ってきます）:

```bash
claude plugin marketplace add schwarz9791/chatter-agent --sparse .claude-plugin plugin
claude plugin install chatter-agent@chatter-agent
```

プラグインの hook は ChatterAgent に同梱した実行ファイルを起こすので、**ChatterAgent を1度起動してから**喋り始めます（起動のたびに実行ファイルのパスを記録します）。

更新するときは、marketplace を読み直してからプラグインを更新します。更新が届くのは、プラグインの
`version` が上がったとき（hook、つまり `plugin/` の中身を変えたとき）だけです。

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

### サーバーとクライアント

サーバー（ChatterAgent）の使い方は [`chatter-agent-app/README-ja.md`](./chatter-agent-app/README-ja.md#実行)、クライアントの繋ぎ方は [`chatter-mascot/README-ja.md`](./chatter-mascot/README-ja.md#実行) にあります。

## モデルとモーション

モデルとモーションの置き方は [`chatter-mascot/README-ja.md`](./chatter-mascot/README-ja.md#モデルとモーション) にあります。

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

マスコットのテストは [`chatter-mascot/README-ja.md`](./chatter-mascot/README-ja.md#開発)、ChatterAgent の lint とテストは [`chatter-agent-app/README-ja.md`](./chatter-agent-app/README-ja.md#開発) にあります。

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

## ライセンス

[Apache-2.0](./LICENSE)。[cc-mascot](https://github.com/kazakago/cc-mascot)（Apache-2.0, Copyright 2026 kazakago）の派生物です。帰属表示は [`NOTICE`](./NOTICE)、移植の詳細は [`docs/origin.md`](./docs/origin.md) にあります。
