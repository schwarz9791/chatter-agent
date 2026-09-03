#!/usr/bin/env node
/**
 * `chatter-agent-server` — 配信キューを WebSocket で流し、音声を HTTP で配る常駐プロセス。
 *
 * **判断ロジックはここに置かない**（docs/core.md）。何を配信済みとするか・ack をどう
 * クランプするかは `dispatcher.ts`、音声を作って持つかは `audioStore.ts` に出してある。
 * ここには配線と終了処理しか置かない。
 *
 * 起動順は **ロック → bind → 古いキューの掃除 → poll**。ロックが最初なのは、
 * bind より後ろだと「2台目が別ポートで bind に成功し、1台目の未配信キューを
 * 巻き込む」窓が残るため（F 参照）。
 *
 * ★ **WebSocket と HTTP は同じポート。** `http.Server` を先に作り、`ws` を載せてから
 *   listen する（順序の理由は `wsServer.ts`）。listen そのものは `createWsServer` が握る。
 */

import * as fs from "fs";
import * as path from "path";
import { createConfigStore } from "../core/config";
import { acquireLock } from "../core/lock";
import { getServerLockDir, getSpeechQueueDir, getSummarizerHomeDir, getSummarizerSessionsPath } from "../core/paths";
import { registerSummarizerSession } from "../core/summarizerSessions";
import { createSpeechQueue } from "../core/speechQueue";
import { createVoicevoxClient, flattenStyles, hasStyle } from "../tts/voicevoxClient";
import { createAudioStore, type Voice } from "./audioStore";
import { createControlApi } from "./controlApi";
import { describeEngineSkip, resolveEngineSpawn, startEngine, type EngineProcess } from "./engineProcess";
import { createDispatcher, type Dispatcher } from "./dispatcher";
import { createHttpServer } from "./httpServer";
import { createWsServer } from "./wsServer";

/**
 * 終了処理の1ステップの制限時間。
 *
 * ★ `CLOSE_GRACE_MS`（wsServer.close() が持つ terminate() 救済の猶予）より大きくすること。
 *   ここが `CLOSE_GRACE_MS` 以下だと、`step()` の watchdog が先に諦めて次へ進んでしまい、
 *   wsServer 側の救済（応答しないクライアントを terminate する経路）に到達できない。
 *   `SHUTDOWN_TIMEOUT_MS`（全体の上限）は超えないこと。
 */
const SHUTDOWN_STEP_TIMEOUT_MS = 2_500;
const SHUTDOWN_TIMEOUT_MS = 6_000;

/**
 * キューを見に行く間隔。
 *
 * ★ ファイル監視は使わない。単一ファイルの監視でローテートの取りこぼしを実測で踏んでおり、
 *   `readdir` の方が経路として単純で確実。
 *
 * ★ 50ms（20回/秒）にできるのは、`poll()` が呼ぶ `queue.list()` がファイル名の列挙だけで
 *   中身を読まないため。`list()` / `read()` に分ける前の `readAll()`（毎回キュー全件を
 *   readFileSync）は実測 7.3〜9.6ms/回（500件）かかっていたが、列挙だけなら 0.33ms/回に
 *   落ちる。20回/秒 × 0.33ms はコアの 0.7% 程度で、頻度を上げても誤差の範囲に収まる。
 */
const POLL_INTERVAL_MS = 50;

/**
 * 起動時に残す entry の新しさの境界。これより古い entry だけを捨てる。
 *
 * ★ [#30] でこの根拠は成立しなくなっている。以前は「CLI は delta ごとに起動されるので、
 *   1つのメッセージの文は複数回のドレインに分かれて publish される。無条件の全消しだと、
 *   たまたま直前のドレインで積まれた entry まで巻き添えにして、マスコットが段落の途中から
 *   喋り出す」という理由でこの時間条件を置いていた。今は `final` を待ってメッセージ全文を
 *   1回の enqueue でまとめて publish するので、途中から喋り出すことはそもそも起こらず、
 *   **メッセージを丸ごと残すか丸ごと捨てるかにしかならない**。
 *
 *   実際の挙動: 例えば30文のメッセージがサーバー再起動の11秒前に enqueue されていた場合、
 *   既定の `STARTUP_KEEP_MS = 10_000` では境界を超えているので丸ごと捨てられ、
 *   クライアントには1文も届かない。逆に9秒前なら丸ごと残る。
 *
 *   **消えること自体は方針として許容している**（10秒以上前に止まっていた発話を、今さら
 *   読み上げても体験として噛み合わない）。ここに残っているのは秒数の閾値であって、
 *   「途中から喋り出すのを防ぐ」という導出根拠はもう無い。
 *
 * [#30]: https://github.com/schwarz9791/chatter-agent/issues/30
 */
const STARTUP_KEEP_MS = 10_000;

/**
 * 合成が失敗したときに、エンジンの診断（話者一覧）を出し直す間隔。
 *
 * ★ 合成の失敗ごとに `listSpeakers` を叩くと、エンジンが落ちている間 1 req/s で
 *   繋ぎに行き続けることになる。診断は「設定が変わった / エンジンが起きた」を
 *   拾えれば十分なので、分単位で足りる。
 */
const ENGINE_RECHECK_INTERVAL_MS = 60_000;

/** 話者が見つからないときに案内する候補の数 */
const SPEAKER_HINT_LIMIT = 20;

/**
 * 疎通確認の結果。
 *
 * ★ **boolean にしないこと。** 意味は「`listSpeakers` に繋がったか」であって
 *   「話者 ID が実在するか」ではない。`true`/`false` だと後者と読み違えられ、
 *   読み違えたまま直すと**エンジンの二重起動**になる（→ `checkEngine`）。
 */
type EngineProbe = "reachable" | "unreachable";

/**
 * 終了処理の1ステップを制限時間つきで実行する。
 * まとめて1つの watchdog に任せると「諦めた」ことしか分からず、
 * どのリソースが閉じられなかったのか追えなくなるため、名指しで報告して先へ進む。
 */
async function step(label: string, work: () => Promise<unknown>): Promise<void> {
  const deadline = new Promise<void>((resolve) => setTimeout(resolve, SHUTDOWN_STEP_TIMEOUT_MS).unref());
  await Promise.race([
    work().then(
      () => undefined,
      (err: unknown) => console.error(`[Server] ${label} の終了処理に失敗:`, err),
    ),
    deadline.then(() =>
      console.warn(`[Server] ${label} の終了処理が ${SHUTDOWN_STEP_TIMEOUT_MS}ms で返りませんでした`),
    ),
  ]);
}

function installShutdown(cleanup: () => Promise<void>): void {
  let shuttingDown = false;

  const onSignal = (signal: NodeJS.Signals) => {
    if (shuttingDown) {
      process.exit(130); // 2回目は即座に
      return;
    }
    shuttingDown = true;
    console.log(`[Server] ${signal} を受信。終了します`);

    setTimeout(() => {
      console.warn("[Server] 終了処理が長引いたため強制終了します");
      process.exit(1);
    }, SHUTDOWN_TIMEOUT_MS).unref();

    void cleanup().then(() => process.exit(0));
  };

  process.on("SIGINT", onSignal);
  process.on("SIGTERM", onSignal);
  // ★ SIGHUP も拾う。**端末のウィンドウを閉じる**のは日常操作なのに、既定動作のまま死ぬと
  //   cleanup が走らず、`detached` で起こしたエンジンが孤児として残る（端末の SIGHUP は
  //   別プロセスグループのエンジンには届かない）。→ PR #52 のレビュー
  //
  // ★ **2回目の Ctrl-C（`process.exit(130)`）と watchdog（`process.exit(1)`）は今も
  //   cleanup を通らない。** 「早く死ぬ」を優先した既存の設計で、残った孤児は次回起動時の
  //   疎通確認（条件3）が再利用する
  process.on("SIGHUP", onSignal);
  // 常駐プロセスなので1件の rejection / 例外で落とさない
  process.on("unhandledRejection", (err) => console.error("[Server] Unhandled rejection:", err));
  process.on("uncaughtException", (err) => console.error("[Server] Uncaught exception:", err));
}

async function main(): Promise<void> {
  // ★ ロックは bind より前に取ること。配信キューのパス（{root}/speech）にはポートも
  //   インスタンス識別子も入っていないので、2台目が別ポートで bind に成功すると、
  //   起動時の掃除（下の dropOlderThan）が1台目の未配信キューを消し、定常状態でも
  //   両者が独立に配信して二重再生になる。「wipe は bind の後だから安全」という順序の
  //   工夫では、同じポートでの衝突にしか効かない。キューを所有できるサーバーを1台に絞る
  const serverLock = acquireLock(getServerLockDir());
  if (!serverLock) {
    console.error("[Server] 既に別の chatter-agent-server が動いています");
    process.exit(1);
  }

  const config = createConfigStore();
  const queueDir = getSpeechQueueDir();
  console.log(`[Server] config: ${config.filePath}`);
  console.log(`[Server] speech queue: ${queueDir}`);

  fs.mkdirSync(path.dirname(queueDir), { recursive: true });
  const queue = createSpeechQueue(queueDir);

  // ★ 音声はプロセス内にしか持たない（`audioStore.ts`）。合成は GET が来たときに走るので、
  //   誰も繋いでいない間はエンジンを一度も叩かない
  //
  // ★ **合成のたびに現在の config から組む。** config は参照のたびに mtime スタンプを見て
  //   読み直す作りなので、起動時の1回きりで固定すると `ttsSpeakerId` を直しても
  //   サーバーを再起動するまで効かない。無音の原因として真っ先に疑ってほしい値なので、
  //   直したらすぐ効く方がよい（クライアント側の警告もそこを名指しする）。
  //   クライアントの生成は object literal と closure だけなので、GET のたびに作って問題ない
  const currentVoice = (): Voice => ({
    baseUrl: config.get("ttsBaseUrl"),
    speakerId: config.get("ttsSpeakerId"),
    speedScale: config.get("ttsSpeedScale"),
  });
  const ttsFor = (voice: Voice) => createVoicevoxClient({ ...voice, timeoutMs: config.get("synthesisTimeoutMs") });

  let lastEngineCheckAt = Number.NEGATIVE_INFINITY;

  /**
   * **間引かずに**必ず見に行く。診断ログもここで出る。
   *
   * ★ これが「無音の原因が分からない」への本命の答え。`ttsSpeakerId` を間違えていると、
   *   ここが候補一覧を出す。エンジンが落ちているなら、繋がらない旨と `baseUrl` を出す。
   *
   * ★ 起動時のエンジン起動判定（条件3）はこちらを直接呼ぶ。`recheckEngine` 経由にすると
   *   60秒の間引きに引っかかって「繋がらないのに繋がった扱い」になりうる。
   */
  const probeEngine = async (): Promise<EngineProbe> => {
    // ★ await の前に進めること。同時に走った2本が二重に診断を出さないようにする
    lastEngineCheckAt = Date.now();
    const voice = currentVoice();
    return await checkEngine(ttsFor(voice), voice.speakerId);
  };

  /**
   * 「音声は出ないがテキストは流れる」という結論。**`checkEngine` ではなくここが出す。**
   * 起動時のプローブは直後に spawn するかもしれないので、結論を出せるのは状況を知っている側だけ。
   */
  const warnAudioUnavailable = (): void => {
    console.warn("[Server] 音声の GET は 503 を返します。テキストの配信は続きます");
  };

  /** 合成が失敗したときの診断。`ENGINE_RECHECK_INTERVAL_MS` に1回だけ実際に走る */
  const recheckEngine = async (): Promise<void> => {
    if (!config.get("ttsEnabled")) return;
    if (Date.now() - lastEngineCheckAt < ENGINE_RECHECK_INTERVAL_MS) return;
    // 合成が**実際に失敗した**あとの診断なので、繋がらなければ結論まで出してよい
    if ((await probeEngine()) === "unreachable") warnAudioUnavailable();
  };

  /**
   * 起こしたエンジン。**起こさなかった場合は `null` のまま**（GUI が上げている / スタブが居る /
   * `ttsSpawn=false` / リモート / コマンドが無い）。終了処理はこれを見て止める。
   */
  let engine: EngineProcess | null = null;
  /** 終了処理が始まったか。**起動判定が spawn する直前に見る**（下の `startEngineIfNeeded`） */
  let stopping = false;

  /**
   * エンジンが居なければ起こす（[#51]）。**起こすだけで、起動を待たない。**
   *
   * 条件は5つで、全部満たすときだけ起こす:
   * 1. `ttsEnabled` / 2. `ttsSpawn` / 3. **起動時の疎通確認に失敗した** /
   * 4. `ttsBaseUrl` がループバック / 5. コマンドが解決できた（4と5は `resolveEngineSpawn`）
   *
   * ★ **条件3が要。** 「まず繋いでみて、居なければ起こす」ことで、GUI 併用・verify のスタブ・
   *   別ポート運用のすべてが追加の分岐なしで素通りする（ポート衝突の判定コードが要らない）。
   *
   * [#51]: https://github.com/schwarz9791/chatter-agent/issues/51
   */
  const startEngineIfNeeded = async (): Promise<void> => {
    if (!config.get("ttsEnabled")) return; // 条件1（理由は上の起動ログで既出）

    // ★ 条件3。この1回は spawn の有無に関わらず必ず走り、話者 ID の診断もここで出る
    if ((await probeEngine()) === "reachable") return;

    if (!config.get("ttsSpawn")) {
      // ★ 黙らない。切ったまま忘れた人の症状が「無音」だけになるのが最悪の失敗の仕方
      console.log("[Server] ttsSpawn=false: 合成エンジンは起こしません");
      warnAudioUnavailable();
      return;
    }

    const plan = resolveEngineSpawn({
      baseUrl: config.get("ttsBaseUrl"),
      command: config.get("ttsSpawnCommand"),
      args: config.get("ttsSpawnArgs"),
    });
    if ("skip" in plan) {
      // 条件4 / 条件5。どちらも従来どおりの 503 運用に落ちるだけ。
      // ★ 文面は `engineProcess.ts` が組む（既知候補の一覧を持っているのがあちらなので）
      for (const line of describeEngineSkip(plan)) console.warn(line);
      warnAudioUnavailable();
      return;
    }

    // ★ **この判定と spawn の間に await を挟まないこと。** 挟むと「終了処理が始まった後に
    //   spawn する」窓ができ、detached の子がサーバーより長生きする（孤児のエンジンが残る）
    // ★ 名前から引いた実行ファイルは**必ず名指しする。** PATH には `~/.local/bin` も
    //   mise / asdf の shims も普通に載っている（実測で 7/7）ので、`run` のようなありふれた名前は
    //   別のバイナリに当たりうる。禁止はしない（`ttsSpawnCommand: "docker"` でコンテナの
    //   エンジンを起こす運用が潰れる）が、**黙って読み替えない**（→ PR #52 のレビュー）
    if (plan.resolvedFrom !== undefined) {
      console.warn(`[Server] ttsSpawnCommand "${plan.resolvedFrom}" を名前から解決しました: ${plan.command}`);
      console.warn("[Server]   意図した実行ファイルでなければ、絶対パスで指定してください");
    }

    if (stopping) return;
    engine = startEngine(plan);

    // ★ **起動を待たない。** ここで疎通を確かめ直すと、モデルロード中なので必ず失敗し、
    //   「繋がりません」という嘘の警告が出る。代わりに間引きを巻き戻して、最初の合成失敗で
    //   `recheckEngine` が即座に診断を出せるようにする。起動そのものに失敗した場合は
    //   `startEngine` の exit ハンドラが終了コードと stderr の末尾を出す
    lastEngineCheckAt = Number.NEGATIVE_INFINITY;
  };

  const audioStore = createAudioStore({
    currentVoice,
    // ★ 声は `audioStore` が1回だけ解決したものを受け取る。ここで config を読み直すと、
    //   キャッシュキーを決めた後・合成する前の書き換えで**別の声の WAV が入る**
    synthesize: (text, voice) => ttsFor(voice).synthesize(text),
  });

  /**
   * 設定パネル（#76）の制御 API。**書き込み口はループバック限定**（→ `server/httpServer.ts`）。
   *
   * ★ 話者一覧と合成は、キュー経由の合成と**同じ声・同じクライアント生成**を通す
   *   （`ttsFor(currentVoice())`）。別々に組むと、テストボタンだけ通って本番が鳴らない
   *   （またはその逆）という切り分けにくいズレになる。
   */
  const control = createControlApi({
    config,
    listSpeakers: async () => flattenStyles(await ttsFor(currentVoice()).listSpeakers()),
    // ★ `audioStore` を通さない。キューに無い文なので `lookup` が引けない
    synthesizePreview: (text) => ttsFor(currentVoice()).synthesize(text),
    summaryPreview: {
      getCommand: () => config.get("aiSummaryCommand"),
      getModel: () => config.get("aiSummaryModel"),
      getTimeoutMs: () => config.get("aiSummaryTimeoutMs"),
      homeDir: getSummarizerHomeDir(),
      // ★ 無限ループ防止の第2層。CLI の `worker.state.json` ではなく専用ファイルに書く
      //   （書き手を1人に保つため。→ `core/summarizerSessions.ts`）
      registerSessionId: (sessionId) => registerSummarizerSession(getSummarizerSessionsPath(), sessionId),
    },
  });

  const httpServer = createHttpServer({
    store: audioStore,
    control,
    // ★ 本文の権威はキュー。ack / trim で消えた entry の音声は作らない
    lookup: (seq) => queue.read(seq),
    allowedOrigins: config.get("allowedOrigins"),
    disabled: () => !config.get("ttsEnabled"),
    // GET を保留する上限。合成そのものの上限（`synthesisTimeoutMs`）とは別で、
    // ここで打ち切っても合成は続き、終わればキャッシュに入る（→ httpServer.ts）
    // ★ **関数で渡す**（`disabled` と同じ）。値にすると、`PATCH /v1/config` で
    //   `synthesisTimeoutMs` を変えてもここだけ再起動まで旧値のまま＝半分しか効かない
    responseTimeoutMs: () => config.get("synthesisTimeoutMs"),
    onSynthesisFailed: () => void recheckEngine(),
  });

  // wsServer の onConnect / onAck から参照するが、生成は wsServer の後（下記）。
  // どちらのコールバックも実際の接続・メッセージが来るまで呼ばれないので、
  // bind が終わってから dispatcher を作っても間に合う
  let dispatcher: Dispatcher;

  // ★ 監視より先にポートを押さえる。埋まっているなら何も触る前に落ちるべき
  const wsServer = await createWsServer({
    host: config.get("host"),
    port: config.get("port"),
    server: httpServer,
    allowedOrigins: config.get("allowedOrigins"),
    onConnect: (send) => dispatcher.catchUp(send),
    onAck: (seq, epoch) => dispatcher.ack(seq, epoch),
  });

  dispatcher = createDispatcher({
    queue,
    broadcast: (line) => wsServer.broadcast(line),
    audioEnabled: () => config.get("ttsEnabled"),
  });

  const bound = wsServer.address();
  console.log(`[Server] listening on ws://${bound.host}:${bound.port}`);
  if (config.get("ttsEnabled")) {
    console.log(`[Server] audio: http://${bound.host}:${bound.port}/audio/ (engine: ${config.get("ttsBaseUrl")})`);
  } else {
    console.log("[Server] ttsEnabled=false: 音声は配りません（クライアントは無音で ack します）");
  }
  if (bound.host === "0.0.0.0") {
    console.warn("[Server] 0.0.0.0 は無認証で LAN 全体に露出します。信頼できない網では host を 127.0.0.1 に");
  }

  // ★ サーバーは1台しかいない前提（上のロック）なので、「2台目が1台目のキューを消す」
  //   事故はここでは考えなくてよい。掃除は STARTUP_KEEP_MS の時間条件だけで判断する
  //   （全消し clear() ではない。落ちている間に書かれた古い entry だけを捨てる。
  //   理由は STARTUP_KEEP_MS のコメント参照）
  const wiped = queue.dropOlderThan(STARTUP_KEEP_MS);
  if (wiped > 0) console.log(`[Server] 起動前に溜まっていた ${wiped} 件を捨てました`);

  // ★ poll を握ること。1接続の送信で throw すると、そのポールの残り entry が
  //   `delivered` に入らないまま抜け、**次のポールで他のクライアントへ二重送信される**。
  //   常駐プロセスの `uncaughtException` ガードはプロセスを守るだけで、ここは守らない
  const poll = setInterval(() => {
    try {
      dispatcher.poll();
    } catch (err) {
      console.error("[Server] ポーリングに失敗しました:", err);
    }
  }, POLL_INTERVAL_MS);
  poll.unref();

  console.log("[Server] Ready");

  // ★ **エンジンの疎通で起動を止めないこと。** テキストの配信は音声と独立している。
  //   止めると、エンジンを起動し忘れているだけで発話そのものが1文も届かなくなり、
  //   クライアント側からは「数十秒の無音は正常」と区別できない（docs/protocol.md）。
  //   合成が要るタイミングで 503 を返す方が、原因が症状に出る。
  //
  // ★ 話者 ID の不一致は `/audio_query` の 4xx になり、全文が 503 になって**無音**になる。
  //   症状から設定ミスに辿り着けないので、起動時に候補を並べておく。
  //
  // ★ エンジンを起こすのもここ（#51）。**`Ready` より後ろのまま**にすること —— 前に出すと
  //   起動が疎通待ちで伸び、上の理由がそのまま当てはまる状態に戻る
  void startEngineIfNeeded().catch((err: unknown) => console.error("[Server] 合成エンジンの起動判定に失敗:", err));

  installShutdown(async () => {
    // ★ 先に立てること。起動判定がまだ走っていれば、spawn する直前で引き返す
    stopping = true;
    clearInterval(poll);
    // wsServer.close() は httpServer も閉じる（ws は options.server を閉じない → wsServer.ts）
    await step("websocket / http server", () => wsServer.close());
    // ★ **入口を閉じてから道具を捨てる。** 先にエンジンを落とすと、受理済みの
    //   `GET /audio/…` が最後の1件だけ 503 に化ける。
    //
    // ★ **step は2つまで。** `SHUTDOWN_STEP_TIMEOUT_MS`(2500) × 2 = 5000ms で
    //   `SHUTDOWN_TIMEOUT_MS`(6000) の内側に収まるが、3つ目を足すと 7500ms になって
    //   watchdog に食われる。この Issue で枠を使い切った
    await step("合成エンジン", async () => {
      await engine?.stop();
    });
    // ★ isStale() は所有印が読めるなら pid の生死だけで判定する（core/lock.ts）。
    //   このサーバーは常駐で staleMs（既定60秒）をとうに超えて動き続けるが、
    //   pid が生きている限り自分のロックを他プロセスに奪われることはない。
    //   ここで release() し損ねてクラッシュしても、pid は死んでいるので
    //   次回起動時の isStale() が即座に回収する
    serverLock.release();
  });
}

/**
 * エンジンに繋がるか、話者 ID が実在するかを見る。**待たないし、止めない。**
 * 結果はログに残すだけで、配信の判断は `GET /audio/…` のたびに行われる。
 *
 * ★ **起動時の1回だけにしないこと。** 起動時に `listSpeakers` が落ちると、そこで
 *   early return するので**話者 ID の検査そのものが行われない**。
 *   「player を先に立ち上げ、後から AivisSpeech を起動する」という最も普通の順序で
 *   `ttsSpeakerId` の診断が永久に出なくなる — これが「無音なのにログが数行しかない」の真因。
 *   合成が失敗するたびに呼び直す（間隔は `ENGINE_RECHECK_INTERVAL_MS` で間引く）。
 */
async function checkEngine(tts: ReturnType<typeof createVoicevoxClient>, speakerId: number): Promise<EngineProbe> {
  let speakers;
  try {
    speakers = await tts.listSpeakers();
  } catch (err) {
    console.warn(`[Server] 音声合成エンジンに繋がりません (${tts.baseUrl}): ${String(err)}`);
    // ★ **「503 になる」の結論はここで出さない。** 起動時のプローブから呼ばれたときは、
    //   直後にエンジンを起こすかもしれず、その場合この行は嘘になる（実機ログで、
    //   spawn する経路でだけ必ず出ていた）。結論は状況を知っている呼び出し側が出す
    //   （`warnAudioUnavailable`）。→ PR #52 のレビュー
    return "unreachable";
  }

  if (hasStyle(speakers, speakerId)) {
    console.log(`[Server] 音声合成エンジンに繋がりました (${tts.baseUrl}, speaker=${speakerId})`);
    return "reachable";
  }

  console.warn(`[Server] ttsSpeakerId=${speakerId} はこのエンジンに存在しません。音声は 503 になります`);
  for (const style of flattenStyles(speakers).slice(0, SPEAKER_HINT_LIMIT)) {
    console.warn(`[Server]   ${style.id}  ${style.label}`);
  }
  // ★ **話者は無いが、エンジンには繋がっている。** ここを "unreachable" にすると、
  //   スタブや GUI が生きているのに `ttsSpeakerId` だけ間違えている状態（verify-tts の
  //   シナリオ⑫がまさにこれ）で**エンジンを二重に起こす**
  return "reachable";
}

main().catch((err: unknown) => {
  if ((err as NodeJS.ErrnoException)?.code === "EADDRINUSE") {
    console.error("[Server] ポートが使用中です。config.json の port か CHATTER_AGENT_PORT を変えてください");
  } else {
    console.error("[Server] 起動に失敗しました:", err);
  }
  process.exit(1);
});
