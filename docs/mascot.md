# 表示側アプリ（Unity + UniVRM）

**Unity + UniVRM で1プロジェクト。** macOS デスクトップ（透過ウィンドウで常駐）と Android XR
グラス（XREAL Aura）の両方をここからビルドする。発話の契約は [`protocol.md`](./protocol.md) が正。
実装で踏んだ罠・なぜその形にしたか・実測値は [`docs/knowledge/`](./knowledge/)（`mascot-unity.md` /
`mascot-desktop.md` / `mascot-settings.md` / `mascot-vrm.md` / `mascot-speech.md` /
`mascot-android-xr.md`）にある。索引は末尾の表。

## 環境

| | |
|---|---|
| Unity | **6**（開発は 6000.3.14f1） |
| レンダーパイプライン | **URP** |
| VRM | [UniVRM](https://github.com/vrm-c/UniVRM) |
| ウィンドウ制御（macOS） | [UniWindowController](https://github.com/kirurobo/UniWindowController) `com.kirurobo.uniwinc`（MIT） |
| JSON | `com.unity.nuget.newtonsoft-json` |
| XR（Android のみ） | `com.unity.xr.androidxr-openxr`（Android XR Extensions for Unity は入れない → [#119](https://github.com/schwarz9791/chatter-agent/issues/119)） |
| グラフィックス API | Metal（macOS）/ Android は **Vulkan 単独**（URP で Android XR を使うときの必須設定） |
| 常駐（macOS のみ） | 自作の Objective-C プラグイン `Assets/Plugins/macOS~/ChatterMascotNative/` |

### 必要な Unity モジュール

- **Mac Build Support (IL2CPP)** — macOS Standalone のビルドに要る
- **Android Build Support**（OpenJDK / SDK & NDK 込み）— Android ビルドに要る。同梱の SDK / NDK / JDK だけで足り、外部の SDK 設定は要らない

### Xcode コマンドラインツール（macOS のみ）

メニューバー常駐のネイティブプラグインを `clang` でビルドするのに要る。

```bash
xcode-select --install   # 既に Xcode があれば不要
./scripts/build-native.sh
```

`./scripts/build.sh` が Unity より先に自動でこれを呼ぶので、普段は意識しなくてよい。

★ **新規クローンでは、Unity を開く前にここまで済ませること。** `.bundle` は git に無いので
クローン直後は `.meta` しか無く、Unity が既定でインポートし直すと**プラットフォームの絞りが
「すべて」に化ける**。バンドルを作ったあとに一度だけ:

```bash
./scripts/run.sh ChatterMascot.EditorTools.NativePluginSettings.FixAll
```

★ **順序を逆にしないこと。** バンドルが無い状態で `test.sh` / `run.sh` を先に走らせると
**`.bundle.meta` の GUID ごと設定が飛ぶ**（`build.sh` は先に `build-native.sh` を呼ぶので踏まない）。
**ビルドもテストも通ってしまうので、気づけるのは `git diff` だけ**
（→ [`knowledge/mascot-unity.md`](./knowledge/mascot-unity.md)）。

### パッケージの導入

`Packages/manifest.json` に入れてある（Package Manager の GUI でも同じ）。

```json
"com.kirurobo.uniwinc": "https://github.com/kirurobo/UniWindowController.git#upm",
"com.vrmc.gltf": "https://github.com/vrm-c/UniVRM.git?path=/Packages/UniGLTF#v0.131.2",
"com.vrmc.vrm": "https://github.com/vrm-c/UniVRM.git?path=/Packages/VRM10#v0.131.2",
"com.unity.nuget.newtonsoft-json": "3.2.1"
```

## 構成

```
Assets/StreamingAssets/
  vita.vrm                          同梱モデル（CC0。→ ../NOTICE）
  idle_loop.vrma                    同梱アイドル
  trayTemplate.png / @2x            メニューバーのアイコン（自作）

Assets/Plugins/
  macOS~/ChatterMascotNative/       `~` 付き。Unity は完全に無視する（ObjC のソース）
    CMNative.h                      ABI。公開するものに CM_EXPORT を付ける
    CMEvent.m                       C# へ返す唯一の口（main thread の保証もここ）
    CMApp.m                         NSApplicationActivationPolicy / 版
    CMStatusItem.m                  NSStatusItem + NSMenu（キーもラベルも書かない）
    CMHotKey.m                      Carbon RegisterEventHotKey
  macOS/ChatterMascotNative.bundle  成果物（.gitignore。.meta だけコミットする）

Assets/ChatterMascot/
  Runtime/                          ChatterMascot.Runtime — 描画に依存しない層
    Protocol/   SpeechFrame.cs      フレームのパースと検証
                SpeechEpoch.cs      epoch / audio path の charset
    Playback/   PlaybackQueue.cs    状態機械。何をいつ取り、いつ鳴らし、いつ ack するか
                PlaybackState.cs / PlaybackOptions.cs / PlaybackEvent.cs
    Net/        SpeechClient.cs     WebSocket。繋ぐ・繋ぎ直す・ack を送る
                AudioFetcher.cs     GET /audio/<epoch>-<seq>.wav
    Audio/      WavDecoder.cs       WAV → AudioClip
                AudioClipPlayer.cs  AudioSource で1件ずつ鳴らす
                MuteState.cs        一時ミュートの状態
                MutedSpeechPlayer.cs 「声だけ消す」デコレータ。ack は通常経路のまま出す
    Ui/         HotKeySpec.cs       "opt+m" ⇄ Carbon の (keyCode, modifiers)
                MenuModel.cs        メニューの並びの唯一の持ち主
                MenuJson.cs         ネイティブとやり取りする JSON
    Settings/   SettingsStore.cs    ~/.config/chatter-agent/mascot/settings.json
                SettingsJson.cs / MascotSettings.cs
    Vrm/        AssetPath.cs        .vrm / .vrma の探索順（純粋。下の表）
                VrmFraming.cs       画面に収まるカメラ距離（純粋）
    Xr/         XrPlacement.cs      起動時の頭の姿勢 → XR Origin の配置（純粋）
                XrGrabRules.cs      つまみのヒステリシスと、離したときの向き（純粋）
    CommandLine.cs                  起動引数（-serverUrl / -vrm / -buildScene が共有）
    FrameRateBudget.cs              フレームレート上限の「戻す先」と「一時的に借りる」
    MascotRunner.cs                 ドライバ。コマンドを実行して結果をイベントで戻す
  Vrm/                              ChatterMascot.Vrm — UniVRM に依存する層（全プラットフォーム）
    VrmStage.cs                     読み込み・フォールバック・Collider・画角合わせ
    VrmAssetLoader.cs               候補を順に読む（UnityWebRequest。自前の期限つき）
    VrmMaterialCheck.cs             シェーダーストリッピングの自己診断
    AssetEnvFactory.cs              Application を触る唯一の場所
  Xr/                               ChatterMascot.Xr — Editor + Android のみ
    XrStage.cs                      XR が起動したときだけ XR Origin を組んで空間固定する（シーンに置かない）
    XrGrab.cs                       手でつまんで置き直す。配置の直後に生やす（シーンに置かない）
  Desktop/                          ChatterMascot.Desktop — Editor + macOS/Windows のみ
    DragHandles.cs                  「Collider を持つものに UniWindowMoveHandle」
    VrmDragHandleBinder.cs          MonoBehaviour にしない
    CursorGazeSource.cs             OS カーソル座標 → 視線（すべてポイントで閉じる）
    WindowGeometry.cs               位置と大きさをポイントで復元・永続化
    DragStateGuard.cs               ドラッグ終了の取りこぼしでクリック透過が死ぬのを救う
    WindowProbe.cs                  座標系の実測（`-windowProbe` のときだけ動く）
    StatusItemBridge.cs             メニューバー常駐の配線（判断は Runtime 側）
    Native/ChatterMascotNative.cs   DllImport。可用性の判定は初回1回だけ
  Editor/
    SceneFixups.cs                  シーンとプロジェクトの修繕・検査
    MacPostBuild.cs                 Info.plist に LSUIElement を書く（Dock に出さない）
    NativePluginSettings.cs         PluginImporter を出荷値にする
    IconSettings.cs                 AppIcon.png を Player Settings の Icon に登録する
    BuildScript.cs / VrmProbe.cs
  Tests/Editor/                     EditMode テスト（状態機械が主）
```

依存の向きは `Editor → Desktop → Vrm → Runtime`。`ChatterMascot.Runtime` は描画に依存しない層
として保つ（→ [`mascot-unity.md`](./knowledge/mascot-unity.md)）。`ChatterMascot.Xr` / `ChatterMascot.Desktop`
はそれぞれ Android / macOS・Windows 限定のアセンブリで、Android ビルドでは `ChatterMascot.Desktop`
系のコンポーネントがビルド時に剥がれる（→ [`mascot-android-xr.md`](./knowledge/mascot-android-xr.md)）。

`./scripts/test.sh` の `total=` が現在のテスト件数を示す。

### モデルとアニメーションの探索順

`.vrm` と `.vrma` で同じ形。**最初に読めたものを採る。**

| 順 | 出どころ | `.vrm` | `.vrma` | 対象 |
|---|---|---|---|---|
| 1 | 起動引数 | `-vrm <path>` | `-vrma <path>` | 全 |
| 2 | 環境変数 | `CHATTER_MASCOT_VRM` | `CHATTER_MASCOT_VRMA` | 全 |
| 3 | 設定パネルで選んだモデル | `models/mascot.vrm`（固定名） | —— | デスクトップのみ |
| 4 | `Application.persistentDataPath/` | `models/mascot.vrm` | `animations/idle.vrma` | 全 |
| 5 | `${XDG_CONFIG_HOME:-~/.config}/chatter-agent/` | `models/*.vrm` | `animations/*.vrma`（直下のみ） | デスクトップのみ |
| 6 | 同梱（`StreamingAssets/`） | `vita.vrm` | `idle_loop.vrma` | 全 |

5 は `core/src/core/paths.ts` の `getRuntimeDir` と**同じ規則**（ユーザーから見て「chatter-agent
の設定はここ1箇所」を保つため）。辞書順の先頭を採る。`animations/<category>/*.vrma`
（`idle` / `happy` / `angry` / `sad` / `relaxed` / `surprised`。感情モーションと小ネタの置き場）
は候補には入らない。全部読めなければ Cube が出たままになる——無地の Cube は「異常事態」の
可視のシグナル。

## Player Settings（macOS Standalone）

UniWindowController の Inspector にある「Player Settings を直す」ボタンが見るのと同じ項目。
コードからも同じものを設定してある。

| 項目 | 値 |
|---|---|
| `Fullscreen Mode` | Windowed |
| `Resizable Window` | オン |
| `Default Is Full Screen` | オフ |
| `Allow Fullscreen Switch` | オフ |
| `Default Is Native Resolution` | オフ |
| `Default Screen Width` | 540 |
| `Default Screen Height` | 540 |
| `Mac Retina Support` | オン。★ **切らないこと** —— 表示がぼやけるうえ、`UniWindowMoveHandle` の Retina 座標系の手当てが前提にしている |
| `Run In Background` | オン |
| `Use Mac App Store Validation` | オフ |
| `Mac App Sandbox` | オフ |
| `API Compatibility Level` | .NET Standard 2.1 |
| `Disable Unity Audio` | ビルド時だけオン（コミットされた値はオフ） |

数値を変えたら実測すること:

```bash
defaults delete tech.sukima.chatter-mascot   # 前回終了時の大きさの焼き付きを消す
./scripts/build.sh
open Build/ChatterMascot.app --args -serverUrl ws://127.0.0.1:9
grep RecreateSurface "$HOME/Library/Logs/schwarz9791/Chatter Mascot/Player.log"
```

位置と大きさは `~/.config/chatter-agent/mascot/window.json` にポイントで永続化される。
設定パネルの「大きさ」（倍率 0.5〜2.0）もこのウィンドウそのものを動かす——`VrmStage` が
`Screen.width/height` の変化を見てフレーミングし直すので、ウィンドウさえ変えればモデルは
勝手に収まる。理由と実測は [`mascot-desktop.md`](./knowledge/mascot-desktop.md) /
[`mascot-settings.md`](./knowledge/mascot-settings.md)。

### 音の出し方（プラットフォームで違う）

| | 再生の実体 | 無音時にデバイスを手放す方法 |
|---|---|---|
| **macOS** | `AfplaySpeechPlayer`（1発話 = 1プロセスで `afplay`） | プロセスが消えれば OS が解放する |
| **Android / iOS** | `AudioClipPlayer`（Unity 内蔵オーディオ） | `AudioSettings.Mobile.StopAudioOutput()` |

`Disable Unity Audio` は `scripts/build.sh` の `BuildScript.BuildMacOS` が**ビルド時だけ**
切り替え、ビルド後に戻す。プロジェクト設定はプラットフォーム別に持てないため
（コミットされている値は Android 側の要求に合わせたオフ）。理由と実測は
[`mascot-speech.md`](./knowledge/mascot-speech.md)「無音時にオーディオ出力デバイスを掴まない」。

### URP 側

| 項目 | 値 |
|---|---|
| `Supports HDR` | オフ |
| `Allow Post Process Alpha Output` | オン |
| Camera の `Background Type` | Solid Color、alpha 0 |
| Renderer の `Rendering Path` | Forward |
| Renderer の `MToon Outline Render Feature` | 追加する（PC / Mobile とも。Editor の GUI から） |
| Renderer の `Screen Space Ambient Occlusion` | オフ |
| Always Included Shaders | `VRM10/Universal Render Pipeline/MToon10`、`UniGLTF/UniUnlit` |

シーン側にも2つ要る。どちらも透過ではなくクリック透過（Raycast ヒットテスト）のため。

| | |
|---|---|
| `UniWindowController.currentCamera` | 空欄にしない |
| シーンに `EventSystem` | `RaycastAll` が要求する |

`./scripts/run.sh ChatterMascot.EditorTools.SceneFixups.FixAll` が後者を保証する。
**Editor 上では透過しない**（ビルドしないと確認できない。UniWindowController の制限事項）。
なぜ各値がこの形かは [`mascot-desktop.md`](./knowledge/mascot-desktop.md) /
[`mascot-vrm.md`](./knowledge/mascot-vrm.md) を参照。

## 実装の決めごと

判断は `PlaybackQueue` に集めてある（`core/src/player/playbackQueue.ts` と同じ形）。イベントを入れるとコマンドの配列が返る**純粋な関数**で、
副作用（取得・再生・ack）は `MascotRunner` が実行して結果をイベントとして戻す。こうしてあるのは、
完了コールバックが状態機械に再入する（「ループの途中で状態が変わる」）バグをテストで捕まえる
ため——テストは「このイベント列でこのコマンド列が出る」を配列比較で固定できる。契約の危険な
箇所はほぼ全部この中にあるので、触るときは [`protocol.md`](./protocol.md) の「クライアント側の
責務」8項目と突き合わせること。`core/src/player/`（Node の発話 CLI）が**同じ契約の参照実装**
なので、挙動に迷ったらそちらと突き合わせる。

[`protocol.md`](./protocol.md) のクライアント責務6は「同じルートに対して繋ぐクライアントは1台にすること」。
ack は累積で、サーバーは `seq <= N` をキューから物理削除する（誰が受け取ったかは見ない）ので、
速いクライアントの ack が遅いクライアントのまだ喋っていない entry を消す。`ProjectSettings.asset`
の `forceSingleInstance: 1` を立ててあるが、**これで防げるのは同じ `.app` の二重起動だけ**——
`npm run start:player` や Android 版との併走は防げない。完全な排他は参照実装と同じ
`player.lock`（`core/src/core/paths.ts` の `getPlayerLockDir`）を Unity 側からも取ることになるが、ランタイムルートの発見が要るうえ Android XR
にはサーバーと共有するファイルシステムが無いので、今は立てていない。

WebSocket クライアントには `ClientWebSocket` を選んだ。`Origin` を送らないのでサーバーの
`allowedOrigins`（既定 `[]` = Origin 付きは全拒否）の設定が要らず（Origin が付くのは WebView から
張ったとき。→ [`protocol.md`](./protocol.md) の表）、追加依存も無いまま
macOS と Android を同じコードで通せる。引き換えは ping watchdog が同等品を作れないこと。

### 感情モーションと小ネタの素材

`animations/<category>/*.vrma` は **VRoid Studio の AnimationClip** を `.vrma` にしたもので、
VRoid Studio 由来のため再配布できない——同梱せず `~/.config/chatter-agent/animations/<category>/`
にだけ置く。抽出と変換の道具は抽出そのものがグレーゾーンなので、このリポジトリには置かず
private の `vroid-motion-exporter` に切り出してある。

起動時に `animations/<category>/*.vrma` を全部プリロードして寝かせておき、文の emotion で
1本ワンショット再生してから 0.5 秒のクロスフェードで待機へ戻す。発火は cc-mascot と同じく
**文ごと**で、再生中の感情モーションには割り込まない代わりにクールダウンで連発を抑える
（`CooldownSeconds` 既定1秒がカテゴリを問わない最短間隔、`SameCategoryCooldownSeconds` 既定
15秒が同じカテゴリの間隔）。`neutral` と `kind: prompt` は感情モーションを出さない。待機の
小ネタ（`idle/`）は発話が止まってから 30〜60 秒の乱数間隔で発火する。

## 動かす

```bash
cd core && npm run start:server            # 合成エンジンはサーバーが起こす

cd chatter-mascot
./scripts/test.sh                          # EditMode テスト
./scripts/build.sh                         # 本番シーン → Build/ChatterMascot.app
./scripts/build.sh Assets/Scenes/TransparencyProbe.unity Build/TransparencyProbe.app
./scripts/run.sh <Editor のメソッド>        # シーンの修繕など
```

どのスクリプトも Editor を閉じてから実行する（Unity はプロジェクトを排他ロックする）。

★ **クローン直後に `test.sh` / `run.sh` から始めないこと。** ネイティブプラグインの
バンドルが無い状態で Unity を回すと `.bundle.meta` が壊れる（→ 上の「Xcode コマンドラインツール」）。

シーンの `MascotRunner` に接続先（既定 `ws://127.0.0.1:8570`）を入れて Play。クライアント側に
合成エンジンは要らない——音声は同じ authority から HTTP で取る。

踏みやすいところ（詳細は [`protocol.md`](./protocol.md)「クライアント側の責務」）:

- 数十秒の無音は正常。`final` はメッセージが閉じる瞬間に届き、`AskUserQuestion` の直前では数十秒に達する
- `503` を「失敗」に数えない。数えると、エンジンを起動し忘れているだけで、溜まっていた発話が全部 ack されて消える
- `seq` の飛びは欠落。埋める手段は無いのでそのまま進む
- `epoch` が変わったら覚えていることを全部捨てる（重複排除の記憶、取得中の item、溜めている ack）

### macOS: 設定パネルとメニューバー

**キャラクターを右クリック**すると設定パネルが開閉する。メニューバーの「設定を開く…」からも
同じパネルが開く。「Chatter Mascot について」は別のダイアログで、版とライセンス全文はそちらに
出る。ショートカットは「記録」ボタンで実際にキーを押して決める（修飾キーを1つ以上含めること。
中止は修飾キー無しの esc）。

常駐の設定は `~/.config/chatter-agent/mascot/settings.json`（`window.json` と同じディレクトリ）。

```json
{
  "version": 1,
  "audio": { "mute": false, "muteHotKey": "ctrl+opt+m", "volume": 1.0 },
  "ui": { "hideHotKey": "ctrl+opt+h" },
  "character": { "idleMotion": true, "cursorGaze": true, "blink": true, "vrm": "" },
  "display": { "frameRate": 30 },
  "connection": { "serverUrl": "", "token": "" },
  "xr": { "scale": 0.18, "distance": 0.6, "azimuth": 20, "feetBelowEye": 0.2 }
}
```

`display.frameRate` は `30` か `60` のみ（既定 `30`。それ以外は既定へフォールバック）。設定
パネルの「モーション」→「フレームレート」から変えられ、反映はデスクトップ限定——Android は
このキーを読むだけで反映しない（XR ではランタイムがフレームペーシングを握る）。`connection`
（`serverUrl` / `token`）と `xr`（`scale` / `distance` / `azimuth` / `feetBelowEye`）はデスクトップの
パネルには出さないが、往復や「すべての設定をリセット」でも落とさない（`MascotSettings.ResetKeepingConnection`）。「大きさ」はここに無く
`window.json` が権威を持つ（スライダーは現在の高さ ÷ 540 の写し）。音声スタイル・話す速さ・
要約の ON/OFF は core の `~/.config/chatter-agent/config.json` が持ち、設定パネルは
`PATCH /v1/config` 経由で書く（→ [`protocol.md`](./protocol.md)「制御 API」）。音量が Unity 側で
速さが core 側なのは紛らわしいが理由がある —— 音量は**再生側のつまみ**で合成し直さなくても効き、
速さは**合成のパラメータ**で `audio_query` を変えない限り WAV が変わらない。`volume` は
0.0〜1.0（パネルには 0〜100% で出る）。

設定ファイルは1秒ポーリング（`mtime` + `size` のスタンプ比較）で外部からの変更も拾うので、
アプリを再起動しなくても直る。

```bash
$EDITOR ~/.config/chatter-agent/mascot/settings.json   # 直すか、消して既定に戻す
```

なぜこの形か・踏んだ症状は [`mascot-desktop.md`](./knowledge/mascot-desktop.md) /
[`mascot-settings.md`](./knowledge/mascot-settings.md)。

## Android / XR

接続 → 音声取得 → 再生 → ack と VRM の表示までが通る。OpenXR が起動すれば Full Space で
キャラクターを空間に固定して立たせ、起動しなければ通常の Android アプリ（平面表示）のまま
動く。前提は **Android Build Support**（OpenJDK / SDK & NDK 込み）だけ。

```bash
cd chatter-mascot
./scripts/build-android.sh                     # → Build/ChatterMascot.apk（.gitignore 済み）

~/Library/Android/sdk/emulator/emulator -avd XR_Glasses &   # 実機なら USB で繋ぐ
./scripts/run-android.sh [APK] [--no-logcat]                # 順不同。APK の既定は Build/ChatterMascot.apk
```

`run-android.sh` は `adb reverse tcp:8570 tcp:${CHATTER_AGENT_PORT:-8570}` → `install -r` →
`am start` → `adb logcat -s Unity` の順に行う。`adb` は
`$HOME/Library/Android/sdk/platform-tools/adb`（`ADB` 環境変数で上書き可）。この経路はサーバーから
見るとループバック接続になる（LAN 越しの接続やトークンの検証には下の「LAN 接続」を使う）。

logcat に出るはずの行:

```
[Mascot] server: ws://127.0.0.1:8570 / audio: http://127.0.0.1:8570/audio/
[Mascot] … から 19,259,304 バイト読みました: jar:file:///…/base.apk!/assets/vita.vrm   ← 同梱モデル
[Mascot] XR: 空間固定 headLocalPosition=… → originPosition=… originYaw=…              ← XR 未起動なら「XR: 起動していないので平面表示のまま」
[Mascot] XR grab: 平面検知を開始しました                                              ← SCENE_UNDERSTANDING_COARSE が許可されていれば出る
[Mascot] XR grab: 掴みました hand=RightHand
[Mascot] XR grab: 離しました plane=… yaw=…                                           ← plane=none は面が見つからなかった場合
[Mascot] 無音が続いたのでオーディオ出力を止めました                                  ← 発話が来れば「掴み直しました」が続く
```

権限（`HAND_TRACKING` / `SCENE_UNDERSTANDING_COARSE`）を試し直すときは:

```bash
adb shell pm grant|revoke tech.sukima.chattermascot android.permission.HAND_TRACKING
```

### モデルとモーションを入れる

Android に設定 UI は無いので、端末の `files/` 配下へ直接置く。**ディレクトリはデスクトップの
`~/.config/chatter-agent/` と同じ**（`models/` と `animations/`）。ただし**直下の2本は固定名**で、
デスクトップのような任意名の走査は効かない（端末に共有のファイルシステムが無いので、その段ごと落ちる）。

```bash
ADB=~/Library/Android/sdk/platform-tools/adb
D=/sdcard/Android/data/tech.sukima.chattermascot/files
$ADB push mascot.vrm  $D/models/mascot.vrm        # 固定名。これ以外は読まない
$ADB push idle.vrma   $D/animations/idle.vrma     # 固定名。待機ループの差し替え
$ADB shell am force-stop tech.sukima.chattermascot
```

感情モーションは `animations/<カテゴリ>/*.vrma`（`idle` / `happy` / `angry` / `sad` / `relaxed` /
`surprised`）。**こちらは任意名のままで効く** —— カテゴリの走査は `persistentDataPath` を無条件に
積むので、固定名に縛られるのは直下の1本だけ。

★ **`files/` の直下に置く旧レイアウト（`model.vrm` / `idle.vrma`）はもう読まない。** 残っていても
警告は出ず、同梱のモデルとモーションで起動する。

### キャラクターの大きさと置き場所（`xr`）

Android に設定 UI は無いので、端末の `settings.json` を直接書き換える。

```bash
ADB=~/Library/Android/sdk/platform-tools/adb
F=/sdcard/Android/data/tech.sukima.chattermascot/files/settings.json
$ADB pull $F settings.json        # 無ければ {} から書く。ほかのキー（connection など）は残す
# "xr": { "scale": 1.0, "distance": 2.0, "azimuth": 20, "feetBelowEye": 1.2 }
$ADB push settings.json $F
$ADB shell am force-stop tech.sukima.chattermascot   # 起動時に1回だけ読むので起動し直す
```

`scale` は身長の倍率（0.05〜1.0）、`distance` は目からの水平距離（m）、`azimuth` は起動時の
正面から右回りの角度（度）、`feetBelowEye` は足元が目より何 m 下か。範囲外は警告して既定に
戻る。頭を水平にして起動した場合の値で、上下を向いて起動すると目から足元へのずれをその
傾きぶん回した位置に出る。起動時の配置だけに効き、手でつまんで置き直した位置は再起動で戻る。

### LAN 接続

ビルドし直さず、`settings.json` を書き換えるだけで Android から Mac の `chatter-agent-server`
に繋がる。接続先とトークンは `connection` セクションに持つ。

```json
{ "connection": { "serverUrl": "ws://192.168.1.10:8570", "token": "…" } }
```

Mac 側はサーバーを LAN に公開してから使う。

```bash
CHATTER_AGENT_HOST=0.0.0.0 npm run start:server
```

```bash
./scripts/configure-android.sh                          # en0/en1 の IP + CHATTER_AGENT_PORT（既定 8570）から自動組み立て
./scripts/configure-android.sh ws://192.168.1.10:8570    # 接続先を明示
./scripts/configure-android.sh --no-restart              # 端末側のアプリを再起動しない
```

なぜこの形か・繋がらないときの切り分けは
[`mascot-android-xr.md`](./knowledge/mascot-android-xr.md)「LAN 接続（#98）」。

### 検証時の接続（デスクトップと Android を同時に確かめる）

1つのランタイムルートに繋ぐクライアントは1台にすること（→ [`protocol.md`](./protocol.md)
「クライアント側の責務」6）。デスクトップの常用サーバーと Android を同時に確かめるなら、
別のランタイムルートで別のサーバーを立てる。

```bash
cd core
XDG_CONFIG_HOME=/tmp/cm-android CHATTER_AGENT_PORT=8571 \
  CHATTER_AGENT_TTS_URL=http://127.0.0.1:10101 npm run start:server
cd ../chatter-mascot
CHATTER_AGENT_PORT=8571 ./scripts/run-android.sh
```

### Android 側の必須設定

`AndroidManifest.xml` に最終的に要るもの（`INTERNET` / `usesCleartextTraffic` 以外はビルド時に
`com.unity.xr.androidxr-openxr` が注入し、手で書かない）:

- `<uses-permission android:name="android.permission.INTERNET" />`
- `<application android:usesCleartextTraffic="true">`（`ws://` と `http://` のため）
- `<property android:name="android.window.PROPERTY_XR_ACTIVITY_START_MODE" android:value="XR_ACTIVITY_START_MODE_FULL_SPACE_UNMANAGED" />`
- `<uses-feature android:name="android.software.xr.api.openxr" android:required="true" android:version="0x00010001" />`

接続先の手動入力（`settings.json` の `connection.serverUrl` / `configure-android.sh`）はすでに
入っている。mDNS によるサーバー自動検出（`NsdManager`）は未着手。

[公式のプロジェクトセットアップ手順](https://developer.android.com/develop/xr/unity/setup)に従うこと。

### エミュレータでの検証

実機がなくても Android XR Emulator で Full Space・空間固定・発話まで確認できる。

1. **Android Studio Canary** を入れる（必須） — [Install and configure Android Studio for XR](https://developer.android.com/develop/xr/jetpack-xr-sdk/get-studio)
2. SDK Manager から `Android XR ARM 64 v8a` イメージを入れる
3. Device Manager で **XR Glasses** フォームファクタの AVD を作る

起動は `~/Library/Android/sdk/emulator/emulator -avd XR_Glasses`。`XR_Glasses`（光学シースルーの
模擬）を使うこと（→ [`mascot-android-xr.md`](./knowledge/mascot-android-xr.md)）。

### 実機（XREAL Aura）で最初に確認すること

| # | 項目 | 影響 |
|---|---|---|
| 1 | XREAL Aura の Android XR 対応レベル（Full Space の挙動、XR Glasses としての扱い） | 設計の前提そのもの |
| 2 | Aura を Mac の外部ディスプレイとして**同時**利用できるか | 排他なら作業面は物理画面に限定される |
| 3 | 光学シースルー越しに Mac の実画面が快適に読めるか | 読みづらいなら代替案の再検討材料 |
| 4 | UniVRM の URP版 MToon が Android XR + Vulkan で正常動作するか | 公式検証情報なし。崩れるなら Unlit 等での代替 |
| 5 | クライアント側にエンジンを置かずに音が出るか | サーバー合成の前提 |
| 6 | spring bone / expression のパフォーマンス | |

## 踏んだことの索引

| 症状・調べたいこと | ファイル |
|---|---|
| ビルドが落ちる / 通ったのに反映されない / テストが嘘をつく / シェーダーが真っ黒・ピンク / asmdef が解決しない / Unity の版を変えたい / CPU が張り付く | [`mascot-unity.md`](./knowledge/mascot-unity.md) |
| 透過しない / 窓の位置と大きさがおかしい / クリックが透けない・透けたまま / ドラッグで壊れる / Dock やメニューバーに出ない・出てしまう / ショートカットが効かない / ミュートが効かない | [`mascot-desktop.md`](./knowledge/mascot-desktop.md) |
| 設定パネルが出ない・作り直される / 右クリックが取れない / 値が保存されない・戻る / スライダーやポップアップの挙動 / ファイル選択 / サーバーに繋がらないときの表示 | [`mascot-settings.md`](./knowledge/mascot-settings.md) |
| モデルが映らない・背中が映る・小さい / 表情が変わらない / まばたき / 視線が合わない / モーションが T ポーズになる・固まる / 髪が流れる | [`mascot-vrm.md`](./knowledge/mascot-vrm.md) |
| 音が出ない・途切れる / 口が合わない / オーディオデバイスを掴んだまま / 接続が切れる / ack が届かない / 終了時に取りこぼす / JSON のパースがおかしい | [`mascot-speech.md`](./knowledge/mascot-speech.md) |
| Android でビルドが通らない / 白飛びする / LAN で繋がらない / XR で何も映らない・位置がおかしい / 背景が黒い / つまめない | [`mascot-android-xr.md`](./knowledge/mascot-android-xr.md) |
