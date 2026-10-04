import * as os from "os";
import ciao from "@homebridge/ciao";
import { isLoopbackAddress } from "./loopback";

/** LAN から繋がらない bind か。広告は LAN に出るときだけ要る */
export function isLoopbackBind(host: string): boolean {
  return isLoopbackAddress(host) || host === "localhost";
}

/** ciao のサービスのうち、ここが触る面だけ */
interface MdnsService {
  advertise(): Promise<void>;
  on(event: "name-change", listener: (name: string) => void): unknown;
}

/** ciao の Responder のうち、ここが触る面だけ */
export interface MdnsResponder {
  createService(options: {
    name: string;
    hostname: string;
    type: string;
    port: number;
    txt: Record<string, string>;
    disabledIpv6?: boolean;
    restrictedAddresses?: string[];
  }): MdnsService;
  shutdown(): Promise<void>;
}

type NetworkInterfaces = NodeJS.Dict<os.NetworkInterfaceInfo[]>;

export interface MdnsAdvertiserDeps {
  /** テスト用。既定は ciao の共有 Responder */
  getResponder?: (options: { interface: string[] }) => MdnsResponder;
  /** テスト用。既定 `os.networkInterfaces()` */
  networkInterfaces?: () => NetworkInterfaces;
  /** テスト用。既定 `os.hostname()` */
  hostname?: () => string;
  log?: (message: string) => void;
  warn?: (message: string) => void;
}

/**
 * bind したホストから、広告するアドレスの絞り方を決める。
 *
 * ★ `0.0.0.0` は IPv4 でしか待ち受けないので AAAA を出さない（出すと、繋がらない
 *   IPv6 アドレスをクライアントが先に試しうる）。
 */
export function addressOptionsForHost(host: string): { disabledIpv6?: boolean; restrictedAddresses?: string[] } {
  if (host === "0.0.0.0") return { disabledIpv6: true };
  if (host === "::") return {};
  return { restrictedAddresses: [host] };
}

/**
 * bind したホストで待ち受けているインターフェース（ループバックを除く）。
 *
 * ★ **広告先の選定を ciao に任せないこと。** macOS の ciao は Wi-Fi が繋がっているかを
 *   `networksetup -getairportnetwork` の出力で判定するが、接続中でも「未接続」と答える macOS が
 *   あり、その Wi-Fi を外す —— いちばん使う経路に広告が出ない。名前を渡せばこの判定を通らない。
 *
 * ponytail: 起動時に1回だけ決める。既存のインターフェースのアドレスの変化は ciao が追うが、
 * 後から増えたもの（Wi-Fi から有線への乗り換えなど）は server の再起動まで拾わない
 */
export function interfacesForHost(host: string, interfaces: NetworkInterfaces): string[] {
  const listens = (addr: os.NetworkInterfaceInfo) => {
    if (addr.internal) return false;
    if (host === "::") return true;
    if (host === "0.0.0.0") return addr.family === "IPv4";
    return addr.address === host;
  };
  return Object.entries(interfaces)
    .filter(([, addrs]) => (addrs ?? []).some(listens))
    .map(([name]) => name);
}

/**
 * `_chatter-agent._tcp` を LAN に広告する。**起動を待たせない**（probing を挟む）。
 *
 * ★ **トークンも、そこから導いた値も載せないこと。** 広告は LAN の誰にでも見える。
 *   見つかるのは接続先の候補だけで、繋ぐにはトークンが要る。
 */
export function startMdnsAdvertiser(
  opts: { host: string; port: number; version: string },
  deps: MdnsAdvertiserDeps = {},
): { stop: () => Promise<void> } {
  const log = deps.log ?? ((m) => console.log(m));
  const warn = deps.warn ?? ((m) => console.warn(m));
  const failed = (err: unknown) =>
    warn(
      `[mDNS] 広告できませんでした: ${String(err)}。Android からは自動で見つけられません（接続先を明示すれば繋がります）`,
    );

  let responder: MdnsResponder | undefined;
  try {
    const interfaces = interfacesForHost(opts.host, (deps.networkInterfaces ?? os.networkInterfaces)());
    if (interfaces.length === 0) {
      failed(`host=${opts.host} で待ち受けている LAN のインターフェースがありません`);
      return { stop: () => Promise.resolve() };
    }
    responder = (deps.getResponder ?? ((o) => ciao.getResponder(o) as unknown as MdnsResponder))({
      interface: interfaces,
    });
    const name = (deps.hostname ?? os.hostname)().replace(/\.local$/, "");
    const service = responder.createService({
      name,
      // ★ **ホスト名をサービス名から導かせないこと**（ciao の既定）。サービス名は Mac の
      //   ホスト名そのものなので、OS の mDNS 応答器が持つ `<host>.local` と probing で衝突し、
      //   負けた側が改名される —— Mac 自身のホスト名が書き換わりうる
      hostname: `${name}-chatter-agent`,
      type: "chatter-agent",
      port: opts.port,
      txt: { version: opts.version },
      ...addressOptionsForHost(opts.host),
    });
    service.on("name-change", (n) => log(`[mDNS] 名前が衝突したので変わりました: ${n}`));
    service
      .advertise()
      .then(
        () => log(`[mDNS] 広告しました: ${name} (_chatter-agent._tcp, port ${opts.port}, ${interfaces.join(" / ")})`),
        failed,
      );
  } catch (err) {
    failed(err);
  }

  return {
    stop: async () => {
      try {
        await responder?.shutdown();
      } catch (err) {
        warn(`[mDNS] 停止に失敗しました: ${String(err)}`);
      }
    },
  };
}
