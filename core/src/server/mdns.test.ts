import { describe, it, expect, vi } from "vitest";
import type * as os from "os";
import { addressOptionsForHost, interfacesForHost, startMdnsAdvertiser, type MdnsResponder } from "./mdns";

const iface = (address: string, family: "IPv4" | "IPv6", internal = false) =>
  ({ address, family, internal }) as os.NetworkInterfaceInfo;

const INTERFACES = {
  lo0: [iface("127.0.0.1", "IPv4", true), iface("::1", "IPv6", true)],
  en0: [iface("192.168.1.10", "IPv4"), iface("fe80::1", "IPv6")],
  utun0: [iface("fd00::2", "IPv6")],
};

function setup(advertise: () => Promise<void> = () => Promise.resolve(), shutdown = () => Promise.resolve()) {
  const createService = vi.fn((_options: unknown) => ({
    advertise,
    on: vi.fn(),
    getFQDN: () => "my-mac._chatter-agent._tcp.local.",
  }));
  const responder = { createService, shutdown: vi.fn(shutdown) } as unknown as MdnsResponder;
  const getResponder = vi.fn((_options: { interface: string[] }) => responder);
  const log = vi.fn();
  const warn = vi.fn();
  const start = (host = "0.0.0.0", interfaces: NodeJS.Dict<os.NetworkInterfaceInfo[]> = INTERFACES) =>
    startMdnsAdvertiser(
      { host, port: 8765, version: "1.2.3" },
      { getResponder, networkInterfaces: () => interfaces, hostname: () => "my-mac.local", log, warn },
    );
  return { createService, getResponder, responder, log, warn, start };
}

const flush = () => new Promise((r) => setImmediate(r));

describe("addressOptionsForHost", () => {
  it("bind 先に応じて広告するアドレスを絞る", () => {
    expect(addressOptionsForHost("0.0.0.0")).toEqual({ disabledIpv6: true });
    expect(addressOptionsForHost("::")).toEqual({});
    expect(addressOptionsForHost("192.168.1.10")).toEqual({ restrictedAddresses: ["192.168.1.10"] });
    expect(addressOptionsForHost("fd00::1")).toEqual({ restrictedAddresses: ["fd00::1"] });
  });
});

describe("interfacesForHost", () => {
  it("bind 先で待ち受けている、ループバック以外のインターフェースだけを選ぶ", () => {
    expect(interfacesForHost("0.0.0.0", INTERFACES)).toEqual(["en0"]);
    expect(interfacesForHost("::", INTERFACES)).toEqual(["en0", "utun0"]);
    expect(interfacesForHost("fd00::2", INTERFACES)).toEqual(["utun0"]);
    expect(interfacesForHost("10.0.0.1", INTERFACES)).toEqual([]);
  });
});

describe("startMdnsAdvertiser", () => {
  it("種別・ポート・バージョンだけを載せ、成功を記録する", async () => {
    const { createService, getResponder, log, start } = setup();
    start();
    await flush();
    expect(getResponder).toHaveBeenCalledWith({ interface: ["en0"] });
    expect(createService).toHaveBeenCalledWith({
      name: "my-mac",
      hostname: "my-mac-chatter-agent",
      type: "chatter-agent",
      port: 8765,
      txt: { version: "1.2.3" },
      disabledIpv6: true,
    });
    expect(log).toHaveBeenCalledWith("[mDNS] 広告を始めました: my-mac._chatter-agent._tcp.local. (port 8765, en0)");
  });

  describe("インターフェースが無いまま起動したとき", () => {
    it("警告を1回だけ出して待ち、現れたら広告する", () => {
      vi.useFakeTimers();
      try {
        const { getResponder, createService, warn, start } = setup();
        const interfaces: NodeJS.Dict<os.NetworkInterfaceInfo[]> = { lo0: INTERFACES.lo0 };
        start("0.0.0.0", interfaces);
        expect(getResponder).not.toHaveBeenCalled();
        expect(warn).toHaveBeenCalledTimes(1);

        vi.advanceTimersByTime(30_000);
        expect(getResponder).not.toHaveBeenCalled();

        interfaces.en0 = INTERFACES.en0;
        vi.advanceTimersByTime(10_000);
        expect(getResponder).toHaveBeenCalledWith({ interface: ["en0"] });
        expect(createService).toHaveBeenCalledTimes(1);

        vi.advanceTimersByTime(60_000);
        expect(getResponder).toHaveBeenCalledTimes(1);
        expect(warn).toHaveBeenCalledTimes(1);
      } finally {
        vi.useRealTimers();
      }
    });

    it("stop で待ちを止める。以後 responder は作られない", async () => {
      vi.useFakeTimers();
      try {
        const { getResponder, start } = setup();
        const interfaces: NodeJS.Dict<os.NetworkInterfaceInfo[]> = { lo0: INTERFACES.lo0 };
        const adv = start("0.0.0.0", interfaces);
        await expect(adv.stop()).resolves.toBeUndefined();
        interfaces.en0 = INTERFACES.en0;
        vi.advanceTimersByTime(60_000);
        expect(getResponder).not.toHaveBeenCalled();
      } finally {
        vi.useRealTimers();
      }
    });
  });

  it("advertise が reject しても投げず、警告を出す", async () => {
    const { warn, start } = setup(() => Promise.reject(new Error("boom")));
    start();
    await flush();
    expect(warn).toHaveBeenCalledWith(expect.stringContaining("[mDNS] 広告できませんでした: Error: boom"));
  });

  it("createService が投げても起動を止めない", () => {
    const { responder, warn, start } = setup();
    (responder.createService as ReturnType<typeof vi.fn>).mockImplementation(() => {
      throw new Error("no iface");
    });
    expect(() => start()).not.toThrow();
    expect(warn).toHaveBeenCalledWith(expect.stringContaining("広告できませんでした"));
  });

  it("stop は shutdown を呼び、失敗しても reject しない", async () => {
    const { responder, warn, start } = setup(undefined, () => Promise.reject(new Error("x")));
    const adv = start();
    await expect(adv.stop()).resolves.toBeUndefined();
    expect(responder.shutdown).toHaveBeenCalledTimes(1);
    expect(warn).toHaveBeenCalledWith(expect.stringContaining("[mDNS] 停止に失敗しました"));
  });
});
