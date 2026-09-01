#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ChatterMascot.Desktop.Native;
using ChatterMascot.Net;
using ChatterMascot.Settings;
using ChatterMascot.Ui;
using ChatterMascot.Vrm;
using Kirurobo;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ChatterMascot.Desktop
{
    /// <summary>
    /// 設定パネルが必要とする「Unity 側の権威」への口。<see cref="StatusItemBridge"/> が実装する。
    ///
    /// ★ <b><see cref="SettingsStore"/> を2つ作らないための境界。</b> パネルが自分で
    ///   ストアを持つと、<b>同じ <c>settings.json</c> を2人が read-modify-write する</b>ことになり、
    ///   先に書いた方の変更が消える。ストアの持ち主は1人だけ。
    /// </summary>
    internal interface ISettingsHost
    {
        MascotSettings Settings { get; }

        /// <summary>適用（シーンへ反映）+ 保存 + メニューの更新をまとめて行う</summary>
        void ApplySettings(MascotSettings next);

        /// <summary>キャラクターの位置と大きさ（<c>window.json</c>）だけ既定へ戻す</summary>
        void ResetWindow();

        /// <summary><c>mascot/</c> 配下すべてを既定へ戻す。★ core の <c>config.json</c> は触らない</summary>
        void ResetAll();

        void Quit();
    }

    /// <summary>
    /// ネイティブの設定パネル（<c>CMSettingsPanel.m</c>）と、Unity / core の設定をつなぐ。
    ///
    /// ★ <b><c>MonoBehaviour</c> にしない。</b> <see cref="StatusItemBridge"/> が所有する
    ///   ただのクラスにしてある —— イベントの経路（ネイティブのコールバック）も
    ///   <see cref="SettingsStore"/> もあちらが持っているので、独立した常駐物にすると
    ///   どちらも二重になる。
    ///
    /// ★★ <b>値の行き先は3つある。混ぜないこと。</b>
    ///   <list type="bullet">
    ///     <item>Unity の <c>settings.json</c>: 大きさ / 音量 / モーション / ミュート / ショートカット / VRM</item>
    ///     <item>core の <c>config.json</c>（<c>PATCH /v1/config</c> 経由）: 音声スタイル / 話す速さ / 要約</item>
    ///     <item>読むだけ: バージョン / ライセンス / 話者一覧</item>
    ///   </list>
    ///   ★ <b>Unity から <c>config.json</c> を直接書かないこと</b> —— core の <c>SPECS</c> の
    ///   パーサを通らない JSON は誰にも検証されない。
    /// </summary>
    internal sealed class SettingsPanelBridge
    {
        /// <summary>スライダーを動かしてから core へ送るまでの猶予</summary>
        private const float PatchDebounceSeconds = 0.3f;

        /// <summary>
        /// テスト要約の予算。★ サーバー側は <c>aiSummaryTimeoutMs</c>（既定60秒）まで粘るので、
        /// それより短くすると「サーバーは答えているのにこちらだけ諦める」形になる。
        /// </summary>
        private const int SummaryPreviewTimeoutMs = 90_000;

        private const int RequestTimeoutMs = 5_000;

        private readonly ISettingsHost _host;
        private readonly CoreConfigClient _client;
        private readonly Func<MascotRunner> _runner;

        private readonly SettingsContext _context = new SettingsContext();

        /// <summary>項目ごとの一時的なメッセージ（テスト要約の結果、拒否の理由など）</summary>
        private readonly Dictionary<string, string> _notices = new Dictionary<string, string>();

        /// <summary>デバウンス待ちの core への変更。key は core の設定キー</summary>
        private readonly Dictionary<string, JToken> _pending = new Dictionary<string, JToken>();

        private float _pendingAt = float.PositiveInfinity;
        private bool _refreshing;
        private bool _open;

        public SettingsPanelBridge(ISettingsHost host, string serverUrl, Func<MascotRunner> runner)
        {
            _host = host;
            _runner = runner;
            _client = new CoreConfigClient(ServerUrl.ToHttpBase(serverUrl), RequestTimeoutMs);

            _context.ProductName = Application.productName;
            _context.Version = Application.version;
            _context.LicenseText = ReadLicense();
        }

        public bool IsVisible
        {
            get { return ChatterMascotNative.IsAvailable && ChatterMascotNative.CM_SettingsPanelIsVisible(); }
        }

        /// <summary>
        /// 開いていれば閉じ、閉じていれば開く。
        ///
        /// ★ <b>自前で「開いているか」を覚えないこと。</b> ユーザーは赤いボタンでも閉じられるので、
        ///   自前の記憶とネイティブの実際がずれる（症状は「1回目のクリックが効かない」）。
        /// </summary>
        public void Toggle()
        {
            if (IsVisible) Close();
            else Open();
        }

        public void Open()
        {
            if (!ChatterMascotNative.IsAvailable)
            {
                Debug.LogWarning("[Mascot] ネイティブプラグインが無いので設定パネルを開けません");
                return;
            }

            _open = true;
            Push(update: false);

            // ★ 開いた「後」に取りに行く。繋がらないサーバーを待つ間パネルが出ないと、
            //   ユーザーからは「押しても何も起きない」に見える
            _ = RefreshFromCoreAsync();
        }

        public void Close()
        {
            _open = false;
            if (ChatterMascotNative.IsAvailable) ChatterMascotNative.CM_SettingsPanelHide();
        }

        /// <summary>毎フレーム呼ぶ。デバウンスの締め切りだけを見る</summary>
        public void Tick()
        {
            if (_pending.Count == 0) return;
            if (Time.realtimeSinceStartup < _pendingAt) return;

            var batch = new List<KeyValuePair<string, JToken>>(_pending);
            _pending.Clear();
            _pendingAt = float.PositiveInfinity;

            foreach (var entry in batch) _ = PatchAsync(entry.Key, entry.Value);
        }

        /// <summary>設定ファイルが外から書き換わったときなど、表示を作り直す</summary>
        public void Refresh()
        {
            if (!_open || !IsVisible) return;
            Push(update: true);
        }

        // ── 変更の振り分け ─────────────────────────────────────

        /// <summary>
        /// パネルの項目が操作された。<b>キーの意味を知っているのはここだけ</b>
        /// （<c>StatusItemBridge.Invoke</c> と同じ役回り）。
        /// </summary>
        public void HandleSetting(string key, string value)
        {
            var settings = _host.Settings;

            switch (key)
            {
                // ── Unity 側 ──────────────────────────────
                case SettingKeys.Scale:
                    _host.ApplySettings(settings.WithCharacterScale(SettingsMapping.Normalize(
                        SettingsMapping.Parse(value, settings.CharacterScale),
                        SettingsMapping.ScaleMin, SettingsMapping.ScaleMax, SettingsMapping.ScaleStep)));
                    return;

                case SettingKeys.Volume:
                    _host.ApplySettings(settings.WithVolume(SettingsMapping.Normalize(
                        SettingsMapping.Parse(value, settings.Volume),
                        SettingsMapping.VolumeMin, SettingsMapping.VolumeMax, SettingsMapping.VolumeStep)));
                    return;

                case SettingKeys.IdleMotion:
                    _host.ApplySettings(settings.WithIdleMotion(SettingsPanelJson.ParseBool(value, settings.IdleMotion)));
                    return;

                case SettingKeys.CursorGaze:
                    _host.ApplySettings(settings.WithCursorGaze(SettingsPanelJson.ParseBool(value, settings.CursorGaze)));
                    return;

                case SettingKeys.Blink:
                    _host.ApplySettings(settings.WithBlink(SettingsPanelJson.ParseBool(value, settings.Blink)));
                    return;

                case SettingKeys.Mute:
                    _host.ApplySettings(settings.WithMuted(SettingsPanelJson.ParseBool(value, settings.Muted)));
                    return;

                case SettingKeys.MuteHotKey:
                    ApplyHotKey(key, value, spec => _host.ApplySettings(_host.Settings.WithMuteHotKey(spec)));
                    return;

                case SettingKeys.HideHotKey:
                    ApplyHotKey(key, value, spec => _host.ApplySettings(_host.Settings.WithHideHotKey(spec)));
                    return;

                case SettingKeys.Vrm:
                    ChooseVrm();
                    return;

                // ── core 側 ───────────────────────────────
                case SettingKeys.Speaker:
                {
                    int id;
                    if (!SettingsPanelJson.TryParseInt(value, out id))
                    {
                        Notice(key, "話者 ID を読めませんでした");
                        return;
                    }
                    _context.SpeakerId = value;
                    Queue(CoreConfigKeys.SpeakerId, id, key);
                    return;
                }

                case SettingKeys.Speed:
                {
                    var speed = SettingsMapping.Normalize(
                        SettingsMapping.Parse(value, _context.SpeedScale),
                        SettingsMapping.SpeedMin, SettingsMapping.SpeedMax, SettingsMapping.SpeedStep);
                    _context.SpeedScale = speed;
                    Queue(CoreConfigKeys.SpeedScale, speed, key);
                    return;
                }

                case SettingKeys.SummaryEnabled:
                {
                    var enabled = SettingsPanelJson.ParseBool(value, _context.SummaryEnabled);
                    _context.SummaryEnabled = enabled;
                    Queue(CoreConfigKeys.SummaryEnabled, enabled, key);
                    return;
                }

                // ── 押すだけ ──────────────────────────────
                case SettingKeys.TtsPreview:
                    _ = TtsPreviewAsync();
                    return;

                case SettingKeys.SummaryPreview:
                    Notice(key, "実行中です…");
                    Push(update: true);
                    _ = SummaryPreviewAsync();
                    return;

                case SettingKeys.ResetPosition:
                    _host.ResetWindow();
                    Notice(key, "位置と大きさを既定に戻しました");
                    Push(update: true);
                    return;

                case SettingKeys.ResetAll:
                    _host.ResetAll();
                    Push(update: true);
                    return;

                case SettingKeys.Quit:
                    _host.Quit();
                    return;

                case SettingKeys.Version:
                case SettingKeys.License:
                    // 読むだけの項目。届いたら黙って捨てる
                    return;

                default:
                    Debug.LogWarning($"[Mascot] 知らない設定のキーです: \"{key}\"");
                    return;
            }
        }

        private void ApplyHotKey(string key, string recorded, Action<string> apply)
        {
            HotKeySpec spec;
            string error;
            if (!HotKeySpec.TryParseRecorded(recorded, out spec, out error))
            {
                Notice(key, error);
                Push(update: true);
                return;
            }

            Notice(key, null);
            // ★ 保存するのは正規化した文字列（ctrl+opt+m）。画面に出るのは記号（⌃⌥M）
            apply(spec.Format());
        }

        // ── core とのやり取り ──────────────────────────────────

        /// <summary>
        /// core への変更を溜める。
        ///
        /// ★ <b>スライダーの1操作で何度も PATCH しないこと。</b> ネイティブ側は
        ///   ドラッグ中を投げないようにしてあるが、矢印キーの連打はそのまま届く。
        ///   ここが本命の間引き（→ <c>docs/protocol.md</c> の「制御 API」）。
        /// </summary>
        private void Queue(string coreKey, JToken value, string uiKey)
        {
            _pending[coreKey] = value;
            _pendingAt = Time.realtimeSinceStartup + PatchDebounceSeconds;
            Notice(uiKey, null);
        }

        private async Task PatchAsync(string coreKey, JToken value)
        {
            try
            {
                var result = await _client.PatchConfigAsync(coreKey, value);
                if (!result.Ok)
                {
                    Notice(UiKeyOf(coreKey), result.Reason);
                    Debug.LogWarning($"[Mascot] 設定を送れませんでした（{coreKey}）: {result.Reason}");
                    // ★ 失敗したら core の値を取り直す。画面だけ新しい値のまま残ると
                    //   「変えたつもりが効いていない」に気づけない
                    await RefreshFromCoreAsync();
                    return;
                }
                ReadConfig(result.Body);
                Push(update: true);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Mascot] 設定の送信で例外: " + e.Message);
            }
        }

        private async Task RefreshFromCoreAsync()
        {
            if (_refreshing) return;
            _refreshing = true;
            try
            {
                var config = await _client.ConfigAsync();
                _context.CoreReachable = config.Ok;
                if (!config.Ok)
                {
                    _context.CoreNote = config.Reason ?? "サーバーに繋がりません";
                    _context.Speakers = new SettingChoice[0];
                    Push(update: true);
                    return;
                }
                ReadConfig(config.Body);

                var speakers = await _client.SpeakersAsync();
                // ★ 話者が取れなくても項目は消さない。空の候補 + note で出す
                _context.Speakers = speakers.Ok
                    ? CoreConfigClient.ReadSpeakers(speakers.Body)
                    : new List<SettingChoice>();

                Push(update: true);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Mascot] 設定の取得で例外: " + e.Message);
            }
            finally
            {
                _refreshing = false;
            }
        }

        private void ReadConfig(JToken body)
        {
            var root = body as JObject;
            if (root == null) return;

            var values = root["values"] as JObject;
            if (values != null)
            {
                var speaker = values[CoreConfigKeys.SpeakerId];
                if (speaker != null) _context.SpeakerId = speaker.ToString();

                var speed = values[CoreConfigKeys.SpeedScale];
                if (speed != null) _context.SpeedScale = SettingsMapping.Parse(speed.ToString(), 1f);

                var summary = values[CoreConfigKeys.SummaryEnabled];
                if (summary != null) _context.SummaryEnabled = summary.Type == JTokenType.Boolean && summary.Value<bool>();
            }

            var origins = root["origins"] as JObject;
            var overridden = new List<string>();
            if (origins != null)
            {
                foreach (var entry in origins)
                {
                    if (entry.Value != null && entry.Value.ToString() == "env") overridden.Add(entry.Key);
                }
            }
            _context.CoreEnvOverridden = overridden;
            _context.CoreReachable = true;
        }

        private async Task TtsPreviewAsync()
        {
            try
            {
                var result = await _client.TtsPreviewAsync();
                if (!result.Ok)
                {
                    Notice(SettingKeys.TtsPreview, result.Reason);
                    Push(update: true);
                    return;
                }

                var runner = _runner != null ? _runner() : null;
                if (runner == null)
                {
                    Notice(SettingKeys.TtsPreview, "再生の準備ができていません");
                    Push(update: true);
                    return;
                }

                // ★ ミュート中は通常の再生経路が「声だけ消す」ので鳴らない。理由を出す
                if (_host.Settings.Muted)
                {
                    Notice(SettingKeys.TtsPreview, "ミュート中なので鳴りません");
                    Push(update: true);
                    return;
                }

                Notice(SettingKeys.TtsPreview, null);
                var error = await runner.PlayPreviewAsync(result.Bytes);
                if (!string.IsNullOrEmpty(error))
                {
                    Notice(SettingKeys.TtsPreview, error);
                    Push(update: true);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Mascot] テスト音声で例外: " + e.Message);
            }
        }

        private async Task SummaryPreviewAsync()
        {
            try
            {
                var result = await _client.SummaryPreviewAsync(SummaryPreviewTimeoutMs);
                if (!result.Ok)
                {
                    Notice(SettingKeys.SummaryPreview, result.Reason);
                    Push(update: true);
                    return;
                }

                var root = result.Body as JObject;
                var outcome = root != null && root["outcome"] != null ? root["outcome"].ToString() : "";
                var summary = root != null && root["summary"] != null && root["summary"].Type == JTokenType.String
                    ? root["summary"].Value<string>()
                    : null;

                Notice(SettingKeys.SummaryPreview, DescribeSummary(outcome, summary));
                Push(update: true);
            }
            catch (Exception e)
            {
                Notice(SettingKeys.SummaryPreview, "実行できませんでした: " + e.Message);
                Push(update: true);
            }
        }

        /// <summary>
        /// 要約の結果を1行にする。
        ///
        /// ★ <b>「失敗しました」で潰さないこと。</b> 要約が効かない原因は
        ///   「CLI が無い」「時間切れ」「出力が採用できない」で手当てが全部違う。
        /// </summary>
        private static string DescribeSummary(string outcome, string summary)
        {
            switch (outcome)
            {
                case "ok": return "要約できました: " + (summary ?? "");
                case "timeout": return "時間内に終わりませんでした（本番では原文がそのまま読み上げられます）";
                case "no-command": return "要約に使うコマンドが見つかりません（aiSummaryCommand）";
                case "invalid": return "要約が返りましたが、採用できる形ではありませんでした";
                case "overflow": return "出力が大きすぎました";
                case "error": return "要約コマンドが失敗しました";
                case "internal": return "要約を開始できませんでした";
                default: return string.IsNullOrEmpty(outcome) ? "結果を読めませんでした" : outcome;
            }
        }

        // ── VRM の選択 ────────────────────────────────────────

        /// <summary>
        /// VRM を選んで <c>~/.config/chatter-agent/models/</c> にコピーする。
        ///
        /// ★ <b>パスではなくコピーを持つ。</b> 元ファイルを消しても動き続ける。
        /// ★ <b>ファイル名も覚える。</b> <c>models/*.vrm</c> の走査は <c>Ordinal</c> の先頭が
        ///   勝つので、名前を覚えないと2つ目を選んでも反映されない（→ <c>Vrm.AssetPath</c>）。
        /// ★★ <b>反映は次回の起動から。</b> 読み込み済みのモデルを差し替えるには、
        ///   spring bone / コライダ / ドラッグハンドル / 待機モーション / 表情の
        ///   結び直しが要る（<c>VrmStage</c> は起動時に1回だけ読む作りになっている）。
        ///   ここで中途半端に作ると「差し替えたのに一部だけ前のモデルのまま」になるので、
        ///   <b>できないことをできると見せない</b>方を採った。
        /// </summary>
        private void ChooseVrm()
        {
            var settings = new FilePanel.Settings
            {
                title = "VRM モデルを選ぶ",
                filters = new[] { new FilePanel.Filter("VRM", "vrm") },
                flags = FilePanel.Flag.FileMustExist,
            };

            FilePanel.OpenFilePanel(settings, paths =>
            {
                if (paths == null || paths.Length == 0) return;
                var source = paths[0];
                if (string.IsNullOrEmpty(source)) return;

                string name;
                string error;
                if (!TryInstallVrm(source, out name, out error))
                {
                    Notice(SettingKeys.Vrm, error);
                    Push(update: true);
                    return;
                }

                Notice(SettingKeys.Vrm, "次に起動したときから反映されます");
                _host.ApplySettings(_host.Settings.WithVrmFileName(name));
                Push(update: true);
            });
        }

        private bool TryInstallVrm(string source, out string name, out string error)
        {
            name = null;
            error = null;
            try
            {
                var env = AssetEnvFactory.Current();
                var root = AssetPath.RuntimeDirectory(env);
                if (string.IsNullOrEmpty(root))
                {
                    error = "設定の置き場所を決められませんでした";
                    return false;
                }

                var models = Path.Combine(root, "models");
                Directory.CreateDirectory(models);

                var fileName = Path.GetFileName(source);
                if (string.IsNullOrEmpty(fileName))
                {
                    error = "ファイル名を読めませんでした";
                    return false;
                }

                var destination = Path.Combine(models, fileName);
                // ★ 同じ場所を選んだときにコピーしないこと（自分自身への copy は例外になる）
                if (Path.GetFullPath(source) != Path.GetFullPath(destination))
                {
                    File.Copy(source, destination, true);
                }

                name = fileName;
                return true;
            }
            catch (Exception e)
            {
                error = "コピーできませんでした: " + e.Message;
                return false;
            }
        }

        // ── 画面の組み立て ─────────────────────────────────────

        private void Push(bool update)
        {
            if (!ChatterMascotNative.IsAvailable) return;

            _context.Settings = _host.Settings;
            var items = SettingsSchema.Build(_context);
            var withNotices = ApplyNotices(items);
            var json = SettingsPanelJson.Write(_context.ProductName + " の設定", withNotices);

            if (update) ChatterMascotNative.CM_SettingsPanelUpdate(json);
            else if (!ChatterMascotNative.CM_SettingsPanelShow(json))
            {
                Debug.LogWarning("[Native] 設定パネルを開けませんでした");
            }
        }

        /// <summary>
        /// 一時的なメッセージをスキーマへ差し込む。
        ///
        /// ★ <b>スキーマ側に持たせないこと。</b> <see cref="SettingsSchema"/> は
        ///   「いまの状態」から並びを作る純粋関数で、「さっき押した結果」は状態ではない。
        ///   混ぜると、テストで固定したい部分に時間の概念が入る。
        /// </summary>
        private IReadOnlyList<SettingSpec> ApplyNotices(IReadOnlyList<SettingSpec> items)
        {
            if (_notices.Count == 0) return items;

            var result = new List<SettingSpec>(items.Count);
            foreach (var spec in items)
            {
                string notice;
                if (spec.Key != null && _notices.TryGetValue(spec.Key, out notice) && !string.IsNullOrEmpty(notice))
                {
                    result.Add(SettingSpec.WithNote(spec, notice));
                    continue;
                }
                result.Add(spec);
            }
            return result;
        }

        private void Notice(string key, string message)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (string.IsNullOrEmpty(message)) _notices.Remove(key);
            else _notices[key] = message;
        }

        /// <summary>core の設定キー → 画面の項目キー（拒否の理由を出す先）</summary>
        private static string UiKeyOf(string coreKey)
        {
            switch (coreKey)
            {
                case CoreConfigKeys.SpeakerId: return SettingKeys.Speaker;
                case CoreConfigKeys.SpeedScale: return SettingKeys.Speed;
                case CoreConfigKeys.SummaryEnabled: return SettingKeys.SummaryEnabled;
                default: return coreKey;
            }
        }

        /// <summary>
        /// <c>StreamingAssets/NOTICE.txt</c> を読む。
        ///
        /// ★ <b>C# の文字列リテラルに埋め込まないこと。</b> リポジトリの <c>NOTICE</c> と
        ///   別々に更新されて静かにズレる。
        /// ★ <b>読めなくても項目は出す</b>（空のまま）。about が丸ごと消えるより、
        ///   「読めていない」と分かる方がよい。
        /// ★ <c>StreamingAssets</c> はビルド後 <c>.app</c> の中のコピーを読む。
        /// </summary>
        private static string ReadLicense()
        {
            try
            {
                var path = Path.Combine(Application.streamingAssetsPath, "NOTICE.txt");
                if (!File.Exists(path)) return "";
                return File.ReadAllText(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Mascot] NOTICE.txt を読めませんでした: " + e.Message);
                return "";
            }
        }
    }
}
#endif
