import assert from "node:assert/strict";
import { test } from "node:test";
import {
  checkHotKey,
  envNote,
  errorMessage,
  formatHotKey,
  formatRemaining,
  mascotGet,
  motionPreviewState,
  pairingView,
  parseHotKey,
  sameCombination,
} from "./model.ts";
import { EN, JA } from "./text.ts";

test("env 注記は origins が env のときだけ", () => {
  assert.equal(
    envNote("ttsSpeedScale", { ttsSpeedScale: "env" }, EN),
    EN.envOverridden("CHATTER_AGENT_TTS_SPEED_SCALE"),
  );
  assert.equal(envNote("ttsSpeedScale", { ttsSpeedScale: "file" }, EN), null);
  assert.equal(envNote("ttsSpeedScale", undefined, EN), null);
});

test("ショートカットは別名・大小を吸収して正規順に組み立てる", () => {
  const h = parseHotKey("Command+Alt+Control+SHIFT+M");
  assert.ok(h);
  assert.equal(formatHotKey(h), "ctrl+opt+shift+cmd+m");
  assert.equal(formatHotKey(parseHotKey("meta+f12")!), "cmd+f12");
});

test("不正なショートカットは null", () => {
  for (const bad of ["", "m", "opt", "opt+m+n", "opt+", "opt+ö", "opt+f13", "+m", "ctrl+toString+m", "constructor+m", 3, null]) {
    assert.equal(parseHotKey(bad), null, String(bad));
  }
});

test("重複判定は表記ゆれを同じとみなし、不正値は重複にしない", () => {
  assert.equal(sameCombination("opt+ctrl+m", "control+option+M"), true);
  assert.equal(sameCombination("ctrl+opt+m", "ctrl+opt+h"), false);
  assert.equal(sameCombination("m", "m"), false);
});

test("書き込み前の検査", () => {
  const none = { ctrl: false, opt: false, shift: false, cmd: false, key: "m" };
  assert.equal(checkHotKey(none, "ctrl+opt+h"), "needsModifier");
  assert.equal(checkHotKey({ ...none, ctrl: true, opt: true }, "opt+ctrl+m"), "clash");
  assert.equal(checkHotKey({ ...none, ctrl: true, opt: true }, "ctrl+opt+h"), "ok");
});

test("マスコット設定は無い・型違いのとき既定値", () => {
  assert.equal(mascotGet({}, "audio.volume"), 1);
  assert.equal(mascotGet({ audio: { volume: 0.4 } }, "audio.volume"), 0.4);
  assert.equal(mascotGet({ audio: { volume: "x" } }, "audio.volume"), 1);
  assert.equal(mascotGet({ character: { blink: false } }, "character.blink"), false);
  assert.equal(mascotGet(null, "display.frameRate"), 30);
});

test("モーション確認の状態は未起動・待機OFF・読み込み中・空・可の順", () => {
  const s = (running: boolean, idleMotion: boolean, motions: string[] | null) =>
    motionPreviewState({ running, idleMotion, motions });
  assert.equal(s(false, false, null), "notRunning");
  assert.equal(s(true, false, null), "idleOff");
  assert.equal(s(true, true, null), "loading");
  assert.equal(s(true, true, []), "empty");
  assert.equal(s(true, true, ["idle/a.vrma"]), "ready");
});

test("大きさの既定値は 1", () => {
  assert.equal(mascotGet({}, "character.scale"), 1);
  assert.equal(mascotGet({ character: { scale: 1.5 } }, "character.scale"), 1.5);
});

test("エラー文言", () => {
  const e = (status: number, body: string) => errorMessage({ status, body }, null, "k", JA);
  assert.equal(e(0, "unreachable"), JA.coreUnreachable);
  assert.equal(e(0, "unsupported_host"), JA.unsupportedHost);
  assert.equal(e(409, '{"error":"env_override","key":"ttsSpeedScale"}'), JA.errorEnvOverride("ttsSpeedScale"));
  assert.equal(e(400, '{"error":"invalid_value"}'), JA.errorInvalidValue("k"));
  assert.equal(e(503, '{"error":"engine_unreachable"}'), JA.errorEngineUnreachable);
  assert.equal(e(429, ""), JA.errorTooManyRequests);
  assert.equal(e(500, "oops"), JA.errorHttp(500));
  assert.equal(e(418, '{"error":"teapot"}'), JA.errorUnknown("teapot", "k"));
  assert.equal(errorMessage(null, "boom", "k", JA), "boom");
});

test("残り時間は m:ss で、負は 0:00", () => {
  assert.equal(formatRemaining(272_000), "4:32");
  assert.equal(formatRemaining(59_100), "1:00");
  assert.equal(formatRemaining(5_000), "0:05");
  assert.equal(formatRemaining(-1), "0:00");
});

test("ペアリングの表示は状態で決まり、pending だけが PIN を出してポーリングを続ける", () => {
  assert.deepEqual(pairingView("pending"), {
    message: "pending",
    showPin: true,
    showReissue: false,
    keepPolling: true,
  });
  assert.equal(pairingView("paired").keepPolling, false);
  assert.equal(pairingView("paired").showReissue, false);
  for (const s of ["expired", "locked", "none"] as const) {
    const v = pairingView(s);
    assert.ok(v.showReissue && !v.showPin && !v.keepPolling, s);
  }
  assert.equal(pairingView("locked").message, "locked");
  assert.equal(pairingView("none").message, "expired");
});

test("not_lan は専用の文言", () => {
  const err = { status: 409, body: JSON.stringify({ error: "not_lan" }) };
  assert.equal(errorMessage(err, err, "", JA), JA.pairingNotLan);
  assert.equal(errorMessage(err, err, "", EN), EN.pairingNotLan);
});
