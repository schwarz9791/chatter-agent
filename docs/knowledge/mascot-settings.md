# 設定の読み込みと右クリックで踏んだこと

基本設計・構成・コマンドは [`../mascot.md`](../mascot.md)。ここは**実装で踏んだことだけ**を残す。
設定の UI は ChatterAgent が持つ（→ [`../agent.md`](../agent.md)）。マスコットは `settings.json` を読み、
右クリックで ChatterAgent を呼ぶだけ。

## ★★ 常駐マスコットの右クリックは `IPointerClickHandler` では成立しない

**症状**: キャラを右クリックしても ChatterAgent の設定窓が出ない。左クリックを1回挟むと開くようになる
——「たまに効く」といういちばん悪い壊れ方をする。

**原因は2段ある。**

**① Input System の `Mouse` デバイスが、非アクティブの間は無効化される。**
`InputSettings.backgroundBehavior` の既定は `ResetAndDisableNonBackgroundDevices` で、
フォーカスを失うと `Mouse`（`canRunInBackground == false`）が
`TemporaryWhilePlayerIsInBackground` で無効になる。`runInBackground: 1` は
イベントストリームの手前の関門しか通さず、ここは塞げない。

```csharp
// ContextClickHandles.AllowInputWithoutFocus()
InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
```

★ **`Assets/` に `InputSettings` アセットを置くより実行時に立てる方がよい。** アセットは
`EditorBuildSettings` の `com.unity.input.settings` に登録が要り、**Android（#97）にも効いてしまう**。
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
ChatterAgentLauncher.OpenSettings();
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

## ★★ `⌃ + 左クリック`は常駐マスコットでは成立しない

macOS の慣習だが、**二重に成立しない**（実測）:

1. **修飾キーが読めない。** キーイベントは前面のアプリにしか配送されないので、
   押していても `Keyboard.current.leftCtrlKey.isPressed` は `false` のまま。
   `backgroundBehavior = IgnoreFocus` はデバイスを生かすだけで、**OS の配送先は変えられない**
2. **非アクティブなアプリへの最初の左クリックはアクティブ化に食われる。**
   右クリックはアクティブ化しないのでそのまま届く

つまり「2回クリックが要り、しかも修飾キーが効かない」ものになる。**押しても何も起きない操作を
残さない**（→ `SettingsSchema` の同じ方針）。副ボタンを出せない環境の逃げ道は
**ChatterAgent のメニューバーの「設定…」**（と、Finder などからの開き直し）で足りている。

★ `UniWindowController.GetModifierKeys()`（`NSEvent.modifierFlags` 由来）なら 1 は回避できる。
それでも 2 が残るので採らなかった。

## ★ 画面のスクリーンショットはディスプレイごとに倍率が違う

`screencapture -R x,y,w,h` の矩形は**ポイント**だが、返る画像は**そのディスプレイの
バックingスケール**になる。Retina のサブディスプレイでは画像が2倍で返るので、
**画像上で測った座標をそのままクリックに使うと外す**。
画像の幅を矩形の幅で割って倍率を出してから換算すること。

## ★ VRM の差し替えは次の起動から

`VrmStage` は起動時に1回だけ読む作りで、差し替えるには spring bone・コライダ・
ドラッグハンドル・待機モーション・表情の結び直しが要る。中途半端に作ると
「差し替えたのに一部だけ前のモデルのまま」になるので、**できないことをできると見せない**方を
採った（ChatterAgent の設定窓の note に「次回の起動から反映されます」と出る）。

★ **選んだファイルは `~/.config/chatter-agent/models/` へ ChatterAgent がコピーする。** パスを覚えるだけだと、
元ファイルを消したときに候補が死ぬ。

## ★★ `models/` には固定名で上書きする —— 選んだ名前をそのまま使わない

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

★ **候補は走査結果から引き上げること。** 無条件に足すと、設定窓で一度も選んでいない人の
起動ログに毎回「読めませんでした」が1本増える。

★ **`models/` に手で置いたファイルは消さない。** 上書きするのは固定名の1本だけ。
まとめて消すのは ChatterAgent の「すべての設定をリセット」だけで、そちらは確認ダイアログで予告している。
——ただし、**ユーザーが自分で `models/mascot.vrm` という名前で置いた1本だけは例外**で、
次に設定窓からモデルを選んだ時点で置き換わる。それらしい名前を選んだ代償として受け入れている
（`__chatter__.vrm` のような衝突しない名前も考えたが、`models/` を覗いた人に
「これは何だ」と思わせる方が高くつく）。

## ★★ `.vrm` はシステムに UTI が無いので、ファイル選択を種別で絞らない

**症状**: 「VRM モデルを選ぶ…」でダイアログは開くが、**`.vrm` がグレーアウトして選べない**。

`.vrm` はシステムに登録された UTI を持たない（実測: `kMDItemContentType = "dyn.ah62d4rv4ge81q6xr"`）ため、
拡張子から作られるのは **dynamic UTType** で、`NSOpenPanel` の `allowedContentTypes` の有効判定に一致しない。

★★ **ファイル選択のダイアログにフィルタを付けないこと。** 拡張子は選んだあとに確かめる
（ChatterAgent は Rust で `.vrm` を確かめる）。UTI の登録状況に依存しないのが要点。

## ★★ 窓の大きさは `Keeper.BeginApplying` だけで決める

`WindowGeometry.Keeper` は目標の矩形に一致するまで何度か書き直して追従する。
`UniWindowController.windowSize` に直接書くと、適用中の追従に打ち消される。
大きさの変更（`character.scale`）・リセット・モニタ変更後の置き直しは、どれも `BeginApplying` を通す。

## ★★ 「大きさ」の権威は `settings.json` の `character.scale` —— `window.json` は位置だけ

窓の大きさは「既定 540×540pt × `character.scale`」（0.5〜2.0、刻み 0.1、既定 1.0。キーが無ければ 1.0）。
書き手は ChatterAgent だけで、マスコットは `settings.json` を書かない（1秒ごとに `mtime` + `size` を見て
読み直す）。マスコットは値の変化をその場で窓に反映し、位置は保つ。

**大きさをマスコットが書く `window.json` に置くと、ChatterAgent から変えられない。** だから大きさの権威は
`settings.json` に置き、`window.json` は位置だけの権威にした（幅と高さは大きさとして読まない）。
**権威が2つになる問題は起きない**——透過窓は枠なしで、端を掴んでリサイズする手段が無いので、
窓の大きさを変えうる書き手は `character.scale` だけ。

**ウィンドウさえ変えればモデルは勝手に収まる** —— `VrmStage.LateUpdate` が
`Screen.width/height` の変化を毎フレーム見てフレーミングし直す（`_framedWidth` 比較）。
`VrmStage.Headroom`（カメラの前後）を動かす形にすると、`headroom < 1` は
「bounds が画面からはみ出す」という意味なので**頭と足が対称に欠ける**。

★ **マスコットが動いていなくても `character.scale` は書ける**（次の起動で効く）。
「位置と大きさをリセット」は ChatterAgent が `character.scale` を消してから依頼 `resetWindow` を置き、
マスコットは**依頼の直前に設定を読み直して**から既定の大きさ・位置に戻す。直前に読み直さないと、
1秒ごとの読み直しより先に依頼が来たときに、消す前の `scale` で窓を戻してしまう。
「すべての設定をリセット」は位置も大きさも戻さない（`character.scale` は書けるがリセットの対象外）。

★ **`headroom` は「大きさ」の権威ではなく、縦方向の余白の調整つまみ。** 腕はフレーミングの
箱から除外されている（→ [`mascot-vrm.md`](./mascot-vrm.md)「T ポーズの腕をフレーミングの箱に入れない」）ので、敬礼や
万歳のように腕を上げる VRMA が入っても箱そのものは動かない。上に伸びる動きを窓からはみ
出させずに収める余地を持っているのは `VrmStage.Headroom` の値だけ。#88 で既定を 1.1 → 1.25
に上げたのはこのためで、**キャラそのものを大きく／小さくするのは `character.scale`、腕の可動域に余白を
残すのは `headroom`** と役割が分かれている。

## ★★ 音量の上限は 1.0 —— プラットフォームで意味の変わる範囲を設定に持たせない

#76 の初版は音量を **0.0〜2.0** にしていた。macOS では `afplay -v` にそのまま渡るので 2.0 まで
効くが、**Android の `AudioSource.volume` は Unity 側で 0〜1 にクランプされる**
（`audioSource.volume = 1.5` は黙って 1.0 になり、`AudioClipPlayer.CopySettings` はその 1.0 を
各 voice に写す）。つまり**スライダーの右半分が XR では no-op** になる。

`settings.json` は **Android と共有している**（issue #98）ので、doc を「macOS だけ 2.0 まで効く」と
書き直す逃げ道は採らなかった。**同じファイルの同じキーが、開いた環境によって意味を変える**のは
設定として成立しない。上限を 1.0 に下げてある（`SettingsMapping.VolumeMax`）。

- 1.0 超えを効かせるには `AudioMixer` が要る。入れるなら**両方で効く形にしてから**
- 既に `1.5` が保存されていても壊れない。`SettingsJson.ReadNumber` が範囲へ収めて警告を1本出す
- `NeedsVolumeArgument` は `Math.Abs(volume - 1f) > VolumeStep / 2f` のまま。
  「大きくする側が効かなくなる」という以前の理由は消えたが、**`< 1` の裸の比較にすると
  `0.9999999` に `-v 0.9999999` が付く**ので、単純化してはいけない

## ★ 設定窓で選んだモデルは起動引数・環境変数より優先度が低い

`-vrm <path>` / `CHATTER_MASCOT_VRM` は**切り分けの逃げ道**（「設定が壊れていてもこれを付ければ
必ず出る」）なので、ChatterAgent の設定窓で選んだモデル（`models/mascot.vrm`）より優先を保つ。

## ★ XR の設定パネル（#143）は `SettingsSchema.BuildXr` が持つ

デスクトップの設定の UI はマスコットに無く、`SettingsSchema` の入口は `BuildXr` だけ。実装は
[`mascot-android-xr.md`](./mascot-android-xr.md)「空間に浮かぶ設定パネル」。XR の設定は Mac とは同期しない。

**「すべての設定をリセット」の確認は、ダイアログではなく「もう一度押す」で行う。** XR には
ネイティブの確認ダイアログが無い——1回押しただけで確定させると誤操作で全部戻る。パネル側に確認用の
キーは足さず、1回目は確認待ちの note に差し替えるだけにして、猶予以内の2回目で確定する。
