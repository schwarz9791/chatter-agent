using ChatterMascot.Protocol;
using ChatterMascot.Vrm;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// 手続き的アイドル（呼吸・重心移動・首の微動・腕の静止姿勢）。
    ///
    /// ★ <c>IdlePose</c> は前傾を持たない（持ち主は <c>VrmPoseAccent</c> 1箇所）。
    ///   ここで固定するのは「<c>Prompt</c> で揺れが小さくなること」だけ。
    /// </summary>
    [TestFixture]
    public sealed class IdlePoseTests
    {
        private static IdlePoseSample Sample(double now, IdleParams p, SpeechKind kind = SpeechKind.Assistant, bool speaking = false)
        {
            return IdlePose.Evaluate(now, in p, kind, speaking);
        }

        /// <summary>全振幅を 0 にすれば、時刻をどれだけ進めても出力は恒等（すべて 0）。</summary>
        [Test]
        public void AllZeroParamsYieldIdentity()
        {
            var zero = new IdleParams(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f);

            foreach (var t in new[] { 0.0, 1.23, 100.0, 9999.0 })
            {
                var s = Sample(t, zero);
                Assert.That(s.HipsOffsetY, Is.EqualTo(0f));
                Assert.That(s.SpineEuler, Is.EqualTo(Vector3.zero));
                Assert.That(s.ChestEuler, Is.EqualTo(Vector3.zero));
                Assert.That(s.NeckEuler, Is.EqualTo(Vector3.zero));
                Assert.That(s.HeadEuler, Is.EqualTo(Vector3.zero));
                Assert.That(s.LeftUpperArmEuler, Is.EqualTo(Vector3.zero));
                Assert.That(s.RightUpperArmEuler, Is.EqualTo(Vector3.zero));
                Assert.That(s.LeftLowerArmEuler, Is.EqualTo(Vector3.zero));
                Assert.That(s.RightLowerArmEuler, Is.EqualTo(Vector3.zero));
            }
        }

        /// <summary>
        /// 振幅上限。どの時刻をサンプルしても各成分の上限を超えない。
        ///
        /// ★ <c>IdlePose.Evaluate</c> は <c>gain</c>（<c>speaking</c> なら <c>SpeakingGain</c>、
        ///   さらに <c>kind == Prompt</c> なら <c>PromptDamp</c> も）を全チャンネルへ掛ける
        ///   （実装で確認済み。ここで検査する5成分はすべて対象）。実運用では発話中に
        ///   このゲインが乗った状態で動くので、<c>speaking = false</c>（gain = 1）だけで
        ///   上限を固定すると、実機の可動域（発話中は最大 <c>SpeakingGain</c> 倍）と
        ///   テストの上限が食い違う。<c>kind</c> / <c>speaking</c> の組み合わせごとに
        ///   期待する上限係数を変え、<c>1.3f</c> などを直接書かず <c>IdleParams</c> の
        ///   フィールドから計算する —— <c>SpeakingGain</c> を将来変えてもこのテストが
        ///   自動的に追随するようにするため。
        /// </summary>
        [Test]
        public void StaysWithinAmplitudeBounds()
        {
            var p = IdleParams.Default;

            AssertAmplitudeWithin(p, SpeechKind.Assistant, speaking: false, gain: 1f);
            AssertAmplitudeWithin(p, SpeechKind.Assistant, speaking: true, gain: p.SpeakingGain);
            AssertAmplitudeWithin(p, SpeechKind.Prompt, speaking: true, gain: p.SpeakingGain * p.PromptDamp);
        }

        /// <summary><c>gain</c> を掛けた上限を、時刻を動かしながら検査する（<see cref="StaysWithinAmplitudeBounds"/> 用）。</summary>
        private static void AssertAmplitudeWithin(IdleParams p, SpeechKind kind, bool speaking, float gain)
        {
            for (var t = 0.0; t < 60.0; t += 0.37)
            {
                var s = Sample(t, p, kind, speaking);
                Assert.That(Mathf.Abs(s.HipsOffsetY), Is.LessThanOrEqualTo(p.BreathHipsMeters * gain + 1e-4f));
                Assert.That(Mathf.Abs(s.ChestEuler.x), Is.LessThanOrEqualTo(p.BreathChestDegrees * gain + 1e-4f));
                Assert.That(Mathf.Abs(s.SpineEuler.z), Is.LessThanOrEqualTo(p.SwayDegrees * gain + 1e-4f));
                Assert.That(Mathf.Abs(s.NeckEuler.y), Is.LessThanOrEqualTo(p.NeckDegrees * gain + 1e-4f));
                Assert.That(Mathf.Abs(s.HeadEuler.y), Is.LessThanOrEqualTo(p.NeckDegrees * gain + 1e-4f));
            }
        }

        /// <summary>微小な dt では出力が不連続に跳ばない（sin の連続性がそのまま乗ること）。</summary>
        [Test]
        public void IsContinuousOverASmallTimeStep()
        {
            var p = IdleParams.Default;
            const double dt = 1e-6;

            foreach (var t in new[] { 0.0, 3.3, 12.5, 50.0 })
            {
                var a = Sample(t, p);
                var b = Sample(t + dt, p);

                Assert.That(Mathf.Abs(b.HipsOffsetY - a.HipsOffsetY), Is.LessThan(1e-4f));
                Assert.That(Mathf.Abs(b.SpineEuler.z - a.SpineEuler.z), Is.LessThan(1e-4f));
                Assert.That(Mathf.Abs(b.ChestEuler.x - a.ChestEuler.x), Is.LessThan(1e-4f));
                Assert.That(Mathf.Abs(b.NeckEuler.y - a.NeckEuler.y), Is.LessThan(1e-4f));
            }
        }

        /// <summary>発話中は <c>SpeakingGain</c> のぶん振幅が上がる。</summary>
        [Test]
        public void SpeakingIncreasesAmplitude()
        {
            var p = IdleParams.Default;

            // 呼吸の 1/4 周期（sin が最大になる点）で比較する
            var t = p.BreathSeconds / 4.0;
            var idle = Sample(t, p, SpeechKind.Assistant, speaking: false);
            var speaking = Sample(t, p, SpeechKind.Assistant, speaking: true);

            Assert.That(Mathf.Abs(speaking.HipsOffsetY), Is.GreaterThan(Mathf.Abs(idle.HipsOffsetY)));
            Assert.That(Mathf.Abs(speaking.ChestEuler.x), Is.GreaterThan(Mathf.Abs(idle.ChestEuler.x)));
        }

        /// <summary><c>Prompt</c> は <c>PromptDamp</c> のぶん振幅が下がる（前傾は乗らない）。</summary>
        [Test]
        public void PromptDampensAmplitude()
        {
            var p = IdleParams.Default;

            var t = p.BreathSeconds / 4.0;
            var assistant = Sample(t, p, SpeechKind.Assistant, speaking: false);
            var prompt = Sample(t, p, SpeechKind.Prompt, speaking: false);

            Assert.That(Mathf.Abs(prompt.HipsOffsetY), Is.LessThan(Mathf.Abs(assistant.HipsOffsetY)));
            Assert.That(Mathf.Abs(prompt.ChestEuler.x), Is.LessThan(Mathf.Abs(assistant.ChestEuler.x)));
        }

        /// <summary>
        /// 呼吸・重心移動・首の微動は互いに周期が違うので、
        /// 呼吸の1周期（既定 4秒）が経っても全体としては元の姿勢に戻らない。
        /// </summary>
        [Test]
        public void DoesNotReturnToTheSamePoseAfterTheBreathPeriod()
        {
            var p = IdleParams.Default;

            var t0 = Sample(0.0, p);
            var t1 = Sample(p.BreathSeconds, p);

            // 呼吸だけは周期どおり一致する
            Assert.That(t1.HipsOffsetY, Is.EqualTo(t0.HipsOffsetY).Within(1e-4f));

            // 重心移動・首の微動は違う周期なので一致しない
            Assert.That(Mathf.Abs(t1.SpineEuler.z - t0.SpineEuler.z), Is.GreaterThan(1e-3f));
            Assert.That(Mathf.Abs(t1.NeckEuler.y - t0.NeckEuler.y), Is.GreaterThan(1e-3f));
        }

        /// <summary>
        /// <b>Neck と Head は親子ボーンで、ControlRig のローカル回転が合成される。</b>
        /// <c>NeckEuler.y</c> と <c>HeadEuler.y</c> は<b>それぞれ</b>が <c>NeckDegrees</c> の
        /// チャンネル単体テスト（<see cref="StaysWithinAmplitudeBounds"/>）を独立に通っても、
        /// <b>足し合わせた見た目の可動域</b>が <c>NeckDegrees</c>（首と頭を合わせた合計という定義）を
        /// 超えていないかは別に固定しないと守れない。ここで直接その合計を検査する。
        /// </summary>
        [Test]
        public void NeckAndHeadTogetherStayWithinTheNeckAmplitude()
        {
            var p = IdleParams.Default;

            // ★ StaysWithinAmplitudeBounds と同じ理由で、発話中のゲインも含めて固定すること。
            //   このテストは「首と頭に同じ値を書くと見た目の振幅が2倍になる」（実機で踏んだ）を
            //   守る本命なので、実運用でいちばん大きく振れる条件（speaking = true）が
            //   抜けていると、いちばん破綻しやすい側を検査していないことになる
            AssertNeckPlusHeadWithin(p, SpeechKind.Assistant, speaking: false, gain: 1f);
            AssertNeckPlusHeadWithin(p, SpeechKind.Assistant, speaking: true, gain: p.SpeakingGain);
            AssertNeckPlusHeadWithin(p, SpeechKind.Prompt, speaking: true, gain: p.SpeakingGain * p.PromptDamp);
        }

        /// <summary>首＋頭の合算が <c>NeckDegrees × gain</c> を超えないこと（<see cref="NeckAndHeadTogetherStayWithinTheNeckAmplitude"/> 用）。</summary>
        private static void AssertNeckPlusHeadWithin(IdleParams p, SpeechKind kind, bool speaking, float gain)
        {
            for (var t = 0.0; t < 60.0; t += 0.37)
            {
                var s = Sample(t, p, kind, speaking);
                Assert.That(Mathf.Abs(s.NeckEuler.y + s.HeadEuler.y), Is.LessThanOrEqualTo(p.NeckDegrees * gain + 1e-4f));
            }
        }

        /// <summary>
        /// ControlRig の正規化 T ポーズは腕を左右対称に開いているので、腕を体側へ下ろす
        /// 静止姿勢も左右対称でなければならない。既定パラメータで、上腕・前腕とも
        /// 左右の成分が要素ごとに符号だけ逆で絶対値が一致することを固定する
        /// （どの軸を使うかは実機で確定させる前提なので、特定の軸ではなく Left/Right の
        /// 関係そのものを検査する）。
        /// </summary>
        [Test]
        public void RestArmAnglesAreMirroredLeftToRight()
        {
            var p = IdleParams.Default;
            var s = Sample(0.0, p);

            Assert.That(s.LeftUpperArmEuler.x, Is.EqualTo(-s.RightUpperArmEuler.x).Within(1e-4f));
            Assert.That(s.LeftUpperArmEuler.y, Is.EqualTo(-s.RightUpperArmEuler.y).Within(1e-4f));
            Assert.That(s.LeftUpperArmEuler.z, Is.EqualTo(-s.RightUpperArmEuler.z).Within(1e-4f));
            Assert.That(s.RightUpperArmEuler, Is.Not.EqualTo(Vector3.zero));

            Assert.That(s.LeftLowerArmEuler.x, Is.EqualTo(-s.RightLowerArmEuler.x).Within(1e-4f));
            Assert.That(s.LeftLowerArmEuler.y, Is.EqualTo(-s.RightLowerArmEuler.y).Within(1e-4f));
            Assert.That(s.LeftLowerArmEuler.z, Is.EqualTo(-s.RightLowerArmEuler.z).Within(1e-4f));
            Assert.That(s.RightLowerArmEuler, Is.Not.EqualTo(Vector3.zero));
        }

        /// <summary>
        /// <c>RestUpperArmDegrees</c> / <c>RestLowerArmDegrees</c> を 0 にすると、呼吸などの
        /// 他のチャンネルは既定どおり効いたまま、腕のチャンネルだけが T ポーズ（全ゼロ）の
        /// ままになることを固定する。既存の「全パラメータ 0 で恒等」と同じ趣旨だが、
        /// こちらは腕だけを狙って 0 にできることを確かめる
        /// （実機で踏んだ不具合——呼吸・重心移動は効くのに腕だけ T ポーズのまま——の裏返し）。
        /// </summary>
        [Test]
        public void ZeroRestArmDegreesKeepsArmsInTPose()
        {
            var d = IdleParams.Default;
            var p = new IdleParams(
                d.BreathSeconds, d.BreathHipsMeters, d.BreathChestDegrees,
                d.SwaySecondsA, d.SwaySecondsB, d.SwayDegrees,
                d.NeckSeconds, d.NeckDegrees, d.SpeakingGain, d.PromptDamp,
                restUpperArmDegrees: 0f, restLowerArmDegrees: 0f);

            // 呼吸の 1/4 周期（sin が最大になる点）。腕が 0 のままなことのコントラストを出す
            var t = p.BreathSeconds / 4.0;
            var s = Sample(t, p);

            Assert.That(s.LeftUpperArmEuler, Is.EqualTo(Vector3.zero));
            Assert.That(s.RightUpperArmEuler, Is.EqualTo(Vector3.zero));
            Assert.That(s.LeftLowerArmEuler, Is.EqualTo(Vector3.zero));
            Assert.That(s.RightLowerArmEuler, Is.EqualTo(Vector3.zero));

            // 腕以外は既定どおり動いていること（＝腕だけを狙ってゼロにできている）
            Assert.That(Mathf.Abs(s.ChestEuler.x), Is.GreaterThan(0f));
        }

        /// <summary>
        /// 腕の静止姿勢には、呼吸・重心移動の揺れをあえて乗せていない
        /// （<c>IdlePose.Evaluate</c> のコメント参照 —— 上腕・前腕は肩→肘の2ボーンチェーンで、
        /// それぞれに独立した sin を足すと振り子のようにブラブラして見えるリスクがあるため。
        /// #59 のいちばんの目的は T ポーズの解消であって腕の芝居ではないので、まず静止姿勢の
        /// 符号・角度を実機で確定させることを優先した）。
        /// ここでは「時刻を変えても腕のチャンネルは変化しない」ことでその決定を固定する。
        /// </summary>
        [Test]
        public void RestArmAnglesDoNotOscillateOverTime()
        {
            var p = IdleParams.Default;

            var t0 = Sample(0.0, p);
            var t1 = Sample(p.BreathSeconds / 4.0, p);
            var t2 = Sample(50.0, p);

            Assert.That(t1.LeftUpperArmEuler, Is.EqualTo(t0.LeftUpperArmEuler));
            Assert.That(t1.RightUpperArmEuler, Is.EqualTo(t0.RightUpperArmEuler));
            Assert.That(t1.LeftLowerArmEuler, Is.EqualTo(t0.LeftLowerArmEuler));
            Assert.That(t1.RightLowerArmEuler, Is.EqualTo(t0.RightLowerArmEuler));

            Assert.That(t2.LeftUpperArmEuler, Is.EqualTo(t0.LeftUpperArmEuler));
            Assert.That(t2.RightUpperArmEuler, Is.EqualTo(t0.RightUpperArmEuler));
        }
    }
}
