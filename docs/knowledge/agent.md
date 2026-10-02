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

## 実機確認

- 2026-10-02（macOS。AivisSpeech と Ollaya を server が spawn する構成）: `open` での起動、メニューからの
  再起動（6回）と「終了」、quit の Apple Event、外で動いている server、ログイン時に起動の ON/OFF。
  全部通った
- 2026-10-02（Android XR エミュレータ）: `host: 0.0.0.0` で起こした server に、LAN のアドレス
  （`ws://<Mac の IP>:8570`）で繋がり、口パクと感情モーションまで動いた。★ **エミュレータの接続は
  `netsimd` が Mac 自身の IP から張るので Mac の外に出ず、「ローカルネットワーク」の許可は求められない
  （システム設定の一覧にも載らない）。** 許可が ChatterAgent に付くかは、実機のヘッドセットでないと
  確かめられない
