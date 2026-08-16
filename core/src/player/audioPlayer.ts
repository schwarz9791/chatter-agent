/**
 * WAV を一時ファイルに置いて、外部コマンド（既定は macOS の `afplay`）で鳴らす部品。
 *
 * ★ `ready` を「WAV を書き終えた状態」と定義してある。合成結果を Buffer のまま持たないので、
 *   メモリが先読み件数に比例して伸びないし、後始末の経路が「ファイルを消す」1本に揃う。
 *
 * ★ コマンドを設定で差し替えられるのは移植性のためだけではない。`/usr/bin/true` や
 *   `/bin/sleep` に差し替えられることが、そのままオーディオデバイス無しで CI を回せることになる
 *   （→ `scripts/verify-player.mjs`）。
 */

import { spawn } from "child_process";
import type { ChildProcess } from "child_process";
import * as fs from "fs";
import * as path from "path";

/** 再生プロセスに SIGTERM を送ってから SIGKILL するまでの猶予 */
const KILL_GRACE_MS = 1_000;

/** WAV の長さが読めなかったときの上限。1文がこれを超えることは実際には無い */
const FALLBACK_TIMEOUT_MS = 120_000;

/** 実長に対する余裕。afplay の起動と、デバイスが詰まったときのぶん */
const TIMEOUT_SLACK_MS = 5_000;

/** `speechQueue` のファイル名と同じ規則。`ls` で順序が見える */
const SEQ_DIGITS = 12;

/**
 * RIFF/PCM ヘッダから再生時間（ミリ秒）を読む。読めなければ null。
 *
 * ★ 再生のタイムアウトを固定値にすると必ずどちらかで壊れる。1文は 0.5 秒から 20 秒以上まで
 *   幅があるので、30 秒固定だと長文の後半が切れ、120 秒固定だとハングの検出が実質機能しない。
 *   Bluetooth ヘッドフォンが再生中に切れると `afplay` は戻ってこないことがあり、
 *   head-of-line blocking なので**1回のハングで以後すべてが無音になる**。
 */
export function wavDurationMs(wav: ArrayBuffer): number | null {
  const view = new DataView(wav);
  if (view.byteLength < 12) return null;

  const tag = (offset: number) => String.fromCharCode(...new Uint8Array(wav, offset, 4));
  if (tag(0) !== "RIFF" || tag(8) !== "WAVE") return null;

  let byteRate: number | null = null;
  let dataBytes: number | null = null;

  // チャンクを順に辿る。fmt と data の間に LIST 等が挟まることがあるので位置を決め打ちしない
  let offset = 12;
  while (offset + 8 <= view.byteLength) {
    const id = tag(offset);
    const size = view.getUint32(offset + 4, true);
    const body = offset + 8;

    if (id === "fmt " && size >= 16 && body + 16 <= view.byteLength) {
      byteRate = view.getUint32(body + 8, true);
    } else if (id === "data") {
      // ストリーミングで書かれた WAV はサイズが 0 や 0xFFFFFFFF のことがある。実体で測り直す
      const declared = size;
      const actual = view.byteLength - body;
      dataBytes = declared > 0 && declared <= actual ? declared : actual;
      break;
    }

    // チャンクは 2 バイト境界に揃う
    offset = body + size + (size % 2);
  }

  if (!byteRate || !dataBytes || byteRate <= 0) return null;
  return Math.round((dataBytes / byteRate) * 1000);
}

export interface AudioPlayerOptions {
  /** WAV を置くディレクトリ。起動時に作り直される */
  tmpDir: string;
  command: string;
  /** `{file}` が WAV のパスに置換される */
  args: string[];
  /** テストから差し替える */
  spawnImpl?: typeof spawn;
}

export interface AudioPlayer {
  /** 一時ディレクトリを作り直す。前回の残骸を消すので、ロックを取ってから呼ぶこと */
  reset(): void;
  /** WAV を書いてパスを返す */
  write(seq: number, wav: ArrayBuffer): string;
  /** 鳴らし終えたら解決する。異常終了・タイムアウトでは reject */
  play(file: string, timeoutMs: number): Promise<void>;
  /** 使い終わった WAV を消す。存在しなくても黙って戻る */
  discard(file: string): void;
  /** 再生中のプロセスを止める（シャットダウン用） */
  stopAll(): void;
  /** 一時ディレクトリごと消す（シャットダウン用） */
  cleanup(): void;
}

/** 合成した WAV の長さから、再生を諦めるまでの時間を決める */
export function playbackTimeoutMs(wav: ArrayBuffer): number {
  const duration = wavDurationMs(wav);
  return duration === null ? FALLBACK_TIMEOUT_MS : duration * 2 + TIMEOUT_SLACK_MS;
}

export function createAudioPlayer(options: AudioPlayerOptions): AudioPlayer {
  const { tmpDir, command, args } = options;
  const doSpawn = options.spawnImpl ?? spawn;
  const running = new Set<ChildProcess>();

  function fileFor(seq: number): string {
    // ★ 受け取った seq をそのままパスに入れない。`speechFrame` が非負の安全整数だけを
    //   通しているので、ここで数値として整形すれば経路が閉じる
    return path.join(tmpDir, `${String(seq).padStart(SEQ_DIGITS, "0")}.wav`);
  }

  return {
    reset() {
      fs.rmSync(tmpDir, { recursive: true, force: true });
      fs.mkdirSync(tmpDir, { recursive: true });
    },

    write(seq, wav) {
      const file = fileFor(seq);
      fs.writeFileSync(file, Buffer.from(wav));
      return file;
    },

    play(file, timeoutMs) {
      return new Promise((resolve, reject) => {
        const child = doSpawn(
          command,
          args.map((arg) => arg.replaceAll("{file}", file)),
          // ★ shell は噛ませない。パスに空白が入るだけで壊れるし、設定ファイル経由の
          //   コマンド実行経路をわざわざ作る理由が無い
          { shell: false, stdio: ["ignore", "ignore", "pipe"] },
        );
        running.add(child);

        let stderr = "";
        child.stderr?.on("data", (chunk) => {
          // 数百バイトあれば原因は分かる。ハングした相手に無限に溜めない
          if (stderr.length < 2048) stderr += String(chunk);
        });

        // ★ error / exit / タイムアウトの3経路すべてで、必ず一度だけ決着させる。
        //   spawn の失敗（ENOENT / EACCES）は exit ではなく error で来るので、
        //   exit だけを待つ実装は Promise が settle せずそのまま恒久停止する
        let settled = false;
        let killTimer: NodeJS.Timeout | null = null;

        const finish = (err: Error | null) => {
          if (settled) return;
          settled = true;
          clearTimeout(timer);
          running.delete(child);
          if (err) reject(err);
          else resolve();
        };

        const timer = setTimeout(() => {
          // ★ SIGKILL の予約は finish より先に置き、finish では消さないこと。
          //   SIGTERM を無視する相手（ドライバごと固まった afplay）に効かなくなる
          child.kill("SIGTERM");
          killTimer = setTimeout(() => child.kill("SIGKILL"), KILL_GRACE_MS);
          killTimer.unref();
          finish(new Error(`再生が ${timeoutMs}ms で終わりませんでした`));
        }, timeoutMs);
        timer.unref();

        child.on("error", (err) => finish(new Error(`再生コマンドを起動できません (${command}): ${String(err)}`)));

        child.on("exit", (code, signal) => {
          // SIGTERM で素直に死んだなら、予約した SIGKILL はもう要らない
          if (killTimer) clearTimeout(killTimer);
          running.delete(child);
          if (code === 0) {
            finish(null);
            return;
          }
          const detail = stderr.trim() ? `: ${stderr.trim()}` : "";
          finish(new Error(`再生コマンドが異常終了しました (code=${code} signal=${signal})${detail}`));
        });
      });
    },

    discard(file) {
      try {
        fs.rmSync(file, { force: true });
      } catch (err) {
        console.warn(`[Player] 一時ファイルを消せませんでした (${file}):`, err);
      }
    },

    stopAll() {
      // 親が exit しても afplay は死なない。プロセスが消えた後も音が鳴り続ける
      for (const child of running) child.kill("SIGTERM");
    },

    cleanup() {
      fs.rmSync(tmpDir, { recursive: true, force: true });
    },
  };
}
