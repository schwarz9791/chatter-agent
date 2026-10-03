#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod clients;
mod control;
mod mascot;
mod mascot_app;
mod server;
mod text;

use std::path::PathBuf;
use std::sync::Mutex;
use std::thread;

use clients::Clients;
use serde::{Deserialize, Serialize};
use server::{Manager, Status};
use tauri::image::Image;
use tauri::menu::{CheckMenuItemBuilder, MenuBuilder, MenuItemBuilder, PredefinedMenuItem};
use tauri::tray::TrayIconBuilder;
use tauri::{Manager as _, RunEvent};
use tauri_plugin_autostart::{MacosLauncher, ManagerExt as _};
use tauri_plugin_dialog::{DialogExt as _, MessageDialogKind};
use tauri_plugin_opener::OpenerExt as _;

/// ChatterAgent 自身の設定。
#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Settings {
    core_dir: Option<PathBuf>,
    #[serde(default = "yes")]
    mascot_visible: bool,
}

fn yes() -> bool {
    true
}

impl Default for Settings {
    fn default() -> Self {
        Settings {
            core_dir: None,
            mascot_visible: true,
        }
    }
}

/// 読み→差し替え→書きを直列にする。メニューと `Clients` が別スレッドから書くため。
static SETTINGS_LOCK: Mutex<()> = Mutex::new(());

/// 他のキーを残して差し替える。
pub(crate) fn update_settings(
    path: &std::path::Path,
    change: impl FnOnce(&mut Settings),
) -> std::io::Result<()> {
    let _guard = SETTINGS_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    let mut settings = Settings::load(path);
    change(&mut settings);
    settings.save(path)
}

impl Settings {
    fn load(path: &std::path::Path) -> Self {
        std::fs::read_to_string(path)
            .ok()
            .and_then(|s| serde_json::from_str(&s).ok())
            .unwrap_or_default()
    }

    /// tmp に書いてから rename し、書きかけを残さない。
    fn save(&self, path: &std::path::Path) -> std::io::Result<()> {
        if let Some(dir) = path.parent() {
            std::fs::create_dir_all(dir)?;
        }
        let tmp = path.with_extension("json.tmp");
        std::fs::write(&tmp, serde_json::to_vec_pretty(self)?)?;
        std::fs::rename(tmp, path)
    }
}

#[derive(serde::Serialize)]
struct About {
    version: String,
    notice: &'static str,
}

/// 通知はビルド時にリポジトリの NOTICE を埋め込む。コピーを持たないので食い違わない。
#[tauri::command]
fn about(app: tauri::AppHandle) -> About {
    About {
        version: app.package_info().version.to_string(),
        notice: include_str!("../../../NOTICE"),
    }
}

/// 設定窓を前面に出す。無ければ作る。
fn open_settings(app: &tauri::AppHandle, title: &str) {
    if let Some(w) = app.get_webview_window("settings") {
        let _ = w.unminimize();
        let _ = w.show();
        let _ = w.set_focus();
        return;
    }
    let built = tauri::WebviewWindowBuilder::new(
        app,
        "settings",
        tauri::WebviewUrl::App("index.html".into()),
    )
    .title(title)
    .inner_size(520.0, 720.0)
    // 幅は固定で、高さだけ変えられる。
    .min_inner_size(520.0, 400.0)
    .max_inner_size(520.0, 4000.0)
    .resizable(true)
    .build();
    // Accessory アプリは自分から前面に出ない。
    if let Ok(w) = built {
        let _ = w.set_focus();
    }
}

fn main() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_global_shortcut::Builder::new().build())
        .plugin(tauri_plugin_autostart::init(
            MacosLauncher::LaunchAgent,
            None,
        ))
        .invoke_handler(tauri::generate_handler![
            control::server_request,
            mascot::mascot_settings_get,
            mascot::mascot_settings_set,
            mascot::pick_vrm,
            mascot::confirm,
            mascot::mascot_reset,
            mascot::mascot_state,
            mascot::mascot_request,
            text::lang,
            about,
        ])
        .setup(|app| {
            // tao は起動時に Regular へ戻すため、LSUIElement だけでは Dock に出る。
            #[cfg(target_os = "macos")]
            app.set_activation_policy(tauri::ActivationPolicy::Accessory);

            let t = text::current();
            let settings_path = app.path().app_config_dir()?.join("settings.json");
            let visible = Settings::load(&settings_path).mascot_visible;
            let status_item = MenuItemBuilder::with_id("status", t.status(&Status::Stopped))
                .enabled(false)
                .build(app)?;
            let mute_item = CheckMenuItemBuilder::with_id("mute", t.mute).build(app)?;
            let visible_item = MenuItemBuilder::with_id(
                "visible",
                if visible {
                    t.hide_mascot
                } else {
                    t.show_mascot
                },
            )
            .build(app)?;
            let settings_item = MenuItemBuilder::with_id("settings", t.settings).build(app)?;
            let restart_item = MenuItemBuilder::with_id("restart", t.start)
                .enabled(false)
                .build(app)?;
            let open_log_item = MenuItemBuilder::with_id("open_log", t.open_log).build(app)?;
            let pick_core_item = MenuItemBuilder::with_id("pick_core", t.pick_core).build(app)?;
            let autostart_item = CheckMenuItemBuilder::with_id("autostart", t.autostart)
                .checked(app.autolaunch().is_enabled().unwrap_or(false))
                .build(app)?;
            let quit_item = MenuItemBuilder::with_id("quit", t.quit).build(app)?;
            let menu = MenuBuilder::new(app)
                .item(&status_item)
                .item(&PredefinedMenuItem::separator(app)?)
                .item(&mute_item)
                .item(&visible_item)
                .item(&PredefinedMenuItem::separator(app)?)
                .item(&settings_item)
                .item(&restart_item)
                .item(&open_log_item)
                .item(&pick_core_item)
                .item(&autostart_item)
                .item(&PredefinedMenuItem::separator(app)?)
                .item(&quit_item)
                .build()?;

            let log_path = app.path().app_log_dir()?.join("server.log");
            let (si, ri) = (status_item.clone(), restart_item.clone());
            let core_dir = Settings::load(&settings_path).core_dir;
            let (mi, vi) = (mute_item.clone(), visible_item.clone());
            let clients = Clients::new(
                app.handle().clone(),
                settings_path.clone(),
                visible,
                move |visible, mute| {
                    let _ = mi.set_checked(mute);
                    let _ = vi.set_text(if visible {
                        t.hide_mascot
                    } else {
                        t.show_mascot
                    });
                },
            );
            let c = clients.clone();
            let manager = Manager::new(log_path.clone(), core_dir, move |s| {
                let _ = si.set_text(t.status(s));
                let _ = ri.set_text(if matches!(s, Status::Running(_)) {
                    t.restart
                } else {
                    t.start
                });
                // 外で動いている server と処理中は触れない。
                let _ = ri.set_enabled(matches!(
                    s,
                    Status::Running(_) | Status::Stopped | Status::Crashed(_) | Status::Cannot(_)
                ));
                // 環境が解決したあとに、繋ぐクライアントを決め直す。
                c.request();
            });
            clients.attach(manager.clone());
            app.manage(manager.clone());
            app.manage(clients.clone());

            // 2x を埋め込む。Retina でも等倍でもシステムが縮小して使う。
            let icon = Image::from_bytes(include_bytes!("../icons/tray/trayTemplate@2x.png"))?;
            let m = manager.clone();
            let cl = clients.clone();
            let log_dir = log_path.parent().map(PathBuf::from).unwrap_or_default();
            TrayIconBuilder::new()
                .icon(icon)
                .icon_as_template(true)
                .tooltip("Chatter Agent")
                .menu(&menu)
                .show_menu_on_left_click(true)
                .on_menu_event(move |app, event| match event.id().as_ref() {
                    "restart" => {
                        m.start_or_restart();
                    }
                    "mute" => {
                        // クリックでチェックは先に切り替わっている。その値を書く。
                        let on = mute_item.is_checked().unwrap_or(false);
                        let c = cl.clone();
                        thread::spawn(move || c.set_mute(on));
                    }
                    "visible" => {
                        let c = cl.clone();
                        thread::spawn(move || c.toggle_visible());
                    }
                    "settings" => open_settings(app, t.settings_title),
                    "open_log" => {
                        // ファイルがまだ無ければディレクトリを開く。
                        let target = if log_path.exists() {
                            &log_path
                        } else {
                            &log_dir
                        };
                        let _ = app
                            .opener()
                            .open_path(target.to_string_lossy(), None::<&str>);
                    }
                    "pick_core" => {
                        let (m, path, handle) = (m.clone(), settings_path.clone(), app.clone());
                        app.dialog()
                            .file()
                            .set_title(t.pick_core_title)
                            .pick_folder(move |picked| {
                                let Some(dir) = picked.and_then(|p| p.into_path().ok()) else {
                                    return;
                                };
                                // 選び直しの失敗は状態欄に出さない。動いている server の状態と食い違うため。
                                let saved = server::check_core(&dir)
                                    .map_err(|f| t.cannot(&f))
                                    .and_then(|()| {
                                        update_settings(&path, |s| s.core_dir = Some(dir.clone()))
                                            .map_err(|e| e.to_string())
                                    });
                                if let Err(reason) = saved {
                                    handle
                                        .dialog()
                                        .message(reason)
                                        .kind(MessageDialogKind::Warning)
                                        .show(|_| {});
                                    return;
                                }
                                m.set_core_dir(dir);
                                m.start_or_restart();
                            });
                    }
                    "autostart" => {
                        // クリックでチェックは先に切り替わっている。失敗したら戻す。
                        let on = autostart_item.is_checked().unwrap_or(false);
                        let launcher = app.autolaunch();
                        let result = if on {
                            launcher.enable()
                        } else {
                            launcher.disable()
                        };
                        if result.is_err() {
                            let _ = autostart_item.set_checked(!on);
                        }
                    }
                    "quit" => {
                        // player → マスコット → server の順に止める。
                        let (app, m) = (app.clone(), m.clone());
                        cl.shutdown(move || m.quit(move || app.exit(0)));
                    }
                    _ => {}
                })
                .build(app)?;

            manager.spawn_monitor();
            clients.spawn_monitor();
            manager.start_or_restart();
            Ok(())
        })
        .build(tauri::generate_context!())
        .expect("ChatterAgent の起動に失敗しました")
        .run(|app, event| {
            match event {
                // 常駐アプリなので、設定窓を全部閉じても終了しない。メニューの「終了」は
                // `app.exit(0)`（code が Some）なので止まらない。
                RunEvent::ExitRequested {
                    code: None, api, ..
                } => api.prevent_exit(),
                // マスコットの右クリックと Finder 等からの開き直しで設定窓を出す。
                #[cfg(target_os = "macos")]
                RunEvent::Reopen { .. } => {
                    open_settings(app, text::current().settings_title);
                }
                RunEvent::Exit => {
                    if let Some(c) = app.try_state::<Clients>() {
                        c.stop_sync();
                    }
                    if let Some(m) = app.try_state::<Manager>() {
                        m.stop_sync();
                    }
                }
                _ => {}
            }
        });
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn update_keeps_other_keys_and_visible_defaults_to_true() {
        let d =
            std::env::temp_dir().join(format!("chatter-agent-app-settings-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        let path = d.join("settings.json");
        assert!(Settings::load(&path).mascot_visible);
        // 旧い形式（mascotVisible 無し）は表示ありとして読む。
        std::fs::create_dir_all(&d).unwrap();
        std::fs::write(&path, r#"{"coreDir":"/c"}"#).unwrap();
        assert!(Settings::load(&path).mascot_visible);
        update_settings(&path, |s| s.mascot_visible = false).unwrap();
        let s = Settings::load(&path);
        assert!(!s.mascot_visible);
        assert_eq!(s.core_dir, Some(PathBuf::from("/c")));
        update_settings(&path, |s| s.core_dir = Some("/d".into())).unwrap();
        let s = Settings::load(&path);
        assert!(!s.mascot_visible && s.core_dir == Some(PathBuf::from("/d")));
        let _ = std::fs::remove_dir_all(&d);
    }
}
