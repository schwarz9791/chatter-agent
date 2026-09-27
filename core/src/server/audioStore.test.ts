import { describe, it, expect, vi } from "vitest";
import { createAudioStore, SynthesisRejectedError, SynthesisUnavailableError, type Voice } from "./audioStore";
import { TtsHttpError, TtsTransportError } from "../tts/ttsEngine";

const VOICE: Voice = { engine: "voicevox", baseUrl: "http://127.0.0.1:10101", speakerId: "888753760", speedScale: 1.0 };

function wav(bytes: number): ArrayBuffer {
  return new ArrayBuffer(bytes);
}

/** 解決のタイミングを握れる合成 */
function deferredSynthesize() {
  const calls: { text: string; resolve: (wav: ArrayBuffer) => void; reject: (err: unknown) => void }[] = [];
  const synthesize = vi.fn(
    (text: string) =>
      new Promise<ArrayBuffer>((resolve, reject) => {
        calls.push({ text, resolve, reject });
      }),
  );
  return { synthesize, calls };
}

describe("createAudioStore", () => {
  it("合成して返し、2度目はキャッシュから返す", async () => {
    const synthesize = vi.fn(() => Promise.resolve(wav(10)));
    const store = createAudioStore({ currentVoice: () => VOICE, synthesize });

    expect((await store.get("gen-1", 1, "あ。")).byteLength).toBe(10);
    expect((await store.get("gen-1", 1, "あ。")).byteLength).toBe(10);

    expect(synthesize).toHaveBeenCalledTimes(1);
  });

  it("★ 同じキーへの同時要求は1回の合成にまとめる（複数クライアントでエンジンを2度叩かない）", async () => {
    const { synthesize, calls } = deferredSynthesize();
    const store = createAudioStore({ currentVoice: () => VOICE, synthesize });

    const a = store.get("gen-1", 1, "あ。");
    const b = store.get("gen-1", 1, "あ。");
    expect(synthesize).toHaveBeenCalledTimes(1);

    calls[0]!.resolve(wav(7));
    expect((await a).byteLength).toBe(7);
    expect((await b).byteLength).toBe(7);
  });

  it("epoch が違えば別のキーとして扱う（seq は世代を跨いで一意でない）", async () => {
    const synthesize = vi.fn(() => Promise.resolve(wav(4)));
    const store = createAudioStore({ currentVoice: () => VOICE, synthesize });

    await store.get("gen-1", 1, "ふるい。");
    await store.get("gen-2", 1, "あたらしい。");

    expect(synthesize).toHaveBeenCalledTimes(2);
    expect(synthesize).toHaveBeenNthCalledWith(1, "ふるい。", VOICE);
    expect(synthesize).toHaveBeenNthCalledWith(2, "あたらしい。", VOICE);
  });

  it("失敗は SynthesisUnavailableError に包む（httpServer が 503 に落とす目印）", async () => {
    const store = createAudioStore({
      currentVoice: () => VOICE,
      synthesize: () => Promise.reject(new Error("ECONNREFUSED")),
    });
    await expect(store.get("gen-1", 1, "あ。")).rejects.toBeInstanceOf(SynthesisUnavailableError);
  });

  it("失敗はキャッシュしない（エンジンが戻れば次の GET で作り直せる）", async () => {
    let attempt = 0;
    const synthesize = vi.fn(() => {
      attempt++;
      return attempt === 1 ? Promise.reject(new Error("down")) : Promise.resolve(wav(3));
    });
    const store = createAudioStore({ currentVoice: () => VOICE, synthesize });

    await expect(store.get("gen-1", 1, "あ。")).rejects.toThrow();
    expect((await store.get("gen-1", 1, "あ。")).byteLength).toBe(3);
  });

  it("件数の上限を超えたら古い方から捨てる", async () => {
    const synthesize = vi.fn(() => Promise.resolve(wav(1)));
    const store = createAudioStore({ currentVoice: () => VOICE, synthesize, maxEntries: 2 });

    await store.get("g", 1, "あ。");
    await store.get("g", 2, "い。");
    await store.get("g", 3, "う。");

    expect(store.stats().entries).toBe(2);
    // seq 1 は落ちているので作り直しになる
    await store.get("g", 1, "あ。");
    expect(synthesize).toHaveBeenCalledTimes(4);
  });

  it("触ったものは末尾へ回る（LRU。先読み窓の中身が落ちない）", async () => {
    const synthesize = vi.fn(() => Promise.resolve(wav(1)));
    const store = createAudioStore({ currentVoice: () => VOICE, synthesize, maxEntries: 2 });

    await store.get("g", 1, "あ。");
    await store.get("g", 2, "い。");
    await store.get("g", 1, "あ。"); // 1 を触る → 2 が最古になる
    await store.get("g", 3, "う。");

    await store.get("g", 1, "あ。");
    expect(synthesize).toHaveBeenCalledTimes(3); // 1 は残っていたので作り直していない
  });

  it("バイト数の上限でも捨てる", async () => {
    const store = createAudioStore({
      currentVoice: () => VOICE,
      synthesize: () => Promise.resolve(wav(600)),
      maxBytes: 1000,
    });

    await store.get("g", 1, "あ。");
    await store.get("g", 2, "い。");

    expect(store.stats().entries).toBe(1);
    expect(store.stats().bytes).toBe(600);
  });

  it("1件で上限を超えるものは覚えない（自分自身を追い出すだけなので）", async () => {
    const store = createAudioStore({
      currentVoice: () => VOICE,
      synthesize: () => Promise.resolve(wav(5000)),
      maxBytes: 1000,
    });

    expect((await store.get("g", 1, "あ。")).byteLength).toBe(5000);
    expect(store.stats()).toMatchObject({ entries: 0, bytes: 0 });
  });

  it("合成が終わったら in-flight から消える", async () => {
    const store = createAudioStore({ currentVoice: () => VOICE, synthesize: () => Promise.resolve(wav(1)) });
    await store.get("g", 1, "あ。");
    expect(store.stats().inFlight).toBe(0);
  });

  it("★ 声が変わればキャッシュに当たらない（ttsSpeakerId を直したのに古い声で返さない）", async () => {
    let voice = VOICE;
    const synthesize = vi.fn(() => Promise.resolve(wav(10)));
    const store = createAudioStore({ currentVoice: () => voice, synthesize });

    await store.get("g", 1, "あ。");
    await store.get("g", 1, "あ。");
    expect(synthesize).toHaveBeenCalledTimes(1);

    // 設定を直した。LRU に残っている古い声をそのまま返してはいけない
    voice = { ...VOICE, speakerId: "1" };
    await store.get("g", 1, "あ。");
    expect(synthesize).toHaveBeenCalledTimes(2);
    expect(synthesize).toHaveBeenLastCalledWith("あ。", { ...VOICE, speakerId: "1" });
  });

  /**
   * ★★ #76。`Voice` に `speedScale` を足したときに、キャッシュキーへ入れ忘れると
   *   **速度を変えた直後に取り直した文だけ古い速度の WAV** が返る。
   *   `Voice` にフィールドを足したら `keyFor` にも足すこと
   */
  it("★★ 話速が変わればキャッシュに当たらない（速度を変えた直後の文が古い速度で鳴らない）", async () => {
    let voice = VOICE;
    const synthesize = vi.fn(() => Promise.resolve(wav(10)));
    const store = createAudioStore({ currentVoice: () => voice, synthesize });

    await store.get("g", 1, "あ。");
    expect(synthesize).toHaveBeenCalledTimes(1);

    voice = { ...VOICE, speedScale: 1.5 };
    await store.get("g", 1, "あ。");
    expect(synthesize).toHaveBeenCalledTimes(2);
    expect(synthesize).toHaveBeenLastCalledWith("あ。", { ...VOICE, speedScale: 1.5 });
  });

  /**
   * ★★ #106。`ttsBaseUrl` はエンジンを跨いで同じ値になりうる（既定値を変えずに
   *   `ttsEngine` だけ切り替えた直後など）ので、`engine` が無いとそこで別エンジンの
   *   WAV が LRU に残ったまま返る。`Voice` にフィールドを足したら `keyFor` にも足すこと
   */
  it("★★ engine が変わればキャッシュに当たらない（voicevox と openai を切り替えても混線しない）", async () => {
    let voice = VOICE;
    const synthesize = vi.fn(() => Promise.resolve(wav(10)));
    const store = createAudioStore({ currentVoice: () => voice, synthesize });

    await store.get("g", 1, "あ。");
    expect(synthesize).toHaveBeenCalledTimes(1);

    voice = { ...VOICE, engine: "openai" };
    await store.get("g", 1, "あ。");
    expect(synthesize).toHaveBeenCalledTimes(2);
    expect(synthesize).toHaveBeenLastCalledWith("あ。", { ...VOICE, engine: "openai" });
  });

  it("★ 声は1回だけ解決する（キーを決めた後に config が変わると、声Bの WAV が声Aのキーに入る）", async () => {
    const currentVoice = vi.fn(() => VOICE);
    const store = createAudioStore({ currentVoice, synthesize: () => Promise.resolve(wav(1)) });

    await store.get("g", 1, "あ。");

    expect(currentVoice).toHaveBeenCalledTimes(1);
  });

  it("★ 同時に走らせる合成には上限がある（無いとキューぶんの GET を並べるだけで実質 DoS）", async () => {
    const { synthesize, calls } = deferredSynthesize();
    const store = createAudioStore({ currentVoice: () => VOICE, synthesize, maxInFlight: 2 });

    const first = store.get("g", 1, "あ。");
    const second = store.get("g", 2, "い。");
    expect(store.stats().inFlight).toBe(2);

    // 3件目は 503（あとで取りに来い）。エンジンには飛ばさない
    await expect(store.get("g", 3, "う。")).rejects.toBeInstanceOf(SynthesisUnavailableError);
    expect(synthesize).toHaveBeenCalledTimes(2);

    // 走っているものが終われば、また受け付ける
    for (const call of calls.splice(0)) call.resolve(wav(1));
    await Promise.all([first, second]);

    const retry = store.get("g", 3, "う。");
    expect(synthesize).toHaveBeenCalledTimes(3);
    calls[0]!.resolve(wav(1));
    await expect(retry).resolves.toBeDefined();
  });

  it("上限に達していても、同じキーの2人目は相乗りできる（合成は増えない）", async () => {
    const { synthesize, calls } = deferredSynthesize();
    const store = createAudioStore({ currentVoice: () => VOICE, synthesize, maxInFlight: 1 });

    const first = store.get("g", 1, "あ。");
    const second = store.get("g", 1, "あ。");
    expect(synthesize).toHaveBeenCalledTimes(1);

    calls[0]!.resolve(wav(7));
    expect((await first).byteLength).toBe(7);
    expect((await second).byteLength).toBe(7);
  });
});

describe("SynthesisRejectedError（規則7の唯一の例外）", () => {
  it("同じ声・世代で後ろの seq が成功済みなら Rejected", async () => {
    const synthesize = vi.fn((text: string) =>
      text === "拒まれる。"
        ? Promise.reject(new TtsHttpError("op", 400, "no speakable text"))
        : Promise.resolve(wav(3)),
    );
    const store = createAudioStore({ currentVoice: () => VOICE, synthesize });

    await store.get("g", 2, "後ろ。"); // seq 2 が先に成功
    await expect(store.get("g", 1, "拒まれる。")).rejects.toBeInstanceOf(SynthesisRejectedError);
  });

  it("後ろが無ければ Unavailable（503 のまま待つ）", async () => {
    const synthesize = vi.fn(() => Promise.reject(new TtsHttpError("op", 400, "no speakable text")));
    const store = createAudioStore({ currentVoice: () => VOICE, synthesize });

    await expect(store.get("g", 1, "拒まれる。")).rejects.toBeInstanceOf(SynthesisUnavailableError);
  });

  it("★ 全文が拒まれるときは Unavailable のまま（規則7が守る「全部消える」事故は起きない）", async () => {
    const synthesize = vi.fn(() => Promise.reject(new TtsHttpError("op", 400, "no speakable text")));
    const store = createAudioStore({ currentVoice: () => VOICE, synthesize });

    await expect(store.get("g", 1, "文1。")).rejects.toBeInstanceOf(SynthesisUnavailableError);
    await expect(store.get("g", 2, "文2。")).rejects.toBeInstanceOf(SynthesisUnavailableError);
  });

  it("5xx / 408 / 429 は Rejected にしない（一時的な失敗として扱う）", async () => {
    const synthesize = vi.fn((text: string) => {
      if (text === "後ろ。") return Promise.resolve(wav(3));
      if (text === "500。") return Promise.reject(new TtsHttpError("op", 500, "internal"));
      if (text === "408。") return Promise.reject(new TtsHttpError("op", 408, "timeout"));
      return Promise.reject(new TtsHttpError("op", 429, "too many"));
    });
    const store = createAudioStore({ currentVoice: () => VOICE, synthesize });

    await store.get("g", 4, "後ろ。");
    await expect(store.get("g", 1, "500。")).rejects.toBeInstanceOf(SynthesisUnavailableError);
    await expect(store.get("g", 2, "408。")).rejects.toBeInstanceOf(SynthesisUnavailableError);
    await expect(store.get("g", 3, "429。")).rejects.toBeInstanceOf(SynthesisUnavailableError);
  });

  it("転送エラー（エンジンに届いていない）は Rejected にしない", async () => {
    const synthesize = vi.fn((text: string) =>
      text === "後ろ。" ? Promise.resolve(wav(3)) : Promise.reject(new TtsTransportError("ECONNREFUSED")),
    );
    const store = createAudioStore({ currentVoice: () => VOICE, synthesize });

    await store.get("g", 2, "後ろ。");
    await expect(store.get("g", 1, "手前。")).rejects.toBeInstanceOf(SynthesisUnavailableError);
  });

  it("声が違う成功は数えない（別の声で通っていても、いまの声では拒む）", async () => {
    let voice = VOICE;
    const synthesize = vi.fn((text: string) =>
      text === "後ろ。" ? Promise.resolve(wav(3)) : Promise.reject(new TtsHttpError("op", 400, "no speakable text")),
    );
    const store = createAudioStore({ currentVoice: () => voice, synthesize });

    await store.get("g", 2, "後ろ。"); // 元の声で成功
    voice = { ...VOICE, speakerId: "1" };
    await expect(store.get("g", 1, "拒まれる。")).rejects.toBeInstanceOf(SynthesisUnavailableError);
  });

  it("epoch が違う成功は数えない（世代を跨いで判定しない）", async () => {
    const synthesize = vi.fn((text: string) =>
      text === "後ろ。" ? Promise.resolve(wav(3)) : Promise.reject(new TtsHttpError("op", 400, "no speakable text")),
    );
    const store = createAudioStore({ currentVoice: () => VOICE, synthesize });

    await store.get("gen-old", 99, "後ろ。"); // 別世代で成功
    await expect(store.get("gen-new", 1, "拒まれる。")).rejects.toBeInstanceOf(SynthesisUnavailableError);
  });

  it("★ 成功の順序は問わない。S が後から成功すれば 200 に戻る", async () => {
    let engineFixed = false;
    const synthesize = vi.fn((text: string) => {
      if (text === "後ろ。") return Promise.resolve(wav(3));
      return engineFixed ? Promise.resolve(wav(5)) : Promise.reject(new TtsHttpError("op", 400, "no speakable text"));
    });
    const store = createAudioStore({ currentVoice: () => VOICE, synthesize });

    await store.get("g", 2, "後ろ。");
    await expect(store.get("g", 1, "手前。")).rejects.toBeInstanceOf(SynthesisRejectedError);

    engineFixed = true;
    await expect(store.get("g", 1, "手前。")).resolves.toBeDefined();
  });
});
