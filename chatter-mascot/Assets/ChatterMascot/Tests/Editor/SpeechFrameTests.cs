using ChatterMascot.Protocol;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class SpeechFrameTests
    {
        private const string Epoch = "test-epoch";
        private static string Path1 => "/audio/" + Epoch + "-000000000001.wav";

        /// <summary>フィールドを差し替えられる素の JSON。</summary>
        private static string Frame(string overrides = null)
        {
            var body =
                "\"epoch\":\"" + Epoch + "\"," +
                "\"seq\":1," +
                "\"ts\":\"2026-08-15T00:00:00.000Z\"," +
                "\"source\":\"claude-code\"," +
                "\"sessionId\":\"s\",\"turnId\":\"t\",\"messageId\":\"m\"," +
                "\"kind\":\"assistant\",\"text\":\"あ。\",\"emotion\":\"happy\"," +
                "\"audio\":{\"path\":\"" + Path1 + "\",\"format\":\"wav\"}";
            return "{" + (overrides == null ? body : overrides) + "}";
        }

        private static SpeechFrame Parse(string json)
        {
            SpeechFrame frame;
            bool declared;
            return SpeechFrameParser.TryParse(json, out frame, out declared) ? frame : null;
        }

        [Test]
        public void ParsesNormalFrame()
        {
            var frame = Parse(Frame());
            Assert.That(frame, Is.Not.Null);
            Assert.That(frame.Epoch, Is.EqualTo(Epoch));
            Assert.That(frame.Seq, Is.EqualTo(1L));
            Assert.That(frame.Text, Is.EqualTo("あ。"));
            Assert.That(frame.Kind, Is.EqualTo(SpeechKind.Assistant));
            Assert.That(frame.Emotion, Is.EqualTo(Emotion.Happy));
            Assert.That(frame.Audio, Is.Not.Null);
            Assert.That(frame.Audio.Path, Is.EqualTo(Path1));
        }

        /// <summary>
        /// ★ ISO8601 の <c>ts</c> を <c>DateTime</c> に変換させない。
        ///
        /// Newtonsoft は既定で「ISO8601 らしき文字列」を自動変換する。そのままだと
        /// <c>ts</c> が <c>JTokenType.String</c> ではなく <c>Date</c> になり、
        /// <b>正常なフレームが1つも通らなくなる</b>（症状は完全な無音で、
        /// 「読めないフレームを捨てました」が接続ごとに1回出るだけ）。
        /// </summary>
        [Test]
        public void IsoTimestampStaysString()
        {
            foreach (var ts in new[]
            {
                "2026-08-15T00:00:00.000Z",
                "2026-08-15T00:00:00Z",
                "2026-08-15T09:00:00+09:00",
                "2026-08-15 00:00:00",
            })
            {
                var frame = Parse(Replace("\"ts\":\"2026-08-15T00:00:00.000Z\"", "\"ts\":\"" + ts + "\""));
                Assert.That(frame, Is.Not.Null, ts);
                // 受け取った文字列がそのまま残ること（変換されて別表記になっていない）
                Assert.That(frame.Ts, Is.EqualTo(ts), ts);
            }
        }

        [Test]
        public void RejectsNonJson()
        {
            Assert.That(Parse("これは JSON ではない"), Is.Null);
            Assert.That(Parse(""), Is.Null);
        }

        [Test]
        public void RejectsNonObject()
        {
            Assert.That(Parse("[1,2,3]"), Is.Null);
            Assert.That(Parse("\"文字列\""), Is.Null);
            Assert.That(Parse("42"), Is.Null);
        }

        /// <summary>
        /// ★ seq は 1 始まりの安全整数だけを通す（Map のキー・ack の値）。
        /// 0 は「未受信」の初期値と衝突して余計な resetEpoch を起こすうえ、
        /// サーバーの ack が 0 を no-op として扱うのでそのキューは永久に消えない。
        /// </summary>
        [Test]
        public void OnlyPositiveSafeIntegerSeq()
        {
            foreach (var seq in new[] { "0", "-1", "1.5", "\"1\"", "9007199254740992", "null" })
            {
                Assert.That(Parse(Replace("\"seq\":1", "\"seq\":" + seq)), Is.Null, "seq=" + seq);
            }
            // 欠落も通さない
            Assert.That(Parse(Remove("\"seq\":1,")), Is.Null);
        }

        [Test]
        public void RejectsNonStringText()
        {
            Assert.That(Parse(Replace("\"text\":\"あ。\"", "\"text\":1")), Is.Null);
            Assert.That(Parse(Remove("\"text\":\"あ。\",")), Is.Null);
        }

        /// <summary>★ ts は必須（重複排除と古さの判定に使う）</summary>
        [Test]
        public void TsIsRequired()
        {
            Assert.That(Parse(Replace("\"ts\":\"2026-08-15T00:00:00.000Z\"", "\"ts\":\"\"")), Is.Null);
            Assert.That(Parse(Remove("\"ts\":\"2026-08-15T00:00:00.000Z\",")), Is.Null);
        }

        /// <summary>空文字の text は通す（鳴らすかどうかはサーバーが audio: null で表す）</summary>
        [Test]
        public void EmptyTextIsAccepted()
        {
            var frame = Parse(Replace("\"text\":\"あ。\"", "\"text\":\"\""));
            Assert.That(frame, Is.Not.Null);
            Assert.That(frame.Text, Is.EqualTo(""));
        }

        /// <summary>未知の kind は assistant として扱う（docs/protocol.md の要求）</summary>
        [Test]
        public void UnknownKindFallsBackToAssistant()
        {
            Assert.That(Parse(Replace("\"kind\":\"assistant\"", "\"kind\":\"future\"")).Kind,
                Is.EqualTo(SpeechKind.Assistant));
            Assert.That(Parse(Replace("\"kind\":\"assistant\"", "\"kind\":\"prompt\"")).Kind,
                Is.EqualTo(SpeechKind.Prompt));
            Assert.That(Parse(Remove("\"kind\":\"assistant\",")).Kind, Is.EqualTo(SpeechKind.Assistant));
        }

        /// <summary>未知の emotion は neutral に丸める</summary>
        [Test]
        public void UnknownEmotionFallsBackToNeutral()
        {
            Assert.That(Parse(Replace("\"emotion\":\"happy\"", "\"emotion\":\"excited\"")).Emotion,
                Is.EqualTo(Emotion.Neutral));
        }

        /// <summary>識別子が欠けていても null で埋めて読む</summary>
        [Test]
        public void MissingIdentifiersBecomeNull()
        {
            var frame = Parse(Remove("\"sessionId\":\"s\",\"turnId\":\"t\",\"messageId\":\"m\","));
            Assert.That(frame, Is.Not.Null);
            Assert.That(frame.SessionId, Is.Null);
            Assert.That(frame.TurnId, Is.Null);
            Assert.That(frame.MessageId, Is.Null);
        }

        /// <summary>
        /// ★ 絶対 URL を通さない（サーバーがクライアントを任意の外部ホストへ向かわせられる）
        /// </summary>
        [Test]
        public void RejectsAbsoluteAndTraversalAudioPaths()
        {
            foreach (var path in new[]
            {
                "http://evil.example.com/audio/test-epoch-000000000001.wav",
                "//evil.example.com/audio/test-epoch-000000000001.wav",
                "/audio/../../etc/passwd",
                "/etc/passwd",
            })
            {
                var frame = Parse(Replace(Path1, path));
                Assert.That(frame, Is.Not.Null, path);
                Assert.That(frame.Audio, Is.Null, path);
            }
        }

        /// <summary>読めない audio は null に倒す（フレームごと捨てない）</summary>
        [Test]
        public void UnreadableAudioBecomesNullWithoutDroppingFrame()
        {
            foreach (var audio in new[]
            {
                "null", "\"wav\"", "1", "{}",
                "{\"path\":\"" + Path1 + "\"}",
                "{\"format\":\"wav\"}",
            })
            {
                var frame = Parse(Replace("{\"path\":\"" + Path1 + "\",\"format\":\"wav\"}", audio));
                Assert.That(frame, Is.Not.Null, audio);
                Assert.That(frame.Audio, Is.Null, audio);
                Assert.That(frame.Seq, Is.EqualTo(1L), audio);
            }
        }

        /// <summary>知らない format は通さない（「音声なし」として扱う）</summary>
        [Test]
        public void UnknownFormatIsRejected()
        {
            var frame = Parse(Replace("\"format\":\"wav\"", "\"format\":\"opus\""));
            Assert.That(frame, Is.Not.Null);
            Assert.That(frame.Audio, Is.Null);
        }

        /// <summary>
        /// ★ audio キーが無いフレームを見分ける（#29 より前のサーバー。全文が無言で ack される）。
        /// ★ 明示的な audio: null では発火しない（ttsEnabled: false は正常な設定）。
        /// </summary>
        [Test]
        public void DistinguishesMissingAudioKeyFromExplicitNull()
        {
            bool declared;
            SpeechFrame frame;

            SpeechFrameParser.TryParse(Remove(",\"audio\":{\"path\":\"" + Path1 + "\",\"format\":\"wav\"}"),
                out frame, out declared);
            Assert.That(declared, Is.False, "キーが無いのに declared が true");

            SpeechFrameParser.TryParse(Replace("{\"path\":\"" + Path1 + "\",\"format\":\"wav\"}", "null"),
                out frame, out declared);
            Assert.That(declared, Is.True, "明示的な null で発火してはいけない");

            SpeechFrameParser.TryParse(Frame(), out frame, out declared);
            Assert.That(declared, Is.True);
        }

        /// <summary>
        /// ★ 形が違う epoch は通さない（音声 URL の材料になる）
        /// </summary>
        [Test]
        public void RejectsMalformedEpoch()
        {
            foreach (var epoch in new[]
            {
                "\"\"", "123", "null", "{}",
                "\"../../etc/passwd\"", "\"-leading-hyphen\"",
                "\"" + new string('a', 65) + "\"",
            })
            {
                Assert.That(Parse(Replace("\"epoch\":\"" + Epoch + "\"", "\"epoch\":" + epoch)), Is.Null, epoch);
            }
            // 欠落も通さない
            Assert.That(Parse(Remove("\"epoch\":\"" + Epoch + "\",")), Is.Null);
        }

        /// <summary>charset に収まる epoch は通す</summary>
        [Test]
        public void AcceptsValidEpochCharset()
        {
            foreach (var epoch in new[]
            {
                "legacy", "1f0a9c3e-5b62-4f1d-9a77-0e2c8d4b6a31", "a", "A.b_c-1", new string('a', 64),
            })
            {
                var frame = Parse(Replace("\"epoch\":\"" + Epoch + "\"", "\"epoch\":\"" + epoch + "\""));
                Assert.That(frame, Is.Not.Null, epoch);
                Assert.That(frame.Epoch, Is.EqualTo(epoch), epoch);
            }
        }

        /// <summary>
        /// ★ <c>long</c> に収まらない <c>seq</c> で<b>例外を投げないこと</b>。
        ///
        /// Newtonsoft は <c>long</c> を超える整数を <c>BigInteger</c> で持つが、
        /// <c>JTokenType</c> は <c>Integer</c> のままなので型検査を通り、
        /// <c>Value&lt;long&gt;()</c> が <c>OverflowException</c> を投げる。
        /// パースの外へ出ると <c>SpeechClient</c> の受信ループごと落ち、繋ぎ直した先で
        /// サーバーが<b>同じ未 ack のフレームを再送する</b>ので、また落ちるループになる。
        ///
        /// ★ <c>OnlyPositiveSafeIntegerSeq</c> の <c>9007199254740992</c> は
        ///   <c>long</c> に収まるので、このケースを踏めていない。
        /// </summary>
        [Test]
        public void HugeSeqIsRejectedWithoutThrowing()
        {
            foreach (var seq in new[] { "99999999999999999999", "-99999999999999999999" })
            {
                Assert.That(Parse(Replace("\"seq\":1", "\"seq\":" + seq)), Is.Null, "seq=" + seq);
            }
        }

        /// <summary>
        /// ★ .NET の <c>$</c> は<b>末尾の改行の手前にもマッチする</b>。
        ///
        /// 写し元の JS（<c>core/src/core/types.ts</c> / <c>audioPath.ts</c>）にその挙動は無いので、
        /// <c>^…$</c> のまま移植すると <c>gen-1\n</c> や
        /// <c>/audio/gen-1-000000000001.wav\n</c> が通ってしまう。
        /// 通った値は <c>BaseUrl</c> と連結されてそのまま URL になる。
        /// </summary>
        [Test]
        public void TrailingNewlineIsRejected()
        {
            Assert.That(Parse(Replace("\"epoch\":\"" + Epoch + "\"", "\"epoch\":\"" + Epoch + "\\n\"")), Is.Null);

            // audio は読めなくてもフレームごとは捨てない（Audio が null になる）
            var frame = Parse(Replace(Path1, Path1 + "\\n"));
            Assert.That(frame, Is.Not.Null);
            Assert.That(frame.Audio, Is.Null);
        }

        /// <summary>
        /// ★ .NET の <c>\d</c> は <b>Unicode の十進数字</b>にマッチする（JS の <c>\d</c> は ASCII のみ）。
        ///
        /// <c>[0-9]</c> に直さないと、アラビア・インド数字12桁の <c>seq</c> が
        /// <c>AudioPath</c> を通り抜けて URL に入る。
        /// </summary>
        [Test]
        public void NonAsciiDigitsInAudioPathAreRejected()
        {
            var arabicIndic = "\u0660\u0661\u0662\u0663\u0664\u0665\u0666\u0667\u0668\u0669\u0660\u0661";
            var path = "/audio/" + Epoch + "-" + arabicIndic + ".wav";

            var frame = Parse(Replace(Path1, path));
            Assert.That(frame, Is.Not.Null);
            Assert.That(frame.Audio, Is.Null);
        }

        private static string Replace(string from, string to)
        {
            return Frame().Replace(from, to);
        }

        private static string Remove(string fragment)
        {
            return Frame().Replace(fragment, "");
        }
    }
}
