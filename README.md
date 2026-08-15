# chatter-agent

**Claude Code の発言を、VRM キャラクターがリアルタイムで読み上げるシステム。**

Claude Code の `MessageDisplay` hook からテキストを直接受け取り、文単位に整形して WebSocket で配信します。受け取った表示側アプリ（Electron デスクトップ / Android XR グラス）が TTS で読み上げ、VRM キャラクターの表情・モーション・リップシンクに反映します。

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
plugin/            bash。payload を spool に追記して即 exit 0
  ▼
chatter-agent-speak    delta 結合 → Markdown除去 → 文分割 → 感情判定
  ├──▶ speech.jsonl  記録（1文1行）
  ▼
speech/<seq>.json  配信キュー（1文1ファイル）
  ▼
chatter-agent-server   キューを読んで WebSocket 配信
  ▲                    ack を受けたぶんを消す
  │ ack
  ▼
chatter-mascot(-xr)    表示側アプリ。TTS → 再生 → VRM描画
```

発話の契約は [`docs/protocol.md`](./docs/protocol.md) にあります。

| ディレクトリ | 内容 |
|---|---|
| `plugin/` | Claude Code プラグイン（bash hook） |
| `core/` | `chatter-agent-core` — CLI と WebSocket サーバー |
| `apps/chatter-mascot/` | 表示側アプリ（Electron デスクトップ） |
| `apps/chatter-mascot-xr/` | 表示側アプリ（Unity / Android XR、XREAL Aura） |

## 対象

**Claude Code のみ。** hook を持たない Codex / Gemini CLI / Antigravity は対象外です。

## 現在の状態

**開発中。`core/` が動く状態になりました。**

| | 状態 |
|---|---|
| `plugin/` | 未作成（バンドル済み CLI `plugin/bin/` だけ先に置いてあります） |
| `core/` | CLI + WebSocket サーバーとも実装済み。AI要約（既定OFF）のみ未着手 |
| `apps/` | 未作成 |

実装フェーズは **A**（plugin + CLI で記録と配信キューが育つ）→ **B**（WebSocket 配信）→ **C**（表示側アプリ）。**C をデスクトップ版と XR 版のどちらから始めるかは未定です。**

Phase A / B は core 側だけ完了しています。**発言を実際に読み上げるには、まだ `plugin/`（bash hook）が要ります。**

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
```

設計方針と作業規約は [`CLAUDE.md`](./CLAUDE.md) と [`docs/`](./docs) にあります。

## ライセンス

[Apache-2.0](./LICENSE)。cc-mascot（Apache-2.0, Copyright 2026 kazakago）の派生物です。帰属表示は [`NOTICE`](./NOTICE)、移植の詳細は [`docs/origin.md`](./docs/origin.md) を参照してください。
