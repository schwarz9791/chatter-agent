using System;

namespace ChatterMascot.Audio
{
    /// <summary>
    /// 一時ミュートの状態。<b>純粋</b>（Unity にも設定ファイルにも依存しない）。
    ///
    /// ★ <b>音量では実装できない。</b> macOS の再生の実体は引数なしの <c>afplay</c> で
    ///   音量を渡す口が無く、<c>Disable Unity Audio</c> が ON なので
    ///   <c>AudioListener.volume</c> も効かない（→ <c>docs/mascot.md</c>）。
    ///   <b>再生そのものを飛ばす</b>のが唯一の手段になる（→ <see cref="MutedSpeechPlayer"/>）。
    /// </summary>
    public sealed class MuteState
    {
        private bool _muted;

        /// <summary>変わったときだけ発火する（同じ値を代入しても鳴らない）。</summary>
        public event Action<bool> Changed;

        public bool Muted
        {
            get { return _muted; }
            set
            {
                if (_muted == value) return;
                _muted = value;

                var changed = Changed;
                if (changed != null) changed(value);
            }
        }

        /// <summary>切り替えて、切り替えた後の値を返す。</summary>
        public bool Toggle()
        {
            Muted = !_muted;
            return _muted;
        }
    }
}
