using System;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// 起動引数の読み方。<c>-serverUrl</c> / <c>-vrm</c> / <c>-buildScene</c> が共有する。
    /// </summary>
    [TestFixture]
    public sealed class CommandLineTests
    {
        [Test]
        public void ReadsTheValueAfterTheName()
        {
            Assert.That(CommandLine.Argument(new[] { "app", "-vrm", "/a.vrm" }, "-vrm"),
                Is.EqualTo("/a.vrm"));
        }

        [Test]
        public void ReturnsNullWhenAbsent()
        {
            Assert.That(CommandLine.Argument(new[] { "app" }, "-vrm"), Is.Null);
        }

        /// <summary>★ 末尾の名前には値が無い。読むと範囲外になる。</summary>
        [Test]
        public void NameAtTheEndHasNoValue()
        {
            Assert.That(CommandLine.Argument(new[] { "app", "-vrm" }, "-vrm"), Is.Null);
        }

        [Test]
        public void FirstOccurrenceWins()
        {
            Assert.That(CommandLine.Argument(new[] { "app", "-vrm", "a", "-vrm", "b" }, "-vrm"),
                Is.EqualTo("a"));
        }

        /// <summary>★ 取れない環境でも起動を止めないこと。</summary>
        [Test]
        public void NullInputsAreTolerated()
        {
            Assert.That(CommandLine.Argument(null, "-vrm"), Is.Null);
            Assert.That(CommandLine.Argument(Array.Empty<string>(), null), Is.Null);
        }

        /// <summary>
        /// ★ <b><c>-flag</c> 単独（末尾）は <c>true</c>。</b> <see cref="CommandLine.Argument"/> は
        /// 「次に来る値」しか返さないので、ここを「指定されなかった」と同じ扱いにすると
        /// <b>いちばん自然な渡し方で黙って無反応</b>になる（実機の切り分けで
        /// 「ログが出ない＝コードが走っていない」と誤読しかねない）。
        /// </summary>
        [Test]
        public void FlagAloneAtTheEndIsTrue()
        {
            Assert.That(CommandLine.Flag(new[] { "app", "-faceLog" }, "-faceLog"), Is.True);
        }

        /// <summary>
        /// ★ 次のトークンが <c>-</c> で始まるなら「値なし」。そうしないと
        /// <c>-faceLog -vrm /path.vrm</c> が <c>-vrm</c> を値として食い、
        /// <b><c>-vrm</c> が解釈されないまま消える</b>。
        /// </summary>
        [Test]
        public void FlagDoesNotSwallowTheNextOption()
        {
            var args = new[] { "app", "-faceLog", "-vrm", "/tmp/a.vrm" };

            Assert.That(CommandLine.Flag(args, "-faceLog"), Is.True);
            Assert.That(CommandLine.Argument(args, "-vrm"), Is.EqualTo("/tmp/a.vrm"));
        }

        [Test]
        public void FlagReadsExplicitTruthyValues()
        {
            foreach (var value in new[] { "1", "true", "True", "yes", "on", "anything" })
            {
                Assert.That(CommandLine.Flag(new[] { "app", "-faceLog", value }, "-faceLog"), Is.True, value);
            }
        }

        [Test]
        public void FlagReadsExplicitFalsyValues()
        {
            foreach (var value in new[] { "0", "false", "FALSE", "no", "off" })
            {
                Assert.That(CommandLine.Flag(new[] { "app", "-faceLog", value }, "-faceLog", defaultValue: true),
                    Is.False, value);
            }
        }

        [Test]
        public void FlagFallsBackToTheDefaultWhenAbsent()
        {
            var args = new[] { "app", "-serverUrl", "ws://x" };

            Assert.That(CommandLine.Flag(args, "-faceLog"), Is.False);
            Assert.That(CommandLine.Flag(args, "-faceLog", defaultValue: true), Is.True);
        }

        [Test]
        public void FlagDoesNotThrowOnNullOrEmptyInput()
        {
            Assert.That(CommandLine.Flag(null, "-faceLog", defaultValue: true), Is.True);
            Assert.That(CommandLine.Flag(new string[0], "-faceLog"), Is.False);
            Assert.That(CommandLine.Flag(new[] { "app" }, null, defaultValue: true), Is.True);
        }
    }
}
