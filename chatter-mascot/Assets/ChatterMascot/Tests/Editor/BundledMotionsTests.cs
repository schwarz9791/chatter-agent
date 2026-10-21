using System;
using System.IO;
using System.Linq;
using ChatterMascot.Vrm;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <see cref="AssetPath.BundledMotions"/> が <c>StreamingAssets/animations/</c> の中身と一致していること。
    ///
    /// ★ 同梱は固定リストで持つ（Android は APK 内を列挙できない）ので、ファイルの足し引きに
    ///   リストが追従しないと<b>追加したモーションが黙って使われない</b>。
    /// </summary>
    [TestFixture]
    public sealed class BundledMotionsTests
    {
        private static string AnimationsRoot =>
            Path.Combine(Application.dataPath, "StreamingAssets", AssetPath.AnimationsDirectory);

        [Test]
        public void ListEqualsTheFilesInTheFolder()
        {
            Assert.That(Directory.Exists(AnimationsRoot), Is.True, $"同梱フォルダが見つかりません: {AnimationsRoot}");

            var onDisk = Directory.GetFiles(AnimationsRoot, "*.vrma", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(AnimationsRoot, f).Replace('\\', '/'))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
            var listed = AssetPath.BundledMotions.OrderBy(f => f, StringComparer.Ordinal).ToList();

            Assert.That(listed, Is.EqualTo(onDisk));
        }

        [Test]
        public void EveryEntryLivesInAKnownCategoryDirectory()
        {
            var known = MotionCategories.All.Select(MotionCategories.DirectoryName).ToList();

            foreach (var relative in AssetPath.BundledMotions)
            {
                var parts = relative.Split('/');
                Assert.That(parts.Length, Is.EqualTo(2), relative);
                Assert.That(known, Does.Contain(parts[0]), relative);
            }
        }
    }
}
