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
| `core/` | `chatter-agent-core`（CLI + WebSocket サーバー + 発話 CLI） | **実装済み。** `summarizer/`（AI要約、既定OFF）も含めて完了（[#31](https://github.com/schwarz9791/chatter-agent/issues/31)） |
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
`speech.jsonl` に載るまでの配管は**約 50ms** で十分速い。**発話は `final:true` を待って
メッセージ単位で出す**ので（[#30](https://github.com/schwarz9791/chatter-agent/issues/30)）、
体感を決めるのは配管の速さではなく `final` の到着タイミングになる。実測では**複数文のメッセージ 179件のうち
97.8%**（175件）で `final` はほぼ即座に届き（中央値・p90 とも 0秒）、数十秒待つのは `AskUserQuestion` の
直前だけ（→ 下の「実測で潰れた前提」/「絶対に守ること」1）。

実測で潰れた前提は [`docs/plugin.md`](./docs/plugin.md) に集約してある。要点だけ:

- `/plugin install` はプラグインを**完全コピー**する。`bin/` も実行権限ごと入るので、バンドル同梱の前提は成立。
  ただし**ローカルディレクトリを marketplace に登録している場合、hook が走るのはコピーではなく登録元**
  （`known_marketplaces.json` の `installLocation`）。実機確認でバンドルを差し替えるときに刺さる（→ [`docs/plugin.md`](./docs/plugin.md)）
- **thinking でもサブエージェントでも発火しない**（読み上げ事故は起きない）
- **メッセージの最終行だけは final flush でしか来ない。** これが `final` を待つ設計の遅延の下限で、`AskUserQuestion` の直前では数十秒に達する

**Phase B は完了している。** `npm run verify:phase-b` で実サーバーを起動した確認に加え、
`npm run verify:player` が **hook → CLI → server → player** を通して音が鳴るところまで見ている。
Unity 側（#12）は同じ契約を踏むので、player が「正しい挙動」の突き合わせ先になる。

**実機（AivisSpeech + afplay）でも音が出るところまで確認した。** 耳で聞いた限りの体感:

- **1文目だけは合成待ちで少し間が空く**（先読みが効くのは2文目以降なので構造的にそうなる）
- **ターンがそのまま終わるなら、メッセージは表示とほぼ同時に喋り出す。** `final` が即座に来るため。
  遅れが問題になるのは手前でツールを呼んだときで、`AskUserQuestion` の直前が最悪（→「絶対に守ること」1）
- **`**` などの記号は音にならない。** 合成エンジンが `audio_query` で読み仮名に変換する時点で落とすため。
  [#2](https://github.com/schwarz9791/chatter-agent/issues/2) の実害は「記号が読まれる」ことではなく、
  **文が変な所で割れて不自然な切れ目が入る**こと

> ★ **上の体感は、粒度を変える前（文単位で流していた頃）に耳で確かめたもの。**
> メッセージ単位（[#30](https://github.com/schwarz9791/chatter-agent/issues/30)）での実機確認は**未実施**。
> 特に「`AskUserQuestion` の直前でどれだけ沈黙するか」は測って [`docs/plugin.md`](./docs/plugin.md) に記録すること。

**AI要約（`summarizer/`）も実装済みになった**（[#31](https://github.com/schwarz9791/chatter-agent/issues/31)）。
**既定 OFF。** 有効にすると、長いメッセージ1件ごとに `claude -p` が走る。要約は AI の生成そのものなので、
所要時間には**ばらつきが大きい**（入力が長いから出力が遅い、とはなりづらい）。`final` の待ちが中央値0秒
（→「絶対に守ること」1）なのに対し、要約 ON ではこの秒数が丸ごと発話の遅れとして乗る。
**要約 ON での実機確認も行った**（Claude Code 2.1.233 / macOS、n=10。詳細は [`docs/plugin.md`](./docs/plugin.md)）。
無限ループ防止の第1層は10件とも効いていた。**原文の長さと所要時間は相関しない**——217文字がタイムアウトし、
745文字が11.0秒で完走する、という逆転が実測に出ている（タイムアウト率 3/10）。要約がいちばん効くはずの
長い発言ほど要約が間に合わずタイムアウトし、原文がそのまま読み上げられるという逆転は起こりうるが、
その原因は「長いから遅い」ではなく「いつタイムアウトするか事前に予測できない」こと。
秒数はマシンとネットワークで変わるので**仕様として扱わないこと**。この実測を受けて
`aiSummaryTimeoutMs` の既定を**30秒→60秒**に上げ、`summaryPipeline.ts` に要約の長さ上限も入れた。

> ★ **耳での体感も確認できた。遅延は許容だった。** 理由は「ずっとターミナル側を見ているわけではない」——
> **むしろターミナルから目を外しておきたい状態で、音声だけで状況を把握するために喋らせている**という、
> この機能の用途そのものに関わる報告だった。#30 で受け入れた「表示と発話のズレがメッセージ全体に乗る」
> という代償（→「絶対に守ること」1）は、この使い方を前提にすれば問題として立ち上がらない。
>
> ★ **ただし、いつタイムアウトするか予測できないという前提だと、この代償の重さが変わる。** 目を離して
> 聞いている状況では、要約待ちの末に**いちばん長い発言が要約されないまま全文**読み上げられるのが、
> いちばん避けたい失敗の仕方になる——実機実測でも実際に踏んだ（`/code-review max` の3571文字の出力が
> タイムアウトし、原文が全文読み上げられた）。`aiSummaryThreshold` を上げる対策は、相関が無い以上
> 効かないと分かった。採った対策は `aiSummaryTimeoutMs` の60秒化と要約の長さ上限の導入。
> タイムアウトの実挙動そのものも実機で確認できた（原文へのフォールバックが機能した）。
> 詳細と残りの未検証事項は [`docs/plugin.md`](./docs/plugin.md)。

## データフロー

```
Claude Code
  │ hooks: MessageDisplay / PreToolUse(AskUserQuestion|ExitPlanMode) / Notification(permission_prompt)
  ▼
plugin/scripts/*.sh          bash。payload を spool/<message_id>.<index>.json に置くだけ。即 exit 0
  │ 毎 delta で CLI をデタッチ起動
  ▼
chatter-agent-speak (CLI)    ロックを取れた1プロセスだけが spool を順に処理
  │                          **final:true を待つ**（非 final では何もせず終わる）
  │                          delta 結合 → Markdown除去 → 文分割 → 要約（既定OFF） → 感情判定 → epoch/seq 採番
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

### 1. `final:true` を待つ — 発話はメッセージ単位

1つの `message_id` は `index` 0..N で分割送信され `final:true` が終端になる。

**`final` が来るまで1文も出さない。** 来たらメッセージ全文をまとめて1回で流す。
→ [`docs/protocol.md`](./docs/protocol.md)（契約）/ `core/src/cli/worker.ts` の `processMessage`

> ★ **これは [#30](https://github.com/schwarz9791/chatter-agent/issues/30) で反転した方針。**
> 設計書 §2-4 と、それ以前のこの節は「`final:true` を待ってはいけない」と書いていた。
> 反転の理由は3つ、いずれも実測に基づく:
>
> 1. **AI要約（[#31](https://github.com/schwarz9791/chatter-agent/issues/31)）が原理的に成立しない。** 要約はメッセージ全体が揃って初めて意味を成すが、1文は平均 34.6 文字しかなく閾値に届かない
> 2. **サーバー合成（[#29](https://github.com/schwarz9791/chatter-agent/issues/29)）の前提が粒度で決まる。** 合成リクエストの 60秒窓ピークが 37 → 5 req/min（7倍差）
> 3. **代償が想定より小さい。** `final` の待ち時間は中央値 0秒 / p90 0秒。数十秒待つのは 179件中 4件（`AskUserQuestion` の直前）だけ

**引き換えに失うもの**（受け入れ済み）:

- 表示と発話のズレが**メッセージ全体**に乗る（以前は最終行の1文だけだった）
- `index` に欠番があると「部分発話」ではなく**全損**になる（→ [#21](https://github.com/schwarz9791/chatter-agent/issues/21)）
- `publish` が throw したときに組み直されるのが1文ではなく**メッセージ全文**になる（→ [#13](https://github.com/schwarz9791/chatter-agent/issues/13)）

**`final` が来ないメッセージは救済する。** ESC 中断・クラッシュ・`index` 欠番でメッセージが閉じないことはある。
**同一セッションの**後続イベントが到着したら、そこで打ち切って全文を出し spool を消す（`hasNewerInSameSession`）。
セッションを限定しないと、Claude Code を2枚開いただけで**まだ伸びる途中のメッセージが分断される**。

> ★ **後続イベントが来なければ、救済は発火しない。** その場合は発話されないまま、
> `spoolMaxAgeHours`（既定6時間）を過ぎたところで孤児掃除（`cleanOrphans`）に破棄される。
> ESC 中断・クラッシュは「その後そのセッションで何もしない」のが普通の展開なので、
> **ここで名指ししている2ケースこそ、救済条件（同一セッションの後続イベント）が
> 成立しないことが多い**。救済はメッセージの生存性を保証する仕組みではなく、
> たまたま同じセッションで後続の活動があったときにだけ拾える偶然の検出だと理解すること。

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

**到着順（`birthtime`）だけでは発話順は決まらない。** `MessageDisplay` と `PreToolUse` は
別プロセスとして同時に走るので、**prompt が本文を追い越して spool に着くことがある**
（実機で `PreToolUse` − `final` = −316ms）。そのまま到着順に処理すると「質問を読み上げてから、
その質問に至る説明を読み上げる」逆転になる（[#33](https://github.com/schwarz9791/chatter-agent/issues/33)）。
`worker.ts` の手当ては**2段**:

1. **引き上げ**（`hoistMessagesBeforePrompt`）— 同一セッション・同一 `prompt_id` の本文を prompt の前へ移す
2. **本文待ち**（`PROMPT_BODY_WAIT_POLLS`）— 発話される prompt に本文が伴っていなければ、
   **`processPrompt` の直前で**最大 **3秒**待ってパスをやり直す（予算はドレイン全体の残ポール数）

★ **1 だけでは足りない。** 短い本文（1〜2文）は改行で終わらないので `final` flush まで
spool にファイルが1つも置かれず（メッセージ全体が単一 delta で届く）、**引き上げる対象が
存在しない**。実機で 2 を入れるまで逆転が残った（2026-08-23、276ms）。
**待ちの秒数を縮めないこと。** 500ms では足りず（実測 550ms）逆転が再現した。
`final` の到着時刻はばらつくので**秒数を仕様として扱わないこと**。
→ [`docs/plugin.md`](./docs/plugin.md) / `npm run verify:phase-a` の ⑱⑲

### 5. 記録と配信を1つのファイルに兼ねさせない

`speech.jsonl`（記録）と `speech/<seq>.json`（配信キュー）は別物。1つに兼ねさせると、ローテートを跨ぐ差分読み取りが要り、読み手だけが際限なく複雑になる。**取りこぼしと二重配信を実際に両方踏んだ。**

分ければ順序はファイル名で決まり、消費は削除で表せる。`?since=` も要らない（接続直後に未 ack 分が流れる）。
→ 経緯は [#8](https://github.com/schwarz9791/chatter-agent/issues/8)、契約は [`docs/protocol.md`](./docs/protocol.md)

### 6. `seq` を単独のキーにしない — 世代は `epoch` が持つ

ランタイムルート（または `speech.state.json` と `speech.jsonl` の両方）が消えると
**CLI の採番は 1 に戻る**。`seq` だけを覚えている受信側は、そこで「もう喋った」と誤判定して
**何百文でも一切喋らなくなる**（エラーも出ない）。

`SpeechRecord.epoch` が**採番のやり直しと一対一**に対応する（[#29](https://github.com/schwarz9791/chatter-agent/issues/29)）。
以前はこれをクライアント側の推論（「seq が戻ったのに ts は進んだ」）に任せていて、
PR #28 のレビューが**クライアント側5件のバグの根本原因**と名指しした。

- **採番のやり直しの後始末は、ロックを持っている書き手（CLI）が行う。** `epochIsNew` なら
  最初の publish で、**`append` より前に**キューを空にする（`cli/publish.ts`）。
  `append` の後ろに置くと、その隙間で kill されたときに state だけが新しい epoch で
  永続化され、**以後どのプロセスも掃除しなくなる**
- **サーバー側では「どちらの世代が新しいか」を決められない。** やり直し直後のキューは
  `1(新) 2(新) … 400(旧)` になり、ファイル名の昇順では**新しい世代が先頭に来る**。
  判定できるのは `ts` だけで、そこも時計の巻き戻しで逆転しうる。だから
  **サーバーは配信しないだけで、世代違いの entry を削除しない**
- ★ **配信済みの記憶は `seq` で持つ。** `clear()` の直後に同じ `seq` が別世代の内容で
  書き直されると、ファイル名の集合からは何も変わって見えない。サーバーは毎 poll
  **キューの先頭を1件だけ読んで**世代を確かめる（`server/dispatcher.ts`）
- **ack にも `epoch` を載せる。** 旧世代の ack は `ackUpTo` の範囲削除で、まだ喋っていない
  新しい entry を消す。**ただし `epoch: null` は「省略」と同じ扱いにすること** —
  未設定の optional を `null` にするのは Unity / C# / Go / Python の既定
- **アップグレードで epoch を変えない。** 採番が復旧できて epoch だけ読めないときは
  `"legacy"` を採る。ここで生成すると、アップグレードした瞬間に in-flight のキューが消える

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

**「cc-mascot のツリーにあった」＝「cc-mascot の著作物」ではない。** 応答待ち通知の整形（`prompt/`）と AI要約（`summarizer/`）は、cc-mascot の作業ブランチ上で書いた**自分の著作物**で、上流の `main` には存在しない。kazakago の帰属を付けないこと。判定手順は [`docs/origin.md`](./docs/origin.md)。

cc-mascot 由来のコードを増減させたら `NOTICE` の記述が実態と合っているか確認すること。
