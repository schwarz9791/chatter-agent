using System;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// 待機が続いたら <c>idle/</c> から小ネタを1本、30〜60秒ごとにランダムな間隔で
    /// 発火させるタイマー。<b>時計と乱数を注入する純粋な状態機械。</b>
    ///
    /// ★ cc-mascot（<c>useVRMAnimation.ts</c>）と同じく、起点は<b>発話が止まった瞬間</b>と
    ///   <b>モーションが終わった瞬間</b>の<u>両方</u>でリセットする——喋り終わった直後や
    ///   感情モーションの直後にいきなり小ネタが重ならないようにするため。前者は
    ///   <see cref="ShouldFire"/> が内部で立ち下がりを検出して行う。後者は呼び出し側
    ///   （<c>VrmCharacter</c>）が <c>VrmMotionPlayer.Tick</c> の <c>Ended</c> を見て
    ///   <see cref="Reset"/> を呼ぶ。
    ///
    /// ★ <see cref="BlinkTimer"/> / <see cref="FaceLatch"/> と同じ流儀で、判断を
    ///   <c>MonoBehaviour</c> に書かない。<c>ChatterMascot.Tests.asmdef</c> は
    ///   <c>ChatterMascot.Runtime</c> しか参照しないので、ここに置かないとテストが1行も当たらない。
    /// </summary>
    public sealed class IdleAccentTimer
    {
        private readonly Func<double> _random;
        private readonly MotionParams _params;

        private bool _started;
        private double _startedAt;
        private double _intervalSeconds;
        private bool _wasSpeaking;

        /// <param name="random">0..1（<b>1 を含まない</b>）を返す乱数</param>
        public IdleAccentTimer(Func<double> random, MotionParams p)
        {
            _random = random;
            _params = p;
        }

        /// <summary>いま引いている間隔（秒）。テスト用に読める。</summary>
        public double NextIntervalSeconds
        {
            get { return _intervalSeconds; }
        }

        /// <summary>
        /// このフレームで小ネタを発火すべきか。<b><paramref name="speaking"/> の間は常に <c>false</c></b>。
        ///
        /// ★ <b>起点は最初の呼び出しの <paramref name="now"/>。</b> コンストラクタでは時計を
        ///   読まない（<c>BlinkTimer</c> と同じ理由——テストと実機で起点がずれ、起動直後だけ
        ///   待ちの長さが変わる）。だから<b>起動直後 <c>AccentMinSeconds</c> 以内には出ない</b>。
        ///
        /// ★ <b>発火したら内部で <see cref="Reset"/> する。</b> 呼び出し側が
        ///   <c>VrmMotionPlayer.Play</c> を呼んでもここを呼び忘れたら、次のフレームでまた
        ///   発火してしまうため。
        /// </summary>
        public bool ShouldFire(double now, bool speaking)
        {
            if (!_started) Reset(now);

            // ★ 発話の立ち下がりだけを見る。喋り続けている間は毎フレーム引き直さない
            //   （引き直すこと自体は無害だが、意味のある区別は「止まった瞬間」だけなので、
            //   そこだけ触ることで挙動が読みやすくなる）。
            if (_wasSpeaking && !speaking) Reset(now);
            _wasSpeaking = speaking;

            if (speaking) return false;
            if (now - _startedAt < _intervalSeconds) return false;

            Reset(now);
            return true;
        }

        /// <summary>
        /// 起点を <paramref name="now"/> にして、次の間隔を <c>[AccentMinSeconds, AccentMaxSeconds)</c>
        /// から引き直す。
        ///
        /// ★ 呼ぶのは3箇所（クラス doc 参照）: (a) 発話の立ち下がり、(b) 感情/小ネタモーションの終了、
        ///   (c) 発火したその場。
        /// </summary>
        public void Reset(double now)
        {
            _started = true;
            _startedAt = now;

            var r = _random != null ? _random() : 0.0;
            if (r < 0.0) r = 0.0;
            if (r >= 1.0) r = 1.0 - 1e-9; // 保険。契約は [0,1) だが境界の丸めで外れても壊れないように
            _intervalSeconds = _params.AccentMinSeconds + r * (_params.AccentMaxSeconds - _params.AccentMinSeconds);
        }
    }
}
