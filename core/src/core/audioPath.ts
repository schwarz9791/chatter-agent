/**
 * 合成済み音声の URL パス。
 *
 * ```
 * /audio/<epoch>-<12桁ゼロ埋めした seq>.wav
 * ```
 *
 * ★ **相対パスとして配ること。** サーバーは自分がどのアドレスで到達されたかを知らない。
 *   既定の bind は `0.0.0.0` で、これは**接続先ではない**（`player/client.ts` の
 *   `deriveServerUrl` に同じ罠のコメントがある）。絶対 URL を組むと
 *   `http://0.0.0.0:8570/...` になり、Mac 上のクライアントは偶然繋がるが
 *   **LAN 上の XR グラスからは繋がらない**。クライアントが自分の WebSocket 接続先の
 *   authority に対して解決する。
 *
 * ★ **`epoch` を短縮しないこと。** ブラウザの `<audio src>` は `Origin` を送らないので
 *   サーバーの Origin 検査を素通りする。`epoch` が推測しにくいことが、LAN 上での
 *   実質的な最後の防御になっている（認証そのものは → issue #3）。
 */

import type { SpeechEpoch } from "./types";

/** `speechQueue` のファイル名と同じ桁数に揃える（`ls` でも URL でも順序が見える） */
const SEQ_DIGITS = 12;

/**
 * ★ `epoch` の charset（`core/types.ts` の `isValidEpoch`）と揃えること。
 *   ここを緩めると、`/audio/../../etc/passwd` のような入力が通る余地ができる。
 *   `epoch` は `-` を含みうるが、`seq` が固定幅の数字なので後ろから決まる。
 */
const AUDIO_PATH = /^\/audio\/([A-Za-z0-9][A-Za-z0-9._-]{0,63})-(\d{12})\.wav$/;

export interface AudioKey {
  epoch: SpeechEpoch;
  seq: number;
}

export function buildAudioPath(epoch: SpeechEpoch, seq: number): string {
  return `/audio/${epoch}-${String(seq).padStart(SEQ_DIGITS, "0")}.wav`;
}

/** 読めたら `(epoch, seq)`、読めなければ null。**受け取った文字列をパスの組み立てに使わない** */
export function parseAudioPath(pathname: string): AudioKey | null {
  const matched = AUDIO_PATH.exec(pathname);
  if (matched === null) return null;

  const seq = Number(matched[2]);
  // 12桁ゼロ埋めなので安全整数を超えることはないが、採番と同じ基準で揃えておく
  if (!Number.isSafeInteger(seq) || seq < 1) return null;

  return { epoch: matched[1]!, seq };
}

/** クライアントが受け取った `audio.path` を検証する。**絶対 URL は通さない** */
export function isAudioPath(value: unknown): value is string {
  return typeof value === "string" && AUDIO_PATH.test(value);
}
