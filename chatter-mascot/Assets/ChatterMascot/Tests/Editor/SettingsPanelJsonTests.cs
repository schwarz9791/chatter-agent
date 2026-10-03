using ChatterMascot.Settings;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class SettingsPanelJsonTests
    {
        [Test]
        public void ParsesBoolValuesLeniently()
        {
            Assert.That(SettingsPanelJson.ParseBool("true", false), Is.True);
            Assert.That(SettingsPanelJson.ParseBool("1", false), Is.True);
            Assert.That(SettingsPanelJson.ParseBool("off", true), Is.False);
            Assert.That(SettingsPanelJson.ParseBool("なんだこれ", true), Is.True, "読めなければ元の値");
        }

        /// <summary>★ note だけ差し替えた複製が、他のフィールドを落とさないこと</summary>
        [Test]
        public void WithNoteKeepsEverythingElse()
        {
            var source = SettingSpec.Slider("s", "s", 1.5f, 0f, 2f, 0.1f, enabled: false, note: "元");
            var copy = SettingSpec.WithNote(source, "新しい");

            Assert.That(copy.Note, Is.EqualTo("新しい"));
            Assert.That(copy.Key, Is.EqualTo(source.Key));
            Assert.That(copy.Kind, Is.EqualTo(source.Kind));
            Assert.That(copy.Value, Is.EqualTo(source.Value));
            Assert.That(copy.Min, Is.EqualTo(source.Min));
            Assert.That(copy.Max, Is.EqualTo(source.Max));
            Assert.That(copy.Step, Is.EqualTo(source.Step));
            Assert.That(copy.Enabled, Is.EqualTo(source.Enabled));
        }
    }
}
