# `core/` — chatter-agent-core の開発規約

`chatter-agent-core` は `chatter-agent-speak`（CLI）と `chatter-agent-server`（WebSocket 配信）を含む Node パッケージ。

ここに書いてあるのは**ツールチェーン固有の地雷**で、設計判断ではない。設計の根拠は `_workspace/chatter-agent-design.md` を見ること。

## 区画の分け方

**`core/src/` はまだ空。** 以下は Phase A 以降で作る想定。

```
core/src/
├── cli/          chatter-agent-speak（spool を読む単一ワーカー）
├── server/       chatter-agent-server（speech.jsonl → WebSocket 配信）
├── core/         speechLog / paths / config
├── text/         テキスト整形・文分割        ← cc-mascot 由来
├── emotion/      ルールベース感情判定        ← cc-mascot 由来
├── prompt/       応答待ち通知の整形          ← cc-mascot 由来
└── summarizer/   AI要約（既定OFF）           ← cc-mascot 由来
```

判断ロジックは `cli/` と `core/` に置く。**`server/` は判断ロジックを持たない** — `speech.jsonl` の新規行を差分読み取りして全クライアントへ流すだけ。エントリポイントは配線に留める。

「cc-mascot 由来」の4区画も**このリポジトリのコードとして自由に改変してよい**。隔離区画ではない。詳細は [`origin.md`](./origin.md)。

## 触るときの注意

### 1. cc-mascot 由来のファイルにはライセンスヘッダが要る

Apache-2.0 §4(b) は、**改変したファイルにその旨の目立つ告知を付ける**ことを要求している。由来のあるファイルを触ったら、ヘッダに `Modified for chatter-agent.` があるか確認すること。

書式と対象ファイルの一覧は [`origin.md`](./origin.md)。

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

`/Users/schwarz/dev/cc-mascot-xr`（GitHub: `schwarz9791/cc-mascot-xr`）に動作確認済みのコードがある。**そのまま使えるものは書き直さないこと。**

| 流用元（`cc-mascot-xr/bridge/`） | 移送先 | 状態 |
|---|---|---|
| `src/server/wsServer.ts` + `.test.ts` | `core/src/server/` | **ほぼそのまま。** 128行 / 6テスト。ping-pong によるデッドコネクション検出、`bufferedAmount` によるバックプレッシャ、graceful close 込み。`?since=` 対応だけ足す |
| `src/config/paths.ts` + `.test.ts` | `core/src/core/` | 環境オブジェクトを引数に取る純関数群。ディレクトリ名を変えて流用 |
| `src/config/configStore.ts` + `.test.ts` | `core/src/core/` | 環境変数 > JSON > 既定値。mtime + size が変わったときだけ再読込。壊れた JSON では直前値を維持 |
| `.github/workflows/validate.yml` | リポジトリルート | typecheck / lint / test の3ジョブ。モノレポ向けに `working-directory` 指定済み |
| `LICENSE` / `NOTICE` | リポジトリルート | Apache-2.0 + 派生物としての帰属表示。構成を踏襲 |

これらは cc-mascot-xr（自分のリポジトリ）由来なので、cc-mascot 由来ファイルのようなライセンスヘッダは不要。

## テスト

- 純粋関数（文の切り出し、seq 採番、spool の走査順）を優先してテストする。ファイル I/O と hook 起動が絡む部分は結合テストで見る
- cc-mascot から移植したテストも**このリポジトリのテストとして育てる**。挙動を変えたらテストも直す

## 開発コマンド

```bash
cd core
npm install
npm run typecheck     # tsc --noEmit
npm run lint          # oxlint
npm run format        # oxfmt
npm run test:run      # vitest run
npm run test:coverage
```

**`build` はまだ無い。** バンドル対象（`src/cli/`）が存在しないため、`tsdown.config.ts` ごと Phase A で作る。tsdown は devDependency に入れてある。

### バンドル時に決まっていること

- **エントリ**: `core/src/cli/index.ts` → **出力**: `plugin/bin/chatter-agent-speak.mjs`
- **拡張子は `.mjs` でなければならない。** `plugin/bin/` に `package.json` を置かないので、`.js` だと Node が CJS として読んで壊れる
- **CLI に npm 依存を持たせない。** バンドルを自己完結させるため、`src/cli/` から到達する範囲は Node 標準モジュールだけで閉じる（`ws` / `chokidar` は `src/server/` 側のみ）

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

### その他

`passWithNoTests` は `vitest.config.ts` にある。**テストが1本でも入ったら消すこと。**
