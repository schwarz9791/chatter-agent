# core/ で踏んだこと・決めた理由

基本設計・構成・コマンドは [`../core.md`](../core.md)。ここは**理由と実測だけ**を残す。

## `tsx` で実行しない。バンドルする

**ここが前身 cc-mascot-xr からの変更点。** cc-mascot-xr のブリッジは常駐プロセス1本だったので `tsx` で直接実行していたが、chatter-agent の CLI は **hook から毎 delta 呼ばれる**。`tsx` の起動コスト（~300ms）は乗せられない。

tsdown 等でバンドルし、成果物を `plugin/bin/chatter-agent-speak.mjs` に出す。

## 常駐プロセス（server / player）から `execFileSync` を呼ばない

**CLI（`chatter-agent-speak`）は hook から起動される単発プロセスで、ロックが直列化を担っている**ので、
`execFileSync` でイベントループを止めて構わない（要約の `summaryPipeline.ts` がそう書いてある）。
**サーバーは違う。** 止めている間は WebSocket の配信も `GET /audio/…` の応答も止まるので、
クライアント側の音声取得が `audioFetchTimeoutMs`（既定45秒）で**転送エラーになり、試行回数を
消費して発話が捨てられる**（→ [`protocol.md`](../protocol.md) の責務8）。
要約は最悪 `aiSummaryTimeoutMs`（既定60秒）掛かるので、設定パネルのテストボタンを押しただけで
発話が落ちることになる。`summaryPreview.ts` が非同期版（`runClaudeCliAsync`）を使うのはこのため。

★★ **失敗の見分け方が同期版と違う**（Node 24.19.0 実測）。同期版の判定をコピーすると
タイムアウトが全部 `error` に化ける（症状は「テスト要約がいつも失敗と出る」で、原因の見当が付かない）:

| | `execFileSync` | `execFile`（非同期） |
|---|---|---|
| タイムアウト | `code: "ETIMEDOUT"` | `code: null` / **`killed: true`** |
| maxBuffer 超過 | `code: "ENOBUFS"` | `code: "ERR_CHILD_PROCESS_STDIO_MAXBUFFER"` |
| 非ゼロ終了 | `status: <n>` | `code: <n>`（**数値**）/ `killed: false` |
| コマンドが無い | `code: "ENOENT"` | `code: "ENOENT"` |

★ **maxBuffer の判定を `killed` より先に置くこと。** maxBuffer 超過でも子は殺されるので、
逆にすると overflow が timeout に化ける。

★ **判断そのものは分けないこと。** 引数（`buildSummaryArgs`）・環境変数（`buildSummaryEnv`）・
指示文（`SUMMARY_INSTRUCTION`）・採用の規則（`isAcceptableSummary`）・整形（`toSpeechSentences`）は
同期版と**同じものを共有している**。片方だけ直すと「テストボタンは通るのに本番では原文が
読み上げられる」という、いちばん切り分けにくいズレになる。

## 前身から流用するもの

手元の `/Users/schwarz/dev/cc-mascot-xr` に動作確認済みのコードがある（非公開・開発中断）。**そのまま使えるものは書き直さないこと。**

| 流用元（`cc-mascot-xr/bridge/`） | 移送先 | 状態 |
|---|---|---|
| `src/server/wsServer.ts` + `.test.ts` | `core/src/server/` | **ほぼそのまま（流用済み）。** ping-pong によるデッドコネクション検出、`bufferedAmount` によるバックプレッシャ、graceful close 込み。ack の受信と Origin 検査を足した |
| `src/config/paths.ts` + `.test.ts` | `core/src/core/paths.ts` | **流用済み。** 環境オブジェクトを引数に取る純関数群。ディレクトリ名と解決先を変えた |
| `src/config/configStore.ts` + `.test.ts` | `core/src/core/config.ts` | **流用済み。** 環境変数 > JSON > 既定値。mtime + size が変わったときだけ再読込。壊れた JSON では直前値を維持 |
| `.github/workflows/validate.yml` | リポジトリルート | **適用済み。** `working-directory` 指定済み。chatter-agent では format / bundle の2ジョブを追加した |
| `LICENSE` / `NOTICE` | リポジトリルート | **適用済み。** Apache-2.0 + 派生物としての帰属表示 |

これらは自分の著作物なので、cc-mascot 由来ファイルのようなライセンスヘッダも `NOTICE` への帰属表示も不要。**ソース内にも由来コメントは書かない**（非公開リポジトリなので、読んだ人が辿れない参照になる）。流用の経緯はこの表だけに残す。

`wsServer.ts` の実装には全て理由がある（`listening` まで resolve しない / タイマーを `unref()` する /
socket に error ハンドラを必ず付ける / `boundAddress` を保持する）。**コメントごと残してあるので消さないこと。**
テストも方針を引き継いでいて、**`ws` をモックせず `127.0.0.1` の `port: 0` に実ポートを開く。**
モックすると「想像した ws の API」しか検証できない。

## 感情判定は「文が感情的か」ではなく「作業で何が起きているか」で決める

`emotion` は VRM の expression に一対一で載る。判定器が答えるべきなのは
**「いま作業で何が起きていて、相棒ならどんな顔をするか」**であって、発話の字義どおりの
情動ではない。計画と実装が仕事である以上、発話は平坦になるのが当たり前で、そこに
少し芝居を乗せる。

| 作業の状況 | 表情 |
|---|---|
| テストが通った / 実装が完了した | `happy` |
| 完了を待っている / これから取り組む | `relaxed` |
| テストが落ちた / 見落とした / 申し訳ない | `sad` |
| 想定と違うと分かった / 原因が判明した | `surprised` |
| 任せた先が止まって自分でやり直す / 本当に苛立っている | `angry` |

★ **技術報告語を怒りに割り当てないこと。** `問題` `失敗` `バグ` `エラー` は Claude Code に
とって業務上の中立語彙で、これらで怒らせると平坦な報告のたびに表情が動く。失敗時の
実際のトーンは自責なので、受け皿は `angry` ではなく `sad`。`angry` が引き受けるのは
**手戻り**——任せた先が止まって自分でやり直す場面。**待つのと待たされるのは違う**ので、
同じ文に待機の語があっても手戻りを優先する。

★ **キーワードは `includes` の部分一致なので、短い語と英字は他の語に埋もれる。**
語を足すときは、実際に何にマッチするかを必ず確かめること（`ウザ` が `ブラウザ` に、
`ok` が `hook` や `Storybook` に、`不安` が `不安定` に埋もれていた）。
★ **複合名詞の一部に当たる穴は残っている。** `ミス` は `ミスマッチ`、`成功` は `成功率`、
`完成` は `完成度` に当たる。一律に「直後が漢字なら弾く」規則は使えない——`完了通知`
`完了条件` のような正しい発火まで消えるため。語ごとに活用形を並べて塞ぐのも同じ理由で
採らない。

★ **キーワード直後の否定形は共通のガード（`hasAffirmativeMatch`）で弾く。** 活用形を
辞書に並べて塞ぐ代わりに、当たった位置の直後の短い窓だけを見て否定形かどうかを
判定する。同じ語が複数回出るときは、すべての出現が否定形のときだけ加点しない。
語ごとの例外は持たせないこと。**否定形そのものが定型句の語は、辞書側にその形で
持たせて解く**（`許せ` ではなく `許せな` / `許せませ`）——語幹だけを持つと、肯定用法に
誤って当たるうえ、ガードにも消える。

★ **同点になったときの優先順は `TIE_BREAK_ORDER` で固定してある。** `scores` オブジェクト
リテラルのキー順に判定を委ねないこと。優先順は、現在の状態や未解決の情報を
報告の喜びより優先する向きに決めている。

★ **`relaxed` を neutral に吸収する抑制を戻さないこと。** 1語では点が足りず、`relaxed` が
構造的に出なくなる。

★ **疑問符で `surprised` に加点しないこと。** 方針を尋ねる文が大量に出るので、質問の
たびに驚いた顔になる。

**感情キーワード辞書（`{root}/emotion-keywords.json`）は `config.json` に含めない。** 理由は3つ:

- 数百語の配列がユーザーの設定ファイルの大半を占めることになる
- `SPECS` は1キー1パーサの表なので、配列の中身の検証だけが非対称になる
- 読み手が CLI だけなので、`SPECS` に載せると server / player が起動のたびに未知キー警告を吐く（下の「server（音声合成）だけが読むキー」と同じ理由）

server（音声合成）だけが読むキー一覧・既定値・意味は [`../core.md`](../core.md)「設定と環境変数」の
「音声合成エンジン」にある。ここに残すのは経緯と実装の細部だけ。

★ **#29 で読み手が player → server に移ったが、キー名も意味も変えていない。** 改名すると、
既存の `config.json` に残った旧キーが**全バイナリで**未知キー警告を出す（#11 で
`speechLogGenerations` を廃止したときに実際に踏んだ）。

- モジュール名は API ファミリ（`voicevoxClient`）、config キーはエンジン中立（`tts*`）で割り切ってある
- ★ **`ttsSpeedScale` はこのファイルで小数を受ける最初のキー。** 既存の `toInt` は
  `Number.isInteger` 縛りなので流用できず、`makeRangeParser` が要った。
  ★ `Number("")` も `Number(" ")` も `Number(null)` も `0` になるので、
  空・空白・非文字列を明示的に落としている（`toInt` では `Number.isInteger(NaN)` が
  その分を弾いていた）
- ★ **`audioStore` のキャッシュキーに `speedScale` が入っている。** 合成結果を変えうる設定を
  `Voice` に足したら、`keyFor` にも足すこと。足さないと**速度を変えた直後に取り直した文だけ
  古い速度の WAV** がキャッシュから返る
- ★ **`synthesisTimeoutMs` を2往復（`audio_query` と `synthesis`）で1つの予算にしないこと。**
  モデルロードで `/audio_query` が食い切ると、CPU 律速の `/synthesis` に残り0が渡る

### エンジンを起こす（`ttsSpawn*`、[#51](https://github.com/schwarz9791/chatter-agent/issues/51)）

**エンジンが居なければサーバーが起こす。** GUI（AivisSpeech.app）を終了すると GUI が spawn した
エンジンも道連れで落ちる（PID の親子を実測）ため、「GUI を閉じてエンジンだけ残す」運用は成立しない。
このエンジンは chatter-agent 以外に使わないので、**サーバーが落ちたらエンジンも一緒に落とす**。

★ **起こすだけで、起動を待たない。** `[Server] Ready` は先に出て、合成は今までどおり
`GET /audio/…` が来たときに走る。#29 の柱（エンジンが落ちてもテキストの配信は止まらない）は
変わらない（→ `CLAUDE.md`「絶対に守ること」7）。

起こすのは**次の6つを全部満たすときだけ**:

1. `ttsEnabled` が `true`
2. `ttsSpawn` が `true`
3. **起動時の疎通確認に失敗した**
4. `ttsBaseUrl` のホストがループバック（`127.0.0.0/8` / `[::1]` / `localhost`）
5. `ttsBaseUrl` のスキームが `http:`（平文のエンジンしか起こせない）
6. コマンドが解決できた

★ **条件3が要。** 「まず繋いでみて、居なければ起こす」形にしたことで、GUI 併用・verify のスタブ・
別ポート運用のすべてが**追加の分岐なしで**素通りする（ポート衝突を判定するコードが要らない）。
判定は `listSpeakers` に**繋がったか**だけで、`ttsSpeakerId` が実在するかは混ぜない
——混ぜると、スタブが生きているのに話者 ID だけ間違えている状態で**二重起動**する。

- `ttsSpawnCommand` が空なら、AivisSpeech.app の既知の場所を順に見る:
  `/Applications/AivisSpeech.app/Contents/Resources/AivisSpeech-Engine/run` →
  `~/Applications/…` の同じパス。**指定した値が見つからないとき、既知候補にフォールバックしない**
  （指定を黙って別のバイナリに読み替えるのは、最も気づきにくい失敗の仕方になる）
  - **非絶対パス（`docker` など）も受ける。** VOICEVOX を Docker で動かすような運用が
    あるため禁止しない。ただし**名前から解決したときは必ず名指しでログに出す** ——
    PATH には `~/.local/bin` も mise / asdf の shims も普通に載っている（実測で 7/7）ので、
    `run` のようなありふれた名前はまったく別のバイナリに当たりうる。
    ★ **「既知の場所を探さない」で塞ごうとしないこと。** 一度そうしたが、上のとおり
    あの並びは結局 PATH に載っているので**穴が1つも塞がらない**まま、
    「PATH だけ見るから安全」という誤った安心だけが残る
  - 見つからないときは**探した場所をフルパスで**ログに並べる（PATH を直すのか・ファイル名を
    直すのか・実行ビットを立てるのかが症状から分かるように）
- `ttsSpawnArgs` が空なら `ttsBaseUrl` から `--host <host> --port <port>` を組む。
  **指定すると導出は行われない**（追加ではなく置換）。自分で書くなら `--host` / `--port` も自分で書く
- **落ちても再起動しない**（初版）。起動失敗ループの方が害が大きい。異常終了なら `[Engine]` が
  終了コードと **出力の末尾**を出す ——これが無いと「起動したはずなのに繋がらない」の原因が
  1文字も残らない。★ **stdout と stderr は別々の窓に溜めて、出すときは stderr を優先する。**
  混ぜると、エンジンが stdout に出すアクセスログ（uvicorn は合成のたびに1行）が窓を埋め、
  落ちた瞬間の stderr を押し出す
  - ★ **落ちた後に「定期診断」が走るわけではない。** `recheckEngine` は合成が失敗したとき
    （`onSynthesisFailed`）にしか呼ばれないので、**クライアントが音声を取りに来なければ
    一度も走らない**。`ENGINE_RECHECK_INTERVAL_MS` は周期実行ではなく「呼ばれたときの間引き」
- ★ **起こすかどうかの判断は起動時の1回きりで、実行中に再評価しない。** 起動時に
  `ttsEnabled: false` だった／後から AivisSpeech をインストールした、はサーバーを再起動すれば
  反映される。**動的にやり直す方が危険側に倒れる** ——「誰が起こしたエンジンなのか」の追跡が
  難しくなり、GUI が上げた共有物を落とす事故に近づく
- 停止は**プロセスグループごと**（`detached: true` で起こし、`process.kill(-pid, …)`）。
  `run` は PyInstaller のバイナリで**自分の子を持つ**ので、`child.kill()` では孫が残ってポートを掴み続ける
- 終了処理は `SIGINT` / `SIGTERM` / `SIGHUP` で走る。**`SIGHUP` を拾うのは、端末のウィンドウを
  閉じるのが日常操作だから**（`detached` で起こしたエンジンに端末の SIGHUP は届かない）
- ★ **次の場合はエンジンが残る**（終了処理が走らないため）: サーバーが `SIGKILL` された /
  **2回目の Ctrl-C**（即座に落とす経路）/ 終了処理が6秒を超えて watchdog に落とされた。
  これは実害が無い ——次回起動時の条件3が残ったエンジンに疎通し、起こさずに再利用する
- ★ **ログのプレフィックスは `[Engine]`。** エンジンのプロセスそのものに関する行だけがこれで、
  起こす / 起こさないの判断は `[Server]` が出す

**実機で確認した**（macOS / AivisSpeech 1.1.0-dev、2026-08-24）:

- GUI を終了し `lsof -nP -iTCP:10101 -sTCP:LISTEN` が空の状態から、`npm run start:server` と
  `npm run start:player` だけで**音が出た**。エンジンの親は `node dist/chatter-agent-server.mjs`
  （GUI ではない）
- サーバーを止めると `lsof` が空に戻る（プロセスグループごとの停止が効いている）
- **GUI が上げている状態ではサーバーは起こさない**（条件3）。ログには
  `音声合成エンジンに繋がりました` だけが出て `[Engine]` の行は出ない
- 起こしてから `/speakers` が応答するまでは**数秒**かかる。★ **この秒数を仕様として扱わないこと** ——
  マシンとモデルで変わる。その間の `GET /audio/…` は `503` になり、クライアントが取り直す

★ **話者を増やすときは GUI が要る。** エンジン単体だと、音声モデルの追加は API を叩くか
`~/Library/Application Support/AivisSpeech-Engine/Models/` に `.aivmx` を直接置くことになる。
モデル自体は GUI から独立しているので、**一度入れた話者はエンジン単体でもそのまま使える**。

player だけが読むキーの一覧・既定値・意味は [`../core.md`](../core.md)「設定と環境変数」の「再生」にある。

以前は `audioFetchTimeoutMs` とサーバー側の `synthesisTimeoutMs` の順序に「長くすること」という
暗黙の制約があり、破ると 503（待てば直る）で来るはずの状態が転送エラー（試行回数を消費する＝
発話が捨てられる）に化けた。しかも `synthesize` は2往復なので**最悪は `synthesisTimeoutMs` の2倍**
になり、旧既定の45秒でも足りなかったことがある。いまはサーバーが `GET` の応答を自分で打ち切って
503 を返すので、この制約そのものが無い。

`chatter-agent-speak`（`summarizer/` の AI要約）だけが読むキーの一覧・既定値・意味は
[`../core.md`](../core.md)「設定と環境変数」の「AI要約」にある。

- `aiSummaryEnabled` を有効にしたときの**代償は遅延の方が大きい**。要約は AI の生成なので、
  所要時間は**入力の長さから予測できない**（実機実測10件で相関が見られず、短い入力がタイムアウトし
  長い入力が10秒台で返ることもあった。詳細は下記と [`plugin.md`](./plugin.md)「AI要約の実機実測」）。秒数は環境で
  変わるので仕様として扱わないこと
- `aiSummaryTimeoutMs` の既定は**60秒**。実機実測10件（`summarizer.log`）では入力の長さと所要時間が
  相関せず、旧既定の30秒では10件中3件（30%）がタイムアウトしていた。**「実測値の N 倍」という決め方は
  していない**——相関しないものに倍率を掛けても意味が無いため。60秒は「旧既定30秒ではタイムアウトが
  3割起きた」という実測だけを根拠にした値で、秒数自体を仕様として扱わないことは変わらない
- `aiSummaryMaxPerDrain` は移植元の「滞留ガード」（同時実行数の待ち行列が閾値を超えたらスキップ）の読み替え。
  同期実行では待ち行列の概念が無いので、「1回のドレインで要約してよい回数の上限」に置き換えてある。
  1回のドレインは最悪 `aiSummaryMaxPerDrain × aiSummaryTimeoutMs` の間ロックを保持しうるため
  （**既定なら 3 × 60秒 = 180秒。上限まで上げ、かつタイムアウトが既定のままなら 8 × 60秒 = 480秒**。
  `aiSummaryTimeoutMs` 自体には実質的な上限が無い——`parseTimeoutMs` は `MAX_TIMER_MS` ≒ 24.8日でしか
  縛らないため、480秒は強制された天井ではない）、また `workerState.ts` の
  `SUMMARIZER_SESSION_LIMIT`（64）は「64 ÷ 8 = 8ドレイン分の要約セッションIDを覚えられる」計算になっている
  ため、上限だけを単独で動かさないこと

**`speechLogGenerations`（記録の退避世代数）は [#8](https://github.com/schwarz9791/chatter-agent/issues/8) で廃止した。** 誰も `speech.jsonl` を tail しなくなったので、
複数世代を繰り下げる必要がなくなり、`speechLogMaxBytes` を超えたら `speech.1.jsonl` に退避する1世代だけになった。
既存の `~/.config/chatter-agent/config.json` に `speechLogGenerations` が残っていると、`config.ts` の未知キー警告
（`[Config] ... の未知のキー "speechLogGenerations" は無視されます`）が CLI 起動のたびに出る。手で消すこと。
それ以前の環境で作られた `speech.2.jsonl` 以降のファイルも、以後は誰も読み書きしない孤児になる。消してよい。

config に載せない環境変数:

- `CHATTER_AGENT_CONFIG` — `config.json` の場所そのもの
- `CHATTER_AGENT_EMOTION_KEYWORDS` — `emotion-keywords.json` の場所そのもの
- `CHATTER_AGENT_DISABLE` — hook と CLI を無効化（無限ループ防止の第1層）
- `CHATTER_AGENT_CLI` — 開発時にバンドルを差し替える
- `CHATTER_AGENT_HOOK_DEBUG` — hook が受けた payload を `{root}/hook-debug.log` に落とす（hook 側だけ。→ [`plugin.md`](./plugin.md)「検証時の落とし穴」）

`CHATTER_AGENT_DISABLE` は `config.ts` の `isSpeakDisabled()` が判定する。**`1/true/yes/on` で無効、
それ以外（未設定・空・`0/false/no/off`・未知の値）は有効**で、`plugin/scripts/_lib.sh` の
`chatter_disabled` と同じトークン集合。以前は presence 判定（空文字以外はすべて truthy）だったため、
`CHATTER_AGENT_DISABLE=0` を「無効化の解除」のつもりで書くと逆に全発話が黙って止まっていた（[#4](https://github.com/schwarz9791/chatter-agent/issues/4)）。

## 実装で設計書から動いたこと

設計書（`_workspace/chatter-agent-design.md`）は一次情報だが、実装中に上書きした点がある。

1. **配信は `speech.jsonl` の tail ではなくディレクトリキュー。** 設計書 §4-4 / §6 は差分読み取りと
   ローテート追従を前提にしていたが、1つのファイルに記録と配信を兼ねさせると読み手だけが際限なく
   複雑になる（取りこぼしと二重配信を両方踏んだ）。分けた経緯は [#8](https://github.com/schwarz9791/chatter-agent/issues/8)、
   結果の契約は [`protocol.md`](../protocol.md)
2. **spool の処理順は mtime ではなく birthtime。** 当初は `<message_id>.jsonl` に delta ごと
   追記していたため、mtime が動き続けて `final:true` が大きく遅れて届くと先行メッセージの mtime
   が後発より新しくなり、順序が入れ替わっていた。**その後、spool の書き込みは追記から
   `<message_id>.<index>.json`（1 delta 1 ファイル、tmp + rename）へ変えた**（bash から任意長の
   追記を原子的にする移植可能な方法が無いため。→ [`plugin.md`](../plugin.md)）が、mtime を避ける
   理由は変わらない。1メッセージが複数ファイルに分かれた分、到着順は「グループの中で最新の
   ファイル」ではなく**必ず `index` が 0 のファイルの birthtime**で決める（→ `spool.ts` の
   `arrivalOrderOfMessage`）
3. **★ 発話の粒度はメッセージ単位。`final:true` を待つ。** 設計書 §2-4「★最重要★ `final:true` を
   待ってはいけない」を [#30](https://github.com/schwarz9791/chatter-agent/issues/30) で**反転させた**。
   理由（AI要約が成立しない / サーバー合成の req/min が7倍違う / `final` の待ち時間は実測で
   中央値 0秒）は [`plugin.md`](./plugin.md)「`final:true` を待つ方針は #30 で反転した」、契約は [`protocol.md`](../protocol.md)「発話の粒度」
4. **未確定なのはフェンスだけではなかった。** 設計書 §4-2 は「未閉じの ``` 以降は保留」としていたが、
   `cleanTextForSpeech` が領域ごと削除する構文は他にもある。ただし **#30 で保留の必要そのものが
   消えた**ので、いま残っているのは「読み上げたくない」フェンスと表の行だけ
   （→ 下の「消えた課題: ストリーミング中の保留」/ `src/text/unstableTail.ts`）

加えて、設計書に無い挙動を足した。

- **`final` が来なかったメッセージを、後続イベントの到着で救済する。** ESC 中断・クラッシュ・
  `index` 欠番でメッセージが閉じないことはある。後続イベントが来た時点でそのメッセージはもう伸びないので、
  そこで打ち切って全文を出し spool を消す。ただし**同一セッションの**後続に限る。spool はグローバルに
  1ディレクトリで、`MessageDisplay` は matcher 非対応で全セッションで発火するため、限定しないと
  Claude Code を2枚開いただけで**まだ伸びる途中のメッセージが分断される**
- **ack をフロー制御として入れた。** TTS は生成よりずっと遅いので、キューは実質バッファ。
  「起動時に空にする」「上限で古い方から捨てる」も同じ原則（古い発話は無価値）で説明がつく
- **AI要約（`summarizer/`、issue #31）を `assembleSentences` の外に置いた。** `cli/messageAssembler.ts` の
  `assembleSentences` は純粋関数として保ちたいので、要約は `cli/worker.ts` の `summarizeSentences` として
  `processMessage` 側に置き、文分割の結果を受けてから `deps.publish` の手前で呼ぶ。実行方式も移植元
  （非同期 spawn + セマフォによる同時実行数の制限）から `execFileSync` の同期実行に変えた。呼び出し元の
  `drainSpool` は完全に同期で、単一ワーカーのロックが直列化を担っているため、同時実行の制御自体が不要になった

### 消えた課題: ストリーミング中の保留

[#30](https://github.com/schwarz9791/chatter-agent/issues/30) で `final:true` を待つようになるまで、
CLI は delta が届くたびに全文を組み直し、**最後の文を保留して**それ以外を流していた。進捗は
「出力済みの文数」（`emitted`）だけをディスクに持ち、その前提は**既に出した範囲が後から変化しないこと**
だった。`unstableTail.ts` が未閉じの `<` / インラインコード / URL / 16進列まで切り落としていたのは、
`cleanTextForSpeech` がそれらを閉じた瞬間に**既に発話した文ごと**削除・変形するからで、
「一度喋ってから取り消す」事故を防ぐためだった。

**この設計ごと無くなった。** メッセージ全文が揃ってから1回だけ組み立てるので、既出範囲という概念が無い。
`emitted` / 進捗サイドカー / 「伸びると不安定になる」構文の保留は全部消えた。残っているのは
「**そもそも読み上げたくない**」もの（未閉じのフェンス、書きかけの表の行）だけで、これは `final` でも切る。

同じ着想（行境界で早期に確定させる `endsAtLineBoundary` など）をまた持ち込まないこと。
**前提が消えたのであって、当時の反証が無効になったわけではない** —
`safe` の末尾が `\n` であることと、`safe` の内部に未閉じ構文が残っていないことは別の話で、
delta 単位の早期確定を復活させるなら同じ回帰をもう一度踏む。

→ [#24](https://github.com/schwarz9791/chatter-agent/issues/24)（保留を外す条件の精緻化）はこれで無効になった。

## 既知の欠落

移植した `cleanTextForSpeech` が扱えていない記法がある。上流にもこれを保持する意図のテストは無く、
単なる未対応。

| 症状 | 例 |
|---|---|
| **URL 直後の `。` 以降が段落ごと消える** | `参考は https://x。まず読みます。次に実装します。` → `["参考は"]` |
| **`<` と `>` に挟まれた文が丸ごと消える** | `1 < 2 なので先に進みます。確認しました。3 > 2 です。` → `1  2 です。` |
| commit hash の正規表現が数値・英単語を食う | `5242880` / `1048576` / `defaced` が消える |
| 強調・リンク記法が残る | `**` / `*` / `__` / `~~`、`[text](` の残骸 |
| 約物だけの発話が `seq` を消費する | `すごい！！` → `["すごい！", "！"]` |

上2つが重く、**文が無言で消える**。どちらも「区切りを決め打ちした正規表現が、次の区切りまで走る」
という同じ形（URL は次の空白まで、タグは次の `>` まで）。残りは「記号が読み上げに混ざる」に留まる。

> `final` を待って1回だけ組み立てるので、**一度発話してから取り消す**事故は起きない
> （組み立て時にはもう `>` が来ているか、永久に来ないかのどちらかに決まっている）。
> `>` があると間の文が失われること自体は残っている。

**実機で頻度を見てから整形規則をまとめて見直す方針**にしたので、現状は
`src/cli/messageAssembler.test.ts` の「既知の欠落」ブロックで挙動を固定して可視化してある。
直すときはリンクを段8（URL除去）より**前**に処理する必要がある。

→ [#2 テキスト整形規則を見直す](https://github.com/schwarz9791/chatter-agent/issues/2)

## 後回しにしている課題

| | |
|---|---|
| [#2](https://github.com/schwarz9791/chatter-agent/issues/2) | テキスト整形規則の見直し（上記）。**実機で強調記号を踏んだ** — `**強調。**` が `**強調。` と `** 続き` に割れて読み上げられる |
| [#5](https://github.com/schwarz9791/chatter-agent/issues/5) | Linux で `birthtimeNs` が当てにならない（spool の命名で解く）。**macOS だけを対象にしている間は実害なし** |
| [#7](https://github.com/schwarz9791/chatter-agent/issues/7) | `cleanOrphans` の追加走査 |

> [#6](https://github.com/schwarz9791/chatter-agent/issues/6)（`messageAssembler` の O(N²) 再パース）は
> [#30](https://github.com/schwarz9791/chatter-agent/issues/30) で解消した。整形と文分割はメッセージあたり
> 1回しか走らない。**ただし `readMessage` は毎 delta で全 delta ファイルを読み直す**ので、
> ファイル読み取りの二乗性は残っている（480 delta で実測 474ms だった正規表現のコストとは別物で、
> 現状は問題になっていない）。
>
> WebSocket / HTTP の認証（[#3](https://github.com/schwarz9791/chatter-agent/issues/3)）は、既定 bind を
> ループバックにし、非ループバックには共有トークンを要求する形で決着している。残存リスク
> （DNS リバインディング）は [`protocol.md`](../protocol.md) の「セキュリティ」。
