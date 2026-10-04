package tech.sukima.chattermascot;

import android.content.Context;
import android.net.nsd.NsdManager;
import android.net.nsd.NsdServiceInfo;
import android.os.Build;
import android.util.Log;

import java.net.Inet4Address;
import java.net.InetAddress;
import java.util.List;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicReference;

/**
 * LAN 上の chatter-agent-server を DNS-SD（_chatter-agent._tcp）で探す。
 *
 * NSD のコールバックはすべてこのクラスの中で完結させ、C# 側へは take() の結果だけを返す。
 * 最初に解決できた1台を採る。
 *
 * ★ onServiceFound は1つの探索につき広告ごとに1回しか来ない。解決の失敗や取りこぼしからは、
 *   探索を張り直す（stop → start）ことでしか立ち直れないので、張り直しは呼び出し側が行う。
 */
public final class ServerDiscovery {
    private static final String TAG = "ChatterMascot";
    private static final String SERVICE_TYPE = "_chatter-agent._tcp";

    private static NsdManager nsd;
    private static NsdManager.DiscoveryListener listener;
    private static boolean started;
    private static final AtomicReference<String[]> result = new AtomicReference<>();
    private static final AtomicBoolean resolving = new AtomicBoolean(false);

    private ServerDiscovery() {}

    private static NsdManager.DiscoveryListener newListener() {
        return new NsdManager.DiscoveryListener() {
            @Override
            public void onDiscoveryStarted(String serviceType) {
                Log.i(TAG, "ServerDiscovery: 探索を始めました: " + serviceType);
            }

            @Override
            public void onServiceFound(NsdServiceInfo info) {
                Log.i(TAG, "ServerDiscovery: 見つけました: " + info.getServiceName());
                if (result.get() != null || !resolving.compareAndSet(false, true)) return;
                resolve(info);
            }

            @Override
            public void onServiceLost(NsdServiceInfo info) {
                Log.i(TAG, "ServerDiscovery: 消えました: " + info.getServiceName());
            }

            @Override
            public void onDiscoveryStopped(String serviceType) {
                Log.i(TAG, "ServerDiscovery: 探索を止めました");
            }

            @Override
            public void onStartDiscoveryFailed(String serviceType, int errorCode) {
                Log.w(TAG, "ServerDiscovery: 探索を始められません: error=" + errorCode);
            }

            @Override
            public void onStopDiscoveryFailed(String serviceType, int errorCode) {
                Log.w(TAG, "ServerDiscovery: 探索を止められません: error=" + errorCode);
            }
        };
    }

    // ★ resolveService / getHost は API 34 で非推奨だが minSdk 30 でも動く。置き換え
    //   （registerServiceInfoCallback）は API 34 以上でしか使えない。
    @SuppressWarnings("deprecation")
    private static void resolve(NsdServiceInfo info) {
        nsd.resolveService(info, new NsdManager.ResolveListener() {
            @Override
            public void onResolveFailed(NsdServiceInfo failed, int errorCode) {
                Log.w(TAG, "ServerDiscovery: 解決できません: " + failed.getServiceName() + " error=" + errorCode);
                resolving.set(false);
            }

            @Override
            public void onServiceResolved(NsdServiceInfo resolved) {
                InetAddress address = pickAddress(resolved);
                if (address == null) {
                    resolving.set(false);
                    return;
                }
                result.set(new String[] {
                    address.getHostAddress(),
                    String.valueOf(resolved.getPort()),
                    resolved.getServiceName(),
                });
                Log.i(TAG, "ServerDiscovery: 解決しました: " + resolved.getServiceName());
            }
        });
    }

    // ★ IPv4 を優先する。ゾーン付きのリンクローカル IPv6 は ws:// の URL に確実には載せられない。
    @SuppressWarnings("deprecation")
    private static InetAddress pickAddress(NsdServiceInfo resolved) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            List<InetAddress> addresses = resolved.getHostAddresses();
            for (InetAddress address : addresses) {
                if (address instanceof Inet4Address) return address;
            }
            return addresses.isEmpty() ? null : addresses.get(0);
        }
        return resolved.getHost();
    }

    public static synchronized void start(Context ctx) {
        if (started) return;
        result.set(null);
        resolving.set(false);
        nsd = (NsdManager) ctx.getSystemService(Context.NSD_SERVICE);
        listener = newListener();
        nsd.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, listener);
        started = true;
    }

    /** 解決できるまで null。できたら {host, port, name} を1回だけ返す。 */
    public static String[] take() {
        return result.getAndSet(null);
    }

    public static synchronized void stop() {
        if (!started) return;
        started = false;
        try {
            nsd.stopServiceDiscovery(listener);
        } catch (IllegalArgumentException e) {
            // 登録されていない（開始に失敗した）リスナーは止められない
        }
    }
}
