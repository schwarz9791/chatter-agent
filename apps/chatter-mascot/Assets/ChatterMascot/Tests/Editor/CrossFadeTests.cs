using ChatterMascot.Vrm;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <see cref="CrossFade"/>。クロスフェードの数式だけを固定する（実際に2つの
    /// <c>IVrm10Animation</c> を混ぜる <c>CrossFadeAnimation</c> は VRM10 に依存するので
    /// EditMode からは見えない）。
    /// </summary>
    [TestFixture]
    public sealed class CrossFadeTests
    {
        // ---- Progress ----

        [Test]
        public void ProgressStartsAtZero()
        {
            Assert.That(CrossFade.Progress(startedAt: 10.0, now: 10.0, durationSeconds: 0.5f), Is.EqualTo(0f));
        }

        [Test]
        public void ProgressReachesOneAtTheDuration()
        {
            Assert.That(CrossFade.Progress(startedAt: 10.0, now: 10.5, durationSeconds: 0.5f), Is.EqualTo(1f));
        }

        [Test]
        public void ProgressIsHalfwayAtHalfTheDuration()
        {
            Assert.That(CrossFade.Progress(startedAt: 10.0, now: 10.25, durationSeconds: 0.5f),
                Is.EqualTo(0.5f).Within(1e-5f));
        }

        [Test]
        public void ProgressClampsPastTheDuration()
        {
            Assert.That(CrossFade.Progress(startedAt: 0.0, now: 999.0, durationSeconds: 0.5f), Is.EqualTo(1f));
        }

        [Test]
        public void ProgressClampsBeforeTheStart()
        {
            // 時計が巻き戻っても壊れない（realtimeSinceStartup では起きないはずだが、他のクラスと同様に保険を見る）
            Assert.That(CrossFade.Progress(startedAt: 10.0, now: 5.0, durationSeconds: 0.5f), Is.EqualTo(0f));
        }

        /// <summary>★ <c>duration &lt;= 0</c> は即 1（フェード無しで完了扱い）。ゼロ除算しないこと。</summary>
        [Test]
        public void ProgressIsImmediatelyDoneWhenDurationIsZeroOrNegative()
        {
            Assert.That(CrossFade.Progress(startedAt: 10.0, now: 10.0, durationSeconds: 0f), Is.EqualTo(1f));
            Assert.That(CrossFade.Progress(startedAt: 10.0, now: 10.0, durationSeconds: -1f), Is.EqualTo(1f));
        }

        // ---- Ease ----

        [Test]
        public void EaseEndpointsMatchTheInput()
        {
            Assert.That(CrossFade.Ease(0f), Is.EqualTo(0f));
            Assert.That(CrossFade.Ease(1f), Is.EqualTo(1f));
        }

        [Test]
        public void EaseIsSymmetricAtTheMidpoint()
        {
            Assert.That(CrossFade.Ease(0.5f), Is.EqualTo(0.5f).Within(1e-5f));
        }

        [Test]
        public void EaseClampsOutOfRangeInput()
        {
            Assert.That(CrossFade.Ease(-1f), Is.EqualTo(0f));
            Assert.That(CrossFade.Ease(2f), Is.EqualTo(1f));
        }

        /// <summary>smoothstep は単調増加。折り返しが無いこと。</summary>
        [Test]
        public void EaseIsMonotonicallyIncreasing()
        {
            var previous = CrossFade.Ease(0f);
            for (var i = 1; i <= 20; i++)
            {
                var t = i / 20f;
                var value = CrossFade.Ease(t);
                Assert.That(value, Is.GreaterThanOrEqualTo(previous), $"t={t}");
                previous = value;
            }
        }

        // ---- BlendRotation ----

        [Test]
        public void BlendRotationAtZeroIsFrom()
        {
            var from = Quaternion.Euler(0f, 30f, 0f);
            var to = Quaternion.Euler(0f, 90f, 0f);

            var blended = CrossFade.BlendRotation(from, to, 0f);

            Assert.That(Quaternion.Angle(blended, from), Is.LessThan(1e-3f));
        }

        [Test]
        public void BlendRotationAtOneIsTo()
        {
            var from = Quaternion.Euler(0f, 30f, 0f);
            var to = Quaternion.Euler(0f, 90f, 0f);

            var blended = CrossFade.BlendRotation(from, to, 1f);

            Assert.That(Quaternion.Angle(blended, to), Is.LessThan(1e-3f));
        }

        // ---- BlendHips ----

        [Test]
        public void BlendHipsAtZeroIsFrom()
        {
            var from = new Vector3(0f, 0.1f, 0f);
            var to = new Vector3(0f, -0.05f, 0f);

            Assert.That(CrossFade.BlendHips(from, to, 0f), Is.EqualTo(from));
        }

        [Test]
        public void BlendHipsAtOneIsTo()
        {
            var from = new Vector3(0f, 0.1f, 0f);
            var to = new Vector3(0f, -0.05f, 0f);

            Assert.That(CrossFade.BlendHips(from, to, 1f), Is.EqualTo(to));
        }

        [Test]
        public void BlendHipsInterpolatesLinearly()
        {
            var from = new Vector3(0f, 0f, 0f);
            var to = new Vector3(0f, 1f, 0f);

            var blended = CrossFade.BlendHips(from, to, 0.25f);

            Assert.That(blended.y, Is.EqualTo(0.25f).Within(1e-5f));
        }

        // ---- NormalizeHipsDelta ----

        /// <summary>
        /// ★★ 眼目のテスト。<c>idle_loop.vrma</c>（cm スケール、hips y≈90）と VRoid 書き出し
        /// （m スケール、hips y≈1）は単位が実測で約100倍違うが、<b>同じ比率の変位</b>なら
        /// 正規化後は同じ値になること。ここでは両方とも「Tポーズから10%持ち上がった」を表す
        /// 組（<c>(0,99,0)/(0,90,0)</c> と <c>(0,1.1,0)/(0,1,0)</c>）を使う。
        ///
        /// ★ <c>(0,95,0)/(0,90,0)</c> と <c>(0,1.05,0)/(0,1,0)</c> は比率が違う
        ///   （5/90 ≠ 0.05/1）ので、あえて使わない。
        /// </summary>
        [Test]
        public void NormalizesDifferentScalesToTheSameValueForTheSameRatio()
        {
            var cmScale = CrossFade.NormalizeHipsDelta(new Vector3(0f, 99f, 0f), new Vector3(0f, 90f, 0f));
            var mScale = CrossFade.NormalizeHipsDelta(new Vector3(0f, 1.1f, 0f), new Vector3(0f, 1f, 0f));

            Assert.That(cmScale.y, Is.EqualTo(mScale.y).Within(1e-4f));
            Assert.That(cmScale.y, Is.EqualTo(0.1f).Within(1e-4f));
        }

        [Test]
        public void NormalizeHipsDeltaReturnsZeroWhenRawEqualsTPose()
        {
            var normalized = CrossFade.NormalizeHipsDelta(new Vector3(0f, 90f, 0f), new Vector3(0f, 90f, 0f));
            Assert.That(normalized, Is.EqualTo(Vector3.zero));
        }

        /// <summary>★ 高さがほぼ0（異常値）なら、ゼロ除算を避けて差分をそのまま返す。</summary>
        [Test]
        public void NormalizeHipsDeltaFallsBackToTheRawDifferenceWhenTPoseHeightIsNearZero()
        {
            var raw = new Vector3(0f, 5f, 0f);
            var tpose = new Vector3(0f, 0f, 0f);

            var normalized = CrossFade.NormalizeHipsDelta(raw, tpose);

            Assert.That(normalized, Is.EqualTo(raw - tpose));
        }
    }
}
