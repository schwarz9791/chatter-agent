//! 画面の取得（ScreenCaptureKit）と H.264 エンコード（VideoToolbox）。
//! 取得の許可はシステムのピッカーで選んだ内容だけに掛かるので、画面収録の権限は要らない。

use std::ffi::c_void;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr::{null_mut, NonNull};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};

use block2::RcBlock;
use dispatch2::DispatchQueue;
use objc2::rc::Retained;
use objc2::runtime::ProtocolObject;
use objc2::{define_class, msg_send, AllocAnyThread, DefinedClass};
use objc2_core_foundation::{CFBoolean, CFDictionary, CFNumber, CFRetained, CFString, CFType};
use objc2_core_media::{
    kCMTimeInvalid, kCMVideoCodecType_H264, CMClock, CMSampleBuffer, CMTime,
    CMVideoFormatDescriptionGetH264ParameterSetAtIndex,
};
use objc2_core_video::{
    kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange, CVImageBuffer, CVPixelBufferGetHeight,
    CVPixelBufferGetWidth,
};
use objc2_foundation::{NSError, NSObject, NSObjectProtocol};
use objc2_screen_capture_kit::{
    SCContentFilter, SCContentSharingPicker, SCContentSharingPickerConfiguration,
    SCContentSharingPickerMode, SCContentSharingPickerObserver, SCFrameStatus, SCStream,
    SCStreamConfiguration, SCStreamDelegate, SCStreamFrameInfoStatus, SCStreamOutput,
    SCStreamOutputType,
};
use objc2_video_toolbox::{
    kVTCompressionPropertyKey_AllowFrameReordering, kVTCompressionPropertyKey_AverageBitRate,
    kVTCompressionPropertyKey_ExpectedFrameRate,
    kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration, kVTCompressionPropertyKey_ProfileLevel,
    kVTCompressionPropertyKey_RealTime, kVTEncodeFrameOptionKey_ForceKeyFrame,
    kVTProfileLevel_H264_Main_AutoLevel, VTCompressionSession, VTEncodeInfoFlags,
    VTSessionSetProperty,
};

use super::wire::{self, Frame};
use super::Hub;

fn lock<T>(m: &Mutex<T>) -> MutexGuard<'_, T> {
    m.lock().unwrap_or_else(|e| e.into_inner())
}

/// 取得とエンコードのスレッドを跨いで持つ Objective-C / CF のオブジェクト。
struct Unsync<T>(T);
unsafe impl<T> Send for Unsync<T> {}
unsafe impl<T> Sync for Unsync<T> {}

struct Ivars {
    inner: Arc<Inner>,
    /// この世代のストリームのものだけを受ける。選び直しで止めた古いストリームの残りを捨てる。
    generation: u64,
}

define_class!(
    #[unsafe(super(NSObject))]
    #[ivars = Ivars]
    struct Handler;

    unsafe impl NSObjectProtocol for Handler {}

    unsafe impl SCContentSharingPickerObserver for Handler {
        #[unsafe(method(contentSharingPicker:didCancelForStream:))]
        fn did_cancel(&self, _picker: &SCContentSharingPicker, _stream: Option<&SCStream>) {
            let _ = catch_unwind(AssertUnwindSafe(|| self.ivars().inner.picker_cancelled()));
        }

        #[unsafe(method(contentSharingPicker:didUpdateWithFilter:forStream:))]
        fn did_update(
            &self,
            _picker: &SCContentSharingPicker,
            filter: &SCContentFilter,
            _stream: Option<&SCStream>,
        ) {
            let _ = catch_unwind(AssertUnwindSafe(|| self.ivars().inner.selected(filter)));
        }

        #[unsafe(method(contentSharingPickerStartDidFailWithError:))]
        fn start_failed(&self, error: &NSError) {
            let msg = error.localizedDescription().to_string();
            let _ = catch_unwind(AssertUnwindSafe(|| {
                self.ivars()
                    .inner
                    .hub
                    .capture_ended(Some(format!("picker failed: {msg}")))
            }));
        }
    }

    unsafe impl SCStreamOutput for Handler {
        #[unsafe(method(stream:didOutputSampleBuffer:ofType:))]
        fn did_output(
            &self,
            _stream: &SCStream,
            buffer: &CMSampleBuffer,
            kind: SCStreamOutputType,
        ) {
            if kind != SCStreamOutputType::Screen {
                return;
            }
            let iv = self.ivars();
            let _ = catch_unwind(AssertUnwindSafe(|| iv.inner.sample(iv.generation, buffer)));
        }
    }

    unsafe impl SCStreamDelegate for Handler {
        #[unsafe(method(stream:didStopWithError:))]
        fn did_stop(&self, _stream: &SCStream, error: &NSError) {
            let iv = self.ivars();
            if iv.generation != iv.inner.generation.load(Ordering::SeqCst) {
                return;
            }
            let msg = error.localizedDescription().to_string();
            let _ = catch_unwind(AssertUnwindSafe(|| {
                iv.inner
                    .hub
                    .capture_ended(Some(format!("stream stopped: {msg}")))
            }));
        }
    }
);

impl Handler {
    fn new(inner: Arc<Inner>, generation: u64) -> Retained<Self> {
        let this = Self::alloc().set_ivars(Ivars { inner, generation });
        unsafe { msg_send![super(this), init] }
    }
}

struct Encoder {
    session: CFRetained<VTCompressionSession>,
    /// コールバックの refcon。`Drop` で手放す。
    hub: *const Hub,
    width: usize,
    height: usize,
}

unsafe impl Send for Encoder {}

impl Encoder {
    fn new(hub: &Arc<Hub>, width: u32, height: u32) -> Result<Self, String> {
        let refcon = Arc::into_raw(hub.clone());
        let mut out: *mut VTCompressionSession = null_mut();
        let status = unsafe {
            VTCompressionSession::create(
                None,
                width as i32,
                height as i32,
                kCMVideoCodecType_H264,
                None,
                None,
                None,
                Some(vt_output),
                refcon as *mut c_void,
                NonNull::from(&mut out),
            )
        };
        let Some(raw) = NonNull::new(out).filter(|_| status == 0) else {
            drop(unsafe { Arc::from_raw(refcon) });
            return Err(format!("VTCompressionSessionCreate failed ({status})"));
        };
        let enc = Encoder {
            session: unsafe { CFRetained::from_raw(raw) },
            hub: refcon,
            width: width as usize,
            height: height as usize,
        };
        enc.configure()?;
        Ok(enc)
    }

    fn set(&self, key: &CFString, value: &CFType) -> Result<(), String> {
        let status = unsafe { VTSessionSetProperty(self.session.as_ref(), key, Some(value)) };
        if status == 0 {
            Ok(())
        } else {
            Err(format!("VTSessionSetProperty failed ({status})"))
        }
    }

    fn configure(&self) -> Result<(), String> {
        unsafe {
            self.set(kVTCompressionPropertyKey_RealTime, CFBoolean::new(true))?;
            self.set(
                kVTCompressionPropertyKey_AllowFrameReordering,
                CFBoolean::new(false),
            )?;
            self.set(
                kVTCompressionPropertyKey_ProfileLevel,
                kVTProfileLevel_H264_Main_AutoLevel,
            )?;
            self.set(
                kVTCompressionPropertyKey_AverageBitRate,
                &CFNumber::new_i32(wire::BITRATE),
            )?;
            self.set(
                kVTCompressionPropertyKey_ExpectedFrameRate,
                &CFNumber::new_i32(wire::FPS),
            )?;
            self.set(
                kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration,
                &CFNumber::new_f64(wire::KEYFRAME_INTERVAL_SECS),
            )?;
            let status = self.session.prepare_to_encode_frames();
            if status != 0 {
                return Err(format!("prepare failed ({status})"));
            }
        }
        Ok(())
    }

    fn encode(&self, image: &CVImageBuffer, force_key: bool) {
        if CVPixelBufferGetWidth(image) != self.width
            || CVPixelBufferGetHeight(image) != self.height
        {
            return;
        }
        let props = force_key.then(|| unsafe {
            CFDictionary::<CFString, CFBoolean>::from_slices(
                &[kVTEncodeFrameOptionKey_ForceKeyFrame],
                &[CFBoolean::new(true)],
            )
        });
        unsafe {
            // 再エンコードでも単調に増えるよう、時刻はホストの時計から取る。
            let pts = CMClock::host_time_clock().time();
            let mut flags = VTEncodeInfoFlags(0);
            let status = self.session.encode_frame(
                image,
                pts,
                kCMTimeInvalid,
                props.as_ref().map(|p| p.as_opaque()),
                null_mut(),
                &mut flags,
            );
            if status != 0 {
                if let Some(hub) = self.hub.as_ref() {
                    hub.encode_failed(status);
                }
            }
        }
    }
}

impl Drop for Encoder {
    fn drop(&mut self) {
        unsafe {
            self.session.complete_frames(kCMTimeInvalid);
            self.session.invalidate();
            drop(Arc::from_raw(self.hub));
        }
    }
}

/// VideoToolbox のスレッドから呼ばれる。触るのは視聴者側の鍵だけで、エンコーダの鍵は取らない。
unsafe extern "C-unwind" fn vt_output(
    refcon: *mut c_void,
    _source: *mut c_void,
    status: i32,
    _flags: VTEncodeInfoFlags,
    sample: *mut CMSampleBuffer,
) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        let (Some(hub), Some(sample)) = (unsafe { (refcon as *const Hub).as_ref() }, unsafe {
            sample.as_ref()
        }) else {
            return;
        };
        if status != 0 {
            hub.encode_failed(status);
            return;
        }
        match annexb_of(sample) {
            Some(frame) => hub.push_frame(frame),
            None => hub.log("Dropped an encoded frame that could not be converted"),
        }
    }));
}

fn annexb_of(sample: &CMSampleBuffer) -> Option<Frame> {
    unsafe {
        let keyframe = match sample.sample_attachments_array(false) {
            Some(arr) if arr.count() > 0 => {
                let dict = (arr.value_at_index(0) as *const CFDictionary).as_ref()?;
                !dict.contains_ptr_key(
                    objc2_core_media::kCMSampleAttachmentKey_NotSync as *const CFString
                        as *const c_void,
                )
            }
            _ => true,
        };

        let desc = sample.format_description()?;
        let mut sets: Vec<&[u8]> = Vec::new();
        let mut nal_len: i32 = 0;
        if keyframe {
            let mut index = 0;
            loop {
                let (mut ptr, mut size, mut count) = (std::ptr::null(), 0usize, 0usize);
                let status = CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
                    &desc,
                    index,
                    &mut ptr,
                    &mut size,
                    &mut count,
                    &mut nal_len,
                );
                if status != 0 || ptr.is_null() {
                    return None;
                }
                sets.push(std::slice::from_raw_parts(ptr, size));
                index += 1;
                if index >= count {
                    break;
                }
            }
        } else {
            let (mut ptr, mut size, mut count) = (std::ptr::null(), 0usize, 0usize);
            if CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
                &desc,
                0,
                &mut ptr,
                &mut size,
                &mut count,
                &mut nal_len,
            ) != 0
            {
                return None;
            }
        }

        let block = sample.data_buffer()?;
        let (mut at_offset, mut total) = (0usize, 0usize);
        let mut data: *mut std::ffi::c_char = null_mut();
        if block.data_pointer(0, &mut at_offset, &mut total, &mut data) != 0
            || data.is_null()
            || at_offset != total
        {
            return None;
        }
        let bytes = std::slice::from_raw_parts(data as *const u8, total);
        let data = wire::avcc_to_annexb(bytes, usize::try_from(nal_len).ok()?, &sets).ok()?;
        Some(Frame { data, keyframe })
    }
}

#[derive(Default)]
struct Objects {
    observer: Option<Unsync<Retained<Handler>>>,
    stream: Option<Unsync<Retained<SCStream>>>,
    /// デリゲートと出力は弱参照なので、ストリームの間はここで生かす。
    handler: Option<Unsync<Retained<Handler>>>,
}

struct Inner {
    hub: Arc<Hub>,
    objects: Mutex<Objects>,
    generation: AtomicU64,
    encoder: Mutex<Option<Encoder>>,
    /// 画面が静止している間はフレームが来ないので、最後の1枚を持っておく。
    latest: Mutex<Option<Unsync<CFRetained<CVImageBuffer>>>>,
    force_key: AtomicBool,
    streaming: AtomicBool,
}

pub struct Capture {
    inner: Arc<Inner>,
    app: tauri::AppHandle,
}

impl Capture {
    pub fn new(app: tauri::AppHandle, hub: Arc<Hub>) -> Self {
        Capture {
            inner: Arc::new(Inner {
                hub,
                objects: Mutex::new(Objects::default()),
                generation: AtomicU64::new(0),
                encoder: Mutex::new(None),
                latest: Mutex::new(None),
                force_key: AtomicBool::new(false),
                streaming: AtomicBool::new(false),
            }),
            app,
        }
    }

    /// ピッカーを出す。
    pub fn present(&self) {
        let inner = self.inner.clone();
        let _ = self.app.run_on_main_thread(move || unsafe {
            let observer = Handler::new(inner.clone(), 0);
            let picker = SCContentSharingPicker::sharedPicker();
            let config = SCContentSharingPickerConfiguration::new();
            config.setAllowedPickerModes(
                SCContentSharingPickerMode::SingleWindow
                    | SCContentSharingPickerMode::SingleDisplay,
            );
            picker.setDefaultConfiguration(&config);
            picker.addObserver(ProtocolObject::from_ref(&*observer));
            picker.setActive(true);
            lock(&inner.objects).observer = Some(Unsync(observer));
            picker.present();
        });
    }

    pub fn stop(&self) {
        self.inner.stop_stream();
        let observer = lock(&self.inner.objects).observer.take();
        let _ = self.app.run_on_main_thread(move || unsafe {
            let picker = SCContentSharingPicker::sharedPicker();
            picker.setActive(false);
            if let Some(Unsync(o)) = observer {
                picker.removeObserver(ProtocolObject::from_ref(&*o));
            }
        });
    }

    /// 視聴者が付いたとき、静止した画面でも最初のキーフレームを出す。
    pub fn request_keyframe(&self) {
        self.inner.force_key.store(true, Ordering::SeqCst);
        let held = lock(&self.inner.latest)
            .as_ref()
            .map(|b| Unsync(b.0.clone()));
        if let Some(Unsync(image)) = held {
            self.inner.encode(&image);
        }
    }
}

impl Inner {
    fn encode(&self, image: &CVImageBuffer) {
        let guard = lock(&self.encoder);
        if let Some(enc) = guard.as_ref() {
            enc.encode(image, self.force_key.swap(false, Ordering::SeqCst));
        }
    }

    fn picker_cancelled(&self) {
        // 選び直しをやめただけなら、映している内容はそのまま。
        if !self.streaming.load(Ordering::SeqCst) {
            self.hub.capture_ended(None);
        }
    }

    fn sample(&self, generation: u64, buffer: &CMSampleBuffer) {
        if generation != self.generation.load(Ordering::SeqCst) {
            return;
        }
        let complete = unsafe {
            buffer
                .sample_attachments_array(false)
                .filter(|a| a.count() > 0)
                .and_then(|a| {
                    (a.value_at_index(0) as *const CFDictionary)
                        .as_ref()
                        .map(|d| {
                            let v = d.value(SCStreamFrameInfoStatus as *const _ as *const c_void)
                                as *const CFNumber;
                            v.as_ref().and_then(|n| n.as_i64())
                                == Some(SCFrameStatus::Complete.0 as i64)
                        })
                })
                .unwrap_or(false)
        };
        if !complete {
            return;
        }
        let Some(image) = (unsafe { buffer.image_buffer() }) else {
            return;
        };
        *lock(&self.latest) = Some(Unsync(image.clone()));
        if self.hub.has_viewer() {
            self.encode(&image);
        }
    }

    /// 取得とエンコードを止める。ピッカーはそのまま。
    fn stop_stream(&self) {
        self.generation.fetch_add(1, Ordering::SeqCst);
        self.streaming.store(false, Ordering::SeqCst);
        let stream = {
            let mut o = lock(&self.objects);
            o.handler = None;
            o.stream.take()
        };
        if let Some(Unsync(s)) = stream {
            let done = RcBlock::new(|_: *mut NSError| {});
            unsafe { s.stopCaptureWithCompletionHandler(Some(&done)) };
        }
        *lock(&self.encoder) = None;
        *lock(&self.latest) = None;
    }

    fn selected(self: &Arc<Self>, filter: &SCContentFilter) {
        self.stop_stream();
        let (rect, scale) = unsafe { (filter.contentRect(), filter.pointPixelScale() as f64) };
        let (w, h) = wire::fit_size(
            (rect.size.width * scale).max(0.0) as u32,
            (rect.size.height * scale).max(0.0) as u32,
        );
        if !self.hub.selected(w, h) {
            return;
        }
        match self.start(filter, w, h) {
            Ok(()) => self.streaming.store(true, Ordering::SeqCst),
            Err(e) => self.hub.capture_ended(Some(e)),
        }
    }

    fn start(self: &Arc<Self>, filter: &SCContentFilter, w: u32, h: u32) -> Result<(), String> {
        *lock(&self.encoder) = Some(Encoder::new(&self.hub, w, h)?);
        let generation = self.generation.fetch_add(1, Ordering::SeqCst) + 1;
        let handler = Handler::new(self.clone(), generation);
        let queue = DispatchQueue::new("chatter-agent.screen", None);
        unsafe {
            let config = SCStreamConfiguration::new();
            config.setWidth(w as usize);
            config.setHeight(h as usize);
            config.setMinimumFrameInterval(CMTime::new(1, wire::FPS));
            config.setPixelFormat(kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange);
            config.setShowsCursor(true);
            config.setPreservesAspectRatio(true);
            config.setQueueDepth(6);
            let stream = SCStream::initWithFilter_configuration_delegate(
                SCStream::alloc(),
                filter,
                &config,
                Some(ProtocolObject::from_ref(&*handler)),
            );
            stream
                .addStreamOutput_type_sampleHandlerQueue_error(
                    ProtocolObject::from_ref(&*handler),
                    SCStreamOutputType::Screen,
                    Some(&queue),
                )
                .map_err(|e| format!("addStreamOutput failed: {}", e.localizedDescription()))?;
            let hub = self.hub.clone();
            let started = RcBlock::new(move |err: *mut NSError| {
                if let Some(e) = err.as_ref() {
                    hub.capture_ended(Some(format!(
                        "startCapture failed: {}",
                        e.localizedDescription()
                    )));
                }
            });
            stream.startCaptureWithCompletionHandler(Some(&started));
            let mut o = lock(&self.objects);
            o.stream = Some(Unsync(stream));
            o.handler = Some(Unsync(handler));
        }
        Ok(())
    }
}
