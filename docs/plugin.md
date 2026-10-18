# `plugin/` — Claude Code プラグインの制約

Claude Code の hook を受けて spool に書くだけの bash スクリプト群。**設計の芯は「捕捉」と「加工」の分離**で、ここは捕捉だけを担当する。

spool より先（記録・配信キュー・WebSocket）は [`protocol.md`](./protocol.md) と [`core.md`](./core.md) の担当。

## hook script がやることは4つだけ

```
1. stdin（payload）を最後まで読み切る            ← 途中で exit すると呼び出し側が EPIPE になる
2. CHATTER_AGENT_DISABLE が設定されていたら exit 0 ← 無限ループ防止
3. payload を spool に tmp + rename で置く（追記はしない）
4. CLI をデタッチ起動（& / nohup）して即 exit 0
```

> ★ **1 と 2 の順序を逆にしないこと。** `CHATTER_AGENT_DISABLE=1` は無効化中も常に成立するので、
> 判定を stdin 読み切りより先に置くと、無効化されている間**ずっと** stdin を読み切らずに
> 終了することになる。Claude Code 側は書き込みが最後まで届かないと EPIPE になる
> （実測は [`knowledge/plugin.md`](./knowledge/plugin.md)「潰れたもの」）。
> `ExitPlanMode` / `AskUserQuestion` の payload は `tool_input` に計画全文を含むので、
> 64KB のパイプバッファを普通に超える。プラグインをミュートしたユーザーが、沈黙ではなく
> delta ごとにエラーを受け取る状態になる。

### Node を起動しない

Node の起動コストを毎 delta 払うと、`MessageDisplay` の10秒タイムアウトと UI ブロックのリスクの両方に近づく。**重い処理は CLI 側でやる。**

判定も抽出も**bash のパラメータ展開だけ**で書いてある（`scripts/_lib.sh`）。CLI を起こす直前まで fork が無い。

- **`message_id` の抽出に `sed` を使わないこと。** 貪欲マッチで最後の出現を拾うため、delta 本文に同じキー名が出てくると別の値を掴む。`${var#*'"message_id":"'}` の最短一致を使う（JSON の文字列内には `"key":"` という並びが現れないため、常にトップレベルの値が取れる）
- `chatter_json_string` / `chatter_json_number`（`_lib.sh`）は探索対象を payload の先頭 `CHATTER_HEAD_WINDOW`（4096バイト）に限る。トップレベルのキーは payload の冒頭に固まるので実害が無い

### `MessageDisplay` の制約

| | |
|---|---|
| matcher | **非対応。毎回必ず発火する** |
| タイムアウト | **10秒**（他の hook は600秒）。UI 表示経路に同期している可能性がある |
| 出力 | **完全な read-only ではない。** hookSpecificOutput に `text`（"Text displayed in place of the delta"）があり、**画面の delta を差し替えられる** |

matcher が効かない＝**全セッションに影響する**ので、無効化手段（`CHATTER_AGENT_DISABLE`）を必ず用意すること。

出力が差し替えに使われる以上、**デタッチした CLI の stdout が hook の stdout に混ざってはいけない**。
`nohup … >/dev/null 2>&1 </dev/null &` で3つとも切る。stdin まで切るのは、hook の fd を握ったままの子が
残ると Claude Code が EOF を待って止まりうるため。`setsid` は macOS に無いので使わない。

### delta の届き方（2.1.233 のスキーマ記述 + 実測）

| フィールド | 仕様 |
|---|---|
| `index` | 0 始まり。flush ごとに1つ増える |
| `final` | 最後の flush だけが真。**1メッセージにちょうど1回** |
| `delta` | **最後の flush を除いて必ず行単位**。final の delta は、メッセージが改行で終わると**空になる** |

ここから帰結が1つある。

**`delta` が空でも spool に書くこと。** `final:true` は**通常経路（メッセージがそのまま閉じた場合）で
発話とファイルの削除を駆動する合図**。唯一ではない — 救済経路（`hasNewerInSameSession`）も同一セッションの
後続イベントを合図に publish とファイル削除の両方を駆動する（→ [`../CLAUDE.md`](../CLAUDE.md)「絶対に守ること」1）。ただし
救済は「後続が来たら」の話で、来る保証は無い。空だからと `final:true` の payload を捨てると、通常経路では
そのメッセージは**一文も**発話されず、救済も発火しなければ孤児掃除（既定6時間）まで残ったまま消える。
[#30](https://github.com/schwarz9791/chatter-agent/issues/30) で `final` を待つようになった分、
ここを落としたときの被害は「最後の1文が出ない」から「（救済が発火しない限り）メッセージ全損」に変わっている。

実測（Claude Code 2.1.233）では、非 final の delta は**すべて改行で終わって**いた。
**thinking では発火しない**（thinking を挟んだ delta が1件も観測されなかった）。

**逆に、メッセージの最終行は改行で終わらないので final flush でしか来ない。** これが遅延の下限を決める（下記）。

## 無限ループ防止

AI 要約は `claude -p` 等をヘッドレス実行する。**その出力自身が `MessageDisplay` を発火させる**ため、対策しないと「要約 → 要約の出力を読み上げ → また要約」で無限に増殖する。

- **第1層**: 要約プロセスを `CHATTER_AGENT_DISABLE=1` を付けて spawn する。hook script は**stdin を読み切った直後にこれを見て `exit 0`**（stdin より先に判定すると EPIPE になる。上の「hook script がやることは4つだけ」参照）。環境変数は子プロセスの Claude Code とそのフックまで伝播する
- **第2層**: 要約用に採番した session-id をレジストリに記録し、payload の `session_id` が一致したら捨てる（CLI 側の責務）

> ★ **第1層は実機で効くことを確認済み。第2層は実経路で一度も発火していない**（第1層で止まるため。
> 実経路の発火は `npm run verify:phase-a` の⑰でしか確認できていない）。
> → [`knowledge/plugin.md`](./knowledge/plugin.md)「AI要約の実機実測」

> ★ **trim は bash 側と Node 側（`core/src/core/config.ts` の `parseBoolean`）で揃えること。**
> `LC_ALL=C` 下の `[[:space:]]` は ASCII の空白しか含まないので、全角スペース（U+3000）・
> NBSP（U+00A0）・BOM（U+FEFF）を落とせない。`CHATTER_AGENT_DISABLE` にこれらが混じった値
> （IME コピペで容易に付く）だと、bash 側は「無効化されていない」、Node 側（`.trim()` は
> Unicode 対応）は「無効化されている」と食い違い、**hook は spool に積み続けるのに CLI は
> ロックを取る前に return して何もドレインしない**——診断も出ないまま孤児掃除（既定6時間）
> まで spool が増え続ける。`_lib.sh` の `chatter_disabled` は、この3種のバイト列
> （`E3 80 80` / `C2 A0` / `EF BB BF`）を ASCII 空白と組み合わせても剥がせるようループする。

cc-mascot はログのパスをエンコードして除外していたが、**`session_id` が payload に直接入っているので本方式の方が正確**に塞げる。

## spool のファイル命名

| 種別 | パス | 書く人 | 書き方 |
|---|---|---|---|
| アシスタントの発言 | `spool/<message_id>.<index>.json` | hook | delta ごとに1ファイルを tmp + rename で置く |
| 応答待ち通知 | `spool/prompt-<…>.json` | hook | 1イベントで完結するので単発で置く |

**追記はしない。** bash から任意長の追記を原子的にする移植可能な方法が無いので、1イベント1ファイルを
tmp + rename で置く（→ [`knowledge/plugin.md`](./knowledge/plugin.md)「なぜ追記をやめたか」）。
tmp の名前は対象パスに `.tmp` を足しただけ。

区切りに `.` を使ってよいのは、`_lib.sh` の `chatter_safe_name` が `[A-Za-z0-9_-]` 以外を弾いていて
`message_id` に `.` が絶対に入らないから。**ここのサニタイズを緩めると命名のパースが壊れる。**

**spool に書くのは hook だけ。spool には状態を持たない。**

ワーカーは**到着順**に処理し、同じ `message_id` の delta ファイルを1エントリにまとめて `index` 昇順に
結合する。処理し終えたら、そのメッセージの delta ファイルを全部削除する。到着順は
**`index` が 0 のファイルの `birthtime`** で決める（`final` は大きく遅れて届くので、遅れて増えた
ファイルに引きずられないよう常に先頭を基準にする）。

### spool のパスに条件分岐を足さない

`${XDG_CONFIG_HOME:-$HOME/.config}/chatter-agent/spool` を一行で組む。これは
`core/src/core/paths.ts` の冒頭が決めている制約で、分岐を足すと bash 側と Node 側が静かにズレる。

その帰結として、**hook は Windows の `%APPDATA%` を見ていない**（core 側は見る）。
当面の対象が macOS なので、ズレる可能性より一行で書けることを取っている。

## hooks.json の3種

| hook | matcher | スクリプト |
|---|---|---|
| `MessageDisplay` | （非対応） | `scripts/on-message.sh` |
| `PreToolUse` | `AskUserQuestion\|ExitPlanMode` | `scripts/on-prompt.sh` |
| `Notification` | `permission_prompt` | `scripts/on-prompt.sh` |

## `${CLAUDE_PLUGIN_ROOT}` の実体（実測済み）

`/plugin install` は、プラグインディレクトリを
`~/.claude/plugins/cache/<marketplace>/<plugin>/<version>/` へ **完全コピー**する（symlink ではない）。
`diff -r plugin <cache>` で差分なし、`scripts/` も **実行権限ごと**入る。

→ **プラグインの外にある `core/dist` は見えない。** CLI は `${CLAUDE_PLUGIN_ROOT}` から探さない。

### CLI の解決順（`_lib.sh` の `chatter_spawn_cli`）

1. **`CHATTER_AGENT_CLI` が空でなければ**、`nohup node "$CHATTER_AGENT_CLI"` で起こす。開発時と、ChatterAgent を
   使わない運用（Linux・手動の `npm run start:server`）向け。指す先は `core/` で `npm run build` した
   `core/dist/chatter-agent-speak.mjs`。ファイルが無い・`node` が PATH に無いときは `hook-debug.log` に診断を
   残して抜ける。★ **`bin-path` へは落ちない**
2. **そうでなければ** `{root}/bin-path` を bash の `read` で読み（fork しない）、実行できるファイルなら
   `nohup "$bin" speak` で起こす。無い・実行できないときは `hook-debug.log` に `SpawnCli` / `bin not found` を残して抜ける

`bin-path` は ChatterAgent が起動のたびに、同梱の実行ファイルの絶対パスを1行で書く（tmp + rename）。
開発モード（`devCoreDir`）では消す。**ChatterAgent を1度起動するまで喋らない。** 終了しても `bin-path` は残る。
プラグイン自体は bash だけで、利用者の node は要らない。

hook script は `${CLAUDE_PLUGIN_ROOT}` を**環境変数としては使っていない**（script 内で見えるか未確認のため）。
`${BASH_SOURCE[0]%/*}` で自分の隣の `_lib.sh` を探す。`hooks.json` 側の `${CLAUDE_PLUGIN_ROOT}` は
Claude Code が置換するので、そちらは確実に効く。

> ★ **コピーが作られることと、そのコピーが走ることは別**（2026-08-23 実測）。
>
> marketplace を**ローカルディレクトリ**として登録していると
> （`known_marketplaces.json` の `source.source` が `"directory"`）、キャッシュにコピーは
> 作られるのに、**hook が実行されるのは登録元のディレクトリの方**だった。
>
> | ファイル | フィールド | 今回の値 | hook が走るか |
> |---|---|---|---|
> | `installed_plugins.json` | `installPath` | `~/.claude/plugins/cache/…/0.1.0` | **走らない** |
> | `known_marketplaces.json` | `installLocation` | `/Users/schwarz/dev/chatter-agent` | **走る** |
>
> `${BASH_SOURCE[0]%/*}` は「実行された script の位置」なので、hook script 側の実装は
> どちらでも正しく動く。問題になるのは**人間が `scripts/` を差し替えるとき**だけ。
>
> ★ **実機確認で `scripts/` を差し替えるなら `installLocation` を見ること。** キャッシュ側だけを
> 差し替えて誤診しやすい（→ [`knowledge/plugin.md`](./knowledge/plugin.md)「検証時の落とし穴」）。
> CLI だけ差し替えるなら `CHATTER_AGENT_CLI` で足りる。

## バージョン

**`plugin/` の中身（`scripts/`・`hooks/hooks.json`）が変わったら `version` を上げる。** CLI は ChatterAgent の側にあるので、CLI だけを直したときは上げない。
`plugin/.claude-plugin/plugin.json` と `.claude-plugin/marketplace.json` の2箇所を同じ値にする。
0.x の間は、機能の追加や挙動の変更なら minor、不具合の修正だけなら patch を上げる。

GitHub など git の marketplace から入れた環境では、キャッシュのコピーが走る。Claude Code は
`plugin.json` の `version`（無ければ marketplace 側の値）で更新の要否を決めるので、上げ忘れると
`/plugin` の更新が「最新」と判定され、**変更が利用者に届かない**。

hook が ChatterAgent に新しく頼る変更（例: `bin-path`）や、spool の形（命名・payload）を変える変更は、
**それを読める ChatterAgent を先に出してから**プラグインの版を上げる。プラグインは git の marketplace から
マージした時点で届くが、ChatterAgent は利用者が更新するまで古いままなので、逆にすると hook が黙るか、
古い CLI が spool を読めずに孤児掃除で捨てる。`agent-v*` のタグは origin の main に入ったコミットにしか
切れない（→ [`release.md`](./release.md)）ので、そういう変更はマージした直後に間を空けず `agent-v*` を切る。

ローカルディレクトリとして登録した開発環境は、登録元のディレクトリがそのまま走る（上の ★）。
`version` に関係なく、pull してセッションを開き直すか `/reload-plugins` すれば新しい中身になる。
`/plugin` の更新が「最新」と出ても、走っているものには影響しない。

2箇所が揃っているかは、リポジトリのルートで `claude plugin validate .` を叩けば分かる
（食い違うと警告が出る。食い違ったときは `plugin.json` の値が使われる）。

## 検証

`npm run verify:phase-a`（CI の `verify` ジョブでも回る）は、**payload を実際の hook の stdin に流す**。
手で spool を組むと「hook が spool に正しい形で置けるか」だけが検証の外に残り、そこが一番壊れやすい。

hook 側のガードとして見ているもの:

- delta 本文の `"message_id"` に引っ張られない（貪欲マッチの回帰）
- `agent_id` 付きの payload を捨てる
- `message_id` が取れない / ファイル名に使えない値を捨てる
- `index` が数値として取れない payload を捨てる（`chatter_json_number`）
- `CHATTER_AGENT_DISABLE=1` で積まない、`=0` では黙らない（hook 側・CLI 側の両方）
- `final:true` の `delta` が空でも、メッセージ全文が出る（★ 検査対象のメッセージは**専用セッションに隔離する**こと。同一セッションに他の probe が居ると `hasNewer` 救済で発話されてしまい、`final` を経由しない経路で緑になる → [#22](https://github.com/schwarz9791/chatter-agent/issues/22)）
- 同一メッセージに30並行で hook を起動しても、delta ファイルが壊れず `index` も欠けない
- `CHATTER_AGENT_DISABLE=1` の間でも stdin を読み切り、300KB 相当の payload で EPIPE にならない（`on-message.sh` / `on-prompt.sh` の両方）
- `message_id` の無い64KB payload の処理が閾値（1秒）以内で終わる（`CHATTER_HEAD_WINDOW` の窓が効いている）
- 全角スペース付きの `CHATTER_AGENT_DISABLE` で bash 側と Node 側の判定が揃う
- `CHATTER_AGENT_CLI` の経路で、CLI が無いとき・`node` が無いときの理由が診断ログ（`hook-debug.log`）に残る
- `bin-path` が無いとき、`bin not found` が診断ログに残る
- `bin-path` が指す実行ファイルが、node の無い PATH でも `speak` 付きで起動される

検証中は `CHATTER_AGENT_CLI` を存在しないパスに向けて**デタッチ起動を止めている**（`bin-path` の検査だけは上書きを外し、引数を記録するだけの偽の実行ファイルを指して起動を見る）。
`nohup` で走らせたままだと CLI がいつドレインしたか分からず、検証が非決定的になる。
