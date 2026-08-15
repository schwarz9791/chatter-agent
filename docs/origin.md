# cc-mascot からの移植 — 由来とライセンス

テキスト整形・文分割・感情判定・AI要約は、[kazakago/cc-mascot](https://github.com/kazakago/cc-mascot)（Apache-2.0）の `electron/` 配下から**初回に一度だけコピー**して持ち込む。

**以後はこのリポジトリのコードとして自由に改変する。** 上流に追従する義務を負わない。

## なぜ無改変ミラーをやめたか

前身の cc-mascot-xr は `bridge/src/upstream/` を**編集禁止の無改変ミラー**として隔離し、上流の改善をそのまま取り込める状態を維持していた。chatter-agent はこれを引き継がない。

hook 方式への転換で、移植する4種のうち3種が**上流と要件が食い違う**ため。

| ファイル | 食い違い |
|---|---|
| `textFilter.ts` | chatter-agent は delta を蓄積した raw 全体へ**繰り返し適用**する。さらに「未閉じの ``` 以降は保留」という上流に存在しない要件がある |
| `summarizer/isolation.ts` | 無限ループ防止が `CHATTER_AGENT_DISABLE` + session-id レジストリ方式になる。上流の `CLAUDE_CONFIG_DIR` / `GEMINI_CLI_HOME` 隔離とは別物 |
| `promptEventFormatter.ts` | `kind` の設計が独自（`speech.jsonl` の契約に従う） |

守り切れないものを「編集禁止」と書いておくと、**制約の方が先に嘘になる**。実態に合わせて降ろした。

**失うもの**: `ruleBasedEmotionClassifier.ts`（501行の感情辞書）は上流が継続的に改善している。ここだけは惜しいので、上流に良い変更があれば diff を見て手で取り込む余地を残す（義務ではない）。

## フォーク点

| | |
|---|---|
| 元リポジトリ | https://github.com/kazakago/cc-mascot |
| ライセンス | Apache License 2.0（Copyright 2026 kazakago） |
| コピー元のツリー | ローカル worktree `/Users/schwarz/dev/cc-mascot/.claude/worktrees/ai-summary` |
| フォーク点のコミット | `db56b52`（cc-mascot-xr が最後に同期した地点。**このリポジトリではまだコピーしていない**） |

初回コピーを実施したら、**実際にコピーしたコミットハッシュをここに記録すること。** これが「どこから分岐したか」の唯一の記録になる。

## ライセンス上の義務（Apache-2.0）

無改変ミラーをやめたので、`NOTICE` だけでは足りなくなる。

- **§4(b): 改変したファイルには、改変した旨の目立つ告知を付ける。** 移植したファイルの先頭に、由来と改変の有無を書いたヘッダコメントを入れる
- **§4(a)/(d): ライセンス本文と `NOTICE` の同梱・帰属表示を維持する**

ヘッダの例:

```ts
/**
 * Originally from kazakago/cc-mascot (Apache-2.0, Copyright 2026 kazakago)
 *   electron/filters/textFilter.ts @ <fork-commit>
 * Modified for chatter-agent.
 */
```

初回コピー直後で未改変のファイルは `Modified` の行を省いてよいが、**手を入れた時点で追記する**。

## 移植するファイル（ソース11 + テスト9 = 20ファイル）

上流の階層をそのまま写す必要はない。**chatter-agent の都合で機能ごとに配置する。**

| 上流 `electron/` | 移送先 `core/src/` | 行数 |
|---|---|---|
| `filters/textFilter.ts` | `text/textFilter.ts` | 56（テスト 341） |
| `services/ruleBasedEmotionClassifier.ts` | `emotion/ruleBasedEmotionClassifier.ts` | 501（テスト 323） |
| `services/promptEventFormatter.ts` | `prompt/promptEventFormatter.ts` | 97（テスト 138） |
| `services/summarizer/types.ts` | `summarizer/types.ts` | 58（テスト無し） |
| `services/summarizer/prompt.ts` | `summarizer/prompt.ts` | 18（テスト無し） |
| `services/summarizer/semaphore.ts` | `summarizer/semaphore.ts` | 43 |
| `services/summarizer/isolation.ts` | `summarizer/isolation.ts` | 55 |
| `services/summarizer/detect.ts` | `summarizer/detect.ts` | 194 |
| `services/summarizer/backends.ts` | `summarizer/backends.ts` | 156 |
| `services/summarizer/cliSummarizer.ts` | `summarizer/cliSummarizer.ts` | 156 |
| `services/summarizer/summaryPipeline.ts` | `summarizer/summaryPipeline.ts` | 123 |

テストはソースと同ディレクトリに並置された `*.test.ts` をそのまま持ってくる。**移植後はこのリポジトリのテストとして育てる**（上流に送る必要はない）。

これらは **Electron ゼロ依存**であることを cc-mascot-xr で確認済み。`from "electron"` は0件、`__dirname` / `require()` / `import.meta` も0件。実行時の依存は Node 標準（`fs` / `path` / `os` / `readline` / `child_process` / `crypto`）のみ。

### それぞれの役割

- **`textFilter.ts`** — `cleanTextForSpeech`（Markdown / コードブロック / URL / git ハッシュ除去、10段の正規表現）と `splitIntoSentences`。純粋関数なので何度適用しても同じ結果になる
- **`ruleBasedEmotionClassifier.ts`** — キーワード辞書 + 文末パターン + ヒューリスティック。LLM 不使用でオフライン・即時
- **`promptEventFormatter.ts`** — hook の payload → 読み上げ文。依存ゼロの純粋関数
- **`summarizer/`** — CLI エージェントのヘッドレス実行による要約。既定 OFF

### 初回コピー時にやること

1. 上記の対応表どおりに配置する
2. **相対 import を直す。** 上流は `services/summarizer/summaryPipeline.ts` から `../../filters/textFilter` を参照している。移送後は `../text/textFilter` になる
3. 各ファイルにライセンスヘッダを入れる
4. `NOTICE` に帰属表示を書く
5. このファイルの「フォーク点のコミット」を実際の値に更新する

## 移植しないファイル

cc-mascot-xr は jsonl ログ監視方式だったため以下も持っていたが、chatter-agent は hook 方式なので**すべて捨てる**。将来「やっぱり要るのでは」と思ったときのために理由を残す。

| ファイル | 捨てる理由 |
|---|---|
| `logMonitor.ts` / `fileTail.ts` | jsonl 監視をやめたため。テキストは `MessageDisplay` hook から直接来る |
| `adapters/` 5本（`harnessAdapter` / `claudeCode` / `codex` / `geminiCli` / `antigravity`） | 対象が Claude Code のみになり、ログ形式の抽象化が不要になったため |
| `parsers/` 4本 | 同上 |
| `activeSessionMonitor.ts` | `session_id` が hook payload に直接入るため、`active-session` ファイルによる伝達が不要 |
| `promptEventMonitor.ts` | `chatter-agent-speak` に統合。spool を読むのは CLI 本体の役割 |
| `main.ts` / `preload.ts` / `autoUpdater.ts` | Electron 固有 |

## 上流の変更を見たくなったとき

追従の義務は無いが、感情辞書の改善などを取り込みたくなることはある。フォーク点が記録してあれば差分は追える。

```bash
UPSTREAM=/Users/schwarz/dev/cc-mascot
git -C "$UPSTREAM" log --oneline <fork-commit>..HEAD -- electron/services/ruleBasedEmotionClassifier.ts
git -C "$UPSTREAM" diff <fork-commit>..HEAD -- electron/services/ruleBasedEmotionClassifier.ts
```

**手で当てる。** rsync で丸ごと上書きすると、こちら側の改変が消える。取り込んだらこのファイルにその旨を追記する。
