# Unity プロジェクトで踏んだこと — 環境・ビルド・テスト

基本設計・構成・コマンドは [`../mascot.md`](../mascot.md)。ここは**実装で踏んだことだけ**を残す。

## ★ Unity の既定はフレームレート無制限。常駐アプリでは必ず上限を入れる

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

★ **既定の 30fps は XR が起動していないときの上限。** XR（Full Space）ではランタイムが
フレームペーシングを握り、`Application.targetFrameRate` は効かない（→ [`mascot-android-xr.md`](./mascot-android-xr.md)「XR（Full Space）」）。
`MascotRunner` の Inspector で変えられる。

★ **「リップシンクが入ったら 30fps で足りるか見直す」という宿題は #58 で閉じた。
結論は 30fps 据え置き。** → [`mascot-speech.md`](./mascot-speech.md)「30fps で口が足りるかの決着（#58）」

★ **#88 で表示側にフレームレート上限を選ぶ設定を足した。** `settings.json` の
`display.frameRate`（`30` か `60` のみ。既定 `SettingsMapping.DefaultFrameRate = 30`）を
設定パネル「モーション」の「フレームレート」で切り替えると、
`StatusItemBridge.ApplySettingsToScene` → `MascotRunner.SetTargetFrameRate` →
`FrameRateBudget.SetBaseline` の経路で反映される。`Application.targetFrameRate` へ直接
書かないのは、VRM 読み込み中の一時的な引き上げ（`FrameRateBudget.Boost`）を上書きで
消さないため。選択肢に無い値（`settings.json` を手で壊した場合など）は**クランプではなく
既定へフォールバック**（警告ログつき）。**反映は許可リストで絞ったプラットフォームだけ**
（`SettingsMapping.AppliesFrameRate`。デスクトップの Player / Editor が対象）——Android は
`settings.json` の `display.frameRate` を読んでも反映しない（→ [`mascot-android-xr.md`](./mascot-android-xr.md)「LAN 接続（#98）」の
「Android で効くキーと効かないキー」）。

## #59 時点の実測: フレームレート上限ありでの常駐 CPU

冒頭の「Cube 1個で無制限なら CPU 261%」と対比できる値。**VRM 表示 + VRMA（待機モーション）+
spring bone + 毎フレームの手続き計算（呼吸・重心移動・視線）を全部載せた状態**で、
`targetFrameRate = 30` のとき **CPU 13.2%**（実測 n=5、9秒間隔、ウィンドウ 300x480、
視線の中立とフレーミングを直した後）。

★ **この値は「フレームレート上限が効いている」前提の値。** 上限を外したときにどこまで
増えるかは測っていない。

窓の既定サイズの変遷（#70 の VRoid モーション対応を含む）は
[`mascot-desktop.md`](./mascot-desktop.md)「ウィンドウの大きさは3箇所で決まる」を参照。
`docs/knowledge/` の他の節にある旧サイズの実測値はそのまま残してある。

EditMode テストの件数は書かない（→「★ テストの件数を文書に書かない」）。

## #88 時点の実測: 窓の拡大・アンチエイリアス・60fps の CPU コスト

同じ条件（アイドル・`idle_loop.vrma`・無発話・外部 4K ディスプレイ scale 1x・Apple M1 Max・
`top` の CPU% を9秒間隔で n=6・中央値。上の「#59 時点の実測」と同じ方法）で、
ウィンドウ 540x540（#88 の新既定）を基準に MSAA off/4x × 30/60fps の4通りを測った:

| | MSAA off | MSAA 4x |
|---|---|---|
| 30 fps | 14.6%（11.8 14.4 14.9 14.7 14.1 14.7） | 16.8%（14.0 16.8 16.7 17.2 16.7 16.8） |
| 60 fps | 22.8%（19.6 22.6 22.4 23.0 23.1 23.3） | 27.6%（26.6 31.2 29.0 26.7 27.7 27.4） |

旧記録（300x480 / 30fps / MSAA off）の **13.2%** と比べると:

- **窓の拡大**（300x480 → 540x540）だけで **+1.4pt**
- **MSAA 4x** は 30fps で **+2.2pt**、60fps で **+4.8pt**
- **60fps** は MSAA off で **+8.2pt**、MSAA 4x で **+10.8pt**（30fps 比で約 **1.6倍**）

★ **この 1.6倍を、#58（リップシンク）で測った 2.1倍と単純比較しないこと。** 「リップシンクが
入ったら 30fps で足りるか見直す」の宿題は #58 で 30fps 据え置きと決着したが、その判断材料
だった倍率とはリップシンクの有無・窓の大きさ・MSAA の有無のすべてが条件として違う。
「軽くなった」と読める数字ではない。

★ **既定のフレームレートは 30fps のまま。** ここまでの実測は「決める材料」であって
「決めた結果」ではない——#88 では数字を残すところまでで、既定を上げるかどうかは別途判断する。

## ★ MCP 経由のビルドは、モーダルダイアログが出た瞬間に沈黙する

`Unity_RunCommand`（unity-mcp）から `BuildPipeline.BuildPlayer` を呼ぶと、
**保存確認ダイアログが出た瞬間に応答が返らなくなる**。人がダイアログを閉じるまで、
呼び出し側からは「ハングした」としか見えない。

症状の見分けがつかないのが厄介で、実際に **30分気づけなかった**。切り分けに使えたのは:

- シェーダーコンパイラのプロセスが12個いるのに **CPU が全部 0.0%**
- `Temp/` の更新時刻が止まっている
- **`Logs/Editor.log` にビルドの行が1行も出ていない**（成功したビルドは必ず
  `Building Player` 以降を書く）

**ビルドとテストは `-batchmode` で回す**（→ `chatter-mascot/scripts/`）。
batchmode はダイアログを出さないので、この失敗の仕方をしない。

★ **Editor を開いたままだと batchmode は失敗する。** Unity はプロジェクトを排他ロックする
（`Temp/UnityLockfile`）。スクリプト側でも起動前に検査している。

★ **Editor 経由でしかできないこと**（シーン編集、パッケージ解決、設定変更）は MCP で行う。
そのときは **`AssetDatabase.SaveAssets()` とシーン保存を先に済ませる**。ダイアログの芽を潰しておく。

## ★ シーンの YAML に無い `[SerializeField]` は 0 にならない（が、揃えておくこと）

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

## ★ シェーダーストリッピングは「読めるのに真っ黒／ピンク」で例外を出さない

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

## ★ asmdef の参照は推移しない（4回踏んだ）

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

## ★ `Kirurobo.UniWindowController` はデスクトップ限定。Runtime から参照しない

実物の `includePlatforms` は
`["Editor", "macOSStandalone", "WindowsStandalone32", "WindowsStandalone64"]` で
**`Android` を含まない**。`includePlatforms` が非空のときは**ホワイトリスト**として扱われるので、
`ChatterMascot.Runtime`（全プラットフォーム）から参照すると Android ビルドで壊れる。

`ChatterMascot.Desktop` に隔離してある。★ **`includePlatforms` は4つを一字一句写すこと。**
部分集合にすると Windows Standalone ビルドでコンパイルエラーになる。

依存の向きは `Editor → Desktop → Vrm → Runtime`。

## ★ デスクトップ限定アセンブリの `MonoBehaviour` をシーンに置かない

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
置いてあるので、そのままでは **Android ビルドで missing script が出る**。
[#97](https://github.com/schwarz9791/chatter-agent/issues/97) の `AndroidSceneStripper` が
ビルド時に asmdef の規則で剥がす（→ [`mascot-android-xr.md`](./mascot-android-xr.md)「プラットフォームを絞る」）。

## ★ `scripts/run.sh` の grep を通らないログは存在しないのと同じ

`run.sh` は出力を
`grep -E "^\[Fixups\]|^\[Build\]|^\[VrmProbe\]|error CS|…"` で絞る。
**ここに無いプレフィックスは `LogError` でも画面に出ない。**

★ **複数行のログは2行目以降が丸ごと消える。** `VrmProbe` の最初の実装がこれで、
`[VrmProbe]` の1行だけ出て**中身が空に見えた**。行ごとにプレフィックスを付けること
（`text.Replace("\n", "\n[VrmProbe] ")`）。

★ **パッケージ解決の失敗も拾わない。** UniVRM を `manifest.json` に足した直後の初回解決は
git 取得になるが、`Failed to resolve` / `Cannot perform upm operation` は既定のパターンに
無かった。足してある。

## アンチエイリアス — 今まで何も効いていなかった（#88）

**#88 まで、ジャギーを抑える設定は何ひとつ有効になっていなかった。** `PC_RPAsset.asset` は
`m_MSAA: 1`（オフ）、カメラに `UniversalAdditionalCameraData` を明示的に持たせていないので
URP の既定（`antialiasing = None`、`renderPostProcessing = false`）のまま、
`m_RenderScale` も等倍の `1`。

★ **`QualitySettings.antiAliasing` を上げても効かない。** Unity 6 の URP では**この値は
無視される**——**権威は URP Asset の `m_MSAA`**（Inspector の `Anti Aliasing (MSAA)`）。
`PC_RPAsset.asset` に `m_MSAA: 4` と書いて 4x MSAA を有効にした。

★ **MSAA はポストプロセスのスタックを通らないので、透過の罠（→ [`mascot-desktop.md`](./mascot-desktop.md)「Unity 6 の URP で
透過しないのは `Supports HDR` のせい」）に触れない。** `Supports HDR` はオフのまま、カメラの
Post Processing も無効のまま——**カメラの post-processing を有効にした瞬間に透過が壊れる**
という既存の制約はそのまま活きている。実機（macOS）で透過が保たれたまま輪郭が目に見えて
滑らかになり、Metal のバリデーションエラーも出ないことを確認した。

★ **もし MSAA が将来どこかで透過を壊したら、次に試す順は** `m_RenderScale: 1.5`
（スーパーサンプリング）→ `UniversalAdditionalCameraData` 経由の FXAA（最後の手段。
ポストプロセス扱いになるぶん透過との相性リスクが上がる）。

`Mobile_RPAsset`（Android / XR）は触っていない（→ #97）。

★ **効果が実機で目立ったのは、常用ディスプレイが 4K パネルの等倍（1x）運用のため。**
`3840x2130 pt = px` で1ピクセル=1ポイントなので、Retina（2x）でスケーリングされる場合より
ジャギーがそのまま見える。

**CPU コストは上の「#88 時点の実測」に含めてある**——MSAA 4x は 30fps で **+2.2pt**、
60fps で **+4.8pt**。

## ★ 起動引数の真偽値フラグは `CommandLine.Flag` で読む（`Argument` ではない）

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

## ★ テストアセンブリの `overrideReferences` に注意

`ChatterMascot.Tests.asmdef` は `overrideReferences: true` なので、
**`precompiledReferences` に挙げた DLL しか参照できない**。テストが Newtonsoft を直接使うなら
`Newtonsoft.Json.dll` を足す（`nunit.framework.dll` だけだとコンパイルが通らない）。

## ★ `build.sh` は終了コードを捨てないこと

`| grep ... || true` にすると `BuildScript` の `EditorApplication.Exit(1)` が消え、
判定が「成果物があるか」だけになる。**一度でも成功していれば古い `.app` が残っている**ので、
コンパイルエラーでも「できました」と言って exit 0 する —— 直っていないバイナリを
直ったつもりで起動することになる。`test.sh` と同じ `PIPESTATUS` の形に揃える。

`$OUTPUT` が絶対パスのとき（`BuildScript.cs` の `Path.IsPathRooted` が許容する）に
`$PROJECT_PATH/` を前置しないことも要る。

## ★ `test.sh` はコンパイル失敗時に前回の結果を表示する

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
- [`../mascot.md`](../mascot.md) も `CLAUDE.md` も「**件数は `./scripts/test.sh` の `total=` を
  見る**」と案内している。つまり**このリポジトリが公式に案内している確認方法が、
  コンパイル失敗を成功として表示する**

直し方は Unity を呼ぶ前に古い XML を消す（`rm -f "$RESULTS"`）。
消せば集計側の `if [ -f "$RESULTS" ]` が偽になり、
「XML が書かれなかった＝走る前に落ちた」が正しく表現される。

★ **`total=` だけを見て緑と判断しないこと。** 出力の上のほう、`error CS` と
`Aborting batchmode` を先に見る。

## ★ ビルド対象シーンは `EditorBuildSettings` にも入れる

`scripts/build.sh` は `-buildScene` を明示で渡すので通るが、
**Unity の `File > Build Settings > Build` や `-buildScene` を渡さない経路（#54 の CI）は
`EditorBuildSettings` を見る**。テンプレート既定の `SampleScene` のままだと、
そこには `MascotRunner` も `UniWindowController` も `EventSystem` も無いので、
出来上がる `.app` は**不透明なウィンドウが出て、何にも繋がらず、エラーも出さない**。

`SceneFixups.EnsureBuildScenes()` が本番シーン1本に揃える。

## ★ ビルド済みアプリが読む `StreamingAssets` は `.app` の中のコピー

`Assets/StreamingAssets/` のファイルを動かしても、**ビルド済みアプリには効かない**。
アプリが読むのは `Build/ChatterMascot.app/Contents/Resources/Data/StreamingAssets/` に
コピーされたもの（`Player.log` に採用したパスが出る）。

★ **同梱ファイルを外して手続き的フォールバックを試すときは、`.app` の中を触るか
再ビルドすること。** リポジトリ側の `Assets/StreamingAssets/idle_loop.vrma` を退避しても、
既にビルドされた `.app` はコピー済みのファイルをそのまま読み続ける。

## Git-LFS 依存は #56 で復活した

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

## Sentis（`com.unity.ai.inference`）はテンプレート同梱だが要らない

3D テンプレートに入っているが、**誰も依存していない**（unity-mcp が使う
`com.unity.ai.assistant` も依存していない）。残すとビルドのたびに**膨大なシェーダー警告**が出て、
コンパイル時間も伸びる。外してある。

## ★★ `HideFlags.HideAndDontSave` のオブジェクトは `FindFirstObjectByType` から見えない

**症状**: 「キャラクターの位置をリセット」がその場で効かず、アプリを再起動して初めて反映される。

`WindowGeometry` の `Keeper` は `HideFlags.HideAndDontSave` を持つ GameObject に載っている。
Unity のドキュメントに明記されているとおり、`Object.FindFirstObjectByType` は
**`HideFlags.DontSave` を持つオブジェクトを返さない**。`WindowGeometry.Reset()` は常に
「見つからない」枝に落ち、`window.json` を消すだけで終わっていた（ログにも
`ウィンドウの管理が動いていないので、位置のリセットは次の起動から効きます` が出ていた）。

**手当て**: `StatusItemBridge` と同じ形に揃えて **static フィールドで保持**する
（`Start` で代入、`OnDestroy` で解除）。`FindFirstObjectByType` を使わない。

## ★★ `.bundle` が無い状態で Unity を起動すると `.bundle.meta` が壊れる（#93 で踏んだ）

`Assets/Plugins/macOS/ChatterMascotNative.bundle` は git に入れていない（→ `NativePluginSettings`）ので、
**新規クローンやクリーンなワークツリーには `.meta` しか無い**。この状態で Unity を起動すると、
Unity は「`.meta` はあるがアセットが無い」と見て**孤児として `.meta` を捨てる**。
あとから `./scripts/build-native.sh` が `.bundle` を作ると、**新しい GUID で再インポートされる**。

実測（2026-09-06 / #93）: `./scripts/run.sh …IconSettings.FixAll` を単独で先に走らせたところ、
`ChatterMascotNative.bundle.meta` から **`PluginImporter` の `platformData` ごと設定が消え、
`guid` が別の値に変わっていた**（残っていたのは `fileFormatVersion` と `guid` の 2 行だけ）。**`.gitignore` が「`.meta` は追跡する
（GUID が動くと、参照している側が壊れる）」と書いている、まさにその事故。**

★ **`build.sh` はこの穴を踏まない。** Unity より先に `build-native.sh` を呼ぶため。
踏むのは **`test.sh` と `run.sh` を、バンドルが無い状態で走らせたとき**
（→ [#95](https://github.com/schwarz9791/chatter-agent/issues/95)。直すのは別の PR）。
[#97](https://github.com/schwarz9791/chatter-agent/issues/97) の新規ワークツリーでも同じ形で踏んだ
（`build-android.sh` も `build-native.sh` を呼ばないので、Android だけ触る場合も先に作っておくこと）。

★★ **ビルドは通ってしまう。** `.app` の `Contents/PlugIns/` にはバンドルが入るし、
EditMode テストも全部通る。**気づけるのは `git diff` だけ** —— batchmode で Unity を回したら
`.meta` の差分を必ず見ること。

直し方は 2 手（`.bundle` が**ある**状態で行うこと）:

```bash
git checkout -- chatter-mascot/Assets/Plugins/macOS/ChatterMascotNative.bundle.meta
./scripts/run.sh ChatterMascot.EditorTools.NativePluginSettings.FixAll
```

## アイコン生成は**元 PNG**を読む —— `textureCompression` は効かない

`Assets/ChatterMascot/Icon/AppIcon.png` の `.meta` は**既定のまま**
（`textureCompression: 1` = 圧縮あり / `isReadable: 0` / `maxTextureSize: 2048`）だが、
**生成された 1024 のアイコンにブロックノイズが無い**。DXT/BC を通っていれば 4x4 の
アーティファクトが出るので、**ビルド時のアイコン生成は `Texture2D` のピクセルではなく
ソース画像を読んでいる**。→ **アイコンのためにインポート設定を変える必要は無い。**

★ **`GetIconSizes` が返すサイズと、`.icns` に入るサイズは一致しない。**
`.icns` に枠が無いサイズ（実測では 64）は落ちる。要求される枚数は Unity のバージョンで
変わるので**値を覚えないこと** —— `IconSettings.FixAll` が走るたびに `[Icon]` のログへ出す。

## 素材と最適化（#93）

| | |
|---|---|
| 原本 | Apple の Icon Composer（`~/Pictures/ChatterMascot/ChatterMascot.icon`）。**リポジトリには入れない** |
| 使ったのは | **macOS の書き出し**（1024x1024）。`-iOS-` の方は使わない —— **macOS 版は周囲にインセットが入る**（Finder で他のアイコンと大きさを揃えるための余白）。並べると一目で違う |
| 最適化 | pngquant（`--quality=95-100 --speed 1 --strip`）を通した。**桁で縮む** |

★ **pngquant は減色する。** 通したあとの `IHDR` は `depth=8 colortype=3`（パレット形式）で
`PLTE` と `tRNS` を持つ —— **256 色のパレットに落ち、アルファも量子化されている**。
「Icon Composer の書き出しは 16 bit/sample で `.icns` 側は 8bit だから、色深度は捨ててよい」
までは正しいが、**pngquant がやっているのはそれだけではない**。

★★ **採用は「測って確かめた」ではなく「見て許容した」。** グラデーションを等倍で切り出して
見比べたが、**見たのは不透明な内側で、そこはアルファの量子化が効かない唯一の領域**だった。
影響が出るとしたら**角丸のアンチエイリアスと macOS 版のインセット影の縁** —— アルファが
連続値から量子化される場所 —— で、そこは見ていない。実機（Finder）で問題が無かったので
採った、が正確なところ。

★ **次にこのファイルを最適化するときは縁を見ること。** 不透明な内側を見比べても、
パレット化とアルファの量子化がシルエットの縁にどう出るかは分からない。

★★ **`.icon` はビルドに入れられない。** あれはディレクトリバンドルの**ソース形式**で、
`icon.json` にレイヤー構成・`automatic-gradient` の背景・glass・shadow を持ち、
ラスタ画像ではない。Unity の Icon が受けるのは `Texture2D` だけ。`iconutil --convert icns` も
通らない（実測: `Invalid Iconset.`。あれが読むのは `.iconset` だけ）。コンパイルできるのは
Xcode の `actool` で、出力は `Assets.car`。

★ **Liquid Glass（macOS 26 の4外観・鏡面反射・Dock のパララックス）は入れていない。**
効かせるには `.icon` を `actool` でコンパイルして `Contents/Resources/Assets.car` を置き、
`Info.plist` に `CFBundleIconName` を書く（`MacPostBuild` に足せる）。**やらない理由は
`LSUIElement`** —— Dock にも ⌘Tab にも出ないので、Liquid Glass の見せ場が効く場所が
このアプリにほぼ無い。Unity のビルドに Xcode のツールチェーンを挟む見返りが小さい。
**Dock に出す日が来たら再検討する。**

★ **次期 macOS で iOS と macOS の geometry が共通になったら書き出し直しが要る。**
PNG は静止画なので OS 側では吸収されない。差し替えたら
`./scripts/run.sh ChatterMascot.EditorTools.IconSettings.FixAll` を走らせて
`ProjectSettings.asset` の差分をコミットすること。

★ **アイコンの確認は Dock ではできない**（`LSUIElement`。⌘Tab にも出ない）。
効くのは **Finder / Spotlight / ⌘I / 通知 / 設定パネル**。

## ★ Unity CLI

`unity` コマンド（[Unity CLI](https://unity.com/ja/blog/meet-the-unity-cli)）が
手元に入っている。実体は `/Users/schwarz/.unity/bin/unity`、実測 **`1.0.0-beta.8`**（`unity --version`）。
`unity doctor` は `auth.loggedIn true` と、インストール済みの Editor を `editor.0` / `editor.1`
（現在は `6000.3.14f1 arm64` と `6000.5.8f1 arm64` の2本）として認識する。`unity editors` は
両方を「Installed」列にパスつきで拾い、どちらも Android / Android SDK & NDK Tools / OpenJDK / Web の
モジュールを持つ。導入は公式の

```bash
curl -fsSL https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.sh | UNITY_CLI_CHANNEL=beta bash
```

`scripts/test.sh` / `scripts/build.sh` / `scripts/run.sh` / `scripts/build-android.sh` は
Unity.app の直叩きから Unity CLI へ寄せた。呼び出し口（`./scripts/test.sh` など）は変わらない。
`unity.sh` は薄い共通部分として残っている。

| やること | 呼び出し口 | 内部で実行する Unity CLI |
|---|---|---|
| EditMode テスト | `./scripts/test.sh` | `unity test --mode EditMode --output Logs/test-results.xml` |
| macOS ビルド | `./scripts/build.sh` | `unity build --execute-method ChatterMascot.EditorTools.BuildScript.BuildMacOS --output-path <絶対パス>` |
| Android ビルド | `./scripts/build-android.sh` | `unity build --target Android --execute-method ChatterMascot.EditorTools.BuildScript.BuildAndroid --output-path <絶対パス>` |
| 任意の Editor メソッド実行 | `./scripts/run.sh <Method>` | `unity run -- -executeMethod <Method> -logFile -`（**`-quit` は渡さない**。**`--command` ではない** — そちらは事前登録が要る `unity pipeline install` 前提の別機能） |
| Editor の一覧 / インストール | 手動（Unity Hub） | `unity editors` / `unity install` / `unity install-modules` |
| 環境の診断 | 無し（`Player.log` を読むだけ） | `unity doctor` |

★ **`-quit` は `unity run` が自分で付ける予約フラグ。** `--` の後に `-quit` / `-batchmode` /
`-projectPath` を渡すと、Unity を起動する前に
`Forwarded argument '-quit' conflicts with a reserved Unity flag managed by this command.`
で弾かれる。`unity test` は逆に `-quit` を渡さない（`-runTests` は Test Runner が自分で終了するため）。
Unity 本体の作り（「`-runTests` に `-quit` を付けない」「`-executeMethod` には `-quit` が要る」）を
取り違える余地は、**CLI 側が引き受けて無くした。**

寄せるにあたって失ってはいけなかった6点は、それぞれこう決着した:

1. **`-quit` の要否** → CLI が引き受けた（上の説明のとおり）
2. **`build.sh` の `trap restore_audio_manager EXIT INT TERM` による
   `AudioManager.asset` の復元** → **残した。** `unity build` は `--execute-method` で
   `BuildScript.BuildMacOS` を呼ぶだけで肩代わりしないので、3段構えは変わらない
3. **`PIPESTATUS` で終了コードを捨てないこと** → `test.sh` はパイプを挟まなくなったので
   不要になった。`build.sh` / `run.sh` は grep を挟むので引き続き必要
4. **NUnit XML を python3 で集計して `total= passed= failed=` を出すこと** → **残した。**
   `unity test` はコンソールに集計を出さず、stdout に流れるのは素の Editor ログだけ
5. **`unity.sh` の `pgrep -f "Unity.app/Contents/MacOS/Unity.*${PROJECT_PATH}"` による
   二重起動検出** → **残した。** `unity` CLI 経由でも
   `Unity.app/Contents/MacOS/Unity ... -projectPath <PROJECT_PATH>` の形で起動するので検出は効く
   （CLI 自身が同等の検出を持つかは確かめていない）
6. **grep フィルタに掛からないログ** → `build.sh` は `Logs/build-macos.log`、
   `build-android.sh` は `Logs/build-android.log`、`run.sh` は
   `Logs/run.log` に全文が残るようにした。`unity build` は `--log-file` へ全文を書きつつ
   stdout にも流すが、`unity run` に `--log-file` は無いので `-logFile -` を渡して `tee` で落とす

注意点:

- `unity test` の終了コードは **0=全部通った / 8=テストが失敗した / 6=走り切らなかった**
  （コンパイルエラー・ライセンス不可・クラッシュ・`--timeout`）。この分割があるので、CI は
  「インフラの失敗だけ再試行する」を終了コードだけで書ける
- **`unity run` / `unity build` は Editor の終了コードをそのまま返さず、失敗を `6` に畳む。**
  非0であることは保たれるので `run.sh` / `build.sh` / `build-android.sh` の判定は変わらないが、
  `EditorApplication.Exit(n)` の `n` は届かない。Editor が起動する前に CLI が弾いた失敗も `6` なので、
  **終了コードでこの2つを見分けられない**（見分けるのは `--log-file` が空かどうか）
- `--` の後の `-nographics` / `-logFile` / `-buildTarget` / `-executeMethod` はそのまま
  Unity へ転送される
- **`unity build` は `-batchmode -nographics -quit` を自分で付ける。** `unity test` と
  `unity run` は `-nographics` を付けないので、必要なら `--` の後で明示する
- **Editor が起動する前に CLI が弾いた失敗**（target 不正・Editor が未インストール・認証切れ）は
  `Error: …` として stderr に出るだけで、`--log-file` のログには入らない。grep で絞るなら
  `^Error:` を拾わないと、終了コードだけが残って理由が画面から消える
- **`unity build` は `--log-file` のログを画面へも流す。** 走らせる前に空にしないと
  前回のビルドの行が今回の出力より先に流れる（`build.sh` がやっている。最終的なファイルの
  中身は1回分だったので Unity 側が開くときに切り詰めている可能性が高いが、確かめていない）
- `unity` は `ProjectSettings/ProjectVersion.txt` から Editor を解決し、Hub に登録された実体
  （`/Applications/Unity/Hub/Editor/<版>-arm64/Unity.app`）を選ぶ
- **`unity build` の未コミット変更ガードは既定（`--versioning-strategy none`）では走らない**
  ので `--allow-dirty-build` は要らない
- **`unity build` の `--output-path` は `--execute-method` と併用すると CLI 自身は解決しない**
  （`-buildOutput` としてそのまま転送され、相対パスを解くかどうかは呼び出し先のメソッド次第。
  `BuildScript.cs` はプロジェクトルート基準で解く）。解決がどこで行われるかに寄りかからないよう、
  呼び出し側で絶対パスにしておく
- ★ **`unity pipeline install` は入れない。** `test` / `build` / `run` はいずれも pipeline 不要で
  動く。`unity command` / `unity status` はこれが要るが、`Packages/manifest.json` に beta の
  依存を1本増やすのに見合う用途が今は無い
- ★ **CLI のバージョンは固定しない。** CI に Unity を起動するジョブがまだ無く（#54 が未着手）、
  固定する先が存在しない。CLI は beta なので、壊れたら `unity --version` を見て対応表を
  確かめ直す運用にする
- ★ `unity editors` が `6000.3.14f1` に対して `6000.3.24f1` へのアップグレードを示唆してくるが、
  **プロジェクトは `6000.3.14f1` 固定**（`ProjectSettings/ProjectVersion.txt` が唯一の版の書き場所。
  [#97](https://github.com/schwarz9791/chatter-agent/issues/97) で `6000.5.8f1` から切り替えた）。
  `UNITY_VERSION` 環境変数は `test.sh` / `build.sh` / `run.sh` / `build-android.sh` の4本すべてで
  Unity CLI の `--editor-version` として渡る。渡さなければ CLI が `ProjectVersion.txt` の版で走る

★ **`unity build` の Android 専用フラグ（`--android-export-type` / `--android-keystore-*` /
`--android-target-sdk-version` / `--android-symbol-type` / `--android-version-code`）は使わない。**
`--execute-method` と併用すると CLI は `BuildPlayerOptions` を握らないので、これらが honor される
保証が無い。出力形式の書き手を2つに持つと、honor されなかったときに黙って壊れた成果物ができる。
targetSdk / symbol / versionCode は `ProjectSettings` が持ち、署名は値が argv に出るのでここでは
行わない。

`build-native.sh` / `configure-android.sh` / `run-android.sh` は Unity を起動しないので、
Unity CLI へ寄せる対象ではない。

## Unity の版を切り替えたときに踏んだこと（#97: 6000.5.8f1 → 6000.3.14f1）

[#99](https://github.com/schwarz9791/chatter-agent/issues/99) で入れる Android XR パッケージ
（`com.google.xr.extensions` 1.3.1）が `package.json` で `"unity": "6000.3"` を宣言していて、
6000.5 では SPATIAL 機能の版が衝突する。**XR パッケージを1つも足す前に** Editor の版を落とした。

★★ **`com.unity.modules.physicscore2d` は 6000.5 で新設されたビルトインモジュール。**
`Packages/manifest.json` に残したまま 6000.3 を起動するとパッケージ解決が
`Project has invalid dependencies: com.unity.modules.physicscore2d ... cannot be found` で失敗するが、
**`scripts/run.sh` は何も出さずに exit 1 した**（grep にこの文言が無かった。
→「`scripts/run.sh` の grep を通らないログは存在しないのと同じ」）。`run.sh` / `build.sh` の grep に
`Project has invalid dependencies|An error occurred while resolving packages` を足してある。
**メジャー / マイナー版を跨ぐときは、移行先の版で動いているプロジェクトと `manifest.json` を diff して、
そこに無いモジュールを外すこと。** ビルトインパッケージ（URP / ugui / test-framework、推移的に
collections / burst / mathematics / shadergraph / render-pipelines.core も）は Editor に付いて
動くので個別の版数を追う必要はない —— 現在の `Packages/manifest.json` に `physicscore2d` は無く、
この移行は完了済み。

- `ProjectSettings.asset` の `serializedVersion` 29 → 28。ほかの差分はスキーマだけ。
  `AudioManager.asset` は不変（→ [`mascot-speech.md`](./mascot-speech.md)「プロジェクト設定まわりで踏んだこと」）
- macOS の透過は切り替え後にビルドして目視で再確認した
  （→ [`mascot-desktop.md`](./mascot-desktop.md)「Unity 6 の URP で透過しないのは `Supports HDR` のせい」）

★★ **`.bundle.meta` が壊れる罠は #93 で踏んだものを #97 でまた踏んだ**
（→ 上の「`.bundle` が無い状態で Unity を起動すると `.bundle.meta` が壊れる」）。

★★ **シェーダーのコンパイル中に Unity を殺すと `Library/ShaderCache` が壊れる。** 症状は
ビルドエラーではなく、次のビルドで **MToon10 の本体パスだけが描かれず、アウトラインの
暗いシルエットだけが出る**（ログには何も出ない）。`Library/ShaderCache` を消して作り直せば戻る。
バリアント削減を切るなど全バリアントの再コンパイルを伴う変更は、途中で止めないこと。

★ **失敗したビルドは `Assets/Resources/`（と `.meta`）を残す。**
`com.unity.test-framework.performance` の `TestRunBuilder`（`IPreprocessBuildWithReport`）が
毎 `BuildPlayer` の前に作り `OnPostprocessBuild` で消すが、ビルドが throw すると後始末が走らない。
我々のものではない。**消すだけでよく、コミットしないこと。**

## ★ `ChatterMascot.Runtime` を描画に依存しない層のまま保つ

契約・状態機械・探索順・画角の計算が **EditMode だけでテストできている**のは、
この層が `UnityEngine` の描画（UniVRM 型を含む）に依存していないから。Runtime の public API に
UniVRM 型が漏れると、`ChatterMascot.Tests.asmdef`（`overrideReferences: true`）から届かなくなり、
テストごと落ちる。

## ★ テストの件数を文書に書かない

テストを足すたびにずれるうえ、`grep -cE '\[Test\]'` で数えると `[UnityTest]` を取りこぼして
**別の誤った数字**が出る。実数が要るときは `./scripts/test.sh` の `total=`（テストランナー自身の数）を見る。

## `IconSettings.FixAll` は毎回のセットアップでは要らない

結果（`m_BuildTargetIcons`）は `ProjectSettings.asset` にコミット済みなので、新規クローンでは
何もしなくてよい —— `NativePluginSettings.FixAll`（新規クローンのたびに要る）とはここが違う。
再実行が要るのはアイコン画像（`Assets/ChatterMascot/Icon/AppIcon.png`）を差し替えたときだけ。

