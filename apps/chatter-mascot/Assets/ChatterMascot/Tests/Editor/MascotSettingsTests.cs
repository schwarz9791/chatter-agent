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
            Assert.That(defaults.CharacterScale, Is.EqualTo(1f));
            Assert.That(defaults.Volume, Is.EqualTo(1f));
            Assert.That(defaults.IdleMotion, Is.True);
            Assert.That(defaults.CursorGaze, Is.True);
            Assert.That(defaults.Blink, Is.True);
            Assert.That(defaults.VrmFileName, Is.Empty);
        }

        [Test]
        public void WithMutedKeepsTheOtherValues()
        {
            var settings = MascotSettings.Defaults.WithVolume(0.3f).WithMuted(true);

            Assert.That(settings.Muted, Is.True);
            Assert.That(settings.Volume, Is.EqualTo(0.3f));
            Assert.That(settings.MuteHotKey, Is.EqualTo(HotKeySpec.Default));
        }

        [Test]
        public void EqualValuesAreEqual()
        {
            var a = MascotSettings.Defaults.WithMuted(true).WithVolume(1.5f);
            var b = MascotSettings.Defaults.WithMuted(true).WithVolume(1.5f);

            Assert.That(a.Equals(b), Is.True);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
        }

        /// <summary>
        /// ★★ <b>プロパティを1つずつ変えて、すべてが <see cref="MascotSettings.Equals"/> に
        /// 効いていることを確かめる。</b>
        ///
        /// ★ <b>手で書き並べないのが要点。</b> それだと「プロパティを足したのに
        ///   テストも足し忘れる」で同じ穴が開く。リフレクションで回せば、
        ///   <b>新しいプロパティは書いた瞬間からこのテストの対象になる</b>。
        ///
        /// ★ 実際に一度落とした —— <c>HideHotKey</c> を <c>Equals</c> に入れ忘れたせいで
        ///   <c>SettingsStore.Refresh</c> が「変わっていない」と返し、次の保存で
        ///   <b>ユーザーの編集がディスクから消えた</b>。#76 で項目が3→9に増えた保険。
        ///
        /// ★★ <b><c>With&lt;プロパティ名&gt;</c> の存在もここで強制している。</b>
        ///   <c>Copy</c> に足し忘れると「変えたつもりが変わらない」で落ちる ——
        ///   <c>Equals</c> への足し忘れと同じ1つのテストで両方捕まる。
        /// </summary>
        [Test]
        public void EqualsAndWithCoverEveryProperty()
        {
            var properties = typeof(MascotSettings).GetProperties(
                BindingFlags.Public | BindingFlags.Instance);

            // Defaults / その他の static は上の BindingFlags で既に落ちている
            Assert.That(properties, Is.Not.Empty, "プロパティを1つも拾えていない");

            var covered = new List<string>();
            foreach (var property in properties)
            {
                var baseline = MascotSettings.Defaults;
                var changed = Mutate(baseline, property);

                Assert.That(
                    baseline.Equals(changed), Is.False,
                    $"{property.Name} を変えても Equals が true のまま。" +
                    "MascotSettings.Equals にこのプロパティを足すこと" +
                    "（足さないと設定の変更が拾われず、次の保存でユーザーの編集が消える）");

                // ★ そのプロパティ**だけ**が変わっていること（Copy の引数の取り違えを捕まえる）
                foreach (var other in properties)
                {
                    if (other.Name == property.Name) continue;
                    Assert.That(
                        other.GetValue(changed), Is.EqualTo(other.GetValue(baseline)),
                        $"With{property.Name} が {other.Name} まで変えている。" +
                        "MascotSettings.Copy の引数の対応を確かめること");
                }

                covered.Add(property.Name);
            }

            // 何を見たのかを失敗時に読めるようにしておく
            Assert.That(covered, Has.Count.EqualTo(properties.Length));
        }

        /// <summary>
        /// そのプロパティ<b>だけ</b>が違う値を作る。
        ///
        /// ★ <c>With&lt;プロパティ名&gt;</c> という命名規約に乗せてある。手書きの switch を
        ///   置くと、プロパティを足したときにそこも直す必要が出て、
        ///   「足し忘れを捕まえる」というこのテストの目的が薄れる。
        /// </summary>
        private static MascotSettings Mutate(MascotSettings baseline, PropertyInfo property)
        {
            var with = typeof(MascotSettings).GetMethod(
                "With" + property.Name, BindingFlags.Public | BindingFlags.Instance);

            Assert.That(
                with, Is.Not.Null,
                $"With{property.Name} がありません。MascotSettings に足すこと" +
                "（Copy を経由する形にしておくと、全フィールドを列挙する場所が1つで済む）");

            var next = DifferentValue(property.GetValue(baseline), property.PropertyType);
            return (MascotSettings)with.Invoke(baseline, new[] { next });
        }

        /// <summary>いまの値と必ず違う値。★ 型が増えたらここに足す（落として教える）</summary>
        private static object DifferentValue(object current, Type type)
        {
            if (type == typeof(bool)) return !(bool)current;
            if (type == typeof(float)) return (float)current + 0.5f;
            if (type == typeof(string)) return (string)current + "-changed";

            Assert.Fail($"知らない型です: {type.Name}。DifferentValue に「違う値」の作り方を足すこと");
            return null;
        }
    }
}
