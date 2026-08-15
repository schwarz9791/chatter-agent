# chatter-agent

**Claude Code の発言を、VRM キャラクターがリアルタイムで読み上げるシステム。**

Claude Code の `MessageDisplay` hook からテキストを直接受け取り、文単位に整形して WebSocket で配信します。受け取った側（Android XR グラス / Electron デスクトップ）が TTS で読み上げ、VRM キャラクターの表情・モーション・リップシンクに反映します。

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
  ▼
speech.jsonl       1文1行
  ▼
chatter-agent-server   差分読み取り → WebSocket 配信
  ▼
chatter-mascot(-xr)    TTS → 再生 → VRM描画
```

| ディレクトリ | 内容 |
|---|---|
| `plugin/` | Claude Code プラグイン（bash hook） |
| `core/` | `chatter-agent-core` — CLI と WebSocket サーバー |
| `apps/chatter-mascot/` | Electron デスクトップ版 |
| `apps/chatter-mascot-xr/` | Unity / Android XR 版（XREAL Aura） |

## 対象

**Claude Code のみ。** hook を持たない Codex / Gemini CLI / Antigravity は対象外です。

## 現在の状態

**開発中。骨格を作り始めた段階で、実装コードはまだありません。**

| | 状態 |
|---|---|
| `plugin/` | 未作成 |
| `core/` | 雛形のみ（ツールチェーン設定） |
| `apps/` | 未作成 |

実装フェーズは **A**（plugin + CLI で `speech.jsonl` が育つ）→ **B**（WebSocket 配信）→ **C**（Unity + UniVRM）。

## 開発

```bash
cd core
npm install
npm run typecheck
npm run lint
npm run format
npm run test:run
```

設計方針と作業規約は [`CLAUDE.md`](./CLAUDE.md) と [`docs/`](./docs) にあります。

## ライセンス

[Apache-2.0](./LICENSE)。cc-mascot（Apache-2.0, Copyright 2026 kazakago）の派生物です。帰属表示は [`NOTICE`](./NOTICE)、移植の詳細は [`docs/origin.md`](./docs/origin.md) を参照してください。
