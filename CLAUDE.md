# chatter-agent — 技術ドキュメント

## プロジェクト概要

**Claude Code の発言を、VRM キャラクターがリアルタイムで読み上げるシステム。**

表示先は Android XR グラス（XREAL Aura）越しの `chatter-mascot-xr`。将来 Electron のデスクトップ版 `chatter-mascot` も同じサーバーに繋ぐ。

[CC Mascot](https://github.com/kazakago/cc-mascot)（Mac / Electron）と目的は同じだが、**発言の取得方式が根本的に違う**。CC Mascot は Claude Code が書く jsonl ログを監視するが、本プロジェクトは **Claude Code の `MessageDisplay` hook から直接テキストを受け取る**。

- **対象は Claude Code のみ。** Codex / Gemini CLI / Antigravity は hook を持たないため対象外
- **`AGENTS.md` は置かない。** hook 依存で Claude Code 専用のため

## 一次情報の所在

**設計・根拠・実測データはすべて `_workspace/chatter-agent-design.md` にある。設計判断に迷ったらまずこれを読むこと。**

hook 方式を選んだ根拠、`MessageDisplay` の実測ペイロード（公式ドキュメントに記載が無い）、`final:true` の遅延実測、`speech.jsonl` の契約、未検証事項の一覧まで、この1文書で実装を開始できるように書いてある。

> `_workspace/` は `.gitignore` 済み。**ローカル専用の作業メモ**でリポジトリには含まれない。

## 現在の状態

**`core/` は Phase A + B が実装済み。次は `plugin/`（bash hook）。**

| ディレクトリ | 内容 | 状態 |
|---|---|---|
| `plugin/` | Claude Code プラグイン（bash hook） | **未作成。** `bin/chatter-agent-speak.mjs`（バンドル済み CLI）だけ置いてある |
| `core/` | `chatter-agent-core`（CLI + WebSocket サーバー） | **実装済み。** `summarizer/`（AI要約、既定OFF）だけ未着手 |
| `apps/chatter-mascot/` | Electron デスクトップ版 | 未作成 |
| `apps/chatter-mascot-xr/` | Unity / Android XR | 未作成 |
| `docs/` | 作業規約 | core / plugin / origin の3本 |
| `.github/workflows/` | CI（typecheck / lint / format / bundle / test） | 稼働中 |

実装フェーズは **A**（plugin + CLI で `speech.jsonl` が正しく育つ）→ **B**（WebSocket 配信）→ **C**（Unity + UniVRM）。

**Phase A / B は core 側だけ完了している。** `npm run verify:phase-a` / `verify:phase-b` で spool を手で置いた確認までは通っているが、
**受け入れ基準の「ターミナル表示と体感で同時」は実機で未確認。** ここは `plugin/` を作らないと測れない。
`plugin/` 着手時にまず `${CLAUDE_PLUGIN_ROOT}` の実体を実測すること（→ [`docs/plugin.md`](./docs/plugin.md)）。

## データフロー

```
Claude Code
  │ hooks: MessageDisplay / PreToolUse(AskUserQuestion|ExitPlanMode) / Notification(permission_prompt)
  ▼
plugin/scripts/*.sh          bash。payload を spool/<message_id>.jsonl に追記するだけ。即 exit 0
  │ 毎 delta で CLI をデタッチ起動
  ▼
chatter-agent-speak (CLI)    ロックを取れた1プロセスだけが spool を順に処理
  │                          delta 結合 → Markdown除去 → 文分割 → 感情判定 → seq 採番
  ├──▶ speech.jsonl          記録。1文1行で残す。誰も読まない
  ▼
speech/<seq>.json            配信キュー。1文1ファイル
  ▼
chatter-agent-server         キューを読んで WebSocket 配信。判断ロジックを持たない
  ▲                          ack を受けたぶんを消す
  │ ack
  ▼
chatter-mascot(-xr)          TTS → 再生 → VRM描画 / 表情 / モーション / リップシンク
```

設計の芯は2つ。

**「捕捉」と「加工」の分離。** hook は追記するだけで重い処理を一切しない。`MessageDisplay` の10秒タイムアウトと、UI をブロックしうるリスクの両方を、構造で回避している。

**「記録」と「配信」の分離。** 1つのファイルに兼ねさせると、ローテートを跨ぐ差分読み取りが要り、読み手だけが際限なく複雑になる。分ければ順序はファイル名で決まり、消費は削除で表せる。契約は [`docs/protocol.md`](./docs/protocol.md)。

## 絶対に守ること

### 1. `final:true` を待たない

1つの `message_id` は `index` 0..N で分割送信され `final:true` が終端になるが、**最終チャンクだけが実測で 34〜80秒遅れて届く**（メッセージが閉じる＝次のツール呼び出しが始まるときに flush されるため）。

**delta が届くたびに、確定した文だけを流す。** 最後の文と、未閉じの ``` 以降は保留する。
→ 根拠と実測データは設計書 §2-4

### 2. jsonl ログ監視に戻らない

jsonl の `timestamp` は**メッセージの生成時刻であって書き込み時刻ではない**。アシスタントのメッセージ行はツール結果と一緒に flush されるため、ツール呼び出しの手前に出したテキストは**ユーザーがそのツールに応答した後**にしかファイルに現れない。ログ監視である限り原理的に間に合わない。

**「hook をトリガーにして transcript を読む」ハイブリッドも同じ理由で不可。** cc-mascot / cc-mascot-xr が一度ずつ踏んだ罠なので、同じところに戻らないこと。
→ 根拠は設計書 §2-1

### 3. hook script で重い処理をしない

`MessageDisplay` のタイムアウトは**10秒**（他の hook は600秒）で、UI 表示経路に同期している可能性がある。hook は spool に追記して CLI をデタッチ起動し、即 `exit 0` する。**Node を起動しない。**
→ [`docs/plugin.md`](./docs/plugin.md)

### 4. 発話の順序を壊さない

`chatter-agent-speak` は hook から毎 delta 起動されるが、**ロックを取れた1プロセスだけが spool を処理する**。`seq` の採番もこのロック下で行う。並列に走らせたり、ロックを取らずに書いたりすると発話順が入れ替わる。

ドレイン完了後、ロックを解放する前にもう一度 spool を見る（走査直後に到着した分の取りこぼし防止）。
→ 設計書 §4-2

## ドキュメント索引

| 文書 | 読むとき |
|---|---|
| `_workspace/chatter-agent-design.md` | **設計判断をするとき。全体の一次情報**（git 管理外） |
| [`docs/protocol.md`](./docs/protocol.md) | **発話の契約。** `SpeechRecord`、配信キュー、WebSocket と ack。クライアントを書くときはここだけで足りる |
| [`docs/core.md`](./docs/core.md) | `core/` を触るとき。tsconfig の制約、バンドル方針、前身からの流用対応表 |
| [`docs/plugin.md`](./docs/plugin.md) | `plugin/` を触るとき。bash hook の制約、spool 命名、検証時の落とし穴 |
| [`docs/origin.md`](./docs/origin.md) | cc-mascot 由来のコードを触るとき。移植の対応表、フォーク点、ライセンス義務 |
| `docs/architecture.md` | **未作成。** 設計書が一次情報。実装で契約が動いたら分離を検討する |
| `docs/mascot.md` | **未作成。** Electron 版の着手時に作る |
| `docs/mascot-xr.md` | **未作成。** Unity 版の着手時に `cc-mascot-xr/xr-app/SETUP.md` を移送して作る |

## 開発コマンド

**Node は 24.11 以上**（tsdown の依存が要求する）。ルートの `mise.toml` で `24.19.0` に固定してある。

```bash
cd core
npm install
npm run typecheck
npm run lint
npm run format
npm run test:run
npm run build            # CLI → plugin/bin/、server → dist/

npm run verify:phase-a   # spool → speech.jsonl（spool を手で置いて確認）
npm run verify:phase-b   # speech.jsonl → WebSocket（実サーバーを起動して確認）
npm run start:server
```

**`plugin/bin/chatter-agent-speak.mjs` は git にコミットする成果物。** ソースを直したら
`npm run build` してコミットすること（CI の `bundle` ジョブが一致を検証する）。
→ バンドルの制約は [`docs/core.md`](./docs/core.md)

## タスク完了時のチェックリスト

- [ ] **テスト追加の検討** — 変更した箇所に関連するテストが必要か考える
- [ ] **ライセンスヘッダの確認** — cc-mascot 由来のファイルを改変したら `Modified for chatter-agent.` があること（→ [`docs/origin.md`](./docs/origin.md)）
- [ ] **ドキュメント更新の検討** — `CLAUDE.md` の状態表 / `docs/` 配下 / `README.md` に追記・編集するものがないか検討し、あればユーザーに提案する
- [ ] `npm run typecheck` — 型エラーがないこと
- [ ] `npm run lint` — エラーがないこと
- [ ] `npm run format` — フォーマットが適用されていること
- [ ] `npm run test:run` — 全てのテストが通ること

いずれも `core/` で実行する。`src/cli/` を触ったら **`npm run build` してバンドルもコミットする**こと。

## ライセンス

Apache-2.0。cc-mascot（Apache-2.0, Copyright 2026 kazakago）の派生物。

テキスト整形（`text/textFilter.ts`）と感情判定（`emotion/ruleBasedEmotionClassifier.ts`）は cc-mascot から**初回に一度だけ移植**し、以後はこのリポジトリのコードとして改変する。上流に追従する義務は負わないが、**帰属表示と改変の告知は Apache-2.0 の義務**として維持する。フォーク点・対象ファイル・ヘッダの書式は [`docs/origin.md`](./docs/origin.md)。

**「cc-mascot のツリーにあった」＝「cc-mascot の著作物」ではない。** 応答待ち通知の整形（`prompt/`）と AI要約（`summarizer/`、未着手）は、cc-mascot の作業ブランチ上で書いた**自分の著作物**で、上流の `main` には存在しない。kazakago の帰属を付けないこと。判定手順は [`docs/origin.md`](./docs/origin.md)。

cc-mascot 由来のコードを増減させたら `NOTICE` の記述が実態と合っているか確認すること。
