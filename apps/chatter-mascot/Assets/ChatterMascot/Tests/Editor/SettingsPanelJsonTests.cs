using System.Collections.Generic;
using ChatterMascot.Settings;
using ChatterMascot.Ui;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class SettingsPanelJsonTests
    {
        private static JObject Write(params SettingSpec[] items)
        {
            return JObject.Parse(SettingsPanelJson.Write("設定", new List<SettingSpec>(items)));
        }

        [Test]
        public void WritesTheTitleAndItems()
        {
            var root = Write(SettingSpec.Section("見出し"));

            Assert.That(root["title"].Value<string>(), Is.EqualTo("設定"));
            Assert.That(((JArray)root["items"]).Count, Is.EqualTo(1));
            Assert.That(root["items"][0]["kind"].Value<string>(), Is.EqualTo("section"));
        }

        /// <summary>
        /// ★★ ボタンの文字までここで持つ。「ボタンの文字くらいネイティブに書いてよい」で
        ///   例外を1つ作ると、項目のラベルとの線引きが説明できなくなる。
        /// </summary>
        [Test]
        public void CarriesTheChromeStringsSoNativeHasNone()
        {
            var strings = (JObject)Write(SettingSpec.Section("x"))["strings"];

            foreach (var name in new[] { "record", "cancel", "recording", "empty" })
            {
                Assert.That(strings[name], Is.Not.Null, name);
                Assert.That(strings[name].Value<string>(), Is.Not.Empty, name);
            }
        }

        /// <summary>★ enum の数値を渡さないこと（並びを変えた瞬間に古いバンドルが別の種類で描く）</summary>
        [Test]
        public void WritesKindsAsStrings()
        {
            var root = Write(
                SettingSpec.Bool("b", "b", true),
                SettingSpec.Slider("s", "s", 1f, 0f, 2f, 0.1f),
                SettingSpec.Choice("c", "c", "1", new[] { new SettingChoice("1", "one") }),
                SettingSpec.Button("btn", "btn"),
                SettingSpec.HotKey("h", "h", "⌃⌥M"),
                SettingSpec.Text("t", "t", "body"));

            var kinds = new List<string>();
            foreach (var item in (JArray)root["items"]) kinds.Add(item["kind"].Value<string>());

            Assert.That(kinds, Is.EqualTo(new[] { "bool", "slider", "choice", "button", "hotkey", "text" }));
        }

        /// <summary>
        /// ★ スライダーは数値で渡すこと。文字列にすると、ネイティブ側でロケール依存の
        ///   パースが要る。
        /// </summary>
        /// <summary>
        /// ★★ <b>見せ方を渡すのはここ</b>（#76）。「音量なら % で出す」を ObjC に書くと、
        ///   ネイティブに設定のキーを持たせないという作りの前提が崩れる。
        ///   ★ <b>値そのものは変えない</b> —— 送るのも保存するのも常に生の数（0.0〜1.0）。
        /// </summary>
        [Test]
        public void CarriesThePercentDisplayForSliders()
        {
            var item = Write(SettingSpec.Slider(
                "s", "s", 0.7f, 0f, 1f, 0.1f, display: SettingDisplay.Percent))["items"][0];

            Assert.That(item["display"].Value<string>(), Is.EqualTo("percent"));
            Assert.That(item["value"].Value<float>(), Is.EqualTo(0.7f), "★ 値は 70 にしない");
        }

        /// <summary>★ 既定（倍率のスライダー）では出さない。JSON に「指定していない」を残す</summary>
        [Test]
        public void OmitsTheDisplayWhenItIsTheDefault()
        {
            Assert.That(Write(SettingSpec.Slider("s", "s", 1.5f, 0.5f, 2f, 0.1f))["items"][0]["display"],
                Is.Null);
        }

        /// <summary>★ 注記を足しても見せ方は落とさない（→ <c>SettingSpec.WithNote</c>）</summary>
        [Test]
        public void KeepsThePercentDisplayThroughWithNote()
        {
            var source = SettingSpec.Slider("s", "s", 0.7f, 0f, 1f, 0.1f, display: SettingDisplay.Percent);
            var item = Write(SettingSpec.WithNote(source, "理由"))["items"][0];

            Assert.That(item["display"].Value<string>(), Is.EqualTo("percent"));
        }

        [Test]
        public void WritesSliderValuesAsNumbers()
        {
            var item = Write(SettingSpec.Slider("s", "s", 0.7000000119f, 0f, 2f, 0.1f))["items"][0];

            Assert.That(item["value"].Type, Is.EqualTo(JTokenType.Float));
            Assert.That(item["value"].Value<float>(), Is.EqualTo(0.7f).Within(0.0001f));
            Assert.That(item["min"].Value<float>(), Is.EqualTo(0f));
            Assert.That(item["max"].Value<float>(), Is.EqualTo(2f));
            Assert.That(item["step"].Value<float>(), Is.EqualTo(0.1f).Within(0.0001f));
        }

        [Test]
        public void WritesBoolValuesAsBooleans()
        {
            var item = Write(SettingSpec.Bool("b", "b", true))["items"][0];

            Assert.That(item["value"].Type, Is.EqualTo(JTokenType.Boolean));
            Assert.That(item["value"].Value<bool>(), Is.True);
        }

        [Test]
        public void WritesChoicesWithValuesAndLabels()
        {
            var item = Write(SettingSpec.Choice(
                "c", "c", "2", new[] { new SettingChoice("1", "one"), new SettingChoice("2", "two") }))["items"][0];

            Assert.That(item["value"].Value<string>(), Is.EqualTo("2"));
            var choices = (JArray)item["choices"];
            Assert.That(choices.Count, Is.EqualTo(2));
            Assert.That(choices[1]["value"].Value<string>(), Is.EqualTo("2"));
            Assert.That(choices[1]["label"].Value<string>(), Is.EqualTo("two"));
        }

        /// <summary>★ 選択肢が空でも項目ごと消さない（ネイティブ側が「取得できません」を出す）</summary>
        [Test]
        public void KeepsChoiceItemsWithNoChoices()
        {
            var item = Write(SettingSpec.Choice("c", "c", "", new SettingChoice[0], enabled: false, note: "無理"))["items"][0];

            Assert.That(((JArray)item["choices"]).Count, Is.EqualTo(0));
            Assert.That(item["enabled"].Value<bool>(), Is.False);
            Assert.That(item["note"].Value<string>(), Is.EqualTo("無理"));
        }

        /// <summary>★ 押しても何も返らない項目を出さない（キーの無い項目は落とす）</summary>
        [Test]
        public void DropsItemsThatCouldNotBeActedOn()
        {
            var root = Write(
                SettingSpec.Bool(null, "キーが無い", true),
                SettingSpec.Bool("k", null, true));

            Assert.That(((JArray)root["items"]).Count, Is.EqualTo(0));
        }

        [Test]
        public void OmitsEmptyNotes()
        {
            var item = Write(SettingSpec.Bool("b", "b", true))["items"][0];
            Assert.That(item["note"], Is.Null);
        }

        [Test]
        public void ParsesBoolValuesLeniently()
        {
            Assert.That(SettingsPanelJson.ParseBool("true", false), Is.True);
            Assert.That(SettingsPanelJson.ParseBool("1", false), Is.True);
            Assert.That(SettingsPanelJson.ParseBool("off", true), Is.False);
            Assert.That(SettingsPanelJson.ParseBool("なんだこれ", true), Is.True, "読めなければ元の値");
        }

        [Test]
        public void ParsesIntsWithTheInvariantCulture()
        {
            int value;
            Assert.That(SettingsPanelJson.TryParseInt("888753760", out value), Is.True);
            Assert.That(value, Is.EqualTo(888753760));
            Assert.That(SettingsPanelJson.TryParseInt("あ", out value), Is.False);
        }

        /// <summary>★ note だけ差し替えた複製が、他のフィールドを落とさないこと</summary>
        [Test]
        public void WithNoteKeepsEverythingElse()
        {
            var source = SettingSpec.Slider("s", "s", 1.5f, 0f, 2f, 0.1f, enabled: false, note: "元");
            var copy = SettingSpec.WithNote(source, "新しい");

            Assert.That(copy.Note, Is.EqualTo("新しい"));
            Assert.That(copy.Key, Is.EqualTo(source.Key));
            Assert.That(copy.Kind, Is.EqualTo(source.Kind));
            Assert.That(copy.Value, Is.EqualTo(source.Value));
            Assert.That(copy.Min, Is.EqualTo(source.Min));
            Assert.That(copy.Max, Is.EqualTo(source.Max));
            Assert.That(copy.Step, Is.EqualTo(source.Step));
            Assert.That(copy.Enabled, Is.EqualTo(source.Enabled));
        }
    }
}
