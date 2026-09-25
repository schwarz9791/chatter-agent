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

### 端末に1行出す（Toast）は JNI 直呼び。`CharSequence` に C# の `string` をそのまま渡せる

`Ui/DeviceToast` が `android.widget.Toast` を `AndroidJavaClass` 経由で叩いている。リポジトリで
唯一の JNI 呼び出し。

★ **Java 側の宣言は `makeText(Context, CharSequence, int)` で、Unity が C# の `System.String` から
導出する signature は `Ljava/lang/String;`。それでも通る。** Unity の `AndroidJNIHelper.GetMethodID`
が、導出した signature で引けなかったときに**リフレクションで互換メソッドを探しに行く**ため。
世に出回る Unity の Toast 例が軒並み `new AndroidJavaObject("java.lang.String", message)` で
包んでいるが、**包まなくてよい**（エミュレータ `XR_Glasses` / API 36 で表示を確認。2026-09-21）。

★★ **ただし「静かに失敗しうる呼び出し」であることは変わらない。** メソッド解決に失敗しても
例外は `Debug.LogWarning` に落ちるので、**端末上では「何も出ない」としか見えない。** signature の
読みだけでは可否を決められないので、**実機で一度も鳴らしていない JNI 呼び出しを足さないこと。**

★ 折り返しは**文面のリテラルに `\n` を書く。** 「。」で機械的に折る形にすると、切りたくない文まで
巻き込む——切る位置は文面ごとに違う。

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
| `audio.mute` / `audio.volume` | 効く。XR は設定パネルの「ミュート」（`SettingKeys.Mute`）からも切り替えられる。デスクトップはメニューバーとショートカットで操作し、パネルには出さない |
| `display.frameRate` | **効かない。** XR ではランタイムがフレームペーシングを握り、XR が起動しなかったときはシーンの `targetFrameRate`（`[SerializeField]`）が権威 |
| `xr.distance` / `xr.azimuth` / `xr.feetBelowEye` | 効く（XR が起動したときだけ。起動時に1回だけ読む。→「XR（Full Space）」） |
| `xr.height` | 効く（XR が起動したときだけ）。他の `xr.*` と違い**設定パネルの「大きさ」からその場で変えられる**——起動時の読み込みだけに限らない |
| `character.idleMotion` / `character.cursorGaze` / `character.blink` | 効く（視線は手を追跡できている間だけ追従し、それ以外は自律的な漂いになる） |
| `character.walk`（既定 `true`） | 効く。設定パネルの「歩く」からその場で切り替えられる。デスクトップでは何もしない（歩かないため） |
| `connection.serverUrl` / `connection.token` | 効く（起動時に1回だけ） |
| `connection.assetSync` | 効く（設定パネルの「モデルとモーションを同期」からも変えられるが、**次回の起動から**——読むのは起動時の1回だけ） |
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

## 素材配布（#117）

### ★★ `adb push` で作らせたディレクトリはアプリから読めない

`adb push` は途中のディレクトリを `shell` 所有の `drwxrws---` で作る。**アプリの uid はそこを
辿れないので、置いたファイルは走査に一切出てこない**（エラーも警告も出ず、数が合わないだけ）。
同期（#117）が自分で作ったディレクトリへ push するぶんには起きない —— **新しいカテゴリの
ディレクトリを手で作るときだけ踏む。**

```bash
$ADB shell chmod -R 777 $D/animations   # push でディレクトリごと作ったら要る
```

### `.part` は「切れた」だけでは消さない

`AssetSyncClient.FetchOneAsync` は `synced/.parts/<sha256>.part` に書きながらモデル・モーションを
取得する。Range で続きから取れる作りにしてあるのは、この経路が細い回線を前提にしていて
**途中で切られるのが普通に起きる**ため。

消してよいのは「中身が信用できないと分かったとき」だけ——応答コードが期待（新規取得なら `200`、
続きからなら `206`）とも「応答そのものが無い」とも違うときに限る。応答が無い・期待どおりの応答
だったときは**残す**。一律に消すと、切られるたびに毎回ゼロからやり直しになり、再開できる作りに
した意味が消える。

### `File.Replace` は Android / IL2CPP でも動く

揃った `.part` を本来の場所へ移すとき、宛先が既にあれば `File.Replace` を使う。`File.Move` に
overwrite 付きの多重定義が無い（`ProjectSettings` の `apiCompatibilityLevel: 6` ＝ .NET Standard
**2.0**。2.1 ではないので3引数の `Move` はコンパイルが通らない）ためで、**消してから書く形にしては
いけない** —— 同期は `Awake`、モデルの読み込みは `Start` から走るので**両者が並走する**。隙間に
読んだ側がファイルを見失うと、同梱のモデルに落ちる。

`File.Replace` が Mono / IL2CPP の Android で動くかは資料が見つからなかったので実測した。
Android XR エミュレータ（`XR_Glasses`、API 36）で、同期済みの `.vrma` にバイトを足して
ハッシュを変えてから起動 → `取得 1/1 件`、ファイルは正しいサイズへ戻り、例外も警告も出なかった
（2026-09-21）。

### 完走した `.part` を手で作って確かめる

`.part` が `entry.Size` ぶん以上あるときに HTTP を叩かない手当て（「最後の1バイトと rename の
間で落ちた」状態の救済）は、**HTTP とファイルシステムの両方が要るので EditMode では固定できない。**
手で作るなら、同期済みの本体をそのまま `.part` 名へコピーして本体を消す:

```bash
D=/sdcard/Android/data/tech.sukima.chattermascot/files/synced
SHA=$(shasum -a 256 ~/.config/chatter-agent/animations/happy/<名前>.vrma | cut -d' ' -f1)
adb shell cp $D/animations/happy/<名前>.vrma $D/.parts/$SHA.part
adb shell chmod 666 $D/.parts/$SHA.part
adb shell rm $D/animations/happy/<名前>.vrma
```

手当てが効いていれば `取得 1/1 件` で本体が戻り、**`HTTP 416` のログ行が1度も出ない**
（＝ HTTP を叩いていない）。効いていなければ `取得に失敗しました (HTTP 416)` が出て、
**完全に正しい `.part` が消える**（2026-09-21 / Android XR エミュレータ `XR_Glasses` API 36 で
前後とも再現）。

★★ **`chmod 666` を省かないこと。** `adb shell cp` / `adb push` で置いたファイルは所有者が
`shell` になり、**アプリから開けない。** そのときの症状は

```
[AssetSync] 同期が異常終了しました: Failed to create file .../.parts/<sha>.part
```

で、`DownloadHandlerFile` がコンストラクタで投げている。これは `SyncAsync` のいちばん外側の
`catch` に落ちるので **`Failed` が上がらず端末に何も出ない**（ログだけ）。**手で置いたファイルの
権限を疑う前に、同期のロジックを疑って時間を溶かしやすい。**

`adb root` は素のエミュレータイメージでは通らず、`run-as` はリリースビルドが debuggable では
ないので使えない。`chmod` が一番手軽。実運用では `synced/` の中身をアプリ自身が作るので、
この権限の問題は**手で置いたときにしか起きない**。

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
   spring bone は追従しないので、読み込み時に縮尺を焼き込む（→ 下）。**実寸（cm）は読み込み直後、
   モーション（VRMA）が乗る前のボーン bounds から1回だけ測る**（`VrmStage.RealHeightCm`）——ポーズで
   身長が動いて見えると「大きさ」の段がぐらつく。余白は頭頂側の分だけ数える
   （`VrmBounds.RealHeightCm`。`OfBones` の余白は上下同じだが、下側は接地面の推定で身長に当たらない
   ので、同じ余白を1回引けば頭頂側の余白＝髪の分だけが残る）
4. **設定パネルの「大きさ」（`xr.height`、cm）はその場で変わる。** `VrmStage.Rescale` が
   `localScale` の差し替えと spring bone の焼き直しを1経路にまとめている——**焼き込みは乗算で
   積み重なる**ので、渡すのは絶対値ではなく**変化の比**（新縮尺 ÷ 旧縮尺）。起動時の初期化
   （legacy scale からの見積もり、または実寸が分かってからの合わせ直し）もこの経路を通る
5. **配置（`distance` / `azimuth` / `feetBelowEye`）は `settings.json` の `xr` を直接書き換えて変える。**
   これが効くのは**起動時の配置だけ**——大きさと違い、設定パネルに項目が無い。既定は
   「机の上のミニチュアを、正面の画面を避けた右側に」。デスクトップのパネルには出さないが、往復で
   落とさない（`connection` と同じ扱い）。**手で置き直した位置は再起動で戻る**（永続化は
   [#122](https://github.com/schwarz9791/chatter-agent/issues/122)。設定パネルの「位置をリセット」は
   再起動せずに同じ状態へ戻す）
6. **視線と首はカメラ＝頭を見る。** `gazeTarget` は `Main Camera` の子で、`VrmPoseAccent` の
   基準の下向き（縦）と左右の基準（`GazeAim.NeutralYawDegrees`。
   [#121](https://github.com/schwarz9791/chatter-agent/issues/121)）は目とカメラのワールド座標の差から
   出すので、カメラ＝頭になれば「ユーザーの頭を見る」になり、横へ回り込んでも首が追う。左右の基準は
   モデルの正面（VRM ルートの forward ではなく、`FaceCamera` が −Z へ向けた時点の向き）から測る。
   デスクトップはカメラが正面にあるのでほぼ 0。Android は aim レイが追跡できている間だけ
   `CursorProvider`（`XrCursorGazeSource`）が値を返し、それ以外は漂いのまま
7. **フレームレートはコードを変えていない。** XR ではランタイムがフレームペーシングを握り、
   `Application.targetFrameRate` は効かない。XR が起動しなかったときは今までどおり `MascotRunner` の値
8. **XR かどうかは `XRGeneralSettings.Instance.Manager.activeLoader` で判定する。** `Application.platform` は
   XR でも `Android` のままなので使えない。起動していなければ `XrStage` は何もせず、#97 と同じ平面表示になる
9. **シーンは変えていない。** XR Origin と `TrackedPoseDriver` は `XrStage` が XR の起動時だけ実行時に組む。
   `ChatterMascot.Xr` は Editor と Android に限ったアセンブリで、`CursorGazeSource` と同じ「シーンに置かない注入」

★ **UniUnlit への差し替え（#110）は XR でもそのまま効く**（`UnlitFallbackPolicy` は `Application.platform` で判定する）。

### キャラを手で置き直す（[#121](https://github.com/schwarz9791/chatter-agent/issues/121)）

Hand Interaction Profile（OpenXR の `XR_EXT_hand_interaction`）の aim レイ（`pointerPosition` /
`pointerRotation`）と `pinchValue` を左右とも読む。aim レイがキャラクター（VRM の `CapsuleCollider`）に
当たった状態で**つまみに入った瞬間**だけ掴む（つまんだままレイを動かしてキャラに当てても掴まない）。
掴んでいる間は、**レイが水平面に当たればその点に足元を置く**。面を指していない間は、最後に指した
面の高さとレイの俯角で奥行きを決める（`XrGrab.UpdateHeld` / `XrGrabRules.HeldDistance`）。
つまみはヒステリシス（`XrGrabRules.IsPinching`。入り 0.9 / 抜け 0.6）。追跡を短く見失っても
0.2 秒は保持する。

★ **マウスも同じ「手」として読む。** Android Mouse Interaction Profile（OpenXR）の aim レイと
`click` を、左右の手と同じ `pinchValue` のヒステリシスへそのまま流し込む——手の入力を読む経路は
増やさず、`_hands` に3つ目として並べるだけ（左手・右手・マウス）。複数が同時に追跡されているときは
マウス＞右手＞左手の順で優先する（`XrGrab.TryGetAimRay`。「目で追う」もこの優先順に従う）。
★ **未確認（実機）。** マウスでの掴み・パネル操作の当たり心地。

★ **掴んだ距離を保ってレイに沿わせるだけでは、狙いより手前に着く。** 奥の床を指して手を下げても
キャラは空中の手前に留まり、離すと真下へ落ちる。

★★ **「掴んだ点をレイに乗せる」形で奥行きを補正しても、段差では縁から落ちる。** 指した面から
掴んだ点の高さぶん上の面とレイの交点は、レイが面に当たる点より**手前で高い**。床からテーブルへ
移そうとすると、キャラが手前へ寄って大きく見え、足元がテーブルの縁の外に出て、離すと下の床へ
落ちた。**足元そのものを指した点に置く**ことで解いた。ただし掴んだ瞬間はレイの先が体の奥の床へ
抜けやすく、そのままだと動かさずに離しても位置がずれる。**掴んだ点と足元の水平方向のずれ
（`_slip`）を保ち、指した点が動いた距離ぶんだけ縮める**ことで、つまんで離すだけでは動かさず、
動かせば足元が指した点へ寄っていくようにした。

★ **面を外れた後も、置き方を「足元を面に置く」にそろえる。** 面を指している間は足元を指した点に
置き、外れた後だけ「掴んだ点をレイに乗せる」にすると、交点が面の高さではなく掴んだ点の高さに
なり、足元の目標が（掴んだ点の高さ ÷ tan 俯角）ほど手前へ跳ぶ。面への当たりは `MaxHeldDistance`
で打ち切るので、遠くを指すたびにこの経路へ入る。一度でも面を指したら、外れた後は最後に指した面の
高さとレイの交点に足元を置く（面の縁では交点が指した点と一致するので連続になる）。面を一度も
指していない間だけ、掴んだ点をレイに沿わせる。

★ **指す面が切り替わると行き先が跳ぶので、アンカーは寄せて追う。** 離したときは追いつき先に
置いてから平面へ下ろす（寄せている途中の位置から真下を探すと、狙った面を外す）。

★ **起動直後のキャラは目の高さに浮いているので、掴むレイは水平か上向きになる。** 掴んだ点の高さの
面と交わらないので、面を指すまでは掴んだ距離のまま動く。

離すと、足元の xz・**当たり判定の上端**から真下へ `ARPlaneManager.Raycast` し、`HorizontalUp` の
最も近い面に足元を乗せる（無ければ離した位置のまま）。続けて `ModelAnchor` のヨーを頭の方へ向ける
（`XrGrabRules.TryYawToFace`）。動かすのは常に `ModelAnchor`（起動時の配置と同じく、XR Origin には
触らない —— Origin を動かすと部屋ごと動いて見える）。離した後は揺れもの（spring bone）を静止形に
戻す（落下と向け直しは瞬間移動なので、慣性で髪などが振り回されないように。`VrmStage.ResetSpringBones`）。

★ **足元ではなく、当たり判定の上端から探す。** 足元は下ろすと天板に潜り、掴んだ点も足元の近くだと
天板より下になる。上端からならどこをつまんでも体の下の面が取れる。

★ **XR Hands（`XRHandSubsystem`）はつまみ判定には使わない。** エミュレータの手
（Hand tracking モード）は体の前に固定でマウスへ aim を向けるだけなので「キャラの近くでつまむ」判定が
成立しない。関節の親指–人差し指の距離も、つまんでも入りの閾値ちょうどの値で判定が揺れる。
`pinchValue` はランタイムが出すつまみの値をそのまま使う——この理由は変わっていない。

**手のひらメニュー（#143）の向き判定だけには使う。** `XrHandTracking` が唯一の読み取り口で、
Palm（取れなければ Wrist）の関節姿勢から「手のひらが自分（頭）の方を向いているか」を毎フレーム
判定する。つまみには関与しない——`XrGrab` の aim レイ・`pinchValue` はそのまま。

- **手のひらの法線はローカル `-Y` で、左右で符号を変えない。** `com.unity.xr.hands` の
  `Gestures.XRHandOrientationUtility.GetHandAxisDirection` が Palm Direction を
  `rootRotation * (0,-1,0)` として求めており（Thumb Direction と違って左右非対称の補正を
  入れていない）、プロバイダが関節姿勢もこの規約へ揃えて返すので、Palm / Wrist の姿勢にも
  そのまま使える
- **subsystem は許可が下りるまで走らせない。** `SubsystemRegistration` の時点で
  `HandTracking.automaticallyInitializeSubsystem = false` を立て、`HAND_TRACKING` の許可
  （`XrGrab` が起動時にまとめて要求する）が下りてから `EnsureSubsystemInitialized()` を呼ぶ
  （docs の既定パターン）。未許可のまま走らせるとランタイムが毎フレームエラーを出す
- **OpenXR の設定で HandTracking（Android）feature を有効にしてある。** つまみ（Hand Interaction
  Profile）はこの feature が無くても動くが、関節姿勢の読み取りには要る
- **左右を別々に判定する。** 片手の手のひらを自分へ向け、もう片方の手でつまんで押すのが本来の
  使い方なので、どちらかを優先すると向けた側を見落とす。見失い（関節が一瞬取れない）も手ごとに
  見る——もう片方の手が見えているだけで、消えた手の「向けている」が残り続けないようにする

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

★ `ARPlaneManager.Raycast` は面を下から見ても当たる。`HorizontalUp` は上から見た面が前提なので、
上向き・水平のレイ（`worldRay.direction.y >= 0f`）は無効な当たりとして弾く
（`XrGrab.TryRaycastHorizontalPlane`）。

★ 面への当たりも `MaxHeldDistance` を超えたら無視する。指した点に足元を置く経路（掴んでいる間）と、
面を指していないときの経路（`XrGrabRules.HeldDistance`）が、同じ上限を共有する。`ARPlaneManager.Raycast` の
`hit.distance` はトラッカブル空間のローカル距離なので、当たった点をワールドへ戻してから
`MaxHeldDistance` と比べる。

置いた位置は残らない（再起動で #99 の配置に戻る。永続化は
[#122](https://github.com/schwarz9791/chatter-agent/issues/122)）。

### 歩行範囲の円の中を歩かせる（[#71](https://github.com/schwarz9791/chatter-agent/issues/71)）

**平面へ置き直した後だけ歩く。** 起動直後（#99 の配置のまま）は床が分からないので歩かない。
`XrGrab.Release` が平面を見つけたときだけ `XrWalk.PlaceAt` を呼び、そこが円の中心になる。
歩行モーション（`animations/walk/`）が1本も読めていなければ、円は出ても歩かない。

★ **設定（`character.walk`）で歩行そのものを止められる。** OFF の間は円が出ない・歩き出さない
——キャラクターの置き直し自体（`XrGrab`）は止めない。**OFF でも歩行範囲（置いた位置・半径）は
捨てない。** 「置いたか」と「歩いてよいか」を別の状態で持ち（`XrWalk.Active`）、ON に戻したら
その場から歩き出して円を短く見せる。捨てると、ON に戻しても置き直すまで歩かず、壊れて見える。

**範囲はユーザーが目で見て決める。** 置き直した直後（とハンドルを操作した後）に、足元へ
半透明の円とフチ・ハンドルを 15 秒だけ出す。ハンドルをつまんで前後に動かすと半径が
10〜60cm で伸び縮みする（既定 20cm）。★ **これで障害物回避が要らなくなる** —— 机の縁や壁を
避けるのは「範囲を決める操作」そのもので、コードは円の外に出ないことだけを守ればよい。

★ **半径は基準の身長（歯車と同じ `XrMenuRules.GearReferenceHeightMeters`）で覚え、ワールドに
出すときだけ歯車と同じ倍率（`XrMenuRules.GearScale`）でキャラクターの大きさに合わせて
伸び縮みさせる**（`XrWalk.ScaleFactor`）。ハンドルをドラッグして決めた距離もこの倍率で
割り戻してから 10〜60cm にクランプする——**「大きさ」の設定を変えても、歩き回れる範囲が
キャラクターの背丈に対して同じ広さに見える**ようにするため。円とハンドル（見た目・当たり
判定）も同じ倍率で拡縮する（`XrWalkAreaView.Sync`）。

★ **掴む対象を増やすのであって、手の入力を増やすのではない。** aim レイ・`pinchValue` の
ヒステリシス・掴みの排他は `XrGrab` に一本化したまま、`TryGrab` が先にハンドル、当たらなければ
モデルを見る。別のコンポーネントで手の入力を読むと、同じつまみで両方が反応する。

★ **見えていないハンドルは掴ませない。** 当たり判定だけ残すと、キャラクターを掴もうとした
つまみが吸われる。

★ **円はワールドに置く。`ModelAnchor` の子にしない** —— 縮尺が二重に掛かるうえ、キャラと
一緒に動いてしまう。床の平面とは 2mm 離す。

★ **フチとハンドルは不透明にし、塗りより後に描く**（`renderQueue`）。半透明どうしだと
重なりの枚数と描画順で色が変わり、**同じマテリアルでも同じ色に見えない**。ハンドルを立体に
しないのも同じ理由（裏面が重なって濃く見える）。

★ **マテリアルは `Resources/` の `.mat` から読む。** 実行時に `Shader.Find` で URP の
シェーダを引かないこと（どのマテリアルからも参照されないシェーダはビルドから落ちる）。
**自前のシェーダも書かないこと** —— single-pass instanced のステレオ描画の手当てが要り、
片目にしか出ない罠を踏む。

★ **円の外へ出さないクランプは「今より遠くならない」形にする。** 半径を縮めた直後はキャラが
円の外にいるので、単純に円へクランプすると縁へ瞬間移動する。進んだ後の中心からの距離を
`max(半径, 進む前の距離)` を超える一歩は**押し戻さずに丸ごと捨てる**。外から中へ戻る動きは通り、
外へ出る動きだけ止まる（`Wander.Advance`）。★ 縁へ押し戻すと、その動きは正面に対して横向きに
なり、**それ自体が横滑りになる**（既定設定のシミュレーションで移動の 1 割強が一度は踏んでいた）。

**歩行範囲の中を指すと、そこへ歩く。** ハンドルにもキャラクターにも当たらなかったつまみを
「行き先の指示」として扱う（`XrWalk.TryWalkTo`）。床と交わらないレイは無視する。

★ **円の外を指したら、円を数秒出す —— ただし検知した平面を指したときだけ。** `TryWalkTo` が
使う床は `y = floorY` の無限平面なので、わずかに下を向いただけのレイもほとんど「範囲外」判定に
なる。検知した平面を指したときに絞ることで、キャラを掴もうとしたつまみのたびに円が点滅しない
（`XrGrab.TryGrab` の、当たり判定を外した経路からだけ呼ぶ。円を出す判断自体は
`XrWalk.ShowAreaBriefly`）。既に出ている円の残り時間は縮めない。
★ 指された移動には**短すぎる移動の見送りを掛けない**（抽選と違って意図された移動なので）。
★ **足元に当たったつまみは、体ではなく床を指したものとして扱う**（足の位置から当たり判定の高さの
1割まで）。当たり判定は足先まで包むので、足元の近くを指すと体を掴んで置き直しになる。
キャラの背中側の足元は、斜め上から見るとレイが先に体へ当たるので指せない。

★ **bounds の余白（`boneBoundsMarginMeters`）はモデルの縮尺に合わせて縮める。** ボーンの位置は
ワールドで測るのに余白を等倍のまま足すと、小さく置いた XR のキャラでは当たり判定が体の何倍にも
膨らみ、足から離れた床を指しても体を掴んでいた。

★★ **進むのは「目的地の方向」ではなく「体の正面」。** 目的地へ直接寄せると、向き直っている
間は正面と進行方向がずれて**横へ滑って見える**。正面へ進めば、回りながら歩いてもカーブを描く
だけで滑らない（棒立ちで回る段は見た目が不自然だったので入れていない）。

★★ **ただし速さと旋回速度が一定だと、旋回半径（速さ÷旋回の角速度）の内側の目的地には原理的に
着かない。** 周りをぐるぐる回るだけになる（既定の縮尺では旋回半径が数 cm、半径を最小に絞ると
移動の 2 割以上が該当した）。一歩の速さを **`旋回の角速度 × 残りの距離` で頭打ちにし、さらに
方位差の cos を掛ける**（横〜背後なら前へ出ない）。並進で方位が変わる速さが旋回速度の半分以下に
収まるので、向きは必ず目的地に追いつく。横〜背後の目的地では歩行クリップのまま足踏みで向きを変える。

★ **`WALK00_R` / `_L` は旋回モーションではない。** クリップの中で hips のヨーは1度も変わらず
（実測: 振れ幅 0.0°、`_R` は −16.9°、`_L` は +14.9° の固定）、**同じ前進サイクルを体ごと
傾けただけ**のカーブ用の差分。回転はルートで回す前提なので、足しても旋回の問題は解決しない。

★ 発話が始まったら歩行は打ち切るが、**向き直りは発話中も進める**（クリップを使わないので。
止めると喋っている間ずっと背を向けたまま固まる）。

**歩行速度は歩幅から決める。フットスライドの原因は速度のズレだけで、接地判定でも IK でもない。**
`Wander.Speed(歩幅, 1周期の歩数, 周期, モデルの縮尺)` が唯一の出どころ。既定値は `WALK00_F.vrma`
を測ったもの:

| 測ったもの | 値 | 測り方 |
|---|---|---|
| クリップ長 | 1.30 秒（2歩） | glTF の animation sampler の input の末尾 |
| 歩幅 | 約 0.53 m | 接地している方のつま先ボーンを FK で解き、後退量 ÷ 時間から逆算 |
| 歩行速度（等倍） | 約 0.81 m/s | 同上（接地足の後退速度の中央値） |

hips の平行移動は 1cm 未満で、**root motion は焼かれていない**（その場歩き）。末尾に重複キーが
あるので #103 の終端ガードが要る素材でもある。

### 空間に浮かぶ設定パネル（[#143](https://github.com/schwarz9791/chatter-agent/issues/143)）

**呼び出し口（頭上の歯車／手のひらのボタン）とパネルの入力は `XrGrab` に一本化する。** 掴む対象を
増やすのであって、手の入力を増やすのではない——`XrGrab.TryGrab` の先頭で aim レイをまず
`XrSettingsBridge` へ渡し、パネル・手のひらボタン・歯車のどれかに当たっていればそちらを押して、
キャラ・歩行範囲の掴みへは進まない。優先順は **パネル → 手のひらボタン → 歯車 → 歩行範囲の
ハンドル → キャラ**。別のコンポーネントで pinch を読むと、同じつまみで2つが反応する
（歩行範囲のハンドルと同じ理由。→ 上「掴む対象を増やすのであって〜」）。

★ **ミュートだけはパネルの先頭に出す**（`SettingsSchema.BuildXr`。デスクトップの
`BuildDesktop` には出さない）。デスクトップと違い、XR にはメニューバーもショートカットも無く、
パネル以外に切り替える手段が無いため。切り替えは `XrSettingsBridge.SetMuted` を唯一の入口に
する——**パネル以外（将来の手のジェスチャーなど）から切り替える経路を足すときもここを通すこと**で、
保存とパネルの表示が食い違わずに済む。

★ **呼び出し口（歯車・手のひらボタン）の絵は `Resources/SettingsIcon` のスプライトを使う。**
読めなければ `⚙`（`LegacyRuntime.ttf`）の `Text` で代用する——`XrWalkAreaView` が `WalkArea`
マテリアルを読めないときと同じ扱いで、画像が無くてもビルドを壊さない。

★★ **レンダラ（`XrSettingsPanel`）に設定のキーを1つも書かない。** デスクトップの
`CMSettingsPanel.m` と同じ規律（→ [`mascot-settings.md`](./mascot-settings.md)）——`SettingSpec.Kind`
だけを見て行を組み、押されたキーは不透明な文字列のまま `XrSettingsBridge` へ返す。キーの意味を
知るのは `XrSettingsBridge` だけ。レビューでは `XrSettingsPanel.cs` に `SettingKeys.` が出てこない
ことを見る。

★ **入力は `EventSystem` を使わず、レイとパネル平面の交点を自前で判定する。** world-space
`Canvas` はあるが `GraphicRaycaster` は乗せない——`XRI`（XR Interaction Toolkit）も
`TrackedDeviceRaycaster` も要らない。パネルの `Transform` を平面としてレイと交わる点を求め、
行ごとの `RectTransform.Contains` で当たりを見る（`TryHover` / `TryPress`）。ワールド座標の
往復（`TransformPoint` / `InverseTransformPoint`）で求めるので、pivot・anchor の解釈を自分で
追わなくても実際の Transform 階層がそのまま答えになる。

★ **同じ項目構成（キーの並び）での更新は行を作り直さない。** 値・有効/無効・note だけ差し替える
（→ [`mascot-settings.md`](./mascot-settings.md)「自分起点の変更でパネルを作り直さない」と同じ
教訓）。構成そのもの（キーと種類の並び）が変わったときだけ作り直す。

★ **Choice の ‹ › は見た目の位置と押す判定の位置をそろえる。** ‹ を値欄の左端、› を値欄の右端に
別々の `Text` として置き、押す判定も同じ境界（値欄の左半分 / 右半分）で分ける——1つの `Text` に
「‹ 値 ›」とまとめて描き、判定だけ行の中央で分けると、見た目の記号と押した結果がずれる。ラベルが
空の Choice は値欄を行の全幅にする（レンダラはラベルの有無だけを見て、キーでは分岐しない）。

★ **「すべての設定をリセット」の確認はダイアログではなく「もう一度押す」。** XR にはネイティブの
確認ダイアログが無い——デスクトップの `NSAlert` に相当するものが無いので、1回目は確認待ちの note
に差し替えるだけにし、既定値の猶予以内の2回目で確定する。パネル側にキーは増やさない
（`XrSettingsBridge` が出来上がった並びの該当行だけ note を差し替える）。

**「すべての設定をリセット」は `ResetKeepingConnection`（接続先を残す）に、位置と大きさのリセットを
重ねたもの。** 同期して取得済みのモデルファイル（`persistentDataPath/synced/`）は消さない——次の
起動でまた同じものを取りに行けばよいので消す必要が無い（デスクトップ版の「すべての設定をリセット」
との違い）。位置は「位置をリセット」と同じ経路（`XrGrab.ResetPosition` + `XrWalk.ResetArea`）を
そのまま呼ぶ。

大きさ（実寸の測り方、比での焼き直し）は上「空間配置の決めごと」3・4。

★ **パネルは視線の正面に中心を合わせ、高さに上限を持たせて縮めて収める。** 目線より下へ
伸ばすと、グラスの狭い視野から外れるうえ、机などの面の奥に隠れる（エミュレータでは仮想の
部屋のテーブルに下半分が隠れ、「閉じる」しか見えなかった）。開いた後は固定し、視線から大きく
外れたときだけ正面へ寄せて戻す（`XrMenuRules.ShouldFollowPanel`。ヒステリシス付き）——常に
追従させると、読んでいる行や指そうとした行が逃げる。

★ **暗い色は透ける。** グラスは加算合成（→ 下「背景に部屋を透かす」）なので、パネルの暗い
背景はほぼ見えない。文字と明るい行の背景だけが見える前提で作る。

★ **長いラベル・値は縮めて収める**（uGUI の best fit）。枠は項目によらず一定なので、どの項目が
長いかをレンダラが知らずに済む。Choice の `‹` `›` は値欄の両端に置き、押す判定も値欄の左半分＝前・
右半分＝次にそろえる（見た目と押す位置がずれると違和感が強い）。ラベルが空の Choice は値欄を全幅にする。

**`XR_Glasses` エミュレータで分かったこと**

- **`XRHandSubsystem` は関節を返す。** ただし**手のひらを自分へ向けられない**（向きが固定）ので、
  手のひらボタンは出せない
- **つまんでいない間、aim レイは動かない**（マウスを動かしても固定のまま。つまむとその位置へ飛ぶ）
- `⚙` は `LegacyRuntime.ttf` のフォールバックで描ける

★ **歯車は「キャラクターをつまんだ・離した後の一定時間」だけ出す。aim レイのホバーでは出さない。**
つまんでいない間 aim が動かない環境があり、ホバーでは出せないことがある。つまむのは明示的な操作
なので、手のひらモードでも出して邪魔にならない——手のひらを向けられない環境でもパネルを開ける。
歯車にレイが当たっている間は出し続ける（押しに行く途中で消えない）。

★ **未確認（実機）。** 手のひらを自分へ向けたときにボタンが出るか、パネルの読みやすさ
（文字サイズ・行間・距離）。

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

読み込み後に大きさを変える経路（設定パネル。→ 上「空間に浮かぶ設定パネル」）も同じ
`BakeSpringBoneScale` を通る——渡す縮尺が「等倍からいまの縮尺へ」ではなく「変化の比」になる点だけが違う。

### ★ XR Origin に `HideFlags.HideAndDontSave` を付けない

`FindFirstObjectByType` から見えなくなる（→「`HideFlags.HideAndDontSave` のオブジェクトは
`FindFirstObjectByType` から見えない」）。AR Foundation の各 Manager など、`XROrigin` を探しに来る側が
見つけられなくなる。

### ★★ グラスの表示視野は狭い — 描けているのに視野の縁で切れる

`XR_Glasses` の画面は、グラスの表示視野の外が黒く切り落とされる。**起動時の正面から右 30° に置くと、
キャラクターの右半分が視野の縁で切れ、ミニチュアだと頭の飾りしか見えなかった**（ログ上は正しく配置され、
描画もされている）。既定の方位と足元の深さは、起動時の正面を向いたまま全身が視野に収まる範囲に留めてある。
実機の視野角での調整は #100。

### ★★ エミュレータのスナップショット（Quick Boot）は使えない

**背景の濃さ（Environment Visibility のスライダー）は毎回初期値に戻る。** アプリからは変えられない
—— アプリが握っているのは environment blend mode を ADDITIVE にする要求だけで、あのスライダーは
エミュレータ側の機能。実機（光学シースルー）にはそもそも無いので、**エミュレータ限定の手間**。

スナップショットで状態ごと持ち越そうとすると、どちらに転んでも駄目だった:

| やり方 | 結果 |
|---|---|
| 既定のまま終了 | `Not saving state: … Reason: UNSUPPORTED_VK_APP` で保存を拒否。**しかも既存の `default_boot` を消す** |
| `-feature VulkanSnapshots` を付けて終了 | 保存は通る。だが**復元するとエミュレータごと落ちる**（`VkDecoder::Impl::decode` の致命ログ → `abort`） |

Android XR は Vulkan 必須なので、この機能を切って回避することもできない。**`-no-snapshot-load` で
起動して、スライダーは毎回合わせる。** 壊れたスナップショットを残してしまったら
`~/.android/avd/<AVD>.avd/snapshots/default_boot` を消す（残っていると次の起動で同じクラッシュを踏む）。

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

★ **`Android Mouse Interaction Profile` も `FixOpenXrFeature` で有効化する**（`XrGrab` の
`AndroidMouseInteraction` バインドが要る）。無効のままだとマウスの入力が来ないだけでエラーは出ない。

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

★ **外すと本当に止まる（実測）。** 上の手順を踏んだうえで、再現条件そのもの（`Library/` を
退避してから `test.sh`）を走らせた。`Keys` は `0100000007000000` のままで、追跡ファイルは
1つも汚れなかった。`modules.json` の差分は `webgl` の `"selected"` の**1行だけ**で、
Android 系のエントリには触れない。`Library/` は 12G → 2.3G に減った
（2026-09-21 / Unity 6000.3.14f1 arm64 / EditMode 815 件は通過したまま）。

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
  [`../mascot.md`](../mascot.md)「接続」の C）。後から
  `NsdManager`（`android.net.nsd`）で mDNS 検出を足すのは未着手

★ **エンジンの `--host 0.0.0.0 --cors_policy_mode all` は不要になった**（#29）。
叩くのは同じ Mac 上の `chatter-agent-server` だけ。

[公式のプロジェクトセットアップ手順](https://developer.android.com/develop/xr/unity/setup)に従うこと。
