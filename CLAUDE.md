# chatter-agent — 開発の規約

**Claude Code の発言を、VRM キャラクターがリアルタイムで読み上げるシステム。** Claude Code の
`MessageDisplay` hook から発言を受け取り、サーバーが整形・合成し、表示側アプリ（macOS 常駐 /
Android XR）が鳴らして VRM に反映する。

**対象は Claude Code のみ。** hook を持たない Codex / Gemini CLI / Antigravity は対象外なので、
**`AGENTS.md` は置かない。**

使い方・ビルド・実行は [`README-ja.md`](./README-ja.md)（英語版は [`README.md`](./README.md)）。

## ディレクトリ構成

| ディレクトリ | 内容 |
|---|---|
| `plugin/` | Claude Code プラグイン。bash hook が payload を spool に置いて即 `exit 0` する |
| `plugin/bin/` | `chatter-agent-speak` のバンドル。**git にコミットする成果物**（`core/` からビルドされる） |
| `core/src/cli/` | `chatter-agent-speak`。spool を読む単一ワーカー |
| `core/src/server/` | `chatter-agent-server`。WebSocket 配信 + 音声の HTTP 配布 + 制御 API |
| `core/src/player/` | `chatter-agent-player`。発話 CLI。**プロトコルの参照実装。捨てない** |
| `core/src/core/` | 契約と基盤（型・パス・設定・ロック・キュー） |
| `core/src/text/` `emotion/` `prompt/` `summarizer/` `tts/` | 整形・感情判定・応答待ち通知・AI要約（既定OFF）・合成クライアント |
| `apps/chatter-mascot/` | 表示側アプリ（Unity + UniVRM）。macOS と Android XR を1プロジェクトから |
| `docs/` | 基本設計・ファイル構成・コマンド |
| `docs/knowledge/` | 実装で踏んだこと・なぜそうしたか・実測値 |

## アーキテクチャ

```
Claude Code
  │ hooks: MessageDisplay / PreToolUse(AskUserQuestion|ExitPlanMode) / Notification(permission_prompt)
  ▼
plugin/scripts/*.sh          bash。payload を spool/<message_id>.<index>.json に置くだけ。即 exit 0
  │ 毎 delta で CLI をデタッチ起動
  ▼
chatter-agent-speak (CLI)    ロックを取れた1プロセスだけが spool を順に処理
  │                          final:true を待つ（非 final では何もせず終わる）
  │                          delta 結合 → Markdown除去 → 文分割 → 要約（既定OFF） → 感情判定 → epoch/seq 採番
  ├──▶ speech.jsonl          記録。1文1行で残す。配信は読まない
  ▼
speech/<seq>.json            配信キュー。1文1ファイル
  ▼
chatter-agent-server         キューを読んで WebSocket 配信（テキスト。即座に seq 順）
  ▲  │                       ack を受けたぶんを消す
  │  ├──▶ GET /audio/<epoch>-<seq>.wav    同じポート。取りに来られた時点で合成する
  │  │                       （エンジンが居なければ起動時に起こす。待たない）
  │  └──▶ /v1/*              設定パネルの制御 API。**書き込み口はループバック限定**
  │                          既定 bind は 127.0.0.1。LAN から繋ぐなら Bearer トークン必須
  │ ack
  ├──▶ chatter-agent-player  発話 CLI
  ▼
chatter-mascot               表示側アプリ（Unity）。再生 → VRM描画 / 表情 / モーション / リップシンク
```

設計の芯は3つ。

**「捕捉」と「加工」の分離。** hook は spool に置くだけで重い処理を一切しない。`MessageDisplay` の
10秒タイムアウトと、UI をブロックしうるリスクの両方を構造で回避している。

**「記録」と「配信」の分離。** 1つのファイルに兼ねさせると、ローテートを跨ぐ差分読み取りが要り、
読み手だけが際限なく複雑になる。分ければ順序はファイル名で決まり、消費は削除で表せる。

**「テキストの配信」と「音声の受け渡し」の分離。** テキストは WebSocket で即座に流れ、音声は
クライアントが必要になったときに HTTP で取りに行く。合成をサーバーへ寄せたのは **XR グラス
（Android）に AivisSpeech を置けない**ため。

## 絶対に守ること

根拠・経緯・実測は [`docs/knowledge/`](./docs/knowledge) にある。**ここにあるのは守るべきことだけ。**

### 1. `final:true` を待つ — 発話はメッセージ単位

1つの `message_id` は `index` 0..N で分割送信され `final:true` が終端になる。**`final` が来るまで
1文も出さない。** 来たらメッセージ全文をまとめて1回で流す（`core/src/cli/worker.ts` の
`processMessage`）。

**`final` が来ないメッセージは救済する。** ESC 中断・クラッシュ・`index` 欠番でメッセージが
閉じないことはある。**同一セッションの**後続イベントが到着したら打ち切って全文を出し spool を
消す（`hasNewerInSameSession`）。セッションを限定しないと、Claude Code を2枚開いただけで
**まだ伸びる途中のメッセージが分断される**。

★ **`final` の到着までの秒数を仕様として扱わないこと。** ターン終了ならほぼ即座、ツール呼び出し
なら数秒、`AskUserQuestion` の直前だと数十秒。マシンと生成内容で変わる。

### 2. jsonl ログ監視に戻らない

jsonl の `timestamp` は**メッセージの生成時刻であって書き込み時刻ではない。** アシスタントの
メッセージ行はツール結果と一緒に flush されるため、ツール呼び出しの手前に出したテキストは
**ユーザーがそのツールに応答した後**にしかファイルに現れない。ログ監視である限り原理的に
間に合わない。

**「hook をトリガーにして transcript を読む」ハイブリッドも同じ理由で不可。**

### 3. hook script で重い処理をしない

`MessageDisplay` のタイムアウトは**10秒**（他の hook は600秒）で、UI 表示経路に同期している
可能性がある。hook は spool に1ファイル置いて CLI をデタッチ起動し、即 `exit 0` する。

- **Node を起動しない。**
- **追記はしない。** bash から任意長の追記を原子的にする移植可能な方法が無い。1イベント1ファイルを
  tmp + rename で置く。

### 4. 発話の順序を壊さない

`chatter-agent-speak` は hook から毎 delta 起動されるが、**ロックを取れた1プロセスだけが spool を
処理する。** `seq` の採番もこのロック下で行う。ドレイン完了後、ロックを解放する前にもう一度
spool を見る（走査直後に到着した分の取りこぼし防止）。

**到着順（`birthtime`）だけでは発話順は決まらない。** `MessageDisplay` と `PreToolUse` は別プロセスと
して同時に走るので、**prompt が本文を追い越して spool に着くことがある。** `worker.ts` の手当ては2段:

1. **引き上げ**（`hoistMessagesBeforePrompt`）— 同一セッション・同一 `prompt_id` の本文を prompt の前へ移す
2. **本文待ち**（`PROMPT_BODY_WAIT_POLLS`）— 発話される prompt に本文が伴っていなければ、
   **`processPrompt` の直前で**最大 **3秒**待ってパスをやり直す

★ **1 だけでは足りない。** 短い本文は改行で終わらないので `final` flush まで spool にファイルが
1つも置かれず、引き上げる対象が存在しない。**待ちの秒数を縮めないこと。**

### 5. 記録と配信を1つのファイルに兼ねさせない

`speech.jsonl`（記録）と `speech/<seq>.json`（配信キュー）は別物。1つに兼ねさせると、ローテートを
跨ぐ差分読み取りが要り、読み手だけが際限なく複雑になる。**取りこぼしと二重配信を実際に両方踏んだ。**

契約は [`docs/protocol.md`](./docs/protocol.md)。

### 6. `seq` を単独のキーにしない — 世代は `epoch` が持つ

ランタイムルートが消えると **CLI の採番は 1 に戻る。** `seq` だけを覚えている受信側は「もう喋った」と
誤判定して**何百文でも一切喋らなくなる**（エラーも出ない）。`SpeechRecord.epoch` が採番のやり直しと
一対一に対応する。

- **採番のやり直しの後始末は、ロックを持っている書き手（CLI）が行う。** `epochIsNew` なら最初の
  publish で、**`append` より前に**キューを空にする（`cli/publish.ts`）
- **サーバーは配信しないだけで、世代違いの entry を削除しない**（どちらが新しいかを決められない）
- **配信済みの記憶は `seq` で持つ。** サーバーは毎 poll **キューの先頭を1件だけ読んで**世代を確かめる
- **ack にも `epoch` を載せる。** ただし **`epoch: null` は「省略」と同じ扱いにすること**
- **アップグレードで epoch を変えない。** 読めないときは `"legacy"` を採る

### 7. 音声はサーバーが押し出さず、クライアントが取りに行く

合成は `GET /audio/<epoch>-<seq>.wav` が来たときに走る。**テキストの配信は音声と独立していて、
エンジンが落ちていても止まらない。** 逆（合成が終わってからフレームを配る形）にしないこと。

- ★ **`503`（あとで取りに来い）を「失敗」に数えないこと。** 数えると、エンジンを起動し忘れている
  だけで溜まっていた発話が全部 ack されて消える
- ★ **合成のエラーを `404` に落とさないこと。** `404` は ack まで通って**キューの本文を物理削除する**。
  **無音の原因は 404 ではなく診断で出す**（`server/index.ts` の `recheckEngine`）
- ★ **応答の期限と合成の期限を混ぜないこと。** `GET` の**応答**は `synthesisTimeoutMs` で打ち切って
  `503` を返すが、**合成は走らせたままにする**
- ★ **`prompt` を配信順で追い越させないこと。** 配信順を変えると 4 の逆転が再発する

## 規約

**コメント** —— コード修正による陳腐化を避ける。**経緯・実測値・チケットの要件を残さない。**
確定事項だけを、なぜやっているのか / 最終的にどうなるかの抽象で残す（具体的な処理はコードを追えば分かる）。

**実測値を仕様として扱わない。** 秒数・CPU 使用率・テスト件数はマシンとネットワークで変わる。
**実測した数字を書くのは `docs/knowledge/` の中だけにする**（測定条件を添えて）。
**既定値・定数・契約上の閾値は実測値ではない**ので、これに当たらない（`docs/` の設定表はそのまま）。
テストの実数が要るときは `./scripts/test.sh` の `total=` を見る。

**ライセンスヘッダ** —— cc-mascot 由来のファイルを改変したら `Modified for chatter-agent.` を入れる。
**「cc-mascot のツリーにあった」＝「cc-mascot の著作物」ではない**（`prompt/` と `summarizer/` は
自分の著作物）。判定手順は [`docs/origin.md`](./docs/origin.md)。cc-mascot 由来のコードを増減させたら
`NOTICE` が実態と合っているか確認する。

**`plugin/bin/chatter-agent-speak.mjs` はコミットする成果物。** `core/src/` を直したら
`npm run build` してコミットする（CI の `bundle` ジョブが一致を検証する）。

**`ChatterMascot.Runtime` を「描画に依存しない層」のまま保つ。** 契約・状態機械・探索順・画角の計算が
EditMode だけでテストできているのは、この層が描画に依存していないから。

## lint とテスト

**Node は 24.11 以上**（ルートの `mise.toml` で `24.19.0` に固定）。

```bash
cd core
npm run typecheck && npm run lint && npm run format && npm run test:run
npm run build            # CLI → plugin/bin/、server と player → dist/

npm run verify:phase-a   # hook → 記録 + 配信キュー
npm run verify:phase-b   # 配信キュー → WebSocket（実サーバーを起動する）
npm run verify:tts       # 合成と GET /audio/（エンジン不要。CI で回る）
npm run verify:player    # WebSocket → 音声取得 → 再生 → ack（エンジンも音も不要。CI で回る）
```

```bash
cd apps/chatter-mascot
./scripts/test.sh        # EditMode テスト
```

### タスク完了時のチェックリスト

- [ ] **テスト追加の検討** —— 変更した箇所に関連するテストが必要か考える
- [ ] **ライセンスヘッダの確認** —— cc-mascot 由来のファイルを改変したら `Modified for chatter-agent.`
- [ ] **ドキュメント更新の検討** —— `docs/` / `docs/knowledge/` / `README.md` に追記するものがないか検討し、あればユーザーに提案する
- [ ] `npm run typecheck` / `npm run lint` / `npm run format` / `npm run test:run` が通ること
- [ ] `src/cli/` を触ったら `npm run build` してバンドルもコミットする

## ドキュメント索引

| 文書 | 読むとき |
|---|---|
| [`docs/protocol.md`](./docs/protocol.md) | **発話の契約。** `SpeechRecord`、配信キュー、WebSocket と ack、制御 API。クライアントを書くときはここだけで足りる |
| [`docs/core.md`](./docs/core.md) | `core/` を触るとき。区画の分け方、tsconfig の制約、バンドル方針、ランタイムのファイル配置 |
| [`docs/plugin.md`](./docs/plugin.md) | `plugin/` を触るとき。bash hook の制約、spool 命名、`hooks.json` の3種 |
| [`docs/mascot.md`](./docs/mascot.md) | `apps/chatter-mascot/` を触るとき。セットアップ、構成、探索順、ビルドと実行 |
| [`docs/origin.md`](./docs/origin.md) | cc-mascot 由来のコードを触るとき。移植の対応表、フォーク点、ライセンス義務 |
| [`docs/knowledge/`](./docs/knowledge) | **踏んだこと・なぜそうしたか・実測値。** 同じ罠に2度目で刺されないため |
| `_workspace/chatter-agent-design.md` | 着手前の検討記録（git 管理外）。**基本設計の正は `docs/` 側** |

## ライセンス

Apache-2.0。cc-mascot（Apache-2.0, Copyright 2026 kazakago）の派生物。テキスト整形
（`text/textFilter.ts`）と感情判定（`emotion/ruleBasedEmotionClassifier.ts`）は cc-mascot から
**初回に一度だけ移植**し、以後はこのリポジトリのコードとして改変する。**上流に追従する義務は
負わないが、帰属表示と改変の告知は Apache-2.0 の義務**として維持する。
