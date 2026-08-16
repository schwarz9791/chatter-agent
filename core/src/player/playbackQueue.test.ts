import { describe, it, expect } from "vitest";
import { createDefaultOptions, createPlaybackState, reduce } from "./playbackQueue";
import type { PlaybackCommand, PlaybackEvent, PlaybackOptions, PlaybackState } from "./playbackQueue";
import type { SpeechRecord } from "../core/types";

const T0 = Date.parse("2026-08-15T00:00:00.000Z");

function record(seq: number, overrides: Partial<SpeechRecord> = {}): SpeechRecord {
  return {
    seq,
    ts: new Date(T0 + seq * 1000).toISOString(),
    source: "claude-code",
    sessionId: "sess-1",
    turnId: "turn-1",
    messageId: "m1",
    kind: "assistant",
    text: `文${seq}。`,
    emotion: "neutral",
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
  for (const event of events) out.push(...reduce(state, event, now).commands);
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
      { kind: "synthesized", seq, file: `/tmp/${seq}.wav` },
      { kind: "played", seq },
    ],
    now,
  );
}

describe("順序と先読み", () => {
  it("受信すると合成が始まり、合成が終われば head から再生される", () => {
    const state = start();
    const queued = run(state, [{ kind: "received", record: record(1) }]);
    expect(only(queued, "synthesize")).toEqual([{ kind: "synthesize", seq: 1, text: "文1。" }]);

    const playing = run(state, [{ kind: "synthesized", seq: 1, file: "/tmp/1.wav" }]);
    expect(only(playing, "play")).toEqual([{ kind: "play", seq: 1, file: "/tmp/1.wav" }]);
  });

  it("先読みの件数だけ合成を先行させる（再生中の1件を含む）", () => {
    const state = start({ lookahead: 2 });
    const commands = run(
      state,
      [1, 2, 3, 4, 5].map((seq) => ({ kind: "received", record: record(seq) })),
    );
    // lookahead=2 なら head を含めて3件まで
    expect(only(commands, "synthesize").map((c) => c.seq)).toEqual([1, 2, 3]);
  });

  it("lookahead=0 なら完全直列になる", () => {
    const state = start({ lookahead: 0 });
    const commands = run(
      state,
      [1, 2, 3].map((seq) => ({ kind: "received", record: record(seq) })),
    );
    expect(only(commands, "synthesize").map((c) => c.seq)).toEqual([1]);
  });

  it("★ 後ろの合成が先に終わっても head を追い越さない", () => {
    const state = start();
    run(
      state,
      [1, 2].map((seq) => ({ kind: "received", record: record(seq) })),
    );

    // seq 2 だけ合成完了
    const commands = run(state, [{ kind: "synthesized", seq: 2, file: "/tmp/2.wav" }]);
    expect(only(commands, "play")).toEqual([]);

    // seq 1 が揃って初めて 1 から鳴る
    const after = run(state, [{ kind: "synthesized", seq: 1, file: "/tmp/1.wav" }]);
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
    expect(only(commands, "synthesize").map((c) => c.seq)).toEqual([4]);

    const next = speak(state, 2);
    expect(only(next, "synthesize").map((c) => c.seq)).toEqual([5]);
  });

  it("★ seq に飛びがあっても先読みが止まらない（数値の窓ではなく位置の窓）", () => {
    // CLI の trim やサーバー再起動で seq は飛ぶ。数値窓（head + lookahead）だと
    // 対象ゼロになり、音は出るのに先読みだけが恒久的に効かなくなる
    const state = start({ lookahead: 2 });
    const commands = run(
      state,
      [10, 51, 52, 53].map((seq) => ({ kind: "received", record: record(seq) })),
    );
    expect(only(commands, "synthesize").map((c) => c.seq)).toEqual([10, 51, 52]);
  });

  it("受信順が seq 順でなくても seq 昇順に再生する（接続直後の追いつき）", () => {
    const state = start();
    run(
      state,
      [3, 1, 2].map((seq) => ({ kind: "received", record: record(seq) })),
    );
    const commands = run(state, [
      { kind: "synthesized", seq: 3, file: "/tmp/3.wav" },
      { kind: "synthesized", seq: 2, file: "/tmp/2.wav" },
      { kind: "synthesized", seq: 1, file: "/tmp/1.wav" },
    ]);
    expect(only(commands, "play").map((c) => c.seq)).toEqual([1]);
  });
});

describe("ack", () => {
  it("再生し終えたら ack する（合成完了では出さない）", () => {
    const state = start();
    run(state, [{ kind: "received", record: record(1) }]);

    const synthesized = run(state, [{ kind: "synthesized", seq: 1, file: "/tmp/1.wav" }]);
    expect(only(synthesized, "ack")).toEqual([]);

    const played = run(state, [{ kind: "played", seq: 1 }]);
    expect(only(played, "ack")).toEqual([{ kind: "ack", seq: 1 }]);
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
    run(state, [{ kind: "synthesized", seq: 1, file: "/tmp/1.wav" }]);

    // seq 3 の合成が失敗。ここで ack(3) が出ると seq 1, 2 のキューが道連れになる
    const failed = run(state, [{ kind: "synthesisFailed", seq: 3, reason: "500" }]);
    expect(only(failed, "ack")).toEqual([]);

    // 1 → 2 と順に片付いて初めて 3 まで ack が進む
    expect(only(run(state, [{ kind: "played", seq: 1 }]), "ack")).toEqual([{ kind: "ack", seq: 1 }]);
    const rest = run(state, [
      { kind: "synthesized", seq: 2, file: "/tmp/2.wav" },
      { kind: "played", seq: 2 },
    ]);
    // 2 の完了で 2 と（失敗済みの）3 がまとめて片付く。累積なので ack は1回
    expect(only(rest, "ack")).toEqual([{ kind: "ack", seq: 3 }]);
  });

  it("連続した失敗はまとめて1回の ack にする", () => {
    const state = start({ lookahead: 3, synthesisAttempts: 1 });
    run(
      state,
      [1, 2, 3].map((seq) => ({ kind: "received", record: record(seq) })),
    );
    const commands = run(state, [
      { kind: "synthesisFailed", seq: 2, reason: "500" },
      { kind: "synthesisFailed", seq: 3, reason: "500" },
      { kind: "synthesisFailed", seq: 1, reason: "500" },
    ]);
    expect(only(commands, "ack")).toEqual([{ kind: "ack", seq: 3 }]);
  });

  it("切断中の ack は溜めて、再接続でまとめて送る", () => {
    const state = start();
    run(state, [{ kind: "received", record: record(1) }]);
    run(state, [{ kind: "synthesized", seq: 1, file: "/tmp/1.wav" }]);

    const offline = run(state, [{ kind: "disconnected" }, { kind: "played", seq: 1 }]);
    expect(only(offline, "ack")).toEqual([]);
    expect(state.pendingAck).toBe(1);

    const online = run(state, [{ kind: "connected" }]);
    expect(only(online, "ack")).toEqual([{ kind: "ack", seq: 1 }]);
    expect(state.pendingAck).toBeNull();
  });
});

describe("重複排除", () => {
  it("再送された同じ (seq, ts) を二度読み上げない", () => {
    const state = start();
    run(state, [{ kind: "received", record: record(1) }]);
    speak(state, 1);

    const again = run(state, [{ kind: "received", record: record(1) }]);
    expect(only(again, "synthesize")).toEqual([]);
    expect(only(again, "play")).toEqual([]);
  });

  it("★ 消費済みが再送されたら ack を打ち直す（サーバー側に残っている証拠なので）", () => {
    // ack が届く前に切断された / サーバー再起動で delivered が空になり upTo=0 で捨てられた、
    // のどちらか。打ち直さないと entry が永久に残り、再接続のたびに再送され続ける
    const state = start();
    run(state, [{ kind: "received", record: record(1) }]);
    speak(state, 1);

    const again = run(state, [{ kind: "received", record: record(1) }]);
    expect(only(again, "ack")).toEqual([{ kind: "ack", seq: 1 }]);
  });

  it("処理中のものが再送されても合成をやり直さない", () => {
    const state = start();
    run(state, [{ kind: "received", record: record(1) }]);
    const again = run(state, [{ kind: "received", record: record(1) }]);
    expect(only(again, "synthesize")).toEqual([]);
    expect(state.items.get(1)?.status).toBe("synthesizing");
  });

  it("消費済みの記憶は上限で古い順に落ちる", () => {
    const state = start({ seenCapacity: 3 });
    for (const seq of [1, 2, 3, 4]) {
      run(state, [{ kind: "received", record: record(seq) }]);
      speak(state, seq);
    }
    expect(state.seen.size).toBe(3);
    // 最初に消費した seq 1 は忘れている
    expect([...state.seen].some((key) => key.startsWith("1:"))).toBe(false);
    expect([...state.seen].some((key) => key.startsWith("4:"))).toBe(true);
  });
});

describe("採番のやり直し（エポック変化）", () => {
  it("★ 旧エポックで消費済みの seq でも、ts が違えば喋る", () => {
    // seq だけで覚えていると、~/.config/chatter-agent を消した後に
    // 何百文でも一切喋らず、エラーも出ないという最悪の症状になる
    const state = start();
    for (const seq of [1, 2, 3]) {
      run(state, [{ kind: "received", record: record(seq) }]);
      speak(state, seq);
    }

    const fresh = record(1, { ts: "2026-08-16T00:00:00.000Z", text: "新しい1。" });
    const commands = run(state, [{ kind: "received", record: fresh }]);
    expect(only(commands, "synthesize")).toEqual([{ kind: "synthesize", seq: 1, text: "新しい1。" }]);
  });

  it("★ エポックが変わったら保留 ack を捨てる", () => {
    // 旧エポックの ack(500) を新エポックのサーバーに打つと、ackUpTo がファイル名で
    // 範囲削除するため、まだ喋っていない新しい seq 1, 2 が消える
    const state = start();
    run(state, [{ kind: "received", record: record(5) }]);
    run(state, [{ kind: "synthesized", seq: 5, file: "/tmp/5.wav" }]);
    run(state, [{ kind: "disconnected" }, { kind: "played", seq: 5 }]);
    expect(state.pendingAck).toBe(5);

    run(state, [{ kind: "received", record: record(1, { ts: "2026-08-16T00:00:00.000Z" }) }]);
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
    run(state, [{ kind: "synthesized", seq: 3, file: "/tmp/3.wav" }]);

    const commands = run(state, [{ kind: "received", record: record(1, { ts: "2026-08-16T00:00:00.000Z" }) }]);
    expect(only(commands, "discardFile")).toEqual([{ kind: "discardFile", seq: 3, file: "/tmp/3.wav" }]);
    expect(only(commands, "warn")).toHaveLength(1);
    expect(state.items.has(2)).toBe(false);
    expect(state.items.has(3)).toBe(false);
  });

  it("★ 再生中の音は最後まで流すが、その完了では ack しない", () => {
    const state = start();
    run(state, [{ kind: "received", record: record(5) }]);
    run(state, [{ kind: "synthesized", seq: 5, file: "/tmp/5.wav" }]);
    run(state, [{ kind: "received", record: record(1, { ts: "2026-08-16T00:00:00.000Z" }) }]);

    const finished = run(state, [{ kind: "played", seq: 5 }]);
    expect(only(finished, "ack")).toEqual([]);
    // WAV の後始末だけは行う
    expect(only(finished, "discardFile")).toEqual([{ kind: "discardFile", seq: 5, file: "/tmp/5.wav" }]);
  });

  it("同じ seq が別の ts で来たらエポック変化として扱う", () => {
    const state = start();
    run(state, [{ kind: "received", record: record(1) }]);
    const commands = run(state, [{ kind: "received", record: record(1, { ts: "2026-08-16T00:00:00.000Z" }) }]);
    expect(only(commands, "warn")).toHaveLength(1);
    expect(state.items.get(1)?.record.ts).toBe("2026-08-16T00:00:00.000Z");
  });
});

describe("失敗の扱い", () => {
  it("合成の失敗は1回だけリトライする", () => {
    const state = start({ synthesisAttempts: 2 });
    run(state, [{ kind: "received", record: record(1) }]);

    const retried = run(state, [{ kind: "synthesisFailed", seq: 1, reason: "500" }]);
    expect(only(retried, "synthesize").map((c) => c.seq)).toEqual([1]);
    expect(only(retried, "ack")).toEqual([]);

    const given = run(state, [{ kind: "synthesisFailed", seq: 1, reason: "500" }]);
    expect(only(given, "synthesize")).toEqual([]);
    expect(only(given, "ack")).toEqual([{ kind: "ack", seq: 1 }]);
    expect(only(given, "warn")).toHaveLength(1);
  });

  it("再生の失敗はリトライしない（途中まで鳴った文が頭から鳴り直す）", () => {
    const state = start();
    run(state, [{ kind: "received", record: record(1) }]);
    run(state, [{ kind: "synthesized", seq: 1, file: "/tmp/1.wav" }]);

    const commands = run(state, [{ kind: "playbackFailed", seq: 1, reason: "exit 1" }]);
    expect(only(commands, "play")).toEqual([]);
    expect(only(commands, "ack")).toEqual([{ kind: "ack", seq: 1 }]);
  });

  it("失敗した文の WAV も消す", () => {
    const state = start();
    run(state, [{ kind: "received", record: record(1) }]);
    run(state, [{ kind: "synthesized", seq: 1, file: "/tmp/1.wav" }]);
    const commands = run(state, [{ kind: "playbackFailed", seq: 1, reason: "timeout" }]);
    expect(only(commands, "discardFile")).toEqual([{ kind: "discardFile", seq: 1, file: "/tmp/1.wav" }]);
  });

  it("約物だけの断片は合成に出さず、そのまま ack する", () => {
    // docs/core.md「既知の欠落」: すごい！！ → ["すごい！", "！"]
    const state = start();
    const commands = run(state, [{ kind: "received", record: record(1, { text: "！" }) }]);
    expect(only(commands, "synthesize")).toEqual([]);
    expect(only(commands, "ack")).toEqual([{ kind: "ack", seq: 1 }]);
  });

  it("捨てた後に合成が返ってきたら WAV だけ消す", () => {
    const state = start({ synthesisAttempts: 1 });
    run(state, [{ kind: "received", record: record(1) }]);
    run(state, [{ kind: "synthesisFailed", seq: 1, reason: "timeout" }]);

    const late = run(state, [{ kind: "synthesized", seq: 1, file: "/tmp/1.wav" }]);
    expect(only(late, "discardFile")).toEqual([{ kind: "discardFile", seq: 1, file: "/tmp/1.wav" }]);
    expect(only(late, "play")).toEqual([]);
  });
});

describe("古い発話", () => {
  it("maxAgeMs が 0 なら何も飛ばさない", () => {
    const state = start({ maxAgeMs: 0 });
    const commands = run(state, [{ kind: "received", record: record(1) }], T0 + 600_000);
    expect(only(commands, "synthesize")).toHaveLength(1);
  });

  it("maxAgeMs を超えた発話は音を出さずに ack する", () => {
    const state = start({ maxAgeMs: 60_000 });
    const commands = run(state, [{ kind: "received", record: record(1) }], T0 + 120_000);
    expect(only(commands, "synthesize")).toEqual([]);
    expect(only(commands, "ack")).toEqual([{ kind: "ack", seq: 1 }]);
  });

  it("ts が読めないものは古さで捨てない", () => {
    const state = start({ maxAgeMs: 60_000 });
    const commands = run(state, [{ kind: "received", record: record(1, { ts: "いつか" }) }], T0 + 120_000);
    expect(only(commands, "synthesize")).toHaveLength(1);
  });

  it("再生中のものは古くなっても止めない", () => {
    const state = start({ maxAgeMs: 60_000 });
    run(state, [{ kind: "received", record: record(1) }]);
    run(state, [{ kind: "synthesized", seq: 1, file: "/tmp/1.wav" }]);
    const commands = run(state, [{ kind: "tick" }], T0 + 120_000);
    expect(only(commands, "ack")).toEqual([]);
    expect(state.items.get(1)?.status).toBe("playing");
  });
});

describe("stall watchdog", () => {
  it("head が動かないまま時間が経つと1回だけ警告する", () => {
    const state = start({ stallWarnMs: 60_000 });
    run(state, [{ kind: "received", record: record(1) }]);

    expect(only(run(state, [{ kind: "tick" }], T0 + 30_000), "warn")).toEqual([]);

    const warned = only(run(state, [{ kind: "tick" }], T0 + 61_000), "warn");
    expect(warned).toHaveLength(1);
    expect(warned[0].message).toContain("seq=1");

    // 繰り返さない
    expect(only(run(state, [{ kind: "tick" }], T0 + 200_000), "warn")).toEqual([]);
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
    run(state, [{ kind: "synthesized", seq: 1, file: "/tmp/1.wav" }]);

    run(state, [{ kind: "disconnected" }]);
    expect(state.items.size).toBe(3);
    expect(state.items.get(1)?.status).toBe("playing");
  });

  it("切断中に再送が届いても重複排除が効く", () => {
    const state = start();
    run(state, [{ kind: "received", record: record(1) }]);
    const commands = run(state, [{ kind: "disconnected" }, { kind: "received", record: record(1) }]);
    expect(only(commands, "synthesize")).toEqual([]);
  });
});
