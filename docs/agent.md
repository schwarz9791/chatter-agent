# ChatterAgent（Tauri）

**メニューバーに常駐して `chatter-agent-server` を起動・停止・再起動するアプリ。** 端末で
`npm run start:server` を叩く代わりに、server の面倒を見る親になる。**設定パネル**（サーバーの設定と
マスコットの設定）を持ち、**マスコットの表示・非表示とミュートを操作する**（非表示でミュートしていないときは `chatter-agent-player` で声だけ鳴らす）。
マスコット（Chatter Mascot）は描画と再生だけを担い、設定の UI・メニューバー・ショートカットはすべてここにある。発話の契約は [`protocol.md`](./protocol.md) が正。

実体は `chatter-agent-app/`（Tauri v2。Rust は `src-tauri/src/`）。

| ファイル | 内容 |
|---|---|
| `src-tauri/src/main.rs` | トレイ・メニュー・設定窓・設定（`settings.json`）の読み書き・終了時の後始末 |
| `src-tauri/src/server.rs` | 環境の解決・事前チェック・起動・停止・監視・ロックの確認・ログの退避 |
| `src-tauri/src/control.rs` | server の制御 API を Rust から叩く。接続先の解決・HTTP/1.0・許可リスト |
| `src-tauri/src/clients.rs` | 繋ぐクライアント（マスコット / player）を表示とミュートから1つに決める。切り替え・player の起動と停止・監視・ショートカット |
| `src-tauri/src/mascot_app.rs` | Chatter Mascot（Unity アプリ）の探索・起動・終了要求・実行中の pid（macOS 依存部） |
| `src-tauri/src/mascot.rs` | `mascot/settings.json`・VRM・マスコットへの依頼（`mascot/requests/`）・モーション一覧（`mascot/motions.json`）の読み取り |
| `src-tauri/src/text.rs` | 日英の文言（OS のロケールが `ja` で始まれば日本語、それ以外は英語）と、画面へ渡す言語 |
| `index.html` | 設定窓（CSS も中に持つ） |
| `pairing.html` | ペアリング窓（Android とペアリング…。CSS も中に持つ） |
| `src/main.ts` | 設定窓の DOM の組み立てとイベント |
| `src/pairing.ts` | ペアリング窓の DOM とポーリング |
| `src/model.ts` | DOM に触らない純粋な判定（声のキーの選択・環境変数の注記・ショートカットの組み立てと分解・エラー文言の選択） |
| `src/text.ts` | 設定窓の日英の文言 |
| `src/tauri.ts` | Rust のコマンドの型付きラッパ |

## ビルドと起動

前提は `core/` がビルド済みであること（server と player の成果物 `core/dist/chatter-agent-server.mjs` / `core/dist/chatter-agent-player.mjs` を使う）。
Rust は `chatter-agent-app/mise.toml` で固定している。

```bash
cd core && npm install && npm run build

cd ../chatter-agent-app
npm install
npm run build      # → build/ChatterAgent.app
npm run dev        # 開発時
npm test           # 設定窓の純粋な判定（node --test）
```

- `npm run dev` は Vite（`dev:web`、ポート 1420）も起こす。`npm run build` は先に `build:web`
  （`tsc --noEmit && vite build`）を走らせ、`dist/` を `.app` に取り込む
- Tauri は `.app` を `src-tauri/target/release/bundle/macos/` に作る。`npm run build` はそれを `build/` へ
  写す（マスコットの `chatter-mascot/Build/` と同じく、成果物の場所を1段で分かるようにするため）。
  `.app` は macOS でしかできないので、Windows に持っていくときは写す元と写し方を書き直す
- `devUrl` を持つので、`cargo clippy` / `cargo test` は `dist/` が無くても通る

Dock にも ⌘Tab にも出ない。操作はメニューバーのアイコンから行う。

### 初回

メニューの「core の場所を選ぶ…」で `core/` を選ぶ。選んだ場所は
`~/Library/Application Support/tech.sukima.chatter-agent/settings.json` の `coreDir` に保存され、
次回からはアプリを起動すると自動で server を起こす。`dist/chatter-agent-server.mjs`・`dist/chatter-agent-player.mjs`・
`node_modules` のどれかが無いフォルダは、警告ダイアログを出して保存しない（動いている server の状態は変わらない）。

## メニューと状態

状態欄は「サーバー: …」の形で、次のどれかが入る。

| 状態 | 意味 |
|---|---|
| 起動中… / 停止中… | 起動・停止の処理中 |
| 起動中（pid N） | 自分が起こした server が動いている |
| 停止 | 動いていない。自分が起こした server が外から止められて、正常に終わったときも |
| 異常終了（終了コード N / シグナル N） | 自分が起こした server が予期せず終わった（終了コード 0 以外） |
| 外で動いている（pid N） | 別の手段で起こされた server がいる（次節） |
| 起動できません: 理由 | core が未設定・`dist` か `node_modules` が無い・node が見つからない／古い（24.11 以上が必要）・環境を取得できない・spawn の失敗 |

| 項目 | 動き |
|---|---|
| ミュート | チェックで `audio.mute` を書く（→「マスコットの表示と player」） |
| マスコットを表示 / マスコットを隠す | 表示状態を反転して保存し、繋ぐクライアントを切り替える |
| Android とペアリング… | ペアリング窓を前面に出す。無ければ作る（→「ペアリング窓」） |
| 設定… | 設定窓を前面に出す。無ければ作る（次節）。マスコットの右クリックや Finder からの開き直しでも同じ（→「マスコットとのやり取り」） |
| サーバーを再起動 / サーバーを起動 | 自分の server が動いていれば再起動、そうでなければ起動。外で動いている間と処理中は無効 |
| ログを開く | `server.log` を開く。無ければディレクトリを開く |
| core の場所を選ぶ… | 上記。保存したら起動（動いていれば再起動）。処理中に選び直したときは、今の処理が終わってから、選び直した core で起こし直す |
| ログイン時に起動 | 下記 |
| 終了 | player → マスコット → server の順に止めてから終わる |

## マスコットの表示と player

表示とミュートから、Mac から繋ぐクライアントを1つに決める。

| 表示 | ミュート | 繋ぐもの |
|---|---|---|
| 表示 | しない / する | Chatter Mascot（ミュートはマスコット自身が `audio.mute` を1秒ごとに読んで無音にする） |
| 非表示 | しない | `chatter-agent-player`（`node dist/chatter-agent-player.mjs`。cwd は `core/`） |
| 非表示 | する | 何も繋がない |

★ **マスコットと player を同時に繋がない。** server はフレームを全員に配り、誰か1台が ack すればキューを
消すので、両方繋ぐと二重に鳴る（→ [`protocol.md`](./protocol.md)）。Unity はロックを取らないので、順序の
保証は ChatterAgent の責任。**片方が止まったのを確かめてから、もう片方を起こす。** マスコットの終了は
pid が消えたことで判定し（10秒まで待つ。消えなければその回は打ち切り、player を起こさない）、
player は SIGTERM を1回だけ送って待つ（停止の仕組みと同じ）。マスコットには通常の quit を要求し、
**強制終了はしない**（Unity は未送信の ack を投げ切ってから自分で終わる）。

- **`.app` の探す順**: `/Applications/ChatterMascot.app` → `~/Applications/ChatterMascot.app` →
  `<core>/../chatter-mascot/Build/ChatterMascot.app`。見つからない・`open` が失敗する・pid が現れない
  ときは、探した場所を警告ダイアログとログに出し、**非表示に寄せて player に切り替える**。見つからないときだけ
  非表示を保存する（置くまで直らないので、次の起動でダイアログを繰り返さない。置いたらメニューから表示する）
- マスコットは `open -a` で、server に渡している環境を引き継いで起こす（同じ設定の場所を見る）。
  別のワークツリーのビルドも同じ bundle id なので、動いているものは全部「動いているマスコット」として扱う
- 単体で起動したマスコットには終了の UI が無い（Dock にも出ない）。終わらせるのは ChatterAgent の
  「マスコットを隠す」と「終了」だけ
- **古い発話を飛ばすのは、ChatterAgent の起動直後と、何も繋がない状態（非表示＋ミュート、player が自分で終わった後）から繋ぐときだけ。**
  マスコットには起動引数 `-speechBacklogMaxAgeMs 60000`、player には環境変数
  `CHATTER_AGENT_SPEECH_BACKLOG_MAX_AGE_MS=60000` を渡す。音量や core による player の起こし直しと、
  マスコット ⇄ player の切り替えでは渡さず、未再生の文を引き継いで鳴らす
- マスコットには、起動のたびに自分の `.app` のパスを `-chatterAgentApp <path>` で渡す（右クリックで開き直す相手に使う。
  `.app` の中で動いていない `tauri dev` のときは付けない）
- **player の音量**は `audio.volume`。1.0 でないときだけ `CHATTER_AGENT_PLAYER_ARGS=-v,<音量>,{file}`
  （`afplay` の引数列）を足す。1.0 のときは足さないので、`config.json` の `playerArgs` が生きる。
  音量が変わると player を起こし直す。core を選び直したときも起こし直す（server と同じ core を使わせる）。
  player の出力は `server.log` に追記する
- ★ **`playerCommand` を `afplay` 以外にしているときは、音量を 1.0 にする。** `-v` は `afplay` の
  引数なので、ほかのコマンドは受け付けず、無音のまま ack されて消えうる
- **XR だけで使うときは「隠す＋ミュート」にする。** 既定は表示で、`.app` が無ければ player が繋ぐので、
  XR と ack を取り合う（→ [`protocol.md`](./protocol.md)「同じルートに対して繋ぐクライアントは1台」）
- ★ **player は自動で起こし直さない。** 終わったら `[Agent]` ログに終了状態を残して捨てるだけ。
  主因は外で動いている player の `player.lock` で、起こし直すと再起動を繰り返すだけになる。
  次の切り替え（表示・ミュートの操作）で、必要なら起こし直す。終わっていた間は誰も繋いでいないので、
  そのときは古い発話を飛ばす
- 環境（server に渡す環境）が解決するまでは何も起こさない。解決したあとに決め直す

**表示状態は `settings.json`（ChatterAgent 自身のもの）の `mascotVisible`**（既定は表示）に保存する。
**保存するのはメニューとショートカットの操作と、`.app` が見つからないときだけ。** クラッシュなどで外から終わった、Finder などで外から起動された、`open` が失敗した・起動待ちを打ち切った、という変化は、
監視（2秒ごと）が**最後に実現できた状態と実態を比べて**拾い（外での終了は、マスコットが**2回続けて**見えなかったときだけ。1回の読みでは動いているマスコットを取りこぼすことがある）、メモリ上の表示状態を実態に寄せるが、
保存しない（外での終了を保存すると、ログアウトで macOS がマスコットと ChatterAgent の両方へ quit を送ったとき、
マスコットが先に終わって非表示が保存され、次のログインでマスコットが出なくなる）。
外で起動されたら player を止める。`mascot/settings.json` の変化（`mtime:size`）も同じ監視で拾い、
`audio.mute` の変更をメニューのチェックに反映する。

**終了のときは `mascotVisible` を書き換えない**（次の起動で元の状態に戻すため）。

### ショートカット

**グローバルショートカットを登録するのは ChatterAgent だけ**（マスコットは登録しないので、押しても二重には届かない）。
`audio.muteHotKey`（既定 `ctrl+opt+m`）がミュート、`ui.hideHotKey`（既定 `ctrl+opt+h`）が表示切替。
値は `mascot/settings.json` から読む（書くのも ChatterAgent の設定窓。マスコットはこのキーを読み飛ばし、警告も出さない）。設定窓の書式（`ctrl+opt+shift+cmd+<key>`）と同じ規則で解釈する。
不正な値は既定に倒す。2つが同じ組み合わせなら、表示切替は登録しない。**押したときだけ反応する。**
登録は環境が解決してから行い、失敗したら `[Agent]` ログに出す。Rust 側からだけ登録するので、capabilities は要らない。

## マスコットとのやり取り

マスコットと ChatterAgent は **ファイル**でやり取りする（server は経由しない。OS に依存しない経路）。
例外は右クリックでの設定窓の呼び出しで、これだけは macOS の再オープンを使う。

| 方向 | 経路 | 内容 |
|---|---|---|
| ChatterAgent → マスコット | `mascot/settings.json` | 設定。マスコットは1秒ごとに `mtime:size` を見て反映する。**マスコットは書かない** |
| ChatterAgent → マスコット | `mascot/requests/*.json`（依頼箱） | 一度きりの操作 |
| マスコット → ChatterAgent | `mascot/motions.json` | 再生できるモーションの一覧 |
| マスコット → ChatterAgent | `open "<起動元の .app>"`（単体起動なら `open -b tech.sukima.chatter-agent`） | 右クリックでの設定窓の呼び出し |

**書き手は ChatterAgent だけ。** マスコットは `settings.json` を読むだけなので、保存の競合（後勝ち）は起きない。

### 右クリック → 設定窓

マスコットのキャラクターを右クリックすると、マスコットが `/usr/bin/open` で ChatterAgent を開く。
ChatterAgent が起動したマスコットは、起動引数 `-chatterAgentApp` で渡された起動元の `.app` を**パスで**開く。
単体起動のマスコット（引数なし）だけが `open -b tech.sukima.chatter-agent` に頼る。
ChatterAgent は macOS の再オープン（Tauri の `RunEvent::Reopen`）で設定窓を前面に出す。Finder などから
ChatterAgent を開き直しても同じ。**ChatterAgent が起動していなければ起動するだけで、設定窓は出ない。**
macOS 限定。Windows は single-instance プラグインで同じ口にできる見込み。

### 依頼箱

`<ランタイムルート>/mascot/requests/<13桁のミリ秒>-<4桁の連番>.json`。中身は次のどちらか。

```json
{"version":1,"type":"resetWindow"}
{"version":1,"type":"playMotion","id":"<カテゴリ>/<ファイル名>"}
```

- ChatterAgent は `.json.tmp` に書いてから rename する（マスコットが書きかけを読まないため）
- マスコット（デスクトップのみ。Android ビルドには入らない）は 0.5 秒ごとに `*.json` を名前順に
  「読む → 消す → 実行」する。**起動時に残っている依頼は実行せずに捨てる**。壊れたもの・版違い・未知の
  `type` は警告して消す
- ★ **ChatterAgent はマスコットが動いていないときは依頼を置かない。** 動いていないまま置くと、
  次の起動で捨てられる依頼が溜まるだけになる
- 依頼は fire-and-forget で、結果は返さない。`playMotion` は感情モーションの再生中などで始まらないことがある

### モーション一覧

マスコットは起動時に `mascot/motions.json` を消し、モーションの読み込みが終わったら書く。

```json
{"version":1,"motions":["<カテゴリ>/<ファイル名>", ...]}
```

ファイルが無ければ読み込み中、`[]` なら空。設定窓の「モーション」節はこれを select に出し、「再生」で
`playMotion` を置く。無効にする理由の優先順は「マスコットが未起動 → 待機モーションが OFF → 読み込み中 → 空」。

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

## 設定パネル

メニューの「設定…」で開く窓。**閉じても ChatterAgent は終わらない**（終了要求を止めている。
メニューの「終了」だけが終わらせる）。窓がフォーカスを得るたびに、server の設定と `settings.json` を読み直す。
画面の言語は Rust の `lang()` が決める（OS のロケールが `ja` で始まれば日本語）。

### 項目と保存先

サーバーの設定（`GET/PATCH /v1/config`。1キーずつ PATCH する）:

| 項目 | キー | 備考 |
|---|---|---|
| 合成エンジン | `ttsEngine` | AivisSpeech は `voicevox`、Kokoro は `openai`。変えると話者一覧を取り直す |
| 音声スタイル | `ttsSpeakerId` / `kokoroVoiceId` | エンジンに応じて書き先が変わる。候補は `GET /v1/speakers` |
| 話す速さ | `ttsSpeedScale` | 0.5–2.0。つまみを離したときに送る |
| テスト音声 | （`POST /v1/tts/preview`） | 下記 |
| AI要約 | `aiSummaryEnabled` | |
| 要約エンジン | `aiSummaryBackend` | |
| 感情判定 | `emotionClassifier` | |

マスコットの設定（`mascot/settings.json`。マスコットが1秒ごとに読み直して反映する）:

| 項目 | キー |
|---|---|
| VRM | `character.vrm`（元の名前。実体は `models/mascot.vrm`） |
| 音量 | `audio.volume` |
| 大きさ（「キャラクター」節のスライダー） | `character.scale`（0.5〜2.0、刻み 0.1、既定 1.0）。マスコットが動いていなくても書ける（次の起動で効く） |
| 待機モーション | `character.idleMotion` |
| カーソルを目で追う | `character.cursorGaze` |
| まばたき | `character.blink` |
| フレームレート | `display.frameRate`（30 / 60） |
| ショートカット（ミュート） | `audio.muteHotKey` |
| ショートカット（表示切替） | `ui.hideHotKey` |

マスコットの操作（依頼箱。マスコットが動いているときだけ押せる）:

| 項目 | 動き |
|---|---|
| 位置と大きさをリセット（「キャラクター」節） | `character.scale` を消してから依頼 `resetWindow` を置く。マスコットは依頼の直前に設定を読み直し、既定の大きさ・既定の位置（`window.json` を消す）に戻す |
| モーションを確認（「モーション」節） | select と「再生」。依頼 `playMotion`（→「マスコットとのやり取り」） |

「Chatter Agent について」節（末尾）は server にもマスコットにも依存せず、常に出る。バージョン（Tauri の
`package_info()`）と、リポジトリ直下の `NOTICE` の全文（`include_str!` でビルド時に埋め込む。コピーを持たないので
食い違わない。Rust の `about()`）を出す。全文は長いので「ライセンス」の下に畳んでおく。

- 窓は縦だけリサイズできる（幅は 520 で固定）
- 環境変数で固定されたキー（`origins` が `env`）は押せず、`CHATTER_AGENT_*` で固定されている旨を注記する
- server に繋がらないと、サーバーの項目は押せず注記が出る。**3秒ごとに取り直す**
- 話者一覧が 503 の間は「取得できません」を出し、**3秒ごとに取り直す**（エンジンを切り替えた直後は、
  エンジンの起動待ちで 503 になる）
- 設定の書き込みに失敗したら、項目の下に理由を出して値を読み直す
- **テスト音声は ChatterAgent が自分で鳴らす**（マスコットの再生経路は使わない）。音量は `audio.volume`、ミュート中（`audio.mute`）は鳴らさない

### server とのやり取り（`control.rs`）

★ **server とは Rust から HTTP で話す。** WebView の `fetch` には `Origin` が付き、server は 403 にする。
この絞りは緩めない（→ [`protocol.md`](./protocol.md)「制御 API」）。

- **許可しているのは `GET/PATCH /v1/config`、`GET /v1/speakers`、`POST /v1/tts/preview`、`GET/POST /v1/pairing` だけ。**
  `POST /v1/pairing/claim` は送らない（端末が叩く口。ChatterAgent が PIN を使い切ってしまう）。
  要約のプレビューは課金されるので入れない
- 接続できないときの status は 0。応答待ちは設定の読み書きが10秒、話者一覧とテスト音声が70秒

接続先は毎回解決し直す。

| | 順 |
|---|---|
| port | `CHATTER_AGENT_PORT` > `config.json` の `port` > 8570 |
| host | `CHATTER_AGENT_HOST` > `config.json` の `host` > `127.0.0.1` |

- `config.json` は `CHATTER_AGENT_CONFIG` があればそれ、無ければランタイムルート直下
- 環境は、server に渡しているもの（ログインシェルから解決したもの。→「server に渡す環境」）と同じ
- `0.0.0.0` / `::` は `127.0.0.1` として扱い、ループバック（`localhost` / `127.x` / `::1`）はそのまま使う
- ★ **LAN の特定 IP は非対応で、繋がない。** server は相手がループバックでないと、書き込みを 404、GET を 401 にする

### `settings.json` の書き方（`mascot.rs`）

- **書けるキーは許可リスト（`MANAGED`）と、リセットでは消さない `character.scale`（`KEPT_ON_RESET`）だけ。** 書き込みの受け付けは両方、リセットは `MANAGED` だけを使う
- 管理キーだけを差し替える。**知らないキーと XR のキー（`xr.*`、`connection.*`）は残す**。`version` が無ければ 1 を入れる
- **壊れた JSON や、最上位が object でないファイルは書き戻さない**（エラーにする）
- 読み→差し替え→書きは直列にし、tmp（`settings.json.chatter-agent.tmp`）へ書いてから rename する
- ★ **フレームレートは JSON の整数で書く。** マスコットは `30.0` を既定へ倒す

### VRM

選んだファイルを `models/mascot.vrm` へコピーし、元の名前を `character.vrm` に書く（反映はマスコットの
**次の起動から**）。コピーは `.vrm` で終わらない tmp を経由する（core の `assetCatalog` が `*.vrm` を走査するため）。

ファイル選択にフィルタは付けない。`.vrm` は動的 UTI で、絞るとグレーアウトしうる
（→ [`knowledge/mascot-settings.md`](./knowledge/mascot-settings.md)「`.vrm` はシステムに UTI が無いので、ファイル選択を種別で絞らない」）。
拡張子は選んだあとに Rust で確かめる。

### ショートカット

修飾キー4つ（ctrl / opt / shift / cmd）のチェックとキーの選択で組み立てる。**キー入力の記録にはしない。**
登録済みのグローバルホットキーや既定メニューのキー割り当てに横取りされるため。規則は修飾キー1つ以上、
ミュートと表示切替の重複は不可。

### すべての設定をリセット

ネイティブの確認ダイアログのあと、マスコット側（`settings.json` の管理キー → `models/*.vrm`）→ server の順に戻す。**途中で失敗しても残りの段は進める**（失敗は最後にまとめて表示する）。

| 戻す | 戻さない |
|---|---|
| server の6キー（`ttsSpeakerId` / `kokoroVoiceId` / `ttsSpeedScale` / `aiSummaryEnabled` / `aiSummaryBackend` / `emotionClassifier`）。環境変数で固定されたキーは飛ばす | `ttsEngine` |
| `settings.json` の管理キー | キャラクターの位置（`window.json`）と大きさ（`character.scale`）。戻すのは「位置と大きさをリセット」だけ |
| `models/` の `*.vrm`（同梱モデルに戻る） | ミュート（`audio.mute`）、接続先（`connection.*`）、XR のキー |

## ペアリング窓

メニューの「Android とペアリング…」で開く専用の窓（ラベル `pairing`、360×340、リサイズ不可。
設定窓とは別の vite エントリ）。LAN の Android XR 端末にトークンを渡す PIN を出す
（契約は [`protocol.md`](./protocol.md)「ペアリング」）。

- 開くと `POST /v1/pairing` で PIN を発行し、大きく表示する。1 秒ごとに残り時間と `GET /v1/pairing` を見て、
  `paired` なら「ペアリングしました」、`expired` / `locked` / `none` なら期限切れやロックの旨を出す。
  「発行し直す」は `paired` でも出す（2 台目の PIN を出す手段になる）
- 状態の取得に失敗しても PIN は隠さない（server ではまだ有効なため）。エラーを出して取り直し続ける
- ★ **窓を閉じても PIN は取り消さない。** 期限（5 分）か、成功、失敗の上限で終わる。取り消す口は無い
- ★ **`409 not_lan` は、server が LAN に出ていないということ。** `config.json` の `host` か
  `CHATTER_AGENT_HOST` を `0.0.0.0` にして server を再起動するよう案内する（→「LAN に公開するとき」）

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

mDNS での広告（`_chatter-agent._tcp`。→ [`protocol.md`](./protocol.md)「発見（DNS-SD）」）も、同じ
「ローカルネットワーク」の許可の対象になる。**許可が無いと広告はループバックにしか出ず、端末からは見つからない。**
許可を付けたら server を再起動すること（許可より前に起動した server は、広告を LAN へ送れないまま動き続ける）。
`広告を始めました` のログは許可が無くても出る（ciao は EPERM などの送信エラーを握りつぶす）ので、
広告が LAN に届いている証拠にはならない。確かめるには Mac で `dns-sd -B _chatter-agent._tcp local.` を引き、
`if` 欄に Wi-Fi のインターフェース番号（`ifconfig -v en0` の `index`）が出ているかを見る。`1`（ループバック）だけなら届いていない。

★ **許可は署名の識別子で引かれる。ChatterAgent はバンドルごと ad-hoc 署名する**（`tauri.conf.json` の
`bundle.macOS.signingIdentity: "-"`）。こうすると識別子がバンドル ID（`tech.sukima.chatter-agent`）になり、
ビルドし直しても置き場所を変えても同じ許可が効く。外すと識別子はリンカが付ける `chatter_agent_app-<ハッシュ>` になり、
許可が最初に許可したビルドの**パス**に紐づく。別のパスのビルドは、一覧で「オン」に見えたまま黙って拒否され、
ダイアログも出ない（→ [`knowledge/agent.md`](./knowledge/agent.md)）。識別子は `codesign -dv ChatterAgent.app` の
`Identifier` で確かめる。

★ **一度「許可しない」を選ぶと、置き場所に関係なくどのビルドもダイアログ無しで拒否される**（一覧では「オフ」に見える）。
戻すには、システム設定 → プライバシーとセキュリティ → ローカルネットワーク で ChatterAgent を手でオンにする。

## 制約

- ★ **ChatterAgent がメニューの「終了」やログアウト（quit の Apple Event）以外で終わると server と player が残る。**
  `kill` の SIGTERM や強制終了がこれに当たる（マスコットも終了しない）。次に起動した ChatterAgent からは「外で動いている」に見える
  （→ [`knowledge/agent.md`](./knowledge/agent.md)）
- **Windows は未対応。** 停止が穏当でなく（server の後始末が走らない）、環境の解決もしない
