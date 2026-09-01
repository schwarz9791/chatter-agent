using System.Linq;
using ChatterMascot.Ui;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class MenuJsonTests
    {
        private static MenuState State(
            bool muted = false, bool hidden = false,
            string muteHotKey = "ctrl+opt+m", string hideHotKey = "ctrl+opt+h")
        {
            return new MenuState(
                muted, hidden, Spec(muteHotKey), Spec(hideHotKey),
                "Chatter Mascot", "1.2.3", 4242,
                "/tmp/trayTemplate.png", "/tmp/trayTemplate@2x.png");
        }

        private static HotKeySpec Spec(string text)
        {
            HotKeySpec spec;
            string error;
            HotKeySpec.TryParse(text, out spec, out error);
            return spec;
        }

        // ---- メニューの組み立て ----

        /// <summary>並びは cc-mascot のトレイメニューに合わせてある。</summary>
        [Test]
        public void BuildsTheExpectedOrder()
        {
            var keys = MascotMenu.Build(State()).Entries
                .Where(e => !e.IsSeparator)
                .Select(e => e.Key)
                .ToArray();

            Assert.That(keys, Is.EqualTo(new[]
            {
                MenuKeys.Mute, MenuKeys.Hide, MenuKeys.Settings, MenuKeys.About, MenuKeys.Quit,
            }));
        }

        [Test]
        public void ChecksMuteAndDimsTheIconWhenMuted()
        {
            var model = MascotMenu.Build(State(muted: true));
            var mute = model.Entries.First(e => e.Key == MenuKeys.Mute);

            Assert.That(mute.Checked, Is.True);
            // ★ 別画像を持たずに状態を目に見せる唯一の手段（ミュートは永続化される）
            Assert.That(model.Dimmed, Is.True);
        }

        [Test]
        public void SwitchesTheHideLabelWithTheState()
        {
            Assert.That(
                MascotMenu.Build(State(hidden: false)).Entries.First(e => e.Key == MenuKeys.Hide).Label,
                Does.StartWith("キャラクターを隠す"));
            Assert.That(
                MascotMenu.Build(State(hidden: true)).Entries.First(e => e.Key == MenuKeys.Hide).Label,
                Does.StartWith("キャラクターを表示する"));
        }

        /// <summary>#76（設定 UI）が入るまで押す先が無い。押せるように見せない。</summary>
        [Test]
        public void TheAboutEntryIsNotClickable()
        {
            var about = MascotMenu.Build(State()).Entries.First(e => e.Key == MenuKeys.About);

            Assert.That(about.Enabled, Is.False);
            Assert.That(about.Label, Does.Contain("1.2.3"));
        }

        /// <summary>★ Dock に出ない以上、二重起動は「アイコンが2つ並ぶ」でしか気づけない。</summary>
        [Test]
        public void PutsThePidInTheTooltip()
        {
            Assert.That(MascotMenu.Build(State()).Tooltip, Does.Contain("4242"));
        }

        [Test]
        public void ShowsTheShortcutInTheMuteLabel()
        {
            var entries = MascotMenu.Build(State()).Entries;
            Assert.That(entries.First(e => e.Key == MenuKeys.Mute).Label, Does.Contain("⌃⌥M"));
            Assert.That(entries.First(e => e.Key == MenuKeys.Hide).Label, Does.Contain("⌃⌥H"));
        }

        /// <summary>登録できていないときは表記を出さない（嘘のショートカットを見せない）。</summary>
        [Test]
        public void OmitsTheShortcutWhenItIsNotRegistered()
        {
            var state = new MenuState(
                false, false, default(HotKeySpec), default(HotKeySpec),
                "Chatter Mascot", "1.0", 1, null, null);
            var entries = MascotMenu.Build(state).Entries;

            Assert.That(entries.First(e => e.Key == MenuKeys.Mute).Label, Is.EqualTo("ミュート"));
            Assert.That(entries.First(e => e.Key == MenuKeys.Hide).Label, Is.EqualTo("キャラクターを隠す"));
        }

        // ---- JSON ----

        [Test]
        public void WritesWhatTheNativeSideExpects()
        {
            var root = JObject.Parse(MenuJson.Write(MascotMenu.Build(State(muted: true))));

            Assert.That(root["tooltip"], Is.Not.Null);
            Assert.That(root["dimmed"].Value<bool>(), Is.True);
            Assert.That(root["icon"]["1x"].Value<string>(), Is.EqualTo("/tmp/trayTemplate.png"));
            Assert.That(root["icon"]["2x"].Value<string>(), Is.EqualTo("/tmp/trayTemplate@2x.png"));

            var items = (JArray)root["items"];
            Assert.That(items.Count, Is.EqualTo(6), "5項目 + 区切り1本");
            Assert.That(items.Any(i => i["separator"] != null), Is.True);

            var mute = items.First(i => (string)i["key"] == MenuKeys.Mute);
            Assert.That(mute["checked"].Value<bool>(), Is.True);
            Assert.That(mute["enabled"].Value<bool>(), Is.True);
        }

        /// <summary>アイコンのパスが無いときは icon ごと出さない（ネイティブは既存の絵を保つ）。</summary>
        [Test]
        public void OmitsTheIconWhenThereIsNoPath()
        {
            var model = MascotMenu.Build(new MenuState(
                false, false, default(HotKeySpec), default(HotKeySpec), "x", "1", 1, null, null));

            Assert.That(JObject.Parse(MenuJson.Write(model))["icon"], Is.Null);
        }

        // ---- イベント ----

        [Test]
        public void ParsesAMenuEvent()
        {
            MenuEvent value;
            string error;
            Assert.That(MenuJson.TryParseEvent("{\"type\":\"menu\",\"key\":\"mute\"}", out value, out error),
                Is.True, error);

            Assert.That(value.Kind, Is.EqualTo(MenuEventKind.Menu));
            Assert.That(value.Key, Is.EqualTo(MenuKeys.Mute));
        }

        [Test]
        public void ParsesAHotKeyEvent()
        {
            MenuEvent value;
            string error;
            Assert.That(MenuJson.TryParseEvent("{\"type\":\"hotkey\",\"id\":1}", out value, out error),
                Is.True, error);

            Assert.That(value.Kind, Is.EqualTo(MenuEventKind.HotKey));
            Assert.That(value.HotKeyId, Is.EqualTo(1));
        }

        /// <summary>
        /// ★ ネイティブの診断はこの経路でしか届かない（<c>NSLog</c> は
        /// Unity の <c>Player.log</c> に入らない）。
        /// </summary>
        [Test]
        public void ParsesALogEvent()
        {
            MenuEvent value;
            string error;
            Assert.That(
                MenuJson.TryParseEvent("{\"type\":\"log\",\"message\":\"ステータスバー: item=あり\"}",
                    out value, out error),
                Is.True, error);

            Assert.That(value.Kind, Is.EqualTo(MenuEventKind.Log));
            Assert.That(value.Message, Is.EqualTo("ステータスバー: item=あり"));
        }

        /// <summary>
        /// ★ 読めないものは <c>Unknown</c> に倒して throw しない。この経路は
        /// <b>ネイティブのコールバックの中</b>から呼ばれる。
        /// </summary>
        [Test]
        public void RejectsMalformedEventsWithoutThrowing()
        {
            foreach (var raw in new[]
            {
                null, "", "ぐちゃぐちゃ", "[1]", "{}",
                "{\"type\":\"menu\"}",            // key が無い
                "{\"type\":\"hotkey\"}",          // id が無い
                "{\"type\":\"hotkey\",\"id\":\"1\"}",  // id が文字列
                "{\"type\":\"log\"}",             // message が無い
                "{\"type\":\"未来のイベント\"}",
            })
            {
                MenuEvent value;
                string error;
                Assert.That(MenuJson.TryParseEvent(raw, out value, out error), Is.False, raw ?? "null");
                Assert.That(value.Kind, Is.EqualTo(MenuEventKind.Unknown));
                Assert.That(error, Is.Not.Null.And.Not.Empty);
            }
        }
    }
}
