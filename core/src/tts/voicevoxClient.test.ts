/**
 * `fetch` をモックせず、127.0.0.1 に実サーバーを立てて検証する（`wsServer.test.ts` と同じ方針）。
 * モックすると「想像した fetch の API」しか検証できず、タイムアウトの挙動は特に嘘になりやすい。
 * port: 0 で ephemeral port を取るので並行実行しても衝突しない。
 */

import { describe, it, expect, afterEach } from "vitest";
import * as http from "http";
import type { AddressInfo } from "net";
import { createVoicevoxClient, flattenStyles, hasStyle, TtsHttpError, TtsTransportError } from "./voicevoxClient";
import type { Speaker } from "./voicevoxClient";

type Handler = (req: http.IncomingMessage, res: http.ServerResponse) => void;

const servers: http.Server[] = [];

afterEach(async () => {
  for (const server of servers.splice(0)) {
    await new Promise<void>((done) => server.close(() => done()));
  }
});

async function serve(handler: Handler): Promise<string> {
  const server = http.createServer(handler);
  servers.push(server);
  await new Promise<void>((done) => server.listen(0, "127.0.0.1", () => done()));
  const { port } = server.address() as AddressInfo;
  return `http://127.0.0.1:${port}`;
}

function client(baseUrl: string, timeoutMs = 2000) {
  return createVoicevoxClient({ baseUrl, speakerId: 888753760, timeoutMs });
}

/** 一度 listen して即閉じ、確実に誰もいないポートを得る */
async function closedPort(): Promise<number> {
  const server = http.createServer();
  await new Promise<void>((done) => server.listen(0, "127.0.0.1", () => done()));
  const { port } = server.address() as AddressInfo;
  await new Promise<void>((done) => server.close(() => done()));
  return port;
}

/** RIFF/WAVE の最小ヘッダ。`synthesize` が中身を検証するので4バイトでは足りない */
const WAV_HEAD = Buffer.concat([Buffer.from("RIFF"), Buffer.alloc(4), Buffer.from("WAVE")]);

const SPEAKERS: Speaker[] = [
  { name: "Anneli", speaker_uuid: "u1", styles: [{ id: 888753760, name: "ノーマル" }] },
  { name: "つくよみちゃん", speaker_uuid: "u2", styles: [{ id: 1, name: "れいせい" }] },
];

describe("synthesize", () => {
  it("audio_query の結果をそのまま synthesis に渡し、WAV を返す", async () => {
    const seen: { url: string; body: string }[] = [];
    const baseUrl = await serve((req, res) => {
      let body = "";
      req.on("data", (chunk) => (body += chunk));
      req.on("end", () => {
        seen.push({ url: req.url ?? "", body });
        if (req.url?.startsWith("/audio_query")) {
          res.writeHead(200, { "Content-Type": "application/json" });
          res.end(JSON.stringify({ accent_phrases: [], speedScale: 1, kana: "コンニチハ" }));
          return;
        }
        res.writeHead(200, { "Content-Type": "audio/wav" });
        res.end(WAV_HEAD);
      });
    });

    const wav = await client(baseUrl).synthesize("こんにちは。");
    expect(Buffer.from(wav).subarray(0, 4).toString("latin1")).toBe("RIFF");

    // text はクエリ文字列、speaker も両方に載る
    expect(seen[0].url).toBe(`/audio_query?text=${encodeURIComponent("こんにちは。")}&speaker=888753760`);
    expect(seen[0].body).toBe("");
    expect(seen[1].url).toBe("/synthesis?speaker=888753760");
    // ★ クエリは解釈せずそのまま返送する（エンジンのバージョン差に強い）
    expect(JSON.parse(seen[1].body)).toEqual({ accent_phrases: [], speedScale: 1, kana: "コンニチハ" });
  });

  it("audio_query のエラーは段階が分かる形で投げる", async () => {
    const baseUrl = await serve((_req, res) => {
      res.writeHead(422);
      res.end();
    });
    await expect(client(baseUrl).synthesize("だめ。")).rejects.toThrow("audio_query が 422 を返しました");
  });

  it("synthesis のエラーも段階が分かる", async () => {
    const baseUrl = await serve((req, res) => {
      if (req.url?.startsWith("/audio_query")) {
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end("{}");
        return;
      }
      res.writeHead(500);
      res.end();
    });
    await expect(client(baseUrl).synthesize("だめ。")).rejects.toThrow("synthesis が 500 を返しました");
  });

  it("★ 応答が返らないリクエストはタイムアウトする（無いと head が固まって以後すべて無音になる）", async () => {
    const baseUrl = await serve(() => {
      // 何も返さない
    });
    await expect(client(baseUrl, 150).synthesize("だんまり。")).rejects.toThrow("タイムアウト");
  });

  it("繋がらない相手も例外になる", async () => {
    // 誰も listen していないポート
    const dead = createVoicevoxClient({ baseUrl: "http://127.0.0.1:1", speakerId: 0, timeoutMs: 1000 });
    await expect(dead.synthesize("だれもいない。")).rejects.toThrow("audio_query に失敗しました");
  });

  it("JSON でない audio_query の応答も例外になる", async () => {
    const baseUrl = await serve((_req, res) => {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end("not json");
    });
    await expect(client(baseUrl).synthesize("こわれた。")).rejects.toThrow("audio_query の読み取り");
  });
});

describe("失敗の診断（#49 のレビュー）", () => {
  it("★ 接続できないとき、どこへ繋ごうとして何が起きたかを出す（cause を辿る）", async () => {
    // undici は `TypeError: fetch failed` としか名乗らず、ECONNREFUSED / アドレス / ポートは
    // `err.cause` にしか入っていない。ここを潰すとログから原因に辿り着けない
    const closed = await closedPort();
    const result = await client(`http://127.0.0.1:${closed}`)
      .synthesize("あ。")
      .catch((err: unknown) => err);

    expect(result).toBeInstanceOf(TtsTransportError);
    const message = (result as Error).message;
    expect(message).toContain("ECONNREFUSED");
    expect(message).toContain(`127.0.0.1:${closed}`);
  });

  it("★ AggregateError（複数アドレスに解決されるホスト）でもアドレスを出す", async () => {
    // `localhost` は ::1 と 127.0.0.1 の両方に解決されるので、undici は
    // **メッセージが空の** AggregateError を cause に置く。errors[] を開かないと何も出ない
    const closed = await closedPort();
    const result = await client(`http://localhost:${closed}`)
      .synthesize("あ。")
      .catch((err: unknown) => err);

    expect((result as Error).message).toContain("ECONNREFUSED");
    expect((result as Error).message).toContain(String(closed));
  });

  it("★ エンジンが 4xx を返したら、status と応答本文を持つ（ttsSpeakerId の診断はここにある）", async () => {
    const baseUrl = await serve((_req, res) => {
      res.writeHead(422, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ detail: "speaker not found: 888753761" }));
    });

    const result = await client(baseUrl)
      .synthesize("あ。")
      .catch((err: unknown) => err);

    expect(result).toBeInstanceOf(TtsHttpError);
    expect((result as TtsHttpError).status).toBe(422);
    expect((result as TtsHttpError).detail).toContain("speaker not found");
    expect((result as Error).message).toContain("422");
  });

  it("本文が長くても切り詰める", async () => {
    const baseUrl = await serve((_req, res) => {
      res.writeHead(500);
      res.end("x".repeat(5000));
    });

    const result = (await client(baseUrl)
      .synthesize("あ。")
      .catch((err: unknown) => err)) as TtsHttpError;

    expect(result.detail.length).toBeLessThanOrEqual(512);
  });

  it("★ 200 でも WAV でなければ弾く（別サービスを指していると HTML が再生に回る）", async () => {
    const baseUrl = await serve((req, res) => {
      req.resume();
      if (req.url?.startsWith("/audio_query")) {
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end("{}");
        return;
      }
      res.writeHead(200, { "Content-Type": "text/html" });
      res.end("<!doctype html><title>dev server</title>");
    });

    const result = await client(baseUrl)
      .synthesize("あ。")
      .catch((err: unknown) => err);

    expect(result).toBeInstanceOf(TtsHttpError);
    expect((result as Error).message).toContain("WAV ではない");
  });

  it("★ タイムアウトは往復ごとに効く（2往復で1つの予算にしない）", async () => {
    // 予算を共有すると、モデルロードで /audio_query が食い切ったとき
    // CPU 律速の /synthesis に残り0が渡って落ちる
    const baseUrl = await serve((req, res) => {
      req.resume();
      if (req.url?.startsWith("/audio_query")) {
        setTimeout(() => {
          res.writeHead(200, { "Content-Type": "application/json" });
          res.end("{}");
        }, 250);
        return;
      }
      setTimeout(() => {
        res.writeHead(200, { "Content-Type": "audio/wav" });
        res.end(WAV_HEAD);
      }, 250);
    });

    // 1往復あたり 400ms。共有予算なら合計 500ms で溢れるが、往復ごとなら通る
    const wav = await client(baseUrl, 400).synthesize("あ。");
    expect(wav.byteLength).toBe(WAV_HEAD.byteLength);
  });
});

describe("listSpeakers", () => {
  it("話者の一覧を返す", async () => {
    const baseUrl = await serve((_req, res) => {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify(SPEAKERS));
    });
    expect(await client(baseUrl).listSpeakers()).toEqual(SPEAKERS);
  });

  it("配列でなければ例外", async () => {
    const baseUrl = await serve((_req, res) => {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end('{"detail":"not found"}');
    });
    await expect(client(baseUrl).listSpeakers()).rejects.toThrow("配列を返しませんでした");
  });
});

describe("flattenStyles / hasStyle", () => {
  it("話者 × スタイルの直積を作る", () => {
    expect(flattenStyles(SPEAKERS)).toEqual([
      { id: 888753760, label: "Anneli（ノーマル）" },
      { id: 1, label: "つくよみちゃん（れいせい）" },
    ]);
  });

  it("styles が壊れていても落ちない", () => {
    const broken = [{ name: "x", speaker_uuid: "u", styles: undefined }] as unknown as Speaker[];
    expect(flattenStyles(broken)).toEqual([]);
  });

  it("設定した話者 ID が実在するか判定できる", () => {
    expect(hasStyle(SPEAKERS, 888753760)).toBe(true);
    expect(hasStyle(SPEAKERS, 999)).toBe(false);
  });
});
