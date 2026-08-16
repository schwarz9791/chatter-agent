# `plugin/` — Claude Code プラグインの制約

Claude Code の hook を受けて spool に書くだけの bash スクリプト群。**設計の芯は「捕捉」と「加工」の分離**で、ここは捕捉だけを担当する。

spool より先（記録・配信キュー・WebSocket）は [`protocol.md`](./protocol.md) と [`core.md`](./core.md) の担当。

## hook script がやることは3つだけ

```
1. CHATTER_AGENT_DISABLE が設定されていたら即 exit 0     ← 無限ループ防止
2. payload を spool に追記
3. CLI をデタッチ起動（& / nohup）して即 exit 0
```

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

- **第1層**: 要約プロセスを `CHATTER_AGENT_DISABLE=1` を付けて spawn する。hook script は**先頭でこれを見て即 `exit 0`**。環境変数は子プロセスの Claude Code とそのフックまで伝播する
- **第2層**: 要約用に採番した session-id をレジストリに記録し、payload の `session_id` が一致したら捨てる（CLI 側の責務）

cc-mascot はログのパスをエンコードして除外していたが、**`session_id` が payload に直接入っているので本方式の方が正確**に塞げる。

## spool のファイル命名

単一の spool ファイルに追記し続けると、「ワーカーが処理済み部分を削る」ときに hook の追記と競合する。**メッセージごとに別ファイルにして、処理し終えたら丸ごと削除する**ことで、この競合ごと消す。

| 種別 | パス | 書く人 | 書き方 |
|---|---|---|---|
| アシスタントの発言 | `spool/<message_id>.jsonl` | hook | delta ごとに1行追記 |
| 応答待ち通知 | `spool/prompt-<…>.json` | hook | 1イベントで完結するので単発で置く |
| 出力済みの文数 | `spool/<message_id>.progress.json` | **ワーカー** | hook は触らない |

`.progress.json` はワーカーのサイドカー。CLI は毎 delta 起動して終了するので、「どこまで発話したか」を
プロセス内に持てず、ここに置いている。`.jsonl` を削除するときに一緒に消える。

ワーカーは**到着順**に処理し、`final:true` を処理し終えたファイルを削除する。

> ★ 到着順は **`birthtime`（ナノ秒）** で決めている。`mtime` は使えない — `<message_id>.jsonl` は
> delta ごとに追記されて mtime が動き続けるので、`final:true` が 34〜80 秒遅れて届くと先行メッセージが
> 後発より「新しく」なり、順序が入れ替わる。ミリ秒でも粗すぎて、同じミリ秒に作られたファイルが
> 同値になる（CI で実際に踏んだ）。
>
> **ただし Linux では `birthtimeNs` が当てにならない。** libuv は statx が無い環境で birthtime を
> ctime から埋めるため、追記のたびに進む値になりうる。根治するならファイル名に順序を埋める
> （`<ns>-<message_id>.jsonl` など）ことになり、**この命名表と CLI の両方を同時に変える**必要がある。
> → [#5](https://github.com/schwarz9791/chatter-agent/issues/5)

hook は `prompt-<…>.json` を **tmp + rename** で置いている。ワーカーは読めない payload を消さずに
次のドレインへ回すので書きかけを掴んでも失われないが、余計な往復が減る。
**tmp の名前は `*.json.tmp` にすること。** `*.tmp.json` だと prompt として拾われる。

`<…>` は `<epoch>-<pid>-<random>`。順序はファイル名ではなく birthtime で決まるので、
名前に求められるのは一意性だけ（時刻は spool を覗いたときに読めるように入れてある）。

**`message_id` はファイル名になるので、hook 側でサニタイズすること。** core は
`path.join(spoolDir, fileName)` するだけで検査しない。`[A-Za-z0-9_-]` 以外を含む値は捨てる
（`.` を弾いているので `..` も `*.progress.json` との衝突も同時に塞がる）。

### 追記は1回の `write(2)` に収める — `LC_ALL=C` を外さない

**マルチバイトのロケールだと bash の `printf` は 1024 バイトごとに `write` を分ける。**
hook は並行して走りうるので（実測: `PreToolUse` と `MessageDisplay` が同時に走って診断ログが割れた）、
同じファイルへの追記が 1024 バイト境界で相手の書き込みに割り込まれ、**UTF-8 文字の途中で千切れる**。

spool の `.jsonl` でこれが起きると、その行が JSON として読めなくなる。core は読めない行を飛ばすので
`index` の連番がそこで途切れ、**そのメッセージの以降の delta が丸ごと発話されない**。
しかも `final` を処理できないまま孤児掃除（既定6時間）まで spool に残る。

payload は日本語を含むと 1024 バイトを普通に超えるので、**実際に踏む**。

`_lib.sh` の先頭で `LC_ALL=C` を立てて回避している。C ロケールならバイト列として一括で write される
（実測: 240 並行追記で破損 0 件。外すと 20 並行で 2 件割れる）。**`export` しないこと** —
bash 自身のロケールだけを変え、デタッチする node には伝播させない。

`verify:phase-a` の ⑧ がこれを見ている。

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

### `final:true` の遅延 — 2.1.233 でも再現する

設計書 §2-4 が 2.1.231 で観測した「最終チャンクだけが 34〜80 秒遅れる」は**再現する**。

`final` は**メッセージが閉じる瞬間**、つまり次のブロック（thinking → ツール呼び出し）が始まるときに届く。
したがって遅延の正体は**その手前の thinking の長さ**で、何の直前かで大きく変わる。

| 直後にあったもの | 直前 delta → `final` |
|---|---|
| ターン終了（ツールを呼ばずに終わる） | +0.62 秒 |
| Bash などのツール呼び出し | +3.3〜7.0 秒 |
| **`AskUserQuestion`** | **+30.06 秒** |

`AskUserQuestion` だけ突出するのは、「何を聞くか」を決める thinking が長いため。
実測では `final` の `MessageDisplay` と `PreToolUse` の hook が**同じミリ秒**に走っていた。

> ★ **この2つが同時に走ることは構造的に保証されている。** 追記の競合（上の `LC_ALL=C` の節）は
> まぐれではなく、質問のたびに起きる。

### 最終行は final flush でしか来ない

**遅れるのは最後の1文だけで、しかもこれは保留のせいではない。**

`delta` は「最後の flush を除いて必ず行単位」なので、**メッセージの最終行（改行で終わっていない行）は
final flush でしか配信されない**。CLI 側にまだ存在しないテキストなので、保留を外しても縮まらない。

実測（`AskUserQuestion` 直前の 17 文のメッセージ）:

| | |
|---|---|
| 16 文 | 各 delta の直後に出た（8 秒以内） |
| 1 文（最終行） | final を待った（+30 秒） |

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
- `CHATTER_AGENT_DISABLE=1` で積まない、`=0` では黙らない
- `final:true` の `delta` が空でも保留中の最終文が出る

検証中は `CHATTER_AGENT_CLI` を存在しないパスに向けて**デタッチ起動を止めている**。
`nohup` で走らせたままだと CLI がいつドレインしたか分からず、検証が非決定的になる。
