//! マスコットの設定（`mascot/settings.json`）と VRM。Unity が1秒ごとに読み直して反映する。

use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

use serde::Serialize;
use serde_json::{Map, Value};
use tauri::State;
use tauri_plugin_dialog::{DialogExt as _, MessageDialogButtons, MessageDialogKind};

use crate::server::{runtime_root, Manager};

/// ChatterAgent が書いてよいキー。Unity は知らないキーを捨てるので、余計なキーを書かせない意味もある。
/// リセットが消す範囲でもある。
const MANAGED: &[&[&str]] = &[
    &["audio", "volume"],
    &["audio", "muteHotKey"],
    &["ui", "hideHotKey"],
    &["character", "idleMotion"],
    &["character", "cursorGaze"],
    &["character", "blink"],
    &["character", "vrm"],
    &["display", "frameRate"],
];

/// 読み→差し替え→書きを直列にする。async コマンドが並行に走ると片方の変更が消えるため。
static SETTINGS_LOCK: Mutex<()> = Mutex::new(());

fn root_of(manager: &Manager) -> Result<PathBuf, String> {
    runtime_root(&manager.env()).ok_or_else(|| "ランタイムルートを決められない".to_string())
}

fn settings_path(root: &Path) -> PathBuf {
    root.join("mascot").join("settings.json")
}

fn models_dir(root: &Path) -> PathBuf {
    root.join("models")
}

fn is_managed(path: &[String]) -> bool {
    MANAGED
        .iter()
        .any(|m| m.len() == path.len() && m.iter().zip(path).all(|(a, b)| a == b))
}

fn is_vrm(path: &Path) -> bool {
    path.extension()
        .is_some_and(|e| e.eq_ignore_ascii_case("vrm"))
}

/// 無ければ `{}`。壊れている・最上位が object でないときはエラー（書き戻して潰さないため）。
fn read_settings(path: &Path) -> Result<Value, String> {
    let text = match fs::read_to_string(path) {
        Ok(t) => t,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(Value::Object(Map::new())),
        Err(e) => return Err(format!("settings.json を読めない: {e}")),
    };
    match serde_json::from_str::<Value>(&text) {
        Ok(v) if v.is_object() => Ok(v),
        Ok(_) => Err("settings.json の最上位が object ではない".into()),
        Err(e) => Err(format!("settings.json が壊れている: {e}")),
    }
}

/// 途中の object を作りながら `value` を差し替える。ほかのキーは触らない。
fn set_path(root: &mut Value, path: &[String], value: Value) {
    let mut cur = root;
    for key in path {
        if !cur.is_object() {
            *cur = Value::Object(Map::new());
        }
        cur = cur
            .as_object_mut()
            .expect("直前で object にした")
            .entry(key.clone())
            .or_insert(Value::Null);
    }
    *cur = value;
}

fn remove_path(root: &mut Value, path: &[&str]) {
    let Some((last, parents)) = path.split_last() else {
        return;
    };
    let mut cur = root;
    for key in parents {
        match cur.get_mut(*key) {
            Some(next) => cur = next,
            None => return,
        }
    }
    if let Some(map) = cur.as_object_mut() {
        map.remove(*last);
    }
}

/// Unity が使う `settings.json.tmp` と名前を分け、お互いの書きかけを踏まない。
fn write_settings(path: &Path, settings: &mut Value) -> Result<(), String> {
    if let Some(map) = settings.as_object_mut() {
        map.entry("version").or_insert(Value::from(1));
    }
    let io = |e: std::io::Error| format!("settings.json を書けない: {e}");
    if let Some(dir) = path.parent() {
        fs::create_dir_all(dir).map_err(io)?;
    }
    let tmp = path.with_extension("json.chatter-agent.tmp");
    let bytes = serde_json::to_vec_pretty(settings).map_err(|e| e.to_string())?;
    fs::write(&tmp, bytes).map_err(io)?;
    fs::rename(&tmp, path).map_err(io)
}

fn apply_set(root: &Path, path: &[String], value: Value) -> Result<(), String> {
    if !is_managed(path) {
        return Err(format!("管理外のキー: {}", path.join(".")));
    }
    let _guard = SETTINGS_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    let file = settings_path(root);
    let mut settings = read_settings(&file)?;
    set_path(&mut settings, path, value);
    write_settings(&file, &mut settings)
}

/// リセットの結果。設定と VRM の両方を試すので、失敗しても消せた数は残る。
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResetOutcome {
    removed: u32,
    error: Option<String>,
}

fn reset_settings(root: &Path) -> Result<(), String> {
    let _guard = SETTINGS_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    let file = settings_path(root);
    if !file.exists() {
        return Ok(());
    }
    let mut settings = read_settings(&file)?;
    for key in MANAGED {
        remove_path(&mut settings, key);
    }
    write_settings(&file, &mut settings)
}

/// 消した数と、最初に起きた失敗。1件失敗しても残りは続ける。
fn reset_models(root: &Path) -> (u32, Option<String>) {
    let entries = match fs::read_dir(models_dir(root)) {
        Ok(e) => e,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return (0, None),
        Err(e) => return (0, Some(format!("models を読めない: {e}"))),
    };
    let mut removed = 0;
    let mut error = None;
    for entry in entries.flatten() {
        let path = entry.path();
        if path.is_file() && is_vrm(&path) {
            match fs::remove_file(&path) {
                Ok(()) => removed += 1,
                Err(e) => {
                    error.get_or_insert(format!("{} を消せない: {e}", path.display()));
                }
            }
        }
    }
    (removed, error)
}

/// 管理キーと `models/` の `.vrm` を、片方が失敗してももう片方まで消す。
fn apply_reset(root: &Path) -> ResetOutcome {
    let settings_error = reset_settings(root).err();
    let (removed, models_error) = reset_models(root);
    let errors: Vec<String> = settings_error.into_iter().chain(models_error).collect();
    ResetOutcome {
        removed,
        error: (!errors.is_empty()).then(|| errors.join(" / ")),
    }
}

/// `src` を `models/mascot.vrm` へ置き換え、元のファイル名を返す。
/// 途中経路は `.vrm` で終わらせない（core の assetCatalog が `*.vrm` を走査するため）。
fn install_vrm(root: &Path, src: &Path) -> Result<String, String> {
    if !is_vrm(src) {
        return Err("not_vrm".into());
    }
    let name = src
        .file_name()
        .map(|n| n.to_string_lossy().into_owned())
        .ok_or("not_vrm")?;
    let io = |e: std::io::Error| format!("VRM を置けない: {e}");
    let dir = models_dir(root);
    fs::create_dir_all(&dir).map_err(io)?;
    let tmp = dir.join("mascot.vrm.chatter-agent.tmp");
    fs::copy(src, &tmp).map_err(io)?;
    fs::rename(&tmp, dir.join("mascot.vrm")).map_err(io)?;
    Ok(name)
}

async fn blocking<T: Send + 'static>(
    job: impl FnOnce() -> Result<T, String> + Send + 'static,
) -> Result<T, String> {
    tauri::async_runtime::spawn_blocking(job)
        .await
        .map_err(|e| e.to_string())?
}

#[tauri::command]
pub async fn mascot_settings_get(manager: State<'_, Manager>) -> Result<Value, String> {
    let root = root_of(&manager)?;
    blocking(move || read_settings(&settings_path(&root))).await
}

#[tauri::command]
pub async fn mascot_settings_set(
    manager: State<'_, Manager>,
    path: Vec<String>,
    value: Value,
) -> Result<(), String> {
    let root = root_of(&manager)?;
    blocking(move || apply_set(&root, &path, value)).await
}

/// ファイル選択。キャンセルは `None`。
#[tauri::command]
pub async fn pick_vrm(
    app: tauri::AppHandle,
    manager: State<'_, Manager>,
) -> Result<Option<String>, String> {
    let root = root_of(&manager)?;
    blocking(move || {
        // ★ フィルタは付けない。`.vrm` は動的 UTI で、フィルタを付けるとグレーアウトしうる。
        // ★ blocking_* はメインスレッドで呼ぶとデッドロックする。
        let Some(picked) = app.dialog().file().blocking_pick_file() else {
            return Ok(None);
        };
        let src = picked.into_path().map_err(|e| e.to_string())?;
        let name = install_vrm(&root, &src)?;
        apply_set(
            &root,
            &["character".into(), "vrm".into()],
            Value::from(name.clone()),
        )?;
        Ok(Some(name))
    })
    .await
}

#[tauri::command]
pub async fn confirm(
    app: tauri::AppHandle,
    title: String,
    message: String,
    ok: String,
    cancel: String,
) -> Result<bool, String> {
    blocking(move || {
        Ok(app
            .dialog()
            .message(message)
            .title(title)
            .kind(MessageDialogKind::Warning)
            .buttons(MessageDialogButtons::OkCancelCustom(ok, cancel))
            .blocking_show())
    })
    .await
}

/// 失敗は `ResetOutcome.error` に載せる。`Err` はルートを決められないときだけ。
#[tauri::command]
pub async fn mascot_reset(manager: State<'_, Manager>) -> Result<ResetOutcome, String> {
    let root = root_of(&manager)?;
    blocking(move || Ok(apply_reset(&root))).await
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn tmp(name: &str) -> PathBuf {
        let d = std::env::temp_dir().join(format!(
            "chatter-agent-app-mascot-{}-{name}",
            std::process::id()
        ));
        let _ = fs::remove_dir_all(&d);
        fs::create_dir_all(&d).unwrap();
        d
    }

    fn p(keys: &[&str]) -> Vec<String> {
        keys.iter().map(|k| k.to_string()).collect()
    }

    fn load(root: &Path) -> Value {
        serde_json::from_str(&fs::read_to_string(settings_path(root)).unwrap()).unwrap()
    }

    fn seed(root: &Path, text: &str) {
        fs::create_dir_all(settings_path(root).parent().unwrap()).unwrap();
        fs::write(settings_path(root), text).unwrap();
    }

    #[test]
    fn set_keeps_unknown_and_xr_keys() {
        let root = tmp("keep");
        seed(
            &root,
            &json!({
                "version": 1,
                "audio": {"mute": true, "volume": 0.2},
                "connection": {"host": "h"},
                "xr": {"scale": 2},
                "character": {"walk": {"on": true}, "blink": true},
                "future": [1, 2]
            })
            .to_string(),
        );
        apply_set(&root, &p(&["audio", "volume"]), json!(0.5)).unwrap();
        apply_set(&root, &p(&["character", "blink"]), json!(false)).unwrap();
        assert_eq!(
            load(&root),
            json!({
                "version": 1,
                "audio": {"mute": true, "volume": 0.5},
                "connection": {"host": "h"},
                "xr": {"scale": 2},
                "character": {"walk": {"on": true}, "blink": false},
                "future": [1, 2]
            })
        );
    }

    #[test]
    fn set_creates_file_dirs_and_version() {
        let root = tmp("create");
        apply_set(&root, &p(&["display", "frameRate"]), json!(60)).unwrap();
        assert_eq!(
            load(&root),
            json!({"version": 1, "display": {"frameRate": 60}})
        );
        assert!(!settings_path(&root)
            .with_extension("json.chatter-agent.tmp")
            .exists());
    }

    #[test]
    fn integers_stay_integers_and_indent_is_two_spaces() {
        let root = tmp("int");
        let value: Value = serde_json::from_str("30").unwrap();
        apply_set(&root, &p(&["display", "frameRate"]), value).unwrap();
        let text = fs::read_to_string(settings_path(&root)).unwrap();
        assert!(text.contains("\"frameRate\": 30\n") || text.contains("\"frameRate\": 30\r\n"));
        assert!(!text.contains("30.0"));
        assert!(text.contains("\n  \"display\""));
    }

    #[test]
    fn broken_or_non_object_json_is_not_overwritten() {
        for (i, bad) in ["{broken", "[1,2]", "\"x\""].into_iter().enumerate() {
            let root = tmp(&format!("broken{i}"));
            seed(&root, bad);
            assert!(apply_set(&root, &p(&["audio", "volume"]), json!(1)).is_err());
            assert!(apply_reset(&root).error.is_some());
            assert_eq!(fs::read_to_string(settings_path(&root)).unwrap(), bad);
        }
    }

    #[test]
    fn unmanaged_keys_are_rejected() {
        let root = tmp("unmanaged");
        for path in [
            p(&["audio", "mute"]),
            p(&["xr", "scale"]),
            p(&["character", "walk"]),
            p(&["audio"]),
            p(&["audio", "volume", "x"]),
            p(&[]),
        ] {
            assert!(apply_set(&root, &path, json!(1)).is_err(), "{path:?}");
        }
        assert!(!settings_path(&root).exists());
    }

    #[test]
    fn tmp_name_differs_from_unity() {
        let t = settings_path(Path::new("/r")).with_extension("json.chatter-agent.tmp");
        assert!(t.ends_with("settings.json.chatter-agent.tmp"));
        assert_ne!(t, Path::new("/r/mascot/settings.json.tmp"));
    }

    #[test]
    fn reset_removes_only_managed_keys_and_vrm_files() {
        let root = tmp("reset");
        seed(
            &root,
            &json!({
                "version": 1,
                "audio": {"mute": true, "volume": 0.2, "muteHotKey": "cmd+m"},
                "ui": {"hideHotKey": "cmd+h"},
                "character": {"walk": {"on": 1}, "blink": false, "idleMotion": false,
                              "cursorGaze": false, "vrm": "a.vrm"},
                "display": {"frameRate": 60},
                "connection": {"host": "h"},
                "xr": {"scale": 2},
                "future": 1
            })
            .to_string(),
        );
        let models = models_dir(&root);
        fs::create_dir_all(&models).unwrap();
        for f in [
            "mascot.vrm",
            "other.VRM",
            "note.txt",
            "mascot.vrm.chatter-agent.tmp",
        ] {
            fs::write(models.join(f), "x").unwrap();
        }
        fs::create_dir_all(models.join("dir.vrm")).unwrap();

        let outcome = apply_reset(&root);
        assert_eq!((outcome.removed, outcome.error), (2, None));
        assert_eq!(
            load(&root),
            json!({
                "version": 1,
                "audio": {"mute": true},
                "ui": {},
                "character": {"walk": {"on": 1}},
                "display": {},
                "connection": {"host": "h"},
                "xr": {"scale": 2},
                "future": 1
            })
        );
        assert!(models.join("note.txt").exists());
        assert!(models.join("mascot.vrm.chatter-agent.tmp").exists());
        assert!(models.join("dir.vrm").is_dir());
        assert!(!models.join("mascot.vrm").exists());
    }

    #[test]
    fn reset_without_files_does_nothing() {
        let root = tmp("reset-empty");
        let outcome = apply_reset(&root);
        assert_eq!((outcome.removed, outcome.error), (0, None));
        assert!(!settings_path(&root).exists());
    }

    #[test]
    fn reset_removes_vrm_even_when_settings_are_broken() {
        let root = tmp("reset-broken");
        seed(&root, "{broken");
        let models = models_dir(&root);
        fs::create_dir_all(&models).unwrap();
        fs::write(models.join("a.vrm"), "x").unwrap();
        fs::write(models.join("b.VRM"), "x").unwrap();

        let outcome = apply_reset(&root);
        assert_eq!(outcome.removed, 2);
        assert!(outcome.error.is_some());
        assert!(!models.join("a.vrm").exists());
        assert_eq!(fs::read_to_string(settings_path(&root)).unwrap(), "{broken");
    }

    #[test]
    fn vrm_extension_check() {
        for (name, ok) in [
            ("a.vrm", true),
            ("a.VRM", true),
            ("a.glb", false),
            ("vrm", false),
            ("a.vrm.txt", false),
        ] {
            assert_eq!(is_vrm(Path::new(name)), ok, "{name}");
        }
    }

    #[test]
    fn install_copies_and_renames_without_leftovers() {
        let root = tmp("install");
        let src_dir = tmp("install-src");
        let src = src_dir.join("Alice.vrm");
        fs::write(&src, b"new").unwrap();
        let models = models_dir(&root);
        fs::create_dir_all(&models).unwrap();
        fs::write(models.join("mascot.vrm"), b"old").unwrap();

        assert_eq!(install_vrm(&root, &src).unwrap(), "Alice.vrm");
        assert_eq!(fs::read(models.join("mascot.vrm")).unwrap(), b"new");
        assert!(!models.join("mascot.vrm.chatter-agent.tmp").exists());
        assert!(src.exists());

        let bad = src_dir.join("x.glb");
        fs::write(&bad, b"x").unwrap();
        assert_eq!(install_vrm(&root, &bad).unwrap_err(), "not_vrm");
        assert_eq!(fs::read(models.join("mascot.vrm")).unwrap(), b"new");
    }
}
