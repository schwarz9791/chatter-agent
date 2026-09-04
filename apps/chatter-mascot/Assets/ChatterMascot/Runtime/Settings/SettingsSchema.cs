using System.Collections.Generic;
using System.Globalization;
using ChatterMascot.Ui;

namespace ChatterMascot.Settings
{
    /// <summary>
    /// 設定項目のキー。<b>C# とネイティブの間で唯一共有される語彙</b>。
    ///
    /// ★ ネイティブ側（<c>CMSettingsPanel.m</c>）はこの文字列を<b>そのまま返すだけ</b>で、
    ///   意味を知らない（#75 の <c>MenuKeys</c> と同じ）。
    /// </summary>
    public static class SettingKeys
    {
        public const string Vrm = "vrm";

        /// <summary>
        /// ファイル選択で選ばれたパス（ネイティブ → C#）。
        ///
        /// ★ <c>Vrm</c> と分けること。同じキーだと「ボタンが押された」と
        ///   「ファイルが選ばれた」を区別できず、選んだ直後にもう一度ダイアログが開く。
        /// </summary>
        public const string VrmChosen = "vrmChosen";
        public const string Scale = "scale";

        public const string Speaker = "speaker";
        public const string Volume = "volume";
        public const string Speed = "speed";
        public const string TtsPreview = "ttsPreview";

        public const string IdleMotion = "idleMotion";
        public const string CursorGaze = "cursorGaze";
        public const string Blink = "blink";
        public const string FrameRate = "frameRate";

        public const string SummaryEnabled = "summaryEnabled";

        public const string MuteHotKey = "muteHotKey";
        public const string HideHotKey = "hideHotKey";

        public const string ResetPosition = "resetPosition";
        public const string ResetAll = "resetAll";

        public const string Version = "version";
        public const string License = "license";
        public const string Quit = "quit";
    }

    /// <summary>core の設定キー。<c>PATCH /v1/config</c> のボディと <c>origins</c> で使う</summary>
    public static class CoreConfigKeys
    {
        public const string SpeakerId = "ttsSpeakerId";
        public const string SpeedScale = "ttsSpeedScale";
        public const string SummaryEnabled = "aiSummaryEnabled";
    }

    /// <summary>
    /// 設定パネルの<b>並び順を持つ唯一の場所</b>。<c>MascotMenu.Build</c>（#75）と同じ役回り。
    ///
    /// ★★ <b>ネイティブ側に項目を書かないこと。</b> <c>CMSettingsPanel.m</c> は
    ///   <see cref="SettingKind"/> を見てビューを組むだけで、キーもラベルも1つも持たない。
    ///   項目の追加・並び替え・ラベルの変更が C# だけの変更で済むのが、この作りを選んだ理由そのもの。
    ///
    /// ★★ <b>「押しても何も起きない項目」を出さないこと。</b> グレーアウトで出す案も採らない ——
    ///   「今はできない」と「壊れている」がユーザーから区別できない。実装が無いものは
    ///   <b>項目ごと出さず、ここにコメントで場所だけ確保する</b>。
    /// </summary>
    /// <summary>
    /// 設定できるショートカット1本ぶん（→ <see cref="SettingsSchema.HotKeySlots"/>）。
    /// </summary>
    public readonly struct HotKeySlot
    {
        public HotKeySlot(string key, string label, string value)
        {
            Key = key;
            Label = label;
            Value = value;
        }

        /// <summary>画面の項目キー（<see cref="SettingKeys"/>）</summary>
        public string Key { get; }

        public string Label { get; }

        /// <summary>いまの指定（<c>ctrl+opt+m</c>）。<b>保存される形</b></summary>
        public string Value { get; }
    }

    public static class SettingsSchema
    {
        /// <summary>
        /// 設定できるショートカットの一覧。<b>増やすときはここに1行足す</b>。
        ///
        /// ★★ <b>読み手が2人いる。</b> 画面の行（<see cref="Build"/>）と、
        ///   <b>重複の判定</b>（<c>SettingsPanelBridge.ApplyHotKey</c>）。並びを2箇所に持つと、
        ///   片方にだけ足したときに<b>「重複を見ていないショートカット」</b>ができる ——
        ///   しかも症状は「登録したのに効かない」で、<b>どこにも理由が出ない</b>。
        ///
        /// ★ <b>「もう片方」を呼び出し側に持たせないこと。</b> 2本のうちは <c>case</c> に
        ///   相手を書けば済むが、3本目からは組み合わせのぶん増える。<b>一覧を配って
        ///   自分以外と比べさせる</b>形なら、増えるのは<b>この配列の1行だけ</b>。
        /// </summary>
        public static IReadOnlyList<HotKeySlot> HotKeySlots(MascotSettings settings)
        {
            return new[]
            {
                new HotKeySlot(SettingKeys.MuteHotKey, "ミュートの切り替え", settings.MuteHotKey),
                new HotKeySlot(SettingKeys.HideHotKey, "キャラクターの表示切り替え", settings.HideHotKey),
            };
        }

        public static IReadOnlyList<SettingSpec> Build(SettingsContext context)
        {
            var c = context ?? new SettingsContext();
            var settings = c.Settings;
            var items = new List<SettingSpec>();

            // ── キャラクター ─────────────────────────────────
            items.Add(SettingSpec.Section("キャラクター"));
            items.Add(SettingSpec.Button(
                SettingKeys.Vrm, "VRM モデルを選ぶ…",
                note: string.IsNullOrEmpty(c.VrmFileName) ? "同梱のモデルを使っています" : c.VrmFileName));
            items.Add(SettingSpec.Slider(
                SettingKeys.Scale, "大きさ", c.WindowScale,
                SettingsMapping.ScaleMin, SettingsMapping.ScaleMax, SettingsMapping.ScaleStep));

            // ── オーディオ ───────────────────────────────────
            items.Add(SettingSpec.Section("オーディオ"));

            // ★ サーバーに繋がらなくても**項目は出す**。消すと「設定が無い」に見える
            var speakerOverridden = c.IsCoreEnvOverridden(CoreConfigKeys.SpeakerId);
            items.Add(SettingSpec.Choice(
                SettingKeys.Speaker, "音声スタイル", c.SpeakerId, c.Speakers,
                enabled: c.CoreReachable && c.Speakers.Count > 0 && !speakerOverridden,
                note: SpeakerNote(c, speakerOverridden)));

            // ★ 音量だけ % で出す。倍率のスライダー（大きさ・話す速さ）は生の数のまま ——
            //   あちらは「1.0 が等倍」に意味があるので、100% と書くとかえって分かりづらい
            items.Add(SettingSpec.Slider(
                SettingKeys.Volume, "音量", settings.Volume,
                SettingsMapping.VolumeMin, SettingsMapping.VolumeMax, SettingsMapping.VolumeStep,
                display: SettingDisplay.Percent));

            var speedOverridden = c.IsCoreEnvOverridden(CoreConfigKeys.SpeedScale);
            items.Add(SettingSpec.Slider(
                SettingKeys.Speed, "話す速さ", c.SpeedScale,
                SettingsMapping.SpeedMin, SettingsMapping.SpeedMax, SettingsMapping.SpeedStep,
                enabled: c.CoreReachable && !speedOverridden,
                note: CoreNote(c, speedOverridden, CoreConfigKeys.SpeedScale, "次に喋る文から変わります")));

            items.Add(SettingSpec.Button(
                SettingKeys.TtsPreview, "テスト音声を再生",
                enabled: c.CoreReachable, note: c.CoreReachable ? "" : c.CoreNote));

            // ★ **「音声出力デバイス」はここに出さない**（#83）。`afplay` に出力先を指す引数が無く、
            //   実体は再生経路そのものの置き換えになる。#83 が入ったらここに1行足す。
            //   ★ Android / XR には出ない項目でもある（`AudioSource` にデバイス選択の API が無い）

            // ── モーション ───────────────────────────────────
            items.Add(SettingSpec.Section("モーション"));
            items.Add(SettingSpec.Bool(SettingKeys.IdleMotion, "待機モーション", settings.IdleMotion));
            items.Add(SettingSpec.Bool(SettingKeys.CursorGaze, "カーソルを目で追う", settings.CursorGaze));
            items.Add(SettingSpec.Bool(SettingKeys.Blink, "まばたき", settings.Blink));
            items.Add(SettingSpec.Choice(
                SettingKeys.FrameRate, "フレームレート",
                settings.FrameRate.ToString(CultureInfo.InvariantCulture),
                FrameRateChoices(),
                note: "60 fps は CPU を約 2 倍使います"));

            // ★ **「発話モーション」「クール系 / かわいい系」は出さない。** cc-mascot には
            //   あるが、chatter-agent に対応する実装が無い（#70 が未実装）。
            //   `VrmPoseAccent` は視線由来の頭の向きと `kind:"prompt"` の前傾を乗せるもので、
            //   発話に連動する体の動きではない。#70 が入ったらここに1行足す。

            // ── AI要約 ──────────────────────────────────────
            items.Add(SettingSpec.Section("AI要約"));
            var summaryOverridden = c.IsCoreEnvOverridden(CoreConfigKeys.SummaryEnabled);
            items.Add(SettingSpec.Bool(
                SettingKeys.SummaryEnabled, "長いメッセージを要約してから読み上げる", c.SummaryEnabled,
                enabled: c.CoreReachable && !summaryOverridden,
                note: CoreNote(c, summaryOverridden, CoreConfigKeys.SummaryEnabled,
                    "要約には時間がかかります（間に合わなければ原文を読み上げます）")));
            // ★ **「テスト要約を実行」は出さない。** 結果（要約文）を項目の note に出す形しか
            //   無く、原文が見えないので「要約されているか」が判断できないうえ、
            //   長い結果でパネルが伸びた。サーバー側の口
            //   （`POST /v1/summary/preview`）は残してあるので、確かめたいときは curl で叩く。

            // ── ショートカット ────────────────────────────────
            // ★ 記録の仕方は**節の全部にかかる**ので見出しに付ける（→ SettingSpec.Section の ★★）
            items.Add(SettingSpec.Section(
                "ショートカット", note: "「記録」を押してキーを押してください（修飾キーを1つ以上）"));
            // ★ **ミュートのチェックボックスは出さない。** ミュートはメニューバー
            //   （アイコンが薄くなる）とショートカットで操作するもの、と割り切った。
            //   ここに置くと、ショートカットで切り替えたときに**パネルだけ古い状態のまま**になる。
            // ★ 画面に出すのは記号（⌃⌥M）。**保存される文字列（ctrl+opt+m）ではない。**
            //   パネルが返してくるのは keyCode と修飾マスクの数値なので、
            //   ネイティブは保存形式を一度も見ない
            // ★★ **並びは `HotKeySlots` が持つ**（→ あちらの ★★）。ここで直に並べると、
            //   ショートカットを1本足すたびに「画面の行」と「重複の判定」の両方に
            //   書き足すことになり、片方を忘れた瞬間に穴が空く
            var slots = HotKeySlots(settings);
            for (var i = 0; i < slots.Count; i++)
            {
                // ★★ **重複していたら画面に出すこと（第2層）。** パネルからの記録は
                //   `SettingsPanelBridge.ApplyHotKey` が保存の手前で弾くので、ここに来るのは
                //   **`settings.json` を手で編集した場合**だけ。放っておくと同じ表記が並び、
                //   `RegisterHotKeys` が黙って2つ目を登録せずに戻る ——
                //   **「押しても何も起きないショートカット」が画面に残る**。
                //   ★ **後の行にだけ出す。** 登録されないのは後から来た方
                //   （→ `StatusItemBridge.RegisterHotKeys` の登録順）
                var clash = -1;
                for (var j = 0; j < i && clash < 0; j++)
                {
                    if (HotKeySpec.SameCombination(slots[i].Value, slots[j].Value)) clash = j;
                }

                items.Add(SettingSpec.HotKey(
                    slots[i].Key, slots[i].Label, Symbols(slots[i].Value),
                    note: clash < 0 ? "" : $"「{slots[clash].Label}」と同じ組み合わせなので登録できません"));
            }

            // ── リセット ─────────────────────────────────────
            items.Add(SettingSpec.Section("リセット"));
            items.Add(SettingSpec.Button(SettingKeys.ResetPosition, "キャラクターの位置と大きさをリセット"));
            // ★★ **取り消せない操作。** models/ に置いた .vrm も消えるので、
            //   ネイティブの確認ダイアログを挟む（→ SettingsPanelBridge）
            items.Add(SettingSpec.Button(
                SettingKeys.ResetAll, "すべての設定をリセット…",
                note: "選んだモデルのファイルも消して、完全に初期状態へ戻します"));

            // ★ **「このアプリについて」はここに出さない。** ライセンス本文が長く、
            //   設定パネルの大半を占めてしまう。メニューバーの「Chatter Mascot について」から
            //   別のダイアログで開く（→ AboutSchema）。

            items.Add(SettingSpec.Button(SettingKeys.Quit, "終了"));

            return items;
        }

        /// <summary>
        /// 「Chatter Mascot について」の中身。**設定とは別のダイアログ**に出す。
        ///
        /// ★ <b>設定パネルに混ぜないこと。</b> ライセンス本文だけで数百行あり、
        ///   設定の項目がその下に埋もれる。
        ///
        /// ★ <b>同じレンダラで描く</b>（<c>CM_PanelShow</c> のパネル ID 違い）。
        ///   2枚目のために ObjC を書き足さない。
        /// </summary>
        public static IReadOnlyList<SettingSpec> BuildAbout(SettingsContext context)
        {
            var c = context ?? new SettingsContext();
            return new List<SettingSpec>
            {
                SettingSpec.Text(
                    SettingKeys.Version, "バージョン",
                    string.IsNullOrEmpty(c.Version) ? c.ProductName : c.ProductName + " " + c.Version),
                SettingSpec.Text(SettingKeys.License, "ライセンス", c.LicenseText),
            };
        }

        /// <summary>
        /// フレームレートの選択肢。
        ///
        /// ★ <b><see cref="SettingsMapping.FrameRateChoices"/> から作ること</b> —— 選べる値の
        ///   一覧をここと <c>SettingsMapping</c> の2箇所に書くと、増減したときに片方だけ直して
        ///   選択肢と検証（<c>NormalizeFrameRate</c>）がズレる。
        /// </summary>
        private static IReadOnlyList<SettingChoice> FrameRateChoices()
        {
            var choices = new SettingChoice[SettingsMapping.FrameRateChoices.Length];
            for (var i = 0; i < choices.Length; i++)
            {
                var fps = SettingsMapping.FrameRateChoices[i].ToString(CultureInfo.InvariantCulture);
                choices[i] = new SettingChoice(fps, fps + " fps");
            }
            return choices;
        }

        /// <summary>保存されている文字列を画面用の記号にする。読めなければそのまま出す</summary>
        private static string Symbols(string hotKey)
        {
            Ui.HotKeySpec spec;
            string error;
            if (Ui.HotKeySpec.TryParse(hotKey, out spec, out error)) return spec.FormatSymbols();
            // ★ 空にしないこと。壊れた値が入っていることが画面から分かる方がよい
            return string.IsNullOrEmpty(hotKey) ? "" : hotKey;
        }

        private static string SpeakerNote(SettingsContext c, bool overridden)
        {
            if (overridden) return EnvNote(CoreConfigKeys.SpeakerId);
            if (!c.CoreReachable) return c.CoreNote;
            if (c.Speakers.Count == 0) return "話者の一覧を取得できませんでした";
            return "次に喋る文から変わります";
        }

        private static string CoreNote(SettingsContext c, bool overridden, string coreKey, string normal)
        {
            if (overridden) return EnvNote(coreKey);
            if (!c.CoreReachable) return c.CoreNote;
            return normal;
        }

        /// <summary>
        /// ★ <b>「効かない」ではなく「なぜ効かないか」を出すこと。</b> 環境変数で固定している
        /// 本人にとっては意図どおりなので、環境変数名まで出せば「自分で決めた」と分かる。
        /// </summary>
        private static string EnvNote(string coreKey)
        {
            return "環境変数（" + EnvNameOf(coreKey) + "）で固定されています";
        }

        /// <summary>
        /// core の設定キー → 環境変数名。
        ///
        /// ★ <b>core の <c>SPECS</c> が権威。</b> ここは表示のための写しなので、
        ///   知らないキーが来たらキー名をそのまま出す（嘘の環境変数名を出さない）。
        /// </summary>
        private static string EnvNameOf(string coreKey)
        {
            switch (coreKey)
            {
                case CoreConfigKeys.SpeakerId: return "CHATTER_AGENT_TTS_SPEAKER_ID";
                case CoreConfigKeys.SpeedScale: return "CHATTER_AGENT_TTS_SPEED_SCALE";
                case CoreConfigKeys.SummaryEnabled: return "CHATTER_AGENT_AI_SUMMARY_ENABLED";
                default: return coreKey;
            }
        }
    }
}
