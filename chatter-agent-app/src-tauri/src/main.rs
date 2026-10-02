#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

#[allow(dead_code)]
mod server;

use tauri::image::Image;
use tauri::menu::{MenuBuilder, MenuItemBuilder, PredefinedMenuItem};
use tauri::tray::TrayIconBuilder;

fn main() {
    tauri::Builder::default()
        .setup(|app| {
            // tao は起動時に Regular へ戻すため、LSUIElement だけでは Dock に出る。
            #[cfg(target_os = "macos")]
            app.set_activation_policy(tauri::ActivationPolicy::Accessory);

            let status = MenuItemBuilder::with_id("status", "状態: —")
                .enabled(false)
                .build(app)?;
            let quit = MenuItemBuilder::with_id("quit", "終了").build(app)?;
            let menu = MenuBuilder::new(app)
                .item(&status)
                .item(&PredefinedMenuItem::separator(app)?)
                .item(&quit)
                .build()?;

            // 2x を埋め込む。Retina でも等倍でもシステムが縮小して使う。
            let icon = Image::from_bytes(include_bytes!("../icons/tray/trayTemplate@2x.png"))?;
            TrayIconBuilder::new()
                .icon(icon)
                .icon_as_template(true)
                .tooltip("Chatter Agent")
                .menu(&menu)
                .show_menu_on_left_click(true)
                .on_menu_event(|app, event| {
                    if event.id() == "quit" {
                        app.exit(0);
                    }
                })
                .build(app)?;
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("ChatterAgent の起動に失敗しました");
}
