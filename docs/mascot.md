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

### ★ ストリーミングで書かれた WAV は `data` のサイズが 0 のことがある

`0xFFFFFFFF`（Int32 では -1）だけでなく **0 も実体で測り直す**
（参照実装 `core/src/player/audioPlayer.ts` の `declared > 0 && declared <= actual`）。

0 を弾くと「data チャンクがありません」になり、`AudioFailed` → 1回リトライ →
**「seq=N の音声を取れなかったので飛ばします」で全文が無音スキップ**される。
合成側が data サイズを後追いで埋める書き方に変えただけでこうなる。

回帰テスト: `WavDecoderTests` の `MeasuresZeroSizedDataChunk` /
`MeasuresOversizedDataChunk` / `RejectsTrulyEmptyDataChunk`。

### ★ テストアセンブリの `overrideReferences` に注意

`ChatterMascot.Tests.asmdef` は `overrideReferences: true` なので、
**`precompiledReferences` に挙げた DLL しか参照できない**。テストが Newtonsoft を直接使うなら
`Newtonsoft.Json.dll` を足す（`nunit.framework.dll` だけだとコンパイルが通らない）。

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
