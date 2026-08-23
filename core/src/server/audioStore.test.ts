import { describe, it, expect, vi } from "vitest";
import { createAudioStore, SynthesisUnavailableError } from "./audioStore";

const VOICE = { baseUrl: "http://127.0.0.1:10101", speakerId: 888753760 };

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
    voice = { ...VOICE, speakerId: 1 };
    await store.get("g", 1, "あ。");
    expect(synthesize).toHaveBeenCalledTimes(2);
    expect(synthesize).toHaveBeenLastCalledWith("あ。", { baseUrl: VOICE.baseUrl, speakerId: 1 });
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
