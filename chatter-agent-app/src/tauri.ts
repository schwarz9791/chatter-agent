// Rust 側のコマンドの型付きラッパ。`withGlobalTauri` が注入する invoke を使い、`@tauri-apps/api` は入れない。
// サーバーは WebView から fetch しない（Origin が付いて拒否される）ので、必ず Rust を経由する。

declare global {
  interface Window {
    __TAURI__: { core: { invoke(cmd: string, args?: Record<string, unknown>): Promise<unknown> } };
  }
}

/** `server_request` の失敗。`status` が 0 のときは送れなかった（`body` が理由の識別子） */
export interface ServerError {
  status: number;
  body: string;
}

export function asServerError(e: unknown): ServerError | null {
  if (typeof e === "object" && e !== null && "status" in e && "body" in e) {
    const { status, body } = e as { status: unknown; body: unknown };
    if (typeof status === "number" && typeof body === "string") return { status, body };
  }
  return null;
}

function invoke<T>(cmd: string, args?: Record<string, unknown>): Promise<T> {
  return window.__TAURI__.core.invoke(cmd, args) as Promise<T>;
}

function toBuffer(v: unknown): ArrayBuffer {
  if (v instanceof ArrayBuffer) return v;
  throw { status: 0, body: "bad_response" } satisfies ServerError;
}

export type Method = "GET" | "PATCH" | "POST";

export async function serverRequest(method: Method, path: string, body?: unknown): Promise<ArrayBuffer> {
  return toBuffer(await invoke("server_request", { method, path, body }));
}

export async function serverJson<T>(method: Method, path: string, body?: unknown): Promise<T> {
  const buf = await serverRequest(method, path, body);
  try {
    return JSON.parse(new TextDecoder().decode(buf)) as T;
  } catch {
    throw { status: 0, body: "bad_response" } satisfies ServerError;
  }
}

export const mascotSettingsGet = () => invoke<unknown>("mascot_settings_get");
export const mascotSettingsSet = (path: string[], value: unknown) =>
  invoke<void>("mascot_settings_set", { path, value });
export const pickVrm = () => invoke<string | null>("pick_vrm");
export const confirmDialog = (title: string, message: string, ok: string, cancel: string) =>
  invoke<boolean>("confirm", { title, message, ok, cancel });
export const mascotReset = () => invoke<{ removed: number; error: string | null }>("mascot_reset");
export const lang = () => invoke<"ja" | "en">("lang");
export const mascotState = () => invoke<{ running: boolean; motions: string[] | null }>("mascot_state");
export const mascotRequest = (kind: "resetWindow" | "playMotion", id?: string) =>
  invoke<void>("mascot_request", { kind, id });
