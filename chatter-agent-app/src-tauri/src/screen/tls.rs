//! 画面の配信の TLS。端末の証明書は信頼の連鎖ではなく指紋で固定し、鍵の持ち主であることだけを署名で確かめる。

use std::collections::HashSet;
use std::io::{Read, Write};
use std::net::IpAddr;
use std::os::unix::fs::OpenOptionsExt;
use std::path::Path;
use std::sync::Arc;
use std::time::{Duration, Instant};

use rustls::client::danger::HandshakeSignatureValid;
use rustls::crypto::{ring as provider, CryptoProvider, WebPkiSupportedAlgorithms};
use rustls::pki_types::{CertificateDer, PrivateKeyDer, UnixTime};
use rustls::server::danger::{ClientCertVerified, ClientCertVerifier};
use rustls::{
    DigitallySignedStruct, DistinguishedName, Error, ServerConfig, ServerConnection,
    SignatureScheme,
};

pub struct Identity {
    pub cert_der: Vec<u8>,
    pub key_der: Vec<u8>,
}

fn write_private(path: &Path, bytes: &[u8]) -> std::io::Result<()> {
    let tmp = path.with_extension("tmp");
    let mut f = std::fs::OpenOptions::new()
        .write(true)
        .create(true)
        .truncate(true)
        .mode(0o600)
        .open(&tmp)?;
    f.write_all(bytes)?;
    f.sync_all()?;
    std::fs::rename(tmp, path)
}

/// 一度作ったものを使い回す。片方でも欠けていれば作り直す。
pub fn load_or_create(dir: &Path) -> std::io::Result<Identity> {
    let (cert_path, key_path) = (dir.join("cert.der"), dir.join("key.der"));
    if let (Ok(cert_der), Ok(key_der)) = (std::fs::read(&cert_path), std::fs::read(&key_path)) {
        return Ok(Identity { cert_der, key_der });
    }
    std::fs::create_dir_all(dir)?;
    let made = rcgen::generate_simple_self_signed(vec!["chatter-agent".to_string()])
        .map_err(std::io::Error::other)?;
    let identity = Identity {
        cert_der: made.cert.der().to_vec(),
        key_der: made.signing_key.serialize_der(),
    };
    // 鍵を先に置く。証明書だけが残る状態を作らない。
    write_private(&key_path, &identity.key_der)?;
    write_private(&cert_path, &identity.cert_der)?;
    Ok(identity)
}

/// 証明書の DER 全体の SHA-256（小文字の16進）。
pub fn fingerprint(cert_der: &[u8]) -> String {
    ring::digest::digest(&ring::digest::SHA256, cert_der)
        .as_ref()
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect()
}

/// 指紋の先頭 16 桁を `XXXX-XXXX-XXXX-XXXX` で。
/// 短いと中間者が同じコードの証明書を探し当てられ、端末の証明書は変わらないので探す時間に期限が無い。
pub fn confirm_code(fp: &str) -> String {
    let head: Vec<char> = fp.chars().take(16).flat_map(char::to_uppercase).collect();
    head.chunks(4)
        .map(|c| c.iter().collect::<String>())
        .collect::<Vec<_>>()
        .join("-")
}

/// どの証明書も通すが、TLS 1.3 の署名は実際に検証する。指紋の判定は接続の後に呼び出し側が行う。
#[derive(Debug)]
struct AnyCertWithKeyProof {
    algs: WebPkiSupportedAlgorithms,
}

impl ClientCertVerifier for AnyCertWithKeyProof {
    fn root_hint_subjects(&self) -> &[DistinguishedName] {
        &[]
    }

    fn verify_client_cert(
        &self,
        _end_entity: &CertificateDer<'_>,
        _intermediates: &[CertificateDer<'_>],
        _now: UnixTime,
    ) -> Result<ClientCertVerified, Error> {
        Ok(ClientCertVerified::assertion())
    }

    fn verify_tls12_signature(
        &self,
        _message: &[u8],
        _cert: &CertificateDer<'_>,
        _dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, Error> {
        Err(Error::PeerIncompatible(
            rustls::PeerIncompatible::Tls12NotOffered,
        ))
    }

    fn verify_tls13_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, Error> {
        rustls::crypto::verify_tls13_signature(message, cert, dss, &self.algs)
    }

    fn supported_verify_schemes(&self) -> Vec<SignatureScheme> {
        self.algs.supported_schemes()
    }
}

/// TLS 1.3 のみ・クライアント証明書必須。
pub fn server_config(identity: &Identity) -> Result<Arc<ServerConfig>, Error> {
    let crypto: Arc<CryptoProvider> = Arc::new(provider::default_provider());
    let verifier = AnyCertWithKeyProof {
        algs: crypto.signature_verification_algorithms,
    };
    let key = PrivateKeyDer::try_from(identity.key_der.clone())
        .map_err(|e| Error::General(e.to_string()))?;
    let mut config = ServerConfig::builder_with_provider(crypto)
        .with_protocol_versions(&[&rustls::version::TLS13])?
        .with_client_cert_verifier(Arc::new(verifier))
        .with_single_cert(vec![CertificateDer::from(identity.cert_der.clone())], key)?;
    // 再開は使わない。クライアントは何も読まないので、チケットを送っても溜まるだけ。
    config.send_tls13_tickets = 0;
    Ok(Arc::new(config))
}

/// 握手を `limit` までに終える。読み書きごとの期限だけでは、少しずつ送り続ける相手を切れない。
pub fn handshake(
    conn: &mut ServerConnection,
    io: &mut (impl Read + Write),
    limit: Duration,
) -> Result<(), String> {
    let deadline = Instant::now() + limit;
    while conn.is_handshaking() {
        if Instant::now() >= deadline {
            return Err("handshake timed out".into());
        }
        if conn.wants_write() {
            conn.write_tls(io).map_err(|e| e.to_string())?;
            continue;
        }
        if conn.read_tls(io).map_err(|e| e.to_string())? == 0 {
            return Err("closed during handshake".into());
        }
        if let Err(e) = conn.process_new_packets() {
            // 拒否の理由を相手へ伝える。届かなくても結果は変わらない。
            let _ = conn.write_tls(io);
            return Err(e.to_string());
        }
    }
    while conn.wants_write() {
        conn.write_tls(io).map_err(|e| e.to_string())?;
    }
    Ok(())
}

#[derive(Debug, PartialEq)]
pub enum Decision {
    Attach,
    Reject,
    Ask,
}

/// 承認済みの指紋は、拒否した IP からでも通す。IP の拒否は未知の鍵を黙らせるためのもので、
/// 秘密鍵の持ち主であることは署名で確かめ済みなので、同じ NAT の裏にいるだけで締め出さない。
pub fn decide(
    fp: &str,
    ip: IpAddr,
    approved: bool,
    denied_fps: &HashSet<String>,
    denied_ips: &HashSet<IpAddr>,
    pending_busy: bool,
) -> Decision {
    if denied_fps.contains(fp) {
        Decision::Reject
    } else if approved {
        Decision::Attach
    } else if denied_ips.contains(&ip) || pending_busy {
        Decision::Reject
    } else {
        Decision::Ask
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::net::{TcpListener, TcpStream};
    use std::time::Duration;

    use rustls::client::danger::{ServerCertVerified, ServerCertVerifier};
    use rustls::client::ResolvesClientCert;
    use rustls::sign::CertifiedKey;
    use rustls::{ClientConfig, ClientConnection, ServerConnection};

    #[derive(Debug)]
    struct TrustServer(WebPkiSupportedAlgorithms);
    impl ServerCertVerifier for TrustServer {
        fn verify_server_cert(
            &self,
            _: &CertificateDer<'_>,
            _: &[CertificateDer<'_>],
            _: &rustls::pki_types::ServerName<'_>,
            _: &[u8],
            _: UnixTime,
        ) -> Result<ServerCertVerified, Error> {
            Ok(ServerCertVerified::assertion())
        }
        fn verify_tls12_signature(
            &self,
            m: &[u8],
            c: &CertificateDer<'_>,
            d: &DigitallySignedStruct,
        ) -> Result<HandshakeSignatureValid, Error> {
            rustls::crypto::verify_tls12_signature(m, c, d, &self.0)
        }
        fn verify_tls13_signature(
            &self,
            m: &[u8],
            c: &CertificateDer<'_>,
            d: &DigitallySignedStruct,
        ) -> Result<HandshakeSignatureValid, Error> {
            rustls::crypto::verify_tls13_signature(m, c, d, &self.0)
        }
        fn supported_verify_schemes(&self) -> Vec<SignatureScheme> {
            self.0.supported_schemes()
        }
    }

    #[derive(Debug)]
    struct Resolver(Arc<CertifiedKey>);
    impl ResolvesClientCert for Resolver {
        fn resolve(&self, _: &[&[u8]], _: &[SignatureScheme]) -> Option<Arc<CertifiedKey>> {
            Some(self.0.clone())
        }
        fn has_certs(&self) -> bool {
            true
        }
    }

    fn make_identity() -> Identity {
        let made = rcgen::generate_simple_self_signed(vec!["test".into()]).unwrap();
        Identity {
            cert_der: made.cert.der().to_vec(),
            key_der: made.signing_key.serialize_der(),
        }
    }

    fn signer(key_der: &[u8]) -> Arc<dyn rustls::sign::SigningKey> {
        provider::sign::any_supported_type(&PrivateKeyDer::try_from(key_der.to_vec()).unwrap())
            .unwrap()
    }

    /// クライアントが提示する証明書と署名鍵。`None` は証明書なし。
    enum Client {
        NoCert,
        Cert { cert: Vec<u8>, key: Vec<u8> },
    }

    /// 戻りはサーバー側の握手の結果と、サーバーが見たクライアント証明書。
    fn handshake(
        client: Client,
        versions: &[&'static rustls::SupportedProtocolVersion],
    ) -> (Result<(), String>, Option<Vec<u8>>) {
        let server = Arc::new(server_config(&make_identity()).unwrap());
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = listener.local_addr().unwrap();
        let handle = std::thread::spawn(move || {
            let (mut tcp, _) = listener.accept().unwrap();
            tcp.set_read_timeout(Some(Duration::from_secs(5))).unwrap();
            let mut conn = ServerConnection::new((*server).clone()).unwrap();
            let r = super::handshake(&mut conn, &mut tcp, Duration::from_secs(5));
            let cert = conn
                .peer_certificates()
                .and_then(|c| c.first().map(|c| c.to_vec()));
            (r, cert)
        });

        let crypto = Arc::new(provider::default_provider());
        let builder = ClientConfig::builder_with_provider(crypto.clone())
            .with_protocol_versions(versions)
            .unwrap()
            .dangerous()
            .with_custom_certificate_verifier(Arc::new(TrustServer(
                crypto.signature_verification_algorithms,
            )));
        let config = match client {
            Client::NoCert => builder.with_no_client_auth(),
            Client::Cert { cert, key } => {
                builder.with_client_cert_resolver(Arc::new(Resolver(Arc::new(CertifiedKey::new(
                    vec![CertificateDer::from(cert)],
                    signer(&key),
                )))))
            }
        };
        let mut tcp = TcpStream::connect(addr).unwrap();
        tcp.set_read_timeout(Some(Duration::from_secs(5))).unwrap();
        let mut conn =
            ClientConnection::new(Arc::new(config), "localhost".try_into().unwrap()).unwrap();
        // クライアント側の結果は見ない。TLS 1.3 では、サーバーが拒否する前に握手を終えたつもりになる。
        while conn.is_handshaking() {
            if conn.complete_io(&mut tcp).is_err() {
                break;
            }
        }
        let _ = conn.complete_io(&mut tcp);
        handle.join().unwrap()
    }

    const TLS13: &[&rustls::SupportedProtocolVersion] = &[&rustls::version::TLS13];

    #[test]
    fn client_without_certificate_fails() {
        let (r, _) = handshake(Client::NoCert, TLS13);
        assert!(r.is_err(), "{r:?}");
    }

    #[test]
    fn certificate_without_its_private_key_fails() {
        let (a, b) = (make_identity(), make_identity());
        let (r, _) = handshake(
            Client::Cert {
                cert: a.cert_der,
                key: b.key_der,
            },
            TLS13,
        );
        assert!(r.is_err(), "{r:?}");
    }

    #[test]
    fn valid_client_certificate_succeeds_and_fingerprint_matches() {
        let id = make_identity();
        let (r, seen) = handshake(
            Client::Cert {
                cert: id.cert_der.clone(),
                key: id.key_der.clone(),
            },
            TLS13,
        );
        assert!(r.is_ok(), "{r:?}");
        assert_eq!(fingerprint(&seen.unwrap()), fingerprint(&id.cert_der));
    }

    #[test]
    fn tls12_only_client_fails() {
        let id = make_identity();
        let (r, _) = handshake(
            Client::Cert {
                cert: id.cert_der,
                key: id.key_der,
            },
            &[&rustls::version::TLS12],
        );
        assert!(r.is_err(), "{r:?}");
    }

    #[test]
    fn slow_client_is_cut_at_the_deadline() {
        use std::io::Write as _;
        let server = server_config(&make_identity()).unwrap();
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = listener.local_addr().unwrap();
        let stop = Arc::new(std::sync::atomic::AtomicBool::new(false));
        let flag = stop.clone();
        let slow = std::thread::spawn(move || {
            let mut tcp = TcpStream::connect(addr).unwrap();
            // 1 バイトずつ送り続けて、読み取りの期限には掛からないようにする
            while !flag.load(std::sync::atomic::Ordering::SeqCst) {
                let _ = tcp.write_all(&[0x16]);
                std::thread::sleep(Duration::from_millis(20));
            }
        });
        let (mut tcp, _) = listener.accept().unwrap();
        tcp.set_read_timeout(Some(Duration::from_secs(5))).unwrap();
        let mut conn = ServerConnection::new(server).unwrap();
        let started = Instant::now();
        let r = super::handshake(&mut conn, &mut tcp, Duration::from_millis(200));
        stop.store(true, std::sync::atomic::Ordering::SeqCst);
        slow.join().unwrap();
        assert!(r.is_err(), "{r:?}");
        assert!(started.elapsed() < Duration::from_secs(4));
    }

    #[test]
    fn fingerprint_and_code_format() {
        let fp = fingerprint(b"abc");
        assert_eq!(
            fp,
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
        assert_eq!(confirm_code(&fp), "BA78-16BF-8F01-CFEA");
    }

    #[test]
    fn identity_is_reused_and_private() {
        use std::os::unix::fs::PermissionsExt;
        let dir = std::env::temp_dir().join(format!("chatter-agent-tls-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        let a = load_or_create(&dir).unwrap();
        let b = load_or_create(&dir).unwrap();
        assert_eq!(a.cert_der, b.cert_der);
        for name in ["cert.der", "key.der"] {
            let mode = std::fs::metadata(dir.join(name))
                .unwrap()
                .permissions()
                .mode();
            assert_eq!(mode & 0o777, 0o600, "{name}");
        }
        std::fs::remove_file(dir.join("cert.der")).unwrap();
        let c = load_or_create(&dir).unwrap();
        assert_ne!(a.cert_der, c.cert_der);
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn decision_table() {
        let ip: IpAddr = "192.168.0.5".parse().unwrap();
        let other: IpAddr = "192.168.0.6".parse().unwrap();
        let none_fp = HashSet::new();
        let none_ip = HashSet::new();
        let d = |fp: &str, ip, ok, dfp: &HashSet<String>, dip: &HashSet<IpAddr>, busy| {
            decide(fp, ip, ok, dfp, dip, busy)
        };
        assert_eq!(
            d("a", ip, true, &none_fp, &none_ip, false),
            Decision::Attach
        );
        assert_eq!(d("a", ip, true, &none_fp, &none_ip, true), Decision::Attach);
        assert_eq!(d("a", ip, false, &none_fp, &none_ip, false), Decision::Ask);
        assert_eq!(
            d("a", ip, false, &none_fp, &none_ip, true),
            Decision::Reject
        );
        let dfp = HashSet::from(["a".to_string()]);
        let dip = HashSet::from([ip]);
        assert_eq!(
            d("a", other, false, &dfp, &none_ip, false),
            Decision::Reject
        );
        assert_eq!(d("a", other, true, &dfp, &none_ip, false), Decision::Reject);
        assert_eq!(d("b", ip, false, &none_fp, &dip, false), Decision::Reject);
        assert_eq!(d("b", other, false, &none_fp, &dip, false), Decision::Ask);
        assert_eq!(d("b", ip, true, &none_fp, &dip, false), Decision::Attach);
    }
}
