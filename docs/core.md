# `core/` — chatter-agent-core の開発規約

`chatter-agent-core` は `chatter-agent-speak`（CLI）と `chatter-agent-server`（WebSocket 配信）を含む Node パッケージ。

ここに書いてあるのは**ツールチェーン固有の地雷**で、設計判断ではない。設計の根拠は `_workspace/chatter-agent-design.md` を見ること。

## 区画の分け方

`summarizer/` 以外は実装済み（Phase A + B）。

```
core/src/
├── cli/          chatter-agent-speak（spool を読む単一ワーカー）
│   ├── index.ts             エントリ。無効化判定 → ロック → ドレイン → 解放
│   ├── spool.ts             走査（到着順）/ 分類 / 読み取り / 削除 / 孤児掃除
│   ├── messageAssembler.ts  ★中核。delta 結合 → 確定した文の切り出し（純粋関数）
│   ├── publish.ts           記録と配信キューの両方に書く合成。append できた時点で「出した」が確定する
│   ├── worker.ts            ドレインループ。応答待ち通知の整形もここ
│   └── workerState.ts       プロセスを跨いで持ち回る重複抑制の状態
├── server/       chatter-agent-server（配信キュー → WebSocket 配信）
│   ├── index.ts             合成ルート。ロック → bind → 古いキューの掃除 → ポーリング
│   ├── dispatcher.ts        配信済み seq の判断。何を配信済みとし、何を消してよいか（ユニットテストのため純粋な部品に切り出してある）
│   └── wsServer.ts          配信と ack。Origin 検査もここ
├── core/         契約と基盤
│   ├── types.ts             SpeechRecord / Emotion / SpeechKind / SpeakMessage
│   ├── paths.ts             ← cc-mascot-xr 流用
│   ├── config.ts            ← cc-mascot-xr configStore 流用
│   ├── lock.ts              mkdir の原子性を使った単一ワーカー / 単一サーバーのロック
│   ├── atomicWrite.ts       tmp + rename の共通化。キュー entry / seq state / worker state / 進捗サイドカーの4箇所が使う
│   ├── speechLog.ts         記録への追記 / seq 採番 / state 整合
│   └── speechQueue.ts       配信キュー。list/read/enqueue/ackUpTo/dropOlderThan/trim/sweepTmp
├── text/         テキスト整形・文分割        ← textFilter.ts のみ cc-mascot 由来
├── emotion/      ルールベース感情判定        ← cc-mascot 由来
├── prompt/       応答待ち通知の整形
└── summarizer/   AI要約（既定OFF）           **未着手**
```

判断ロジックは基本的に `cli/` と `core/` に置く。**`server/index.ts` と `wsServer.ts` は判断ロジックを持たない** — キューを読んで全クライアントへ流すだけの配線に留める。唯一の例外が `server/dispatcher.ts`（何を配信済みとし、何を消してよいか）で、`index.ts` に埋めるとユニットテストから触れないため純粋な部品として切り出してある。

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
npm run build         # tsdown（CLI と server の2エントリ）
```

### Node のバージョン

**24.11 以上が要る。** tsdown の依存（`rolldown-plugin-dts`）が `^22.18.0 || >=24.11.0` を要求するため、
`~/.npmrc` に `engine-strict=true` を置いている環境では、これを下回ると `npm install` の時点で弾かれる。

リポジトリルートの `mise.toml` で `node = "24.19.0"` に固定してある。mise を使っていれば
ディレクトリに入った時点で切り替わる。`package.json` の `engines.node` も `>=24.11` にしてあるので、
別の経路で古い Node を掴んでいても install で気づける。

CI（`setup-node`）は `node-version: "24"` で、常に最新の 24 系が入るのでこの条件を満たす。

### 受け入れ確認

`plugin/` がまだ無いので、bash hook の代わりに spool を手で置いて確認する。どちらも使い捨ての
`XDG_CONFIG_HOME` を掘るので、実際の `~/.config/chatter-agent` は汚さない。

```bash
npm run build
npm run verify:phase-a   # spool → speech.jsonl（scripts/verify-phase-a.sh）
npm run verify:phase-b   # 配信キュー → WebSocket（scripts/verify-phase-b.mjs）
npm run start:server     # 手で動かすとき
```

**実機での確認（ターミナル表示と体感で同時か）は `plugin/` 着手時に行う。** ここまでは
「spool に置いたものが正しく育つ・正しく配信される」までしか見ていない。

**CI の `verify` ジョブでも回している**（`.github/workflows/validate.yml`）。どちらも
バンドル（`plugin/bin/` と `core/dist/`）を実行するので `npm run build` が先に要る。
手元でも同じコマンドで回せる。

### バンドル

| エントリ | 出力 | 依存 |
|---|---|---|
| `src/cli/index.ts` | `plugin/bin/chatter-agent-speak.mjs`（**git にコミット**） | 全部バンドル。npm 依存ゼロ |
| `src/server/index.ts` | `core/dist/chatter-agent-server.mjs`（gitignore） | `ws` は external |

- **拡張子は `.mjs` でなければならない。** `plugin/bin/` に `package.json` を置かないので、`.js` だと Node が CJS として読んで壊れる
- **CLI に npm 依存を持たせない。** `src/cli/` から到達する範囲は Node 標準だけで閉じる。ビルド後に
  `grep '^import' plugin/bin/chatter-agent-speak.mjs` が `fs` / `os` / `path` だけであることを確認できる
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
| seq の state | `{root}/speech.state.json` | CLI |
| 抑制の state | `{root}/speak.state.json` | CLI |
| CLI のロック | `{root}/speak.lock/`（ディレクトリ） | CLI |
| サーバーのロック | `{root}/server.lock/`（ディレクトリ） | **server**（bind の前に取る。2台目は起動に失敗する） |

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
2. **spool の処理順は mtime ではなく birthtime。** `<message_id>.jsonl` は delta ごとに追記されて
   mtime が動き続けるので、`final:true` が 34〜80 秒遅れて届くと先行メッセージの mtime が後発より
   新しくなり、順序が入れ替わる
3. **未確定なのはフェンスだけではない。** 設計書 §4-2 は「未閉じの ``` 以降は保留」としているが、
   `cleanTextForSpeech` が領域ごと削除する構文は他にもある。**`<` が閉じると、既に発話した文まで
   まとめて消える。** 保留の対象を広げてある（→ `src/text/unstableTail.ts`）

加えて、設計書に無い挙動を足した。

- **行が閉じていれば最後の文を保留しない。** `MessageDisplay` の delta は
  **最後の flush を除いて必ず行単位**で届き（Claude Code のスキーマ記述。実測でも全て改行終わり）、
  `splitIntoSentences` は改行でも分割する。つまり蓄積テキストが行として閉じていれば、最後の文はもう伸びない。
  → `messageAssembler.ts` の `endsAtLineBoundary`

  **これが受け入れ基準を決めた。** 入れる前は段落ごとに最後の1文だけが次の delta を待っており、
  実測で 1.4〜5.7 秒遅れていた。1文しかない段落は丸ごと遅れる。入れた後は delta 到着から
  `speech.jsonl` まで**約 50ms**。

  句点（`。！？`）で終わっているだけでは外さない。`truncateAtUnstableTail` が行の途中で切ると
  句点止まりになりうるが、その先は次の delta で伸びる
- **保留している最後の文を、後続イベントの到着で先に流す。** 上の判定で外れなかった場合の受け皿。
  後続イベントが来た時点でそのメッセージはもう伸びない。順序を保ったまま遅延だけを消せる。
  ただし**同一セッションの**後続に限る。spool はグローバルに1ディレクトリで、`MessageDisplay` は
  matcher 非対応で全セッションで発火するため、限定しないと Claude Code を2枚開いただけで
  書きかけの断片が読み上げられる
- **ack をフロー制御として入れた。** TTS は生成よりずっと遅いので、キューは実質バッファ。
  「起動時に空にする」「上限で古い方から捨てる」も同じ原則（古い発話は無価値）で説明がつく

## 発話の順序について

**メッセージ A が文の途中で止まっているとき、A の末尾は `final` を待つので B の後に発話される。**

断片を読み上げないための意図的な代償で、断片を喋る方が事故に聞こえる。`seq` は書いた順に振られる
ので `seq` の順序は保たれるが、**「同じ `messageId` の発話が連続するとは限らない」**。
クライアント側で messageId ごとにまとめる作りにしないこと。

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

> `unstableTail` が未閉じの `<` を保留するので、**一度発話してから取り消す**事故は起きない。
> `>` が到着した時点で間の文が失われること自体は残っている。

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
| [#6](https://github.com/schwarz9791/chatter-agent/issues/6) | `messageAssembler` の O(N²) 再パース |
| [#7](https://github.com/schwarz9791/chatter-agent/issues/7) | `cleanOrphans` の追加走査 |
