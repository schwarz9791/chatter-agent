# chatter-agent

**Claude Code の発言を、VRM キャラクターがリアルタイムで読み上げるシステム。**

Claude Code の `MessageDisplay` hook からテキストを直接受け取り、メッセージが閉じたタイミングで文単位に整形して WebSocket で配信します。**音声の合成はサーバー側で行い**、表示側アプリ（デスクトップ / Android XR グラス）は音声を取りに行って鳴らし、VRM キャラクターの表情・モーション・リップシンクに反映します。

[CC Mascot](https://github.com/kazakago/cc-mascot)（Mac / Electron）の派生プロジェクトです。

## 何が違うのか

CC Mascot は Claude Code が書く jsonl ログを監視しますが、本プロジェクトは **hook から直接テキストを受け取ります**。

jsonl の `timestamp` はメッセージの生成時刻であって書き込み時刻ではなく、アシスタントのメッセージ行はツール結果と一緒に flush されます。つまりツール呼び出しの手前に出したテキストは、**ユーザーがそのツールに応答した後**にしかファイルに現れません。ログ監視である限り、ターミナルの表示に追いつけません。

hook 方式なら、テキストが表示されるのと同じタイミングで受け取れます。

## 構成

```
Claude Code
  │ hooks: MessageDisplay / PreToolUse / Notification
  ▼
plugin/            bash。payload を spool に置いて即 exit 0
  ▼
chatter-agent-speak    final:true を待つ → delta 結合 → Markdown除去 → 文分割 → 感情判定
  ├──▶ speech.jsonl  記録（1文1行）
  ▼
speech/<seq>.json  配信キュー（1文1ファイル）
  ▼
chatter-agent-server   キューを読んで WebSocket 配信（テキスト）
  ▲  │                 ack を受けたぶんを消す
  │  └──▶ GET /audio/…  同じポート。取りに来られた時点で合成する
  │ ack
  ▼
chatter-mascot         表示側アプリ（Unity）。音声を取得 → 再生 → VRM描画
```

**合成をサーバーに寄せてあるので、表示側アプリに音声合成エンジンが要りません。** Android XR グラスには AivisSpeech（Python ベースのネイティブバイナリ）を置けないため、この形にしています。

発話の契約は [`docs/protocol.md`](./docs/protocol.md) にあります。

| ディレクトリ | 内容 |
|---|---|
| `plugin/` | Claude Code プラグイン（bash hook） |
| `core/` | `chatter-agent-core` — CLI、WebSocket/HTTP サーバー（AivisSpeech で合成する）、発話 CLI |
| `apps/chatter-mascot/` | 表示側アプリ（Unity + UniVRM）。macOS 常駐と Android XR（XREAL Aura）を同じプロジェクトから |

## 対象

**Claude Code のみ。** hook を持たない Codex / Gemini CLI / Antigravity は対象外です。

## 現在の状態

**開発中。macOS の透過ウィンドウに VRM キャラクターが常駐して、Claude Code の発言を表情・モーション・リップシンク付きで読み上げるところまで動きます。**

| | 状態 |
|---|---|
| `plugin/` | 実装済み。実機で確認済み |
| `core/` | CLI + WebSocket/HTTP サーバー + 発話 CLI + AI要約（既定OFF）+ サーバー合成とも実装済み |
| `apps/chatter-mascot/` | **土台と発話・VRM の表示とも実装済み。** macOS ビルドで透過ウィンドウが成立し、WebSocket → 音声取得 → 再生 → ack が実機で通っています。VRM は表示・待機モーション・視線・表情・まばたき・リップシンクまで入りました。**ウィンドウの位置と大きさも覚えます**（ディスプレイをまたいでも物理サイズが変わらず、画面外に置いても次の起動で拾い直す）。残るはデスクトップ常駐（メニューバー・設定 UI）と XR |

実装フェーズは **A**（plugin + CLI で記録と配信キューが育つ）→ **B**（WebSocket 配信）→ **C**（表示側アプリ）。**C は Unity + UniVRM で1プロジェクトにまとめ、発話 → VRM 表示 → プラットフォーム固有（デスクトップの透過ウィンドウ / XR の Full Space）の順に積みます。**

Phase A は実機で動作確認しました。delta が hook に届いてから `speech.jsonl` に載るまでの配管は**約 50ms** です。発話は**メッセージが閉じた（`final:true`）タイミングで全文をまとめて**流します（実測では複数文のメッセージ 179件のうち 97.8% で即座に届きます）。

Phase C は VRM の表示まで進んでいます。実機（macOS 26 / Unity 6）で確認できたこと:

- **透過ウィンドウ**が成立する（URP の `Supports HDR` を切るのが条件）
- **クライアント側に音声合成エンジンを置かずに音が出る**
- **合成エンジンを止めても接続が切れず**、起動し直すと溜まっていた分が順に鳴る
- **サーバーを落としても自動で繋ぎ直し**、未 ack 分だけが届く
- **VRM が表示され、待機モーションと視線追従が動く**
- **発言の感情が表情になり、まばたきする**
- **発話に合わせて口が動く**（音声の振幅から作ったエンベロープで、30fps のまま）

**残っているのはプラットフォーム固有の作り込み**（デスクトップの常駐 —— Dock 非表示・メニューバー・設定 UI / XR の Full Space）です。ウィンドウの位置と大きさの管理は済んでいます。

### 試す

```bash
claude plugin marketplace add ./
claude plugin install chatter-agent@chatter-agent
# セッションを再起動してから
tail -f "${XDG_CONFIG_HOME:-$HOME/.config}/chatter-agent/speech.jsonl"
```

`CHATTER_AGENT_DISABLE=1` を付けて起動すると完全に黙ります。

### 声で聞く

`chatter-agent-player` が、配信された発話を読み上げます。Unity の表示側アプリを待たずに音が出せて、プロトコルの参照実装も兼ねています。合成そのものは `chatter-agent-server` が [AivisSpeech](https://aivis-project.com/) にやらせるので、player は音声を取りに行って鳴らすだけです。

**AivisSpeech をインストールだけしておいてください**（既定の接続先は `http://127.0.0.1:10101`）。エンジンが動いていなければ `chatter-agent-server` が起こし、サーバーを止めれば一緒に落ちます（`Ctrl-C` を連打して即座に落とした場合や `SIGKILL` の場合は残りますが、次回起動時にそれを見つけて再利用します）。繋ぎ先を設定するのは**サーバー側**です。

> 起こさせたくないときは `CHATTER_AGENT_TTS_SPAWN=0`。**新しい話者をダウンロードするときだけは AivisSpeech.app（GUI）が要ります** — 一度入れた音声モデルは GUI から独立しているので、以後はエンジン単体でもそのまま使えます。

```bash
cd core && npm install && npm run build
npm run start:server     # 別ターミナル
npm run start:player     # 別ターミナル
```

話者を変えるには**サーバーに** `CHATTER_AGENT_TTS_SPEAKER_ID`、話す速さは `CHATTER_AGENT_TTS_SPEED_SCALE`（0.5〜2.0）を指定します（起動時に話者の候補一覧が出ます）。macOS 以外では **player に** `CHATTER_AGENT_PLAYER_COMMAND` で再生コマンドを指定してください（既定は `afplay`）。

> どちらも**設定パネルから変えられます**（サーバーの `/v1/*` 越し。→ [`docs/protocol.md`](./docs/protocol.md)）。環境変数で指定した場合は環境変数が勝つので、パネル側はその項目を無効にして理由を出します。

## 開発

```bash
cd core
npm install
npm run typecheck
npm run lint
npm run format
npm run test:run
npm run build

npm run verify:phase-a   # spool → speech.jsonl
npm run verify:phase-b   # 配信キュー → WebSocket
npm run verify:tts       # server の合成と GET /audio/
npm run verify:player    # WebSocket → 音声取得 → 再生 → ack
```

`verify:tts` と `verify:player` は合成エンジンと再生コマンドをスタブに差し替えるので、AivisSpeech もオーディオデバイスも要りません（スタブに疎通できるぶん、エンジンを起こすこともありません）。`verify:phase-b` はスタブを持たないので、`CHATTER_AGENT_TTS_SPAWN=0` を渡して起こさないようにしてあります。

設計方針と作業規約は [`CLAUDE.md`](./CLAUDE.md) と [`docs/`](./docs) にあります。

## ライセンス

[Apache-2.0](./LICENSE)。cc-mascot（Apache-2.0, Copyright 2026 kazakago）の派生物です。帰属表示は [`NOTICE`](./NOTICE)、移植の詳細は [`docs/origin.md`](./docs/origin.md) を参照してください。
