# 発話と音声で踏んだこと — 再生・リップシンク・WebSocket

基本設計・構成・コマンドは [`../mascot.md`](../mascot.md)。ここは**実装で踏んだことだけ**を残す。

## ★ Unity は無音でも macOS の出力デバイスを掴み続ける

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
| `AudioSettings.Mobile.StopAudioOutput()` | **macOS では無効**（実行すると Unity が `"implemented for iOS and Android only"` と出して何もしない。実測: 40秒間 `proc=1` のまま）。→ プラットフォーム別の手放し方は下の「★ 無音時にオーディオ出力デバイスを掴まない」 |
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

## 測り方（同じ測定をやり直すとき）

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

★ **測定でクライアントを2台繋がないこと。** → [`docs/protocol.md`](../protocol.md) の
クライアント側の責務6。速いクライアントの ack が、遅いクライアントのまだ喋っていない
entry を消す。CLI Player を動かしたまま Unity を繋いで実際に踏んだ。

## ★ `kind: "prompt"` の実機確認 —— サーバーは起動前に溜まったキューを捨てる

**確認方法**: `XDG_CONFIG_HOME` を一時ディレクトリに向けてサーバーを隔離環境で起動し、
**サーバー起動後に**配信キューへ `kind: "prompt"` の entry を1件手で置いた。

**確認できた挙動**: **再生中だけ**視線がカーソル追従をやめて中央に固定され、上体が前傾する。
再生が終わると通常のカーソル追従に復帰する。

★ **「サーバー起動後に置く」が重要。** サーバーは**起動前に溜まっていたキューを捨てる**
（実機ログ: `[Server] 起動前に溜まっていた 1 件を捨てました`）。先に置くと消えて確認できない。
**これは独立した罠として書く価値がある** —— `kind: "prompt"` に限らずキュー全般に効くので、
実機で挙動を確かめるときは必ず「サーバーを起動 → 起動できたことをログで確認 → そのあとでキューに置く」
の順を守ること。

## ★ Newtonsoft は `ts` を勝手に `DateTime` にする

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

## ★ Newtonsoft は `long` を超える整数を `BigInteger` で持つ（`JTokenType` は `Integer` のまま）

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

`settings.json`（`SettingsJson`）の数値読み取りも同じ手当て（`TryValue<T>` が `Value<T>()` を
try/catch で囲む）で、読めなければ警告してそのキーだけ既定へ倒す。`int` で読む項目
（`display.frameRate`）は、`long` には収まる値でも `int` を超えると `OverflowException` になる。
回帰テスト: `SettingsJsonTests.FallsBackPerKeyWhenNumbersExceedLong` ほか。

## ★ 購読者の例外を接続の外へ出さない

`SpeechClient` は購読者が何をするか知らない。`MascotRunner` は `FrameReceived` /
`Connected` / `Disconnected` の3つとも `PlaybackQueue.Reduce` → コマンド実行に繋いでいるので、
そこの1つの例外が**受信ループや再接続ループを道連れにする**。

とくに `RunAsync` は `_ = RunAsync()` で起動しているので、そこまで上がった例外は
**未観測の `Task` の fault として捨てられる** —— ログが1行も出ないまま再接続ループだけが消え、
セッションが終わるまで無音になる。「サーバーが何も言っていない」と区別がつかない。

`SafeInvoke` で握って必ず `Warn` に出す。`RunAsync` の最外殻にも try/catch を置くが、
**あれは復旧のためではなく可視化のため**（復旧できないなら、せめて
「再接続ループが止まりました」と言わせる）。

## ★ `ClientWebSocket.SendAsync` を `_ = ` で投げると、例外が `catch` を素通りする

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

## ★ 終了時の ack は `Application.wantsToQuit` で保留しないと投げ切れない

`OnDestroy` から `_ = client.CloseAsync()` を投げても、await の継続が走る前に
プロセスが消える。喋り終えた ack が落ちると、**次回起動でその文がもう一度鳴る**。

`wantsToQuit` で1回だけ `false` を返して終了を保留し、閉じ切ってから
`Application.Quit()` を呼び直す。予算（3秒）を切ること —— 返らない相手を掴むと
**アプリが終了しなくなる**。

★ **Editor の Play Mode 停止では保留できない。** Unity のドキュメントが
「The return value of this event is ignored when exiting Play mode in the Editor」と
明記している。イベント自体は呼ばれるが `false` が効かないので、
**この経路の確認はビルドした `.app` でしか取れない**。Editor で ack が落ちても実装の失敗ではない。

★ **iOS / iPadOS では戻り値が効かない**（ドキュメント明記）。**Android での挙動は未確認**（→ #97）。

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

## ★★ `wantsToQuit` の継続からその場で呼ぶ `Application.Quit()` は無視される

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

## 実測（2026-08-30 / macOS 26.6.2。`-quitProbe` で保留を強制し、終了要求を1回だけ送る）

| `Application.Quit()` を呼ぶ場所 | 結果 |
|---|---|
| **継続からその場で**（旧） | ログは出るが、**15秒待っても終了しない。** `wantsToQuit` の2周目すら来ない |
| **`Update` の先頭から**（新） | 1回目の呼び出しだけで**約1秒で終了** |

## ★ ストリーミングで書かれた WAV は `data` のサイズが 0 のことがある

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

## ★ .NET の正規表現は JS より緩い（`$` と `\d`）

契約の charset を JS から写すときに2箇所ずれる:

- **`$` は末尾の改行の手前にもマッチする。** `^…$` のままだと `gen-1\n` や
  `/audio/gen-1-000000000001.wav\n` が通る。**`\A` / `\z`** を使う
- **`\d` は Unicode の十進数字にマッチする**（JS の `\d` は ASCII のみ）。
  アラビア・インド数字（`٠`-`٩`）12桁の `seq` が通る。**`[0-9]`** を使う

どちらも `core/src/core/audioPath.ts` は弾く。通った値は `BaseUrl` と連結されて
**そのまま URL になる**（`Runtime/Protocol/SpeechEpoch.cs`）。

回帰テスト: `SpeechFrameTests` の `TrailingNewlineIsRejected` /
`NonAsciiDigitsInAudioPathAreRejected`。

★ **同じ契約が `Net/AssetSyncPlan.cs`（#117）にもある。** `animations/<category>/<name>.vrma` の
`name` を検査する `AnimationFilePattern` も `core/src/core/assetPath.ts` の `NAME_PATTERN` を
`\A` / `\z` で括って移している——ここも `$` のままだと末尾に改行を持つ名前を通してしまう。

## ★ `JsonUtility` を使わない

契約は `audio` キーの**欠落**と `null` を区別することを要求している
（欠落 = #29 より前のサーバー / `null` = `ttsEnabled: false` という正常な設定）が、
`JsonUtility` にはこの区別ができない。**潰すと、繋ぎ先が古いことに気づく唯一の手がかりが消える。**

`com.unity.nuget.newtonsoft-json` の `JObject` で判定している
（`Runtime/Protocol/SpeechFrame.cs` の `TryParse`）。

## ★ `AudioSource` 1本では孤児（旧 epoch）の契約を守れない

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

## 実機での確かめ方（1回目は失敗した）

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

## ★ 無音時にオーディオ出力デバイスを掴まない

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
`SuspendOutput` は no-op。**Android / iOS でだけ効く**（→ [#97](https://github.com/schwarz9791/chatter-agent/issues/97)）。
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

## プロジェクト設定まわりで踏んだこと

★ **Unity は YAML に無い `SerializeField` に C# のイニシャライザ値を残す。** `MascotRunner` に
`audioIdleSuspendMs = 5000` を足したがシーンを保存し直していないので、`Mascot.unity` の
`MonoBehaviour` ブロックにこのキーは**無い**。それでも実測では 5000 が効いていた
（キーが無い状態のビルドで「無音が続いたので…」のログが12回出た）。型の既定値 0 が
当たるわけではない。★ ただし**シーンを一度でも保存すると値が焼かれる**ので、
既定を変えるときはシーンも見ること。

★ **`AudioManager.asset` に Unity 6 世代の新キーが4つある**（`m_EnableOutputSuspension: 1` /
`m_AudioFoundation: 0` / `m_OutputChannelLayout: 2` / `m_OutputSamplingRate: 48000`）。
`BuildScript` が `m_DisableAudio` を書き換えるとき `AssetDatabase.SaveAssets()` が走り、
Unity がアセット全体を再シリアライズしてテンプレートに無かったフィールドを既定値で書き出したもの。
[#97](https://github.com/schwarz9791/chatter-agent/issues/97) で Unity 6000.3.14f1 に切り替え、
`FixAll`・EditMode テスト・ビルドの一連を通しても4キーとも値は変わらず、**6000.5.8f1 固有ではなく
Unity 6 世代の既定値のままでバージョンに依存しない**と決着した（Editor バイナリの
`-enhancedAudioFoundation` のヘルプが `Default: 48000` / `Stereo (default)` と明記、この Mac の
既定出力デバイスは 44100 `system_profiler` なので環境固有の値でもない）。
`m_AudioFoundation: 0`（Classic。disabled）なので `m_OutputSamplingRate` / `m_OutputChannelLayout`
（sampling rate and channel layout parameters）は無視され（Android で AivisSpeech の 24kHz が
余計にリサンプルされることはない）、従来の `m_SampleRate: 0` は別キーのままで変更されていない。

★ **`mixerSuspend` 系の API は `mixerResume` と同じスレッドから呼ぶ必要がある。**
`Update()` も `Execute()` も `PlayAsync` の継続も Unity のメインスレッドなので自然に
満たせるが、実装側にも検査を置くこと。壊れ方が「たまに無音」なので静かに壊れさせない。

★ **resume に失敗しても「掴んでいる」側に倒すこと。** suspend したままのフラグが残ると
**二度と resume を試さず恒久的に無音になる**。無音より二重 resume の方が軽い。

## ★ 口は「再生中の音を測る」のではなく「`Prepare` の時点で作っておく」（#58）

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

## ★ エンベロープが作れなくても `Prepare` を失敗させない

**リップシンクの都合で発話を落とすのは本末転倒。** `Prepare` が `null` を返すと
`AudioFailed` → skip + ack となり、**サーバーのキューから物理削除されて二度と鳴らせない**。
`Envelope = null` に倒して1回だけ警告する。

回帰テスト: `AfplaySpeechPlayerTests.PrepareSucceedsEvenWhenTheEnvelopeCannotBeBuilt`。
材料は **`bitsPerSample = 12` の PCM** —— `TryReadHeader` は通り、サンプルの読み出しだけが落ちる。

## ★ 20ms のエンベロープを 33.3ms で「点サンプリング」しない

エンベロープの刻み（20ms）と表示（30fps = 33.3ms）は割り切れない。33.3ms 刻みで点を取ると
**フレーム 2 / 4 / 7 / 9 … に一度も当たらず、4割を読み飛ばす**（＝立ち上がりが落ちて口が鈍る）。
`SpeakingSet.Mouth(from, to, offsetMs)` は**前フレームからの区間の最大値**を返す。

★ **区間の始点を `MascotRunner` に持たせないこと。** `Mouth()` が冪等でなくなる（呼び出し元の
数に依存する API になる）うえ、`MascotRunner.Update` は `VrmCharacter.LateUpdate` より前の
位相なので区間が半フレームずれる。始点は `MouthTracker`（`Runtime/Vrm/`）が持つ。

★★ **始点を `double.NegativeInfinity` で初期化しないこと。** `Mouth(-∞, now)` は
**エンベロープ全体を走査して全体最大を返す**ので、**最初のフレームで口が全開に飛ぶ**。
未サンプルは `NaN` で表し、`from = to` の点サンプル1回に倒す。

## ★★ ラグの補正で「負のインデックスを 0 にクランプ」しない

`afplay` は `Process.Start` が音より前に返るので、その起動ラグぶん口が先に動く。
`lipSyncOffsetMs` を引いて索引するのだが、**`index = max(0, index)` と書くと
offset ぶんの先行区間で `envelope[0]` を返す＝音より先に口が動く**ので、補正の意味が消える。
正しいのは**区間 `[lo, hi]` 全体が音より前（`hi < 0`）なら 0（口を閉じたまま）**。

回帰テスト: `SpeakingSetTests.OffsetKeepsTheMouthClosedBeforeTheSoundStarts`。

## ★ 端数フレームは実サンプル数で割る

`LipSyncEnvelope.Build` の末尾のフレームはフレーム長に満たない。ゼロ埋めしてフレーム長で
割ると最後だけ小さくなり、**語尾で口が閉じる**。半フレームぶんの直流 1.0 はゼロ埋め実装だと
0.707 になるので、`LipSyncEnvelopeTests.TrailingPartialFrameIsNotDiluted` の1本で確実に捕まる。

★ **24000Hz / 1ch に決め打ちしないこと。** `ttsBaseUrl` を VOICEVOX に向ければ別のレートに
なりうる（そのとき口が音に合わなくなるが、**エラーは出ない**）。

## ★ サンプルの読み出しは `WavDecoder` を再利用する（ただしバッファは渡す）

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
（約 1ms）なので今はやっていない。この実装が主役になるのは Android（#97）。

## ★ ゲインと減衰は `FacePolicy` ではなく `MouthTracker` に置く

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

## ★★ `SpeakingSet` が `SpeakingView` を置き換えた（孤児の穴が閉じた）

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

## ★ 30fps で口が足りるかの決着（#58）— 据え置き

`Application.targetFrameRate = 30` のままで足りる。逃げ道（`speakingFrameRate`）は用意したが
**既定 0（＝変えない）**。

**実測**（2026-08-29 / macOS 26.6.2 / ウィンドウ 300x480 / `AvatarSample_A.vrm` +
同梱 `idle_loop.vrma` / 1〜30 の数え上げを連続再生）: 発話中の CPU は 30fps
（`speakingFrameRate: 0`）で**中央値 17.7%**、60fps（`speakingFrameRate: 60`）で
**中央値 37.7%** —— **約 2.1 倍**。常駐アプリの電力設計（#55）に対してこの差は大きい。

★ **口の応答は 30fps でも落ちていない。** 20ms 刻みのエンベロープを 33.3ms 間隔で読むと
4割のフレームを読み飛ばすが、`SpeakingSet.Mouth` が**区間の最大**を取るので拾い切る。
実測（`-faceLog 1 -faceLogMs 100`）は**中央値 0.40 / 最大 1.00** で、オフライン予測
（20ms ごとの RMS に `gain = 4` を掛けた値）とほぼ一致した —— **`gain = 4`（cc-mascot の
`rms * 4`）はそのまま使えた。**

★ **`speakingFrameRate` を「念のため」常時 60 にしないこと。** 上の 2.1 倍がそのまま乗る。

## 目視でも確認した（2026-08-29）

上の実測はすべて `Player.log` からの機械判定で、**「口が階段状に見えないか」「音と口が
ずれて見えないか」「笑顔で口がはみ出ないか」は目で見ないと決まらない**。実機の
macOS ビルドで確認し、いずれも問題なしと判断した（`lipSyncOffsetMs: 120` でずれない /
`speakingFrameRate: 0` のまま階段状に見えない / `mouthScaleHappy: 0.2` ・
`mouthScaleSad: 0.5` でメッシュからはみ出ない）。

★ **確認はウィンドウを一時的に 2 倍（600x960）にして行った**（既定の 300x480 では小さくて
粗が判別できない。`open Build/ChatterMascot.app --args -screen-width 600 -screen-height 960`）。
**確認後は戻すこと** —— Unity は終了時にその大きさを焼き付けるので（→ [`mascot-desktop.md`](./mascot-desktop.md)
「ウィンドウの大きさは3箇所で決まる」の 1）、放っておくと次回から 600x960 で開き、バンドル ID は
チェックアウトを跨いで共通なので**別 worktree のマスコットまで大きくなる**:

```bash
defaults write tech.sukima.chatter-mascot "Screenmanager Resolution Width" -int 300
defaults write tech.sukima.chatter-mascot "Screenmanager Resolution Height" -int 480
```

（または `~/Library/Preferences/tech.sukima.chatter-mascot.plist` を削除する）

## ★ `afplay` の起動ラグは 116ms。較正ログの「差」をそのまま入れない

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

## ★ `-faceLogMs` は実機確認専用（既定の1秒を縮めない）

`faceDebugLog` のログは既定 1秒間隔。**孤児が重なっている間も口が止まらないこと**を
`aa=` の連続で判定するには粗すぎるので、起動引数 `-faceLogMs 100` で縮められるようにした。
**常用では縮めないこと** —— `Player.log` が流れて他の診断が読めなくなる。

## ★ 再生の期限は `WavHeader.DurationMs` から出す（`AudioClip.length` ではない）

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

## ★ ストリーム再生にしない（音声の持ち方はプラットフォームで違う）

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

## ★ ping watchdog は同等品が作れない（劣化を受け入れている）

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

## ★ `serverUrl` が不正だと「動いて見える死体」になる

`AudioFetcher.DeriveAudioBaseUrl` は `new Uri(serverUrl)` を呼ぶので、Inspector に
`127.0.0.1:8570`（スキーム無し）や空文字を入れただけで `UriFormatException` が飛び、
`MascotRunner.Start()` が最後まで走らない。すると **ウィンドウは出て、フレームレート上限も効いて、
接続先のログすら出ない**。Player.log に埋もれたスタックトレース1本以外に手がかりが残らない。

`Start()` の頭で `Uri.TryCreate` + スキーム（`ws` / `wss`）を検査して、
駄目なら入力値を名指しした `LogError` を出して `enabled = false` で止める。

## ★ `.app` を終了しても `afplay` は死なない

`MascotRunner.OnDestroy` の `StopAll()` が止めている。消すとアプリを閉じた後も喋り続ける。
**実測で確認済み**（発話中に `pkill -TERM` → 3秒後に `afplay` が0個）。

一時ファイルは `$TMPDIR`（`/var/folders/…/T/<company>/<product>/speech-<pid>/`）。
`Application.temporaryCachePath` は macOS では `~/Library/Caches` ではない。
**ディレクトリ名に pid が入る**のは、Editor の Play Mode とビルド済み `.app` を同時に
動かしたときに、後発が先行インスタンスの再生中の WAV を消さないため。

## ★ `chatter-agent-player`（Node）と同じルートへ同時に繋がない

ack は累積で消費されるので、同じランタイムルートに2つ目のクライアント（`npm run start:player`
や Android 版）を繋ぐと、速い方の ack が遅い方のまだ喋っていない entry を消す。**完全な排他は
まだ実装していない**（参照実装と同じ `player.lock` を Unity 側からも取ることになるが未着手）。

★ **症状は「発話を食い合う」＋「音声が404になる」。** 耳で聞くと「たまに飛ぶ」にしか聞こえない
ので、無音の切り分けをする前に **player（や別のクライアント）が動いていないかを先に確認する**こと。

