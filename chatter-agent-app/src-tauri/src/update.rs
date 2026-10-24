//! 新しい版の検知と、ChatterAgent・マスコットの入れ替え。
//!
//! ★ 検知は Releases の一覧を1回引き、タグの接頭辞ごとに semver 最大を取る。`releases/latest` は
//!   後から publish した系統に移るので使わない。
//! ★ 版に pre-release 部（`0.0.0-dev`）があれば手元ビルドとみなし、検知も更新もしない。

use std::collections::BTreeMap;
use std::io;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::thread;
use std::time::{Duration, SystemTime};

use base64::engine::general_purpose::STANDARD;
use base64::Engine as _;
use minisign_verify::{PublicKey, Signature};
use semver::Version;
use serde::Serialize;
use serde_json::Value;
use tauri::menu::MenuItem;
use tauri::{AppHandle, Manager as _, Wry};
use tauri_plugin_dialog::{DialogExt as _, MessageDialogButtons, MessageDialogKind};
use tauri_plugin_updater::UpdaterExt as _;

use crate::clients::Clients;
use crate::mascot_app::{bundle_version, home_of, running_pids, update_target};
use crate::server::{lock, Manager};
use crate::text;
use crate::Settings;

const REPO: &str = "schwarz9791/chatter-agent";
const AGENT_PREFIX: &str = "agent-v";
const MASCOT_PREFIX: &str = "mascot-v";
const LATEST_JSON: &str = "latest.json";
const MASCOT_ZIP: &str = "ChatterMascot-macOS.zip";
const MASCOT_SIG: &str = "ChatterMascot-macOS.zip.sig";
/// 自動確認の間隔と、それを見に起きる間隔。スリープで `sleep` が進まない分は、壁時計で測ることで補う。
const CHECK_INTERVAL: Duration = Duration::from_secs(6 * 60 * 60);
const WAKE_INTERVAL: Duration = Duration::from_secs(30 * 60);
const CONNECT_TIMEOUT: Duration = Duration::from_secs(15);
const READ_TIMEOUT: Duration = Duration::from_secs(60);

/// 見つかった Release。`assets` は名前 → ダウンロード URL。
#[derive(Clone, Debug, PartialEq)]
pub struct Found {
    pub version: Version,
    assets: BTreeMap<String, String>,
}

/// `prefix` の系統で最新の公開 Release。draft・prerelease・版を読めないタグ・semver の
/// プレリリース版は数えない。更新に要る `required` のアセットが揃っていない Release も数えない。
fn newest(releases: &Value, prefix: &str, required: &[&str]) -> Option<Found> {
    releases
        .as_array()?
        .iter()
        .filter(|r| r["draft"] != true && r["prerelease"] != true)
        .filter_map(|r| {
            let version = Version::parse(r["tag_name"].as_str()?.strip_prefix(prefix)?).ok()?;
            if is_dev(&version) {
                return None;
            }
            let assets: BTreeMap<String, String> = r["assets"]
                .as_array()?
                .iter()
                .filter_map(|a| {
                    Some((
                        a["name"].as_str()?.to_string(),
                        a["browser_download_url"].as_str()?.to_string(),
                    ))
                })
                .collect();
            required
                .iter()
                .all(|name| assets.contains_key(*name))
                .then_some(Found { version, assets })
        })
        .max_by(|a, b| a.version.cmp(&b.version))
}

fn is_dev(v: &Version) -> bool {
    !v.pre.is_empty()
}

#[derive(Default)]
struct State {
    agent: Option<Found>,
    mascot: Option<Found>,
    /// 最後に確認できた時刻。失敗は数えない。
    last_ok: Option<SystemTime>,
    /// 確認からダウンロード・入れ替えまでの全体に掛ける。
    busy: Option<Phase>,
}

/// 処理中の段階。メニューの文言を分ける。
#[derive(Clone, Copy, PartialEq)]
enum Phase {
    Checking,
    Updating,
}

pub struct Updates {
    state: Mutex<State>,
    item: MenuItem<Wry>,
    settings_path: PathBuf,
    /// ChatterAgent 自身が手元ビルド。
    dev: bool,
}

/// 処理中の印。落とすとメニューを戻す（再起動へ進んだときは `keep` で戻さない）。
struct Busy<'a> {
    updates: &'a Updates,
    keep: bool,
}

impl Busy<'_> {
    /// 確認が終わって入れ替えに進んだことを、メニューに出す。
    fn updating(&self) {
        lock(&self.updates.state).busy = Some(Phase::Updating);
        self.updates.refresh_menu();
    }
}

impl Drop for Busy<'_> {
    fn drop(&mut self) {
        if !self.keep {
            lock(&self.updates.state).busy = None;
            self.updates.refresh_menu();
        }
    }
}

impl Updates {
    pub fn new(app: &AppHandle, item: MenuItem<Wry>, settings_path: PathBuf) -> Self {
        let dev = is_dev(&app.package_info().version);
        let u = Updates {
            state: Mutex::default(),
            item,
            settings_path,
            dev,
        };
        u.refresh_menu();
        u
    }

    fn begin(&self) -> Option<Busy<'_>> {
        if self.dev || lock(&self.state).busy.replace(Phase::Checking).is_some() {
            return None;
        }
        self.refresh_menu();
        Some(Busy {
            updates: self,
            keep: false,
        })
    }

    fn refresh_menu(&self) {
        let t = text::current();
        let (label, enabled) = {
            let s = lock(&self.state);
            if self.dev {
                (t.update_check, false)
            } else if let Some(phase) = s.busy {
                match phase {
                    Phase::Checking => (t.update_checking, false),
                    Phase::Updating => (t.updating, false),
                }
            } else if s.agent.is_some() || s.mascot.is_some() {
                (t.update_available, true)
            } else {
                (t.update_check, true)
            }
        };
        let _ = self.item.set_text(label);
        let _ = self.item.set_enabled(enabled);
    }

    fn settings(&self) -> Settings {
        Settings::load(&self.settings_path)
    }

    fn pending(&self) -> (Option<Found>, Option<Found>) {
        let s = lock(&self.state);
        (s.agent.clone(), s.mascot.clone())
    }
}

fn log(app: &AppHandle, msg: &str) {
    if let Some(m) = app.try_state::<Manager>() {
        m.log(msg);
    }
}

fn notice(app: &AppHandle, kind: MessageDialogKind, msg: String) {
    app.dialog()
        .message(msg)
        .title(text::current().update_title)
        .kind(kind)
        .blocking_show();
}

/// 更新先の `.app` とその版。入っていない・版を読めない・手元ビルドなら `None`（マスコットだけ対象外）。
fn mascot_current(app: &AppHandle) -> Option<(PathBuf, Version)> {
    let home = home_of(&app.try_state::<Manager>()?.env());
    let target = update_target(&home)?;
    let v = Version::parse(&bundle_version(&target)?).ok()?;
    (!is_dev(&v)).then_some((target, v))
}

fn client(app: &AppHandle) -> Result<reqwest::Client, String> {
    reqwest::Client::builder()
        .user_agent(format!("ChatterAgent/{}", app.package_info().version))
        .connect_timeout(CONNECT_TIMEOUT)
        .read_timeout(READ_TIMEOUT)
        .build()
        .map_err(|e| e.to_string())
}

async fn get(client: &reqwest::Client, url: &str) -> Result<reqwest::Response, String> {
    let r = client
        .get(url)
        .header("Accept", "application/vnd.github+json")
        .send()
        .await
        .map_err(|e| e.to_string())?;
    match r.status().as_u16() {
        200..=299 => Ok(r),
        403 | 429 => Err(format!(
            "GitHub rate limit reached (HTTP {}). Will retry later",
            r.status()
        )),
        _ => Err(format!("GitHub returned HTTP {}", r.status())),
    }
}

/// 呼び出しは非同期ランタイムの外のスレッドから。
fn check(app: &AppHandle, u: &Updates) -> Result<(), String> {
    let repo = u.settings().dev_update_repo.unwrap_or_else(|| REPO.into());
    let url = format!("https://api.github.com/repos/{repo}/releases?per_page=100");
    let client = client(app)?;
    let releases: Value = tauri::async_runtime::block_on(async {
        get(&client, &url)
            .await?
            .json()
            .await
            .map_err(|e| e.to_string())
    })?;
    let current = app.package_info().version.clone();
    let agent = newest(&releases, AGENT_PREFIX, &[LATEST_JSON]).filter(|f| f.version > current);
    let mascot = mascot_current(app).and_then(|(_, cur)| {
        newest(&releases, MASCOT_PREFIX, &[MASCOT_ZIP, MASCOT_SIG]).filter(|f| f.version > cur)
    });
    let mut s = lock(&u.state);
    s.agent = agent;
    s.mascot = mascot;
    s.last_ok = Some(SystemTime::now());
    Ok(())
}

/// 起動時と、その後は自動確認が ON のあいだ定期的に確認する。
pub fn spawn_watcher(app: AppHandle) {
    thread::spawn(move || loop {
        auto_check(&app);
        thread::sleep(WAKE_INTERVAL);
    });
}

fn auto_check(app: &AppHandle) {
    let u = app.state::<Updates>();
    let due = lock(&u.state).last_ok.is_none_or(|t| {
        SystemTime::now()
            .duration_since(t)
            .map_or(true, |d| d >= CHECK_INTERVAL)
    });
    if !due || !u.settings().check_updates {
        return;
    }
    let Some(_busy) = u.begin() else { return };
    if let Err(e) = check(app, &u) {
        log(app, &format!("Update check failed: {e}"));
    }
}

/// メニューの「アップデート…」。確認 → 一覧 → マスコットを落として確かめる → ChatterAgent を入れ替えて再起動、
/// の順に進む。マスコットの入れ替えは止まったところで行い、止めるのは1回にする。呼び出しはスレッドから。
pub fn run_from_menu(app: AppHandle) {
    let u = app.state::<Updates>();
    let Some(mut busy) = u.begin() else { return };
    let t = text::current();
    if let Err(e) = check(&app, &u) {
        log(&app, &format!("Update check failed: {e}"));
        notice(&app, MessageDialogKind::Warning, t.update_check_failed(&e));
        return;
    }
    let (mut agent, mascot) = u.pending();
    // 隔離されたままでは自分を入れ替えられない。マスコットの更新は妨げない。
    let mut translocated = false;
    if agent.is_some() && is_translocated() {
        notice(
            &app,
            MessageDialogKind::Warning,
            t.update_translocated.into(),
        );
        agent = None;
        translocated = true;
    }
    let mut lines = Vec::new();
    if let Some(f) = &agent {
        lines.push(format!(
            "ChatterAgent {} → {}",
            app.package_info().version,
            f.version
        ));
    }
    if let (Some(f), Some((_, cur))) = (&mascot, mascot_current(&app)) {
        lines.push(format!("ChatterMascot {cur} → {}", f.version));
    }
    if lines.is_empty() {
        if !translocated {
            notice(&app, MessageDialogKind::Info, t.update_up_to_date.into());
        }
        return;
    }
    let go = app
        .dialog()
        .message(t.update_prompt(&lines.join("\n"), agent.is_some()))
        .title(t.update_title)
        .buttons(MessageDialogButtons::OkCancelCustom(
            t.update_ok.into(),
            t.update_cancel.into(),
        ))
        .blocking_show();
    if !go {
        return;
    }
    busy.updating();

    let mut download = None;
    if let Some(f) = &mascot {
        match fetch_mascot(&app, f) {
            Ok(d) => download = Some(d),
            Err(e) => mascot_failed(&app, &e, agent.is_some()),
        }
    }
    if let Some(f) = &agent {
        match install_agent(&app, f) {
            Ok(()) => {
                busy.keep = true;
                restart(&app, download);
                return;
            }
            Err(e) => {
                log(&app, &format!("ChatterAgent update failed: {e}"));
                notice(&app, MessageDialogKind::Warning, t.update_failed(&e));
            }
        }
    }
    if let Some(d) = download {
        match app.state::<Clients>().with_mascot_stopped(|| d.apply(&app)) {
            Ok(()) => lock(&u.state).mascot = None,
            Err(e) => mascot_failed(&app, &e.to_string(), false),
        }
    }
}

/// マスコットが書けなくても ChatterAgent は更新できるので、`continuing` なら続けることを添える。
fn mascot_failed(app: &AppHandle, e: &str, continuing: bool) {
    log(app, &format!("Mascot update failed: {e}"));
    let t = text::current();
    let msg = if continuing {
        t.mascot_update_failed(e)
    } else {
        t.update_failed(e)
    };
    notice(app, MessageDialogKind::Warning, msg);
}

/// 隔離された場所（AppTranslocation）の下からは、入れ替える先を取り違える。
fn is_translocated() -> bool {
    std::env::current_exe().is_ok_and(|p| p.to_string_lossy().contains("/AppTranslocation/"))
}

fn asset<'a>(f: &'a Found, name: &str) -> Result<&'a str, String> {
    f.assets
        .get(name)
        .map(String::as_str)
        .ok_or_else(|| format!("the release {} has no {name}", f.version))
}

/// ディスク上の `.app` を入れ替える。動いている ChatterAgent は再起動するまで古いまま。
fn install_agent(app: &AppHandle, f: &Found) -> Result<(), String> {
    let url = asset(f, LATEST_JSON)?
        .parse::<tauri::Url>()
        .map_err(|e| e.to_string())?;
    let updater = app
        .updater_builder()
        .endpoints(vec![url])
        .map(|b| {
            b.configure_client(|c| {
                c.connect_timeout(CONNECT_TIMEOUT)
                    .read_timeout(READ_TIMEOUT)
            })
        })
        .and_then(|b| b.build())
        .map_err(|e| e.to_string())?;
    // ★ メインスレッドで呼ばない。管理者の認証の AppleScript をここで待つ。
    tauri::async_runtime::block_on(async {
        let update = updater
            .check()
            .await
            .map_err(|e| e.to_string())?
            .ok_or_else(|| format!("latest.json of {} has no newer version", f.version))?;
        update
            .download_and_install(|_, _| {}, || {})
            .await
            .map_err(|e| e.to_string())
    })?;
    log(
        app,
        &format!("ChatterAgent updated to {}. Restarting", f.version),
    );
    Ok(())
}

/// 「終了」と同じ順に止めてから再起動する。先に止めないと、新しいプロセスが古いマスコットの
/// 終了を「外で終了された」と見て非表示にする。マスコットはここで止まったところで入れ替え、
/// 起こすのは再起動後の1回だけにする。
fn restart(app: &AppHandle, mascot: Option<MascotDownload>) {
    let (cl, m, app) = (
        (*app.state::<Clients>()).clone(),
        (*app.state::<Manager>()).clone(),
        app.clone(),
    );
    cl.shutdown(move || {
        if let Some(d) = mascot {
            // 止めきれていなければ、動いているものの下を差し替えない。
            let result = if running_pids().is_empty() {
                d.apply(&app)
            } else {
                Err(io::Error::other("the mascot did not quit"))
            };
            if let Err(e) = result {
                mascot_failed(&app, &e.to_string(), true);
            }
        }
        m.quit(move || app.request_restart());
    });
}

/// 落としたマスコットの zip と署名。入れ替えはマスコットが止まっている間に `apply` する。
struct MascotDownload {
    zip: Vec<u8>,
    sig: String,
    pubkey: String,
    target: PathBuf,
    version: String,
}

impl MascotDownload {
    fn apply(&self, app: &AppHandle) -> io::Result<()> {
        let warn = |m: &str| log(app, m);
        apply_mascot(
            &self.zip,
            &self.sig,
            &self.pubkey,
            &self.target,
            &self.version,
            &warn,
        )?;
        log(app, &format!("Mascot updated to {}", self.version));
        Ok(())
    }
}

/// 止める前に落として署名まで確かめる。合わなければ何も止めない。
fn fetch_mascot(app: &AppHandle, f: &Found) -> Result<MascotDownload, String> {
    let pubkey = pubkey(app)?;
    let (target, _) = mascot_current(app)
        .ok_or_else(|| "ChatterMascot.app is not in /Applications or ~/Applications".to_string())?;
    let (zip_url, sig_url) = (asset(f, MASCOT_ZIP)?, asset(f, MASCOT_SIG)?);
    let client = client(app)?;
    let (zip, sig) = tauri::async_runtime::block_on(async {
        let zip = get(&client, zip_url).await?.bytes().await;
        let sig = get(&client, sig_url).await?.text().await;
        Ok::<_, String>((
            zip.map_err(|e| e.to_string())?,
            sig.map_err(|e| e.to_string())?,
        ))
    })?;
    verify(&zip, &sig, &pubkey)?;
    Ok(MascotDownload {
        zip: zip.to_vec(),
        sig,
        pubkey,
        target,
        version: f.version.to_string(),
    })
}

/// updater と同じ公開鍵（`tauri.conf.json`）。
fn pubkey(app: &AppHandle) -> Result<String, String> {
    app.config()
        .plugins
        .0
        .get("updater")
        .and_then(|v| v.get("pubkey"))
        .and_then(Value::as_str)
        .map(str::to_string)
        .ok_or_else(|| "no updater public key in tauri.conf.json".into())
}

/// minisign の署名を確かめる。署名と公開鍵は `tauri signer` が出す base64 のまま受ける。
fn verify(data: &[u8], sig_b64: &str, pubkey_b64: &str) -> Result<(), String> {
    let decode = |what: &str, s: &str| -> Result<String, String> {
        let bytes = STANDARD
            .decode(s.trim())
            .map_err(|e| format!("invalid {what}: {e}"))?;
        String::from_utf8(bytes).map_err(|e| format!("invalid {what}: {e}"))
    };
    let key = PublicKey::decode(&decode("public key", pubkey_b64)?)
        .map_err(|e| format!("invalid public key: {e}"))?;
    let sig = Signature::decode(&decode("signature", sig_b64)?)
        .map_err(|e| format!("invalid signature: {e}"))?;
    key.verify(data, &sig, false)
        .map_err(|e| format!("signature verification failed: {e}"))
}

/// 署名を確かめてから入れ替える。確かめに落ちたら `target` には触らない。
fn apply_mascot(
    zip: &[u8],
    sig_b64: &str,
    pubkey_b64: &str,
    target: &Path,
    expected_version: &str,
    log: &dyn Fn(&str),
) -> io::Result<()> {
    verify(zip, sig_b64, pubkey_b64).map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e))?;
    let path =
        std::env::temp_dir().join(format!("chatter-mascot-update-{}.zip", std::process::id()));
    std::fs::write(&path, zip)?;
    let result = install_mascot(&path, target, expected_version, log);
    let _ = std::fs::remove_file(&path);
    result
}

/// zip を `target` の隣に展開してから、1回で入れ替える（全部成功か、何も変わらないか）。
#[cfg(target_os = "macos")]
fn install_mascot(
    zip: &Path,
    target: &Path,
    expected_version: &str,
    log: &dyn Fn(&str),
) -> io::Result<()> {
    let parent = target
        .parent()
        .ok_or_else(|| io::Error::other("the install target has no parent directory"))?;
    let tmp = parent.join(".ChatterMascot.update");
    // 前回の残骸が消せないまま進むと、`create_dir` の失敗が書き込み権限の問題に見える。
    if let Err(e) = std::fs::remove_dir_all(&tmp) {
        if e.kind() != io::ErrorKind::NotFound {
            return Err(io::Error::new(
                e.kind(),
                format!("cannot remove {}: {e}", tmp.display()),
            ));
        }
    }
    std::fs::create_dir(&tmp).map_err(|e| {
        io::Error::new(
            e.kind(),
            format!("cannot write to {}: {e}", parent.display()),
        )
    })?;
    let result = swap_in(zip, &tmp, target, expected_version);
    // 入れ替えに成功していれば、中身は元の `.app`。
    if let Err(e) = std::fs::remove_dir_all(&tmp) {
        log(&format!("Failed to remove {}: {e}", tmp.display()));
    }
    result
}

#[cfg(target_os = "macos")]
fn swap_in(zip: &Path, tmp: &Path, target: &Path, expected_version: &str) -> io::Result<()> {
    use std::ffi::CString;
    use std::os::unix::ffi::OsStrExt as _;

    let status = std::process::Command::new("ditto")
        .args(["-x", "-k"])
        .arg(zip)
        .arg(tmp)
        .status()?;
    if !status.success() {
        return Err(io::Error::other(format!("ditto failed: {status}")));
    }
    let new_app = tmp.join(crate::mascot_app::APP_NAME);
    let got = bundle_version(&new_app);
    if got.as_deref() != Some(expected_version) {
        return Err(io::Error::other(format!(
            "the downloaded mascot is version {got:?}, expected {expected_version}"
        )));
    }
    let c = |p: &Path| CString::new(p.as_os_str().as_bytes()).map_err(io::Error::other);
    let (from, to) = (c(&new_app)?, c(target)?);
    // SAFETY: どちらも NUL 終端の C 文字列で、呼び出しの間は生きている。
    if unsafe { libc::renamex_np(from.as_ptr(), to.as_ptr(), libc::RENAME_SWAP) } != 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(())
}

#[cfg(not(target_os = "macos"))]
fn install_mascot(
    _zip: &Path,
    _target: &Path,
    _expected_version: &str,
    _log: &dyn Fn(&str),
) -> io::Result<()> {
    Err(io::Error::new(
        io::ErrorKind::Unsupported,
        "cannot update the mascot on non-macOS",
    ))
}

#[derive(Serialize)]
pub struct Info {
    agent: Option<String>,
    mascot: Option<String>,
    auto: bool,
}

/// 見つかった新しい版と、自動確認の値。
#[tauri::command]
pub async fn update_info(app: AppHandle) -> Result<Info, String> {
    tauri::async_runtime::spawn_blocking(move || {
        let u = app.state::<Updates>();
        let (agent, mascot) = u.pending();
        Info {
            agent: agent.map(|f| f.version.to_string()),
            mascot: mascot.map(|f| f.version.to_string()),
            auto: u.settings().check_updates,
        }
    })
    .await
    .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn update_set_auto(app: AppHandle, on: bool) -> Result<(), String> {
    tauri::async_runtime::spawn_blocking(move || {
        let u = app.state::<Updates>();
        crate::update_settings(&u.settings_path, |s| s.check_updates = on)
            .map_err(|e| e.to_string())
    })
    .await
    .map_err(|e| e.to_string())?
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    // テスト専用の鍵で署名した固定のバイト列。秘密鍵はリポジトリに無い。
    const PAYLOAD: &[u8] = b"chatter-agent update payload";
    const PUBKEY: &str = "dW50cnVzdGVkIGNvbW1lbnQ6IG1pbmlzaWduIHB1YmxpYyBrZXk6IDdDRTI5MDQ4RDRFNzEyRDYKUldUV0V1ZlVTSkRpZkp1RUN1Z0NQKzRBMEV1VzlxZHlSTEZjNVB3b3NWUGY3SENXSlJOS3Q4NXAK";
    const OTHER_PUBKEY: &str = "dW50cnVzdGVkIGNvbW1lbnQ6IG1pbmlzaWduIHB1YmxpYyBrZXk6IEJEOTQ1QkY3REVBMTQ5MzQKUldRMFNhSGU5MXVVdlZHeHFiY25pcTJtUHk4SFFLWTh5SHBYMjdnN1R6S2FRODl0SUhBT3NIZzIK";
    const SIG: &str = "dW50cnVzdGVkIGNvbW1lbnQ6IHNpZ25hdHVyZSBmcm9tIHRhdXJpIHNlY3JldCBrZXkKUlVUV0V1ZlVTSkRpZkdCZC8zSFBtSHVFdndLZHFpVmFJVnZVd3Z5Q2xQLzFKMm9VWHovZVJHU0c0Z1pHcFl6TDluRUpYVlhkdlMvbGRWSFVqeG9WZlNIb0d5UGV2VmswMFFNPQp0cnVzdGVkIGNvbW1lbnQ6IHRpbWVzdGFtcDoxNzkyNzQxODc4CWZpbGU6cGF5bG9hZC5iaW4KaDFpdi9xbWN0dmt6U1ZrdnBVOUdCSExxUk5Ic0c5L1BKSFNVcXVtaUpBQ2xadGI2Q0hDQXNUWCtFSFdDTGIrVW9MMHNpZFRBcG5PYWg4U0pDY2dYQ3c9PQo=";

    fn release_with(tag: &str, draft: bool, prerelease: bool, names: &[&str]) -> Value {
        let assets: Vec<Value> = names
            .iter()
            .map(|n| json!({"name": n, "browser_download_url": format!("https://x/{tag}/{n}")}))
            .collect();
        json!({"tag_name": tag, "draft": draft, "prerelease": prerelease, "assets": assets})
    }

    fn release(tag: &str, draft: bool, prerelease: bool) -> Value {
        release_with(
            tag,
            draft,
            prerelease,
            &[LATEST_JSON, MASCOT_ZIP, MASCOT_SIG],
        )
    }

    #[test]
    fn newest_picks_the_highest_public_release_of_the_prefix() {
        let releases = json!([
            release("agent-v0.2.0", false, false),
            release("mascot-v9.0.0", false, false),
            release("agent-v0.10.0", false, false),
            release("agent-v1.0.0", true, false),
            release("agent-v1.1.0", false, true),
            release("agent-vbroken", false, false),
            release("v3.0.0", false, false),
            release("agent-v0.9.0", false, false),
        ]);
        let f = newest(&releases, AGENT_PREFIX, &[LATEST_JSON]).unwrap();
        assert_eq!(f.version, Version::new(0, 10, 0));
        assert_eq!(
            f.assets["latest.json"],
            "https://x/agent-v0.10.0/latest.json"
        );
        assert_eq!(
            newest(&releases, MASCOT_PREFIX, &[MASCOT_ZIP, MASCOT_SIG])
                .unwrap()
                .version,
            Version::new(9, 0, 0)
        );
        assert_eq!(newest(&json!([]), AGENT_PREFIX, &[]), None);
        assert_eq!(
            newest(&json!({"message": "rate limit"}), AGENT_PREFIX, &[]),
            None
        );
    }

    #[test]
    fn newest_skips_releases_without_the_required_assets_and_semver_prereleases() {
        let releases = json!([
            release_with("mascot-v0.1.0", false, false, &[MASCOT_ZIP, MASCOT_SIG]),
            release_with("mascot-v0.2.0", false, false, &[MASCOT_ZIP]),
            release_with("mascot-v0.3.0", false, false, &["ChatterMascot.apk"]),
            release_with(
                "mascot-v0.4.0-rc.1",
                false,
                false,
                &[MASCOT_ZIP, MASCOT_SIG]
            ),
        ]);
        let required = [MASCOT_ZIP, MASCOT_SIG];
        assert_eq!(
            newest(&releases, MASCOT_PREFIX, &required).unwrap().version,
            Version::new(0, 1, 0)
        );
        assert_eq!(
            newest(&json!([releases[1], releases[2]]), MASCOT_PREFIX, &required),
            None
        );
    }

    #[test]
    fn dev_builds_are_recognized_by_the_pre_release_part() {
        assert!(is_dev(&Version::parse("0.0.0-dev").unwrap()));
        assert!(!is_dev(&Version::parse("0.2.1").unwrap()));
    }

    #[test]
    fn verify_accepts_only_the_signed_bytes_and_the_matching_key() {
        assert_eq!(verify(PAYLOAD, SIG, PUBKEY), Ok(()));
        assert_eq!(verify(PAYLOAD, &format!("{SIG}\n"), PUBKEY), Ok(()));
        assert!(verify(b"chatter-agent update payloae", SIG, PUBKEY).is_err());
        assert!(verify(PAYLOAD, SIG, OTHER_PUBKEY).is_err());
        assert!(verify(PAYLOAD, SIG, "PLACEHOLDER_UPDATER_PUBKEY").is_err());
        assert!(verify(PAYLOAD, "not base64!", PUBKEY).is_err());
    }

    fn scratch(name: &str) -> PathBuf {
        let d =
            std::env::temp_dir().join(format!("chatter-agent-app-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(&d).unwrap();
        d
    }

    fn write_app(dir: &Path, version: &str, marker: &str) -> PathBuf {
        let app = dir.join("ChatterMascot.app");
        std::fs::create_dir_all(app.join("Contents")).unwrap();
        std::fs::write(
            app.join("Contents/Info.plist"),
            format!(
                r#"<?xml version="1.0" encoding="UTF-8"?>
<plist version="1.0"><dict>
<key>CFBundleShortVersionString</key><string>{version}</string>
</dict></plist>"#
            ),
        )
        .unwrap();
        std::fs::write(app.join("marker"), marker).unwrap();
        app
    }

    #[test]
    fn apply_mascot_does_not_touch_the_target_when_the_signature_fails() {
        let d = scratch("apply");
        let target = write_app(&d, "0.1.0", "old");
        let err = apply_mascot(PAYLOAD, SIG, OTHER_PUBKEY, &target, "0.2.0", &|_| {}).unwrap_err();
        assert_eq!(err.kind(), io::ErrorKind::InvalidData);
        assert_eq!(bundle_version(&target).as_deref(), Some("0.1.0"));
        assert!(!d.join(".ChatterMascot.update").exists());
        let _ = std::fs::remove_dir_all(&d);
    }

    #[cfg(target_os = "macos")]
    fn zip_of(app: &Path, zip: &Path) {
        let status = std::process::Command::new("ditto")
            .args(["-c", "-k", "--keepParent"])
            .arg(app)
            .arg(zip)
            .status()
            .unwrap();
        assert!(status.success());
    }

    #[cfg(target_os = "macos")]
    #[test]
    fn install_mascot_swaps_in_the_new_app_and_leaves_no_temporary_directory() {
        let d = scratch("install");
        let src = d.join("src");
        std::fs::create_dir_all(&src).unwrap();
        zip_of(&write_app(&src, "0.2.0", "new"), &d.join("m.zip"));
        let dest = d.join("dest");
        std::fs::create_dir_all(&dest).unwrap();
        let target = write_app(&dest, "0.1.0", "old");

        install_mascot(&d.join("m.zip"), &target, "0.2.0", &|_| {}).unwrap();
        assert_eq!(bundle_version(&target).as_deref(), Some("0.2.0"));
        assert_eq!(
            std::fs::read_to_string(target.join("marker")).unwrap(),
            "new"
        );
        assert!(!dest.join(".ChatterMascot.update").exists());
        let _ = std::fs::remove_dir_all(&d);
    }

    #[cfg(target_os = "macos")]
    #[test]
    fn install_mascot_reports_an_unwritable_directory_and_keeps_the_original() {
        use std::os::unix::fs::PermissionsExt as _;
        let d = scratch("install-readonly");
        let src = d.join("src");
        std::fs::create_dir_all(&src).unwrap();
        zip_of(&write_app(&src, "0.2.0", "new"), &d.join("m.zip"));
        let dest = d.join("dest");
        std::fs::create_dir_all(&dest).unwrap();
        let target = write_app(&dest, "0.1.0", "old");
        std::fs::set_permissions(&dest, std::fs::Permissions::from_mode(0o555)).unwrap();

        let err = install_mascot(&d.join("m.zip"), &target, "0.2.0", &|_| {}).unwrap_err();
        std::fs::set_permissions(&dest, std::fs::Permissions::from_mode(0o755)).unwrap();
        assert_eq!(err.kind(), io::ErrorKind::PermissionDenied);
        assert!(err.to_string().contains("cannot write to"));
        assert_eq!(
            std::fs::read_to_string(target.join("marker")).unwrap(),
            "old"
        );
        let _ = std::fs::remove_dir_all(&d);
    }

    #[cfg(target_os = "macos")]
    #[test]
    fn install_mascot_stops_with_a_reason_when_a_leftover_cannot_be_removed() {
        use std::os::unix::fs::PermissionsExt as _;
        let d = scratch("install-leftover");
        let src = d.join("src");
        std::fs::create_dir_all(&src).unwrap();
        zip_of(&write_app(&src, "0.2.0", "new"), &d.join("m.zip"));
        let dest = d.join("dest");
        std::fs::create_dir_all(&dest).unwrap();
        let target = write_app(&dest, "0.1.0", "old");
        let leftover = dest.join(".ChatterMascot.update");
        std::fs::create_dir_all(leftover.join("stale")).unwrap();
        std::fs::write(leftover.join("stale/file"), "x").unwrap();
        std::fs::set_permissions(
            leftover.join("stale"),
            std::fs::Permissions::from_mode(0o555),
        )
        .unwrap();

        let err = install_mascot(&d.join("m.zip"), &target, "0.2.0", &|_| {}).unwrap_err();
        std::fs::set_permissions(
            leftover.join("stale"),
            std::fs::Permissions::from_mode(0o755),
        )
        .unwrap();
        assert!(err.to_string().contains("cannot remove"), "{err}");
        assert_eq!(
            std::fs::read_to_string(target.join("marker")).unwrap(),
            "old"
        );
        let _ = std::fs::remove_dir_all(&d);
    }

    #[cfg(target_os = "macos")]
    #[test]
    fn install_mascot_refuses_a_different_version_and_keeps_the_original() {
        let d = scratch("install-mismatch");
        let src = d.join("src");
        std::fs::create_dir_all(&src).unwrap();
        zip_of(&write_app(&src, "0.3.0", "new"), &d.join("m.zip"));
        let dest = d.join("dest");
        std::fs::create_dir_all(&dest).unwrap();
        let target = write_app(&dest, "0.1.0", "old");

        assert!(install_mascot(&d.join("m.zip"), &target, "0.2.0", &|_| {}).is_err());
        assert_eq!(
            std::fs::read_to_string(target.join("marker")).unwrap(),
            "old"
        );
        assert!(!dest.join(".ChatterMascot.update").exists());
        let _ = std::fs::remove_dir_all(&d);
    }
}
