package tech.sukima.chattermascot;

import android.media.MediaCodec;
import android.media.MediaFormat;
import android.util.Log;
import android.view.Surface;

import java.io.ByteArrayOutputStream;
import java.io.FileInputStream;
import java.io.InputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.ByteBuffer;

/**
 * Annex-B H.264 のバイト列（tcp://host:port か絶対パスのファイル）を MediaCodec でデコードして Surface へ描く。
 */
public final class SpikeVideoReceiver {
    private static final String TAG = "ChatterMascot";
    private static Session current;

    public static synchronized void start(Object surface, String source, int width, int height) {
        stop();
        current = new Session((Surface) surface, source, width, height);
        current.thread.start();
    }

    public static synchronized void stop() {
        if (current == null) return;
        current.halt();
        current = null;
    }

    private static final class Session implements Runnable {
        final Surface surface;
        final String source;
        final int width, height;
        final boolean tcp;
        final Thread thread = new Thread(this, "SpikeVideoReceiver");
        volatile boolean stopped;
        volatile Socket socket;

        MediaCodec codec;
        long startMs;
        long frameIndex;
        boolean firstFrameLogged;
        int framesThisSecond;
        long fpsWindowStartMs;
        long nextDueMs;
        final MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();

        Session(Surface surface, String source, int width, int height) {
            this.surface = surface;
            this.source = source;
            this.width = width;
            this.height = height;
            this.tcp = source.startsWith("tcp://");
        }

        void halt() {
            stopped = true;
            try {
                Socket s = socket;
                if (s != null) s.close();
            } catch (Exception ignored) {
            }
            try {
                thread.join(1500);
            } catch (InterruptedException ignored) {
            }
        }

        @Override
        public void run() {
            Log.i(TAG, "[Spike] receiver start source=" + source + " " + width + "x" + height);
            while (!stopped) {
                InputStream in = null;
                try {
                    in = open();
                    startMs = System.currentTimeMillis();
                    firstFrameLogged = false;
                    frameIndex = 0;
                    fpsWindowStartMs = startMs;
                    framesThisSecond = 0;
                    nextDueMs = 0;
                    createCodec();
                    do {
                        pump(in);
                        if (!tcp && !stopped) {
                            in.close();
                            in = new FileInputStream(source);
                        }
                    } while (!tcp && !stopped);
                } catch (Exception e) {
                    if (!stopped) Log.e(TAG, "[Spike] receiver error: " + e, e);
                } finally {
                    releaseCodec();
                    try {
                        if (in != null) in.close();
                    } catch (Exception ignored) {
                    }
                    socket = null;
                }
                if (!tcp) break;
                for (int i = 0; i < 20 && !stopped; i++) sleep(100);
            }
            Log.i(TAG, "[Spike] receiver end");
        }

        InputStream open() throws Exception {
            if (!tcp) return new FileInputStream(source);
            String hostPort = source.substring("tcp://".length());
            int colon = hostPort.lastIndexOf(':');
            String host = hostPort.substring(0, colon);
            int port = Integer.parseInt(hostPort.substring(colon + 1));
            while (!stopped) {
                Socket s = new Socket();
                try {
                    s.setTcpNoDelay(true);
                    s.connect(new InetSocketAddress(host, port), 2000);
                    socket = s;
                    Log.i(TAG, "[Spike] connected " + source);
                    return s.getInputStream();
                } catch (Exception e) {
                    try {
                        s.close();
                    } catch (Exception ignored) {
                    }
                    Log.i(TAG, "[Spike] connect failed (" + e.getMessage() + "), retry in 2s");
                    for (int i = 0; i < 20 && !stopped; i++) sleep(100);
                }
            }
            throw new java.io.IOException("stopped");
        }

        void createCodec() throws Exception {
            MediaFormat format = MediaFormat.createVideoFormat("video/avc", width, height);
            format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1);
            format.setInteger(MediaFormat.KEY_PRIORITY, 0);
            codec = MediaCodec.createDecoderByType("video/avc");
            Log.i(TAG, "[Spike] decoder=" + codec.getName());
            codec.configure(format, surface, null, 0);
            codec.start();
        }

        void releaseCodec() {
            if (codec == null) return;
            try {
                codec.stop();
            } catch (Exception ignored) {
            }
            try {
                codec.release();
            } catch (Exception ignored) {
            }
            codec = null;
        }

        // 先頭が開始コードの NAL を1つずつ切り出し、AU の境界で feed する。
        // buf[0..len) は「いま組み立て中の NAL」から始まる（active のとき）。
        final ByteArrayOutputStream au = new ByteArrayOutputStream();
        boolean auHasVcl;

        void pump(InputStream in) throws Exception {
            byte[] buf = new byte[1 << 20];
            int len = 0;
            boolean active = false;
            boolean decided = false;
            boolean curVcl = false;
            while (!stopped) {
                if (len == buf.length) buf = java.util.Arrays.copyOf(buf, buf.length * 2);
                int n = in.read(buf, len, buf.length - len);
                boolean eof = n < 0;
                if (!eof) len += n;
                while (true) {
                    if (!active) {
                        int s = findStart(buf, 0, len);
                        if (s < 0) {
                            int keep = Math.min(len, 2);
                            System.arraycopy(buf, len - keep, buf, 0, keep);
                            len = keep;
                            break;
                        }
                        System.arraycopy(buf, s, buf, 0, len - s);
                        len -= s;
                        active = true;
                        decided = false;
                    }
                    if (!decided) {
                        if (len < 5) break;
                        int type = buf[3] & 0x1F;
                        curVcl = type == 1 || type == 5;
                        boolean firstSlice = curVcl && (buf[4] & 0x80) != 0;
                        if (auHasVcl && (type == 9 || type == 7 || type == 8 || type == 6 || firstSlice)) flushAu();
                        decided = true;
                    }
                    int next = findStart(buf, 3, len);
                    if (next < 0) break;
                    int end = next;
                    while (end > 3 && buf[end - 1] == 0) end--;
                    au.write(buf, 0, end);
                    if (curVcl) auHasVcl = true;
                    System.arraycopy(buf, next, buf, 0, len - next);
                    len -= next;
                    decided = false;
                }
                if (eof) {
                    if (active && decided) {
                        int end = len;
                        while (end > 3 && buf[end - 1] == 0) end--;
                        au.write(buf, 0, end);
                        if (curVcl) auHasVcl = true;
                    }
                    flushAu();
                    return;
                }
            }
        }

        static int findStart(byte[] b, int from, int len) {
            for (int i = from; i + 2 < len; i++) {
                if (b[i + 2] > 1) {
                    i += 2;
                } else if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1) {
                    return i;
                }
            }
            return -1;
        }

        void flushAu() throws Exception {
            if (au.size() > 0 && auHasVcl) feed(au.toByteArray());
            au.reset();
            auHasVcl = false;
        }

        void feed(byte[] data) throws Exception {
            if (!tcp) {
                long now = System.currentTimeMillis();
                if (nextDueMs == 0 || now - nextDueMs > 100) nextDueMs = now;
                while (!stopped && System.currentTimeMillis() < nextDueMs) {
                    drain();
                    sleep(2);
                }
                nextDueMs += 1000 / 30 + (frameIndex % 3 == 0 ? 1 : 0);
            }
            while (!stopped) {
                drain();
                int idx = codec.dequeueInputBuffer(10000);
                if (idx >= 0) {
                    ByteBuffer b = codec.getInputBuffer(idx);
                    b.clear();
                    b.put(data);
                    long pts = tcp ? System.nanoTime() / 1000 : frameIndex * 33333;
                    codec.queueInputBuffer(idx, 0, data.length, pts, 0);
                    frameIndex++;
                    break;
                }
            }
            drain();
        }

        void drain() {
            while (true) {
                int idx = codec.dequeueOutputBuffer(info, 0);
                if (idx == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                    MediaFormat f = codec.getOutputFormat();
                    Log.i(TAG, "[Spike] output format " + f.getInteger(MediaFormat.KEY_WIDTH) + "x"
                            + f.getInteger(MediaFormat.KEY_HEIGHT));
                } else if (idx >= 0) {
                    codec.releaseOutputBuffer(idx, true);
                    long now = System.currentTimeMillis();
                    if (!firstFrameLogged) {
                        firstFrameLogged = true;
                        Log.i(TAG, "[Spike] first frame rendered after " + (now - startMs) + " ms");
                    }
                    framesThisSecond++;
                    if (now - fpsWindowStartMs >= 1000) {
                        Log.i(TAG, "[Spike] rendered fps=" + framesThisSecond);
                        framesThisSecond = 0;
                        fpsWindowStartMs = now;
                    }
                } else if (idx < 0 && idx != MediaCodec.INFO_OUTPUT_BUFFERS_CHANGED) {
                    return;
                }
            }
        }

        static void sleep(long ms) {
            try {
                Thread.sleep(ms);
            } catch (InterruptedException ignored) {
            }
        }
    }
}
