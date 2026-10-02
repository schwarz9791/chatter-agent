import type { ConfigKey } from "../../core/src/core/config.ts";
import {
  HOTKEY_KEYS,
  SERVER_RESET_KEYS,
  checkHotKey,
  envNote,
  errorMessage,
  formatHotKey,
  mascotGet,
  parseHotKey,
  voiceKey,
  type ConfigSnapshot,
  type HotKey,
  type MascotKey,
  type PanelKey,
} from "./model.ts";
import {
  asServerError,
  confirmDialog,
  lang,
  mascotReset,
  mascotSettingsGet,
  mascotSettingsSet,
  pickVrm,
  serverJson,
  serverRequest,
} from "./tauri.ts";
import { EN, JA, type Text } from "./text.ts";

const RETRY_MS = 3000;

let t: Text = JA;
let snap: ConfigSnapshot | null = null;
let serverErr: { status: number; body: string } | null = null;
let speakers: { id: string; label: string }[] | null = null;
let speakersUnavailable = false;
let mascot: unknown = {};
let mascotErr = "";
let busyPreview = false;

/** 行ごとの一時的な注記（失敗理由など）。成功すると消える */
const flash: Record<string, string> = {};
const warn = new Set<string>();

let cfgTimer: ReturnType<typeof setTimeout> | undefined;
let spkTimer: ReturnType<typeof setTimeout> | undefined;
let spkGen = 0;

// ── DOM 部品 ──

interface Row {
  note: HTMLElement;
  staticNote: () => string;
  envKey?: () => PanelKey | null;
  server: boolean;
  controls: HTMLElement[];
}
const rows: Record<string, Row> = {};
const updaters: (() => void)[] = [];

function el<K extends keyof HTMLElementTagNameMap>(tag: K, cls = "", text = ""): HTMLElementTagNameMap[K] {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text) e.textContent = text;
  return e;
}

function section(title: () => string): HTMLElement {
  const h = el("h2");
  const s = el("section");
  updaters.push(() => (h.textContent = title()));
  document.getElementById("app")!.append(h, s);
  return s;
}

function row(
  parent: HTMLElement,
  id: string,
  label: () => string,
  controls: HTMLElement[],
  opts: { server?: boolean; staticNote?: () => string; envKey?: () => PanelKey | null } = {},
): void {
  const r = el("div", "row");
  const line = el("div", "line");
  const lab = el("span");
  const ctl = el("div", "ctl");
  ctl.append(...controls);
  line.append(lab, ctl);
  const note = el("div", "note");
  r.append(line, note);
  parent.append(r);
  rows[id] = {
    note,
    staticNote: opts.staticNote ?? (() => ""),
    envKey: opts.envKey,
    server: opts.server ?? false,
    controls,
  };
  updaters.push(() => {
    lab.textContent = label();
    for (const c of controls) {
      if (c instanceof HTMLInputElement || c instanceof HTMLSelectElement) c.setAttribute("aria-label", label());
    }
  });
}

function select(onChange: (v: string) => void) {
  const s = el("select");
  s.addEventListener("change", () => onChange(s.value));
  return s;
}

function fillSelect(s: HTMLSelectElement, options: { value: string; label: string }[], value: string) {
  const sig = JSON.stringify(options);
  if (s.dataset.sig !== sig) {
    s.replaceChildren(...options.map((o) => Object.assign(el("option"), { value: o.value, textContent: o.label })));
    s.dataset.sig = sig;
  }
  s.value = value;
}

function checkbox(onChange: (v: boolean) => void) {
  const c = el("input");
  c.type = "checkbox";
  c.addEventListener("change", () => onChange(c.checked));
  return c;
}

function range(min: number, max: number, step: number, onInput: (v: number) => void, onCommit: (v: number) => void) {
  const r = el("input");
  r.type = "range";
  r.min = String(min);
  r.max = String(max);
  r.step = String(step);
  r.addEventListener("input", () => onInput(Number(r.value)));
  r.addEventListener("change", () => onCommit(Number(r.value)));
  return r;
}

// ── 通信 ──

function failText(e: unknown, key: string): string {
  return errorMessage(asServerError(e), e, key, t);
}

function cfgValue<K extends ConfigKey>(key: K) {
  return snap?.values[key];
}

async function loadConfig(): Promise<void> {
  clearTimeout(cfgTimer);
  try {
    snap = await serverJson<ConfigSnapshot>("GET", "/v1/config");
    serverErr = null;
  } catch (e) {
    serverErr = asServerError(e) ?? { status: -1, body: String(e) };
  }
  if (serverErr?.status === 0) {
    cfgTimer = setTimeout(() => void loadConfig().then(render), RETRY_MS);
  } else if (serverErr === null && speakers === null) {
    void loadSpeakers();
  }
}

async function loadSpeakers(): Promise<void> {
  clearTimeout(spkTimer);
  const gen = ++spkGen;
  try {
    const r = await serverJson<{ speakers: { id: string; label: string }[] }>("GET", "/v1/speakers");
    if (gen !== spkGen) return;
    speakers = r.speakers;
    speakersUnavailable = false;
  } catch (e) {
    if (gen !== spkGen) return;
    speakers = null;
    speakersUnavailable = true;
    const err = asServerError(e);
    if (err && err.status !== 503 && err.status !== 0) flash.voice = failText(e, "speakers");
    spkTimer = setTimeout(() => void loadSpeakers(), RETRY_MS);
  }
  render();
}

async function loadMascot(): Promise<void> {
  try {
    mascot = await mascotSettingsGet();
    mascotErr = "";
  } catch (e) {
    mascotErr = t.settingsUnreadable(String(e));
  }
}

async function reloadAll(): Promise<void> {
  await Promise.all([loadConfig(), loadMascot()]);
  render();
}

async function patch(key: PanelKey, value: unknown, rowId: string): Promise<boolean> {
  try {
    const r = await serverJson<Pick<ConfigSnapshot, "values" | "origins">>("PATCH", "/v1/config", { [key]: value });
    if (snap) {
      snap.values = r.values;
      snap.origins = r.origins;
    }
    delete flash[rowId];
    render();
    return true;
  } catch (e) {
    flash[rowId] = failText(e, key);
    warn.add(rowId);
    await loadConfig();
    render();
    return false;
  }
}

async function setMascot(key: MascotKey, value: unknown, rowId: string): Promise<boolean> {
  try {
    await mascotSettingsSet(key.split("."), value);
    delete flash[rowId];
    await loadMascot();
    render();
    return true;
  } catch (e) {
    flash[rowId] = String(e);
    warn.add(rowId);
    await loadMascot();
    render();
    return false;
  }
}

// ── 画面 ──

const ui: Record<string, HTMLInputElement | HTMLSelectElement | HTMLButtonElement | HTMLElement> = {};

function build(): void {
  // キャラクター
  let s = section(() => t.sectionCharacter);
  const vrmBtn = el("button");
  const vrmName = el("span");
  ui.vrmBtn = vrmBtn;
  ui.vrmName = vrmName;
  updaters.push(() => (vrmBtn.textContent = t.chooseVrm));
  vrmBtn.addEventListener("click", async () => {
    delete flash.vrm;
    try {
      const name = await pickVrm();
      if (name === null) return;
      await loadMascot();
      flash.vrm = t.appliesFromNextLaunch;
      warn.delete("vrm");
    } catch (e) {
      flash.vrm = String(e) === "not_vrm" ? t.notVrm : t.copyFailed(String(e));
      warn.add("vrm");
    }
    render();
  });
  row(s, "vrm", () => "VRM", [vrmName, vrmBtn]);

  // オーディオ
  s = section(() => t.sectionAudio);
  const engine = select((v) => void onEngine(v));
  ui.engine = engine;
  row(s, "engine", () => t.ttsEngine, [engine], { server: true, envKey: () => "ttsEngine" });

  const voice = select((v) => {
    const key = voiceKey(String(cfgValue("ttsEngine")));
    void patch(key, v, "voice");
  });
  ui.voice = voice;
  row(s, "voice", () => t.voiceStyle, [voice], {
    server: true,
    envKey: () => voiceKey(String(cfgValue("ttsEngine"))),
    staticNote: () => (speakersUnavailable ? t.speakerListUnavailable : ""),
  });

  const vol = range(0, 1, 0.1, (v) => (volVal.textContent = `${Math.round(v * 100)}%`), (v) =>
    void setMascot("audio.volume", Math.round(v * 10) / 10, "volume"),
  );
  const volVal = el("span", "val");
  ui.vol = vol;
  ui.volVal = volVal;
  row(s, "volume", () => t.volume, [vol, volVal]);

  const speed = range(0.5, 2, 0.1, (v) => (speedVal.textContent = v.toFixed(1)), (v) =>
    void patch("ttsSpeedScale", Math.round(v * 10) / 10, "speed"),
  );
  const speedVal = el("span", "val");
  ui.speed = speed;
  ui.speedVal = speedVal;
  row(s, "speed", () => t.speakingSpeed, [speed, speedVal], {
    server: true,
    envKey: () => "ttsSpeedScale",
    staticNote: () => t.appliesFromNextSentence,
  });

  const test = el("button");
  ui.test = test;
  updaters.push(() => (test.textContent = t.playTestVoice));
  test.addEventListener("click", () => void playTest());
  row(s, "test", () => "", [test], { server: true });

  // モーション
  s = section(() => t.sectionMotion);
  const flags: [string, MascotKey, () => string][] = [
    ["idle", "character.idleMotion", () => t.idleMotion],
    ["gaze", "character.cursorGaze", () => t.cursorGaze],
    ["blink", "character.blink", () => t.blink],
  ];
  for (const [id, key, label] of flags) {
    const c = checkbox((v) => void setMascot(key, v, id));
    ui[id] = c;
    row(s, id, label, [c]);
  }
  const fps = select((v) => void setMascot("display.frameRate", Number(v), "fps"));
  ui.fps = fps;
  row(s, "fps", () => t.frameRate, [fps], { staticNote: () => t.frameRateNote });

  // AI要約
  s = section(() => t.sectionAiSummary);
  const sum = checkbox((v) => void patch("aiSummaryEnabled", v, "sum"));
  ui.sum = sum;
  row(s, "sum", () => t.summarizeLongMessages, [sum], {
    server: true,
    envKey: () => "aiSummaryEnabled",
    staticNote: () => t.summarizeNote,
  });
  const sumEng = select((v) => void patch("aiSummaryBackend", v, "sumEng"));
  ui.sumEng = sumEng;
  row(s, "sumEng", () => t.summaryEngine, [sumEng], {
    server: true,
    envKey: () => "aiSummaryBackend",
    staticNote: () => t.summaryEngineNote,
  });

  // 感情判定
  s = section(() => t.sectionEmotion);
  const emo = select((v) => void patch("emotionClassifier", v, "emo"));
  ui.emo = emo;
  row(s, "emo", () => t.emotionEngine, [emo], {
    server: true,
    envKey: () => "emotionClassifier",
    staticNote: () => t.emotionEngineNote,
  });

  // ショートカット
  s = section(() => t.sectionShortcuts);
  hotKeyRow(s, "mute", "audio.muteHotKey", "ui.hideHotKey", () => t.hotKeyMute, () => t.hotKeyHide);
  hotKeyRow(s, "hide", "ui.hideHotKey", "audio.muteHotKey", () => t.hotKeyHide, () => t.hotKeyMute);

  // リセット
  s = section(() => t.sectionReset);
  const reset = el("button");
  ui.reset = reset;
  updaters.push(() => (reset.textContent = t.resetAll));
  reset.addEventListener("click", () => void resetAll());
  row(s, "reset", () => "", [reset], { staticNote: () => t.resetAllNote });
}

function hotKeyRow(
  parent: HTMLElement,
  id: string,
  key: MascotKey,
  otherKey: MascotKey,
  label: () => string,
  otherLabel: () => string,
): void {
  const mods = el("span", "mods");
  const boxes: Record<"ctrl" | "opt" | "shift" | "cmd", HTMLInputElement> = {
    ctrl: checkbox(commit),
    opt: checkbox(commit),
    shift: checkbox(commit),
    cmd: checkbox(commit),
  };
  const symbols = { ctrl: "⌃", opt: "⌥", shift: "⇧", cmd: "⌘" } as const;
  for (const m of ["ctrl", "opt", "shift", "cmd"] as const) {
    const l = el("label");
    l.append(boxes[m], symbols[m]);
    mods.append(l);
  }
  const keySel = select(commit);
  fillSelect(
    keySel,
    HOTKEY_KEYS.map((k) => ({ value: k, label: k.toUpperCase() })),
    "a",
  );
  function read(): HotKey {
    return {
      ctrl: boxes.ctrl.checked,
      opt: boxes.opt.checked,
      shift: boxes.shift.checked,
      cmd: boxes.cmd.checked,
      key: keySel.value,
    };
  }
  function commit(): void {
    const h = read();
    const verdict = checkHotKey(h, mascotGet(mascot, otherKey));
    if (verdict === "needsModifier") {
      flash[id] = t.hotKeyNeedsModifier;
      warn.add(id);
      render();
    } else if (verdict === "clash") {
      flash[id] = t.hotKeyClashRejected(otherLabel());
      warn.add(id);
      render();
    } else {
      void setMascot(key, formatHotKey(h), id);
    }
  }
  row(parent, id, label, [mods, keySel]);
  // 書かなかった入力は、保存済みの値へ戻さず選んだまま注記を見せる
  updaters.push(() => {
    if (flash[id]) return;
    const h = parseHotKey(mascotGet(mascot, key));
    for (const m of ["ctrl", "opt", "shift", "cmd"] as const) boxes[m].checked = h?.[m] ?? false;
    keySel.value = h?.key ?? "a";
  });
}

function render(): void {
  for (const u of updaters) u();
  const down = serverErr !== null;
  const origins = snap?.origins;

  // 値
  const v = snap?.values;
  fillSelect(
    ui.engine as HTMLSelectElement,
    [
      { value: "voicevox", label: "AivisSpeech" },
      { value: "openai", label: "Kokoro" },
    ],
    String(v?.ttsEngine ?? "voicevox"),
  );
  const cur = v ? String(v[voiceKey(String(v.ttsEngine))]) : "";
  const list = (speakers ?? []).map((s) => ({ value: s.id, label: s.label }));
  if (!list.some((o) => o.value === cur)) list.unshift({ value: cur, label: cur || t.panelEmpty });
  fillSelect(ui.voice as HTMLSelectElement, list, cur);

  setRange(ui.speed as HTMLInputElement, ui.speedVal, v?.ttsSpeedScale ?? 1, (x) => x.toFixed(1));
  const volume = mascotGet(mascot, "audio.volume");
  setRange(ui.vol as HTMLInputElement, ui.volVal, volume, (x) => `${Math.round(x * 100)}%`);

  (ui.idle as HTMLInputElement).checked = mascotGet(mascot, "character.idleMotion");
  (ui.gaze as HTMLInputElement).checked = mascotGet(mascot, "character.cursorGaze");
  (ui.blink as HTMLInputElement).checked = mascotGet(mascot, "character.blink");
  fillSelect(
    ui.fps as HTMLSelectElement,
    [30, 60].map((n) => ({ value: String(n), label: `${n} fps` })),
    String(mascotGet(mascot, "display.frameRate")),
  );
  (ui.sum as HTMLInputElement).checked = v?.aiSummaryEnabled ?? true;
  fillSelect(
    ui.sumEng as HTMLSelectElement,
    [
      { value: "fm", label: "fm" },
      { value: "claude", label: t.summaryClaude },
    ],
    String(v?.aiSummaryBackend ?? "fm"),
  );
  fillSelect(
    ui.emo as HTMLSelectElement,
    [
      { value: "ollaya", label: "Ollaya(laya)" },
      { value: "fm", label: "fm" },
      { value: "dictionary", label: t.emotionDictionary },
    ],
    String(v?.emotionClassifier ?? "ollaya"),
  );

  const vrm = mascotGet(mascot, "character.vrm");
  ui.vrmName.textContent = vrm || t.bundledModelNote;

  // 無効化と注記
  for (const [id, r] of Object.entries(rows)) {
    const env = r.envKey?.() ?? null;
    const envText = env ? envNote(env, origins, t) : null;
    let disabled = false;
    if (r.server && down) disabled = true;
    if (envText) disabled = true;
    if (id === "voice" && speakersUnavailable) disabled = true;
    if (id === "test" && busyPreview) disabled = true;
    for (const c of r.controls) setDisabled(c, disabled);

    let text = flash[id] ?? "";
    if (!text && r.server && down) text = serverErrText();
    if (!text && envText) text = envText;
    if (!text && id === "vrm") text = mascotErr;
    if (!text) text = r.staticNote();
    r.note.textContent = text;
    r.note.classList.toggle("warn", warn.has(id) && !!flash[id]);
  }
}

function serverErrText(): string {
  return errorMessage(serverErr, null, "config", t);
}

function setRange(input: HTMLInputElement, label: HTMLElement, value: number, fmt: (x: number) => string) {
  // 操作中の値を読み直しで巻き戻さない
  if (document.activeElement !== input) input.value = String(value);
  label.textContent = fmt(Number(input.value));
}

function setDisabled(c: HTMLElement, disabled: boolean) {
  if (c instanceof HTMLInputElement || c instanceof HTMLSelectElement || c instanceof HTMLButtonElement) {
    c.disabled = disabled;
  } else {
    c.querySelectorAll("input,select").forEach((x) => ((x as HTMLInputElement).disabled = disabled));
  }
}

// ── 操作 ──

async function onEngine(value: string): Promise<void> {
  if (!(await patch("ttsEngine", value, "engine"))) return;
  speakers = null;
  speakersUnavailable = true;
  delete flash.voice;
  render();
  void loadSpeakers();
}

async function playTest(): Promise<void> {
  delete flash.test;
  warn.delete("test");
  // ミュートはショートカットでマスコットが書き換えるので、窓にフォーカスがあったままでも古くなる
  await loadMascot();
  if (mascotGet(mascot, "audio.mute")) {
    flash.test = t.mutedNoSound;
    render();
    return;
  }
  busyPreview = true;
  render();
  try {
    const buf = await serverRequest("POST", "/v1/tts/preview", {});
    const audio = new Audio(URL.createObjectURL(new Blob([buf], { type: "audio/wav" })));
    audio.volume = Math.min(1, Math.max(0, mascotGet(mascot, "audio.volume")));
    const done = new Promise<void>((resolve) => {
      audio.addEventListener("ended", () => resolve());
      audio.addEventListener("error", () => resolve());
    });
    await audio.play();
    await done;
    URL.revokeObjectURL(audio.src);
  } catch (e) {
    flash.test = failText(e, "preview");
    warn.add("test");
  }
  busyPreview = false;
  render();
}

// マスコット側はファイルなので、server に繋がらなくても戻せる
async function resetAll(): Promise<void> {
  if (!(await confirmDialog(t.confirmResetTitle, t.confirmResetMessage, t.confirmResetOk, t.confirmResetCancel))) {
    return;
  }
  warn.add("reset");
  let removed: number;
  try {
    removed = await mascotReset();
  } catch (e) {
    flash.reset = t.resetMascotFailed(String(e));
    return reloadAll();
  }
  const coreError = await resetServer();
  flash.reset = coreError === null ? t.resetDone(removed) : `${t.resetDone(removed)} / ${coreError}`;
  if (coreError === null) warn.delete("reset");
  await reloadAll();
}

/** 戻せなかったときの理由。全部戻せたら null */
async function resetServer(): Promise<string | null> {
  if (!snap) return t.resetCoreDefaultsUnavailable;
  for (const key of SERVER_RESET_KEYS) {
    if (snap.origins[key] === "env") continue;
    try {
      await serverJson("PATCH", "/v1/config", { [key]: snap.defaults[key] });
    } catch (e) {
      if (asServerError(e)?.status === 409) continue;
      return t.resetKeyFailed(key, failText(e, key));
    }
  }
  return null;
}

async function main(): Promise<void> {
  try {
    t = (await lang()) === "en" ? EN : JA;
  } catch {
    t = JA;
  }
  document.documentElement.lang = t === EN ? "en" : "ja";
  document.title = t.title;
  build();
  render();
  await reloadAll();
  window.addEventListener("focus", () => void reloadAll());
}

void main();
