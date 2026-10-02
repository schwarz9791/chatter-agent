# ChatterAgent（Tauri）

**メニューバーに常駐して `chatter-agent-server` を起動・停止・再起動するアプリ。** 端末で
`npm run start:server` を叩く代わりに、server の面倒を見る親になる。設定パネルとマスコットの
表示はまだ持たない（後続の段階で入る）。発話の契約は [`protocol.md`](./protocol.md) が正。

実体は `chatter-agent-app/`（Tauri v2。Rust は `src-tauri/src/`）。

| ファイル | 内容 |
|---|---|
| `src-tauri/src/main.rs` | トレイ・メニュー・設定（`settings.json`）の読み書き・終了時の後始末 |
| `src-tauri/src/server.rs` | 環境の解決・事前チェック・起動・停止・監視・ロックの確認・ログの退避 |
| `src-tauri/src/text.rs` | 日英の文言（OS のロケールが `ja` で始まれば日本語、それ以外は英語） |

## ビルドと起動

前提は `core/` がビルド済みであること（server の成果物 `core/dist/chatter-agent-server.mjs` を使う）。
Rust は `chatter-agent-app/mise.toml` で固定している。

```bash
cd core && npm install && npm run build

cd ../chatter-agent-app
npm install
npm run build      # → src-tauri/target/release/bundle/macos/ChatterAgent.app
npm run dev        # 開発時
```

Dock にも ⌘Tab にも出ない。操作はメニューバーのアイコンから行う。

### 初回

メニューの「core の場所を選ぶ…」で `core/` を選ぶ。選んだ場所は
`~/Library/Application Support/tech.sukima.chatter-agent/settings.json` の `coreDir` に保存され、
次回からはアプリを起動すると自動で server を起こす。`dist/chatter-agent-server.mjs` か
`node_modules` が無いフォルダは、警告ダイアログを出して保存しない（動いている server の状態は変わらない）。

## メニューと状態

状態欄は「サーバー: …」の形で、次のどれかが入る。

| 状態 | 意味 |
|---|---|
| 起動中… / 停止中… | 起動・停止の処理中 |
| 起動中（pid N） | 自分が起こした server が動いている |
| 停止 | 動いていない |
| 異常終了（終了コード N / シグナル N） | 自分が起こした server が予期せず終わった |
| 外で動いている（pid N） | 別の手段で起こされた server がいる（次節） |
| 起動できません: 理由 | core が未設定・`dist` か `node_modules` が無い・node が見つからない／古い（24.11 以上が必要）・環境を取得できない・spawn の失敗 |

| 項目 | 動き |
|---|---|
| サーバーを再起動 / サーバーを起動 | 自分の server が動いていれば再起動、そうでなければ起動。外で動いている間と処理中は無効 |
| ログを開く | `server.log` を開く。無ければディレクトリを開く |
| core の場所を選ぶ… | 上記。保存したら起動（動いていれば再起動） |
| ログイン時に起動 | 下記 |
| 終了 | server を止めてから終わる |

## server に渡す環境

**ログインシェルの環境を丸ごと渡す。** `$SHELL`（無ければ `/bin/zsh`）に `-ilc` を渡し、cwd を
`core/` にして `env` を取り出す。だから端末で `npm run start:server` したときと同じになる
（mise が選ぶ node、`XDG_CONFIG_HOME`、`CHATTER_AGENT_*`）。Finder から起動したアプリは薄い環境しか
持たないので、これが要る。

- ★ **ログインシェルが10秒で終わらなければ起動しない。** 状態欄に理由を出し、シェルの stderr の末尾を
  ログへ書く。既知のディレクトリへのフォールバックはしない
- node は解決した環境の PATH で探し、`node --version`（cwd は `core/`）が 24.11 未満なら起動しない

## 外で動いている server

起動の前に `${XDG_CONFIG_HOME:-$HOME/.config}/chatter-agent/server.lock` の所有者 pid を見る。
**生きていれば起動せず「外で動いている」と出す。** 止める・再起動するのは自分が起こしたものだけ。

- 手動の server が止まれば（2秒ごとの監視で気づく）「停止」に戻り、「サーバーを起動」できる
- `npm run start:server` の手動起動はこれまでどおり使える
- 起動した子がすぐ終わり、ロックの所有者が別の pid だったときは、「異常終了」ではなく「外で動いている」にする

## 停止の仕組み

- **server の pid へ SIGTERM を1回だけ送る。** 2回目を送ると server が後始末を飛ばして終わる
  ので、送るのは1回に限る
- 合成エンジンは server が**別のプロセスグループ**で起こしている。ChatterAgent が止めるのは server
  だけで、エンジンを止めるのは server の後始末
- **10秒待っても終わらなければ SIGKILL する。** このときエンジンが残りうる（ログの `[Agent]` 行に
  警告が出る）。残ったエンジンは次の起動の疎通確認で見つかって再利用される
  （→ [`knowledge/core.md`](./knowledge/core.md)「エンジンを起こす」）

## ログ

`~/Library/Logs/tech.sukima.chatter-agent/server.log`。server の stdout / stderr（ファイルへ直接
書かせるので、ChatterAgent が先に終わっても server は壊れない）と、ChatterAgent 自身の `[Agent]` 行が
入る。spawn のたびに 10MB を超えていたら `server.log.1` へ1世代退避する。**回すのは spawn 時だけ**
なので、長く動き続ける1回の実行は上限を超えうる。

## ログイン時に起動

メニューのチェックで切り替える。`~/Library/LaunchAgents/` に plist ができる。**その時点の実行ファイルの
場所を登録する**ので、`/Applications` へ置いてから有効にする。

## LAN に公開するとき

`config.json` の `host` を `0.0.0.0` にして LAN へ公開する場合、macOS の「ローカルネットワーク」の
許可は**server を起動したアプリ**に付く。ChatterAgent から起こしたときは ChatterAgent が対象になる
（→ [`knowledge/mascot-android-xr.md`](./knowledge/mascot-android-xr.md)「繋がらないときの症状と切り分け」）。

## 制約

- ★ **ChatterAgent がメニューの「終了」やログアウト（quit の Apple Event）以外で終わると server が残る。**
  `kill` の SIGTERM や強制終了がこれに当たる。次に起動した ChatterAgent からは「外で動いている」に見える
  （→ [`knowledge/agent.md`](./knowledge/agent.md)）
- **Windows は未対応。** 停止が穏当でなく（server の後始末が走らない）、環境の解決もしない
- **制御 API（`/v1/*`）はまだ叩いていない。** 叩くときは Rust 側から行うこと。WebView から `fetch`
  すると `Origin` が付いて 403 になる（→ [`protocol.md`](./protocol.md)）
