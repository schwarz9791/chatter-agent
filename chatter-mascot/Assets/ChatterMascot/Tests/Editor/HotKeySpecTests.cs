using ChatterMascot.Ui;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class HotKeySpecTests
    {
        // ---- 重複の判定 ----

        /// <summary>
        /// ★★ <b>表記ゆれを吸収すること。</b> 文字列の比較にすると
        ///   <c>opt+ctrl+m</c> と <c>ctrl+opt+m</c> が別物になり、
        ///   <b>すり抜けた方が「押しても何も起きないショートカット」になる</b>。
        /// </summary>
        [Test]
        public void SeesThroughTheOrderOfModifiers()
        {
            Assert.That(HotKeySpec.SameCombination("ctrl+opt+m", "opt+ctrl+m"), Is.True);
            Assert.That(HotKeySpec.SameCombination("ctrl+opt+m", "CTRL+OPT+M"), Is.True);
        }

        [Test]
        public void TellsDifferentCombinationsApart()
        {
            Assert.That(HotKeySpec.SameCombination("ctrl+opt+m", "ctrl+opt+h"), Is.False);
            Assert.That(HotKeySpec.SameCombination("ctrl+opt+m", "ctrl+shift+m"), Is.False);
        }

        /// <summary>
        /// ★ 読めないもの・空は <c>false</c>。
        ///   「読めない2つは同じ」にすると、**壊れた設定を直そうとした人が弾かれる**。
        /// </summary>
        [Test]
        public void TreatsUnreadableSpecsAsDifferent()
        {
            Assert.That(HotKeySpec.SameCombination("", ""), Is.False);
            Assert.That(HotKeySpec.SameCombination(null, null), Is.False);
            Assert.That(HotKeySpec.SameCombination("ぐちゃぐちゃ", "ぐちゃぐちゃ"), Is.False);
            // ★ 修飾キー無しは IsValid でない（→ TryParse）
            Assert.That(HotKeySpec.SameCombination("m", "m"), Is.False);
        }


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
        public void ParsesTheDefaults()
        {
            var mute = Parse(HotKeySpec.Default);
            Assert.That(mute.Key, Is.EqualTo("m"));
            Assert.That(mute.KeyCode, Is.EqualTo(0x2Eu), "kVK_ANSI_M");

            var hide = Parse(HotKeySpec.DefaultHide);
            Assert.That(hide.Key, Is.EqualTo("h"));
            Assert.That(hide.KeyCode, Is.EqualTo(0x04u), "kVK_ANSI_H");

            Assert.That(mute, Is.Not.EqualTo(hide), "既定どうしがぶつかっていないこと");
        }

        /// <summary>
        /// ★★ <b>既定に <c>⌥</c> 単体と <c>⌘⌥</c> を選ばないこと（実測で潰した）。</b>
        /// <c>⌥M</c> は <c>µ</c> を、<c>⌥H</c> は <c>˙</c> を<b>実際に入力する</b>ので
        /// 登録すると全アプリでその文字が打てなくなり、<c>⌘⌥H</c> / <c>⌘⌥M</c> は
        /// macOS 標準の「ほかを非表示」「すべてをしまう」と衝突する。
        /// <c>RegisterEventHotKey</c> はどちらも「登録できた」と言ってくるので、
        /// <b>ここで固定しておかないと誰も気づけない</b>。
        /// </summary>
        [Test]
        public void TheDefaultsAvoidTextInputAndSystemShortcuts()
        {
            foreach (var text in new[] { HotKeySpec.Default, HotKeySpec.DefaultHide })
            {
                var spec = Parse(text);
                Assert.That(spec.ModifierMask & HotKeySpec.ModifierControl, Is.Not.Zero,
                    $"{text}: ⌃ が要る（⌥ 単体は文字を入力する）");
                Assert.That(spec.ModifierMask & HotKeySpec.ModifierCommand, Is.Zero,
                    $"{text}: ⌘⌥ は macOS 標準（ほかを非表示 / すべてをしまう）と衝突する");
            }
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
    
        // ── 記録方式（#76） ────────────────────────────────────

        /// <summary>
        /// ★★ ネイティブが返すのは<b>数値だけ</b>（keyCode と Carbon の修飾マスク）。
        ///   <c>ctrl+opt+m</c> という語彙はこの変換でしか作らない。
        /// </summary>
        [Test]
        public void BuildsFromRecordedCodes()
        {
            HotKeySpec spec;
            string error;

            // 0x2E = kVK_ANSI_M、0x1000|0x0800 = ctrl+opt
            Assert.That(HotKeySpec.TryFromCode(0x2E, 0x1000 | 0x0800, out spec, out error), Is.True, error);
            Assert.That(spec.Format(), Is.EqualTo("ctrl+opt+m"));
            Assert.That(spec.FormatSymbols(), Is.EqualTo("⌃⌥M"));
        }

        /// <summary>★ 修飾キー無しは受けない（そのキーが全アプリで入力できなくなる）</summary>
        [Test]
        public void RejectsRecordedKeysWithoutModifiers()
        {
            HotKeySpec spec;
            string error;

            Assert.That(HotKeySpec.TryFromCode(0x2E, 0, out spec, out error), Is.False);
            Assert.That(error, Is.Not.Empty);
        }

        /// <summary>
        /// ★ 表に無いキーは受けない。記録できてしまうと <c>settings.json</c> に
        ///   往復できない値が入り、次の起動でショートカットが消える。
        /// </summary>
        [Test]
        public void RejectsRecordedKeysThatCannotRoundTrip()
        {
            HotKeySpec spec;
            string error;

            // 0x21 = kVK_ANSI_LeftBracket（表に載せていない）
            Assert.That(HotKeySpec.TryFromCode(0x21, 0x1000, out spec, out error), Is.False);
            Assert.That(error, Is.Not.Empty);
        }

        /// <summary>★ Caps Lock / Fn / 左右の区別が混ざったまま登録すると、打っても一致しない</summary>
        [Test]
        public void DropsUnknownModifierBits()
        {
            HotKeySpec spec;
            string error;

            Assert.That(HotKeySpec.TryFromCode(0x2E, 0x1000 | 0x0800 | 0x00010000, out spec, out error), Is.True);
            Assert.That(spec.ModifierMask, Is.EqualTo(HotKeySpec.ModifierControl | HotKeySpec.ModifierOption));
        }

        [Test]
        public void ParsesTheRecordedWireFormat()
        {
            HotKeySpec spec;
            string error;

            Assert.That(HotKeySpec.TryParseRecorded("46,6144", out spec, out error), Is.True, error);
            Assert.That(spec.Format(), Is.EqualTo("ctrl+opt+m"));
        }

        [Test]
        public void RejectsAMalformedRecordedWireFormat()
        {
            HotKeySpec spec;
            string error;

            Assert.That(HotKeySpec.TryParseRecorded("", out spec, out error), Is.False);
            Assert.That(HotKeySpec.TryParseRecorded("46", out spec, out error), Is.False);
            Assert.That(HotKeySpec.TryParseRecorded("46,6144,1", out spec, out error), Is.False);
            Assert.That(HotKeySpec.TryParseRecorded("あ,6144", out spec, out error), Is.False);
        }

        /// <summary>★ 記録 → 保存 → 読み直しが往復すること（保存形式は文字列）</summary>
        [Test]
        public void RecordedKeysRoundTripThroughTheSavedFormat()
        {
            HotKeySpec recorded;
            string error;
            Assert.That(HotKeySpec.TryParseRecorded("46,6144", out recorded, out error), Is.True);

            HotKeySpec reparsed;
            Assert.That(HotKeySpec.TryParse(recorded.Format(), out reparsed, out error), Is.True);
            Assert.That(reparsed, Is.EqualTo(recorded));
        }
    }
}
