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

import { TtsHttpError } from "../tts/voicevoxClient";

/**
 * 合成できなかった。`httpServer` が 503 に落とすために型で区別する。
 *
 * ★ **エンジンの応答で「恒久的」を判定して 404 に落とさないこと。** 一度そう設計したが、
 *   404 はクライアント側で `audioGone → finish → ack → ackUpTo` まで通り、
 *   **キューのファイルが物理削除される**。`ttsSpeakerId` を30秒後に直しても復元できない
 *   （503 のままなら直した瞬間に全部鳴る）。しかも「恒久」の線引きは実質不可能で、
 *   モデルロード中の 4xx・`ttsBaseUrl` のパス違いで別サービスが返す 404/405・
 *   プロキシの 407 まで巻き込む。「溜まった発話を今さら鳴らすか」は
 *   `speechMaxAgeMs`（既定0＝無効）で既にユーザーの選択として表現してある。
 *
 * ★ 代わりに**理由を持ち回る**。無音の原因はログにしか出ないので、ここで捨てない。
 */
export class SynthesisUnavailableError extends Error {
  /** エンジンが応答したなら、その HTTP ステータス */
  readonly status: number | null;
  constructor(message: string, options?: { cause?: unknown; status?: number | null }) {
    super(message, options);
    this.name = "SynthesisUnavailableError";
    this.status = options?.status ?? null;
  }
}

export interface AudioStoreDeps {
  /**
   * いま設定されている声。**キャッシュキーの一部**になる。
   *
   * ★ `synthesize` の中で config を読み直すのではなく、ここで**1回だけ解決して渡す**こと。
   *   別々に読むと、キーを決めた後・合成する前に `config.json` が書き換わったときに
   *   **声 B の WAV が声 A のキーで入る**。
   */
  currentVoice: () => Voice;
  /** 1文ぶんの WAV。`tts/voicevoxClient.ts` の `synthesize` を、解決済みの声で呼ぶ */
  synthesize: (text: string, voice: Voice) => Promise<ArrayBuffer>;
  /** 保持する件数の上限 */
  maxEntries?: number;
  /** 保持する合計バイト数の上限 */
  maxBytes?: number;
  /** 同時に走らせる合成の上限 */
  maxInFlight?: number;
}

/** キャッシュキーに混ぜる、声を決める設定 */
export interface Voice {
  baseUrl: string;
  speakerId: number;
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

/**
 * 同時に走らせる合成の上限。
 *
 * ★ **無制限にしないこと。** `/synthesis` は CPU 律速で、この口は既定で `0.0.0.0` に
 *   **無認証**で開いている（`server/index.ts` が自分でそう警告している）。`epoch` は
 *   正規クライアントに1フレーム届けば分かるので、キューにある seq（最大500）を並べて
 *   GET すれば **500本の合成が同時にエンジンへ飛ぶ**。実質 DoS になる。
 *
 * ★ クライアント1台の先読み窓は既定4件なので、8 あれば複数クライアントでも詰まらない。
 *   超えた分は 503（あとで取りに来い）にすればよく、クライアントは待つだけ。
 */
const DEFAULT_MAX_IN_FLIGHT = 8;

function keyFor(voice: Voice, epoch: string, seq: number): string {
  // ★ 声をキーに混ぜること。`ttsSpeakerId` を直しても、LRU にいる分は古い声のまま返る
  return `${voice.baseUrl}|${voice.speakerId}|${epoch}:${seq}`;
}

export function createAudioStore(deps: AudioStoreDeps): AudioStore {
  const maxEntries = deps.maxEntries ?? DEFAULT_MAX_ENTRIES;
  const maxBytes = deps.maxBytes ?? DEFAULT_MAX_BYTES;
  const maxInFlight = deps.maxInFlight ?? DEFAULT_MAX_IN_FLIGHT;

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
      // ★ 声は**ここで1回だけ**解決する（→ `AudioStoreDeps.currentVoice`）
      const voice = deps.currentVoice();
      const key = keyFor(voice, epoch, seq);

      const cached = cache.get(key);
      if (cached !== undefined) {
        // LRU: 触ったものを末尾へ回す
        cache.delete(key);
        cache.set(key, cached);
        return cached;
      }

      const pending = inFlight.get(key);
      if (pending !== undefined) return pending;

      if (inFlight.size >= maxInFlight) {
        throw new SynthesisUnavailableError(
          `合成が同時に ${inFlight.size} 件走っているので受け付けません（上限 ${maxInFlight}）`,
        );
      }

      const promise = deps
        .synthesize(text, voice)
        .then((wav) => {
          remember(key, wav);
          return wav;
        })
        .catch((err: unknown) => {
          // ★ 「あとで取りに来い」に落とす。クライアントは 503 を受けても
          //    試行回数を減らさないので、エンジンが戻れば追いつける。
          //    理由は握り潰さずに持ち回る（無音の原因はログにしか出ない）
          const status = err instanceof TtsHttpError ? err.status : null;
          throw new SynthesisUnavailableError(err instanceof Error ? err.message : String(err), { cause: err, status });
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
