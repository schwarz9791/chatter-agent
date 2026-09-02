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

#### ★ 物理的な大きさはディスプレイのスケールで変わる（#16 で解決）

`defaultScreenWidth/Height` も、Unity が永続化する `Screenmanager Resolution *` も
**バッキング px**。だから:

- **Retina 2x で起動すると物理的に半分**になる（250x400 px = **125x200 pt**）
- **Retina で終了すると、次に 4K で開いたとき倍になる。** Retina 上の `Screen.*` は
  500x800 px なので、それが永続化され、1x のディスプレイでは **500x800 pt** の窓として開く。
  **実測で確認した**（当時の `WindowSizeKeeper` は「起動直後の大きさ」を守る作りだったので、
  これは打ち消せなかった）

当時の `WindowSizeKeeper` が直せるのは**同じディスプレイでの累積**だけだった。

**手当ては「ポイントで意図した大きさを自前で永続化する」こと**（#16 の
`Desktop/WindowGeometry.cs`）。`windowPosition` / `windowSize` / `GetMonitorRect` は
**すべてポイント**なので、**そこで閉じれば px↔pt の換算が production から1箇所も無くなる**。
（だから `WindowSizeKeeper` にあった「換算率をコントローラ自身から測る」コードも消えた。
測り方そのものは上の実測として残してある。）

### ★ ウィンドウの座標系は bottom-up・左下基準。モニタ矩形は「作業領域」

| | |
|---|---|
| `windowPosition` | 窓の**左下（最小コーナー）**。原点はメインディスプレイの**フルフレームの左下**（bottom-up） |
| `GetMonitorRect(i)` | **同じ空間**。ただし返るのは **visible frame（作業領域）** —— メニューバーや Dock の帯を含まない |
| 並び | `[0]` がメイン（AppKit が画面の一覧の先頭を「メニューバーのある画面」と定めている） |

★ **作業領域の和集合には隙間がある。** メニューバーや Dock の帯はどの矩形にも入らない。
**「どのモニタ矩形にも入らない＝画面外」と判定しないこと。**

★ **こちらから位置を書いたぶんは引き戻されない。** 画面外の位置を入れてもそのまま読み戻る
——**`isFreePositioningEnabled` が false のままでも**。macOS がウィンドウを画面内へ引き戻す
仕組みは、位置を直接書く経路には効かない。
→ **[#16](https://github.com/schwarz9791/chatter-agent/issues/16) のコメント1
「はみ出た分だけ画面内に戻される」は、位置の書き込みが引き戻された結果ではない。**
別の原因（ドラッグ終了の取りこぼし → 下の節）を先に見ること。

★ **`isFreePositioningEnabled` は attach 後なら実行時に立てられる。** attach 前は
**何もせずシリアライズ値も更新しない**ので「シーンに焼くしかない」ように見えるが、
掴み取りを待ってから代入すれば効く。**いまは立てていない** ——
引き戻しが観測されない以上、立てる根拠がない。

★ **掴み取った時点で、枠なし化で増えたぶんは既に乗っている。** `clientSize` が読めるように
なったときにはもう膨らんでいて、`Screen.*` の更新はそこから1フレームずれる。
**「意図した大きさ」をランタイムから復元することはできない** ——
起動時の権威は**自前の永続化（ポイント）**に持つしかない（→ `Desktop/WindowGeometry.cs`）。

#### 実測（2026-08-30 / macOS 26.6.2 / 外部 4K(1x) + 内蔵 Retina）

```
monitors=2 [0]=(0,0 3840,2130) [1]=(1041,-1111 1800,1072)
window=(1770,1598 300,480)pt client=300,480pt screen=300x480px cursor=2306,-23
画面外(freePositioning=false) へ 3780,2070 を入れます
→ window=(3780,2070 300,480)pt      ← そのまま読み戻る
```

- `(3540,1650)` を入れた窓が画面の**右上**（top-down で y=30..510）に着いた → 左下基準
- メインの作業領域の高さが **2130**（フルフレームは 2160）→ メニューバーぶんが除かれている
- カーソルが `y=-23`（モニタ0 の下端 `0` と モニタ1 の上端 `-39` の間）に居た → 隙間の実在
- 掴み取りの時点で `client=300,512pt` に対し `Screen` はまだ `300x480px`

**測り直すときは `-windowProbe`**（`Desktop/WindowProbe.cs`）。Unity / macOS /
UniWindowController のバージョンが上がったら、この節の値ごと取り直すこと。

### ★ ウィンドウは起動のたびに縦へ 32 伸びる（自前の永続化で消えた）

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

**いまは `Desktop/WindowGeometry.cs` が、意図した大きさを自分で持っているので育たない**
（#16）。上の 1〜4 のループは「Unity が復元した px の値」を出発点にしているが、
**出発点を自前の pt の永続化（`~/.config/chatter-agent/mascot/window.json`）に差し替えると
ループそのものが成立しなくなる。**

> ★ **以前は `Desktop/WindowSizeKeeper.cs` が「起動直後に見えていた大きさ」へ戻す
> 対症療法で打ち消していた。** [#66](https://github.com/schwarz9791/chatter-agent/issues/66) が
> 指摘していた2点（`_intended` を捕まえる Start 順序が未保証 /
> 補正が最初の1回で打ち切り）は、**権威を移したことで構造的に消えた**ので、
> keeper は削除した。**2人が `windowSize` を書く状態を作らないこと。**

★ **縮んだ側も追いかけない、という区別が要らなくなった。** keeper は「勝手に増えるぶんだけ」を
打ち消す必要があったが、いまは**ユーザーが変えた大きさがそのまま次回の意図**になる。

★ **書いた値が効いたかを見張るのはやめないこと**（既定5秒 / 最大5回）。枠なし化は
起動直後の数フレームで起きるが、VRM の読み込みでメインスレッドが詰まると実時間では
後ろへずれるし、**`Metal RecreateSurface` は起動ごとに2回出る**（＝膨らむ機会が2回ある）。

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

`Desktop/` に置く自前の常駐物（ドラッグハンドルの配線・ウィンドウの位置と大きさ・
ドラッグ状態のガード・カーソル追従）は `MonoBehaviour` をシーンに置かず、
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

★ **上のログは #56 当時のもので、いまは当てはまらない。** 起動時の大きさは
`Desktop/WindowGeometry.cs` が**ポイントで持った既定と自前の永続化**から決めるので、
枠なし化で増えたぶんは残らない（→「ウィンドウは起動のたびに縦へ 32 伸びる」）。
`Default Screen Width/Height` が効くのは窓を掴むまでの数フレームだけ。
「368 と入れて 400 になる」という回避策も**もう当てはまらない**。

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

★ **位置と大きさは自前でポイントで永続化している**（#16。→ 上の「ウィンドウの座標系」と
`Desktop/WindowGeometry.cs`）。Unity 本体も `Screenmanager *` を plist に焼き続けるが、
**そちらはもう権威ではない** —— あちらはバッキング px なので、ディスプレイのスケールを
またぐと物理的な大きさが変わる。

### ★★ ドラッグ終了を取りこぼすと、クリック透過が死んだまま残る

同梱のドラッグ用ハンドルは、**掴んでいる間だけウィンドウのヒットテストを切り、
離したときに戻す**。ヒットテストが切れている間は**クリック透過の再判定そのものが走らない**ので、
「離した」を受け取り損ねると**透過が二度と復活しない**。
上流にも、マルチモニタ間の移動で終了通知が正しく届かない、というコメントアウトされた懸念が残っている。

★ **これが [#16](https://github.com/schwarz9791/chatter-agent/issues/16) のコメント1
（画面外へドラッグするとクリック透過が効かなくなる）の第一容疑者。**
`Desktop/DragStateGuard.cs` が、**左ボタンが離れているのにヒットテストが切れたまま**
猶予（`StuckSeconds`）を超えたら復帰させ、警告を1本出す。

★ **この警告が出るかどうかが、そのまま切り分けになる。** 出れば上流の取りこぼし
（こちらが救っている）。**出ないのにクリック透過が効かないなら別の原因。**

★★ **ヒットテストを自分で書き戻さないこと。** それだと**ハンドルの側は掴んだままだと
思い込んだまま**になり、次に掴んだときヒットテストが切られない。すると掴んでいる最中に
透過の再判定が走り、透明な部分にカーソルが乗った瞬間に入力が下へ抜けて
**ドラッグが途中で外れる**（思い込みは残るので次も同じ）。
**ハンドルに「離した」を渡す**と、掴んでいる状態も戻すべきヒットテストの値も
上流の作法どおりに戻る。

★ **渡す引数を `null` にしないこと。** 受け手がいま引数を見ないのは**現在の実装の都合**で
あって契約ではない。参照され始めると**救済が例外で止まり、症状は「透過が死んだまま」なので
気づけない**。

★ **ハンドルの探索は非アクティブも含めること。** フォールバックの立方体は読み込み成功で
非アクティブになるので、**掴んでいる最中に非アクティブ化されたハンドルが残る**経路が実在する。

★ **渡したうえで検算すること。** 掴んだままのハンドルが1つも見つからないことは実際に起きる
（読み込み直しでモデルごと消えるなど）。そのときは**外から戻す以外に復帰手段が無い**うえ、
「何もしない」を選ぶと切れたままなので入口の早期 return に戻れず、
**警告が永久に繰り返される**。

★ **上流をフォークしないこと。** 外から状態を見て戻すだけなら、パッケージを上げても壊れない。
自前のドラッグ実装に置き換える方が高くつく（→ 上の節）。

> **もう一方の容疑者は実測で否定された。** 「macOS が窓を画面内へ引き戻している」という
> 見立ては外れ —— こちらから位置を書いたぶんは引き戻されない
> （→「ウィンドウの座標系は bottom-up・左下基準」）。

#### 実機で試した範囲（2026-08-30。**再現せず**）

ディスプレイまたぎ（8往復）/ 移った先の画面に収まる位置まで運ぶ / 掴んだまま修飾キーを
押しっぱなしにして離す —— **いずれも警告 0 件、クリック透過も正常。**
「狙って踏みに行ってもこの環境では起きない」までは言えるが、**コメント1 の症状が
何だったのかは未確定**。ガードは救済であると同時に、**次に出たときの切り分けの計器**として残す。

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

`SpeakingSet` はそこで空になる（`TryGetFace` が `false` を返す）ので、**猶予が無いと
メッセージの途中で毎文 Neutral に落ちる**。cc-mascot は hold を持たない（`onended` で即 neutral）ので実際にそうなっている。
`faceHoldSeconds`（既定 **1.5秒**）は「余韻」ではなく**この分断を埋めるためのもの**。短くしないこと。

★★ **猶予だけでは足りない。emotion をラッチしないと1行も効かない。**
`SpeakingSet.TryGetFace` は false のとき `kind = Assistant` / `emotion = Neutral` に**倒す契約**
（`SpeakingSetTests.TryGetFaceFallsBackToAssistantNeutral` が固定している）。だから
`VrmCharacter.Emotion` を素通しすると、**喋り終わった瞬間に目標が Neutral になり、
猶予の秒数をいくら伸ばしても顔は即座に戻る**。`FaceLatch`（`Speaking` が true の間だけ
更新する）が保つ値を渡すこと。

> ★ **この節は #57 の時点では `SpeakingView` について書かれていた。** #58 が
> `SpeakingSet` に置き換えて `SpeakingView` ごと消したので、名前を差し替えてある
> （同じ内容を書いている `FaceLatch.cs` と `SceneFixups.cs` は #58 の中で直っていたのに、
> **ここだけ取り残されていた** —— PR #74 のレビューの過程で気づいた）。

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

### ★★ `wantsToQuit` の継続からその場で呼ぶ `Application.Quit()` は無視される

**[#68](https://github.com/schwarz9791/chatter-agent/issues/68)（Dock からの「終了」を
2回選ぶ必要がある）の正体。** `wantsToQuit` で `false` を返して保留したあと、
非同期の後始末が終わってから**その継続の中で `Application.Quit()` を呼んでも、
macOS では何も起きない**。**フレームを1つ跨ぐだけで効く。**

だから後始末は「終了してよい」と印を立てるだけにして、**次のフレームの `Update` から呼ぶ**。
★ **`Update` の早期 return より手前に置くこと。** 後ろだと1回も走らない。

★ **1回目の終了要求は、OS から見ると「拒否された」ことになる**（Apple Event の返りが
キャンセル）。**保留する以上これは避けられない**ので、
「拒否されても、こちらから終了し直せる」ことが成立の条件になる。

★ **保留そのものを消して直さないこと。** 消すと終了時 ack が落ちて次回起動で二重発話する
（上の節）。手当ては**2つで1組**:

1. **投げ切るものが無ければ保留しない**（`ShutdownPolicy.ShouldDefer`）。
   実測では投げ切るものが無い経路も**毎回保留していた**ので、
   **普段の終了がこれで1回で終わるようになる**
2. **保留したときは `Update` から呼び直す**（上記）。ack を投げ切る経路はこちらでしか直らない

★ **保留経路は起動引数 `-quitProbe` でしか確かめられない。** 保留が起きるのは未 ack が
残っているごく短い窓だけで、**サーバーと実際の発話が無いと再現できない**。
#68 が長いあいだ未検証で残っていたのはこれが理由。**実機確認専用**なので既定では立てない
（`-faceLogMs` と同じ扱い）。ログには**実際の未 ack と、強制であることを別々に**出す ——
混ぜると、この経路を通る実行はすべてログが嘘になる。

★ **再試行（間隔と上限つき）は残してある。** いまは1回で通るが、
「効かなくなったこと」がログで分かる形にしておく。判断は `ShutdownPolicy` に切り出して
EditMode で固定した（終了経路は Editor の Play Mode では戻り値が無視されるので、
ここを `MonoBehaviour` に埋めると1行も固定できない）。

★ **「接続を閉じます / 閉じました」の詳細は保留経路でしか出ない。** 通常経路は投げ切るものが
無いので閉じる過程を残す意味が無い。**切り分けの入口（「終了要求」の行）は両方の経路で出る。**

#### 実測（2026-08-30 / macOS 26.6.2。`-quitProbe` で保留を強制し、終了要求を1回だけ送る）

| `Application.Quit()` を呼ぶ場所 | 結果 |
|---|---|
| **継続からその場で**（旧） | ログは出るが、**15秒待っても終了しない。** `wantsToQuit` の2周目すら来ない |
| **`Update` の先頭から**（新） | 1回目の呼び出しだけで**約1秒で終了** |

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

### ★ Unity 単体では常駐アプリにならない（#75）

メニューバー常駐に要る3つのうち、**Unity で書けるものが1つも無い。**

| やりたいこと | Unity だけでできるか |
|---|---|
| メニューバーのアイコンとメニュー（`NSStatusItem` / `NSMenu`） | **できない。** UniWindowController にもステータスバーの API は無い |
| フォーカスが無い状態でのキー入力（グローバルショートカット） | **できない。** Input System はフォーカスを持つときしかキーを受け取らない。常駐マスコットは基本フォーカスを持たない |
| Dock に出さない（`LSUIElement`） | ビルド後処理で `Info.plist` を書けばできる |

→ **Objective-C のネイティブプラグインを自作する**（`Assets/Plugins/macOS~/ChatterMascotNative/`）。
設定パネル（[#76](https://github.com/schwarz9791/chatter-agent/issues/76)）もこの土台に乗る。

★ **cc-mascot から流用できるコードは1行も無い。** あちらは Electron の `Tray` と
`app.dock.hide()` で済んでおり、ObjC のコードが存在しない。**流用したのはアイコン画像だけ**
（`trayTemplate.png` / `@2x`。→ [`origin.md`](./origin.md)）。

★ **ただし知見は1つ効いた。** cc-mascot は `app.dock.hide()` を `ready-to-show` から
**500ms 遅らせている**（コメント: 「起動時に呼ぶとフルスクリーン Space で起動してしまうため」）。
`LSUIElement` は起動前から accessory なので同じ罠は踏まない見込みだが、
保険の `CM_SetActivationPolicy` も**起動から1秒待ってから**呼んでいる。

### ★★ `-fvisibility=hidden` で作ったバンドルは、シンボルが1つも見えない

`clang -bundle` に `-fvisibility=hidden` を付けると、**`CM_` で始まる関数が
`nm -gU` に1つも出なくなる**（実測）。C の関数は既定で external だが、
このフラグは `__attribute__((visibility("default")))` が無いものを全部隠す。

**症状は `DllNotFoundException` ではなく `EntryPointNotFoundException`。**
バンドル自体は読めているので「プラグインが無い」とは言われず、
呼んだ関数だけが見つからない。

```c
#define CM_EXPORT __attribute__((visibility("default")))
CM_EXPORT bool CM_Initialize(void);
```

★ **フラグを外すのではなく、公開するものに印を付けること。** 外すと ObjC のクラスや
内部ヘルパまで全部エクスポートされ、**何が ABI なのかがソースから読めなくなる**。

確かめ方:

```bash
nm -gU Assets/Plugins/macOS/ChatterMascotNative.bundle/Contents/MacOS/ChatterMascotNative | grep _CM_
```

### ★★ `PluginImporter` の設定は、バッチモードの初回インポートでは書かれない

`.bundle` を置いて `scripts/test.sh` を回すと `.meta` は作られるが、中身が**GUID だけ**になる:

```yaml
fileFormatVersion: 2
guid: a692ce6a5257a459fb5b8910fa38355f
```

`PluginImporter` のブロックが無い＝**プラットフォームの絞り込みが記録されていない**。
このままだと Unity が既定（すべてのプラットフォーム）でインポートし直すことがあり、
**macOS のバンドルが Android ビルドに混ざる**（→ [#25](https://github.com/schwarz9791/chatter-agent/issues/25)）。

★ **Inspector で直さないこと。** `.bundle` は git に入っていない（バイナリはレビューできず、
`plugin/bin/*.mjs` のように CI でソースとの一致を検証できない）ので、
**新規クローンには `.meta` しか無い**。手で直すと「誰かのマシンでだけ通る」状態になる。
直し方はコードに置いてある:

```bash
./scripts/run.sh ChatterMascot.EditorTools.NativePluginSettings.FixAll
```

正しく入ると `.meta` はこうなる（`Any: enabled: 0` が要点）:

```yaml
  platformData:
    Any:
      enabled: 0
    Editor:
      enabled: 1
      settings: { CPU: AnyCPU, OS: OSX }
    OSXUniversal:
      enabled: 1
```

### ★★ `Info.plist` を `System.Xml.Linq` で書き換えると壊れる

plist は普通の XML に見えるので `XDocument` で読んで保存したくなるが、**2箇所壊れた**（実測）:

1. **DOCTYPE の末尾に `[]` が付く。** `XDocumentType.InternalSubset` が
   `null` ではなく空文字列になり、`Save` がそれを内部サブセットとして出力する。
   結果、`PlistBuddy` が
   `Encountered unexpected character [ on line 2 while parsing DTD` で読めなくなる
2. **UTF-8 BOM が付く**（`XDocument.Save(path)` の既定）

```xml
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd"[]>
```

どちらも XML としては直せるが、**直し続ける理由が無い** —— plist を壊さずに書き換える道具が
OS に入っている。`MacPostBuild` は `/usr/libexec/PlistBuddy` を呼ぶ:

```
Set :LSUIElement true    # 既にあれば成功
Add :LSUIElement bool true    # 無ければこちら
```

★ **`UnityEditor.iOS.Xcode.PlistDocument` も使わないこと。** iOS Build Support に入っているので、
モジュールを入れていない環境では**コンパイルすら通らない**。

★ **ビルド後処理が失敗してもビルドを落とさないこと。** ここが転んで失うのは
「Dock にアイコンが出ない」だけで、マスコットは動く。例外を投げると
`BuildPipeline` がビルドごと失敗させる。

### ★ リバース P/Invoke（ネイティブ → C#）で踏むところ

| # | |
|---|---|
| 1 | **`static` メソッド + `[AOT.MonoPInvokeCallback]`。** インスタンスメソッドやクロージャを渡さない。Mono では動くが IL2CPP で落ちる |
| 2 | **デリゲートを `static readonly` フィールドで保持する。** `CM_SetEventCallback(OnNativeEvent)` と直接書くと暗黙に作られたデリゲートが GC され、症状は**「しばらく使っていると SIGSEGV」**になる |
| 3 | ★ **コールバックの中から Unity API を呼ばない。** AppKit の menu action と Carbon の hotkey handler は**Unity のプレイヤーループの外**（メニュー追跡中のネストした run loop）で発火しうる。`ConcurrentQueue` に積み、`Update()` で drain する（`AfplaySpeechPlayer` が `Process.Exited` を使わないのと同じ判断） |
| 4 | **managed 例外をネイティブのスタックへ抜かない。** `try/catch` で握る |
| 5 | **`CM_SetEventCallback(NULL)` → `CM_Shutdown()` の順**で終了処理する。逆だと、終了中の menu action がもう生きていない Mono ドメインを叩く |
| 6 | **可用性の判定は初回1回だけ。** 「無い」をフラグで覚え、以降は try/catch を回さない（イベントの drain は毎フレーム走る） |
| 7 | **`CM_Version()` の戻り値を `string` で受けない。** P/Invoke の戻り値 `string` はマーシャラが**受け取った領域を解放しようとする**。`IntPtr` で受けて `Marshal.PtrToStringAnsi` |

★ **コールバックを1本に統一してある。** 理由が2つ: (1) 上の 2 を守る対象が増えない、
(2)「main thread かどうか」の保証を ObjC 側1箇所に書ける。

★ **ObjC 側にメニューのキーもラベルも書かないこと。** C# が JSON でメニューを渡し、
ネイティブは `representedObject` に載せた key を返すだけ。項目の追加・並び替え・ラベルの変更が
C# だけの変更で済むのが、この作りを選んだ理由そのもの（#76 の設定パネルが同じ形で乗る）。

### ★ `NSStatusItemBehaviorTerminationOnRemoval` を立てない

「⌘ドラッグでアイコンを外したらアプリを終了する」挙動。一見親切だが、
**Dock に居ない常駐アプリでは「黙って消えた」にしか見えない**。戻す手段も無い。

### ★★ 一時ミュートは「声だけ消す」—— 音量では実装できない

**音量を変える手段が無い。** macOS の再生の実体は引数なしの `afplay`（音量を渡す口が無い）で、
`Disable Unity Audio` が ON なので `AudioListener.volume` も効かない。
**再生そのものを飛ばす**のが唯一の手段になる。

`MutedSpeechPlayer`（`ISpeechPlayer` のデコレータ）が3つを同時に満たす:

| | |
|---|---|
| **ack は必ず出す** | `PlayAsync` が成功を返せば `Played` → `Finish` → `ConsumeHead` → `EmitAck` の**通常経路そのまま**が走る。★ 止めるとキューが `speechQueueMaxEntries`（500）まで溜まって古い方から捨てられ、解除後に**歯抜けで喋り出す**（→ [`protocol.md`](./protocol.md) の責務2・3）。**`PlaybackQueue` には1行も触らない** |
| **長さぶん待つ** | ★ 即座に返すと、溜まっていた発話が数百 ms で全部消化されて**表情が高速で切り替わる**。「声だけ消す」は実時間を消費して初めて成立する |
| **口だけ止める** | `MascotRunner.BeginSpeaking` が `_mute.Muted` のときエンベロープを落とす。★ **`_speaking` への登録そのものは飛ばさない** —— 飛ばすと表情も体の動きも止まり、「声を消した」ではなく「居なくなった」に見える（→ `SpeakingSet.Begin` の doc） |

★ **`ActiveCount` に「無音で待っている本数」を足すこと。** 足さないとミュート中は
本物の `ActiveCount` が常に 0 になり、`AudioIdleGate` が「鳴っていない」と判定して
出力デバイスを手放す。macOS は `CanSuspendOutput == false` なので無害だが、
**Android では実際に手放してしまう**（→ #25）。

★ **`Prepare` は本物に委譲すること。** WAV の検証もエンベロープ生成も走るので、
ミュートの有無で**ログの見え方が変わらない**。無音の原因を切り分けるとき、
ミュートかどうかで診断の出方が変わるのはいちばん困る。

★ **ミュートにした瞬間、鳴っているものを止める。** 押す動機は「いま喋っているのを黙らせたい」
なので、次の発話から効くのでは遅い。**そのとき返る失敗は成功に倒す**（止めたのは自分なので、
押した本人に向かって警告を出さない）。

★ **合成は止まらない。** ミュート中も `GET /audio/…` は走る。止めるには `PlaybackQueue` に
触る必要があり、上の規律と引き換えになるのでしない。

### ★ `LSUIElement` の代償

| # | 代償 | 手当て |
|---|---|---|
| 1 | **Dock から終了できない** → [#68](https://github.com/schwarz9791/chatter-agent/issues/68) の再現手段が消える | **#16 で #68 を閉じてから着手した。** 保留経路の確認は `-quitProbe` が唯一の手段になる |
| 2 | **⌘Q が効かない**（メニューバーが無い） | ステータスバーのメニューに「終了」を置く。★ **ネイティブから `[NSApp terminate:]` を呼ばないこと** —— `Application.Quit()` なら #68 で直した経路（`wantsToQuit` で ack を投げ切ってから `Update` の先頭で呼び直す）にそのまま乗る。`applicationShouldTerminate:` を握る必要が無いので `CM_ReplyToTerminate` も要らない |
| 3 | **アプリがアクティブになれない** | 何かウィンドウを出すときは `[NSApp activateIgnoringOtherApps:YES]`（本番になるのは #76） |
| 4 | ★ **`forceSingleInstance` が防げない二重起動が見えなくなる** | (a) 起動時に `[Mascot] pid=… bundlePath=…` (b) ★ **ステータスバーのツールチップに pid を入れる** —— アイコンが2つ並ぶので目で分かる。これが実際にいちばん効く |

### ★ 「キャラクターを隠す」は窓ではなくカメラを止める

`UniWindowController` は窓の生成と透過の面倒を見ているので、**窓そのものを消しに行くと
透過とクリック透過の設定ごと崩れる**。`cullingMask` を 0 にすれば
カメラは透明でクリアし続け、ヒットテストの raycast も当たらなくなる
（＝クリック透過も自然に成立する）。

★ **この状態を永続化しないこと。** 隠れたまま次を起動すると「マスコットが出ない」に化ける。
ミュートはアイコンが薄くなるので気づけるが、隠れているものは気づきようが無い。
（`settings.json` に入るのは `audio.mute` / `audio.muteHotKey` / `ui.hideHotKey` の3つ。
ショートカットは**設定**なので永続化するが、**隠している状態は永続化しない**。）

### ★★ ObjC の `NSLog` は Unity の `Player.log` に入らない

ネイティブプラグインの中で `NSLog` を呼んでも、**ビルドした `.app` ではどこにも残らない**（実測）。
`Player.log` に入らないのはもちろん、`log show --predicate 'process == "Chatter Mascot"'` にも出ず、
`.app` の実行ファイルを直接叩いて stderr をリダイレクトしても出なかった。

つまり **「メニューバーに出ない」ときに手掛かりが1つも残らない**。
`CMEmitLog` を足して、診断も**イベントと同じ1本のコールバック**で C# へ返している:

```objc
CMEmitLog("ステータスバー: item=あり button=あり image=あり visible=1");
```
```csharp
case MenuEventKind.Log:
    Debug.Log("[Native] " + value.Message);   // ここで初めて Player.log に載る
```

★ **`CMEmit` の失敗そのものを `CMEmitLog` で報告しないこと**（同じ経路なので無限に回る）。

★ **コールバックが付く前の診断は捨てられる。** `CM_SetEventCallback` より前に起きたことは残らないので、
初期化の順序を変えるときは診断の見え方も一緒に動く。

### ★ `NSStatusItem` を作った直後に frame を測っても意味が無い

レイアウトは**次の run loop** で走るので、`CM_StatusItemShow` の中で
`gStatusItem.button.window.frame` を読むと **必ず `0,0 38x0`（高さ 0）** が返る。
「高さ 0 だから表示されていない」と読むと、丸1本ぶん誤った方向へ進む。

2秒後に測り直すと `frame=3103,2130 38x30` で正しく載っていた（AppKit の座標系は
**bottom-up**。y=2130 は 2160 の画面の上端＝メニューバー）。

★ **アクセシビリティからは見えない。** `System Events` で
`menu bar 2` も `AXExtrasMenuBar` も取れない（`missing value`）。
一方 **`click at {x, y}` は効く** ので、実機確認でメニューを開くならこれを使う:

```bash
osascript -e 'tell application "System Events" to click at {3196, 15}'
```

★ **`orca computer click` は使えない。** `LSUIElement` のアプリは
「フォーカスされた最前面のウィンドウ」を持てないので `window_not_focused` で拒否される。

### #75 の実機確認（macOS 26.6.2 / `.app`）

| 確認したこと | 結果 |
|---|---|
| Dock に出ない | `lsappinfo` が **`type="UIElement"`**（同じアプリの旧ビルドは `type="Foreground"`） |
| メニューバーのアイコン | 出る。テンプレート画像なので背景に応じて白/黒が入れ替わる |
| **二重起動** | ★ **アイコンが並ぶ。** 3つ動かしたら3つ並んだ —— pid をツールチップに入れた狙いどおり、目で分かる |
| メニューの中身 | ミュート（⌥M）/ キャラクターを隠す / 設定を開く… / ── / Chatter Mascot 0.1.0（**灰色**）/ 終了 |
| 状態の反映 | ミュートに ✓ が付き、ラベルが「キャラクターを**表示する**」に変わる |
| **⌥M（他アプリにフォーカスがある状態）** | ★ 効く。**アクセシビリティ権限のダイアログは出ない**（Carbon を選んだ理由） |
| ミュート中のアイコン | **薄くなる**（`appearsDisabled`） |
| 設定を開く | TextEdit が開く（#76 までの繋ぎ） |
| **メニューの「終了」** | ★ **1回で終わる。** `試行 2` も `LogError` も出ない（#68 の手当てが `LSUIElement` でも効いている） |
| **ミュート中の発話** | ★ `afplay` は**起動せず**、サーバー側は `seq<=1 を 1 件消しました`（**ack は出ている**）。キューは空のまま |
| 解除後の発話 | `afplay の実時間 1454ms / WAV の長さ 1140ms`（鳴る） |
| **バンドルを消した `.app`** | ★ 起動する。`[Native] ChatterMascotNative.bundle が見つかりません…` の**警告1本**だけで、VRM も読み込まれ、マスコットは動く |

★ **`.app` の中の `.bundle` を手で差し替えないこと。** コード署名が壊れて
`open` から起動できなくなる（`Player.log` が空のまま終了する）。
ネイティブだけ直したときも `./scripts/build.sh` を通すこと（2回目以降は5秒で終わる）。

★ **`open` は同じ bundle id のアプリが動いていると新しいプロセスを起こさない。**
別ワークツリーのビルドと並べて試すときは `open -n` を使う。

### ★ グローバルショートカットは Carbon で登録する

`RegisterEventHotKey`（HIToolbox）は **アクセシビリティ権限のダイアログが出ない**。
`NSEvent.addGlobalMonitorForEvents` は出る —— 常駐マスコットのために
「入力の監視」を許可させるのは要求として重すぎる。

★ **`RegisterEventHotKey` は非推奨ではない**（SDK 26.5 で確認）。宣言に付いているのは
`AVAILABLE_MAC_OS_X_VERSION_10_0_AND_LATER` だけで、deprecation 指定が無い。
同じ `CarbonEvents.h` の中には `DEPRECATED_IN_MAC_OS_X_VERSION_…` が **69 箇所**ある
（`RetainMouseTrackingRegion` など）ので、Apple は畳むべきものには印を付けていて、
**ホットキー登録には付けていない**。`-Wdeprecated-declarations` を明示してビルドしても警告ゼロ。

「Carbon は非推奨」という一般論が指すのは主に GUI 部分（HIView / HIWindow / QuickDraw）で、
64bit 移行時に消えた。ホットキー登録が残っているのは**代替が無い**ため。

★ **修飾キー無しを拒否すること。** 単独のキーを登録すると、そのキーが
**どのアプリでも入力できなくなる**。`HotKeySpec` とネイティブ側の両方で弾いている。

### ★★ 既定のショートカットに `⌥` 単体と `⌘⌥` を選ばない

**`RegisterEventHotKey` は「他アプリの排他登録」しか見ない。** 実測では
`⌥M` / `⌥H` / `⌘⌥M` / `⌘⌥H` / `⌃⌥M` / `⌃⌥H` の**6候補すべてが登録に成功する**。
実害はその先にある。

**1. `⌥` 単体は文字を入力する。** `UCKeyTranslate` に現在のレイアウト（ABC）で聞くと:

| | 入力される文字 |
|---|---|
| `⌥M` | **`µ`**（U+00B5） |
| `⌥H` | **`˙`**（U+02D9） |

ホットキーとして登録すれば横取りできる（実際に `⌥M` でミュートが切り替わった）が、
**マスコットが起動している間、全アプリでその文字が打てなくなる**。

**2. `⌘⌥` は macOS 標準と衝突する。** Finder のメニューから
`AXMenuItemCmdChar` / `AXMenuItemCmdModifiers` を引くと:

| メニュー項目 | |
|---|---|
| ほかを非表示 | **`⌘⌥H`** |
| すべてをしまう | **`⌘⌥M`** |

「ほかを非表示」はアプリメニューにあるので**全アプリ共通**。奪うと他のアプリの標準機能が効かなくなる。

**3. `⌃⌥` はどちらでもない。** 文字入力に使われず（`⌃⌥M` は U+000D の制御文字）、
標準ショートカットの割り当ても無い。**既定はこれ**（`ctrl+opt+m` / `ctrl+opt+h`）。

★ **ユーザーが設定で `⌥M` を選ぶぶんには止めない。** 既定として押しつけないだけ。

### ★★ アイコンが出ないのに `visible=1` なら、メニューバー管理ツールが隠している

`CM_StatusItemShow` が成功し（`item=あり button=あり image=あり visible=1`）、
メニューもショートカットも動いているのに、**アイコンが画面に出ないこと**がある。
`button.window.frame` を測ると:

```
frame=-2287,2130 38x30 onScreen=1 screen={{0, 0}, {0, 0}}
```

**x が負**（画面の左外）で、`screen` も取れない。

★ **これは Thaw / Bartender / Ice のようなメニューバー管理ツールの仕業。**
あの種のツールは、隠したいアイコンを**画面外の負の座標へ移動させる**ことで隠す。
`y=2130`（メニューバーの高さ）が正しいまま x だけが飛んでいるのが目印。

★ **macOS の「混雑して入り切らない」ではない。** 最初そう誤診したが、
実際には**ツールが意図どおり隠していた**だけだった。同じビルドで
「出るときと出ないとき」があったのも、ツールが新しいアイコンを検出して
隠す側へ入れたタイミングの差で説明がつく。

★ **アプリ側から押し戻す手段は無い**（`autosaveName` を付けても変わらない。実測）。
ユーザーがツール側で表示に切り替えるしかない。

★ **切り分けはログでできる。** `[Native] ステータスバー: …` が
`item=あり button=あり image=あり visible=1` なら**こちらは仕事を終えている**。
そこから先はメニューバー管理ツールの管轄。

★ `autosaveName` は付けてある。目的は**ユーザーが ⌘ドラッグで並べ替えた位置を覚えること**で、
この問題の手当てではない。

### ★★ 縦の `NSStackView` は「左揃えにするだけ」で、幅は内容依存になる

設定パネル（#76）で最初に踏んだ。`alignment = NSLayoutAttributeLeading` の縦スタックに
行を積むと、**note を持つ行だけスライダーが縮む**（note の無い行と幅が揃わない）。
note がある行は「行 + 注記」を縦スタックに包んでいるので、その包みの幅が内容依存になるため。

```objc
for (NSView *row in rows) {
    [row.widthAnchor constraintEqualToAnchor:group.widthAnchor].active = YES;
}
```

★ **一番外側のスタックでも同じことをすること**（`edgeInsets` のぶんを引く）。
★ **症状が「一部の行だけ狭い」なので、レイアウトの崩れではなく仕様に見えてしまう。**

### ★★ `setFrameAutosaveName:` だけではウィンドウ位置が復元されない

保存はされるが、読み戻すのは `setFrameUsingName:`。付け忘れると、毎回
`initWithContentRect:` の矩形に出る —— **AppKit は bottom-up なので画面の左下**。
症状は「動かしたのに次に開くと左下へ戻る」。

```objc
gPanel.frameAutosaveName = @"ChatterMascotSettingsPanel";
if (![gPanel setFrameUsingName:gPanel.frameAutosaveName]) [gPanel center];
```

### ★ パネルを出したら frame をログに残す

「開かない」には (a) `Show` が失敗した (b) 画面外に出た (c) **他のウィンドウの背後に居る**
の3通りがあり、手当てが全部違う。実際に (c) を踏んだ —— `CM_SettingsPanelIsVisible()` は
`1` を返すのに画面のどこにも見えず、原因は「アプリが非アクティブなので前に出ていない」だった。

```
[Native] 設定パネル: frame=1041,-1111 606x664 visible=1 key=0 screen={{1041, -1169}, {1800, 1169}}
```

★ **`NSStatusItem` と違い、frame はその場で測ってよい**（`makeKeyAndOrderFront:` の後なので
確定している）。あちらはレイアウトが次の run loop に回るので必ず高さ 0 が返る。

★ **`key=0` を「キーウィンドウを取れていない」と早合点しないこと。** 実測では
`makeKeyAndOrderFront:` の直後は `0` だが、その後キーになる ——
ショートカットの記録（キー入力を要求する）は**実際に動いた**。

### ★★ `LSUIElement` でもショートカットの記録はできる（ローカルモニタ）

`NSEvent.addLocalMonitorForEventsMatchingMask:` は**自分のアプリに配送されるイベント**しか
見ないので、**アクセシビリティ権限が要らない**（グローバルショートカットの登録に Carbon を
選んだのと同じ理由。`addGlobalMonitorForEvents` は権限のダイアログを出す）。

要るのは `[NSApp activateIgnoringOtherApps:YES]` だけ。#75 の「`LSUIElement` の代償 3」で
「本番になるのは #76」と書いた手当てが、ここで実際に効いた。

★ **ハンドラで `nil` を返して飲み込むこと。** 返さないと Unity 側にもキーが届く。
★ **修飾キー無しの `esc` を「中止」にすること。** 抜ける手段が無いと、パネルのどこを押しても
記録が続く。★ **修飾キー付きの `esc` は記録する**（`⌃⌥esc` は正当なショートカット）。

★ **ネイティブが返すのは数値だけ。** `NSEvent` の `keyCode` と Carbon の修飾マスクを
`"46,6144"` の形で返し、`ctrl+opt+m` という語彙への変換は C# の `HotKeySpec.TryFromCode` が行う
（#75 で決めた「ネイティブに設定の語彙を書かない」をショートカットにも通す）。
Cocoa の `NSEventModifierFlag*` と Carbon の `cmdKey` 等は**ビットが違う**ので、
`RegisterEventHotKey` が要求する Carbon の形に**ネイティブ側で**直してから渡す。

### ★★ 常駐マスコットの右クリックは `IPointerClickHandler` では成立しない（#76）

**症状**: キャラを右クリックしても設定パネルが開かない。左クリックを1回挟むと開くようになる
——「たまに効く」といういちばん悪い壊れ方をする。

**原因は2段ある。**

**① Input System の `Mouse` デバイスが、非アクティブの間は無効化される。**
`InputSettings.backgroundBehavior` の既定は `ResetAndDisableNonBackgroundDevices` で、
フォーカスを失うと `Mouse`（`canRunInBackground == false`）が
`TemporaryWhilePlayerIsInBackground` で無効になる。`runInBackground: 1` は
イベントストリームの手前の関門しか通さず、ここは塞げない。

```csharp
// StatusItemBridge.AllowInputWithoutFocus()
InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
```

★ **`Assets/` に `InputSettings` アセットを置くより実行時に立てる方がよい。** アセットは
`EditorBuildSettings` の `com.unity.input.settings` に登録が要り、**Android（#25）にも効いてしまう**。
常駐マスコットの都合なので `Desktop` asmdef に閉じる。

**② それでも `OnPointerClick` は呼ばれない。座標が古いから。**
macOS が `mouseMoved` を配送するのは**前面のアプリだけ**なので、
`Mouse.current.position` は**最後にフォーカスがあったときの座標で止まる**。
右ボタンのイベント自体は届いているのに、UI のレイキャストが**別の場所**を撃つ。

```
[DIAG] click button=Right ...   ← 窓は 1770,1680 に居るのに
[DIAG] mouse enabled=True added=True pos=(588.00, 156.00)
```

同じことがパッケージ側にも書いてある（`UniWindowController.GetClientCursorPosition`）:

> New Input System ではフォーカスが無い場合にマウス座標が取得できないため独自に計算する

**採った形**: **押下はイベント、位置はクリック透過の状態**（`ContextClickHandles.cs`）。

```csharp
var pressed = (mouse != null && mouse.rightButton.wasPressedThisFrame) || (downNow && !_wasDown);
...
if (_controller.isClickThrough) return;   // 不透明な画素の上＝キャラクターの上
StatusItemBridge.ToggleSettings();
```

★★ **押下をポーリングだけで取らないこと。** `UniWindowController.GetMouseButtons()`
（`NSEvent.pressedMouseButtons`）は毎フレーム覗くだけなので、30fps では
**1フレーム（33ms）より短い押下が丸ごと消える**。トラックパッドの2本指タップはまさにそれで、
実測でも合成した 60ms の右クリックを **1/4 しか拾えなかった**。イベント側を主にして、
取りこぼしに備えてポーリングも併せて見る（同じ押下で2回開閉しないよう押しっぱなし扱いに畳む）。

★ **当たり判定は自分で作らない。** `isClickThrough` は**グローバルなカーソル座標**から
毎フレーム計算されていて（`ReadPixels` によるα判定）フォーカスに依存しない。
これを使えば**掴める領域と右クリックできる領域が定義上ずれない**。
コライダーに `IPointerClickHandler` を配る旧実装は、granularity が
コライダー任せになるうえ、VRM を差し替えるたびに付け直しが要った。

★ **ドラッグとは衝突しない。** 同梱の `UniWindowMoveHandle` は `OnBeginDrag` の先頭で
`if (eventData.button != PointerEventData.InputButton.Left) return;` と早期 return する。

★ **既知の穴。** 設定パネルがキャラクターに重なっていると、**パネルの上での右クリックでも閉じる**
（マスコット側からは「不透明な画素の上で右ボタンが押された」と区別がつかない）。
パネルには右クリックで何かが出る部品が無いので実害は「閉じる」だけ。潰すには
ネイティブ側にパネルの矩形を問い合わせる口が要るので、割に合わないと判断した。

### ★★ `⌃ + 左クリック`は常駐マスコットでは成立しない（#76）

macOS の慣習だが、**二重に成立しない**（実測）:

1. **修飾キーが読めない。** キーイベントは前面のアプリにしか配送されないので、
   押していても `Keyboard.current.leftCtrlKey.isPressed` は `false` のまま。
   `backgroundBehavior = IgnoreFocus` はデバイスを生かすだけで、**OS の配送先は変えられない**
2. **非アクティブなアプリへの最初の左クリックはアクティブ化に食われる。**
   右クリックはアクティブ化しないのでそのまま届く

つまり「2回クリックが要り、しかも修飾キーが効かない」ものになる。**押しても何も起きない操作を
残さない**（→ `SettingsSchema` の同じ方針）。副ボタンを出せない環境の逃げ道は
**メニューバーの「設定を開く…」**で足りている。

★ `UniWindowController.GetModifierKeys()`（`NSEvent.modifierFlags` 由来）なら 1 は回避できる。
それでも 2 が残るので採らなかった。

### ★★ `HideFlags.HideAndDontSave` のオブジェクトは `FindFirstObjectByType` から見えない

**症状**: 「キャラクターの位置をリセット」がその場で効かず、アプリを再起動して初めて反映される。

`WindowGeometry` の `Keeper` は `HideFlags.HideAndDontSave` を持つ GameObject に載っている。
Unity のドキュメントに明記されているとおり、`Object.FindFirstObjectByType` は
**`HideFlags.DontSave` を持つオブジェクトを返さない**。`WindowGeometry.Reset()` は常に
「見つからない」枝に落ち、`window.json` を消すだけで終わっていた（ログにも
`ウィンドウの管理が動いていないので、位置のリセットは次の起動から効きます` が出ていた）。

**手当て**: `StatusItemBridge` と同じ形に揃えて **static フィールドで保持**する
（`Start` で代入、`OnDestroy` で解除）。`FindFirstObjectByType` を使わない。

### ★ メニューバーの項目はアクセシビリティから触れる（#75 の記述を訂正）

`LSUIElement` のアプリでも、ステータス項目は **`menu bar 2`**（`menu bar 1` はアプリの
メインメニュー）から見えるし、**名前でクリックできる**。#76 で実測:

```bash
osascript -e 'tell application "System Events" to tell process "Chatter Mascot" \
  to return name of every menu item of menu 1 of menu bar item 1 of menu bar 2'
# → ミュート（⌃⌥J）, キャラクターを隠す（⌃⌥H）, 設定を開く…, missing value, Chatter Mascot について, 終了

osascript -e 'tell application "System Events" to tell process "Chatter Mascot" \
  to click menu item "Chatter Mascot について" of menu 1 of menu bar item 1 of menu bar 2'
```

★ **ターミナルにアクセシビリティ権限が要る。** 権限が無いと項目そのものが見えないので、
#75 の「見えない」はおそらくそれ。**権限の有無で結論が変わる測定**だと分かるように書くこと。

★ `-settingsProbe` は残してある（`-quitProbe` / `-windowProbe` と同じ位置づけ）。
権限を与えたくない環境や、メニューバー管理ツールが項目を隠している場合に効く。

### ★ 画面のスクリーンショットはディスプレイごとに倍率が違う

`screencapture -R x,y,w,h` の矩形は**ポイント**だが、返る画像は**そのディスプレイの
バックingスケール**になる。Retina のサブディスプレイでは画像が2倍で返るので、
**画像上で測った座標をそのままクリックに使うと外す**（#76 の自動確認で踏んだ）。
画像の幅を矩形の幅で割って倍率を出してから換算すること。

### ★ VRM の差し替えは次の起動から

`VrmStage` は起動時に1回だけ読む作りで、差し替えるには spring bone・コライダ・
ドラッグハンドル・待機モーション・表情の結び直しが要る。中途半端に作ると
「差し替えたのに一部だけ前のモデルのまま」になるので、**できないことをできると見せない**方を
採った（note に「次に起動したときから反映されます」と出す）。

★ **選んだファイルは `~/.config/chatter-agent/models/` へコピーする。** パスを覚えるだけだと、
元ファイルを消したときに候補が死ぬ。

### ★★ `models/` には固定名で上書きする —— 選んだ名前をそのまま使わない

**実機で2つ踏んだ。**

1. **選ぶたびにファイルが積み上がる。** 元の名前でコピーしていたので、選び直すと
   前のファイルが残り、**消す責任が誰にも無くなる**
2. **選んだモデルが読まれない。** `AssetPath` には
   「settings.json のファイル名を名指しで先に出す」段があったが、
   **本番コードが誰も `AssetEnv.SelectedVrmFileName` に渡していなかった**
   （テストだけが設定していたので、テストは通り続けた）。候補が一度も出ず、
   `models/*.vrm` の走査（`Ordinal` の先頭が勝つ）が常に勝っていた

**手当て**: 置き場所を `models/mascot.vrm`（`AssetPath.SelectedVrmFile`）に固定した。
`File.Copy(overwrite: true)` が前の1本を必ず置き換えるので積み上がらず、
探索側も**設定を読まない**ので「設定は覚えているのに誰も見ていない」が起こらない。

★ **元の名前は表示のためだけに覚える**（`settings.json` の `character.vrm`）。
画面には `AvatarSample_A2.vrm` と出るが、ディスク上は `mascot.vrm` の1本だけ。

★ **候補は走査結果から引き上げること。** 無条件に足すと、パネルで一度も選んでいない人の
起動ログに毎回「読めませんでした」が1本増える。

★ **`models/` に手で置いたファイルは消さない。** 上書きするのは固定名の1本だけ。
まとめて消すのは「すべての設定をリセット」だけで、そちらは確認ダイアログで予告している。
——ただし、**ユーザーが自分で `models/mascot.vrm` という名前で置いた1本だけは例外**で、
次にパネルからモデルを選んだ時点で置き換わる。それらしい名前を選んだ代償として受け入れている
（`__chatter__.vrm` のような衝突しない名前も考えたが、`models/` を覗いた人に
「これは何だ」と思わせる方が高くつく）。

### ★★ `.vrm` はシステムに UTI が無いので、`allowedContentTypes` では絞れない

**症状**: 「VRM モデルを選ぶ…」でダイアログは開くが、**`.vrm` がグレーアウトして選べない**。

`LibUniWinC`（`FilePanel.OpenFilePanel`）は `NSOpenPanel.allowedContentTypes` に
`UTType(tag: "vrm", tagClass: .filenameExtension, conformingTo: nil)` を渡す。
`.vrm` はシステムに登録された UTI を持たない（実測: `kMDItemContentType = "dyn.ah62d4rv4ge81q6xr"`）ため、
返るのは **dynamic UTType** で、パネルの有効判定に一致しない。
`setAllowedFileTypes:` はそもそも呼ばれていない（バイナリにセレクタが無い）。

**手当て**: ネイティブプラグインに自前の `NSOpenPanel` を足した（`CM_OpenFilePanel`）。

★★ **`allowedContentTypes` で絞らないこと。** 代わりに `NSOpenPanelDelegate` の
`panel:shouldEnableURL:` で「ディレクトリ、または拡張子が一致するファイル」だけを有効にする。
**UTI の登録状況に依存しない**のが要点。

★ **拡張子もタイトルも C# から渡す**（`CM_OpenFilePanel(const char* optionsJson)`）。
ネイティブに `"vrm"` を書かない。

★ **`runModal` は Unity のメインスレッドを止める。** WebSocket の watchdog が
「何も届かない」と判断して繋ぎ直すが、それは**正常な復帰**であって異常ではない
（実測でも `切断されました … 繋ぎ直します` → `接続しました` が出る）。

### ★★ 自分起点の変更でパネルを作り直さない

★★ **`PATCH /v1/config` の応答でも作り直さないこと。** 成功しただけで `Push` すると、
**スライダーを離した 300ms 後（＋往復）に再構築が着地する**。続けてもう一度掴んだ人の
手の中でつまみが死ぬので、「**話す速さだけドラッグできない**」という形で出る
（音量と大きさは Unity 側で完結するので同じ経路を通らない）。作り直すのは
**core が画面と違う値を返したとき**だけ ——丸め直し・env での固定・繋がるようになった、のどれか
（`CoreSnapshot()` の前後比較）。

★ **注記を消したときだけは例外。** ネイティブは値のラベルしか自分で更新しないので、
前回の失敗の注記は作り直さないと消えない。`Queue` で消したことを覚えておいて、
**成功した後の `Push`** で反映する（`_noticesStale`）。


**症状**: スライダーの**つまみを掴んでドラッグできない**（バーのクリックだけは効く）。重い。

`HandleSetting` → `ApplySettings` → `Refresh()` → `CMApplySchema` が**全ビューを作り直す**ので、
スライダーの最初のイベント（mouseDown）で**自分自身が破棄される**。

**手当ては3つ**:

1. ★★ **自分起点では作り直さない。** 画面には既に新しい値が出ている（ネイティブが `%g` で
   追従させている）。作り直すのは**外から変わったとき**だけ
2. **Unity 側の適用と保存もデバウンスする**（core への `PATCH` と同じ 300ms）。
   ★ とくに**ウィンドウのリサイズは重い**（`Keeper` が最大5回書き直して追従する）
3. **ドラッグ中に届いた変更は保留にする**（ネイティブはドラッグ中を投げないが、矢印キーの連打は届く）

### ★★ ウィンドウの反映は数フレーム遅れる —— 直後に読むと古い値が返る

**症状**: 「キャラクターの位置と大きさをリセット」を押しても、**「大きさ」のスライダーが
1.5 のまま**残る。

`WindowGeometry.Reset()` / `SetSize()` は位置と大きさを何度か書き直して追従するので、
**押した直後の `CurrentSize()` はまだ古い**。`Notice` と一緒にその場で読むと外す。

**手当て**: `Tick()` で毎フレーム `WindowScale` を見張り、`_context` と食い違ったら追いつかせる
（`SettingsPanelBridge.WatchWindowSize`）。**窓の端を掴んでリサイズしたときにも同じ経路で追いつく**。

★ **保留中（`_pendingScale != null`）は見送ること。** つまみをドラッグしている最中に
作り直すと、掴んでいるスライダーごと消える。

### ★★ 「大きさ」の権威は `window.json` ひとつ —— `settings.json` に持たせない

cc-mascot の「キャラクターサイズ」はコンテナ（ウィンドウ）の大きさで、ユーザーの期待もそちら。
`VrmStage.Headroom`（カメラの前後）を動かす形にすると、`headroom < 1` は
「bounds が画面からはみ出す」という意味なので**頭と足が対称に欠ける**。

**ウィンドウさえ変えればモデルは勝手に収まる** —— `VrmStage.LateUpdate` が
`Screen.width/height` の変化を毎フレーム見てフレーミングし直す（`_framedWidth` 比較）。

★★ **`character.scale` を `settings.json` から外した。** ウィンドウの大きさは既に
`window.json` が持っており、**両方に持つと権威が2つになる**（ユーザーが窓を直接リサイズしたら
どちらが勝つのかが説明できない）。スライダーの値は**現在のウィンドウの高さ ÷ 480** から出す
（`SettingsContext.WindowScale`）。

★ 既存の `settings.json` に残る `character.scale` は「知らないキー」として警告のうえ無視される
（未リリースなので移行は不要）。**その1キーだけ黙って捨てる**分岐は入れない —— 例外を作ると、
次に消すキーでも同じ判断を迫られる。

### ★ `preferredMaxLayoutWidth` を固定値にしない

折り返す複数行ラベルには `preferredMaxLayoutWidth` が要る（無いと Auto Layout は
「1行ぶんの幅」を要求し続け、長い note で**ウィンドウごと横に伸びる**）。

だが**固定値にすると、パネルを広げても折り返し位置が動かない**。ライセンス本文のように
**元から整形済み**の文章では二重の折り返しになり、1文字だけの行が出る。

**手当て**: `-layout` で自分の幅に合わせ直す `CMWrappingLabel` を作り、
**幅いっぱいに置くラベル（見出し・note・本文）だけ**それにする。行の中に置くラベル
（項目名・数値・記号）は中身で幅が決まるので**そのまま**にすること
（変えるとレイアウトが振動する）。

★ **変わったときだけ書くこと。** 毎回書くと `invalidateIntrinsicContentSize` が
レイアウトを呼び戻して振動する。

### #76 の実機確認（macOS / `.app` / AivisSpeech 稼働）

**合成イベント（`CGEvent`）とスクリーンショットで自動化した。** 手で触るより再現性が高く、
「アプリが一度もアクティブになっていない状態」のような**手では作りにくい前提**を固定できる。

| 確認したこと | 結果 |
|---|---|
| **非アクティブのままキャラを右クリック** | ★★ 冷えた起動直後から **6/6 で開閉**。以前は「左クリックを1回挟むまで効かない」 |
| **透明な場所での右クリック** | ★ 3/3 で何も起きない（窓の外も同じ） |
| **短い押下（50ms）** | ★★ 6/6。ポーリングだけの実装では 1/4 しか拾えなかった |
| `⌃ + 左クリック` | ✕ **成立しないので落とした**（→ 上の節） |
| **大きさのスライダー** | ★★ つまみを掴んでドラッグでき、**ウィンドウごと** 300x480 → 450x720 に変わる。フレーミングも追従し**頭が欠けない** |
| ドラッグ中の再描画 | ★ 起きない（値のラベルだけネイティブが `%g` で追従する） |
| **話す速さ（core を往復する）** | ★★ 0.4秒間隔で3連続ドラッグしても全部効く。**パネルのスクロール位置が保たれる**＝作り直していない証拠 |
| 3つのスライダーを続けて操作 | ★ 話す速さ 1.6 / 大きさ 1.4（窓 420x672）/ 音量 50% がすべて着地 |
| サーバーを止めてスライダーを動かす | ★ 「サーバーに繋がりません」が出て項目が無効になり、復帰後は注記が消えて実際の値に戻る |
| **VRM を選ぶ** | ★★ `.vrm` が**選べる**（`.vroid` はグレーアウト）。`models/mascot.vrm` に**固定名**でコピーされ、note には**元の名前**（`AvatarSample_A2.vrm`）が出る |
| **2本目を選ぶ** | ★★ `mascot.vrm` が**上書き**され、ファイルは増えない。手で置いた `aaa-decoy.vrm` は無傷 |
| **選んだモデルが実際に読まれる** | ★★ 再起動すると `設定: …/models/mascot.vrm` を読む。名称順で先に来る `aaa-decoy.vrm` に**負けない**（以前は負けていた） |
| **位置と大きさのリセット** | ★★ **その場で**効く（450x720 → 300x480、既定位置へ）。スライダーも 1 に戻る |
| **すべての設定をリセット** | ★★ 確認ダイアログ（`NSAlert`・destructive）→ `settings.json` / `window.json` / `config.json` の3キー / `models/*.vrm` が**すべて**初期状態へ。note に「既定に戻しました（モデル 1 件を削除）」 |
| **ショートカットの記録** | ★★ `⌃⌥J` を記録。キー押下の直後から連写しても**古い表記（`⌃⌥M`）が出ない**（以前は `CMStopRecording` が成功時にも元の文字列へ戻していた）。`settings.json` に `ctrl+opt+j`、メニューバーの表記もその場で変わる |
| **「Chatter Mascot について」** | ★★ **別ダイアログ**（680x664）で開き、MIT 全文が読める。折り返しがパネル幅に追従する |
| 設定パネルの項目 | ★ 「ミュート」「テスト要約を実行」「このアプリについて」が**消えている** |
| 話者一覧 | ★ 実際のエンジンから取れる（「まお（ノーマル）」等） |
| **音量 0.3 / 1.5** | ★ `ps` に `/usr/bin/afplay -v 0.3 …` / `-v 1.5`（`< 1` で判定していたら出ない）。★ **この 1.5 は #85 のレビュー C-4 で無くなった** —— 上限は 1.0（→ 下の節）。読み直すときは 30% / 100% で見ること |
| スライダーの刻み | ★ 0.1 に吸着し、`settings.json` にも `0.3` / `1.5`（`0.30000001` ではない） |
| 話す速さ / 要約 / 話者 | ★ `config.json` に `ttsSpeedScale` / `aiSummaryEnabled` / `ttsSpeakerId`（**Unity は `config.json` を直接書いていない**） |
| テスト音声 | 鳴る（`afplay の実時間 4973ms / WAV の長さ 4031ms`） |
| **サーバーを止めて開く** | ★★ 話者・速さ・テスト音声が**項目ごと消えず**、「（取得できません）」「サーバーに繋がりません」で無効になる |
| **再起動しても残る** | ★ 音量 / 速さ / 話者 / ショートカットがそのまま復元される |

★ **未確認**: サーバーを止めた状態での「すべての設定をリセット」（core のぶんが戻らなかったことが
note に出るか）、`⌃⌥J` の実発火、パネルがキャラに重なっているときの右クリック（→ 既知の穴）。

### #85 のレビュー修正の実機確認（macOS / `.app` / AivisSpeech 稼働）

**同じやり方で自動化した**（`CGEvent` + `screencapture`）。設定パネルは**ネイティブの
`NSPanel`** なので、`-settingsProbe` で開いてしまえば座標を打つだけで触れる。

★ **サーバーもアプリも `XDG_CONFIG_HOME` を別に向けて走らせた。** 常用のインスタンスと
`settings.json` / `window.json` / `config.json` を取り合わないようにするため
（`forceSingleInstance` が立っているので**常用のものは一度落とす必要がある**）。

| 確認したこと | 結果 |
|---|---|
| **音量スライダー**（C-4） | ★★ 読み値が **`70%`**、`settings.json` は **`0.7`**（`70` ではない）。`ps` にも `/usr/bin/afplay -v 0.6 …`（`-v 60` ではない）。上限は 100% で止まる |
| **音量を離した直後（300ms 以内）に「まばたき」を外す**（A-3） | ★★ 音量 60% と まばたき OFF が**両方**残る。以前は保留の着地でまばたきが true に戻り、**外れて見えるのに実際はオン**になっていた |
| **「大きさ」を動かして離す**（A-2） | ★★ 窓が 420x672 になっても**パネルのスクロール位置が保たれる**（0.6s / 2.1s / 5.1s のスクリーンショットが**バイト単位で一致**）。以前は `WatchWindowSize` が誤読して全ビューを破棄していた |
| **音量を離した直後（300ms 以内）に「終了」**（A-4） | ★★ `settings.json` に **0.3** が残る（以前は 0.6 のまま）。再起動すると 30% で開く |
| **赤いボタンで閉じて開き直す**（B-2） | ★★ 前の注記が**残っていない**。閉じたことが `{"type":"panel","state":"closed"}` で C# に届いている |
| **`ttsEnabled: false` でテスト音声**（#84 の追随） | ★★ 「サーバー側で音声が無効になっています（ttsEnabled）」が**日本語で**出る（`409 tts_disabled` → `DescribeError` → note の経路） |
| **合成に 10 秒かかる状態でテスト音声**（A-1） | ★★ **10.4 秒後に鳴った**。以前は 5秒で `ConnectionError` になり「サーバーに繋がりません」と出ていた（実際には合成中）。エンジンへの中継に 4秒 × 2 の遅延を挟んで作った |
| **同じラベルの話者が2つある状態**（C-2） | ★★ 16件すべてが並び、**「まお（ノーマル）」が2つ**出て、**チェックは実際の値（`888753760`）の方**に付く。以前は先に入れた方が消えて 15件になっていた。話者一覧の中継で重複を1件足して作った |
| **`-serverUrl` が設定パネルにも効く**（B-1） | ★ 上書き先（8571）だけが `/v1/*` を持つ状態で、話者一覧もスライダーも動いた（常用の 8570 は `/v1/config` が 404） |
| キャラクターの右クリック | ★ 開閉できる（#76 から変わらず） |
| 再起動 | ★ 音量 30% / 大きさ 1.4 / まばたき OFF がすべて復元される |

★ **未確認**: `runModal` 中に ack が止まること（doc に書いただけ。再現には発話中にファイル選択を
開く必要がある）。C-1（ホットキーの注記）と C-3（同時に落ちた PATCH の2件目）は
EditMode でも実機でも固定していない —— どちらも**注記の出方**なので、目で見る以外の確かめ方が無い。

### ★★ 制御 API を持たないサーバーに繋ぐと「not found」としか出なかった

**音声スタイル・話す速さ・AI要約が3つまとめて無効になり、note に `not found` とだけ出る。**
実際に踏んだ（マスコットはワークツリーのビルド、サーバーは `main` のチェックアウトから
起動していた —— `#76` 以前の `chatter-agent-server` には `/v1/*` が無い）。

原因はクライアント側の出し方にある。`/v1` を持たないサーバーはルートに当たらない要求へ
**`not found` というプレーンテキスト2語**を返すが、`DescribeError` は
「知らない `error` はそのまま出す」方針なので、**JSON ですらない本文がそのまま note に出る**。

`DescribeFailure(status, body)` を切り出して、**404 は本文より先に見る**ようにした:

```
API not found (/v1). Update chatter-agent-server.
```

- ★ **英語のまま出す。** 他の note と揃わないのは承知のうえで —— これは設定の説明ではなく
  **配線の診断**で、読む相手は「サーバーを更新する人」。検索して突き合わせられる方がよい
- ★★ **「版が違う」と言い切らないこと。** 404 が言っているのは**その口が無い**ことだけで、
  なぜ無いのか（古い / 別物 / 将来消した）はこちらの推論にすぎない。
  観測した事実だけを出して、対処だけ添える
- ★★ **このクライアントにとって 404 は「口が無い」だけ**だが、サーバー側にはもう1つ
  404 を返す枝がある（**ループバック以外からの書き込み**を「口の存在ごと見せない」で断る絞り）。
  同じマシンで動く前提なので今は当たらないが、**#25 で別ホストに繋ぐようになると
  書き込みだけ 404 になる** —— そのときはメソッドで出し分けること
- ★ `Player.log` にも1行残す。パネルの note は**開いている間しか見えない**うえ、
  3項目がまとめて無効になる原因は後から突き合わせたくなる種類の情報

### ★★ 音量の上限は 1.0 —— プラットフォームで意味の変わる範囲を設定に持たせない（#85 レビュー C-4）

#76 の初版は音量を **0.0〜2.0** にしていた。macOS では `afplay -v` にそのまま渡るので 2.0 まで
効くが、**Android の `AudioSource.volume` は Unity 側で 0〜1 にクランプされる**
（`audioSource.volume = 1.5` は黙って 1.0 になり、`AudioClipPlayer.CopySettings` はその 1.0 を
各 voice に写す）。つまり**スライダーの右半分が XR では no-op** になる。

`settings.json` は **XR（#25）と共有する前提**なので、doc を「macOS だけ 2.0 まで効く」と
書き直す逃げ道は採らなかった。**同じファイルの同じキーが、開いた環境によって意味を変える**のは
設定として成立しない。上限を 1.0 に下げてある（`SettingsMapping.VolumeMax`）。

- 1.0 超えを効かせるには `AudioMixer` が要る。入れるなら**両方で効く形にしてから**
- 既に `1.5` が保存されていても壊れない。`SettingsJson.ReadNumber` が範囲へ収めて警告を1本出す
- `NeedsVolumeArgument` は `Math.Abs(volume - 1f) > VolumeStep / 2f` のまま。
  「大きくする側が効かなくなる」という以前の理由は消えたが、**`< 1` の裸の比較にすると
  `0.9999999` に `-v 0.9999999` が付く**ので、単純化してはいけない

### ★★ スライダーの表示文字列を、C# へ送る値に使わない

上の変更に合わせて表示を **0〜100%** にした（`SettingDisplay.Percent`）。ここで踏みかけたのが、
ネイティブ側の `onSlider:` が**読み値のラベルに使った文字列をそのまま `CMEmitSetting` に
渡していた**こと。`%` を付けた瞬間に C# の `SettingsMapping.Parse` が `"70%"` を読めず、
**そのキーだけ既定に戻る**——ロケールで `0,7` になるのと同じ壊れ方をする。

**表示は `CMSliderText`、送るのは常に `%g` の生の数**、と分けてある。

★ **「音量なら % で出す」を ObjC に書かないこと。** どう見せるかは C# のスキーマが
`display` で渡し、ネイティブが知っているのは「% かどうか」だけ。これを崩すと、
**ネイティブに設定のキーを一切書かない**という #76 の作りの前提が1箇所だけ破れる。

### ★★ 窓の適用中は「外から変わった」と読まない（#85 レビュー A-2）

`WindowGeometry.SetSize` は窓に書くだけで、`CurrentSize()`（＝`WindowScale`）が新しい値を
返すのは `Applying` が一致を確かめた後。最大5回の書き直しぶん遅れる。その間に
`SettingsPanelBridge.WatchWindowSize` が「外からリサイズされた」と誤読すると
`Push(update: true)` ＝ **全ビューの破棄**に入り、スクロール位置が先頭へ飛び、掴み直していれば
つまみが手の中で死ぬ ——「**話す速さで潰したはずの teardown が、大きさで再発する**」。

`WindowGeometry.IsApplying`（`ISettingsHost.WindowSizeSettling`）を見て見送る。
**「押した直後は読まない」をフレーム数の当て推量ではなく状態で表す**形なので、
「位置と大きさをリセット」の後の追いつき（`WatchWindowSize` の本来の仕事）は壊れない。

### ★★ 保留（デバウンス）の起点は「保留があるならそれ」（#85 レビュー A-3 / A-4）

`MascotSettings` は構造体なので、保留は**まるごとの写し**になる。変更を積む起点を
「いまホストが持っている値」にすると、デバウンス中に確定した別の項目を**あとから巻き戻す**:

1. 音量スライダーを離す → 保留に `{volume=0.5, blink=true}`
2. 300ms 以内に「まばたき」を外す → ホストの値から作って即座に確定
3. 締め切りで保留が着地 → **`blink` が `true` に戻る**

チェックボックスは意図的にパネルを作り直さないので、症状は
**「外れて見えるのに実際はオン」**という気づけない形になる。判断は
`Runtime/Settings/PendingChanges.cs`（EditMode で固定してある）。

- **即時適用も保留を通す。** 別経路にすると権威が2つになる
- **リセットの手前で捨てる。** 残すと既定へ戻した**後ろ**に古い値が着地する。
  「位置と大きさをリセット」は `ClearScale()`（窓だけ戻すので、同じ窓に居る音量を道連れにしない）
- **終了の手前で投げ切る。** `Quit` は `Application.Quit()` を先に走らせるので、
  `Flush()` が無いと締め切りが二度と来ない。**閉じるだけなら失われない**
  （`Bridge.Update` が `Tick()` を無条件に呼ぶ）—— 落ちるのは `Quit` と `OnDestroy` の2経路

### ★★ 赤いボタンで閉じたことは、ネイティブから返さないと届かない（#85 レビュー B-2）

`windowWillClose:` で `{"type":"panel","id":n,"state":"closed"}` を投げる。返さないと
C# 側の `_open` が真のまま残り、`_notices` も消えないので、**開き直したときに数分前の
「音声を合成できませんでした」が、いま起きたことのように出る**。

- **`setting` に相乗りさせない。** あちらは「設定のキー」を運ぶ口で、ObjC に設定のキーを
  書かないという規律がある。`menu` / `hotkey` / `log` と同じ**プロトコルの語彙**として型を足す
- `CM_PanelHide` の `orderOut:` は `NSWindowWillCloseNotification` を**出さない**ので、
  自分で閉じたときに二重には来ない
- 「について」（id=1）でも飛ぶので、C# 側が id で弾く

### ★★ `NSPopUpButton` の `addItemWithTitle:` は同名の項目を削除する（#85 レビュー C-2）

文書化された挙動で、同じタイトルの項目が既にあると**古い方を取り除いてから**追加する。
話者のラベル（`名前（スタイル名）`）が衝突すると:

- 先に入れた話者がメニューから外れ、**パネルから選べなくなる**
- `count` は加算されるので、`count == 0` の「—」フォールバックにも落ちない
- 現在の `ttsSpeakerId` が外れた方だと、`selectItem:` は切り離された項目に対して行われ、
  **別の話者が選択中であるかのように見える**

`NSMenuItem` を自分で作って `[popUp.menu addItem:]` すれば重複を許容できる。

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
