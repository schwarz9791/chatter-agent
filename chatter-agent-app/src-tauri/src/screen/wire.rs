//! 画面の配信の線上の形と、エンコード・配信の定数。

use std::collections::VecDeque;

pub const HELLO: u8 = 0;
pub const VIDEO: u8 = 1;
pub const KEEPALIVE: u8 = 2;

/// 出力の画素数の上限。これを超える画面は縦横比を保って縮める。
pub const MAX_PIXELS: u64 = 1920 * 1200;
pub const FPS: i32 = 30;
pub const BITRATE: i32 = 12_000_000;
pub const KEYFRAME_INTERVAL_SECS: f64 = 10.0;
/// 詰まったときに古い映像を持ち続けないための上限。
pub const QUEUE_LIMIT: usize = 6;

/// `u32be 本文の長さ | u8 種別 | 本文`
pub fn frame(kind: u8, payload: &[u8]) -> Vec<u8> {
    let mut out = Vec::with_capacity(5 + payload.len());
    out.extend_from_slice(&(payload.len() as u32).to_be_bytes());
    out.push(kind);
    out.extend_from_slice(payload);
    out
}

#[derive(Debug, PartialEq)]
pub struct Truncated;

/// 長さ前置きの NAL 列を開始コード形式にする。キーフレームでは先頭に SPS / PPS を付ける。
pub fn avcc_to_annexb(
    data: &[u8],
    nal_length_size: usize,
    parameter_sets: &[&[u8]],
) -> Result<Vec<u8>, Truncated> {
    const START: [u8; 4] = [0, 0, 0, 1];
    if !(1..=4).contains(&nal_length_size) {
        return Err(Truncated);
    }
    let mut out = Vec::with_capacity(data.len() + 64);
    for ps in parameter_sets {
        out.extend_from_slice(&START);
        out.extend_from_slice(ps);
    }
    let mut rest = data;
    while !rest.is_empty() {
        let (len_bytes, tail) = rest.split_at_checked(nal_length_size).ok_or(Truncated)?;
        let len = len_bytes
            .iter()
            .fold(0usize, |acc, b| (acc << 8) | *b as usize);
        let (nal, tail) = tail.split_at_checked(len).ok_or(Truncated)?;
        out.extend_from_slice(&START);
        out.extend_from_slice(nal);
        rest = tail;
    }
    Ok(out)
}

/// 拡大はせず、`MAX_PIXELS` 以下になるまで縦横比を保って縮める。寸法は偶数で最小 2。
pub fn fit_size(w: u32, h: u32) -> (u32, u32) {
    let even = |v: f64| ((v as u32) & !1).max(2);
    let (w, h) = (w as f64, h as f64);
    if w * h <= MAX_PIXELS as f64 {
        return (even(w), even(h));
    }
    let s = (MAX_PIXELS as f64 / (w * h)).sqrt();
    (even(w * s), even(h * s))
}

pub struct Frame {
    pub data: Vec<u8>,
    pub keyframe: bool,
}

/// 視聴者ごとの送信待ち。P フレームだけが欠けた列を渡さないよう、捨てたらキーフレームまで待つ。
pub struct SendQueue {
    frames: VecDeque<Frame>,
    waiting_keyframe: bool,
    needs_keyframe: bool,
}

impl SendQueue {
    pub fn new() -> Self {
        SendQueue {
            frames: VecDeque::new(),
            waiting_keyframe: true,
            needs_keyframe: false,
        }
    }

    pub fn push(&mut self, f: Frame) {
        if f.keyframe {
            self.waiting_keyframe = false;
        } else if self.waiting_keyframe {
            return;
        }
        if self.frames.len() >= QUEUE_LIMIT {
            self.frames.clear();
            if !f.keyframe {
                self.waiting_keyframe = true;
                self.needs_keyframe = true;
                return;
            }
        }
        self.frames.push_back(f);
    }

    pub fn pop(&mut self) -> Option<Frame> {
        self.frames.pop_front()
    }

    /// 読んだら下ろす。
    pub fn take_needs_keyframe(&mut self) -> bool {
        std::mem::take(&mut self.needs_keyframe)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn f(keyframe: bool) -> Frame {
        Frame {
            data: vec![keyframe as u8],
            keyframe,
        }
    }

    #[test]
    fn frame_layout() {
        assert_eq!(frame(VIDEO, &[9, 8]), vec![0, 0, 0, 2, 1, 9, 8]);
        assert_eq!(frame(KEEPALIVE, &[]), vec![0, 0, 0, 0, 2]);
    }

    #[test]
    fn annexb_converts_and_prepends_parameter_sets() {
        let avcc = [0, 0, 0, 2, 0xAA, 0xBB, 0, 0, 0, 1, 0xCC];
        let out = avcc_to_annexb(&avcc, 4, &[&[0x67, 1], &[0x68, 2]]).unwrap();
        assert_eq!(
            out,
            [0, 0, 0, 1, 0x67, 1, 0, 0, 0, 1, 0x68, 2, 0, 0, 0, 1, 0xAA, 0xBB, 0, 0, 0, 1, 0xCC]
        );
        assert_eq!(
            avcc_to_annexb(&[0, 3, 7, 8, 9], 2, &[]).unwrap(),
            [0, 0, 0, 1, 7, 8, 9]
        );
    }

    #[test]
    fn annexb_rejects_truncated_or_bad_length_size() {
        assert_eq!(avcc_to_annexb(&[0, 0, 0, 5, 1, 2], 4, &[]), Err(Truncated));
        assert_eq!(avcc_to_annexb(&[0, 0], 4, &[]), Err(Truncated));
        assert_eq!(avcc_to_annexb(&[0, 0, 0, 1, 1], 0, &[]), Err(Truncated));
        assert_eq!(avcc_to_annexb(&[0, 0, 0, 1, 1], 5, &[]), Err(Truncated));
    }

    #[test]
    fn fit_size_never_upscales_and_keeps_limits() {
        assert_eq!(fit_size(1280, 720), (1280, 720));
        assert_eq!(fit_size(1281, 721), (1280, 720));
        assert_eq!(fit_size(1, 1), (2, 2));
        for (w, h) in [(3840, 4260), (7680, 4320), (5120, 1440), (100_000, 10)] {
            let (a, b) = fit_size(w, h);
            assert!(a as u64 * b as u64 <= MAX_PIXELS, "{w}x{h}");
            assert!(a % 2 == 0 && b % 2 == 0 && a >= 2 && b >= 2);
            let ratio = (w as f64 / h as f64) / (a as f64 / b as f64);
            if w.min(h) > 1000 {
                assert!((0.98..1.02).contains(&ratio), "{w}x{h}");
            }
        }
    }

    #[test]
    fn queue_starts_at_a_keyframe() {
        let mut q = SendQueue::new();
        q.push(f(false));
        assert!(q.pop().is_none());
        q.push(f(true));
        q.push(f(false));
        assert!(q.pop().unwrap().keyframe);
        assert!(!q.pop().unwrap().keyframe);
    }

    #[test]
    fn overflow_drops_until_next_keyframe() {
        let mut q = SendQueue::new();
        q.push(f(true));
        for _ in 0..QUEUE_LIMIT - 1 {
            q.push(f(false));
        }
        assert!(!q.take_needs_keyframe());
        q.push(f(false));
        assert!(q.pop().is_none());
        assert!(q.take_needs_keyframe());
        assert!(!q.take_needs_keyframe());
        q.push(f(false));
        assert!(q.pop().is_none());
        q.push(f(true));
        q.push(f(false));
        assert!(q.pop().unwrap().keyframe);
        assert!(!q.pop().unwrap().keyframe);
    }

    #[test]
    fn overflow_keeps_an_incoming_keyframe() {
        let mut q = SendQueue::new();
        q.push(f(true));
        for _ in 0..QUEUE_LIMIT - 1 {
            q.push(f(false));
        }
        q.push(f(true));
        assert!(!q.take_needs_keyframe());
        assert!(q.pop().unwrap().keyframe);
        assert!(q.pop().is_none());
    }
}
