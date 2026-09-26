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
│   ├── index.ts             合成ルート。ロック → トークン確保 → bind → 古いキューの掃除 → ポーリング
│   ├── dispatcher.ts        配信済み seq と**採番の世代**の判断。フレームの組み立てもここ（ユニットテストのため純粋な部品に切り出してある）
│   ├── audioStore.ts        ★合成のキャッシュと single-flight。ディスクを持たない（issue #29）
│   ├── engineProcess.ts     ★合成エンジンを起こす条件の判断と、プロセスグループごとの停止（issue #51）
│   ├── assetCatalog.ts      配布する VRM / VRMA のカタログ（固定名優先→Ordinal 先頭、sha256 のキャッシュ。issue #117）
│   ├── httpServer.ts        ルーティング（`/audio/…` と `/v1/*`）。認証の関所（issue #98）と、書き込み口の3重の絞り（issue #76）
│   ├── controlApi.ts        ★設定パネルの制御 API（`/v1/*`）。**HTTP を知らない層**（issue #76）
│   ├── auth.ts              非ループバックからの `Authorization: Bearer` を検証する（純粋関数。issue #98）
│   ├── lanToken.ts          共有トークンの読み書き（`{root}/server.token`。無ければ生成。issue #98）
│   ├── loopback.ts          peer がループバックか（純粋関数）。`auth.ts` の免除判定と、書き込み口を絞るのに使う（issue #76 / #98）
│   ├── wsServer.ts          配信と ack。トークン認証（issue #98）→ Origin 検査、外部 http.Server への相乗りもここ
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
│   ├── assetPath.ts         `/v1/assets/<path>` の3形の検証（issue #117）
│   ├── paths.ts             ← cc-mascot-xr 流用。`getServerTokenPath` は `server.token`（issue #98）
│   ├── config.ts            ← cc-mascot-xr configStore 流用
│   ├── configPatch.ts       ★`PATCH /v1/config` の検証と書き戻しの組み立て（純粋関数。issue #76）
│   ├── version.ts           バンドルに焼き込むバージョン。`package.json` との一致はテストが固定する
│   ├── summarizerSessions.ts サーバーが起こした要約の session_id（無限ループ防止の第2層。issue #76）
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
    ├── claudeCli.ts         引数組み立て / 実行（同期版と**非同期版**。コマンド解決は `core/commandPath.ts`）
    ├── summaryPipeline.ts   判定とフォールバック（createSummaryPipeline）。cli/worker.ts から呼ばれる
    └── summaryPreview.ts    ★テスト要約（`POST /v1/summary/preview`）。**非同期**。server から呼ばれる
```

判断ロジックは基本的に `cli/` と `core/` に置く。**`server/index.ts` と `wsServer.ts` は判断ロジックを持たない** — 配線に留める。

判断は**3つの部品に切り出してある**。`index.ts` に埋めるとユニットテストから触れないため。

| | |
|---|---|
| `server/dispatcher.ts` | 何を配信済みとし、何を消してよいか。どの世代の entry を配信し、どの ack を弾くか |
| `server/audioStore.ts` | 何を合成し、何を覚えておくか（issue #29） |
| `server/engineProcess.ts` | 合成エンジンを起こしてよいか、どう止めるか（issue #51） |
| `server/controlApi.ts` | 設定を読み書きしてよいか、プレビューを走らせてよいか（issue #76） |

> ★ **`controlApi.ts` は「HTTP を知らない層」にしてある。** `req` / `res` は `httpServer.ts` が扱い、
> こちらは「入力 → レスポンスの値」だけを返す。実サーバーを立てずにテストが書ける。
> ★ **書き込み口の絞り（ループバック限定 / `Origin` 禁止 / `Content-Type` 必須）は
> `controlApi.ts` に置かないこと。** ルーティングの手前で効かせるものなので `httpServer.ts` が持つ。
> こちらに置くと「ハンドラを1つ足したときに絞りを付け忘れる」形になる。

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

**対象は `text/textFilter.ts`、`emotion/ruleBasedEmotionClassifier.ts`（+ 前二者のテスト）、`emotion/defaultEmotionKeywords.ts` の5ファイルだけ。** 同じディレクトリに並んでいる `text/unstableTail.ts` と `prompt/` 配下は cc-mascot 由来ではないので、ヘッダを足さないこと。区別の根拠は [`origin.md`](./origin.md)。

### 2. `tsconfig.json` の `moduleResolution: "bundler"` を変えない

cc-mascot 由来のコードは相対 import が拡張子なし（`from "./filters/textFilter"`）。`nodenext` にすると該当ファイルが軒並み `TS2835` になる。**最も起きやすい事故。**

拡張子を全部書き足せば `nodenext` にもできるが、**どのみちバンドルするので変える利点が無い**。触らないこと。

### 3. `tsconfig.json` の `erasableSyntaxOnly` を外さない

esbuild 系のツールは型を消すだけなので、enum / parameter properties / namespace があると壊れる。このフラグが「バンドラで必ず動く」ことの静的保証になっている。

外すと **`tsc` は通るのに実行時に壊れる**、という気づきにくい状態になる。

### 4. `tsx` で実行しない。バンドルする

**ここが前身 cc-mascot-xr からの変更点。** cc-mascot-xr のブリッジは常駐プロセス1本だったので `tsx` で直接実行していたが、chatter-agent の CLI は **hook から毎 delta 呼ばれる**。`tsx` の起動コストは乗せられない（詳細は [`knowledge/core.md`](./knowledge/core.md)）。

tsdown 等でバンドルし、成果物を `plugin/bin/chatter-agent-speak.mjs` に出す。

### 5. バンドル成果物を git にコミットする

`/plugin install` するとプラグインは複製されるため、`${CLAUDE_PLUGIN_ROOT}` から `core/dist` が見える保証がない。そこで**バンドル済み CLI を `plugin/bin/chatter-agent-speak.mjs` としてコミットし、hook script はそれを直接呼ぶ**。解決順の分岐を作らない。

- ★ **`plugin/bin/` の中身を手で編集しないこと。** `core/` で `npm run build` して生成する
- ビルド成果物を git に入れるのは本意ではないが、`/plugin install` だけで完結する導入体験と引き換える
- **CI で「コミット済みバンドルがソースと一致するか」を検証**して腐敗を防ぐ
- 開発時にソースから直接動かせるよう、`CHATTER_AGENT_CLI` 環境変数による上書きだけ残す。**それ以外の解決経路を足さない**

### 6. 常駐プロセス（server / player）から `execFileSync` を呼ばない

**CLI（`chatter-agent-speak`）は hook から起動される単発プロセスで、ロックが直列化を担っている**ので、
`execFileSync` でイベントループを止めて構わない（要約の `summaryPipeline.ts` がそう書いてある）。
**サーバーは違う。** 止めている間は WebSocket の配信も `GET /audio/…` の応答も止まるので、
クライアント側の音声取得が転送エラーになり、試行回数を消費して発話が捨てられる
（→ [`protocol.md`](./protocol.md) の責務8）。`summaryPreview.ts` が非同期版（`runClaudeCliAsync`）を
使うのはこのため。**失敗の見分け方が同期版と非同期版で違う**ので、判定をコピーして使い回さないこと
（詳細は [`knowledge/core.md`](./knowledge/core.md)）。

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
npm run verify:assets    # マニフェスト → GET /v1/assets/ → Range で再開（scripts/verify-assets.mjs。issue #117）
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

4本（`phase-b` / `tts` / `player` / `assets`）は `scripts/lib/harness.mjs` を共有する。入っているのは
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

cc-mascot から移植したコードを oxfmt で整形すると、上流との差分が読みにくくなる。特に `emotion/ruleBasedEmotionClassifier.ts` と、辞書を切り出した `emotion/defaultEmotionKeywords.ts` は、上流の辞書改善を手で取り込む余地を残してある（→ [`origin.md`](./origin.md)）。

整形から外したい場合は `.oxfmtrc.json` の `ignorePatterns` に足す。**oxfmt は指定が無ければ `.gitignore` と `.prettierignore` も読む**が、このリポジトリに `.prettierignore` は置いていない。

## ランタイムのファイル配置

すべて1つのルートの直下に置く。**plugin の bash hook が同じ spool パスを自力で組み立てる**ため、
`${XDG_CONFIG_HOME:-$HOME/.config}/chatter-agent`（win32 は `%APPDATA%/chatter-agent`）という
**bash で一行で書ける規則から外れないこと。** 条件分岐を足すと bash 側と Node 側が静かにズレる。

| | パス | 書く人 |
|---|---|---|
| 設定 | `{root}/config.json` | 人間 |
| 感情キーワード辞書 | `{root}/emotion-keywords.json` | ★ CLI（無いときだけ既定を書き出す）→ 以後は人間 |
| spool | `{root}/spool/` | hook が書き、CLI が消す |
| 発話の記録 | `{root}/speech.jsonl`（退避は `speech.1.jsonl` の1世代だけ） | CLI |
| 配信キュー | `{root}/speech/<seq>.json` | CLI が書く。上限超過は CLI が切り、ack と起動時の掃除は server が行う |
| seq の state | `{root}/speech.state.json`（`{nextSeq, epoch}`） | CLI |
| 抑制の state | `{root}/speak.state.json` | CLI |
| 要約セッションの共有レジストリ | `{root}/summarizer-sessions.json` | **server**（`POST /v1/summary/preview` が起こした要約の `--session-id`。読むのは CLI） |
| 要約 CLI の cwd | `{root}/summarizer-home/` | CLI（要約 CLI を隔離実行する作業ディレクトリ。プロジェクトの `CLAUDE.md` を読ませないため） |
| 要約の実測ログ | `{root}/summarizer.log` | CLI（要約が有効なときだけ書く。既定 OFF なら1バイトも増えない） |
| CLI のロック | `{root}/speak.lock/`（ディレクトリ） | CLI |
| サーバーのロック | `{root}/server.lock/`（ディレクトリ） | **server**（bind の前に取る。2台目は起動に失敗する） |
| player のロック | `{root}/player.lock/`（ディレクトリ） | **player**（接続の前に取る。2台目は起動に失敗する） |
| player の一時 WAV | `{root}/player-tmp/<エポック>-<seq>.wav` | **player**（起動時にディレクトリごと作り直す。`seq` は採番の世代を跨いで一意でないので、ファイル名に世代を混ぜる） |
| VRM モデル | `{root}/models/` | 人間 / 設定パネル（書く）。server が読んでマニフェストに載せる（→ `server/assetCatalog.ts`） |
| VRMA モーション | `{root}/animations/` | 人間（書く）。server が読んでマニフェストに載せる |

★ **`emotion-keywords.json` は「書く人」が2者になる唯一のファイル。** 最初だけ CLI が既定を書き出し、
以後は人間が編集する。CLI は**ファイルが無いときだけ**書く——既にあれば絶対に上書きしない。
server / player はこのファイルを読みも書きもしない（読むのも CLI だけ）。

★★ **`summarizer-sessions.json` を `speak.state.json` に相乗りさせないこと。**
あちらは CLI が「ドレインの先頭で読み、途中と末尾で全体を書き戻す」形で使っている。
ロックの外に居るサーバーが同じファイルを read-modify-write すると、**CLI の tombstone
（`publishedMessageIds`）を巻き添えで消しうる** —— 症状は「同じメッセージを2回喋る」で、
設定パネルのテスト要約を押しただけで起きる。しかも再現条件がタイミングなので追えない。
ファイルを分けて**書き手を1人だけ**にしてある（CLI 側は `speak.state.json`、
サーバー側はこちら）。読む側（CLI の無限ループ防止・第2層）が or で見る。

★ **CLI のロックを取りに行くのも駄目。** あのロックはドレイン全体（要約中は数十秒、上限は
`aiSummaryTimeoutMs × aiSummaryMaxPerDrain`）保持される。テストボタン1つのためにそこまで待つか、
待たずに諦めるかの二択になる。

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

## 設定と環境変数

環境変数 > `config.json` > 既定値。キーを増やすときは `ChatterAgentConfig` と `SPECS` の両方を直す
（`satisfies` で網羅を型に担保させてあるので、片方だけだとコンパイルが通らない）。

| キー | 既定値 | 環境変数 |
|---|---|---|
| `port` | `8570` | `CHATTER_AGENT_PORT` |
| `host` | `"127.0.0.1"` | `CHATTER_AGENT_HOST` |
| `speakPrompts` | `true` | `CHATTER_AGENT_SPEAK_PROMPTS` |
| `speechLogMaxBytes` | `5242880` | `CHATTER_AGENT_SPEECH_LOG_MAX_BYTES` |
| `speechQueueMaxEntries` | `500` | `CHATTER_AGENT_SPEECH_QUEUE_MAX_ENTRIES` |
| `spoolMaxAgeHours` | `6` | `CHATTER_AGENT_SPOOL_MAX_AGE_HOURS` |
| `allowedOrigins` | `[]` | `CHATTER_AGENT_ALLOWED_ORIGINS`（カンマ区切り） |

★ **LAN から繋ぐ（Android など）には `host` を `0.0.0.0` へ明示的に変える必要がある。**
具体的な LAN IP で bind すると、同じ Mac の player やマスコットからの接続も非ループバックに
見える（player にはトークンが要り、マスコットは `127.0.0.1` で繋げない）ので `0.0.0.0` にする。
非ループバックからの接続には共有トークンが要る（→ [`protocol.md`](./protocol.md) の「セキュリティ」）。
このトークンは**config のキーでも環境変数でもない** —— `GET /v1/config` が設定を丸ごと返すので、
キーにすると漏れる。置き場は `{root}/server.token`（`server/lanToken.ts` が生成・管理する）1箇所に絞ってある。

★ player が別ホストのサーバーに繋ぐときだけ要るトークンは `CHATTER_AGENT_PLAYER_TOKEN`。
理由は同じ（config に置くと `GET /v1/config` の snapshot で漏れる）なので、`player/index.ts` が
`process.env` から直接読む —— `ChatterAgentConfig` のキーにはしていない。

`SPECS` は全バイナリで共有する1枚の設定表で、載っていないキーは起動のたびに未知キーとして
警告される。下の3グループはそれぞれ特定のバイナリしか読まないが、別ファイルには分けていない
——分けると、読まない側のバイナリが起動のたびにそのキーを未知キーとして警告してしまう。

### 音声合成エンジン（`tts*`。server が読む）

| キー | 既定値 | 環境変数 |
|---|---|---|
| `ttsEnabled` | `true` | `CHATTER_AGENT_TTS_ENABLED` |
| `ttsBaseUrl` | `"http://127.0.0.1:10101"` | `CHATTER_AGENT_TTS_URL` |
| `ttsSpeakerId` | `888753760` | `CHATTER_AGENT_TTS_SPEAKER_ID` |
| `ttsSpeedScale` | `1.0` | `CHATTER_AGENT_TTS_SPEED_SCALE` |
| `synthesisTimeoutMs` | `30000` | `CHATTER_AGENT_SYNTHESIS_TIMEOUT_MS` |
| `ttsSpawn` | `true` | `CHATTER_AGENT_TTS_SPAWN` |
| `ttsSpawnCommand` | `""` | `CHATTER_AGENT_TTS_SPAWN_COMMAND` |
| `ttsSpawnArgs` | `[]` | `CHATTER_AGENT_TTS_SPAWN_ARGS` |

- 既定の `ttsBaseUrl` は AivisSpeech の標準ポート。cc-mascot はエンジンを自分で `--port 8564` で
  spawn するので、そちらに繋ぐなら明示的に指定する
- `ttsSpeakerId` の既定は AivisSpeech 標準同梱の Anneli（ノーマル）。起動時に `/speakers` で
  存在を検査し、無ければ候補を並べて警告する（設定ミスの症状が「無音」なので、これが無いと
  切り分けできない）。
  ★ ここで起動を止めないこと。止めるとテキストの配信まで巻き添えになり、クライアントからは
  「数十秒の無音は正常」と区別できなくなる。音声だけを 503 に落として、原因を症状に出す
- `ttsEnabled: false` にすると配信フレームの `audio` が常に `null` になり、`GET /audio/…` も
  404 を返す。**テキストの配信は止まらない**ので、自前で合成するクライアントや字幕だけの
  クライアントの逃げ道になる。
  ★ `POST /v1/tts/preview` は 409 `tts_disabled`。ここを見ずに合成へ入ると、待ち切って
  `503 synthesis_unavailable` になり、設定パネルには「エンジンに繋がりません」と出る ——
  本当の理由は利用者自身が切ったことなので、名指しで断る
- ★ `synthesisTimeoutMs` は2つの場所に効く。エンジンへの**1リクエストあたり**の上限
  （`audio_query` と `synthesis` に別々に）と、`GET /audio/…` の**応答**を保留する上限。
  後者は応答を打ち切るだけで**合成は続ける**ので、クライアントの取り直しがキャッシュに
  当たって即 200 になる。
  ★ どちらもリクエストごとに読み直す。応答側を起動時の値で固定すると、`PATCH /v1/config`
  で変えたときに片方にしか効かない
- ★★ `ttsSpeedScale`（[#76](https://github.com/schwarz9791/chatter-agent/issues/76)）は
  `voicevoxClient` の「`AudioQuery` の中身は解釈しない」方針の唯一の例外。`audio_query` が
  返した JSON の `speedScale` **だけ**を書き換えて `synthesis` に投げる（`applySpeedScale`）。
  例外をここ1つに留めること —— `pitchScale` / `intonationScale` を同じ理屈で足していくと、
  「エンジンが返した JSON をそのまま返送する」という一番安全な形が失われる。`speedScale` を
  持たないエンジンには何もしない
- ★ 範囲 0.5〜2.0 はエンジンの受理範囲ではなく実用の範囲。VOICEVOX 互換 API はもっと広い値も
  受けるが、0.5 未満は間延びして意味を取りづらく、2.0 超は聞き取れない

★ エンジンを起こす条件と実機確認は [`knowledge/core.md`](./knowledge/core.md)「エンジンを起こす」。

### 再生（`player` が読む）

| キー | 既定値 | 環境変数 |
|---|---|---|
| `synthesisLookahead` | `3`（0 で直列） | `CHATTER_AGENT_SYNTHESIS_LOOKAHEAD` |
| `audioFetchTimeoutMs` | `45000` | `CHATTER_AGENT_AUDIO_FETCH_TIMEOUT_MS` |
| `playerCommand` | `"afplay"` | `CHATTER_AGENT_PLAYER_COMMAND` |
| `playerArgs` | `["{file}"]` | `CHATTER_AGENT_PLAYER_ARGS`（カンマ区切り） |
| `playerServerUrl` | `""`（空なら `host`/`port` から導出） | `CHATTER_AGENT_PLAYER_SERVER_URL` |
| `speechMaxAgeMs` | `0`（無効） | `CHATTER_AGENT_SPEECH_MAX_AGE_MS` |

- ★ `synthesisLookahead` はサーバーの先読みではなく、player が「先何件を先読み取得するか」の窓。
  サーバーは投機的な先読みを持たず `GET` が来たときに合成するので、この窓がそのまま合成の
  需要信号になる
- ★ `audioFetchTimeoutMs` と `synthesisTimeoutMs` の順序は気にしなくてよい。サーバーが `GET`
  の応答を自分で打ち切って `503` を返すため、両者の長さの間に暗黙の制約は無い
- ★ `playerServerUrl` が `host` と別なのは、`0.0.0.0` / `::`（LAN 公開のため明示的に指定した
  とき）が bind アドレスであって接続先ではないから。空のときはこれらを `127.0.0.1` に
  読み替えて組み立てる。音声の取得元もこの URL の authority から導く（サーバーは自分の
  到達アドレスを知らない → `core/audioPath.ts`）

★ `audioFetchTimeoutMs` の経緯は [`knowledge/core.md`](./knowledge/core.md)「設定キーと環境変数の経緯」。

### AI要約（`aiSummary*`。`chatter-agent-speak` が読む）

| キー | 既定値 | 環境変数 |
|---|---|---|
| `aiSummaryEnabled` | `true` | `CHATTER_AGENT_AI_SUMMARY_ENABLED` |
| `aiSummaryBackend` | `"fm"`（`"fm" \| "claude"`） | `CHATTER_AGENT_AI_SUMMARY_BACKEND` |
| `aiSummaryThreshold` | `200` | `CHATTER_AGENT_AI_SUMMARY_THRESHOLD` |
| `aiSummaryCommand` | `"claude"`（`aiSummaryBackend: "claude"` のときだけ見る） | `CHATTER_AGENT_AI_SUMMARY_COMMAND` |
| `aiSummaryModel` | `"haiku"`（空文字なら `--model` を渡さない。同上） | `CHATTER_AGENT_AI_SUMMARY_MODEL` |
| `aiSummaryTimeoutMs` | `60000` | `CHATTER_AGENT_AI_SUMMARY_TIMEOUT_MS` |
| `aiSummaryMaxPerDrain` | `3`（上限8。`parseAiSummaryMaxPerDrain`） | `CHATTER_AGENT_AI_SUMMARY_MAX_PER_DRAIN` |

- `aiSummaryEnabled` は既定 ON（issue #107 で反転）。有効な間、`aiSummaryThreshold` を超えた
  メッセージのたびに要約 CLI が走る。要約は AI の生成そのものなので所要時間は入力の長さから
  予測できず、その遅れは丸ごと発話の遅延として乗る（→ `CLAUDE.md`「絶対に守ること」1）
- `aiSummaryBackend: "fm"` は macOS 27 以降の Apple Foundation Models CLI を**固定名 `"fm"`**
  で解決する（`aiSummaryCommand` は見ない）。無い環境では `no-command` の経路に落ち、原文が
  そのまま読み上げられる。`"claude"` にすると従来どおり `aiSummaryCommand` / `aiSummaryModel`
  （`claude -p`）を使う
- `aiSummaryMaxPerDrain` は「1回のドレインで要約してよい回数」の上限（既定3、上限8）

★ 実測とばらつきの詳細は [`knowledge/core.md`](./knowledge/core.md)「設定キーと環境変数の経緯」。
★ バックエンドの選定根拠（fm と claude の速度・失敗の種類の比較）は
[`knowledge/core.md`](./knowledge/core.md)「要約と感情判定のバックエンド選定（issue #107）」。

### 感情判定のバックエンド（`emotionClassifier` 等。`chatter-agent-speak` が読む。`ollayaSpawn` だけは server も読む）

| キー | 既定値 | 環境変数 |
|---|---|---|
| `emotionClassifier` | `"ollaya"`（`"ollaya" \| "fm" \| "dictionary"`） | `CHATTER_AGENT_EMOTION_CLASSIFIER` |
| `ollayaBaseUrl` | `"http://127.0.0.1:11435"`（制御 API から書けない。(c) 区分） | `CHATTER_AGENT_OLLAYA_URL` |
| `ollayaModel` | `"laya:multilingual"` | `CHATTER_AGENT_OLLAYA_MODEL` |
| `ollayaSpawn` | `true` | `CHATTER_AGENT_OLLAYA_SPAWN` |
| `emotionTimeoutMs` | `10000` | `CHATTER_AGENT_EMOTION_TIMEOUT_MS` |

- `emotionClassifier` は `classify: (texts: string[]) => Emotion[]`（メッセージ単位で1回だけ
  呼ぶ契約。→ `cli/worker.ts` の `DrainDeps.classify`）を組み立てる分岐。どの方式でも
  接続不可・タイムアウト・壊れた応答は**辞書式に落ちる**（throw しない）
  - `"ollaya"`: ローカルの Jev 互換ランタイム（[ollaya.dev](https://ollaya.dev/)）へ1文ずつ
    `/v1/systemone` の score で問い合わせる。CLI は同期実行なので、`spawnSync` の子プロセス1個に
    メッセージぶんの文をまとめて渡す（→ `emotion/ollayaClassifier.ts`）
  - `"fm"`: macOS 27 以降の Apple Foundation Models CLI。メッセージ全体を1回だけ `--schema` 付きで
    判定し、同じ感情を全部の文に適用する（→ `emotion/fmClassifier.ts`）
  - `"dictionary"`: 既存のルールベース（`emotion/ruleBasedEmotionClassifier.ts`）をそのまま使う
- `ollayaBaseUrl` は `ttsBaseUrl` と同じ理由（本文の外部送信路になる）で制御 API から書けない
- `ollayaSpawn` は Ollaya が居なければ `chatter-agent-server` が起こすか（`ttsSpawn` と同じ役回り。
  → `server/engineProcess.ts` の `resolveOllayaSpawn`）。`ollaya serve` は `--host`/`--port` を
  持たないので、`OLLAYA_HOST`（`host:port` 形式の環境変数）で bind 先を渡す
- `emotionTimeoutMs` は判定1回（メッセージ単位）の上限。超えたら辞書式に落ちる。要約
  （既定60秒）と違って「無くても発話は止まらない」保険的な機能なので、短めに倒してある

★ バックエンドの選定根拠（精度・遅延の比較表）は
[`knowledge/core.md`](./knowledge/core.md)「要約と感情判定のバックエンド選定（issue #107）」。

## 感情判定は「文が感情的か」ではなく「作業で何が起きているか」で決める

`emotion` は VRM の expression に一対一で載る。判定器が答えるべきなのは、発話の字義どおりの情動ではなく
**「いま作業で何が起きていて、相棒ならどんな顔をするか」**である。辞書の設計判断・実測・踏んだ罠は
[`knowledge/core.md`](./knowledge/core.md) にまとめてある。
