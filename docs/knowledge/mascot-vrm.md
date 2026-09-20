# VRM の表示で踏んだこと — フレーミング・視線・表情・モーション

基本設計・構成・コマンドは [`../mascot.md`](../mascot.md)。ここは**実装で踏んだことだけ**を残す。

## ★ `UnityWebRequest.timeout` は `file://` に効かない

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

## ★ ランタイムの Collider は「奥行き」に合わせる。幅に合わせない

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

## ★ VRM は放っておくと背中が映る（仕様の読みで決めない）

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

## ★ ControlRig は `Vrm10Instance` の transform が単位回転であることを暗黙に前提にしている

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

## ★ `SkinnedMeshRenderer.bounds` は姿勢を反映しない（T ポーズの腕幅で測り続ける）

`updateWhenOffscreen == false`（既定）のとき、Unity は**メッシュに焼かれた静的な bounds**
を transform で変換して返すだけで、**ボーンを動かしても縮まない**。VRM 1.0 はレストポーズが
T ポーズ必須なので、その幅は**常に「広げた腕」**になる。

★ **`updateWhenOffscreen = true` で直してはいけない。** 毎フレーム CPU スキニングで bounds を
測り直すことになり、常駐アプリの電力予算を壊す（→ [`mascot-unity.md`](./mascot-unity.md)「Unity の既定はフレームレート無制限」）。
**Humanoid のボーン位置から測る**（`VrmBounds.OfBones`）。

実測（同梱 `vita.vrm` + `idle_loop.vrma`、300x480）:

```
[Mascot] フレーミング: 300x480 aspect=0.625 bounds=(1.54, 1.66, 0.33) distance=2.51 支配軸=水平   ← VRMA 適用前 / Renderer.bounds
[Mascot] フレーミング: 300x480 aspect=0.625 bounds=(0.62, 1.66, 0.37) distance=1.76 支配軸=垂直   ← VRMA 適用後 / ボーンから測定
```

幅 1.54m → **0.62m**、距離 2.51 → **1.76**（キャラが約3割大きく映る）、支配軸が
**水平 → 垂直**。

## ★ VRM 1.0 は T ポーズ必須。自動フレーミングの支配軸がそれで決まる

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

## ★ T ポーズの腕をフレーミングの箱に入れない（起動直後だけ小さく映るポップ）

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

★ **#88 でこの引き換えを緩和した。** 窓を 5:8 → 1:1 に、`VrmStage.Headroom` の既定を
1.1 → 1.25 に広げたことで、同じカメラ距離のまま横方向の余裕は 1.6倍、腕を上げる動きの
縦方向の逃げ場も増えた。実機で #70 の VRoid モーション（`WIN00` の敬礼など、腕を上げる・
広げるクリップ）を確認し、はみ出しは解消した。★ **`boneBoundsMarginMeters` 自体は
変えていない。** これを広げると余白は**カメラの前後（Z）方向にも**効き、クリック透過の
当たり判定の半径まで一緒に広がってしまうため——縦方向だけを広げたいときの調整つまみは
`headroom` の方（→ [`mascot-settings.md`](./mascot-settings.md)「『大きさ』の権威は `window.json` ひとつ」）。

## ★ `VrmProbe` の出力は「ランタイムと同じ関数」でなければならない

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
しかも [`mascot.md`](../mascot.md) は環境変数について
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

## ★ 同じ `TryGetBoneTransform` が、同一フレーム内で実行順によって別の値を返す

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

## ★ URP のレンダラーは Forward にする

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

## ★ アウトラインは「出ているのに見えない」ことがある

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

## ★ 画面空間の量をモデル空間の軸で回さない

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

## ★ カーソルの正規化をウィンドウの大きさで割らない

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

## ★ 視線の中立 —— 最初は「目標の位置」で直そうとして届かなかった

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

## ★ 視線の中立が合わないのは「頭が見る人の方を向いていない」から

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

## ★ 手続き的アイドルは腕を書かないと T ポーズのまま残る

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

## ★ 起動直後にだけ成立しない状態を「異常」として警告しない

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

## ★ `SetWeight` は黙って無視される。しかも「一覧に載っている」＝「顔が動く」ではない

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

## ★ expression の weight は自動でゼロに戻らない

`_inputWeights` は次に上書きされるまで保持され続ける。**アニメーションではなく状態**なので、
「もう出さない」を表すには**明示的に 0 を書く**必要がある。

いちばん刺さるのは口で、**喋り終わっても `aa` が開いたまま固まる**。`FacePolicy` は
`Speaking` が false になったフレームで `aa` を**猶予なしで即 0** にしている（表情には猶予があるのと対照的）。
★ **#58（リップシンク）が口を入れる前に、この経路だけ先に作ってある。** 後付けにすると必ず一度踏む。

## ★ override の判定は「モデルの静的な定義」で行う。ランタイムの `*OverrideRate` では検出できない

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

## ★ 表情を「体を止める条件」で止めない（`LateUpdate` の早期 return）

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

## ★ 発話は文の切れ目で必ず途切れる。だから表情に猶予が要る

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

★ **`messageId` で束ねて解決しようとしないこと。** [`protocol.md`](../protocol.md) が
「`messageId` の変化だけを根拠にした安全なバッチ化はできない」を3つの理由で明示的に禁じている。

## ★ VRoid の `happy` は目を細める。`override` が `none` なら瞬きと素で加算される

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

## ★ 瞬きの間隔は cc-mascot、形は UniVRM サンプル

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

## ★ `SetWeights` ではなく `SetWeightsNonAlloc` を使う

`Vrm10RuntimeExpression` は両方持っている。`SetWeights(IEnumerable<KeyValuePair<…>>)` は
`Dictionary` を渡しても**インターフェース越しに列挙するので列挙子がボックス化する**。
毎フレーム（30回/秒）書く場所なので、`Dictionary` を1本使い回して `SetWeightsNonAlloc` に渡す。

## ★ 「顔が動かないのが正常」と「壊れて動かない」はログでしか区別できない

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

## #57 の実機実測（2026-08-28 / macOS ビルド / `AvatarSample_A.vrm`）

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

## ★ VRMA の末尾の重複キーは、クリップ長を越えた評価で hips を NaN にする

**実機で観測された髪の飛びはストールではなく、これだった。** ストール（`Time.deltaTime` の
素通し。次の項）は同じ期間に何度も出ていたが、そのときは髪が飛んでいない。

**根本原因**（実機ログと UniVRM のソースから特定）:

- 一部の VRMA（VRoid Studio 書き出し）は**末尾のキーの時刻が重複している**
  （最後の2〜3キーが同じ時刻で並ぶ）
- UniVRM の `AnimationImporterUtil.CalculateTangent` は隣接キーの時刻差で割って接線を作るので、
  重複キーの直前のキーの outTangent が `0/0 = NaN` になる（`AnimationCurve.AddKey` は
  同時刻のキーを足さないが、NaN の接線はそのまま残る）
- 範囲内（0 〜 クリップ長）の評価ではこの接線は使われない。**クリップ長を越えた時刻で
  評価したときだけ**、その NaN の接線で外挿されて hips の位置が NaN になる
- フェードアウトは `state.time >= length - FadeSeconds` になった最初のフレームで始まるため、
  実際の終了は最大1フレームぶん後ろにずれる。位相によっては `WrapMode.ClampForever` の
  legacy Animation がクリップ長を数 ms 超えて評価される——そのフレームだけ hips が NaN になり、
  モデルが1フレーム消え、SpringBone が異常な刻みを受けて髪が「上から降りてくる」ように見える

**対策**（`Runtime/Vrm/ClipEnd.cs`）: 提示中のクリップを、終端の手前（既定 1ms）より先まで
進ませない。`VrmMotionPlayer.Tick` の `Playing` / `FadeIn` / `FadeOut` の3分岐すべてで
（`ClampToClipEnd` に1箇所へ寄せてある）、`state.time` がその手前を越えていたら巻き戻して
`Animation.Sample()` で差し直す——ファイル側の重複キーは直さず、**評価が届かない範囲に
押し込める**ことで無害化する。`FadeIn` にも当てるのは、あちらの終了が壁時計だけで決まり
`state.time` を見ないため——`FadeSeconds` より短いクリップだと、ここが無いと `to` 側が
フェードの残り時間ずっと終端を越えて評価される。

★ **重複キーの検査は読み込み時に1回だけ**（`VrmaLoader.ParseAsync`）。`animations[].samplers[].input`
（時刻キー）に単調増加でない箇所があれば、ファイル名と件数を起動ログへ1回だけ警告する
（`Runtime/Vrm/VrmaKeyframes.cs`）。フェード中の毎フレーム診断（後述の `Player.log` の行とは別物）
に置くと、常駐アプリの寿命中ずっとコストが乗り続けるうえ、`FadeIn` 側は `to` クリップが
`time≈0` なので原理的に末尾の重複キーを捕まえられない——読み込み時なら1回で済み、
フェードの位相に依存しない。

`Player.log` の5行の読み方（`stall:` / `hipsJump:` / `nanPose:` / `motionEdge:` の4つは
**既定 OFF。`-stallProbe` の opt-in**——付けるのは `Player.log` の分布を取って調べるときだけ。
[#105](https://github.com/schwarz9791/chatter-agent/issues/105) に着手するときに ON にする。
`afplay` の行だけは opt-in ではなく常時出る）:

- `stall: frame=… dt=… gap=… hips=… head=…` —— `dt` か `gap` のどちらかが閾値を超えたフレーム。
  `hips=` / `head=` が小さければ姿勢（実ボーン）は連続で、飛んでいるのは SpringBone の刻みだけ
- `hipsJump: frame=… moved=…` —— 実ボーンの hips 自体が飛んだフレーム。出ていれば
  SpringBone ではなく姿勢そのもの（クロスフェード or Retarget）を疑う。**非有限（NaN /
  ±Infinity）から有限へ戻った瞬間もここに出る**（`moved=n/a→finite`）——`Vector3.Distance` は
  非有限を含むと NaN を返すので、素の距離判定では SpringBone が異常な刻みを受ける当のフレーム
  （復帰フレーム）を取り落とす
- `nanPose: frame=… hips=<nan|ok> head=<nan|ok>…` —— hips / head のワールド位置が
  非有限（`NaN` または `±Infinity`。両方とも `nan` と表示する）なフレーム。窓や閾値に関係なく、
  非有限が続く間は毎フレーム出る。実機で髪が飛んだ瞬間はこれが出ていた
- `motionEdge: frame=… age=… hips=… head=… event=…` —— モーションの遷移が起きたフレームと、
  その直後数フレームを閾値に関係なく無条件に出す。`stall:` / `hipsJump:` のどちらも出ない
  ほど小さい飛びでも、切り替え直後の hips / head の動きをここで直接見られる
- `afplay の起動に …ms かかりました（frame=…）` —— `Process.Start` の所要時間。`stall:` と
  `frame` が近ければ、ストール源をここまで絞り込める

いずれも `frame=` で突き合わせて読むこと（配線は増やしていない——afplay 側とモーション側は
互いを参照しない）。

## ★ SpringBone は `Time.deltaTime` を素通しで積分する

`FastSpringBoneService.LateUpdate`（UniVRM、実行順 11010）は `Time.deltaTime` を**クランプ無しで**
Verlet 積分に渡す。メインスレッドが詰まった直後のフレームは `deltaTime` が
`Maximum Allowed Timestep`（`TimeManager.asset`）に張り付き、理屈のうえでは髪や揺れものの
刻みが飛んで見えうる——**ただし実機で確認された髪の飛びの原因はこれではなかった**（→ 前項）。

**この機構は一度、別の形ですでに踏んでいる。** `VrmStage.LateUpdate` はロード直後の1フレームだけ
（`_framePending`）`_instance?.Runtime?.SpringBone?.RestoreInitialTransform()` を呼んでいる——
これは「読み込み中に積もった巨大な `deltaTime` で髪が吹き飛ぶのを戻す」ための手当てで、
今回と同じ現象への対処がすでに1箇所ある（→ `VrmStage.cs` の `Adopt` / `LateUpdate` のコメント）。
**ただしその手当てはロード時の1回しか効かない。** 稼働中に起きるストールには誰も手当てしていない。

★ **症状は詰まった当のフレームではなく、次のフレームに出る。** `deltaTime` が膨らむのは
詰まりが終わって次の `Update` が回ったときなので、`Player.log` を読むときは1フレーム
ずらして相関を取ること（`VrmCharacter.LateUpdate` に足したプローブは `frame=` を全行に載せている）。

★ **`dt` は頭打ちになるので、詰まりの本当の長さは `gap`（`LateUpdate` 間の
`Time.realtimeSinceStartupAsDouble` の差）で見ること。** `deltaTime` は
`Maximum Allowed Timestep` で 0.333s に丸められるが、`gap` はクランプされない生の実時間差。

★ **恒久対策（`Time.maximumDeltaTime` を下げる等）はまだ入れていない。** `stall:` 行は実機で
0.2〜1秒のものが何度も出ているが、それ自体が髪を飛ばした証拠はまだ無い（→ 前項）。対策を
入れるかは `Player.log` の分布を見てから別途判断する（#103）。

## ★ VRMA に無い指ボーンは identity（T ポーズ）になる — Retarget はモデル側の全ボーンを回す

**症状**: 腕は VRMA どおり下りているのに、指だけ真っ直ぐ伸びきって見える。同梱
`idle_loop.vrma`（22 ボーン、指を持たない）を当てたときに出る。

**原因**: `Vrm10Runtime.Process()` は毎フレーム
`Vrm10Retarget.Retarget(VrmAnimation.ControlRig, (ControlRig, ControlRig))` を呼ぶ。
`Retarget` が走査するのは**モデル側**（`sink.TPose.EnumerateBoneParentPairs()`。指30本を
含む全ボーン）で、各ボーンについて**VRMA 側**の
`INormalizedPoseProvider.GetNormalizedLocalRotation` に回転を問い合わせる。ファイルに無い
ボーンに対して UniVRM の既定実装（`InitRotationPoseProvider`）が返すのは
`Quaternion.identity`——**正規化空間での identity は VRM 1.0 の T ポーズ**、つまり指が
真っ直ぐ伸びた状態そのもの。「アニメーションが指を動かしていない」のではなく、**問い合わせ
に答えが無いので T ポーズの値がそのまま出てくる**、という仕組みの結果。

★ **`LateUpdate` で直接 ControlRig に書いてはいけない。** `Retarget` は
`Vrm10Instance.LateUpdate`（実行順 11000）の内部で ControlRig を毎フレーム上書きする
（`VrmPoseAccent` が実行順 11005 に置いてある理由と同じ罠）。書いてもその場で消される。

**手当て**: `LateUpdate` の後ろで上書きするのではなく、**Retarget が問い合わせる相手そのもの**
を差し替える。`Vrm10AnimationInstance.ControlRig` に public setter があるので、
`INormalizedPoseProvider` をラップする `FingerFallbackPoseProvider`
（`Vrm/FingerFallbackPoseProvider.cs`）を作り、`ITPoseProvider.GetWorldTransform(bone)`
が `HasValue == false`（＝ファイルにこのボーンが無い）を返すときだけ既定の丸めに差し替える
——**これは `Retarget` 自身が「ファイルにあるかどうか」を判定するのと同じ述語**なので、
判定がズレる余地が無い。挿入点は `VrmIdleAnimation.Adopt` の
`target.Runtime.VrmAnimation = vrma` の**手前**と、`VrmCharacter.UpdateProceduralIdle` の
VRMA 不在時のフォールバック経路の両方。52 ボーンの VRoid クリップ（#70 の素材）は指を
自前で持つので `HasValue == true` となり、差し替えは 0 本——**ファイル側の指があるのに
上書きして原作アニメーションを壊す**ことはない。ログ:
`[Mascot] VRMA に無い指ボーン 30 本を既定の丸めで補います`（VRoid クリップでは `0 本`）。

角度は `FingerPose` の定数（`ProximalDegrees` / `IntermediateDegrees` / `DistalDegrees`）が
権威で、**ここには数字を書かない**（実機で調整するたびに文書側が古くなる。この PR の中でも
一度ずれた）。Z 軸まわり、**右手が負・左手が正**（`IdlePose.Evaluate` の腕と同じ符号の約束）。
符号は実機のスクリーンショットで確認済み——指は掌側に丸まる。**親指は基準角 0**
（`ThumbProximalDegrees` 等。他の4指と可動軸の向きが違い、同じ Z 軸の回転をそのまま当てると
不自然に曲がりかねないため。実機で軸を確定できるまで「伸びたまま」にとどめてある）。

**補った指は静止せず、呼吸と同じ桁の周期で微動する**（`FingerPose.RelaxedCurl(bone, now)`）。
基準の丸め角に `SwayDegrees` の sin を乗せ、周期は `SwaySeconds`、指ごとに
`SwayLagPerFingerRadians` × 指番号（人差し指 0 〜 小指 3）だけ位相をずらして一枚板に見せない。

- ★ **周期は `IdleParams.Default` の呼吸と同じ値だが、意図して独立した定数。** VRMA の待機
  ループは外部データで内部の周期を読めないので、合わせられるのは「桁が近い」まで。
  **位相を合わせることは目標にしていない**
- ★ **体の姿勢（`IdlePoseSample`）とは結合しない、時間だけの揺れ。** 次に触る人が
  `sample` に混ぜたくなる箇所だが、VRMA 経路には `sample` が存在しない（VRMA が体を
  動かしている）ので、両経路で同じ関数を使える形は時間ベースしか無い
- ★ **基準角 0 のボーンは揺れない。** 揺れは「既にある丸めを揺らす」もので、揺れが丸めを
  作ってはいけない。親指の定数を 0 以外にすれば、それだけで自動的に揺れ始める
- 時計は `Time.realtimeSinceStartupAsDouble` を `FingerFallbackPoseProvider` のコンストラクタに
  注入（`Wrap` が決める。`VrmCharacter.LateUpdate` と同じ時計）。位相は `Oscillator.Phase`
  で周期に畳んでから float にする（常駐して日単位で `now` が伸びても止まらない）

★ **#70 のクロスフェードに引き継ぐ宿題。** 複数の VRMA を混ぜるとき、混ぜる**各ソース**を
このラッパーで包んでから合成すること。片方だけ指を持つクリップ同士をそのまま混ぜると、
「指が無い側は identity へ補間される」問題が形を変えて戻ってくる。

`FingerPoseTests` が純粋部分（`FingerPose.cs`）を確認する。`ChatterMascot.Tests` の
asmdef からは `ChatterMascot.Runtime` しか見えないため、値のテーブル（`FingerPose.cs`）と
差し替えの仕組み（`FingerFallbackPoseProvider.cs`、Vrm レイヤー）を分けてある。

## ★ `MToon Outline Render Feature` を追加し忘れると、エラー無しでアウトラインだけ出ない

Renderer（PC / Mobile とも）に `MToonOutlineRenderFeature` を足し忘れても、読み込みも描画も
成功する。**アウトラインだけが出ず、ログにもコンソールにも何も出ない。** 追加は Unity Editor
の GUI から（→「アウトラインは『出ているのに見えない』ことがある」は、足したうえで幅が細すぎて
見えないケースで、これとは別の失敗モード）。

## Screen Space Ambient Occlusion を切る理由

トゥーンの陰影と喧嘩するうえ、常駐アプリで常時走らせるのは無駄なコスト。

## ★ `animations/` の探索は非再帰。`.vrma` を選ばせる設定は無い

探索順の「`~/.config/chatter-agent/animations/*.vrma`」段は、**`animations/` の直下だけ**を見る。
`animations/<category>/*.vrma`（`idle` / `happy` / `angry` / `sad` / `relaxed` / `surprised`。
感情モーションと小ネタの置き場）はこの段の対象に**含まれない**——直下に `.vrma` を1本置くと、
それが同梱 `idle_loop.vrma` の代わりに待機ループとして使われる。

★★ **モデル（`.vrm`）と違って `.vrma` に対応する設定パネルの項目は無い。** モーションを選ばせる
UI を作っていないための意図的な非対称。

## ★ VRM 0.x（`com.vrmc.univrm`）は入れない

扱うモデルが 1.0 なので要らない。読み込みも `canLoadVrm0X: false` で閉じてあり、
0.x のモデルを渡したときの失敗メッセージが具体的になる（「読めません」ではなく
「1.0 ではありません」と分かる）。

## ★ `AvatarSample_A.vrm` を公開物に使わない

差し替え検証によく使う VRoid の配布モデルだが、`allowRedistribution: false` /
`modification: prohibited` / `creditNotation: required` なので、コミット・
スクリーンショットの公開・デモには使えない。差し替え検証では `-vrm` から読ませるだけにする。
`.gitignore` は `StreamingAssets/` 以外の `.vrm` を落とすが、最後は人の判断。

## ★ 感情モーションのクールダウンはなぜ2段か（#70）

発火の規則はユーザーと決めたこと（2026-09-04）: cc-mascot と同じく**文ごと**。ただし
**再生中の感情モーションには割り込まない**（最後まで見せる）代わりに、クールダウンで
連発を抑える。クールダウンは2段（`MotionParams`）——`CooldownSeconds`（既定1秒）はカテゴリを
問わない最短間隔、`SameCategoryCooldownSeconds`（既定15秒）は**同じカテゴリ**をもう一度出す
までの間隔。

★ **同じ表情の連発だけを抑え、切り替わりは待たせない。** カテゴリを区別しないと、同じ
モーションが何十回も続く区間ができる（感情の付く発話の約半分は直前と同じカテゴリになる）。
一方でクールダウンを一律に伸ばすと、happy → sad のような**表情が変わる瞬間まで潰れる**。

★ **同カテゴリの起点は「終わった時刻」ではなく「発火した時刻」。** `NotifyEnded` はカテゴリを
受け取らないので、`EmotionMotionTrigger.Update` が発火を決めた時点で記録する。感情モーションは
待機の小ネタには割り込める。`neutral` と `kind: prompt` は感情モーションを出さない。小ネタ
（`idle/`）は発話が止まってから 30〜60 秒の乱数間隔で発火し、発話の立ち下がりとモーション
終了の両方でタイマーを引き直す。

## ワンショット再生とクロスフェードで踏んだ罠（#70）

| 罠 | 症状 | 回避 |
|---|---|---|
| hips を生の位置で Lerp する | 腰が瞬間的に飛ぶ | `idle_loop.vrma` は cm スケール（hips y≈90）、VRoid 書き出しは m スケール（y≈0.98）で単位が約 100 倍違う。`Vrm10Retarget` は `source.TPose.Hips.y` で割ってスケールするので、差分を高さで正規化してから混ぜ、合成 TPose は Hips だけ `(0,1,0)` を返す（`Retarget` が source の TPose を使うのは Hips だけ。`null` を返すと `.Value` で落ちる） |
| `animation[animation.clip.name]` で state を引く | VRoid 書き出しはアニメーション名が空で見つからない | `WIN00.vrma` の `animations[0].name` は無し（`idle_loop.vrma` は `"animation"`）。UniVRM 自身と同じ `foreach (AnimationState s in animation) { break; }` で先頭を取る |
| importer は `wrapMode = Loop` 固定 | ワンショットのつもりが最終フレームで止まらずループする | `Once` は rest に戻ってしまうので `ClampForever` に上書き。`Animation.Play()` は再生中の state を巻き戻さないので `state.time = 0` を明示し、legacy Animation の更新は `LateUpdate` より前なので差した直後に `Sample()` を呼ぶ |
| 「`Speaking` の立ち上がり」を文の開始と見なす | 先読みが効くと文の切れ目で `Speaking` が `false` に落ちず、モーションが発火しない | `AfplaySpeechPlayer.PlayAsync → End → Dispatch(Played) → 次の Play → BeginSpeaking` が同じ継続で同期に走るため。`SpeakingSet.Entry.Order` を `TryGetSpeaking` の3引数版で出し、その変化で文の開始を取る |
| `ExpressionMap` を毎回 `new` する | 常駐アプリの GC 予算を削る | `Vrm10Runtime.Process()` が毎フレーム foreach するので、中身が空の `static readonly` Dictionary を1つだけ持つ |
| 設定「待機モーション」OFF の順序 | 状態がズレる | `VrmMotionPlayer.Stop()` を `_idle.Enabled = false` より**先**に呼ぶ（`Apply()` が blend を上書きするため）。`Present` は `!Enabled` で無条件 no-op、`Play` も `!Enabled` の間は開始しない |
| `Tick` を `LateUpdate` の早期 return の後ろに置く | VRMA 有効時（通常状態）は到達せず `FadeOut` が終わらないまま感情モーションが Playing に固まる | `UpdateFace` の後・`_instance == null` の前に置く |
| 寝かせたクリップを `Animation.enabled = false` で止める | **2 回目以降の再生が前回の終端から始まり、0.5 秒で待機に戻る**（実機: `time` が 3.98 のまま起き、3 回目は 6.65） | 無効化した legacy `Animation` は有効化し直しても**最初の更新まで state への操作（`time = 0` / `Rewind()` / `wrapMode`）を捨てる**。有効化直後に何を書いても効かない。寝かせるのは `Stop()`（止めて先頭へ巻き戻す契約）、起こすのは `Stop()` → `Play()` → `wrapMode = ClampForever`。止まっている `Animation` 15 本の CPU コストは測って無し（A/B と同じ条件で 23.6%） |
| 混ぜる各ソースに `FingerFallbackPoseProvider.Wrap` を掛け忘れる | 指の無いクリップ側が identity（T ポーズ）へ補間され、「指が真っ直ぐ伸びる」問題が形を変えて戻る | 混ぜる前に各ソースへ掛ける（`CrossFadeAnimation` 自身は掛けない。#88 からの宿題） |
| 設定の適用は毎回 `IdleMotion` を代入する | 同値ガード無しでは、この項目と無関係な設定変更（音量スライダー等）のたびに再生中の感情モーション・小ネタが問答無用で待機へ畳まれる | setter の先頭で `proceduralIdle == value` を確かめて同値なら何もしない（PR #91 レビュー） |
| `Pick`（母集合）と `Play`（実際に再生できる集合）を分けない | 走査はできたが読み込みに失敗した／まだプリロード中のクリップを選んでしまい、`Play` が黙って失敗する（壊れた `.vrma` があっても気づけない） | 走査結果は `Manifest`、実際に読み込めた集合は `Loaded`（`AnimationManifest.FromClips` で組み直す）に分け、`Pick` は必ず `Loaded` から（PR #91 レビュー） |

**実機確認（2026-09-04、macOS `.app`、AivisSpeech 稼働、窓 810×810）**: 配信キューに7文
（neutral / happy / happy / sad / surprised / prompt(surprised) / angry）を直接置いた結果 ——
neutral は出ない、happy → `super_delicious`、2文目の happy はクールダウンで出ない（意図どおり）、
sad → `REFLESH00`、**surprised は sad のモーション終了から5秒以内だったので出なかった**
（当時のクールダウンは5秒。その後 hook 経由の本番でも happy → `Hub_laugh01` 7.9秒の後ろで
sad と surprised が両方潰れたので、**クールダウンを1秒に縮めた**。「割り込まない」規則で
連発は十分抑えられる）、prompt は出ない、angry → `determined`。モーション中も `face:` ログで
`sad=1.00` / `aa=0.35〜0.50` が生きている（`ExpressionMap` を奪っていない）。小ネタは放置中に
`Hub_Idle03` ×2 / `Hub_Idle01` が 30〜60秒間隔で出て待機へ戻った。例外0件。`-motionProbe happy`
で `WIN00`（ジャンプ→敬礼）と `Hub_laugh01` を 0.4秒間隔のスクリーンショットで目視: 腰の飛び・
180°ずれ・Tポーズの指は無し、`WIN00` の両腕は窓の上端に収まる。

**CPU の A/B**（同条件: 外部4K 1x、窓810×810、30fps、MSAA 4x、`-serverUrl ws://127.0.0.1:9` で
無発話、`top` 9秒間隔 n=6）: A = main（#90）**28.2%**（28.4 28.3 28.4 27.7 28.0 27.8）、
B = #70 **27.3%**（26.7 26.8 27.8 28.3 27.3 27.3。途中で小ネタが1本再生された）。15本を寝かせた
ぶんの回帰は無い。

**設定パネルの「モーションを確認」**: 「待機モーション」の下に、読み込んだ全クリップを
`カテゴリ/ファイル名`（`idle/Hub_Idle01.vrma`）で並べた Select と「再生」ボタン。本番と同じ経路
（`VrmMotionPlayer.Play` → クロスフェード → 待機へ）で1本流すので、ファイル名とモーションの対応と
実際の見え方を同時に確かめられる。**選択は保存しない**（`settings.json` にも core にも書かない。
値の行き先が3つある設定の中で、これだけがそのどれでもない）。読み込み中・一覧が空・
「待機モーション」OFF はそれぞれ無効化して理由を note に出す。踏んだ罠2つ:
一度も選び直していないと `SettingsContext.MotionPreview` は空で、表示だけを先頭に倒すと
「再生」が「選べるモーションがありません」になる（表示と押下の解決を
`SettingsSchema.EffectiveMotionPreview` の1関数に寄せた）。「待機モーション」を切り替えても
自分起点なのでパネルは作り直されず、この項目の有効/無効が古いまま残る（チェックボックスは
引きずるものではないので、この1箇所だけ `Push(update: true)` で出し直す）。「再生」を押した
結果は `MotionPlayResult`（`Started` / `Disposed` / `IdleNotLoaded` / `IdleDisabled` / `NotLoaded` /
`Busy`）で返り、`SettingsSchema.MotionPlayNotice` が文言に変換する——読み込み中で押せない
（`NotLoaded`）ともう鳴っている（`Busy`）は別の文言で出る（PR #91 レビュー#5。以前は両方
「再生中です」に潰れていた）。

**目視の道具**: `open Build/ChatterMascot.app --args -serverUrl ws://127.0.0.1:9 -motionProbe happy`
で読み込み完了の3秒後に1本だけ再生する。ログは
`[Mascot] モーション: idle=5 happy=3 …（読めた 15/15 本）` と
`[Mascot] モーション開始: Emotion WIN00.vrma（happy、4.0s）` / `モーション終了`。

**残した宿題**: `idle/` の 8.8〜18.2秒が小ネタとして長いかは耳と目で判断する（Issue #70に
持ち越し）。Android の同梱マニフェスト JSON は #97（`AnimationManifest.Build` の `bundled` 引数が
口）。`__cool` / `__cute` は分類だけで設定なし。

## ワンショット再生とクロスフェードの仕組み（#70）

起動時に `animations/<category>/*.vrma`（探索順は `persistentDataPath` → `~/.config/chatter-agent` →
同梱、同名ファイルは先勝ち）を**全部プリロード**して `Animation.Stop()` で寝かせておく（`enabled = false` ではない。下の罠）。
発火したクリップは巻き戻して `Play()` + `Sample()`、`CrossFadeAnimation`（`IVrm10Animation` の自前実装。
`ControlRig` のレベルで2本を混ぜる合成レイヤー）で待機から 0.5 秒かけて `Slerp` → フェードが終わったら
クリップそのものを直に差す → `length - 0.5s` の地点で今度は待機へ 0.5 秒フェードして戻す。**発火すべきか
の判断は Runtime の純粋クラス**（`EmotionMotionTrigger` / `IdleAccentTimer`）に置き、`Vrm/` 側（`VrmMotionPlayer` /
`VrmCharacter`。VRM10 依存で EditMode テストが当たらない層）は配線だけを持つ。`Runtime.VrmAnimation` への代入は
`VrmIdleAnimation.Present` の1箇所に寄せてある。

## 素材の `.vrma` は別リポジトリで作る（#70）

#70 の感情モーションと待機の小ネタは **VRoid Studio 2.14.0 の AnimationClip** を `.vrma` にして使う。
VRoid Studio 由来なので**再配布できない** —— `.vrma` は同梱せず `~/.config/chatter-agent/animations/<category>/` にだけ置く。
抽出と変換の道具（AssetRipper の headless API を叩く `rip.py` と、Humanoid の `.anim` を `vita.vrm` に当てて
サンプリングし `.vrma` に書く Editor スクリプト）は、抽出そのものがグレーゾーンなので
**このリポジトリには置かず、private の `vroid-motion-exporter`** に切り出してある（#91）。
手順・AssetRipper の API の癖・`VrmAnimationExporter` の罠・同名クリップの扱いは、すべてそちらの README にある。

このリポジトリ側に残る事実だけ書いておく:

- 変換した `.vrma` は humanBones **52**（目・顎を除いた全部。指の muscle 40 本を含む）で、同梱
  `idle_loop.vrma` の 22 本より多い。★ **「指が真っ直ぐ伸びる問題は素材側で解決した（ControlRig 側の
  手当ては要らなかった）」は VRoid クリップに限った話。** 同梱 `idle_loop.vrma`（22 本、指を持たない）や、
  ユーザーが置く 22 本構成の `.vrma` は今も ControlRig 側の手当てが要る——詳しくは上の「VRMA に無い指ボーンは identity（T ポーズ）になる」節（#88）
- 目・顎は書き出しから外してある。**視線は #59 の `LookAt` が持つ**ので、VRMA に目を書くと奪い合う。
  素材を別の道具で作るときも同じ除外にすること（除外の一覧は exporter 側と
  `FingerFallbackPoseProvider` の両方にあり、片側だけ直すとズレる）
- importer 側は `clip.wrapMode = WrapMode.Loop` 固定（`AnimationImporterUtil.cs`）。`-vrma` で1本再生すると
  ワンショットもループする。目視確認ではそれでよく、ワンショット再生（上の「ワンショット再生とクロスフェードの仕組み」）では `ClampForever` に上書きする
- 同名のクリップは `__<pathID>` 付きで出てくる（`Hub_Idle01〜04` は 2 組ある）。設定パネルの
  「モーションを確認」で見比べて、残す方だけカテゴリに置く

