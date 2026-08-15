# `core/` — chatter-agent-core の開発規約

`chatter-agent-core` は `chatter-agent-speak`（CLI）と `chatter-agent-server`（WebSocket 配信）を含む Node パッケージ。

ここに書いてあるのは**ツールチェーン固有の地雷**で、設計判断ではない。設計の根拠は `_workspace/chatter-agent-design.md` を見ること。

## 区画の分け方

`summarizer/` 以外は実装済み（Phase A + B）。

```
core/src/
├── cli/          chatter-agent-speak（spool を読む単一ワーカー）
│   ├── index.ts             エントリ。無効化判定 → ロック → ドレイン → 解放
│   ├── lock.ts              mkdir の原子性を使った単一ワーカーのロック
│   ├── spool.ts             走査（到着順）/ 分類 / 読み取り / 削除 / 孤児掃除
│   ├── messageAssembler.ts  ★中核。delta 結合 → 確定した文の切り出し（純粋関数）
│   ├── worker.ts            ドレインループ。応答待ち通知の整形もここ
│   └── workerState.ts       プロセスを跨いで持ち回る重複抑制の状態
├── server/       chatter-agent-server（speech.jsonl → WebSocket 配信）
│   ├── index.ts             合成ルート。監視より先にポートを押さえる
│   ├── wsServer.ts          ← cc-mascot-xr 流用 + `?since=` 対応
│   └── speechTail.ts        差分読み取り / ローテート追従 / 取りこぼし埋め
├── core/         契約と基盤
│   ├── types.ts             SpeechRecord / Emotion / SpeechKind / SpeakMessage
│   ├── paths.ts             ← cc-mascot-xr 流用
│   ├── config.ts            ← cc-mascot-xr configStore 流用
│   └── speechLog.ts         追記 / ローテート / seq 採番 / state 整合
├── text/         テキスト整形・文分割        ← textFilter.ts のみ cc-mascot 由来
├── emotion/      ルールベース感情判定        ← cc-mascot 由来
├── prompt/       応答待ち通知の整形
└── summarizer/   AI要約（既定OFF）           **未着手**
```

判断ロジックは `cli/` と `core/` に置く。**`server/` は判断ロジックを持たない** — `speech.jsonl` の新規行を差分読み取りして全クライアントへ流すだけ。エントリポイントは配線に留める。

cc-mascot 由来のファイルも**このリポジトリのコードとして自由に改変してよい**。隔離区画ではない。詳細は [`origin.md`](./origin.md)。

## 触るときの注意

### 1. cc-mascot 由来のファイルにはライセンスヘッダが要る

Apache-2.0 §4(b) は、**改変したファイルにその旨の目立つ告知を付ける**ことを要求している。由来のあるファイルを触ったら、ヘッダに `Modified for chatter-agent.` があるか確認すること。

**対象は `text/textFilter.ts` と `emotion/ruleBasedEmotionClassifier.ts`（+ 両者のテスト）の4ファイルだけ。** 同じディレクトリに並んでいる `text/pendingFence.ts` と `prompt/` 配下は cc-mascot 由来ではないので、ヘッダを足さないこと。区別の根拠は [`origin.md`](./origin.md)。

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
| `src/server/wsServer.ts` + `.test.ts` | `core/src/server/` | **ほぼそのまま（流用済み）。** ping-pong によるデッドコネクション検出、`bufferedAmount` によるバックプレッシャ、graceful close 込み。`?since=` 対応を足した |
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
npm run verify:phase-b   # speech.jsonl → WebSocket（scripts/verify-phase-b.mjs）
npm run start:server     # 手で動かすとき
```

**実機での確認（ターミナル表示と体感で同時か）は `plugin/` 着手時に行う。** ここまでは
「spool に置いたものが正しく育つ・正しく配信される」までしか見ていない。

CI には入れていない（ビルドとプロセス起動が要るため）。手元で回すもの。

### バンドル

| エントリ | 出力 | 依存 |
|---|---|---|
| `src/cli/index.ts` | `plugin/bin/chatter-agent-speak.mjs`（**git にコミット**） | 全部バンドル。npm 依存ゼロ |
| `src/server/index.ts` | `core/dist/chatter-agent-server.mjs`（gitignore） | `ws` / `chokidar` は external |

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
| 発話ログ | `{root}/speech.jsonl`（退避は `speech.1.jsonl` …） | CLI |
| seq の state | `{root}/speech.state.json` | CLI |
| 抑制の state | `{root}/speak.state.json` | CLI |
| ロック | `{root}/speak.lock/`（ディレクトリ） | CLI |

### 設定と環境変数

環境変数 > `config.json` > 既定値。キーを増やすときは `ChatterAgentConfig` と `SPECS` の両方を直す
（`satisfies` で網羅を型に担保させてあるので、片方だけだとコンパイルが通らない）。

| キー | 既定値 | 環境変数 |
|---|---|---|
| `port` | `8570` | `CHATTER_AGENT_PORT` |
| `host` | `"0.0.0.0"` | `CHATTER_AGENT_HOST` |
| `speakPrompts` | `true` | `CHATTER_AGENT_SPEAK_PROMPTS` |
| `speechLogMaxBytes` | `5242880` | `CHATTER_AGENT_SPEECH_LOG_MAX_BYTES` |
| `speechLogGenerations` | `3` | `CHATTER_AGENT_SPEECH_LOG_GENERATIONS` |
| `spoolMaxAgeHours` | `6` | `CHATTER_AGENT_SPOOL_MAX_AGE_HOURS` |

config に載せない環境変数:

- `CHATTER_AGENT_CONFIG` — `config.json` の場所そのもの
- `CHATTER_AGENT_DISABLE` — hook と CLI を無効化（無限ループ防止の第1層）
- `CHATTER_AGENT_CLI` — 開発時にバンドルを差し替える

## 実装で設計書から動いたこと

設計書（`_workspace/chatter-agent-design.md`）は一次情報だが、実装中に2点だけ上書きした。

1. **spool の処理順は mtime ではなく birthtime。** `<message_id>.jsonl` は delta ごとに追記されて
   mtime が動き続けるので、`final:true` が 34〜80 秒遅れて届くと先行メッセージの mtime が後発より
   新しくなり、順序が入れ替わる
2. **世代交代の検出は inode を主に使う。** 設計書 §6 は「サイズが読み取り位置より小さくなったら」
   としているが、新世代がたまたま同じサイズに達した瞬間に読むと取りこぼす。inode が当てにならない
   環境（Windows）ではサイズの逆行で拾う
3. **世代交代を検出したら、退避された直前世代の未読部分を先に読む。** 読み取りとローテートの間に
   書かれた行は、単に位置をリセットするだけだと消える。退避先の inode が読んでいたファイルと
   一致することを確かめてから読む

4. **未確定なのはフェンスだけではない。** 設計書 §4-2 は「未閉じの ``` 以降は保留」としているが、
   `cleanTextForSpeech` が領域ごと削除する構文は他にもある。**`<` が閉じると、既に発話した文まで
   まとめて消える。** 保留の対象を広げてある（→ `src/text/unstableTail.ts`）

加えて、設計書に無い挙動を2つ足した。

- **保留している最後の文を、後続イベントの到着で先に流す。** `final:true` を待つと 34〜80 秒遅れるが、
  後続イベントが来た時点でそのメッセージはもう伸びない。順序を保ったまま遅延だけを消せる。
  ただし**同一セッションの**後続に限る。spool はグローバルに1ディレクトリで、`MessageDisplay` は
  matcher 非対応で全セッションで発火するため、限定しないと Claude Code を2枚開いただけで
  書きかけの断片が読み上げられる
- **server は watcher に加えてポーリングもする。** 単一ファイルパスの監視はローテートで対象が
  差し替わると取りこぼす（実測で、何秒待っても届かない行が出た）。watcher は通常時の遅延のために
  残し、`readNew` の定期実行を保険にしている

## 既知の限界

### 1回の読み取りの間に2世代以上が流れると、中間世代は配信されない

server は世代交代を検出したとき、退避された**直前1世代**の未読部分までは拾う。それより古い世代は、
読み取り位置を当てにできないので追わない（誤った位置から読んで壊れた行を配信するより、諦める方が良い）。

実運用でここに至るには、**2回の読み取りの間に上限サイズの2倍**（既定なら 10MB）が書かれる必要がある。
`npm run verify:phase-b` の ⑤ が上限を 500 バイトに絞ってこの状態を再現しており、
そこでも「順序が入れ替わらない」「重複しない」ことは保たれる。欠落は `seq` の飛びとして
クライアントに見えるので、`?since=` で埋め直せる（ただし埋め直せるのも現世代の範囲まで）。

## 既知の欠落

移植した `cleanTextForSpeech` が扱えていない記法がある。上流にもこれを保持する意図のテストは無く、
単なる未対応。

| 症状 | 例 |
|---|---|
| **URL 直後の `。` 以降が段落ごと消える** | `参考は https://x。まず読みます。次に実装します。` → `["参考は"]` |
| commit hash の正規表現が数値・英単語を食う | `5242880` / `1048576` / `defaced` が消える |
| 強調・リンク記法が残る | `**` / `*` / `__` / `~~`、`[text](` の残骸 |
| 約物だけの発話が `seq` を消費する | `すごい！！` → `["すごい！", "！"]` |

URL の件がいちばん重く、**文が無言で消える**。それ以外は「記号が読み上げに混ざる」に留まる。

**実機で頻度を見てから整形規則をまとめて見直す方針**にしたので、現状は
`src/cli/messageAssembler.test.ts` の「既知の欠落」ブロックで挙動を固定して可視化してある。
直すときはリンクを段8（URL除去）より**前**に処理する必要がある。

→ [#2 テキスト整形規則を見直す](https://github.com/schwarz9791/chatter-agent/issues/2)

## 後回しにしている課題

| | |
|---|---|
| [#2](https://github.com/schwarz9791/chatter-agent/issues/2) | テキスト整形規則の見直し（上記） |
| [#3](https://github.com/schwarz9791/chatter-agent/issues/3) | WebSocket に認証も Origin 検査も無い。**任意の Web ページから会話全文が読める** |
| [#4](https://github.com/schwarz9791/chatter-agent/issues/4) | `CHATTER_AGENT_DISABLE` の真偽値解釈を bash hook と揃える |
| [#5](https://github.com/schwarz9791/chatter-agent/issues/5) | Linux で `birthtimeNs` が当てにならない（spool の命名で解く） |
| [#6](https://github.com/schwarz9791/chatter-agent/issues/6) | `messageAssembler` の O(N²) 再パース |
| [#7](https://github.com/schwarz9791/chatter-agent/issues/7) | `cleanOrphans` の追加走査 |
