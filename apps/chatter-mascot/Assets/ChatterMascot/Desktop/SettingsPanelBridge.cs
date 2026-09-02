#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using ChatterMascot.Desktop.Native;
using ChatterMascot.Net;
using ChatterMascot.Settings;
using ChatterMascot.Ui;
using ChatterMascot.Vrm;
using Newtonsoft.Json;
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

        /// <summary>Unity 側（<c>settings.json</c> と <c>window.json</c>）を既定へ戻す</summary>
        void ResetUnitySettings();

        /// <summary>ウィンドウの大きさを変える（倍率ではなくポイント）</summary>
        void SetWindowSize(float widthPoints, float heightPoints);

        /// <summary>いまのウィンドウの倍率（<c>window.json</c> が権威。→ <c>SettingsMapping.ScaleForWindow</c>）</summary>
        float WindowScale { get; }

        /// <summary>
        /// ウィンドウの大きさが<b>まだ落ち着いていない</b>か（→ <c>WindowGeometry.SetSize</c>）。
        ///
        /// ★★ <b>見送らないと、この PR が「話す速さ」で潰した teardown が「大きさ」で再発する。</b>
        ///   <c>WindowGeometry</c> は書いた値が読み返しで一致するまで最大5回書き直すが、
        ///   その間 <see cref="WindowScale"/> は<b>まだ古い倍率</b>を返す。
        ///   <c>WatchWindowSize</c> がそれを「外から変わった」と誤読すると
        ///   <c>Push(update: true)</c>＝<b>全ビューの破棄</b>に入り、スクロール位置が先頭へ飛び、
        ///   掴み直していればつまみが手の中で死ぬ。
        ///
        /// ★ <b>「押した直後は読まない」をフレーム数の当て推量ではなく状態で表す。</b>
        ///   適用が終われば（一致でも打ち切りでも）実際の矩形が読めるので、
        ///   リセット後の追いつき（<c>WatchWindowSize</c> の本来の仕事）は壊れない。
        /// </summary>
        bool WindowSizeSettling { get; }

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
        /// <summary>
        /// 制御 API の1リクエストの上限。<b>設定の読み書きはファイル I/O だけ</b>なので短くてよい。
        /// </summary>
        private const int RequestTimeoutMs = 5_000;

        /// <summary>
        /// テスト音声だけの予算。
        ///
        /// ★★ <b>他の口と同じ 5秒にしないこと。</b> サーバー側は <c>synthesize</c> の2往復ぶん
        ///   （<c>synthesisTimeoutMs</c> 既定30秒の<b>最悪2倍</b>。→ core の <c>config.ts</c>）粘る。
        ///   短くすると、<b>エンジンは起きているがモデルがまだロードされていない</b>という
        ///   「1文目だけは合成待ちで少し間が空く」場面 —— <b>テスト音声がいちばん効くはずの場面</b>
        ///   —— でだけ <c>UnityWebRequest.timeout</c> が先に発火し、実際には合成中なのに
        ///   「サーバーに繋がりません」（status 0）と出る。#84 で足した
        ///   <c>synthesis_unavailable</c> / <c>tts_disabled</c> の出し分けにも到達しなくなる。
        /// ★ <c>SummaryPreviewAsync</c> が専用の予算を受けているのと同じ理由（→ <c>CoreConfigClient</c>）。
        /// </summary>
        private const int TtsPreviewTimeoutMs = 70_000;

        /// <summary>
        /// ネイティブのパネル ID。<b>同じレンダラで2枚描く</b>（→ <c>CMSettingsPanel.m</c>）。
        ///
        /// ★ ライセンス本文が長いので、about を設定と同じパネルに置くと設定の項目が埋もれる。
        /// </summary>
        private const int SettingsPanelId = 0;

        private const int AboutPanelId = 1;

        private readonly ISettingsHost _host;
        private readonly CoreConfigClient _client;
        private readonly Func<MascotRunner> _runner;

        private readonly SettingsContext _context = new SettingsContext();

        /// <summary>項目ごとの一時的なメッセージ（テスト要約の結果、拒否の理由など）</summary>
        private readonly Dictionary<string, string> _notices = new Dictionary<string, string>();

        /// <summary>
        /// 保留中の変更（core への <c>PATCH</c> / Unity 側 / ウィンドウの倍率）。
        ///
        /// ★ <b>判断は <see cref="PendingChanges"/> が持つ</b>（<c>Runtime</c> 側なので
        ///   EditMode で固定してある）。ここは締め切りに届いたものを実際に適用する配線だけ。
        /// </summary>
        private readonly PendingChanges _pending = new PendingChanges();

        /// <summary>注記を消したが、まだ画面に反映していない（→ <see cref="Queue"/>）</summary>
        private bool _noticesStale;

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
            get
            {
                return ChatterMascotNative.IsAvailable
                    && ChatterMascotNative.CM_PanelIsVisible(SettingsPanelId);
            }
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
            // ★ **捨てる前に投げ切ること。** ここは OnDestroy（シーンのアンロード /
            //   ドメインリロード）からも来る。閉じるだけなら Bridge.Update の Tick() が
            //   着地させるが、その経路では二度と Tick が来ない
            Flush();

            _open = false;
            // ★ 一時的なメッセージは持ち越さないこと。閉じて開き直したときに
            //   「さっき押した結果」が残っていると、いま起きたことと区別が付かない
            _notices.Clear();
            _noticesStale = false;
            if (ChatterMascotNative.IsAvailable) ChatterMascotNative.CM_PanelHide(SettingsPanelId);
        }

        /// <summary>
        /// ネイティブ側で<b>閉じられた</b>（→ <c>CMSettingsPanel.m</c> の <c>windowWillClose:</c>）。
        ///
        /// ★★ <b>赤いボタンで閉じる経路は、これでしか届かない。</b> <see cref="Toggle"/> は
        ///   ネイティブに「見えているか」を聞くので<b>開閉のずれは起きない</b>が、
        ///   <see cref="_open"/> と <see cref="_notices"/> はこちらの持ち物なので、
        ///   閉じたことを知らないと<b>数分前の注記が、開き直したときに
        ///   「いま起きたこと」として出る</b>（→ <see cref="Close"/> の ★）。
        ///
        /// ★ <b><c>CM_PanelHide</c> は呼ばない。</b> もう閉じている。
        /// ★ 「について」でも飛んでくるので、id で弾くこと。
        /// </summary>
        public void NotifyClosed(int panelId)
        {
            if (panelId != SettingsPanelId) return;

            Flush();
            _open = false;
            _notices.Clear();
            _noticesStale = false;
        }

        /// <summary>
        /// 「Chatter Mascot について」を開く。<b>設定とは別のダイアログ</b>。
        ///
        /// ★ ライセンス本文だけで数百行あるので、設定パネルに混ぜると項目が埋もれる。
        /// </summary>
        public void OpenAbout()
        {
            if (!ChatterMascotNative.IsAvailable)
            {
                Debug.LogWarning("[Mascot] ネイティブプラグインが無いので「について」を開けません");
                return;
            }
            var json = SettingsPanelJson.Write(
                _context.ProductName + " について", SettingsSchema.BuildAbout(_context));
            if (!ChatterMascotNative.CM_PanelShow(AboutPanelId, json))
            {
                Debug.LogWarning("[Native]「について」を開けませんでした");
            }
        }

        /// <summary>
        /// 毎フレーム呼ぶ。デバウンスの締め切りだけを見る。
        ///
        /// ★ core への <c>PATCH</c>・Unity 側の保存・ウィンドウのリサイズを<b>同じ締め切りに
        ///   まとめてある</b>。別々に持つと、1回のスライダー操作で締め切りが3つ走る。
        /// </summary>
        public void Tick()
        {
            WatchWindowSize();
            if (!_pending.Due(Time.realtimeSinceStartup)) return;
            Flush();
        }

        /// <summary>
        /// 保留を締め切りを待たずに適用する。
        ///
        /// ★★ <b>終了の手前で呼ぶこと。</b> <c>Quit</c> は <c>Application.Quit()</c> を
        ///   先に走らせるので、呼ばないと締め切りが来ないまま終わる ——
        ///   音量は古いまま / <c>PATCH</c> は飛ばない / ウィンドウは変わらない。
        ///   <b>閉じるだけなら失われない</b>（<c>Bridge.Update</c> が <c>Tick()</c> を
        ///   無条件に呼ぶので、見えていなくても締め切りは着地する）。落ちるのは
        ///   <c>Quit</c> と <c>OnDestroy</c> → <see cref="Close"/> の2経路だけ。
        ///
        /// ★ <b>即時適用もここを通す</b>（→ <see cref="Apply"/>）。別経路にすると権威が2つになる。
        ///
        /// ★ <c>PATCH</c> は非同期なので、<c>Quit</c> では<b>投げるところまで</b>しか保証できない
        ///   （Unity 側の <c>settings.json</c> とウィンドウは同期で確定する）。実際には
        ///   <c>wantsToQuit</c> が ack を投げ切るぶん終了が遅れるので、そこで着地することが多い。
        /// </summary>
        public void Flush()
        {
            if (_pending.IsEmpty) return;

            MascotSettings? settings;
            float? scale;
            List<KeyValuePair<string, JToken>> batch;
            _pending.Take(out settings, out scale, out batch);

            if (settings != null) _host.ApplySettings(settings.Value);
            if (scale != null)
            {
                float width;
                float height;
                SettingsMapping.WindowSizeFor(
                    scale.Value, WindowGeometry.DefaultWidthPoints, WindowGeometry.DefaultHeightPoints,
                    out width, out height);
                _host.SetWindowSize(width, height);
            }
            foreach (var entry in batch) _ = PatchAsync(entry.Key, entry.Value);
        }

        /// <summary>
        /// ウィンドウの大きさが<b>外から</b>変わったら「大きさ」を追いつかせる。
        ///
        /// ★★ <b>「リセットした直後に読む」では足りない。</b> <c>ResetWindow()</c> の反映は
        ///   数フレームかかる（<c>WindowGeometry</c> は位置と大きさを何度か書き直して追従する）ので、
        ///   押した直後の <c>WindowScale</c> は<b>まだ古い倍率</b>を返す。実機で
        ///   「位置と大きさをリセットしたのにスライダーが 1.5 のまま」を踏んだ。
        ///
        /// ★ <b>窓の端を掴んでリサイズしたときにも同じ経路で追いつく。</b> 大きさの権威は
        ///   <c>window.json</c>（＝ウィンドウそのもの）で、スライダーはその写しでしかない
        ///   （→ <c>SettingsContext.WindowScale</c>）。
        ///
        /// ★ <b>保留中は見送る。</b> つまみをドラッグしている最中に作り直すと、
        ///   掴んでいるスライダーごと消える（→ <see cref="Push"/>）。
        ///
        /// ★★ <b>適用中も見送る</b>（<c>ISettingsHost.WindowSizeSettling</c>）。保留を出した
        ///   <b>直後</b>が抜けていた —— <c>SetWindowSize</c> は窓に書くだけで、
        ///   <c>WindowScale</c> が新しい値を返すのは数フレーム後。その間ここが
        ///   「外から変わった」と誤読して、<b>「話す速さ」で潰したはずの teardown を
        ///   「大きさ」で再発させていた</b>（#85 レビュー A-2）。
        /// </summary>
        private void WatchWindowSize()
        {
            if (!_open || _pending.HasScale || _host.WindowSizeSettling) return;

            var scale = _host.WindowScale;
            if (Mathf.Abs(scale - _context.WindowScale) < 0.001f) return;

            _context.WindowScale = scale;
            Push(update: true);
        }

        /// <summary>Unity 側の変更を保留する（→ <see cref="Tick"/>）</summary>
        private void Defer(MascotSettings next)
        {
            _pending.Defer(next, Time.realtimeSinceStartup);
        }

        /// <summary>
        /// Unity 側の変更を<b>その場で</b>確定する（チェックボックス・ショートカット・VRM）。
        ///
        /// ★ 連打されるものではないし、遅らせると「押したのに効いていない」に見える。
        ///
        /// ★★ <b><c>_host.ApplySettings</c> を直接呼ばないこと。</b> 保留を素通りすると、
        ///   同じ 300ms の窓に居るデバウンス中の変更が<b>あとから巻き戻す</b>
        ///   （→ <see cref="PendingChanges"/> の ★★）。<b>権威は1つ</b>。
        /// </summary>
        private void Apply(MascotSettings next)
        {
            Defer(next);
            Flush();
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
            // ★★ **保留があるならそこから積むこと**（→ PendingChanges の ★★）。
            //   `_host.Settings` から始めると、デバウンス中の保留が、ここで確定した
            //   別の項目を**あとから巻き戻す**（症状は「外れて見えるのに実際はオン」）
            var settings = _pending.Base(_host.Settings);

            switch (key)
            {
                // ── Unity 側 ──────────────────────────────
                //
                // ★★ ここから **パネルを作り直さないこと**。画面には既に新しい値が出ている。
                //   作り直すと、ドラッグ中のスライダーごとビューが破棄されて
                //   **つまみが掴めなくなる**（実機で踏んだ）。作り直すのは
                //   「外から変わったとき」だけ（→ Refresh）。

                case SettingKeys.Scale:
                {
                    // ★ 大きさは settings.json に持たない。**ウィンドウそのものを変える**
                    //   （→ SettingsMapping.WindowSizeFor / MascotSettings の型 doc）
                    var scale = SettingsMapping.Normalize(
                        SettingsMapping.Parse(value, _context.WindowScale),
                        SettingsMapping.ScaleMin, SettingsMapping.ScaleMax, SettingsMapping.ScaleStep);
                    _pending.DeferScale(scale, Time.realtimeSinceStartup);
                    _context.WindowScale = scale;
                    return;
                }

                case SettingKeys.Volume:
                    // ★ スライダーなのでデバウンスする（1ティックごとに保存しない）
                    Defer(settings.WithVolume(SettingsMapping.Normalize(
                        SettingsMapping.Parse(value, settings.Volume),
                        SettingsMapping.VolumeMin, SettingsMapping.VolumeMax, SettingsMapping.VolumeStep)));
                    return;

                // ★ チェックボックスは即座に反映する。連打されるものではないし、
                //   遅らせると「押したのに効いていない」に見える
                // ★★ ただし **Apply を通すこと**（＝保留に載せてすぐ flush）。
                //   `_host.ApplySettings` を直接呼ぶと権威が2つになる（→ Apply の ★★）
                case SettingKeys.IdleMotion:
                    Apply(settings.WithIdleMotion(SettingsPanelJson.ParseBool(value, settings.IdleMotion)));
                    return;

                case SettingKeys.CursorGaze:
                    Apply(settings.WithCursorGaze(SettingsPanelJson.ParseBool(value, settings.CursorGaze)));
                    return;

                case SettingKeys.Blink:
                    Apply(settings.WithBlink(SettingsPanelJson.ParseBool(value, settings.Blink)));
                    return;

                case SettingKeys.MuteHotKey:
                    // ★ 起点は `settings`（＝保留があるならそれ）。`_host.Settings` を
                    //   読み直すと、同じ窓に居る保留を巻き戻す（→ 上の ★★）
                    ApplyHotKey(key, value, spec => Apply(settings.WithMuteHotKey(spec)));
                    return;

                case SettingKeys.HideHotKey:
                    ApplyHotKey(key, value, spec => Apply(settings.WithHideHotKey(spec)));
                    return;

                case SettingKeys.Vrm:
                    ChooseVrm();
                    return;

                case SettingKeys.VrmChosen:
                    InstallVrm(value);
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

                case SettingKeys.ResetPosition:
                    // ★★ **保留の倍率を捨ててから戻すこと。** 残すと、既定へ戻した**後ろ**に
                    //   古い倍率が着地する（レビューは挙げていないが A-3 / A-4 と同型）。
                    //   ★ Clear() にはしない —— 戻すのは窓だけなので、同じ窓に居る
                    //     音量や話す速さまで道連れにしない
                    _pending.ClearScale();
                    _host.ResetWindow();
                    // ★ ここで WindowScale を読まないこと。反映は数フレーム遅れるので
                    //   まだ古い倍率が返る。スライダーは WatchWindowSize が追いつかせる
                    Notice(key, "位置と大きさを既定に戻しました");
                    Push(update: true);
                    return;

                case SettingKeys.ResetAll:
                    _ = ResetAllAsync();
                    return;

                case SettingKeys.Quit:
                    // ★★ **保留を投げ切ってから終わること。** Application.Quit() が先に走ると
                    //   Tick() の締め切りが二度と来ない（#85 レビュー A-4）
                    Flush();
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

            var cleared = Notice(key, null);
            // ★ 保存するのは正規化した文字列（ctrl+opt+m）。画面に出るのは記号（⌃⌥M）
            apply(spec.Format());

            // ★★ **消えたなら作り直すこと。** ネイティブは値のラベルしか自分で更新しないので、
            //   Push しないと注記が残る —— 修飾キー無しで押して怒られた直後に ⌃⌥J を正しく
            //   記録すると、**⌃⌥J の下にさっきのエラー文が残ったまま**になる（#85 レビュー C-1）。
            //   ★ ここで作り直してよいのは、記録がもう終わっているから（掴んでいるスライダーは無い）
            if (cleared) Push(update: true);
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
            _pending.Queue(coreKey, value, Time.realtimeSinceStartup);

            // ★ ここでは作り直さない（ドラッグ中かもしれない）。前回の失敗の注記が
            //   残っていたら、成功したあとの Push で消す（→ PatchAsync）
            if (Notice(uiKey, null)) _noticesStale = true;
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

                    // ★★ **描画を下の refresh に任せないこと。** `Tick()` は溜まった batch を
                    //   並行に投げるので、300ms 以内に2項目を触って**両方が落ちる**と、
                    //   2件目の `RefreshFromCoreAsync` は `_refreshing` で即 return して
                    //   Push が走らない。失敗が5秒のタイムアウトだと、**その5秒間どちらの変更が
                    //   拒否されたのか画面に何も出ない**（#85 レビュー C-3）。
                    //   ★ 失敗枝はもともと作り直す経路なので、「自分起点で作り直さない」とは衝突しない
                    Push(update: true);

                    // ★ 失敗したら core の値を取り直す。画面だけ新しい値のまま残ると
                    //   「変えたつもりが効いていない」に気づけない
                    await RefreshFromCoreAsync();
                    return;
                }
                // ★★ **成功しただけで作り直さないこと。** `CMApplySchema` は全ビューを捨てて
                //   組み直すので、**掴んでいるスライダーごと消える**。PATCH はスライダーを
                //   離した 300ms 後（＋往復）に着地するので、続けてもう一度掴んだ人の手の中で
                //   つまみが死ぬ ——「話す速さだけドラッグできない」という形で出た
                //   （音量と大きさは Unity 側で完結するので同じ経路を通らない）。
                //
                // ★ 画面には既に新しい値が出ている（ネイティブが `%g` で追従させている）。
                //   作り直すのは **core が画面と違う値を返したとき**だけ ——
                //   丸め直し・env での固定・繋がるようになった、のどれか。
                var before = CoreSnapshot();
                ReadConfig(result.Body);
                if (_noticesStale || before != CoreSnapshot())
                {
                    _noticesStale = false;
                    Push(update: true);
                }
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

        /// <summary>
        /// core 由来の値のスナップショット。<b>PATCH の応答が画面と食い違ったか</b>を
        /// 見るためだけに使う（→ <see cref="PatchAsync"/>）。
        ///
        /// ★ <c>SettingsSchema</c> が core から読む値を漏れなく並べること。
        ///   ここに載せ忘れたものは「変わったのに画面が古いまま」になる。
        /// </summary>
        private string CoreSnapshot()
        {
            return string.Join("\u001f", new[]
            {
                _context.SpeakerId ?? string.Empty,
                _context.SpeedScale.ToString("R", CultureInfo.InvariantCulture),
                _context.SummaryEnabled ? "1" : "0",
                _context.CoreReachable ? "1" : "0",
                _context.CoreNote ?? string.Empty,
                _context.CoreEnvOverridden == null ? "" : string.Join(",", _context.CoreEnvOverridden),
            });
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
                var result = await _client.TtsPreviewAsync(TtsPreviewTimeoutMs);
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

        /// <summary>
        /// すべての設定を既定へ戻す。
        ///
        /// ★★ <b>取り消せない</b>（<c>models/</c> の <c>.vrm</c> を消す）ので、必ず確認を取る。
        /// ★ <b>core のぶんは <c>PATCH</c> で戻す。</b> 既定値はサーバーが返す
        ///   （<c>GET /v1/config</c> の <c>defaults</c>）—— <b>C# に書き写さないこと</b>。
        ///   写した瞬間に「core を直したのにこちらだけ古い既定に戻す」がありうる。
        /// ★ <b>サーバーに繋がらないときは、戻らなかったものを言うこと。</b>
        ///   黙って半分だけ戻すのがいちばん悪い。
        /// </summary>
        private async Task ResetAllAsync()
        {
            if (!Confirm()) return;

            // ★★ **保留を捨ててから戻すこと。** 残すと、既定へ戻した**後ろ**に古い値が
            //   着地する（→ ResetPosition と同型。こちらは全部戻すので Clear でよい）
            _pending.Clear();

            // 1. Unity 側（settings.json + window.json）
            _host.ResetUnitySettings();
            _context.WindowScale = _host.WindowScale;

            // 2. 選んだモデルのファイル
            string modelsError;
            var removedModels = TryRemoveModels(out modelsError);

            // 3. core 側
            var coreError = await ResetCoreAsync();

            var message = "既定に戻しました";
            if (removedModels > 0) message += $"（モデル {removedModels} 件を削除）";
            if (!string.IsNullOrEmpty(modelsError)) message += " / " + modelsError;
            if (!string.IsNullOrEmpty(coreError)) message += " / " + coreError;
            Notice(SettingKeys.ResetAll, message);

            await RefreshFromCoreAsync();
            Push(update: true);
        }

        private bool Confirm()
        {
            if (!ChatterMascotNative.IsAvailable) return false;

            var options = new JObject
            {
                ["title"] = "すべての設定をリセットしますか？",
                ["message"] =
                    "大きさ・位置・音量・モーション・ショートカット・音声スタイル・話す速さ・要約の設定が既定に戻り、"
                    + "選んだ VRM モデルのファイルも削除されます。この操作は取り消せません。",
                ["ok"] = "リセットする",
                ["cancel"] = "やめる",
                ["destructive"] = true,
            };
            return ChatterMascotNative.CM_Confirm(options.ToString(Formatting.None));
        }

        /// <summary>
        /// <c>models/</c> の <c>.vrm</c> を消す。消した件数を返す。
        ///
        /// ★ <b>ディレクトリごと消さないこと。</b> <c>animations/</c> と同じ親を共有していないとはいえ、
        ///   ユーザーが置いた別のものが同居している可能性がある。拡張子で絞る。
        /// </summary>
        private static int TryRemoveModels(out string error)
        {
            error = null;
            var removed = 0;
            try
            {
                var root = AssetPath.RuntimeDirectory(AssetEnvFactory.Current());
                if (string.IsNullOrEmpty(root)) return 0;

                var models = Path.Combine(root, "models");
                if (!Directory.Exists(models)) return 0;

                foreach (var file in Directory.GetFiles(models, "*.vrm"))
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch (Exception e)
            {
                error = "モデルを消せませんでした: " + e.Message;
            }
            return removed;
        }

        /// <summary>core 側の3つを既定へ。戻せなかったら理由を返す</summary>
        private async Task<string> ResetCoreAsync()
        {
            var config = await _client.ConfigAsync();
            if (!config.Ok) return "音声スタイル・話す速さ・要約は戻せませんでした（" + config.Reason + "）";

            var root = config.Body as JObject;
            var defaults = root != null ? root["defaults"] as JObject : null;
            if (defaults == null) return "音声スタイル・話す速さ・要約は戻せませんでした（既定値を取れません）";

            foreach (var key in new[]
                     { CoreConfigKeys.SpeakerId, CoreConfigKeys.SpeedScale, CoreConfigKeys.SummaryEnabled })
            {
                var value = defaults[key];
                if (value == null) continue;
                var result = await _client.PatchConfigAsync(key, value);
                // ★ 環境変数で固定されているキーは 409。**失敗ではない**ので、そこで止めない
                if (!result.Ok && result.Status != 409) return key + " を戻せませんでした（" + result.Reason + "）";
            }
            return null;
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
            if (!ChatterMascotNative.IsAvailable)
            {
                Notice(SettingKeys.Vrm, "ネイティブプラグインが無いのでファイルを選べません");
                Push(update: true);
                return;
            }

            // ★★ UniWindowController の FilePanel を使わないこと。 あちらは
            //   NSOpenPanel の allowedContentTypes に UTType(tag:"vrm") を渡すが、
            //   .vrm はシステム登録の UTI を持たないので dynamic UTI になり、
            //   **拡張子が一致してもグレーアウトする**（実機で踏み、バイナリで確認した）
            var options = new JObject
            {
                ["key"] = SettingKeys.VrmChosen,
                ["title"] = "VRM モデルを選ぶ",
                ["message"] = "選んだファイルは models/ にコピーされます",
                ["button"] = "選ぶ",
                // ★ 拡張子は C# が持つ（ネイティブに "vrm" を書かない）
                ["extensions"] = new JArray("vrm"),
            };

            // ★ 取り消しは何も返らない（false）。エラーではないので何も出さない
            ChatterMascotNative.CM_OpenFilePanel(options.ToString(Formatting.None));
        }

        /// <summary>ネイティブのファイル選択で選ばれたパスを受ける</summary>
        private void InstallVrm(string source)
        {
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
            Apply(_pending.Base(_host.Settings).WithVrmFileName(name));
            // ★ ここは作り直す。選んだモデル名は note に出るので、画面が自分で追いつけない
            Push(update: true);
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

                // ★★ **固定名で上書きすること。** 選んだファイルの名前をそのまま使うと、
                //   選び直すたびに models/ に積み上がって消す責任が誰にも無くなる
                //   （実機で「元からあったファイルが残る」と報告された）。
                //   ★ 返す `name` は**元のファイル名**。画面に出すためだけに覚える
                //     （探索は固定名で行う。→ AssetPath.SelectedVrmFile）
                var destination = Path.Combine(models, AssetPath.SelectedVrmFile);
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
            // ★ 大きさは settings.json ではなく**いまの窓**から出す（権威は window.json）
            _context.WindowScale = _host.WindowScale;
            var items = SettingsSchema.Build(_context);
            var withNotices = ApplyNotices(items);
            var json = SettingsPanelJson.Write(_context.ProductName + " の設定", withNotices);

            if (update) ChatterMascotNative.CM_PanelUpdate(SettingsPanelId, json);
            else if (!ChatterMascotNative.CM_PanelShow(SettingsPanelId, json))
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

        /// <returns>注記が実際に変わったか。<b>変わったなら Push が要る</b>（ネイティブは
        /// 値のラベルしか自分で更新しないので、注記は作り直さないと消えない）</returns>
        private bool Notice(string key, string message)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (string.IsNullOrEmpty(message)) return _notices.Remove(key);

            string current;
            if (_notices.TryGetValue(key, out current) && current == message) return false;
            _notices[key] = message;
            return true;
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
