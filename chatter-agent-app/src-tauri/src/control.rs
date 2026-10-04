//! chatter-agent-server の制御 API を Rust から叩く。
//! WebView の fetch には `Origin` が付き、server はそれを 403 にする。この絞りは緩めず、Origin を持たない
//! ここから送る。

use std::io::{Read, Write};
use std::net::{IpAddr, TcpStream, ToSocketAddrs};
use std::path::PathBuf;
use std::time::Duration;

use serde::Serialize;
use serde_json::Value;
use tauri::State;

use crate::server::{runtime_root, Env, Manager};

const DEFAULT_PORT: u16 = 8570;
const DEFAULT_HOST: &str = "127.0.0.1";
const CONNECT_TIMEOUT: Duration = Duration::from_secs(2);
const CONFIG_TIMEOUT: Duration = Duration::from_secs(10);
/// 合成を待つ経路（話者一覧・テスト音声）。
const SYNTHESIS_TIMEOUT: Duration = Duration::from_secs(70);

/// 設定パネルが使う口だけ。`/v1/summary/preview` は課金されるので入れない。
const ALLOWED: &[(&str, &str)] = &[
    ("GET", "/v1/config"),
    ("PATCH", "/v1/config"),
    ("GET", "/v1/speakers"),
    ("POST", "/v1/tts/preview"),
    ("GET", "/v1/pairing"),
    ("POST", "/v1/pairing"),
];

#[derive(Debug, PartialEq)]
pub struct Target {
    host: String,
    port: u16,
}

/// 送れなかった理由。JS へは `code()` の文字列で渡す。
#[derive(Debug, PartialEq)]
pub enum Failure {
    Unreachable,
    UnsupportedHost,
    NotAllowed,
    BadResponse,
}

impl Failure {
    fn code(&self) -> &'static str {
        match self {
            Failure::Unreachable => "unreachable",
            Failure::UnsupportedHost => "unsupported_host",
            Failure::NotAllowed => "not_allowed",
            Failure::BadResponse => "bad_response",
        }
    }
}

/// JS へ返すエラー。`status` が 0 なら送れていない（`body` はコード）。
#[derive(Debug, Serialize)]
pub struct ApiError {
    status: u16,
    body: String,
}

impl From<Failure> for ApiError {
    fn from(f: Failure) -> Self {
        ApiError {
            status: 0,
            body: f.code().into(),
        }
    }
}

/// core の `toInt` と同じ規則で、1–65535 の整数値（`9000.0` や前後に空白のある文字列も受ける）。
fn parse_port(v: &Value) -> Option<u16> {
    let n = match v {
        Value::Number(n) => n.as_f64()?,
        Value::String(s) => s.trim().parse::<f64>().ok()?,
        _ => return None,
    };
    if n.fract() != 0.0 || !(1.0..=65535.0).contains(&n) {
        return None;
    }
    Some(n as u16)
}

fn env_nonempty<'a>(env: &'a Env, key: &str) -> Option<&'a str> {
    env.get(key).map(String::as_str).filter(|v| !v.is_empty())
}

/// server が読む config.json（`CHATTER_AGENT_CONFIG` か ランタイムルート直下）。無い・壊れているときは None。
fn read_config(env: &Env) -> Option<Value> {
    let path = match env_nonempty(env, "CHATTER_AGENT_CONFIG") {
        Some(p) => PathBuf::from(p),
        None => runtime_root(env)?.join("config.json"),
    };
    serde_json::from_str(&std::fs::read_to_string(path).ok()?).ok()
}

fn is_loopback(host: &str) -> bool {
    host == "localhost" || host.parse::<IpAddr>().is_ok_and(|a| a.is_loopback())
}

/// 環境変数 > config.json > 既定。毎回解決し直す。
/// server は相手がループバックでないと書き込みを 404、GET を 401 にするので、LAN の特定 IP へは繋がない。
pub fn resolve_target(env: &Env) -> Result<Target, Failure> {
    let config = read_config(env);
    let from_config = |key: &str| config.as_ref().and_then(|c| c.get(key));

    let port = env_nonempty(env, "CHATTER_AGENT_PORT")
        .and_then(|s| parse_port(&Value::String(s.into())))
        .or_else(|| from_config("port").and_then(parse_port))
        .unwrap_or(DEFAULT_PORT);

    let host = env_nonempty(env, "CHATTER_AGENT_HOST")
        .map(str::trim)
        .filter(|h| !h.is_empty())
        .or_else(|| {
            from_config("host")
                .and_then(Value::as_str)
                .map(str::trim)
                .filter(|h| !h.is_empty())
        })
        .unwrap_or(DEFAULT_HOST);
    let host = match host {
        "0.0.0.0" | "::" => DEFAULT_HOST,
        h if is_loopback(h) => h,
        _ => return Err(Failure::UnsupportedHost),
    };
    Ok(Target {
        host: host.into(),
        port,
    })
}

fn is_allowed(method: &str, path: &str) -> bool {
    ALLOWED.contains(&(method, path))
}

fn read_timeout(path: &str) -> Duration {
    if matches!(path, "/v1/config" | "/v1/pairing") {
        CONFIG_TIMEOUT
    } else {
        SYNTHESIS_TIMEOUT
    }
}

/// 応答をヘッダと本文に分ける。本文は WAV のバイナリもありうるのでバイト列のまま扱う。
fn parse_response(raw: &[u8]) -> Result<(u16, Vec<u8>), Failure> {
    let split = raw
        .windows(4)
        .position(|w| w == b"\r\n\r\n")
        .ok_or(Failure::BadResponse)?;
    let head = String::from_utf8_lossy(&raw[..split]);
    let status = head
        .lines()
        .next()
        .filter(|l| l.starts_with("HTTP/"))
        .and_then(|l| l.split_whitespace().nth(1))
        .and_then(|s| s.parse().ok())
        .ok_or(Failure::BadResponse)?;
    Ok((status, raw[split + 4..].to_vec()))
}

// ponytail: HTTP/1.0 の読み切り。keep-alive / chunked / TLS が要るなら ureq
fn http(
    target: &Target,
    method: &str,
    path: &str,
    body: Option<&[u8]>,
    timeout: Duration,
) -> Result<(u16, Vec<u8>), Failure> {
    let addrs = (target.host.as_str(), target.port)
        .to_socket_addrs()
        .map_err(|_| Failure::Unreachable)?;
    let mut stream = addrs
        .into_iter()
        .find_map(|a| TcpStream::connect_timeout(&a, CONNECT_TIMEOUT).ok())
        .ok_or(Failure::Unreachable)?;
    let _ = stream.set_read_timeout(Some(timeout));
    let _ = stream.set_write_timeout(Some(CONFIG_TIMEOUT));

    let host = if target.host.contains(':') {
        format!("[{}]:{}", target.host, target.port)
    } else {
        format!("{}:{}", target.host, target.port)
    };
    let mut req = format!("{method} {path} HTTP/1.0\r\nHost: {host}\r\n");
    if let Some(b) = body {
        req.push_str(&format!(
            "Content-Type: application/json\r\nContent-Length: {}\r\n",
            b.len()
        ));
    }
    req.push_str("\r\n");
    let mut payload = req.into_bytes();
    payload.extend_from_slice(body.unwrap_or_default());
    stream
        .write_all(&payload)
        .map_err(|_| Failure::Unreachable)?;

    let mut raw = Vec::new();
    stream
        .read_to_end(&mut raw)
        .map_err(|_| Failure::Unreachable)?;
    parse_response(&raw)
}

/// 許可リストを通ったものだけを送り、2xx なら本文、それ以外は `ApiError` にする。
fn call(
    target: &Target,
    method: &str,
    path: &str,
    body: Option<&Value>,
) -> Result<Vec<u8>, ApiError> {
    if !is_allowed(method, path) {
        return Err(Failure::NotAllowed.into());
    }
    let body = body.map(|b| b.to_string().into_bytes());
    let (status, bytes) = http(target, method, path, body.as_deref(), read_timeout(path))?;
    if (200..300).contains(&status) {
        Ok(bytes)
    } else {
        Err(ApiError {
            status,
            body: String::from_utf8_lossy(&bytes).into_owned(),
        })
    }
}

/// 同期コマンドはメインスレッドで走りトレイごと固まるので、`async` にしてブロッキング用スレッドへ逃がす。
#[tauri::command]
pub async fn server_request(
    manager: State<'_, Manager>,
    method: String,
    path: String,
    body: Option<Value>,
) -> Result<tauri::ipc::Response, ApiError> {
    let env = manager.env();
    let bytes = tauri::async_runtime::spawn_blocking(move || {
        let target = resolve_target(&env)?;
        call(&target, &method, &path, body.as_ref())
    })
    .await
    .map_err(|_| ApiError::from(Failure::Unreachable))??;
    Ok(tauri::ipc::Response::new(bytes))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::net::TcpListener;
    use std::sync::mpsc;
    use std::thread;

    #[test]
    fn read_timeout_is_long_only_for_synthesis() {
        assert_eq!(read_timeout("/v1/config"), CONFIG_TIMEOUT);
        assert_eq!(read_timeout("/v1/pairing"), CONFIG_TIMEOUT);
        assert_eq!(read_timeout("/v1/tts/preview"), SYNTHESIS_TIMEOUT);
    }

    fn env_of(pairs: &[(&str, &str)]) -> Env {
        pairs
            .iter()
            .map(|(k, v)| (k.to_string(), v.to_string()))
            .collect()
    }

    fn tmp(name: &str) -> PathBuf {
        let d = std::env::temp_dir().join(format!(
            "chatter-agent-app-control-{}-{name}",
            std::process::id()
        ));
        let _ = fs::remove_dir_all(&d);
        fs::create_dir_all(&d).unwrap();
        d
    }

    /// config.json を置いた環境。
    fn env_with_config(name: &str, config: &str, extra: &[(&str, &str)]) -> Env {
        let dir = tmp(name);
        let path = dir.join("config.json");
        fs::write(&path, config).unwrap();
        let mut env = env_of(extra);
        env.insert("CHATTER_AGENT_CONFIG".into(), path.display().to_string());
        env
    }

    fn target(env: &Env) -> (String, u16) {
        let t = resolve_target(env).unwrap();
        (t.host, t.port)
    }

    #[test]
    fn defaults_without_env_or_config() {
        let env = env_of(&[("CHATTER_AGENT_CONFIG", "/nonexistent/config.json")]);
        assert_eq!(target(&env), ("127.0.0.1".into(), 8570));
    }

    #[test]
    fn env_beats_config_beats_default() {
        let config = r#"{"port":9000,"host":"localhost"}"#;
        let env = env_with_config("prio1", config, &[]);
        assert_eq!(target(&env), ("localhost".into(), 9000));

        let env = env_with_config(
            "prio2",
            config,
            &[
                ("CHATTER_AGENT_PORT", "9100"),
                ("CHATTER_AGENT_HOST", "::1"),
            ],
        );
        assert_eq!(target(&env), ("::1".into(), 9100));
    }

    #[test]
    fn config_is_found_under_runtime_root() {
        let home = tmp("root");
        let dir = home.join(".config").join("chatter-agent");
        fs::create_dir_all(&dir).unwrap();
        fs::write(dir.join("config.json"), r#"{"port":9200}"#).unwrap();
        let env = env_of(&[("HOME", home.to_str().unwrap())]);
        assert_eq!(target(&env).1, 9200);
    }

    #[test]
    fn port_accepts_digit_strings_and_rejects_the_rest() {
        let from = |config: &str| target(&env_with_config("port", config, &[])).1;
        assert_eq!(from(r#"{"port":"9300"}"#), 9300);
        assert_eq!(from(r#"{"port":9000.0}"#), 9000);
        assert_eq!(from(r#"{"port":" 9100 "}"#), 9100);
        for bad in [
            r#"{"port":"inf"}"#,
            r#"{"port":"NaN"}"#,
            r#"{"port":0}"#,
            r#"{"port":65536}"#,
            r#"{"port":-1}"#,
            r#"{"port":1.5}"#,
            r#"{"port":"80a"}"#,
            r#"{"port":""}"#,
            r#"{"port":null}"#,
            "{broken",
        ] {
            assert_eq!(from(bad), DEFAULT_PORT, "{bad}");
        }
        // 環境変数が不正なら config へ落ちる。
        let env = env_with_config("port2", r#"{"port":9400}"#, &[("CHATTER_AGENT_PORT", "x")]);
        assert_eq!(target(&env).1, 9400);
    }

    #[test]
    fn wildcard_and_blank_hosts_become_loopback() {
        for host in ["0.0.0.0", "::", "  ", ""] {
            let config = format!(r#"{{"host":"{host}"}}"#);
            let env = env_with_config("wild", &config, &[]);
            assert_eq!(target(&env).0, "127.0.0.1", "{host:?}");
        }
        let env = env_with_config("wild2", "{}", &[("CHATTER_AGENT_HOST", "0.0.0.0")]);
        assert_eq!(target(&env).0, "127.0.0.1");
    }

    #[test]
    fn loopback_hosts_pass_and_others_are_unsupported() {
        for host in ["localhost", "127.0.0.1", "127.1.2.3", "::1", " ::1 "] {
            let config = format!(r#"{{"host":"{host}"}}"#);
            let env = env_with_config("lo", &config, &[]);
            assert_eq!(target(&env).0, host.trim(), "{host}");
        }
        for host in ["192.168.0.5", "example.com", "127.evil.example"] {
            let env = env_with_config("lan", "{}", &[("CHATTER_AGENT_HOST", host)]);
            assert_eq!(
                resolve_target(&env),
                Err(Failure::UnsupportedHost),
                "{host}"
            );
        }
    }

    #[test]
    fn response_is_split_as_bytes() {
        let mut raw = b"HTTP/1.0 200 OK\r\nContent-Length: 5\r\n\r\n".to_vec();
        raw.extend_from_slice(&[0, 0xff, b'\r', b'\n', 0x80]);
        let (status, body) = parse_response(&raw).unwrap();
        assert_eq!(status, 200);
        assert_eq!(body, [0, 0xff, b'\r', b'\n', 0x80]);
        for bad in [
            &b"HTTP/1.0 200 OK\r\n"[..],
            b"garbage\r\n\r\n",
            b"HTTP/1.0 x\r\n\r\n",
        ] {
            assert_eq!(parse_response(bad), Err(Failure::BadResponse));
        }
    }

    /// 1回だけ応答する偽 server。受け取った要求（生）を返す。
    fn fake_server(response: Vec<u8>) -> (Target, mpsc::Receiver<Vec<u8>>) {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        let (tx, rx) = mpsc::channel();
        thread::spawn(move || {
            let (mut s, _) = listener.accept().unwrap();
            let mut req = Vec::new();
            let mut chunk = [0u8; 1024];
            loop {
                let n = s.read(&mut chunk).unwrap();
                if n == 0 {
                    break;
                }
                req.extend_from_slice(&chunk[..n]);
                let Some(head_end) = req.windows(4).position(|w| w == b"\r\n\r\n") else {
                    continue;
                };
                let head = String::from_utf8_lossy(&req[..head_end]).to_lowercase();
                let len = head
                    .lines()
                    .find_map(|l| l.strip_prefix("content-length: "))
                    .and_then(|v| v.parse::<usize>().ok())
                    .unwrap_or(0);
                if req.len() >= head_end + 4 + len {
                    break;
                }
            }
            s.write_all(&response).unwrap();
            tx.send(req).unwrap();
        });
        (
            Target {
                host: "127.0.0.1".into(),
                port,
            },
            rx,
        )
    }

    #[test]
    fn success_returns_binary_body_and_sends_no_origin() {
        let mut resp = b"HTTP/1.0 200 OK\r\nContent-Type: audio/wav\r\n\r\n".to_vec();
        resp.extend_from_slice(&[b'R', b'I', 0, 0xff, 0x80]);
        let (t, rx) = fake_server(resp);
        let body = serde_json::json!({"text":"こんにちは"});
        let got = call(&t, "POST", "/v1/tts/preview", Some(&body)).unwrap();
        assert_eq!(got, [b'R', b'I', 0, 0xff, 0x80]);

        let req = String::from_utf8(rx.recv().unwrap()).unwrap();
        let lower = req.to_lowercase();
        assert!(req.starts_with("POST /v1/tts/preview HTTP/1.0\r\n"));
        assert!(!lower.contains("origin"));
        assert!(lower.contains("content-type: application/json"));
        assert!(lower.contains(&format!("host: 127.0.0.1:{}", t.port)));
        assert!(req.ends_with(&body.to_string()));
    }

    #[test]
    fn error_status_keeps_body() {
        let (t, _rx) = fake_server(b"HTTP/1.0 409 Conflict\r\n\r\n{\"error\":\"env\"}".to_vec());
        let e = call(&t, "PATCH", "/v1/config", Some(&serde_json::json!({}))).unwrap_err();
        assert_eq!((e.status, e.body.as_str()), (409, "{\"error\":\"env\"}"));
    }

    #[test]
    fn connection_failure_is_unreachable() {
        // 一度開いて閉じたポートへ繋ぐ。
        let port = TcpListener::bind("127.0.0.1:0")
            .unwrap()
            .local_addr()
            .unwrap()
            .port();
        let t = Target {
            host: "127.0.0.1".into(),
            port,
        };
        let e = call(&t, "GET", "/v1/config", None).unwrap_err();
        assert_eq!((e.status, e.body.as_str()), (0, "unreachable"));
    }

    #[test]
    fn disallowed_requests_are_not_sent() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        listener.set_nonblocking(true).unwrap();
        let t = Target {
            host: "127.0.0.1".into(),
            port: listener.local_addr().unwrap().port(),
        };
        for (m, p) in [
            ("POST", "/v1/summary/preview"),
            ("DELETE", "/v1/config"),
            ("GET", "/v1/tts/preview"),
            ("GET", "/audio/1-1.wav"),
            ("GET", "/v1/config?x=1"),
            ("POST", "/v1/pairing/claim"),
        ] {
            let e = call(&t, m, p, None).unwrap_err();
            assert_eq!((e.status, e.body.as_str()), (0, "not_allowed"), "{m} {p}");
        }
        assert!(listener.accept().is_err(), "接続が来てはいけない");
    }
}
