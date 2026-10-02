//! chatter-agent-server の面倒を見る。環境の解決・事前チェック・起動・停止・監視。

use std::collections::HashMap;
use std::fs::{self, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, ExitStatus, Stdio};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};
use std::thread;
use std::time::{Duration, Instant};

use crate::text;

pub type Env = HashMap<String, String>;

/// server が要求する node の下限（`core/package.json` の engines と揃える）。
const NODE_MIN: (u32, u32, u32) = (24, 11, 0);
const LOG_MAX_BYTES: u64 = 10 * 1024 * 1024;
const ENV_MARKER: &[u8] = b"\0__CHATTER_AGENT_ENV__\0";

/// 起動できない理由。
#[derive(Clone, Debug, PartialEq)]
pub enum Fail {
    NoCoreDir,
    NoDist,
    NoNodeModules,
    Env,
    NodeMissing,
    NodeUnknown,
    NodeOld(String),
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

/// `node --version` の出力（`v24.19.0`）を解析する。
pub fn parse_node_version(output: &str) -> Option<(u32, u32, u32)> {
    let mut parts = output.trim().strip_prefix('v')?.split('.');
    let v = (
        parts.next()?.parse().ok()?,
        parts.next()?.parse().ok()?,
        parts.next()?.parse().ok()?,
    );
    parts.next().is_none().then_some(v)
}

pub fn node_is_new_enough(v: (u32, u32, u32)) -> bool {
    v >= NODE_MIN
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
    if !core.join("dist").join("chatter-agent-server.mjs").is_file() {
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
/// server の watchdog（6秒）より長く待つ。
const STOP_TIMEOUT: Duration = Duration::from_secs(10);
const MONITOR_INTERVAL: Duration = Duration::from_secs(2);
const STDERR_TAIL_BYTES: usize = 500;

fn lock<T>(m: &Mutex<T>) -> MutexGuard<'_, T> {
    m.lock().unwrap_or_else(|e| e.into_inner())
}

#[cfg(unix)]
fn drain<R: Read + Send + 'static>(r: Option<R>) -> thread::JoinHandle<Vec<u8>> {
    thread::spawn(move || {
        let mut buf = Vec::new();
        if let Some(mut r) = r {
            let _ = r.read_to_end(&mut buf);
        }
        buf
    })
}

/// 端末から起動したときと同じ環境を、ログインシェルから得る。失敗の詳細（シェルの stderr の末尾など）を返す。
#[cfg(unix)]
pub fn resolve_login_env(core: &Path) -> Result<Env, String> {
    let shell = std::env::var("SHELL")
        .ok()
        .filter(|s| !s.is_empty())
        .unwrap_or_else(|| "/bin/zsh".into());
    let mut child = Command::new(&shell)
        .args(["-ilc", ENV_SCRIPT])
        .current_dir(core)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|e| format!("{shell} を起動できない: {e}"))?;
    let out = drain(child.stdout.take());
    let err = drain(child.stderr.take());
    let deadline = Instant::now() + ENV_TIMEOUT;
    loop {
        match child.try_wait() {
            Ok(Some(_)) => break,
            Ok(None) if Instant::now() < deadline => thread::sleep(Duration::from_millis(50)),
            _ => {
                let _ = child.kill();
                let _ = child.wait();
                return Err("ログインシェルが時間内に終わらなかった".into());
            }
        }
    }
    let stdout = out.join().unwrap_or_default();
    parse_env_output(&stdout).ok_or_else(|| {
        let stderr = err.join().unwrap_or_default();
        let tail = &stderr[stderr.len().saturating_sub(STDERR_TAIL_BYTES)..];
        format!(
            "環境を取り出せなかった。stderr: {}",
            String::from_utf8_lossy(tail).trim()
        )
    })
}

#[cfg(not(unix))]
pub fn resolve_login_env(_core: &Path) -> Result<Env, String> {
    Ok(std::env::vars().collect())
}

fn node_version(core: &Path, env: &Env) -> Result<(u32, u32, u32), Fail> {
    let out = Command::new("node")
        .arg("--version")
        .current_dir(core)
        .envs(env)
        .stdin(Stdio::null())
        .output()
        .map_err(|e| match e.kind() {
            std::io::ErrorKind::NotFound => Fail::NodeMissing,
            _ => Fail::SpawnFailed(e.to_string()),
        })?;
    parse_node_version(&String::from_utf8_lossy(&out.stdout)).ok_or(Fail::NodeUnknown)
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

fn exit_info(status: &ExitStatus) -> ExitInfo {
    if let Some(code) = status.code() {
        return ExitInfo::Code(code);
    }
    #[cfg(unix)]
    if let Some(sig) = std::os::unix::process::ExitStatusExt::signal(status) {
        return ExitInfo::Signal(sig);
    }
    ExitInfo::Unknown
}

struct Prepared {
    core: PathBuf,
    env: Env,
    node: (u32, u32, u32),
}

enum Poll {
    Exited(u32, ExitStatus),
    Alive,
    NoChild,
}

struct Inner {
    child: Mutex<Option<Child>>,
    status: Mutex<Status>,
    busy: AtomicBool,
    core_dir: Mutex<Option<PathBuf>>,
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
        core_dir: Option<PathBuf>,
        on_change: impl Fn(&Status) + Send + Sync + 'static,
    ) -> Self {
        Manager(Arc::new(Inner {
            child: Mutex::new(None),
            status: Mutex::new(Status::Stopped),
            busy: AtomicBool::new(false),
            core_dir: Mutex::new(core_dir),
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

    /// 自分の子が動いているか。
    pub fn is_running(&self) -> bool {
        matches!(self.status(), Status::Running(_))
    }

    /// `server.log` に ChatterAgent 自身の出来事を1行足す。
    fn log(&self, msg: &str) {
        let line = format!(
            "[Agent] {} {msg}\n",
            chrono::Local::now().format("%Y-%m-%d %H:%M:%S")
        );
        if let Ok(mut f) = OpenOptions::new()
            .create(true)
            .append(true)
            .open(&self.0.log_path)
        {
            let _ = f.write_all(line.as_bytes());
        }
    }

    /// 処理中でなければ背景スレッドで走らせる。起動・停止の二重実行を防ぐ。
    fn run_bg(&self, f: impl FnOnce(&Manager) + Send + 'static) {
        if self.0.busy.swap(true, Ordering::SeqCst) {
            return;
        }
        let m = self.clone();
        thread::spawn(move || {
            f(&m);
            m.0.busy.store(false, Ordering::SeqCst);
        });
    }

    pub fn start(&self) {
        self.run_bg(|m| m.do_start());
    }

    pub fn restart(&self) {
        self.run_bg(|m| {
            m.do_stop();
            m.do_start();
        });
    }

    /// 自分の子を止めてから `then` を呼ぶ。処理中なら終わるのを待つ。
    pub fn quit(&self, then: impl FnOnce() + Send + 'static) {
        let m = self.clone();
        thread::spawn(move || {
            while m.0.busy.swap(true, Ordering::SeqCst) {
                thread::sleep(Duration::from_millis(100));
            }
            m.set_status(Status::Stopping);
            m.do_stop();
            m.0.busy.store(false, Ordering::SeqCst);
            then();
        });
    }

    /// メニュー以外からの終了の保険。子が残っていれば同期で止める。
    pub fn stop_sync(&self) {
        self.do_stop();
    }

    fn prepare(&self) -> Result<Prepared, Status> {
        let core = lock(&self.0.core_dir)
            .clone()
            .ok_or(Status::Cannot(Fail::NoCoreDir))?;
        check_core(&core).map_err(Status::Cannot)?;
        let env = resolve_login_env(&core).map_err(|detail| {
            self.log(&format!("ログインシェルから環境を取得できない: {detail}"));
            Status::Cannot(Fail::Env)
        })?;
        *lock(&self.0.env) = Some(env.clone());
        let node = node_version(&core, &env).map_err(Status::Cannot)?;
        if !node_is_new_enough(node) {
            let v = format!("v{}.{}.{}", node.0, node.1, node.2);
            return Err(Status::Cannot(Fail::NodeOld(v)));
        }
        if let Some(pid) = runtime_root(&env).and_then(|r| external_server_pid(&r)) {
            return Err(Status::External(pid));
        }
        Ok(Prepared { core, env, node })
    }

    fn do_start(&self) {
        self.set_status(Status::Starting);
        let status = match self.prepare().and_then(|p| self.spawn(p)) {
            Ok(pid) => Status::Running(pid),
            Err(status) => {
                self.log(&format!("起動しない: {}", text::JA.status(&status)));
                status
            }
        };
        self.set_status(status);
    }

    fn spawn(&self, p: Prepared) -> Result<u32, Status> {
        let fail = |e: std::io::Error| Status::Cannot(Fail::SpawnFailed(e.to_string()));
        if let Some(dir) = self.0.log_path.parent() {
            fs::create_dir_all(dir).map_err(fail)?;
        }
        let _ = rotate_log(&self.0.log_path, LOG_MAX_BYTES);
        let log = OpenOptions::new()
            .create(true)
            .append(true)
            .open(&self.0.log_path)
            .map_err(fail)?;
        let log_err = log.try_clone().map_err(fail)?;
        // ★ stdout / stderr はログの fd を直接渡す。パイプで中継すると、ChatterAgent が先に死んだとき
        //   server が EPIPE で壊れる。
        // ★ プロセスグループは分けない。端末で tauri dev を Ctrl-C したとき、SIGINT が server にも届いて
        //   server が自分で後始末して終わる。
        let child = Command::new("node")
            .arg("dist/chatter-agent-server.mjs")
            .current_dir(&p.core)
            .envs(&p.env)
            .stdin(Stdio::null())
            .stdout(log)
            .stderr(log_err)
            .spawn()
            .map_err(|e| {
                Status::Cannot(match e.kind() {
                    std::io::ErrorKind::NotFound => Fail::NodeMissing,
                    _ => Fail::SpawnFailed(e.to_string()),
                })
            })?;
        let pid = child.id();
        self.log(&format!(
            "起動した: pid={pid} node=v{}.{}.{} core={}",
            p.node.0,
            p.node.1,
            p.node.2,
            p.core.display()
        ));
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
        self.log(&format!("停止を要求した: pid={pid}"));
        request_stop(&mut child);
        let deadline = Instant::now() + STOP_TIMEOUT;
        let mut exited = false;
        while Instant::now() < deadline {
            if matches!(child.try_wait(), Ok(Some(_))) {
                exited = true;
                break;
            }
            thread::sleep(Duration::from_millis(100));
        }
        if !exited {
            let _ = child.kill();
            let _ = child.wait();
            self.log("止まらなかったので強制終了した。合成エンジンが残っている可能性がある");
        }
        self.log(&format!("停止を完了した: pid={pid}"));
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

    fn current_root(&self) -> Option<PathBuf> {
        match lock(&self.0.env).as_ref() {
            Some(env) => runtime_root(env),
            None => runtime_root(&std::env::vars().collect()),
        }
    }

    /// 監視の1回分。予期しない終了と、外で動いている server の出入りを状態へ反映する。
    pub fn tick(&self) {
        match self.poll_child() {
            Poll::Alive => {}
            Poll::Exited(pid, st) => {
                let info = exit_info(&st);
                self.log(&format!("予期しない終了: pid={pid} {info:?}"));
                // 起動の直前に別の server がロックを取っていると、子は exit 1 で終わる。
                let owner = self.current_root().and_then(|r| external_server_pid(&r));
                self.set_status(match owner {
                    Some(owner) if info == ExitInfo::Code(1) => Status::External(owner),
                    _ => Status::Crashed(info),
                });
            }
            Poll::NoChild => {
                if self.0.busy.load(Ordering::SeqCst) {
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
    fn node_version_threshold() {
        for (s, ok) in [
            ("v24.10.9", false),
            ("v24.11.0", true),
            ("v25.0.0", true),
            ("v24.19.0\n", true),
        ] {
            assert_eq!(
                node_is_new_enough(parse_node_version(s).unwrap()),
                ok,
                "{s}"
            );
        }
        for s in ["", "24.11.0", "v24.11", "v24.x.0", "v24.11.0.1", "garbage"] {
            assert!(parse_node_version(s).is_none(), "{s}");
        }
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
