using System;
using System.Collections.Generic;
using ChatterMascot.Window;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class WindowStateStoreTests
    {
        private static readonly PointRect Rect = new PointRect(1770f, 1598f, 300f, 480f);

        private List<string> _warnings;
        private string _written;

        [SetUp]
        public void SetUp()
        {
            _warnings = new List<string>();
            _written = null;
        }

        private WindowStateStore Store(Func<string> read) =>
            new WindowStateStore(read, text => _written = text, _warnings.Add);

        /// <summary>初回起動。★ 警告しないこと（毎回出ると本物の異常が埋もれる）。</summary>
        [Test]
        public void LoadReturnsNoneWithoutWarningWhenTheFileIsAbsent()
        {
            Assert.That(Store(() => null).Load().Rect.IsValid, Is.False);
            Assert.That(_warnings, Is.Empty);
        }

        [Test]
        public void LoadWarnsAndFallsBackWhenTheFileIsBroken()
        {
            Assert.That(Store(() => "{ぐちゃぐちゃ").Load().Rect.IsValid, Is.False);
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        /// <summary>読み取りが例外を投げても起動を止めない（TCC で弾かれる等）。</summary>
        [Test]
        public void LoadSurvivesAThrowingReader()
        {
            Assert.That(Store(() => throw new InvalidOperationException("boom")).Load().Rect.IsValid, Is.False);
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void RoundTripsThroughSaveAndLoad()
        {
            var store = Store(() => _written);
            Assert.That(store.Save(new WindowState(Rect, "sig")), Is.True);

            var loaded = store.Load();
            Assert.That(loaded.Rect, Is.EqualTo(Rect));
            Assert.That(loaded.DisplaySignature, Is.EqualTo("sig"));
            Assert.That(_warnings, Is.Empty);
        }

        /// <summary>★ 終了処理からも呼ばれるので、書けなくても throw しない。</summary>
        [Test]
        public void SaveReportsFailureInsteadOfThrowing()
        {
            var store = new WindowStateStore(() => null, _ => throw new UnauthorizedAccessException("ro"), _warnings.Add);

            Assert.That(store.Save(new WindowState(Rect, "sig")), Is.False);
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }
    }
}
