using System.Linq;
using System.Xml.Linq;
using ChatterMascot.EditorTools;
using NUnit.Framework;
using UnityEditor.Build;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class AndroidManifestPostProcessorTests
    {
        private static readonly XNamespace AndroidNs = "http://schemas.android.com/apk/res/android";

        private const string LauncherClass = "tech.sukima.chattermascot.ChatterMascotGameActivity";

        private static XDocument BuildManifest(
            bool withApplication = true, string cleartext = null, bool withActivity = true,
            string activityName = "com.unity3d.player.UnityPlayerGameActivity")
        {
            var application = new XElement("application");
            if (withActivity)
            {
                application.Add(new XElement(
                    "activity",
                    new XAttribute(AndroidNs + "name", activityName),
                    new XAttribute(AndroidNs + "exported", "true"),
                    new XElement("intent-filter"),
                    new XElement(
                        "meta-data",
                        new XAttribute(AndroidNs + "name", "unityplayer.UnityActivity"),
                        new XAttribute(AndroidNs + "value", "true"))));
            }

            if (cleartext != null)
            {
                application.SetAttributeValue(AndroidNs + "usesCleartextTraffic", cleartext);
            }

            var manifest = new XElement("manifest");
            if (withApplication) manifest.Add(application);

            return new XDocument(manifest);
        }

        [Test]
        public void AddsInternetAndCleartextToABareManifest()
        {
            var document = BuildManifest();

            var changed = AndroidManifestPostProcessor.Apply(document);

            Assert.That(changed, Is.True);
            var manifest = document.Root;
            Assert.That(
                manifest.Elements("uses-permission")
                    .Any(e => e.Attribute(AndroidNs + "name")?.Value == "android.permission.INTERNET"),
                Is.True);
            var application = manifest.Element("application");
            Assert.That(application.Attribute(AndroidNs + "usesCleartextTraffic")?.Value, Is.EqualTo("true"));
        }

        [Test]
        public void AddsHandTrackingToABareManifest()
        {
            var document = BuildManifest();

            AndroidManifestPostProcessor.Apply(document);

            Assert.That(
                document.Root.Elements("uses-permission")
                    .Any(e => e.Attribute(AndroidNs + "name")?.Value == "android.permission.HAND_TRACKING"),
                Is.True);
        }

        [Test]
        public void SecondApplyIsANoOp()
        {
            var document = BuildManifest();
            AndroidManifestPostProcessor.Apply(document);

            var changed = AndroidManifestPostProcessor.Apply(document);

            Assert.That(changed, Is.False);
        }

        [Test]
        public void FlipsUsesCleartextTrafficFromFalseToTrue()
        {
            var document = BuildManifest(cleartext: "false");

            var changed = AndroidManifestPostProcessor.Apply(document);

            Assert.That(changed, Is.True);
            var application = document.Root.Element("application");
            Assert.That(application.Attribute(AndroidNs + "usesCleartextTraffic")?.Value, Is.EqualTo("true"));
        }

        [Test]
        public void DoesNotDuplicateExistingPermissions()
        {
            var document = BuildManifest(cleartext: "true", activityName: LauncherClass);
            document.Root.Add(new XElement(
                "uses-permission", new XAttribute(AndroidNs + "name", "android.permission.INTERNET")));
            document.Root.Add(new XElement(
                "uses-permission", new XAttribute(AndroidNs + "name", "android.permission.HAND_TRACKING")));

            var changed = AndroidManifestPostProcessor.Apply(document);

            Assert.That(changed, Is.False);
            Assert.That(document.Root.Elements("uses-permission").Count(), Is.EqualTo(2));
        }

        [Test]
        public void ThrowsWhenApplicationElementIsMissing()
        {
            var document = BuildManifest(withApplication: false);

            Assert.Throws<BuildFailedException>(() => AndroidManifestPostProcessor.Apply(document));
        }

        [Test]
        public void ThrowsWhenRootElementIsNotManifest()
        {
            var document = new XDocument(new XElement("not-manifest"));

            Assert.Throws<BuildFailedException>(() => AndroidManifestPostProcessor.Apply(document));
        }

        [Test]
        public void RenamesTheUnityActivity()
        {
            var document = BuildManifest();

            var changed = AndroidManifestPostProcessor.Apply(document);

            Assert.That(changed, Is.True);
            var activity = document.Root.Element("application").Element("activity");
            Assert.That(activity.Attribute(AndroidNs + "name")?.Value, Is.EqualTo(LauncherClass));
        }

        [Test]
        public void KeepsOtherActivityAttributesAndChildrenWhenRenaming()
        {
            var document = BuildManifest();

            AndroidManifestPostProcessor.Apply(document);

            var activity = document.Root.Element("application").Element("activity");
            Assert.That(activity.Attribute(AndroidNs + "exported")?.Value, Is.EqualTo("true"));
            Assert.That(activity.Element("intent-filter"), Is.Not.Null);
            Assert.That(activity.Element("meta-data"), Is.Not.Null);
        }

        [Test]
        public void AlreadyRenamedActivityIsLeftAlone()
        {
            var document = BuildManifest(cleartext: "true", activityName: LauncherClass);
            document.Root.Add(new XElement(
                "uses-permission", new XAttribute(AndroidNs + "name", "android.permission.INTERNET")));
            document.Root.Add(new XElement(
                "uses-permission", new XAttribute(AndroidNs + "name", "android.permission.HAND_TRACKING")));

            var changed = AndroidManifestPostProcessor.Apply(document);

            Assert.That(changed, Is.False);
        }

        [Test]
        public void ThrowsWhenUnityActivityIsMissing()
        {
            var document = BuildManifest(withActivity: false);

            Assert.Throws<BuildFailedException>(() => AndroidManifestPostProcessor.Apply(document));
        }
    }
}
