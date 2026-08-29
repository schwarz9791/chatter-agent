using ChatterMascot.Protocol;
using ChatterMascot.Vrm;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <see cref="FacePolicy"/>。emotion / kind / 時間 → VRM の expression weight。
    ///
    /// ★ <b>対応表そのものは <see cref="FacePolicy.Target"/> を見る。</b>
    ///   <see cref="FacePolicy.Evaluate"/> は指数緩和を挟むので 1.0 に到達せず、等値比較が書けない。
    /// </summary>
    [TestFixture]
    public sealed class FacePolicyTests
    {
        private static FaceParams Params(
            float lerp = 0.15f,
            float hold = 1.5f,
            float promptSurprise = 0f,
            float blinkSuppressAboveHappy = 0.1f,
            bool useNeutralExpression = false,
            float mouthScaleHappy = 0f,
            float mouthScaleSad = 0f)
        {
            return new FaceParams(
                lerp, hold, promptSurprise, blinkSuppressAboveHappy, useNeutralExpression,
                mouthScaleHappy, mouthScaleSad);
        }

        private static FaceInput Input(
            bool speaking = true,
            Emotion emotion = Emotion.Neutral,
            SpeechKind kind = SpeechKind.Assistant,
            float mouth = 0f,
            float blink = 0f,
            double now = 100.0,
            double speechEndedAt = double.NegativeInfinity)
        {
            return new FaceInput(speaking, emotion, kind, mouth, blink, now, speechEndedAt);
        }

        /// <summary>合計（表情チャンネルのみ）。one-hot であることの確認に使う</summary>
        private static float ExpressionSum(in FaceWeights w)
        {
            return w.Happy + w.Angry + w.Sad + w.Relaxed + w.Surprised + w.Neutral;
        }

        [Test]
        public void EachEmotionRaisesExactlyOneChannel()
        {
            Assert.That(FacePolicy.Target(Input(emotion: Emotion.Happy), Params()).Happy, Is.EqualTo(1f));
            Assert.That(FacePolicy.Target(Input(emotion: Emotion.Angry), Params()).Angry, Is.EqualTo(1f));
            Assert.That(FacePolicy.Target(Input(emotion: Emotion.Sad), Params()).Sad, Is.EqualTo(1f));
            Assert.That(FacePolicy.Target(Input(emotion: Emotion.Relaxed), Params()).Relaxed, Is.EqualTo(1f));
            Assert.That(FacePolicy.Target(Input(emotion: Emotion.Surprised), Params()).Surprised, Is.EqualTo(1f));

            // ★ 他のチャンネルは 0（合計が 1 なら、立っているのは1本だけ）
            foreach (var emotion in new[] { Emotion.Happy, Emotion.Angry, Emotion.Sad, Emotion.Relaxed, Emotion.Surprised })
            {
                var w = FacePolicy.Target(Input(emotion: emotion), Params());
                Assert.That(ExpressionSum(w), Is.EqualTo(1f).Within(1e-6f), $"emotion={emotion}");
            }
        }

        /// <summary>
        /// ★ <b><c>Neutral</c> は「全部 0」。</b> <c>vita.vrm</c> の <c>neutral</c> は
        /// <c>Fcl_ALL_Neutral</c> に weight 1.0 で bind されていて、立てたまま <c>happy</c> を
        /// 重ねると二重にブレンドされる（モデルによっては別の顔にもなる）。
        /// </summary>
        [Test]
        public void NeutralRaisesNothingByDefault()
        {
            var w = FacePolicy.Target(Input(emotion: Emotion.Neutral), Params());

            Assert.That(ExpressionSum(w), Is.EqualTo(0f));
            Assert.That(w.Neutral, Is.EqualTo(0f));
        }

        [Test]
        public void NeutralUsesTheNeutralExpressionWhenEnabled()
        {
            var w = FacePolicy.Target(Input(emotion: Emotion.Neutral), Params(useNeutralExpression: true));

            Assert.That(w.Neutral, Is.EqualTo(1f));
            Assert.That(ExpressionSum(w), Is.EqualTo(1f));
        }

        /// <summary>
        /// ★ <b>猶予は「余韻」ではなく、文と文の分断を埋めるためのもの。</b>
        /// 1文＝1レコード＝1音声ファイルなので、<c>PlaybackQueue</c> は文の切れ目で必ず
        /// <c>Speaking</c> を false に落とす。ここが効かないとメッセージの途中で毎文顔が抜ける。
        /// </summary>
        [Test]
        public void HoldsTheLastEmotionWhileWithinTheGracePeriod()
        {
            var p = Params(hold: 1.5f);
            var w = FacePolicy.Target(
                Input(speaking: false, emotion: Emotion.Happy, now: 101.0, speechEndedAt: 100.0), p);

            Assert.That(w.Happy, Is.EqualTo(1f));
        }

        [Test]
        public void RelaxesToNeutralAfterTheGracePeriod()
        {
            var p = Params(hold: 1.5f);
            var w = FacePolicy.Target(
                Input(speaking: false, emotion: Emotion.Happy, now: 102.0, speechEndedAt: 100.0), p);

            Assert.That(ExpressionSum(w), Is.EqualTo(0f));
        }

        /// <summary>
        /// 一度も喋っていない間（<c>SpeechEndedAt</c> が <c>-∞</c>）は、猶予をどう設定しても
        /// 表情を出さない。★ 差が <c>+∞</c> になるので、起動直後に前の顔が残ることはない。
        /// </summary>
        [Test]
        public void ShowsNothingBeforeTheFirstUtterance()
        {
            var w = FacePolicy.Target(
                Input(speaking: false, emotion: Emotion.Happy, now: 0.0, speechEndedAt: double.NegativeInfinity),
                Params(hold: 3600f));

            Assert.That(ExpressionSum(w), Is.EqualTo(0f));
        }

        [Test]
        public void AaFollowsMouthWhileSpeaking()
        {
            var w = FacePolicy.Target(Input(speaking: true, mouth: 0.7f), Params());

            Assert.That(w.Aa, Is.EqualTo(0.7f));
        }

        /// <summary>
        /// ★ <b>UniVRM の weight は自動でゼロに戻らない</b>（<c>_inputWeights</c> は保持され続ける）。
        /// 猶予の対象にすると、喋り終わっても口が開いたままになる。
        /// </summary>
        [Test]
        public void AaDropsToZeroImmediatelyWhenSpeechEnds()
        {
            var p = Params(hold: 1.5f);
            var input = Input(speaking: false, emotion: Emotion.Happy, mouth: 1f, now: 100.1, speechEndedAt: 100.0);

            // 表情はまだ猶予の中で保たれているのに、口だけは 0 に落ちている
            var target = FacePolicy.Target(input, p);
            Assert.That(target.Happy, Is.EqualTo(1f));
            Assert.That(target.Aa, Is.EqualTo(0f));

            // 補間を挟んでも同じ（Aa は緩和しない）
            var evaluated = FacePolicy.Evaluate(input, new FaceWeights(0f, 0f, 0f, 0f, 0f, 0f, 1f, 0f), 1f / 30f, p);
            Assert.That(evaluated.Aa, Is.EqualTo(0f));
        }

        [Test]
        public void BlinkPassesThroughWithoutSmoothing()
        {
            var w = FacePolicy.Evaluate(Input(blink: 0.4f), FaceWeights.Zero, 1f / 30f, Params());

            Assert.That(w.Blink, Is.EqualTo(0.4f));
        }

        /// <summary>
        /// ★ <b>同梱 <c>vita.vrm</c> の <c>happy</c> は <c>Fcl_ALL_Joy</c>（目を細める形を含む）</b>
        /// なのに、preset 14個すべて <c>overrideBlink: none</c> なので UniVRM は減衰させない。
        /// cc-mascot が実測で入れているのと同じガード（閾値 0.1）。
        /// </summary>
        [Test]
        public void BlinkIsSuppressedWhileHappyIsStrong()
        {
            // lerp 0 なら 1 フレームで happy = 1.0 に到達する
            var p = Params(lerp: 0f, blinkSuppressAboveHappy: 0.1f);
            var w = FacePolicy.Evaluate(Input(emotion: Emotion.Happy, blink: 1f), FaceWeights.Zero, 1f / 30f, p);

            Assert.That(w.Happy, Is.EqualTo(1f));
            Assert.That(w.Blink, Is.EqualTo(0f));
        }

        [Test]
        public void BlinkSuppressionIsDisabledWhenTheThresholdIsZero()
        {
            var p = Params(lerp: 0f, blinkSuppressAboveHappy: 0f);
            var w = FacePolicy.Evaluate(Input(emotion: Emotion.Happy, blink: 1f), FaceWeights.Zero, 1f / 30f, p);

            Assert.That(w.Happy, Is.EqualTo(1f));
            Assert.That(w.Blink, Is.EqualTo(1f));
        }

        /// <summary>
        /// ★ cc-mascot がやっているのは「happy のときは瞬きを<b>飛ばす</b>」であって
        /// 「進行中の瞬きを<b>切る</b>」ではない。出力を無条件に 0 にすると、閉じ切っている最中
        /// （<c>blink = 1.0</c>）に happy が立った瞬間に1フレームで目が開く段差になる ——
        /// <c>Blink</c> は補間していないので吸収するものが無い。
        /// </summary>
        [Test]
        public void BlinkAlreadyInProgressIsNotCutOff()
        {
            var p = Params(lerp: 0f, blinkSuppressAboveHappy: 0.1f);
            // 直前のフレームで既に閉じ切っている
            var current = new FaceWeights(0f, 0f, 0f, 0f, 0f, 0f, 0f, 1f);

            var w = FacePolicy.Evaluate(Input(emotion: Emotion.Happy, blink: 1f), current, 1f / 30f, p);

            Assert.That(w.Happy, Is.EqualTo(1f));
            Assert.That(w.Blink, Is.EqualTo(1f), "始まった瞬きは最後まで走る");
        }

        /// <summary>瞬きが終わって（<c>current.Blink == 0</c>）以降は、次の瞬きが飛ばされる。</summary>
        [Test]
        public void BlinkIsSuppressedOnceTheEyesAreOpenAgain()
        {
            var p = Params(lerp: 0f, blinkSuppressAboveHappy: 0.1f);
            var current = new FaceWeights(1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f);

            var w = FacePolicy.Evaluate(Input(emotion: Emotion.Happy, blink: 0.2f), current, 1f / 30f, p);

            Assert.That(w.Blink, Is.EqualTo(0f));
        }

        [Test]
        public void BlinkIsNotSuppressedByOtherEmotions()
        {
            var p = Params(lerp: 0f, blinkSuppressAboveHappy: 0.1f);
            var w = FacePolicy.Evaluate(Input(emotion: Emotion.Angry, blink: 1f), FaceWeights.Zero, 1f / 30f, p);

            Assert.That(w.Blink, Is.EqualTo(1f));
        }

        /// <summary>
        /// ★ <c>deltaTime</c> を分割して複数回呼んでも合計の効果が変わらないこと
        /// （<see cref="GazeAim.Smooth"/> の性質。フレームレートで顔の切り替わる速さが変わらない）。
        /// cc-mascot の <c>LERP_FACTOR = 0.1</c> は毎フレーム適用なので、ここが成立しない。
        /// </summary>
        [Test]
        public void SmoothingIsFrameRateIndependent()
        {
            var p = Params(lerp: 0.15f);
            var input = Input(emotion: Emotion.Happy);

            var once = FacePolicy.Evaluate(input, FaceWeights.Zero, 0.1f, p);
            var half = FacePolicy.Evaluate(input, FaceWeights.Zero, 0.05f, p);
            var twice = FacePolicy.Evaluate(input, half, 0.05f, p);

            Assert.That(twice.Happy, Is.EqualTo(once.Happy).Within(1e-5f));
        }

        [Test]
        public void SmoothingApproachesTheTargetWithoutOvershooting()
        {
            var p = Params(lerp: 0.15f);
            var input = Input(emotion: Emotion.Happy);

            var w = FaceWeights.Zero;
            var previous = 0f;
            for (var i = 0; i < 60; i++)
            {
                w = FacePolicy.Evaluate(input, w, 1f / 30f, p);
                Assert.That(w.Happy, Is.GreaterThanOrEqualTo(previous));
                Assert.That(w.Happy, Is.LessThanOrEqualTo(1f));
                previous = w.Happy;
            }

            Assert.That(w.Happy, Is.EqualTo(1f).Within(1e-3f));
        }

        /// <summary>
        /// ★ <b><c>prompt</c> を表情で区別しないこと。</b> <c>prompt</c> フレームにも emotion が
        /// 載っている（<c>AskUserQuestion</c> の質問文は <c>？</c> で終わるので、分類器は
        /// ほぼ確実に <c>surprised</c> を返す）。重ねると「怒りながら驚いた顔」になる。
        /// 区別は視線・姿勢（#59）と瞬きの3チャンネルで足りている。
        /// </summary>
        [Test]
        public void PromptDoesNotChangeTheExpressionByDefault()
        {
            var assistant = FacePolicy.Target(Input(emotion: Emotion.Angry, kind: SpeechKind.Assistant), Params());
            var prompt = FacePolicy.Target(Input(emotion: Emotion.Angry, kind: SpeechKind.Prompt), Params());

            Assert.That(prompt.Angry, Is.EqualTo(assistant.Angry));
            Assert.That(prompt.Surprised, Is.EqualTo(assistant.Surprised));
        }

        [Test]
        public void PromptSurpriseIsAddedOnlyWhenConfigured()
        {
            var p = Params(promptSurprise: 0.3f);
            var w = FacePolicy.Target(Input(emotion: Emotion.Neutral, kind: SpeechKind.Prompt), p);

            Assert.That(w.Surprised, Is.EqualTo(0.3f));

            // ★ 既に surprised が立っていても 1.0 を超えない
            var saturated = FacePolicy.Target(Input(emotion: Emotion.Surprised, kind: SpeechKind.Prompt), p);
            Assert.That(saturated.Surprised, Is.EqualTo(1f));
        }

        /// <summary>
        /// ★ 全パラメータ 0 の恒等入力（<c>IdlePoseTests</c> / <c>GazeAimTests</c> と同じ形）。
        /// 補間・猶予・上乗せ・抑制がすべて無効になり、<see cref="FacePolicy.Evaluate"/> は
        /// <see cref="FacePolicy.Target"/> と一致する。
        /// </summary>
        [Test]
        public void AllZeroParamsMakeEvaluateEqualTarget()
        {
            var p = new FaceParams(0f, 0f, 0f, 0f, false);
            var input = Input(emotion: Emotion.Happy, kind: SpeechKind.Prompt, mouth: 0.5f, blink: 0.5f);

            var target = FacePolicy.Target(input, p);
            var evaluated = FacePolicy.Evaluate(input, new FaceWeights(0.9f, 0.9f, 0.9f, 0.9f, 0.9f, 0.9f, 0.9f, 0.9f), 1f / 30f, p);

            Assert.That(evaluated.Happy, Is.EqualTo(target.Happy));
            Assert.That(evaluated.Angry, Is.EqualTo(target.Angry));
            Assert.That(evaluated.Sad, Is.EqualTo(target.Sad));
            Assert.That(evaluated.Relaxed, Is.EqualTo(target.Relaxed));
            Assert.That(evaluated.Surprised, Is.EqualTo(target.Surprised));
            Assert.That(evaluated.Neutral, Is.EqualTo(target.Neutral));
            Assert.That(evaluated.Aa, Is.EqualTo(target.Aa));
            Assert.That(evaluated.Blink, Is.EqualTo(target.Blink));
        }

        [Test]
        public void ClampsMouthAndBlinkIntoRange()
        {
            var w = FacePolicy.Target(Input(mouth: 3f, blink: -1f), Params());

            Assert.That(w.Aa, Is.EqualTo(1f));
            Assert.That(w.Blink, Is.EqualTo(0f));
        }

        // ---- 口のスケール（#58） ----

        /// <summary>
        /// ★ <b>モデル側は守ってくれない。</b> 笑顔（<c>Fcl_ALL_Joy</c>）と <c>aa</c>
        ///   （<c>Fcl_MTH_A</c>）は別のモーフで、同梱 <c>vita.vrm</c> は preset 14個すべて
        ///   <c>overrideMouth: none</c> なので素で加算される。cc-mascot の
        ///   <c>useVRM.ts</c> の <c>setMouthOpen</c> と同じ倍率。
        /// ★ <b>判定は緩和後の weight</b>（<c>lerp = 0</c> にして <c>happy</c> を立て切らせている）。
        /// </summary>
        [Test]
        public void MouthIsScaledDownWhileHappy()
        {
            var w = FacePolicy.Evaluate(
                Input(speaking: true, emotion: Emotion.Happy, mouth: 1f),
                FaceWeights.Zero, 1f / 30f, Params(lerp: 0f, mouthScaleHappy: 0.2f));

            Assert.That(w.Happy, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(w.Aa, Is.EqualTo(0.2f).Within(1e-5f));
        }

        [Test]
        public void MouthIsScaledDownWhileSad()
        {
            var w = FacePolicy.Evaluate(
                Input(speaking: true, emotion: Emotion.Sad, mouth: 1f),
                FaceWeights.Zero, 1f / 30f, Params(lerp: 0f, mouthScaleSad: 0.5f));

            Assert.That(w.Aa, Is.EqualTo(0.5f).Within(1e-5f));
        }

        /// <summary>
        /// ★ <b>0 は「口を閉じる」ではなく「掛けない」。</b> <see cref="FaceParams"/> の
        ///   他の値と同じ「0 = 無効」の語彙に揃えてある —— これで
        ///   <see cref="AllZeroParamsMakeEvaluateEqualTarget"/> が保たれる。
        /// </summary>
        [Test]
        public void ZeroMouthScaleLeavesTheMouthAlone()
        {
            var w = FacePolicy.Evaluate(
                Input(speaking: true, emotion: Emotion.Happy, mouth: 1f),
                FaceWeights.Zero, 1f / 30f, Params(lerp: 0f, mouthScaleHappy: 0f, mouthScaleSad: 0f));

            Assert.That(w.Aa, Is.EqualTo(1f).Within(1e-5f));
        }

        /// <summary>
        /// ★★ <b><c>Aa</c> だけが 0..1 の契約を破りうる。</b> 他のチャンネルは 0..1 の目標へ
        ///   緩和するので範囲外にならないが、<c>Aa</c> は倍率を掛けるので 1 を超えうる。
        ///   <b>Unity 側は潰してくれない</b>（UniVRM の <c>ExpressionMerger</c> に <c>Clamp01</c> は
        ///   無く、<c>legacyClampBlendShapeWeights</c> は 0）ので、ここで閉じる。
        /// </summary>
        [Test]
        public void MouthNeverExceedsOneEvenWithAScaleAboveOne()
        {
            var w = FacePolicy.Evaluate(
                Input(speaking: true, emotion: Emotion.Happy, mouth: 1f),
                FaceWeights.Zero, 1f / 30f, Params(lerp: 0f, mouthScaleHappy: 2f));

            Assert.That(w.Aa, Is.EqualTo(1f));
        }

        /// <summary>
        /// ★ 表情が立ち上がる途中では倍率も途中の値になる（段差が入らない）。
        ///   目標ではなく<b>実際に適用される weight</b> で掛けている証拠。
        /// </summary>
        [Test]
        public void MouthScaleFollowsTheEasedWeight()
        {
            var half = new FaceWeights(0.5f, 0f, 0f, 0f, 0f, 0f, 0f, 0f);

            // lerp を 0 にすると happy が即 1.0 になってしまうので、前フレームの値を
            // そのまま採る（tau が十分大きければ 1フレームではほとんど動かない）
            var w = FacePolicy.Evaluate(
                Input(speaking: true, emotion: Emotion.Happy, mouth: 1f),
                half, 0f, Params(lerp: 10f, mouthScaleHappy: 0.2f));

            Assert.That(w.Happy, Is.EqualTo(0.5f).Within(1e-4f));
            // Lerp(1, 0.2, 0.5) = 0.6
            Assert.That(w.Aa, Is.EqualTo(0.6f).Within(1e-4f));
        }

    }
}
