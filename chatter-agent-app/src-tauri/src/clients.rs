//! 繋ぐクライアント（マスコット / player）を表示とミュートから1つに決め、実態をそこへ寄せる。
//!
//! ★ マスコットと player を同時に繋がない。server はフレームを全員に配り、誰か1台が ack すればキューを
//!   消すので、両方繋ぐと二重に鳴る。Unity はロックを取らないため、順序の保証はここの責任になる:
//!   片方が止まったのを確かめてから、もう片方を起こす。

use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::str::FromStr;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::thread;
use std::time::{Duration, Instant};

use serde_json::Value;
use tauri::AppHandle;
use tauri_plugin_dialog::{DialogExt as _, MessageDialogKind};
use tauri_plugin_global_shortcut::{
    Code, GlobalShortcutExt as _, Modifiers, Shortcut, ShortcutState,
};

use crate::mascot::{read_settings, set_mute, settings_path};
use crate::mascot_app::{candidates, find_app, launch, request_quit, running_pids};
use crate::server::{exit_info, exit_label, lock, runtime_root, stop_child, Env, Manager, Serial};
use crate::text;

/// 誰も繋いでいなかった間の後に繋ぐクライアントへ、起動時点でこれより古い発話を音なしで飛ばさせる
/// （マスコットにも player にも渡す）。繋ぎ替えでは渡さず、未再生の文を引き継いで鳴らす。
pub const BACKLOG_MAX_AGE_MS: u64 = 60_000;
const MASCOT_QUIT_TIMEOUT: Duration = Duration::from_secs(10);
const MASCOT_START_TIMEOUT: Duration = Duration::from_secs(15);
const MONITOR_INTERVAL: Duration = Duration::from_secs(2);
const POLL: Duration = Duration::from_millis(200);
const DEFAULT_MUTE_KEY: &str = "ctrl+opt+m";
const DEFAULT_HIDE_KEY: &str = "ctrl+opt+h";

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Want {
    Mascot,
    Player,
    Nothing,
}

pub fn want(visible: bool, mute: bool) -> Want {
    match (visible, mute) {
        (true, _) => Want::Mascot,
        (false, false) => Want::Player,
        (false, true) => Want::Nothing,
    }
}

/// 古い発話を飛ばすのは、起動直後か、誰も繋いでいなかった後だけ。
fn skips_backlog(prev: Option<Want>) -> bool {
    matches!(prev, None | Some(Want::Nothing))
}

/// マスコットを起こせなかった理由。中身は利用者へ見せる文言。
enum StartFail {
    /// `.app` が無い。置くまで直らないので、非表示を保存して次の起動で繰り返さない。
    NotFound(String),
    /// `open` の失敗や起動待ちの打ち切り。一時的でありうるので保存しない。
    Failed(String),
}

/// player の子と、起こしたときの音量・core。
struct PlayerProc {
    child: Child,
    volume: String,
    core: PathBuf,
}

/// `mascot/settings.json` のうち、ここが使う値。
#[derive(Clone, Debug, PartialEq)]
struct MascotCfg {
    mute: bool,
    volume: f64,
    mute_key: String,
    hide_key: String,
}

impl Default for MascotCfg {
    fn default() -> Self {
        MascotCfg {
            mute: false,
            volume: 1.0,
            mute_key: DEFAULT_MUTE_KEY.into(),
            hide_key: DEFAULT_HIDE_KEY.into(),
        }
    }
}

fn parse_cfg(v: &Value) -> MascotCfg {
    let d = MascotCfg::default();
    let text = |group: &str, key: &str, default: String| {
        v.pointer(&format!("/{group}/{key}"))
            .and_then(Value::as_str)
            .map_or(default, str::to_string)
    };
    MascotCfg {
        mute: v
            .pointer("/audio/mute")
            .and_then(Value::as_bool)
            .unwrap_or(d.mute),
        volume: v
            .pointer("/audio/volume")
            .and_then(Value::as_f64)
            .map_or(d.volume, |x| x.clamp(0.0, 1.0)),
        mute_key: text("audio", "muteHotKey", d.mute_key),
        hide_key: text("ui", "hideHotKey", d.hide_key),
    }
}

/// `ctrl+opt+shift+cmd+<key>` を登録用の形へ。規則は設定窓の `parseHotKey` と同じ。
fn parse_shortcut(text: &str) -> Option<Shortcut> {
    let mut mods = Modifiers::empty();
    let mut key = None;
    for raw in text.split('+') {
        let token = raw.trim().to_lowercase();
        let m = match token.as_str() {
            "cmd" | "command" | "meta" => Modifiers::SUPER,
            "opt" | "option" | "alt" => Modifiers::ALT,
            "ctrl" | "control" => Modifiers::CONTROL,
            "shift" => Modifiers::SHIFT,
            _ => {
                if key.is_some() {
                    return None;
                }
                key = Some(code_of(&token)?);
                continue;
            }
        };
        mods |= m;
    }
    let key = key?;
    (!mods.is_empty()).then(|| Shortcut::new(Some(mods), key))
}

fn code_of(token: &str) -> Option<Code> {
    let name = match token {
        "space" => "Space".to_string(),
        "return" => "Enter".to_string(),
        "tab" => "Tab".to_string(),
        "escape" => "Escape".to_string(),
        t if t.len() == 1 && t.as_bytes()[0].is_ascii_lowercase() => {
            format!("Key{}", t.to_uppercase())
        }
        t if t.len() == 1 && t.as_bytes()[0].is_ascii_digit() => format!("Digit{t}"),
        t if t
            .strip_prefix('f')
            .and_then(|n| n.parse::<u8>().ok())
            .is_some_and(|n| (1..=12).contains(&n)) =>
        {
            t.to_uppercase()
        }
        _ => return None,
    };
    Code::from_str(&name).ok()
}

type Pair = (Option<Shortcut>, Option<Shortcut>);

/// 不正な値は既定へ倒す。2つが同じ組み合わせなら表示切替は登録しない。
fn resolve_hotkeys(mute: &str, hide: &str) -> Pair {
    let pick = |text: &str, default: &str| parse_shortcut(text).or_else(|| parse_shortcut(default));
    let m = pick(mute, DEFAULT_MUTE_KEY);
    let h = pick(hide, DEFAULT_HIDE_KEY);
    (m, if m == h { None } else { h })
}

/// 変化の検知用。`mtime:size`。
fn stamp(path: &Path) -> String {
    match fs::metadata(path) {
        Ok(m) => {
            let nanos = m
                .modified()
                .ok()
                .and_then(|t| t.duration_since(std::time::UNIX_EPOCH).ok())
                .map_or(0, |d| d.as_nanos());
            format!("{nanos}:{}", m.len())
        }
        Err(_) => "none".into(),
    }
}

fn wait_until(timeout: Duration, mut done: impl FnMut() -> bool) -> bool {
    let deadline = Instant::now() + timeout;
    loop {
        if done() {
            return true;
        }
        if Instant::now() >= deadline {
            return false;
        }
        thread::sleep(POLL);
    }
}

struct St {
    visible: bool,
    cfg: MascotCfg,
    /// 最後に実現できた状態。一度も reconcile できていなければ None。
    applied: Option<Want>,
    stamp: Option<String>,
    hotkeys: Option<Pair>,
}

struct Inner {
    app: AppHandle,
    settings_path: PathBuf,
    manager: OnceLock<Manager>,
    player: Mutex<Option<PlayerProc>>,
    st: Mutex<St>,
    shutting_down: AtomicBool,
    serial: Arc<Serial>,
    /// 表示とミュートが変わったとき、メニューへ反映する。
    on_view: Box<dyn Fn(bool, bool) + Send + Sync>,
}

#[derive(Clone)]
pub struct Clients(Arc<Inner>);

impl Clients {
    pub fn new(
        app: AppHandle,
        settings_path: PathBuf,
        visible: bool,
        on_view: impl Fn(bool, bool) + Send + Sync + 'static,
    ) -> Self {
        Clients(Arc::new(Inner {
            app,
            settings_path,
            manager: OnceLock::new(),
            player: Mutex::new(None),
            st: Mutex::new(St {
                visible,
                cfg: MascotCfg::default(),
                applied: None,
                stamp: None,
                hotkeys: None,
            }),
            shutting_down: AtomicBool::new(false),
            serial: Arc::default(),
            on_view: Box::new(on_view),
        }))
    }

    /// `Manager` の on_change が `Clients` を呼ぶので、`Manager` は後から結ぶ。
    pub fn attach(&self, manager: Manager) {
        let _ = self.0.manager.set(manager);
    }

    fn log(&self, msg: &str) {
        if let Some(m) = self.0.manager.get() {
            m.log(msg);
        }
    }

    fn notify(&self) {
        let (visible, mute) = {
            let st = lock(&self.0.st);
            (st.visible, st.cfg.mute)
        };
        (self.0.on_view)(visible, mute);
    }

    /// 実態を表示とミュートに合わせる。呼び出しスレッドを塞がない。
    pub fn request(&self) {
        let c = self.clone();
        self.0.serial.request(move || c.reconcile());
    }

    pub fn set_mute(&self, on: bool) {
        let result = self
            .0
            .manager
            .get()
            .and_then(|m| runtime_root(&m.env()))
            .ok_or_else(|| "ランタイムルートを決められない".to_string())
            .and_then(|root| set_mute(&root, on));
        match result {
            Ok(()) => {
                lock(&self.0.st).cfg.mute = on;
                self.request();
            }
            Err(e) => self.log(&format!("ミュートを書けなかった: {e}")),
        }
        // 失敗したときは、先に切り替わったメニューのチェックを実態へ戻す。
        self.notify();
    }

    pub fn toggle_mute(&self) {
        let on = !lock(&self.0.st).cfg.mute;
        self.set_mute(on);
    }

    /// `persist` は利用者の操作と、`.app` が無いときだけ true。外で起きた変化は保存しない
    /// （ログアウトではマスコットが先に終わるため、保存すると次のログインで出なくなる）。
    fn set_visible(&self, visible: bool, persist: bool) {
        lock(&self.0.st).visible = visible;
        if persist {
            if let Err(e) =
                crate::update_settings(&self.0.settings_path, |s| s.mascot_visible = visible)
            {
                self.log(&format!("表示状態を保存できなかった: {e}"));
            }
        }
        self.notify();
        self.request();
    }

    pub fn toggle_visible(&self) {
        let visible = !lock(&self.0.st).visible;
        self.set_visible(visible, true);
    }

    /// `mascot/settings.json` を読み直す。読めない・壊れているときは前回の値を使い続ける。
    fn refresh(&self, env: &Env) {
        let Some(root) = runtime_root(env) else {
            return;
        };
        let path = settings_path(&root);
        let stamp = stamp(&path);
        let cfg = read_settings(&path).map(|v| parse_cfg(&v));
        if let Err(e) = &cfg {
            self.log(&format!("マスコットの設定を読めない。前回の値を使う: {e}"));
        }
        let mut st = lock(&self.0.st);
        st.stamp = Some(stamp);
        if let Ok(cfg) = cfg {
            st.cfg = cfg;
        }
    }

    fn reconcile(&self) {
        if self.0.shutting_down.load(Ordering::SeqCst) {
            return;
        }
        let Some(m) = self.0.manager.get() else {
            return;
        };
        let (Some(env), Some(core)) = (m.resolved_env(), m.core_dir()) else {
            return;
        };
        self.refresh(&env);
        self.notify();
        let (visible, cfg) = {
            let st = lock(&self.0.st);
            (st.visible, st.cfg.clone())
        };
        let w = want(visible, cfg.mute);
        let backlog = skips_backlog(lock(&self.0.st).applied);
        let volume = format!("{}", cfg.volume);

        // 先に止める。
        let stale = lock(&self.0.player)
            .as_ref()
            .is_some_and(|p| w != Want::Player || p.volume != volume || p.core != core);
        if stale {
            self.stop_player();
        }
        if w != Want::Mascot && !self.quit_mascot() {
            self.log("マスコットが終わらなかった。この回は player を起こさない");
            // 実態はまだマスコットが居る。遅れて終わったら、監視が実態との差を見て決め直す。
            lock(&self.0.st).applied = Some(Want::Mascot);
            return;
        }

        // それから起こす。
        match w {
            Want::Mascot => {
                if running_pids().is_empty() {
                    if let Err(fail) = self.start_mascot(&env, &core, backlog) {
                        let (msg, persist) = match fail {
                            StartFail::NotFound(m) => (m, true),
                            StartFail::Failed(m) => (m, false),
                        };
                        self.log(&format!("マスコットを起動できない: {msg}"));
                        let t = text::current();
                        self.0
                            .app
                            .dialog()
                            .message(msg)
                            .title(t.mascot_unavailable_title)
                            .kind(MessageDialogKind::Warning)
                            .show(|_| {});
                        // 隠した状態に寄せ、player への切り替えへ進める。
                        self.set_visible(false, persist);
                        return;
                    }
                    // ★ 終了処理（`stop_sync`）は `serial.busy` を取らないので、起こした直後に見直して残さない。
                    if self.0.shutting_down.load(Ordering::SeqCst) {
                        for pid in running_pids() {
                            request_quit(pid);
                        }
                        return;
                    }
                }
            }
            Want::Player => {
                if lock(&self.0.player).is_none() {
                    // 失敗しても起こし直しはしない（終わった player と同じ扱い）。
                    if let Err(e) = self.spawn_player(m, &env, &core, cfg.volume, volume, backlog) {
                        self.log(&format!("player を起動できない: {e}"));
                    }
                    // ★ 終了処理（`stop_sync`）は `serial.busy` を取らないので、起こした直後に見直して残さない。
                    if self.0.shutting_down.load(Ordering::SeqCst) {
                        self.stop_player();
                        return;
                    }
                }
            }
            Want::Nothing => {}
        }
        lock(&self.0.st).applied = Some(w);
    }

    /// マスコットが全部消えたら true。
    fn quit_mascot(&self) -> bool {
        let pids = running_pids();
        if pids.is_empty() {
            return true;
        }
        self.log(&format!("マスコットの終了を要求した: pid={pids:?}"));
        for pid in pids {
            request_quit(pid);
        }
        wait_until(MASCOT_QUIT_TIMEOUT, || running_pids().is_empty())
    }

    fn start_mascot(&self, env: &Env, core: &Path, backlog: bool) -> Result<(), StartFail> {
        let t = text::current();
        let home = env
            .get("HOME")
            .map(PathBuf::from)
            .or_else(|| std::env::var_os("HOME").map(PathBuf::from))
            .unwrap_or_default();
        let Some(app) = find_app(Some(core), &home) else {
            let places: Vec<_> = candidates(Some(core), &home)
                .iter()
                .map(|p| p.display().to_string())
                .collect();
            return Err(StartFail::NotFound(t.mascot_not_found(&places.join("\n"))));
        };
        launch(&app, env, backlog)
            .map_err(|e| StartFail::Failed(t.mascot_launch_failed(&e.to_string())))?;
        if !wait_until(MASCOT_START_TIMEOUT, || !running_pids().is_empty()) {
            return Err(StartFail::Failed(t.mascot_launch_failed("timeout")));
        }
        self.log(&format!("マスコットを起動した: {}", app.display()));
        Ok(())
    }

    fn spawn_player(
        &self,
        m: &Manager,
        env: &Env,
        core: &Path,
        volume: f64,
        volume_label: String,
        backlog: bool,
    ) -> std::io::Result<()> {
        let log = m.open_log()?;
        let log_err = log.try_clone()?;
        let mut cmd = Command::new("node");
        cmd.arg("dist/chatter-agent-player.mjs")
            .current_dir(core)
            .envs(env);
        if backlog {
            cmd.env(
                "CHATTER_AGENT_SPEECH_BACKLOG_MAX_AGE_MS",
                BACKLOG_MAX_AGE_MS.to_string(),
            );
        }
        // 1.0 のときは足さず、利用者が `config.json` に書いた `playerArgs` を生かす。
        if volume != 1.0 {
            cmd.env(
                "CHATTER_AGENT_PLAYER_ARGS",
                format!("-v,{volume_label},{{file}}"),
            );
        }
        // ★ stdout / stderr はログの fd を直接渡す（server と同じ理由。パイプ中継は先に死ぬと EPIPE になる）。
        let child = cmd
            .stdin(Stdio::null())
            .stdout(log)
            .stderr(log_err)
            .spawn()?;
        self.log(&format!(
            "player を起動した: pid={} volume={volume_label}",
            child.id()
        ));
        *lock(&self.0.player) = Some(PlayerProc {
            child,
            volume: volume_label,
            core: core.to_path_buf(),
        });
        Ok(())
    }

    fn stop_player(&self) {
        // ★ 子を take してから止める。SIGTERM の2回目は player が後始末を飛ばすので、二重に送らない。
        let Some(PlayerProc { mut child, .. }) = lock(&self.0.player).take() else {
            return;
        };
        let pid = child.id();
        self.log(&format!("player の停止を要求した: pid={pid}"));
        if !stop_child(&mut child) {
            self.log("player が止まらなかったので強制終了した");
        }
    }

    /// 外から起きた変化を表示状態へ反映する。
    fn follow_outside(&self, visible: bool, why: &str) {
        if self.0.serial.busy.load(Ordering::SeqCst) {
            return;
        }
        if lock(&self.0.st).visible == visible {
            // 表示状態は合っていて、実態が遅れて追いついた（終了待ちを打ち切った後など）。決め直す。
            self.request();
            return;
        }
        self.log(why);
        self.set_visible(visible, false);
    }

    /// ショートカットを登録し直す。macOS ではメインスレッドへ回して待つので、メインスレッドから呼ばない。
    fn sync_hotkeys(&self) {
        let want = {
            let st = lock(&self.0.st);
            resolve_hotkeys(&st.cfg.mute_key, &st.cfg.hide_key)
        };
        if lock(&self.0.st).hotkeys == Some(want) {
            return;
        }
        let gs = self.0.app.global_shortcut();
        if let Err(e) = gs.unregister_all() {
            self.log(&format!("ショートカットを外せなかった: {e}"));
        }
        let toggle_mute: fn(&Clients) = Clients::toggle_mute;
        let actions = [(want.0, toggle_mute), (want.1, Clients::toggle_visible)];
        for (shortcut, action) in actions {
            let Some(shortcut) = shortcut else { continue };
            let c = self.clone();
            let result = gs.on_shortcut(shortcut, move |_, _, ev| {
                // 押したときだけ反応し、重い処理はスレッドへ逃がす。
                if ev.state == ShortcutState::Pressed {
                    let c = c.clone();
                    thread::spawn(move || action(&c));
                }
            });
            if let Err(e) = result {
                self.log(&format!("ショートカットを登録できなかった: {e}"));
            }
        }
        lock(&self.0.st).hotkeys = Some(want);
    }

    fn tick(&self) {
        if self.0.serial.busy.load(Ordering::SeqCst) || self.0.shutting_down.load(Ordering::SeqCst)
        {
            return;
        }
        {
            let mut guard = lock(&self.0.player);
            if let Some(p) = guard.as_mut() {
                if let Ok(Some(st)) = p.child.try_wait() {
                    let (pid, info) = (p.child.id(), exit_label(exit_info(&st)));
                    *guard = None;
                    drop(guard);
                    // 終わっていた間は誰も繋いでいないので、次に起こすときは溜まった古い発話を飛ばす。
                    lock(&self.0.st).applied = Some(Want::Nothing);
                    // ponytail: 自動では起こし直さない。主因は外で動いている player の `player.lock` で、
                    // 起こし直すと再起動を繰り返すだけになる。次の reconcile か手動の切り替えで戻る。
                    self.log(&format!("player が終わった: pid={pid} {info}"));
                }
            }
        }
        // `applied` と実態を比べる。`visible` とは比べない（env の解決前に誤判定しないため）。
        let applied = lock(&self.0.st).applied;
        let running = !running_pids().is_empty();
        match applied {
            Some(Want::Mascot) if !running => {
                self.follow_outside(false, "マスコットが外で終了された");
            }
            Some(Want::Player | Want::Nothing) if running => {
                self.follow_outside(true, "マスコットが外で起動された");
            }
            _ => {}
        }
        // 設定ファイルの変化（Unity のメニューやショートカットでのミュートを含む）。
        let Some(env) = self.0.manager.get().and_then(Manager::resolved_env) else {
            return;
        };
        let Some(root) = runtime_root(&env) else {
            return;
        };
        if lock(&self.0.st).stamp.as_deref() != Some(&stamp(&settings_path(&root))) {
            self.refresh(&env);
            self.notify();
            self.request();
        }
        // reconcile が先に読み直していると上の変化には現れないので、毎回突き合わせる。
        self.sync_hotkeys();
    }

    pub fn spawn_monitor(&self) {
        let c = self.clone();
        thread::spawn(move || loop {
            thread::sleep(MONITOR_INTERVAL);
            c.tick();
        });
    }

    /// 処理が空くのを待ってから、player → マスコットの順に止めて `then` を呼ぶ。
    /// `visible` は書き換えない（次の起動で戻すため）。
    pub fn shutdown(&self, then: impl FnOnce() + Send + 'static) {
        let c = self.clone();
        thread::spawn(move || {
            while c.0.serial.busy.swap(true, Ordering::SeqCst) {
                thread::sleep(Duration::from_millis(100));
            }
            c.0.shutting_down.store(true, Ordering::SeqCst);
            c.stop_player();
            c.quit_mascot();
            then();
        });
    }

    /// メニュー以外からの終了の保険。マスコットは要求を送るだけで待たない。
    pub fn stop_sync(&self) {
        self.0.shutting_down.store(true, Ordering::SeqCst);
        self.stop_player();
        for pid in running_pids() {
            request_quit(pid);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn want_follows_the_table() {
        assert_eq!(want(true, false), Want::Mascot);
        assert_eq!(want(true, true), Want::Mascot);
        assert_eq!(want(false, false), Want::Player);
        assert_eq!(want(false, true), Want::Nothing);
    }

    #[test]
    fn backlog_is_skipped_only_after_nothing_was_connected() {
        assert!(skips_backlog(None));
        assert!(skips_backlog(Some(Want::Nothing)));
        assert!(!skips_backlog(Some(Want::Mascot)));
        assert!(!skips_backlog(Some(Want::Player)));
    }

    #[test]
    fn shortcut_conversion() {
        let s = |m: Modifiers, c: Code| Some(Shortcut::new(Some(m), c));
        assert_eq!(
            parse_shortcut("ctrl+opt+m"),
            s(Modifiers::CONTROL | Modifiers::ALT, Code::KeyM)
        );
        assert_eq!(
            parse_shortcut("Cmd+Shift+7"),
            s(Modifiers::SUPER | Modifiers::SHIFT, Code::Digit7)
        );
        assert_eq!(
            parse_shortcut("ctrl+return"),
            s(Modifiers::CONTROL, Code::Enter)
        );
        assert_eq!(parse_shortcut("alt+f12"), s(Modifiers::ALT, Code::F12));
        assert_eq!(
            parse_shortcut("ctrl+space"),
            s(Modifiers::CONTROL, Code::Space)
        );
        assert_eq!(
            parse_shortcut("ctrl+escape"),
            s(Modifiers::CONTROL, Code::Escape)
        );
        assert_eq!(parse_shortcut("ctrl+tab"), s(Modifiers::CONTROL, Code::Tab));
        for bad in [
            "", "m", "ctrl", "ctrl+", "ctrl+a+b", "ctrl+f13", "ctrl+f0", "ctrl+é", "ctrl++m",
        ] {
            assert_eq!(parse_shortcut(bad), None, "{bad}");
        }
    }

    #[test]
    fn hotkeys_fall_back_and_never_clash() {
        let m = parse_shortcut("ctrl+opt+m");
        let h = parse_shortcut("ctrl+opt+h");
        assert_eq!(resolve_hotkeys("garbage", "also bad"), (m, h));
        assert_eq!(
            resolve_hotkeys("cmd+x", "ctrl+opt+h").0,
            parse_shortcut("cmd+x")
        );
        // 同じ組み合わせなら表示切替は登録しない。
        assert_eq!(
            resolve_hotkeys("cmd+x", "cmd+x"),
            (parse_shortcut("cmd+x"), None)
        );
    }

    #[test]
    fn cfg_defaults_and_clamp() {
        assert_eq!(parse_cfg(&json!({})), MascotCfg::default());
        let c = parse_cfg(&json!({
            "audio": {"mute": true, "volume": 3, "muteHotKey": "cmd+m"},
            "ui": {"hideHotKey": "cmd+h"}
        }));
        assert_eq!(
            c,
            MascotCfg {
                mute: true,
                volume: 1.0,
                mute_key: "cmd+m".into(),
                hide_key: "cmd+h".into()
            }
        );
        assert_eq!(parse_cfg(&json!({"audio": {"volume": -1}})).volume, 0.0);
        assert_eq!(
            parse_cfg(&json!({"audio": {"mute": "yes", "volume": "x"}})),
            MascotCfg::default()
        );
    }

    #[test]
    fn stamp_changes_with_content_and_missing_is_none() {
        let d =
            std::env::temp_dir().join(format!("chatter-agent-app-stamp-{}", std::process::id()));
        let _ = fs::remove_dir_all(&d);
        fs::create_dir_all(&d).unwrap();
        let f = d.join("settings.json");
        assert_eq!(stamp(&f), "none");
        fs::write(&f, "a").unwrap();
        let a = stamp(&f);
        fs::write(&f, "ab").unwrap();
        assert_ne!(stamp(&f), a);
        let _ = fs::remove_dir_all(&d);
    }
}
