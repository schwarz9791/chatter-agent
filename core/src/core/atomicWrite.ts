/**
 * tmp を書いてから rename する原子書き込み。
 *
 * ★ 素の `writeFileSync` は対象を O_TRUNC してから書くので、書き込みの途中でプロセスが
 *   落ちると、読み手には空バイト・あるいは書きかけのバイト列が見える窓ができる。
 *   同じディレクトリに `.tmp` を書いてから rename すれば、POSIX の rename はファイル
 *   システム内で原子的なので、読み手には「書く前」か「書き終わった後」の2状態しか
 *   見えない。
 *
 * 何が壊れるかは呼び出し側ごとに違う（配信キューの entry か、記録の seq state か、
 * 応答待ちの重複抑制 state か、spool の進捗サイドカーか）。ここは機構だけを持ち、
 * 具体的な帰結は各呼び出し側のコメントに残す。
 */

import * as fs from "fs";

const TMP_SUFFIX = ".tmp";

export function writeFileAtomic(filePath: string, data: string): void {
  const tmp = `${filePath}${TMP_SUFFIX}`;
  fs.writeFileSync(tmp, data);
  fs.renameSync(tmp, filePath);
}
