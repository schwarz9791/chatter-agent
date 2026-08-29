using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace ChatterMascot.Audio
{
    /// <summary>
    /// Unity 内蔵オーディオで鳴らすときのハンドル。<see cref="AudioClipPlayer.Prepare"/> が作る。
    ///
    /// ★ <b>長さを <c>AudioClip.length</c> から取らない</b>のは、再生の実体が
    ///   <see cref="AudioSource"/> でない実装（FMOD / 外部プロセス）と<b>期限の根拠を
    ///   揃える</b>ため。どれも <see cref="WavHeader.DurationMs"/>（fmt の byteRate 由来、
    ///   参照実装の <c>wavDurationMs</c> と同じ式）を見る。
    /// </summary>
    public sealed class UnityAudioHandle : ILipSyncSource
    {
        public AudioClip Clip;

        /// <summary>再生時間（ミリ秒）。<b>0 は「長さ 0」ではなく「不明」</b></summary>
        public int DurationMs;

        /// <summary>
        /// <c>null</c> 可（口が動かないだけ。発話は落とさない）。
        ///
        /// ★ <b><c>AudioSource.GetOutputData</c> を読む形にしないこと。</b> この実装だけ別の
        ///   出どころにすると、macOS（<c>AfplaySpeechPlayer</c>）とタイミングの性質が変わる。
        ///   → <see cref="ILipSyncSource"/>
        /// </summary>
        public float[] Envelope { get; set; }

        public int EnvelopeFrameMs { get; set; }
    }

    /// <summary>
    /// <see cref="AudioSource"/> で鳴らす。<b>順序の判断は持たない</b>
    /// （→ <c>ChatterMascot.Playback.PlaybackQueue</c> が head しか再生しない）。
    ///
    /// ★ <b><see cref="AudioSource"/> は1本では足りない。</b> 採番のやり直しを検出すると
    ///   <c>PlaybackQueue.ResetEpoch</c> は再生中の item を孤児に移して
    ///   <b>「音は最後まで流す」</b>ことにするので、孤児と新しいエポックの1文目が
    ///   同時に鳴りうる。1本を共有すると:
    ///
    ///   1. 新しい文の <c>Play()</c> が<b>孤児の音を消す</b>
    ///   2. しばらくして孤児側のループが期限切れで <c>Stop()</c> し、
    ///      <b>新しい文が途中で切れる</b>
    ///   3. それでも <c>Played</c> / <c>PlaybackFailed</c> は返るので、切れた文は
    ///      喋り切ったものとして ack され、<b>サーバーのキューから物理削除される</b>
    ///
    ///   参照実装（<c>core/src/player/audioPlayer.ts</c>）は clip ごとに <c>afplay</c> を
    ///   spawn するので孤児が本当に並行して鳴り切る。ここでは voice をプールして
    ///   <b><c>Stop()</c> の効果を自分の再生ぶんに限定する</b>ことで同じ性質を作る。
    /// </summary>
    public sealed class AudioClipPlayer : ISpeechPlayer
    {
        /// <summary>
        /// 再生完了を待つときの余裕。
        ///
        /// ★ 期限は<b>クリップの実長に比例させる</b>（<c>length * 2 + この値</c>）。
        ///   参照実装（<c>core/src/player/audioPlayer.ts</c>）が <c>duration * 2 + 5秒</c> に
        ///   しているのと同じで、倍にしているのは「デバイスが詰まったときのぶん」——
        ///   長い文ほど詰まる余地も大きい。
        ///
        /// ★ <b>固定値にしないこと。</b> 長さによらず一律 +2秒だと、Bluetooth の再ネゴなどで
        ///   数秒止まっただけで20秒の文が途中で切られる。切られた側は
        ///   <c>PlaybackFailed</c> → ack に落ちるので、<b>サーバーのキューからも消えて
        ///   二度と鳴らせない</b>。
        /// </summary>
        private const float PlaybackGraceSeconds = 5f;

        /// <summary>
        /// WAV の長さが読めなかったときの期限。
        ///
        /// ★ 参照実装の <c>FALLBACK_TIMEOUT_MS</c>（120秒）と同じ。1文がこれを超えることは
        ///   実際には無いので、「長さ不明」を長さ 0 と読んで<b>全部の再生を数秒で打ち切る</b>
        ///   事故だけを防げばよい。
        /// </summary>
        private const float FallbackTimeoutSeconds = 120f;

        /// <summary>
        /// voice がこの本数を超えたら警告する。
        ///
        /// ★ <b>上限ではなく診断の閾値。</b> 足りなくて鳴らないより、増えて気づける方がよい。
        ///   voice が積むのは「採番のやり直しが、音が鳴り終わる前に繰り返されている」ときだけなので、
        ///   <b>本数そのものが無音（や二重再生）の原因を指す材料になる</b>。
        /// </summary>
        private const int VoiceWarnThreshold = 8;

        /// <summary>診断。<c>MascotRunner</c> が <c>Debug.LogWarning</c> に繋ぐ。</summary>
        public event Action<string> Warn;

        private sealed class Voice
        {
            public AudioSource Source;

            /// <summary>
            /// 誰かが掴んでいる。
            ///
            /// ★ <b>横取り（steal）はしない。</b> 掴んでいる間は他の再生が触らないので、
            ///   「自分がまだ持ち主か」を確かめる世代カウンタのような見張りが要らない。
            ///   「足りない可能性」より「他人の再生を止めてしまう可能性」を消す。
            /// </summary>
            public bool Busy;
        }

        private readonly AudioSource _template;
        private readonly List<Voice> _voices = new List<Voice>();
        private bool _warnedVoiceCount;

        /// <summary>エンベロープを作れなかったことの警告は1回だけ（読めない WAV は同じ形が続く）</summary>
        private bool _warnedEnvelope;

        /// <param name="template">1本目の voice であり、増やすときの設定の写し元。</param>
        public AudioClipPlayer(AudioSource template)
        {
            _template = template;
            if (template != null) _voices.Add(new Voice { Source = template });
        }

        // ★ かつてここに `Current`（最後に鳴らし始めた AudioSource）があった。#17 のリップシンクが
        //   GetOutputData をどこから読むかを1つに決めるためだったが、**macOS ではその前提が
        //   成立しない**（再生の実体が AfplaySpeechPlayer で、音は子プロセスの中にある）。
        //   #58 は Prepare の時点で WAV から振幅エンベロープを作ってハンドルに載せる方式に寄せ、
        //   このプロパティは**読み手が1つも無いまま**残っていた。
        //   消費者ゼロのために voice プールを跨いだ正しさを保ち続ける必要は無いので、削除した。
        //   → ILipSyncSource / LipSyncEnvelope

        /// <summary>今鳴っている本数（孤児を含む）。</summary>
        public int ActiveCount
        {
            get
            {
                var count = 0;
                foreach (var voice in _voices)
                {
                    if (voice.Busy) count++;
                }
                return count;
            }
        }

        /// <summary>WAV を <see cref="AudioClip"/> にして、長さと一緒に包む。</summary>
        public object Prepare(byte[] wav, string name, out string error)
        {
            WavHeader header;
            if (!WavDecoder.TryReadHeader(wav, out header, out error)) return null;

            // ★ 読んだヘッダを渡す。引数無しの Decode は中でもう一度 RIFF を走査する
            var clip = WavDecoder.Decode(wav, name, header, out error);
            if (clip == null) return null;

            return new UnityAudioHandle
            {
                Clip = clip,
                DurationMs = header.DurationMs,
                // ★ **作れなくても Prepare は成功させる**（→ LipSyncEnvelope.BuildOrWarn）。
                // ★ ここは AudioClip 用とエンベロープ用で**サンプルを二度デコードしている**。
                //   消すには Decode から float[] を貰う形にする必要があるが、1発話あたり
                //   数百 KB を一度余分になめるだけ（約 1ms）なので今はやらない。
                //   この実装が主役になるのは Android（#25）なので、そのときに測って判断する
                Envelope = LipSyncEnvelope.BuildOrWarn(
                    wav, header, LipSyncEnvelope.DefaultFrameMs, ref _warnedEnvelope, Warn),
                EnvelopeFrameMs = LipSyncEnvelope.DefaultFrameMs,
            };
        }

        /// <summary>使い終わった（あるいは捨てる）クリップを解放する。</summary>
        public void Discard(object audio)
        {
            var handle = audio as UnityAudioHandle;
            if (handle == null || handle.Clip == null) return;
            UnityEngine.Object.Destroy(handle.Clip);
            handle.Clip = null;
        }

        /// <summary>
        /// ★ <b>効くのは iOS / Android だけ</b>（→ <see cref="SuspendOutput"/>）。
        ///   macOS では <c>AudioSettings.Mobile.StopAudioOutput</c> が no-op なので <c>false</c>。
        /// </summary>
        public bool CanSuspendOutput
        {
            get
            {
#if UNITY_IOS || UNITY_ANDROID
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// 出力を止める。<b>効くのは iOS / Android だけ。</b>
        ///
        /// ★ <b>macOS では何も起きない。</b> <c>AudioSettings.Mobile.StopAudioOutput</c> は
        ///   コンパイルは通るが、実行すると Unity 自身がログに
        ///   <c>"AudioSettings.Mobile.StopAudioOutput is implemented for iOS and Android only"</c>
        ///   と出して<b>何もしない</b>（実測: 呼んだ後も
        ///   <c>kAudioProcessPropertyIsRunningOutput</c> は 1 のまま）。
        ///   だから <c>#if</c> で囲んである —— macOS ビルドに入れても警告が出るだけで無意味。
        ///
        /// ★ <b>macOS で他に手段は無い。</b> <c>AudioListener.pause</c> も <c>volume = 0</c> も
        ///   DSP を止めるだけで出力ストリームは開いたまま、<c>AudioSettings.Reset()</c> は
        ///   再初期化であって解放ではない、<c>Enable Output Suspension</c> は Editor 専用、
        ///   <c>Disable Unity Audio</c> は静的な設定でランタイムに切り替えられない。
        ///   → <see cref="ISpeechPlayer"/> のクラスコメント
        /// </summary>
        public void SuspendOutput()
        {
#if UNITY_IOS || UNITY_ANDROID
            AudioSettings.Mobile.StopAudioOutput();
#endif
        }

        /// <summary>掴み直す。<see cref="SuspendOutput"/> と同じくモバイルだけ。</summary>
        public void ResumeOutput()
        {
#if UNITY_IOS || UNITY_ANDROID
            // ★ 止まっていなければ何も起きない（べき等）
            AudioSettings.Mobile.StartAudioOutput();
#endif
        }

        /// <summary>
        /// 鳴らし終えたら戻る。例外は投げず、失敗の理由を返す（<c>null</c> なら成功）。
        /// 呼び出し側は成功も失敗も同じ経路（<c>Played</c> / <c>PlaybackFailed</c>）へ落とす。
        /// </summary>
        public async Task<string> PlayAsync(object audio)
        {
            var handle = audio as UnityAudioHandle;
            if (handle == null) return "音声のハンドルがありません";

            var clip = handle.Clip;
            if (clip == null) return "AudioClip がありません";

            // ★ **これを外すと発話が黙って消える。** 出力を止めたまま Play() すると
            //   isPlaying が即 false になり、下の完了待ちが1回目のチェックで抜ける。
            //   timedOut は false なので**成功として返り**、Played → ack →
            //   サーバーのキューから物理削除される。ログも出ない。
            //   ゲートの resume は Tick 経由だと次フレームなので、ここで塞ぐ
            ResumeOutput();

            var voice = Claim();
            if (voice == null) return "AudioSource がありません";

            try
            {
                var source = voice.Source;
                try
                {
                    source.clip = clip;
                    source.Play();
                }
                catch (Exception e)
                {
                    return e.Message;
                }

                // ★ 実長に比例させる契約はそのまま。根拠が clip.length から
                //   WavHeader.DurationMs に変わっただけ（実装をまたいで揃えるため）
                var limit = TimeoutSecondsFor(handle.DurationMs);
                var deadline = Time.realtimeSinceStartupAsDouble + limit;
                // ★ Unity の SynchronizationContext により、この継続はメインスレッドの次のフレームで走る。
                //   AudioSource をここから触ってよいのはそのため
                while (source != null && source.isPlaying && Time.realtimeSinceStartupAsDouble < deadline)
                {
                    await Task.Yield();
                }

                if (source == null) return "再生中に AudioSource が失われました";

                var timedOut = source.isPlaying;
                // ★ 止めてよいのは自分が掴んでいる voice だけ。ここが共有されていると、
                //   期限切れの孤児が**別の文の再生を止める**
                source.Stop();
                source.clip = null;

                return timedOut ? $"{limit:F1} 秒で終わりませんでした" : null;
            }
            finally
            {
                voice.Busy = false;
            }
        }

        /// <summary>今鳴っている音を全部止める（終了処理用）。</summary>
        public void StopAll()
        {
            foreach (var voice in _voices)
            {
                if (voice.Source == null) continue;
                voice.Source.Stop();
                voice.Source.clip = null;
            }
        }

        /// <summary>
        /// 再生を諦めるまでの秒数。参照実装の <c>playbackTimeoutMs</c> と同じ形。
        /// </summary>
        private static float TimeoutSecondsFor(int durationMs)
        {
            // ★ 0 は「長さ 0」ではなく「不明」（→ WavHeader.DurationMs）。
            //   長さ 0 として 0 * 2 + 5 秒にすると、**すべての再生が5秒で打ち切られる**
            if (durationMs <= 0) return FallbackTimeoutSeconds;
            return durationMs / 1000f * 2f + PlaybackGraceSeconds;
        }

        /// <summary>空いている voice を掴む。無ければ増やす。</summary>
        private Voice Claim()
        {
            foreach (var voice in _voices)
            {
                if (voice.Busy || voice.Source == null) continue;
                voice.Busy = true;
                return voice;
            }

            var created = CreateVoice(_voices.Count);
            if (created == null) return null;

            created.Busy = true;
            _voices.Add(created);

            if (_voices.Count > VoiceWarnThreshold && !_warnedVoiceCount)
            {
                _warnedVoiceCount = true;
                Warn?.Invoke($"同時に鳴らす AudioSource が {_voices.Count} 本になりました" +
                             "（採番のやり直しが、音が鳴り終わる前に繰り返されている可能性）");
            }
            return created;
        }

        /// <summary>
        /// 2本目以降の voice を作る。
        ///
        /// ★ <b>子 <c>GameObject</c> にすること。</b> テンプレートと同じ <c>GameObject</c> に
        ///   足すこともできるが、Inspector 上で見分けがつかなくなる。
        ///   親ごと破棄されるので後始末も要らない。
        /// </summary>
        private Voice CreateVoice(int index)
        {
            if (_template == null) return null;

            var go = new GameObject("Voice " + index);
            go.transform.SetParent(_template.transform, false);

            var source = go.AddComponent<AudioSource>();
            CopySettings(_template, source);
            return new Voice { Source = source };
        }

        /// <summary>
        /// テンプレートの設定を写す。
        ///
        /// ★ <b>写し漏れは見つけにくい形で出る。</b> #17 でミキサーや 3D 配置を入れたときに
        ///   落とすと、症状は「<b>孤児だけ音量が違う</b>」「たまに定位がおかしい」のような、
        ///   再現条件が採番のやり直しに縛られたものになる。
        ///   ここを増やしたら <c>docs/mascot.md</c> にも書くこと。
        /// </summary>
        private static void CopySettings(AudioSource from, AudioSource to)
        {
            to.outputAudioMixerGroup = from.outputAudioMixerGroup;
            to.volume = from.volume;
            to.pitch = from.pitch;
            to.panStereo = from.panStereo;
            to.spatialBlend = from.spatialBlend;
            to.reverbZoneMix = from.reverbZoneMix;
            to.dopplerLevel = from.dopplerLevel;
            to.spread = from.spread;
            to.rolloffMode = from.rolloffMode;
            to.minDistance = from.minDistance;
            to.maxDistance = from.maxDistance;
            to.priority = from.priority;
            to.mute = from.mute;
            to.bypassEffects = from.bypassEffects;
            to.bypassListenerEffects = from.bypassListenerEffects;
            to.bypassReverbZones = from.bypassReverbZones;

            // ★ この2つはテンプレートに関係なく必ず false。ループすると ack が永久に出ず、
            //   playOnAwake だと生成した瞬間に無音のクリップを鳴らそうとする
            to.loop = false;
            to.playOnAwake = false;
        }
    }
}
