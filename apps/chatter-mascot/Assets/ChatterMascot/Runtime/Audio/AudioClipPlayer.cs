using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace ChatterMascot.Audio
{
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
    public sealed class AudioClipPlayer
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

        /// <param name="template">1本目の voice であり、増やすときの設定の写し元。</param>
        public AudioClipPlayer(AudioSource template)
        {
            _template = template;
            if (template != null) _voices.Add(new Voice { Source = template });
        }

        /// <summary>
        /// 最後に鳴らし始めた <see cref="AudioSource"/>。鳴っていなければ <c>null</c>。
        ///
        /// ★ <b>多voice 化で「どこから読むか」が曖昧になるのを先に潰すためにある。</b>
        ///   #17 のリップシンクは <c>GetOutputData</c> をここから読む。
        /// </summary>
        public AudioSource Current { get; private set; }

        /// <summary>
        /// 鳴らし終えたら戻る。例外は投げず、失敗の理由を返す（<c>null</c> なら成功）。
        /// 呼び出し側は成功も失敗も同じ経路（<c>Played</c> / <c>PlaybackFailed</c>）へ落とす。
        /// </summary>
        public async Task<string> PlayAsync(AudioClip clip)
        {
            if (clip == null) return "AudioClip がありません";

            var voice = Claim();
            if (voice == null) return "AudioSource がありません";

            try
            {
                var source = voice.Source;
                try
                {
                    source.clip = clip;
                    source.Play();
                    Current = source;
                }
                catch (Exception e)
                {
                    return e.Message;
                }

                var limit = clip.length * 2f + PlaybackGraceSeconds;
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
                if (Current == source) Current = null;

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
            Current = null;
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
