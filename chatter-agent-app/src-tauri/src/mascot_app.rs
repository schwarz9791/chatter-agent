//! Chatter Mascot（Unity アプリ）を探す・起こす・畳む。macOS 依存部はここに閉じる。

use std::io;
use std::path::{Path, PathBuf};

#[cfg(target_os = "macos")]
use std::process::{Command, Stdio};

#[cfg(target_os = "macos")]
use crate::clients::BACKLOG_MAX_AGE_MS;
#[cfg(target_os = "macos")]
use crate::server::Env;

#[cfg(target_os = "macos")]
const BUNDLE_ID: &str = "tech.sukima.chatter-mascot";
const APP_NAME: &str = "ChatterMascot.app";

/// `.app` の探す場所を優先順に並べる。
pub fn candidates(core: Option<&Path>, home: &Path) -> Vec<PathBuf> {
    let mut v = vec![
        Path::new("/Applications").join(APP_NAME),
        home.join("Applications").join(APP_NAME),
    ];
    if let Some(parent) = core.and_then(Path::parent) {
        v.push(parent.join("chatter-mascot/Build").join(APP_NAME));
    }
    v
}

pub fn find_app(core: Option<&Path>, home: &Path) -> Option<PathBuf> {
    first_dir(candidates(core, home))
}

fn first_dir(paths: impl IntoIterator<Item = PathBuf>) -> Option<PathBuf> {
    paths.into_iter().find(|p| p.is_dir())
}

/// 動いているマスコットの pid。別のワークツリーのビルドも同じ bundle id なので全部拾う
/// （どれが繋がっても二重に鳴る）。
#[cfg(target_os = "macos")]
pub fn running_pids() -> Vec<u32> {
    use objc2_app_kit::NSRunningApplication;
    use objc2_foundation::NSString;
    NSRunningApplication::runningApplicationsWithBundleIdentifier(&NSString::from_str(BUNDLE_ID))
        .iter()
        .filter(|a| !a.isTerminated())
        .filter_map(|a| u32::try_from(a.processIdentifier()).ok())
        .collect()
}

/// 通常の quit を要求する。★ 強制終了はしない（Unity は未送信の ack を投げ切ってから自分で終わる）。
/// 戻り値は当てにならないので、終わったかは pid が消えたことで見る。
#[cfg(target_os = "macos")]
pub fn request_quit(pid: u32) {
    use objc2_app_kit::NSRunningApplication;
    if let Some(app) = NSRunningApplication::runningApplicationWithProcessIdentifier(pid as i32) {
        app.terminate();
    }
}

/// `open` は呼び出し元の環境をアプリへ引き継ぐので、マスコットも server と同じ設定の場所を見る。
#[cfg(target_os = "macos")]
pub fn launch(app: &Path, env: &Env) -> io::Result<()> {
    let status = Command::new("open")
        .arg("-a")
        .arg(app)
        .args(["--args", "-speechBacklogMaxAgeMs"])
        .arg(BACKLOG_MAX_AGE_MS.to_string())
        .envs(env)
        .stdin(Stdio::null())
        .status()?;
    if status.success() {
        Ok(())
    } else {
        Err(io::Error::other(format!("open が失敗した: {status}")))
    }
}

// ponytail: macOS 以外ではマスコットを扱わない。Windows 対応時に実装する。
#[cfg(not(target_os = "macos"))]
pub fn running_pids() -> Vec<u32> {
    Vec::new()
}

#[cfg(not(target_os = "macos"))]
pub fn request_quit(_pid: u32) {}

#[cfg(not(target_os = "macos"))]
pub fn launch(_app: &Path, _env: &crate::server::Env) -> io::Result<()> {
    Err(io::Error::new(
        io::ErrorKind::Unsupported,
        "macOS 以外ではマスコットを起動できない",
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn candidates_in_priority_order() {
        let v = candidates(Some(Path::new("/w/core")), Path::new("/h"));
        assert_eq!(
            v,
            [
                "/Applications/ChatterMascot.app",
                "/h/Applications/ChatterMascot.app",
                "/w/chatter-mascot/Build/ChatterMascot.app",
            ]
            .map(PathBuf::from)
        );
        assert_eq!(candidates(None, Path::new("/h")).len(), 2);
    }

    #[test]
    fn first_existing_directory_wins_and_files_do_not_count() {
        let d = std::env::temp_dir().join(format!("chatter-agent-app-find-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        let (a, b, c) = (d.join("a.app"), d.join("b.app"), d.join("c.app"));
        std::fs::create_dir_all(&d).unwrap();
        std::fs::write(&a, "x").unwrap();
        std::fs::create_dir_all(&b).unwrap();
        std::fs::create_dir_all(&c).unwrap();
        assert_eq!(first_dir([a.clone(), b.clone(), c]), Some(b));
        assert_eq!(first_dir([a]), None);
        let _ = std::fs::remove_dir_all(&d);
    }
}
