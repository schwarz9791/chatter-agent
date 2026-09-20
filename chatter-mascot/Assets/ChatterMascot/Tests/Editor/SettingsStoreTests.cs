using System;
using System.Collections.Generic;
using ChatterMascot.Settings;
using ChatterMascot.Ui;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class SettingsStoreTests
    {
        private List<string> _warnings;
        private string _written;
        private string _file;
        private string _stamp;
        private int _reads;

        [SetUp]
        public void SetUp()
        {
            _warnings = new List<string>();
            _written = null;
            _file = null;
            _stamp = null;
            _reads = 0;
        }

        private SettingsStore Store()
        {
            return new SettingsStore(
                () => { _reads++; return _file; },
                () => _stamp,
                text => { _written = text; _file = text; _stamp = Guid.NewGuid().ToString(); },
                _warnings.Add);
        }

        /// <summary>初回起動。★ 警告しないこと（毎回出ると本物の異常が埋もれる）。</summary>
        [Test]
        public void UsesDefaultsWithoutWarningWhenTheFileIsAbsent()
        {
            var settings = Store().Current;

            Assert.That(settings.Muted, Is.False);
            Assert.That(settings.MuteHotKey, Is.EqualTo(HotKeySpec.Default));
            Assert.That(_warnings, Is.Empty);
        }

        [Test]
        public void RoundTripsThroughSaveAndReload()
        {
            var store = Store();
            Assert.That(store.Save(MascotSettings.Defaults.WithMuted(true).WithMuteHotKey("cmd+shift+m")), Is.True);
            Assert.That(_written, Is.Not.Null);

            Assert.That(store.Current.Muted, Is.True);
            Assert.That(store.Current.MuteHotKey, Is.EqualTo("cmd+shift+m"));
        }

        /// <summary>
        /// ★★ <b>スタンプが同じなら読まないこと。</b> <c>Current</c> は毎フレームに近い頻度で
        /// 触られるので、そのたびにファイルを開くわけにはいかない。
        /// </summary>
        [Test]
        public void DoesNotReadAgainWhileTheStampIsUnchanged()
        {
            _file = SettingsJson.Write(MascotSettings.Defaults.WithMuted(true));
            _stamp = "1:100";

            var store = Store();
            Assert.That(store.Current.Muted, Is.True);
            var afterFirst = _reads;

            for (var i = 0; i < 5; i++) { var unused = store.Current; }

            Assert.That(_reads, Is.EqualTo(afterFirst), "スタンプが同じ間は読み直さない");
        }

        [Test]
        public void ReadsAgainWhenTheStampChanges()
        {
            _file = SettingsJson.Write(MascotSettings.Defaults.WithMuted(false));
            _stamp = "1:100";

            var store = Store();
            Assert.That(store.Current.Muted, Is.False);

            _file = SettingsJson.Write(MascotSettings.Defaults.WithMuted(true));
            _stamp = "2:120";

            Assert.That(store.Refresh(), Is.True, "変わったら true");
            Assert.That(store.Current.Muted, Is.True);
        }

        /// <summary>内容が同じなら「変わった」と言わない（メニューを毎秒作り直さないため）。</summary>
        [Test]
        public void ReportsNoChangeWhenTheContentIsTheSame()
        {
            _file = SettingsJson.Write(MascotSettings.Defaults.WithMuted(true));
            _stamp = "1:100";

            var store = Store();
            var unused = store.Current;

            _stamp = "2:100";
            Assert.That(store.Refresh(), Is.False);
        }

        /// <summary>
        /// ★★ <b>壊れた JSON では直前値を維持すること。</b> 既定へ戻すと、
        /// エディタが保存の途中に書いた半端なファイルを読んだだけでミュートが解けて喋り出す。
        /// </summary>
        [Test]
        public void KeepsThePreviousValueWhenTheFileBreaks()
        {
            _file = SettingsJson.Write(MascotSettings.Defaults.WithMuted(true));
            _stamp = "1:100";

            var store = Store();
            Assert.That(store.Current.Muted, Is.True);

            _file = "{ぐちゃぐちゃ";
            _stamp = "2:20";

            Assert.That(store.Refresh(), Is.False);
            Assert.That(store.Current.Muted, Is.True, "直前値を使い続ける");
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void SurvivesAThrowingReader()
        {
            var store = new SettingsStore(
                () => throw new InvalidOperationException("boom"),
                () => "1:1",
                text => { },
                _warnings.Add);

            Assert.That(store.Current.MuteHotKey, Is.EqualTo(HotKeySpec.Default));
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        /// <summary>書き込みの失敗で throw しない（終了処理からも呼ばれる）。</summary>
        [Test]
        public void SurvivesAThrowingWriter()
        {
            var store = new SettingsStore(
                () => null,
                () => null,
                text => throw new InvalidOperationException("boom"),
                _warnings.Add);

            Assert.That(store.Save(MascotSettings.Defaults.WithMuted(true)), Is.False);
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        /// <summary>同じ問題で毎秒警告しない（<c>Refresh</c> はポーリングから呼ばれる）。</summary>
        [Test]
        public void WarnsOnlyOnceForTheSameProblem()
        {
            var store = new SettingsStore(
                () => "{ぐちゃぐちゃ",
                () => Guid.NewGuid().ToString(),
                text => { },
                _warnings.Add);

            for (var i = 0; i < 5; i++) store.Refresh();

            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        /// <summary>
        /// ★ 読み直せたら警告の履歴を捨てる。直したあとにまた壊したとき、黙っていては困る。
        /// </summary>
        [Test]
        public void WarnsAgainAfterASuccessfulReload()
        {
            _file = "{ぐちゃぐちゃ";
            _stamp = "1:10";

            var store = Store();
            store.Refresh();
            Assert.That(_warnings, Has.Count.EqualTo(1));

            _file = SettingsJson.Write(MascotSettings.Defaults);
            _stamp = "2:50";
            store.Refresh();

            // ★ 1回目と同じ壊れ方にすること。 別の壊れ方だと warnOnce のキーが変わり、
            //   履歴を捨てていなくても警告が出てしまう（テストが何も検証しなくなる）
            _file = "{ぐちゃぐちゃ";
            _stamp = "3:10";
            store.Refresh();

            Assert.That(_warnings, Has.Count.EqualTo(2));
        }
    }
}
