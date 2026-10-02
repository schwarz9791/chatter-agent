#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod server;
mod text;

use std::path::PathBuf;

use serde::{Deserialize, Serialize};
use server::{Manager, Status};
use tauri::image::Image;
use tauri::menu::{MenuBuilder, MenuItemBuilder, PredefinedMenuItem};
use tauri::tray::TrayIconBuilder;
use tauri::{Manager as _, RunEvent};

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
}

fn main() {
    tauri::Builder::default()
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
            let quit_item = MenuItemBuilder::with_id("quit", t.quit).build(app)?;
            let menu = MenuBuilder::new(app)
                .item(&status_item)
                .item(&PredefinedMenuItem::separator(app)?)
                .item(&restart_item)
                .item(&PredefinedMenuItem::separator(app)?)
                .item(&quit_item)
                .build()?;

            let log_path = app.path().app_log_dir()?.join("server.log");
            let (si, ri) = (status_item.clone(), restart_item.clone());
            let settings_path = app.path().app_config_dir()?.join("settings.json");
            let core_dir = Settings::load(&settings_path).core_dir;
            let manager = Manager::new(log_path, core_dir, move |s| {
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
