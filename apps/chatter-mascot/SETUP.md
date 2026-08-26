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

### 必要な Unity モジュール

- **Mac Build Support (IL2CPP)** — macOS Standalone のビルドに要る
- **Android Build Support**（OpenJDK / SDK & NDK 込み）— #25 で要る

### パッケージの導入

`Packages/manifest.json` に入れてある（Package Manager の GUI でも同じ）。

```json
"com.kirurobo.uniwinc": "https://github.com/kirurobo/UniWindowController.git#upm",
"com.unity.nuget.newtonsoft-json": "3.2.1"
```

★ **`JsonUtility` を使わないこと。** 契約は `audio` キーの**欠落**と `null` を区別することを
要求している（欠落 = #29 より前のサーバー、`null` = 正常な設定）が、`JsonUtility` には
その区別ができない。Newtonsoft の `JObject` なら判定できる。
→ `Assets/ChatterMascot/Runtime/Protocol/SpeechFrame.cs`

★ **プラグインのプラットフォームを絞ること。** UniWindowController の macOS ネイティブプラグインが
Android ビルドに混ざらないよう Plugin Inspector で macOS に限定し、XR パッケージは Android にだけ効かせる。

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
| `Default Screen Width` | **250** | ★ 下記 |
| `Default Screen Height` | **400** | 同上 |
| `Run In Background` | **オン** | 常駐して背面でも喋る。フォーカスを失って止まると発話が止まる |
| `Use Mac App Store Validation` | オフ | 透過をブロックしうる |
| `Mac App Sandbox` | オフ | 同上（Unity のビルドは entitlements を付けないので既定でオフ） |
| `API Compatibility Level` | .NET Standard 2.1 | `System.Net.WebSockets.ClientWebSocket` と `System.Diagnostics.Process` を使う（どちらも .NET Standard 2.1 で足りることを確認済み） |
| **`Disable Unity Audio`** | **ビルド時だけオン**（コミットされた値は**オフ**） | ★ 下記 |

### ウィンドウの大きさ

常駐マスコットなので小さく出す。**`Default Is Native Resolution` を切るのが本命**で、
オンのままだと `Default Screen Width/Height` は Inspector でもグレーアウトして効かない。

★ **入れた値がそのまま出るのは `WindowSizeKeeper` があるからで、素では出ない。**
`UniWindowController` が枠なし化した瞬間にタイトルバーぶん（実測 **+32**）が
コンテンツ領域へ編入され、それが終了時に永続化されて、**起動のたびに +32 で伸びていく**
（600x1632 はこうして育ったもの）。`Desktop/WindowSizeKeeper.cs` が起動直後の大きさへ
戻すことで打ち消している（→ [`../../docs/mascot.md`](../../docs/mascot.md)）。
**数値を仕様として扱わないこと。** 変えたら必ず実測する:

```bash
defaults delete tech.sukima.chatter-mascot   # ★ 前回終了時の大きさが焼き付いている
./scripts/build.sh
open Build/ChatterMascot.app --args -serverUrl ws://127.0.0.1:9
grep RecreateSurface "$HOME/Library/Logs/schwarz9791/Chatter Mascot/Player.log"
```

★ **`macRetinaSupport` は切らないこと。** 表示がぼやけるうえ、`UniWindowMoveHandle` の
Retina 座標系の手当てが前提にしている。

★ **サイズ調整の UI と位置の永続化は [#16](https://github.com/schwarz9791/chatter-agent/issues/16)。**
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
Assets/ChatterMascot/
  Runtime/
    Protocol/   SpeechFrame.cs      フレームのパースと検証
                SpeechEpoch.cs      epoch / audio path の charset
    Playback/   PlaybackQueue.cs    ★ 状態機械。何をいつ取り、いつ鳴らし、いつ ack するか
                PlaybackState.cs / PlaybackOptions.cs / PlaybackEvent.cs
    Net/        SpeechClient.cs     WebSocket。繋ぐ・繋ぎ直す・ack を送る
                AudioFetcher.cs     GET /audio/<epoch>-<seq>.wav
    Audio/      WavDecoder.cs       WAV → AudioClip
                AudioClipPlayer.cs  AudioSource で1件ずつ鳴らす
    MascotRunner.cs                 ドライバ。コマンドを実行して結果をイベントで戻す
  Tests/Editor/                     EditMode テスト（状態機械が主）
```

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
