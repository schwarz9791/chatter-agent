using System;
using System.Collections.Generic;
using System.Reflection;
using ChatterMascot.Settings;
using ChatterMascot.Ui;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class MascotSettingsTests
    {
        [Test]
        public void DefaultsUseTheSpecDefaults()
        {
            var defaults = MascotSettings.Defaults;

            Assert.That(defaults.Muted, Is.False);
            Assert.That(defaults.MuteHotKey, Is.EqualTo(HotKeySpec.Default));
            Assert.That(defaults.HideHotKey, Is.EqualTo(HotKeySpec.DefaultHide));
        }

        [Test]
        public void WithMutedKeepsTheOtherValues()
        {
            var settings = new MascotSettings(false, "ctrl+opt+m", "ctrl+opt+j").WithMuted(true);

            Assert.That(settings.Muted, Is.True);
            Assert.That(settings.MuteHotKey, Is.EqualTo("ctrl+opt+m"));
            Assert.That(settings.HideHotKey, Is.EqualTo("ctrl+opt+j"));
        }

        [Test]
        public void EqualValuesAreEqual()
        {
            var a = new MascotSettings(true, "ctrl+opt+m", "ctrl+opt+h");
            var b = new MascotSettings(true, "ctrl+opt+m", "ctrl+opt+h");

            Assert.That(a.Equals(b), Is.True);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
        }

        /// <summary>
        /// ★★ <b>プロパティを1つずつ変えて、すべてが <see cref="MascotSettings.Equals"/> に
        /// 効いていることを確かめる。</b>
        ///
        /// ★ <b>手で3つ書き並べないのが要点。</b> それだと「プロパティを足したのに
        ///   テストも足し忘れる」で同じ穴が開く。リフレクションで回せば、
        ///   <b>新しいプロパティは書いた瞬間からこのテストの対象になる</b>。
        ///
        /// ★ 実際に一度落とした —— <c>HideHotKey</c> を <c>Equals</c> に入れ忘れたせいで
        ///   <c>SettingsStore.Refresh</c> が「変わっていない」と返し、次の保存で
        ///   <b>ユーザーの編集がディスクから消えた</b>。#76 で項目が増えるときの保険。
        /// </summary>
        [Test]
        public void EqualsCoversEveryProperty()
        {
            var properties = typeof(MascotSettings).GetProperties(
                BindingFlags.Public | BindingFlags.Instance);

            // Defaults / その他の static は上の BindingFlags で既に落ちている
            Assert.That(properties, Is.Not.Empty, "プロパティを1つも拾えていない");

            var covered = new List<string>();
            foreach (var property in properties)
            {
                var baseline = new MascotSettings(false, "ctrl+opt+m", "ctrl+opt+h");
                var changed = Mutate(baseline, property);

                Assert.That(
                    baseline.Equals(changed), Is.False,
                    $"{property.Name} を変えても Equals が true のまま。" +
                    "MascotSettings.Equals にこのプロパティを足すこと" +
                    "（足さないと設定の変更が拾われず、次の保存でユーザーの編集が消える）");

                covered.Add(property.Name);
            }

            // 何を見たのかを失敗時に読めるようにしておく
            Assert.That(covered, Has.Count.EqualTo(properties.Length));
        }

        /// <summary>そのプロパティ<b>だけ</b>が違う値を作る。型が増えたらここに足す。</summary>
        private static MascotSettings Mutate(MascotSettings baseline, PropertyInfo property)
        {
            var muted = baseline.Muted;
            var muteHotKey = baseline.MuteHotKey;
            var hideHotKey = baseline.HideHotKey;

            switch (property.Name)
            {
                case nameof(MascotSettings.Muted):
                    muted = !muted;
                    break;
                case nameof(MascotSettings.MuteHotKey):
                    muteHotKey = "ctrl+opt+n";
                    break;
                case nameof(MascotSettings.HideHotKey):
                    hideHotKey = "ctrl+opt+j";
                    break;
                default:
                    // ★ 新しいプロパティを足したらここも足す。落として教える
                    Assert.Fail(
                        $"知らないプロパティです: {property.Name}。" +
                        "Mutate に「そのプロパティだけ違う値」の作り方を足すこと");
                    break;
            }
            return new MascotSettings(muted, muteHotKey, hideHotKey);
        }
    }
}
