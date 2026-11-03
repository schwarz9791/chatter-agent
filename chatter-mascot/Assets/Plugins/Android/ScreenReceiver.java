package tech.sukima.chattermascot;

import android.content.Context;
import android.content.SharedPreferences;
import android.media.MediaCodec;
import android.media.MediaFormat;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Log;
import android.view.Surface;

import org.json.JSONObject;

import java.io.BufferedInputStream;
import java.io.DataInputStream;
import java.io.IOException;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.security.KeyPairGenerator;
import java.security.KeyStore;
import java.security.MessageDigest;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import java.security.spec.ECGenParameterSpec;

import javax.net.ssl.KeyManagerFactory;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLSocket;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;
import javax.security.auth.x500.X500Principal;

/**
 * Mac の画面（ChatterAgent が配る H.264）を TLS で受け、MediaCodec で Surface へ描く。
 * 契約は docs/protocol.md「画面の配信」。
 *
 * C# から state() などを引いて使う。start 1 回が接続 1 回で、切れたら IDLE に戻る。
 * 繋ぎ直しは呼び出し側が決める。start / stop は呼び出しスレッドを待たせない。
 *
 * hello の後は Surface が渡るまでセッションのスレッドを止める。その間に届く映像はソケットに溜まり、
 * Mac 側のキューが背圧を引き受ける。
 */
public final class ScreenReceiver {
    public static final int IDLE = 0;
    public static final int CONNECTING = 1;
    public static final int WAITING_APPROVAL = 2;
    public static final int READY = 3;
    public static final int STREAMING = 4;
    public static final int CERT_MISMATCH = 5;

    private static final String TAG = "ChatterMascot";
    private static final String LOG = "[Mascot] XR screen: ";
    private static final String KEY_ALIAS = "chatter-mascot-screen";
    private static final String PREFS = "chatter-mascot-screen";
    private static final String PIN_KEY = "macFingerprint";
    private static final int CONNECT_TIMEOUT_MS = 2000;
    private static final int APPROVAL_TIMEOUT_MS = 120000;
    private static final int STREAM_TIMEOUT_MS = 6000;
    private static final long APPROVAL_NOTICE_MS = 1000;
    private static final long SURFACE_TIMEOUT_MS = 15000;
    private static final long OUTPUT_POLL_US = 10000;
    private static final int MAX_FRAME = 16 * 1024 * 1024;

    private static final String[] STATE_NAMES = {"IDLE", "CONNECTING", "WAITING_APPROVAL", "READY", "STREAMING", "CERT_MISMATCH"};

    private static volatile Session current;
    private static volatile String confirmCode = "";

    private ScreenReceiver() {}

    public static synchronized void start(Context ctx, String host, int port) {
        Session previous = current;
        if (previous != null) previous.halt();
        current = new Session(ctx.getApplicationContext(), host, port, previous);
        current.thread.start();
    }

    public static synchronized void stop() {
        if (current != null) current.halt();
    }

    public static int state() {
        Session s = current;
        return s == null ? IDLE : s.currentState();
    }

    public static int width() {
        Session s = current;
        return s == null ? 0 : s.width;
    }

    public static int height() {
        Session s = current;
        return s == null ? 0 : s.height;
    }

    public static String confirmCode() {
        return confirmCode;
    }

    public static void attachSurface(Object surface) {
        Session s = current;
        if (s != null) s.attach((Surface) surface);
    }

    public static void forgetMac(Context ctx) {
        ctx.getApplicationContext().getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().remove(PIN_KEY).apply();
    }

    private static String hex(byte[] bytes) {
        StringBuilder sb = new StringBuilder();
        for (byte b : bytes) sb.append(String.format("%02x", b & 0xFF));
        return sb.toString();
    }

    private static String fingerprint(byte[] der) throws Exception {
        return hex(MessageDigest.getInstance("SHA-256").digest(der));
    }

    private static KeyStore clientKeyStore() throws Exception {
        KeyStore ks = KeyStore.getInstance("AndroidKeyStore");
        ks.load(null);
        if (!ks.containsAlias(KEY_ALIAS)) {
            KeyPairGenerator gen = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore");
            gen.initialize(new KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_SIGN)
                    .setAlgorithmParameterSpec(new ECGenParameterSpec("secp256r1"))
                    .setDigests(KeyProperties.DIGEST_NONE, KeyProperties.DIGEST_SHA256)
                    .setCertificateSubject(new X500Principal("CN=chatter-mascot"))
                    .build());
            gen.generateKeyPair();
        }
        String fp = fingerprint(ks.getCertificate(KEY_ALIAS).getEncoded());
        confirmCode = (fp.substring(0, 4) + "-" + fp.substring(4, 8) + "-" + fp.substring(8, 12) + "-" + fp.substring(12, 16)).toUpperCase();
        return ks;
    }

    private static final class Session implements Runnable, X509TrustManager {
        final Context context;
        final String host;
        final int port;
        Session previous;
        final Thread thread = new Thread(this, "ScreenReceiver");
        final Object surfaceLock = new Object();

        volatile boolean stopped;
        volatile int state = CONNECTING;
        volatile long handshakeEndMs;
        volatile boolean helloReceived;
        volatile boolean mismatch;
        volatile int width;
        volatile int height;
        volatile Socket raw;
        volatile SSLSocket tls;
        volatile MediaCodec codec;

        Surface surface;
        String candidateFingerprint;
        Thread outputThread;

        Session(Context context, String host, int port, Session previous) {
            this.context = context;
            this.host = host;
            this.port = port;
            this.previous = previous;
            thread.setDaemon(true);
        }

        int currentState() {
            if (stopped) return IDLE;
            int s = state;
            long end = handshakeEndMs;
            if (s == CONNECTING && end > 0 && !helloReceived && System.currentTimeMillis() - end > APPROVAL_NOTICE_MS) {
                return WAITING_APPROVAL;
            }
            return s;
        }

        void setState(int next) {
            state = next;
            Log.i(TAG, LOG + "state " + STATE_NAMES[next]);
        }

        void attach(Surface s) {
            synchronized (surfaceLock) {
                if (surface == null) surface = s;
                surfaceLock.notifyAll();
            }
        }

        void halt() {
            stopped = true;
            // TLS の close は後始末で待つことがあるので、呼び出し側（Unity のメインスレッド）を止めない
            Thread closer = new Thread(new Runnable() {
                @Override
                public void run() {
                    closeQuietly(tls);
                    closeQuietly(raw);
                }
            }, "ScreenReceiverClose");
            closer.setDaemon(true);
            closer.start();
            synchronized (surfaceLock) {
                surfaceLock.notifyAll();
            }
        }

        static void closeQuietly(Socket s) {
            try {
                if (s != null) s.close();
            } catch (Exception ignored) {
            }
        }

        @Override
        public void run() {
            try {
                // 同じ Surface に 2 つのコーデックを触れさせないため、前のセッションの後始末を待つ
                if (previous != null) previous.thread.join();
                previous = null;
                if (stopped) return;
                connectAndRead();
            } catch (Exception e) {
                if (!stopped) Log.w(TAG, LOG + "session ended: " + e);
            } finally {
                releaseCodec();
                closeQuietly(tls);
                closeQuietly(raw);
                int last = mismatch ? CERT_MISMATCH : IDLE;
                state = last;
                Log.i(TAG, LOG + "state " + STATE_NAMES[last]);
            }
        }

        void connectAndRead() throws Exception {
            KeyManagerFactory kmf = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm());
            kmf.init(clientKeyStore(), null);
            SSLContext ssl = SSLContext.getInstance("TLSv1.3");
            ssl.init(kmf.getKeyManagers(), new TrustManager[] {this}, null);

            Socket s = new Socket();
            raw = s;
            s.setTcpNoDelay(true);
            s.connect(new InetSocketAddress(host, port), CONNECT_TIMEOUT_MS);
            SSLSocket t = (SSLSocket) ssl.getSocketFactory().createSocket(s, host, port, true);
            tls = t;
            // 代入より前に止められていると halt は閉じられない。ハンドシェイクで待たずに抜ける
            if (stopped) return;
            t.setEnabledProtocols(new String[] {"TLSv1.3"});
            t.setSoTimeout(APPROVAL_TIMEOUT_MS);
            t.startHandshake();
            handshakeEndMs = System.currentTimeMillis();
            Log.i(TAG, LOG + "handshake done");

            DataInputStream in = new DataInputStream(new BufferedInputStream(t.getInputStream()));
            while (!stopped) {
                int length = in.readInt();
                if (length < 0 || length > MAX_FRAME) throw new IOException("bad frame length " + length);
                int kind = in.readByte() & 0xFF;
                byte[] body = new byte[length];
                in.readFully(body);
                if (kind == 0) {
                    onHello(body, t);
                } else if (kind == 1) {
                    if (codec == null) throw new IOException("video before hello");
                    feed(body);
                }
            }
        }

        void onHello(byte[] body, SSLSocket t) throws Exception {
            if (helloReceived) throw new IOException("second hello");
            JSONObject json = new JSONObject(new String(body, "UTF-8"));
            int w = json.getInt("width");
            int h = json.getInt("height");
            if (w <= 0 || h <= 0) throw new IOException("bad hello size");

            // Mac が承認した後にだけ指紋を覚える
            SharedPreferences prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
            if (prefs.getString(PIN_KEY, null) == null && candidateFingerprint != null) {
                prefs.edit().putString(PIN_KEY, candidateFingerprint).apply();
            }

            width = w;
            height = h;
            helloReceived = true;
            t.setSoTimeout(STREAM_TIMEOUT_MS);
            setState(READY);

            Surface s;
            long deadline = System.currentTimeMillis() + SURFACE_TIMEOUT_MS;
            synchronized (surfaceLock) {
                while (surface == null && !stopped) {
                    long left = deadline - System.currentTimeMillis();
                    if (left <= 0) throw new IOException("no surface");
                    surfaceLock.wait(left);
                }
                s = surface;
            }
            if (stopped) return;
            createCodec(s, w, h);
            setState(STREAMING);
        }

        void createCodec(Surface s, int w, int h) throws Exception {
            MediaFormat format = MediaFormat.createVideoFormat("video/avc", w, h);
            format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1);
            format.setInteger(MediaFormat.KEY_PRIORITY, 0);
            // 文字の多い画面のキーフレームは大きい。入らないと繋ぎ直しを繰り返す
            format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, w * h);
            final MediaCodec c = MediaCodec.createDecoderByType("video/avc");
            codec = c;
            c.configure(format, s, null, 0);
            c.start();
            // 映像は止まることがあるので、次のフレームが来なくても描けるように出力を別のスレッドで取る
            Thread t = new Thread(new Runnable() {
                @Override
                public void run() {
                    render(c);
                }
            }, "ScreenReceiverOutput");
            t.setDaemon(true);
            outputThread = t;
            t.start();
        }

        void render(MediaCodec c) {
            MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
            boolean firstFrameLogged = false;
            try {
                while (codec == c) {
                    int idx = c.dequeueOutputBuffer(info, OUTPUT_POLL_US);
                    if (idx < 0) continue;
                    c.releaseOutputBuffer(idx, true);
                    if (!firstFrameLogged) {
                        firstFrameLogged = true;
                        Log.i(TAG, LOG + "first frame rendered");
                    }
                }
            } catch (IllegalStateException e) {
                if (stopped) return;
                // 映像が止まっていると入力側は失敗に気づけないので、ここで畳んで繋ぎ直しに任せる
                Log.w(TAG, LOG + "decoder failed: " + e);
                halt();
            }
        }

        void releaseCodec() {
            MediaCodec c = codec;
            if (c == null) return;
            codec = null;
            Thread t = outputThread;
            if (t != null) {
                try {
                    t.join();
                } catch (InterruptedException ignored) {
                }
            }
            try {
                c.stop();
            } catch (Exception ignored) {
            }
            try {
                c.release();
            } catch (Exception ignored) {
            }
        }

        void feed(byte[] data) {
            while (!stopped) {
                int idx = codec.dequeueInputBuffer(10000);
                if (idx >= 0) {
                    ByteBuffer b = codec.getInputBuffer(idx);
                    b.clear();
                    b.put(data);
                    codec.queueInputBuffer(idx, 0, data.length, System.nanoTime() / 1000, 0);
                    break;
                }
            }
        }

        @Override
        public void checkServerTrusted(X509Certificate[] chain, String authType) throws CertificateException {
            try {
                String fp = fingerprint(chain[0].getEncoded());
                String pinned = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(PIN_KEY, null);
                if (pinned != null && !pinned.equals(fp)) {
                    mismatch = true;
                    Log.w(TAG, LOG + "Mac certificate changed");
                    throw new CertificateException("pinned fingerprint differs");
                }
                candidateFingerprint = fp;
            } catch (CertificateException e) {
                throw e;
            } catch (Exception e) {
                throw new CertificateException(e);
            }
        }

        @Override
        public void checkClientTrusted(X509Certificate[] chain, String authType) throws CertificateException {
            throw new CertificateException("client mode only");
        }

        @Override
        public X509Certificate[] getAcceptedIssuers() {
            return new X509Certificate[0];
        }
    }
}
