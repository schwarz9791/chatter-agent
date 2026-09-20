using System.Collections.Generic;
using ChatterMascot.Settings;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// 設定パネルの保留（デバウンス）。<b>#85 のレビューで踏んだ穴をここで固定する。</b>
    /// </summary>
    [TestFixture]
    public sealed class PendingChangesTests
    {
        private const float Due = PendingChanges.DebounceSeconds;

        private static PendingChanges New()
        {
            return new PendingChanges();
        }

        [Test]
        public void StartsEmpty()
        {
            var pending = New();

            Assert.That(pending.IsEmpty, Is.True);
            Assert.That(pending.HasScale, Is.False);
            // ★ 空なら締め切りは来ない（PositiveInfinity）
            Assert.That(pending.Due(1000f), Is.False);
        }

        // ---- 起点（A-3） ----

        /// <summary>★ 保留が無ければホストの現在値がそのまま起点。</summary>
        [Test]
        public void FallsBackToTheHostValue()
        {
            var host = MascotSettings.Defaults.WithVolume(0.4f);

            Assert.That(New().Base(host).Volume, Is.EqualTo(0.4f));
        }

        /// <summary>
        /// ★★ <b>#85 レビュー A-3。</b> 保留があるならそれを起点にすること。
        ///   ホストの値から積むと、デバウンス中の保留が<b>あとから巻き戻す</b> ——
        ///   「まばたきを外したのに、音量の保留が着地した瞬間に戻る」。
        /// </summary>
        [Test]
        public void BuildsOnTopOfWhatIsAlreadyPending()
        {
            var pending = New();
            var host = MascotSettings.Defaults;   // blink = true

            // 1. 音量スライダーを離す（保留に載る）
            pending.Defer(host.WithVolume(0.5f), 0f);

            // 2. 300ms 以内に「まばたき」を外す —— 起点は保留の側
            var next = pending.Base(host).WithBlink(false);
            pending.Defer(next, 0.1f);

            // 3. 締め切りで着地したものに、両方が入っていること
            MascotSettings? settings;
            float? scale;
            List<KeyValuePair<string, JToken>> core;
            pending.Take(out settings, out scale, out core);

            Assert.That(settings.HasValue, Is.True);
            Assert.That(settings.Value.Volume, Is.EqualTo(0.5f), "音量が落ちていない");
            Assert.That(settings.Value.Blink, Is.False, "★ まばたきが true に戻っていない");
        }

        // ---- 締め切り ----

        /// <summary>★ 触るたびに延ばす（＝最後の操作から数える）</summary>
        [Test]
        public void CountsFromTheLastTouch()
        {
            var pending = New();
            pending.Defer(MascotSettings.Defaults.WithVolume(0.5f), 0f);
            Assert.That(pending.Due(Due), Is.True);

            pending.Defer(MascotSettings.Defaults.WithVolume(0.6f), 1f);
            Assert.That(pending.Due(1f + Due * 0.5f), Is.False, "延びていること");
            Assert.That(pending.Due(1f + Due), Is.True);
        }

        /// <summary>★ 種類が違っても締め切りは1つ（1回の操作で3つ走らせない）</summary>
        [Test]
        public void SharesOneDeadlineAcrossKinds()
        {
            var pending = New();
            pending.Queue("ttsSpeedScale", 1.2f, 0f);
            pending.DeferScale(1.5f, 0.2f);

            Assert.That(pending.Due(0.2f + Due * 0.5f), Is.False);
            Assert.That(pending.Due(0.2f + Due), Is.True);
        }

        // ---- core への変更 ----

        /// <summary>★ 同じキーは上書きする（矢印キーの連打で何度も PATCH しない）</summary>
        [Test]
        public void OverwritesTheSameCoreKey()
        {
            var pending = New();
            pending.Queue("ttsSpeedScale", 1.1f, 0f);
            pending.Queue("ttsSpeedScale", 1.4f, 0.1f);
            pending.Queue("aiSummaryEnabled", true, 0.1f);

            MascotSettings? settings;
            float? scale;
            List<KeyValuePair<string, JToken>> core;
            pending.Take(out settings, out scale, out core);

            Assert.That(core.Count, Is.EqualTo(2));
            Assert.That(core.Find(e => e.Key == "ttsSpeedScale").Value.Value<float>(), Is.EqualTo(1.4f));
        }

        // ---- 取り出しと掃除（A-4 / リセット） ----

        /// <summary>★ 取り出したら空。適用の途中で届いた操作と混ざらない</summary>
        [Test]
        public void EmptiesItselfWhenTaken()
        {
            var pending = New();
            pending.Defer(MascotSettings.Defaults.WithVolume(0.5f), 0f);
            pending.DeferScale(1.5f, 0f);
            pending.Queue("ttsSpeedScale", 1.2f, 0f);

            MascotSettings? settings;
            float? scale;
            List<KeyValuePair<string, JToken>> core;
            pending.Take(out settings, out scale, out core);

            Assert.That(settings.HasValue && scale.HasValue && core.Count == 1, Is.True);
            Assert.That(pending.IsEmpty, Is.True);
            Assert.That(pending.HasScale, Is.False);
            Assert.That(pending.Due(1000f), Is.False, "★ 締め切りも一緒に落ちる");
        }

        /// <summary>
        /// ★★ 「すべての設定をリセット」の手前で呼ぶもの。残っていると、
        ///   既定へ戻した<b>後ろ</b>に古い値が着地する。
        /// </summary>
        [Test]
        public void ClearDropsEverything()
        {
            var pending = New();
            pending.Defer(MascotSettings.Defaults.WithVolume(0.5f), 0f);
            pending.DeferScale(1.5f, 0f);
            pending.Queue("ttsSpeedScale", 1.2f, 0f);

            pending.Clear();

            Assert.That(pending.IsEmpty, Is.True);
            Assert.That(pending.Due(1000f), Is.False);
        }

        /// <summary>
        /// ★ 「位置と大きさをリセット」は<b>窓だけ</b>戻すので、同じ窓に居た
        ///   音量や話す速さまで道連れにしないこと。
        /// </summary>
        [Test]
        public void ClearScaleKeepsTheOtherPendingChanges()
        {
            var pending = New();
            pending.Defer(MascotSettings.Defaults.WithVolume(0.5f), 0f);
            pending.Queue("ttsSpeedScale", 1.2f, 0f);
            pending.DeferScale(1.5f, 0f);

            pending.ClearScale();

            Assert.That(pending.HasScale, Is.False);
            Assert.That(pending.IsEmpty, Is.False, "★ 音量と話す速さは残る");

            MascotSettings? settings;
            float? scale;
            List<KeyValuePair<string, JToken>> core;
            pending.Take(out settings, out scale, out core);

            Assert.That(scale.HasValue, Is.False);
            Assert.That(settings.Value.Volume, Is.EqualTo(0.5f));
            Assert.That(core.Count, Is.EqualTo(1));
        }
    }
}
