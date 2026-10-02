//! chatter-agent-server の面倒を見る。環境の解決・事前チェック・起動・停止・監視。

use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};

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
