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
    pub autostart: &'static str,
    pub quit: &'static str,
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
    autostart: "ログイン時に起動",
    quit: "終了",
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
    autostart: "Launch at Login",
    quit: "Quit",
};

/// OS のロケールが `ja` で始まれば日本語、それ以外は英語。
pub fn current() -> &'static Text {
    match sys_locale::get_locale() {
        Some(l) if l.starts_with("ja") => &JA,
        _ => &EN,
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
