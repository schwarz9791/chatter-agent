using System;
using ChatterMascot.Net;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// 接続エラーのメッセージから 401（トークンが無いか違う）を見分ける判定。
    ///
    /// ★ <c>ClientWebSocket.ConnectAsync</c> は専用の型を持たず、<c>WebSocketException.Message</c>
    ///   の文字列にステータスコードを埋め込む。実行環境によっては外側の例外にそれが出ず、
    ///   <c>InnerException</c> 側に入ることがあるため、判定は <c>DescribeExceptionChain</c> で
    ///   連結した文字列に対して行う。
    /// </summary>
    [TestFixture]
    public sealed class SpeechClientTests
    {
        [Test]
        public void RecognisesA401InTheExceptionMessage()
        {
            Assert.That(SpeechClient.LooksUnauthorized(
                "The server returned status code '401' when status code '101' was expected."), Is.True);
        }

        [Test]
        public void DoesNotMatchUnrelatedMessages()
        {
            Assert.That(SpeechClient.LooksUnauthorized("Unable to connect to the remote server"), Is.False);
        }

        [Test]
        public void DoesNotMatchANullMessage()
        {
            Assert.That(SpeechClient.LooksUnauthorized(null), Is.False);
        }

        [Test]
        public void ChainsMessagesFromInnerExceptions()
        {
            var inner = new Exception("The server returned status code '401' when status code '101' was expected.");
            var outer = new Exception("Unable to connect to the remote server", inner);

            var chain = SpeechClient.DescribeExceptionChain(outer);

            Assert.That(chain, Does.Contain("Unable to connect to the remote server"));
            Assert.That(chain, Does.Contain("401"));
        }

        [Test]
        public void RecognisesA401BuriedInTheInnerException()
        {
            var inner = new Exception("The server returned status code '401' when status code '101' was expected.");
            var outer = new Exception("Unable to connect to the remote server", inner);

            Assert.That(SpeechClient.LooksUnauthorized(SpeechClient.DescribeExceptionChain(outer)), Is.True);
        }

        [Test]
        public void PlainConnectionFailureStaysUnrelatedAfterChaining()
        {
            var e = new Exception("Unable to connect to the remote server");

            Assert.That(SpeechClient.LooksUnauthorized(SpeechClient.DescribeExceptionChain(e)), Is.False);
        }

        [Test]
        public void ExceptionChainOfNullIsEmpty()
        {
            Assert.That(SpeechClient.DescribeExceptionChain(null), Is.EqualTo(string.Empty));
        }

        [Test]
        public void ExceptionChainStopsAtTheDepthLimit()
        {
            Exception deepest = new Exception("innermost");
            for (var i = 0; i < 10; i++)
            {
                deepest = new Exception("level" + i, deepest);
            }

            var chain = SpeechClient.DescribeExceptionChain(deepest);

            Assert.That(chain, Does.Not.Contain("innermost"));
            Assert.That(chain, Does.EndWith("…"));
        }

        [Test]
        public void ExceptionChainIsTruncatedWhenTooLong()
        {
            var e = new Exception(new string('a', 1000));

            var chain = SpeechClient.DescribeExceptionChain(e);

            Assert.That(chain.Length, Is.LessThanOrEqualTo(501));
            Assert.That(chain, Does.EndWith("…"));
        }
    }
}
