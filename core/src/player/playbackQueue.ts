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

/**
 * `done` に落ちた理由。
 *
 * ★ 失敗も再生完了もすべて `done` を経由させること。失敗を見つけた瞬間に ack を打つと、
 *   先読みのぶんだけ head を追い越す。`ack` は累積で、server の `speechQueue.ackUpTo` は
 *   `seq <= upTo` を**ファイル名で範囲削除**するので、まだ喋っていない手前の entry が
 *   キューから消える。そこから先の任意の切断（1013 / ping 無応答 / Ctrl-C）で、
 *   その entry は再送されないまま恒久的に失われる。
 *
 *   失敗は「長さ 0 の再生」として扱う、と考えると迷わない。
 */
export type DoneReason = "played" | "synthesis-failed" | "playback-failed" | "empty-text" | "stale";

export interface QueueItem {
  record: SpeechRecord;
  status: ItemStatus;
  /** `ready` 以降で、合成済み WAV のパス */
  file: string | null;
  doneReason: DoneReason | null;
  /** 合成を試みた回数。`synthesisAttempts` に達したら諦める */
  attempts: number;
}

export type PlaybackEvent =
  | { kind: "received"; record: SpeechRecord }
  | { kind: "synthesized"; seq: number; file: string }
  | { kind: "synthesisFailed"; seq: number; reason: string }
  | { kind: "played"; seq: number }
  | { kind: "playbackFailed"; seq: number; reason: string }
  | { kind: "connected" }
  | { kind: "disconnected" }
  /** 時間で進む判断（stale / stall watchdog）のためだけに入れる */
  | { kind: "tick" };

export type PlaybackCommand =
  | { kind: "synthesize"; seq: number; text: string }
  | { kind: "play"; seq: number; file: string }
  /**
   * 累積 ack（「seq までは片付いた」）。
   *
   * ★ 送出の間引きは**ドライバの責務**。接続直後の追いつきで消費済みの entry が最大 500 件
   *   まとめて再送されると、ここからは 500 個の ack コマンドが出る。累積なので最大値の1回で
   *   足りる。ドライバ側で最大値を覚えて次のティックに1回だけ送ること。
   */
  | { kind: "ack"; seq: number }
  /** 使い終わった（あるいは捨てた）WAV を消す */
  | { kind: "discardFile"; seq: number; file: string }
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
  /** 受け取った最大の seq。エポック変化の検出に使う */
  maxSeqSeen: number;
  /**
   * 切断中に確定した ack。累積 ack なので最大値だけ意味がある。
   *
   * ★ エポック変化を観測したら**捨てること**。旧エポックの `ack(500)` を新エポックの
   *   サーバーに打つと、`dispatcher.ack` のクランプは `delivered` の範囲に値を押さえるだけで、
   *   `ackUpTo` はファイル名で範囲削除するため、**まだ喋っていない seq 1, 2 が消える**。
   */
  pendingAck: number | null;
  connected: boolean;
  /**
   * エポックリセットで items から外した、再生中の item。
   * 音は最後まで流すが、完了しても ack しない（もう別のエポックなので意味を持たない）。
   */
  orphans: Map<number, string | null>;
  /** stall watchdog: 現在の head と、それが head になった時刻 */
  headSeq: number | null;
  headSince: number;
  stallWarned: boolean;
}

export function createPlaybackState(options: PlaybackOptions = createDefaultOptions()): PlaybackState {
  return {
    options,
    items: new Map(),
    seen: new Set(),
    maxSeqSeen: 0,
    pendingAck: null,
    connected: false,
    orphans: new Map(),
    headSeq: null,
    headSince: 0,
    stallWarned: false,
  };
}

function seenKey(record: SpeechRecord): string {
  return `${record.seq}:${record.ts}`;
}

/** seq 昇順。Map の挿入順は受信順であって seq 順とは限らない（再接続の追いつきなど） */
function sortedSeqs(state: PlaybackState): number[] {
  return [...state.items.keys()].sort((a, b) => a - b);
}

function headItem(state: PlaybackState): QueueItem | undefined {
  let min = Infinity;
  for (const seq of state.items.keys()) if (seq < min) min = seq;
  return min === Infinity ? undefined : state.items.get(min);
}

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
}

function isStale(state: PlaybackState, item: QueueItem, now: number): boolean {
  const { maxAgeMs } = state.options;
  if (maxAgeMs <= 0) return false;
  const ts = Date.parse(item.record.ts);
  if (Number.isNaN(ts)) return false; // 読めない ts で発話を捨てない
  return now - ts > maxAgeMs;
}

function finish(item: QueueItem, reason: DoneReason): void {
  item.status = "done";
  item.doneReason = reason;
}

/** ack を出すか、切断中なら溜める */
function emitAck(state: PlaybackState, seq: number, commands: PlaybackCommand[]): void {
  if (state.connected) {
    commands.push({ kind: "ack", seq });
    return;
  }
  state.pendingAck = state.pendingAck === null ? seq : Math.max(state.pendingAck, seq);
}

/** 古くなった pending / ready を落とす。再生中には触らない（もう鳴っている） */
function markStale(state: PlaybackState, now: number, commands: PlaybackCommand[]): boolean {
  let changed = false;
  for (const item of state.items.values()) {
    if (item.status !== "pending" && item.status !== "ready") continue;
    if (!isStale(state, item, now)) continue;
    commands.push({ kind: "log", message: `seq=${item.record.seq} は古いので飛ばします` });
    finish(item, "stale");
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
    if (head.file) commands.push({ kind: "discardFile", seq, file: head.file });
    state.items.delete(seq);
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
  commands.push({ kind: "play", seq: head.record.seq, file: head.file });
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
  const window = sortedSeqs(state).slice(0, state.options.lookahead + 1);
  let changed = false;

  for (const seq of window) {
    const item = state.items.get(seq);
    // done は窓の枠を1つ食うが、head に来た瞬間に消えるので実害は無い。
    // ここで選んでしまうと合成を無限に投げ直すことになるので必ず除く
    if (!item || item.status !== "pending") continue;

    // 約物だけの断片は合成に出さない。/audio_query は空の WAV か 4xx を返す
    if (!hasSpeakableText(item.record.text)) {
      finish(item, "empty-text");
      changed = true;
      continue;
    }

    item.status = "synthesizing";
    item.attempts++;
    commands.push({ kind: "synthesize", seq, text: item.record.text });
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

  for (const [seq, item] of state.items) {
    if (item.status === "playing") {
      state.orphans.set(seq, item.file);
    } else if (item.file) {
      commands.push({ kind: "discardFile", seq, file: item.file });
    }
    state.items.delete(seq);
  }

  state.seen.clear();
  state.maxSeqSeen = 0;
  state.pendingAck = null;
  state.headSeq = null;
  state.stallWarned = false;
}

function onReceived(state: PlaybackState, record: SpeechRecord, commands: PlaybackCommand[]): void {
  const { seq } = record;
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
    if (existing.record.ts === record.ts) return;
    // 同じ seq で別の ts ＝ エポックが変わっている
    resetEpoch(state, commands);
  } else if (seq <= state.maxSeqSeen) {
    // 知らない (seq, ts) なのに seq が戻っている ＝ エポックが変わっている
    resetEpoch(state, commands);
  }

  state.items.set(seq, { record, status: "pending", file: null, doneReason: null, attempts: 0 });
  if (seq > state.maxSeqSeen) state.maxSeqSeen = seq;
}

/** 孤児（エポックリセットで items から外した再生中の item）の後始末。扱ったら true */
function settleOrphan(state: PlaybackState, seq: number, commands: PlaybackCommand[]): boolean {
  if (!state.orphans.has(seq)) return false;
  const file = state.orphans.get(seq);
  if (file) commands.push({ kind: "discardFile", seq, file });
  state.orphans.delete(seq);
  return true;
}

/**
 * イベントを1つ入れて、実行すべきコマンドを受け取る。
 *
 * `state` は in-place で更新され、同じ参照が返る（呼び出し側の書き味を揃えるためだけの戻り値）。
 */
export function reduce(
  state: PlaybackState,
  event: PlaybackEvent,
  now: number,
): { state: PlaybackState; commands: PlaybackCommand[] } {
  const commands: PlaybackCommand[] = [];

  switch (event.kind) {
    case "received":
      onReceived(state, event.record, commands);
      break;

    case "synthesized": {
      const item = state.items.get(event.seq);
      // エポックリセットや stale で消えた後に合成が返ってきた。WAV だけ捨てる
      if (!item || item.status !== "synthesizing") {
        commands.push({ kind: "discardFile", seq: event.seq, file: event.file });
        break;
      }
      item.status = "ready";
      item.file = event.file;
      break;
    }

    case "synthesisFailed": {
      const item = state.items.get(event.seq);
      if (!item || item.status !== "synthesizing") break;
      if (item.attempts < state.options.synthesisAttempts) {
        // pending へ戻せば、次の step で窓が拾い直す
        item.status = "pending";
        break;
      }
      commands.push({ kind: "warn", message: `seq=${event.seq} の合成に失敗したので飛ばします: ${event.reason}` });
      finish(item, "synthesis-failed");
      break;
    }

    case "played": {
      if (settleOrphan(state, event.seq, commands)) break;
      const item = state.items.get(event.seq);
      if (!item || item.status !== "playing") break;
      finish(item, "played");
      break;
    }

    case "playbackFailed": {
      if (settleOrphan(state, event.seq, commands)) break;
      const item = state.items.get(event.seq);
      if (!item || item.status !== "playing") break;
      // 再生はリトライしない。途中まで鳴った文がもう一度頭から鳴る
      commands.push({ kind: "warn", message: `seq=${event.seq} の再生に失敗しました: ${event.reason}` });
      finish(item, "playback-failed");
      break;
    }

    case "connected":
      state.connected = true;
      if (state.pendingAck !== null) {
        commands.push({ kind: "ack", seq: state.pendingAck });
        state.pendingAck = null;
      }
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
  return { state, commands };
}
