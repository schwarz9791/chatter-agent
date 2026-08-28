using System;
using UnityEngine;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// 自動まばたき。<b>時計と乱数を注入する純粋な時間関数。</b>
    ///
    /// ★ <b>UniVRM の runtime パッケージに自動瞬きコンポーネントは無い。</b>
    ///   <c>VRM10Blinker.cs</c> は <c>Packages/VRM10/Samples~/VRM10Viewer/</c> にあり、
    ///   <c>Samples~</c> は Package Manager から明示的にインポートしない限り Unity が読まない。
    ///   だから自前で書く。<b>数値は参考にしているがコードは自前なので <c>NOTICE</c> の義務は増えない。</b>
    ///
    /// ★ <b>コルーチンにしないこと。</b> サンプルは <c>StartCoroutine</c> + <c>WaitForSeconds</c> で
    ///   書かれているが、それだと EditMode テストで固定できない。
    ///   <c>AudioIdleGate</c> と同じ「状態は持つが時計は引数で受け取る」形にする。
    ///
    /// ★ <b>状態は <c>double</c> の期限で持ち、<c>float</c> へ落とすのは「期限との差」だけにする。</b>
    ///   このアプリは常駐するので <c>Time.realtimeSinceStartupAsDouble</c> は日単位まで伸びる。
    ///   経過時間を <c>(float)now</c> から作ると、7日で float の刻み幅が1フレームぶんの差を上回り、
    ///   <b>「何日か点けっぱなしにすると瞬きがカクつく／止まる」がエラー無しで起きる</b>
    ///   （<see cref="Oscillator.Phase"/> が位相を周期で畳んでいるのと同じ理由）。
    ///
    /// 既定値の出どころ:
    /// <list type="bullet">
    ///   <item><b>間隔 2〜6秒</b> —— cc-mascot（<c>useBlink.ts</c> の
    ///     <c>DEFAULT_MIN_INTERVAL</c> / <c>DEFAULT_MAX_INTERVAL</c>）。
    ///     ★ サンプルの <c>VRM10Blinker</c> は <c>Random.value * Interval</c> ＝ <b>U(0, 5秒)</b> で、
    ///     0秒近い間隔が出て連続瞬きに見える。人の瞬きは3〜4秒に1回なのでこちらを採る</item>
    ///   <item><b>閉 0.1 / 保持 0.06 / 開 0.03 秒</b> —— <c>VRM10Blinker</c> の
    ///     <c>CloseSeconds</c> / <c>ClosingTime</c> / <c>OpeningSeconds</c>。
    ///     ★ cc-mascot は「閉 75ms → 開 75ms、保持なし・線形」で、閉じたままの間が無いぶん
    ///     速く見える。形はサンプル側を採る</item>
    /// </list>
    /// </summary>
    public sealed class BlinkTimer
    {
        private enum Phase
        {
            /// <summary>目を開けたまま次の瞬きを待っている</summary>
            Waiting,
            Closing,
            Holding,
            Opening,
        }

        /// <summary>
        /// <see cref="Request"/> の間引き。<c>VRM10Blinker.Request</c> の setter が持つ
        /// <c>m_nextRequest = Time.time + 1.0f</c> と同じ。
        ///
        /// ★ <b>保険であって、これに頼らないこと。</b> 呼び出し側は <c>kind</c> が
        ///   <c>prompt</c> に<b>変わったエッジ</b>で1回だけ呼ぶ。毎フレーム呼ぶと
        ///   （デバウンスがあっても）1秒ごとに瞬き続ける。
        /// </summary>
        private const float RequestDebounceSeconds = 1f;

        /// <summary>
        /// 1回の <see cref="Tick"/> で進めるフェーズ数の上限。
        ///
        /// ★ <b>保持や開きを 0 秒に設定できる以上、上限が無いと無限ループになりうる。</b>
        ///   実運用では 1 フレームに 0〜1 回しか進まない（閉+保持+開で 0.19 秒 ＝ 30fps で 6 フレーム）。
        /// </summary>
        private const int MaxPhaseAdvancesPerTick = 8;

        private readonly Func<float> _random;
        private readonly float _minIntervalSeconds;
        private readonly float _maxIntervalSeconds;
        private readonly float _closeSeconds;
        private readonly float _holdSeconds;
        private readonly float _openSeconds;

        private bool _running;
        private Phase _phase;
        private double _phaseStartedAt;
        private float _phaseSeconds;
        private bool _requested;
        private double _lastRequestedBlinkAt = double.NegativeInfinity;

        /// <summary>
        /// キルスイッチ。false の間は常に 0 を返し、内部状態も畳む
        /// （<c>AudioIdleGate.Enabled</c> と同じ趣旨）。
        ///
        /// ★ 再び true にしたときは<b>そのときの <c>now</c> から待ちをやり直す</b>。
        ///   止めていた間の経過を持ち越すと、再開した瞬間に瞬く。
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <param name="random">0..1 を返す乱数。<c>UnityEngine.Random.value</c> を渡す想定</param>
        public BlinkTimer(
            Func<float> random,
            float minIntervalSeconds = 2f,
            float maxIntervalSeconds = 6f,
            float closeSeconds = 0.1f,
            float holdSeconds = 0.06f,
            float openSeconds = 0.03f)
        {
            _random = random;
            _minIntervalSeconds = Mathf.Max(0f, minIntervalSeconds);
            // ★ 逆転して渡されても壊れないようにする（負の幅になると Lerp が min を下回る）
            _maxIntervalSeconds = Mathf.Max(_minIntervalSeconds, maxIntervalSeconds);
            _closeSeconds = Mathf.Max(0f, closeSeconds);
            _holdSeconds = Mathf.Max(0f, holdSeconds);
            _openSeconds = Mathf.Max(0f, openSeconds);
        }

        /// <summary>
        /// 次の待ちを打ち切って1回だけ瞬かせる。<c>kind: "prompt"</c> の到着で呼ぶ。
        ///
        /// ★ <b>時計を取らない。</b> 実際に消費するのは次の <see cref="Tick"/> で、
        ///   デバウンスの判定もそこで行う。呼び出し側（<c>MonoBehaviour</c>）と
        ///   時計の取り方を結合させないため。
        /// </summary>
        public void Request()
        {
            _requested = true;
        }

        /// <summary>
        /// このフレームの <c>blink</c> weight（0..1）。
        /// </summary>
        public float Tick(double now)
        {
            if (!Enabled)
            {
                _running = false;
                _requested = false;
                return 0f;
            }

            if (!_running)
            {
                // ★ コンストラクタでは時計を読まない。起点は最初の Tick の now
                //   （読むとテストと実機で起点がずれ、初回の待ちだけ長さが変わる）
                _running = true;
                EnterWaiting(now);
            }

            ConsumeRequest(now);

            var advances = 0;
            while (now - _phaseStartedAt >= _phaseSeconds)
            {
                if (++advances > MaxPhaseAdvancesPerTick)
                {
                    // 進み切らないほど長く止まっていた（あるいは 0 秒フェーズが並んでいる）。
                    // ★ ここで now に揃えないと、次フレーム以降も毎回上限まで回り続ける
                    _phaseStartedAt = now;
                    break;
                }

                Advance(_phaseStartedAt + _phaseSeconds);
            }

            return Value(now);
        }

        private void ConsumeRequest(double now)
        {
            if (!_requested) return;
            _requested = false;

            // 既に瞬いている最中なら捨てる（重ねても見た目は変わらない）
            if (_phase != Phase.Waiting) return;
            if (now - _lastRequestedBlinkAt < RequestDebounceSeconds) return;

            _lastRequestedBlinkAt = now;
            EnterPhase(Phase.Closing, now, _closeSeconds);
        }

        private void Advance(double at)
        {
            switch (_phase)
            {
                case Phase.Waiting:
                    EnterPhase(Phase.Closing, at, _closeSeconds);
                    break;
                case Phase.Closing:
                    EnterPhase(Phase.Holding, at, _holdSeconds);
                    break;
                case Phase.Holding:
                    EnterPhase(Phase.Opening, at, _openSeconds);
                    break;
                default:
                    EnterWaiting(at);
                    break;
            }
        }

        private void EnterWaiting(double at)
        {
            var r = _random != null ? Mathf.Clamp01(_random()) : 0.5f;
            EnterPhase(Phase.Waiting, at, Mathf.Lerp(_minIntervalSeconds, _maxIntervalSeconds, r));
        }

        private void EnterPhase(Phase phase, double at, float seconds)
        {
            _phase = phase;
            _phaseStartedAt = at;
            _phaseSeconds = seconds;
        }

        /// <summary>
        /// ★ <c>elapsed</c> は<b>期限との差</b>なので、<c>now</c> が日単位まで伸びても
        ///   <c>float</c> に落として精度が落ちない（クラスの doc を参照）。
        /// </summary>
        private float Value(double now)
        {
            var elapsed = (float)(now - _phaseStartedAt);

            switch (_phase)
            {
                case Phase.Closing:
                    return _phaseSeconds <= 0f ? 1f : Mathf.Clamp01(elapsed / _phaseSeconds);
                case Phase.Holding:
                    return 1f;
                case Phase.Opening:
                    return _phaseSeconds <= 0f ? 0f : Mathf.Clamp01(1f - elapsed / _phaseSeconds);
                default:
                    return 0f;
            }
        }
    }
}
