using ChatterMascot.Net;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class PairingClientTests
    {
        [Test]
        public void ClassifiesASuccessfulClaimAsPaired()
        {
            var r = PairingClient.Classify(200, false, "{\"token\":\"a1_B2-c3\"}");

            Assert.That(r.Kind, Is.EqualTo(PairingKind.Paired));
            Assert.That(r.Token, Is.EqualTo("a1_B2-c3"));
        }

        /// <summary>★ そのままヘッダに載るので、使えない形のトークンは受け取らない</summary>
        [TestCase("{\"token\":\"ab\\r\\nX-Evil: 1\"}")]
        [TestCase("{\"token\":\"\"}")]
        [TestCase("{\"token\":5}")]
        [TestCase("{}")]
        public void RejectsAnUnusableTokenEvenOn200(string body)
        {
            Assert.That(PairingClient.Classify(200, false, body).Kind, Is.EqualTo(PairingKind.BadResponse));
        }

        [Test]
        public void WrongPinCarriesTheRemainingAttempts()
        {
            var r = PairingClient.Classify(403, false, "{\"error\":\"wrong_pin\",\"remaining\":2}");

            Assert.That(r.Kind, Is.EqualTo(PairingKind.WrongPin));
            Assert.That(r.Remaining, Is.EqualTo(2));
        }

        [Test]
        public void WrongPinWithNoAttemptsLeftNeedsANewPin()
        {
            Assert.That(
                PairingClient.Classify(403, false, "{\"error\":\"wrong_pin\",\"remaining\":0}").Kind,
                Is.EqualTo(PairingKind.Reissue));
        }

        [TestCase("expired")]
        [TestCase("locked")]
        [TestCase("no_pairing")]
        public void ClassifiesDeadPinsAsReissue(string error)
        {
            Assert.That(PairingClient.Classify(403, false, "{\"error\":\"" + error + "\"}").Kind, Is.EqualTo(PairingKind.Reissue));
        }

        [TestCase(401)]
        [TestCase(404)]
        public void ClassifiesMissingRoutesAsAnOldServer(int status)
        {
            Assert.That(PairingClient.Classify(status, false, "not found").Kind, Is.EqualTo(PairingKind.OldServer));
        }

        [Test]
        public void ClassifiesTransferFailuresAsUnreachable()
        {
            Assert.That(PairingClient.Classify(0, true, null).Kind, Is.EqualTo(PairingKind.Unreachable));
        }

        [TestCase(200, "<html>")]
        [TestCase(200, "")]
        [TestCase(200, null)]
        [TestCase(400, "{\"error\":\"invalid_body\"}")]
        [TestCase(403, "{\"error\":\"something_new\"}")]
        [TestCase(500, "oops")]
        public void ClassifiesUnreadableOrUnknownResponsesAsBad(int status, string body)
        {
            Assert.That(PairingClient.Classify(status, false, body).Kind, Is.EqualTo(PairingKind.BadResponse));
        }
    }
}
