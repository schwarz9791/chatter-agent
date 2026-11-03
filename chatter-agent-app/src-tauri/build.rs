fn main() {
    // macOS 12.3 未満でも起動できるよう、ScreenCaptureKit は弱いリンクにする。
    if std::env::var("CARGO_CFG_TARGET_OS").is_ok_and(|os| os == "macos") {
        println!("cargo:rustc-link-arg=-Wl,-weak_framework,ScreenCaptureKit");
    }
    tauri_build::build()
}
