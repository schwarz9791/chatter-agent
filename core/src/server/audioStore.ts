/**
 * 合成済み WAV の置き場。
 *
 * ★ **ディスクを持たない。** 要るのはクライアントの先読み窓（既定 `synthesisLookahead + 1`
 *   = 4 件）ぶんだけで、溢れたら捨てて次の GET で作り直せばよい。ディスクにすると
 *   `{root}/audio/` の GC、起動時の作り直し、ack との寿命合わせ、受け取った文字列を
 *   `path.join` に渡す経路が全部ついてくるが、そのどれも「作り直せる」なら要らない。
 *
 * ★ **合成は GET が来たときに始める。** サーバー側の投機的な先読みを持たないことで、
 *   #29 の完了判定のうち2つが構造的に満たされる:
 *   「誰も繋いでいない間は合成しない」（誰も GET しない）と
 *   「複数クライアントでも合成は1回」（下の single-flight）。
 *
 * ★ **テキストの権威はキュー。** ここはテキストを保持しない。呼び出し側
 *   （`httpServer`）が `speechQueue.read()` から取ってきたものを渡す。
 *   保持すると「キューから消えたのに古い本文で合成する」経路ができる。
 */

/** 合成の失敗。`httpServer` が 503 に落とすために型で区別する */
export class SynthesisUnavailableError extends Error {
  constructor(message: string, options?: { cause?: unknown }) {
    super(message, options);
    this.name = "SynthesisUnavailableError";
  }
}

export interface AudioStoreDeps {
  /** 1文ぶんの WAV。`tts/voicevoxClient.ts` の `synthesize` をそのまま渡す */
  synthesize: (text: string) => Promise<ArrayBuffer>;
  /** 保持する件数の上限 */
  maxEntries?: number;
  /** 保持する合計バイト数の上限 */
  maxBytes?: number;
}

export interface AudioStore {
  /**
   * `(epoch, seq)` の WAV。無ければ `text` から合成して返す。
   *
   * 同じキーへの同時要求は**1回の合成にまとめる**（複数クライアントが同じ文を
   * 同時に取りに来ても、エンジンを2度叩かない）。
   */
  get(epoch: string, seq: number, text: string): Promise<ArrayBuffer>;
  /** テスト・診断用 */
  stats(): { entries: number; bytes: number; inFlight: number };
}

/**
 * 既定の上限。
 *
 * クライアントは先読み窓（既定 4 件）ぶんしか同時に取りに来ないので、16 件あれば
 * 複数クライアントと再接続時の取り直しを吸収できる。バイト数の上限は、長文1件が
 * 数MBになりうることへの歯止め。**どちらも「溢れたら作り直す」だけ**なので、
 * 大きくする理由が無い。
 */
const DEFAULT_MAX_ENTRIES = 16;
const DEFAULT_MAX_BYTES = 32 * 1024 * 1024;

function keyFor(epoch: string, seq: number): string {
  return `${epoch}:${seq}`;
}

export function createAudioStore(deps: AudioStoreDeps): AudioStore {
  const maxEntries = deps.maxEntries ?? DEFAULT_MAX_ENTRIES;
  const maxBytes = deps.maxBytes ?? DEFAULT_MAX_BYTES;

  /** Map の挿入順を LRU に使う（触ったら delete → set で末尾へ回す） */
  const cache = new Map<string, ArrayBuffer>();
  let bytes = 0;

  /** 合成中の約束。同じキーの2人目はこれに相乗りする */
  const inFlight = new Map<string, Promise<ArrayBuffer>>();

  function evict(): void {
    while (cache.size > maxEntries || bytes > maxBytes) {
      const oldest = cache.keys().next();
      if (oldest.done === true) break;
      const wav = cache.get(oldest.value);
      cache.delete(oldest.value);
      bytes -= wav?.byteLength ?? 0;
    }
  }

  function remember(key: string, wav: ArrayBuffer): void {
    // 1件で上限を超えるものは覚えない。覚えると自分自身を追い出すだけで、
    // ループの回数が増える以外に何も起きない
    if (wav.byteLength > maxBytes) return;
    cache.set(key, wav);
    bytes += wav.byteLength;
    evict();
  }

  return {
    async get(epoch, seq, text) {
      const key = keyFor(epoch, seq);

      const cached = cache.get(key);
      if (cached !== undefined) {
        // LRU: 触ったものを末尾へ回す
        cache.delete(key);
        cache.set(key, cached);
        return cached;
      }

      const pending = inFlight.get(key);
      if (pending !== undefined) return pending;

      const promise = deps
        .synthesize(text)
        .then((wav) => {
          remember(key, wav);
          return wav;
        })
        .catch((err: unknown) => {
          // ★ 「あとで取りに来い」に落とす。クライアントは 503 を受けても
          //    試行回数を減らさないので、エンジンが戻れば追いつける
          throw new SynthesisUnavailableError(err instanceof Error ? err.message : String(err), { cause: err });
        })
        .finally(() => {
          inFlight.delete(key);
        });

      inFlight.set(key, promise);
      return promise;
    },

    stats: () => ({ entries: cache.size, bytes, inFlight: inFlight.size }),
  };
}
