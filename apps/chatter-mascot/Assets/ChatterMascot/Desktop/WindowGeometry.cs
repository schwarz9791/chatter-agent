using System;
using System.Collections.Generic;
using System.IO;
using ChatterMascot.Vrm;
using ChatterMascot.Window;
using Kirurobo;
using UnityEngine;

namespace ChatterMascot.Desktop
{
    /// <summary>
    /// <b>ウィンドウの位置と大きさを、ポイントで、自分で覚える。</b>
    ///
    /// ★ <b>Unity の永続化には乗れない。</b> <c>Screenmanager Resolution/Window Position</c> は
    ///   <b>バッキング px</b> なので、Retina で終了して 1x のディスプレイで開くと窓が倍になる
    ///   （実測済み。→ <c>docs/mascot.md</c>）。<c>UniWindowController</c> の
    ///   <c>windowPosition</c> / <c>windowSize</c> / <c>GetMonitorRect</c> は
    ///   <b>すべて NSWindow のポイント</b>なので、そこで閉じれば換算そのものが要らなくなる。
    ///
    /// ★ <b>これは <c>WindowSizeKeeper</c> の置き換え。</b> あちらは「起動直後に見えていた大きさ」を
    ///   守って枠なし化の +32 を打ち消す対症療法で、
    ///   <a href="https://github.com/schwarz9791/chatter-agent/issues/66">#66</a> の2点
    ///   （<c>_intended</c> を捕まえる順序が未保証 / 補正が最初の1回で打ち切り）を抱えていた。
    ///   <b>意図した大きさの権威を自前の永続化（pt）に移すと、どちらも構造的に消える。</b>
    ///
    /// ★ <b>2人が <c>windowSize</c> を書く状態を作らないこと。</b> だから
    ///   <c>WindowSizeKeeper</c> は削除してある。
    ///
    /// ★ <b><see cref="MonoBehaviour"/> をシーンに置かないこと。</b> Android ではこの
    ///   アセンブリごと存在しないので、属性の走査対象にすらならない（→ <see cref="VrmDragHandleBinder"/>）。
    /// </summary>
    public static class WindowGeometry
    {
        /// <summary>
        /// 初回起動の大きさ（ポイント）。
        ///
        /// ★ <b>ランタイムからは復元できない。</b> <c>clientSize</c> が読めるようになる
        ///   （= attach）時点で、枠なし化の +32 は<b>もう乗っている</b>
        ///   （実測: <c>client=300,512pt</c> に対し <c>Screen</c> はまだ <c>300x480px</c>）。
        ///   だから<b>定数で持つ</b>。<c>ProjectSettings</c> の
        ///   <c>defaultScreenWidth/Height</c> は、ここが効くまでの一瞬しか効かない。
        /// ★ <b>食い違ったら <c>SceneFixups</c> が警告する。</b>
        /// </summary>
        public const float DefaultWidthPoints = 300f;
        public const float DefaultHeightPoints = 480f;

        /// <summary>これ以上小さくはしない。手で潰して二度と掴めなくなるのを防ぐ。</summary>
        private const float MinWidthPoints = 120f;
        private const float MinHeightPoints = 160f;

        /// <summary>
        /// 「掴める」と認める最小の可視矩形。★ <b>面積比で判定しないこと</b>
        /// （縦長の窓では下端の帯だけでも面積の条件を満たしてしまう。→ <see cref="WindowPlacement"/>）。
        /// </summary>
        private const float MinVisiblePoints = 96f;

        /// <summary>ディスプレイ構成が変わっていたときの、厳しい方の閾値。</summary>
        private const float StrictVisiblePoints = 160f;

        /// <summary>保存先。★ <c>PlayerPrefs</c> を使わない理由は <see cref="WindowState"/> に。</summary>
        private const string StateDirectory = "mascot";
        private const string StateFile = "window.json";

        internal static PlacementLimits Limits => new PlacementLimits(
            DefaultWidthPoints, DefaultHeightPoints,
            MinWidthPoints, MinHeightPoints,
            MinVisiblePoints, MinVisiblePoints,
            StrictVisiblePoints, StrictVisiblePoints);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            // Editor では attach も枠なし化も起きない（UniWindowController の制限）
            if (Application.isEditor) return;
            // ★ 実測モードとは同居しない。窓を動かし合うと何を測っているのか分からなくなる
            if (CommandLine.Flag(WindowProbe.ProbeFlag)) return;
            // ★ `using System;` があるので Object を修飾すること（CS0104）
            if (UnityEngine.Object.FindFirstObjectByType<UniWindowController>() == null) return;

            var go = new GameObject(nameof(WindowGeometry)) { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<Keeper>();
        }

        /// <summary>
        /// <c>{XDG_CONFIG_HOME:-~/.config}/chatter-agent/mascot/window.json</c>。
        /// ★ <c>AssetPath.RuntimeDirectory</c> を使い回して、ユーザーから見た
        ///   「chatter-agent の設定はここ1箇所」を崩さない。
        /// </summary>
        internal static string ResolveStatePath()
        {
            var root = AssetPath.RuntimeDirectory(AssetEnvFactory.Current());
            if (string.IsNullOrEmpty(root)) return null;
            return Path.Combine(root, StateDirectory, StateFile);
        }

        private enum Phase
        {
            /// <summary><c>clientSize</c> が読めるようになるまで待つ。</summary>
            Attaching,

            /// <summary>復元先を書いて、効いたことを確かめる。</summary>
            Applying,

            /// <summary><b>読むだけ。</b>変化を間引いて保存する。</summary>
            Watching,
        }

        private sealed class Keeper : MonoBehaviour
        {
            /// <summary>
            /// attach を待つ上限。★ <b>短くしないこと</b> —— VRM の読み込みでメインスレッドが
            /// 詰まると実時間では後ろへずれる。
            /// </summary>
            private const float AttachTimeoutSeconds = 10f;

            /// <summary>
            /// 書いた値が効いたかを見張る時間と回数。
            ///
            /// ★ <b>1回で打ち切らないこと</b>（#66-2）。枠なし化は複数フレームに分かれて起きうるし、
            ///   <c>Metal RecreateSurface</c> は起動ごとに2回出る。
            /// ★ <b>無限に直し続けないこと。</b> OS が押し返してくる状況では
            ///   毎フレーム P/Invoke が走り続ける。
            /// </summary>
            private const float ApplyWatchSeconds = 5f;
            private const int MaxCorrections = 5;

            /// <summary>ズレとみなす閾値（ポイント）。丸めの1pt を追いかけない。</summary>
            private const float Epsilon = 1f;

            /// <summary>
            /// <c>Watching</c> で位置を読む間隔と、保存までの落ち着き待ち。
            ///
            /// ★ <b>毎フレーム読まないこと。</b> <c>windowPosition</c> / <c>windowSize</c> は
            ///   どちらも P/Invoke で、常駐アプリの電力予算に効く。
            /// ★ <b>デバウンスがドラッグ中の保存も兼ねる。</b> ドラッグ中は矩形が動き続けるので
            ///   期限が延び続け、離してから保存される（<c>IsDragging</c> を見る必要が無い）。
            /// </summary>
            private const float PollIntervalSeconds = 0.25f;
            private const float SaveDebounceSeconds = 0.5f;

            private UniWindowController _controller;
            private WindowStateStore _store;
            private string _statePath;

            private Phase _phase = Phase.Attaching;
            private float _deadline;
            private WindowState _saved;

            private PointRect _wanted;
            private int _corrections;
            private float _applyDeadline;

            private PointRect _lastSeen;
            private PointRect _lastPersisted;
            private bool _dirty;
            private float _saveAt;
            private float _nextPollAt;

            private void Start()
            {
                _controller = FindFirstObjectByType<UniWindowController>();
                _statePath = ResolveStatePath();
                _store = new WindowStateStore(ReadState, WriteState, message => Debug.LogWarning("[Mascot] " + message));
                _saved = _store.Load();
                _deadline = Time.realtimeSinceStartup + AttachTimeoutSeconds;
            }

            private void OnEnable()
            {
                // ★ ディスプレイ構成が変わったら置き直す。抜いたディスプレイに窓を残さない
                if (_controller != null) _controller.OnMonitorChanged += OnMonitorChanged;
            }

            private void OnDisable()
            {
                if (_controller != null) _controller.OnMonitorChanged -= OnMonitorChanged;
            }

            private void OnDestroy()
            {
                // ★ 溜めていた変更を投げ切る。終了時にしか動かさなかった人の位置が落ちる
                if (_dirty) Persist(Current());
            }

            private void LateUpdate()
            {
                if (_controller == null)
                {
                    Destroy(gameObject);
                    return;
                }

                switch (_phase)
                {
                    case Phase.Attaching:
                        WaitForAttach();
                        break;
                    case Phase.Applying:
                        Applying();
                        break;
                    default:
                        Watching();
                        break;
                }
            }

            // ── Attaching ─────────────────────────────────────────────

            private void WaitForAttach()
            {
                // ★ attach 前の clientSize は (0,0)。Start() では必ずこれ
                var client = _controller.clientSize;
                if (client.x > 0f && client.y > 0f)
                {
                    BeginApplying(_saved);
                    return;
                }

                if (Time.realtimeSinceStartup > _deadline)
                {
                    Debug.LogWarning("[Mascot] ウィンドウを掴めないので位置を復元しません: " +
                                     $"clientSize={client}");
                    Destroy(gameObject);
                }
            }

            // ── Applying ──────────────────────────────────────────────

            private void BeginApplying(WindowState from)
            {
                var layout = ReadLayout();
                var placement = WindowPlacement.Resolve(from, layout, Limits);

                Debug.Log($"[Mascot] ウィンドウ: {placement.Reason} monitor={placement.MonitorIndex} " +
                          $"rect={placement.Rect} 保存={(from.Rect.IsValid ? from.Rect.ToString() : "なし")} " +
                          $"displays={layout.Signature}");

                _wanted = placement.Rect;
                _corrections = 0;
                _applyDeadline = Time.realtimeSinceStartup + ApplyWatchSeconds;
                _phase = Phase.Applying;
                Write(_wanted);
            }

            private void Applying()
            {
                var actual = Current();
                if (Matches(actual, _wanted))
                {
                    _phase = Phase.Watching;
                    _lastSeen = actual;
                    // ★ 復元で位置が変わったぶん（Clamped / Defaulted）はここで書き戻す。
                    //   次の起動で同じ判定をやり直させない
                    Persist(actual);
                    _nextPollAt = Time.realtimeSinceStartup + PollIntervalSeconds;
                    return;
                }

                if (_corrections >= MaxCorrections || Time.realtimeSinceStartup > _applyDeadline)
                {
                    Debug.LogWarning($"[Mascot] ウィンドウを {_wanted} にできませんでした（実際は {actual}）。" +
                                     "そのまま使います");
                    _phase = Phase.Watching;
                    _lastSeen = actual;
                    _lastPersisted = actual;
                    _nextPollAt = Time.realtimeSinceStartup + PollIntervalSeconds;
                    return;
                }

                _corrections++;
                Debug.Log($"[Mascot] ウィンドウを直します ({_corrections}/{MaxCorrections}): " +
                          $"{actual} → {_wanted}");
                Write(_wanted);
            }

            // ── Watching ──────────────────────────────────────────────

            private void Watching()
            {
                var now = Time.realtimeSinceStartup;
                if (now >= _nextPollAt)
                {
                    _nextPollAt = now + PollIntervalSeconds;
                    var actual = Current();
                    if (!Matches(actual, _lastSeen))
                    {
                        _lastSeen = actual;
                        _dirty = true;
                        _saveAt = now + SaveDebounceSeconds;
                    }
                }

                if (_dirty && now >= _saveAt) Persist(_lastSeen);
            }

            private void OnMonitorChanged()
            {
                if (_phase == Phase.Attaching) return;

                // ★ 「いまの矩形」を、**前の構成の指紋つきで**投げ直す。
                //   指紋が食い違うことが、そのまま「厳しい方の閾値を使う」条件になる
                var current = Current();
                Debug.Log($"[Mascot] ディスプレイ構成が変わりました。ウィンドウを置き直します（いま {current}）");
                BeginApplying(new WindowState(current, _saved.DisplaySignature));
            }

            // ── I/O ───────────────────────────────────────────────────

            private PointRect Current()
            {
                var position = _controller.windowPosition;
                var size = _controller.windowSize;
                return new PointRect(position.x, position.y, size.x, size.y);
            }

            private void Write(PointRect rect)
            {
                // ★ 大きさを先に。位置を先に書くと、大きさが変わったぶんだけ
                //   最小コーナー基準の位置がずれて見える
                _controller.windowSize = new Vector2(rect.Width, rect.Height);
                _controller.windowPosition = new Vector2(rect.X, rect.Y);
            }

            private void Persist(PointRect rect)
            {
                _dirty = false;
                if (!rect.IsValid) return;
                if (Matches(rect, _lastPersisted)) return;

                var state = new WindowState(rect, ReadLayout().Signature);
                if (!_store.Save(state)) return;
                _lastPersisted = rect;
                _saved = state;
            }

            private DisplayLayout ReadLayout()
            {
                var count = UniWindowController.GetMonitorCount();
                var monitors = new List<PointRect>(Math.Max(0, count));
                for (var i = 0; i < count; i++)
                {
                    var rect = UniWindowController.GetMonitorRect(i);
                    monitors.Add(new PointRect(rect.x, rect.y, rect.width, rect.height));
                }
                return DisplayLayout.Of(monitors);
            }

            private static bool Matches(PointRect a, PointRect b) =>
                Math.Abs(a.X - b.X) < Epsilon && Math.Abs(a.Y - b.Y) < Epsilon &&
                Math.Abs(a.Width - b.Width) < Epsilon && Math.Abs(a.Height - b.Height) < Epsilon;

            private string ReadState()
            {
                if (string.IsNullOrEmpty(_statePath)) return null;
                if (!File.Exists(_statePath)) return null;
                return File.ReadAllText(_statePath);
            }

            /// <summary>
            /// ★ <b>tmp + rename で置くこと。</b> 書きかけを次の起動が読むと
            ///   「壊れている」と判定されて位置が1回リセットされる。
            /// </summary>
            private void WriteState(string text)
            {
                if (string.IsNullOrEmpty(_statePath)) throw new IOException("保存先を決められません");

                var directory = Path.GetDirectoryName(_statePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                var tmp = _statePath + ".tmp";
                File.WriteAllText(tmp, text);
                if (File.Exists(_statePath)) File.Delete(_statePath);
                File.Move(tmp, _statePath);
            }
        }
    }
}
