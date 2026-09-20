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

        private static XDocument BuildManifest(bool withApplication = true, string cleartext = null)
        {
            var application = new XElement("application");
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
            var document = BuildManifest(cleartext: "true");
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
    }
}
