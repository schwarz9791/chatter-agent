using System;
using System.IO;
using ChatterMascot.Settings;
using UnityEngine;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// 「<c>settings.json</c> → シーン」の反映。デスクトップでも Android でも同じものが動く。
    /// <b>ここが settings.json の唯一の書き手</b>（デスクトップの設定パネルもここを経由する。
    /// → <see cref="Apply"/>）。メニューバーやホットキーのようなデスクトップ固有の見た目は
    /// <c>StatusItemBridge</c> が <see cref="ChangedExternally"/> を購読して担う。
    ///
    /// ★ <b>置き場所が Vrm asmdef なのは、<see cref="MascotRunner"/> と <see cref="VrmCharacter"/>
    ///   の両方に触るため。</b>（<c>Vrm</c> → <c>Runtime</c> の一方向参照はあるが逆は無いので、
    ///   <c>Runtime</c> 側からは <c>VrmCharacter</c> を参照できず、置けない）。
    /// </summary>
    public sealed class MascotSettingsHost : MonoBehaviour
    {
        /// <summary>設定ファイルの更新を見る間隔。★ 毎フレーム stat しないこと</summary>
        private const float PollSeconds = 1f;

        /// <summary>
        /// ★ <c>FindFirstObjectByType</c> で毎回探さないこと。<c>StatusItemBridge</c> は
        ///   <c>Start</c> で1回だけ取りに来る。
        /// </summary>
        public static MascotSettingsHost Instance { get; private set; }

        /// <summary>
        /// ファイルの外部変更（手編集 / 別プロセス）を検出したときに発火する（旧い値, 新しい値）。
        /// 反映そのものはこの型が既に済ませている ——購読者はホットキーの再登録や
        /// メニューの更新など、<b>デスクトップ固有の見た目</b>だけを行う。
        /// </summary>
        public event Action<MascotSettings, MascotSettings> ChangedExternally;

        /// <summary>いまの値。<b>読むだけ</b> —— 書くのは <see cref="Apply"/> 経由に限る。</summary>
        public MascotSettings Current { get; private set; } = MascotSettings.Defaults;

        private SettingsStore _store;
        private string _settingsPath;
        private MascotRunner _runner;
        private VrmCharacter _character;
        private float _nextPollAt;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            // ★ StatusItemBridge.Install と同じ理由。Editor では動かさない
            if (Application.isEditor) return;

            var go = new GameObject(nameof(MascotSettingsHost)) { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            go.AddComponent<MascotSettingsHost>();
        }

        private void Awake()
        {
            Instance = this;

            _settingsPath = SettingsLocation.Resolve(AssetEnvFactory.Current());
            _store = new SettingsStore(
                ReadSettings, StampSettings, WriteSettings,
                message => Debug.LogWarning("[Mascot] " + message));
            Current = _store.Current;

            // ★ 起動直後にも通すこと。ファイルの値（ミュート・音量・待機モーションなど）を
            //   シーンへ反映するのはここが唯一の経路（→ ApplySettingsToScene の doc）
            ApplySettingsToScene();

            _nextPollAt = Time.realtimeSinceStartup + PollSeconds;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (Time.realtimeSinceStartup < _nextPollAt) return;
            _nextPollAt = Time.realtimeSinceStartup + PollSeconds;

            var previous = Current;
            if (!_store.Refresh()) return;

            Current = _store.Current;
            ApplySettingsToScene();

            // ★ 接続先は Awake で1回きり捕まえる設計（→ MascotRunner.ResolveServerUrl）。
            //   ここで書き換わっても次回の起動まで反映されない
            if (!string.Equals(previous.ServerUrl, Current.ServerUrl, StringComparison.Ordinal) ||
                !string.Equals(previous.Token, Current.Token, StringComparison.Ordinal))
            {
                Debug.Log("[Mascot] 接続先の変更は再起動で反映されます");
            }

            ChangedExternally?.Invoke(previous, Current);
        }

        /// <summary>
        /// 反映（シーンへ適用）+ 保存。<b>settings.json への唯一の書き込み口</b>。
        ///
        /// ★ ストアを2つ作らないための境界でもある——設定パネル（デスクトップ）はここを
        ///   経由するだけで、自分では read-modify-write しない。
        /// </summary>
        public void Apply(MascotSettings next)
        {
            Current = next;
            ApplySettingsToScene();
            _store.Save(Current);
        }

        /// <summary>
        /// <c>settings.json</c> の値をシーンへ反映する。
        ///
        /// ★★ <b>ここが「設定 → 見た目・音」の唯一の経路。</b> 起動時にも、
        ///   パネルからの変更でも、ファイルを直接編集したときにも同じものが通る。
        ///   経路を分けると「パネルからは効くのに、ファイルを直したときだけ効かない」
        ///   （またはその逆）が生まれる。
        ///
        /// ★ 例外は起動時に1回だけ読む値（接続先と <c>xr.*</c>）。ここを通らないので、
        ///   ファイルを直しても再起動まで効かない（接続先 → <c>MascotRunner.ResolveServerUrl</c>、
        ///   xr → <c>XrStage</c>）。
        ///
        /// ★ <b>対象が居なくても警告しないこと。</b> <c>TransparencyProbe</c> のような
        ///   VRM を出さないシーンでも同じ常駐物が動く。
        ///
        /// ★ Android では視線（<c>CursorGazeEnabled</c>）の注入元（<c>CursorProvider</c>）が
        ///   存在しないので自律的な漂いに倒れるだけで、fps を除けばこの反映自体は
        ///   デスクトップと同じでよい（→ <c>SettingsMapping.AppliesFrameRate</c>）。
        /// </summary>
        private void ApplySettingsToScene()
        {
            ApplyMuteToRunner();

            var runner = ResolveRunner();
            if (runner != null)
            {
                runner.Volume = Current.Volume;
                // ★ FrameRateBudget.SetBaseline 経由なので、VRM 読み込み中の一時的な
                //   引き上げ（Boost）を踏み荒らさない。★ Android には適用しない
                //   （→ SettingsMapping.AppliesFrameRate）
                if (SettingsMapping.AppliesFrameRate(Application.platform))
                {
                    runner.SetTargetFrameRate(Current.FrameRate);
                }
            }

            // ★★ ここで VrmStage の headroom を触らないこと。 あれは「bounds をどれだけ
            //   余裕を持って収めるか」の係数で、1 を下回るとモデルが画面からはみ出す
            //   （実機で頭と足が対称に欠けた）。キャラの大きさは**ウィンドウ**で変える
            //   （→ WindowGeometry.SetSize）。窓が変われば VrmStage が自動で収め直す。
            var character = ResolveCharacter();
            if (character != null)
            {
                character.IdleMotion = Current.IdleMotion;
                character.CursorGazeEnabled = Current.CursorGaze;
                character.BlinkEnabled = Current.Blink;
            }
        }

        private void ApplyMuteToRunner()
        {
            var runner = ResolveRunner();
            if (runner == null) return;
            runner.Mute.Muted = Current.Muted;
        }

        /// <summary>★ 1回引いたら使い回す。常駐アプリの電力予算に効く（毎フレーム走査しない）。</summary>
        private MascotRunner ResolveRunner()
        {
            if (_runner != null) return _runner;
            _runner = FindFirstObjectByType<MascotRunner>();
            return _runner;
        }

        private VrmCharacter ResolveCharacter()
        {
            if (_character != null) return _character;
            _character = FindFirstObjectByType<VrmCharacter>(FindObjectsInactive.Include);
            return _character;
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
