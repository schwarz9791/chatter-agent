using ChatterMascot.Ui;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// 繋がらないときの案内の選び方（<see cref="UnreachableNotices.Decide"/>）。
    ///
    /// ★ トークンがある、または一度繋がっていれば、ペアリングではなく再起動を案内する。
    /// </summary>
    [TestFixture]
    public sealed class UnreachableNoticeTests
    {
        [TestCase(false, false, false, UnreachableNotice.PairingToast)]
        [TestCase(false, false, true, UnreachableNotice.PairingPrompt)]
        [TestCase(false, true, false, UnreachableNotice.ServerDown)]
        [TestCase(false, true, true, UnreachableNotice.ServerDown)]
        [TestCase(true, false, false, UnreachableNotice.ServerDown)]
        [TestCase(true, false, true, UnreachableNotice.ServerDown)]
        [TestCase(true, true, false, UnreachableNotice.ServerDown)]
        [TestCase(true, true, true, UnreachableNotice.ServerDown)]
        public void DecidePicksNotice(bool hasToken, bool connectedOnce, bool canPrompt, UnreachableNotice expected)
        {
            Assert.AreEqual(expected, UnreachableNotices.Decide(hasToken, connectedOnce, canPrompt));
        }
    }
}
