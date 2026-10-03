# chatter-agent-app/ で踏んだこと・決めた理由

基本設計・制約・コマンドは [`../agent.md`](../agent.md)。ここは**理由と実測だけ**を残す。

## `LSUIElement` だけでは Dock に出る

`Info.plist` に `LSUIElement` を入れても、起動すると Dock に出る（`lsappinfo` の `ApplicationType` が
`Foreground`）。tao（Tauri の窓まわり）は起動を終えたときに自分の activation policy を `NSApp` に
適用し、その既定が `Regular` だから（tao 0.37 の `platform_impl/macos/app_state.rs` の
`apply_activation_policy`）。

→ `setup` で `set_activation_policy(ActivationPolicy::Accessory)` を呼ぶ（`main.rs`）。これで
`ApplicationType` が `UIElement` になり、Dock にも ⌘Tab にも出ない。`LSUIElement` は外していない
（外したときの挙動は確かめていない）。

## `kill` で終わらせると server が残る

| 終わり方 | `RunEvent::Exit` | server |
|---|---|---|
| メニューの「終了」 | 走る（止めた後なので何もしない） | 止めてから終わる |
| quit の Apple Event（ログアウト、`osascript -e 'tell application id "tech.sukima.chatter-agent" to quit'`） | 走る | 止めてから終わる |
| SIGTERM（`kill`） | 走らない | **ppid=1 で残る** |
| SIGKILL（強制終了） | 走らない | **残る** |

quit の Apple Event が届くのは、tao の `applicationWillTerminate:` が `LoopDestroyed` を出し、Tauri が
それを `RunEvent::Exit` にするから。SIGTERM は既定の動作のままプロセスが死ぬので、この経路に乗らない。

残った server は、次に起動した ChatterAgent からは「外で動いている」に見える（止める・再起動するのは
自分が起こしたものだけなので、メニューからは止められない）。**手当てはしていない。** 日常の終わり方
（メニューの「終了」とログアウト）はどちらも server を止めてから終わる。SIGTERM も拾うなら、
シグナルハンドラから `app.exit` に繋ぐ。

## 設定窓を閉じても終わらないようにする

窓を全部閉じると、Tauri は終了要求（`RunEvent::ExitRequested { code: None }`）を出す。止めないと
ChatterAgent ごと終わり、server も止まる。→ `api.prevent_exit()` で止める（`main.rs`）。

メニューの「終了」は `app.exit(0)` で、code が `Some` なので止まらない。終了要求を止めても、終わらせる
経路は塞がない。

## 設定窓から server への HTTP は Rust の std で書く

- **WebView の `fetch` は使えない。** `Origin` が付き、server が 403 にする。この絞りは緩めず、
  Origin を持たない Rust から送る
- **HTTP クライアントの crate は足さない。** reqwest は macOS では未コンパイルで、足すと hyper 一式が
  乗る。std の `TcpStream` で HTTP/1.0 の要求を送り、`read_to_end` で読み切る
- server の応答は `content-length` 付きで、HTTP/1.0 の要求には chunked で返さない。だから本文の切り出しは
  ヘッダの終わりまでで済む。keep-alive / chunked / TLS が要るなら `ureq` を使う

## 同期の `#[tauri::command]` は使わない

同期のコマンドはメインスレッドで走る。HTTP やファイル操作で待つと、トレイも窓も固まる。
→ HTTP・ファイル操作・ダイアログは `async` にして `spawn_blocking` へ逃がす。

★ **`blocking_*` のダイアログをメインスレッドで呼ぶとデッドロックする。** ファイル選択も確認も
`spawn_blocking` の中で呼ぶ。

## 確認はネイティブダイアログで出す

JS の `confirm()` は wry で効かない。確認は `tauri-plugin-dialog` のネイティブダイアログ（`confirm`
コマンド）で出す。

## 画面の言語は Rust から取る

`navigator.language` は使わず、Rust の `lang()`（メニューと同じ `sys_locale`）から取る。WKWebView の
`navigator.language` はアプリのローカライズに引かれて、OS の言語と食い違いうる。

## `@tauri-apps/api` を入れない

`tauri.conf.json` の `app.withGlobalTauri` を立て、`window.__TAURI__.core.invoke` を使う。アプリ定義の
コマンドだけなら capabilities のファイルは要らない（event 系を使うと要る）。

## core の型は `import type` で引く

設定窓は core の `config.ts` の型（`ChatterAgentConfig` / `ConfigKey` / `ConfigOrigin` /
`ConfigEnvNames`）を `import type` で引く。

★ **`import { type X }` で書かないこと。** `verbatimModuleSyntax` の下では、型だけの import でも
モジュールの import が残る。Vite が `config.ts` とその先の `fs` をバンドルしに行く。

環境変数名の表（`model.ts` の `ENV_NAMES`）は、`ConfigEnvNames`（core の `SPECS` から導く型）と
`satisfies` で突き合わせる。core の名前が変わるとコンパイルが落ちる。

## `devUrl` があれば `cargo` は `dist/` を要求しない

`tauri.conf.json` の `build.devUrl` があると、`cargo clippy` / `cargo test` は `frontendDist`
（`dist/`）の存在を要求しない。CI の Rust のジョブが、フロントの事前ビルド無しで通る理由。

## マスコットの終了は `NSRunningApplication.terminate()` で頼む

`terminate()` は通常の quit の Apple Event を送るだけで、Unity はそれを `wantsToQuit` で受ける
（`Player.log` に `[Mascot] 終了要求: 未 ack=… → 通します/保留します` が出る）。保留した回の戻りは
「拒否」になるので、終わったかは pid が消えたことで見る（`clients.rs` の `quit_mascot`）。

pid で引くので、別のワークツリーのビルドを名前で取り違えない。bundle id で列挙する
（`runningApplicationsWithBundleIdentifier`）ので、どのワークツリーのビルドも「動いているマスコット」に入る。

## 終わらないスレッドから AppKit を呼ぶなら autoreleasepool で包む

AppKit の内部で autorelease されたオブジェクトは、呼んだスレッドの暗黙のプールに積まれる。
監視スレッドは終わらないので、そのプールが掃除されず、常駐中にメモリが増え続ける。`Serial` のスレッドは
ジョブのたびに終わるので掃除される。`mascot_app.rs` の `running_pids` / `request_quit` は
`objc2::rc::autoreleasepool` で包んでいる。

実測（新しいスレッドで `running_pids()` を 20 万回呼び、マスコットが動いている状態で 2 万回ごとに RSS を見た）:

- 包まない: 1 回あたり約 310 バイトずつ一定の傾きで増え、9.5MB → 72MB になった
- `objc_autoreleasePoolPush` / `Pop` で包む: 最初に約 1.4MB 増えたあとは横ばい
- 2 秒ごとの監視なら、包まないと 1 日で約 13MB 増える計算になる

## 画面のロック中は GUI の操作を自動化できない

- Orca の computer-use はメニューバーとメニューを扱えない（`capabilities` の `surfaces.menubar` /
  `menus` が false）
- System Events から状態メニューの項目に `AXPress` は送れるが、ロック中はメニューが開かない
  （`menus` が 0 個のまま）。ショートカットも同じ理由で届かない

→ メニューとショートカットの確認は、ロックを外した画面で行う。

メニューバーのアイコンは、`System Events` から `menu bar 2`（`menu bar 1` はアプリのメインメニュー）として見え、
名前でクリックできる（ターミナルにアクセシビリティ権限が要る。権限が無いと項目そのものが見えない）。
`orca computer click` は使えない（`LSUIElement` のアプリは「フォーカスされた最前面のウィンドウ」を
持てず `window_not_focused` で拒否される）。表示状態の切り替えのうち、外での
終了・起動、`audio.mute` / `audio.volume` の書き換え、ChatterAgent の起動時の決め直しは GUI を通らないので、
ロック中でも確かめられる。

## 実機確認

- 2026-10-02（macOS。AivisSpeech と Ollaya を server が spawn する構成）: `open` での起動、メニューからの
  再起動（6回）と「終了」、quit の Apple Event、外で動いている server、ログイン時に起動の ON/OFF。
  全部通った
- 2026-10-02（Android XR エミュレータ）: `host: 0.0.0.0` で起こした server に、LAN のアドレス
  （`ws://<Mac の IP>:8570`）で繋がり、口パクと感情モーションまで動いた。★ **エミュレータの接続は
  `netsimd` が Mac 自身の IP から張るので Mac の外に出ず、「ローカルネットワーク」の許可は求められない
  （システム設定の一覧にも載らない）。** 許可が ChatterAgent に付くかは、実機のヘッドセットでないと
  確かめられない
- 2026-10-03（macOS。画面ロック中。AivisSpeech と Ollaya を server が spawn する構成）: GUI を通らない経路だけ確認した。
  全部通った
  - 起動時に表示ならマスコットを `-speechBacklogMaxAgeMs 60000` 付きで起こす
  - 外での終了 → 非表示に寄せて player を起こす
  - ミュート → player を止めて誰も繋がない
  - ミュートを外す → 起動時点で 60 秒より古い発話を飛ばす
  - 音量の変更 → `-v` を変えて player を起こし直す
  - 非表示で起動したときに動いていたマスコットを終了させてから player を起こす（Unity は 0.5 秒以内に終了）
  - quit の Apple Event → player → マスコット → server の順に残らず止まる
  - 8570 に繋がるクライアントは常に1台
- 2026-10-03（macOS。画面のロックを外して手で操作）: メニューの表示切替・ミュート、⌃⌥H / ⌃⌥M、メニューの「終了」。
  全部通った。どの切り替えも、片方が止まってから、もう片方が起きた

## メニューバーのアイコンが出ないのに登録は成功しているとき

アイコンが画面に出ないのに、メニューもショートカットも動いていることがある。ステータス項目のウィンドウの
frame を測ると **x が負**（画面の左外）で `screen` も取れない。y は正しいまま（メニューバーの高さ）x だけが飛んでいる。

★ **これは Thaw / Bartender / Ice のようなメニューバー管理ツールの仕業。** あの種のツールは、隠したい
アイコンを画面外の負の座標へ移動させて隠す。macOS の「混雑して入り切らない」ではない。**アプリ側から
押し戻す手段は無い**（`autosaveName` を付けても変わらない）。ユーザーがツール側で表示に切り替える。

★ **アイコンを作った直後に frame を測っても意味が無い。** レイアウトは次の run loop で走るので、
作った直後は必ず高さ 0 が返る。測るなら少し待ってから。

## マスコットとのやり取りは、ファイルで済ませる

設定（`settings.json`）、依頼箱（`mascot/requests/`）、モーション一覧（`mascot/motions.json`）は、
**server を経由せず、OS 固有の IPC（ソケット・XPC）も使わない**。ファイルなら Windows にも持っていける。
例外は右クリックでの設定窓の呼び出し（`open` → `RunEvent::Reopen`）で、
これだけは macOS の再オープンに頼る。仕様は [`../agent.md`](../agent.md)「マスコットとのやり取り」。

- ★ **依頼は `.json.tmp` に書いてから rename する。** マスコットは 0.5 秒ごとに `*.json` を読むので、
  書きかけを拾わせない
- ★ **起動時に残っていた依頼は実行せずに捨てる。** 動いていない間に置かれた古い「リセット」や
  「再生」が、次の起動で突然走るのを防ぐ。マスコットが動いていないときに依頼を置かないのも同じ理由
- ★ **リセットは、依頼の前に ChatterAgent が `character.scale` を消し、マスコットが依頼の直前に
  設定を読み直す。** 1秒ごとの読み直しより先に依頼が来ても、消す前の大きさで窓を戻さないため
- ★ **`motions.json` は起動時に消す。** 前回の一覧が残っていると、読み込み前のマスコットに対して
  古い一覧から再生を頼めてしまう（ファイルが無い = 読み込み中、`[]` = 空と区別するため）

## `open -b` は動いている方に届くとは限らない

同じ bundle id のビルドが複数登録されている（ワークツリーごとのビルドなど）と、`open -b tech.sukima.chatter-agent`
で LaunchServices が選ぶのは動いている ChatterAgent とは限らない。別のコピーが2つ目として起動した
（実機では動いていない旧ビルドが起動した）。`.app` を**パスで** `open` すれば、その実体（動いていればそれ）に
再オープンが届いて設定窓が出る。

- ★ **だから起動元がパスを渡す。** ChatterAgent はマスコットを起こすとき自分の `.app` を `-chatterAgentApp` で渡し、
  マスコットはそれを `open` する。引数が無い単体起動のときだけ `-b` に頼る
