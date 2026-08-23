import { describe, it, expect } from "vitest";
import { createDefaultOptions, createPlaybackState, reduce } from "./playbackQueue";
import type { PlaybackCommand, PlaybackEvent, PlaybackOptions, PlaybackState } from "./playbackQueue";
import { buildAudioPath } from "../core/audioPath";
import type { SpeechFrame } from "../core/types";

const T0 = Date.parse("2026-08-15T00:00:00.000Z");

/** サーバーが名乗る採番の世代（#29）。`E2` へ切り替えることが「採番のやり直し」の再現になる */
const E1 = "gen-1";
const E2 = "gen-2";

function record(seq: number, overrides: Partial<SpeechFrame> = {}): SpeechFrame {
  const epoch = overrides.epoch ?? E1;
  return {
    epoch,
    seq,
    ts: new Date(T0 + seq * 1000).toISOString(),
    source: "claude-code",
    sessionId: "sess-1",
    turnId: "turn-1",
    messageId: "m1",
    kind: "assistant",
    text: `文${seq}。`,
    emotion: "neutral",
    audio: { path: buildAudioPath(epoch, seq), format: "wav" },
    ...overrides,
  };
}

function start(overrides: Partial<PlaybackOptions> = {}): PlaybackState {
  const state = createPlaybackState({ ...createDefaultOptions(), ...overrides });
  // 既定は未接続。ほとんどのテストは繋がった状態を見たいので進めておく
  reduce(state, { kind: "connected" }, T0);
  return state;
}

/** イベントを順に流し、出たコマンドを全部集める */
function run(state: PlaybackState, events: PlaybackEvent[], now = T0): PlaybackCommand[] {
  const out: PlaybackCommand[] = [];
  for (const event of events) out.push(...reduce(state, event, now));
  return out;
}

function only<K extends PlaybackCommand["kind"]>(
  commands: PlaybackCommand[],
  kind: K,
): Extract<PlaybackCommand, { kind: K }>[] {
  return commands.filter((c) => c.kind === kind) as Extract<PlaybackCommand, { kind: K }>[];
}

/** 合成 → 再生 → 完了 を1文ぶん通す */
function speak(state: PlaybackState, seq: number, now = T0): PlaybackCommand[] {
  return run(
    state,
    [
      { kind: "audioReady", epoch: 0, seq, file: `/tmp/${seq}.wav` },
      { kind: "played", epoch: 0, seq },
    ],
    now,
  );
}

describe("順序と先読み", () => {
  it("受信すると合成が始まり、合成が終われば head から再生される", () => {
    const state = start();
    const queued = run(state, [{ kind: "received", record: record(1) }]);
    expect(only(queued, "fetchAudio")).toEqual([{ kind: "fetchAudio", epoch: 0, seq: 1, path: buildAudioPath(E1, 1) }]);

    const playing = run(state, [{ kind: "audioReady", epoch: 0, seq: 1, file: "/tmp/1.wav" }]);
    expect(only(playing, "play")).toEqual([{ kind: "play", epoch: 0, seq: 1, file: "/tmp/1.wav" }]);
  });

  it("先読みの件数だけ合成を先行させる（再生中の1件を含む）", () => {
    const state = start({ lookahead: 2 });
    const commands = run(
      state,
      [1, 2, 3, 4, 5].map((seq) => ({ kind: "received", record: record(seq) })),
    );
    // lookahead=2 なら head を含めて3件まで
    expect(only(commands, "fetchAudio").map((c) => c.seq)).toEqual([1, 2, 3]);
  });

  it("lookahead=0 なら完全直列になる", () => {
    const state = start({ lookahead: 0 });
    const commands = run(
      state,
      [1, 2, 3].map((seq) => ({ kind: "received", record: record(seq) })),
    );
    expect(only(commands, "fetchAudio").map((c) => c.seq)).toEqual([1]);
  });

  it("★ 後ろの合成が先に終わっても head を追い越さない", () => {
    const state = start();
    run(
      state,
      [1, 2].map((seq) => ({ kind: "received", record: record(seq) })),
    );

    // seq 2 だけ合成完了
    const commands = run(state, [{ kind: "audioReady", epoch: 0, seq: 2, file: "/tmp/2.wav" }]);
    expect(only(commands, "play")).toEqual([]);

    // seq 1 が揃って初めて 1 から鳴る
    const after = run(state, [{ kind: "audioReady", epoch: 0, seq: 1, file: "/tmp/1.wav" }]);
    expect(only(after, "play").map((c) => c.seq)).toEqual([1]);
  });

  it("★ 消費するたびに窓を再評価する（lookahead+1 文目以降が無音にならない）", () => {
    // cc-mascot は投入時に必ず合成を開始するので pending が滞留しないが、
    // 窓を入れると滞留する。ここが抜けると 4 文目以降が永久に喋られない
    const state = start({ lookahead: 2 });
    run(
      state,
      [1, 2, 3, 4, 5].map((seq) => ({ kind: "received", record: record(seq) })),
    );

    const commands = speak(state, 1);
    expect(only(commands, "fetchAudio").map((c) => c.seq)).toEqual([4]);

    const next = speak(state, 2);
    expect(only(next, "fetchAudio").map((c) => c.seq)).toEqual([5]);
  });

  it("★ seq に飛びがあっても先読みが止まらない（数値の窓ではなく位置の窓）", () => {
    // CLI の trim やサーバー再起動で seq は飛ぶ。数値窓（head + lookahead）だと
    // 対象ゼロになり、音は出るのに先読みだけが恒久的に効かなくなる
    const state = start({ lookahead: 2 });
    const commands = run(
      state,
      [10, 51, 52, 53].map((seq) => ({ kind: "received", record: record(seq) })),
    );
    expect(only(commands, "fetchAudio").map((c) => c.seq)).toEqual([10, 51, 52]);
  });

  it("受信順が seq 順でなくても seq 昇順に再生する（接続直後の追いつき）", () => {
    const state = start();
    run(
      state,
      [3, 1, 2].map((seq) => ({ kind: "received", record: record(seq) })),
    );
    const commands = run(state, [
      { kind: "audioReady", epoch: 0, seq: 3, file: "/tmp/3.wav" },
      { kind: "audioReady", epoch: 0, seq: 2, file: "/tmp/2.wav" },
      { kind: "audioReady", epoch: 0, seq: 1, file: "/tmp/1.wav" },
    ]);
    expect(only(commands, "play").map((c) => c.seq)).toEqual([1]);
  });
});

describe("ack", () => {
  it("再生し終えたら ack する（合成完了では出さない）", () => {
    const state = start();
    run(state, [{ kind: "received", record: record(1) }]);

    const synthesized = run(state, [{ kind: "audioReady", epoch: 0, seq: 1, file: "/tmp/1.wav" }]);
    expect(only(synthesized, "ack")).toEqual([]);

    const played = run(state, [{ kind: "played", epoch: 0, seq: 1 }]);
    expect(only(played, "ack")).toEqual([{ kind: "ack", seq: 1, epochId: E1 }]);
  });

  it("★ ack を出した時点で、それ以下の seq は1つも残っていない", () => {
    // これが崩れると、server の ackUpTo が「まだ喋っていない手前の entry」を消す。
    // そこから先の任意の切断で、その文は再送されないまま失われる
    const state = start({ lookahead: 3 });
    run(
      state,
      [1, 2, 3, 4].map((seq) => ({ kind: "received", record: record(seq) })),
    );

    const seen: number[] = [];
    for (const seq of [1, 2, 3, 4]) {
      for (const command of speak(state, seq)) {
        if (command.kind !== "ack") continue;
        seen.push(command.seq);
        for (const remaining of state.items.keys()) expect(remaining).toBeGreaterThan(command.seq);
      }
    }
    expect(seen).toEqual([1, 2, 3, 4]);
  });

  it("★ 先読みの先で合成が失敗しても、head を追い越して ack しない", () => {
    const state = start({ lookahead: 3, synthesisAttempts: 1 });
    run(
      state,
      [1, 2, 3].map((seq) => ({ kind: "received", record: record(seq) })),
    );

    // seq 1 を再生中にしておく
    run(state, [{ kind: "audioReady", epoch: 0, seq: 1, file: "/tmp/1.wav" }]);

    // seq 3 の合成が失敗。ここで ack(3) が出ると seq 1, 2 のキューが道連れになる
    const failed = run(state, [{ kind: "audioFailed", epoch: 0, seq: 3, reason: "500" }]);
    expect(only(failed, "ack")).toEqual([]);

    // 1 → 2 と順に片付いて初めて 3 まで ack が進む
    expect(only(run(state, [{ kind: "played", epoch: 0, seq: 1 }]), "ack")).toEqual([
      { kind: "ack", seq: 1, epochId: E1 },
    ]);
    const rest = run(state, [
      { kind: "audioReady", epoch: 0, seq: 2, file: "/tmp/2.wav" },
      { kind: "played", epoch: 0, seq: 2 },
    ]);
    // 2 の完了で 2 と（失敗済みの）3 がまとめて片付く。累積なので ack は1回
    expect(only(rest, "ack")).toEqual([{ kind: "ack", seq: 3, epochId: E1 }]);
  });

  it("連続した失敗はまとめて1回の ack にする", () => {
    const state = start({ lookahead: 3, synthesisAttempts: 1 });
    run(
      state,
      [1, 2, 3].map((seq) => ({ kind: "received", record: record(seq) })),
    );
    const commands = run(state, [
      { kind: "audioFailed", epoch: 0, seq: 2, reason: "500" },
      { kind: "audioFailed", epoch: 0, seq: 3, reason: "500" },
      { kind: "audioFailed", epoch: 0, seq: 1, reason: "500" },
    ]);
    expect(only(commands, "ack")).toEqual([{ kind: "ack", seq: 3, epochId: E1 }]);
  });

  it("切断中の ack は溜めて、再接続後に最初のフレームで送る", () => {
    const state = start();
    run(state, [{ kind: "received", record: record(1) }]);
    run(state, [{ kind: "audioReady", epoch: 0, seq: 1, file: "/tmp/1.wav" }]);

    const offline = run(state, [{ kind: "disconnected" }, { kind: "played", epoch: 0, seq: 1 }]);
    expect(only(offline, "ack")).toEqual([]);
    expect(state.pendingAck).toEqual({ epoch: 0, seq: 1 });

    // ★ connected だけでは流さない（サーバーが同じものかまだ分からない）
    expect(only(run(state, [{ kind: "connected" }]), "ack")).toEqual([]);
    expect(state.pendingAck).toEqual({ epoch: 0, seq: 1 });

    // 同じエポックのフレームが届いて初めて、溜めていた ack が出る
    const resumed = run(state, [{ kind: "received", record: record(2) }]);
    expect(only(resumed, "ack")).toEqual([{ kind: "ack", seq: 1, epochId: E1 }]);
    expect(state.pendingAck).toBeNull();
  });

  it("★ ws の順序（open → message）で、サーバーが作り直されていたら保留 ack を出さない", () => {
    // `connected` より先に `received` を食わせるテストは **ws が生成しえない順序**で、
    // false confidence になる。実際は必ず open → message なので、`connected` の時点では
    // サーバーが同じものか判断できない。ここで ack を出すと、新しいサーバーの
    // `ackUpTo` が配信済み・未発話の entry（最大 500 件）を消す
    const state = start();
    run(state, [{ kind: "received", record: record(5) }]);
    run(state, [{ kind: "audioReady", epoch: 0, seq: 5, file: "/tmp/5.wav" }]);
    run(state, [{ kind: "disconnected" }, { kind: "played", epoch: 0, seq: 5 }]);
    expect(state.pendingAck).toEqual({ epoch: 0, seq: 5 });

    // 再接続。ws の順序どおり connected が先
    expect(only(run(state, [{ kind: "connected" }]), "ack")).toEqual([]);

    // そのあとで「採番がやり直された」フレームが届く
    const fresh = run(state, [{ kind: "received", record: record(1, { epoch: E2 }) }]);
    expect(only(fresh, "ack")).toEqual([]);
    expect(only(fresh, "dropPendingAck")).toHaveLength(1);
    expect(state.pendingAck).toBeNull();
  });
});

describe("重複排除", () => {
  it("再送された同じ (epoch, seq) を二度読み上げない", () => {
    const state = start();
    run(state, [{ kind: "received", record: record(1) }]);
    speak(state, 1);

    const again = run(state, [{ kind: "received", record: record(1) }]);
    expect(only(again, "fetchAudio")).toEqual([]);
    expect(only(again, "play")).toEqual([]);
  });

  it("★ 消費済みが再送されたら ack を打ち直す（サーバー側に残っている証拠なので）", () => {
    // ack が届く前に切断された / サーバー再起動で delivered が空になり upTo=0 で捨てられた、
    // のどちらか。打ち直さないと entry が永久に残り、再接続のたびに再送され続ける
    const state = start();
    run(state, [{ kind: "received", record: record(1) }]);
    speak(state, 1);

    const again = run(state, [{ kind: "received", record: record(1) }]);
    expect(only(again, "ack")).toEqual([{ kind: "ack", seq: 1, epochId: E1 }]);
  });

  it("処理中のものが再送されても合成をやり直さない", () => {
    const state = start();
    run(state, [{ kind: "received", record: record(1) }]);
    const again = run(state, [{ kind: "received", record: record(1) }]);
    expect(only(again, "fetchAudio")).toEqual([]);
    expect(state.items.get(1)?.status).toBe("fetching");
  });

  it("消費済みの記憶は上限で古い順に落ちる", () => {
    const state = start({ seenCapacity: 3 });
    for (const seq of [1, 2, 3, 4]) {
      run(state, [{ kind: "received", record: record(seq) }]);
      speak(state, seq);
    }
    expect(state.seen.size).toBe(3);
    // 最初に消費した seq 1 は忘れている
    expect(state.seen.has(`${E1}:1`)).toBe(false);
    expect(state.seen.has(`${E1}:4`)).toBe(true);
  });

  it("★ seen から溢れた消費済みの再送を、採番のやり直しと取り違えない", () => {
    // `seenCapacity` はサーバー側の `speechQueueMaxEntries` とズレうる（リモート / 別ルート /
    // 設定違い）ので溢れは起きる。ここを resetEpoch に落とすと**同じ文を2回喋る**
    const state = start({ seenCapacity: 3 });
    for (const seq of [1, 2, 3, 4, 5]) {
      run(state, [{ kind: "received", record: record(seq) }]);
      speak(state, seq);
    }
    // seq 1 は seen から溢れている
    expect(state.seen.has(`${E1}:1`)).toBe(false);

    // その seq 1 が **元の ts のまま** 再送される
    const resent = run(state, [{ kind: "received", record: record(1) }]);
    expect(only(resent, "fetchAudio")).toEqual([]);
    expect(only(resent, "warn")).toEqual([]);
    expect(only(resent, "ack")).toEqual([{ kind: "ack", seq: 1, epochId: E1 }]);
    expect(state.epoch).toBe(0);
  });

  it("★ 追いつきが seq 昇順で来なくても、未消費のフレームを捨てない", () => {
    // 受信ベースの水位だけで「seq が戻った」を判定すると、順序が乱れただけの
    // 未消費フレームを再送と誤読して**無音になる**
    const state = start({ lookahead: 3 });
    const commands = run(
      state,
      [3, 1, 2].map((seq) => ({ kind: "received", record: record(seq) })),
    );
    expect(
      only(commands, "fetchAudio")
        .map((c) => c.seq)
        .sort(),
    ).toEqual([1, 2, 3]);
    expect(state.epoch).toBe(0);
  });
});

describe("採番のやり直し（エポック変化）", () => {
  it("★ 旧エポックで消費済みの seq でも、epoch が違えば喋る", () => {
    // seq だけで覚えていると、~/.config/chatter-agent を消した後に
    // 何百文でも一切喋らず、エラーも出ないという最悪の症状になる
    const state = start();
    for (const seq of [1, 2, 3]) {
      run(state, [{ kind: "received", record: record(seq) }]);
      speak(state, seq);
    }

    const fresh = record(1, { epoch: E2, text: "新しい1。" });
    const commands = run(state, [{ kind: "received", record: fresh }]);
    // エポックが1つ進んでいるので、合成も新しいエポックで走る
    expect(only(commands, "fetchAudio")).toEqual([
      { kind: "fetchAudio", epoch: 1, seq: 1, path: buildAudioPath(E2, 1) },
    ]);
    expect(state.epoch).toBe(1);
  });

  it("★ エポックが変わったら保留 ack を捨てる", () => {
    // 旧エポックの ack(500) を新エポックのサーバーに打つと、ackUpTo がファイル名で
    // 範囲削除するため、まだ喋っていない新しい seq 1, 2 が消える
    const state = start();
    run(state, [{ kind: "received", record: record(5) }]);
    run(state, [{ kind: "audioReady", epoch: 0, seq: 5, file: "/tmp/5.wav" }]);
    run(state, [{ kind: "disconnected" }, { kind: "played", epoch: 0, seq: 5 }]);
    expect(state.pendingAck).toEqual({ epoch: 0, seq: 5 });

    run(state, [{ kind: "received", record: record(1, { epoch: E2 }) }]);
    expect(state.pendingAck).toBeNull();

    const online = run(state, [{ kind: "connected" }]);
    expect(only(online, "ack")).toEqual([]);
  });

  it("エポックが変わったら合成待ちを捨て、WAV も消す", () => {
    const state = start({ lookahead: 3 });
    run(
      state,
      [2, 3].map((seq) => ({ kind: "received", record: record(seq) })),
    );
    run(state, [{ kind: "audioReady", epoch: 0, seq: 3, file: "/tmp/3.wav" }]);

    const commands = run(state, [{ kind: "received", record: record(1, { epoch: E2 }) }]);
    expect(only(commands, "discardFile")).toEqual([{ kind: "discardFile", epoch: 0, seq: 3, file: "/tmp/3.wav" }]);
    expect(only(commands, "warn")).toHaveLength(1);
    expect(state.items.has(2)).toBe(false);
    expect(state.items.has(3)).toBe(false);
  });

  it("★ 再生中の音は最後まで流すが、その完了では ack しない", () => {
    const state = start();
    run(state, [{ kind: "received", record: record(5) }]);
    run(state, [{ kind: "audioReady", epoch: 0, seq: 5, file: "/tmp/5.wav" }]);
    run(state, [{ kind: "received", record: record(1, { epoch: E2 }) }]);

    const finished = run(state, [{ kind: "played", epoch: 0, seq: 5 }]);
    expect(only(finished, "ack")).toEqual([]);
    // WAV の後始末だけは行う
    expect(only(finished, "discardFile")).toEqual([{ kind: "discardFile", epoch: 0, seq: 5, file: "/tmp/5.wav" }]);
  });

  it("★ 旧エポックの合成結果を新しい item が拾わない", () => {
    // seq だけで突き合わせると、「こんにちは」を鳴らしながら「さようなら」を ack する
    const state = start();
    run(state, [{ kind: "received", record: record(1, { text: "こんにちは。" }) }]);
    run(state, [{ kind: "received", record: record(1, { epoch: E2, text: "さようなら。" }) }]);
    expect(state.epoch).toBe(1);

    // 旧エポックで投げた合成が今ごろ返ってくる
    const late = run(state, [{ kind: "audioReady", epoch: 0, seq: 1, file: "/tmp/OLD.wav" }]);
    expect(only(late, "play")).toEqual([]);
    expect(only(late, "discardFile")).toEqual([{ kind: "discardFile", epoch: 0, seq: 1, file: "/tmp/OLD.wav" }]);
    // 新しい item は合成待ちのまま。古い WAV を掴んでいない
    expect(state.items.get(1)?.status).toBe("fetching");
    expect(state.items.get(1)?.file).toBeNull();
  });

  it("★ 一時ファイルのパスがエポックを跨いで衝突しない", () => {
    // discardFile / play / synthesize がすべて (epoch, seq) を持つので、
    // ドライバは同じ seq でも別のパスを組める
    const state = start();
    run(state, [{ kind: "received", record: record(1) }]);
    const first = run(state, [{ kind: "audioReady", epoch: 0, seq: 1, file: "/tmp/e0-1.wav" }]);
    expect(only(first, "play")).toEqual([{ kind: "play", epoch: 0, seq: 1, file: "/tmp/e0-1.wav" }]);

    // 再生中に採番がやり直される → 旧 item は orphan
    run(state, [{ kind: "received", record: record(1, { epoch: E2 }) }]);
    const second = run(state, [{ kind: "audioReady", epoch: 1, seq: 1, file: "/tmp/e1-1.wav" }]);
    // 新しい方は鳴らない（head が orphan ではなく新 item で、まだ古い方が再生中でもない）
    expect(only(second, "play")).toEqual([{ kind: "play", epoch: 1, seq: 1, file: "/tmp/e1-1.wav" }]);

    // 旧エポックの再生完了は orphan として処理され、**新しい item の完了を飲まない**
    const orphanDone = run(state, [{ kind: "played", epoch: 0, seq: 1 }]);
    expect(only(orphanDone, "ack")).toEqual([]);
    expect(only(orphanDone, "discardFile")).toEqual([{ kind: "discardFile", epoch: 0, seq: 1, file: "/tmp/e0-1.wav" }]);
    expect(state.items.get(1)?.status).toBe("playing");
  });

  it("★ 世代の判定は epoch 一本。ts が動いてもエポック変化にしない", () => {
    // 以前は「同じ seq が別の ts」を推論の根拠にしていた。#30 で1メッセージ内の `ts` が
    // 同値になったこともあり、`ts` は世代の指標として当てにならない。契約が epoch を
    // 運ぶようになったので、推論そのものを持たない
    const state = start();
    run(state, [{ kind: "received", record: record(1) }]);

    const commands = run(state, [{ kind: "received", record: record(1, { ts: "2026-08-16T00:00:00.000Z" }) }]);

    expect(only(commands, "warn")).toEqual([]);
    expect(state.epoch).toBe(0);
    // 同じ世代の同じ seq は同じ文。合成をやり直さず、最初に受けたレコードのまま
    expect(only(commands, "fetchAudio")).toEqual([]);
    expect(state.items.get(1)?.record.ts).toBe(record(1).ts);
  });

  it("★ epoch が変われば、ts が戻っていてもエポック変化として扱う", () => {
    const state = start();
    run(state, [{ kind: "received", record: record(5) }]);

    // 新しい世代の seq 1。ts は前の世代より**古い**（バックアップ復元などで起こりうる）
    const commands = run(state, [
      { kind: "received", record: record(1, { epoch: E2, ts: "2020-01-01T00:00:00.000Z" }) },
    ]);

    expect(only(commands, "warn")).toHaveLength(1);
    expect(state.epoch).toBe(1);
    expect(state.epochId).toBe(E2);
    expect(state.items.has(5)).toBe(false);
  });
});

describe("失敗の扱い", () => {
  it("合成の失敗は1回だけリトライする", () => {
    const state = start({ synthesisAttempts: 2 });
    run(state, [{ kind: "received", record: record(1) }]);

    const retried = run(state, [{ kind: "audioFailed", epoch: 0, seq: 1, reason: "500" }]);
    expect(only(retried, "fetchAudio").map((c) => c.seq)).toEqual([1]);
    expect(only(retried, "ack")).toEqual([]);

    const given = run(state, [{ kind: "audioFailed", epoch: 0, seq: 1, reason: "500" }]);
    expect(only(given, "fetchAudio")).toEqual([]);
    expect(only(given, "ack")).toEqual([{ kind: "ack", seq: 1, epochId: E1 }]);
    expect(only(given, "warn")).toHaveLength(1);
  });

  it("再生の失敗はリトライしない（途中まで鳴った文が頭から鳴り直す）", () => {
    const state = start();
    run(state, [{ kind: "received", record: record(1) }]);
    run(state, [{ kind: "audioReady", epoch: 0, seq: 1, file: "/tmp/1.wav" }]);

    const commands = run(state, [{ kind: "playbackFailed", epoch: 0, seq: 1, reason: "exit 1" }]);
    expect(only(commands, "play")).toEqual([]);
    expect(only(commands, "ack")).toEqual([{ kind: "ack", seq: 1, epochId: E1 }]);
  });

  it("失敗した文の WAV も消す", () => {
    const state = start();
    run(state, [{ kind: "received", record: record(1) }]);
    run(state, [{ kind: "audioReady", epoch: 0, seq: 1, file: "/tmp/1.wav" }]);
    const commands = run(state, [{ kind: "playbackFailed", epoch: 0, seq: 1, reason: "timeout" }]);
    expect(only(commands, "discardFile")).toEqual([{ kind: "discardFile", epoch: 0, seq: 1, file: "/tmp/1.wav" }]);
  });

  it("音声が無いフレーム（audio: null）は取りに行かず、そのまま ack する", () => {
    // 約物だけの断片（docs/core.md「既知の欠落」: すごい！！ → ["すごい！", "！"]）と
    // `ttsEnabled: false` が、サーバー側でどちらも `audio: null` になる（→ #29）
    const state = start();
    const commands = run(state, [{ kind: "received", record: record(1, { text: "！", audio: null }) }]);
    expect(only(commands, "fetchAudio")).toEqual([]);
    expect(only(commands, "ack")).toEqual([{ kind: "ack", seq: 1, epochId: E1 }]);
  });

  it("捨てた後に合成が返ってきたら WAV だけ消す", () => {
    const state = start({ synthesisAttempts: 1 });
    run(state, [{ kind: "received", record: record(1) }]);
    run(state, [{ kind: "audioFailed", epoch: 0, seq: 1, reason: "timeout" }]);

    const late = run(state, [{ kind: "audioReady", epoch: 0, seq: 1, file: "/tmp/1.wav" }]);
    expect(only(late, "discardFile")).toEqual([{ kind: "discardFile", epoch: 0, seq: 1, file: "/tmp/1.wav" }]);
    expect(only(late, "play")).toEqual([]);
  });
});

describe("音声が用意できないとき（503 / 404。#29）", () => {
  it("★ 503 は試行回数を消費しない（エンジンが落ちているだけでバックログを燃やさない）", () => {
    const state = start({ synthesisAttempts: 2, audioRetryMs: 1_000 });
    run(state, [{ kind: "received", record: record(1) }]);

    // 何度 503 を受けても諦めない
    for (let i = 0; i < 10; i++) {
      const commands = run(state, [{ kind: "audioUnavailable", epoch: 0, seq: 1, reason: "503" }], T0 + i * 2_000);
      expect(only(commands, "ack")).toEqual([]);
      run(state, [{ kind: "tick" }], T0 + i * 2_000 + 1_500);
    }
    expect(state.items.get(1)?.status).not.toBe("done");
  });

  it("503 の後は audioRetryMs だけ待ってから取り直す", () => {
    const state = start({ audioRetryMs: 1_000 });
    run(state, [{ kind: "received", record: record(1) }]);
    run(state, [{ kind: "audioUnavailable", epoch: 0, seq: 1, reason: "503" }], T0);

    // まだ待ち時間の中
    expect(only(run(state, [{ kind: "tick" }], T0 + 500), "fetchAudio")).toEqual([]);
    // 過ぎたら取り直す
    expect(only(run(state, [{ kind: "tick" }], T0 + 1_500), "fetchAudio")).toHaveLength(1);
  });

  it("★ 404 はその場で終端。head なら ack まで進む", () => {
    const state = start();
    run(state, [{ kind: "received", record: record(1) }]);

    const commands = run(state, [{ kind: "audioGone", epoch: 0, seq: 1, reason: "404" }]);

    expect(only(commands, "warn")).toHaveLength(1);
    expect(only(commands, "ack")).toEqual([{ kind: "ack", seq: 1, epochId: E1 }]);
  });

  it("転送の失敗は今までどおり1回リトライして諦める", () => {
    const state = start({ synthesisAttempts: 2 });
    run(state, [{ kind: "received", record: record(1) }]);

    const retried = run(state, [{ kind: "audioFailed", epoch: 0, seq: 1, reason: "ECONNREFUSED" }]);
    expect(only(retried, "fetchAudio")).toHaveLength(1);
    expect(only(retried, "ack")).toEqual([]);

    const given = run(state, [{ kind: "audioFailed", epoch: 0, seq: 1, reason: "ECONNREFUSED" }]);
    expect(only(given, "ack")).toEqual([{ kind: "ack", seq: 1, epochId: E1 }]);
  });

  it("★ 用意できない状態が続いたら、設定を疑う手がかりを出す", () => {
    const state = start({ unavailableWarnAfter: 3, audioRetryMs: 0, audioRetryMaxMs: 0 });
    run(
      state,
      [1, 2, 3, 4, 5].map((seq) => ({ kind: "received", record: record(seq) })),
    );

    const hints = (commands: PlaybackCommand[]) =>
      only(commands, "warn").filter((c) => c.message.includes("ttsSpeakerId"));

    let warned = 0;
    for (const seq of [1, 2, 3, 4, 5]) {
      warned += hints(run(state, [{ kind: "audioGone", epoch: 0, seq, reason: "404" }])).length;
    }

    expect(warned).toBe(1);
  });

  it("★ 用意できない状態が続くなら、unavailableWarnRepeatMs ごとに出し直す", () => {
    // boolean のラッチだと、数日走る player が「停止 → 復旧 → 再停止」を見ても
    // 最初の1回しか出さない
    const state = start({ unavailableWarnAfter: 1, unavailableWarnRepeatMs: 60_000, audioRetryMaxMs: 0 });
    run(
      state,
      [1, 2].map((seq) => ({ kind: "received", record: record(seq) })),
    );

    const hints = (commands: PlaybackCommand[]) =>
      only(commands, "warn").filter((c) => c.message.includes("ttsSpeakerId"));

    expect(hints(run(state, [{ kind: "audioUnavailable", epoch: 0, seq: 1, reason: "503" }], T0))).toHaveLength(1);
    // 間隔の中では出さない
    expect(hints(run(state, [{ kind: "audioUnavailable", epoch: 0, seq: 1, reason: "503" }], T0 + 10_000))).toEqual([]);
    // 過ぎたら出し直す
    expect(
      hints(run(state, [{ kind: "audioUnavailable", epoch: 0, seq: 1, reason: "503" }], T0 + 70_000)),
    ).toHaveLength(1);
  });

  it("★ 503 が続く間は取り直しの間隔を倍にする（窓ぶんのリクエストが飛び続けない）", () => {
    const state = start({ audioRetryMs: 1_000, audioRetryMaxMs: 8_000 });
    run(state, [{ kind: "received", record: record(1) }]);

    const retryAfter = () => state.items.get(1)?.retryAfter ?? 0;
    const waits: number[] = [];
    for (let i = 0; i < 5; i++) {
      run(state, [{ kind: "audioUnavailable", epoch: 0, seq: 1, reason: "503" }], T0);
      waits.push(retryAfter() - T0);
      // 次の取得を走らせる（バックオフが明けた体で）
      run(state, [{ kind: "tick" }], retryAfter() + 1);
    }

    expect(waits).toEqual([1_000, 2_000, 4_000, 8_000, 8_000]);
  });

  it("★ 音声が取れたらバックオフも警告のラッチも解ける", () => {
    const state = start({ audioRetryMs: 1_000, audioRetryMaxMs: 8_000, unavailableWarnAfter: 1 });
    run(
      state,
      [1, 2].map((seq) => ({ kind: "received", record: record(seq) })),
    );

    run(state, [{ kind: "audioUnavailable", epoch: 0, seq: 1, reason: "503" }], T0);
    run(state, [{ kind: "tick" }], T0 + 2_000);
    run(state, [{ kind: "audioUnavailable", epoch: 0, seq: 1, reason: "503" }], T0 + 2_000);
    expect(state.unavailableBackoffStep).toBe(2);

    // バックオフが明けてから取り直させる（`audioReady` は `fetching` の item にしか効かない）
    run(state, [{ kind: "tick" }], T0 + 5_000);
    expect(state.items.get(1)?.status).toBe("fetching");
    run(state, [{ kind: "audioReady", epoch: 0, seq: 1, file: "/tmp/1.wav" }], T0 + 5_000);

    expect(state.unavailableBackoffStep).toBe(0);
    expect(state.unavailableWarnedAt).toBe(0);
    expect(state.unavailableStreak).toBe(0);
  });

  it("★ 503 は何度来ても試行回数を消費しない（数えるのは audioFailed だけ）", () => {
    const state = start({ synthesisAttempts: 2, audioRetryMs: 0, audioRetryMaxMs: 0 });
    run(state, [{ kind: "received", record: record(1) }]);

    for (let i = 0; i < 20; i++) {
      run(state, [{ kind: "audioUnavailable", epoch: 0, seq: 1, reason: "503" }]);
      run(state, [{ kind: "tick" }]);
    }
    expect(state.items.get(1)?.attempts).toBe(0);
    expect(state.items.get(1)?.status).not.toBe("done");

    // 転送失敗だけが数えられ、2回で諦める
    run(state, [{ kind: "audioFailed", epoch: 0, seq: 1, reason: "ECONNRESET" }]);
    expect(state.items.get(1)?.attempts).toBe(1);
    const given = run(state, [{ kind: "audioFailed", epoch: 0, seq: 1, reason: "ECONNRESET" }]);
    expect(only(given, "ack")).toEqual([{ kind: "ack", seq: 1, epochId: E1 }]);
  });

  it("音声が取れたら連続の数え直し", () => {
    const state = start({ unavailableWarnAfter: 2, audioRetryMs: 0 });
    run(
      state,
      [1, 2, 3].map((seq) => ({ kind: "received", record: record(seq) })),
    );

    run(state, [{ kind: "audioUnavailable", epoch: 0, seq: 1, reason: "503" }]);
    run(state, [{ kind: "audioReady", epoch: 0, seq: 2, file: "/tmp/2.wav" }]);
    const commands = run(state, [{ kind: "audioUnavailable", epoch: 0, seq: 3, reason: "503" }]);

    expect(only(commands, "warn")).toEqual([]);
    expect(state.unavailableStreak).toBe(1);
  });
});

describe("古い発話", () => {
  it("maxAgeMs が 0 なら何も飛ばさない", () => {
    const state = start({ maxAgeMs: 0 });
    const commands = run(state, [{ kind: "received", record: record(1) }], T0 + 600_000);
    expect(only(commands, "fetchAudio")).toHaveLength(1);
  });

  it("maxAgeMs を超えた発話は音を出さずに ack する", () => {
    const state = start({ maxAgeMs: 60_000 });
    const commands = run(state, [{ kind: "received", record: record(1) }], T0 + 120_000);
    expect(only(commands, "fetchAudio")).toEqual([]);
    expect(only(commands, "ack")).toEqual([{ kind: "ack", seq: 1, epochId: E1 }]);
  });

  it("ts が読めないものは古さで捨てない", () => {
    const state = start({ maxAgeMs: 60_000 });
    const commands = run(state, [{ kind: "received", record: record(1, { ts: "いつか" }) }], T0 + 120_000);
    expect(only(commands, "fetchAudio")).toHaveLength(1);
  });

  it("再生中のものは古くなっても止めない", () => {
    const state = start({ maxAgeMs: 60_000 });
    run(state, [{ kind: "received", record: record(1) }]);
    run(state, [{ kind: "audioReady", epoch: 0, seq: 1, file: "/tmp/1.wav" }]);
    const commands = run(state, [{ kind: "tick" }], T0 + 120_000);
    expect(only(commands, "ack")).toEqual([]);
    expect(state.items.get(1)?.status).toBe("playing");
  });
});

describe("stall watchdog", () => {
  it("head が動かないまま時間が経つと警告し、★ stallWarnMs ごとに出し直す", () => {
    const state = start({ stallWarnMs: 60_000 });
    run(state, [{ kind: "received", record: record(1) }]);

    expect(only(run(state, [{ kind: "tick" }], T0 + 30_000), "warn")).toEqual([]);

    const warned = only(run(state, [{ kind: "tick" }], T0 + 61_000), "warn");
    expect(warned).toHaveLength(1);
    expect(warned[0].message).toContain("seq=1");

    // 間隔の中では繰り返さない
    expect(only(run(state, [{ kind: "tick" }], T0 + 90_000), "warn")).toEqual([]);

    // ★ 恒久的に詰まると head は永遠に変わらない。`headSeq` の変化でしか再武装しない形だと
    //   生涯1行しか出ず、「無音なのにログが2行だけ」になる
    expect(only(run(state, [{ kind: "tick" }], T0 + 130_000), "warn")).toHaveLength(1);
  });

  it("head が進めば警告しない", () => {
    const state = start({ stallWarnMs: 60_000 });
    run(
      state,
      [1, 2].map((seq) => ({ kind: "received", record: record(seq) })),
    );
    speak(state, 1, T0 + 30_000);
    expect(only(run(state, [{ kind: "tick" }], T0 + 61_000), "warn")).toEqual([]);
  });

  it("キューが空なら警告しない", () => {
    const state = start({ stallWarnMs: 60_000 });
    expect(only(run(state, [{ kind: "tick" }], T0 + 600_000), "warn")).toEqual([]);
  });
});

describe("切断", () => {
  it("★ 切断で items を捨てない（再送で合成をやり直さない）", () => {
    const state = start({ lookahead: 3 });
    run(
      state,
      [1, 2, 3].map((seq) => ({ kind: "received", record: record(seq) })),
    );
    run(state, [{ kind: "audioReady", epoch: 0, seq: 1, file: "/tmp/1.wav" }]);

    run(state, [{ kind: "disconnected" }]);
    expect(state.items.size).toBe(3);
    expect(state.items.get(1)?.status).toBe("playing");
  });

  it("切断中に再送が届いても重複排除が効く", () => {
    const state = start();
    run(state, [{ kind: "received", record: record(1) }]);
    const commands = run(state, [{ kind: "disconnected" }, { kind: "received", record: record(1) }]);
    expect(only(commands, "fetchAudio")).toEqual([]);
  });
});
