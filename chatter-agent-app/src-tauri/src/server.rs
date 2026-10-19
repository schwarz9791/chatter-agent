//! chatter-agent-server の面倒を見る。環境の解決・事前チェック・起動・停止・監視。

use std::collections::HashMap;
use std::fs::{self, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, ExitStatus, Stdio};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{mpsc, Arc, Mutex, MutexGuard};
use std::thread;
use std::time::{Duration, Instant};

use crate::text;

pub type Env = HashMap<String, String>;

const LOG_MAX_BYTES: u64 = 10 * 1024 * 1024;
const ENV_MARKER: &[u8] = b"\0__CHATTER_AGENT_ENV__\0";

/// 起動できない理由。
#[derive(Clone, Debug, PartialEq)]
pub enum Fail {
    NoCore,
    NoDist,
    NoNodeModules,
    Env,
    NodeMissing,
    SpawnFailed(String),
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum ExitInfo {
    Code(i32),
    Signal(i32),
    Unknown,
}

#[derive(Clone, Debug, PartialEq)]
pub enum Status {
    Starting,
    Running(u32),
    Stopping,
    Stopped,
    Crashed(ExitInfo),
    External(u32),
    Cannot(Fail),
}

/// ログインシェルが出した `env -0` の出力を、マーカー以降だけ取り出して分解する。
/// rc が出すゴミがマーカーの前に混ざるので、マーカーが無ければ失敗とする。
pub fn parse_env_output(stdout: &[u8]) -> Option<Env> {
    let pos = stdout
        .windows(ENV_MARKER.len())
        .position(|w| w == ENV_MARKER)?;
    let env = stdout[pos + ENV_MARKER.len()..]
        .split(|b| *b == 0)
        .filter_map(|entry| {
            let entry = String::from_utf8_lossy(entry);
            let (k, v) = entry.split_once('=')?;
            (!k.is_empty()).then(|| (k.to_string(), v.to_string()))
        })
        .collect();
    Some(env)
}

/// server / player の起こし方。
#[derive(Clone, Debug, PartialEq)]
pub enum Core {
    /// 同梱の実行ファイル（サブコマンドで役割を選ぶ）。
    Bundled(PathBuf),
    /// 開発用に `devCoreDir` で指した core。ビルド済みの `dist/` を node で動かす。
    Dir(PathBuf),
}

impl Core {
    pub fn command(&self, role: &str) -> Command {
        let mut c = match self {
            Core::Bundled(bin) => {
                let mut c = Command::new(bin);
                c.arg(role);
                c
            }
            Core::Dir(_) => {
                let mut c = Command::new("node");
                c.arg(format!("dist/chatter-agent-{role}.mjs"));
                c
            }
        };
        // 継がせると ChatterAgent の cwd（Finder 起動では `/`）になり、ログインシェルの `PWD` と食い違って、
        // 相対パスの設定の基準がずれる。
        c.current_dir(self.cwd());
        c
    }

    /// server を動かす作業ディレクトリ。環境の解決（ログインシェル）と同じ場所に揃える。
    pub fn cwd(&self) -> PathBuf {
        match self {
            Core::Dir(d) => d.clone(),
            Core::Bundled(_) => {
                std::env::var_os("HOME").map_or_else(|| PathBuf::from("/"), PathBuf::from)
            }
        }
    }

    pub fn dir(&self) -> Option<&Path> {
        match self {
            Core::Dir(d) => Some(d),
            Core::Bundled(_) => None,
        }
    }
}

/// `devCoreDir` があれば同梱より優先する。
pub fn pick_core(dev_dir: Option<PathBuf>, bundled: Option<PathBuf>) -> Option<Core> {
    dev_dir.map(Core::Dir).or(bundled.map(Core::Bundled))
}

/// `.app` の `Contents/MacOS` には実行ファイルと並んで置かれる（開発ビルドでは `target/<profile>/`）。
/// hook が別の cwd で読むので、絶対パスにして返す。
pub fn bundled_bin() -> Option<PathBuf> {
    let bin = std::env::current_exe()
        .ok()?
        .parent()?
        .join("chatter-agent");
    let bin = fs::canonicalize(bin).ok()?;
    bin.is_file().then_some(bin)
}

/// server が使うランタイムルート（`core/src/core/paths.ts` と同じ解決）。
pub fn runtime_root(env: &Env) -> Option<PathBuf> {
    let get = |k: &str| env.get(k).filter(|v| !v.is_empty());
    if cfg!(windows) {
        return get("APPDATA").map(|d| Path::new(d).join("chatter-agent"));
    }
    let base = match get("XDG_CONFIG_HOME") {
        Some(x) => PathBuf::from(x),
        None => Path::new(get("HOME")?).join(".config"),
    };
    Some(base.join("chatter-agent"))
}

/// hook が起こす実行ファイルの絶対パスを、ランタイムルートの `bin-path` に1行で置く
/// （読み手は `plugin/scripts/_lib.sh` の `chatter_spawn_cli`）。`None` は消す。
/// 開発用の core を使うときに、同梱版の CLI が hook から呼ばれて混ざるのを避ける。
pub fn write_bin_path(root: &Path, bin: Option<&Path>) -> std::io::Result<()> {
    let path = root.join("bin-path");
    let Some(bin) = bin else {
        return match fs::remove_file(&path) {
            Err(e) if e.kind() != std::io::ErrorKind::NotFound => Err(e),
            _ => Ok(()),
        };
    };
    fs::create_dir_all(root)?;
    let tmp = root.join("bin-path.tmp");
    fs::write(&tmp, format!("{}\n", bin.display()))?;
    fs::rename(tmp, path)
}

/// pid が生きているか。権限が無くて確認できない場合は「生きている」に倒す
/// （`core/src/core/lock.ts` の判定と同じ）。
#[cfg(unix)]
pub fn pid_alive(pid: u32) -> bool {
    // pid 0 や負数は「プロセスグループ宛て」になるので、実在しうる pid だけを通す。
    if pid == 0 || pid > i32::MAX as u32 {
        return false;
    }
    // SAFETY: シグナル 0 は存在確認だけで、何も送らない。
    unsafe {
        libc::kill(pid as i32, 0) == 0
            || std::io::Error::last_os_error().raw_os_error() == Some(libc::EPERM)
    }
}

// ponytail: 非 unix では外部 server を検知しない。Windows 対応時に OpenProcess で判定する。
#[cfg(not(unix))]
pub fn pid_alive(_pid: u32) -> bool {
    false
}

/// ロックの所有者として記録された pid。読めない・壊れているときは None。
pub fn lock_owner_pid(root: &Path) -> Option<u32> {
    let text = fs::read_to_string(root.join("server.lock").join("owner.json")).ok()?;
    let v: serde_json::Value = serde_json::from_str(&text).ok()?;
    u32::try_from(v.get("pid")?.as_u64()?).ok()
}

/// 自分以外が動かしている server の pid。
pub fn external_server_pid(root: &Path) -> Option<u32> {
    lock_owner_pid(root).filter(|pid| pid_alive(*pid))
}

/// 動かすのに要る成果物が core に揃っているか。
pub fn check_core(core: &Path) -> Result<(), Fail> {
    let dist = core.join("dist");
    if !dist.join("chatter-agent-server.mjs").is_file()
        || !dist.join("chatter-agent-player.mjs").is_file()
    {
        return Err(Fail::NoDist);
    }
    if !core.join("node_modules").is_dir() {
        return Err(Fail::NoNodeModules);
    }
    Ok(())
}

/// ログが上限を超えていたら `.1` へ1世代だけ退避する（既存の `.1` は置き換える）。
// ponytail: 回すのは spawn 時だけ。1回の実行が長いと上限を超えうる。
pub fn rotate_log(path: &Path, max_bytes: u64) -> std::io::Result<()> {
    match fs::metadata(path) {
        Ok(m) if m.len() > max_bytes => {
            let mut old = path.as_os_str().to_owned();
            old.push(".1");
            fs::rename(path, old)
        }
        _ => Ok(()),
    }
}

/// 環境変数の解決に使うログインシェルの呼び出し。`-i` は mise の shim が interactive な rc 経由でしか
/// PATH に載らないため。
#[cfg(unix)]
const ENV_SCRIPT: &str = "printf '\\0__CHATTER_AGENT_ENV__\\0'; exec /usr/bin/env -0";
const ENV_TIMEOUT: Duration = Duration::from_secs(10);
/// server の終了処理の上限（`SHUTDOWN_TIMEOUT_MS`）より長く待つ。
pub(crate) const STOP_TIMEOUT: Duration = Duration::from_secs(10);
const MONITOR_INTERVAL: Duration = Duration::from_secs(2);
const STDERR_TAIL_BYTES: usize = 500;
/// 子が終わったあと、パイプの EOF を待つ猶予。子孫がパイプを握っていると EOF は来ない。
const PIPE_GRACE: Duration = Duration::from_millis(300);

pub(crate) fn lock<T>(m: &Mutex<T>) -> MutexGuard<'_, T> {
    m.lock().unwrap_or_else(|e| e.into_inner())
}

/// パイプを読むたびに共有バッファへ足す。rc が起こしたプロセスがパイプを握ったままだと EOF が
/// 来ないので、読めた分をいつでも取り出せるようにしてある。
#[cfg(unix)]
struct Drain {
    buf: Arc<Mutex<Vec<u8>>>,
    eof: mpsc::Receiver<()>,
}

#[cfg(unix)]
impl Drain {
    fn new<R: Read + Send + 'static>(r: Option<R>) -> Self {
        let buf = Arc::new(Mutex::new(Vec::new()));
        let (tx, eof) = mpsc::channel();
        let shared = Arc::clone(&buf);
        thread::spawn(move || {
            if let Some(mut r) = r {
                let mut chunk = [0u8; 4096];
                while let Ok(n) = r.read(&mut chunk) {
                    if n == 0 {
                        break;
                    }
                    lock(&shared).extend_from_slice(&chunk[..n]);
                }
            }
            let _ = tx.send(());
        });
        Drain { buf, eof }
    }

    /// EOF を最大 `wait` 待ってから、読めた分を返す。
    fn take(&self, wait: Duration) -> Vec<u8> {
        let _ = self.eof.recv_timeout(wait);
        lock(&self.buf).clone()
    }

    fn stderr_tail(&self) -> String {
        let bytes = self.take(PIPE_GRACE);
        let tail = &bytes[bytes.len().saturating_sub(STDERR_TAIL_BYTES)..];
        String::from_utf8_lossy(tail).trim().to_string()
    }
}

/// 端末から起動したときと同じ環境を、ログインシェルから得る。失敗の詳細（シェルの stderr の末尾など）を返す。
#[cfg(unix)]
pub fn resolve_login_env(cwd: &Path) -> Result<Env, String> {
    let shell = std::env::var("SHELL")
        .ok()
        .filter(|s| !s.is_empty())
        .unwrap_or_else(|| "/bin/zsh".into());
    let mut child = Command::new(&shell)
        .args(["-ilc", ENV_SCRIPT])
        .current_dir(cwd)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|e| format!("cannot launch {shell}: {e}"))?;
    let out = Drain::new(child.stdout.take());
    let err = Drain::new(child.stderr.take());
    let deadline = Instant::now() + ENV_TIMEOUT;
    loop {
        match child.try_wait() {
            Ok(Some(_)) => break,
            Ok(None) if Instant::now() < deadline => thread::sleep(Duration::from_millis(50)),
            _ => {
                let _ = child.kill();
                let _ = child.wait();
                return Err(format!(
                    "login shell did not finish in time. stderr: {}",
                    err.stderr_tail()
                ));
            }
        }
    }
    parse_env_output(&out.take(PIPE_GRACE)).ok_or_else(|| {
        format!(
            "could not extract the environment. stderr: {}",
            err.stderr_tail()
        )
    })
}

#[cfg(not(unix))]
pub fn resolve_login_env(_cwd: &Path) -> Result<Env, String> {
    Ok(std::env::vars().collect())
}

#[cfg(unix)]
fn request_stop(child: &mut Child) {
    // SAFETY: 自分が起こした子の pid へ SIGTERM を1つ送るだけ。
    unsafe {
        libc::kill(child.id() as i32, libc::SIGTERM);
    }
}

// ponytail: 非 unix では穏当な停止をしていない。server の後始末（エンジンの停止）は走らない。
#[cfg(not(unix))]
fn request_stop(child: &mut Child) {
    let _ = child.kill();
}

pub(crate) fn exit_info(status: &ExitStatus) -> ExitInfo {
    if let Some(code) = status.code() {
        return ExitInfo::Code(code);
    }
    #[cfg(unix)]
    if let Some(sig) = std::os::unix::process::ExitStatusExt::signal(status) {
        return ExitInfo::Signal(sig);
    }
    ExitInfo::Unknown
}

/// ログ用の終了状態。
pub(crate) fn exit_label(info: ExitInfo) -> String {
    match info {
        ExitInfo::Code(c) => format!("exit code {c}"),
        ExitInfo::Signal(s) => format!("signal {s}"),
        ExitInfo::Unknown => "unknown exit status".into(),
    }
}

/// SIGTERM を1回だけ送り、`STOP_TIMEOUT` 待っても終わらなければ kill する。穏当に終わったら true。
pub(crate) fn stop_child(child: &mut Child) -> bool {
    request_stop(child);
    let deadline = Instant::now() + STOP_TIMEOUT;
    while Instant::now() < deadline {
        if matches!(child.try_wait(), Ok(Some(_))) {
            return true;
        }
        thread::sleep(Duration::from_millis(100));
    }
    let _ = child.kill();
    let _ = child.wait();
    false
}

/// 自分が起こした server が終わったときの状態。
/// 0 は外から止められた正常終了。起動の直前に別の server がロックを取っていると、子は 1 で終わる。
pub fn status_after_exit(info: ExitInfo, owner: Option<u32>) -> Status {
    match (info, owner) {
        (ExitInfo::Code(0), _) => Status::Stopped,
        (ExitInfo::Code(1), Some(owner)) => Status::External(owner),
        _ => Status::Crashed(info),
    }
}

struct Prepared {
    core: Core,
    env: Env,
}

enum Poll {
    Exited(u32, ExitStatus),
    Alive,
    NoChild,
}

/// 処理を背景で1本ずつ流す。処理中に来た要求は捨てず、今の処理が終わったあとに1回だけ流し直す。
#[derive(Default)]
pub(crate) struct Serial {
    pub(crate) busy: AtomicBool,
    pending: AtomicBool,
}

impl Serial {
    pub(crate) fn request(self: &Arc<Self>, job: impl Fn() + Send + 'static) {
        self.pending.store(true, Ordering::SeqCst);
        if self.busy.swap(true, Ordering::SeqCst) {
            return;
        }
        let s = Arc::clone(self);
        thread::spawn(move || loop {
            while s.pending.swap(false, Ordering::SeqCst) {
                job();
            }
            s.busy.store(false, Ordering::SeqCst);
            // busy を下ろす直前に来た要求を拾う。
            if !s.pending.load(Ordering::SeqCst) || s.busy.swap(true, Ordering::SeqCst) {
                break;
            }
        });
    }
}

struct Inner {
    child: Mutex<Option<Child>>,
    status: Mutex<Status>,
    serial: Arc<Serial>,
    core: Option<Core>,
    /// 直近に解決した環境。外部 server の検知でランタイムルートを求めるのに使う。
    env: Mutex<Option<Env>>,
    log_path: PathBuf,
    on_change: Box<dyn Fn(&Status) + Send + Sync>,
}

/// server の起動・停止・監視。起動・停止は呼び出しスレッドを塞がない。
#[derive(Clone)]
pub struct Manager(Arc<Inner>);

impl Manager {
    pub fn new(
        log_path: PathBuf,
        core: Option<Core>,
        on_change: impl Fn(&Status) + Send + Sync + 'static,
    ) -> Self {
        Manager(Arc::new(Inner {
            child: Mutex::new(None),
            status: Mutex::new(Status::Stopped),
            serial: Arc::default(),
            core,
            env: Mutex::new(None),
            log_path,
            on_change: Box::new(on_change),
        }))
    }

    pub fn status(&self) -> Status {
        lock(&self.0.status).clone()
    }

    pub fn set_status(&self, status: Status) {
        {
            let mut cur = lock(&self.0.status);
            if *cur == status {
                return;
            }
            *cur = status.clone();
        }
        (self.0.on_change)(&status);
    }

    /// `server.log` に ChatterAgent 自身の出来事を1行足す。
    pub(crate) fn log(&self, msg: &str) {
        let line = format!(
            "[Agent] {} {msg}\n",
            chrono::Local::now().format("%Y-%m-%d %H:%M:%S")
        );
        if let Ok(mut f) = self.open_log() {
            let _ = f.write_all(line.as_bytes());
        }
    }

    /// ログを追記で開く。ディレクトリが無ければ作る。
    pub(crate) fn open_log(&self) -> std::io::Result<fs::File> {
        if let Some(dir) = self.0.log_path.parent() {
            fs::create_dir_all(dir)?;
        }
        OpenOptions::new()
            .create(true)
            .append(true)
            .open(&self.0.log_path)
    }

    /// 子が居れば止めてから起こす。処理中なら、今の処理が終わってから1回だけ流し直す。
    pub fn start_or_restart(&self) {
        let m = self.clone();
        self.0.serial.request(move || {
            m.do_stop();
            m.do_start();
        });
    }

    /// 自分の子を止めてから `then` を呼ぶ。処理中なら終わるのを待つ。
    pub fn quit(&self, then: impl FnOnce() + Send + 'static) {
        let m = self.clone();
        thread::spawn(move || {
            while m.0.serial.busy.swap(true, Ordering::SeqCst) {
                thread::sleep(Duration::from_millis(100));
            }
            m.set_status(Status::Stopping);
            m.do_stop();
            m.0.serial.busy.store(false, Ordering::SeqCst);
            then();
        });
    }

    /// メニュー以外からの終了の保険。子が残っていれば同期で止める。
    pub fn stop_sync(&self) {
        self.do_stop();
    }

    fn prepare(&self) -> Result<Prepared, Status> {
        let core = self.core().ok_or(Status::Cannot(Fail::NoCore))?;
        if let Core::Dir(dir) = &core {
            check_core(dir).map_err(Status::Cannot)?;
        }
        let env = resolve_login_env(&core.cwd()).map_err(|detail| {
            self.log(&format!(
                "Cannot get the environment from the login shell: {detail}"
            ));
            Status::Cannot(Fail::Env)
        })?;
        *lock(&self.0.env) = Some(env.clone());
        let root = runtime_root(&env);
        // 外部の server が動いていても、hook は実行ファイルを要る。
        if let Some(root) = &root {
            let bin = match &core {
                Core::Bundled(bin) => Some(bin.as_path()),
                Core::Dir(_) => None,
            };
            if let Err(e) = write_bin_path(root, bin) {
                self.log(&format!("Cannot update bin-path: {e}"));
            }
        }
        if let Some(pid) = root.and_then(|r| external_server_pid(&r)) {
            return Err(Status::External(pid));
        }
        Ok(Prepared { core, env })
    }

    fn do_start(&self) {
        self.set_status(Status::Starting);
        let status = match self.prepare().and_then(|p| self.spawn(p)) {
            Ok(pid) => Status::Running(pid),
            Err(status) => {
                self.log(&format!("Not starting. {}", text::EN.status(&status)));
                status
            }
        };
        self.set_status(status);
    }

    fn spawn(&self, p: Prepared) -> Result<u32, Status> {
        let fail = |e: std::io::Error| Status::Cannot(Fail::SpawnFailed(e.to_string()));
        let _ = rotate_log(&self.0.log_path, LOG_MAX_BYTES);
        let log = self.open_log().map_err(fail)?;
        let log_err = log.try_clone().map_err(fail)?;
        // ★ stdout / stderr はログの fd を直接渡す。パイプで中継すると、ChatterAgent が先に死んだとき
        //   server が EPIPE で壊れる。
        // ★ プロセスグループは分けない。端末で tauri dev を Ctrl-C したとき、SIGINT が server にも届いて
        //   server が自分で後始末して終わる。
        let bundled = matches!(p.core, Core::Bundled(_));
        let child = p
            .core
            .command("server")
            .envs(&p.env)
            .stdin(Stdio::null())
            .stdout(log)
            .stderr(log_err)
            .spawn()
            .map_err(|e| {
                Status::Cannot(match e.kind() {
                    std::io::ErrorKind::NotFound if !bundled => Fail::NodeMissing,
                    _ => Fail::SpawnFailed(e.to_string()),
                })
            })?;
        let pid = child.id();
        self.log(&format!("Started the server: pid={pid} {:?}", p.core));
        *lock(&self.0.child) = Some(child);
        Ok(pid)
    }

    fn do_stop(&self) {
        // ★ 子を take してから止める。SIGTERM の2回目は server が後始末を飛ばして終わるので、
        //   二重に送らないことを構造で保証する。
        let Some(mut child) = lock(&self.0.child).take() else {
            return;
        };
        let pid = child.id();
        self.set_status(Status::Stopping);
        self.log(&format!("Requested the server to stop: pid={pid}"));
        if !stop_child(&mut child) {
            self.log("Server did not stop, so it was killed. The TTS engine may still be running");
        }
        self.log(&format!("Server stopped: pid={pid}"));
        self.set_status(Status::Stopped);
    }

    fn poll_child(&self) -> Poll {
        let mut guard = lock(&self.0.child);
        let Some(child) = guard.as_mut() else {
            return Poll::NoChild;
        };
        match child.try_wait() {
            Ok(Some(st)) => {
                let pid = child.id();
                *guard = None;
                Poll::Exited(pid, st)
            }
            _ => Poll::Alive,
        }
    }

    /// 解決済みの環境。まだなら None（`env()` と違い、ChatterAgent 自身の環境で代用しない）。
    pub fn resolved_env(&self) -> Option<Env> {
        lock(&self.0.env).clone()
    }

    /// 起動時に決めた core。
    pub fn core(&self) -> Option<Core> {
        self.0.core.clone()
    }

    /// server に渡している環境。まだ解決していなければ ChatterAgent 自身の環境。
    pub fn env(&self) -> Env {
        lock(&self.0.env)
            .clone()
            .unwrap_or_else(|| std::env::vars().collect())
    }

    fn current_root(&self) -> Option<PathBuf> {
        runtime_root(&self.env())
    }

    /// 監視の1回分。予期しない終了と、外で動いている server の出入りを状態へ反映する。
    pub fn tick(&self) {
        match self.poll_child() {
            Poll::Alive => {}
            Poll::Exited(pid, st) => {
                let info = exit_info(&st);
                let owner = self.current_root().and_then(|r| external_server_pid(&r));
                let status = status_after_exit(info, owner);
                let how = exit_label(info);
                self.log(&format!(
                    "Server exited: pid={pid} {how} → {}",
                    text::EN.status(&status)
                ));
                self.set_status(status);
            }
            Poll::NoChild => {
                if self.0.serial.busy.load(Ordering::SeqCst) {
                    return;
                }
                let owner = self.current_root().and_then(|r| external_server_pid(&r));
                match (owner, self.status()) {
                    (Some(pid), _) => self.set_status(Status::External(pid)),
                    (None, Status::External(_)) => self.set_status(Status::Stopped),
                    _ => {}
                }
            }
        }
    }

    /// 監視スレッドを起こす。
    pub fn spawn_monitor(&self) {
        let m = self.clone();
        thread::spawn(move || loop {
            thread::sleep(MONITOR_INTERVAL);
            m.tick();
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::process::Command;

    fn env_of(pairs: &[(&str, &str)]) -> Env {
        pairs
            .iter()
            .map(|(k, v)| (k.to_string(), v.to_string()))
            .collect()
    }

    fn tmp(name: &str) -> PathBuf {
        let d =
            std::env::temp_dir().join(format!("chatter-agent-app-{}-{name}", std::process::id()));
        let _ = fs::remove_dir_all(&d);
        fs::create_dir_all(&d).unwrap();
        d
    }

    #[test]
    fn bin_path_is_one_line_and_replaced_atomically() {
        let root = tmp("bin-path");
        write_bin_path(&root, Some(Path::new("/a/chatter-agent"))).unwrap();
        write_bin_path(&root, Some(Path::new("/b/chatter-agent"))).unwrap();
        assert_eq!(
            fs::read_to_string(root.join("bin-path")).unwrap(),
            "/b/chatter-agent\n"
        );
        assert!(!root.join("bin-path.tmp").exists());
        write_bin_path(&root, None).unwrap();
        assert!(!root.join("bin-path").exists());
        write_bin_path(&root, None).unwrap();
    }

    #[test]
    fn env_output_ignores_garbage_before_marker() {
        let out =
            b"rc noise\0A=1\0\0__CHATTER_AGENT_ENV__\0PATH=/a:/b\0MULTI=x=y\nz\0EMPTY=\0junk\0";
        let env = parse_env_output(out).unwrap();
        assert_eq!(env.get("PATH").unwrap(), "/a:/b");
        assert_eq!(env.get("MULTI").unwrap(), "x=y\nz");
        assert_eq!(env.get("EMPTY").unwrap(), "");
        assert!(!env.contains_key("A") && !env.contains_key("junk"));
    }

    #[test]
    fn env_output_without_marker_fails() {
        assert!(parse_env_output(b"PATH=/a\0").is_none());
    }

    #[test]
    fn dev_core_dir_beats_bundled() {
        let (d, b) = (PathBuf::from("/d"), PathBuf::from("/b"));
        assert_eq!(
            pick_core(Some(d.clone()), Some(b.clone())),
            Some(Core::Dir(d.clone()))
        );
        assert_eq!(pick_core(None, Some(b.clone())), Some(Core::Bundled(b)));
        assert_eq!(pick_core(Some(d.clone()), None), Some(Core::Dir(d)));
        assert_eq!(pick_core(None, None), None);
    }

    #[cfg(unix)]
    #[test]
    fn runtime_root_prefers_xdg() {
        let with = env_of(&[("XDG_CONFIG_HOME", "/x"), ("HOME", "/h")]);
        assert_eq!(runtime_root(&with), Some(PathBuf::from("/x/chatter-agent")));
        let without = env_of(&[("HOME", "/h")]);
        assert_eq!(
            runtime_root(&without),
            Some(PathBuf::from("/h/.config/chatter-agent"))
        );
        let empty_xdg = env_of(&[("XDG_CONFIG_HOME", ""), ("HOME", "/h")]);
        assert_eq!(
            runtime_root(&empty_xdg),
            Some(PathBuf::from("/h/.config/chatter-agent"))
        );
        assert_eq!(runtime_root(&Env::new()), None);
    }

    #[cfg(unix)]
    #[test]
    fn owner_pid_liveness() {
        let root = tmp("owner");
        let lock = root.join("server.lock");
        fs::create_dir_all(&lock).unwrap();
        let write = |s: &str| fs::write(lock.join("owner.json"), s).unwrap();

        write(&format!(
            "{{\"pid\":{},\"token\":\"t\"}}",
            std::process::id()
        ));
        assert_eq!(external_server_pid(&root), Some(std::process::id()));

        let mut child = Command::new("true").spawn().unwrap();
        let dead = child.id();
        child.wait().unwrap();
        write(&format!("{{\"pid\":{dead},\"token\":\"t\"}}"));
        assert_eq!(external_server_pid(&root), None);

        write("{broken");
        assert_eq!(external_server_pid(&root), None);
        fs::remove_file(lock.join("owner.json")).unwrap();
        assert_eq!(external_server_pid(&root), None);
        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn exit_status_mapping() {
        let owner = Some(42);
        assert_eq!(status_after_exit(ExitInfo::Code(0), owner), Status::Stopped);
        assert_eq!(
            status_after_exit(ExitInfo::Code(1), owner),
            Status::External(42)
        );
        assert_eq!(
            status_after_exit(ExitInfo::Code(1), None),
            Status::Crashed(ExitInfo::Code(1))
        );
        assert_eq!(
            status_after_exit(ExitInfo::Signal(9), owner),
            Status::Crashed(ExitInfo::Signal(9))
        );
    }

    #[cfg(unix)]
    #[test]
    fn drain_returns_output_even_if_descendant_holds_pipe() {
        let mut child = Command::new("/bin/sh")
            .args(["-c", "printf abc; sleep 5 &"])
            .stdout(Stdio::piped())
            .spawn()
            .unwrap();
        let drain = Drain::new(child.stdout.take());
        child.wait().unwrap();
        let started = Instant::now();
        assert_eq!(drain.take(Duration::from_millis(300)), b"abc");
        assert!(started.elapsed() < Duration::from_secs(2));
    }

    #[test]
    fn command_runs_in_cwd() {
        let dir = PathBuf::from("/some/core");
        assert_eq!(Core::Dir(dir.clone()).cwd(), dir);
        let home = std::env::var_os("HOME").map_or_else(|| PathBuf::from("/"), PathBuf::from);
        assert_eq!(Core::Bundled("/bin/x".into()).cwd(), home);
        for core in [Core::Dir(dir), Core::Bundled("/bin/x".into())] {
            let c = core.command("server");
            assert_eq!(c.get_current_dir(), Some(core.cwd().as_path()));
        }
    }

    #[test]
    fn log_creates_missing_directory() {
        let dir = tmp("log");
        let path = dir.join("sub").join("server.log");
        let m = Manager::new(path.clone(), None, |_| {});
        m.log("hello");
        assert!(fs::read_to_string(&path).unwrap().contains("hello"));
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn serial_reruns_once_for_requests_during_job() {
        let runs = Arc::new(std::sync::atomic::AtomicUsize::new(0));
        let serial = Arc::new(Serial::default());
        let r = Arc::clone(&runs);
        let job = move || {
            thread::sleep(Duration::from_millis(200));
            r.fetch_add(1, Ordering::SeqCst);
        };
        serial.request(job.clone());
        thread::sleep(Duration::from_millis(50));
        serial.request(job.clone());
        serial.request(job);
        while serial.busy.load(Ordering::SeqCst) {
            thread::sleep(Duration::from_millis(20));
        }
        assert_eq!(runs.load(Ordering::SeqCst), 2);
    }

    #[test]
    fn rotate_replaces_existing_backup() {
        let dir = tmp("rotate");
        let log = dir.join("server.log");
        let old = dir.join("server.log.1");
        fs::write(&old, "ancient").unwrap();

        fs::write(&log, "small").unwrap();
        rotate_log(&log, 10).unwrap();
        assert!(log.exists());
        assert_eq!(fs::read_to_string(&old).unwrap(), "ancient");

        fs::write(&log, "0123456789ab").unwrap();
        rotate_log(&log, 10).unwrap();
        assert!(!log.exists());
        assert_eq!(fs::read_to_string(&old).unwrap(), "0123456789ab");
        rotate_log(&log, 10).unwrap();
        let _ = fs::remove_dir_all(&dir);
    }
}
