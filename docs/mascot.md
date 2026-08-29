# `apps/chatter-mascot/` を触るとき

表示側アプリ（Unity + UniVRM）の作業規約。**セットアップ手順は
[`../apps/chatter-mascot/SETUP.md`](../apps/chatter-mascot/SETUP.md)、発話の契約は
[`protocol.md`](./protocol.md) が正。** ここには「実装で踏んだ罠」だけを書く。

## 実測で潰れた前提

### ★ Unity の既定はフレームレート無制限。常駐アプリでは必ず上限を入れる

テンプレートは `vSyncCount: 0`（VSync 無効）で、`Application.targetFrameRate` の既定は
`-1`（無制限）。**両方が効いていないと、Cube 1個のシーンでも CPU 261% / GPU 93.5% に行く**
（実測。スレッド62、1時間で CPU 時間 2:22:41）。

`Update()` が毎秒数千回回るので、その頻度で次が全部動く:

- `MascotRunner.Update()` → `SpeechClient.Tick()` — 毎回 `DateTimeOffset.UtcNow`（システムコール）
- UniWindowController の `HitTestCoroutine` — ネイティブのカーソル座標取得 + `EventSystem.RaycastAll`
- `AudioClipPlayer.PlayAsync` の `while (isPlaying) await Task.Yield()`

★ **症状は「アプリが重い」より先に「接続が繰り返し切れる」として出る。**
メインスレッドが飽和すると `ReceiveAsync` の継続が遅れ、**サーバーの ping に pong を
返せなくなる**。サーバー側（`core/src/server/wsServer.ts`）はそれを見て切る:

```
[WS] No pong, terminating dead connection   → socket.terminate()
[WS] Backpressure (NB buffered), closing    → socket.close(1013, "too slow")
```

実際に `Player.log` に「切断されました」が15回出ていて、CPU を見るまで原因が分からなかった。
**切断のログには必ず close コードを出すこと**（→ `SpeechClient.DescribeClose`）。
1013 なら「こちらが遅い」、close フレーム無しなら「pong が返せていない」と読める。

★ **`vSyncCount` ではなく `Application.targetFrameRate` で絞る。**
`targetFrameRate` は VSync が有効だと**無視される**ので、`vSyncCount: 0` のままの方が
確実に効く（透過ウィンドウで VSync が効くかも確かめていない）。

★ **既定の 30fps はデスクトップ限定。** Android XR ではヘッドセットのリフレッシュレートに
合わせる必要がある（→ #25）。`MascotRunner` の Inspector で変えられる。

★ **「リップシンクが入ったら 30fps で足りるか見直す」という宿題は #58 で閉じた。
結論は 30fps 据え置き。** → 下の「30fps で口が足りるかの決着（#58）」

### #59 時点の実測: フレームレート上限ありでの常駐 CPU

冒頭の「Cube 1個で無制限なら CPU 261%」と対比できる値。**VRM 表示 + VRMA（待機モーション）+
spring bone + 毎フレームの手続き計算（呼吸・重心移動・視線）を全部載せた状態**で、
`targetFrameRate = 30` のとき **CPU 14.3%**（実測 n=6、9秒間隔、ウィンドウ 300x480）。

★ **この値は「フレームレート上限が効いている」前提の値。** 上限を外したときにどこまで
増えるかは測っていない。

視線の中立とフレーミングを直した後に測り直すと **CPU 13.2%**（実測 n=5、9秒間隔、
ウィンドウ 300x480、`targetFrameRate = 30`）。上の 14.3% は視線の中立とフレーミングを
直す**前**の値なので、条件が違う2つの数字として並べて読むこと。

ウィンドウの既定サイズは #59 で **250x400 → 300x480** に変更した（縦横比 5:8 は維持）。
この文書の他の節にある「250x400」の実測値は、変更前に取った記録としてそのまま残してある。

EditMode テストは #59 で件数が増えた（**具体的な件数はここには書かない**——
件数は変わっていくので `./scripts/test.sh` の `total=` を都度確認すること）。

### ★ Unity は無音でも macOS の出力デバイスを掴み続ける

常駐アプリなので、実使用時間のほとんどが「無音」になる。そのあいだ Bluetooth の
A2DP リンクが張られたままになり、**イヤホンの電池を食う**。

実測（2026-08-25 / macOS 26.6.2 / 既定の出力は Bluetooth イヤホン）:

| | 掴んでいる時間 | 手放すまで |
|---|---|---|
| CLI Player（`afplay` を1発話ごとに spawn） | 発話中だけ | 発話終了から **0.5〜1秒** |
| Unity | **起動から終了までずっと** | 手放さない |

Unity 側は `ws://127.0.0.1:9`（listen していないポート）を焼いたビルドで測ったので、
**`AudioSource.Play()` を一度も呼んでいない**。それでも 60秒間 119サンプルすべてで
`kAudioProcessPropertyIsRunningOutput` が 1 だった。`-batchmode -nographics` で
シーンをビルドしているだけの Unity も掴んでいる。

**macOS には、Unity 内蔵オーディオで手放す手段が無い:**

| 手段 | なぜ使えないか |
|---|---|
| `AudioSettings.Mobile.StopAudioOutput()` | **iOS / Android 専用。** macOS でもコンパイルは通り例外も出ないが、実行すると Unity が `"implemented for iOS and Android only"` とログに出して**何もしない**（実測: 呼んだ後も 40秒間 `proc=1` のまま）。★ **Android では効くので #25 では使える** |
| `Enable Output Suspension` | **Editor 専用**（公式マニュアル明記）。スタンドアロンには効かない |
| `Disable Unity Audio` | 静的なプロジェクト設定。ランタイムに切り替えられない |
| `AudioSettings.Reset()` | 「再初期化」であって解放ではない |
| `AudioListener.pause` / `volume = 0` | DSP を止めるだけ。出力ストリームは開いたまま |

★ **`AudioSettings.Mobile` の存在は見落としやすい。** `AudioSettings` 直下ではなく
`Mobile` のネストクラスにあり、macOS ビルドターゲットでも**コンパイルが通ってしまう**ので、
書いた側は効いているつもりになる。手がかりは `Player.log` の1行だけ。

内蔵オーディオは FMOD を組み込んだものだが、その System ハンドルが公開されていないので
`System::mixerSuspend()`（「オーディオハードウェアの使用を手放す」）を呼ぶ口が無い。
Native Audio Plugin SDK は DSP エフェクトを挿す仕組みで、デバイスの開閉には触れない。
**「`AudioSource` はそのままで無音時だけ解放する」プラグインは原理的に作れない。**
解放するにはエンジンごと差し替えるしかない（→ `ISpeechPlayer`）。

#### 測り方（同じ測定をやり直すとき）

★ **CoreAudio の `kAudioProcessPropertyIsRunningOutput`（macOS 14.2+）を pid ごとに読む。**
`kAudioDevicePropertyDeviceIsRunningSomewhere` はシステム全体の値なので、ブラウザや
通知音で 1 になる。**プロセス単位でないと帰属が取れない。**
`kAudioHardwarePropertyTranslatePIDToProcessObject` で pid → AudioObjectID を引く。

★ **「自分が黙っていれば無音」ではない。** サーバーは1つで、**複数の Claude Code
セッションの発話が同じキューに入る**。1回目の測定はこれで無効になった（別セッションの
発話が Unity に流れて、実際に鳴っていた）。**サーバーに繋がない状態で測ること**:

```bash
open Build/ChatterMascot.app --args -serverUrl ws://127.0.0.1:9
```

ポート 9（discard）は listen していないので絶対に繋がらない。`MascotRunner.Start()` が
起動引数で `serverUrl` を上書きする（`IsValidServerUrl` は後段なので、不正な値を渡しても
「動いて見える死体」にはならず `enabled = false` で止まる）。

★ **測定のためにシーンを複製しないこと。** 一度やって消した。`Mascot.unity` の完全コピーで
差分は `serverUrl` の1行だけ（797行の重複）だったが、問題は重複そのものではなく
**失敗が見えないこと** —— #17 で VRM が入った瞬間、#16 で UniWindowController が付いた瞬間に
複製は本番を代表しなくなるが、**変わらずビルドでき、変わらず計測でき、ただ別のアプリを
測っているだけになる**。この設計の根拠にした CoreAudio の実測値が、そこで静かに無効化される。

★ **測定でクライアントを2台繋がないこと。** → [`docs/protocol.md`](./protocol.md) の
クライアント側の責務6。速いクライアントの ack が、遅いクライアントのまだ喋っていない
entry を消す。CLI Player を動かしたまま Unity を繋いで実際に踏んだ。

### ★ MCP 経由のビルドは、モーダルダイアログが出た瞬間に沈黙する

`Unity_RunCommand`（unity-mcp）から `BuildPipeline.BuildPlayer` を呼ぶと、
**保存確認ダイアログが出た瞬間に応答が返らなくなる**。人がダイアログを閉じるまで、
呼び出し側からは「ハングした」としか見えない。

症状の見分けがつかないのが厄介で、実際に **30分気づけなかった**。切り分けに使えたのは:

- シェーダーコンパイラのプロセスが12個いるのに **CPU が全部 0.0%**
- `Temp/` の更新時刻が止まっている
- **`Logs/Editor.log` にビルドの行が1行も出ていない**（成功したビルドは必ず
  `Building Player` 以降を書く）

**ビルドとテストは `-batchmode` で回す**（→ `apps/chatter-mascot/scripts/`）。
batchmode はダイアログを出さないので、この失敗の仕方をしない。

★ **Editor を開いたままだと batchmode は失敗する。** Unity はプロジェクトを排他ロックする
（`Temp/UnityLockfile`）。スクリプト側でも起動前に検査している。

★ **Editor 経由でしかできないこと**（シーン編集、パッケージ解決、設定変更）は MCP で行う。
そのときは **`AssetDatabase.SaveAssets()` とシーン保存を先に済ませる**。ダイアログの芽を潰しておく。

### ★ `Screen.*` はバッキング px、ネイティブのウィンドウ API はポイント

`macRetinaSupport: 1` なので **`Screen.width/height` は描画ピクセル**、
`UniWindowController.windowSize`（→ `LibUniWinC.SetSize`）は **NSWindow のポイント**。
**同じウィンドウについて別の数を返す。**

実測（2026-08-26。窓を内蔵 Retina パネルへ移して戻しただけ）:

```
Metal RecreateSurface: surface size 250x400
[Mascot] フレーミング: 250x400 aspect=0.625 bounds=(1.39, 1.73, 0.55) distance=2.39
Metal RecreateSurface: surface size 500x800            ← 内蔵 Retina パネルへ移した
[Mascot] フレーミング: 500x800 aspect=0.625 bounds=(1.39, 1.73, 0.55) distance=2.39
Metal RecreateSurface: surface size 250x400            ← 外部 4K へ戻した
```

**`aspect` も `bounds` も `distance` も変わらないまま `Screen.*` だけが倍**になっている。

★ **混ぜると「打ち消し」が「倍化」に変わる。** `WindowSizeKeeper` は当初
`Screen.*` で読んで `windowSize` へそのまま書いていた。Retina 2x で起動すると
`_intended` が (500,800) px、それをポイントとして書くので **1000x1600 px** の窓になり、
+32 どころか**起動ごとに倍**へ育つ。**scale 1 の外部ディスプレイでは px == pt なので
この症状は出ない** —— 最初の実測をそこで取ったせいで見落とした。

→ **実測した結果を書くときは、どのディスプレイで測ったかを必ず添えること。**

手当ては**換算率をコントローラ自身から測る**こと（`#if UNITY_STANDALONE_OSX` も
`Screen.dpi` も使わない）:

```csharp
var client = _controller.clientSize;                        // pt
var scale = Mathf.Max(1f, Mathf.Round(Screen.height / client.y));
_controller.windowSize = new Vector2(_intended.x / scale, _intended.y / scale);
```

実測（修正後）:

| 起動先 | `Screen`(px) | `clientSize`(pt) | `scale` | 実ウィンドウ |
|---|---|---|---|---|
| 外部 4K（1x） | 250x432 | 250x432 | **1** | 250x400 pt |
| 内蔵 Retina（2x） | 250x464 | 125x232 | **2** | 125x200 pt |

どちらも3回連続で起動して**増えない**。

★ **`clientSize` で「意図した大きさ」を読み直す形にはできない。**
`UniWinCore.AttachMyWindow` は `UniWindowController.Update()` の中で、
**枠なし化も同じ `Update()` の中**（`UpdateTargetWindow` → `SetTransparent` →
`LibUniWinC.SetBorderless`）。だから `Start()` では `clientSize` が **(0,0)**、
最初の `LateUpdate` では**もう膨らんでいる**。捕まえられるのは `Start()` の `Screen.*` だけ。

★ **`Screen.SetResolution` に寄せない。** styleMask が戻った場合、
`UniWindowController` は `IsActive` が立っている限り**枠を剥がし直さない**
（再適用は `if (!IsActive)` のときだけ）。「+32 が残る」より
「**タイトルバーが出たまま常駐**」の方が悪い。

#### ★ 物理的な大きさはディスプレイのスケールで変わる（未解決。→ #16）

`defaultScreenWidth/Height` も、Unity が永続化する `Screenmanager Resolution *` も
**バッキング px**。だから:

- **Retina 2x で起動すると物理的に半分**になる（250x400 px = **125x200 pt**）
- **Retina で終了すると、次に 4K で開いたとき倍になる。** Retina 上の `Screen.*` は
  500x800 px なので、それが永続化され、1x のディスプレイでは **500x800 pt** の窓として開く。
  **実測で確認した**（`WindowSizeKeeper` は「起動直後の大きさ」を守るので、これは打ち消さない）

`WindowSizeKeeper` が直せるのは**同じディスプレイでの累積**だけ。
ディスプレイをまたいだときに物理サイズを保つには、**ポイントで意図した大きさを
自前で永続化する**しかない —— それは位置の永続化・マルチモニタと同じ設計なので
[#16](https://github.com/schwarz9791/chatter-agent/issues/16) でまとめて扱う。

### ★ ウィンドウは起動のたびに縦へ 32 伸びる（`WindowSizeKeeper` で打ち消している）

「いつのまにか窓が縦長になっている」の正体。実測（macOS 26.6.2 / #56）:

1. Unity が前回終了時のクライアント高さを復元する
2. `UniWindowController` が枠なし化し、**タイトルバーぶん（32）がクライアント領域へ編入される**
   —— ウィンドウの外形は縮まないので、クライアントは 32 大きくなる
3. その大きくなった値が終了時に永続化される
4. 次の起動で 1 に戻る

```
起動1: surface 250x400 → 250x432
起動2: surface 250x432 → 250x464
起動3: surface 250x464 → 250x496
```

**既定を 600x800 にしていた頃に 600x1632 まで育っていた。**

`Desktop/WindowSizeKeeper.cs` が、**起動直後（枠なし化の前）に見えていた大きさ**へ
戻すことで打ち消す。これを入れてから3回連続で起動して 250x400 のまま動かないことを確認した。

★ **縮んだ側は追いかけないこと。** ユーザーが手で小さくしたのを戻すと操作を奪う。
打ち消したいのは「勝手に増える」ぶんだけ。

★ **見張る時間を短くしないこと**（既定5秒）。枠なし化は起動直後の数フレームで起きるが、
VRM の読み込みでメインスレッドが詰まると実時間では後ろへずれる。長い側の害は
「起動直後に手で広げても戻される」だけ。

★ **これは対症療法。** ウィンドウの大きさ・位置・永続化の設計は
[#16](https://github.com/schwarz9791/chatter-agent/issues/16)。

### ★ `UnityWebRequest.timeout` は `file://` に効かない

macOS の TCC で保護されたフォルダ（`~/Downloads` / `~/Desktop` / `~/Documents`）の
モデルを `-vrm` で渡すと、リクエストが**返らず・エラーも出さず・`timeout` も発火しない**
（実測。30秒に設定していたが1分待っても何も起きなかった）。

**症状が凶悪**で、探索順の1段目で止まるので:

- モデルが出ない
- **`Player.log` に1行も増えない**（読み込み開始のログすら出ない）
- **同梱モデルへのフォールバックにも落ちない**

「動いて見える死体」そのもの。TCC のダイアログも出ないので、権限が原因だと気づけない。

手当ては `Vrm/VrmAssetLoader.cs` の**自前の期限**（15秒）。`Task.WhenAny` で打ち切って
`request.Abort()` し、次の候補へ進む。最悪でも同梱モデルまでは落ちる。
ログには権限の可能性を名指しで書く。

★ **`request.timeout` の方も残してある。** HTTP には効くので、両方要る。

### ★ ランタイムの Collider は「奥行き」に合わせる。幅に合わせない

ランタイムロードしたモデルには Collider が無いので `VrmStage` が
`CapsuleCollider` を1本起こす。**クリック透過（`hitTestType: 2` = Raycast）と
ドラッグの両方がこれを見る**ので、大きさを間違えると窓全体がクリックを食う。

★ **`bounds.extents.x` を半径に使わないこと。** VRM 1.0 はレストポーズが T ポーズ必須なので、
これは**広げた腕の長さ**になる。実測（vita.vrm / 250x400 / 145 px/m）:

| 半径の取り方 | 半径 | 画面上の直径 | ウィンドウ 250px に対して |
|---|---|---|---|
| `max(extents.x, extents.z)`（腕） | 0.695m | 202px | **81%** |
| **`extents.z`（奥行き）** | 0.275m | 80px | 32% |

前者だと**腕の高さ以外の左右の空白まで掴む**ので、クリック透過がほぼ意味を失う。
「キャラのキワでクリックが抜けない」という形で出る。

★ **引き換えに、伸ばした腕の上では掴めない**（クリックが下へ抜ける）。
[#59](https://github.com/schwarz9791/chatter-agent/issues/59) でアイドルモーションが入って
腕が下りれば差はほぼ消える。**部位ごとに Collider を分けるのは
[#16](https://github.com/schwarz9791/chatter-agent/issues/16)。**

★ **実際に必要だったのは、Collider の計算式そのものの変更ではなく ControlRig の
生成順序の修正だった。** #59 実装当初は VRMA を適用しても腕が実際には下りず
（→「ControlRig は `Vrm10Instance` の transform が単位回転であることを暗黙に前提にしている」）、
この節の予告は宙に浮いていた。その順序を直して腕が実際に下がって初めて、上の差が
実機で解消した。半径に使う `extents.z`（奥行き）は腕の上げ下げでは変わらない軸なので、
**`Renderer.bounds` が姿勢を反映しない問題（→「`SkinnedMeshRenderer.bounds` は姿勢を
反映しない」）の影響は受けていない**——影響を受けたのは自動フレーミング側（上の節）だけ。

★ **`hitTestType` を `Opacity`（ピクセルのアルファ判定）にすればシルエットと完全に一致する**が、
公式が重いと明記している方式で、常駐アプリで毎フレーム走るコストを測り直す必要がある。
また「掴める領域とドラッグできる領域が定義上ずれない」という現行の規律が崩れる。

### ★ VRM は放っておくと背中が映る（仕様の読みで決めない）

#56 の issue 本文は「モデルの glTF 座標でつま先が足首より +Z。glTF→Unity は Z 反転なので
**Unity 上ではモデルが −Z を向く** → Main Camera は `(0,0,-4)` で +Z を見るので
**顔がこちらを向く。180°回転は不要**」としていた。

**実機では背中が映った。**

手当ては `Runtime/Vrm/VrmOrientation.cs`。**仕様の読みではなくボーンの並びから
実際の向きを出す** —— VRM 1.0 はレストポーズが T ポーズ必須なので、読み込み直後の
上腕2本の位置から正面が求まる:

```csharp
var rightToLeft = leftUpperArm - rightUpperArm;   // 高さのぶれは落とす
var forward = Vector3.Cross(Vector3.up, rightToLeft.normalized);
```

Unity は左手系で「+Z を向いた人物の右手が +X 側」に来るので、これで正面が出る。
あとは `−Z`（カメラの方）との符号付き角度ぶんだけ Y 軸で回す。
**真横を向いたモデルでも正面へ向け直せる**ので、非準拠のモデルにも耐える。

★ **回すのは bounds を測る前。** `Renderer.bounds` はワールド軸に沿った箱なので、
回したあとで測り直さないとカメラ距離がずれる。

★ **照明は回さない。** シーンの Directional Light（Euler `(50, -30, 0)`）は
光が +Z 方向へ進む向きなので、**−Z を向いた面＝カメラ側が照らされる**。
モデルの正面をカメラへ向ければ、そのまま顔に光が当たる。

### ★ ControlRig は `Vrm10Instance` の transform が単位回転であることを暗黙に前提にしている

**症状**: 待機モーション（VRMA）で**両腕が頭の上に上がったまま**固まる。T ポーズは
解消しているので「動いている」ようには見える。

**切り分け**: `idle_loop.vrma` のノード階層を直接歩いて t=0 のワールド位置を計算したところ、
`leftHand y=82.7` < `hips y=90.4` < `leftUpperArm y=133.7` で、**ファイル側は腕を下ろした
姿勢**だった。つまり適用側の反転。

**原因**: `Vrm10Runtime` は**遅延生成**で、`Vrm10.LoadBytesAsync` も `InitializeAtRuntime` も
`FinalizeAsync` も `Runtime` に触らない。放っておくと `VrmStage.LateUpdate` の
`_instance?.Runtime?.SpringBone?.RestoreInitialTransform()` が初回アクセスになり、
**`FaceCamera`（→「VRM は放っておくと背中が映る」）がモデルを 180° 回した後**に
ControlRig が作られる。

- `Vrm10ControlBone` は `ControlBone` を**ワールド単位回転**で作る（`ControlBone.position =
  controlTarget.position` と位置だけ合わせる。`SetParent` も引数1つの overload なので
  回転が保たれる）＝正規化姿勢は**ワールド軸**で表される
- 一方 `_initialTargetGlobalRotation = controlTarget.rotation` には**モデルの 180° が入る**
- `ProcessRecursively` の `Inverse(G) * ControlBone.localRotation * G` が両者を突き合わせるので
  **Y 軸まわり 180° ぶん食い違い、Z 軸まわりの回転（＝腕の上下）が反転する**。実測した VRMA の
  上腕は Z 軸まわり約 75°（`quat z ≈ ∓0.6`）なので症状と一致

**手当て**: `VrmStage.Adopt` で `FaceCamera` の**前**に `_ = instance.Runtime;` を置いて、
回す前に ControlRig を作らせる。

★ **#56 単独では表面化せず、#59 で VRMA / ControlRig を使い始めて初めて出た。**
`_ = instance.Runtime;` は**副作用の無い行に見えるので、消されると静かに再発する**。

### ★ `SkinnedMeshRenderer.bounds` は姿勢を反映しない（T ポーズの腕幅で測り続ける）

`updateWhenOffscreen == false`（既定）のとき、Unity は**メッシュに焼かれた静的な bounds**
を transform で変換して返すだけで、**ボーンを動かしても縮まない**。VRM 1.0 はレストポーズが
T ポーズ必須なので、その幅は**常に「広げた腕」**になる。

★ **`updateWhenOffscreen = true` で直してはいけない。** 毎フレーム CPU スキニングで bounds を
測り直すことになり、常駐アプリの電力予算を壊す（→「Unity の既定はフレームレート無制限」）。
**Humanoid のボーン位置から測る**（`VrmBounds.OfBones`）。

実測（同梱 `vita.vrm` + `idle_loop.vrma`、300x480）:

```
[Mascot] フレーミング: 300x480 aspect=0.625 bounds=(1.54, 1.66, 0.33) distance=2.51 支配軸=水平   ← VRMA 適用前 / Renderer.bounds
[Mascot] フレーミング: 300x480 aspect=0.625 bounds=(0.62, 1.66, 0.37) distance=1.76 支配軸=垂直   ← VRMA 適用後 / ボーンから測定
```

幅 1.54m → **0.62m**、距離 2.51 → **1.76**（キャラが約3割大きく映る）、支配軸が
**水平 → 垂直**。

### ★ VRM 1.0 は T ポーズ必須。自動フレーミングの支配軸がそれで決まる

`Camera.fieldOfView` は `m_FOVAxisMode` に関わらず**常に垂直 FOV**で、水平は
`tan(hFov/2) = tan(vFov/2) * aspect` で決まる。**縦長のウィンドウほど横が狭い。**

同梱モデル `vita.vrm` の `Renderer.bounds` は **1.39m × 1.73m**（`VrmProbe` の実測）。
横幅を決めているのは**広げた腕**なので、250x400（5:8）では:

| | 必要距離 | |
|---|---|---|
| 垂直 | 1.50 | |
| **水平** | **1.93** | ← こちらが採用される |

`VrmFraming.Solve` は距離に `headroom`（既定 1.1）を掛け、さらに `+ extents.z` する
（bounds の手前面が near clip に刺さらないように）ので、実際の距離は
`1.9260 * 1.1 + 0.275 = 2.394` —— 実行ログの `distance=2.39` と一致する。
可視高は `2 * 2.394 * tan(30°) = 2.764m` なので、**縦の占有率は約 62%**。**これは想定どおり。**

★ **`headroom` と `+extents.z` を落として手計算しないこと。** 落とすと 77% / 190px/m という
別の数値が出て、**同じ文書の別の行（145 px/m）と食い違う**。実行ログが `distance=` を
出しているので、必ずそちらと突き合わせること。
[#59](https://github.com/schwarz9791/chatter-agent/issues/59) でアイドルモーションが入って
腕が下りれば `extents.x` が縮み、**支配軸が水平から垂直へ移って同じウィンドウのまま
占有率が上がる**。いま bounds の比（325x400）に合わせると、#59 の後に横が余る。

★ **どちらの軸で決まったかを必ずログに出すこと。** 「小さく映る」の原因が
腕の張り出しなのか身長なのかは、これが無いと切り分けられない。

★ **`Start()` の1回では足りない。** `UniWindowController` が起動直後にウィンドウを
作り直すので、その時点の `Screen.*` は最終値ではない。`resizableWindow: 1` なので
実行中にも変わる。**`OnRectTransformDimensionsChange` は `RectTransform` 専用**で
3D カメラには届かず、Unity にウィンドウリサイズの通知は無いので**ポーリングが唯一の手段**。

★ **`camera.aspect` に代入しないこと。** 一度代入すると `ResetAspect()` を呼ぶまで固定される。

★ **上の予告どおりにはならなかった（#59 で確認）。** 「腕が下りれば `extents.x` が縮む」は、
**`Renderer.bounds` が姿勢を反映しない**（→「`SkinnedMeshRenderer.bounds` は姿勢を反映
しない」）ため外れた。VRMA を適用して実際に腕が下りても、`Renderer.bounds` は**メッシュに
焼かれた静的な T ポーズの箱のまま**で `extents.x` は縮まない。**支配軸を水平から垂直へ
動かすには、bounds の測り方自体を Humanoid のボーン位置ベースへ変える必要があった**
（`VrmBounds.OfBones`）。変えて初めて、幅 1.54m → 0.62m・距離 2.51 → 1.76 で支配軸が
水平から垂直へ反転した（実測は上の節を参照）。

### ★ T ポーズの腕をフレーミングの箱に入れない（起動直後だけ小さく映るポップ）

**症状**: 起動直後、キャラが小さく映ったあと、VRMA が効いて約2秒後に一段大きくなる
（実機での指摘）。実機ログ:

```
[Mascot] フレーミング: … bounds=(1.54, 1.66, 0.33) distance=2.51 支配軸=水平   ← 読み込み直後
[Mascot] フレーミング: … bounds=(0.62, 1.66, 0.37) distance=1.76 支配軸=垂直   ← VRMA 適用後
```

**原因**: VRM 1.0 はレストポーズが T ポーズ必須なので、読み込んだ直後（VRMA が非同期で
効くまでの数百 ms〜数秒の間）は**腕を広げた姿勢のまま bounds を測る**。上の節でボーンベースの
測定へ切り替えたが、**その測定自体は正しく「そのときの姿勢」を反映してしまう**ので、
VRMA が適用されて腕が下りるまでのあいだだけ広い箱のまま——**測定方法の話ではなく、
測定するタイミングの話**。

**手当て**: **腕（`UpperArm` / `LowerArm` / `Hand` と全ての指ボーン）をフレーミングの箱から
除外する。肩（`Shoulder`）は残す**——胴の幅を決めているのはこちら。

- T ポーズでも腕が下りていても**肩幅で決まるので値がほぼ変わらない** → ポップが消える
- 支配軸は最初から垂直になる
- ★ **引き換えに、腕を大きく広げる VRMA を置くとフレームからはみ出しうる。**
  余白（`boneBoundsMarginMeters`、既定 0.1m）がある程度吸収するが、限界がある
- ★ **測り直しの窓（読み込み後5秒間・毎秒）は残すこと。** 腕を外しても、髪や裾の
  spring bone が落ち着くまでの微差はあるし、ユーザーが別の `.vrma` を置いたときの保険になる

### ★ `VrmProbe` の出力は「ランタイムと同じ関数」でなければならない

`Tests/Editor/VrmFramingTests.cs` の定数 `Vita()` は **`VrmProbe.Report` の出力を貼ったもの**で、
そのことがテスト側にもコメントで書いてある。だから**probe が出す数値の作り方が
ランタイムからズレると、テストがランタイムのもう作らない箱を守り始める**。

実際にズレていた。上の節でランタイムを `VrmBounds.Of(Renderer)` から
`VrmBounds.OfBones(ボーン)` へ切り替えたのに、`VrmProbe` は `Of(Renderer)` のまま出し続け、
そこには「**VrmStage が実行時に使うのと同じ関数の出力**」というコメントが付いたままだった。
結果、`Vita()` は幅 **1.39m**（T ポーズの腕を含む Renderer bounds）を守り続け、
`PortraitWindowIsDominatedByTheTPose` は**ランタイムが二度と生成しない箱**に対して
支配軸＝水平を固定していた。**マージンや `IsFramingBone` の除外リストを壊しても、
このテスト群は何も検出できない状態だった**（PR #69 のレビューで判明）。

**手当て**: `VrmStage.MeasureBounds` を `public static Bounds MeasureBounds(Vrm10Instance, float)`
にして、`VrmProbe` から**同じ関数**を呼ぶ。マージンも `VrmStage.DefaultBoneBoundsMarginMeters`
を共有する。

★ **ボーンを集めるループを probe 側に書き写して「揃える」のでは駄目。** 書き写した瞬間に
「同じ関数の出力」が「いまのところ同じ結果になる別実装」に変わり、除外リストやマージンを
片方だけ直したときに黙ってズレる。**probe が出す値は、テストの定数の出所であるという一点で、
ランタイムと同一の呼び先でなければならない。**

★ **probe はシーンを経由しない**ので `[SerializeField]` の値は取れない。シーンで
`boneBoundsMarginMeters` を既定から変えたら、probe の出力は実行時の箱と食い違う。

`vita.vrm` の実測（2種類とも出す）:

```
  bounds size: (1.39, 1.73, 0.55)          ← Renderer.bounds の合成。ランタイムは使わない
  bounds W/H: 0.803
  frame bounds size: (0.35, 1.66, 0.31)    ← VrmStage.MeasureBounds。テストに貼るのはこちら
  frame bounds W/H: 0.214
```

W/H が 0.214 なので、ウィンドウのアスペクト（300/480 = 0.625）より細い。
**支配軸は垂直で、ウィンドウの幅を変えてもカメラ距離は動かない**。
ここが水平に戻ったら、箱に腕が混ざっている。

★ **「同じ関数」でも「同じ箱」にはならない。入力の姿勢も揃える必要がある。**
`MeasureBounds` を共有しても、それだけでは足りなかった。`VrmStage.Adopt` は
`FaceCamera`（モデルをカメラへ向けて回す）→ `MeasureBounds` の順で測るのに対し、
`VrmProbe` は**回さずに測っていた**。ボーンのワールド位置から組む箱は
ワールド軸に沿うので、回す前と後では別の箱になる。

★ **`size` の一致は証拠にならない。** 180° 回転では AABB の `size` は不変で、
変わるのは `center` の x / z の符号だけ。`Frame` のログはこれまで `size` しか
出していなかったので、「実機の1フレーム目が probe の出力と一致した」を
根拠にしてしまったが、**一致した量がそもそも判別できない量だった**
（PR #69 の再レビューで判明）。いまはログに `center` も出すようにしてある。

★ **90°の倍数でないヨーでは `size` そのものが変わる。** 点群の AABB は向きに
依存するので、90° 回るモデルでは x と z が入れ替わるだけでは済まず、
90°の倍数でないヨーでは符号反転でも済まない。`YawToFaceCamera` は
`SignedAngle` で任意角を返す（doc に「真横を向いているモデルでも
正面へ向け直せる」とある）ので、これは仮定ではなく仕様の射程内。

**手当て**: `VrmStage.FaceCamera` を `public static float FaceCamera(Vrm10Instance)`
にして適用したヨーを返すようにし、`VrmProbe.Report` が `Describe` の直前に
これを呼んで同じ staging を通してから測るようにした。

`vita.vrm` の実測（staging を揃えた後の出力）:

```
  faceCamera yaw: 180 度
  frame bounds size: (0.35, 1.66, 0.31)     ← size は不変（180° 回転のため）
  frame bounds center: (0.00, 0.80, -0.02)  ← center.z の符号だけ反転した
```

予測どおり `size` は変わらず、`center` の z の符号だけが変わった
（`Vita()` は `center` を `(0f, 0.80f, 0.02f)` から `(0f, 0.80f, -0.02f)` に貼り直した）。

**実行時のログと突き合わせた結果**（`-vrm` で `vita.vrm` を明示して起動）:

```
probe :  frame bounds size (0.35, 1.66, 0.31)  center (0.00, 0.80, -0.02)
実行時:        bounds (0.35, 1.66, 0.31)       center (0.00, 0.80, -0.02)
```

★ **`center` が一致したことが証拠になる。** 修正前の probe は `+0.02` を出していた。
`size` は修正の前後どちらでも一致したので、**`size` だけを見ていた限りこの食い違いは
永久に見えなかった**。

★ **probe が読むモデルも揃えること（→ [#64](https://github.com/schwarz9791/chatter-agent/issues/64)）。**
`VrmProbe.ProbeEnv` は `PersistentDataPath` しか潰しておらず、
`HasUserConfigDirectory` は `OSXEditor` で `true` のままだった。つまり探索順に
`~/.config/chatter-agent/models/*.vrm` が生きていて、**自分のモデルを置いて動作確認する**
という普通の使い方をしているだけで、probe が同梱の `vita.vrm` ではなくそちらを測る。
出力はテストの定数の出所なので、**マシンによって基準値が変わるのに変わったことに気づけない**。
`ProbeEnv` で `HasUserConfigDirectory = false` も落とす。

★ **潰すのは probe だけ。** アプリ側の探索順（`AssetEnvFactory.Current()`）は変えないので、
`~/.config/chatter-agent/models/` に置いたモデルはこれまでどおりアプリが読む。
probe だけを同梱モデルに固定したいのであって、差し替えの仕組みを塞ぎたいわけではない。

★ **環境変数（探索順2）も潰すこと。同じ穴が2つ空いていた。** `AssetEnvFactory.Current()` は
`Variables = ReadEnvironment()` を入れるので、`HasUserConfigDirectory` を落としただけでは
`CHATTER_MASCOT_VRM` が生きたままになる。`scripts/run.sh` は開発者のシェルから Unity を
起動するので、**`export` しっぱなしの値をそのまま継承する**。

★ **こちらのほうが気づきにくい。** ユーザー設定ディレクトリは「置いたファイル」なので
消せば直るが、環境変数は**シェルに残った状態**で、`env` を見に行くまで存在に気づけない。
しかも [`SETUP.md`](../apps/chatter-mascot/SETUP.md) は環境変数について
「**`.app` を Finder から起動すると環境変数は空**（シェルを継承しない）」と書いている ——
**アプリでは効かないが probe では効く**という、いちばん見つけにくい向きの非対称。

★ **起動引数（探索順1）は残す。** `-vrm <path>` は**その実行に対して明示的に渡すもの**で、
probe を別モデルで回すための意図的な口。周囲の状態に左右されない点が 2〜4 と決定的に違う。

```console
# 環境変数は無視される（＝ 探索順2 を潰した）
$ CHATTER_MASCOT_VRM=/tmp/decoy.vrm ./scripts/run.sh ChatterMascot.EditorTools.VrmProbe.Report
[VrmProbe] 読みます: .../Assets/StreamingAssets/vita.vrm

# 起動引数は効く（＝ 探索順1 は残す）
$ ./scripts/run.sh ChatterMascot.EditorTools.VrmProbe.Report -vrm /tmp/decoy.vrm
[VrmProbe] 読みます: /tmp/decoy.vrm
```

★ **根っこは「doc が主張していることをコードが実行していなかった」こと。** `ProbeEnv` の doc は
最初から「ここは**同梱と起動引数だけ**見れば足りる」と書いていたのに、実際に潰していたのは
`PersistentDataPath`（探索順3）だけだった。**その食い違いが、そのまま2回のバグになった**
（探索順4 = #64、探索順2 = その直後）。`AssetPath` の探索順の表に段を足したら、
`ProbeEnv` も見直すこと。

★ **探索順3 も、実は「消えていなかった」（PR #69 の再レビューで判明）。** `env.PersistentDataPath = "";`
は「この段を消す」つもりの1行だったが、`AssetPath.Join` は

```csharp
private static string Join(string left, string right)
{
    if (string.IsNullOrEmpty(left)) return right;   // ← 左辺が空でも右辺をそのまま返していた
    ...
}
```

だったので、`Join("", "model.vrm")` は **`"model.vrm"`（相対パス）をそのまま返す**。`Add` は
空文字しか弾かないので、この相対パスは探索順3の候補としてそのまま積まれる。`File.Exists("model.vrm")`
は Unity のカレントディレクトリ（プロジェクトルート）基準で評価されるので、**同梱（探索順5）より
上位で当たる**。つまり `PersistentDataPath = ""` は「探索順3を消す」のではなく、
**「探索順3の基準ディレクトリをプロジェクトルートに変える」だけ**になっていた。

再現（プロジェクトルートに `model.vrm` を置くだけで再現する）:

```console
$ cp Assets/StreamingAssets/vita.vrm ./model.vrm
$ ./scripts/run.sh ChatterMascot.EditorTools.VrmProbe.Report
[VrmProbe] 読みます: model.vrm      ← 同梱ではなくこちらを読む
```

★ **同じ穴は、`Join` の左辺が空になりうる箇所すべてに空いていた。**

| 箇所 | 左辺が空になる条件 | 直す前の結果 |
|---|---|---|
| `Enumerate` 探索順3 | `PersistentDataPath = ""`（`ProbeEnv` が意図的にやる） | 相対 `model.vrm` |
| `Enumerate` 探索順5 | `StreamingAssetsPath` が空 | 相対 `vita.vrm` |
| `RuntimeDirectory`（探索順4の基準） | `HomeDirectory` が空かつ `XDG_CONFIG_HOME` 未設定 | 相対 `.config/chatter-agent` |
| `Add` の `~/` 展開 | `HomeDirectory` が空 | `~/x.vrm` が相対 `x.vrm` になる |

だから `ProbeEnv` 側で段ごとに空文字を弾く小細工を足すのではなく、**`Join` そのものに
「空の基準からは候補を作らない（左辺が空なら `null` を返す）」を1つ入れて**、4箇所を一括で閉じた。

★ **アプリ側の穴も同時に閉じた。** `AssetEnvFactory.Home()` は例外時に `""` を返す実装なので、
`HomeDirectory` が空になる経路は probe に限らず実在する。`Join` を直したことで、
`RuntimeDirectory`（探索順4）と `~/` 展開（起動引数・環境変数）は、アプリ側でも
相対パスに化けなくなった。

### ★ 同じ `TryGetBoneTransform` が、同一フレーム内で実行順によって別の値を返す

`VrmCharacter`（実行順 0）と `VrmPoseAccent`（11005）が**どちらも視線の原点（目ボーン）を
測っていて**、両方のコメントが「同じ点を使うことが重要」と宣言していた。だが実際には
別の点を返していた:

| 呼び出し元 | 実行順 | そのとき目ボーンが持っている姿勢 |
|---|---|---|
| `VrmCharacter.LateUpdate` | 0 | **前フレームの** `VrmPoseAccent` が乗せた頭の回転が入ったまま |
| `VrmPoseAccent.LateUpdate` | 11005 | `Vrm10Instance`（11000）の `ControlRig.Process()` が書き戻した後＝**アクセント抜き** |

目ボーンは頭の子なので、頭の回転で位置が動く。**「同じ関数を呼んでいるから同じ点」は
実行順を跨ぐと成立しない。**

**手当て**: 測るのは1フレームに1回だけ（実行順 0 の `VrmCharacter.LateUpdate`）にして、
`TryGetCachedGazeOrigin` でキャッシュを配る。`TryGetGazeOrigin` は `private` に戻す。

★ ズレの大きさ自体は小さい（頭から目までのオフセット約 0.06m × 基準の下向き約 11.4° の sin
＝ **1cm ほどと見積もれる**。実測はしていない）。直した理由は挙動ではなく、
**doc が宣言している不変条件がコード上は成立していなかった**こと。

★ キャッシュは「アクセント込み」の位置なので、`VrmPoseAccent` がこれを使うと弱い帰還路になる
（頭が下を向く → 目が下がる → 次フレームの基準の下向きがわずかに小さくなる）。**負帰還**で
利得は 0.02 程度と見積もれるので数フレームで収束する —— これも見積もりであって実測ではない。
**視線が微振動するようならここを疑うこと。**

### ★ シーンの YAML に無い `[SerializeField]` は 0 にならない（が、揃えておくこと）

Unity のデシリアライズは、**シーン YAML にキーが無いフィールドについてフィールド初期化子の
値をそのまま保つ**。0 で潰されはしない。

実証: `VrmCharacter.neutralAimFraction = 0.6f` も `VrmStage.boneBoundsMarginMeters = 0.1f` も
`Mascot.unity` に載っていなかったが、実機ビルドでどちらも効いていた（視線の中立は下がり、
フレーミングは1フレーム目から `distance=1.74` で安定していた）。Inspector にも普通に表示され、
そこで編集して保存した時点で初めて YAML に載る。

★ **それでも揃えておくこと。** 揃っていないと、いま効いている値がシーンの側なのか初期化子の
側なのかが YAML を見ただけでは判別できない。「フィールドを足したあとシーンを保存し直して
いないだけ」の状態が積み上がる。

★ **`SceneFixups` に「調整用の値」の復旧を足さないこと。** `Assign` は `Object` 参照専用で、
float 版を足すと **`FixAll` を回すたびに Inspector で実機に合わせた値が既定へ戻る**。
`neutralAimFraction` は「実機で見て調整する口」として置いたものなので、復旧処理が上書きするのは
目的と正反対になる。**シーンに1行足すだけにする。**

### ★ シェーダーストリッピングは「読めるのに真っ黒／ピンク」で例外を出さない

`UrpVrm10MToon10MaterialImporter` は `Shader.Find` でシェーダーを引くが、
**シーンのマテリアルから参照されないシェーダーはビルドから落ちる**ので、
ランタイムロードでは確実に踏む。UniVRM 側に自動対策は無い。

`SceneFixups.EnsureAlwaysIncludedShaders()` が `GraphicsSettings.asset` の
`m_AlwaysIncludedShaders` に2本を冪等に足す:

- `VRM10/Universal Render Pipeline/MToon10`
- `UniGLTF/UniUnlit`

★ **`Universal Render Pipeline/Lit` は絶対に入れない**（UniVRM 公式が
「ビルド時間が過大になる」と明記）。同梱モデルは 15 マテリアル全部が MToon なので不要。

★ **シェーダーは名前で引くこと**（パスではなく）。実際のパスは issue に書かれていた
`UniUnlit/Runtime/UniUnlit.shader` ではなく **`UniUnlit/Shaders/UniUnlit.shader`** だった。
`Shader.Find` なら Editor が AssetDatabase から引くのでパスの変更に強い。

診断は2段:

1. **読み込みより前**に `Shader.Find` が null かを見る（`VrmMaterialCheck.WarnIfShadersStripped`）
2. **読み込み直後**に `RuntimeGltfInstance.Materials` を回して
   `shader == null || !shader.isSupported || shader.name == "Hidden/InternalErrorShader"` を数える

★ **予想に反してビルド時間はほとんど伸びなかった。** MToon URP の `UniversalForward` は
`multi_compile` が13本あるので大幅に遅くなると見込んでいたが、実測は
**Unity 自身の計測で 97秒**（UniVRM 導入前は壁時計 145秒。ただしそちらは
初回のアセットインポートを含む）。**遅くなる前提で設計しないこと。**

### ★ URP のレンダラーは Forward にする

MToon10(URP) に **`UniversalGBuffer` パスが無い**（`UniversalForward` / `MToonOutline` /
`DepthOnly` / `DepthNormals` / `ShadowCaster` / `XRMotionVectors` のみ）。
Deferred のままだと未検証の経路に入る。UniVRM 公式の URP サンプルも Forward。

| | #12 時点 | #56 で |
|---|---|---|
| `PC_Renderer.asset` の `m_RenderingMode` | `2`（Deferred） | **`0`（Forward）** |
| 同 `ScreenSpaceAmbientOcclusion` | 有効 | **無効**（トゥーンの陰影と喧嘩する。常駐アプリでフルスクリーンパスが常時走るのも無駄） |
| `Mobile_Renderer.asset` | **既に Forward で Renderer Feature も空** | そのまま |

★ **`Mobile_Renderer` は最初から Forward だった。** issue #56 の表は「同上（Deferred）」と
書いていたが実態と違う。**Mobile で要るのは Renderer Feature の追加だけ。**

★ **SSAO を切ると `PC_RPAsset.asset` と `UniversalRenderPipelineGlobalSettings.asset` にも
差分が出る。** Unity がシェーダーバリアントの prefiltering と「実行時に要る設定」の一覧を
組み直すため（`ScreenSpaceAmbientOcclusion*Resources` が実行時リストから落ちる）。
**手で書いた差分ではない。**

★ **`MToonOutlineRenderFeature` の追加は Editor の GUI で行うこと。**
`m_RendererFeatureMap` のハッシュをコードで組むのは脆い。`SceneFixups` は
**検査して `LogError` するだけ**（`AssertRendererFeatures`）。
無いとアウトラインだけ出ず、**エラーも出ない**。

★ **`MToonOutlineRenderFeature` は `#if MTOON_URP` で囲まれている。** 定義しているのは
`VRM10.MToon10.Runtime.asmdef` の `versionDefines`（`com.unity.render-pipelines.universal`）なので、
URP が入っていれば自動で立つ。

### ★ アウトラインは「出ているのに見えない」ことがある

`MToonOutlineRenderFeature` を追加しても**見た目が変わらなかった**。壊れているのではなく、
**同梱モデルの線が細すぎて見えないだけ**だった。

`vita.vrm` の実測:

| | |
|---|---|
| `outlineWidthMode` | `worldCoordinates`（15 マテリアル中 **4つだけ**） |
| `outlineWidthFactor` | **0.00075 m（0.75mm）** |
| 付いているもの | Face / Body の SKIN、Shoes / Tops の CLOTH |
| **付いていないもの** | **髪（HAIR 4種）**、目、眉、まつげ、口 |

250x400 のウィンドウでは画面上 **約 145 px/m**（`400 / 2.764m`）なので、
0.75mm は **約 0.11 px**。拡大しても見えない。
**シルエットで最も目立つ髪に線が無い**のも効いている。

★ **「アウトラインが出るか」を目視の合否条件にしないこと。** このモデルでは
出ていても見えない。効いているかを確かめるには**一時的に線を太らせる**:

```csharp
foreach (var m in _gltf.Materials)
    if (m != null && m.HasProperty("_OutlineWidth")) m.SetFloat("_OutlineWidth", 0.02f);
```

0.02（シェーダーの上限は 0.05）まで上げると顎・首・肩に黒い線がはっきり出る。
**確認したら必ず戻すこと。**

★ **プロパティ名は `_OutlineWidth`。** glTF 側のキーは `outlineWidthFactor` だが、
シェーダーのプロパティ名は違う（`_OutlineWidthFactor` は**存在しない**）。
`Material.HasProperty` は false を返すだけで**エラーにならない**ので、
名前を間違えると「効いていない」と「そもそも設定できていない」の区別がつかない。
一度これで空振りした。

★ **Renderer Feature の追加は Unity Editor の GUI から。** ★ **Unity Hub で
「プロジェクトを開く」ではなく「新規作成」してしまうと、当然この機能は出てこない** ——
`Add Renderer Feature` の一覧に URP 標準の6つしか並ばないときは、
**開いているプロジェクトを疑うこと**（`ps` で `-createproject` が出ていれば新規作成されている）。

★ **`scripts/*.sh` のロック検査はパスの前方一致。** プロジェクトの**中に**別の Unity
プロジェクトができていると、そちらを開いているだけで
「Unity Editor がこのプロジェクトを開いています」と言われて何も動かせなくなる。

### ★ asmdef の参照は推移しない（4回踏んだ）

`ChatterMascot.Editor` → `ChatterMascot.Vrm` → `VRM10` と繋がっていても、
Editor 側が UniVRM の型を直接使うなら **Editor の asmdef にも `VRM10` を書く**必要がある。

実際に踏んだ順:

1. `ChatterMascot.Vrm` に `UniGLTF` はあるが **`UniGLTF.Utils` が無い** →
   `IAwaitCaller` が「参照されていないアセンブリで定義されている」
2. `ChatterMascot.Editor` から `VrmProbe` が `UniVRM10` / `UniGLTF` を使う → 同じエラー
3. `Kirurobo.UniWindowController` を `ChatterMascot.Desktop` へ移したので、
   Editor の references も **`ChatterMascot.Desktop` に差し替え**が要った
4. `target.Runtime.VrmAnimation = vrma;`（`vrma` は `Vrm10AnimationInstance`）と書いたら
   `error CS0012: The type 'ITimeControl' is defined in an assembly that is not referenced`。
   `Vrm10AnimationInstance : MonoBehaviour, IVrm10Animation, ITimeControl` の
   **`ITimeControl` が `Unity.Timeline`（`Unity.Timeline` アセンブリ）にあり**、
   `ChatterMascot.Vrm.asmdef` は `VRM10` は参照していても `Unity.Timeline` は
   参照していなかった。手当ては `references` に `"Unity.Timeline"` を足す

★ **4番目はこれまでの3回と質が違う。** 1〜3は「自分が名前を書いた型」のアセンブリが
足りないケースだったが、4番目は**自分が名前すら書いていない `ITimeControl`**
（使っている型が実装している基底インターフェース）で落ちている。`using` を見ても、
自分が書いた型名を見ても気づけない。**UniVRM の型を新しく1つ触るたびに再発しうる形。**

### ★ `Kirurobo.UniWindowController` はデスクトップ限定。Runtime から参照しない

実物の `includePlatforms` は
`["Editor", "macOSStandalone", "WindowsStandalone32", "WindowsStandalone64"]` で
**`Android` を含まない**。`includePlatforms` が非空のときは**ホワイトリスト**として扱われるので、
`ChatterMascot.Runtime`（全プラットフォーム）から参照すると Android ビルドで壊れる。

`ChatterMascot.Desktop` に隔離してある。★ **`includePlatforms` は4つを一字一句写すこと。**
部分集合にすると Windows Standalone ビルドでコンパイルエラーになる。

依存の向きは `Editor → Desktop → Vrm → Runtime`。

### ★ デスクトップ限定アセンブリの `MonoBehaviour` をシーンに置かない

シーンは `MonoBehaviour` を `m_Script` の GUID として持つだけで、
**asmdef の `includePlatforms` と無関係に常にシリアライズされる**。
Android ではそのアセンブリが存在しないので解決先が無く、ビルドエラーではなく
**シーンロード時の "The referenced script on this Behaviour is missing!" が1本出るだけ**になる。
症状は「Android で掴めない」、原因は `Player.log` の1行。

`VrmDragHandleBinder` と `WindowSizeKeeper` は `MonoBehaviour` にせず、
`[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]` から自分で組み立てる。
**Android ではアセンブリごと存在しないので属性の走査対象にすらならない** ——
`#if` もプラットフォーム分岐も要らず、切り分けが asmdef 1箇所に閉じる。

★ **購読は sticky にすること。** `AfterSceneLoad` は全 `Awake` の後・最初の `Start` の前に
走るので `VrmStage` の読み込み開始には間に合うが、**その保証に寄りかからない**。
`VrmStage.AddLoadedHandler` は、もう読み終わっていたら即座に呼ぶ。

★ **他人のコンポーネントは移せない。** `Mascot.unity` には `UniWindowController` プレハブが
置いてあるので、**Android ビルドでは missing script が1件出たままになる**。
剥がすならビルド時処理で、それは [#25](https://github.com/schwarz9791/chatter-agent/issues/25)。

### ★ `scripts/run.sh` の grep を通らないログは存在しないのと同じ

`run.sh` は出力を
`grep -E "^\[Fixups\]|^\[Build\]|^\[VrmProbe\]|error CS|…"` で絞る。
**ここに無いプレフィックスは `LogError` でも画面に出ない。**

★ **複数行のログは2行目以降が丸ごと消える。** `VrmProbe` の最初の実装がこれで、
`[VrmProbe]` の1行だけ出て**中身が空に見えた**。行ごとにプレフィックスを付けること
（`text.Replace("\n", "\n[VrmProbe] ")`）。

★ **パッケージ解決の失敗も拾わない。** UniVRM を `manifest.json` に足した直後の初回解決は
git 取得になるが、`Failed to resolve` / `Cannot perform upm operation` は既定のパターンに
無かった。足してある。

### ★ ウィンドウの大きさは3箇所で決まる。ProjectSettings だけ見ても分からない

常駐マスコットとして 250x400 に絞ったときに全部踏んだ。**効く順に**:

| # | 場所 | 効き方 |
|---|---|---|
| 1 | `~/Library/Preferences/tech.sukima.chatter-mascot.plist` | **前回終了時の実値が最優先で復元される。** `Screenmanager Resolution Width/Height` |
| 2 | `ProjectSettings.asset` の **`defaultIsNativeResolution`** | ★ **これが `1` の間は 3 が効かない。** Inspector でも `Default Screen Width/Height` がグレーアウトする |
| 3 | 同 `defaultScreenWidth` / `defaultScreenHeight` | 初回起動時の大きさ |

★ **1 が効いていることに気づけない。** `ProjectSettings.asset` には `600x800` と書いてあるのに
実際のウィンドウは **600x1632** だった、という食い違いから始まって、リポジトリを
いくら grep しても `1632` が出てこない。**`defaults read tech.sukima.chatter-mascot` を先に見ること。**

```bash
defaults delete tech.sukima.chatter-mascot   # 焼き付きを消してから測る
```

#### 実測（2026-08-26 / macOS 26.6.2 / 4K 外部ディスプレイ）

`Player.log` に**大きさが決まる瞬間が2回**出る:

```
Metal RecreateSurface: surface size 250x200     ← 起動直後（= defaultScreen* のまま）
[Mascot] server: ws://127.0.0.1:9 / ...         ← MascotRunner.Start()
Metal RecreateSurface: surface size 250x232     ← ★ +32。UniWindowController が枠なし化した直後
```

**+32 はタイトルバーぶん**が枠なし化でコンテンツ領域へ編入されたもの。高さにだけ乗る
（横に枠が無いので幅は入れた値のまま）。

★ **上のログは `WindowSizeKeeper` を入れる前のもの。** いまは keeper が +32 を打ち消すので
**`defaultScreenHeight` に入れた値がそのまま出る**（`ProjectSettings.asset` は **400**）。
「368 と入れて 400 になる」は keeper 導入前の回避策で、**もう当てはまらない**。

★ **当初これを「Retina で2倍されている」と読んで `200` を入れ、232 になって外した。**
**推測で式を組まずに測ること。** ——ただし「2倍にならない」という結論も
**scale 1 の外部ディスプレイでしか成立していなかった**（→ 下の節）。

★ **`UniWindowController` は大きさを変えていない。** `_shouldFitMonitor` は既定 `false` で
prefab にもシーンにも override が無く、`SetWindowSize` を呼ぶのは `#if UNITY_EDITOR` の
`OnApplicationQuit` だけ。`forceWindowed` はフルスクリーンを解除するだけで大きさに触らない。

★ **`resizableWindow: 1` なのでユーザーがいつでも変えられる**（そして 1 に焼き付く）。
**大きさを前提にした描画を書かないこと** —— VRM の自動フレーミングが毎フレーム
`Screen.width/height` の変化を見ているのはこのため（→ `Vrm/VrmStage.cs`）。

★ **測るときは走っている他のインスタンスに注意。** 別 checkout の `.app` が常駐していると
`osascript` の「名前で最初に見つかったプロセス」がそちらを掴む。**pid で引くこと**。
`forceSingleInstance: 1` は**別パスの `.app` の同時起動を防がない**（実際に2つ動いた）。

### ★ Unity 6 の URP で透過しないのは `Supports HDR` のせい

`Is Transparent` を入れても**背景が黒いまま**になる。枠なしウィンドウにはなるので、
「ネイティブプラグインは動いているのに中身が透けない」という分かりにくい壊れ方をする。

**決め手は URP Asset の `Supports HDR` を切ること、1つだけ**だった
（macOS 26 / Unity 6000.5.8f1 / URP 17.5.0 で、on/off を往復させて確認した）。

| 設定 | 透過に要るか |
|---|---|
| URP Asset の **`Supports HDR`** | **オフが必須。** これだけで決まる |
| URP Asset の `Allow Post Process Alpha Output` | **今の構成では不要**（カメラの Post Processing が無効なので効かない）。ただし**有効にした瞬間に透過が壊れる**ので、保険でオンにしてある |
| `UniWindowController.currentCamera` | 透過には無関係。**クリック透過（Raycast ヒットテスト）に要る** |
| シーンの `EventSystem` | 同上（下の項） |

★ **Editor 上では透過しない。ビルドしないと確認できない**（UniWindowController の制限事項）。

★ **透過が効かないときは、まずビルドしたアプリのログを読むこと**
（`~/Library/Logs/<company>/<product>/Player.log`）。Editor のコンソールには出ない。

> 上流にも同じ症状の未解決 Issue がある
> （[kirurobo/UniWindowController#92](https://github.com/kirurobo/UniWindowController/issues/92)）。
> HDR との相性なので、**Unity や URP のバージョンが上がったら再確認すること。**
> 「効いた組み合わせ」を仕様として扱わない。

### ★ `EventSystem` があってもポインタイベントは配送されない

**入力モジュール（`InputSystemUIInputModule`）とレイキャスタ（3D なら `PhysicsRaycaster`）が
別に要る。** どちらも無いと `IDragHandler` / `IPointerDownHandler` は永久に呼ばれず、
**エラーも出ない**。

★ **クリック透過が動いていたのは EventSystem のおかげではない。**
`UniWindowController.HitTestByRaycast` は `EventSystem.RaycastAll` を呼んだあと、
ヒットが無ければ **`Physics.Raycast` に落ちる**。レイキャスタが1つも登録されていなかったので、
実際にはこの後者だけで動いていた。`PhysicsRaycaster` を足すと前者で当たるようになるが、
ヒットテストの結果は変わらない。

`ProjectSettings.asset` の `activeInputHandler: 1`（Input System のみ）なので、
`StandaloneInputModule` ではなく `InputSystemUIInputModule` を足す。
`SceneFixups` が面倒を見る。

### ★ マスコットのドラッグ移動は UniWindowController 同梱の `UniWindowMoveHandle` を使う

自前で書かないのは、**macOS の Retina 座標系の手当てが既に入っている**ため:

```csharp
// UniWindowMoveHandle.cs
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
    // eventData.position の系と、ウィンドウ座標系でスケールが一致しなくなってしまう
    _dragStartedPosition = _uniwinc.windowPosition - _uniwinc.cursorPosition;
#else
    _dragStartedPosition = eventData.position;
#endif
```

このプロジェクトは `macRetinaSupport: 1` なので、自前実装だと必ず踏む。
修飾キー中は動かさない / 最大化中は無効 / ドラッグ中だけヒットテストを切って戻す、も入っている。

★ **対象を名前で決め打ちにしない。** `SceneFixups.EnsureDragHandles()` の判定は
「`Collider` を持っているか」——クリック透過のヒットテストが `Physics.Raycast` で見ているのと
同じ条件なので、**掴める領域とドラッグできる領域が定義上ずれない**。#17 で Cube が VRM に
置き換わっても、クリック透過のために `Collider` を付ける以上そのまま乗る。

★ **位置の永続化を自分では入れていない。** ただし **Unity 本体が勝手に永続化している** ——
`~/Library/Preferences/tech.sukima.chatter-mascot.plist` の `Screenmanager Window Position X/Y`。
自分で制御していないので、マルチモニタ・解像度変更・画面外からの復帰は #16 でまとめて設計する。

### ★ `UniWindowController.GetCursorPosition()` の Y は bottom-up

**実測で確定**: `CGWarpMouseCursorPosition` で top-down (300, 200) にカーソルを置いて
起動したところ、ログは `cursor=(300.00, 1960.00)`。メインディスプレイの高さが 2160 なので
**2160 − 200 = 1960**、つまり**メインディスプレイの下端が原点の bottom-up**
（macOS ネイティブの `NSWindow` / `NSEvent` の慣習）。`windowPosition` も同じ系。

★ **`Mouse.current` / `Input.mousePosition` は使えない。**
`UniWindowController.GetClientCursorPosition()` に「New Input System ではフォーカスが
無い場合にマウス座標が取得できないため独自に計算する」というコメントがある。
常駐マスコットは基本フォーカスを持たない。

★ **正規化はポイント空間で閉じること。** `cursorPosition` / `windowPosition` / `clientSize`
はすべて LibUniWinC 由来のポイントなので、`Screen.*`（バッキング px）を1つでも混ぜると
Retina 2x で2倍ずれる（→「`Screen.*` はバッキング px、ネイティブのウィンドウ API はポイント」）。

### ★ 画面空間の量をモデル空間の軸で回さない

**症状**: カーソル追従で**上下左右すべてが鏡像**になる（カーソル右 → 頭が左、
カーソル上 → 顎を引く）。

**原因**: 頭の pitch / yaw は**カーソルの画面座標から作った画面空間の量**なのに、
`_instance.transform.right / up`（モデルの軸）で回していた。モデルは `FaceCamera`
（→「VRM は放っておくと背中が映る」）で 180° 回ってカメラを向いているので
**`transform.right` はワールド −X ＝ 画面の右と逆**（`up` は +Y のまま変わらない）。

**手当て**: `Camera.main.transform.right / up`（画面の軸）で回す。
★ **`Camera.main` を毎フレーム引かないこと**（タグ検索）。

★ **符号は実機のスクリーンショットで決めること。** 左手系の回転方向を頭の中で追って
決めると間違える（このリポジトリでは実際に2回間違えた）。**カーソルを既知の位置へ動かして
撮り比べる**のが確実。

### ★ カーソルの正規化をウィンドウの大きさで割らない

窓幅（250〜300pt）で割ると、3840pt の画面では正規化値が **±15** に達する。`GazeAim` は
`c.x * HeadSensitivity(0.1) * HeadYawRangeDegrees(35)` を ±35° で clamp するので、
**正規化値が 10 を超えた時点で振り切れ**、ウィンドウのすぐ隣から先はどこでも最大角＝
「追従」ではなく「最大まで曲げて固まる」になる（実機で確認）。

cc-mascot が**固定 800px のコンテナ**で正規化しているのと同じ趣旨で、**割る量を固定
（800pt）にし、オフセットの基準はウィンドウ中心**にする。実測では画面の右端で
正規化値 8.29（＝約 29°）となり、振り切れなくなった。

★ **`HeadYawRangeDegrees`（35）は「上限」であると同時に「係数」でもある。** 式のとおり
`c × 感度 × Range` を `±Range` で clamp しているので、この値を下げると
**clamp が早く来る**だけでなく**追従の効き方そのものが弱くなる**。「上限だから安全側」と
考えて下げないこと（名前が `MaxHead*Degrees` だったせいで、doc も表も上限としか
書いていなかった —— PR #69 のレビューで判明し `*RangeDegrees` に改名した）。

★ **実効の可動域を決めているのは clamp ではなくディスプレイの広さ。** 上の 8.29 は
clamp の閾値 10 まで**2割ほどしか余裕がない**ので、より広い構成では実際に振り切れる。
clamp は死んだコードではない。

### ★ 視線の中立 —— 最初は「目標の位置」で直そうとして届かなかった

★ **この節に書いてある対処は最終的には採用していない。** ここで行った対処は
「視線の目標（`gazeTarget`）の中立位置をカメラの位置からキャラの目の高さへ動かす」ことだけで、
実機ではそれでも中立が高いままだった（下画面の一番下までカーソルを下げないと正面に
感じない）。**根本原因は「頭が見る人の方を向いていない」ことで**（確定した原因と実測は
次の節「視線の中立が合わないのは『頭が見る人の方を向いていない』から」）、**最終的な実装は
目標をカメラの位置（`Vector3.zero`）へ戻し、代わりに頭そのものをカメラへ向ける
（`VrmPoseAccent` が持つ「基準の下向き」）方式に変わっている**
（`VrmCharacter.cs` の `UpdateGaze` / `VrmPoseAccent.cs`）。

★ **この節を消していないのは、外した仮説を記録する価値があるため。** 「目標の位置だけを
動かす」対処では実機で足りなかった、という経緯は次の担当者が同じ道を辿らないための記録。
以下は**その時点の症状・原因分析・対処**をそのまま残したもの——**規則として読まないこと。**

**症状**（実機）: カーソルをキャラの顔の真横に置いても**やや上めの目線**になる。
縦に並べた2画面構成で、マスコットは上画面の下部。**下画面までカーソルを下げて、やっと
正面を向く**。左右は正しい。

**原因は2つ重なっていた。**

**1. 自動フレーミングのカメラは「体の中心」を向いている。** `Runtime/Vrm/VrmFraming.cs`:

```csharp
public static Vector3 CameraPosition(Bounds bounds, float distance) =>
    new Vector3(bounds.center.x, bounds.center.y, bounds.center.z - distance);
```

カメラは **bounds の中心の高さ**に置かれる。実測（同梱 `vita.vrm` + `idle_loop.vrma`、
`bounds=(0.62, 1.66, 0.35) distance=1.75`）では中心の高さが約 **0.83m**（＝腰のあたり）で、
キャラの目は約 **1.5m**。**カメラは目線より約 0.65m 下、角度にして約 20° 下**にある。

`LookAtTarget` をカメラの位置に置くと「カメラを見る」＝「**見る人より 20° 下を見る**」に
なり、**結果として顔が上を向いて見える**。さらに `vita.vrm` は `lookAt.type = "bone"` で、
`VRM10ObjectLookAt` の `VerticalDown` が `CurveMapper(90, 10)` ——**入力 90° を実際の目の
回転 10° に圧縮する**ので、目もほとんど下がらない。頭も直立のままなので、ずれが
解消されない。

★ **カメラそのものを目の高さへ上げて直してはいけない。** `CameraPosition` はキャラを
画面内に収める構図を決めていて、上げると足元がフレーム外へ出る。**動かすのは視線の
目標（`gazeTarget`）の中立位置だけ。** 中立を「カメラと同じ x / z、キャラの目の高さ」に
置けば、構図を変えずに正面を向く。

★ **目の位置は目ボーンから取ること。** `LeftEye` / `RightEye` は任意ボーンなので、無ければ
`Head` の位置に `VRM10ObjectLookAt.OffsetFromHead`（既定 `(0, 0.06, 0)`）を**ローカルで**
足す（`head.TransformPoint(offset)`）。UniVRM 自身の
`Vrm10RuntimeLookAt.InitializeLookAtOriginTransform` が同じ計算をしている。

**2. カーソルの縦の基準をウィンドウ中心にすると、顔の高さで上を向く。** ウィンドウの
中心は**キャラの腰のあたり**なので、そこを基準（0）にすると、カーソルを顔の高さに
置いても正の値になり上を向く。

cc-mascot の `src/hooks/useCursorTracking.ts` は**頭の画面上の位置を明示的に引いている**:

```js
// Calculate eye offset Y from head bone position
// We want the face position to be the "center" of gaze
const targetY = (mouse.y - headY) * eyeSensitivity * 2;
```

**横は補正していない**（頭は横方向には中央にあるため）。実機でも左右のずれは出なかった。

★ **出力（`gazeTarget` の中立）と入力（カーソルの縦の基準）で、同じ点を使うこと。**
別々の点を使うとまた中立がずれる。

**実測での確認**（カーソルを既知の位置へ動かしてスクリーンショットを撮り比べた）:

| カーソル | 直した後 |
|---|---|
| 画面最上部 | はっきり上を向く |
| 顔より少し上 | わずかに上 |
| **顔の高さ・左右中央** | **正面** |
| 下画面の最下部 | 水平〜わずかに下 |

### ★ 視線の中立が合わないのは「頭が見る人の方を向いていない」から

**UniVRM のソースと同梱 `vita.vrm` の実設定値を読んで確定した。** 上の節で「目標をカメラの位置から
キャラの目の高さへ動かす」修正を入れたが、それでもなお中立が高い（実機の追加指摘:
「下画面の一番下にカーソルを置いたときくらいが正面に感じる」）。

**確定している事実:**

- **`Vrm10RuntimeLookAt` は頭を一切動かさない。** 触るのは `LookAtType.bone` のとき
  `LeftEye` / `RightEye` ボーンだけ。`LookAtType.expression` なら `lookUp` / `lookDown` /
  `lookLeft` / `lookRight` の weight だけ。**どちらの経路でも Head / Neck は動かない**。
  `Vrm10Runtime.m_head` は取得されるだけで未使用
- **`CurveMapper.Map` は線形 + クランプ**（名前に "Curve" と付くが `AnimationCurve` ではない。
  v0.128.3 で線形マップに変わった）:

  ```csharp
  var t = Mathf.Clamp01(src / Mathf.Max(0.001f, CurveXRangeDegree));
  return t * CurveYRangeDegree;
  ```

  2引数は `(inputMaxValue, outputScale)`。方向（Inner / Outer / Up / Down）は
  **インスタンスが4本ある**ことで表す
- **同梱 `vita.vrm` の実設定値**:

  ```
  rangeMapHorizontalInner: { inputMaxValue: 90, outputScale:  8.894 }
  rangeMapHorizontalOuter: { inputMaxValue: 90, outputScale: 14.424 }
  rangeMapVerticalDown:    { inputMaxValue: 90, outputScale: 21.060 }
  rangeMapVerticalUp:      { inputMaxValue: 90, outputScale: 15.991 }
  ```

  → **目は目標角の 23.4%（下方向）しか動かない。** 入力 20° 下 → 実際の目の回転 **4.68°**

  | 入力角 | Down (21.06) | Up (15.99) | Inner (8.894) | Outer (14.424) |
  |---|---|---|---|---|
  | 5° | 1.17° | 0.89° | 0.49° | 0.80° |
  | 10° | 2.34° | 1.78° | 0.99° | 1.60° |
  | 20° | 4.68° | 3.55° | 1.98° | 3.21° |
  | 45° | 10.53° | 8.00° | 4.45° | 7.21° |
  | 90°以上 | 21.06°(max) | 15.99°(max) | 8.89°(max) | 14.42°(max) |

- ★ **`vita.vrm` は `lookUp` / `lookDown` / `lookLeft` / `lookRight` の expression を持たない。**
  `type: "bone"` かつ目ボーンがあるので `bone` 経路が使われるが、**仮に expression 経路へ
  落ちると視線が全く動かなくなる**
- **カメラは `VrmFraming.CameraPosition` で bounds の中心（＝腰、実測で約 0.78m）に置かれる。**
  キャラの目は約 1.38m、距離約 1.75m → **カメラは目線より約 19° 下**
- ★ **結論: カメラが体の中心にある以上、「見る人を見る」には頭を回すしかない。**
  目だけでは 19° × 0.234 ＝ 約 4.4° しか下がらない
- ★ **`LookAt` の原点は Head ボーンの子**なので yaw / pitch は「いまの頭の向きからの相対」。
  **先に頭を回せば目の角度は自動的に残差になる**ので、頭と目で二重に効かない

### ★ 手続き的アイドルは腕を書かないと T ポーズのまま残る

**症状**（実機のスクリーンショットで確認）: 同梱の `idle_loop.vrma` を退避して手続き的アイドルへ
フォールバックさせると、フォールバックのログは正しく出て呼吸・重心移動も効くのに、
**キャラは腕を真横に開いた T ポーズのまま**だった。

**原因**: `IdlePose.Evaluate` が返していたのは `HipsOffsetY` / `SpineEuler` / `ChestEuler` /
`NeckEuler` / `HeadEuler` だけで、**腕に一切触っていなかった**。ControlRig の腕の
`localRotation` は `identity` のまま ＝ **VRM 1.0 の正規化 T ポーズ**なので、腕が開いたまま残る。

★ **VRMA がある通常経路では起きない。** VRMA は 22 ボーンを持っていて腕も動かすため。
**フォールバック経路でだけ**出る＝**同梱ファイルを消さない限り誰も気づかない**。
#59 の見出しの目標が「T ポーズの棒立ちを解消する」だったので、これは埋めるべき穴だった。

**手当て**: `IdlePose` に**腕の静止姿勢**（`RestUpperArmDegrees` 既定 70度 /
`RestLowerArmDegrees` 既定 10度）を足し、`LeftUpperArmEuler` / `RightUpperArmEuler` /
`LeftLowerArmEuler` / `RightLowerArmEuler` として返して `VrmCharacter` が ControlRig へ書く。

★ **Z 軸まわりで、左右は符号が反転する**（ControlRig の正規化 T ポーズが左右対称に開いているため）。
★ **符号は実測で決めた。** 最初 `右 = +RestUpperArmDegrees` で書いたところ、実機では
**腕が万歳の向きに上がった**。反転させて体側へ下りることを確認した。**導出で書き換えないこと。**
★ **揺れ（呼吸・重心移動）は腕には乗せていない。** 上腕・前腕は肩→肘の2ボーンチェーンで、
独立した sin を足すと振り子のようにブラブラして見えるリスクがあるため。テスト
`RestArmAnglesDoNotOscillateOverTime` でこの判断を固定してある。

### ★ 起動直後にだけ成立しない状態を「異常」として警告しない

**症状**: `VrmCharacter` が出す

```
[Mascot] 視線の原点（目 / 頭ボーン）が取れないので、GazeOriginViewportY をフォールバック値にします
```

が、**VRM が正常に読めている場合でも起動のたびに必ず1回出ていた。**

**原因**: `VrmCharacter.LateUpdate` は**フレーム1から**走るが、`_instance` が入るのは
`OnLoaded`（実測で**約1.6秒後**）。その間 `TryGetGazeOrigin` は当然 `false` を返すので、
**ラッチされた警告がフレーム1で消費される**。

つまり「VRM が読めているかどうかに関わらず毎回出る」形になっていて、**異常を知らせる役に
立たない**。むしろ「毎回出る警告」として読み飛ばす癖がつくぶん有害。

**手当て**: **モデルが読み込まれた後にだけ**警告を出す（`_instance != null` を条件に足す）。
それ以前は黙ってフォールバック値を維持する。

★ **同じ形の失敗を #59 の中で2回踏んでいる。**

1. `CursorGazeSource` の1回だけのログが、`GazeOriginViewportY` が実測で埋まる前に発火して
   **初期値 `0.5` しか記録しなかった**（この値を確かめようとして使えず、スクリーンショットで
   測り直す羽目になった）
2. 上の「視線の原点が取れない」警告

**一般化**: **非同期の読み込みが終わるまで成立しない条件を、起動直後のフレームで判定しない。**
ラッチ付きのログ／警告は特に危ない —— 1回しか出ないので、**成立前に消費されると永久に
本当の値が出ない**。`_instance` のような「読み込みが終わった印」を条件に足すこと。

### ★ `kind: "prompt"` の実機確認 —— サーバーは起動前に溜まったキューを捨てる

**確認方法**: `XDG_CONFIG_HOME` を一時ディレクトリに向けてサーバーを隔離環境で起動し、
**サーバー起動後に**配信キューへ `kind: "prompt"` の entry を1件手で置いた。

**確認できた挙動**: **再生中だけ**視線がカーソル追従をやめて中央に固定され、上体が前傾する。
再生が終わると通常のカーソル追従に復帰する。

★ **「サーバー起動後に置く」が重要。** サーバーは**起動前に溜まっていたキューを捨てる**
（実機ログ: `[Server] 起動前に溜まっていた 1 件を捨てました`）。先に置くと消えて確認できない。
**これは独立した罠として書く価値がある** —— `kind: "prompt"` に限らずキュー全般に効くので、
実機で挙動を確かめるときは必ず「サーバーを起動 → 起動できたことをログで確認 → そのあとでキューに置く」
の順を守ること。

### ★ `SetWeight` は黙って無視される。しかも「一覧に載っている」＝「顔が動く」ではない

**2段構えで空振りする。** どちらもエラーにならない。

1. **`Vrm10RuntimeExpression.SetWeight` / `SetWeights` は `_inputWeights.ContainsKey(key)` で弾く。**
   モデルが持たない preset を渡しても**例外もログも出ない**。「表情が変わらない」だけが起きる
2. ★ **UniVRM の importer は、モデルが宣言していない preset にも「中身が空のクリップ」を作る。**
   実測（同梱 `vita.vrm`、v0.131.2）: **glTF の `VRMC_vrm.expressions.preset` は 14 個**しか無いのに、
   `Vrm.Expression.Clips` は **18 個**ある。増えているのは `lookUp` / `lookDown` / `lookLeft` /
   `lookRight` の4つで、**`morphTargetBinds` が 0 件**。つまり `SetWeight` は通り、
   `ExpressionKeys` にも載り、それでも顔は 1mm も動かない

**だから「キーがあるか」だけを見る診断は、いちばん見たいケースで嘘をつく。** bind の数まで見ること。

- `VrmProbe` の `expressions:` は bind が 0 のクリップに `(空)` を付ける
- `VrmCharacter` は読み込み時に1回、`使う preset: happy=○ angry=○ …（○=動く / 空=枠はあるが中身が無い / ×=無い）` を出す

★★ **bind の配列は4本ある。** `MorphTargetBindings` / `MaterialColorBindings` /
`MaterialUVBindings` / **`NodeTransformBindings`**。最後のひとつは実験扱いの名前だが
`NodeTransformBindingMerger` が実際に適用しているので、**数え落とすと眉や耳や尻尾を
ボーンで動かすモデルの効いている表情を「空」と誤報する** —— この診断は
「顔が動かないのが正常」と「壊れて動かない」を区別するためにあるのだから、
**作られた目的そのものの場面で誤誘導する**ことになる。

★★ **判定は `VrmCharacter.HasBindings`（`public static`）1箇所に置き、`VrmProbe` はそれを呼ぶ。**
最初は probe 側に手写ししていて、**両方が同じ抜け（`NodeTransformBindings`）を持っていた**
（#57 のレビューで判明）。`VrmStage.MeasureBounds` を `public static` にしてあるのと同じ理由で、
独立実装が2つあると片方だけ直したときに黙ってズレる。

★ **`vita.vrm` の bind 先（実測）**:

```
happy → Fcl_ALL_Joy      angry → Fcl_ALL_Angry      sad → Fcl_ALL_Sorrow
relaxed → Fcl_ALL_Fun    surprised → Fcl_ALL_Surprised
blink → Fcl_EYE_Close    aa → Fcl_MTH_A             neutral → Fcl_ALL_Neutral（w=1.0）
```

★ **上の「視線の中立」の節にある「`vita.vrm` は `lookUp` / `lookDown` / `lookLeft` / `lookRight` の
expression を持たない」と、probe の一覧に4つが載ることは矛盾していない。** **枠はあって中身が無い。**
この2つの記述が食い違って見えたら、ここを思い出すこと。

### ★ expression の weight は自動でゼロに戻らない

`_inputWeights` は次に上書きされるまで保持され続ける。**アニメーションではなく状態**なので、
「もう出さない」を表すには**明示的に 0 を書く**必要がある。

いちばん刺さるのは口で、**喋り終わっても `aa` が開いたまま固まる**。`FacePolicy` は
`Speaking` が false になったフレームで `aa` を**猶予なしで即 0** にしている（表情には猶予があるのと対照的）。
★ **#58（リップシンク）が口を入れる前に、この経路だけ先に作ってある。** 後付けにすると必ず一度踏む。

### ★ override の判定は「モデルの静的な定義」で行う。ランタイムの `*OverrideRate` では検出できない

`Vrm10RuntimeExpression` は `BlinkOverrideRate` / `MouthOverrideRate` / `LookAtOverrideRate` を
公開しているが、**これを異常検知に使わないこと。** 2つ理由がある。

1. **いま立てている weight に依存する動的な値。** `DefaultExpressionValidator` は
   `block` なら weight>0 で 1、`blend` なら weight そのものを足して clamp01 する。実運用では
   `neutral` が支配的（`ruleBasedEmotionClassifier` がコード説明文を `neutral` に倒すよう明示的に
   チューニングされている）なので、**ほとんどの時間 0 のまま＝警告が一度も出ない**
2. **更新されるのは `Vrm10Runtime.Process()`（実行順 11000）の中。** 実行順 0 の
   `VrmCharacter` から読むと**前フレームの値**になる

**静的な `Vrm.Expression.Clips` の `OverrideBlink` / `OverrideMouth` / `OverrideLookAt` は
読み込み直後に確定していて weight に依存しない。** こちらを1回走査して警告する。

★ **`OverrideLookAt` も見ること。** ここが `none` でないモデルは、表情を出した瞬間に
#59 のカーソル追従（`LookAtEyeDirection` が `1 - lookAtOverrideRate` 倍される）が死ぬ。
「表情を入れたら視線が動かなくなった」の切り分けはこの警告が無いと難しい。

★★ **走査するクリップは「このアプリが実際に weight を書く8つ」に絞ること。**
override 率は `GetOverrideRate(clip.Override*, weight)` ＝ **weight 依存**なので、
一度も weight を立てないクリップは寄与 0 で**何もブロックできない**。全クリップを見ると、
`ih` / `ou` / `ee` / `oh`（使わない口の preset）やカスタムクリップに `overrideBlink` が
付いているだけで、**成立しえない条件の警告を毎起動・永久に出す**ことになる
（#57 のレビュー指摘）。上の「起動直後にだけ成立しない状態を『異常』として警告しない」と
同じ失敗の仕方 ——「読み飛ばす癖がつくぶん有害」。
★ **絞る対象はキーの集合であって、見る項目ではない。** `OverrideLookAt` を見る判断は維持する。

★ **実測: 同梱 `vita.vrm` は preset 14個すべて `override*: none` かつ `isBinary: false`。**
つまり `happy` と `aa` と `blink` は互いに一切干渉しない。**このモデルでは UniVRM は何も守ってくれない**、
と読み替えること（下の「VRoid の happy は目を細める」に効く）。

### ★ 表情を「体を止める条件」で止めない（`LateUpdate` の早期 return）

`VrmCharacter.LateUpdate` は下3つで早期 return する。

```csharp
if (_instance == null) return;
if (_idle != null && _idle.IsPlaying) return;   // ← VRMA が読めている＝通常の状態
if (!proceduralIdle) return;
```

**表情の適用をこの後ろに置くと、同梱 `idle_loop.vrma` が読めている通常状態で一度も走らない。**
2番目は「手続き的アイドルと VRMA が `ControlRig` を奪い合わないため」の条件で、
**顔とは何の関係も無い**。顔は `Kind` / `Emotion` を確定させた直後、早期 return より前で適用する。

★ 一般化: **`LateUpdate` に早期 return がある関数へ新しいチャンネルを足すときは、
その return が何を止めるための条件かを読むこと。** ここは「体」を止める条件だった。

### ★ 発話は文の切れ目で必ず途切れる。だから表情に猶予が要る

**1文＝1レコード＝1音声ファイル。** `PlaybackQueue` は再生完了で head を `Done` にして削除し、
次を `Playing` にする。つまり**文の切れ目では必ず `Playing` が 0 件になる瞬間がある** ——
先読み（`Lookahead = 3`）が効いていれば数フレーム、合成が詰まっていれば秒単位。

`SpeakingView.TryRead` はそこで `false` を返すので、**猶予が無いとメッセージの途中で毎文
Neutral に落ちる**。cc-mascot は hold を持たない（`onended` で即 neutral）ので実際にそうなっている。
`faceHoldSeconds`（既定 **1.5秒**）は「余韻」ではなく**この分断を埋めるためのもの**。短くしないこと。

★★ **猶予だけでは足りない。emotion をラッチしないと1行も効かない。**
`SpeakingView.TryRead` は false のとき `kind = Assistant` / `emotion = Neutral` に**倒す契約**
（`SpeakingViewTests` の4本が固定している）。だから `VrmCharacter.Emotion` を素通しすると、
**喋り終わった瞬間に目標が Neutral になり、猶予の秒数をいくら伸ばしても顔は即座に戻る**。
`_lastSpokenEmotion`（`Speaking` が true の間だけ更新する）を渡すこと。

★★ **`Kind` も一緒にラッチすること。** `Emotion` だけ直して `Kind` を生のまま渡すと、
猶予の途中で**片方だけ崩れる** —— `promptSurpriseWeight` を 0 から開けたとき、
emotion 由来の表情は猶予ぶん保たれるのに prompt の上乗せだけが発話終了の次フレームで抜け、
**目に見える段差**が入る（#57 のレビュー指摘）。

★★ **ただし prompt の<u>エッジ</u>は生の値で見ること。** ラッチ済みの `Kind` は猶予の間も
（次の発話まで）`Prompt` のまま残るので、そちらでエッジを取ると
**2回目以降の prompt でエッジが立たず、瞬きが一度も入らなくなる**。

★★ **この記憶を `MonoBehaviour` のフィールドとして書かないこと。** `ChatterMascot.Tests.asmdef` は
`ChatterMascot.Runtime` しか参照しないので、`VrmCharacter` に書いた時点で**テストが1行も当たらない**
—— しかもここは「これが無いと猶予が1行も効かない」と分かっている場所。
`Runtime/Vrm/FaceLatch.cs` に切り出して `FaceLatchTests` で固定してある。
**`Runtime/` に純粋ロジックを寄せる判断は、残った glue にも最後まで適用すること。**

★ **`messageId` で束ねて解決しようとしないこと。** [`protocol.md`](./protocol.md) が
「`messageId` の変化だけを根拠にした安全なバッチ化はできない」を3つの理由で明示的に禁じている。

### ★ VRoid の `happy` は目を細める。`override` が `none` なら瞬きと素で加算される

`vita.vrm` の `happy` は `Fcl_ALL_Joy` に bind されていて、VRoid の Joy は**目を細める形を含む**。
`blink` は `Fcl_EYE_Close`。両方 `overrideBlink: none` なので、**UniVRM は減衰させず素で足す**。

cc-mascot が実測で入れているガード（`useBlink.ts` の `HAPPY_EXPRESSION_THRESHOLD = 0.1`）を
踏襲して、`happy` の緩和後の weight が閾値を超えている間は瞬きを止める
（`blinkSuppressAboveHappy`、既定 0.1。0 で無効）。

★ **判定は「目標」ではなく「緩和後の値」で行う。** 目が細まっているかは、実際に適用される
weight で決まる（cc-mascot も lerp 後の `currentEmotionValues` を見ている）。

★ **#58 で入れた。** cc-mascot は同じ理由で**口も抑えている** ——
`aa` を `happy` で **0.2倍**、`sad` で **0.5倍**にスケールする（`useVRM.ts` の `setMouthOpen`。
「笑顔時や悲しいときに口が開きすぎてメッシュからはみ出るのを防ぐ」）。
#57 では `Mouth` が常に 0 で目視確認できなかったので見送っていたが、#58 で口が動くように
なったので `FaceParams.MouthScaleHappy` / `MouthScaleSad` として同じ値で入れてある。

★ **判定は瞬きの抑制と同じく「緩和後の weight」。** 表情が立ち上がる途中では倍率も途中の値に
なるので段差が入らない。
★ **`0` は「口を閉じる」ではなく「掛けない」。** `FaceParams` の他の値と同じ「0 = 無効」の
語彙に揃えてある —— そうしないと `FacePolicyTests.AllZeroParamsMakeEvaluateEqualTarget` が
固定している「全部 0 なら `Evaluate` は `Target` と一致する」が壊れる。

### ★ 瞬きの間隔は cc-mascot、形は UniVRM サンプル

`Samples~/VRM10Viewer/VRM10Blinker.cs` は Package Manager から明示的にインポートしない限り
Unity が読まないので、**自前で書く**（数値を参考にしただけなので `NOTICE` の義務は増えない）。

| | 採用 | 出どころ | 採らなかった側 |
|---|---|---|---|
| 間隔 | **U(2秒, 6秒)** | cc-mascot `useBlink.ts` | `VRM10Blinker` は `Random.value * 5f` ＝ **U(0, 5秒)** で下限が無く、0秒近い間隔が出て連続瞬きに見える |
| 形 | **閉 0.1 / 保持 0.06 / 開 0.03 秒** | `VRM10Blinker` | cc-mascot は閉 75ms → 開 75ms で**保持なし**。閉じたままの間が無いぶん速く見える |

★ **コルーチンにしないこと。** サンプルは `StartCoroutine` + `WaitForSeconds` だが、それだと
EditMode から回せない。`AudioIdleGate` と同じ「状態は持つが時計は引数で受け取る」形にする。

★ **状態は `double` の期限で持ち、`float` へ落とすのは「期限との差」だけにする。** 常駐アプリなので
`Time.realtimeSinceStartupAsDouble` は日単位まで伸びる。経過を `(float)now` から作ると、7日で
float の刻み幅が1フレームぶんの差を上回り、**瞬きがカクつく／止まるがエラーは出ない**
（`Oscillator.Phase` が位相を周期で畳んでいるのと同じ理由）。

★★ **`Request()` の消費は、フェーズを進めた<u>後</u>に置くこと。** 先に消費すると、
期限を過ぎているのにまだ `Waiting` へ進んでいない**古いフェーズ**を見て「既に瞬いている」と
誤判定し、要求を恒久的に捨てる（要求フラグはクリア済みなので再試行も無い）。
30fps で瞬きの終端フレームに prompt が重なると、`Request()` の存在理由そのものが失われる。

★★ **フェーズ進行の上限で打ち切ったら、そのフェーズを残さず「目を開けた状態」へ倒すこと。**
上限が4フェーズ周期の整数倍だと、毎 `Tick` で**同じフェーズ**へ戻る。それが `Closing` /
`Holding` なら出力は永久に 1 ＝ **目が閉じたまま固着する**のに、1 は 0..1 に収まるので
**「範囲内か」だけを見るテストはすり抜ける**。#57 のレビュー指摘（J）を受けて
「有限回で 0 に戻ること」まで assert したら、実際にこれを踏んでいた。
打ち切りに至るのは「設定が縮退している」か「長く止まっていた」かのどちらかで、
いずれも瞬いていない状態へ倒すのが正しい（寝ていた間の瞬きを取り戻す必要は無い）。

★ **30fps では開きのランプは事実上見えない。** 1フレーム 33.3ms に対して
`openSeconds = 0.03` なので、`Tick` が開きの窓に落ちない周期のほうが多く、
`blink` は 1.0 → 0.0 と一段で戻る（`holdSeconds = 0.06` も約2フレーム）。
**上の表は「出典どおりの値」であって「30fps 用に調整した値」ではない** ——
開きの緩さが欲しくなったら、出典との対応が切れることを承知のうえで伸ばすこと。

★ **抑制は「始まっていない瞬きを飛ばす」であって「進行中の瞬きを切る」ではない。**
cc-mascot も `performBlink` の入口で `return` している。出力を無条件に 0 にすると、
閉じ切っている最中に `happy` が立った瞬間に**1フレームで目が開く段差**になる
（`Blink` は意図的に補間していないので吸収するものが無い）。
`FacePolicy` は「前フレームの `blink` が 0 のときだけ」止めている。

### ★ `SetWeights` ではなく `SetWeightsNonAlloc` を使う

`Vrm10RuntimeExpression` は両方持っている。`SetWeights(IEnumerable<KeyValuePair<…>>)` は
`Dictionary` を渡しても**インターフェース越しに列挙するので列挙子がボックス化する**。
毎フレーム（30回/秒）書く場所なので、`Dictionary` を1本使い回して `SetWeightsNonAlloc` に渡す。

### ★ 「顔が動かないのが正常」と「壊れて動かない」はログでしか区別できない

`ruleBasedEmotionClassifier` は Claude Code のコード説明文が `neutral` に倒れるよう明示的に
チューニングされている（`applyHeuristics` がコードブロック・ファイルパス・技術用語で `neutral` に
最大 +12 まで加点するのに対し、感情側は文末パターン1本が +2）。**実運用では `neutral` が支配的**で、
顔はほとんど動かないのが正しい。

しかも #59 でアイドルと視線が動いているので、**体が動いているのを見て「動いているから大丈夫」と
流しやすい**。だから `VrmCharacter` に「今の emotion / kind と**実効** weight を1秒ごとに出す」
デバッグフラグを付けてある（`faceDebugLog`、ビルド済みアプリでは `-faceLog 1`）。

- ★ **「実効」は `Runtime.Expression.ActualWeights` から読むこと。** 自前で計算した値を出しても、
  `SetWeight` が空振りしたケース（上の節）を検出できない。**目標と実効を両方出す**のはそのため
- ★ **実効は1フレーム古い。** `ActualWeights` を埋めるのは `Vrm10Runtime.Process()`（11000）の中で、
  実行順 0 のここはその手前。**切り替わりの最中に目標と実効がずれて見えるのは正常**
- ★ **同じ文面を間引かないこと。** `neutral` のまま動かないのが正常なので、重複を抑えると
  **「正常」のときだけ何も出なくなり、目的と正反対**になる。時間で間引くだけにする

#### #57 の実機実測（2026-08-28 / macOS ビルド / `AvatarSample_A.vrm`）

`XDG_CONFIG_HOME` を一時ディレクトリに向けたサーバー（`CHATTER_AGENT_PORT=8571`）へ
`-serverUrl ws://127.0.0.1:8571 -faceLog 1` で繋ぎ、**サーバー起動後に**キューへ手で置いた。

読み込み時（1回だけ出る）:

```
[Mascot] expression: aa, angry, blink, blinkLeft, blinkRight, ee, happy, ih, lookDown, lookLeft,
         lookRight, lookUp, neutral, oh, ou, relaxed, sad, surprised（18 件）
[Mascot] 使う preset: happy=○ angry=○ sad=○ relaxed=○ surprised=○ neutral=○ blink=○ aa=○
[Mascot] VRMA の ExpressionMap: 0 件
```

6つの emotion を順に流したときの `目標`（0 のチャンネルは省いた）:

```
emotion=Happy      happy=0.98 → happy=1.00
emotion=Angry      happy=0.09 angry=0.91 → angry=1.00
emotion=Sad        angry=0.64 sad=0.36   → sad=1.00
emotion=Relaxed    sad=0.03 relaxed=0.97 → relaxed=1.00
emotion=Surprised  relaxed=0.16 surprised=0.84 → surprised=1.00
emotion=Neutral    surprised=0.01 → （全部 0）
```

★ **クロスフェードの中間値がそのまま観測できる。** 前の emotion が残ったまま次が立ち上がっていて、
どこにも段差が無い。**「パタパタしない」はこの中間値の存在で確かめられる**（目で見るより確実）。

★ **`ExpressionMap: 0 件` の VRMA が回っている状態で emotion が効いた** ——
#59 から引き継いだ宿題（表情が VRMA に奪われていないこと）はこれで閉じた。

`kind: "prompt"` の瞬き（`Request()` のエッジで1回）:

```
kind=Prompt emotion=Surprised surprised=0.67 blink=1.00   ← prompt へ移り始めた直後
kind=Prompt emotion=Surprised surprised=1.00 blink=0.84
```

★ **`surprised` が 0.67 ＝ 遷移が始まって 0.17 秒ほどの時点で blink が 1.00 に達している。**
自然な瞬き（2〜6秒間隔）がその一瞬に偶然重なる確率は低いので、これは `Request()` 由来と読める。

★ **`blink` は 1秒に1回しかログに出ない一方、瞬きは 0.19 秒で終わる。**
だから **`blink=0.00` の行が並んでいても「瞬いていない」証拠にはならない**（捕捉率は2割ほど）。
瞬きの有無をログで確かめたいときは、prompt を何回か挟んでエッジを増やすこと。

★ **`happy=1.00` の行では `blink` が常に 0**（`blinkSuppressAboveHappy` が効いている）のに対し、
**`angry=1.00` の行には `blink=1.00` が出る** —— 抑制が `happy` だけに掛かっていることも読める。

★ **実効（`ActualWeights`）は目標と一致した。** 差が出たのは遷移の最中だけで（例: 目標 `blink=1.00` /
実効 `blink=0.75`）、これは実行順による1フレームの遅れ。上の節のとおり正常。

★ **ログの取り違えに注意。** `Player.log` は**バンドル ID ごと**なので、
`~/dev/chatter-agent` 側のマスコットと `~/orca/workspaces/...` 側のマスコットが**同じファイルを共有する**。
片方が起動すると相手のログが `Player-prev.log` へ回される。**実機確認の途中で「ログが消えた」ように
見えたら、まず `Player-prev.log` を見ること**（実際に踏んだ）。

### ★ 起動引数の真偽値フラグは `CommandLine.Flag` で読む（`Argument` ではない）

`CommandLine.Argument` は「name の**次に来る値**」を返す作りで、**末尾の name は拾わない**
（ループが `args.Count - 1` まで）。だから `-faceLog` を単独で渡すと `null` が返る。

| 渡し方 | `Argument` | 期待 |
|---|---|---|
| `-faceLog 1` | `"1"` | 有効 |
| **`-faceLog`（単独）** | **`null`** | 有効にしたい |
| `-faceLog -vrm /path.vrm` | `"-vrm"` | 有効。かつ `-vrm` を食わない |

真ん中を「指定されなかった」と同じ扱いにすると、**いちばん自然な渡し方で黙って無反応**になる。
実機で `Player.log` を読むための口がそれだと、切り分け中に
「ログが出ない＝コードが走っていない」と誤読しかねない（#57 のレビュー指摘）。

`CommandLine.Flag(args, name, defaultValue)` が3つとも面倒を見る:

- **値なし（末尾、または次のトークンが `-` で始まる）＝ `true`**
- 偽と読むのは `0` / `false` / `no` / `off` だけ（大文字小文字は無視）。それ以外の値は真
- name が無ければ `defaultValue`

★ **規則を `MonoBehaviour` の中に書かないこと。** `CommandLine` は
`Argument(IReadOnlyList<string>, string)` を純粋関数として持ち `CommandLineTests` で固定している。
真偽値の規則だけ MonoBehaviour に置くと、そこだけテストで固定できなくなる。

### ★ シーンに `EventSystem` が無いとクリック透過が死ぬ

`UniWindowController` の Raycast ヒットテストは `EventSystem.current.RaycastAll` を呼ぶ。
**シーンに `EventSystem` が無いと毎フレーム `NullReferenceException`** で、
クリック透過が一切効かない。

★ **ウィンドウの透過そのものは成立する**ので、見た目には気づけない。
Inspector 上も正常に見える。**ビルドしたアプリのログを読むまで分からない。**

`Editor/SceneFixups.cs` の `FixAll` が保証する。**シーンを作り直したら必ず走らせること**:

```bash
./scripts/run.sh ChatterMascot.EditorTools.SceneFixups.FixAll
```

### ★ Newtonsoft は `ts` を勝手に `DateTime` にする

`JToken.Parse` の既定（`DateParseHandling.DateTime`）は「ISO8601 らしき文字列」を自動変換する。
そのままだと `ts` が `JTokenType.String` ではなく `Date` になり、文字列として読めない。

**症状が凶悪**で、`SpeechFrameParser` の必須フィールド検査に引っかかって
**正常なフレームが1つも通らなくなる**。外から見えるのは:

- **完全な無音**（テキストも表情も出ない）
- ログは「読めないフレームを捨てました」が**接続ごとに1回だけ**（洪水を避けるラッチ）

サーバー側は正常に配信し続けているので、切り分けが難しい。

`JsonTextReader` に `DateParseHandling.None` を立てて読む
（`Runtime/Protocol/SpeechFrame.cs`）。契約上 `ts` は**不透明な文字列**で、
古さの判定のときだけパースする（参照実装も `Date.parse` を `isStale` でしか呼ばない）。

回帰テスト: `Tests/Editor/SpeechFrameTests.cs` の `IsoTimestampStaysString`。

### ★ Newtonsoft は `long` を超える整数を `BigInteger` で持つ（`JTokenType` は `Integer` のまま）

`seq` の型検査（`token.Type != JTokenType.Integer`）は**通ってしまう**。落ちるのはその次の
`Value<long>()` で、**例外の型は `OverflowException` ではない** —— 実測では
`InvalidCastException`（"Object must implement IConvertible"）だった。
`System.Numerics.BigInteger` が `IConvertible` を実装していないので、
`Convert.ChangeType` の手前で落ちる。

★ **この壊れ方が悪いのは、1フレームで済まないこと。** `TryParse` の外へ例外が出ると:

1. `SpeechClient` の受信ループが終わる（ログは「受信でエラー」1行）
2. 再接続する
3. サーバーは**未 ack の同じフレームを再送する**
4. また落ちる —— 直すまで永久にこのループ

だから手当ては2層にしてある:

- `TryAsInteger` が例外を握って「読めなかった」に倒す（**型を決め打ちにしない**。
  値の持ち方は Newtonsoft のビルド構成 `HAVE_BIG_INTEGER` で変わる）
- `SpeechClient` が `FrameReceived` の購読者例外を**1フレームぶんだけ**握る
  （→ 下の「購読者の例外を接続の外へ出さない」）

回帰テスト: `SpeechFrameTests.HugeSeqIsRejectedWithoutThrowing`。
既存の `OnlyPositiveSafeIntegerSeq` の `9007199254740992` は **`long` に収まる**ので、
このケースを踏めていなかった。

### ★ 購読者の例外を接続の外へ出さない

`SpeechClient` は購読者が何をするか知らない。`MascotRunner` は `FrameReceived` /
`Connected` / `Disconnected` の3つとも `PlaybackQueue.Reduce` → コマンド実行に繋いでいるので、
そこの1つの例外が**受信ループや再接続ループを道連れにする**。

とくに `RunAsync` は `_ = RunAsync()` で起動しているので、そこまで上がった例外は
**未観測の `Task` の fault として捨てられる** —— ログが1行も出ないまま再接続ループだけが消え、
セッションが終わるまで無音になる。「サーバーが何も言っていない」と区別がつかない。

`SafeInvoke` で握って必ず `Warn` に出す。`RunAsync` の最外殻にも try/catch を置くが、
**あれは復旧のためではなく可視化のため**（復旧できないなら、せめて
「再接続ループが止まりました」と言わせる）。

### ★ `ClientWebSocket.SendAsync` を `_ = ` で投げると、例外が `catch` を素通りする

返るのは `Task` なので、送信中の例外（送信の重なりによる `InvalidOperationException`、
`State` 検査の直後にソケットが落ちた、half-open の書き込みエラー）は**その `Task` に載る**。
同期的には投げられないので、囲った `try/catch` はほとんど発火しない。

ack のように「送れたことを前提に手元から消す」値でこれをやると、**ack が
こちら側からも状態機械側からも消える**。復旧するのはサーバーが同じ entry を再送して
重複排除の枝が ack を再発行したときだけで、偶然に頼ることになる。

手当ては「**消してから送る」を「送れてから消す」に反転させる**こと
（`Runtime/Net/SpeechClient.cs` の `FlushAckAsync`）。消さなければ、
失敗しても次の `Tick` がそのまま再送する。

★ **復元処理（送れなかったら戻す）を書かないこと。** 「await の間に `DropPendingAck()` が
走った」「もっと新しい seq が積まれた」「世代が変わった」を見分ける必要があり、
どれか1つ落とすと**まだ喋っていない entry を消す ack** が飛ぶ。消さなければその分岐が無い。

★ **`SendAsync` は同時に2本走らせられない。** `Tick()` は毎フレーム呼ばれるので、
送信中フラグで直列化する。`SemaphoreSlim` は要らない（Unity の
`SynchronizationContext` により継続はメインスレッドで走る）。

### ★ 終了時の ack は `Application.wantsToQuit` で保留しないと投げ切れない

`OnDestroy` から `_ = client.CloseAsync()` を投げても、await の継続が走る前に
プロセスが消える。喋り終えた ack が落ちると、**次回起動でその文がもう一度鳴る**。

`wantsToQuit` で1回だけ `false` を返して終了を保留し、閉じ切ってから
`Application.Quit()` を呼び直す。予算（3秒）を切ること —— 返らない相手を掴むと
**アプリが終了しなくなる**。

★ **Editor の Play Mode 停止では保留できない。** Unity のドキュメントが
「The return value of this event is ignored when exiting Play mode in the Editor」と
明記している。イベント自体は呼ばれるが `false` が効かないので、
**この経路の確認はビルドした `.app` でしか取れない**。Editor で ack が落ちても実装の失敗ではない。

★ **iOS / iPadOS では戻り値が効かない**（ドキュメント明記）。**Android での挙動は未確認**（→ #25）。

★ `CloseAsync` の中で最後の ack を待つときは、**`_cancellation.Token` を渡さないこと**。
直後の `Cancel()` が、たった今投げた送信を自分で中断する。

★ **終了処理が何をしたかをログに残すこと。** 保留中の ack があったかどうかは
**再生終了から数十 ms の窓**（`AckFlushMs` 20ms + `Tick` の 1フレーム）でしか変わらないので、
ログが無いと「終了処理が働いたのか、そもそも出番が無かったのか」を後から区別できない。

実機確認でここに詰まった。**「終了後にキューが空だった」だけでは何も言えない** ——
通常の `Tick` が先に送っていた可能性と区別がつかない。`AckFlushMs` を一時的に
10秒へ広げたうえで、終了処理に成功／失敗の1行を出させて初めて判定できた:

```
[Mascot] 終了時に保留していた ack を送りました (seq=5145)
```

送れなかった側（`Warn`）は**次回起動での二重発話に直結する**ので、これは恒久の診断として残してある。

### ★ ストリーミングで書かれた WAV は `data` のサイズが 0 のことがある

`0xFFFFFFFF`（Int32 では -1）だけでなく **0 も実体で測り直す**
（参照実装 `core/src/player/audioPlayer.ts` の `declared > 0 && declared <= actual`）。

0 を弾くと「data チャンクがありません」になり、`AudioFailed` → 1回リトライ →
**「seq=N の音声を取れなかったので飛ばします」で全文が無音スキップ**される。
合成側が data サイズを後追いで埋める書き方に変えただけでこうなる。

★ **測り直しを全チャンクに広げないこと。** 広げると、宣言サイズ 0 のチャンクで
「末尾まで」が採用されて `offset` が範囲外へ飛び、**その先の `data` に到達しないまま
「data チャンクがありません」になる**。踏むのは「`data` より手前に長さ 0 の
`LIST` / `fact` がある WAV」だけだが、**この PR で一度実際に入れて指摘された**。

写し元も測り直しは `data` の分岐の中だけで、**前進には宣言値をそのまま使っている**
（`core/src/player/audioPlayer.ts`）。長さ 0 のチャンクはそこで 8 バイトだけ進んで走査が続く。

★ **ただし「前進は宣言値」だけだと int が溢れる。** `body + declared` は int で計算するので、
`declared` が `int.MaxValue` 級だと**負に折り返す**。すると `offset` が負のままループ条件
（`offset + 8 <= wav.Length`）を通り、`Encoding4` の `data[offset]` が
`IndexOutOfRangeException` を投げる。`Decode` に try/catch は無く、呼び出し元の
`FetchAudioAsync` は `_ = FetchAudioAsync(...)` の fire-and-forget なので、
**例外は未観測のまま捨てられ、その seq に `AudioReady` も `AudioFailed` も来ないまま
キューの head が黙って止まる**——無音の原因が読めない、いちばん困る形。

`declared <= available` を前進の条件に足せば `body + declared <= wav.Length` なので溢れない。
越えている時点でその先に走査するものは無いので、打ち切りが正しい挙動でもある。

> **これは測り直しの回帰（上）を直した副作用ではなく、最初からあった。** 全チャンクに
> 広げていた間だけ、`declared <= available` の判定が偶然ガードになって隠れていた。

回帰テスト: `WavDecoderTests` の `MeasuresZeroSizedDataChunk` /
`MeasuresOversizedDataChunk` / `RejectsTrulyEmptyDataChunk` /
`SkipsZeroSizedChunkBeforeData` / `DoesNotOverflowOnHugeChunkSize`。

### ★ .NET の正規表現は JS より緩い（`$` と `\d`）

契約の charset を JS から写すときに2箇所ずれる:

- **`$` は末尾の改行の手前にもマッチする。** `^…$` のままだと `gen-1\n` や
  `/audio/gen-1-000000000001.wav\n` が通る。**`\A` / `\z`** を使う
- **`\d` は Unicode の十進数字にマッチする**（JS の `\d` は ASCII のみ）。
  アラビア・インド数字（`٠`-`٩`）12桁の `seq` が通る。**`[0-9]`** を使う

どちらも `core/src/core/audioPath.ts` は弾く。通った値は `BaseUrl` と連結されて
**そのまま URL になる**（`Runtime/Protocol/SpeechEpoch.cs`）。

回帰テスト: `SpeechFrameTests` の `TrailingNewlineIsRejected` /
`NonAsciiDigitsInAudioPathAreRejected`。

### ★ テストアセンブリの `overrideReferences` に注意

`ChatterMascot.Tests.asmdef` は `overrideReferences: true` なので、
**`precompiledReferences` に挙げた DLL しか参照できない**。テストが Newtonsoft を直接使うなら
`Newtonsoft.Json.dll` を足す（`nunit.framework.dll` だけだとコンパイルが通らない）。

### ★ `build.sh` は終了コードを捨てないこと

`| grep ... || true` にすると `BuildScript` の `EditorApplication.Exit(1)` が消え、
判定が「成果物があるか」だけになる。**一度でも成功していれば古い `.app` が残っている**ので、
コンパイルエラーでも「できました」と言って exit 0 する —— 直っていないバイナリを
直ったつもりで起動することになる。`test.sh` と同じ `PIPESTATUS` の形に揃える。

`$OUTPUT` が絶対パスのとき（`BuildScript.cs` の `Path.IsPathRooted` が許容する）に
`$PROJECT_PATH/` を前置しないことも要る。

### ★ `test.sh` はコンパイル失敗時に前回の結果を表示する

**実際に踏んだ（2026-08-27）。** UniVRM の型を触っていてコンパイルが通らなくなったときの出力:

```
Assets/ChatterMascot/Vrm/VrmIdleAnimation.cs(170,43): error CS0012:
The type 'ITimeControl' is defined in an assembly that is not referenced. ...
Aborting batchmode due to failure:

total=175 passed=175 failed=0 skipped=0 duration=0.8596495s
```

**コンパイルが通っていないのに `total=175 passed=175 failed=0` と出る。** 原因は
`Logs/test-results.xml` が**前回成功時のまま残っている**こと。集計側は

```bash
if [ -f "$RESULTS" ]; then
  python3 - "$RESULTS" <<'PY'
  ...
```

と**存在だけ**を見ていて、**今回の実行で書かれたものかを確かめていない**。テストが1件も
走らずに Editor が落ちても、ファイルさえ残っていれば古い集計がそのまま出る。

- **終了コードは正しく非0になる。** `STATUS=${PIPESTATUS[0]}` はコンパイル失敗を正しく拾うので、
  **CI では検出できる。壊れているのは人が読む1行のほう**
- `docs/mascot.md`（このファイル）も `CLAUDE.md` も「**件数は `./scripts/test.sh` の `total=` を
  見る**」と案内している。つまり**このリポジトリが公式に案内している確認方法が、
  コンパイル失敗を成功として表示する**

直し方は `run_unity` を呼ぶ前に古い XML を消す（`rm -f "$RESULTS"`）。
消せば集計側の `if [ -f "$RESULTS" ]` が偽になり、
「XML が書かれなかった＝走る前に落ちた」が正しく表現される。

★ **`total=` だけを見て緑と判断しないこと。** 出力の上のほう、`error CS` と
`Aborting batchmode` を先に見る。

### ★ ビルド対象シーンは `EditorBuildSettings` にも入れる

`scripts/build.sh` は `-buildScene` を明示で渡すので通るが、
**Unity の `File > Build Settings > Build` や `-buildScene` を渡さない経路（#54 の CI）は
`EditorBuildSettings` を見る**。テンプレート既定の `SampleScene` のままだと、
そこには `MascotRunner` も `UniWindowController` も `EventSystem` も無いので、
出来上がる `.app` は**不透明なウィンドウが出て、何にも繋がらず、エラーも出さない**。

`SceneFixups.EnsureBuildScenes()` が本番シーン1本に揃える。

### ★ ビルド済みアプリが読む `StreamingAssets` は `.app` の中のコピー

`Assets/StreamingAssets/` のファイルを動かしても、**ビルド済みアプリには効かない**。
アプリが読むのは `Build/ChatterMascot.app/Contents/Resources/Data/StreamingAssets/` に
コピーされたもの（`Player.log` に採用したパスが出る）。

★ **同梱ファイルを外して手続き的フォールバックを試すときは、`.app` の中を触るか
再ビルドすること。** リポジトリ側の `Assets/StreamingAssets/idle_loop.vrma` を退避しても、
既にビルドされた `.app` はコピー済みのファイルをそのまま読み続ける。

### Git-LFS 依存は #56 で復活した

`Assets/StreamingAssets/vita.vrm`（19MB）と `idle_loop.vrma`（154KB）が入ったので、
**clone と #54 の CI checkout に `git lfs` が要る**。`.gitattributes` の
`*.vrm` / `*.vrma` 規則は #17 のために先回りで置いてあったのでそのまま発火した。

> #12 の時点では**ゼロだった**。`Assets/TutorialInfo/`（`ReadmeEditor.cs` / `Readme.cs` /
> `Layout.wlt` / `Icons/URP.png`）と `Assets/Readme.asset` /
> `Assets/Scenes/SampleScene.unity` はどこからも参照されておらず、外した理由は
> diff のノイズだけではなかった —— `.gitattributes` の `*.png` が `Icons/URP.png` を
> LFS 送りにしていて、**`git lfs ls-files` の出力がこの1件だけ**だった。
> 消してゼロにしたことで、一時的に LFS が要らなくなっていた。

★ **`*.png` 規則を「VRM のテクスチャが入るから」という理由で残していたのは誤り**だった。
VRM のテクスチャは `.vrm` の中にあるので、`.png` が単体で入ることはない。
規則自体は他の用途（アイコンなど）で意味があるので残してあるが、根拠は上のものではない。

★ **`.gitignore` は `Assets/StreamingAssets/` 以外の `*.vrm` / `*.vrma` を落とす。**
再配布禁止のモデル（`AvatarSample_A.vrm` など）を差し替え検証のあと
うっかりコミットする導線を塞ぐため。差し替えは `-vrm` 起動引数か
`~/.config/chatter-agent/models/` から読ませること。

★ あわせて `com.unity.ai.assistant`（unity-mcp が使う）も外した。MCP ビルドが
モーダルダイアログで沈黙する罠を踏んで CLI batchmode に切り替えたので、依存の理由が消えている。

### Sentis（`com.unity.ai.inference`）はテンプレート同梱だが要らない

3D テンプレートに入っているが、**誰も依存していない**（unity-mcp が使う
`com.unity.ai.assistant` も依存していない）。残すとビルドのたびに**膨大なシェーダー警告**が出て、
コンパイル時間も伸びる。外してある。

## 実装の決めごと

### `PlaybackQueue` に判断を集める

`core/src/player/playbackQueue.ts` と同じ形で、**イベントを入れるとコマンドの配列が返る純粋な関数**。
副作用（取得・再生・ack）は `MascotRunner` が実行し、結果をまたイベントとして戻す。

こうしてあるのは、**完了コールバックが状態機械に再入する**（「ループの途中で状態が変わる」）
バグをテストで捕まえるため。テストは「このイベント列でこのコマンド列が出る」を配列比較で固定する。

契約の危険な箇所はほぼ全部この中にある。触るときは
[`protocol.md`](./protocol.md) の「クライアント側の責務」8項目と突き合わせること。

### ★ `JsonUtility` を使わない

契約は `audio` キーの**欠落**と `null` を区別することを要求している
（欠落 = #29 より前のサーバー / `null` = `ttsEnabled: false` という正常な設定）が、
`JsonUtility` にはこの区別ができない。**潰すと、繋ぎ先が古いことに気づく唯一の手がかりが消える。**

`com.unity.nuget.newtonsoft-json` の `JObject` で判定している
（`Runtime/Protocol/SpeechFrame.cs` の `TryParse`）。

### ★ `AudioSource` 1本では孤児（旧 epoch）の契約を守れない

`PlaybackQueue.ResetEpoch` は採番のやり直しを検出すると、再生中の item を孤児に移して
**「音は最後まで流す」**ことにする（途中で切る方が事故に聞こえるため）。
参照実装（`core/src/player/audioPlayer.ts`）は clip ごとに `afplay` を spawn するので、
孤児が本当に並行して鳴り切る。

`AudioSource` 1本を共有すると、この契約が3段で壊れる:

1. 新しいエポックの1文目の `Play()` が**孤児の音を消す**
2. しばらくして孤児側のループが期限切れで `Stop()` し、**新しい文が途中で切れる**
3. それでも `Played` / `PlaybackFailed` は返るので、切れた文は喋り切ったものとして
   ack され、**サーバーのキューから物理削除される**（もう取り直せない）

voice をプールして **`Stop()` の効果を自分の再生ぶんに限定する**
（`Runtime/Audio/AudioClipPlayer.cs`）。

- **横取り（steal）はしない。** 掴んでいる間は他の再生が触らないので、
  「自分がまだ持ち主か」を確かめる世代カウンタが要らない
- **上限は設けず、閾値を超えたら警告する。** voice が積むのは
  「採番のやり直しが、音が鳴り終わる前に繰り返されている」ときだけなので、
  **本数そのものが原因を指す材料**になる
- ★ **かつて `Current`（最後に鳴らし始めた voice）を公開していた。#58 のレビューで消した。**
  もともと #17 のリップシンクが `GetOutputData` を読む先を1つに決めるためだったが、
  **macOS でこの前提が成立しなかった** —— 再生の実体が `AfplaySpeechPlayer` になり、
  **音は `afplay` 子プロセスの中にあって `GetOutputData` に相当するものが存在しない**。
  `MascotRunner._player` も `ISpeechPlayer` 型なので、インターフェース越しにも届かない。
  → #58 は **`Prepare` の時点で WAV から振幅エンベロープ（20ms ごとの RMS）を作って
  ハンドルに載せる**方式で入れた（`ILipSyncSource` / `LipSyncEnvelope`）。`WavDecoder` が既に
  サンプル位置とフォーマットを読めるので追加のパースは要らず、**3つの実装すべてで同じコードが使える**。
  ★ **同じものを作り直さないこと。** 読み手ゼロの `Current` は、voice プールを跨いだ
  正しさ（`PlayAsync` の代入・後始末・`StopAll`）を**消費者のために保つのではなく、
  保つこと自体のために**保っていた
- **設定の写し取り（`CopySettings`）を増やしたらここにも書くこと。**
  #17 でミキサーや 3D 配置を入れて写し漏らすと、症状は「**孤児だけ音量が違う**」のような、
  再現条件が採番のやり直しに縛られた形になる

★ **再生の期限はクリップの実長に比例させる**（`length * 2 + 5秒`）。参照実装と同じで、
倍にしているのは「デバイスが詰まったときのぶん」。固定の +2秒だと Bluetooth の再ネゴなどで
数秒止まっただけで20秒の文が切られ、上の3と同じ経路で二度と鳴らせなくなる。

★ **EditMode では確認できない。** `AudioSource.Play()` は Play Mode でないと
`isPlaying` にならないので、再生ループはテストで固定できない。

#### 実機での確かめ方（1回目は失敗した）

**「長い文を積んで、しばらく待ってから採番をやり直す」では判定できない。** 直前のメッセージの
読み上げがまだ終わっていないと、長い文は**再生されずにキューで待っている**だけなので、
採番のやり直しで `DiscardAudio` に落ちる。孤児になるのは別の文で、耳では区別がつかない。

判定できる形にするには2つ要る:

1. **キューが空になるまで待ってから積む**（直前の読み上げが ack まで終わっている）
2. **自分でも `GET /audio/…` して長さを測る。** サーバーは single-flight なので合成は1回だけ。
   GET が返った時点 ≒ 合成完了 ≒ 再生開始なので、そこから「長さ × 0.4」待てば
   **必ず再生の途中**でやり直しをかけられる

耳で聞くものは**1から30まで数える音声**にするとよい。切れたら何番で切れたかが分かる。

実測（2026-08-24 / macOS）: 全長 **19.3秒**の数え上げが、**7.7秒の時点**で孤児になったあとも
**「さんじゅう」まで鳴り切った**（残り 11.6秒）。同時に新しい世代の1文目が頭から重なって鳴った。

★ このとき**3つの音が重なって聞こえた**が、うち1つはテストの副産物。スクリプトが先に
seq 7000 を積み、あとから実際の発話が seq 6000台で publish されたため、サーバーが
「配信済みを `seq` で覚えている」性質で **7000 → 6000台の順に配った**。マスコット側は
7000 を再生中により小さい seq が head として入る。実運用では CLI が seq を戻さないので
起きないが、**多voice 化していなければ「重なる」ではなく「消える」になっていた**。

### ★ 無音時にオーディオ出力デバイスを掴まない

**手放し方はプラットフォームで違う。** 選ぶのは `SpeechPlayerFactory`。

| | 再生の実体 | 手放し方 |
|---|---|---|
| **macOS** | `AfplaySpeechPlayer`（1発話 = 1プロセスで `afplay`） | プロセスが消えれば OS が解放する（実測 **0.5〜1秒**） |
| **Android / iOS** | `AudioClipPlayer`（Unity 内蔵） | `AudioSettings.Mobile.StopAudioOutput()` |
| その他 | `AudioClipPlayer` | **手放せない**（Windows / Linux は未対応） |

★ **macOS では `Disable Unity Audio` が ON でないと意味が無い。** 外部プロセスで鳴らしても、
Unity 内蔵オーディオが有効なままだと Unity 側がデバイスを掴む（上の実測）。
`BuildScript.BuildMacOS` が**ビルド時だけ**切り替えて、ビルド後に戻す。
プロジェクト設定はプラットフォーム別に持てないので、**コミットされた値は Android 側の要求
（オフ）に合わせてある**。★ **Editor の GUI からビルドすると切り替わらない**ので、
ビルドは `scripts/build.sh` から行うこと。

★ **実測（本番ビルドで実際に喋らせた / 2026-08-25）**: afplay の pid が発話ごとに入れ替わり、
文の切れ目で `device=0` になる。Unity 本体は **103サンプルすべてで CoreAudio に認識されず**、
**うち 95サンプルは afplay が鳴っている最中**だった（＝「鳴っていないから掴んでいない」ではない）。

★ **`AudioIdleGate` は macOS では働かない。** afplay 方式には手放すものが残っていないので
`SuspendOutput` は no-op。**Android / iOS でだけ効く**（→ [#25](https://github.com/schwarz9791/chatter-agent/issues/25)）。
それでも判定を切り出してあるのは、猶予の設計とテストをプラットフォーム間で共有するため。

**「喋っていない期間」は `AudioIdleGate` を作るまでコードのどこにも存在しなかった。**
`PlaybackQueue.HeadItem(state) == null` と `ActiveCount == 0` の副次的な帰結としてしか
観測できず、名前が無かった。デバイスを手放す判断は間違えると**孤児の音が凍る**ので、
テストで固定できる純粋クラスに切り出してある。

**アイドルの定義**: `ISpeechPlayer.ActiveCount == 0` かつ `PlaybackState.Items.Count == 0`
かつ `Orphans.Count == 0` が猶予ぶん続いた状態。

- `Items` を見るのは、`Pending` / `Fetching` / `Ready` が「合成待ちで、まもなく鳴る」から。
  ここで手放すと掴み直しが再生に間に合わず**1文目の頭が切れる**
- **`Orphans` を見るのは契約（孤児を鳴らし切る）のため。** `ResetEpoch` は再生中の item を
  `Items` から外して `Orphans` へ移すので、**`Items` が空でも鳴っていることがある**

★ **`PlaybackQueue` には手を入れないこと。** `Items` / `Orphans` は public なので
ドライバから**読むだけ**で足りる。状態機械にコマンドを増やすと、EditMode テストの
コマンド列比較が全部壊れる。

★ **resume は再生の直前ではなく `FetchAudio` で打つ。** `GET /audio/…` はサーバーに
合成させるので数百ms〜数秒かかり、先読みのぶんだけ再生よりさらに手前で走る。
デバイスの掴み直し（Bluetooth なら A2DP の張り直し）は**その待ちの裏に隠れる**。
保険として各実装の `PlayAsync` 冒頭でも `ResumeOutput()` を呼ぶ（べき等）。

★ **猶予を短くしすぎないこと。** 文と文の間で往復すると、A2DP の張り直しが毎文入って
**かえって悪化する**。長すぎる害は省電力が薄れるだけ（無害側）。既定は 5秒。
`audioIdleSuspendMs` を 0 以下にすると無効（キルスイッチ）。

★ **アイドル判定を `TickIntervalSeconds`（1秒）の間引きに乗せないこと。** 判定は加算と
比較だけなので毎フレームで足りるし、間引きに乗せると **resume が最大1秒遅れる**。

★ **時計は `Time.realtimeSinceStartupAsDouble` を使う**（`DateTimeOffset.UtcNow` ではなく）。
猶予は差分でしか見ないので、**時計が巻き戻ると手放したまま戻らない**。

#### プロジェクト設定まわりで踏んだこと

★ **Unity は YAML に無い `SerializeField` に C# のイニシャライザ値を残す。** `MascotRunner` に
`audioIdleSuspendMs = 5000` を足したがシーンを保存し直していないので、`Mascot.unity` の
`MonoBehaviour` ブロックにこのキーは**無い**。それでも実測では 5000 が効いていた
（キーが無い状態のビルドで「無音が続いたので…」のログが12回出た）。型の既定値 0 が
当たるわけではない。★ ただし**シーンを一度でも保存すると値が焼かれる**ので、
既定を変えるときはシーンも見ること。

★ **`AudioManager.asset` に Unity 6 の新キーが4つ増えている**（`m_EnableOutputSuspension: 1` /
`m_AudioFoundation: 0` / `m_OutputChannelLayout: 2` / `m_OutputSamplingRate: 48000`）。
`BuildScript` が `m_DisableAudio` を書き換えるとき `AssetDatabase.SaveAssets()` が走り、
Unity がアセット全体を再シリアライズしてテンプレートに無かったフィールドを既定値で書き出したもの。

- **値はすべて Unity 6000.5.8f1 の既定値**。Editor バイナリの `-enhancedAudioFoundation` の
  ヘルプが `Default: 48000` / `Stereo (default)` と明記している
- **環境固有の値ではない**。この Mac の既定出力デバイスは 44100（`system_profiler`）で、
  48000 とは一致しない
- ★ **`m_AudioFoundation: 0`（Classic）なので、`m_OutputSamplingRate` と
  `m_OutputChannelLayout` は無視される**（"If it is disabled, sampling rate and channel layout
  parameters will be ignored."）。Android で AivisSpeech の 24kHz が余計にリサンプルされる、
  ということは起きない
- 従来設定の `m_SampleRate: 0`（システム既定に従う）は**別のキーで、変更されていない**

★ **`mixerSuspend` 系の API は `mixerResume` と同じスレッドから呼ぶ必要がある。**
`Update()` も `Execute()` も `PlayAsync` の継続も Unity のメインスレッドなので自然に
満たせるが、実装側にも検査を置くこと。壊れ方が「たまに無音」なので静かに壊れさせない。

★ **resume に失敗しても「掴んでいる」側に倒すこと。** suspend したままのフラグが残ると
**二度と resume を試さず恒久的に無音になる**。無音より二重 resume の方が軽い。

### ★ 口は「再生中の音を測る」のではなく「`Prepare` の時点で作っておく」（#58）

cc-mascot は `AudioSource.GetOutputData()` の RMS を毎フレーム読んで `aa` に流している。
**この方式が macOS で成立しない**（→ 上の「`AudioSource` 1本では孤児の契約を守れない」）ので、
**`ISpeechPlayer.Prepare` の時点で WAV から振幅エンベロープ（20ms ごとの RMS）を作って
ハンドルに載せる**。ハンドルが `ILipSyncSource` を実装し、`MascotRunner` が
`audio as ILipSyncSource` で読む。

```
Prepare(wav) → ILipSyncSource を実装したハンドル → SpeakingSet → MouthTracker → FacePolicy → aa
   (3実装で共通)      (macOS / Unity 内蔵で別型)      (純粋)      (純粋)      (純粋)
```

★ **`ISpeechPlayer` も `PlaybackQueue` も変更していない。** 状態機械から見ればハンドルは
依然 `object` のままで、`AudioIdleGate`（#55）と同じ「`Items` を読むだけ」の流儀。
コマンドを増やすと EditMode テストのコマンド列比較が全部壊れる。

#### ★ エンベロープが作れなくても `Prepare` を失敗させない

**リップシンクの都合で発話を落とすのは本末転倒。** `Prepare` が `null` を返すと
`AudioFailed` → skip + ack となり、**サーバーのキューから物理削除されて二度と鳴らせない**。
`Envelope = null` に倒して1回だけ警告する。

回帰テスト: `AfplaySpeechPlayerTests.PrepareSucceedsEvenWhenTheEnvelopeCannotBeBuilt`。
材料は **`bitsPerSample = 12` の PCM** —— `TryReadHeader` は通り、サンプルの読み出しだけが落ちる。

#### ★ 20ms のエンベロープを 33.3ms で「点サンプリング」しない

エンベロープの刻み（20ms）と表示（30fps = 33.3ms）は割り切れない。33.3ms 刻みで点を取ると
**フレーム 2 / 4 / 7 / 9 … に一度も当たらず、4割を読み飛ばす**（＝立ち上がりが落ちて口が鈍る）。
`SpeakingSet.Mouth(from, to, offsetMs)` は**前フレームからの区間の最大値**を返す。

★ **区間の始点を `MascotRunner` に持たせないこと。** `Mouth()` が冪等でなくなる（呼び出し元の
数に依存する API になる）うえ、`MascotRunner.Update` は `VrmCharacter.LateUpdate` より前の
位相なので区間が半フレームずれる。始点は `MouthTracker`（`Runtime/Vrm/`）が持つ。

★★ **始点を `double.NegativeInfinity` で初期化しないこと。** `Mouth(-∞, now)` は
**エンベロープ全体を走査して全体最大を返す**ので、**最初のフレームで口が全開に飛ぶ**。
未サンプルは `NaN` で表し、`from = to` の点サンプル1回に倒す。

#### ★★ ラグの補正で「負のインデックスを 0 にクランプ」しない

`afplay` は `Process.Start` が音より前に返るので、その起動ラグぶん口が先に動く。
`lipSyncOffsetMs` を引いて索引するのだが、**`index = max(0, index)` と書くと
offset ぶんの先行区間で `envelope[0]` を返す＝音より先に口が動く**ので、補正の意味が消える。
正しいのは**区間 `[lo, hi]` 全体が音より前（`hi < 0`）なら 0（口を閉じたまま）**。

回帰テスト: `SpeakingSetTests.OffsetKeepsTheMouthClosedBeforeTheSoundStarts`。

#### ★ 端数フレームは実サンプル数で割る

`LipSyncEnvelope.Build` の末尾のフレームはフレーム長に満たない。ゼロ埋めしてフレーム長で
割ると最後だけ小さくなり、**語尾で口が閉じる**。半フレームぶんの直流 1.0 はゼロ埋め実装だと
0.707 になるので、`LipSyncEnvelopeTests.TrailingPartialFrameIsNotDiluted` の1本で確実に捕まる。

★ **24000Hz / 1ch に決め打ちしないこと。** `ttsBaseUrl` を VOICEVOX に向ければ別のレートに
なりうる（そのとき口が音に合わなくなるが、**エラーは出ない**）。

#### ★ サンプルの読み出しは `WavDecoder` を再利用する（ただしバッファは渡す）

8/16/24/32bit PCM と IEEE float32 の分岐（特に **24bit の符号拡張**）を書き写さないこと。
独立実装が2つあると片方だけ直したときに黙ってズレる（`VrmCharacter.HasBindings` /
`VrmStage.MeasureBounds` を `public static` にしてあるのと同じ理由）。

★ **ただし `TryReadSamples`（`float[]` を確保して返す版）をそのまま使わないこと。**
`AfplaySpeechPlayer.Prepare` は**もともとサンプルをデコードしていない**ので、24kHz mono 5秒で
**480KB の使い捨てゴミが丸ごと新規に**乗る（しかもメインスレッド）。`TryReadSamplesInto`
（呼び出し側のバッファに詰める版）を足して、`Build` は 20ms 分（約 2KB）を使い回す。
**分岐は1箇所のままなので、再利用の趣旨は損なわれていない。**

★ `AudioClipPlayer` 側は `AudioClip` 用とエンベロープ用で**サンプルを二度デコードする**。
消すには `Decode` から `float[]` を貰う形にする必要があるが、数百 KB を一度余分になめるだけ
（約 1ms）なので今はやっていない。この実装が主役になるのは Android（#25）。

#### ★ ゲインと減衰は `FacePolicy` ではなく `MouthTracker` に置く

`FaceParams` は「**0 = 無効**」で統一されている（`PromptSurpriseWeight` も
`BlinkSuppressAboveHappy` も）。**ゲインはこの語彙に乗らない** —— 入れると
`FacePolicyTests.AllZeroParamsMakeEvaluateEqualTarget` が固定している
「`FaceParams` を全部 0 にすると `Evaluate` は `Target` と一致する」が壊れる（`gain = 0` で
口が常に閉じる）。だから `mouthGain`（cc-mascot の `rms * 4`）と `mouthReleasePerSecond` は
`VrmCharacter` の `[SerializeField]` → `MouthTracker` の引数で渡す。

★ **口のスケール（happy / sad）だけは `FacePolicy` にしか書けない。** 緩和後の weight に
依存するので、上流の `MouthTracker` からは見えない。

★ **attack は即時 / release だけ減衰**（`w = max(target, w - release * dt)`）。
非対称なので指数緩和（`GazeAim.Smooth`）ではないが、`* dt` があるのでフレームレート
非依存性は保たれる（`MouthTrackerTests.ReleaseIsFrameRateIndependent` が 30fps と 60fps で
同じ値になることを固定している）。

★ **`FacePolicy` の2つの宣言はそのまま生きている。** 「`Aa` は補間しない」（整形は上流で
済んでいる）と「`Speaking` が false なら猶予なしで 0」（口を確実に閉じる**最後の砦**）。
release が効くのは**発話中の音素の谷**だけ。

#### ★★ `SpeakingSet` が `SpeakingView` を置き換えた（孤児の穴が閉じた）

`SpeakingView` は `PlaybackState.Items` の `Status == Playing` を走査していたが、
採番のやり直しで `Orphans` へ移った発話は**音声ハンドルしか持たず `SpeechFrame`（`Record`）を
持たない**ので、**孤児が鳴っている間ずっと `false`（＝喋っていない）と答えていた**。

`SpeakingSet` は**再生を始めた時点で emotion / kind を写し取る**ので、`Items` から消えた後も
答えられる。結果として、孤児が鳴っている間も口・表情・体の動き（`IdlePose` の `SpeakingGain`）が
続くようになった。

- **口の開きは全発話の `max`。** 口は1つでスピーカーも1つなので、「今いちばん大きく鳴っている音」に
  合わせるのが物理的に正しい
- **`TryGetFace` は最後に始まったもの。** 表情は「今の話題」に従うべきで、消えゆく旧エポックではない
- ★ **`false` のとき `Assistant` / `Neutral` に倒す契約は `SpeakingView` から移送した。**
  `VrmCharacter.LateUpdate` がこれに寄りかかっていて、呼び出し側で `Speaking ? kind : 既定` と
  書き直していない（`SpeakingSetTests.TryGetFaceFallsBackToAssistantNeutral`）

★ **`Begin` は `Execute` の `Play` の直前、`End` は `PlayAsync` の `finally`。**
`PlayAsync` は同期完了する経路（「音声のハンドルがありません」など）があり、そこから
`Dispatch` がコマンドループへ再入する。また `_ = PlayAsync(...)` の fire-and-forget なので、
実装が例外を投げるとその例外は**未観測のまま捨てられる** —— `finally` でないと
**口が開きっぱなしのまま永久に固まる**。

#### ★ 30fps で口が足りるかの決着（#58）— 据え置き

`Application.targetFrameRate = 30` のままで足りる。逃げ道（`speakingFrameRate`）は用意したが
**既定 0（＝変えない）**。

**実測**（2026-08-29 / macOS 26.6.2 / ウィンドウ 300x480 / `AvatarSample_A.vrm` +
同梱 `idle_loop.vrma` / 1〜30 の数え上げを連続再生 / `ps -o %cpu` を6秒間隔で n=6）:

| | 発話中の CPU |
|---|---|
| **30fps**（`speakingFrameRate: 0`） | 13.9 / 17.5 / 17.9 / 14.2 / 18.8 / 18.0 → **中央値 17.7%** |
| 60fps（`speakingFrameRate: 60`） | 34.3 / 36.0 / 37.5 / 37.8 / 37.8 / 38.5 → **中央値 37.7%** |

**60fps はおよそ 2.1 倍。** 常駐アプリの電力設計（#55）に対してこの差は大きい。
#59 時点の**無音時**の実測（同条件で 13.2%）と並べると、30fps では発話が乗っても +4.5 ポイントで済む。

★ **口の応答は 30fps でも落ちていない。** 20ms 刻みのエンベロープを 33.3ms 間隔で読むと
4割のフレームを読み飛ばすが、`SpeakingSet.Mouth` が**区間の最大**を取るので拾い切る。
実測（`-faceLog 1 -faceLogMs 100`、発話中の `目標 aa` を 86 行）:

```
min=0.00 p25=0.00 p50=0.40 p75=0.73 max=1.00 平均=0.40
飽和(1.00) 10.5% / ゼロ(0.00) 30.2%
```

同じ WAV をオフラインで 20ms ごとに RMS へ落として `gain = 4` を掛けた予測は
「飽和 9.6% / 平均開度 0.35」なので、**実機の分布が予測とほぼ一致している**
（＝区間最大が読み飛ばしを埋めている）。★ **`gain = 4`（cc-mascot の `rms * 4`）はそのまま使えた。**
AivisSpeech の出力は 44100Hz mono で、エンベロープの中央値 0.063 / p90 0.240 / 最大 0.392。

★ **`speakingFrameRate` を「念のため」常時 60 にしないこと。** 上の 2.1 倍がそのまま乗る。

#### 目視でも確認した（2026-08-29）

上の実測はすべて `Player.log` からの機械判定で、**「口が階段状に見えないか」「音と口が
ずれて見えないか」「笑顔で口がはみ出ないか」は目で見ないと決まらない**。実機の
macOS ビルドで確認し、いずれも問題なしと判断した。

- **音と口のタイミング**（`lipSyncOffsetMs: 120`）— ずれて見えない
- **30fps で階段状に見えない**（`speakingFrameRate: 0` のまま）
- **`happy` / `sad` で口がメッシュからはみ出ない**（`mouthScaleHappy: 0.2` / `mouthScaleSad: 0.5`）

★ **確認はウィンドウを一時的に 2 倍（600x960）にして行った。** 既定の 300x480 では
**キャラクターが小さくて口の粗が判別できない**。大きさは起動引数だけで変えられる:

```bash
open Build/ChatterMascot.app --args -screen-width 600 -screen-height 960
```

★ **確認のあと `~/Library/Preferences/tech.sukima.chatter-mascot.plist` を戻すこと。**
Unity は終了時にそのときの大きさを焼き付けるので（→ 上の「ウィンドウの大きさは3箇所で
決まる」の 1）、放っておくと**次回から 600x960 で開く**。しかもバンドル ID は
チェックアウトを跨いで共通なので、**別 worktree のマスコットまで大きくなる**。

```bash
defaults write tech.sukima.chatter-mascot "Screenmanager Resolution Width" -int 300
defaults write tech.sukima.chatter-mascot "Screenmanager Resolution Height" -int 480
```

#### ★ `afplay` の起動ラグは 116ms。較正ログの「差」をそのまま入れない

`lipSyncOffsetMs` の既定 **120** は実測値（2026-08-29 / macOS 26.6.2 / **内蔵スピーカー**）。

★★ **`PlayAsync` の較正ログが出す「実時間 − WAV の長さ」は約 470ms だが、これをそのまま
`lipSyncOffsetMs` に入れてはいけない。** 内訳は **起動ラグ 116ms + 終了処理 357ms** で、
欲しいのは前者だけ。後者まで足すと口が音より **0.35 秒遅れる**。

内訳は CoreAudio の `kAudioDevicePropertyDeviceIsRunningSomewhere` を 2ms 間隔でポーリングして
直接測った（`Process.Start` → 出力デバイスが動き出すまで）:

```
起動 → 音が出るまで:            中央値 116ms  (100, 106, 116, 125, 134)
音の開始 → プロセス終了 − 長さ: 中央値 357ms  (352, 357, 357, 366, 368)
```

★ **`afplay` 単体の総オーバーヘッドは 400〜900ms とばらつく**（0.05秒 / 0.5秒 / 3.0秒の
サイン波で n=5）。デバイスの開閉が効いているとみられ、**総時間から起動ラグを推定するのは無理**。
だから較正ログは「桁の確認」（100ms オーダーか、それとも桁が違うか）にだけ使う。

★ **Bluetooth ではもっと大きい。** 上は内蔵スピーカーでの値で、A2DP の遅延はデバイス側で
さらに乗る。**秒数を仕様として扱わないこと。**

★ 測定は他のアプリが音を出していると成立しない（`IsRunningSomewhere` はシステム全体の値）。
プロセス単位で見たいときは `kAudioProcessPropertyIsRunningOutput`（→ 上の「無音でも出力デバイスを
掴み続ける」の測り方）。

#### ★ `-faceLogMs` は実機確認専用（既定の1秒を縮めない）

`faceDebugLog` のログは既定 1秒間隔。**孤児が重なっている間も口が止まらないこと**を
`aa=` の連続で判定するには粗すぎるので、起動引数 `-faceLogMs 100` で縮められるようにした。
**常用では縮めないこと** —— `Player.log` が流れて他の診断が読めなくなる。

### ★ 再生の期限は `WavHeader.DurationMs` から出す（`AudioClip.length` ではない）

実装をまたいで（Unity / FMOD / 外部プロセス）**期限の根拠を1つに揃える**ため、
`WavDecoder.TryReadHeader` が fmt チャンクの `byteRate` から計算する。式は参照実装
（`core/src/player/audioPlayer.ts` の `wavDurationMs`）と同じ `dataBytes / byteRate * 1000`。

★ **`DurationMs` の 0 は「長さ 0」ではなく「不明」。** 呼び出し側は 120秒
（参照実装の `FALLBACK_TIMEOUT_MS`）に倒すこと。長さ 0 として `0 * 2 + 5秒` を計算すると
**すべての再生が5秒で打ち切られ**、切られた文は `PlaybackFailed` → ack に落ちて
サーバーのキューからも消える（二度と鳴らせない）。

★ **ヘッダの検証を再生エンジンに任せないこと。** FMOD の `createSound` も OS のプレイヤーも、
失敗したときに返すのは「読めなかった」だけで**理由が残らない**。無音の原因を残す窓を
潰さないために、渡す前に `TryReadHeader` で見る。

### ★ ストリーム再生にしない（音声の持ち方はプラットフォームで違う）

契約の「ローカルに落としてから再生すること」は**ストリーム再生の禁止**であって、
ファイルを要求しているわけではない。**先に全部受け取ってから鳴らす**のが趣旨。

音声の実体は再生の実装ごとに違う（→ 上の「★ 無音時にオーディオ出力デバイスを掴まない」）:

| | 音声の持ち方 | `DiscardAudio` の実体 |
|---|---|---|
| macOS（`AfplaySpeechPlayer`） | 一時ファイル（`afplay` に渡すため） | `File.Delete` |
| Android / iOS（`AudioClipPlayer`） | メモリ上の `AudioClip` | `Object.Destroy(clip)` |

どちらも状態機械からは `object` の不透明なハンドルで、`DiscardAudio` コマンドは
**`ISpeechPlayer.Discard`** に読み替える。

★ **`UnityWebRequestMultimedia.GetAudioClip` を URL に直接使わないこと。** ストリーム再生に
なりうるうえ、**503 / 404 の本文（診断の理由）が取れない**。無音の原因を残す唯一の窓なので落とせない。
`DownloadHandlerBuffer` で `byte[]` を受けて `WavDecoder` に通す。

### ★ ping watchdog は同等品が作れない（劣化を受け入れている）

参照実装（`core/src/player/client.ts`）は、サーバーの ping が 90 秒途切れたら繋ぎ直す。
スリープ復帰や NAT テーブル切れで half-open になったとき、**「接続中のまま永久に無音」**を
検出する唯一の仕組みだった。

**`System.Net.WebSockets.ClientWebSocket` は ping を受け取っても通知しない。**
代わりに `SpeechClient` は2本立てにしてある:

1. `Options.KeepAliveInterval = 30秒`（送る側だけ設定できる）
2. **無受信 watchdog（既定5分）** — 何も受信しない状態が続いたら能動的に繋ぎ直す

★ **閾値を短くしないこと。** 数十秒の無音は正常（`AskUserQuestion` の直前）。
誤爆しても未 ack 分が再送されるだけなので安全側に倒れるが、短くすると
「正常な沈黙のたびに切断する」ことになる。

### `forceSingleInstance` が防げること / 防げないこと

`docs/protocol.md` のクライアント責務6は「同じルートに対して繋ぐクライアントは1台にすること」。
ack は累積で、サーバーは `seq <= N` を**キューから物理削除する**（誰が受け取ったかは見ない）ので、
速いクライアントの ack が遅いクライアントのまだ喋っていない entry を消す。

`ProjectSettings.asset` の `forceSingleInstance: 1` を立ててあるが、**これで防げるのは
同じ `.app` の二重起動だけ**。`npm run start:player` との併走は防げない。

完全な排他は参照実装と同じ `player.lock`（`core/src/core/paths.ts` の `getPlayerLockDir`）を
Unity 側からも取ることになるが、ランタイムルートの発見が要るうえ、
**Android XR にはサーバーと共有するファイルシステムが無い**ので macOS 限定の仕組みになる。
今は立てていない。

★ 症状は「発話を食い合う」＋「音声が 404 になる」。**耳で聞くと『たまに飛ぶ』**にしか
聞こえないので、無音の切り分けをする前に**player が動いていないかを先に確認する**こと。

### ★ `serverUrl` が不正だと「動いて見える死体」になる

`AudioFetcher.DeriveAudioBaseUrl` は `new Uri(serverUrl)` を呼ぶので、Inspector に
`127.0.0.1:8570`（スキーム無し）や空文字を入れただけで `UriFormatException` が飛び、
`MascotRunner.Start()` が最後まで走らない。すると **ウィンドウは出て、フレームレート上限も効いて、
接続先のログすら出ない**。Player.log に埋もれたスタックトレース1本以外に手がかりが残らない。

`Start()` の頭で `Uri.TryCreate` + スキーム（`ws` / `wss`）を検査して、
駄目なら入力値を名指しした `LogError` を出して `enabled = false` で止める。

### `ClientWebSocket` を選んだ理由

- **`Origin` を送らない** → サーバーの `allowedOrigins`（既定 `[]` = Origin 付きは全拒否）の
  設定が要らない。WebView から張ると Origin が付く（→ [`protocol.md`](./protocol.md) の表）
- 追加依存なし。macOS と Android を同じコードで通せる
- 引き換えが上の ping watchdog

## 動かす

```bash
cd apps/chatter-mascot
./scripts/test.sh                                         # EditMode テスト
./scripts/build.sh                                        # 本番シーン → Build/ChatterMascot.app
./scripts/build.sh Assets/Scenes/TransparencyProbe.unity Build/TransparencyProbe.app
```

**どれも Editor を閉じてから。**

耳で確認するときは `cd core && npm run start:server`（合成エンジンはサーバーが起こす）。

### ★ Unity CLI

`unity` コマンド（[Unity CLI](https://unity.com/ja/blog/meet-the-unity-cli)）が
手元に入っている。実体は `/Users/schwarz/.unity/bin/unity`、実測 **`1.0.0-beta.5`**（`unity --version`）。
`unity doctor` は `auth.loggedIn true` / `editor.0 6000.5.8f1 arm64` を認識し、`unity editors` は
インストール済みの `6000.5.8f1`（Android, SDK & NDK Tools, OpenJDK, Web）を拾う。導入は公式の

```bash
curl -fsSL https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.sh | UNITY_CLI_CHANNEL=beta bash
```

`test` / `build` / `run` の各サブコマンドが `scripts/*.sh` と役割が重なる:

| やること | いまの `scripts/` | Unity CLI |
|---|---|---|
| EditMode テスト | `./scripts/test.sh` | `unity test --mode EditMode --output Logs/test-results.xml` |
| macOS ビルド | `./scripts/build.sh` | `unity build --execute-method ChatterMascot.EditorTools.BuildScript.BuildMacOS -o Build/ChatterMascot.app` |
| 任意の Editor メソッド実行 | `./scripts/run.sh <Method>` | `unity run -- -quit -executeMethod <Method>`（**`--command` ではない** — そちらは事前登録が要る `unity pipeline install` 前提の別機能） |
| Editor の一覧 / インストール | 手動（Unity Hub） | `unity editors` / `unity install` / `unity install-modules` |
| 環境の診断 | 無し（`Player.log` を読むだけ） | `unity doctor` |

★ **`-quit` の要否は `-runTests` と `-executeMethod` で逆になる。** `-runTests` に `-quit` を
付けるとテストが走り切る前に落ちる（→ 下の1点目）が、`-executeMethod` は逆に **`-quit` を
付けないと Editor が終了しない**（`run.sh` の実装どおり）。ここを取り違えるのが移行で
いちばん間違えやすいところ。

★ **右列は `--help` の記載から組み立てたもので、まだ実行して確かめていない。** `unity test` /
`unity build` の実行は別 Issue（下記）に切り出してあり、ここでは対応関係だけを記録する。

**いまは `scripts/*.sh` を置き換えない。** 中身には #12 / #56 で実機を踏んで積んだ知見が入っていて、
機能追加のついでに差し替えると回帰リスクが乗る。置き換えるときに**失ってはいけない6点**:

1. **`-runTests` に `-quit` を付けない**（`test.sh` のコメント参照）。付けるとテストが走り切る前に落ちる
2. **`build.sh` の `trap restore_audio_manager EXIT INT TERM` による
   `ProjectSettings/AudioManager.asset` の `m_DisableAudio` 復元。**
   macOS で afplay 方式（1発話 = 1プロセス）が成立するための前提条件で、これが OFF だと
   外部プロセスで鳴らしても Unity 本体がデバイスを掴み続ける
   （→「無音時にオーディオ出力デバイスを掴まない」）。コミットされた値は Android 側の要求
   （オフ）に合わせてあるので、**ビルド時だけ切り替えて戻す**必要がある
3. **`PIPESTATUS` で終了コードを捨てないこと。** `test.sh` / `build.sh` はどちらも `| grep ...` を
   挟むので、素の `$?` は grep の終了コードになる
4. **NUnit XML を python3 で集計して `total= passed= failed=` を出すこと**（`test.sh`）
5. **`unity.sh` の `pgrep -f "Unity.app/Contents/MacOS/Unity.*${PROJECT_PATH}"` による
   「Editor が同プロジェクトを開いていたら中断」**
6. **`run.sh` の grep フィルタ（`^\[Fixups\]|^\[Build\]|^\[VrmProbe\]|error CS|...`）に
   無いプレフィックスのログは、`LogError` であっても画面に出ない**（`run.sh` のコメント
   参照）。この弱点自体を引き継ぐ必要はないが、`unity run` の出力がフィルタ無しで
   全ログを流すのか、移行時に確認すること

注意点:

- ★ **`unity test` が内部で `-runTests` をどう組み立てるかは確かめていない。** `--quit` 相当の
  オプションが表に出ていないので CLI 側が引き受けている可能性はあるが、**内部で付けていない
  保証は無い**。上の1点目は移行時に**実際に走り切ることを確かめる**まで未解決として扱う
- ★ **`unity build` は `Disable Unity Audio` の切り替えをやってくれない。** `--execute-method`
  を通しても `BuildScript.BuildMacOS` を呼ぶだけなので、上の2点目の trap は
  **`BuildScript.BuildMacOS` の責務のまま**残る
- ★ **`unity command` / `unity status` は `unity pipeline install` が要る** —
  `Packages/manifest.json` に依存が1本増える。**今は入れていない**
- ★ `unity editors` が `6000.5.10f1` へのアップグレードを示唆してくるが、
  **プロジェクトは `6000.5.8f1` 固定**（`ProjectSettings/ProjectVersion.txt` と
  `scripts/unity.sh` の `UNITY_VERSION`）

`scripts/*.sh` を Unity CLI に寄せる移行そのものは
[#67](https://github.com/schwarz9791/chatter-agent/issues/67) で追う。

## プラットフォームを絞る

★ **UniWindowController の macOS ネイティブプラグインが Android ビルドに混ざらないよう
Plugin Inspector で macOS に限定すること。** XR パッケージは Android にだけ効かせる
（→ [#25](https://github.com/schwarz9791/chatter-agent/issues/25)）。
