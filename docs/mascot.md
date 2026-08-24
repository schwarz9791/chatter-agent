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
合わせる必要がある（→ #25）。VRM のリップシンクと spring bone が入ったら、
30fps で口の動きが足りるか見直す（→ #17）。`MascotRunner` の Inspector で変えられる。

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

★ **位置の永続化は入れていない**（起動のたびに中央へ戻る）。マルチモニタ・解像度変更・
画面外からの復帰の扱いが要るので #16 でまとめて設計する。

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

回帰テスト: `WavDecoderTests` の `MeasuresZeroSizedDataChunk` /
`MeasuresOversizedDataChunk` / `RejectsTrulyEmptyDataChunk` /
`SkipsZeroSizedChunkBeforeData`。

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

### ★ ビルド対象シーンは `EditorBuildSettings` にも入れる

`scripts/build.sh` は `-buildScene` を明示で渡すので通るが、
**Unity の `File > Build Settings > Build` や `-buildScene` を渡さない経路（#54 の CI）は
`EditorBuildSettings` を見る**。テンプレート既定の `SampleScene` のままだと、
そこには `MascotRunner` も `UniWindowController` も `EventSystem` も無いので、
出来上がる `.app` は**不透明なウィンドウが出て、何にも繋がらず、エラーも出さない**。

`SceneFixups.EnsureBuildScenes()` が本番シーン1本に揃える。

### テンプレートの残骸はリポジトリ唯一の Git-LFS 依存だった

`Assets/TutorialInfo/`（`ReadmeEditor.cs` / `Readme.cs` / `Layout.wlt` / `Icons/URP.png`）と
`Assets/Readme.asset` / `Assets/Scenes/SampleScene.unity` は、どこからも参照されていなかった。

外した理由は diff のノイズだけではない。`.gitattributes` の `*.png` が
`Icons/URP.png` を LFS 送りにしていて、**`git lfs ls-files` の出力がこの1件だけ**だった。
消したことでリポジトリの LFS オブジェクトがゼロになり、clone と #54 の CI checkout が
LFS を要求しなくなった。

★ `.gitattributes` の `*.png` 規則は**残してある**（#17 で VRM のテクスチャが入る）。

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
- **`Current`（最後に鳴らし始めた voice）を公開している。** #17 のリップシンクが
  `GetOutputData` を読む先を1つに決めるため
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

### ★ 音声はメモリ上の `AudioClip` にする。ファイルに落とさない

契約の「ローカルに落としてから再生すること」は**ストリーム再生の禁止**であって、
ファイルを要求しているわけではない。参照実装（Node の player）が一時ファイルを使うのは
`afplay` に渡すためで、Unity は `AudioClip` をメモリに持てる。

状態機械の `DiscardAudio` コマンドは **`Object.Destroy(clip)`** に読み替える。

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

## プラットフォームを絞る

★ **UniWindowController の macOS ネイティブプラグインが Android ビルドに混ざらないよう
Plugin Inspector で macOS に限定すること。** XR パッケージは Android にだけ効かせる
（→ [#25](https://github.com/schwarz9791/chatter-agent/issues/25)）。
