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

            /// <summary>
            /// <c>Watching</c> で位置を読む間隔と、保存までの落ち着き待ち。
            ///
            /// ★ <b>毎フレーム読まないこと。</b> <c>windowPosition</c> / <c>windowSize</c> は
            ///   どちらも P/Invoke で、常駐アプリの電力予算に効く。
            /// ★ <b>デバウンスがドラッグ中の保存も兼ねる。</b> ドラッグ中は矩形が動き続けるので
            ///   期限が延び続け、離してから保存される（ドラッグ中かどうかを見る必要が無い）。
            /// </summary>
            private const float PollIntervalSeconds = 0.25f;
            private const float SaveDebounceSeconds = 0.5f;

            /// <summary>
            /// 保存に失敗したあと、次に試すまでの間隔と、諦めるまでの回数。
            ///
            /// ★ <b>間隔を置かないと毎フレーム書き込みを試すことになる</b>（保存先が
            ///   読み取り専用、ディスク満杯など）。★ <b>上限を置かないと永遠に鳴り続ける。</b>
            /// </summary>
            private const float SaveRetrySeconds = 5f;
            private const int MaxSaveFailures = 3;

            /// <summary>
            /// ディスプレイ構成が変わったという通知を受けてから、置き直すまでの落ち着き待ち。
            ///
            /// ★ <b>通知は連発する</b>（スリープ復帰・解像度変更・抜き差し）。そのつど読み直して
            ///   書き直すと、<b>いちばん不安定な瞬間に最も多く窓を書く</b>ことになる。
            /// </summary>
            private const float MonitorSettleSeconds = 1f;

            private UniWindowController _controller;
            private WindowStateStore _store;
            private string _statePath;

            private Phase _phase = Phase.Attaching;
            private float _deadline;
            private WindowState _saved;

            /// <summary>
            /// いまのディスプレイ構成の指紋。
            ///
            /// ★ <b>保存のたびに読み直さないこと。</b> 変わる契機は「掴み取り」と「構成変更」だけで、
            ///   <b>どちらも置き直しを通る</b>ので、そこで控えれば陳腐化しない。
            ///   保存のたびに読むと、モニタ数ぶんの P/Invoke が保存のたびに走る。
            /// </summary>
            private string _signature = string.Empty;

            private PointRect _wanted;
            private int _corrections;
            private float _applyDeadline;

            private PointRect _lastSeen;
            private WindowState _lastPersisted;
            private bool _dirty;
            private float _saveAt;
            private float _nextPollAt;
            private int _saveFailures;
            private bool _gaveUpSaving;

            /// <summary>置き直しの予約。0 未満なら予約なし。</summary>
            private float _reapplyAt = -1f;
            private PointRect _reapplyFrom;

            private void Start()
            {
                _controller = FindFirstObjectByType<UniWindowController>();
                _statePath = ResolveStatePath();
                _store = new WindowStateStore(ReadState, WriteState, message => Debug.LogWarning("[Mascot] " + message));
                _saved = _store.Load();
                _deadline = Time.realtimeSinceStartup + AttachTimeoutSeconds;

                // ★ **購読は Start でしか張れない。** `OnEnable` は `AddComponent` の中で
                //   **Awake と一緒に同期的に**呼ばれるので、そこではまだ相手を引けていない。
                //   しかもこの GameObject は無効化されないので、OnEnable は二度と来ない。
                //   —— それで**構成変化の追従が丸ごと動いていなかった**（レビュー指摘）。
                // ★ 取りこぼしは無い: 通知の発火元はどれも最初の Start より後に走る。
                if (_controller != null) _controller.OnMonitorChanged += OnMonitorChanged;
            }

            private void OnApplicationQuit()
            {
                // ★ **ここが「全員がまだ生きている」ことを当てにできる唯一のフック。**
                //   OnDestroy は破棄順が未規定なうえ、下の自壊経路で先に消えていることもある
                SaveNow();
            }

            private void OnDestroy()
            {
                if (_controller != null) _controller.OnMonitorChanged -= OnMonitorChanged;

                // 終了以外の経路（自壊・シーンの破棄）で溜めていた変更を投げ切る。
                // 二重に呼ばれても保存の判断が弾く
                SaveNow();
            }

            /// <summary>
            /// 溜めている変更を、いま書けるだけ書く。
            ///
            /// ★ <b>初期化前に呼ばれうる。</b> 以前は「保留があるときだけ」という条件が
            ///   偶然その盾を兼ねていたので、条件を外すなら<b>保存先が用意できているか</b>を
            ///   自分で見る必要がある。
            /// ★ <b>矩形が無効かどうかの判断は持たない</b>（→ <see cref="SavePolicy"/>）。
            ///   両方に置くと、片方が「もう一方が見ているから」と消される。
            /// </summary>
            private void SaveNow()
            {
                if (_store == null) return;

                var rect = _controller != null ? Current() : default;
                // ★ 終了時は再試行の待ちを飛ばす。直前に失敗していても、
                //   原因（権限・空き容量）が解消していればここで書けることがある
                Persist(rect.IsValid ? rect : _lastSeen, immediate: true);
            }

            private void LateUpdate()
            {
                if (_controller == null)
                {
                    Destroy(gameObject);
                    return;
                }

                PumpReapply();

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

                // ★ **モニタが1枚も取れないフレームでは指紋を上書きしない。** 空の指紋を焼くと、
                //   次の起動で「構成が変わった」と読まれて厳しい方の閾値が使われ、
                //   ユーザーが端に寄せた窓が押し戻される
                if (layout.HasMonitors) _signature = layout.Signature;

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
                if (actual.Matches(_wanted))
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
                    // ★ 諦めた矩形を「保存済み」として覚える。**書きはしない** ——
                    //   届かなかった位置で、ユーザーが選んだ保存値を上書きしないため。
                    //   次の起動でもう一度その位置を目指す
                    _lastPersisted = new WindowState(actual, _signature);
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
                    if (!actual.Matches(_lastSeen))
                    {
                        _lastSeen = actual;
                        _dirty = true;
                        _saveAt = now + SaveDebounceSeconds;
                    }
                }

                if (_dirty && now >= _saveAt) Persist(_lastSeen);
            }

            /// <summary>
            /// ディスプレイ構成が変わった。<b>ここでは予約するだけ。</b>
            ///
            /// ★ <b>その場で置き直さないこと。</b> 通知は連発するので、そのつど読み直して
            ///   書き直すと<b>いちばん不安定な瞬間に最も多く窓を書く</b>ことになる。
            ///
            /// ★ <b>置き直しの入力に「いまの矩形」を使わないこと。</b> OS は
            ///   <b>通知より前に窓を動かしている</b>。いまの矩形を拾うと、
            ///   <b>OS がずらした後の位置を保存して、ユーザーが選んだ位置を上書きする</b>。
            ///   だから<b>予約した瞬間に、直前のポーリング値を入力として確定させる</b>。
            /// </summary>
            private void OnMonitorChanged()
            {
                if (_phase == Phase.Attaching) return;

                if (_reapplyAt < 0f)
                {
                    _reapplyFrom = _lastSeen.IsValid ? _lastSeen : _saved.Rect;
                }
                _reapplyAt = Time.realtimeSinceStartup + MonitorSettleSeconds;
            }

            private void PumpReapply()
            {
                if (_reapplyAt < 0f || Time.realtimeSinceStartup < _reapplyAt) return;
                _reapplyAt = -1f;

                // ★ 指紋は「その位置を選んだときの構成」のまま渡す。食い違うことが、
                //   そのまま「厳しい方の閾値を使う」条件になる
                Debug.Log("[Mascot] ディスプレイ構成が変わりました。ウィンドウを置き直します" +
                          $"（{_reapplyFrom}）");
                BeginApplying(new WindowState(_reapplyFrom, _saved.DisplaySignature));
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

            /// <summary>
            /// 書けるなら書く。<b>失敗しても保留は落とさない</b>（→ <see cref="SavePolicy"/>）。
            /// </summary>
            private void Persist(PointRect rect, bool immediate = false)
            {
                var now = Time.realtimeSinceStartup;
                var candidate = new WindowState(rect, _signature);
                var retryAt = immediate ? now : _saveAt;

                switch (SavePolicy.Decide(candidate, _lastPersisted, _saveFailures, MaxSaveFailures,
                                          now, retryAt))
                {
                    case SaveAction.Skip:
                        _dirty = false;
                        return;
                    case SaveAction.WaitForRetry:
                        return;
                }

                if (_store.Save(candidate))
                {
                    _lastPersisted = candidate;
                    _saved = candidate;
                    _saveFailures = 0;
                    _dirty = false;
                    return;
                }

                // ★ 保留は落とさない。ただし**必ず待たせる** ——
                //   落とさないだけだと毎フレーム書き込みを試すことになる
                _saveFailures++;
                _saveAt = now + SaveRetrySeconds;

                if (_saveFailures >= MaxSaveFailures && !_gaveUpSaving)
                {
                    _gaveUpSaving = true;
                    Debug.LogWarning("[Mascot] ウィンドウの保存に繰り返し失敗したので、以後あきらめます。" +
                                     "次の起動では前回の位置に戻ります");
                }
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

            private string ReadState()
            {
                if (string.IsNullOrEmpty(_statePath)) return null;
                if (!File.Exists(_statePath)) return null;
                return File.ReadAllText(_statePath);
            }

            /// <summary>
            /// ★ <b>別名で書いてから置き換えること。</b> 直接書くと、書きかけを次の起動が読んで
            ///   「壊れている」と判定し、位置が1回リセットされる。
            ///
            /// ★ <b>「消してから移す」にしないこと。</b> 2段になると、その間にプロセスが落ちたり
            ///   移動が失敗したりしたときに<b>保存そのものが消える</b>。
            ///   しかもこの経路は<b>終了処理からも呼ばれる</b>ので、プロセスの終了と正面から重なる。
            ///   置き換えは1操作で済ませる（core の原子書き込みと同じ形）。
            ///
            /// ★ <b>「上書きする移動」はこのプロファイルに無い</b>（コンパイルで確認した）ので、
            ///   置き換え先の有無で2つの API を使い分ける。<b>どちらの枝も1操作</b>なので、
            ///   消えてしまう窓は無い。
            /// </summary>
            private void WriteState(string text)
            {
                if (string.IsNullOrEmpty(_statePath)) throw new IOException("保存先を決められません");

                var directory = Path.GetDirectoryName(_statePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                var tmp = _statePath + ".tmp";
                File.WriteAllText(tmp, text);
                if (File.Exists(_statePath)) File.Replace(tmp, _statePath, null);
                else File.Move(tmp, _statePath);
            }
        }
    }
}
