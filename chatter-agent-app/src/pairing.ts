// ペアリング窓。server が発行した PIN を表示し、端末が入力し終えるまで状態を見る。
// 窓を閉じても PIN は取り消さない（期限と失敗回数の上限で切れる）。

import { asServerError, lang, serverJson } from "./tauri.ts";
import { errorMessage, formatRemaining, type PairingState, pairingView } from "./model.ts";
import { EN, JA, type Text } from "./text.ts";

const el = (id: string) => document.getElementById(id) as HTMLElement;
const pinEl = el("pin");
const statusEl = el("status");
const reissueEl = el("reissue") as HTMLButtonElement;

let t: Text = JA;
let expiresAt = 0;
let timer: number | undefined;
// 発行のたびに進める。古い発行への応答が、今の表示を上書きしないようにする。
let generation = 0;

function show(text: string, kind: "" | "sub" | "error"): void {
  statusEl.textContent = text;
  statusEl.className = `status ${kind}`;
}

function stop(): void {
  clearTimeout(timer);
  timer = undefined;
}

/** 発行の失敗。PIN を隠して、発行し直せるようにする */
function fail(e: unknown): void {
  stop();
  pinEl.hidden = true;
  reissueEl.hidden = false;
  show(errorMessage(asServerError(e), e, "", t), "error");
}

/** 前の問い合わせが終わってから次を張る。応答が遅くても重ならない */
async function tick(): Promise<void> {
  const mine = generation;
  let keepPolling = true;
  try {
    const r = await serverJson<{ state: PairingState }>("GET", "/v1/pairing");
    if (mine !== generation) return;
    const view = pairingView(r.state);
    pinEl.hidden = !view.showPin;
    reissueEl.hidden = !view.showReissue;
    if (view.message === "pending") {
      show(t.pairingRemaining(formatRemaining(expiresAt - Date.now())), "sub");
    } else {
      show(
        { paired: t.pairingPaired, expired: t.pairingExpired, locked: t.pairingLocked }[view.message],
        view.message === "paired" ? "" : "error",
      );
    }
    keepPolling = view.keepPolling;
  } catch (e) {
    // server ではまだ有効な PIN なので、隠さずに続ける。
    if (mine !== generation) return;
    show(errorMessage(asServerError(e), e, "", t), "error");
  }
  if (keepPolling) timer = window.setTimeout(() => void tick(), 1000);
}

async function issue(): Promise<void> {
  stop();
  generation++;
  reissueEl.hidden = true;
  try {
    const r = await serverJson<{ pin: string; expiresAt: number }>("POST", "/v1/pairing", {});
    pinEl.textContent = r.pin;
    expiresAt = r.expiresAt;
  } catch (e) {
    fail(e);
    return;
  }
  pinEl.hidden = false;
  await tick();
}

async function main(): Promise<void> {
  try {
    t = (await lang()) === "en" ? EN : JA;
  } catch {
    t = JA;
  }
  document.documentElement.lang = t === EN ? "en" : "ja";
  document.title = t.pairingTitle;
  el("guide").textContent = t.pairingGuide;
  reissueEl.textContent = t.pairingReissue;
  reissueEl.addEventListener("click", () => void issue());
  await issue();
}

void main();
