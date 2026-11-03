//! Mac の画面を XR の板へ送る。server は通らず、共有している間だけ `port + 1` で待ち受ける。

mod capture;
mod tls;
mod wire;

use std::collections::HashSet;
use std::io::Write;
use std::net::{IpAddr, Shutdown, TcpListener, TcpStream};
use std::os::fd::AsRawFd;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex, MutexGuard};
use std::thread;
use std::time::{Duration, Instant};

use rustls::{ServerConfig, ServerConnection, StreamOwned};
use tauri::{AppHandle, Manager as _};
use tauri_plugin_dialog::{DialogExt as _, MessageDialogKind};

use crate::server::Manager;
use crate::{text, update_settings, ScreenPeer, Settings};
use capture::Capture;
use tls::Decision;
use wire::{Frame, SendQueue};

const HANDSHAKE_TIMEOUT: Duration = Duration::from_secs(5);
const WRITE_TIMEOUT: Duration = Duration::from_secs(5);
const KEEPALIVE_AFTER: Duration = Duration::from_secs(2);
const ACCEPT_POLL: Duration = Duration::from_millis(100);
/// 詰まりをカーネルに隠さず、送信待ちの上限で受けるための小さな送信バッファ。
const SEND_BUFFER: libc::c_int = 256 * 1024;

fn lock<T>(m: &Mutex<T>) -> MutexGuard<'_, T> {
    m.lock().unwrap_or_else(|e| e.into_inner())
}

/// メニューに映す状態。
pub struct View {
    pub sharing: bool,
    pub viewer: Option<IpAddr>,
}

type Queue = Arc<(Mutex<SendQueue>, Condvar)>;

struct Viewer {
    queue: Queue,
    generation: u64,
    ip: IpAddr,
    fingerprint: String,
    socket: TcpStream,
}

impl Viewer {
    fn close(&self) {
        let _ = self.socket.shutdown(Shutdown::Both);
        self.queue.1.notify_all();
    }
}

struct Share {
    id: u64,
    capture: Arc<Capture>,
    host: String,
    port: u16,
    listening: bool,
    stop: Arc<AtomicBool>,
    size: Option<(u32, u32)>,
    denied_fingerprints: HashSet<String>,
    denied_ips: HashSet<IpAddr>,
}

pub struct Hub {
    app: AppHandle,
    settings_path: PathBuf,
    tls: Arc<ServerConfig>,
    on_change: Box<dyn Fn(&View) + Send + Sync>,
    share: Mutex<Option<Share>>,
    viewer: Mutex<Option<Viewer>>,
    has_viewer: AtomicBool,
    asking: AtomicBool,
    encode_failure_logged: AtomicBool,
    next_id: AtomicU64,
}

/// tauri の管理下に置く入口。
pub struct Screen(Arc<Hub>);

impl Screen {
    pub fn new(
        app: AppHandle,
        settings_path: PathBuf,
        on_change: impl Fn(&View) + Send + Sync + 'static,
    ) -> Result<Self, String> {
        let dir = app.path().app_data_dir().map_err(|e| e.to_string())?;
        let identity = tls::load_or_create(&dir.join("screen")).map_err(|e| e.to_string())?;
        let tls = tls::server_config(&identity).map_err(|e| e.to_string())?;
        Ok(Screen(Arc::new(Hub {
            app,
            settings_path,
            tls,
            on_change: Box::new(on_change),
            share: Mutex::new(None),
            viewer: Mutex::new(None),
            has_viewer: AtomicBool::new(false),
            asking: AtomicBool::new(false),
            encode_failure_logged: AtomicBool::new(false),
            next_id: AtomicU64::new(0),
        })))
    }

    pub fn toggle(&self) {
        let hub = &self.0;
        if lock(&hub.share).is_some() {
            hub.stop_sharing();
            return;
        }
        let t = text::current();
        let env = hub.app.state::<Manager>().env();
        let Ok((host, port)) = crate::control::server_listen(&env) else {
            hub.notice(t.screen_no_server.into());
            return;
        };
        hub.encode_failure_logged.store(false, Ordering::SeqCst);
        let capture = Arc::new(Capture::new(hub.app.clone(), hub.clone()));
        *lock(&hub.share) = Some(Share {
            id: hub.next_id.fetch_add(1, Ordering::SeqCst) + 1,
            capture: capture.clone(),
            host,
            port,
            listening: false,
            stop: Arc::new(AtomicBool::new(false)),
            size: None,
            denied_fingerprints: HashSet::new(),
            denied_ips: HashSet::new(),
        });
        hub.changed();
        capture.present();
    }

    /// 解除した端末が映っていれば切る。
    pub fn disconnect_fingerprint(&self, fingerprint: &str) {
        if let Some(v) = lock(&self.0.viewer).as_ref() {
            if v.fingerprint == fingerprint {
                v.close();
            }
        }
    }
}

impl Hub {
    pub(crate) fn log(&self, msg: &str) {
        if let Some(m) = self.app.try_state::<Manager>() {
            m.log(msg);
        }
    }

    fn notice(&self, msg: String) {
        let app = self.app.clone();
        thread::spawn(move || {
            app.dialog()
                .message(msg)
                .title(text::current().screen_title)
                .kind(MessageDialogKind::Warning)
                .blocking_show();
        });
    }

    fn changed(&self) {
        let view = View {
            sharing: lock(&self.share).is_some(),
            viewer: lock(&self.viewer).as_ref().map(|v| v.ip),
        };
        (self.on_change)(&view);
    }

    pub(crate) fn has_viewer(&self) -> bool {
        self.has_viewer.load(Ordering::SeqCst)
    }

    /// 板が固まる原因を追えるように残す。フレームごとに書かないよう、共有ごとに 1 回だけ。
    pub(crate) fn encode_failed(&self, status: i32) {
        if !self.encode_failure_logged.swap(true, Ordering::SeqCst) {
            self.log(&format!("Screen encoding failed ({status})"));
        }
    }

    pub(crate) fn push_frame(&self, frame: Frame) {
        if let Some(v) = lock(&self.viewer).as_ref() {
            lock(&v.queue.0).push(frame);
            v.queue.1.notify_one();
        }
    }

    /// 解像度が変わるときは、映している相手を切ってから繋ぎ直させる。
    /// 待ち受けを始められなければ共有をやめて `false`。
    pub(crate) fn selected(self: &Arc<Self>, width: u32, height: u32) -> bool {
        self.disconnect_viewer();
        let start = {
            let mut guard = lock(&self.share);
            let Some(share) = guard.as_mut() else {
                return false;
            };
            share.size = Some((width, height));
            let first = !share.listening;
            share.listening = true;
            first.then(|| (share.id, share.host.clone(), share.port, share.stop.clone()))
        };
        if let Some((id, host, port, stop)) = start {
            if let Err(e) = self.listen(id, &host, port, stop) {
                self.log(&format!("Cannot listen for screen viewers: {e}"));
                self.stop_sharing();
                self.notice(text::current().screen_bind_failed(&e));
                return false;
            }
        }
        true
    }

    pub(crate) fn capture_ended(self: &Arc<Self>, reason: Option<String>) {
        if lock(&self.share).is_none() {
            return;
        }
        self.stop_sharing();
        if let Some(r) = reason {
            self.log(&format!("Screen sharing ended: {r}"));
            self.notice(text::current().screen_failed(&r));
        }
    }

    fn stop_sharing(&self) {
        let share = lock(&self.share).take();
        if let Some(share) = share {
            share.stop.store(true, Ordering::SeqCst);
            share.capture.stop();
        }
        self.disconnect_viewer();
        self.changed();
    }

    fn disconnect_viewer(&self) {
        if let Some(v) = lock(&self.viewer).as_ref() {
            v.close();
        }
    }

    fn listen(
        self: &Arc<Self>,
        id: u64,
        host: &str,
        port: u16,
        stop: Arc<AtomicBool>,
    ) -> Result<(), String> {
        let port = port.checked_add(1).ok_or("port out of range")?;
        let listener =
            TcpListener::bind((host, port)).map_err(|e| format!("{host}:{port}: {e}"))?;
        listener.set_nonblocking(true).map_err(|e| e.to_string())?;
        self.log(&format!("Listening for screen viewers on {host}:{port}"));
        let hub = self.clone();
        thread::spawn(move || {
            while !stop.load(Ordering::SeqCst) {
                match listener.accept() {
                    Ok((tcp, peer)) => {
                        let hub = hub.clone();
                        thread::spawn(move || hub.serve(id, tcp, peer.ip()));
                    }
                    Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                        thread::sleep(ACCEPT_POLL)
                    }
                    Err(e) => {
                        hub.log(&format!("Screen listener stopped: {e}"));
                        break;
                    }
                }
            }
        });
        Ok(())
    }

    fn is_sharing(&self, id: u64) -> bool {
        lock(&self.share).as_ref().is_some_and(|s| s.id == id)
    }

    fn serve(self: Arc<Self>, id: u64, tcp: TcpStream, ip: IpAddr) {
        if let Err(e) = self.serve_inner(id, tcp, ip) {
            self.log(&format!("Screen viewer {ip} disconnected: {e}"));
        }
    }

    fn serve_inner(&self, id: u64, tcp: TcpStream, ip: IpAddr) -> Result<(), String> {
        let e = |e: std::io::Error| e.to_string();
        tcp.set_nonblocking(false).map_err(e)?;
        tcp.set_nodelay(true).map_err(e)?;
        set_send_buffer(&tcp);
        tcp.set_read_timeout(Some(HANDSHAKE_TIMEOUT)).map_err(e)?;
        tcp.set_write_timeout(Some(WRITE_TIMEOUT)).map_err(e)?;
        let socket = tcp.try_clone().map_err(e)?;
        let mut conn = ServerConnection::new(self.tls.clone()).map_err(|e| e.to_string())?;
        let mut tcp = tcp;
        tls::handshake(&mut conn, &mut tcp, HANDSHAKE_TIMEOUT)
            .map_err(|e| format!("handshake: {e}"))?;
        let fingerprint = conn
            .peer_certificates()
            .and_then(|c| c.first())
            .map(|c| tls::fingerprint(c))
            .ok_or("no client certificate")?;

        let approved = Settings::load(&self.settings_path)
            .screen_peers
            .iter()
            .any(|p| p.fingerprint == fingerprint);
        let decision = {
            let guard = lock(&self.share);
            let Some(share) = guard.as_ref().filter(|s| s.id == id) else {
                return Ok(());
            };
            let busy = self.asking.load(Ordering::SeqCst);
            tls::decide(
                &fingerprint,
                ip,
                approved,
                &share.denied_fingerprints,
                &share.denied_ips,
                busy,
            )
        };
        match decision {
            Decision::Reject => return Ok(()),
            Decision::Attach => {}
            Decision::Ask => {
                if self.asking.swap(true, Ordering::SeqCst) {
                    return Ok(());
                }
                let allowed = self.ask(ip, &fingerprint);
                self.asking.store(false, Ordering::SeqCst);
                if !self.is_sharing(id) {
                    return Ok(());
                }
                if !allowed {
                    if let Some(share) = lock(&self.share).as_mut() {
                        share.denied_fingerprints.insert(fingerprint);
                        share.denied_ips.insert(ip);
                    }
                    return Ok(());
                }
                let peer = ScreenPeer {
                    fingerprint: fingerprint.clone(),
                    label: ip.to_string(),
                    approved_at: chrono::Local::now().to_rfc3339(),
                };
                if let Err(err) = update_settings(&self.settings_path, |s| {
                    s.screen_peers.retain(|p| p.fingerprint != peer.fingerprint);
                    s.screen_peers.push(peer);
                }) {
                    self.log(&format!("Cannot save the approved screen viewer: {err}"));
                }
            }
        }

        let mut stream = StreamOwned::new(conn, tcp);
        self.attach(id, &mut stream, socket, ip, fingerprint)
    }

    /// 確認ダイアログ。メインスレッドでは呼ばない。
    fn ask(&self, ip: IpAddr, fingerprint: &str) -> bool {
        let t = text::current();
        crate::mascot::confirm_blocking(
            &self.app,
            t.screen_title.into(),
            t.screen_confirm(&ip.to_string(), &tls::confirm_code(fingerprint)),
            t.screen_allow.into(),
            t.screen_deny.into(),
        )
    }

    fn attach(
        &self,
        id: u64,
        out: &mut impl Write,
        socket: TcpStream,
        ip: IpAddr,
        fingerprint: String,
    ) -> Result<(), String> {
        let (capture, size) = {
            let guard = lock(&self.share);
            match guard.as_ref().filter(|s| s.id == id) {
                Some(Share {
                    capture,
                    size: Some(size),
                    ..
                }) => (capture.clone(), *size),
                _ => return Ok(()),
            }
        };
        let queue: Queue = Arc::new((Mutex::new(SendQueue::new()), Condvar::new()));
        let generation = self.next_id.fetch_add(1, Ordering::SeqCst) + 1;
        if let Some(old) = lock(&self.viewer).replace(Viewer {
            queue: queue.clone(),
            generation,
            ip,
            fingerprint,
            socket,
        }) {
            old.close();
        }
        self.has_viewer.store(true, Ordering::SeqCst);
        self.changed();
        self.log(&format!("Screen viewer attached: {ip}"));

        let result = self.write_loop(generation, out, &queue, &capture, size);

        let mut guard = lock(&self.viewer);
        if guard.as_ref().is_some_and(|v| v.generation == generation) {
            if let Some(v) = guard.take() {
                v.close();
            }
            self.has_viewer.store(false, Ordering::SeqCst);
            drop(guard);
            self.changed();
        }
        result
    }

    fn write_loop(
        &self,
        generation: u64,
        out: &mut impl Write,
        queue: &Queue,
        capture: &Capture,
        (width, height): (u32, u32),
    ) -> Result<(), String> {
        let send = |out: &mut dyn Write, kind: u8, body: &[u8]| -> Result<(), String> {
            out.write_all(&wire::frame(kind, body))
                .and_then(|_| out.flush())
                .map_err(|e| e.to_string())
        };
        let current = || {
            lock(&self.viewer)
                .as_ref()
                .is_some_and(|v| v.generation == generation)
        };
        let hello = format!("{{\"width\":{width},\"height\":{height}}}");
        send(out, wire::HELLO, hello.as_bytes())?;
        capture.request_keyframe();

        let mut last_write = Instant::now();
        while current() {
            let (next, need_key) = {
                let (m, cv) = &**queue;
                let mut q = lock(m);
                let mut frame = q.pop();
                if frame.is_none() {
                    let wait = KEEPALIVE_AFTER.saturating_sub(last_write.elapsed());
                    q = cv
                        .wait_timeout(q, wait)
                        .unwrap_or_else(|e| e.into_inner())
                        .0;
                    frame = q.pop();
                }
                (frame, q.take_needs_keyframe())
            };
            if need_key {
                capture.request_keyframe();
            }
            match next {
                Some(f) => send(out, wire::VIDEO, &f.data)?,
                None if last_write.elapsed() >= KEEPALIVE_AFTER => send(out, wire::KEEPALIVE, &[])?,
                None => continue,
            }
            last_write = Instant::now();
        }
        Ok(())
    }
}

fn set_send_buffer(tcp: &TcpStream) {
    unsafe {
        libc::setsockopt(
            tcp.as_raw_fd(),
            libc::SOL_SOCKET,
            libc::SO_SNDBUF,
            &SEND_BUFFER as *const _ as *const libc::c_void,
            std::mem::size_of_val(&SEND_BUFFER) as libc::socklen_t,
        );
    }
}
