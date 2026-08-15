# `plugin/` — Claude Code プラグインの制約

Claude Code の hook を受けて spool に書くだけの bash スクリプト群。**設計の芯は「捕捉」と「加工」の分離**で、ここは捕捉だけを担当する。

## hook script がやることは3つだけ

```
1. CHATTER_AGENT_DISABLE が設定されていたら即 exit 0     ← 無限ループ防止
2. payload を spool に追記
3. CLI をデタッチ起動（& / nohup）して即 exit 0
```

### Node を起動しない

`message_id` は `grep` / `sed` で抜く。Node の起動コスト（~50ms〜）を毎 delta 払うと、`MessageDisplay` の10秒タイムアウトと UI ブロックのリスクの両方に近づく。**重い処理は CLI 側でやる。**

### `MessageDisplay` の制約

| | |
|---|---|
| matcher | **非対応。毎回必ず発火する** |
| タイムアウト | **10秒**（他の hook は600秒）。UI 表示経路に同期している可能性がある |
| 出力 | **read-only。** exit 2 でも "Original text is displayed; stderr ignored"、JSON 出力も無視される |

matcher が効かない＝**全セッションに影響する**ので、無効化手段（`CHATTER_AGENT_DISABLE`）を必ず用意すること。

## 無限ループ防止

AI 要約は `claude -p` 等をヘッドレス実行する。**その出力自身が `MessageDisplay` を発火させる**ため、対策しないと「要約 → 要約の出力を読み上げ → また要約」で無限に増殖する。

- **第1層**: 要約プロセスを `CHATTER_AGENT_DISABLE=1` を付けて spawn する。hook script は**先頭でこれを見て即 `exit 0`**。環境変数は子プロセスの Claude Code とそのフックまで伝播する
- **第2層**: 要約用に採番した session-id をレジストリに記録し、payload の `session_id` が一致したら捨てる（CLI 側の責務）

cc-mascot はログのパスをエンコードして除外していたが、**`session_id` が payload に直接入っているので本方式の方が正確**に塞げる。

## spool のファイル命名

単一の spool ファイルに追記し続けると、「ワーカーが処理済み部分を削る」ときに hook の追記と競合する。**メッセージごとに別ファイルにして、処理し終えたら丸ごと削除する**ことで、この競合ごと消す。

| 種別 | パス | 書き方 |
|---|---|---|
| アシスタントの発言 | `spool/<message_id>.jsonl` | delta ごとに1行追記 |
| 応答待ち通知 | `spool/prompt-<…>.json` | 1イベントで完結するので単発で置く |

ワーカーは**到着順（mtime 順）**に処理し、`final:true` を処理し終えたファイルを削除する。

## hooks.json の3種

| hook | matcher | スクリプト |
|---|---|---|
| `MessageDisplay` | （非対応） | `scripts/on-message.sh` |
| `PreToolUse` | `AskUserQuestion\|ExitPlanMode` | `scripts/on-prompt.sh` |
| `Notification` | `permission_prompt` | `scripts/on-prompt.sh` |

## 検証時の落とし穴

### 設定変更はセッション再起動が必要

プロジェクトローカルの `.claude/settings.local.json` を書いても、**そのセッション中は反映されない**。対照として `PostToolUse` を仕掛けても発火しないことで確認済み。書き換えたら必ずセッションを立て直す。

### 実装の最初のステップ

**`/plugin install` 後に `${CLAUDE_PLUGIN_ROOT}` が実際に何を指すかを実測する。**

```bash
# ダミーのフックで
echo "$CLAUDE_PLUGIN_ROOT" >> /tmp/x
```

ここが想定と違うと、バンドル済み CLI を `plugin/bin/` に同梱する前提（→ [`core.md`](./core.md)）が崩れ、**配布方法から見直しになる**。

## 未検証事項

推測で埋めず、実装時に潰すもの。詳細は `_workspace/chatter-agent-design.md` §10。

- `MessageDisplay` が UI をブロックするか（10秒タイムアウトの意味）
- **thinking / tool_use の表示でも発火するか** — 実測では text のみ観測。**thinking を読み上げてしまうと事故**
- サブエージェントの発言で発火するか（`agent_id` が入るか）
- delta 間隔の下限（実測は 0.5〜3.5秒。もっと速くなると bash 起動が積み上がる）
- **bash で `message_id` を安定して抜けるか** — spool のファイル分割がこれに依存する。抜けなかった場合は単一 spool + 位置管理に戻す

## 現在の状態

**`plugin/` は未作成。** 作成したらルート `CLAUDE.md` の状態表を更新すること。
