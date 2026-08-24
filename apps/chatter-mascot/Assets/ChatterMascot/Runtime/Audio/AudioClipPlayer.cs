using System;
using System.Threading.Tasks;
using UnityEngine;

namespace ChatterMascot.Audio
{
    /// <summary>
    /// <see cref="AudioSource"/> で1件ずつ鳴らす。<b>順序の判断は持たない</b>
    /// （→ <c>ChatterMascot.Playback.PlaybackQueue</c> が head しか再生しない）。
    /// </summary>
    public sealed class AudioClipPlayer
    {
        /// <summary>
        /// 再生完了を待つときの余裕。
        ///
        /// ★ 期限は<b>クリップの実長から決める</b>。固定値だと長文が切れるか、
        ///   ハングを見逃すかのどちらかになる。
        /// </summary>
        private const float PlaybackGraceSeconds = 2f;

        private readonly AudioSource _source;

        public AudioClipPlayer(AudioSource source)
        {
            _source = source;
        }

        /// <summary>
        /// 鳴らし終えたら戻る。例外は投げず、失敗の理由を返す（<c>null</c> なら成功）。
        /// 呼び出し側は成功も失敗も同じ経路（<c>Played</c> / <c>PlaybackFailed</c>）へ落とす。
        /// </summary>
        public async Task<string> PlayAsync(AudioClip clip)
        {
            if (_source == null) return "AudioSource がありません";
            if (clip == null) return "AudioClip がありません";

            try
            {
                _source.clip = clip;
                _source.Play();
            }
            catch (Exception e)
            {
                return e.Message;
            }

            var deadline = Time.realtimeSinceStartupAsDouble + clip.length + PlaybackGraceSeconds;
            // ★ Unity の SynchronizationContext により、この継続はメインスレッドの次のフレームで走る。
            //   AudioSource をここから触ってよいのはそのため
            while (_source != null && _source.isPlaying && Time.realtimeSinceStartupAsDouble < deadline)
            {
                await Task.Yield();
            }

            if (_source == null) return "再生中に AudioSource が失われました";

            var timedOut = _source.isPlaying;
            _source.Stop();
            _source.clip = null;

            return timedOut ? $"{clip.length + PlaybackGraceSeconds:F1} 秒で終わりませんでした" : null;
        }

        /// <summary>今鳴っている音を止める（終了処理用）。</summary>
        public void StopAll()
        {
            if (_source == null) return;
            _source.Stop();
            _source.clip = null;
        }
    }
}
