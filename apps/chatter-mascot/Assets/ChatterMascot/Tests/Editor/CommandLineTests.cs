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
    }
}
