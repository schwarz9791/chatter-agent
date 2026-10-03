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

## 実機確認

- 2026-10-02（macOS。AivisSpeech と Ollaya を server が spawn する構成）: `open` での起動、メニューからの
  再起動（6回）と「終了」、quit の Apple Event、外で動いている server、ログイン時に起動の ON/OFF。
  全部通った
- 2026-10-02（Android XR エミュレータ）: `host: 0.0.0.0` で起こした server に、LAN のアドレス
  （`ws://<Mac の IP>:8570`）で繋がり、口パクと感情モーションまで動いた。★ **エミュレータの接続は
  `netsimd` が Mac 自身の IP から張るので Mac の外に出ず、「ローカルネットワーク」の許可は求められない
  （システム設定の一覧にも載らない）。** 許可が ChatterAgent に付くかは、実機のヘッドセットでないと
  確かめられない
