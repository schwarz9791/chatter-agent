# cc-mascot からの移植 — 由来とライセンス

テキスト整形・文分割・感情判定は、[kazakago/cc-mascot](https://github.com/kazakago/cc-mascot)（Apache-2.0）の `electron/` 配下から**初回に一度だけコピー**して持ち込む。

**以後はこのリポジトリのコードとして自由に改変する。** 上流に追従する義務を負わない。

## ⚠ 「cc-mascot 由来」と「自分が cc-mascot の上で書いたもの」を混同しない

移植元として見ていたローカル worktree `worktree-ai-summary` は、**kazakago の `main`（`46f7def`）から分岐した自分のブランチ**で、分岐後のコミットはすべて Masaki Matsumura 名義。上流に PR を出しておらず、公開もしていない。

したがって、そこにあるファイルは2種類に分かれる。

| ファイル | 著作者 | 帰属表示 |
|---|---|---|
| `filters/textFilter.ts` + `.test.ts` | kazakago | **要る** |
| `services/ruleBasedEmotionClassifier.ts` + `.test.ts` | kazakago | **要る** |
| `services/promptEventFormatter.ts` + `.test.ts` | 自分（`e2c566b` で新規作成） | 不要 |
| `services/summarizer/*` | 自分（`9b23434` で新規作成） | 不要 |
| `mutedSessionMonitor.ts` | 自分 | 不要 |

前者2つは `main` と**バイト単位で同一**なので、公開リポジトリから引ける。後者は `main` に存在しないので、`@ <hash>` を書いても誰も辿れない。**自分の著作物に kazakago の帰属を付けない。**

判定はこれで確認できる:

```bash
UPSTREAM=/Users/schwarz/dev/cc-mascot
git -C "$UPSTREAM" log --format='%an %s' 46f7def..worktree-ai-summary -- electron/<path>
```

## なぜ無改変ミラーをやめたか

前身の cc-mascot-xr は `bridge/src/upstream/` を**編集禁止の無改変ミラー**として隔離し、上流の改善をそのまま取り込める状態を維持していた。chatter-agent はこれを引き継がない。

hook 方式への転換で、`textFilter.ts` が**上流と要件で食い違う**ため。chatter-agent は delta を結合した raw 全体へ適用し、さらに「**閉じていない構文の手前で切り落とす**」という上流に存在しない要件がある（未閉じのコードフェンス、書きかけの表の行。閉じ側が来ないと除去の正規表現が空振りして、コードや生の表がそのまま読み上げられる。→ `core/src/text/unstableTail.ts`）。

守り切れないものを「編集禁止」と書いておくと、**制約の方が先に嘘になる**。実態に合わせて降ろした。

**失うもの**: `ruleBasedEmotionClassifier.ts`（501行の感情辞書）は上流が継続的に改善している。ここだけは惜しいので、上流に良い変更があれば diff を見て手で取り込む余地を残す（義務ではない）。

## フォーク点

| | |
|---|---|
| 元リポジトリ | https://github.com/kazakago/cc-mascot |
| ライセンス | Apache License 2.0（Copyright 2026 kazakago） |
| コピー元のコミット | **`46f7def7e3f80e347571760a625da939fad6b852`**（`46f7def` / 2026-08-06 / `main` / "Merge pull request #208 from kazakago/chore/remove-local-git-skills"） |
| コピー実施日 | 2026-08-15（`text/textFilter.ts` と `emotion/ruleBasedEmotionClassifier.ts` および両者のテスト） |

作業は手元のローカル worktree `/Users/schwarz/dev/cc-mascot/.claude/worktrees/ai-summary`（`bc9b4dd`）から行ったが、**コピーした4ファイルは `main` の `46f7def` と同一**なので、ソースのヘッダには公開リポジトリから引ける `46f7def` を書いてある。

## ライセンス上の義務（Apache-2.0）

無改変ミラーをやめたので、`NOTICE` だけでは足りなくなる。

- **§4(b): 改変したファイルには、改変した旨の目立つ告知を付ける。** 移植したファイルの先頭に、由来と改変の有無を書いたヘッダコメントを入れる
- **§4(a)/(d): ライセンス本文と `NOTICE` の同梱・帰属表示を維持する**

ヘッダの書式:

```ts
/**
 * Originally from kazakago/cc-mascot (Apache-2.0, Copyright 2026 kazakago)
 *   electron/filters/textFilter.ts @ 46f7def
 * Modified for chatter-agent.
 */
```

初回コピー直後で未改変のファイルは `Modified` の行を省いてよいが、**手を入れた時点で追記する**。

**ハッシュは公開リポジトリから引けるものだけを書く。** 手元のブランチにしか無いコミットを書くと、読んだ人が辿れない謎の表記になる。

## 移植したファイル

上流の階層をそのまま写す必要はない。**chatter-agent の都合で機能ごとに配置する。**

| 上流 `electron/` | 移送先 `core/src/` | 行数 | 状態 |
|---|---|---|---|
| `filters/textFilter.ts` | `text/textFilter.ts` | 56（テスト 341） | ✅ 済 |
| `services/ruleBasedEmotionClassifier.ts` | `emotion/ruleBasedEmotionClassifier.ts` | 501（テスト 323） | ✅ 済 |

テストはソースと同ディレクトリに並置された `*.test.ts` をそのまま持ってくる。**移植後はこのリポジトリのテストとして育てる**（上流に送る必要はない）。

どちらも **Electron ゼロ依存**。`from "electron"` は0件、`__dirname` / `require()` / `import.meta` も0件。相対 import も無いので、階層を上流と揃える必要はない。

### それぞれの役割

- **`textFilter.ts`** — `cleanTextForSpeech`（Markdown / コードブロック / URL / git ハッシュ除去、10段の正規表現）と `splitIntoSentences`。副作用は持たないが、★ **冪等ではない。2回適用すると結果が変わる**

  ```
  "`# 見出し`"    → 1回目 "# 見出し"    → 2回目 "見出し"
  "`- リスト`"    → 1回目 "- リスト"    → 2回目 "リスト"
  "`| a | b |`"   → 1回目 "| a | b |"   → 2回目 ""      ← 行が丸ごと消える
  ```

  1回目でバッククォートが外れた結果が、2回目には見出し・リスト・表として解釈されて除去されるため。**同じテキストに二度通さないこと**（要約結果の再整形で実際に踏んだ。→ `core/src/cli/worker.ts` の `summarizeSentences`）
- **`ruleBasedEmotionClassifier.ts`** — キーワード辞書 + 文末パターン + ヒューリスティック。LLM 不使用でオフライン・即時

### 実際に加えた改変（2026-08-15 の初回コピー）

型の重複を潰しただけで、ロジックは触っていない。**契約の正は `core/src/core/types.ts`** に置く。

| ファイル | 改変 |
|---|---|
| `text/textFilter.ts` | ヘッダのみ（**未改変**。`Modified` 行なし） |
| `text/textFilter.test.ts` | ヘッダのみ（**未改変**） |
| `emotion/ruleBasedEmotionClassifier.ts` | `export type Emotion = ...` の定義を削除し、`core/types` から `import type` + `export type` で再輸出 |
| `emotion/ruleBasedEmotionClassifier.test.ts` | ヘッダのみ（**未改変**） |

`Emotion` を `core/types` 側に寄せたのは、この union が VRM の標準 expression 名と一対一で、`speech.jsonl` の契約そのものだから。感情判定器はその契約の実装であって、定義元ではない。

**上流と byte 単位で比較したいときは、ヘッダの4〜5行と上の改変行だけを無視すればよい。** 整形は上流の `printWidth: 120` と `.oxfmtrc.json` が一致しているので、`npm run format` をかけても差分は出ない。

## 同梱したアセット（コードではない）

上の表は `electron/` → `core/src/` のコード移植だけを扱う。**Unity 側に持ち込んだバイナリは別枠。**

| 上流 | 移送先 | 大きさ | 改変 |
|---|---|---|---|
| `public/animations/idle_loop.vrma` | `apps/chatter-mascot/Assets/StreamingAssets/idle_loop.vrma` | 157,664 B | **無改変**（バイト単位で同一） |
| `resources/icons/trayTemplate.png` | `apps/chatter-mascot/Assets/StreamingAssets/trayTemplate.png` | 671 B | **無改変**（バイト単位で同一） |
| `resources/icons/trayTemplate@2x.png` | `apps/chatter-mascot/Assets/StreamingAssets/trayTemplate@2x.png` | 1,410 B | **無改変**（バイト単位で同一） |

| | |
|---|---|
| 最終更新のコミット（`idle_loop.vrma`） | **`7ce44bd674c0f5fc65c41b20d5344d4e358f6d5e`**（`7ce44bd` / 2026-02-08 / kazakago / ":recycle: idle_loop.vrmaアニメーションの調整"） |
| 最終更新のコミット（`trayTemplate*.png`） | **`a61a572`**（2026-02-06 / kazakago / ":sparkles: システムトレイアイコンを追加"）。`c641331`（2026-02-16 / "アプリアイコンを差し替え"）は `icon.*` のみで、トレイ側は触っていない |
| コピー実施日 | `idle_loop.vrma` は 2026-08-26（[#56](https://github.com/schwarz9791/chatter-agent/issues/56)。使うのは [#59](https://github.com/schwarz9791/chatter-agent/issues/59)）、`trayTemplate*.png` は 2026-08-31（[#75](https://github.com/schwarz9791/chatter-agent/issues/75)） |

**どちらも kazakago の著作物**。冒頭の「⚠」節の判定に照らすと、上流の `main` に kazakago 名義で
入っているので**帰属が要る側**（`public/` や `resources/` にあるのは自分の作業ブランチで書いたものではない）。
`trayTemplate*.png` はフォーク点 `46f7def` の時点で既に存在することを確認してある
（`git cat-file -e 46f7def:resources/icons/trayTemplate.png`）。

★ **`Modified for chatter-agent.` は要らない。** 無改変なうえ、**バイナリなので
ヘッダコメントを埋め込む場所が無い**。Apache-2.0 §4(b)（改変の告知）は改変していないので
発生せず、§4(a)/(d)（ライセンス本文と帰属の維持）は `NOTICE` とこの表で担保する。

★ **VRoid 公式の VRMA サンプル7種は BOOTH 規約で二次配布禁止**なので同梱できない。
cc-mascot 側も再配布不可のモーションは**プライベート submodule に隔離**したうえで、
これだけを本体に置いている。そこを取り違えないこと。

同じディレクトリに置く `vita.vrm` は **cc-mascot とは無関係**（VRoid Studio 旧ベータ版の
サンプルモデル。CC0）。→ `NOTICE`

## cc-mascot 由来でないもの

同じディレクトリに並んでいるが、**帰属表示もヘッダも要らない**。

| ファイル | 出どころ |
|---|---|
| `text/unstableTail.ts` | chatter-agent で新規に書いた（読み上げたくない末尾の切り落とし。未閉じの ``` と書きかけの表の行） |
| `prompt/promptEventFormatter.ts` + `.test.ts` | 自分が cc-mascot の作業ブランチ上で書いたものを持ち込んだ |
| `summarizer/` 配下すべて | 自分が cc-mascot の作業ブランチ上で書いたものを持ち込んだ（`9b23434` で新規作成） |
| `core/` `cli/` `server/` 配下すべて | chatter-agent 独自 |
| `apps/chatter-mascot/Assets/Plugins/macOS~/ChatterMascotNative/` | chatter-agent で新規に書いた（#75 のネイティブプラグイン。**cc-mascot に相当する実装は無い** —— あちらは Electron の `Tray` / `app.dock.hide()` で済んでおり、ObjC のコードは1行も存在しない） |
| `apps/chatter-mascot/Assets/ChatterMascot/Runtime/Ui/` `Runtime/Settings/` | chatter-agent で新規に書いた（#75。メニューの組み立て・ショートカットの解釈・設定の永続化） |

`prompt/promptEventFormatter.ts` は移送時に `SpeakMessage` の import 元を `../adapters/harnessAdapter`（kazakago の `adapters/` は移植しない）から `../core/types` に張り替えてある。

### `summarizer/`（AI要約）の移植で落としたもの・変えたもの

`summarizer/` は上の表のとおり kazakago の帰属もライセンスヘッダも `NOTICE` への追記も不要（→冒頭の「⚠」節の判定）。ただし移送時に `services/summarizer/` の一部を落とし、設計をいくつか変えている。

**落としたもの**（バックエンド抽象化が claude 専用化で不要になったため。→ `core/src/summarizer/types.ts` のヘッダ）:

| ファイル | 何を落としたか |
|---|---|
| `backends.ts` | codex / gemini のバックエンド分岐 |
| `detect.ts` | CLI 自動検出（ログインシェル PATH の解決 `zsh -ilc` + `--version` の疎通確認） |
| `isolation.ts` | ログファイルパスをエンコードして除外する方式 |
| `semaphore.ts` | 同時実行数の制限 |

**変えたもの**:

| | 移植元 | chatter-agent |
|---|---|---|
| 実行方式 | 非同期 spawn + セマフォ | `execFileSync` の同期実行。呼び出し元 `drainSpool` が完全に同期で、単一ワーカーのロックが直列化を担うため、同時実行の制御自体が不要になった |
| 滞留ガード | 待ち行列が閾値を超えたらスキップ | 1回のドレインで要約してよい回数の上限（`aiSummaryMaxPerDrain`）。同期実行では待ち行列の概念が無いための読み替え |
| 無限ループ防止（要約 CLI 自身の出力の除外） | ログファイルパスのエンコード（`isolation.ts`） | 要約 CLI に渡した `session_id` のレジストリ（`workerState.ts` の `summarizerSessionIds`）。`session_id` が hook payload に直接入っているので、パス突き合わせより正確に塞げる |

移送先は `core/src/summarizer/`（`types.ts` / `prompt.ts` / `claudeCli.ts` / `summaryPipeline.ts`）。★ **コマンドの解決（`findCommandPath`）だけは [#51](https://github.com/schwarz9791/chatter-agent/issues/51) で `core/src/core/commandPath.ts` へ出した**（合成エンジンの実行パス解決にも要るようになったため）。移動しただけでロジックは変えていない。**帰属の判定は変わらない**（これも自分の著作物なので kazakago の帰属は付けない）。`summaryPipeline.ts` の import は移植元の `../../filters/textFilter` から `../text/speechText`（`toSpeechSentences`）に張り替えてある（`cleanTextForSpeech` / `splitIntoSentences` の直呼びはしない。理由は上の「それぞれの役割」の項の冪等性の記述）。

## 持ち込まないファイル

cc-mascot は jsonl ログ監視方式なので以下も持っているが、chatter-agent は hook 方式なので**すべて捨てる**。将来「やっぱり要るのでは」と思ったときのために理由を残す。

| ファイル | 捨てる理由 |
|---|---|
| `logMonitor.ts` / `fileTail.ts` | jsonl 監視をやめたため。テキストは `MessageDisplay` hook から直接来る |
| `adapters/` 5本（`harnessAdapter` / `claudeCode` / `codex` / `geminiCli` / `antigravity`） | 対象が Claude Code のみになり、ログ形式の抽象化が不要になったため |
| `parsers/` 4本 | 同上 |
| `activeSessionMonitor.ts` | `session_id` が hook payload に直接入るため、`active-session` ファイルによる伝達が不要 |
| `promptEventMonitor.ts` | `chatter-agent-speak` に統合。spool を読むのは CLI 本体の役割（整形担当の `promptEventFormatter.ts` は持ち込んでいるので混同しないこと） |
| `main.ts` / `preload.ts` / `autoUpdater.ts` | Electron 固有 |

## 上流の変更を見たくなったとき

追従の義務は無いが、感情辞書の改善などを取り込みたくなることはある。

```bash
UPSTREAM=/Users/schwarz/dev/cc-mascot
git -C "$UPSTREAM" log --oneline 46f7def..origin/main -- electron/services/ruleBasedEmotionClassifier.ts
git -C "$UPSTREAM" diff 46f7def..origin/main -- electron/services/ruleBasedEmotionClassifier.ts
```

**手で当てる。** rsync で丸ごと上書きすると、こちら側の改変が消える。取り込んだらこのファイルにその旨と、新しい基準コミットを追記する。
