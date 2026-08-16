/**
 * 発話キューの判断だけを持つ部品。**合成も再生も ack もここでは行わない。**
 *
 * `server/dispatcher.ts` と同じ理由で切り出してあるが、あちらより一段厳しくしてある。
 * dispatcher の副作用は同期の `broadcast` 1本だけなので注入で足りるが、player の副作用は
 * 非同期で、しかも**完了コールバックが状態機械に再入する**（cc-mascot の `useSpeech.ts` が
 * promise の中から `processQueueRef.current?.()` を呼ぶ形がまさにそれ）。注入した関数を
 * 機械の内側から呼ぶと「ループの途中で状態が変わる」再入バグをテストで捕まえられない。
 *
 * そこで **イベントを入れるとコマンドの配列が返る**形にした。副作用は `index.ts` の
 * 薄いドライバが実行し、その結果をまたイベントとして戻す。テストは
 * 「このイベント列でこのコマンド列が出る」を配列比較で固定できる。
 *
 * `state` は同じオブジェクトを in-place で更新する（Map を毎回コピーしない）。
 * 「純粋」の意味はあくまで**外部への副作用が無い**ことで、不変性ではない。
 */

import { hasSpeakableText } from "./speechFrame";
import type { SpeechRecord } from "../core/types";

export type ItemStatus = "pending" | "synthesizing" | "ready" | "playing" | "done";

export interface QueueItem {
  record: SpeechRecord;
  status: ItemStatus;
  /** `ready` 以降で、合成済み WAV のパス */
  file: string | null;
  /** 合成を試みた回数。`synthesisAttempts` に達したら諦める */
  attempts: number;
}

/**
 * ★ 非同期の結果を戻すイベントには必ず `epoch` を載せること。
 *
 *   `seq` は**エポックを跨いで一意ではない**。採番がやり直された直後に古い合成が返ってくると、
 *   `seq` だけで突き合わせる実装は**同じ seq の新しい item に別の文の音声を入れる**。
 *   「こんにちは」を鳴らしながら「さようなら」を ack する、という壊れ方をする。
 */
export type PlaybackEvent =
  | { kind: "received"; record: SpeechRecord }
  | { kind: "synthesized"; epoch: number; seq: number; file: string }
  | { kind: "synthesisFailed"; epoch: number; seq: number; reason: string }
  | { kind: "played"; epoch: number; seq: number }
  | { kind: "playbackFailed"; epoch: number; seq: number; reason: string }
  | { kind: "connected" }
  | { kind: "disconnected" }
  /** 時間で進む判断（stale / stall watchdog）のためだけに入れる */
  | { kind: "tick" };

export type PlaybackCommand =
  | { kind: "synthesize"; epoch: number; seq: number; text: string }
  | { kind: "play"; epoch: number; seq: number; file: string }
  /**
   * 累積 ack（「seq までは片付いた」）。
   *
   * ★ 送出の間引きは**ドライバの責務**。接続直後の追いつきで消費済みの entry が最大 500 件
   *   まとめて再送されると、ここからは 500 個の ack コマンドが出る。累積なので最大値の1回で
   *   足りる。ドライバ側で最大値を覚えて次のティックに1回だけ送ること。
   */
  | { kind: "ack"; seq: number }
  /**
   * ドライバが溜めている未送出の ack を捨てる。
   *
   * ★ エポックが変わった瞬間に必ず出すこと。ドライバ側の間引きバッファ（20ms）に旧エポックの
   *   ack が残っていると、**切断を挟まなくても**それが新しいサーバーに飛ぶ。`ackUpTo` は
   *   ファイル名で範囲削除するので、まだ喋っていない entry が消える。
   */
  | { kind: "dropPendingAck" }
  /** 使い終わった（あるいは捨てた）WAV を消す */
  | { kind: "discardFile"; epoch: number; seq: number; file: string }
  | { kind: "log"; message: string }
  | { kind: "warn"; message: string };

export interface PlaybackOptions {
  /** 再生中の1件を含めて、いくつ先まで合成を走らせるか。0 なら完全直列 */
  lookahead: number;
  /** これより古い発話は音を出さずに飛ばす。0 なら無効 */
  maxAgeMs: number;
  /** 合成を試みる上限回数。2 = 初回 + 1リトライ */
  synthesisAttempts: number;
  /** 消費済みキーの保持数 */
  seenCapacity: number;
  /** head が動かないまま この時間 が過ぎたら警告する。0 なら無効 */
  stallWarnMs: number;
}

export function createDefaultOptions(): PlaybackOptions {
  return {
    lookahead: 3,
    maxAgeMs: 0,
    synthesisAttempts: 2,
    seenCapacity: 512,
    stallWarnMs: 120_000,
  };
}

export interface PlaybackState {
  readonly options: PlaybackOptions;
  /**
   * 現在のエポックの通し番号。採番がやり直されるたびに +1 する。
   *
   * これはプロセス内のカウンタで、サーバー由来ではない。`seq` がエポックを跨いで一意でない以上、
   * **非同期の結果・一時ファイル・孤児・保留 ack のすべてを `(epoch, seq)` で識別する**必要がある。
   * `SpeechRecord` に generation id が載れば、これはそのまま置き換えられる（→ #29）。
   */
  epoch: number;
  /** seq → item。順序は seq の昇順で都度求める（挿入順とは限らない） */
  items: Map<number, QueueItem>;
  /**
   * 消費済みの `${seq}:${ts}`。
   *
   * ★ `seq` 単独（`Set<number>`）にしないこと。`~/.config/chatter-agent` を消すと CLI の採番が
   *   1 からやり直される。seq だけで覚えていると、新しい seq 1..N が「もう喋った」と判定されて
   *   **何百文でも一切喋らず、エラーも出ない**。`ts` を混ぜれば、再送は必ず同じ `ts` を持ち、
   *   新しいエポックの seq 1 は別の `ts` を持つので取り違えない。
   *
   *   `server/dispatcher.ts` が `delivered` を水位ではなく集合にしたのと同じ罠の、鏡像。
   */
  seen: Set<string>;
  /** 受け取った最大の seq と `ts`（ISO8601 なので辞書順＝時刻順）。**エポック変化の検出に使う** */
  maxSeqSeen: number;
  maxTsSeen: string;
  /**
   * **消費した**（＝ack を打った）最大の seq。`seen` から溢れた再送の検出に使う。
   *
   * ★ 受信ベースの `maxSeqSeen` と役割を分けること。エポック変化は「seq が戻ったのに ts は
   *   進んだ」で見るので受信ベースが要る。一方「seen から溢れた消費済みの再送」は、
   *   消費した範囲でしか起きないので消費ベースで判定する。1つの水位で兼ねると、
   *   接続直後の追いつきが seq 昇順で来なかっただけのフレームを**捨てて無音になる**。
   */
  maxSeqConsumed: number;
  /**
   * 切断中に確定した ack。累積 ack なので最大値だけ意味がある。
   *
   * ★ どのエポックのものかを持つこと。旧エポックの `ack(500)` を新エポックのサーバーに打つと、
   *   `dispatcher.ack` のクランプは `delivered` の範囲に値を押さえるだけで、`ackUpTo` は
   *   ファイル名で範囲削除するため、**まだ喋っていない seq 1, 2 が消える**。
   *   ws は必ず `open` → `message` の順に発火するので、`connected` の時点では
   *   「新エポックの最初のフレーム」はまだ届いていない。エポックの一致を見るしか止める手が無い。
   */
  pendingAck: { epoch: number; seq: number } | null;
  connected: boolean;
  /**
   * エポックリセットで items から外した、再生中の item。キーは `${epoch}:${seq}`。
   * 音は最後まで流すが、完了しても ack しない（もう別のエポックなので意味を持たない）。
   */
  orphans: Map<string, string | null>;
  /** head の走査結果。null = 未計算、-1 = 空。`trackInsert` / `trackDelete` が維持する */
  headCache: number | null;
  /** stall watchdog: 現在の head と、それが head になった時刻 */
  headSeq: number | null;
  headSince: number;
  stallWarned: boolean;
}

export function createPlaybackState(options: PlaybackOptions = createDefaultOptions()): PlaybackState {
  return {
    options,
    epoch: 0,
    items: new Map(),
    seen: new Set(),
    maxSeqSeen: 0,
    maxTsSeen: "",
    maxSeqConsumed: 0,
    pendingAck: null,
    connected: false,
    orphans: new Map(),
    headCache: null,
    headSeq: null,
    headSince: 0,
    stallWarned: false,
  };
}

function seenKey(record: SpeechRecord): string {
  return `${record.seq}:${record.ts}`;
}

/**
 * 最小の seq を `k` 件だけ、昇順で返す。
 *
 * ★ 全体をソートしないこと。必要なのは窓のぶん（既定 4 件）だけで、`items` は最大
 *   `speechQueueMaxEntries`（既定 500、上限なし）まで育つ。接続直後の追いつきでは
 *   フレームごとに `step()` が回るので、O(n log n) を毎回払うとイベントループが止まる。
 */
function smallestSeqs(state: PlaybackState, k: number): number[] {
  if (k <= 0) return [];
  const out: number[] = [];
  for (const seq of state.items.keys()) {
    if (out.length < k) {
      out.push(seq);
      out.sort((a, b) => a - b);
      continue;
    }
    if (seq >= out[out.length - 1]) continue;
    out[out.length - 1] = seq;
    out.sort((a, b) => a - b);
  }
  return out;
}

/**
 * head（最小 seq の item）。
 *
 * ★ 呼ばれる回数が多い（`step()` の1周で3回 + `checkStall`）ので、走査結果を持ち回る。
 *   挿入では最小値を更新するだけ、削除では head が消えたときにだけ再走査する。
 */
function headItem(state: PlaybackState): QueueItem | undefined {
  if (state.headCache === null) {
    let min = Infinity;
    for (const seq of state.items.keys()) if (seq < min) min = seq;
    state.headCache = min === Infinity ? -1 : min;
  }
  return state.headCache < 0 ? undefined : state.items.get(state.headCache);
}

function trackInsert(state: PlaybackState, seq: number): void {
  if (state.headCache === null) return;
  if (state.headCache < 0 || seq < state.headCache) state.headCache = seq;
}

function trackDelete(state: PlaybackState, seq: number): void {
  if (state.headCache === seq) state.headCache = null;
}

/** 消費した（＝ack を打った）ことを覚える。エポック判定の基準はここだけで進む */
function remember(state: PlaybackState, record: SpeechRecord): void {
  const key = seenKey(record);
  // 追い出しは**挿入順**（Set のイテレーション順）。数値の最小から追い出すと、
  // 採番やり直しの直後に「新しく来た小さい seq」を優先的に忘れることになり、
  // 次の再送で二度読み上げる
  state.seen.add(key);
  while (state.seen.size > state.options.seenCapacity) {
    const oldest = state.seen.values().next();
    if (oldest.done) break;
    state.seen.delete(oldest.value);
  }

  if (record.seq > state.maxSeqConsumed) state.maxSeqConsumed = record.seq;
}

function isStale(state: PlaybackState, item: QueueItem, now: number): boolean {
  const { maxAgeMs } = state.options;
  if (maxAgeMs <= 0) return false;
  const ts = Date.parse(item.record.ts);
  if (Number.isNaN(ts)) return false; // 読めない ts で発話を捨てない
  return now - ts > maxAgeMs;
}

/**
 * 終端へ落とす。
 *
 * ★ 失敗も再生完了もすべてここを通すこと。失敗を見つけた瞬間に ack を打つと、
 *   先読みのぶんだけ head を追い越す。`ack` は累積で、server の `speechQueue.ackUpTo` は
 *   `seq <= upTo` を**ファイル名で範囲削除**するので、まだ喋っていない手前の entry が
 *   キューから消え、そこから先の任意の切断（1013 / ping 無応答 / Ctrl-C）で失われる。
 *   失敗は「長さ 0 の再生」として扱う、と考えると迷わない。
 */
function finish(item: QueueItem): void {
  item.status = "done";
}

function orphanKey(epoch: number, seq: number): string {
  return `${epoch}:${seq}`;
}

/** ack を出すか、切断中なら溜める */
function emitAck(state: PlaybackState, seq: number, commands: PlaybackCommand[]): void {
  if (state.connected) {
    commands.push({ kind: "ack", seq });
    return;
  }
  // 溜めるときはエポックごと覚える。エポックが変われば下の resetEpoch が捨てる
  const held = state.pendingAck;
  state.pendingAck =
    held !== null && held.epoch === state.epoch
      ? { epoch: state.epoch, seq: Math.max(held.seq, seq) }
      : { epoch: state.epoch, seq };
}

/** 古くなった pending / ready を落とす。再生中には触らない（もう鳴っている） */
function markStale(state: PlaybackState, now: number, commands: PlaybackCommand[]): boolean {
  // 既定（0 = 無効）では判定するものが無い。step() のループから毎回呼ばれるので入口で抜ける
  if (state.options.maxAgeMs <= 0) return false;

  let changed = false;
  for (const item of state.items.values()) {
    if (item.status !== "pending" && item.status !== "ready") continue;
    if (!isStale(state, item, now)) continue;
    commands.push({ kind: "log", message: `seq=${item.record.seq} は古いので飛ばします` });
    finish(item);
    changed = true;
  }
  return changed;
}

/**
 * head が `done` である限り消費し、まとめて1回だけ ack する。
 * 累積 ack なので、連続した done に対して ack を何度も打つ必要は無い。
 */
function consumeHead(state: PlaybackState, commands: PlaybackCommand[]): boolean {
  let acked: number | null = null;

  for (;;) {
    const head = headItem(state);
    if (!head || head.status !== "done") break;

    const { seq } = head.record;
    if (head.file) commands.push({ kind: "discardFile", epoch: state.epoch, seq, file: head.file });
    state.items.delete(seq);
    trackDelete(state, seq);
    remember(state, head.record);
    acked = seq;
  }

  if (acked === null) return false;
  emitAck(state, acked, commands);
  return true;
}

/** head が鳴らせる状態なら鳴らす。head より先には進まない（順序の保証はここ1点） */
function startPlayback(state: PlaybackState, commands: PlaybackCommand[]): boolean {
  const head = headItem(state);
  if (!head || head.status !== "ready" || !head.file) return false;
  head.status = "playing";
  commands.push({ kind: "play", epoch: state.epoch, seq: head.record.seq, file: head.file });
  return true;
}

/**
 * 先読みの窓を埋める。
 *
 * ★ 窓は「seq 昇順に並べた**生存 item の位置**で先頭 `lookahead + 1` 件」。
 *   「再生中の seq + lookahead」という**数値の窓にしないこと**。seq の飛びは仕様
 *   （CLI の `trim`、サーバー再起動）で、一度飛ぶと数値窓は対象ゼロになり、
 *   **音は出るのに先読みだけが恒久的に無効化される**（しかも気づけない）。
 */
function fillWindow(state: PlaybackState, commands: PlaybackCommand[]): boolean {
  const window = smallestSeqs(state, state.options.lookahead + 1);
  let changed = false;

  for (const seq of window) {
    const item = state.items.get(seq);
    // done は窓の枠を1つ食うが、head に来た瞬間に消えるので実害は無い。
    // ここで選んでしまうと合成を無限に投げ直すことになるので必ず除く
    if (!item || item.status !== "pending") continue;

    // 約物だけの断片は合成に出さない。/audio_query は空の WAV か 4xx を返す
    if (!hasSpeakableText(item.record.text)) {
      finish(item);
      changed = true;
      continue;
    }

    item.status = "synthesizing";
    item.attempts++;
    commands.push({ kind: "synthesize", epoch: state.epoch, seq, text: item.record.text });
    changed = true;
  }

  return changed;
}

function checkStall(state: PlaybackState, now: number, commands: PlaybackCommand[]): void {
  const head = headItem(state);
  const seq = head ? head.record.seq : null;

  if (seq !== state.headSeq) {
    state.headSeq = seq;
    state.headSince = now;
    state.stallWarned = false;
    return;
  }

  const { stallWarnMs } = state.options;
  if (stallWarnMs <= 0 || seq === null || state.stallWarned) return;
  if (now - state.headSince < stallWarnMs) return;

  // 無音は症状として何も語らない。最後の保険として、どこで止まったかだけは残す
  state.stallWarned = true;
  const status = head ? head.status : "?";
  commands.push({
    kind: "warn",
    message: `seq=${seq} が ${Math.round((now - state.headSince) / 1000)} 秒 ${status} のまま進んでいません`,
  });
}

/**
 * 状態を進められるだけ進める。
 *
 * ★ **すべてのイベントから必ずこれを通すこと。** cc-mascot は投入時に必ず合成を開始するので
 *   `pending` が滞留しないが、先読みの窓を入れると滞留する。「消費したときに窓を再評価する」
 *   経路が抜けていると、`lookahead + 1` 文目以降が永久に無音になる（移植で最も踏みやすい穴）。
 */
function step(state: PlaybackState, now: number, commands: PlaybackCommand[]): void {
  // 各操作は item を減らすか status を単調に進める（pending → synthesizing → ready →
  // playing → done → 削除）ので必ず収束する。guard は将来の改変に対する保険
  for (let guard = 0; guard < 1000; guard++) {
    const staled = markStale(state, now, commands);
    const consumed = consumeHead(state, commands);
    const started = startPlayback(state, commands);
    const filled = fillWindow(state, commands);
    if (!staled && !consumed && !started && !filled) break;
  }
  checkStall(state, now, commands);
}

/**
 * 採番がやり直された（`~/.config/chatter-agent` の削除、バックアップ復元）ときの後始末。
 *
 * 再生中のものだけは最後まで流す（音を途中で切る方が事故に聞こえる）が、
 * **その完了で ack は打たない**。もう別のエポックなので、その seq に意味が無い。
 */
function resetEpoch(state: PlaybackState, commands: PlaybackCommand[]): void {
  commands.push({ kind: "warn", message: "seq の採番がやり直されました。再生キューの状態をリセットします" });

  const previous = state.epoch;
  state.epoch++;

  for (const [seq, item] of state.items) {
    if (item.status === "playing") {
      state.orphans.set(orphanKey(previous, seq), item.file);
    } else if (item.file) {
      commands.push({ kind: "discardFile", epoch: previous, seq, file: item.file });
    }
    state.items.delete(seq);
    trackDelete(state, seq);
  }

  state.seen.clear();
  state.maxSeqSeen = 0;
  state.maxTsSeen = "";
  state.maxSeqConsumed = 0;
  state.pendingAck = null;
  state.headSeq = null;
  state.stallWarned = false;
  // ドライバ側の間引きバッファに残っている旧エポックの ack も落とす
  commands.push({ kind: "dropPendingAck" });
}

function onReceived(state: PlaybackState, record: SpeechRecord, commands: PlaybackCommand[]): void {
  const { seq, ts } = record;
  const key = seenKey(record);

  if (state.seen.has(key)) {
    // 消費済みのものが再送された ＝ サーバー側にまだ entry が残っている。
    // ack が届く前に切断された / サーバー再起動で `delivered` が空になり
    // `dispatcher.ack` が upTo=0 で捨てた、のどちらか。ack を打ち直さないと永久に残る
    emitAck(state, seq, commands);
    return;
  }

  const existing = state.items.get(seq);
  if (existing) {
    // 処理中のものの再送。同じ ts なら黙って捨てる（合成をやり直す意味が無い）
    if (existing.record.ts === ts) return;
    // 同じ seq で別の ts ＝ エポックが変わっている
    resetEpoch(state, commands);
  } else if (seq <= state.maxSeqSeen && ts > state.maxTsSeen) {
    // seq が戻っているのに ts は進んでいる ＝ 採番がやり直された
    resetEpoch(state, commands);
  } else if (seq <= state.maxSeqConsumed) {
    // ★ 消費済みの範囲なのに `seen` に無い ＝ 上限から溢れたキーの再送。
    //   ここを resetEpoch に落とすと、追いつきが `seenCapacity` を超えるたびに状態を捨てて
    //   **同じ文を2回喋る**。`seenCapacity` はサーバー側の設定とズレうるので溢れは起きる
    emitAck(state, seq, commands);
    return;
  }

  state.items.set(seq, { record, status: "pending", file: null, attempts: 0 });
  trackInsert(state, seq);
  if (seq > state.maxSeqSeen) state.maxSeqSeen = seq;
  if (ts > state.maxTsSeen) state.maxTsSeen = ts;

  // ここまで来たなら、このフレームは今のエポックのもの。溜めてある ack を出してよい
  flushPendingAck(state, commands);
}

/**
 * 切断中に溜めた ack を、**エポックが変わっていないと確認できてから**送る。
 *
 * ★ `connected` では呼ばない（上の case のコメント参照）。呼び出し口は
 *   「今のエポックのフレームを受け入れた直後」の1箇所だけにする。
 */
function flushPendingAck(state: PlaybackState, commands: PlaybackCommand[]): void {
  const held = state.pendingAck;
  if (held === null || !state.connected) return;
  state.pendingAck = null;
  if (held.epoch === state.epoch) commands.push({ kind: "ack", seq: held.seq });
}

/** 孤児（エポックリセットで items から外した再生中の item）の後始末。扱ったら true */
function settleOrphan(state: PlaybackState, epoch: number, seq: number, commands: PlaybackCommand[]): boolean {
  const key = orphanKey(epoch, seq);
  if (!state.orphans.has(key)) return false;
  const file = state.orphans.get(key);
  if (file) commands.push({ kind: "discardFile", epoch, seq, file });
  state.orphans.delete(key);
  return true;
}

/**
 * イベントを1つ入れて、実行すべきコマンドを受け取る。`state` は in-place で更新される。
 */
export function reduce(state: PlaybackState, event: PlaybackEvent, now: number): PlaybackCommand[] {
  const commands: PlaybackCommand[] = [];

  switch (event.kind) {
    case "received":
      onReceived(state, event.record, commands);
      break;

    case "synthesized": {
      // ★ エポックを先に見る。採番のやり直しを跨いだ結果を新しい item に入れると、
      //   別の文の音声で ready になり、鳴っている内容と ack がずれる
      const item = event.epoch === state.epoch ? state.items.get(event.seq) : undefined;
      // エポックリセットや stale で消えた後に合成が返ってきた。WAV だけ捨てる
      if (!item || item.status !== "synthesizing") {
        commands.push({ kind: "discardFile", epoch: event.epoch, seq: event.seq, file: event.file });
        break;
      }
      item.status = "ready";
      item.file = event.file;
      break;
    }

    case "synthesisFailed": {
      const item = event.epoch === state.epoch ? state.items.get(event.seq) : undefined;
      if (!item || item.status !== "synthesizing") break;
      if (item.attempts < state.options.synthesisAttempts) {
        // pending へ戻せば、次の step で窓が拾い直す
        item.status = "pending";
        break;
      }
      commands.push({ kind: "warn", message: `seq=${event.seq} の合成に失敗したので飛ばします: ${event.reason}` });
      finish(item);
      break;
    }

    case "played": {
      if (settleOrphan(state, event.epoch, event.seq, commands)) break;
      const item = event.epoch === state.epoch ? state.items.get(event.seq) : undefined;
      if (!item || item.status !== "playing") break;
      finish(item);
      break;
    }

    case "playbackFailed": {
      if (settleOrphan(state, event.epoch, event.seq, commands)) break;
      const item = event.epoch === state.epoch ? state.items.get(event.seq) : undefined;
      if (!item || item.status !== "playing") break;
      // 再生はリトライしない。途中まで鳴った文がもう一度頭から鳴る
      commands.push({ kind: "warn", message: `seq=${event.seq} の再生に失敗しました: ${event.reason}` });
      finish(item);
      break;
    }

    case "connected":
      // ★ ここで保留 ack を流さないこと。ws は必ず `open` → `message` の順に発火するので、
      //   この時点では**サーバーが作り直されたかどうかを知る手段が無い**。
      //   旧エポックの `ack(500)` を新しいサーバーに打つと、`dispatcher.ack` は
      //   `max(delivered ≤ 500)` にクランプし、`ackUpTo` はファイル名で範囲削除するため、
      //   **配信済み・未発話の entry**（長い切断なら最大 500 件）が消えて復旧できない。
      //   最初のフレームでエポックが変わっていないと確認できてから `flushPendingAck` で流す
      state.connected = true;
      break;

    case "disconnected":
      // ★ items を触らない。再送は同じ ts で来るので重複排除が拾うし、捨てると
      //   切断のたびに合成をやり直して数秒の無音が入る。再生中の音も止めない
      state.connected = false;
      break;

    case "tick":
      break;
  }

  step(state, now, commands);
  return commands;
}
