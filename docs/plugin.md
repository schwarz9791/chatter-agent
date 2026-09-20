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
> （実測: 300KB の payload で確定的に、小さい payload でも書き込みに遅延があると 30回中16回）。
> `ExitPlanMode` / `AskUserQuestion` の payload は `tool_input` に計画全文を含むので、
> 64KB のパイプバッファを普通に超える。プラグインをミュートしたユーザーが、沈黙ではなく
> delta ごとにエラーを受け取る状態になる。

### Node を起動しない

Node の起動コスト（~50ms〜）を毎 delta 払うと、`MessageDisplay` の10秒タイムアウトと UI ブロックのリスクの両方に近づく。**重い処理は CLI 側でやる。**

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

実測（Claude Code 2.1.233）では、非 final の delta は**すべて改行で終わって**いて、到着間隔は 0.7〜5.7 秒だった。
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
`diff -r plugin <cache>` で差分なし、`bin/` も **実行権限ごと**入る。

→ **バンドル済み CLI を `plugin/bin/` に同梱する前提（→ [`core.md`](./core.md)）は成立している。**
`core/dist` は見えないので、CLI の解決経路は `${CHATTER_AGENT_CLI:-$PLUGIN_ROOT/bin/chatter-agent-speak.mjs}`
の1本だけにする。

hook script は `${CLAUDE_PLUGIN_ROOT}` を**環境変数としては使っていない**（script 内で見えるか未確認のため）。
`${BASH_SOURCE[0]%/*}/..` で自分の位置から辿る。`hooks.json` 側の `${CLAUDE_PLUGIN_ROOT}` は
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
> `${BASH_SOURCE[0]%/*}/..` は「実行された script の位置」なので、hook script 側の実装は
> どちらでも正しく動く。問題になるのは**人間がバンドルを差し替えるとき**だけ。
>
> ★ **実機確認でバンドルを差し替えるなら `installLocation` を見ること。** キャッシュ側だけを
> 差し替えて誤診しやすい（→ [`knowledge/plugin.md`](./knowledge/plugin.md)「検証時の落とし穴」）。
> **迷うなら両方に置くのが速い。**
>
> ```bash
> # どちらが走るか分からないときは両方に置く
> /bin/cp -f plugin/bin/chatter-agent-speak.mjs "$(node -e 'console.log(JSON.parse(require("fs").readFileSync(process.env.HOME+"/.claude/plugins/known_marketplaces.json","utf8"))["chatter-agent"].installLocation)')/plugin/bin/"
> /bin/cp -f plugin/bin/chatter-agent-speak.mjs ~/.claude/plugins/cache/chatter-agent/chatter-agent/*/bin/
> ```
>
> ★ `cp` が `-i` の alias になっている環境では確認プロンプトで**黙って上書きされない**。
> `/bin/cp -f` のように alias を迂回すること（これも1回踏んだ）。

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
- CLI / `node` が見つからないとき、それぞれの理由が診断ログ（`hook-debug.log`）に残る

検証中は `CHATTER_AGENT_CLI` を存在しないパスに向けて**デタッチ起動を止めている**。
`nohup` で走らせたままだと CLI がいつドレインしたか分からず、検証が非決定的になる。
