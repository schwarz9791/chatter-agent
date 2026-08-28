using System;
using ChatterMascot.Vrm;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <see cref="Oscillator.Phase"/>（<c>IdlePose</c> と <c>GazeAim</c> が共有する位相計算）。
    ///
    /// ★ 以前は同じ検査（<see cref="StaysAccurateAfterWeeksOfUptime"/> に相当するもの）が
    ///   <c>GazeAimTests</c> と <c>IdlePoseTests</c> の両方に、<c>Evaluate</c> 経由の間接的な
    ///   形で逐語で重複していた（PR #69 のレビューで指摘）。<c>Phase</c> が <c>public</c> に
    ///   なったので、ここで <c>Oscillator</c> を直接検査する1本に寄せる。
    /// </summary>
    [TestFixture]
    public sealed class OscillatorTests
    {
        /// <summary>周期が 0 以下なら止まっているものとして 0 を返す。</summary>
        [Test]
        public void ReturnsZeroForNonPositivePeriod()
        {
            Assert.That(Oscillator.Phase(10.0, 0f), Is.EqualTo(0f));
            Assert.That(Oscillator.Phase(10.0, -1f), Is.EqualTo(0f));
        }

        /// <summary>
        /// このアプリは常駐する（デスクトップに出しっぱなしにするのが用途そのもの）ので、
        /// <c>now</c>（<c>Time.realtimeSinceStartupAsDouble</c>）は日単位まで伸びる。
        ///
        /// ★ <c>Phase</c> が周期で畳まずに <c>now</c> を直接 float 化すると、この規模の
        ///   <c>now</c> では <c>Mathf.Sin</c> に渡る引数そのものが float の刻み幅
        ///   （数十分の一 rad）で丸められ、出力が「その時刻での正しい sin 値」から
        ///   大きくずれる（実測: 30日相当で sin 値の誤差が 0.002〜0.08。呼吸や視線の漂いが
        ///   対象なので、症状としては「何日か点けっぱなしにすると呼吸や視線の漂いが
        ///   カクつく／止まって見える」）。
        ///
        /// ★ <b>隣接フレームの差分がほぼ 0、という形ではこの不具合を検出できない。</b>
        ///   dt を極小にすると、この規模の <c>now</c> では float の刻み幅（〜0.06 rad）が
        ///   真の位相差よりずっと大きいため、壊れていても差分はやはり 0 に潰れて
        ///   見分けが付かない（実測で確認済み）。ここでは実際のフレーム間隔に近い
        ///   <c>dt = 1/30秒</c> を使い、各周期・各時刻の値を<b>二重精度で計算した sin の真値</b>
        ///   と直接突き合わせる（畳んで float 化していれば真値と一致し続けるはず）。
        /// </summary>
        [Test]
        public void StaysAccurateAfterWeeksOfUptime()
        {
            // ★ 症状は7日程度でも実測されているが、成分によっては特定の日数で
            //   たまたま誤差が小さく出ることがある（周期の整数倍に近いなど）ので、
            //   数値マージンを確実に取れる 30日を使う。
            const double thirtyDays = 30.0 * 86400.0;
            const double dt = 1.0 / 30.0;

            // ★ 実際に IdlePose / GazeAim が渡す周期を代表させる
            //   （呼吸 4秒・重心移動 7/11秒・首の微動 13秒・視線の漂い 5.3/8.7秒）。
            foreach (var period in new[] { 4f, 5.3f, 7f, 8.7f, 11f, 13f })
            {
                foreach (var t in new[] { thirtyDays, thirtyDays + dt })
                {
                    var actual = Mathf.Sin(Oscillator.Phase(t, period));

                    // 二重精度のまま計算した「真値」。float へ落とすのは最後の1回だけ。
                    var trueValue = (float)Math.Sin(2.0 * Math.PI * t / period);

                    Assert.That(actual, Is.EqualTo(trueValue).Within(1e-3f));
                }
            }
        }
    }
}
