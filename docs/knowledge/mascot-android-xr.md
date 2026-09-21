# Android / XR で踏んだこと

基本設計・構成・コマンドは [`../mascot.md`](../mascot.md)。ここは**実装で踏んだことだけ**を残す。

## プラットフォームを絞る

★ **UniWindowController の macOS ネイティブプラグインを Android ビルドに混ぜない。** XR ローダーは
Android にだけ割り当てる（→「XR（Full Space）」）。
**Plugin Inspector で絞る手は git 参照のパッケージには効かない**（→ 下の「同梱プラグインはビルド時の
delegate で外す」）。

[#97](https://github.com/schwarz9791/chatter-agent/issues/97) で **Android（XR なし）のビルドが通っている。**
Unity 6000.3.14f1 同梱の SDK / NDK / JDK だけで足りる（外部の SDK 設定は要らない）。
初回は約6分、差分ビルドは約1分、APK は約 68MB（2026-09-13 実測）。
XR 自体は [#99](https://github.com/schwarz9791/chatter-agent/issues/99)、接続先の恒久化は
[#98](https://github.com/schwarz9791/chatter-agent/issues/98)、実機は
[#100](https://github.com/schwarz9791/chatter-agent/issues/100)。

★★ **`-buildTarget Android` はアクティブなビルドターゲットを Library に残す。** その後
`test.sh` / `build.sh` を明示無しで開くと Android のまま動き、EditMode テストが Android の
`#if` でコンパイルされたり、`build.sh` が `BuildPlayer` の中で切り替えと再インポートを待ったりする。
`test.sh` は `--` 以降で `-buildTarget OSXUniversal` を Unity へ転送して踏まないようにしている。
`build.sh` は `unity build --target StandaloneOSX`（**`unity build` は `OSXUniversal` を
ビルドターゲットとして受け付けない**——渡すと `Invalid build target "OSXUniversal"` で弾かれる）。

### Android では MToon10 を UniUnlit に差し替える

VRM の読み込み直後、`VrmStage.Adopt` が `VrmMaterialCheck.Inspect` の後に
`UnlitFallbackPolicy.AppliesTo(Application.platform)`（Android のみの許可リスト）で判定し、
真なら `UnlitFallback.Apply`（`Assets/ChatterMascot/Vrm/`）が全マテリアルを
`UniGLTF/UniUnlit` へ差し替える。理由は MToon10 の陰影計算が Android で白飛びするため
（原因は未特定 → [#110](https://github.com/schwarz9791/chatter-agent/issues/110)）。
テクスチャと色は unlit 化前に読み直しているので正しく出るが、陰影・アウトライン・
リムライトは失う（表情には影響しない）。実機で MToon10 がそのまま正しく出るなら
外す判断は [#100](https://github.com/schwarz9791/chatter-agent/issues/100)。

### Player Settings は `AndroidPlayerSettings.FixAll` が書く（1回走らせてコミット）

| 項目 | 値 | なぜ |
|---|---|---|
| アプリケーション ID | `tech.sukima.chattermascot` | ★ **Android はハイフンを許さない**（Java のパッケージ名規則）。Standalone は `tech.sukima.chatter-mascot` のまま |
| minSdk | 30 | 動いている Android XR サンプルの値 |
| `ForceInternetPermission` | オン | Unity が `INTERNET` を書く根拠 |
| `insecureHttpOption` | `AlwaysAllowed` | ★ 下記 |
| targetSdk | Automatic（触らない） | 6000.3 同梱の SDK が platforms 34 / 35 / 36 を持つので 36 に解決される。★ **37 から `ACCESS_LOCAL_NETWORK` がランタイム権限になる**が、このランタイム要求はコード化していない。37 に上がったら要る |

★ **`insecureHttpOption` は Unity 自身の門で、Android の `usesCleartextTraffic` とは別物。**
`UnityWebRequest` は既定で http を拒むが**ループバックだけは例外**。LAN のホストへ http で
繋ぐ経路（[#98](https://github.com/schwarz9791/chatter-agent/issues/98)。→ 下の「LAN 接続」）
があるので、`AlwaysAllowed` にしてそちらも通している。これが無いと**音声の GET だけ**が落ちる。

### マニフェストは静的に置かず、Gradle 生成後に注入する

`Assets/Plugins/Android/AndroidManifest.xml` は無い。`AndroidManifestPostProcessor`
（`IPostGenerateGradleAndroidProject`。`path` は unityLibrary のルートで、
`src/main/AndroidManifest.xml` を `XDocument` で編集する）が `INTERNET` と
`<application android:usesCleartextTraffic="true">` を保証する。冪等で、失敗したら
`BuildFailedException` でビルドを止める（注入漏れを成功扱いにしないため）。★ **Unity は後処理が
`BuildFailedException` を投げても APK を出力先へ書き出してから `Failed` を返す**（`usesCleartextTraffic` の
無い APK が残ることを確かめた）ので、`build-android.sh` は失敗したら成果物を消す。静的な1枚を置かないのは、
[#99](https://github.com/schwarz9791/chatter-agent/issues/99) の
XR パッケージも同じフックで同じマニフェストへ注入してくるので、「最終形を決める仕組み」を1つに保つため。

★★ **Unity 6000.3 は `INTERNET` を自分で書く（`ForceInternetPermission`）が、`usesCleartextTraffic` は
書かない。** Gradle がマニフェストを作り直したビルドで `[Build] AndroidManifest.xml: usesCleartextTraffic を追加`
が出た。後処理は保険ではなく**必須**。

確認は APK を直接読む（`aapt2` は Editor の `PlaybackEngines/AndroidPlayer/SDK/build-tools/<ver>/` にある）:

```bash
aapt2 dump xmltree Build/ChatterMascot.apk --file AndroidManifest.xml
```

★ `MacPostBuild` は plist に `XDocument` を禁じている（DOCTYPE の問題）が、`AndroidManifest.xml` に
DOCTYPE は無いので `XDocument` でよい。

### シーンに焼かれたデスクトップ限定コンポーネントはビルド時に剥がす

`AndroidSceneStripper`（`IProcessSceneWithReport`。Android のときだけ。`report == null` は
Play Mode なので何もしない）が、コンポーネントの型が属するアセンブリの asmdef が Android を
含まなければ外す。判定は `AsmdefPlatformFilter`（Unity の規則をそのまま写した純粋関数）:
`includePlatforms` が非空ならホワイトリスト → そうでなく `excludePlatforms` が非空ならブラックリスト →
どちらも空なら含む → **読めなければ残す**。型の決め打ちリストではないので、デスクトップ限定
パッケージが増えてもこのクラスは変えない（→ [`mascot-unity.md`](./mascot-unity.md)「デスクトップ限定アセンブリの
`MonoBehaviour` をシーンに置かない」）。

`Mascot.unity` で外れるのは `UniWindowController/UniWindowController`（空になった GameObject ごと）と
`ModelAnchor/ModelPlaceholder/UniWindowMoveHandle`。ログは
`[Build] Android 非対応のコンポーネントを外しました:` で始まる。

★★ **GameObject を消すのは「このパスで空にしたもの」だけ。** `Main Camera/GazeTarget` は仕様として
Transform しか持たない空オブジェクトで、初版は「空だから」と消していた（`VrmCharacter` が実行時に
作り直すので実害は無かったが、意図して置いたものを巻き込む規則は誤り）。

★ ログが出るのは Unity がシーンを実際に処理し直したときだけ。差分ビルドでは出ないことがある。

### 同梱プラグインはビルド時の delegate で外す（`PluginImporter` は書けない）

`com.kirurobo.uniwinc` は `LibUniWinC.dll` を x86 / x64 の2本同梱していて、どちらも既定で
「Any Platform」。そのまま Android をビルドすると
`Cannot include plugin '…LibUniWinC.dll'… since plugin with the same name and architecture was already added`
で落ちる。

★★ **git 参照のパッケージは読み取り専用で、`PluginImporter.SetCompatibleWithAnyPlatform` +
`SaveAndReimport` は成功したように見えて永続化されない**（自前の `.bundle` に
`NativePluginSettings.FixAll` が使っている手は効かない）。`BuildScript.BuildAndroid` は
`ExcludeDesktopWindowPluginsFromBuild()` で `PluginImporter.GetAllImporters()` を舐め、
`Packages/com.kirurobo.uniwinc/` 配下の3件（`.bundle` / x64 dll / x86 dll）に
`SetIncludeInBuildDelegate(_ => false)` を掛ける。**その1回のビルド呼び出しの間だけ**効き、
メタファイルは触らない。GUID やパスは決め打ちしない（`Library/PackageCache` のパスは
リビジョンハッシュを含む）。

### アイコン

`IconSettings.FixAll` は Android にも `IconKind.Application` で登録する。★ **`IconKind.Legacy` は
存在しない** —— `Application` を渡すと Inspector の「Legacy」枠に入る。Adaptive / Round は別の API
（`PlatformIconKind`）で、触っていない。Android 側はサイズ一覧が空でも落とさない。

### 音は Unity 内蔵オーディオのまま

`BuildAndroid` は `Disable Unity Audio` を切り替えない。コミットされている `m_DisableAudio: 0` が
Android の出荷値そのもの（→ [`mascot-speech.md`](./mascot-speech.md)「無音時にオーディオ出力デバイスを掴まない」）。
`scripts/build-android.sh` に `build-native.sh` の呼び出しも `AudioManager.asset` の trap も無いのは
変わらないが、ネイティブプラグインのバンドルの実体は `unity.sh` が Unity の手前で用意する
（Android には積まないが、実体が無いと `.bundle.meta` が孤児として捨てられるため）。
IL2CPP の作業ディレクトリ `.utmp/` は `.gitignore` 済み。

### 検証時の接続

`scripts/run-android.sh` が `adb reverse tcp:8570 tcp:${CHATTER_AGENT_PORT:-8570}` を張るので、
既定のまま実行すれば `MascotRunner` の既定 `ws://127.0.0.1:8570` のままで Mac のサーバーに届く。

★ **これはサーバーから見るとループバック接続。** トークンの経路を確かめたいときは使わないこと
（→ 下の「LAN 接続（#98）」）。

★★ **1つのランタイムルートに繋ぐクライアントは1台**（→ [`protocol.md`](../protocol.md) の
「クライアント側の責務」6）。デスクトップのマスコットが常用のサーバーに繋がっている間に Android を
確かめるなら、**別のランタイムルートで別のサーバー**を立て、`run-android.sh` に同じポートを渡す。
合成エンジンは共有でよい:

```bash
cd core
XDG_CONFIG_HOME=/tmp/cm-android CHATTER_AGENT_PORT=8571 \
  CHATTER_AGENT_TTS_URL=http://127.0.0.1:10101 npm run start:server
cd ../chatter-mascot
CHATTER_AGENT_PORT=8571 ./scripts/run-android.sh
```

発話を流すには hook と同じ形の payload を `<runtime>/spool/<message_id>.0.json` に置き、
同じ環境変数で `plugin/bin/chatter-agent-speak.mjs` を走らせる:

```json
{"session_id":"…","hook_event_name":"MessageDisplay","turn_id":"…","message_id":"…","index":0,"final":true,"delta":"…"}
```

### LAN 接続（#98）

ビルドし直さず、`settings.json` を書き換えるだけで Android から Mac の
`chatter-agent-server` に繋がる。

#### 設定ファイルの置き場と優先順位

| 環境 | 置き場所 | 解決 |
|---|---|---|
| デスクトップ | `{RuntimeDirectory}/mascot/settings.json`（`~/.config/chatter-agent/mascot/settings.json`。`window.json` と同じディレクトリ） | `SettingsLocation.Resolve` |
| Android | `Application.persistentDataPath/settings.json` | 同上 |

接続先とトークンは `connection` セクションに持つ。

```json
{ "connection": { "serverUrl": "ws://192.168.1.10:8570", "token": "…" } }
```

優先順位は **`-serverUrl`（起動引数）＞ `connection.serverUrl` ＞ `[SerializeField]` の既定**
（`MascotRunner.ResolveServerUrl`）。トークンは `connection.token` からしか読まない
（起動引数は無い）。**`-serverUrl` で接続先を上書きしたときは `connection.token` を使わない**
（別のホストへトークンを送らないため）。トークンが要る接続先は `connection` で指定する。

★★ **どちらも `Awake` で**、専用のストアを作らず**起動時に1回だけ**読む。ファイルを
書き換えても**次回の起動まで反映されない** —— 接続を1回きり捕まえる設計（`MascotRunner.ServerUrl`
の doc）を保つため。採用した出どころ（起動引数 / 設定ファイル / 既定）とトークンの有無はログ（デスクトップは
Player.log、Android は `adb logcat -s Unity`）に出る。

#### Mac 側の準備

サーバーを `host: 0.0.0.0` で起動しないと、Android からは繋がらない（既定はループバックのみ。
具体的な LAN IP で bind すると、同じ Mac の player やマスコットから繋げなくなる。→
[`protocol.md`](../protocol.md) の「セキュリティ」）。

```bash
CHATTER_AGENT_HOST=0.0.0.0 npm run start:server
```

起動ログに接続先候補（`ws://<LAN IP>:<port>`）とトークンファイルの**パス**が出る
（値そのものは出ない）。

#### `configure-android.sh`

```bash
./scripts/configure-android.sh                          # en0/en1 の IP + CHATTER_AGENT_PORT（既定 8570）から自動組み立て
./scripts/configure-android.sh ws://192.168.1.10:8570    # 接続先を明示
./scripts/configure-android.sh --no-restart              # 端末側のアプリを再起動しない
```

トークンは `${XDG_CONFIG_HOME:-$HOME/.config}/chatter-agent/server.token` から読む
（先にサーバーを起動しておくこと）。端末側の `settings.json` は `adb pull` → `connection` だけ
差し替え → `adb push` するので、共有キー（音量・ミュートなど）は消えない。

★ 別ルートの検証用サーバー（`XDG_CONFIG_HOME` / `CHATTER_AGENT_PORT` を変えて立てたもの）に
繋ぐときは、`configure-android.sh` を呼ぶときにも**同じ** `XDG_CONFIG_HOME` / `CHATTER_AGENT_PORT`
を渡すこと。揃えないとトークンファイルの場所と既定ポートがずれ、常用のサーバーの
トークン・接続先を組み立ててしまう。

★★ **`adb reverse`（`10.0.2.2` 経由を含む）は、サーバーから見るとループバック接続になる。**
ループバックはトークンを免除されるので、トークン無し・誤りのどちらでも繋がってしまい、
トークンの経路を検証したことにならない。トークンを確かめるときは端末の実 IP から
Mac の LAN IP へ接続する経路（`configure-android.sh` が書く経路）を使うこと。

#### Android で効くキーと効かないキー

`settings.json` の書き手は `MascotSettingsHost`（`Vrm/`）に一本化されていて、
「設定 → シーン」の反映経路（ミュート・音量・待機モーション・視線・瞬き）はデスクトップと
Android で共通。1秒ポーリングで外部変更も拾う。

| キー | Android で効くか |
|---|---|
| `audio.mute` / `audio.volume` | 効く |
| `display.frameRate` | **効かない。** XR ではランタイムがフレームペーシングを握り、XR が起動しなかったときはシーンの `targetFrameRate`（`[SerializeField]`）が権威 |
| `xr.scale` / `xr.distance` / `xr.azimuth` / `xr.feetBelowEye` | 効く（XR が起動したときだけ。起動時に1回だけ読む。→「XR（Full Space）」） |
| `character.idleMotion` / `character.cursorGaze` / `character.blink` | 効く（視線は `CursorProvider` が無いので自律的な漂いになる） |
| `connection.serverUrl` / `connection.token` | 効く（起動時に1回だけ） |
| `character.vrm` | **効かない。** VRM の探索は `AssetEnv.HasUserConfigDirectory` のときだけユーザー段を見るが、Android はこれが `false`（共有ファイルシステムが無い） |
| `audio.muteHotKey` / `ui.hideHotKey` | **効かない。** グローバルショートカットはデスクトップ固有のネイティブプラグイン（`StatusItemBridge`）にしか無い |

#### 繋がらないときの症状と切り分け

Android のログは `adb logcat -s Unity`。★★ **Android では 401 と「接続を拒否された」が同じ定型文で
始まる。** 見分けるのは内側の例外が付くかどうか —— 401 のときは `ClientWebSocket` の例外に
ステータスがどこにも載らないので、`トークンが無いか違います` のヒントは付かない（デスクトップで
ステータスが内側に載る環境なら付く）。

| 症状（ログ） | 原因 | 確かめ方 |
|---|---|---|
| `[Mascot] 接続エラー: Unable to connect to the remote server`（**内側の例外が付かない**）。サーバー側に `[WS] Rejected unauthorized connection: <端末の IP>` | トークンが無いか違う（`401`） | 起動ログの `[Mascot] トークン: 設定あり / 設定なし`。`connection.token` が `server.token` と一致しているか（`configure-android.sh` を同じ `XDG_CONFIG_HOME` で撃ち直す） |
| `[Mascot] 接続エラー: Unable to connect to the remote server → mono-io-layer-error (111)` | 相手のポートが開いていない。サーバーが止まっている、または `host` がループバックのまま | Mac 側の起動ログに「LAN からは繋げません（host=127.0.0.1）」が出ていないか |
| （未実測）Mac から `curl http://<LAN IP>:<port>/v1/health` は `401` が返るのに、端末からは届かない | macOS のローカルネットワーク許可が拒否されている | システム設定 → プライバシーとセキュリティ → ローカルネットワーク。**この許可は node ではなく起動元のターミナルアプリに紐づく** —— 過去に拒否していると 127.0.0.1 からは繋がるのに LAN からだけ症状が出る |
| （未実測）WS は `接続しました` まで進むが、音声の取得だけ失敗する | `insecureHttpOption` が `AlwaysAllowed` になっていない（`UnityWebRequest` だけが掛かる門） | `Edit > Project Settings > Player` の `Configuration > Insecure HTTP Option`。出荷値は `AndroidPlayerSettings.FixAll` が書く |
| （未実測）targetSdk 37 以上で全部繋がらない | `ACCESS_LOCAL_NETWORK` のランタイム許可が要る | 現状は Automatic 解決で 37 未満なので該当しない（→ 上の `AndroidPlayerSettings.FixAll` の表） |
| `[Mascot] serverUrl: 既定を使います ("ws://127.0.0.1:8570")` | 設定が読まれていない。パス違い・JSON が壊れている・`connection.serverUrl` が不正（警告が出る） | `adb shell cat /sdcard/Android/data/tech.sukima.chattermascot/files/settings.json`。`adb reverse` が張られていると既定のままでも繋がってしまい気付かない |
| `[Mascot] serverUrl: 起動引数を使います (…)` | `-serverUrl` が設定より優先されている | 起動引数を外す |

#### ★★ close フレーム無しで切れた後、Android では `Abort` しないと再接続が止まる

サーバーの `terminate()` など close ハンドシェイクを伴わない切断の後、使い終えた `ClientWebSocket` を
`Dispose()` だけで捨てると、Android では**非ループバックの相手に対して次の `ConnectAsync` が返らなくなる**。
`切断されました（close フレーム無し。…）。繋ぎ直します` を最後にログが止まり、`接続エラー` も出ない
（アプリはフリーズしていない）。**ループバック（`adb reverse`）では起きない**ので、`run-android.sh` の
経路だけで確かめていると見えない。仕組みは特定できていない（`ServicePointManager` の接続数には余裕があった）。

`SpeechClient.AbortAndDispose` が先に `Abort()` してから破棄する。加えて `ConnectAsync` には期限
（`ConnectTimeoutMs`）を付けてあり、返らない接続は `接続が N 秒以内に確立しませんでした` として
警告してバックオフに戻る —— 同じ種類の詰まりが別の原因で起きても、再接続ループが無言で止まる形にはならない。
切断を決定的に起こすには、検証用サーバーを `kill -9` する（close フレームが出ない）。

#### #97 の実機実測（2026-09-13 / Android XR エミュレータ `XR_Glasses` API 34 arm64 / `vita.vrm`）

★ **エミュレータであって実機ではない。** 実機（XREAL Aura）は
[#100](https://github.com/schwarz9791/chatter-agent/issues/100)。

| 確認したこと | 結果 |
|---|---|
| 起動 | APK が入り `[Mascot] server: ws://127.0.0.1:8570` で接続する |
| VRM | `jar:file:///…/base.apk!/assets/vita.vrm`（19,259,304 バイト）を `UnityWebRequest` で約 5.3 秒で読む。`VRM10/Universal Render Pipeline/MToon10` のマテリアル 15、expression 18。`idle_loop.vrma` も APK から。"referenced script … missing" も例外も無し |
| 探索順 | `persistentDataPath` の候補（`/storage/emulated/0/Android/data/tech.sukima.chattermascot/files/` の `models/mascot.vrm` / `animations/idle.vrma`）は 404、ユーザー設定の候補は飛ばされる（想定どおり） |
| 発話 | 接続時に未読 1 件 + 追加 2 件が届き、3 件とも ack（`seq<=3`）でキューが空になる。音は Mac のスピーカーからエミュレータ経由で聞こえる |
| 無音時の解放 | `無音が続いたのでオーディオ出力を止めました` → `オーディオ出力を掴み直しました` → 再び停止。`AudioSettings.Mobile.StopAudioOutput/StartAudioOutput` の経路が動き、発話は落ちない |
| fps / 音量 | 設定パネルが無いので既定（30 fps / 1.0） |

★ **キャラクターが白飛びする（未解決）。** 切り分けの経過と結果は
[#110](https://github.com/schwarz9791/chatter-agent/issues/110)。
対応は UniUnlit への差し替え（→ 上の「Android では MToon10 を UniUnlit に差し替える」）。

★ Unity 6 の Release プレイヤーは logcat にグラフィックス API 名を出さない。

★ **`XR_Glasses` AVD の Home Space パネルは、カメラを不透明の黒でクリアしても部屋が透けて見える。**
フレームバッファの alpha に関わらず**黒は見えない**（光学シースルーの模擬。黒 = 光が無い）。
`XR_Headset2` では同じ APK が不透明の黒いパネルになる。下の「代替案: Home Space + 2Dパネル」にあった
「パネル背景を透過できるか」はエミュレータの範囲で答えが出た —— グラスでは何もしなくても透けるが、
**暗い色は実背景に負けて薄まる**。`XR_Glasses` のパネルには `_ × [] [ ]` のタイトルバーが付く。

環境: `~/Library/Android/sdk/emulator/emulator -avd XR_Glasses` で起動、`adb` は
`~/Library/Android/sdk/platform-tools/adb`。

#### 二度デコードの実測（#97）— 据え置き

`AudioClipPlayer.Prepare` は WAV を `AudioClip` 用（`WavDecoder.Decode`）と
エンベロープ用（`LipSyncEnvelope.BuildOrWarn`）で二度デコードしている。消すには
`Decode` から `float[]` を貰う形に API を変える必要があるため、先に所要時間を測って
要否を決めた。

**測り方**（2026-09-13 / `XR_Glasses` エミュレータ / AivisSpeech 24kHz・16bit・mono / n=12）:
`Prepare` に `System.Diagnostics.Stopwatch` を一時的に仕込んだ計測用ビルドを
`./scripts/build-android.sh` で作り、1回目のデコード（`AudioClip` 生成まで含む）と
2回目のエンベロープ生成をそれぞれ計測して `Debug.Log` に1行ずつ出し、logcat から拾った。

| bytes | decode(ms) | envelope(ms) |
|---|---|---|
| 107216 | 0.76 | 0.62 |
| 602382 | 12.41 | 79.38 |
| 155344 | 0.23 | 0.21 |
| 278418 | 55.74 | 5.94 |
| 531146 | 8.53 | 0.73 |
| 154320 | 0.27 | 9.14 |
| 331380 | 4.58 | 12.73 |
| 610000 | 1.70 | 8.28 |
| 689872 | 0.80 | 26.66 |
| 346832 | 4.57 | 0.53 |
| 66662 | 0.12 | 0.09 |
| 641742 | 7.89 | 4.66 |

中央値: decode ≈ 3.1ms / envelope ≈ 5.3ms。最大: decode 55.7ms / envelope 79.4ms。
**サイズとの相関は無い**（同じ 600KB 級で 79ms と 0.7ms）。

**決定: 据え置き。** 二度目のデコードは一度目と同じオーダーで、ばらつきは実行環境の
ジッタが支配する。消しても中央値で数 ms しか変わらず、`WavDecoder` の API を変える
（`Decode` から `float[]` を返す形にする）価値が無い。

★ **エミュレータであって実機ではない。** 実機（XREAL Aura）での再測定は
[#100](https://github.com/schwarz9791/chatter-agent/issues/100)。計測コードはこの決定の後
`AudioClipPlayer.cs` から取り除いてある。

## XR（Full Space）

Android ビルドは OpenXR（`com.unity.xr.androidxr-openxr`）で Full Space に入り、キャラクターを空間に固定して立たせる
（[#99](https://github.com/schwarz9791/chatter-agent/issues/99)）。起動後は手でつまんで置き直せる
（[#121](https://github.com/schwarz9791/chatter-agent/issues/121)。→ 下「キャラを手で置き直す」）。
背景は environment blend mode を ADDITIVE にして部屋を透かす（→ 下「背景に部屋を透かす」）。
Android XR Extensions for Unity（`com.google.xr.extensions`）は入れない（[#119](https://github.com/schwarz9791/chatter-agent/issues/119)。理由も同じ節）。
実機（XREAL Aura）での見え方・視野・距離感は [#100](https://github.com/schwarz9791/chatter-agent/issues/100)。

### 空間配置の決めごと

1. **空間固定。起動時に1回だけ、頭の姿勢を基準に置く。以後 XR Origin は動かさない。**
   頭の上下の傾きも使う —— 目から足元へのずれをその傾きぶん回すので、見下ろして起動しても視界の同じ位置に
   出る（`XrPlacement.TiltByHeadPitch`。足元が頭の真下に来るとキャラが頭の方を向けないので、水平距離に下限がある）。
   キャラクターの置き直し（[#121](https://github.com/schwarz9791/chatter-agent/issues/121)。→ 下）は
   手でつまんで行う操作で、頭には追従させない。頭に追従させると、
   [#71](https://github.com/schwarz9791/chatter-agent/issues/71) で歩かせたときに相対位置が二重に動く
2. **起動時の配置が動かすのは XR Origin（位置とヨーだけ）、置き直しが動かすのは `ModelAnchor`。**
   `VrmStage.FaceCamera` がモデルをワールドの −Z へ向ける処理は**読み込み時の1回だけ**なので、
   起動時の配置でそのタイミングに `ModelAnchor` を回すと打ち消されるが、読み込みが終わった後に
   `XrGrab` がつまんで動かす分は打ち消されない。起動時はキャラクターは `ModelAnchor` の位置で −Z を
   向いたまま、**頭がその正面に来るように Origin を置く**（`XrPlacement.Solve`。不変条件は
   `XrPlacementTests`）
3. **大きさは `ModelAnchor.localScale` で変える。XR Origin は拡縮しない。** Origin を拡縮したときに
   眼間距離まで拡縮されるかはランタイム任せで、されなければ実機では「机の上の小人」ではなく
   「遠くの等身大」に見える（エミュレータの画像では判別できない）。`MeasureBounds` はワールド座標で
   測るので追従する。`FitCollider` は `UniformedLossyScale()` で割ってワールド寸法のまま当たり判定を
   保つ（つまむ判定がこの Collider を読むため。等倍のデスクトップでは割っても値は変わらない）。
   spring bone は追従しないので、読み込み時に縮尺を焼き込む（→ 下）
4. **配置は `settings.json` の `xr`（`scale` / `distance` / `azimuth` / `feetBelowEye`）で変える。**
   これが効くのは**起動時の配置だけ**。既定は「机の上のミニチュアを、正面の画面を避けた右側に」。
   Android には設定 UI が無いので、端末のファイルを書き換える（→ [`../mascot.md`](../mascot.md)）。デスクトップの
   パネルには出さないが、往復で落とさない（`connection` と同じ扱い）。**手で置き直した位置は
   再起動で戻る**（永続化は [#122](https://github.com/schwarz9791/chatter-agent/issues/122)）
5. **視線と首はカメラ＝頭を見る。** `gazeTarget` は `Main Camera` の子で、`VrmPoseAccent` の
   基準の下向き（縦）と左右の基準（`GazeAim.NeutralYawDegrees`。
   [#121](https://github.com/schwarz9791/chatter-agent/issues/121)）は目とカメラのワールド座標の差から
   出すので、カメラ＝頭になれば「ユーザーの頭を見る」になり、横へ回り込んでも首が追う。左右の基準は
   モデルの正面（VRM ルートの forward ではなく、`FaceCamera` が −Z へ向けた時点の向き）から測る。
   デスクトップはカメラが正面にあるのでほぼ 0。Android には `CursorProvider` が無いので目は漂いのまま
6. **フレームレートはコードを変えていない。** XR ではランタイムがフレームペーシングを握り、
   `Application.targetFrameRate` は効かない。XR が起動しなかったときは今までどおり `MascotRunner` の値
7. **XR かどうかは `XRGeneralSettings.Instance.Manager.activeLoader` で判定する。** `Application.platform` は
   XR でも `Android` のままなので使えない。起動していなければ `XrStage` は何もせず、#97 と同じ平面表示になる
8. **シーンは変えていない。** XR Origin と `TrackedPoseDriver` は `XrStage` が XR の起動時だけ実行時に組む。
   `ChatterMascot.Xr` は Editor と Android に限ったアセンブリで、`CursorGazeSource` と同じ「シーンに置かない注入」

★ **UniUnlit への差し替え（#110）は XR でもそのまま効く**（`UnlitFallbackPolicy` は `Application.platform` で判定する）。

### キャラを手で置き直す（[#121](https://github.com/schwarz9791/chatter-agent/issues/121)）

Hand Interaction Profile（OpenXR の `XR_EXT_hand_interaction`）の aim レイ（`pointerPosition` /
`pointerRotation`）と `pinchValue` を左右とも読む。aim レイがキャラクター（VRM の `CapsuleCollider`）に
当たった状態で**つまみに入った瞬間**だけ掴む（つまんだままレイを動かしてキャラに当てても掴まない）。
掴んだときのレイ上の距離を保ってレイの先に追従させる —— **奥行きは変えない。Mac のドラッグ移動に
相当する操作**（`XrGrab.UpdateHeld`）。つまみはヒステリシス（`XrGrabRules.IsPinching`。入り 0.9 /
抜け 0.6）。追跡を短く見失っても 0.2 秒は保持する。

離すと、足元の xz・**当たり判定の上端**から真下へ `ARPlaneManager.Raycast` し、`HorizontalUp` の
最も近い面に足元を乗せる（無ければ離した位置のまま）。続けて `ModelAnchor` のヨーを頭の方へ向ける
（`XrGrabRules.TryYawToFace`）。動かすのは常に `ModelAnchor`（起動時の配置と同じく、XR Origin には
触らない —— Origin を動かすと部屋ごと動いて見える）。離した後は揺れもの（spring bone）を静止形に
戻す（落下と向け直しは瞬間移動なので、慣性で髪などが振り回されないように。`VrmStage.ResetSpringBones`）。

★ **足元ではなく、当たり判定の上端から探す。** 足元は下ろすと天板に潜り、掴んだ点も足元の近くだと
天板より下になる。上端からならどこをつまんでも体の下の面が取れる。

★ **XR Hands（Hand Tracking Subsystem の feature・手の関節）は使わない。** エミュレータの手
（Hand tracking モード）は体の前に固定でマウスへ aim を向けるだけなので「キャラの近くでつまむ」判定が
成立しない。関節の親指–人差し指の距離も、つまんでも入りの閾値ちょうどの値で判定が揺れる。
`pinchValue` はランタイムが出すつまみの値をそのまま使う。将来 XR Hands の subsystem を使うなら:
権限が無い間はランタイムが毎フレームエラーを出し、
`Stop()` では止まらない（OpenXR のローダーがセッションの READY のたびに Start し直す）。
`SubsystemRegistration` で `HandTracking.automaticallyInitializeSubsystem = false` を立て、
許可後に `EnsureSubsystemInitialized()` する。

**権限は `HAND_TRACKING` と `SCENE_UNDERSTANDING_COARSE`。** `XrGrab` が起動時に**未許可のものだけ**
まとめて要求する（許可済みまで含めて要求すると、権限 Activity が一瞬起動して pause/resume する）。
`SCENE_UNDERSTANDING_COARSE` が許可されたら XR Origin の GameObject に `ARPlaneManager`（Horizontal）を
`AddComponent` する（`RequireComponent(XROrigin)`。別の GameObject に付けると XROrigin が勝手に生える）。
拒否されたら何もしない（#99 の配置のまま。権限エラーは出続けない）。

★ **`HAND_TRACKING` のマニフェスト宣言は自前で足す**（`AndroidManifestPostProcessor`）。Hand Interaction
Profile も同じ権限を要る（Android XR パッケージの doc）が、パッケージ側は Hand Tracking Subsystem の
feature が有効なときにしかマニフェストへ書かない。

★ `android.hardware.xr.input.hand_tracking` の `uses-feature` が **`required="true"`** で入る
（`XR_EXT_hand_interaction` からパッケージが注入。任意にする口は internal）。影響は Play ストアの
端末フィルタだけで、`adb install` は通る。

★ `SCENE_UNDERSTANDING_FINE` は入らない（`ARRaycastManager` は使わず `ARPlaneManager.Raycast` で
足りる。Raycast feature を有効にすると FINE が入る）。

★ `InverseTransformRay` は core-utils と ARFoundation の拡張が衝突する（CS0121）。
`InverseTransformPoint` / `InverseTransformDirection` で組む。

★ `Physics.autoSyncTransforms` はオフなので、掴む判定の `Collider.Raycast` の前に
`Physics.SyncTransforms()` を呼ぶ（直前のフレームでキャラを動かしていると見ない）。

置いた位置は残らない（再起動で #99 の配置に戻る。永続化は
[#122](https://github.com/schwarz9791/chatter-agent/issues/122)）。

### 背景に部屋を透かす（environment blend mode。[#119](https://github.com/schwarz9791/chatter-agent/issues/119)）

**グラスでは environment blend mode を ADDITIVE にする。** 描かなかった所（カメラの背景はアルファ 0 の黒）から
部屋が見える。自前の OpenXR feature（`XrAdditiveBlendFeature`）が `OnEnvironmentBlendModeChange` で ADDITIVE を
要求する（呼ばれるのはセッションの準備時だけ）。ADDITIVE を持たないランタイム（ヘッドセット）では要求しても既定のまま。

★★ **グラスのランタイムは OPAQUE / ADDITIVE しか持たず、既定は OPAQUE。** `XR_Glasses` のログに
`Available Environment Blend Modes: (2)` → `XR_ENVIRONMENT_BLEND_MODE_OPAQUE (Selected)` /
`XR_ENVIRONMENT_BLEND_MODE_ADDITIVE` と出る。OPAQUE のままだと背景は黒く、エミュレータの減光
（Environment Visibility）のスライダーも動かせない。

★ **AR Camera（`ARCameraFeature` + `ARCameraManager`。パッケージの「パススルー」）では代わりにならない。**
あちらは ALPHA_BLEND を要求するが、グラスには無いので OPAQUE に戻される。

★ **Extensions は要らない。** Extensions の Environment Blend Mode 機能は 1.3.0 で削除され、「Unity OpenXR
Android XR の AR Camera を使え」とある。Extensions の Passthrough は「メッシュ形の穴」で、背景全体ではない。
さらに 1.3.1 はマニフェストに大文字の `android.software.xr.api.SPATIAL`（`required="true"`）を混ぜる
（`androidxr-openxr` 1.4.1 が直したのと同じバグ。1.3.2 で修正）。

★ 加算合成なので、キャラクターは暗い所ほど透けて見える。光学シースルーのグラスの見え方そのもの
（実機での見え方は #100）。

★ **自前の OpenXR feature は、設定アセットに登録されるまで `GetFeature<T>()` で見つからない。** batchmode の
`FixAll` は Editor の UI を開かないので、先に `FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android)` を呼ぶ。

### ★★ XR Origin のトラッキング原点が切り替わる前に、頭の姿勢を読まない

`XROrigin` に `Device` を要求しても、切り替わるのは有効化の数フレーム後。その前はランタイム既定の原点
（床基準）の値が返り、切り替わった瞬間に原点が頭へ移る。**切り替え前の値で置くと、キャラクターが
頭の上へ外れて画面に何も映らない。エラーも警告も出ない。**

実測（2026-09-17 / `XR_Glasses`）: 配置の瞬間は `CurrentTrackingOriginMode=Unknown`・頭 `(0, 1.60, 0)`、
1秒後には `Device`・頭 `(0, 0, 0)`。`XrStage` は `CurrentTrackingOriginMode` が `Device` になってから
1フレーム置いて読む。切り替えは期限なしで待ち、期限は切り替え後の頭の追跡待ちにだけかける
（期限で切り替え前に置くと同じ外れ方をする）。

### ★★ `SetParent(parent, false)` はローカル姿勢をゼロにしない

`worldPositionStays: false` は「ワールドを保たない」だけで、ローカルの値はそのまま引き継ぐ。
`Main Camera` を Camera Offset の下へ付け替えると、**シーンに置いたデスクトップ用の位置 `(0, 0, -4)` が
ローカルに残る**。付け替えたら `SetLocalPositionAndRotation(zero, identity)` で明示的に消す。

### ★ 頭の姿勢はカメラの transform ではなく、追跡状態を確かめたデバイスから読む

`TrackedPoseDriver` がカメラへ書き込むのは Update / 描画直前。追跡状態になったフレームにカメラの
transform を読むと、まだ書き込まれていない値を拾う。`XrStage` は `InputDevices.GetDeviceAtXRNode(CenterEye)`
から、追跡状態と `centerEyePosition` / `centerEyeRotation` を同じデバイスで読む。

### ★★ `ModelAnchor` を拡縮すると、髪が横に流れたまま固まる（spring bone は拡縮に追従しない）

UniVRM の spring bone は、**コライダーの半径は毎フレーム `lossyScale` を掛けるのに、骨の当たり半径
（`m_jointRadius`）・剛性・重力は掛けない。** 骨の長さは初期化時の `lossyScale` で測るが、読み込み時の
初期化は `ModelAnchor` の下に入る前（等倍）に済んでいる。0.18 倍にすると、小さくなった頭のコライダーに
等身大の太さの髪が押し出され、**左の後ろ上の髪が横に流れた形で固まった**（2026-09-17 / `XR_Glasses`）。

`VrmStage.BakeSpringBoneScale` が、`ModelAnchor` が等倍でないときだけ、全ジョイントの当たり半径・剛性・
重力に縮尺を掛けてから `ReconstructSpringBone()` で作り直す（骨の長さもここで測り直される）。
剛性と重力を掛けるのは、骨が縮尺倍に短くなっても見た目の角速度を変えないため。

★ **`BlittableModelLevel.SupportsScalingAtRuntime` は使わない。** 剛性と重力に拡縮を掛けるフラグだが、
`SetModelLevel` で渡した値は結合バッファの作り直し（spring bone の登録や作り直しのたび）で既定に戻る。
しかも骨の当たり半径には効かない。

★ **拡縮は VRM の読み込みより前に済ませること。** `XrStage` は `AfterSceneLoad` で拡縮し、読み込みは
`VrmStage.Start` から始まるので間に合う。

### ★ XR Origin に `HideFlags.HideAndDontSave` を付けない

`FindFirstObjectByType` から見えなくなる（→「`HideFlags.HideAndDontSave` のオブジェクトは
`FindFirstObjectByType` から見えない」）。AR Foundation の各 Manager など、`XROrigin` を探しに来る側が
見つけられなくなる。

### ★★ グラスの表示視野は狭い — 描けているのに視野の縁で切れる

`XR_Glasses` の画面は、グラスの表示視野の外が黒く切り落とされる。**起動時の正面から右 30° に置くと、
キャラクターの右半分が視野の縁で切れ、ミニチュアだと頭の飾りしか見えなかった**（ログ上は正しく配置され、
描画もされている）。既定の方位と足元の深さは、起動時の正面を向いたまま全身が視野に収まる範囲に留めてある。
実機の視野角での調整は #100。

### エミュレータでの実測（2026-09-17）

★ **公式ドキュメントは「Android XR Emulator は Unity / OpenXR アプリに対応しない」と書いているが、
システムイメージには OpenXR ランタイムが入っていて、Full Space で動く**
（`/system/etc/openxr/1/active_runtime.json`、`libopenxr.google.so`）。

| AVD（システムイメージ） | 結果 |
|---|---|
| `XR_Glasses`（Google Play XR Preview API v4） | `xrDesktopMode=full-space-unmanaged` で起動し、セッションは `FOCUSED` まで進む。空間固定・縮尺（0.18。髪は spring bone への焼き込みで XR 化前と同じ形）・待機モーション・発話 → ack まで通る |
| `XR_Headset2`（Google Play XR API v1） | アプリは `READY` まで進むが、`com.android.systemui` が `Buffer processing hung up due to stuck fence. Indicates GPU hang` で ANR する。**使わない** |

- スワップチェーンはテクスチャ配列が `XR_ERROR_FEATURE_UNSUPPORTED` で一度失敗し、配列なしに落ちて描ける
- 背景は黒く、部屋は見えなかった。原因は environment blend mode が OPAQUE のままだったこと（→「背景に部屋を透かす」。2026-09-19 に ADDITIVE にして見えるようになった）
- `adb exec-out screencap -p` で撮ると、グラスの表示視野の外は黒く切り落とされて写る
- エミュレータのウィンドウでは、視野の中央でもキャラクターがやや縦につぶれて見える。`screencap` の画像（正方形）では比率は自然で、ディスプレイ（1920×1200）と片目の推奨描画サイズ（2560×2558）の縦横比が違う。実機での比率は #100
- 新しい APK の初回起動は、Home Space のときと同じく 30 秒以上白い

つまんで置き直す確認（2026-09-19 / `XR_Glasses` /
[#121](https://github.com/schwarz9791/chatter-agent/issues/121)）:

- 権限ダイアログは Full Space でも出る。`HAND_TRACKING` と `SCENE_UNDERSTANDING_COARSE` は
  1つのダイアログ（「目・顔・体の追跡とエリアのスキャン」）にまとまり、1回の Allow で両方許可される。
  2回目の表示では「Don't allow」が「今後表示しない」になる
- 平面はエミュレータでも来る（`HorizontalUp` が4枚）。いちばん低い面（床）は目の高さからかなり下にあり、
  そこへ落とすとグラスの視野の外に出る（下を向けば見える）
- Hand tracking モードでは、aim がマウスを追い、クリック／ドラッグで `pinchValue` が 0 → 1。右手のみ
- ADDITIVE にすると部屋（シミュレートされた室内）が見え、目のアイコンのスライダー（Environment Visibility）で
  濃さを変えられる。離したキャラが机（`plane=-0.39`）と床（`plane=-0.87`）のどちらにも乗ることを目視で確かめた
- 掴んで動かせる／何も無いところでは掴まない／離すと平面に乗ってこちらを向く／見下ろすと顔が
  上を向いてこちらを見る／横へドラッグすると（体は離すまで向きを変えない）首がこちらへ回る、を確認した
- 権限を拒否（今後表示しない）しても落ちず、#99 の配置のまま。権限エラーは出続けない

### ビルド設定（`AndroidPlayerSettings.FixAll`）

| 項目 | 値 | なぜ |
|---|---|---|
| XR Plug-in Management | **Android にだけ** OpenXR ローダー。feature は Android XR Support / Hand Interaction Profile / Android XR: Session / Android XR: Planes と、自前の Chatter Mascot: Additive Blend の5つ | Standalone に割り当てないので macOS ビルドは変わらない。Session は Planes の Project Validation が要求するので有効化する（有効にすると、パッケージが `OpenXRLifeCycleFeature` も連動して有効にする） |
| Graphics API（Android） | **Vulkan 単独** | URP で Android XR を使うときの必須設定 |
| `Mobile_Renderer` の Post Processing | **無効**（`postProcessData` を外す） | Project Validation の error。`PC_Renderer` は触らない |

★ **`Mobile_RPAsset` の `m_PrefilterXRKeywords` は、XR を有効にしたビルドで URP が `1 → 0` に書き換える。**
戻さないこと（XR 用のシェーダーバリアントを削らせないための値）。

★ **Unity を回すと追跡ファイルが汚れる。** `Assets/XR/Settings/OpenXR Package Settings.asset` は
Editor に入っているモジュール（ここでは Web）の枠が生える。
我々のものではないのでコミットしないこと。

★ **`Keys` は Editor にインストールされている Build Support モジュールと一対一で対応する。**
`01`=Standalone（macOS の Editor 本体）/ `07`=Android / `0d`=WebGL。実測したマシンの
`unity editors` は `Platforms: Android, Android SDK & NDK Tools, OpenJDK, Web` を返し、
`Keys` は `01000000070000000d000000` だった（2026-09-21 / Unity 6000.3.14f1）。

★ **全 `BuildTargetGroup` を舐めているのではない。** 舐めているなら iOS(4) や WSA(14) も
生えるはずで、生えていない。生えるのはモジュールが入っている枠だけ ——
`docs/mascot.md` が要求するのは Mac / Android Build Support だけなので、Web を
入れていない構成が正。この枠は出荷物ではなく、コミットしない理由もそこにある。

★ **切り分けた範囲では、引き金は `test.sh` × `Library/` が無いとき。** `run.sh` では出ない
（`-buildTarget OSXUniversal` を付けても出ない）。ビルド経路は確かめていない。`Library/` が
要るのは、リフレッシュが `SessionState`（実体は `Library/` の中）で「Editor を開いた最初の1回
だけ」に制限されているため。新規作成のときだけ `fileID` がランダムに振られる（実測3回とも別の値）。

★ **一度コミットすると、そのマシンでは差分が止まる。** 既存のエントリは作り直されず
再利用されるため（実測: 冷えた `Library/` で2サイクル、ファイルのハッシュが不変）。
**だからといってコミットしないこと** —— 止まるのは同じモジュール構成のマシンだけで、
構成が違う人には出所不明の差分として残る。

★ **消したければ Web モジュールを外す。** Unity Hub にも `unity` CLI にもモジュール削除の
機能は無く、`PlaybackEngines/WebGLSupport` を手で消して `modules.json` の該当エントリを
`"selected": false` にする必要がある（非公式な手順。戻すには再ダウンロード）。

- **失敗したビルドは後始末をしない** —— XR Simulation の設定は `Assets/XR/Temp/` へ退避されたきり
  元の場所から消え（追跡ファイルの**削除**として出る）、`ProjectSettings.asset` の
  `preloadedAssets` には XR の項目が足されたまま残る。退避先の `Assets/XR/Temp/` は
  `.meta` ごと `.gitignore` 済みだが、**消えた側と `ProjectSettings.asset` は追跡ファイルなので
  人が戻すしかない**。★★ **戻すときは退避先を先に消すこと。** 残したまま次に Unity を回すと
  同じ GUID のアセットが2箇所にあることになり、**Unity が原本の GUID を振り直す**
  （`.meta` の差分として出る。参照している側が壊れる）。退避先は ignore 済みで `git status` に
  出ないので、忘れやすい ——
  `unity_build_player`（`scripts/unity.sh`）は失敗時に `Assets/XR/Temp/` の有無を見て
  `git checkout` の手順を画面に出すだけで、自動では戻さない
  （編集の途中でビルドが失敗したときに、その編集ごと戻してしまわないため）

★ **マニフェストの XR まわりはパッケージが注入する**（`XR_ACTIVITY_START_MODE_FULL_SPACE_UNMANAGED`、
`android.software.xr.api.openxr` / `android.software.xr.api.spatial`、`android.hardware.vulkan.version`、
Hand Interaction Profile からの `android.hardware.xr.input.hand_tracking` の `uses-feature`
[`required="true"`]）。手で書かない。`AndroidManifestPostProcessor` は **`HAND_TRACKING` 権限だけは
自前で足す**（パッケージが書くのは Hand Tracking Subsystem の feature を有効にしたときだけ。→
「キャラを手で置き直す」）。`INTERNET` / `usesCleartextTraffic` とは共存する。確かめるときは
`aapt2 dump xmltree`（→「マニフェストは静的に置かず、Gradle 生成後に注入する」）。

★ **Project Validation の残りは `FixAll` が `[Build]` で出す。** 公開 API が無いので
`BuildValidator.GetCurrentValidationIssues` を reflection で呼んでいる。

## Android の必須設定はなぜその形か

`XR_ACTIVITY_START_MODE` の値が **`XR_ACTIVITY_START_MODE_FULL_SPACE_UNMANAGED`** なのは、
`MANAGED` が Jetpack XR 専用だから。OpenXR アプリ（本プロジェクト）は `UNMANAGED` を使う。

`uses-feature` は **`android.software.xr.api.openxr`**（`android.software.xr.immersive` ではない）。
どちらも Unity 側で手書きしない（→「マニフェストは静的に置かず、Gradle 生成後に注入する」）。

## なぜ Unity なのか（Jetpack XR ではなく）

Jetpack XR の SceneCore では **VRM を本来の見た目で扱えない**。

| | SceneCore 1.0.0-beta02 時点 |
|---|---|
| `VRMC_vrm` / `VRMC_materials_mtoon` / `VRMC_springBone` | いずれも**非対応**（[対応 glTF 拡張の公式リスト](https://developer.android.com/develop/xr/jetpack-xr-sdk/add-3d-models#gltf-extensions)） |
| モーフターゲット操作 API | `morph` / `blendShape` / `setWeight` が**全ソースで0件** |
| ボーン操作 | `GltfModelNodeFeature.localPose` / `localScale` に setter あり |
| アニメーション制御 | `pauseAnimation()` / `seekAnimation(t)` / `setAnimationSpeed()` |

VRM の表情（`happy` / `blink` / `aa` など）はモーフターゲット実装なので、SceneCore では直接操作できない。
**UniVRM が expressions / spring bone / lookAt をすべて備える唯一のルート**であり、UniVRM は Unity 前提。

## なぜ Full Space なのか

> **Home Space** — It does not support spatial panels, **3D models**, or an app's spatial environments.
> **Full Space** — One app runs at a time, with no space boundaries. **All other apps are hidden.**
>
> — [Design Foundations | Android XR](https://developer.android.com/design/ui/xr/guides/foundations)

Unity / OpenXR / WebXR アプリは[そもそも Full Space でしか動かない](https://developer.android.com/design/ui/xr/guides/openxr)ので選択の余地はない。

ターゲットの **XREAL Aura は光学シースルー**なので、Full Space が隠すのは「他の Android アプリ」だけで、
レンズ越しに見える物理的な Mac の画面は実光として残る。**この一点が本プロジェクトの成立根拠**。

## 代替案: Home Space + 2Dパネル（優先度低）

「本当にそこにいる」感を最優先するなら Full Space 案を採る。ただし他の Android アプリと
並べたくなった場合の逃げ道として記録しておく。

Unity を **XR機能なしの通常 Android アプリ**としてビルドすれば Home Space の2Dパネルとして動き、
Android版 Claude アプリ等と並べられる。引き換えに空間的な実在感は失われる（#97 の APK がまさにこの形）。
パネルには `_ × [] [ ]` のタイトルバーが付く。実機での見え方は [#100](https://github.com/schwarz9791/chatter-agent/issues/100)。

## エミュレータでは測れないもの

Android XR Emulator で確認できるのは Full Space での表示・空間固定の配置・アニメーション・発話まで。
**確認できないもの**: 実際のフレームレート、実機の視野角での見え方、ハンドトラッキング精度、
背景の部屋（Full Space では黒い。→「背景に部屋を透かす」で ADDITIVE にするまでは）。

## 参考: `~/dev/android-xr-test`

Jetpack XR（Unity ではない）の動く Gradle 構成。Unity 採用なら直接の再利用はほぼないが、
**Android XR まわりのバージョン組み合わせと既知の罠**が参考になる。

- `implementation(extensions-xr)` にすると実行時クラッシュする。**`compileOnly` にすること**
- **`PlanarEmbeddedSubspace` は埋め込み領域が不透明な板として描画される**
- **glTF は非同期ロードでロード完了コールバックがない**
- glTF 本来のマテリアルは環境光に依存して暗く沈む

## ネットワークまわりの根拠と未着手（#98 / #100）

見落としやすいところ。


- **`ACCESS_LOCAL_NETWORK` はランタイム権限**で、**targetSdk 37 以降で必須**。ローカルアドレスへの
  TCP 接続・mDNS・`.local` 解決が対象で、**`UnityWebRequest` / `ClientWebSocket` のようなライブラリ経由の
  通信も含む** — [Local network permission](https://developer.android.com/privacy-and-security/local-network-permission)。
  ★ 今の targetSdk は Automatic 解決で 37 未満なので該当しない。このランタイム要求はまだコード化していない
  ——37 に上がったら要る（→ [`../mascot.md`](../mascot.md)「Player Settings は
  `AndroidPlayerSettings.FixAll` が書く」）
- **`ws://`（非TLS）を使うなら cleartext 許可が必要**。Android 9 以降デフォルト無効 —
  [Network Security Configuration](https://developer.android.com/privacy-and-security/security-config)。
  非 XR の Android（[#97](https://github.com/schwarz9791/chatter-agent/issues/97)）はすでに
  `AndroidManifestPostProcessor` と `insecureHttpOption` で満たしている
- 接続先の手動入力は**すでに入っている**（[#98](https://github.com/schwarz9791/chatter-agent/issues/98)。
  `settings.json` の `connection.serverUrl` / `configure-android.sh` — 手順は
  [`../mascot.md`](../mascot.md)「LAN 接続」）。後から
  `NsdManager`（`android.net.nsd`）で mDNS 検出を足すのは未着手

★ **エンジンの `--host 0.0.0.0 --cors_policy_mode all` は不要になった**（#29）。
叩くのは同じ Mac 上の `chatter-agent-server` だけ。

[公式のプロジェクトセットアップ手順](https://developer.android.com/develop/xr/unity/setup)に従うこと。
