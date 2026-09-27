/**
 * AivisSpeech / VOICEVOX の互換 API を叩く `TtsEngine`（→ `tts/ttsEngine.ts`）。
 *
 * `POST /audio_query?text=…&speaker=<id>` で読み仮名とアクセントの入ったクエリを作り、
 * それをそのままボディに載せて `POST /synthesis?speaker=<id>` に投げると WAV が返る。
 * cc-mascot も同じ2段構えだが、こちらは公開されている API 仕様を見て書いてある
 * （そのため cc-mascot 由来の帰属表示は付かない → docs/origin.md）。
 *
 * ★ `AudioQuery` の中身は解釈しない。エンジンが返した JSON をそのまま返送するのが
 *   最も安全（エンジンのバージョン差に強い）。
 *
 * ★★ **例外は `speedScale` だけ**（#76 で入った `ttsSpeedScale`。→ `applySpeedScale`）。
 *   合成し直さないと話速は変えられず、再生側で伸縮するとリップシンク（#58）が
 *   WAV から作ったエンベロープとズレるため、ここで触る以外の道が無い。
 *   **例外をここ1つに留めること** —— `pitchScale` / `intonationScale` を同じ理屈で
 *   足していくと、「そのまま返送する」という一番安全な形が失われる。
 */

import {
  createRequester,
  describe,
  looksLikeWav,
  TtsHttpError,
  type TtsEngine,
  type TtsEngineOptions,
} from "./ttsEngine";

/** エンジンが返す読み仮名クエリ。中身は解釈せずそのまま返送する（例外は `applySpeedScale`） */
export type AudioQuery = Record<string, unknown>;

interface SpeakerStyle {
  id: number;
  name: string;
}

interface Speaker {
  name: string;
  speaker_uuid: string;
  styles: SpeakerStyle[];
}

/**
 * `audio_query` が返した JSON の `speedScale` **だけ**を書き換える。**純粋関数。**
 *
 * ★ **キーを持たないエンジンには何もしない。** 無いところに作ると、そのエンジンが
 *   知らないフィールドを載せた JSON を返送することになる。「触る」と「生やす」は別。
 *
 * ★ **1.0 でも、キーがあれば書く。** 「既定値なら触らない」形にすると、
 *   エンジン側の既定が 1.0 でないときに**等倍へ戻せなくなる**。
 *
 * ★ **元のオブジェクトを破壊しない。** `audioStore` は single-flight で1つの
 *   `synthesize` を共有するが、`AudioQuery` はその中で作られて捨てられるので現状は
 *   共有されない —— それは今の実装の都合であって契約ではない。
 */
export function applySpeedScale(query: AudioQuery, speedScale: number | undefined): AudioQuery {
  if (speedScale === undefined) return query;
  if (!Object.hasOwn(query, "speedScale")) return query;
  return { ...query, speedScale };
}

/** `<話者名>（<スタイル名>）` の一覧。`id` は文字列にする（`TtsEngine.listVoices` の契約） */
function flattenStyles(speakers: Speaker[]): { id: string; label: string }[] {
  const out: { id: string; label: string }[] = [];
  for (const speaker of speakers) {
    if (!Array.isArray(speaker?.styles)) continue;
    for (const style of speaker.styles) {
      if (typeof style?.id !== "number") continue;
      out.push({ id: String(style.id), label: `${speaker.name}（${style.name}）` });
    }
  }
  return out;
}

export function createVoicevoxEngine(options: TtsEngineOptions): TtsEngine {
  const { baseUrl, voiceId, timeoutMs, speedScale } = options;
  const request = createRequester(timeoutMs);
  const speaker = encodeURIComponent(voiceId);

  return {
    baseUrl,

    // VOICEVOX 系のスタイル ID は1声を1つの ID で指す。完全一致で足りる
    hasVoice: (voices, id) => voices.some((voice) => voice.id === id),

    async synthesize(text) {
      // text はクエリ文字列に載る。長文だと 414 になりうるが、その1文が捨てられるだけ
      const queryUrl = `${baseUrl}/audio_query?text=${encodeURIComponent(text)}&speaker=${speaker}`;
      const queryRes = await request("audio_query", queryUrl, { method: "POST" });

      let query: AudioQuery;
      try {
        query = (await queryRes.json()) as AudioQuery;
      } catch (err) {
        throw describe("audio_query の読み取り", err);
      }

      const synthRes = await request("synthesis", `${baseUrl}/synthesis?speaker=${speaker}`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(applySpeedScale(query, speedScale)),
      });

      let wav: ArrayBuffer;
      try {
        wav = await synthRes.arrayBuffer();
      } catch (err) {
        throw describe("synthesis の読み取り", err);
      }

      // ★ 200 でも WAV とは限らない。`ttsBaseUrl` が別サービス（開発サーバー、
      //   キャプティブポータル）を指していると 200 + HTML が返り、そのまま再生に回って
      //   「再生に失敗しました」で ack される。**症状が原因からいちばん遠いところに出る**ので、
      //   ここで名指ししておく
      if (!looksLikeWav(wav)) {
        throw new TtsHttpError(
          "synthesis",
          synthRes.status,
          `WAV ではない応答が返りました（${wav.byteLength} バイト）`,
        );
      }
      return wav;
    },

    async listVoices() {
      const res = await request("speakers", `${baseUrl}/speakers`, { method: "GET" });
      let parsed: unknown;
      try {
        parsed = await res.json();
      } catch (err) {
        throw describe("speakers の読み取り", err);
      }
      if (!Array.isArray(parsed)) throw new Error("speakers が配列を返しませんでした");
      return flattenStyles(parsed as Speaker[]);
    },
  };
}
