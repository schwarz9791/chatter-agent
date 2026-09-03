# chatter-mascot — セットアップ

**Unity + UniVRM で1プロジェクト。** macOS デスクトップ（透過ウィンドウで常駐）と
Android XR グラス（XREAL Aura）の両方をここからビルドする。

> 前身 `cc-mascot-xr` の `xr-app/SETUP.md` からの移送。XR 専用の文書ではなくなったので、
> **Unity 共通**と **XR 固有**（→ [#25](https://github.com/schwarz9791/chatter-agent/issues/25)）に分けてある。
> 発話の契約は [`../../docs/protocol.md`](../../docs/protocol.md) が正。

---

## 環境

| | |
|---|---|
| Unity | **6**（開発は 6000.5.8f1） |
| レンダーパイプライン | **URP** |
| VRM | [UniVRM](https://github.com/vrm-c/UniVRM) |
| ウィンドウ制御（macOS） | [UniWindowController](https://github.com/kirurobo/UniWindowController) `com.kirurobo.uniwinc`（MIT） |
| JSON | `com.unity.nuget.newtonsoft-json` |
| XR（Android のみ） | `com.unity.xr.androidxr-openxr` + [Android XR Extensions for Unity](https://github.com/android/android-xr-unity-package) |
| グラフィックス API | Metal（macOS）/ Vulkan（Android XR） |
| 常駐（macOS のみ） | **自作の Objective-C プラグイン** `Assets/Plugins/macOS~/ChatterMascotNative/`（→ [#75](https://github.com/schwarz9791/chatter-agent/issues/75)） |

### 必要な Unity モジュール

- **Mac Build Support (IL2CPP)** — macOS Standalone のビルドに要る
- **Android Build Support**（OpenJDK / SDK & NDK 込み）— #25 で要る

### Xcode コマンドラインツール（macOS のみ）

メニューバー常駐（#75）のネイティブプラグインを `clang` でビルドするのに要る。

```bash
xcode-select --install   # 既に Xcode があれば不要
./scripts/build-native.sh
```

★ **`.bundle` は git に入っていない。** バイナリはレビューできず、`plugin/bin/*.mjs` のように
CI で「ソースと一致するか」を検証する手段が無い（clang の出力に再現性が無い）。
`./scripts/build.sh` が Unity より先に自動で作るので、普段は意識しなくてよい。

★ **バンドルが無くても起動する。** `DllNotFoundException` を握って警告1本
（`[Native] ChatterMascotNative.bundle が見つかりません…`）にしてあり、落ちるのは
**メニューバー常駐とミュートのショートカットだけ**。マスコットは出て喋る。

### パッケージの導入

`Packages/manifest.json` に入れてある（Package Manager の GUI でも同じ）。

```json
"com.kirurobo.uniwinc": "https://github.com/kirurobo/UniWindowController.git#upm",
"com.vrmc.gltf": "https://github.com/vrm-c/UniVRM.git?path=/Packages/UniGLTF#v0.131.2",
"com.vrmc.vrm": "https://github.com/vrm-c/UniVRM.git?path=/Packages/VRM10#v0.131.2",
"com.unity.nuget.newtonsoft-json": "3.2.1"
```

★ **VRM 0.x（`com.vrmc.univrm`）は入れない。** 扱うモデルが 1.0 なので要らない。
読み込みも `canLoadVrm0X: false` で閉じてある（失敗メッセージが具体的になる）。

★ **`JsonUtility` を使わないこと。** 契約は `audio` キーの**欠落**と `null` を区別することを
要求している（欠落 = #29 より前のサーバー、`null` = 正常な設定）が、`JsonUtility` には
その区別ができない。Newtonsoft の `JObject` なら判定できる。
→ `Assets/ChatterMascot/Runtime/Protocol/SpeechFrame.cs`

★ **プラグインのプラットフォームを絞ること。** UniWindowController の macOS ネイティブプラグインが
Android ビルドに混ざらないよう Plugin Inspector で macOS に限定し、XR パッケージは Android にだけ効かせる。

★ **自作プラグイン（`ChatterMascotNative.bundle`）の設定は手で直さないこと。**
`.bundle` が git に無いので、新規クローンには `.meta` しか無い。Unity が既定でインポートし直すと
**「すべてのプラットフォーム」に化ける**ことがある。直し方はコードに置いてある:

```bash
./scripts/run.sh ChatterMascot.EditorTools.NativePluginSettings.FixAll
```

---

## Player Settings（macOS Standalone）

UniWindowController の Inspector にある「Player Settings を直す」ボタンが見るのと同じ項目。
コードからも同じものを設定してある。

| 項目 | 値 | なぜ |
|---|---|---|
| `Fullscreen Mode` | **Windowed** | 透過ウィンドウの前提 |
| `Resizable Window` | オン | 同上 |
| `Default Is Full Screen` | **オフ** | 同上 |
| `Allow Fullscreen Switch` | **オフ** | 同上 |
| **`Default Is Native Resolution`** | **オフ** | ★ **オンだと下2行がそもそも効かない**（Inspector でもグレーアウトする） |
| `Default Screen Width` | **300** | ★ 下記 |
| `Default Screen Height` | **480** | 同上 |
| `Run In Background` | **オン** | 常駐して背面でも喋る。フォーカスを失って止まると発話が止まる |
| `Use Mac App Store Validation` | オフ | 透過をブロックしうる |
| `Mac App Sandbox` | オフ | 同上（Unity のビルドは entitlements を付けないので既定でオフ） |
| `API Compatibility Level` | .NET Standard 2.1 | `System.Net.WebSockets.ClientWebSocket` と `System.Diagnostics.Process` を使う（どちらも .NET Standard 2.1 で足りることを確認済み） |
| **`Disable Unity Audio`** | **ビルド時だけオン**（コミットされた値は**オフ**） | ★ 下記 |

### ウィンドウの大きさ

常駐マスコットなので小さく出す。**`Default Is Native Resolution` を切るのが本命**で、
オンのままだと `Default Screen Width/Height` は Inspector でもグレーアウトして効かない。

★ **ここの値が効くのは、ウィンドウを掴むまでの最初の数フレームだけ。**
そのあとは `Desktop/WindowGeometry.cs` が
**ポイントで持った既定（`DefaultWidthPoints` / `DefaultHeightPoints`）と、
自前の永続化 `~/.config/chatter-agent/mascot/window.json`** で置き直す。
**`Default Screen Width/Height` だけを変えても、窓の大きさは変わらない**
（食い違うと `SceneFixups` が `LogError` で教える）。

> ★ **以前は `WindowSizeKeeper` が枠なし化の +32 を打ち消していた。** いまは意図した
> 大きさを自前で持っているので、その累積そのものが起きない
> （→ [`../../docs/mascot.md`](../../docs/mascot.md)）。

**数値を仕様として扱わないこと。** 変えたら必ず実測する:

```bash
defaults delete tech.sukima.chatter-mascot   # ★ 前回終了時の大きさが焼き付いている
./scripts/build.sh
open Build/ChatterMascot.app --args -serverUrl ws://127.0.0.1:9
grep RecreateSurface "$HOME/Library/Logs/schwarz9791/Chatter Mascot/Player.log"
```

★ **`macRetinaSupport` は切らないこと。** 表示がぼやけるうえ、`UniWindowMoveHandle` の
Retina 座標系の手当てが前提にしている。

★ **設定パネルの「大きさ」もここを動かす**（#76）。倍率 0.5〜2.0 を
`DefaultWidthPoints x DefaultHeightPoints` に掛けてウィンドウごと変える。
`VrmStage` が `Screen.width/height` の変化を見てフレーミングし直すので、
**ウィンドウさえ変えればモデルは勝手に収まる**（カメラの前後＝`Headroom` は触らない。
触ると頭と足が対称に欠ける）。

★ **位置と大きさは `~/.config/chatter-agent/mascot/window.json` に
ポイントで永続化される**（#16）。**`PlayerPrefs` は使っていない** ——
macOS では `tech.sukima.chatter-mascot.plist` に書かれるので、上の
`defaults delete`（焼き付き消し）が自前の永続化ごと消してしまう。
**Unity が px を焼く場所と、我々が pt を書く場所を物理的に分けてある。**

★ **サイズ調整の UI は [#76](https://github.com/schwarz9791/chatter-agent/issues/76)。**
ここにあるのは既定値だけ。

### 音の出し方（プラットフォームで違う）

| | 再生の実体 | 無音時にデバイスを手放す方法 |
|---|---|---|
| **macOS** | `AfplaySpeechPlayer`（1発話 = 1プロセスで `afplay`） | プロセスが消えれば OS が解放する（実測 **0.5〜1秒**） |
| **Android / iOS** | `AudioClipPlayer`（Unity 内蔵オーディオ） | `AudioSettings.Mobile.StopAudioOutput()` |

★ **なぜ macOS で `AudioSource` を使わないか。** Unity 内蔵オーディオは**無音でも出力デバイスを
掴み続ける**（実測: `AudioSource.Play()` を一度も呼ばなくても起動から終了までずっと）。
Bluetooth イヤホンの電池を食う。macOS には手放す API が無い（→
[`../../docs/mascot.md`](../../docs/mascot.md)）。

★ **`Disable Unity Audio` はビルド時だけオンになる。** `scripts/build.sh` から呼ばれる
`BuildScript.BuildMacOS` が切り替えて、ビルド後に戻す。**コミットされている値はオフ**
（Android が Unity 内蔵オーディオで鳴らすため）。プロジェクト設定はプラットフォーム別に
持てないので、こうするしかない。

★ **これが無いと省電力にならない。** 外部プロセスで鳴らしても、Unity 内蔵オーディオが
有効なままだと Unity 側がデバイスを掴む。オンにしたビルドでは CoreAudio がプロセスを
認識すらしない（実測）。

★ **Editor（macOS）でも `afplay` 経路になる。** 本番と同じ経路を Play Mode で確かめられる。
ただし **Android 向けの開発を macOS の Editor でするときは、Editor と実機で再生の実体が変わる**。

### URP 側

| 項目 | 値 | なぜ |
|---|---|---|
| **`Supports HDR`** | **オフ** | ★ **透過の決め手はこれ1つ。** オンだと背景が黒いまま（→ [`docs/mascot.md`](../../docs/mascot.md)） |
| `Allow Post Process Alpha Output` | オン | 今の構成（Post Processing 無効）では効かないが、**有効にした瞬間に透過が壊れる**ので保険 |
| Camera の `Background Type` | Solid Color、**alpha 0** | 透過する背景 |
| Renderer の `Rendering Path` | **Forward** | MToon10(URP) に `UniversalGBuffer` パスが無い |
| Renderer の `MToon Outline Render Feature` | **追加する**（PC / Mobile とも） | ★ 無いと**アウトラインだけ出ない。エラーも出ない**。追加は Editor の GUI から |
| Renderer の `Screen Space Ambient Occlusion` | オフ | トゥーンの陰影と喧嘩する。常駐アプリで常時走るのも無駄 |
| Always Included Shaders | MToon10(URP) と UniUnlit | ★ 無いと**モデルは読めるのに真っ黒／ピンク**。`SceneFixups.FixAll` が入れる |

シーン側にも2つ要る。どちらも**透過ではなくクリック透過（Raycast ヒットテスト）のため**で、
欠けると毎フレーム `NullReferenceException` が出るだけで見た目には気づけない。

| | |
|---|---|
| `UniWindowController.currentCamera` | 空欄にしない（Inspector 上は正常に見える） |
| シーンに `EventSystem` | `RaycastAll` が要求する |

`./scripts/run.sh ChatterMascot.EditorTools.SceneFixups.FixAll` が後者を保証する。

★ **Editor 上では透過しない。** ビルドしないと確認できない（UniWindowController の制限事項）。
★ **透過が効かないときはビルドしたアプリのログを読む**（`~/Library/Logs/<company>/<product>/Player.log`）。

---

## 構成

```
Assets/StreamingAssets/
  vita.vrm                          同梱モデル（CC0。→ ../../NOTICE）
  idle_loop.vrma                    同梱アイドル（使うのは #59）
  trayTemplate.png / @2x            メニューバーのアイコン（cc-mascot 由来。→ ../../NOTICE）

Assets/Plugins/
  macOS~/ChatterMascotNative/       ★ `~` 付き。Unity は完全に無視する（ObjC のソース）
    CMNative.h                      ABI。★ 公開するものに CM_EXPORT を付ける
    CMEvent.m                       C# へ返す唯一の口（main thread の保証もここ）
    CMApp.m                         NSApplicationActivationPolicy / 版
    CMStatusItem.m                  NSStatusItem + NSMenu（★ キーもラベルも書かない）
    CMHotKey.m                      Carbon RegisterEventHotKey
  macOS/ChatterMascotNative.bundle  成果物（.gitignore。.meta だけコミットする）

Assets/ChatterMascot/
  Runtime/                          ChatterMascot.Runtime — 描画に依存しない層
    Protocol/   SpeechFrame.cs      フレームのパースと検証
                SpeechEpoch.cs      epoch / audio path の charset
    Playback/   PlaybackQueue.cs    ★ 状態機械。何をいつ取り、いつ鳴らし、いつ ack するか
                PlaybackState.cs / PlaybackOptions.cs / PlaybackEvent.cs
    Net/        SpeechClient.cs     WebSocket。繋ぐ・繋ぎ直す・ack を送る
                AudioFetcher.cs     GET /audio/<epoch>-<seq>.wav
    Audio/      WavDecoder.cs       WAV → AudioClip
                AudioClipPlayer.cs  AudioSource で1件ずつ鳴らす
                MuteState.cs        一時ミュートの状態（#75）
                MutedSpeechPlayer.cs ★ 「声だけ消す」デコレータ。ack は通常経路のまま出す
    Ui/         HotKeySpec.cs       "opt+m" ⇄ Carbon の (keyCode, modifiers)
                MenuModel.cs        ★ メニューの並びの唯一の持ち主
                MenuJson.cs         ネイティブとやり取りする JSON
    Settings/   SettingsStore.cs    ~/.config/chatter-agent/mascot/settings.json
                SettingsJson.cs / MascotSettings.cs
    Vrm/        AssetPath.cs        ★ .vrm / .vrma の探索順（純粋。下の表）
                VrmFraming.cs       ★ 画面に収まるカメラ距離（純粋）
    CommandLine.cs                  起動引数（-serverUrl / -vrm / -buildScene が共有）
    FrameRateBudget.cs              フレームレート上限の「戻す先」と「一時的に借りる」
    MascotRunner.cs                 ドライバ。コマンドを実行して結果をイベントで戻す
  Vrm/                              ChatterMascot.Vrm — UniVRM に依存する層（全プラットフォーム）
    VrmStage.cs                     読み込み・フォールバック・Collider・画角合わせ
    VrmAssetLoader.cs               候補を順に読む（UnityWebRequest。★ 自前の期限つき）
    VrmMaterialCheck.cs             シェーダーストリッピングの自己診断
    AssetEnvFactory.cs              Application を触る唯一の場所
  Desktop/                          ChatterMascot.Desktop — Editor + macOS/Windows のみ
    DragHandles.cs                  「Collider を持つものに UniWindowMoveHandle」
    VrmDragHandleBinder.cs          ★ MonoBehaviour にしない（下記）
    CursorGazeSource.cs             OS カーソル座標 → 視線（すべてポイントで閉じる）
    WindowGeometry.cs               ★ 位置と大きさをポイントで復元・永続化
    DragStateGuard.cs               ★ ドラッグ終了の取りこぼしでクリック透過が死ぬのを救う
    WindowProbe.cs                  座標系の実測（`-windowProbe` のときだけ動く）
    StatusItemBridge.cs             ★ メニューバー常駐の配線（判断は Runtime 側）
    Native/ChatterMascotNative.cs   DllImport。★ 可用性の判定は初回1回だけ
  Editor/
    SceneFixups.cs                  シーンとプロジェクトの修繕・検査
    MacPostBuild.cs                 ★ Info.plist に LSUIElement を書く（Dock に出さない）
    NativePluginSettings.cs         PluginImporter を出荷値にする
    BuildScript.cs / VrmProbe.cs
  Tests/Editor/                     EditMode テスト（状態機械が主）
```

★ **`ChatterMascot.Runtime` を「描画に依存しない層」のまま保つこと。** 契約・状態機械・
探索順・画角の計算がすべて **EditMode だけでテストできている**のは、この層が
`UnityEngine` の描画に依存していないから。Runtime の public API に UniVRM 型が漏れると、
`ChatterMascot.Tests.asmdef`（`overrideReferences: true`）から届かなくなって
**テストごと落ちる**。

★ **テストの件数を文書に書かないこと。** テストを足すたびにずれるうえ、
`grep -cE '\[Test\]'` で数えると `[UnityTest]` を取りこぼして**別の誤った数字**が出る。
実数が要るときは `./scripts/test.sh` の `total=` を見る（それがテストランナー自身の数）。

★ **asmdef の参照は推移しない。** `Editor → Desktop → Vrm → Runtime` と繋がっていても、
Editor が UniVRM の型を直接使うなら Editor の asmdef にも `VRM10` を書く。

★ **`Desktop` の `MonoBehaviour` をシーンに置かないこと。** Android では
そのアセンブリごと存在しないので、シーンに焼くと missing script になる
（→ [`../../docs/mascot.md`](../../docs/mascot.md)）。

### モデルとアニメーションの探索順

`.vrm` と `.vrma` で同じ形。**最初に読めたものを採る。**

| 順 | 出どころ | `.vrm` | `.vrma` | 対象 |
|---|---|---|---|---|
| 1 | 起動引数 | `-vrm <path>` | `-vrma <path>` | 全 |
| 2 | 環境変数 | `CHATTER_MASCOT_VRM` | `CHATTER_MASCOT_VRMA` | 全 |
| 3 | **設定パネルで選んだモデル**（#76） | `models/mascot.vrm`（**固定名**） | —— | デスクトップのみ |
| 4 | `Application.persistentDataPath/` | `model.vrm` | `idle.vrma` | 全 |
| 5 | `${XDG_CONFIG_HOME:-~/.config}/chatter-agent/` | `models/*.vrm` | `animations/*.vrma` | デスクトップのみ |
| 6 | 同梱（`StreamingAssets/`） | `vita.vrm` | `idle_loop.vrma` | 全 |

- 5 は `core/src/core/paths.ts` の `getRuntimeDir` と**同じ規則**。ユーザーから見て
  「chatter-agent の設定はここ1箇所」を保つため。辞書順の先頭を採る
- ★ **3 が要るのは 5 が辞書順だから。** 設定パネルは選んだファイルを `models/` へ**コピー**する
  （元ファイルを消しても動く）が、これが無いと `models/` に別のファイルがあるとき
  **選んだ方が反映されない**
- ★★ **3 は固定名。** 元の名前でコピーすると選び直すたびに積み上がるので、
  `models/mascot.vrm` に上書きする。**元の名前は表示のためだけ**に
  `settings.json` の `character.vrm` が覚える（探索には使わない）。
  手で `models/` に置いたファイルは触らない（→ `docs/mascot.md`）
- ★ **3 は起動引数・環境変数より下。** `-vrm` は切り分けの逃げ道
  （「設定が壊れていてもこれを付ければ必ず出る」）なので、設定より優先を保つ
- ★ **`.vrma` に対応する設定は無い。** モーションを選ばせる UI を作っていないため（意図的な非対称）
- ★ **Android には共有ファイルシステムが無い**ので 3 と 5 は落ちる
- ★ **`.app` を Finder から起動すると環境変数は空**（シェルを継承しない）。2 に頼らない
- ★ **`~/Downloads` / `~/Desktop` / `~/Documents` は macOS の TCC で止められる。**
  読み込みが返らないので、15秒で打ち切って次の候補へ進む（→ `docs/mascot.md`）
- 全部読めなければ Cube が出たままになる。**無地の Cube は「異常事態」の可視のシグナル**

★ **`AvatarSample_A.vrm` を公開物に使わないこと。** `allowRedistribution: false` /
`modification: prohibited` / `creditNotation: required` なので、コミット・
スクリーンショットの公開・デモに使えない。**差し替え検証で `-vrm` から読ませるだけ**にする。
`.gitignore` が `StreamingAssets/` 以外の `.vrm` を落とすようにしてあるが、最後は人の判断。

★ **設定パネルからの差し替えは次の起動から効く**（[#76](https://github.com/schwarz9791/chatter-agent/issues/76)）。
`VrmStage` は起動時に1回だけ読む作りで、差し替えるには spring bone・コライダ・ドラッグハンドル・
待機モーション・表情の結び直しが要る（→ `docs/mascot.md`）。

**判断は `PlaybackQueue` に集めてある。** イベントを入れるとコマンドの配列が返る純粋な関数で、
副作用（取得・再生・ack）は `MascotRunner` が実行して結果をイベントとして戻す。
テストが「このイベント列でこのコマンド列が出る」を配列比較で固定できるのはそのため。

`core/src/player/`（Node の発話 CLI）が**同じ契約の参照実装**なので、挙動に迷ったら
そちらと突き合わせる。

---

## 動かす

```bash
cd core && npm run start:server            # 合成エンジンはサーバーが起こす（#51）

cd apps/chatter-mascot
./scripts/test.sh                          # EditMode テスト
./scripts/build.sh                         # 本番シーン → Build/ChatterMascot.app
./scripts/run.sh <Editor のメソッド>        # シーンの修繕など
```

★ **どのスクリプトも Editor を閉じてから。** Unity はプロジェクトを排他ロックする。
★ **ビルドは Editor 経由（MCP など）で回さないこと。** モーダルダイアログが出た瞬間に
沈黙し、呼び出し側からは「ハングした」としか見えない（→ [`docs/mascot.md`](../../docs/mascot.md)）。

シーンの `MascotRunner` に接続先（既定 `ws://127.0.0.1:8570`）を入れて Play。
**クライアント側に合成エンジンは要らない**（#29）。音声は同じ authority から HTTP で取る。

### 踏みやすいところ

- **数十秒の無音は正常。** `final` はメッセージが閉じる瞬間に届き、`AskUserQuestion` の直前では
  数十秒に達する。「接続が死んだ」と誤判定しないこと
- **`503` を「失敗」に数えないこと。** エンジンを起動し忘れているだけで、溜まっていた発話が
  数百 ms で全部 ack されて消える
- **`seq` の飛びは欠落。** 埋める手段は無いのでそのまま進む
- **`epoch` が変わったら覚えていることを全部捨てる。** 重複排除の記憶、取得中の item、溜めている ack

すべて [`../../docs/protocol.md`](../../docs/protocol.md) の「クライアント側の責務」にある。

音まわりで踏みやすいのは別口:

- ★ **Editor の GUI からビルドすると `Disable Unity Audio` が切り替わらない。** できた `.app` は
  音は鳴るが**デバイスを掴みっぱなし**になる（＝この対応が丸ごと無効）。**ビルドは
  `scripts/build.sh` から行うこと**
- ★ **`AudioSettings.Mobile.StopAudioOutput()` は macOS でもコンパイルが通る。** 実行すると
  Unity が `Player.log` に `"implemented for iOS and Android only"` と1行出して**何もしない**。
  例外も出ないので、書いた側は効いているつもりになる
- ★ **`.app` を終了しても `afplay` は死なない。** `MascotRunner.OnDestroy` の `StopAll()` が
  止めている。消すとアプリを閉じた後も喋り続ける。
  **実測で確認済み**（発話中に `pkill -TERM` → 3秒後に `afplay` が 0個）
- ★ **一時ファイルは `$TMPDIR`**（`/var/folders/…/T/<company>/<product>/speech-<pid>/`）。
  `Application.temporaryCachePath` は macOS では `~/Library/Caches` ではない。
  **ディレクトリ名に pid が入る**のは、Editor の Play Mode とビルド済み `.app` を同時に
  動かしたときに、後発が先行インスタンスの再生中の WAV を消さないため

---

#### 設定パネルまわり（#76）

**キャラクターを右クリック**すると開閉する（もう一度押すと閉じる）。メニューバーの
「設定を開く…」からも同じパネルが開く。「Chatter Mascot について」は**別のダイアログ**で、
版とライセンス全文はそちらに出る。

★ **`⌃ + 左クリック`は効かない。** macOS の慣習だが常駐マスコットでは成立しない
（→ [`../../docs/mascot.md`](../../docs/mascot.md)）。副ボタンを出せない環境では
メニューバーの「設定を開く…」を使う。

★ **パネルが開かないときは、まず `Player.log` を見る。**

```
[Native] パネル0: frame=1041,-1111 520x664 visible=1 key=0 screen={{1041, -1169}, {1800, 1169}}
```

`パネル0` が設定、`パネル1` が「について」。`visible=1` なら**こちらは仕事を終えている** —— 見えないのは
「他のウィンドウの背後に居る」か「別のディスプレイに出ている」。
`frame` と `screen` がそれを教える。

★ **右クリックが効かないときは、この行が出ているかを見る。**

```
[Mascot] 右クリックを見張ります
```

出ていなければ見張りが据わっていない（`UniWindowController` の居ないシーンでは据えない）。
出ているのに効かないときは、**キャラクターの不透明な画素の上を押しているか**を疑う ——
判定はクリック透過の状態そのもの（`isClickThrough`）で、脚の間などの透けている場所では
下のアプリへ抜ける（→ [`../../docs/mascot.md`](../../docs/mascot.md)）。

★ **メニューバーはアクセシビリティから触れる**（`menu bar 2`。ターミナルに権限が要る）。

```bash
osascript -e 'tell application "System Events" to tell process "Chatter Mascot" \
  to click menu item "設定を開く…" of menu 1 of menu bar item 1 of menu bar 2'
```

★ **権限を与えたくないときは `-settingsProbe`。** 起動時にパネルを開く経路を用意してある
（`-quitProbe` / `-windowProbe` と同じ）。

```bash
"Build/ChatterMascot.app/Contents/MacOS/Chatter Mascot" -settingsProbe
```

★ **ショートカットは「記録」ボタンで実際にキーを押して決める。** 修飾キーを1つ以上入れること
（単独のキーはそのキーが全アプリで入力できなくなるので弾かれる）。
中止は**修飾キー無しの esc**。

#### 常駐まわり（#75）

設定は `~/.config/chatter-agent/mascot/settings.json`（`window.json` と同じディレクトリ）。

```json
{
  "version": 1,
  "audio": { "mute": false, "muteHotKey": "ctrl+opt+m", "volume": 1.0 },
  "ui": { "hideHotKey": "ctrl+opt+h" },
  "character": {
    "idleMotion": true,
    "cursorGaze": true,
    "blink": true,
    "vrm": ""
  }
}
```

★★ **「大きさ」もここに無い。** ウィンドウの大きさは `window.json` が持っていて、
スライダーはその写しでしかない（**現在の高さ ÷ 480** が倍率）。両方に持つと権威が2つになり、
ユーザーが窓を直接リサイズしたときにどちらが勝つのか説明できなくなる
（→ [`../../docs/mascot.md`](../../docs/mascot.md)）。

★ **音声スタイル・話す速さ・要約の ON/OFF もここに無い。** あれは core の
`~/.config/chatter-agent/config.json` が持ち、設定パネルは `PATCH /v1/config` 経由で書く
（→ [`../../docs/protocol.md`](../../docs/protocol.md) の「制御 API」）。
音量が Unity 側で速さが core 側なのは紛らわしいが理由がある —— 音量は**再生側のつまみ**で
合成し直さなくても効き、速さは**合成のパラメータ**で `audio_query` を変えない限り WAV が変わらない。

★ **`volume` は 0.0〜1.0**（パネルには 0〜100% で出る）。1.0 超えが効くのは macOS だけで
Android の `AudioSource.volume` は 0〜1 にクランプされるため、**XR と共有する設定に
プラットフォームで意味の変わる範囲を持たせない**方を採った（→ [`../../docs/mascot.md`](../../docs/mascot.md)）。

★ **既定を変えたら、自分の `settings.json` も直すこと。** ファイルの値は既定より優先されるので、
**コードの既定を変えても、既に書かれているキーには届かない**。しかも保存のたびに全キーが書き出される
（ミュートを1回トグルすればそれだけで固定される）ので、**開発中に既定を変えると自分の環境にだけ古い値が残る**。

実際に踏んだ: 既定を `opt+m` → `ctrl+opt+m` に変えたのに `⌥M` のままで、
新設の `ui.hideHotKey` だけが新しい既定になった（ファイルに無いキーは既定が使われるため）。
片方だけ変わったように見えるのがこの形。

```bash
# 直すか、消して既定に戻す（mute の状態も一緒に消える）
$EDITOR ~/.config/chatter-agent/mascot/settings.json
```

★ 動いている最中でも直る。★ **1秒ポーリングで拾って登録し直す**（`mtime`+`size` のスタンプ比較）ので、
アプリを再起動しなくてよい。効いたかどうかは `[Mascot] ミュートのショートカット: ⌃⌥M` で分かる。

★ **既定と同じ値でも書き出すのは意図的。** ファイルを開けば何が設定できるか分かる方を採った
（設定 UI が入るのは [#76](https://github.com/schwarz9791/chatter-agent/issues/76)。それまではテキスト編集が唯一の手段）。
**設定ファイルはユーザーの意思の記録**なので、既定の変更で勝手に上書きしない。

★ **既定に `⌥` 単体や `⌘⌥` を選ばないこと。** `⌥M` は `µ` を、`⌥H` は `˙` を**実際に入力する**ので
全アプリからその文字を奪い、`⌘⌥M` / `⌘⌥H` は macOS 標準の「すべてをしまう」「ほかを非表示」と衝突する。
`RegisterEventHotKey` はどちらも「登録できた」と言ってくる（→ [`../../docs/mascot.md`](../../docs/mascot.md)）。

★ **アイコンが出ないときはログを見る。** `[Native] ステータスバー: item=あり …` が出ていれば
こちら側は仕事を終えている。出ないのは **Thaw / Bartender / Ice のようなメニューバー管理ツールが
隠している**ため（あの種のツールは画面外の負の座標へ移動させて隠す）。ツール側で表示に切り替える。


★ **`.app` の中の `.bundle` を手で差し替えない。** コード署名が壊れて `open` から
起動できなくなる（`Player.log` が空のまま終了する）。ネイティブだけ直したときも
`./scripts/build.sh` を通すこと（2回目以降は5秒で終わる）。

★ **`open` は同じ bundle id のアプリが動いていると新しいプロセスを起こさない。**
別ワークツリーのビルドと並べて試すときは `open -n`。

★ **メニューはアクセシビリティから触れない**（`menu bar 2` も `AXExtrasMenuBar` も
`missing value`）。実機確認で開くなら座標クリック:

```bash
osascript -e 'tell application "System Events" to click at {3196, 15}'
```

★ **Dock に出ないことの確認は `lsappinfo`。**

```bash
lsappinfo list | grep -A2 "pid = <pid>"   # type="UIElement" なら Dock に出ない
```

★ **ObjC 側の `NSLog` はどこにも残らない**（→ [`../../docs/mascot.md`](../../docs/mascot.md)）。
ネイティブの診断は `CMEmitLog` で C# へ返し、`[Native]` 付きで `Player.log` に出す。

## XR 固有（→ [#25](https://github.com/schwarz9791/chatter-agent/issues/25)）

### なぜ Unity なのか（Jetpack XR ではなく）

Jetpack XR の SceneCore では **VRM を本来の見た目で扱えない**。

| | SceneCore 1.0.0-beta02 時点 |
|---|---|
| `VRMC_vrm` / `VRMC_materials_mtoon` / `VRMC_springBone` | いずれも**非対応**（[対応 glTF 拡張の公式リスト](https://developer.android.com/develop/xr/jetpack-xr-sdk/add-3d-models#gltf-extensions)） |
| モーフターゲット操作 API | `morph` / `blendShape` / `setWeight` が**全ソースで0件** |
| ボーン操作 | `GltfModelNodeFeature.localPose` / `localScale` に setter あり |
| アニメーション制御 | `pauseAnimation()` / `seekAnimation(t)` / `setAnimationSpeed()` |

VRM の表情（`happy` / `blink` / `aa` など）はモーフターゲット実装なので、SceneCore では直接操作できない。
**UniVRM が expressions / spring bone / lookAt をすべて備える唯一のルート**であり、UniVRM は Unity 前提。

### なぜ Full Space なのか

> **Home Space** — It does not support spatial panels, **3D models**, or an app's spatial environments.
> **Full Space** — One app runs at a time, with no space boundaries. **All other apps are hidden.**
>
> — [Design Foundations | Android XR](https://developer.android.com/design/ui/xr/guides/foundations)

Unity / OpenXR / WebXR アプリは[そもそも Full Space でしか動かない](https://developer.android.com/design/ui/xr/guides/openxr)ので選択の余地はない。

ターゲットの **XREAL Aura は光学シースルー**なので、Full Space が隠すのは「他の Android アプリ」だけで、
レンズ越しに見える物理的な Mac の画面は実光として残る。**この一点が本プロジェクトの成立根拠**なので、
実機入手後に最優先で検証すること。

### Android 側の必須設定

`AndroidManifest.xml`:

- `<uses-permission android:name="android.permission.INTERNET" />`
- `<property android:name="android.window.PROPERTY_XR_ACTIVITY_START_MODE" android:value="XR_ACTIVITY_START_MODE_FULL_SPACE_MANAGED" />`
- `<uses-feature android:name="android.software.xr.immersive" />`

**ネットワーク**（見落としやすい）:

- **`ACCESS_LOCAL_NETWORK` はランタイム権限**で、**targetSdk 37 以降で必須**。ローカルアドレスへの
  TCP 接続・mDNS・`.local` 解決がすべて対象 — [Local network permission](https://developer.android.com/privacy-and-security/local-network-permission)
- **`ws://`（非TLS）を使うなら cleartext 許可が必要**。Android 9 以降デフォルト無効 —
  [Network Security Configuration](https://developer.android.com/privacy-and-security/security-config)
- 接続先は**まず手動 IP 入力**でよい。後から `NsdManager`（`android.net.nsd`）で mDNS 検出を足す

★ **エンジンの `--host 0.0.0.0 --cors_policy_mode all` は不要になった**（#29）。
叩くのは同じ Mac 上の `chatter-agent-server` だけ。

[公式のプロジェクトセットアップ手順](https://developer.android.com/develop/xr/unity/setup)に従うこと。

### エミュレータでの検証

実機がなくても Android XR Emulator でモデル表示までは確認できる。

1. **Android Studio Canary** を入れる（必須） — [Install and configure Android Studio for XR](https://developer.android.com/develop/xr/jetpack-xr-sdk/get-studio)
2. SDK Manager から `Android XR ARM 64 v8a` イメージを入れる
3. Device Manager で **XR Glasses** フォームファクタの AVD を作る

**できること**: モデル表示・アニメーション・パネル配置の確認、Passthrough トグル、Environment Dimming
**できないこと**: 実際のフレームレート、視野角での見え方、ハンドトラッキング精度

### 実機（XREAL Aura）で最初に確認すること

**最優先**: Full Space に入った状態で、レンズ越しに Mac の画面が問題なく見えるか。
ここが崩れると設計の前提そのものが変わる。

| # | 項目 | 影響 |
|---|---|---|
| 1 | XREAL Aura の Android XR 対応レベル（Full Space の挙動、XR Glasses としての扱い） | 設計の前提そのもの |
| 2 | Aura を Mac の外部ディスプレイとして**同時**利用できるか | 排他なら作業面は物理画面に限定される |
| 3 | 光学シースルー越しに Mac の実画面が快適に読めるか | 読みづらいなら代替案の再検討材料 |
| 4 | UniVRM の URP版 MToon が Android XR + Vulkan で正常動作するか | 公式検証情報なし。崩れるなら Unlit 等での代替 |
| 5 | **クライアント側にエンジンを置かずに音が出るか**（#29 の未検証事項） | サーバー合成の前提 |
| 6 | spring bone / expression のパフォーマンス | |

### 代替案（優先度低）: Home Space + 2Dパネル

「本当にそこにいる」感を最優先するなら Full Space 案を採る。ただし他の Android アプリと
並べたくなった場合の逃げ道として記録しておく。

Unity を **XR機能なしの通常 Android アプリ**としてビルドすれば Home Space の2Dパネルとして動き、
Android版 Claude アプリ等と並べられる。引き換えに空間的な実在感は失われる。
また**パネル背景を透過できるかは未検証**で、できない場合はキャラの周りに四角い板が見える。

---

## 参考: `~/dev/android-xr-test`

Jetpack XR（Unity ではない）の動く Gradle 構成。Unity 採用なら直接の再利用はほぼないが、
**Android XR まわりのバージョン組み合わせと既知の罠**が参考になる。

- `implementation(extensions-xr)` にすると実行時クラッシュする。**`compileOnly` にすること**
- **`PlanarEmbeddedSubspace` は埋め込み領域が不透明な板として描画される**
- **glTF は非同期ロードでロード完了コールバックがない**
- glTF 本来のマテリアルは環境光に依存して暗く沈む
