import { describe, it, expect, vi } from "vitest";
import { createAudioStore, SynthesisUnavailableError } from "./audioStore";

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
    const store = createAudioStore({ synthesize });

    expect((await store.get("gen-1", 1, "あ。")).byteLength).toBe(10);
    expect((await store.get("gen-1", 1, "あ。")).byteLength).toBe(10);

    expect(synthesize).toHaveBeenCalledTimes(1);
  });

  it("★ 同じキーへの同時要求は1回の合成にまとめる（複数クライアントでエンジンを2度叩かない）", async () => {
    const { synthesize, calls } = deferredSynthesize();
    const store = createAudioStore({ synthesize });

    const a = store.get("gen-1", 1, "あ。");
    const b = store.get("gen-1", 1, "あ。");
    expect(synthesize).toHaveBeenCalledTimes(1);

    calls[0]!.resolve(wav(7));
    expect((await a).byteLength).toBe(7);
    expect((await b).byteLength).toBe(7);
  });

  it("epoch が違えば別のキーとして扱う（seq は世代を跨いで一意でない）", async () => {
    const synthesize = vi.fn(() => Promise.resolve(wav(4)));
    const store = createAudioStore({ synthesize });

    await store.get("gen-1", 1, "ふるい。");
    await store.get("gen-2", 1, "あたらしい。");

    expect(synthesize).toHaveBeenCalledTimes(2);
    expect(synthesize).toHaveBeenNthCalledWith(1, "ふるい。");
    expect(synthesize).toHaveBeenNthCalledWith(2, "あたらしい。");
  });

  it("失敗は SynthesisUnavailableError に包む（httpServer が 503 に落とす目印）", async () => {
    const store = createAudioStore({ synthesize: () => Promise.reject(new Error("ECONNREFUSED")) });
    await expect(store.get("gen-1", 1, "あ。")).rejects.toBeInstanceOf(SynthesisUnavailableError);
  });

  it("失敗はキャッシュしない（エンジンが戻れば次の GET で作り直せる）", async () => {
    let attempt = 0;
    const synthesize = vi.fn(() => {
      attempt++;
      return attempt === 1 ? Promise.reject(new Error("down")) : Promise.resolve(wav(3));
    });
    const store = createAudioStore({ synthesize });

    await expect(store.get("gen-1", 1, "あ。")).rejects.toThrow();
    expect((await store.get("gen-1", 1, "あ。")).byteLength).toBe(3);
  });

  it("件数の上限を超えたら古い方から捨てる", async () => {
    const synthesize = vi.fn(() => Promise.resolve(wav(1)));
    const store = createAudioStore({ synthesize, maxEntries: 2 });

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
    const store = createAudioStore({ synthesize, maxEntries: 2 });

    await store.get("g", 1, "あ。");
    await store.get("g", 2, "い。");
    await store.get("g", 1, "あ。"); // 1 を触る → 2 が最古になる
    await store.get("g", 3, "う。");

    await store.get("g", 1, "あ。");
    expect(synthesize).toHaveBeenCalledTimes(3); // 1 は残っていたので作り直していない
  });

  it("バイト数の上限でも捨てる", async () => {
    const store = createAudioStore({ synthesize: () => Promise.resolve(wav(600)), maxBytes: 1000 });

    await store.get("g", 1, "あ。");
    await store.get("g", 2, "い。");

    expect(store.stats().entries).toBe(1);
    expect(store.stats().bytes).toBe(600);
  });

  it("1件で上限を超えるものは覚えない（自分自身を追い出すだけなので）", async () => {
    const store = createAudioStore({ synthesize: () => Promise.resolve(wav(5000)), maxBytes: 1000 });

    expect((await store.get("g", 1, "あ。")).byteLength).toBe(5000);
    expect(store.stats()).toMatchObject({ entries: 0, bytes: 0 });
  });

  it("合成が終わったら in-flight から消える", async () => {
    const store = createAudioStore({ synthesize: () => Promise.resolve(wav(1)) });
    await store.get("g", 1, "あ。");
    expect(store.stats().inFlight).toBe(0);
  });
});
