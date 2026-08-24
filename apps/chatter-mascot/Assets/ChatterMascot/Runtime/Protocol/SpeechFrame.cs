using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChatterMascot.Protocol
{
    /// <summary>
    /// 発話の種別。受信側は<b>未知の kind を <c>Assistant</c> として扱う</b>（docs/protocol.md）。
    /// </summary>
    public enum SpeechKind
    {
        Assistant,
        /// <summary>応答待ち通知（質問・計画承認・許可プロンプト）</summary>
        Prompt,
    }

    /// <summary>VRM の標準 expression 名と一対一。未知なら中立に倒す。</summary>
    public enum Emotion
    {
        Neutral,
        Happy,
        Angry,
        Sad,
        Relaxed,
        Surprised,
    }

    /// <summary>
    /// 音声の参照。<c>null</c> なら「音声は用意されない」（<c>ttsEnabled: false</c>、
    /// または読み上げる中身が無い文）。<b>何も鳴らさずに ack する。</b>
    /// </summary>
    public sealed class AudioRef
    {
        /// <summary>
        /// サーバー上の<b>相対</b>パス（<c>/audio/&lt;epoch&gt;-&lt;seq&gt;.wav</c>）。
        ///
        /// ★ サーバーは自分がどのアドレスで到達されたかを知らない（既定の bind は
        ///   <c>0.0.0.0</c> で、これは接続先ではない）。authority を補うのはクライアントの責務。
        /// </summary>
        public readonly string Path;

        public AudioRef(string path)
        {
            Path = path;
        }
    }

    /// <summary>
    /// サーバーから届く1フレーム。<c>SpeechRecord</c> に <c>audio</c> を足したもの。
    /// </summary>
    public sealed class SpeechFrame
    {
        /// <summary>採番の世代。<b><c>Seq</c> はこの中でしか一意でない</b>（→ <see cref="SpeechEpoch"/>）。</summary>
        public string Epoch;

        /// <summary>1 始まり。<b><c>Epoch</c> を跨いで一意ではない。</b>単独でキーにしないこと。</summary>
        public long Seq;

        /// <summary>
        /// ISO8601。
        ///
        /// ★ <b>単独のキーにしないこと。</b> 同一メッセージ内で同値になるので、
        ///   これで Map を引いたり重複排除したりすると<b>1メッセージが1文に潰れる</b>。
        /// </summary>
        public string Ts;

        public string SessionId;
        public string TurnId;
        public string MessageId;
        public SpeechKind Kind;

        /// <summary>1文。Markdown 除去済み。</summary>
        public string Text;

        public Emotion Emotion;

        /// <summary><c>null</c> 可。→ <see cref="AudioRef"/></summary>
        public AudioRef Audio;
    }

    /// <summary>
    /// サーバーから届いたテキストフレームを <see cref="SpeechFrame"/> として読む。
    ///
    /// ★ サーバーは「<c>SpeechRecord</c> の JSON 以外は送らない」契約だが、
    ///   <b>受け取る側でも通す値を絞る</b>。<c>Seq</c> は Map のキーであり ack の値でもあるので、
    ///   ここが緩いと壊れたプロデューサーの影響がキュー全体に及ぶ。
    ///
    /// ★ 読めないフレームは<b>警告して捨てる。接続は切らない</b>。
    ///   1フレームの不整合でストリーム全体を落とす理由が無い。
    ///
    /// ★ <b><c>JsonUtility</c> を使わないこと。</b> <c>audio</c> キーの「欠落」と「null」を
    ///   区別する必要があり（下の <see cref="TryParse"/> の <c>audioDeclared</c>）、
    ///   <c>JsonUtility</c> にはその区別ができない。
    /// </summary>
    public static class SpeechFrameParser
    {
        /// <summary>
        /// JS の <c>Number.MAX_SAFE_INTEGER</c>。契約の「非負の安全整数」を C# 側でも同じ範囲に揃える。
        /// </summary>
        private const long MaxSafeInteger = 9007199254740991L;

        /// <summary>
        /// 読めたら <c>true</c>。
        /// </summary>
        /// <param name="audioDeclared">
        /// フレームに <c>audio</c> キーが<b>載っていたか</b>。
        ///
        /// ★ <b><c>"audio": null</c> と別物として扱うこと。</b> ワイヤ上では区別できる:
        ///   <c>ttsEnabled: false</c> や読み上げる中身が無い文では<b>明示的に <c>null</c> が載る</b>が、
        ///   #29 より前のサーバーでは<b>キーが存在しない</b>。潰したままだと、後者が前者と
        ///   区別なく<b>全文が無言で ack され、どちらの側にも1行も出ない</b>。
        ///
        /// JSON として読めなかったときは <c>true</c>（＝警告しない）。フレームごと捨てる経路が
        /// 別に警告するので、ここで二重に出さない。
        /// </param>
        public static bool TryParse(string raw, out SpeechFrame frame, out bool audioDeclared)
        {
            frame = null;
            audioDeclared = true;

            JObject root;
            try
            {
                // ★ **DateParseHandling.None を指定すること。**
                //   Newtonsoft は既定で「ISO8601 らしき文字列」を DateTime に自動変換する。
                //   そのままだと `ts` が JTokenType.String ではなく Date になり、
                //   **文字列として読めず、正常なフレームが1つも通らなくなる**
                //   （症状は「全フレームが読めないフレームとして捨てられる」＝完全な無音）。
                //   契約上 `ts` は不透明な文字列で、古さの判定のときだけパースする。
                using (var reader = new JsonTextReader(new StringReader(raw)))
                {
                    reader.DateParseHandling = DateParseHandling.None;
                    root = JToken.Load(reader) as JObject;
                }
                if (root == null) return false;
            }
            catch (Exception)
            {
                return false;
            }

            audioDeclared = root["audio"] != null;

            // ★ charset から外れる値を通さないこと。この値は音声の URL の材料になる
            var epoch = AsString(root["epoch"]);
            if (!SpeechEpoch.IsValid(epoch)) return false;

            // ★ 1 始まり。0 を通さないこと。0 は「未受信」の初期値と衝突する
            long seq;
            if (!TryAsInteger(root["seq"], out seq)) return false;
            if (seq < 1 || seq > MaxSafeInteger) return false;

            var text = AsString(root["text"]);
            if (text == null) return false;

            var ts = AsString(root["ts"]);
            if (string.IsNullOrEmpty(ts)) return false;

            frame = new SpeechFrame
            {
                Epoch = epoch,
                Seq = seq,
                Ts = ts,
                SessionId = AsString(root["sessionId"]),
                TurnId = AsString(root["turnId"]),
                MessageId = AsString(root["messageId"]),
                Kind = ParseKind(AsString(root["kind"])),
                Text = text,
                Emotion = ParseEmotion(AsString(root["emotion"])),
                Audio = ParseAudio(root["audio"]),
            };
            return true;
        }

        /// <summary>docs/protocol.md: 未知の <c>kind</c> は <c>assistant</c> として扱う</summary>
        private static SpeechKind ParseKind(string raw)
        {
            return raw == "prompt" ? SpeechKind.Prompt : SpeechKind.Assistant;
        }

        private static Emotion ParseEmotion(string raw)
        {
            switch (raw)
            {
                case "happy": return Emotion.Happy;
                case "angry": return Emotion.Angry;
                case "sad": return Emotion.Sad;
                case "relaxed": return Emotion.Relaxed;
                case "surprised": return Emotion.Surprised;
                default: return Emotion.Neutral;
            }
        }

        /// <summary>
        /// 音声の参照。読めなければ <c>null</c>（＝鳴らさずに ack する）。
        ///
        /// ★ <b>絶対 URL を通さないこと。</b> 任意の URL を受け入れると、サーバーが
        ///   クライアントを任意の外部ホストへ向かわせられる。
        /// ★ <c>format</c> の<b>知らない値は「音声なし」として扱う</b>（docs/protocol.md）。
        /// </summary>
        private static AudioRef ParseAudio(JToken raw)
        {
            var obj = raw as JObject;
            if (obj == null) return null;

            var path = AsString(obj["path"]);
            if (!AudioPath.IsValid(path)) return null;
            if (AsString(obj["format"]) != "wav") return null;

            return new AudioRef(path);
        }

        /// <summary>取れなかった識別子は <c>null</c>。文字列以外も <c>null</c> に丸める。</summary>
        private static string AsString(JToken token)
        {
            if (token == null || token.Type != JTokenType.String) return null;
            return token.Value<string>();
        }

        /// <summary>
        /// 整数として読む。<c>long</c> に収まらなければ読めなかったことにする。
        ///
        /// ★ <b>Newtonsoft は <c>long</c> を超える整数を <c>BigInteger</c> で持つが、
        ///   <c>JTokenType</c> は <c>Integer</c> のまま。</b> 型検査は通り、
        ///   <c>Value&lt;long&gt;()</c> が投げる。
        ///
        /// ★ <b>例外の型を決め打ちにしないこと。</b> 実測で投げたのは <c>OverflowException</c> ではなく
        ///   <c>InvalidCastException</c>（"Object must implement IConvertible"）だった——
        ///   <c>System.Numerics.BigInteger</c> が <c>IConvertible</c> を実装していないため、
        ///   <c>Convert.ChangeType</c> の手前で落ちる。値の持ち方は Newtonsoft のビルド構成
        ///   （<c>HAVE_BIG_INTEGER</c>）で変わるので、<b>「読めたか読めなかったか」だけを返す</b>。
        ///
        /// ★ <b>ここで握らないと、受信ループごと落ちる。</b> <see cref="TryParse"/> の
        ///   <c>try</c> は <c>JToken.Load</c> しか囲っていないので、例外は素通りして
        ///   <c>SpeechClient</c> の受信ループまで上がる。そこで接続が切れ、繋ぎ直した先で
        ///   サーバーが<b>同じ未 ack のフレームを再送する</b>ので、**また落ちる**。
        ///   ログは「受信でエラー」1行だけで、以後ずっと無音になる。
        /// </summary>
        private static bool TryAsInteger(JToken token, out long value)
        {
            value = 0;
            if (token == null || token.Type != JTokenType.Integer) return false;
            try
            {
                value = token.Value<long>();
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }
    }
}
