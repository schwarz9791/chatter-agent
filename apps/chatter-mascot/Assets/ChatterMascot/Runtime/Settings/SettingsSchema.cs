using System.Collections.Generic;

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
        public const string Scale = "scale";

        public const string Speaker = "speaker";
        public const string Volume = "volume";
        public const string Speed = "speed";
        public const string TtsPreview = "ttsPreview";

        public const string IdleMotion = "idleMotion";
        public const string CursorGaze = "cursorGaze";
        public const string Blink = "blink";

        public const string SummaryEnabled = "summaryEnabled";
        public const string SummaryPreview = "summaryPreview";

        public const string Mute = "mute";
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
    public static class SettingsSchema
    {
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
                SettingKeys.Scale, "大きさ", settings.CharacterScale,
                SettingsMapping.ScaleMin, SettingsMapping.ScaleMax, SettingsMapping.ScaleStep));

            // ── オーディオ ───────────────────────────────────
            items.Add(SettingSpec.Section("オーディオ"));

            // ★ サーバーに繋がらなくても**項目は出す**。消すと「設定が無い」に見える
            var speakerOverridden = c.IsCoreEnvOverridden(CoreConfigKeys.SpeakerId);
            items.Add(SettingSpec.Choice(
                SettingKeys.Speaker, "音声スタイル", c.SpeakerId, c.Speakers,
                enabled: c.CoreReachable && c.Speakers.Count > 0 && !speakerOverridden,
                note: SpeakerNote(c, speakerOverridden)));

            items.Add(SettingSpec.Slider(
                SettingKeys.Volume, "音量", settings.Volume,
                SettingsMapping.VolumeMin, SettingsMapping.VolumeMax, SettingsMapping.VolumeStep));

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
            items.Add(SettingSpec.Button(
                SettingKeys.SummaryPreview, "テスト要約を実行",
                enabled: c.CoreReachable, note: c.CoreReachable ? "" : c.CoreNote));

            // ── ショートカット ────────────────────────────────
            items.Add(SettingSpec.Section("ショートカット"));
            items.Add(SettingSpec.Bool(SettingKeys.Mute, "ミュート（声だけ消す）", settings.Muted));
            // ★ 画面に出すのは記号（⌃⌥M）。**保存される文字列（ctrl+opt+m）ではない。**
            //   パネルが返してくるのは keyCode と修飾マスクの数値なので、
            //   ネイティブは保存形式を一度も見ない
            items.Add(SettingSpec.HotKey(
                SettingKeys.MuteHotKey, "ミュートの切り替え", Symbols(settings.MuteHotKey),
                note: "「記録」を押してキーを押してください（修飾キーを1つ以上）"));
            items.Add(SettingSpec.HotKey(
                SettingKeys.HideHotKey, "キャラクターの表示切り替え", Symbols(settings.HideHotKey)));

            // ── リセット ─────────────────────────────────────
            items.Add(SettingSpec.Section("リセット"));
            items.Add(SettingSpec.Button(SettingKeys.ResetPosition, "キャラクターの位置をリセット"));
            // ★ core の `config.json` は触らない。別プロセスの設定を消すのは越権
            items.Add(SettingSpec.Button(
                SettingKeys.ResetAll, "すべての設定をリセット",
                note: "音声スタイル・話す速さ・要約の設定は残ります"));

            // ── このアプリについて ─────────────────────────────
            items.Add(SettingSpec.Section("このアプリについて"));
            items.Add(SettingSpec.Text(
                SettingKeys.Version, "バージョン",
                string.IsNullOrEmpty(c.Version) ? c.ProductName : c.ProductName + " " + c.Version));
            items.Add(SettingSpec.Text(SettingKeys.License, "ライセンス", c.LicenseText));
            items.Add(SettingSpec.Button(SettingKeys.Quit, "終了"));

            return items;
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
