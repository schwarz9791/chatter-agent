using System;
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

        /// <summary>既定パラメータでの振幅上限。どの時刻をサンプルしても各成分の上限を超えない。</summary>
        [Test]
        public void StaysWithinAmplitudeBounds()
        {
            var p = IdleParams.Default;

            for (var t = 0.0; t < 60.0; t += 0.37)
            {
                var s = Sample(t, p);
                Assert.That(Mathf.Abs(s.HipsOffsetY), Is.LessThanOrEqualTo(p.BreathHipsMeters + 1e-4f));
                Assert.That(Mathf.Abs(s.ChestEuler.x), Is.LessThanOrEqualTo(p.BreathChestDegrees + 1e-4f));
                Assert.That(Mathf.Abs(s.SpineEuler.z), Is.LessThanOrEqualTo(p.SwayDegrees + 1e-4f));
                Assert.That(Mathf.Abs(s.NeckEuler.y), Is.LessThanOrEqualTo(p.NeckDegrees + 1e-4f));
                Assert.That(Mathf.Abs(s.HeadEuler.y), Is.LessThanOrEqualTo(p.NeckDegrees + 1e-4f));
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

            for (var t = 0.0; t < 60.0; t += 0.37)
            {
                var s = Sample(t, p);
                Assert.That(Mathf.Abs(s.NeckEuler.y + s.HeadEuler.y), Is.LessThanOrEqualTo(p.NeckDegrees + 1e-4f));
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

        /// <summary>
        /// このアプリは常駐する（デスクトップに出しっぱなしにするのが用途そのもの）ので、
        /// <c>now</c>（<c>Time.realtimeSinceStartupAsDouble</c>）は日単位まで伸びる。
        ///
        /// ★ <c>Phase</c> が周期で畳まずに <c>now</c> を直接 float 化すると、この規模の
        ///   <c>now</c> では <c>Mathf.Sin</c> に渡る引数そのものが float の刻み幅
        ///   （数十分の一 rad）で丸められ、出力が「その時刻での正しい sin 値」から
        ///   大きくずれる（実測: 30日相当で sin 値の誤差が 0.002〜0.08）。
        ///
        /// ★ <b><see cref="IsContinuousOverASmallTimeStep"/> と同じ「隣接フレームの差分が
        ///   ほぼ 0」という形ではこの不具合を検出できない。</b> あちらは <c>dt = 1e-6</c> を
        ///   使うが、この規模の <c>now</c> では float の刻み幅（〜0.06 rad）が
        ///   <c>dt = 1e-6</c> 相当の真の位相差（〜1e-6 rad）よりずっと大きいため、
        ///   壊れていても差分はやはり 0 に潰れて見分けが付かない（実測で確認済み）。
        ///   ここでは実際のフレーム間隔に近い <c>dt = 1/30秒</c> を使い、各時刻の値を
        ///   <b>二重精度で計算した sin の真値</b>と直接突き合わせる
        ///   （畳んで float 化していれば真値と一致し続けるはず）。
        /// </summary>
        [Test]
        public void StaysAccurateAfterWeeksOfUptime()
        {
            var p = IdleParams.Default;

            // ★ 症状は7日程度でも実測されているが、成分によっては特定の日数で
            //   たまたま誤差が小さく出ることがある（周期の整数倍に近いなど）ので、
            //   数値マージンを確実に取れる 30日を使う。
            const double thirtyDays = 30.0 * 86400.0;
            const double dt = 1.0 / 30.0;

            foreach (var t in new[] { thirtyDays, thirtyDays + dt })
            {
                var s = Sample(t, p);

                // 二重精度のまま計算した「真値」。float へ落とすのは最後の1回だけ。
                var chestTrue = (float)(Math.Sin(2.0 * Math.PI * t / p.BreathSeconds) * p.BreathChestDegrees);
                var spineTrue = (float)((Math.Sin(2.0 * Math.PI * t / p.SwaySecondsA) * 0.5
                                        + Math.Sin(2.0 * Math.PI * t / p.SwaySecondsB) * 0.5) * p.SwayDegrees);
                var neckTotalTrue = (float)(Math.Sin(2.0 * Math.PI * t / p.NeckSeconds) * p.NeckDegrees);

                Assert.That(s.ChestEuler.x, Is.EqualTo(chestTrue).Within(1e-3f));
                Assert.That(s.SpineEuler.z, Is.EqualTo(spineTrue).Within(1e-3f));
                Assert.That(s.NeckEuler.y + s.HeadEuler.y, Is.EqualTo(neckTotalTrue).Within(1e-3f));
            }
        }
    }
}
