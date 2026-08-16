# chatter-agent — 技術ドキュメント

## プロジェクト概要

**Claude Code の発言を、VRM キャラクターがリアルタイムで読み上げるシステム。**

読み上げるのは**表示側アプリ `chatter-mascot`**。**Unity + UniVRM で1つ作り**、macOS デスクトップ（透過ウィンドウで常駐）と Android XR グラス（XREAL Aura）の両方を同じプロジェクトからビルドする。**どちらのプラットフォームから着手するかは決めていない**（→ [#12](https://github.com/schwarz9791/chatter-agent/issues/12)）。

[CC Mascot](https://github.com/kazakago/cc-mascot)（Mac / Electron）と目的は同じだが、**発言の取得方式が根本的に違う**。CC Mascot は Claude Code が書く jsonl ログを監視するが、本プロジェクトは **Claude Code の `MessageDisplay` hook から直接テキストを受け取る**。

- **対象は Claude Code のみ。** Codex / Gemini CLI / Antigravity は hook を持たないため対象外
- **`AGENTS.md` は置かない。** hook 依存で Claude Code 専用のため

## 一次情報の所在

**設計・根拠・実測データはすべて `_workspace/chatter-agent-design.md` にある。設計判断に迷ったらまずこれを読むこと。**

hook 方式を選んだ根拠、`MessageDisplay` の実測ペイロード（公式ドキュメントに記載が無い）、`final:true` の遅延実測、未検証事項の一覧まで、この1文書で実装を開始できるように書いてある。

> ★ **発話の契約だけは例外。** [#8](https://github.com/schwarz9791/chatter-agent/issues/8) で配信の形が変わり、設計書の §3 の図 / §4-4 / §5 / §6 は旧仕様（`speech.jsonl` の tail + ローテート追従 + `?since=`）のまま残っている。**契約は [`docs/protocol.md`](./docs/protocol.md) が正。** 設計書側にも註記を入れてある。

> `_workspace/` は `.gitignore` 済み。**ローカル専用の作業メモ**でリポジトリには含まれない。

## 現在の状態

**Phase A は実機で確定した。配管は player（発話 CLI）まで通っている。次は Phase C（表示側アプリ）。**

| ディレクトリ | 内容 | 状態 |
|---|---|---|
| `plugin/` | Claude Code プラグイン（bash hook） | **実装済み。** 実機で動作確認している |
| `core/` | `chatter-agent-core`（CLI + WebSocket サーバー + 発話 CLI） | **実装済み。** `summarizer/`（AI要約、既定OFF）だけ未着手 |
| `core/src/player/` | `chatter-agent-player`（WebSocket → AivisSpeech → 再生 → ack） | **実装済み**（[#11](https://github.com/schwarz9791/chatter-agent/issues/11)）。**プロトコルの参照実装。捨てない** |
| `apps/chatter-mascot/` | 表示側アプリ（**Unity + UniVRM**。macOS 常駐 + Android XR を1プロジェクトで） | 未作成 |
| `docs/` | 作業規約 | protocol / core / plugin / origin の4本 |
| `.github/workflows/` | CI（typecheck / lint / format / bundle / test / verify） | 稼働中 |

実装フェーズは **A**（plugin + CLI で記録と配信キューが正しく育つ）→ **B**（WebSocket 配信）→ **C**（表示側アプリ）。
**Phase C は Unity + UniVRM で1プロジェクト。** プラットフォーム別ではなく**レイヤーで分けてある**:
[#11](https://github.com/schwarz9791/chatter-agent/issues/11)（Node の発話 CLI）→
[#12](https://github.com/schwarz9791/chatter-agent/issues/12)（Unity の土台と発話）→
[#17](https://github.com/schwarz9791/chatter-agent/issues/17)（UniVRM の表示）→
[#16](https://github.com/schwarz9791/chatter-agent/issues/16)（デスクトップ固有）/ [#25](https://github.com/schwarz9791/chatter-agent/issues/25)（XR 固有）。
**#11 は完了した**（`core/src/player/`）。Unity のビルドを待たずに音が出る。

**Phase A は実機で動作確認した**（Claude Code 2.1.233 / macOS）。delta が hook に届いてから
`speech.jsonl` に載るまでの配管は**約 50ms** で十分速い。`MessageDisplay` は表示と同時に発火するので、
**確定した文**はターミナル表示とほぼ体感差なく発話される。ただし段落の最後の一文（さらにメッセージの
最終行）は確定が遅れる分だけ後から追いつく（→ 下の「実測で潰れた前提」/「絶対に守ること」1）。

実測で潰れた前提は [`docs/plugin.md`](./docs/plugin.md) に集約してある。要点だけ:

- `/plugin install` はプラグインを**完全コピー**する。`bin/` も実行権限ごと入るので、バンドル同梱の前提は成立
- **thinking でもサブエージェントでも発火しない**（読み上げ事故は起きない）
- **メッセージの最終行だけは final flush でしか来ない。** 保留を外しても縮まらない遅延の下限で、`AskUserQuestion` の直前では数十秒に達する

**Phase B は完了している。** `npm run verify:phase-b` で実サーバーを起動した確認に加え、
`npm run verify:player` が **hook → CLI → server → player** を通して音が鳴るところまで見ている。
Unity 側（#12）は同じ契約を踏むので、player が「正しい挙動」の突き合わせ先になる。

**実機（AivisSpeech + afplay）でも音が出るところまで確認した。** 耳で聞いた限りの体感:

- **確定した文は表示とほぼ同時。** ただし**1文目だけは合成待ちで少し間が空く**（先読みが効くのは2文目以降なので構造的にそうなる）
- **メッセージの最終行は、ターンがそのまま終わるなら遅れない。** `final` が即座に来るため。
  遅れが問題になるのは手前でツールを呼んだときで、`AskUserQuestion` の直前が最悪（→「絶対に守ること」1）
- **`**` などの記号は音にならない。** 合成エンジンが `audio_query` で読み仮名に変換する時点で落とすため。
  [#2](https://github.com/schwarz9791/chatter-agent/issues/2) の実害は「記号が読まれる」ことではなく、
  **文が変な所で割れて不自然な切れ目が入る**こと

## データフロー

```
Claude Code
  │ hooks: MessageDisplay / PreToolUse(AskUserQuestion|ExitPlanMode) / Notification(permission_prompt)
  ▼
plugin/scripts/*.sh          bash。payload を spool/<message_id>.<index>.json に置くだけ。即 exit 0
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
  ├──▶ chatter-agent-player  発話 CLI。AivisSpeech → afplay。**プロトコルの参照実装**
  ▼
chatter-mascot               表示側アプリ（Unity）。TTS → 再生 → VRM描画 / 表情 / モーション / リップシンク
```

設計の芯は2つ。

**「捕捉」と「加工」の分離。** hook は追記するだけで重い処理を一切しない。`MessageDisplay` の10秒タイムアウトと、UI をブロックしうるリスクの両方を、構造で回避している。

**「記録」と「配信」の分離。** 1つのファイルに兼ねさせると、ローテートを跨ぐ差分読み取りが要り、読み手だけが際限なく複雑になる。分ければ順序はファイル名で決まり、消費は削除で表せる。契約は [`docs/protocol.md`](./docs/protocol.md)。

## 絶対に守ること

### 1. `final:true` を待たない

1つの `message_id` は `index` 0..N で分割送信され `final:true` が終端になる。

**delta が届くたびに、確定した文だけを流す。** 最後の文と、未閉じの ``` 以降は保留する。
→ [`docs/core.md`](./docs/core.md) / `core/src/cli/messageAssembler.ts`

> 設計書 §2-4 の「最終チャンクだけが大きく遅れる」は **2.1.233 でも起きる**。`final` はメッセージが閉じる瞬間＝次のブロックが始まるときに届くので、遅延は**その手前でモデルが何をどれだけ生成したか**で決まる。ターン終了ならほぼ即座、ツール呼び出しなら数秒、**`AskUserQuestion` の直前だと数十秒**。**秒数を仕様として扱わないこと**（→ [`docs/plugin.md`](./docs/plugin.md)）。

### 2. jsonl ログ監視に戻らない

jsonl の `timestamp` は**メッセージの生成時刻であって書き込み時刻ではない**。アシスタントのメッセージ行はツール結果と一緒に flush されるため、ツール呼び出しの手前に出したテキストは**ユーザーがそのツールに応答した後**にしかファイルに現れない。ログ監視である限り原理的に間に合わない。

**「hook をトリガーにして transcript を読む」ハイブリッドも同じ理由で不可。** cc-mascot / cc-mascot-xr が一度ずつ踏んだ罠なので、同じところに戻らないこと。
→ 根拠は設計書 §2-1

### 3. hook script で重い処理をしない

`MessageDisplay` のタイムアウトは**10秒**（他の hook は600秒）で、UI 表示経路に同期している可能性がある。hook は spool に1ファイル置いて CLI をデタッチ起動し、即 `exit 0` する。**Node を起動しない。**

**追記はしない。** bash から任意長の追記を原子的にする移植可能な方法が無い（`printf` は stdio が 1024 バイト境界で write を分割する）ため、1イベント1ファイルを tmp + rename で置く。
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
| `docs/mascot.md` | **未作成。** 表示側アプリ（Unity）の着手時に作る。`cc-mascot-xr/xr-app/SETUP.md` の移送先は `apps/chatter-mascot/SETUP.md` |

## 開発コマンド

**Node は 24.11 以上**（tsdown の依存が要求する）。ルートの `mise.toml` で `24.19.0` に固定してある。

```bash
cd core
npm install
npm run typecheck
npm run lint
npm run format
npm run test:run
npm run build            # CLI → plugin/bin/、server と player → dist/

npm run verify:phase-a   # spool → 記録 + 配信キュー（payload を実際の hook に食わせて確認）
npm run verify:phase-b   # 配信キュー → WebSocket（実サーバーを起動して確認）
npm run verify:player    # WebSocket → 合成 → 再生 → ack（エンジンも音も要らない。CI で回る）
npm run start:server
npm run start:player     # 耳で確認する。AivisSpeech を起動しておくこと
```

**発話を耳で聞くには AivisSpeech.app を単体で起動する**（既定の接続先は `http://127.0.0.1:10101`）。
cc-mascot が `--port 8564` で spawn するエンジンとは別物なので、そちらに繋ぐなら
`CHATTER_AGENT_TTS_URL=http://127.0.0.1:8564` を渡す。

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
