#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod server;
mod text;

use std::path::PathBuf;

use serde::{Deserialize, Serialize};
use server::{Manager, Status};
use tauri::image::Image;
use tauri::menu::{CheckMenuItemBuilder, MenuBuilder, MenuItemBuilder, PredefinedMenuItem};
use tauri::tray::TrayIconBuilder;
use tauri::{Manager as _, RunEvent};
use tauri_plugin_autostart::{MacosLauncher, ManagerExt as _};
use tauri_plugin_dialog::DialogExt as _;
use tauri_plugin_opener::OpenerExt as _;

/// ChatterAgent 自身の設定。
#[derive(Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Settings {
    core_dir: Option<PathBuf>,
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

fn main() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_autostart::init(
            MacosLauncher::LaunchAgent,
            None,
        ))
        .setup(|app| {
            // tao は起動時に Regular へ戻すため、LSUIElement だけでは Dock に出る。
            #[cfg(target_os = "macos")]
            app.set_activation_policy(tauri::ActivationPolicy::Accessory);

            let t = text::current();
            let status_item = MenuItemBuilder::with_id("status", t.status(&Status::Stopped))
                .enabled(false)
                .build(app)?;
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
                .item(&restart_item)
                .item(&open_log_item)
                .item(&pick_core_item)
                .item(&autostart_item)
                .item(&PredefinedMenuItem::separator(app)?)
                .item(&quit_item)
                .build()?;

            let log_path = app.path().app_log_dir()?.join("server.log");
            let (si, ri) = (status_item.clone(), restart_item.clone());
            let settings_path = app.path().app_config_dir()?.join("settings.json");
            let core_dir = Settings::load(&settings_path).core_dir;
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
            });
            app.manage(manager.clone());

            // 2x を埋め込む。Retina でも等倍でもシステムが縮小して使う。
            let icon = Image::from_bytes(include_bytes!("../icons/tray/trayTemplate@2x.png"))?;
            let m = manager.clone();
            let log_dir = log_path.parent().map(PathBuf::from).unwrap_or_default();
            TrayIconBuilder::new()
                .icon(icon)
                .icon_as_template(true)
                .tooltip("Chatter Agent")
                .menu(&menu)
                .show_menu_on_left_click(true)
                .on_menu_event(move |app, event| match event.id().as_ref() {
                    "restart" => {
                        if m.is_running() {
                            m.restart();
                        } else {
                            m.start();
                        }
                    }
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
                        let (m, path) = (m.clone(), settings_path.clone());
                        app.dialog()
                            .file()
                            .set_title(t.pick_core_title)
                            .pick_folder(move |picked| {
                                let Some(dir) = picked.and_then(|p| p.into_path().ok()) else {
                                    return;
                                };
                                if let Err(f) = server::check_core(&dir) {
                                    // 動いている子がいる間は、状態欄を実態と食い違わせない。
                                    if !m.is_running() {
                                        m.set_status(Status::Cannot(f));
                                    }
                                    return;
                                }
                                if Settings::save(
                                    &Settings {
                                        core_dir: Some(dir.clone()),
                                    },
                                    &path,
                                )
                                .is_err()
                                {
                                    return;
                                }
                                m.set_core_dir(dir);
                                if m.is_running() {
                                    m.restart();
                                } else {
                                    m.start();
                                }
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
                        let app = app.clone();
                        m.quit(move || app.exit(0));
                    }
                    _ => {}
                })
                .build(app)?;

            manager.spawn_monitor();
            manager.start();
            Ok(())
        })
        .build(tauri::generate_context!())
        .expect("ChatterAgent の起動に失敗しました")
        .run(|app, event| {
            if let RunEvent::Exit = event {
                if let Some(m) = app.try_state::<Manager>() {
                    m.stop_sync();
                }
            }
        });
}
