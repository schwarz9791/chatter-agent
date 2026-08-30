using System.Text;
using Kirurobo;
using UnityEngine;

namespace ChatterMascot.Desktop
{
    /// <summary>
    /// <b>ウィンドウの座標系を実機で測るための道具。起動引数 <c>-windowProbe</c> でだけ動く。</b>
    ///
    /// ★ <b>実測結果は下に書いてある。</b> もう分かっていることを測り直すためではなく、
    ///   <b>Unity / macOS / UniWindowController のバージョンが上がったときに
    ///   同じ測定をやり直せるようにする</b>ために残してある
    ///   （<c>VrmProbe</c> と同じ立ち位置）。
    ///
    /// ★ <b>これが動いている間 <see cref="WindowGeometry"/> は動かない。</b>
    ///   窓を動かし合うと何を測っているのか分からなくなるし、実測で飛ばした位置が
    ///   永続化されてしまう。
    ///
    /// <b>実測で分かったこと</b>（2026-08-30 / macOS 26.6.2 / 外部 4K + 内蔵 Retina):
    ///
    /// <list type="bullet">
    ///   <item><c>windowPosition</c> は<b>左下（最小コーナー）</b>。<c>(3540,1650)</c> を入れた窓が
    ///         画面の<b>右上</b>（top-down で y=30..510）に着いた</item>
    ///   <item><see cref="UniWindowController.GetMonitorRect"/> は<b>同じ bottom-up 空間</b>を返すが、
    ///         ★ <b>visible frame（作業領域）</b>であってフルフレームではない ——
    ///         メインが <c>(0,0 3840x2130)</c> で、<b>2160 ではなく 2130</b>
    ///         （メニューバーの 30pt を除いてある）。<b>だから作業領域の和集合には隙間がある</b>
    ///         （カーソルが <c>y=-23</c> という、どのモニタ矩形にも入らない位置に居た実測がある）</item>
    ///   <item>作業領域の中に収まる位置なら<b>引き戻しは起きない</b>。入れた値がそのまま読み戻る</item>
    /// </list>
    ///
    /// ★ <b><see cref="MonoBehaviour"/> をシーンに置かないこと。</b> Android ではこの
    ///   アセンブリごと存在しないので、属性の走査対象にすらならない（→ <see cref="VrmDragHandleBinder"/>）。
    /// </summary>
    public static class WindowProbe
    {
        /// <summary>
        /// この道具を動かす起動引数。<b>実機確認専用。</b>
        ///
        /// ★ <b>既定では絶対に立てないこと。</b> 起動のたびに窓が飛ぶうえ、
        ///   <see cref="WindowGeometry"/>（位置の復元と永続化）が止まる。
        /// </summary>
        internal const string ProbeFlag = "-windowProbe";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            // Editor では attach も枠なし化も起きない（UniWindowController の制限）
            if (Application.isEditor) return;
            if (!CommandLine.Flag(ProbeFlag)) return;
            if (Object.FindFirstObjectByType<UniWindowController>() == null) return;

            var go = new GameObject(nameof(WindowProbe)) { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(go);
            go.AddComponent<Probe>();
        }

        private sealed class Probe : MonoBehaviour
        {
            /// <summary>
            /// attach を待つ上限。これを過ぎたら測れなかったものとして諦める。
            ///
            /// ★ <b>短くしないこと。</b> <c>AttachMyWindow</c> は
            ///   <c>UniWindowController.Update()</c> の中で走るが、VRM の読み込みで
            ///   メインスレッドが詰まると実時間では後ろへずれる（<c>WindowSizeKeeper</c> と同じ理由）。
            /// </summary>
            private const float AttachTimeoutSeconds = 10f;

            /// <summary>
            /// 画面外へどれだけはみ出させるか。
            ///
            /// ★ <b>両軸とも破ること。</b> 片方だけだと、引き戻しが x / y のどちらに効いたのかが
            ///   1回の観測で分からない。窓は 300x480pt なので、最大コーナーから 60pt 内側に
            ///   左下を置けば <b>右へ 240pt / 上へ 420pt</b> はみ出す。
            /// </summary>
            private const float OverhangInsetPoints = 60f;

            private UniWindowController _controller;
            private float _deadline;
            private float _attachedAt = -1f;
            private int _step;

            private void Start()
            {
                _controller = FindFirstObjectByType<UniWindowController>();
                _deadline = Time.realtimeSinceStartup + AttachTimeoutSeconds;
            }

            private void LateUpdate()
            {
                if (_controller == null)
                {
                    Destroy(gameObject);
                    return;
                }

                if (_attachedAt < 0f)
                {
                    WaitForAttach();
                    return;
                }

                var elapsed = Time.realtimeSinceStartup - _attachedAt;

                // 受動的な観測。枠なし化と WindowSizeKeeper が落ち着いた後をもう1回見る
                if (_step == 0 && elapsed >= 3f)
                {
                    _step = 1;
                    Report("settled");
                    return;
                }

                // ★ 引き戻し（macOS の constrainFrameRect）が起きるかを、
                //   freePositioning を切った状態と立てた状態で1回ずつ測る。
                //   入れた値がそのまま読み戻れば引き戻しは無い。
                if (_step == 1 && elapsed >= 4f)
                {
                    _step = 2;
                    MoveTo(Overhang(), "画面外(freePositioning=false)");
                    return;
                }
                if (_step == 2 && elapsed >= 5f)
                {
                    _step = 3;
                    Report("画面外(freePositioning=false)の後");
                    return;
                }

                // ★ <b>attach 済みなら実行時に立てられるはず</b>を確かめる。
                //   UniWindowController.SetFreePositioning は _uniWinCore が null の間
                //   （= attach 前）は何もせずシリアライズ値も更新しないので、
                //   「シーンに焼くしかない」のか「attach 後なら効く」のかで手当てが変わる。
                if (_step == 3 && elapsed >= 6f)
                {
                    _step = 4;
                    _controller.isFreePositioningEnabled = true;
                    Debug.Log("[Mascot] 窓の実測: isFreePositioningEnabled = true を入れました " +
                              $"→ 読み戻し {_controller.isFreePositioningEnabled}");
                    return;
                }
                if (_step == 4 && elapsed >= 7f)
                {
                    _step = 5;
                    MoveTo(Overhang(), "画面外(freePositioning=true)");
                    return;
                }
                if (_step == 5 && elapsed >= 8f)
                {
                    _step = 6;
                    Report("画面外(freePositioning=true)の後");
                    Destroy(gameObject);
                }
            }

            private void WaitForAttach()
            {
                // ★ attach 前の clientSize は (0,0)。Start() では必ずこれなので、
                //   「読めるようになった最初のフレーム」を待つしかない
                var client = _controller.clientSize;
                if (client.x > 0f && client.y > 0f)
                {
                    _attachedAt = Time.realtimeSinceStartup;
                    Report("attach");
                    return;
                }

                if (Time.realtimeSinceStartup > _deadline)
                {
                    Debug.LogWarning("[Mascot] ウィンドウを掴めないので座標系を測れませんでした: " +
                                     $"clientSize={client} Screen={Screen.width}x{Screen.height}px");
                    Destroy(gameObject);
                }
            }

            /// <summary>主モニタの作業領域から右上へはみ出す位置（左下コーナー基準）。</summary>
            private Vector2 Overhang()
            {
                var monitor = MonitorRect(0);
                return monitor.position + monitor.size - new Vector2(OverhangInsetPoints, OverhangInsetPoints);
            }

            private void MoveTo(Vector2 position, string label)
            {
                Debug.Log($"[Mascot] 窓の実測: {label} へ {Fmt(position)} を入れます " +
                          $"(freePositioning={_controller.isFreePositioningEnabled})");
                _controller.windowPosition = position;
            }

            /// <summary>
            /// ★ <b>生の数値を全部1行で出すこと。</b> これが座標系についての唯一の観測点で、
            ///   出ていない値は「後から計算し直せば分かる」ではなく<b>取り直しになる</b>
            ///   （実機のビルドは1回2分かかる）。
            /// </summary>
            private void Report(string phase)
            {
                var sb = new StringBuilder(256);
                sb.Append("[Mascot] 窓の実測(").Append(phase).Append("): ");

                var count = UniWindowController.GetMonitorCount();
                sb.Append("monitors=").Append(count);
                for (var i = 0; i < count; i++)
                {
                    var rect = MonitorRect(i);
                    sb.Append(" [").Append(i).Append("]=(").Append(Fmt(rect.position))
                      .Append(' ').Append(Fmt(rect.size)).Append(')');
                }

                var client = _controller.clientSize;
                var scale = client.y > 0f ? Screen.height / client.y : 0f;
                sb.Append(" window=(").Append(Fmt(_controller.windowPosition))
                  .Append(' ').Append(Fmt(_controller.windowSize)).Append(")pt")
                  .Append(" client=").Append(Fmt(client)).Append("pt")
                  .Append(" screen=").Append(Screen.width).Append('x').Append(Screen.height).Append("px")
                  .Append(" scale=").Append(Fmt1(scale))
                  .Append(" cursor=").Append(Fmt(UniWindowController.GetCursorPosition()))
                  .Append(" topmost=").Append(_controller.isTopmost)
                  .Append(" freePositioning=").Append(_controller.isFreePositioningEnabled);

                Debug.Log(sb.ToString());
            }

            private static Rect MonitorRect(int index) => UniWindowController.GetMonitorRect(index);

            /// <summary>★ ロケールで <c>0,5</c> にならないよう固定書式で出す。</summary>
            private static string Fmt(Vector2 v) =>
                v.x.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + "," +
                v.y.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);

            private static string Fmt1(float v) =>
                v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
