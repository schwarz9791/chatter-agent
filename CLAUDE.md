# chatter-agent — 技術ドキュメント

## プロジェクト概要

**Claude Code の発言を、VRM キャラクターがリアルタイムで読み上げるシステム。**

読み上げるのは**表示側アプリ**。デスクトップ版 `chatter-mascot` と、Android XR グラス（XREAL Aura）向けの `chatter-mascot-xr` が、どちらも同じサーバーに繋ぐ。**どちらから着手するかも、デスクトップ版のフレームワーク（Electron / Tauri / Wails）も決めていない**（→ [#11](https://github.com/schwarz9791/chatter-agent/issues/11)）。

[CC Mascot](https://github.com/kazakago/cc-mascot)（Mac / Electron）と目的は同じだが、**発言の取得方式が根本的に違う**。CC Mascot は Claude Code が書く jsonl ログを監視するが、本プロジェクトは **Claude Code の `MessageDisplay` hook から直接テキストを受け取る**。

- **対象は Claude Code のみ。** Codex / Gemini CLI / Antigravity は hook を持たないため対象外
- **`AGENTS.md` は置かない。** hook 依存で Claude Code 専用のため

## 一次情報の所在

**設計・根拠・実測データはすべて `_workspace/chatter-agent-design.md` にある。設計判断に迷ったらまずこれを読むこと。**

hook 方式を選んだ根拠、`MessageDisplay` の実測ペイロード（公式ドキュメントに記載が無い）、`final:true` の遅延実測、未検証事項の一覧まで、この1文書で実装を開始できるように書いてある。

> ★ **発話の契約だけは例外。** [#8](https://github.com/schwarz9791/chatter-agent/issues/8) で配信の形が変わり、設計書の §3 の図 / §4-4 / §5 / §6 は旧仕様（`speech.jsonl` の tail + ローテート追従 + `?since=`）のまま残っている。**契約は [`docs/protocol.md`](./docs/protocol.md) が正。** 設計書側にも註記を入れてある。

> `_workspace/` は `.gitignore` 済み。**ローカル専用の作業メモ**でリポジトリには含まれない。

## 現在の状態

**Phase A は実機で確定した。次は Phase C（表示側アプリ）。**

| ディレクトリ | 内容 | 状態 |
|---|---|---|
| `plugin/` | Claude Code プラグイン（bash hook） | **実装済み。** 実機で受け入れ基準を満たしている |
| `core/` | `chatter-agent-core`（CLI + WebSocket サーバー） | **実装済み。** `summarizer/`（AI要約、既定OFF）だけ未着手 |
| `apps/chatter-mascot/` | 表示側アプリ（デスクトップ常駐） | 未作成。**フレームワーク未定** |
| `apps/chatter-mascot-xr/` | 表示側アプリ（Unity / Android XR） | 未作成 |
| `docs/` | 作業規約 | protocol / core / plugin / origin の4本 |
| `.github/workflows/` | CI（typecheck / lint / format / bundle / test / verify） | 稼働中 |

実装フェーズは **A**（plugin + CLI で記録と配信キューが正しく育つ）→ **B**（WebSocket 配信）→ **C**（表示側アプリ）。
**Phase C をデスクトップ版と XR 版のどちらから始めるかは決めていない。**

**Phase A は実機で確定済み**（Claude Code 2.1.233 / macOS）。受け入れ基準の「ターミナル表示と体感で同時」は、
delta が hook に届いてから `speech.jsonl` に載るまで**約 50ms** で満たしている。
`MessageDisplay` は表示と同時に発火するので、体感の差は出ない。

実測で潰れた前提は [`docs/plugin.md`](./docs/plugin.md) に集約してある。要点だけ:

- `/plugin install` はプラグインを**完全コピー**する。`bin/` も実行権限ごと入るので、バンドル同梱の前提は成立
- **thinking でもサブエージェントでも発火しない**（読み上げ事故は起きない）
- **メッセージの最終行だけは final flush でしか来ない。** 保留を外しても縮まらない遅延の下限で、`AskUserQuestion` の直前では 30 秒に達する

**Phase B は core 側だけ完了している。** `npm run verify:phase-b` で実サーバーを起動した確認までは通っているが、
実際の表示側アプリを繋いだ確認は Phase C 待ち。

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
chatter-mascot(-xr)          表示側アプリ。TTS → 再生 → VRM描画 / 表情 / モーション / リップシンク
```

設計の芯は2つ。

**「捕捉」と「加工」の分離。** hook は追記するだけで重い処理を一切しない。`MessageDisplay` の10秒タイムアウトと、UI をブロックしうるリスクの両方を、構造で回避している。

**「記録」と「配信」の分離。** 1つのファイルに兼ねさせると、ローテートを跨ぐ差分読み取りが要り、読み手だけが際限なく複雑になる。分ければ順序はファイル名で決まり、消費は削除で表せる。契約は [`docs/protocol.md`](./docs/protocol.md)。

## 絶対に守ること

### 1. `final:true` を待たない

1つの `message_id` は `index` 0..N で分割送信され `final:true` が終端になる。**delta が届くたびに、確定した文だけを流す。**

「確定した文」の判定は2段ある。**非 final の delta は必ず行単位で届く**（Claude Code のスキーマ記述。実測でも全て改行終わり）ので、蓄積テキストが行として閉じていれば最後の文はもう伸びない。閉じていなければ最後の文を保留する。未閉じの ``` 以降も保留する。
→ [`docs/core.md`](./docs/core.md) / `core/src/cli/messageAssembler.ts`

> 設計書 §2-4 の「最終チャンクだけが 34〜80秒遅れて届く」は **2.1.233 でも再現する**。`final` はメッセージが閉じる瞬間＝次のブロックが始まるときに届くので、遅延の正体は**その手前の thinking の長さ**。ターン終了で +0.62 秒、ツール呼び出しで +3.3〜7.0 秒、**`AskUserQuestion` の直前で +30.06 秒**（→ [`docs/plugin.md`](./docs/plugin.md)）。

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

### 5. 記録と配信を1つのファイルに兼ねさせない

`speech.jsonl`（記録）と `speech/<seq>.json`（配信キュー）は別物。1つに兼ねさせると、ローテートを跨ぐ差分読み取りが要り、読み手だけが際限なく複雑になる。**取りこぼしと二重配信を実際に両方踏んだ。**

分ければ順序はファイル名で決まり、消費は削除で表せる。`?since=` も要らない（接続直後に未 ack 分が流れる）。
→ 経緯は [#8](https://github.com/schwarz9791/chatter-agent/issues/8)、契約は [`docs/protocol.md`](./docs/protocol.md)

## ドキュメント索引

| 文書 | 読むとき |
|---|---|
| `_workspace/chatter-agent-design.md` | **設計判断をするとき。全体の一次情報**（git 管理外） |
| [`docs/protocol.md`](./docs/protocol.md) | **発話の契約。** `SpeechRecord`、配信キュー、WebSocket と ack。クライアントを書くときはここだけで足りる |
| [`docs/core.md`](./docs/core.md) | `core/` を触るとき。tsconfig の制約、バンドル方針、前身からの流用対応表 |
| [`docs/plugin.md`](./docs/plugin.md) | `plugin/` を触るとき。bash hook の制約、spool 命名、検証時の落とし穴 |
| [`docs/origin.md`](./docs/origin.md) | cc-mascot 由来のコードを触るとき。移植の対応表、フォーク点、ライセンス義務 |
| `docs/architecture.md` | **未作成。** 設計書が一次情報。実装で契約が動いたら分離を検討する |
| `docs/mascot.md` | **未作成。** 表示側アプリ（デスクトップ）の着手時に作る |
| `docs/mascot-xr.md` | **未作成。** 表示側アプリ（Unity / Android XR）の着手時に `cc-mascot-xr/xr-app/SETUP.md` を移送して作る |

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

npm run verify:phase-a   # spool → 記録 + 配信キュー（spool を手で置いて確認）
npm run verify:phase-b   # 配信キュー → WebSocket（実サーバーを起動して確認）
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
