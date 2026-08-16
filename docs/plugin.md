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

> ★ **`message_id` の抽出に `sed` を使わないこと。** `sed 's/.*"message_id"…'` は貪欲マッチで
> **最後の出現**を拾うので、delta 本文に同じキー名が出てくると別の値を掴み、spool のファイルが割れる。
> **このリポジトリでは実際に起きる**（`message_id` の話を書いた瞬間に踏む）。
>
> `${var#*'"message_id":"'}` は最短一致なので常にトップレベルの値を取る。JSON の文字列の中では
> `"` が必ず `\"` にエスケープされるため、`"key":"` という並びは本文側には現れない。
> 同じ理屈で `agent_id` の有無も `case` で判定できる。

> ★ **その最短一致も、マッチしないときは bash 3.2 で二乗になる。** 実測（`LC_ALL=C`・
> 非マッチ時の1回の strip）: 4KB 16ms → 16KB 141ms → 64KB **2.1秒**。`message_id` が改名・
> 省略された payload（64KB・delta 込み）だと、整形済み JSON フォールバックの分まで含めて
> 非マッチ×2 で **4.2秒**（実測）になり、10秒の timeout に近づく。
>
> `chatter_json_string` / `chatter_json_number`（`_lib.sh`）は、この対策として探索対象を
> payload の先頭 `CHATTER_HEAD_WINDOW`（4096バイト）に限っている。トップレベルのキーは
> `JSON.stringify` がオブジェクトの宣言順にそのまま出す以上 payload の冒頭に固まっているので
> 実害が無い。窓を切っているのは「マッチの有無を確かめる走査」で、値の切り出しも同じ窓の中で
> 完結する前提を置いている（`message_id` / `index` の値は数十バイトを超えない）。

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

ここから2つの帰結がある。

1. **`delta` が空でも spool に書くこと。** `final:true` はファイルの削除と最後の1文の flush を駆動する
   唯一の合図で、空だからと捨てると両方が止まる
2. **非 final の delta は行として閉じている** → 蓄積テキストが行境界で終わっていれば、最後の文はもう伸びない。
   `messageAssembler` はこれを使って保留を外している（→ [`core.md`](./core.md)）

実測（Claude Code 2.1.233）では、非 final の delta は**すべて改行で終わって**いて、到着間隔は 0.7〜5.7 秒だった。
**thinking では発火しない**（thinking を挟んだ delta が1件も観測されなかった）。

**逆に、メッセージの最終行は改行で終わらないので final flush でしか来ない。** これが遅延の下限を決める（下記）。

## 無限ループ防止

AI 要約は `claude -p` 等をヘッドレス実行する。**その出力自身が `MessageDisplay` を発火させる**ため、対策しないと「要約 → 要約の出力を読み上げ → また要約」で無限に増殖する。

- **第1層**: 要約プロセスを `CHATTER_AGENT_DISABLE=1` を付けて spawn する。hook script は**stdin を読み切った直後にこれを見て `exit 0`**（stdin より先に判定すると EPIPE になる。上の「hook script がやることは4つだけ」参照）。環境変数は子プロセスの Claude Code とそのフックまで伝播する
- **第2層**: 要約用に採番した session-id をレジストリに記録し、payload の `session_id` が一致したら捨てる（CLI 側の責務）

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

### なぜ追記をやめたか

以前は `<message_id>.jsonl` に delta ごと1行追記していたが、**bash から任意長の追記を
原子的にする移植可能な方法は無い**と分かってやめた。

- `printf` は stdio が**1024 バイト境界**で `write` を分割する。hook は並行して走りうるので
  （実測: `PreToolUse` と `MessageDisplay` が同時に走って診断ログが割れた）、同じファイルへの
  追記がこの境界で相手の書き込みに割り込まれ、UTF-8 文字の途中で千切れる。
  **実測: ASCII 4000B・30並行・4試行で 240 行中 8 行が破損**
- `LC_ALL=C` では防げない。マルチバイトだと分割確率が上がるだけで、原因はロケールではなく
  stdio の書き込み単位そのものにある
- `cat` 経由は macOS では原子的だが、GNU cat は `st_blksize` 単位で書くので Linux で割れる

壊れると `spool.ts` が行を捨て、`index` の連番が途切れ、**そのメッセージの以降の delta が
丸ごと発話されなくなる**。ファイルは `final` を処理できないまま孤児掃除（既定6時間）まで残る。

`rename(2)` はファイルシステム内で原子的なので、**追記そのものをやめて delta ごとに
tmp + rename で1ファイルを置く**ことで、この競合を構造から消した。区切りに `.` を
使ってよいのは、`_lib.sh` の `chatter_safe_name` が `[A-Za-z0-9_-]` 以外を弾いていて
`message_id` に `.` が絶対に入らないから。ここのサニタイズを緩めると、下の命名の
パースが壊れる。

| 種別 | パス | 書く人 | 書き方 |
|---|---|---|---|
| アシスタントの発言 | `spool/<message_id>.<index>.json` | hook | delta ごとに1ファイルを tmp + rename で置く |
| 応答待ち通知 | `spool/prompt-<…>.json` | hook | 1イベントで完結するので単発で置く |
| 出力済みの文数 | `spool/<message_id>.progress.json` | **ワーカー** | hook は触らない |

`.progress.json` はワーカーのサイドカー。CLI は毎 delta 起動して終了するので、「どこまで発話したか」を
プロセス内に持てず、ここに置いている。メッセージの全 delta ファイルを削除するときに一緒に消える。

ワーカーは**到着順**に処理し、同じ `message_id` の delta ファイルを1エントリにまとめて
`index` 昇順に結合する。`final:true` を処理し終えたら、そのメッセージの delta ファイルを
サイドカーごと全部削除する。

> ★ 到着順は **`birthtime`（ナノ秒）** で決めている。ただし「そのメッセージの delta ファイルの
> どれか」ではなく**必ず `index` が 0 のファイルの birthtime** を使うこと。`final:true` は
> 大きく遅れて届くため、遅れて増えた delta ファイルの方が新しく作られるのが普通に起きる。
> それに引きずられて先行メッセージが後発より「新しく」扱われないよう、常に先頭を基準にする
> （index 0 が無い場合は、手元にある中で最小の到着順にフォールバックする）。
> `mtime` は使えない — 1 delta 1 ファイルにしても、複数ファイルの中の「最新」を拾うと
> 上と同じ理由で順序が入れ替わる。ミリ秒でも粗すぎて、同じミリ秒に作られたファイルが
> 同値になる（CI で実際に踏んだ）。
>
> **ただし Linux では `birthtimeNs` が当てにならない。** libuv は statx が無い環境で birthtime を
> ctime から埋めるため、書き込みのたびに進む値になりうる。**この変更（1 delta 1 ファイル）でも
> 解けていない** — bash 3.2 でサブ秒時刻を取るには fork が要る（`perl` 等）ため、hook 側で
> 埋めるとタイムアウト予算を消費してしまう。issue は開けたままにする。
> → [#5](https://github.com/schwarz9791/chatter-agent/issues/5)

孤児掃除（`cleanOrphans`）は CLI が起動しないまま終わった spool を消すが、
**メッセージ単位（＝そのメッセージの delta ファイル全部）でまとめて無活動時間を判定する。**
1 delta 1 ファイルでは各ファイルの mtime は書かれた瞬間で止まるので、ファイル単位で見ると
進行中メッセージの古い delta だけが消えて `index` に欠番ができ、**そのメッセージが永久に
発話されなくなる**。`prompt-*.json` と孤立した `.tmp` は1イベントで完結するので、
従来どおりファイル単位で判定してよい。

hook は spool へのすべての書き込み（delta / `prompt-<…>.json`）を **tmp + rename** で置いている
（`_lib.sh` の `chatter_write_atomic`）。ワーカーは読めない payload を消さずに次のドレインへ回すので
書きかけを掴んでも失われないが、余計な往復が減る。
**tmp の名前は対象パスに `.tmp` を足しただけ**（`<message_id>.<index>.json.tmp` /
`prompt-<…>.json.tmp`）。`.json` で終わらないので、`classify` のどの判定にも一致せず、
rename が終わるまで自然に無視される。

`prompt-<…>` の `<…>` は `<epoch>-<pid>-<random>`。順序はファイル名ではなく birthtime で決まるので、
名前に求められるのは一意性だけ（時刻は spool を覗いたときに読めるように入れてある）。

**`message_id` はファイル名になるので、hook 側でサニタイズすること。** core は
`path.join(spoolDir, fileName)` するだけで検査しない。`[A-Za-z0-9_-]` 以外を含む値は捨てる
（`.` を弾いているので `..` も `*.progress.json` との衝突も同時に塞がる）。

**`index` もファイル名の一部になる。** `chatter_json_number`（`_lib.sh`）が数値として
取り出せない payload（欠落・負数・小数など）は、そのイベントごと捨てる。

### `chatter_safe_name` に `LC_ALL=C` が要る理由 — 追記の原子性とは無関係

`_lib.sh` は先頭で `LC_ALL=C` を立てている。**これは追記を1回の `write(2)` に収めるためではない**
（そもそも上のとおり追記自体をやめている）。本当の理由は `chatter_safe_name` のバイト単位マッチ。

`case "$1" in *[!A-Za-z0-9_-]*)` は ASCII 以外を弾く意図で書いてあるが、**UTF-8 ロケールでは
全角英数（`Ａ` `ａ` `１` 等）がこのパターンマッチで `[A-Za-z0-9_-]` に一致してしまう**
（ロケールの文字クラスがマルチバイト文字を「英数字相当」として扱うため）。C ロケールなら
1バイトずつ比較するので、全角文字は必ずどこかのバイトが `[!A-Za-z0-9_-]` に当たって弾かれる。
`message_id` はファイル名になるので、ここが最後の砦になる。

**`export` しないこと。** ただし「export しなければ子プロセスに伝播しない」わけではない —
呼び出し元のシェルが既に `LC_ALL` を export 済みなら、export 属性ごと引き継がれてデタッチした
node にまで伝播する（実測済み）。ここでの `LC_ALL=C` は「bash 自身のこのプロセスの中だけは
確実に C ロケールにする」ためのもので、伝播を止める効果は期待しないこと。

`verify:phase-a` の ⑧ は、追記をやめたことで「同一メッセージに30並行で hook を起動しても
ファイルが壊れず `index` も欠けない」ことを見ている（旧⑧の「並行追記で行が割れない」という
主張は誤りで、しかも低確率で落ちるテストだったため差し替えた）。

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

## 検証時の落とし穴

### 設定変更はセッション再起動が必要

プロジェクトローカルの `.claude/settings.local.json` を書いても、**そのセッション中は反映されない**。対照として `PostToolUse` を仕掛けても発火しないことで確認済み。書き換えたら必ずセッションを立て直す。

### キャッシュはバージョン単位。同じ版のまま直しても反映されない

コピーはインストール時のスナップショットで、パスに `version` が入る。ソースを直しても
`claude plugin update` は「already at the latest version」と言って**何もしない**。

開発中は次のどちらかを使う。

```bash
# 版を上げずに入れ直す（開発中はこれ）
claude plugin uninstall chatter-agent@chatter-agent --scope local
claude plugin install   chatter-agent@chatter-agent --scope local -y

# CLI だけ差し替えたいなら（hook script の変更は反映されない）
export CHATTER_AGENT_CLI=<repo>/plugin/bin/chatter-agent-speak.mjs
```

**`bin/` の差し替えはセッション再起動が要らない。** hook が毎回 spawn し直すため。
`scripts/` と `hooks.json` を直したときだけ再起動する。

### 実機で測るときは診断ログを点ける

`CHATTER_AGENT_HOOK_DEBUG` を有効にすると、hook が受けた payload を到着時刻つきで
`{root}/hook-debug.log` に落とす。hook は stdout / stderr を握り潰されるので、
「発火したか」「payload に何が入っていたか」を見る窓はここしかない。

**既定 OFF。** 毎 delta で `perl`（ミリ秒の取得）を起動するうえ、**会話の本文がそのまま残る**。
測り終えたら切ること。

`chatter_spawn_cli` が CLI を起動できなかった理由（CLI が無い / `node` が PATH に無い）も、
ここに `SpawnCli` タグで1行残る。**spool をドレインする経路も孤児掃除（既定6時間）を走らせる
経路もここ以外に無い**ので、無言だと診断情報ゼロのまま恒久的に沈黙するプラグインになる。
本リポジトリは mise で Node を固定していて、shim が PATH に載るのは対話 rc 経由のみ——
**Finder / Dock から起動した Claude Code はその PATH を継承しない**ため、`node` が見つからない
状況は現実に起きる。

## 未検証事項

推測で埋めず、潰すもの。詳細は `_workspace/chatter-agent-design.md` §10。

- delta 間隔の下限（実測は 0.7〜5.7 秒。もっと速くなると bash 起動が積み上がる）
- `ExitPlanMode` の直前（`AskUserQuestion` は下記のとおり実測済み）

### 潰れたもの（Claude Code 2.1.233 / macOS で実測）

| 項目 | 結果 |
|---|---|
| `${CLAUDE_PLUGIN_ROOT}` の実体 | キャッシュへの完全コピー。`bin/` も入る（上記） |
| thinking / tool_use でも発火するか | **発火しない。** thinking を挟んでも text の delta のみ |
| **サブエージェントの発言で発火するか** | **発火しない。** 3段落の回答を返す Explore を1本走らせて、`agent_id` / `agent_type` を持つ payload が 0 件。サブの発言は `speech.jsonl` にも混ざらない。hook 側の `agent_id` 除外は**保険として残す**（発火する版が来ても事故らない） |
| `MessageDisplay` が UI をブロックするか | 体感なし。hook は数 ms で返り、delta 到着から `speech.jsonl` まで **約 50ms** |
| bash で `message_id` を安定して抜けるか | 抜ける。ただし `sed` の貪欲マッチは不可（上記） |

### `final:true` の遅延 — 2.1.233 でも起きる

設計書 §2-4 が 2.1.231 で観測した「最終チャンクだけが大きく遅れる」は起きる。

`final` は**メッセージが閉じる瞬間**、つまり次のブロックが始まるときに届く。
したがって遅延は **最後の行を書き終えてから次のブロックを出し始めるまでに、モデルが何をどれだけ
生成したか**で決まる。何の直前かで桁が変わる。

| 直後にあったもの | 直前 delta → `final` |
|---|---|
| ターン終了（ツールを呼ばずに終わる） | ほぼ即座 |
| Bash などのツール呼び出し | 数秒 |
| **`AskUserQuestion`** | **数十秒** |

`AskUserQuestion` だけ桁が違うのは、質問文と選択肢の生成そのものが大きいため。

> ★ **秒数を仕様として扱わないこと。** モデル・thinking の量・ツール入力の大きさで簡単に動く。
> 設計書 §2-4 に載っている秒数も同じ性質の観測値で、上限ではない。
> **回答待ちではない**ので、ユーザーの応答時間とも無関係。

> ★ **これはユーザーの回答待ちではない。** `PreToolUse` はツールが動く前＝質問が画面に出る前に
> 発火し、`final` はそれと**同じミリ秒**に届く。実測では回答が返るより 50 秒ほど早かった。
>
> ついでに、**`MessageDisplay` と `PreToolUse` が同時に走ることは構造的**（質問のたびに起きる）
> ということでもある。旧実装（`.jsonl` への追記）はこれが直接の火種だった。今は
> `MessageDisplay` が書く delta ファイルと `PreToolUse`/`Notification` が書く `prompt-<…>.json`
> が最初から別ファイルなので、同時に走っても衝突しようがない。

### 最終行は final flush でしか来ない

**遅れるのは最後の1文だけで、しかもこれは保留のせいではない。**

`delta` は「最後の flush を除いて必ず行単位」なので、**メッセージの最終行（改行で終わっていない行）は
final flush でしか配信されない**。CLI 側にまだ存在しないテキストなので、保留を外しても縮まらない。

実測（`AskUserQuestion` 直前の 17 文のメッセージ）では、**16 文が各 delta の直後に出て、
final を待ったのは最終行の 1 文だけ**だった。

**`final:true` を待たない設計は変えない。** 最終行以外は待たずに出せているし、
待つ設計にすると全部が最終行の遅れに引きずられる。

## 現在の状態

**実装済み。** 実機（Claude Code 2.1.233 / macOS）で Phase A の受け入れ基準を満たしている。

```
plugin/
├── .claude-plugin/plugin.json    マニフェスト
├── hooks/hooks.json              MessageDisplay / PreToolUse / Notification
├── scripts/_lib.sh               共通処理（fork ゼロ）
├── scripts/on-message.sh         MessageDisplay
├── scripts/on-prompt.sh          PreToolUse / Notification
├── bin/chatter-agent-speak.mjs   バンドル済み CLI（core/ から生成してコミットする）
├── LICENSE                       Apache-2.0 全文
└── NOTICE                        帰属表示
```

`LICENSE` / `NOTICE` があるのは、`/plugin install` が**このディレクトリだけを複製する**ため。
リポジトリルートの `NOTICE` は配布物に含まれないので、Apache-2.0 §4(a)/(d) を満たすには
ここにも要る。バンドル自体にも帰属表示と改変告知を banner で焼き込んでいる
（→ [`core.md`](./core.md)）。CI がこの3点を検証する。

**`bin/` の中身を手で編集しないこと。** `core/` で `npm run build` して生成する。

リポジトリルートの `.claude-plugin/marketplace.json` が `"source": "./plugin"` でここを指している。

## 検証

`npm run verify:phase-a`（CI の `verify` ジョブでも回る）は、**payload を実際の hook の stdin に流す**。
手で spool を組むと「hook が spool に正しい形で置けるか」だけが検証の外に残り、そこが一番壊れやすい。

hook 側のガードとして見ているもの:

- delta 本文の `"message_id"` に引っ張られない（貪欲マッチの回帰）
- `agent_id` 付きの payload を捨てる
- `message_id` が取れない / ファイル名に使えない値を捨てる
- `index` が数値として取れない payload を捨てる（`chatter_json_number`）
- `CHATTER_AGENT_DISABLE=1` で積まない、`=0` では黙らない（hook 側・CLI 側の両方）
- `final:true` の `delta` が空でも保留中の最終文が出る
- 同一メッセージに30並行で hook を起動しても、delta ファイルが壊れず `index` も欠けない
- `CHATTER_AGENT_DISABLE=1` の間でも stdin を読み切り、300KB 相当の payload で EPIPE にならない（`on-message.sh` / `on-prompt.sh` の両方）
- `message_id` の無い64KB payload の処理が閾値（1秒）以内で終わる（`CHATTER_HEAD_WINDOW` の窓が効いている）
- 全角スペース付きの `CHATTER_AGENT_DISABLE` で bash 側と Node 側の判定が揃う
- CLI / `node` が見つからないとき、それぞれの理由が診断ログ（`hook-debug.log`）に残る

検証中は `CHATTER_AGENT_CLI` を存在しないパスに向けて**デタッチ起動を止めている**。
`nohup` で走らせたままだと CLI がいつドレインしたか分からず、検証が非決定的になる。
