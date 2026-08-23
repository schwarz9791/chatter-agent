/**
 * `fetch` をモックせず、127.0.0.1 に実サーバーを立てて検証する（`wsServer.test.ts` と同じ方針）。
 * モックすると「想像した fetch の API」しか検証できず、タイムアウトの挙動は特に嘘になりやすい。
 * port: 0 で ephemeral port を取るので並行実行しても衝突しない。
 */

import { describe, it, expect, afterEach } from "vitest";
import * as http from "http";
import type { AddressInfo } from "net";
import { createVoicevoxClient, flattenStyles, hasStyle } from "./voicevoxClient";
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
        res.end(Buffer.from([0x52, 0x49, 0x46, 0x46]));
      });
    });

    const wav = await client(baseUrl).synthesize("こんにちは。");
    expect(Buffer.from(wav).toString("latin1")).toBe("RIFF");

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
