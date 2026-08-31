using ChatterMascot.Ui;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class HotKeySpecTests
    {
        private static HotKeySpec Parse(string text)
        {
            HotKeySpec spec;
            string error;
            Assert.That(HotKeySpec.TryParse(text, out spec, out error), Is.True, error);
            return spec;
        }

        private static string Reject(string text)
        {
            HotKeySpec spec;
            string error;
            Assert.That(HotKeySpec.TryParse(text, out spec, out error), Is.False, $"\"{text}\" を通してはいけない");
            Assert.That(spec.IsValid, Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty, "理由を返すこと（ユーザーに見せる）");
            return error;
        }

        [Test]
        public void ParsesTheDefault()
        {
            var spec = Parse(HotKeySpec.Default);
            Assert.That(spec.Key, Is.EqualTo("m"));
            Assert.That(spec.KeyCode, Is.EqualTo(0x2Eu), "kVK_ANSI_M");
            Assert.That(spec.ModifierMask, Is.EqualTo(HotKeySpec.ModifierOption));
        }

        /// <summary>
        /// ★★ <b>修飾キー無しを拒否すること。</b> 単独のキーを登録すると、
        /// そのキーが<b>どのアプリでも入力できなくなる</b>。
        /// </summary>
        [Test]
        public void RejectsABareKey()
        {
            Reject("m");
            Reject("space");
        }

        [Test]
        public void RejectsEmptyAndMalformedInput()
        {
            Reject(null);
            Reject("");
            Reject("opt+");
            Reject("+m");
            Reject("opt");
            Reject("opt+m+n");
            Reject("opt+ほげ");
        }

        /// <summary>設定は人が手で書く。綴りを1つに強制しない。</summary>
        [Test]
        public void AcceptsAliasesForModifiers()
        {
            var expected = Parse("opt+m");
            Assert.That(Parse("option+m"), Is.EqualTo(expected));
            Assert.That(Parse("alt+m"), Is.EqualTo(expected));
            Assert.That(Parse("ALT+M"), Is.EqualTo(expected), "大文字小文字を問わない");
            Assert.That(Parse(" opt + m "), Is.EqualTo(expected), "空白を許す");
        }

        [Test]
        public void CombinesModifiers()
        {
            var spec = Parse("cmd+shift+ctrl+opt+m");
            Assert.That(spec.ModifierMask, Is.EqualTo(
                HotKeySpec.ModifierCommand | HotKeySpec.ModifierShift |
                HotKeySpec.ModifierControl | HotKeySpec.ModifierOption));
        }

        /// <summary>順番が違っても同じもの（ユーザーがどう書いても同じ登録になる）。</summary>
        [Test]
        public void IgnoresTheOrderOfModifiers()
        {
            Assert.That(Parse("shift+cmd+m"), Is.EqualTo(Parse("cmd+shift+m")));
        }

        [Test]
        public void RoundTripsThroughFormat()
        {
            foreach (var text in new[] { "opt+m", "ctrl+opt+shift+cmd+m", "cmd+space", "ctrl+f5" })
            {
                Assert.That(Parse(text).Format(), Is.EqualTo(Parse(Parse(text).Format()).Format()));
            }
            Assert.That(Parse("cmd+shift+m").Format(), Is.EqualTo("shift+cmd+m"), "並びは ⌃⌥⇧⌘ の順");
        }

        /// <summary>メニューのラベルに出す表記（→ <c>MascotMenu</c>）。</summary>
        [Test]
        public void FormatsSymbolsForTheMenu()
        {
            Assert.That(Parse("opt+m").FormatSymbols(), Is.EqualTo("⌥M"));
            Assert.That(Parse("ctrl+opt+shift+cmd+m").FormatSymbols(), Is.EqualTo("⌃⌥⇧⌘M"));
        }

        /// <summary>既定コンストラクタの値は登録できない（＝メニューにも表記を出さない）。</summary>
        [Test]
        public void TheDefaultStructIsNotValid()
        {
            var spec = default(HotKeySpec);
            Assert.That(spec.IsValid, Is.False);
            Assert.That(spec.Format(), Is.Empty);
            Assert.That(spec.FormatSymbols(), Is.Empty);
        }
    }
}
