# デスクトップ常駐で踏んだこと — 透過ウィンドウ・クリック透過・常駐

基本設計・構成・コマンドは [`../mascot.md`](../mascot.md)。ここは**実装で踏んだことだけ**を残す。

## ★ `Screen.*` はバッキング px、ネイティブのウィンドウ API はポイント

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

## ★ 物理的な大きさはディスプレイのスケールで変わる（#16 で解決）

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

## ★ ウィンドウの座標系は bottom-up・左下基準。モニタ矩形は「作業領域」

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

## 実測（2026-08-30 / macOS 26.6.2 / 外部 4K(1x) + 内蔵 Retina）

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

## ★ ウィンドウは起動のたびに縦へ 32 伸びる（自前の永続化で消えた）

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

**いまは `Desktop/WindowGeometry.cs` が、意図した大きさを自分で決めているので育たない**
（#16）。上の 1〜4 のループは「Unity が復元した px の値」を出発点にしているが、
**出発点を自前の値（大きさは `settings.json` の `character.scale`、位置は
`~/.config/chatter-agent/mascot/window.json`）に差し替えるとループそのものが成立しなくなる。**

> ★ **以前は `Desktop/WindowSizeKeeper.cs` が「起動直後に見えていた大きさ」へ戻す
> 対症療法で打ち消していた。** [#66](https://github.com/schwarz9791/chatter-agent/issues/66) が
> 指摘していた2点（`_intended` を捕まえる Start 順序が未保証 /
> 補正が最初の1回で打ち切り）は、**権威を移したことで構造的に消えた**ので、
> keeper は削除した。**2人が `windowSize` を書く状態を作らないこと。**

★ **縮んだ側も追いかけない、という区別が要らなくなった。** keeper は「勝手に増えるぶんだけ」を
打ち消す必要があったが、いまは**大きさは常に「既定 540pt × `character.scale`」から決まる**ので、Unity が復元した値を読み返さない。

★ **書いた値が効いたかを見張るのはやめないこと**（既定5秒 / 最大5回）。枠なし化は
起動直後の数フレームで起きるが、VRM の読み込みでメインスレッドが詰まると実時間では
後ろへずれるし、**`Metal RecreateSurface` は起動ごとに2回出る**（＝膨らむ機会が2回ある）。

## ★ ウィンドウの大きさは3箇所で決まる。ProjectSettings だけ見ても分からない

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

## 実測（2026-08-26 / macOS 26.6.2 / 4K 外部ディスプレイ）

★ **#56 当時の記録（いまは当てはまらない）。** `Player.log` に起動直後
`Metal RecreateSurface: surface size 250x200`（`defaultScreen*` のまま）→
`MascotRunner.Start()` の `[Mascot] server: ...` ログを挟み →
`UniWindowController` が枠なし化した直後 `250x232` で **+32**
（タイトルバーぶんがコンテンツ領域へ編入。高さにだけ乗る）。いまは
`Desktop/WindowGeometry.cs` がポイントで持った既定と自前の永続化から大きさを決めるので
この伸びは残らず（→「ウィンドウは起動のたびに縦へ 32 伸びる」）、`Default Screen Width/Height`
が効くのは窓を掴むまでの数フレームだけで、「368 と入れて 400 になる」という回避策も
当てはまらない。

★ **当初これを「Retina で2倍されている」と読んで `200` を入れ、232 になって外した。**
**推測で式を組まずに測ること。** ——ただし「2倍にならない」という結論も
**scale 1 の外部ディスプレイでしか成立していなかった**（→ 下の節）。

★ **`UniWindowController` は大きさを変えていない。** `_shouldFitMonitor` は既定 `false` で
prefab にもシーンにも override が無く、`SetWindowSize` を呼ぶのは `#if UNITY_EDITOR` の
`OnApplicationQuit` だけ。`forceWindowed` はフルスクリーンを解除するだけで大きさに触らない。

★ **`resizableWindow: 1` だが、透過窓は枠なしで端を掴んでリサイズはできない。** 窓の大きさは
「既定 540×540pt × `character.scale`」で、変えられるのは `character.scale` だけ。ただし
**大きさを前提にした描画を書かないこと** —— VRM の自動フレーミングが毎フレーム
`Screen.width/height` の変化を見ているのはこのため（→ `Vrm/VrmStage.cs`）。

★ **測るときは走っている他のインスタンスに注意。** 別 checkout の `.app` が常駐していると
`osascript` の「名前で最初に見つかったプロセス」がそちらを掴む。**pid で引くこと**。
`forceSingleInstance: 1` は**別パスの `.app` の同時起動を防がない**（実際に2つ動いた）。

★★ **#88 で既定を 300x480 → 540x540（1:1）に変えた。** 起動直後の大きさは `WindowGeometry.cs` の
既定（540pt）に `character.scale` を掛けたもの。`window.json` は**位置だけ**を持ち、
保存された幅と高さは大きさとして読まない（旧既定 5:8 の矩形が残っていても大きさに影響しない）。

★ **`window.json` の `version` は上げない。** `WindowStateJson` は未知の `version` を拒否して
既定配置へ落とすので、上げた瞬間にユーザーの位置そのものが失われる。

実機（2026-09-04）: 既定サイズでの起動では `フレーミング: 540x540 aspect=1.000 …
支配軸=垂直` で、腕を上げる `WIN00` の敬礼モーションでも頭上に約10%の余白が残った
（`Hub_laugh01`、`Hub_Idle01` も問題なし）。

## ★ Unity 6 の URP で透過しないのは `Supports HDR` のせい

`Is Transparent` を入れても**背景が黒いまま**になる。枠なしウィンドウにはなるので、
「ネイティブプラグインは動いているのに中身が透けない」という分かりにくい壊れ方をする。

**決め手は URP Asset の `Supports HDR` を切ること、1つだけ**だった
（macOS 26 / Unity 6000.5.8f1 / URP 17.5.0 で、on/off を往復させて確認した）。
[#97](https://github.com/schwarz9791/chatter-agent/issues/97) で Unity 6000.3.14f1 / URP 17.3.0 に切り替えた後も同じ設定のままで、透過が保たれていることをビルドで再確認した。

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

## ★ `EventSystem` があってもポインタイベントは配送されない

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

## ★ マスコットのドラッグ移動は UniWindowController 同梱の `UniWindowMoveHandle` を使う

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

## ★★ ドラッグ終了を取りこぼすと、クリック透過が死んだまま残る

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

## 実機で試した範囲（2026-08-30。**再現せず**）

ディスプレイまたぎ（8往復）/ 移った先の画面に収まる位置まで運ぶ / 掴んだまま修飾キーを
押しっぱなしにして離す —— **いずれも警告 0 件、クリック透過も正常。**
「狙って踏みに行ってもこの環境では起きない」までは言えるが、**コメント1 の症状が
何だったのかは未確定**。ガードは救済であると同時に、**次に出たときの切り分けの計器**として残す。

## ★ `UniWindowController.GetCursorPosition()` の Y は bottom-up

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

## ★ シーンに `EventSystem` が無いとクリック透過が死ぬ

`UniWindowController` の Raycast ヒットテストは `EventSystem.current.RaycastAll` を呼ぶ。
**シーンに `EventSystem` が無いと毎フレーム `NullReferenceException`** で、
クリック透過が一切効かない。

★ **ウィンドウの透過そのものは成立する**ので、見た目には気づけない。
Inspector 上も正常に見える。**ビルドしたアプリのログを読むまで分からない。**

`Editor/SceneFixups.cs` の `FixAll` が保証する。**シーンを作り直したら必ず走らせること**:

```bash
./scripts/run.sh ChatterMascot.EditorTools.SceneFixups.FixAll
```

## ★ Dock に出さないのは `LSUIElement` だけ

Unity で書けるのは、ビルド後処理で `Info.plist` に `LSUIElement` を書いて Dock に出さないことだけ
（`MacPostBuild`）。メニューバーのアイコン・フォーカスが無いときのグローバルショートカット・設定の UI は
Unity だけでは作れないので、マスコットは持たない（→ ChatterAgent。[`../agent.md`](../agent.md)）。

★ **cc-mascot の知見は1つ効いた。** cc-mascot は `app.dock.hide()` を `ready-to-show` から
**500ms 遅らせている**（コメント: 「起動時に呼ぶとフルスクリーン Space で起動してしまうため」）。
`LSUIElement` は起動前から accessory なので同じ罠は踏まない。起動後に activation policy を
呼び直す保険は持たず、`LSUIElement` だけに頼る。

## ★★ `Info.plist` を `System.Xml.Linq` で書き換えると壊れる

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

## ★★ 一時ミュートは「声だけ消す」—— 音量では実装できない

**音量を変える手段が無い。** macOS の再生の実体は引数なしの `afplay`（音量を渡す口が無い）で、
`Disable Unity Audio` が ON なので `AudioListener.volume` も効かない。
**再生そのものを飛ばす**のが唯一の手段になる。

`MutedSpeechPlayer`（`ISpeechPlayer` のデコレータ）が3つを同時に満たす:

| | |
|---|---|
| **ack は必ず出す** | `PlayAsync` が成功を返せば `Played` → `Finish` → `ConsumeHead` → `EmitAck` の**通常経路そのまま**が走る。★ 止めるとキューが `speechQueueMaxEntries`（500）まで溜まって古い方から捨てられ、解除後に**歯抜けで喋り出す**（→ [`protocol.md`](../protocol.md) の責務2・3）。**`PlaybackQueue` には1行も触らない** |
| **長さぶん待つ** | ★ 即座に返すと、溜まっていた発話が数百 ms で全部消化されて**表情が高速で切り替わる**。「声だけ消す」は実時間を消費して初めて成立する |
| **口だけ止める** | `MascotRunner.BeginSpeaking` が `_mute.Muted` のときエンベロープを落とす。★ **`_speaking` への登録そのものは飛ばさない** —— 飛ばすと表情も体の動きも止まり、「声を消した」ではなく「居なくなった」に見える（→ `SpeakingSet.Begin` の doc） |

★ **`ActiveCount` に「無音で待っている本数」を足すこと。** 足さないとミュート中は
本物の `ActiveCount` が常に 0 になり、`AudioIdleGate` が「鳴っていない」と判定して
出力デバイスを手放す。macOS は `CanSuspendOutput == false` なので無害だが、
**Android では実際に手放してしまう**（→ #97）。

★ **`Prepare` は本物に委譲すること。** WAV の検証もエンベロープ生成も走るので、
ミュートの有無で**ログの見え方が変わらない**。無音の原因を切り分けるとき、
ミュートかどうかで診断の出方が変わるのはいちばん困る。

★ **ミュートにした瞬間、鳴っているものを止める。** 押す動機は「いま喋っているのを黙らせたい」
なので、次の発話から効くのでは遅い。**そのとき返る失敗は成功に倒す**（止めたのは自分なので、
押した本人に向かって警告を出さない）。

★ **合成は止まらない。** ミュート中も `GET /audio/…` は走る。止めるには `PlaybackQueue` に
触る必要があり、上の規律と引き換えになるのでしない。

## ★ `LSUIElement` の代償

| # | 代償 | 手当て |
|---|---|---|
| 1 | **Dock から終了できない** → [#68](https://github.com/schwarz9791/chatter-agent/issues/68) の再現手段が消える | **#16 で #68 を閉じてから着手した。** 保留経路の確認は `-quitProbe` が唯一の手段になる |
| 2 | **⌘Q が効かない**（メニューバーが無い）。単体で起動したマスコットには終了の UI が無い | 終わらせるのは ChatterAgent の「マスコットを隠す」と「終了」（`NSRunningApplication.terminate()` の通常の quit）。★ **マスコットから `[NSApp terminate:]` を呼ばないこと** —— `Application.Quit()` なら #68 で直した経路（`wantsToQuit` で ack を投げ切ってから `Update` の先頭で呼び直す）にそのまま乗る |
| 3 | ★ **`forceSingleInstance` が防げない二重起動が見えなくなる** | 起動時に `[Mascot] pid=… bundlePath=…` をログに出す |

## ★ グローバルショートカットは Carbon（`RegisterEventHotKey`）で登録する

ChatterAgent が登録する（`tauri-plugin-global-shortcut`。macOS では同じ Carbon の `RegisterEventHotKey`）。
`RegisterEventHotKey`（HIToolbox）は **アクセシビリティ権限のダイアログが出ない**。
`NSEvent.addGlobalMonitorForEvents` は出る —— 常駐アプリのために
「入力の監視」を許可させるのは要求として重すぎる。

★ **`RegisterEventHotKey` は非推奨ではない**（SDK 26.5 で確認）。宣言に付いているのは
`AVAILABLE_MAC_OS_X_VERSION_10_0_AND_LATER` だけで、deprecation 指定が無い。
同じ `CarbonEvents.h` の中には `DEPRECATED_IN_MAC_OS_X_VERSION_…` が **69 箇所**ある
（`RetainMouseTrackingRegion` など）ので、Apple は畳むべきものには印を付けていて、
**ホットキー登録には付けていない**。`-Wdeprecated-declarations` を明示してビルドしても警告ゼロ。

「Carbon は非推奨」という一般論が指すのは主に GUI 部分（HIView / HIWindow / QuickDraw）で、
64bit 移行時に消えた。ホットキー登録が残っているのは**代替が無い**ため。

★ **修飾キー無しを拒否すること。** 単独のキーを登録すると、そのキーが
**どのアプリでも入力できなくなる**。ChatterAgent の設定窓と登録の両方で弾いている。

## ★★ 既定のショートカットに `⌥` 単体と `⌘⌥` を選ばない

**`RegisterEventHotKey` は「他アプリの排他登録」しか見ない。** 実測では
`⌥M` / `⌥H` / `⌘⌥M` / `⌘⌥H` / `⌃⌥M` / `⌃⌥H` の**6候補すべてが登録に成功する**。
実害はその先にある。

**1. `⌥` 単体は文字を入力する。** `UCKeyTranslate` に現在のレイアウト（ABC）で聞くと:

| | 入力される文字 |
|---|---|
| `⌥M` | **`µ`**（U+00B5） |
| `⌥H` | **`˙`**（U+02D9） |

ホットキーとして登録すれば横取りできる（実際に `⌥M` でミュートが切り替わった）が、
**登録している間、全アプリでその文字が打てなくなる**。

**2. `⌘⌥` は macOS 標準と衝突する。** Finder のメニューから
`AXMenuItemCmdChar` / `AXMenuItemCmdModifiers` を引くと:

| メニュー項目 | |
|---|---|
| ほかを非表示 | **`⌘⌥H`** |
| すべてをしまう | **`⌘⌥M`** |

「ほかを非表示」はアプリメニューにあるので**全アプリ共通**。奪うと他のアプリの標準機能が効かなくなる。

**3. `⌃⌥` はどちらでもない。** 文字入力に使われず（`⌃⌥M` は U+000D の制御文字）、
標準ショートカットの割り当ても無い。**ChatterAgent の既定はこれ**（`ctrl+opt+m` / `ctrl+opt+h`）。

★ **ユーザーが設定で `⌥M` を選ぶぶんには止めない。** 既定として押しつけないだけ。

## #93 の実機確認（macOS 26.6.2 / `.app`）

アプリアイコン（Finder に出る。他アプリと並べて浮かない）・`PlayerIcon.icns` の生成
（`Contents/Resources/` に 16〜512 / 512@2x）・`LSApplicationCategoryType`
（`public.app-category.utilities`。`LSUIElement` は健在で `MacPostBuild` を壊していない）・
Game Mode のロケット消滅（カテゴリを変えるだけで消え、`LSSupportsGameMode` /
`GCSupportsGameMode` は足していない）を実機確認、全項目パス。

## ★ `open` は同じ bundle id のアプリが動いていると新しいプロセスを起こさない

別ワークツリーのビルドと並べて試すときは `open -n` を使う。同じ bundle id
（`tech.sukima.chatter-mascot`）を名乗る `.app` は**ワークツリーの数だけ LaunchServices に登録される**
（`Build/` と `Temp/BurstOutput/` の両方が入るので、すぐ数十件になる）。**別ワークツリーの `.app` が
起動したままだと `open` が新しいプロセスを起こさない**ので、確認の前に
`pgrep -fl ChatterMascot.app` で見ること。

★ **`.app` の中身を手で差し替えないこと。** コード署名が壊れて `open` から起動できなくなる
（`Player.log` が空のまま終了する）。直したら `./scripts/build.sh` を通すこと。

## ★★ 公開する画像は、編集ツールのメタデータを落とす

PNG の XMP（`iTXt`）と ICC（`iCCP`）には**実名・作成時刻・オーサリングツール**が入り、
そのまま公開リポジトリと `.app` に載る。**目で見て分からない**。書き出し設定でそもそも埋めないか、
標準的な道具（`exiftool -all=` / ImageMagick の `-strip` / `oxipng --strip all`）で落とす。
画像形式（パレット形式・`tRNS`）は問わない。

## ★ `window.json` の永続化を `PlayerPrefs` と物理的に分けた理由

`PlayerPrefs` は使っていない。macOS では `PlayerPrefs` は `tech.sukima.chatter-mascot.plist`
に書かれるので、もしウィンドウの位置をそちらへ持たせていたら、Unity が焼き付けた
画面サイズを消すための `defaults delete tech.sukima.chatter-mascot`（→「ウィンドウの大きさは
3箇所で決まる」の焼き付き消し）が**自前の永続化ごと消してしまう**。位置は
`~/.config/chatter-agent/mascot/window.json` にポイントで持たせ、Unity が px を焼く場所と
物理的に分けてある（大きさは `settings.json` の `character.scale`）。

## ★ `.app` を Finder から起動すると環境変数は空

シェルを継承しないため、`CHATTER_MASCOT_VRM` / `CHATTER_MASCOT_VRMA` のような環境変数は
**アプリでは効かないが、開発者のシェルから直接起動する `scripts/run.sh` 経由（`VrmProbe` など）
では効く**。切り分けに環境変数を使うときは、この非対称を前提にすること。

## Player Settings（macOS）の値の理由

`Default Is Native Resolution` / `Default Screen Width` / `Default Screen Height` /
`Disable Unity Audio` の理由は「ウィンドウの大きさは3箇所で決まる」と「無音時に
オーディオ出力デバイスを掴まない」（→ [`mascot-speech.md`](./mascot-speech.md)）にある。
残りの項目:

- **`Run In Background`（オン）**: 常駐して背面でも喋るため。フォーカスを失って止まると発話が止まる
- **`Use Mac App Store Validation` / `Mac App Sandbox`（オフ）**: 透過をブロックしうる
  （`Mac App Sandbox` は Unity のビルドが entitlements を付けないので既定でオフ）
- **`API Compatibility Level`（.NET Standard 2.1）**: `System.Net.WebSockets.ClientWebSocket` と
  `System.Diagnostics.Process` を使う。どちらも .NET Standard 2.1 で足りることを確認済み
