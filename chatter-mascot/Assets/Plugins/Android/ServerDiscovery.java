package tech.sukima.chattermascot;

import android.content.Context;
import android.net.nsd.NsdManager;
import android.net.nsd.NsdServiceInfo;
import android.util.Log;

import java.util.concurrent.atomic.AtomicBoolean;

/**
 * LAN 上の chatter-agent-server を DNS-SD（_chatter-agent._tcp）で探す。
 *
 * NSD のコールバックはすべてこのクラスの中で完結させ、C# 側へは poll() の結果だけを返す。
 * 最初に解決できた1台を採る。
 */
public final class ServerDiscovery {
    private static final String TAG = "ChatterMascot";
    private static final String SERVICE_TYPE = "_chatter-agent._tcp";

    private static NsdManager nsd;
    private static boolean started;
    private static volatile String[] result;
    private static final AtomicBoolean resolving = new AtomicBoolean(false);

    private ServerDiscovery() {}

    private static final NsdManager.DiscoveryListener discoveryListener = new NsdManager.DiscoveryListener() {
        @Override
        public void onDiscoveryStarted(String serviceType) {
            Log.i(TAG, "ServerDiscovery: 探索を始めました: " + serviceType);
        }

        @Override
        public void onServiceFound(NsdServiceInfo info) {
            Log.i(TAG, "ServerDiscovery: 見つけました: " + info.getServiceName());
            if (result != null || !resolving.compareAndSet(false, true)) return;
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
                if (resolved.getHost() == null) {
                    resolving.set(false);
                    return;
                }
                result = new String[] {
                    resolved.getHost().getHostAddress(),
                    String.valueOf(resolved.getPort()),
                    resolved.getServiceName(),
                };
                Log.i(TAG, "ServerDiscovery: 解決しました: " + resolved.getServiceName());
            }
        });
    }

    public static synchronized void start(Context ctx) {
        if (started) return;
        result = null;
        resolving.set(false);
        nsd = (NsdManager) ctx.getSystemService(Context.NSD_SERVICE);
        nsd.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, discoveryListener);
        started = true;
    }

    /** 解決できるまで null。できたら {host, port, name}。 */
    public static String[] poll() {
        return result;
    }

    public static synchronized void stop() {
        if (!started) return;
        started = false;
        try {
            nsd.stopServiceDiscovery(discoveryListener);
        } catch (IllegalArgumentException e) {
            // 登録されていない（開始に失敗した）リスナーは止められない
        }
    }
}
