#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using ChatterMascot.Desktop.Native;
using ChatterMascot.Settings;
using ChatterMascot.Ui;
using ChatterMascot.Vrm;
using Kirurobo;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ChatterMascot.Desktop
{
    /// <summary>
    /// メニューバー常駐（#75）の配線。<b>判断はすべて <c>ChatterMascot.Runtime</c> 側にある</b>
    /// （<c>MascotMenu</c> / <c>MenuJson</c> / <c>HotKeySpec</c> / <c>SettingsStore</c>）ので、
    /// ここは<b>ネイティブとの受け渡しと、押されたときに何をするか</b>だけを持つ。
    ///
    /// ★ <b><c>MonoBehaviour</c> をシーンに置かない。</b> <c>ChatterMascot.Desktop</c> は
    ///   Android を含まないアセンブリなので、シーンに置くと Android で
    ///   「referenced script is missing」になる。<c>WindowGeometry</c> / <c>DragStateGuard</c> と
    ///   同じく自分で生やす（→ <c>docs/mascot.md</c>）。
    /// </summary>
    public static class StatusItemBridge
    {
        private const string SettingsDirectory = "mascot";
        private const string SettingsFile = "settings.json";

        /// <summary>アイコン。<c>StreamingAssets</c> はビルド後 <c>.app</c> 内のコピーを読む</summary>
        private const string Icon1xFile = "trayTemplate.png";
        private const string Icon2xFile = "trayTemplate@2x.png";

        /// <summary>ミュートのショートカットに割り当てる id（ネイティブには数値しか渡さない）</summary>
        private const int MuteHotKeyId = 1;

        /// <summary>
        /// ★ <b><c>static readonly</c> で保持すること。</b>
        ///   <c>CM_SetEventCallback(OnNativeEvent)</c> と直接書くと、暗黙に作られたデリゲートが
        ///   GC され、症状は<b>「しばらく使っていると SIGSEGV」</b>になる。
        /// </summary>
        private static readonly NativeEventCallback Callback = OnNativeEvent;

        /// <summary>
        /// ★ <b>コールバックの中から Unity API を呼ばない。</b> AppKit の menu action と
        ///   Carbon の hotkey handler は<b>Unity のプレイヤーループの外</b>
        ///   （メニュー追跡中のネストした run loop）で発火しうる。積むだけにして
        ///   <c>Update</c> で drain する（<c>AfplaySpeechPlayer</c> が
        ///   <c>Process.Exited</c> を使わないのと同じ判断）。
        /// </summary>
        private static readonly ConcurrentQueue<string> Events = new ConcurrentQueue<string>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            // ★ Editor では動かさない。 Unity Editor のメニューバーにアイコンが増え、
            //   終了もホットキーも Editor を巻き込む
            if (Application.isEditor) return;

            // ★★ プラグインの有無より先に出すこと。 Dock に出ない以上、
            //   「どれが動いているか」はこのログとツールチップでしか分からない
            //   （→ #75 の LSUIElement の代償4）。バンドルを作り忘れた起動でこそ
            //   切り分けが要るので、IsAvailable の後ろに置いてはいけない
            Debug.Log($"[Mascot] pid={Process.GetCurrentProcess().Id} bundlePath={Application.dataPath}");

            if (!ChatterMascotNative.IsAvailable) return;

            var go = new GameObject(nameof(StatusItemBridge)) { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<Bridge>();
        }

        /// <summary>
        /// <c>{XDG_CONFIG_HOME:-~/.config}/chatter-agent/mascot/settings.json</c>。
        /// ★ <c>window.json</c> と同じディレクトリ（ユーザーから見た「設定はここ1箇所」を崩さない）。
        /// </summary>
        internal static string ResolveSettingsPath()
        {
            var root = AssetPath.RuntimeDirectory(AssetEnvFactory.Current());
            if (string.IsNullOrEmpty(root)) return null;
            return Path.Combine(root, SettingsDirectory, SettingsFile);
        }

        [AOT.MonoPInvokeCallback(typeof(NativeEventCallback))]
        private static void OnNativeEvent(string json)
        {
            // ★ managed 例外をネイティブのスタックへ抜かないこと
            try
            {
                Events.Enqueue(json);
            }
            catch (Exception)
            {
                // ここで Debug.Log すら呼べない（Unity API）。落とさないことだけが仕事
            }
        }

        private sealed class Bridge : MonoBehaviour
        {
            /// <summary>設定ファイルの更新を見る間隔。★ 毎フレーム stat しないこと</summary>
            private const float SettingsPollSeconds = 1f;

            /// <summary>
            /// Dock 非表示の念押しを入れるまで。
            ///
            /// ★ <b>本命は <c>Info.plist</c> の <c>LSUIElement</c></b>（<c>MacPostBuild</c>）。
            ///   ここは<b>plist の書き換えが失敗したときの保険</b>で、既に accessory なら何も起きない。
            /// ★ <b>0 にしないこと。</b> cc-mascot は <c>app.dock.hide()</c> を起動直後に呼ぶと
            ///   <b>フルスクリーンの Space で起動してしまう</b>ため 500ms 遅らせている。
            ///   同じ轍を踏まないよう、こちらも間を置く。
            /// </summary>
            private const float ActivationPolicyDelaySeconds = 1f;

            private SettingsStore _store;
            private string _settingsPath;
            private MascotSettings _settings = MascotSettings.Defaults;
            private HotKeySpec _hotKey;
            private bool _hotKeyRegistered;

            private MascotRunner _runner;
            private Camera _camera;
            private int _savedCullingMask;
            private bool _hidden;

            private string _icon1xPath;
            private string _icon2xPath;
            private int _pid;

            private float _nextSettingsPollAt;
            private float _activationPolicyAt;
            private bool _activationPolicyApplied;
            private bool _shown;

            private void Start()
            {
                _pid = Process.GetCurrentProcess().Id;
                _settingsPath = ResolveSettingsPath();
                _store = new SettingsStore(
                    ReadSettings, StampSettings, WriteSettings,
                    message => Debug.LogWarning("[Mascot] " + message));
                _settings = _store.Current;

                _icon1xPath = Path.Combine(Application.streamingAssetsPath, Icon1xFile);
                _icon2xPath = Path.Combine(Application.streamingAssetsPath, Icon2xFile);

                if (!ChatterMascotNative.CM_Initialize())
                {
                    Debug.LogWarning("[Native] 初期化に失敗しました。メニューバーには出ません");
                    enabled = false;
                    return;
                }
                ChatterMascotNative.CM_SetEventCallback(Callback);

                ApplyMuteToRunner();

                // ★ メニューより先に登録すること。 ラベルにショートカットの表記が乗るので、
                //   後にすると初回のメニューだけ表記が抜ける
                RegisterHotKey();

                _shown = ChatterMascotNative.CM_StatusItemShow(MenuJson.Write(BuildMenu()));
                if (!_shown) Debug.LogWarning("[Native] ステータスバーに出せませんでした");

                _nextSettingsPollAt = Time.realtimeSinceStartup + SettingsPollSeconds;
                _activationPolicyAt = Time.realtimeSinceStartup + ActivationPolicyDelaySeconds;
            }

            private void Update()
            {
                DrainEvents();
                PumpActivationPolicy();
                PumpSettings();
            }

            private void OnDestroy()
            {
                // ★ この順序を守ること。 逆だと、終了中の menu action が
                //   もう生きていない Mono ドメインを叩く
                ChatterMascotNative.CM_SetEventCallback(null);

                if (_hotKeyRegistered)
                {
                    ChatterMascotNative.CM_HotKeyUnregister(MuteHotKeyId);
                    _hotKeyRegistered = false;
                }
                ChatterMascotNative.CM_Shutdown();
            }

            private void DrainEvents()
            {
                string raw;
                while (Events.TryDequeue(out raw))
                {
                    MenuEvent value;
                    string error;
                    if (!MenuJson.TryParseEvent(raw, out value, out error))
                    {
                        Debug.LogWarning("[Native] イベントを読めませんでした: " + error);
                        continue;
                    }

                    switch (value.Kind)
                    {
                        case MenuEventKind.Menu:
                            Invoke(value.Key);
                            break;

                        case MenuEventKind.HotKey:
                            // ★ id → 意味の対応は C# が持つ（ネイティブにキーを書かないため）
                            if (value.HotKeyId == MuteHotKeyId) Invoke(MenuKeys.Mute);
                            break;

                        case MenuEventKind.Log:
                            // ★ ネイティブの診断はここでしか残らない（NSLog は Player.log に入らない）
                            Debug.Log("[Native] " + value.Message);
                            break;
                    }
                }
            }

            /// <summary>押されたときにすること。<b>キーの意味を知っているのはここだけ</b>。</summary>
            private void Invoke(string key)
            {
                switch (key)
                {
                    case MenuKeys.Mute:
                        ToggleMute();
                        break;

                    case MenuKeys.Hide:
                        ToggleHidden();
                        break;

                    case MenuKeys.Settings:
                        OpenSettings();
                        break;

                    case MenuKeys.Quit:
                        // ★★ ネイティブから [NSApp terminate:] を呼ばないこと。
                        //   Application.Quit() なら #68 で直した経路（wantsToQuit で ack を
                        //   投げ切ってから Update の先頭で呼び直す）にそのまま乗る
                        Debug.Log("[Mascot] メニューから終了します");
                        Application.Quit();
                        break;

                    case MenuKeys.About:
                        // 押せない項目（→ MascotMenu）。届いたら黙って捨てる
                        break;

                    default:
                        Debug.LogWarning($"[Native] 知らないメニューのキーです: \"{key}\"");
                        break;
                }
            }

            private void ToggleMute()
            {
                var muted = !_settings.Muted;
                _settings = _settings.WithMuted(muted);

                ApplyMuteToRunner();
                _store.Save(_settings);
                UpdateMenu();

                Debug.Log(muted ? "[Mascot] ミュートしました" : "[Mascot] ミュートを解除しました");
            }

            /// <summary>
            /// ★ <b>描画だけを止める。</b> ウィンドウは透明のまま残す ——
            ///   <c>UniWindowController</c> は窓の生成と透過の面倒を見ているので、
            ///   窓そのものを消しに行くと透過とクリック透過の設定ごと崩れる。
            ///   <c>cullingMask</c> を落とせばカメラは透明でクリアし続け、
            ///   ヒットテストの raycast も当たらなくなる（＝クリック透過も自然に成立する）。
            ///
            /// ★ <b>この状態を永続化しない。</b> 隠れたまま次を起動すると
            ///   「マスコットが出ない」に化ける（ミュートと違い、目で気づく手がかりが無い）。
            /// </summary>
            private void ToggleHidden()
            {
                var camera = ResolveCamera();
                if (camera == null)
                {
                    Debug.LogWarning("[Mascot] 隠す対象のカメラが見つかりません");
                    return;
                }

                if (!_hidden)
                {
                    _savedCullingMask = camera.cullingMask;
                    camera.cullingMask = 0;
                    _hidden = true;
                }
                else
                {
                    camera.cullingMask = _savedCullingMask;
                    _hidden = false;
                }

                UpdateMenu();
                Debug.Log(_hidden ? "[Mascot] キャラクターを隠しました" : "[Mascot] キャラクターを表示しました");
            }

            /// <summary>
            /// 設定ファイルをテキストエディタで開く。
            ///
            /// ★ <b>#76（設定 UI）までの繋ぎ。</b> パネルが入ったらここを差し替える。
            /// ★ <b>先に書き出すこと。</b> 初回起動では設定ファイルがまだ無く、
            ///   「開く」を押しても何も起きないように見える。
            /// </summary>
            private void OpenSettings()
            {
                if (string.IsNullOrEmpty(_settingsPath))
                {
                    Debug.LogWarning("[Mascot] 設定ファイルの場所を決められません");
                    return;
                }

                if (!File.Exists(_settingsPath) && !_store.Save(_settings)) return;

                try
                {
                    var info = new ProcessStartInfo
                    {
                        FileName = "/usr/bin/open",
                        // ★ shell を噛ませないこと（パスに空白が入る）
                        UseShellExecute = false,
                    };
                    info.ArgumentList.Add("-t");
                    info.ArgumentList.Add(_settingsPath);
                    Process.Start(info);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Mascot] 設定ファイルを開けませんでした: " +
                                     ChatterMascotNative.OneLine(e.Message));
                }
            }

            /// <summary>
            /// 設定ファイルが外から書き換わっていたら拾う。
            /// ★ #76 が入るまでは手編集が唯一の変更手段なので、追従できないと直せない。
            /// </summary>
            private void PumpSettings()
            {
                if (Time.realtimeSinceStartup < _nextSettingsPollAt) return;
                _nextSettingsPollAt = Time.realtimeSinceStartup + SettingsPollSeconds;

                if (!_store.Refresh()) return;

                var previousHotKey = _settings.MuteHotKey;
                _settings = _store.Current;

                ApplyMuteToRunner();
                if (!string.Equals(previousHotKey, _settings.MuteHotKey, StringComparison.Ordinal))
                {
                    RegisterHotKey();
                }
                UpdateMenu();
            }

            /// <summary>→ <see cref="ActivationPolicyDelaySeconds"/></summary>
            private void PumpActivationPolicy()
            {
                if (_activationPolicyApplied) return;
                if (Time.realtimeSinceStartup < _activationPolicyAt) return;

                _activationPolicyApplied = true;
                ChatterMascotNative.CM_SetActivationPolicy(1);
            }

            private void RegisterHotKey()
            {
                if (_hotKeyRegistered)
                {
                    ChatterMascotNative.CM_HotKeyUnregister(MuteHotKeyId);
                    _hotKeyRegistered = false;
                }

                // ★ 先に忘れること。 登録に失敗したまま古い表記がメニューに残ると、
                //   効かないショートカットを案内することになる（→ MascotMenu は
                //   IsValid でない指定なら表記を出さない）
                _hotKey = default(HotKeySpec);

                HotKeySpec spec;
                string error;
                if (!HotKeySpec.TryParse(_settings.MuteHotKey, out spec, out error))
                {
                    // SettingsJson が弾いているので通常ここには来ない
                    Debug.LogWarning("[Mascot] ミュートのショートカットを登録できません: " + error);
                    return;
                }

                var status = ChatterMascotNative.CM_HotKeyRegister(
                    MuteHotKeyId, spec.KeyCode, spec.ModifierMask);
                if (status != 0)
                {
                    // -9878 = eventHotKeyExistsErr
                    Debug.LogWarning(
                        $"[Native] ショートカット \"{spec.Format()}\" を登録できませんでした (status={status})。" +
                        "他のアプリが同じ組み合わせを取っている可能性があります");
                    return;
                }

                _hotKey = spec;
                _hotKeyRegistered = true;
                Debug.Log($"[Mascot] ミュートのショートカット: {spec.FormatSymbols()}");
            }

            private void ApplyMuteToRunner()
            {
                var runner = ResolveRunner();
                if (runner == null) return;
                runner.Mute.Muted = _settings.Muted;
            }

            private void UpdateMenu()
            {
                if (!_shown) return;
                ChatterMascotNative.CM_StatusItemUpdate(MenuJson.Write(BuildMenu()));
            }

            private MenuModel BuildMenu()
            {
                return MascotMenu.Build(new MenuState(
                    muted: _settings.Muted,
                    hidden: _hidden,
                    muteHotKey: _hotKey,
                    productName: Application.productName,
                    version: Application.version,
                    pid: _pid,
                    icon1xPath: _icon1xPath,
                    icon2xPath: _icon2xPath));
            }

            /// <summary>
            /// ★ <b>掴んだままにしないこと。</b> <c>Bridge</c> は
            ///   <c>DontDestroyOnLoad</c> で生き続けるが <c>MascotRunner</c> はシーンと共に死ぬ。
            /// </summary>
            private MascotRunner ResolveRunner()
            {
                if (_runner != null) return _runner;
                _runner = FindFirstObjectByType<MascotRunner>();
                return _runner;
            }

            private Camera ResolveCamera()
            {
                if (_camera != null) return _camera;

                // 透過とクリック透過が見ているカメラと同じものを使う
                var controller = FindFirstObjectByType<UniWindowController>();
                if (controller != null && controller.currentCamera != null)
                {
                    _camera = controller.currentCamera;
                    return _camera;
                }

                _camera = Camera.main;
                return _camera;
            }

            private string ReadSettings()
            {
                if (string.IsNullOrEmpty(_settingsPath)) return null;
                if (!File.Exists(_settingsPath)) return null;
                return File.ReadAllText(_settingsPath);
            }

            /// <summary>
            /// ★ 内容のハッシュにしないこと（読まずに済ませるための仕組み）。
            ///   core の <c>createConfigStore</c> と同じ <c>mtime:size</c>。
            /// </summary>
            private string StampSettings()
            {
                if (string.IsNullOrEmpty(_settingsPath)) return null;

                var info = new FileInfo(_settingsPath);
                if (!info.Exists) return null;
                return info.LastWriteTimeUtc.Ticks + ":" + info.Length;
            }

            /// <summary>★ 別名で書いてから置き換える（→ <c>WindowGeometry.WriteState</c> と同じ）</summary>
            private void WriteSettings(string text)
            {
                if (string.IsNullOrEmpty(_settingsPath)) throw new IOException("保存先を決められません");

                var directory = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                var tmp = _settingsPath + ".tmp";
                File.WriteAllText(tmp, text);
                if (File.Exists(_settingsPath)) File.Replace(tmp, _settingsPath, null);
                else File.Move(tmp, _settingsPath);
            }
        }
    }
}
#endif
