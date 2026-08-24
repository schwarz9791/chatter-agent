# `core/` — chatter-agent-core の開発規約

`chatter-agent-core` は `chatter-agent-speak`（CLI）、`chatter-agent-server`（WebSocket 配信）、`chatter-agent-player`（発話 CLI）を含む Node パッケージ。

ここに書いてあるのは**ツールチェーン固有の地雷**で、設計判断ではない。設計の根拠は `_workspace/chatter-agent-design.md` を見ること。

## 区画の分け方

すべて実装済み（Phase A + B。`summarizer/` を含む）。

```
core/src/
├── cli/          chatter-agent-speak（spool を読む単一ワーカー）
│   ├── index.ts             エントリ。無効化判定 → ロック → ドレイン → 解放
│   ├── spool.ts             走査（到着順）/ 分類 / 読み取り / 削除 / 孤児掃除
│   ├── messageAssembler.ts  delta の結合だけを担う薄い adapter（純粋関数）。整形の本体は `text/speechText.ts`
│   ├── publish.ts           記録と配信キューの両方に書く合成。append できた時点で「出した」が確定する
│   ├── worker.ts            ドレインループ。応答待ち通知の整形もここ
│   └── workerState.ts       プロセスを跨いで持ち回る重複抑制の状態
├── server/       chatter-agent-server（配信キュー → WebSocket 配信 + 音声の HTTP 配布）
│   ├── index.ts             合成ルート。ロック → bind → 古いキューの掃除 → ポーリング
│   ├── dispatcher.ts        配信済み seq と**採番の世代**の判断。フレームの組み立てもここ（ユニットテストのため純粋な部品に切り出してある）
│   ├── audioStore.ts        ★合成のキャッシュと single-flight。ディスクを持たない（issue #29）
│   ├── engineProcess.ts     ★合成エンジンを起こす条件の判断と、プロセスグループごとの停止（issue #51）
│   ├── httpServer.ts        `GET /audio/<epoch>-<seq>.wav`。200 / 503 / 404 / 403 の切り分け
│   ├── wsServer.ts          配信と ack。Origin 検査、外部 http.Server への相乗りもここ
│   └── throttledWarn.ts     同じ警告を間引く（503 の連発と Origin 拒否。黙らせずに件数を出す）
├── tts/          音声合成エンジンのクライアント（issue #29 で player/ から移設）
│   └── voicevoxClient.ts    AivisSpeech / VOICEVOX 互換 API（fetch + AbortSignal.timeout）
├── player/       chatter-agent-player（WebSocket → 音声取得 → 再生 → ack）
│   ├── index.ts             合成ルート。ロック → 一時dir → 接続。コマンドを実行してイベントを戻すドライバ
│   ├── playbackQueue.ts     ★中核。取得/再生/ack の判断だけを持つ reducer（副作用ゼロ）
│   ├── speechFrame.ts       受信フレームの検証。`wsServer.parseAck` と対称
│   ├── audioFetcher.ts      `GET /audio/…`。結果を ready / unavailable / gone / failed の4値に分ける
│   ├── audioPlayer.ts       WAV を一時ファイルに置いて外部コマンドで鳴らす
│   └── client.ts            ws 接続 / 再接続 / ping watchdog / ack の間引き
├── core/         契約と基盤
│   ├── types.ts             SpeechRecord / SpeechFrame / SpeechEpoch / LEGACY_EPOCH / Emotion / SpeechKind / SpeakMessage
│   ├── audioPath.ts         `/audio/<epoch>-<seq>.wav` の組み立てと検証。server と player が共有する
│   ├── paths.ts             ← cc-mascot-xr 流用
│   ├── config.ts            ← cc-mascot-xr configStore 流用
│   ├── lock.ts              mkdir の原子性を使った単一ワーカー / 単一サーバーのロック
│   ├── atomicWrite.ts       tmp + rename の共通化。キュー entry / seq state / worker state の3箇所が使う
│   ├── commandPath.ts       外部コマンドの絶対パス探索（spawn しない）。要約 CLI と合成エンジンが共有する（issue #51 で summarizer/ から移設）
│   ├── speechLog.ts         記録への追記 / epoch と seq の採番 / state 整合
│   └── speechQueue.ts       配信キュー。list/read/enqueue/ackUpTo/dropOlderThan/trim/clear/sweepTmp
├── text/         テキスト整形・文分割        ← textFilter.ts のみ cc-mascot 由来
│   ├── speechText.ts        ★中核。メッセージ全文の整形 → 文の切り出し（`toSpeechSentences`。純粋関数）。
│   │                         `cli/` からも `summarizer/` からも参照するため、`summarizer/ → cli/` の
│   │                         逆依存を作らないよう `cli/messageAssembler.ts` から移設した（issue #38）
│   └── speakable.ts         合成に出す意味のあるテキストか。**合成する側が持つ判定**（issue #29 で player/ から移設）
├── emotion/      ルールベース感情判定        ← cc-mascot 由来
├── prompt/       応答待ち通知の整形
└── summarizer/   AI要約（既定OFF。issue #31）
    ├── types.ts             Summarize / SummaryOutcome / ClaudeCliResult の型定義
    ├── prompt.ts            要約 CLI に渡す指示文（SUMMARY_INSTRUCTION）
    ├── claudeCli.ts         引数組み立て / execFileSync での同期実行（コマンド解決は `core/commandPath.ts`）
    └── summaryPipeline.ts   判定とフォールバック（createSummaryPipeline）。cli/worker.ts から呼ばれる
```

判断ロジックは基本的に `cli/` と `core/` に置く。**`server/index.ts` と `wsServer.ts` は判断ロジックを持たない** — 配線に留める。

判断は**3つの部品に切り出してある**。`index.ts` に埋めるとユニットテストから触れないため。

| | |
|---|---|
| `server/dispatcher.ts` | 何を配信済みとし、何を消してよいか。どの世代の entry を配信し、どの ack を弾くか |
| `server/audioStore.ts` | 何を合成し、何を覚えておくか（issue #29） |
| `server/engineProcess.ts` | 合成エンジンを起こしてよいか、どう止めるか（issue #51） |

> ★ **`audioStore.ts` は #29 で増えた2つ目の例外。** 「サーバーは判断ロジックを持たない」という
> 方針そのものは変えていない。合成を GET が来たときに走らせる形にしたので、
> **サーバーが持つ判断は「同じキーの同時要求をまとめる」ことと「上限で捨てる」ことだけ**に
> なっている。いつ合成するか・どこまで先読みするかはクライアント側（`playbackQueue.ts` の
> 先読み窓）が決めていて、サーバーには投機的な合成が無い。

`player/` も同じ形だが、切り出し方を一段厳しくしてある。**`playbackQueue.ts` はイベントを入れるとコマンドの配列が返る reducer で、合成も再生も ack も自分では行わない。** dispatcher の副作用は同期の `broadcast` 1本なので注入で足りるが、player の副作用は非同期で、しかも完了コールバックが状態機械に**再入する**（cc-mascot の `useSpeech.ts` が promise の中から `processQueue()` を呼ぶ形）。注入した関数を機械の内側から呼ぶと、「ループの途中で状態が変わる」再入バグをテストで捕まえられない。

発話の契約（`SpeechRecord`、キューの形、WebSocket）は [`protocol.md`](./protocol.md) にある。

cc-mascot 由来のファイルも**このリポジトリのコードとして自由に改変してよい**。隔離区画ではない。詳細は [`origin.md`](./origin.md)。

## 触るときの注意

### 1. cc-mascot 由来のファイルにはライセンスヘッダが要る

Apache-2.0 §4(b) は、**改変したファイルにその旨の目立つ告知を付ける**ことを要求している。由来のあるファイルを触ったら、ヘッダに `Modified for chatter-agent.` があるか確認すること。

**対象は `text/textFilter.ts` と `emotion/ruleBasedEmotionClassifier.ts`（+ 両者のテスト）の4ファイルだけ。** 同じディレクトリに並んでいる `text/unstableTail.ts` と `prompt/` 配下は cc-mascot 由来ではないので、ヘッダを足さないこと。区別の根拠は [`origin.md`](./origin.md)。

### 2. `tsconfig.json` の `moduleResolution: "bundler"` を変えない

cc-mascot 由来のコードは相対 import が拡張子なし（`from "./filters/textFilter"`）。`nodenext` にすると該当ファイルが軒並み `TS2835` になる。**最も起きやすい事故。**

拡張子を全部書き足せば `nodenext` にもできるが、**どのみちバンドルするので変える利点が無い**。触らないこと。

### 3. `tsconfig.json` の `erasableSyntaxOnly` を外さない

esbuild 系のツールは型を消すだけなので、enum / parameter properties / namespace があると壊れる。このフラグが「バンドラで必ず動く」ことの静的保証になっている。

外すと **`tsc` は通るのに実行時に壊れる**、という気づきにくい状態になる。

### 4. `tsx` で実行しない。バンドルする

**ここが前身 cc-mascot-xr からの変更点。** cc-mascot-xr のブリッジは常駐プロセス1本だったので `tsx` で直接実行していたが、chatter-agent の CLI は **hook から毎 delta 呼ばれる**。`tsx` の起動コスト（~300ms）は乗せられない。

tsdown 等でバンドルし、成果物を `plugin/bin/chatter-agent-speak.mjs` に出す。

### 5. バンドル成果物を git にコミットする

`/plugin install` するとプラグインは複製されるため、`${CLAUDE_PLUGIN_ROOT}` から `core/dist` が見える保証がない。そこで**バンドル済み CLI を `plugin/bin/chatter-agent-speak.mjs` としてコミットし、hook script はそれを直接呼ぶ**。解決順の分岐を作らない。

- ビルド成果物を git に入れるのは本意ではないが、`/plugin install` だけで完結する導入体験と引き換える
- **CI で「コミット済みバンドルがソースと一致するか」を検証**して腐敗を防ぐ
- 開発時にソースから直接動かせるよう、`CHATTER_AGENT_CLI` 環境変数による上書きだけ残す。**それ以外の解決経路を足さない**

## 前身から流用するもの

手元の `/Users/schwarz/dev/cc-mascot-xr` に動作確認済みのコードがある（非公開・開発中断）。**そのまま使えるものは書き直さないこと。**

| 流用元（`cc-mascot-xr/bridge/`） | 移送先 | 状態 |
|---|---|---|
| `src/server/wsServer.ts` + `.test.ts` | `core/src/server/` | **ほぼそのまま（流用済み）。** ping-pong によるデッドコネクション検出、`bufferedAmount` によるバックプレッシャ、graceful close 込み。ack の受信と Origin 検査を足した |
| `src/config/paths.ts` + `.test.ts` | `core/src/core/paths.ts` | **流用済み。** 環境オブジェクトを引数に取る純関数群。ディレクトリ名と解決先を変えた |
| `src/config/configStore.ts` + `.test.ts` | `core/src/core/config.ts` | **流用済み。** 環境変数 > JSON > 既定値。mtime + size が変わったときだけ再読込。壊れた JSON では直前値を維持 |
| `.github/workflows/validate.yml` | リポジトリルート | **適用済み。** `working-directory` 指定済み。chatter-agent では format / bundle の2ジョブを追加した |
| `LICENSE` / `NOTICE` | リポジトリルート | **適用済み。** Apache-2.0 + 派生物としての帰属表示 |

これらは自分の著作物なので、cc-mascot 由来ファイルのようなライセンスヘッダも `NOTICE` への帰属表示も不要。**ソース内にも由来コメントは書かない**（非公開リポジトリなので、読んだ人が辿れない参照になる）。流用の経緯はこの表だけに残す。

`wsServer.ts` の実装には全て理由がある（`listening` まで resolve しない / タイマーを `unref()` する /
socket に error ハンドラを必ず付ける / `boundAddress` を保持する）。**コメントごと残してあるので消さないこと。**
テストも方針を引き継いでいて、**`ws` をモックせず `127.0.0.1` の `port: 0` に実ポートを開く。**
モックすると「想像した ws の API」しか検証できない。

## テスト

- 純粋関数（文の切り出し、seq 採番、spool の走査順）を優先してテストする。ファイル I/O と hook 起動が絡む部分は結合テストで見る
- cc-mascot から移植したテストも**このリポジトリのテストとして育てる**。挙動を変えたらテストも直す
- テストは `*.test.ts` としてソースと**同ディレクトリに並置**する（`vitest.config.ts` の `include`）
- 時刻とプロセスIDは注入する（`now` / `pid`）。`vi.useFakeTimers` に頼らず、ロックの stale 判定や
  応答待ちの抑制窓を素直に書けるようにしてある

## 開発コマンド

```bash
cd core
npm install
npm run typecheck     # tsc --noEmit
npm run lint          # oxlint
npm run format        # oxfmt
npm run test:run      # vitest run
npm run test:coverage
npm run build         # tsdown（CLI / server / player の3エントリ）
```

### Node のバージョン

**24.11 以上が要る。** tsdown の依存（`rolldown-plugin-dts`）が `^22.18.0 || >=24.11.0` を要求するため、
`~/.npmrc` に `engine-strict=true` を置いている環境では、これを下回ると `npm install` の時点で弾かれる。

リポジトリルートの `mise.toml` で `node = "24.19.0"` に固定してある。mise を使っていれば
ディレクトリに入った時点で切り替わる。`package.json` の `engines.node` も `>=24.11` にしてあるので、
別の経路で古い Node を掴んでいても install で気づける。

CI（`setup-node`）は `node-version: "24"` で、常に最新の 24 系が入るのでこの条件を満たす。

### 受け入れ確認

いずれも使い捨ての `XDG_CONFIG_HOME` を掘るので、実際の `~/.config/chatter-agent` は汚さない。

```bash
npm run build
npm run verify:phase-a   # spool → speech.jsonl（scripts/verify-phase-a.sh。実際の bash hook に食わせる）
npm run verify:phase-b   # 配信キュー → WebSocket（scripts/verify-phase-b.mjs）
npm run verify:tts       # server の合成と GET /audio/（scripts/verify-tts.mjs）
npm run verify:player    # WebSocket → 音声取得 → 再生 → ack（scripts/verify-player.mjs）
npm run start:server     # 手で動かすとき。**エンジンが居なければサーバーが起こす**（#51）
npm run start:player     # 耳で聞くとき
```

★ **`start:*` は `dist/` を実行するだけでビルドしない。** `dist` は `.gitignore` 済みなので、
ソースを直したら `npm run build` してから起動すること。**`[Player]` というプレフィックスで
エンジンのエラーが出たら、それは #29 より前の古いビルド**（読み手が player → server に移る前のもの）。

**`verify:tts` も `verify:player` も AivisSpeech もオーディオデバイスも要らない。** 合成エンジンは
スタブ HTTP に、再生コマンドは `scripts/fake-player.mjs` に差し替わる。**スタブに疎通できる＝
条件3が成立するので、エンジンを起こすこともない**（#51）。`verify:tts` の⑭がそれを検査していて、
**この2本には `CHATTER_AGENT_TTS_SPAWN=0` を入れていない** —— 入れると条件3が壊れても素通りし、
開発機では代わりに本物の AivisSpeech が黙って起動してしまう。
逆に **`verify:phase-b` だけは `CHATTER_AGENT_TTS_SPAWN=0` を渡す**。あちらは TTS 系の env を
一切渡さないので、既定の `127.0.0.1:10101` に繋ぎに行って失敗し、そこで本物を起こしてしまう
（`CHATTER_AGENT_TTS_ENABLED=false` では逃げられない。音声の相対パスを検査しているため）。**プレイヤーコマンドを
config で差し替えられるようにした決定が、そのまま CI 可能性になっている**（`playerCommand` /
`playerArgs`）。偽プレイヤーが受け取ったファイル名を追記するので、**実際に何がどの順で鳴ったか**まで
検証できる。`verify:player` の最後のシナリオでは本物の server と CLI を通して、
hook → CLI → server → player の全経路を1本で見る。

**2つを分けてあるのは、落ちたときに原因を切り分けるため。** `verify:tts` は player を挟まず、
本物の server に直接 `GET /audio/…` を投げる。合わせて見ると「合成が1回だったのはサーバーが
まとめたからか、クライアントの先読み窓が小さかっただけか」が分かる。
`verify:player` 側は逆に、スタブのサーバーが返す 200 / 503 / 404 に対してクライアントが
どう振る舞うかだけを見る。

3本（`phase-b` / `tts` / `player`）は `scripts/lib/harness.mjs` を共有する。入っているのは
`check` / `show` / `until`・使い捨てルート・スタブ用の WAV・「子プロセスを起動してこの行が
出るまで待つ」まで。**判定とスタブはここに置かないこと** — 落ちたときに「スタブの挙動」と
「本物の挙動」のどちらを疑うかが増える。

**実機での確認（ターミナル表示と体感で同時か）は耳で行う。** 自動の検証が見ているのは形と順序だけ。

**CI の `verify` ジョブでも回している**（`.github/workflows/validate.yml`）。いずれも
バンドル（`plugin/bin/` と `core/dist/`）を実行するので `npm run build` が先に要る。
手元でも同じコマンドで回せる。

### バンドル

| エントリ | 出力 | 依存 |
|---|---|---|
| `src/cli/index.ts` | `plugin/bin/chatter-agent-speak.mjs`（**git にコミット**） | 全部バンドル。npm 依存ゼロ |
| `src/server/index.ts` | `core/dist/chatter-agent-server.mjs`（gitignore） | `ws` は external |
| `src/player/index.ts` | `core/dist/chatter-agent-player.mjs`（gitignore） | `ws` は external |

- ★ **`dist` に出すエントリのうち `clean: true` を持てるのは1つだけ。** 両方が true だと、
  実行順によって先に出た方の成果物が消える。今は server 側が持っている

- **拡張子は `.mjs` でなければならない。** `plugin/bin/` に `package.json` を置かないので、`.js` だと Node が CJS として読んで壊れる
- **CLI に npm 依存を持たせない。** `src/cli/` から到達する範囲は Node 標準だけで閉じる。ビルド後に
  `grep '^import' plugin/bin/chatter-agent-speak.mjs` を見ると確認できる。`summarizer/` を取り込んでから
  `fs` / `os` / `path` に加えて `crypto`（`randomUUID`）と `child_process`（`execFileSync`）が増えたが、
  いずれも Node 標準モジュールで npm 依存ではない
- 出力は決定的なので、CI の `bundle` ジョブが `npm run build` 後の `git diff --exit-code` で腐敗を検出する

### ツールチェーン

lint と format は **oxlint / oxfmt**（Oxc）。eslint / prettier は使わない。バンドラの tsdown も Rolldown/Oxc なので、ツールチェーンが揃っている。

| ツール | 設定ファイル |
|---|---|
| `oxlint` | `.oxlintrc.json` — `correctness` カテゴリを error、plugins は typescript / unicorn / oxc |
| `oxfmt` | `.oxfmtrc.json` — `printWidth: 120` |
| `tsc` | `tsconfig.json` — 型検査のみ（`noEmit`） |
| `vitest` | `vitest.config.ts` |

**eslint / typescript-eslint に戻さない。** `typescript-eslint@8` の peer は `>=4.8.4 <6.1.0` で、これが TypeScript のバージョンを縛る。oxlint は自前パーサなので TS のバージョンに追従を強いられない（実際、これを外したことで TS 7 に上げられた）。

**型情報ありの lint は入れていない。** 必要になったら `oxlint-tsgolint` を足す（typescript-eslint の 61 ルール中 59 に対応）。CLI はロックと非同期 I/O を扱うので、`no-floating-promises` が欲しくなったらそのタイミング。

### 移植コードのフォーマットについて

cc-mascot から移植したコードを oxfmt で整形すると、上流との差分が読みにくくなる。特に `emotion/ruleBasedEmotionClassifier.ts` は、上流の辞書改善を手で取り込む余地を残してある（→ [`origin.md`](./origin.md)）。

整形から外したい場合は `.oxfmtrc.json` の `ignorePatterns` に足す。**oxfmt は指定が無ければ `.gitignore` と `.prettierignore` も読む**が、このリポジトリに `.prettierignore` は置いていない。

## ランタイムのファイル配置

すべて1つのルートの直下に置く。**plugin の bash hook が同じ spool パスを自力で組み立てる**ため、
`${XDG_CONFIG_HOME:-$HOME/.config}/chatter-agent`（win32 は `%APPDATA%/chatter-agent`）という
**bash で一行で書ける規則から外れないこと。** 条件分岐を足すと bash 側と Node 側が静かにズレる。

| | パス | 書く人 |
|---|---|---|
| 設定 | `{root}/config.json` | 人間 |
| spool | `{root}/spool/` | hook が書き、CLI が消す |
| 発話の記録 | `{root}/speech.jsonl`（退避は `speech.1.jsonl` の1世代だけ） | CLI |
| 配信キュー | `{root}/speech/<seq>.json` | CLI が書く。上限超過は CLI が切り、ack と起動時の掃除は server が行う |
| seq の state | `{root}/speech.state.json`（`{nextSeq, epoch}`） | CLI |

| 抑制の state | `{root}/speak.state.json` | CLI |
| 要約 CLI の cwd | `{root}/summarizer-home/` | CLI（要約 CLI を隔離実行する作業ディレクトリ。プロジェクトの `CLAUDE.md` を読ませないため） |
| 要約の実測ログ | `{root}/summarizer.log` | CLI（要約が有効なときだけ書く。既定 OFF なら1バイトも増えない） |
| CLI のロック | `{root}/speak.lock/`（ディレクトリ） | CLI |
| サーバーのロック | `{root}/server.lock/`（ディレクトリ） | **server**（bind の前に取る。2台目は起動に失敗する） |
| player のロック | `{root}/player.lock/`（ディレクトリ） | **player**（接続の前に取る。2台目は起動に失敗する） |
| player の一時 WAV | `{root}/player-tmp/<エポック>-<seq>.wav` | **player**（起動時にディレクトリごと作り直す。`seq` は採番の世代を跨いで一意でないので、ファイル名に世代を混ぜる） |

### 採番の世代（`epoch`）

`seq` は**この世代の中でしか一意でない**。`speech.state.json` と `speech.jsonl` の両方が
消えると採番は 1 に戻り、そのとき `epoch` も新しくなる（[#29](https://github.com/schwarz9791/chatter-agent/issues/29)）。
契約は [`protocol.md`](./protocol.md)。実装で守ること:

| | |
|---|---|
| `LEGACY_EPOCH` | **`core/types.ts` に1箇所だけ置く。** 記録側（`speechLog.reconcile`）とキュー側（`speechQueue.read`）が別々の値を使うと、「ログ由来の legacy」と「キュー由来の legacy」が別世代として扱われる |
| 生成 | `globalThis.crypto.randomUUID()` を**分岐の中で**触る。`crypto` を top-level import しないこと（[#43](https://github.com/schwarz9791/chatter-agent/issues/43): CLI の起動が毎 delta 約2.6ms 重くなる）。**推測しやすい値へフォールバックしない** — `epoch` は音声の URL に載り、`<audio src>` は Origin 検査を素通りする |
| 掃除 | やり直しの後始末は**書き手（CLI）**が、最初の publish の `append` より**前**に行う。サーバーは配信しないだけで削除しない（→ `server/dispatcher.ts` の `resolveGeneration`） |
| 検証 | `epoch` を通す入口は全部 `isValidEpoch` を通す（`parseAck` / `parseSpeechFrame` / `readState` / `readLastEntry` / `speechQueue.read`）。**欠落だけ**を `LEGACY_EPOCH` に倒すのは `speechQueue.read` の1箇所 |
| `ts` | 世代の新しさの判定に使う。**字句比較にしないこと**（`ts: "z"` ひとつでその世代が永久に勝つ）。`speechQueue.read` が `Date.parse` で弾き、比較も数値で行う |

### 設定と環境変数

環境変数 > `config.json` > 既定値。キーを増やすときは `ChatterAgentConfig` と `SPECS` の両方を直す
（`satisfies` で網羅を型に担保させてあるので、片方だけだとコンパイルが通らない）。

| キー | 既定値 | 環境変数 |
|---|---|---|
| `port` | `8570` | `CHATTER_AGENT_PORT` |
| `host` | `"0.0.0.0"` | `CHATTER_AGENT_HOST` |
| `speakPrompts` | `true` | `CHATTER_AGENT_SPEAK_PROMPTS` |
| `speechLogMaxBytes` | `5242880` | `CHATTER_AGENT_SPEECH_LOG_MAX_BYTES` |
| `speechQueueMaxEntries` | `500` | `CHATTER_AGENT_SPEECH_QUEUE_MAX_ENTRIES` |
| `spoolMaxAgeHours` | `6` | `CHATTER_AGENT_SPOOL_MAX_AGE_HOURS` |
| `allowedOrigins` | `[]` | `CHATTER_AGENT_ALLOWED_ORIGINS`（カンマ区切り） |

server（音声合成）だけが読むキー。**別ファイルに分けないこと。** `SPECS` は全バイナリで共有していて、
載っていないキーは未知キーとして警告されるので、分けると `chatter-agent-speak` が
毎 delta の起動ごとに警告を吐く。

| キー | 既定値 | 環境変数 |
|---|---|---|
| `ttsEnabled` | `true` | `CHATTER_AGENT_TTS_ENABLED` |
| `ttsBaseUrl` | `"http://127.0.0.1:10101"` | `CHATTER_AGENT_TTS_URL` |
| `ttsSpeakerId` | `888753760` | `CHATTER_AGENT_TTS_SPEAKER_ID` |
| `synthesisTimeoutMs` | `30000` | `CHATTER_AGENT_SYNTHESIS_TIMEOUT_MS` |
| `ttsSpawn` | `true` | `CHATTER_AGENT_TTS_SPAWN` |
| `ttsSpawnCommand` | `""` | `CHATTER_AGENT_TTS_SPAWN_COMMAND` |
| `ttsSpawnArgs` | `[]` | `CHATTER_AGENT_TTS_SPAWN_ARGS` |

★ **#29 で読み手が player → server に移ったが、キー名も意味も変えていない。** 改名すると、
既存の `config.json` に残った旧キーが**全バイナリで**未知キー警告を出す（#11 で
`speechLogGenerations` を廃止したときに実際に踏んだ）。

- 既定の `ttsBaseUrl` は **AivisSpeech の標準ポート**。cc-mascot は
  エンジンを自分で `--port 8564` で spawn するので、そちらに繋ぐなら明示的に指定する
- `ttsSpeakerId` の既定は AivisSpeech 標準同梱の Anneli（ノーマル）。起動時に `/speakers` で
  存在を検査し、無ければ候補を並べて警告する（**設定ミスの症状が「無音」なので、これが無いと切り分けできない**）。
  ★ **ここで起動を止めないこと。** 止めるとテキストの配信まで巻き添えになり、クライアントからは
  「数十秒の無音は正常」と区別できなくなる。音声だけを 503 に落として、原因を症状に出す
- `ttsEnabled: false` にすると配信フレームの `audio` が常に `null` になり、`GET /audio/…` も 404 を返す。
  **テキストの配信は止まらない**ので、自前で合成するクライアントや字幕だけのクライアントの逃げ道になる
- ★ **`synthesisTimeoutMs` は2つの場所に効く。** エンジンへの**1リクエストあたり**の上限
  （`audio_query` と `synthesis` に別々に。2往復で1つの予算にすると、モデルロードで
  `/audio_query` が食い切ったとき CPU 律速の `/synthesis` に残り0が渡る）と、
  `GET /audio/…` の**応答**を保留する上限。後者は応答を打ち切るだけで**合成は続ける**ので、
  クライアントの取り直しがキャッシュに当たって即 200 になる
- モジュール名は API ファミリ（`voicevoxClient`）、config キーはエンジン中立（`tts*`）で割り切ってある

#### エンジンを起こす（`ttsSpawn*`、[#51](https://github.com/schwarz9791/chatter-agent/issues/51)）

**エンジンが居なければサーバーが起こす。** GUI（AivisSpeech.app）を終了すると GUI が spawn した
エンジンも道連れで落ちる（PID の親子を実測）ため、「GUI を閉じてエンジンだけ残す」運用は成立しない。
このエンジンは chatter-agent 以外に使わないので、**サーバーが落ちたらエンジンも一緒に落とす**。

★ **起こすだけで、起動を待たない。** `[Server] Ready` は先に出て、合成は今までどおり
`GET /audio/…` が来たときに走る。#29 の柱（エンジンが落ちてもテキストの配信は止まらない）は
変わらない（→ `CLAUDE.md`「絶対に守ること」7）。

起こすのは**次の5つを全部満たすときだけ**:

1. `ttsEnabled` が `true`
2. `ttsSpawn` が `true`
3. **起動時の疎通確認に失敗した**
4. `ttsBaseUrl` のホストがループバック（`127.0.0.0/8` / `[::1]` / `localhost`）
5. コマンドが解決できた

★ **条件3が要。** 「まず繋いでみて、居なければ起こす」形にしたことで、GUI 併用・verify のスタブ・
別ポート運用のすべてが**追加の分岐なしで**素通りする（ポート衝突を判定するコードが要らない）。
判定は `listSpeakers` に**繋がったか**だけで、`ttsSpeakerId` が実在するかは混ぜない
——混ぜると、スタブが生きているのに話者 ID だけ間違えている状態で**二重起動**する。

- `ttsSpawnCommand` が空なら、AivisSpeech.app の既知の場所を順に見る:
  `/Applications/AivisSpeech.app/Contents/Resources/AivisSpeech-Engine/run` →
  `~/Applications/…` の同じパス。**指定した値が見つからないとき、既知候補にフォールバックしない**
  （指定を黙って別のバイナリに読み替えるのは、最も気づきにくい失敗の仕方になる）
  - ★ **非絶対パスを指定したときは `PATH` だけを見る。** 要約 CLI 側が使っている
    「既知のインストール先」（mise / asdf の shim、`~/.local/bin` など）は**探さない**。
    エンジンのバイナリ名が literally `run` なので、あの並びを探すと無関係な `run` を掴む
  - 見つからないときは**探した場所をフルパスで**ログに並べる（PATH を直すのか・ファイル名を
    直すのか・実行ビットを立てるのかが症状から分かるように）
- ★ **起こせるのは平文（`http:`）のループバックだけ。** `ttsBaseUrl` に `https:` を書くと
  `not-http` で起こさない（受理すると `--port 443` で平文サーバーが立つ）
- `ttsSpawnArgs` が空なら `ttsBaseUrl` から `--host <host> --port <port>` を組む。
  **指定すると導出は行われない**（追加ではなく置換）。自分で書くなら `--host` / `--port` も自分で書く
- **落ちても再起動しない**（初版）。起動失敗ループの方が害が大きい。異常終了なら `[Engine]` が
  終了コードと **出力の末尾**（stdout と stderr）を出す ——これが無いと「起動したはずなのに
  繋がらない」の原因が1文字も残らない
  - ★ **落ちた後に「定期診断」が走るわけではない。** `recheckEngine` は合成が失敗したとき
    （`onSynthesisFailed`）にしか呼ばれないので、**クライアントが音声を取りに来なければ
    一度も走らない**。`ENGINE_RECHECK_INTERVAL_MS` は周期実行ではなく「呼ばれたときの間引き」
- ★ **起こすかどうかの判断は起動時の1回きりで、実行中に再評価しない。** 起動時に
  `ttsEnabled: false` だった／後から AivisSpeech をインストールした、はサーバーを再起動すれば
  反映される。**動的にやり直す方が危険側に倒れる** ——「誰が起こしたエンジンなのか」の追跡が
  難しくなり、GUI が上げた共有物を落とす事故に近づく
- 停止は**プロセスグループごと**（`detached: true` で起こし、`process.kill(-pid, …)`）。
  `run` は PyInstaller のバイナリで**自分の子を持つ**ので、`child.kill()` では孫が残ってポートを掴み続ける
- 終了処理は `SIGINT` / `SIGTERM` / `SIGHUP` で走る。**`SIGHUP` を拾うのは、端末のウィンドウを
  閉じるのが日常操作だから**（`detached` で起こしたエンジンに端末の SIGHUP は届かない）
- ★ **次の場合はエンジンが残る**（終了処理が走らないため）: サーバーが `SIGKILL` された /
  **2回目の Ctrl-C**（即座に落とす経路）/ 終了処理が6秒を超えて watchdog に落とされた。
  これは実害が無い ——次回起動時の条件3が残ったエンジンに疎通し、起こさずに再利用する
- ★ **ログのプレフィックスは `[Engine]`。** エンジンのプロセスそのものに関する行だけがこれで、
  起こす / 起こさないの判断は `[Server]` が出す

**実機で確認した**（macOS / AivisSpeech 1.1.0-dev、2026-08-24）:

- GUI を終了し `lsof -nP -iTCP:10101 -sTCP:LISTEN` が空の状態から、`npm run start:server` と
  `npm run start:player` だけで**音が出た**。エンジンの親は `node dist/chatter-agent-server.mjs`
  （GUI ではない）
- サーバーを止めると `lsof` が空に戻る（プロセスグループごとの停止が効いている）
- **GUI が上げている状態ではサーバーは起こさない**（条件3）。ログには
  `音声合成エンジンに繋がりました` だけが出て `[Engine]` の行は出ない
- 起こしてから `/speakers` が応答するまでは**数秒**かかる。★ **この秒数を仕様として扱わないこと** ——
  マシンとモデルで変わる。その間の `GET /audio/…` は `503` になり、クライアントが取り直す

★ **話者を増やすときは GUI が要る。** エンジン単体だと、音声モデルの追加は API を叩くか
`~/Library/Application Support/AivisSpeech-Engine/Models/` に `.aivmx` を直接置くことになる。
モデル自体は GUI から独立しているので、**一度入れた話者はエンジン単体でもそのまま使える**。

player だけが読むキー。**これも別ファイルに分けない**（理由は上と同じ）。

| キー | 既定値 | 環境変数 |
|---|---|---|
| `synthesisLookahead` | `3`（0 で直列） | `CHATTER_AGENT_SYNTHESIS_LOOKAHEAD` |
| `audioFetchTimeoutMs` | `45000` | `CHATTER_AGENT_AUDIO_FETCH_TIMEOUT_MS` |
| `playerCommand` | `"afplay"` | `CHATTER_AGENT_PLAYER_COMMAND` |
| `playerArgs` | `["{file}"]` | `CHATTER_AGENT_PLAYER_ARGS`（カンマ区切り） |
| `playerServerUrl` | `""`（空なら `host`/`port` から導出） | `CHATTER_AGENT_PLAYER_SERVER_URL` |
| `speechMaxAgeMs` | `0`（無効） | `CHATTER_AGENT_SPEECH_MAX_AGE_MS` |

- ★ **`synthesisLookahead` の意味は #29 でも変わっていない。** サーバーは投機的な先読みを
  持たず、GET が来たときに合成するので、**この窓がそのまま合成の需要信号になる**
- ★ **`audioFetchTimeoutMs` とサーバー側の `synthesisTimeoutMs` の順序は気にしなくてよい。**
  以前は「長くすること」という暗黙の制約があり、破ると 503（待てば直る）で来るはずの状態が
  転送エラー（試行回数を消費する＝発話が捨てられる）に化けた。しかも `synthesize` は2往復なので
  **最悪は `synthesisTimeoutMs` の2倍**で、既定の45秒でも足りなかった。いまはサーバーが
  `GET` の応答を自分で打ち切って 503 を返すので、この制約そのものが無い
- ★ **`playerServerUrl` が `host` と別なのは、既定の `0.0.0.0` が bind アドレスであって接続先ではないから。**
  空のときは `0.0.0.0` / `::` を `127.0.0.1` に読み替えて組み立てる。音声の取得元も
  この URL の authority から導く（サーバーは自分の到達アドレスを知らない → `core/audioPath.ts`）

`chatter-agent-speak`（`summarizer/` の AI要約）だけが読むキー。**これも別ファイルに分けないこと。**
理由は player のキーと同じだが、**警告を吐く側が逆になる**: これは `chatter-agent-speak` だけが読むキーなので、
別ファイルに分けると今度は server と player が起動のたびに未知キー警告を吐く。

| キー | 既定値 | 環境変数 |
|---|---|---|
| `aiSummaryEnabled` | `false` | `CHATTER_AGENT_AI_SUMMARY_ENABLED` |
| `aiSummaryThreshold` | `200` | `CHATTER_AGENT_AI_SUMMARY_THRESHOLD` |
| `aiSummaryCommand` | `"claude"` | `CHATTER_AGENT_AI_SUMMARY_COMMAND` |
| `aiSummaryModel` | `"haiku"`（空文字なら `--model` を渡さない） | `CHATTER_AGENT_AI_SUMMARY_MODEL` |
| `aiSummaryTimeoutMs` | `60000` | `CHATTER_AGENT_AI_SUMMARY_TIMEOUT_MS` |
| `aiSummaryMaxPerDrain` | `3`（上限8。`parseAiSummaryMaxPerDrain`） | `CHATTER_AGENT_AI_SUMMARY_MAX_PER_DRAIN` |

- `aiSummaryEnabled` は**既定 OFF**。有効にすると `aiSummaryThreshold` を超えたメッセージのたびに `claude -p`
  が走り、ユーザーの課金を消費する。**代償は遅延の方が大きい**: 要約は AI の生成なので、所要時間は
  **入力の長さから予測できない**（実機実測10件で相関が見られず、短い入力がタイムアウトし長い入力が
  10秒台で返ることもあった。詳細は下記と [`plugin.md`](./plugin.md)）。`final` の待ちが中央値0秒
  （→ `CLAUDE.md`）なのに対し、これは丸ごと発話の遅れとして乗る。秒数は環境で変わるので仕様として扱わないこと
- `aiSummaryTimeoutMs` の既定は**60秒**。実機実測10件（`summarizer.log`）では入力の長さと所要時間が
  相関せず、旧既定の30秒では10件中3件（30%）がタイムアウトしていた。**「実測値の N 倍」という決め方は
  していない**——相関しないものに倍率を掛けても意味が無いため。60秒は「旧既定30秒ではタイムアウトが
  3割起きた」という実測だけを根拠にした値で、秒数自体を仕様として扱わないことは変わらない
- `aiSummaryMaxPerDrain` は移植元の「滞留ガード」（同時実行数の待ち行列が閾値を超えたらスキップ）の読み替え。
  同期実行では待ち行列の概念が無いので、「1回のドレインで要約してよい回数の上限」に置き換えてある。
  上限8が入った（`parseAiSummaryMaxPerDrain`）。1回のドレインは最悪 `aiSummaryMaxPerDrain × aiSummaryTimeoutMs`
  の間ロックを保持しうるため（**既定なら 3 × 60秒 = 180秒。上限まで上げ、かつタイムアウトが既定のままなら
  8 × 60秒 = 480秒**。`aiSummaryTimeoutMs` 自体には実質的な上限が無い——`parseTimeoutMs` は `MAX_TIMER_MS`
  ≒ 24.8日でしか縛らないため、480秒は強制された天井ではない）、また `workerState.ts` の
  `SUMMARIZER_SESSION_LIMIT`（64）は「64 ÷ 8 = 8ドレイン分の要約セッションIDを覚えられる」計算になっている
  ため、上限だけを単独で動かさないこと

**`speechLogGenerations`（記録の退避世代数）は [#8](https://github.com/schwarz9791/chatter-agent/issues/8) で廃止した。** 誰も `speech.jsonl` を tail しなくなったので、
複数世代を繰り下げる必要がなくなり、`speechLogMaxBytes` を超えたら `speech.1.jsonl` に退避する1世代だけになった。
既存の `~/.config/chatter-agent/config.json` に `speechLogGenerations` が残っていると、`config.ts` の未知キー警告
（`[Config] ... の未知のキー "speechLogGenerations" は無視されます`）が CLI 起動のたびに出る。手で消すこと。
それ以前の環境で作られた `speech.2.jsonl` 以降のファイルも、以後は誰も読み書きしない孤児になる。消してよい。

config に載せない環境変数:

- `CHATTER_AGENT_CONFIG` — `config.json` の場所そのもの
- `CHATTER_AGENT_DISABLE` — hook と CLI を無効化（無限ループ防止の第1層）
- `CHATTER_AGENT_CLI` — 開発時にバンドルを差し替える
- `CHATTER_AGENT_HOOK_DEBUG` — hook が受けた payload を `{root}/hook-debug.log` に落とす（hook 側だけ。→ [`plugin.md`](./plugin.md)）

`CHATTER_AGENT_DISABLE` は `config.ts` の `isSpeakDisabled()` が判定する。**`1/true/yes/on` で無効、
それ以外（未設定・空・`0/false/no/off`・未知の値）は有効**で、`plugin/scripts/_lib.sh` の
`chatter_disabled` と同じトークン集合。以前は presence 判定（空文字以外はすべて truthy）だったため、
`CHATTER_AGENT_DISABLE=0` を「無効化の解除」のつもりで書くと逆に全発話が黙って止まっていた（[#4](https://github.com/schwarz9791/chatter-agent/issues/4)）。

## 実装で設計書から動いたこと

設計書（`_workspace/chatter-agent-design.md`）は一次情報だが、実装中に上書きした点がある。

1. **配信は `speech.jsonl` の tail ではなくディレクトリキュー。** 設計書 §4-4 / §6 は差分読み取りと
   ローテート追従を前提にしていたが、1つのファイルに記録と配信を兼ねさせると読み手だけが際限なく
   複雑になる（取りこぼしと二重配信を両方踏んだ）。分けた経緯は [#8](https://github.com/schwarz9791/chatter-agent/issues/8)、
   結果の契約は [`protocol.md`](./protocol.md)
2. **spool の処理順は mtime ではなく birthtime。** 当初は `<message_id>.jsonl` に delta ごと
   追記していたため、mtime が動き続けて `final:true` が大きく遅れて届くと先行メッセージの mtime
   が後発より新しくなり、順序が入れ替わっていた。**その後、spool の書き込みは追記から
   `<message_id>.<index>.json`（1 delta 1 ファイル、tmp + rename）へ変えた**（bash から任意長の
   追記を原子的にする移植可能な方法が無いため。→ [`plugin.md`](./plugin.md)）が、mtime を避ける
   理由は変わらない。1メッセージが複数ファイルに分かれた分、到着順は「グループの中で最新の
   ファイル」ではなく**必ず `index` が 0 のファイルの birthtime**で決める（→ `spool.ts` の
   `arrivalOrderOfMessage`）
3. **★ 発話の粒度はメッセージ単位。`final:true` を待つ。** 設計書 §2-4「★最重要★ `final:true` を
   待ってはいけない」を [#30](https://github.com/schwarz9791/chatter-agent/issues/30) で**反転させた**。
   理由（AI要約が成立しない / サーバー合成の req/min が7倍違う / `final` の待ち時間は実測で
   中央値 0秒）は `CLAUDE.md`「絶対に守ること」1、契約は [`protocol.md`](./protocol.md)「発話の粒度」
4. **未確定なのはフェンスだけではなかった。** 設計書 §4-2 は「未閉じの ``` 以降は保留」としていたが、
   `cleanTextForSpeech` が領域ごと削除する構文は他にもある。ただし **#30 で保留の必要そのものが
   消えた**ので、いま残っているのは「読み上げたくない」フェンスと表の行だけ
   （→ 下の「消えた課題: ストリーミング中の保留」/ `src/text/unstableTail.ts`）

加えて、設計書に無い挙動を足した。

- **`final` が来なかったメッセージを、後続イベントの到着で救済する。** ESC 中断・クラッシュ・
  `index` 欠番でメッセージが閉じないことはある。後続イベントが来た時点でそのメッセージはもう伸びないので、
  そこで打ち切って全文を出し spool を消す。ただし**同一セッションの**後続に限る。spool はグローバルに
  1ディレクトリで、`MessageDisplay` は matcher 非対応で全セッションで発火するため、限定しないと
  Claude Code を2枚開いただけで**まだ伸びる途中のメッセージが分断される**
- **ack をフロー制御として入れた。** TTS は生成よりずっと遅いので、キューは実質バッファ。
  「起動時に空にする」「上限で古い方から捨てる」も同じ原則（古い発話は無価値）で説明がつく
- **AI要約（`summarizer/`、issue #31）を `assembleSentences` の外に置いた。** `cli/messageAssembler.ts` の
  `assembleSentences` は純粋関数として保ちたいので、要約は `cli/worker.ts` の `summarizeSentences` として
  `processMessage` 側に置き、文分割の結果を受けてから `deps.publish` の手前で呼ぶ。実行方式も移植元
  （非同期 spawn + セマフォによる同時実行数の制限）から `execFileSync` の同期実行に変えた。呼び出し元の
  `drainSpool` は完全に同期で、単一ワーカーのロックが直列化を担っているため、同時実行の制御自体が不要になった

### 消えた課題: ストリーミング中の保留

[#30](https://github.com/schwarz9791/chatter-agent/issues/30) で `final:true` を待つようになるまで、
CLI は delta が届くたびに全文を組み直し、**最後の文を保留して**それ以外を流していた。進捗は
「出力済みの文数」（`emitted`）だけをディスクに持ち、その前提は**既に出した範囲が後から変化しないこと**
だった。`unstableTail.ts` が未閉じの `<` / インラインコード / URL / 16進列まで切り落としていたのは、
`cleanTextForSpeech` がそれらを閉じた瞬間に**既に発話した文ごと**削除・変形するからで、
「一度喋ってから取り消す」事故を防ぐためだった。

**この設計ごと無くなった。** メッセージ全文が揃ってから1回だけ組み立てるので、既出範囲という概念が無い。
`emitted` / 進捗サイドカー / 「伸びると不安定になる」構文の保留は全部消えた。残っているのは
「**そもそも読み上げたくない**」もの（未閉じのフェンス、書きかけの表の行）だけで、これは `final` でも切る。

同じ着想（行境界で早期に確定させる `endsAtLineBoundary` など）をまた持ち込まないこと。
**前提が消えたのであって、当時の反証が無効になったわけではない** —
`safe` の末尾が `\n` であることと、`safe` の内部に未閉じ構文が残っていないことは別の話で、
delta 単位の早期確定を復活させるなら同じ回帰をもう一度踏む。

→ [#24](https://github.com/schwarz9791/chatter-agent/issues/24)（保留を外す条件の精緻化）はこれで無効になった。

## 既知の欠落

移植した `cleanTextForSpeech` が扱えていない記法がある。上流にもこれを保持する意図のテストは無く、
単なる未対応。

| 症状 | 例 |
|---|---|
| **URL 直後の `。` 以降が段落ごと消える** | `参考は https://x。まず読みます。次に実装します。` → `["参考は"]` |
| **`<` と `>` に挟まれた文が丸ごと消える** | `1 < 2 なので先に進みます。確認しました。3 > 2 です。` → `1  2 です。` |
| commit hash の正規表現が数値・英単語を食う | `5242880` / `1048576` / `defaced` が消える |
| 強調・リンク記法が残る | `**` / `*` / `__` / `~~`、`[text](` の残骸 |
| 約物だけの発話が `seq` を消費する | `すごい！！` → `["すごい！", "！"]` |

上2つが重く、**文が無言で消える**。どちらも「区切りを決め打ちした正規表現が、次の区切りまで走る」
という同じ形（URL は次の空白まで、タグは次の `>` まで）。残りは「記号が読み上げに混ざる」に留まる。

> `final` を待って1回だけ組み立てるので、**一度発話してから取り消す**事故は起きない
> （組み立て時にはもう `>` が来ているか、永久に来ないかのどちらかに決まっている）。
> `>` があると間の文が失われること自体は残っている。

**実機で頻度を見てから整形規則をまとめて見直す方針**にしたので、現状は
`src/cli/messageAssembler.test.ts` の「既知の欠落」ブロックで挙動を固定して可視化してある。
直すときはリンクを段8（URL除去）より**前**に処理する必要がある。

→ [#2 テキスト整形規則を見直す](https://github.com/schwarz9791/chatter-agent/issues/2)

## 後回しにしている課題

| | |
|---|---|
| [#2](https://github.com/schwarz9791/chatter-agent/issues/2) | テキスト整形規則の見直し（上記）。**実機で強調記号を踏んだ** — `**強調。**` が `**強調。` と `** 続き` に割れて読み上げられる |
| [#3](https://github.com/schwarz9791/chatter-agent/issues/3) | WebSocket の**認証**。Origin 検査は入ったが、LAN 上の他端末は素通り |
| [#5](https://github.com/schwarz9791/chatter-agent/issues/5) | Linux で `birthtimeNs` が当てにならない（spool の命名で解く）。**macOS だけを対象にしている間は実害なし** |
| [#7](https://github.com/schwarz9791/chatter-agent/issues/7) | `cleanOrphans` の追加走査 |

> [#6](https://github.com/schwarz9791/chatter-agent/issues/6)（`messageAssembler` の O(N²) 再パース）は
> [#30](https://github.com/schwarz9791/chatter-agent/issues/30) で解消した。整形と文分割はメッセージあたり
> 1回しか走らない。**ただし `readMessage` は毎 delta で全 delta ファイルを読み直す**ので、
> ファイル読み取りの二乗性は残っている（480 delta で実測 474ms だった正規表現のコストとは別物で、
> 現状は問題になっていない）。
