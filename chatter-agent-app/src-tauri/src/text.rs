//! 日英の文言。言語ごとに全項目を持つ構造体にして、訳し漏れをコンパイルで検出する。

use crate::server::{version_label, ExitInfo, Fail, Status, NODE_MIN};

pub struct Text {
    status_prefix: &'static str,
    starting: &'static str,
    running: fn(u32) -> String,
    stopping: &'static str,
    stopped: &'static str,
    crashed_code: fn(i32) -> String,
    crashed_signal: fn(i32) -> String,
    crashed_unknown: &'static str,
    external: fn(u32) -> String,
    cannot_prefix: &'static str,
    no_core_dir: &'static str,
    no_dist: &'static str,
    no_node_modules: &'static str,
    env: &'static str,
    node_missing: &'static str,
    node_old: fn(&str, &str) -> String,
    node_unknown: &'static str,
    spawn_failed: fn(&str) -> String,
    pub restart: &'static str,
    pub start: &'static str,
    pub open_log: &'static str,
    pub pick_core: &'static str,
    pub pick_core_title: &'static str,
    pub settings: &'static str,
    pub settings_title: &'static str,
    pub pairing: &'static str,
    pub pairing_title: &'static str,
    pub autostart: &'static str,
    pub quit: &'static str,
    pub mute: &'static str,
    pub show_mascot: &'static str,
    pub hide_mascot: &'static str,
    pub mascot_unavailable_title: &'static str,
    mascot_not_found: fn(&str) -> String,
    mascot_launch_failed: fn(&str) -> String,
}

pub const JA: Text = Text {
    status_prefix: "サーバー: ",
    starting: "起動中…",
    running: |pid| format!("起動中（pid {pid}）"),
    stopping: "停止中…",
    stopped: "停止",
    crashed_code: |c| format!("異常終了（終了コード {c}）"),
    crashed_signal: |s| format!("異常終了（シグナル {s}）"),
    crashed_unknown: "異常終了",
    external: |pid| format!("外で動いている（pid {pid}）"),
    cannot_prefix: "起動できません: ",
    no_core_dir: "core の場所を選んでください",
    no_dist: "dist が無い（core で npm run build）",
    no_node_modules: "node_modules が無い（core で npm install）",
    env: "ログインシェルから環境を取得できません",
    node_missing: "node が見つかりません",
    node_old: |v, min| format!("node {v} は古い（{min} 以上が必要）"),
    node_unknown: "node の版を取得できません",
    spawn_failed: |e| format!("起動に失敗（{e}）"),
    restart: "サーバーを再起動",
    start: "サーバーを起動",
    open_log: "ログを開く",
    pick_core: "core の場所を選ぶ…",
    pick_core_title: "chatter-agent の core フォルダを選ぶ",
    settings: "設定…",
    settings_title: "Chatter Agent 設定",
    pairing: "Android とペアリング…",
    pairing_title: "Android とペアリング",
    autostart: "ログイン時に起動",
    quit: "終了",
    mute: "ミュート",
    show_mascot: "マスコットを表示",
    hide_mascot: "マスコットを隠す",
    mascot_unavailable_title: "マスコットを表示できません",
    mascot_not_found: |places| {
        format!("ChatterMascot.app が見つかりません。探した場所:\n{places}\n\nマスコットは隠したままにします。")
    },
    mascot_launch_failed: |e| {
        format!("ChatterMascot.app を起動できません（{e}）。\n\nマスコットは隠したままにします。")
    },
};

pub const EN: Text = Text {
    status_prefix: "Server: ",
    starting: "Starting…",
    running: |pid| format!("Running (pid {pid})"),
    stopping: "Stopping…",
    stopped: "Stopped",
    crashed_code: |c| format!("Crashed (exit code {c})"),
    crashed_signal: |s| format!("Crashed (signal {s})"),
    crashed_unknown: "Crashed",
    external: |pid| format!("Running outside Chatter Agent (pid {pid})"),
    cannot_prefix: "Cannot start: ",
    no_core_dir: "choose the core folder",
    no_dist: "dist is missing (run npm run build in core)",
    no_node_modules: "node_modules is missing (run npm install in core)",
    env: "could not get the environment from the login shell",
    node_missing: "node not found",
    node_old: |v, min| format!("node {v} is too old ({min} or newer required)"),
    node_unknown: "could not determine the node version",
    spawn_failed: |e| format!("failed to spawn ({e})"),
    restart: "Restart Server",
    start: "Start Server",
    open_log: "Open Log",
    pick_core: "Choose Core Folder…",
    pick_core_title: "Choose the chatter-agent core folder",
    settings: "Settings…",
    settings_title: "Chatter Agent Settings",
    pairing: "Pair with Android…",
    pairing_title: "Pair with Android",
    autostart: "Launch at Login",
    quit: "Quit",
    mute: "Mute",
    show_mascot: "Show Mascot",
    hide_mascot: "Hide Mascot",
    mascot_unavailable_title: "Cannot Show the Mascot",
    mascot_not_found: |places| {
        format!("ChatterMascot.app was not found. Searched:\n{places}\n\nThe mascot stays hidden.")
    },
    mascot_launch_failed: |e| {
        format!("Could not launch ChatterMascot.app ({e}).\n\nThe mascot stays hidden.")
    },
};

/// OS のロケールが `ja` で始まるか。
pub fn is_ja() -> bool {
    sys_locale::get_locale().is_some_and(|l| l.starts_with("ja"))
}

pub fn current() -> &'static Text {
    if is_ja() {
        &JA
    } else {
        &EN
    }
}

/// 画面の言語。WKWebView の `navigator.language` はアプリのローカライズに引かれて OS と食い違いうる。
#[tauri::command]
pub fn lang() -> &'static str {
    if is_ja() {
        "ja"
    } else {
        "en"
    }
}

impl Text {
    /// 起動できない理由（「起動できません: …」）。
    pub fn cannot(&self, f: &Fail) -> String {
        let reason = match f {
            Fail::NoCoreDir => self.no_core_dir.into(),
            Fail::NoDist => self.no_dist.into(),
            Fail::NoNodeModules => self.no_node_modules.into(),
            Fail::Env => self.env.into(),
            Fail::NodeMissing => self.node_missing.into(),
            Fail::NodeUnknown => self.node_unknown.into(),
            Fail::NodeOld(v) => (self.node_old)(v, &version_label(NODE_MIN)),
            Fail::SpawnFailed(e) => (self.spawn_failed)(e),
        };
        format!("{}{reason}", self.cannot_prefix)
    }

    pub fn mascot_not_found(&self, places: &str) -> String {
        (self.mascot_not_found)(places)
    }

    pub fn mascot_launch_failed(&self, e: &str) -> String {
        (self.mascot_launch_failed)(e)
    }

    /// 状態欄の文言（「サーバー: …」）。
    pub fn status(&self, s: &Status) -> String {
        let body = match s {
            Status::Starting => self.starting.into(),
            Status::Running(pid) => (self.running)(*pid),
            Status::Stopping => self.stopping.into(),
            Status::Stopped => self.stopped.into(),
            Status::Crashed(ExitInfo::Code(c)) => (self.crashed_code)(*c),
            Status::Crashed(ExitInfo::Signal(sig)) => (self.crashed_signal)(*sig),
            Status::Crashed(ExitInfo::Unknown) => self.crashed_unknown.into(),
            Status::External(pid) => (self.external)(*pid),
            Status::Cannot(f) => self.cannot(f),
        };
        format!("{}{body}", self.status_prefix)
    }
}
