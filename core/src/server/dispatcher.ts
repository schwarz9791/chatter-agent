/**
 * キューを見て「まだ配信していないものは何か」を決める部品。
 *
 * `server/` は判断ロジックを持たない方針（docs/core.md）だが、この判断──何を配信済みとし、
 * 何を消してよいか、どの世代を配信するか──はレビューの指摘が集中した箇所だった。
 * `server/index.ts` の `main()` に埋めたままだとユニットテストから触れないので、
 * 純粋な部品として切り出す。
 */

import type { SpeechQueue } from "../core/speechQueue";
import type { SpeechEpoch, SpeechRecord } from "../core/types";

export interface DispatcherDeps {
  queue: SpeechQueue;
  /** 接続中の全クライアントへ流す */
  broadcast: (line: string) => void;
  /** テストから時刻を固定するため（世代解決の間引きに使う） */
  now?: () => number;
}

export interface Dispatcher {
  /** キューを見て、まだ配信していないものを流す */
  poll(): void;
  /** 接続直後の追いつき。配信済みのものだけを送る */
  catchUp(send: (line: string) => boolean): void;
  /**
   * クライアントの累積 ack。
   *
   * `epoch` が `null` なら「世代を名乗らない ack」で、現在の世代のものとして扱う
   * （契約上 `epoch` は任意フィールド → docs/protocol.md）。
   */
  ack(seq: number, epoch?: SpeechEpoch | null): void;
}

/**
 * 世代の解決が**空振りだった**あと、次に解決してよくなるまでの間隔。
 *
 * ★ 解決は**全 entry の read**（500件で実測 7.3〜9.6ms）なので、毎 poll（20回/秒）
 *   走らせると 1 コアの 19% を持っていく。旧世代の entry が先頭に残り続けると
 *   プローブが毎回不一致を返すので、その状態で degrade しないための歯止め。
 *
 * ★ **空振りのときだけ効かせること。** 世代が実際に切り替わったときまで間引くと、
 *   採番のやり直しの検出そのものが遅れる（＝この歯止めが直そうとしている
 *   「新世代が配信されない」に、時間の形で戻ってしまう）。
 */
const RESOLVE_BACKOFF_MS = 1_000;

/** 警告済み epoch の保持上限。クライアントが名乗る値なので、無制限に溜めない */
const WARNED_EPOCH_LIMIT = 64;

export function createDispatcher(deps: DispatcherDeps): Dispatcher {
  const now = deps.now ?? (() => Date.now());

  /**
   * 配信済みの seq の集合。
   *
   * ★ 単調増加の水位（旧 `sentUpTo`）ではなく集合で持つ。水位だと、キューの採番が
   *   1からやり直された（`~/.config/chatter-agent` の削除やバックアップ復元）ときに、
   *   新しい seq が古い水位を下回ってしまい**永久に配信されなくなる**。
   *
   * ★ **ただし集合も `seq` 単独キーであることに変わりはない。** 同じ seq のファイルが
   *   別世代の内容に**入れ替わった**ことは、ファイル名の集合を見ても分からない。
   *   それを拾うのが下の世代プローブ（`poll` の先頭）。
   */
  const delivered = new Set<number>();

  /**
   * 世代違いで配信を見送った seq。
   *
   * `delivered` と同じく、キューから消えたぶんを毎 poll で落とす。これを持たないと
   * 20回/秒のポーリングのたびに同じファイルを読み直すことになる。
   */
  const skipped = new Set<number>();

  /**
   * 今配信している採番の世代。
   *
   * ★ **本来ここは1つしか無いはず。** 採番がやり直されたときは、ロックを持っている
   *   書き手（CLI）が `speechQueue.clear()` で旧世代を捨てる（`cli/publish.ts`）。
   *   ここにあるのは、CLI を経由しない経路（バックアップの復元、手で置いたファイル）に
   *   対する安全網。
   */
  let currentEpoch: SpeechEpoch | null = null;
  /** 一度警告した世代。20回/秒のポーリングと ack のたびに同じ行を出さないため */
  const warnedEpochs = new Set<string>();
  /** 空振りの解決のあと、次に解決してよくなる時刻 */
  let resolveBackoffUntil = Number.NEGATIVE_INFINITY;
  /**
   * 次の poll で世代を解決し直す。
   *
   * ★ プローブは**先頭しか見ない**ので、`clear()` が失敗して新世代が高い seq に
   *   現れたケース（先頭は旧世代のまま）を拾えない。ループが別世代の entry を
   *   踏んだらここを立てて、次の poll で全件から勝者を決め直す。
   */
  let resolveWanted = false;

  function warnOnce(key: string, message: string): void {
    if (warnedEpochs.has(key)) return;
    // ★ 上限を付けること。`ack:<epoch>` の側はクライアントが名乗る値なので、
    //   でたらめな epoch を送り続けられると無制限に増える
    if (warnedEpochs.size >= WARNED_EPOCH_LIMIT) warnedEpochs.clear();
    warnedEpochs.add(key);
    console.warn(message);
  }

  /**
   * いま present な entry を全部読み、**どの世代を配信するか**を決め直す。
   *
   * ★ 勝者は「`Date.parse(ts)` が最大の entry の世代」。「どちらの世代が新しいか」は
   *   ファイル名からも走査順からも決まらない — 採番は必ず 1 から始まるので、
   *   やり直し直後は**新しい世代が先頭に来る**。判定できるのは `ts` だけ。
   *   （`ts` の形は `speechQueue.read()` が担保する。字句比較にしないこと）
   *
   * ★ **1回の `append` はバッチ全体で `ts` が1つ**なので、CLI が30文を書いている
   *   最中に prefix だけ見えても勝者は変わらない。
   *
   * ★ **敗者を削除しないこと。** CLI と server は別のロック（`speak.lock` /
   *   `server.lock`）なので、全件 read → unlink の間に CLI が rename を完了すると
   *   **書いたばかりの entry を消す**。しかもその窓が開くのは世代交代の瞬間だけで、
   *   この関数を起動するイベントと同一。時計が巻き戻ったときは新世代を消し続ける
   *   ループにもなる。掃除は CLI の `clear()` と、サーバー起動時の `dropOlderThan` に任せる。
   *
   * 読んだ record を返すので、呼び出し側は同じ poll の中で配信まで進める。
   */
  function resolveGeneration(seqs: number[]): Map<number, SpeechRecord> {
    const records = new Map<number, SpeechRecord>();
    let best: SpeechRecord | null = null;
    let bestTs = Number.NEGATIVE_INFINITY;

    for (const seq of seqs) {
      const record = deps.queue.read(seq);
      if (record === null) continue;
      records.set(seq, record);

      const ts = Date.parse(record.ts);
      if (ts > bestTs) {
        bestTs = ts;
        best = record;
      }
    }

    if (best === null || best.epoch === currentEpoch) {
      // 空振り。旧世代が先頭に残り続けている状態なので、しばらく間引く
      resolveBackoffUntil = now() + RESOLVE_BACKOFF_MS;
      return records;
    }
    resolveBackoffUntil = Number.NEGATIVE_INFINITY;

    if (currentEpoch !== null) {
      console.warn(`[Server] 採番のやり直しを検出しました（epoch=${best.epoch}）`);
    }
    currentEpoch = best.epoch;
    // 旧世代について覚えていたことを全部捨てる。`warnedEpochs` も落とさないと、
    // A → B → A と戻ったときに2回目の「やり直し」が無言になる
    delivered.clear();
    skipped.clear();
    warnedEpochs.clear();
    return records;
  }

  /** 先頭から数えて最初に読めた entry。世代プローブに使う */
  function firstReadable(seqs: number[]): SpeechRecord | null {
    for (const seq of seqs) {
      const record = deps.queue.read(seq);
      if (record !== null) return record;
    }
    return null;
  }

  return {
    poll() {
      const seqs = deps.queue.list();

      // キューから消えた分（ack / trim / 起動時の掃除）を集合から落とす。
      // 集合の大きさをキューの上限で頭打ちにするための後始末
      const present = new Set(seqs);
      for (const seq of delivered) if (!present.has(seq)) delivered.delete(seq);
      for (const seq of skipped) if (!present.has(seq)) skipped.delete(seq);

      if (seqs.length === 0) return;

      // ★ 世代プローブ。**先頭を毎 poll 1件だけ読む**（実測 ≒ 0.014ms、`list()` の 5%）。
      //   `delivered` は seq 単独キーなので、CLI が `clear()` してから同じ seq を
      //   別世代で書き直すと、ファイル名の集合からは何も変わって見えない。
      //   その状態では prune も効かず、全 seq が「配信済み」として飛ばされ、
      //   **新世代の最初のメッセージが丸ごと落ちる**（しかも ack も世代違いで弾かれる）。
      //
      // ★ 「先頭が `delivered` にあるときだけ読む」のような条件を付けないこと。
      //   定常状態では先頭はほぼ常に `delivered` にいるので節約にならないうえ、
      //   **先頭が読めない entry だと素通りして**上の事故がそのまま再現する。
      let records: Map<number, SpeechRecord> | null = null;
      const probe = firstReadable(seqs);
      if (resolveWanted || (probe !== null && probe.epoch !== currentEpoch)) {
        if (now() >= resolveBackoffUntil) {
          resolveWanted = false;
          records = resolveGeneration(seqs);
        }
      }

      for (const seq of seqs) {
        if (delivered.has(seq) || skipped.has(seq)) continue;

        const record = records?.get(seq) ?? deps.queue.read(seq);
        if (record === null || record === undefined) {
          // 読めない entry は配信できない（protocol.md の「SpeechRecord の JSON 以外は
          // 送らない」を破れない）。配信済み扱いにしておかないと、毎 poll（20回/秒）
          // 同じ entry を見て警告し続ける
          console.warn(`[Server] seq=${seq} を読めなかったのでスキップします`);
          delivered.add(seq);
          continue;
        }

        if (currentEpoch === null) currentEpoch = record.epoch;

        if (record.epoch !== currentEpoch) {
          // ★ 先頭が現世代のまま、高い seq に別世代が現れた形（`clear()` の失敗など）。
          //   プローブでは拾えないので、次の poll で全件から勝者を決め直させる
          resolveWanted = true;
          // ★ 消さない（`resolveGeneration` の doc 参照）。掃除は CLI の `clear()` と
          //   サーバー起動時の `dropOlderThan` が行う
          warnOnce(
            record.epoch,
            `[Server] 旧世代（epoch=${record.epoch}）の entry は配信しません。` +
              "サーバーを再起動するまで残ります（trim は seq 昇順なので、" +
              "旧世代が高い seq を占めている間は新しい entry から先に捨てられます）",
          );
          skipped.add(seq);
          continue;
        }

        deps.broadcast(`${JSON.stringify(record)}\n`);
        delivered.add(seq);
      }
    },

    catchUp(send) {
      let sent = 0;
      let truncated = false;

      // 配信済みのものだけを seq 昇順に送る。queue.list() が昇順を返すので、
      // delivered（Set）側を別途ソートする必要はない。
      //
      // ★ ここに `currentEpoch` のフィルタを足さないこと。`currentEpoch` が古いまま
      //   詰まったときに追いつきまで無言になると、**今なら気づける症状が消える**
      for (const seq of deps.queue.list()) {
        if (!delivered.has(seq)) continue;

        const record = deps.queue.read(seq);
        if (record === null) continue; // 追いつきの走査中に ack 等で消えた

        if (!send(`${JSON.stringify(record)}\n`)) {
          truncated = true;
          break;
        }
        sent++;
      }

      if (truncated) {
        // 旧文言（「最後の seq から接続し直すこと」）は ?since= の廃止で実行不能。
        // バックプレッシャでは wsServer 側が接続を切るので、繋ぎ直せば未 ack 分が
        // 接続直後の追いつきで再送される、というのが今の実態
        console.warn(`[Server] 追いつきを ${sent} 件で打ち切りました。繋ぎ直せば未 ack 分が再送されます`);
      } else if (sent > 0) {
        console.log(`[Server] 未読 ${sent} 件を送りました`);
      }
    },

    ack(seq, epoch = null) {
      // ★ 旧世代の ack を通さない。累積 ack は `seq <= upTo` を**ファイル名で範囲削除**するので、
      //   採番がやり直された直後に旧世代の ack（例: 500）が届くと、まだ喋っていない
      //   新しい seq 1, 2 がまとめて消える。以前はクライアント側がこれを自衛していたが、
      //   その自衛は「open では保留 ack を送らない」という約40行の状態機械を要求していた。
      //
      // ★ 黙って捨てないこと。捨てられた ack はキューを減らさないので、症状は
      //   「上限まで溜まって古い方から捨てられ続ける」になり、エラーが1行も出ない
      if (epoch !== null && currentEpoch !== null && epoch !== currentEpoch) {
        warnOnce(`ack:${epoch}`, `[Server] 旧世代（epoch=${epoch}）の ack は無視します。現在は epoch=${currentEpoch}`);
        return;
      }

      // ★ クライアント由来の値。配信済みの範囲に頭を押さえること。`parseAck` は形
      //   （非負の安全整数）しか見ないので、ここで押さえないと `Number.MAX_SAFE_INTEGER`
      //   のような値が通り、まだ配信していない entry まで含めてキューを全消しされる
      let upTo = 0;
      for (const s of delivered) if (s <= seq && s > upTo) upTo = s;
      if (upTo === 0) return;

      const removed = deps.queue.ackUpTo(upTo);
      if (removed > 0) console.log(`[Server] seq<=${upTo} を ${removed} 件消しました`);
    },
  };
}
