using Kirurobo;
using UnityEngine;

namespace ChatterMascot.Desktop
{
    /// <summary>
    /// <b>ドラッグ終了の取りこぼしでクリック透過が死んだままになるのを救う。</b>
    /// 同時に、<b>それが実際に起きているのかどうかをログで答える。</b>
    ///
    /// <b>何が起きるか。</b> 同梱の <c>UniWindowMoveHandle</c> はドラッグ中だけヒットテストを切る:
    ///
    /// <code>
    /// // OnBeginDrag
    /// _isHitTestEnabled = _uniwinc.isHitTestEnabled;
    /// _uniwinc.isHitTestEnabled = false;
    /// _uniwinc.isClickThrough = false;
    /// // EndDragging —— 戻すのはここだけ
    /// if (_isDragging) _uniwinc.isHitTestEnabled = _isHitTestEnabled;
    /// </code>
    ///
    /// <c>UniWindowController.UpdateClickThrough()</c> は先頭で
    /// <c>if (!isHitTestEnabled …) return;</c> するので、<b><c>EndDragging</c> が呼ばれないと
    /// クリック透過が二度と復活しない。</b> 上流にも
    /// <c>// Macの場合、マルチモニター間を移動するとEventSystemのOnEndDragが正しく呼ばれないため、マウスボタンを常に監視</c>
    /// というコメントアウト済みの懸念が残っている。
    /// <a href="https://github.com/schwarz9791/chatter-agent/issues/16">#16</a> のコメント1
    /// （画面外へドラッグするとクリック透過が効かなくなる）の症状と一致する。
    ///
    /// ★ <b>上流をフォークしない。</b> 外から状態を見て戻すだけにすれば、
    ///   パッケージを上げても壊れない。
    ///
    /// ★ <b>この警告が出るかどうかが、そのまま切り分けになる。</b>
    ///   出れば上流のバグ（こちらが救っている）。<b>出ないのにクリック透過が効かないなら別原因</b>。
    ///   #16 のコメント1 が求めている「まず <c>isHitTestEnabled</c> が戻っているか見る」を、
    ///   人が Inspector を覗くのではなくログで答えるためにここに置いている。
    ///
    /// ★ <b><c>isClickThrough</c> は自分で書かないこと。</b> <c>isHitTestEnabled</c> さえ戻れば
    ///   <c>UpdateClickThrough</c> が次フレームで正しい値にする。両方書くと二重に書き合う。
    ///
    /// ★ <b><see cref="MonoBehaviour"/> をシーンに置かないこと</b>（→ <see cref="VrmDragHandleBinder"/>）。
    /// </summary>
    public static class DragStateGuard
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            // Editor ではクリック透過そのものが働かない（UniWindowController の制限）
            if (Application.isEditor) return;
            if (Object.FindFirstObjectByType<UniWindowController>() == null) return;

            var go = new GameObject(nameof(DragStateGuard)) { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(go);
            go.AddComponent<Guard>();
        }

        private sealed class Guard : MonoBehaviour
        {
            /// <summary>
            /// ボタンが離れているのにヒットテストが切れたまま、と認めるまでの猶予。
            ///
            /// ★ <b>0 にしないこと。</b> ドラッグの最後のフレームでは
            ///   「ボタンは離れたが <c>EndDragging</c> はまだ」という1〜2フレームの窓が
            ///   正常系にもある。そこで割り込むと、正常なドラッグのたびに警告が出る。
            /// </summary>
            private const float StuckSeconds = 2f;

            private UniWindowController _controller;
            private float _offSince = -1f;
            private int _restored;

            private void Start()
            {
                _controller = FindFirstObjectByType<UniWindowController>();
            }

            private void LateUpdate()
            {
                if (_controller == null)
                {
                    Destroy(gameObject);
                    return;
                }

                // ★ 正常系ではここで抜ける。フィールドを読むだけで P/Invoke は走らない
                if (_controller.isHitTestEnabled)
                {
                    _offSince = -1f;
                    return;
                }

                // ★ Mouse.current / Input.mousePosition は使えない。
                //   常駐マスコットは基本フォーカスを持たない（→ CursorGazeSource と同じ理由）
                var buttons = UniWindowController.GetMouseButtons();
                if ((buttons & UniWindowController.MouseButton.Left) != UniWindowController.MouseButton.None)
                {
                    // 誰かが左ボタンを押している = ドラッグ中。正常
                    _offSince = -1f;
                    return;
                }

                var now = Time.realtimeSinceStartup;
                if (_offSince < 0f)
                {
                    _offSince = now;
                    return;
                }
                if (now - _offSince < StuckSeconds) return;

                _offSince = -1f;
                _restored++;
                _controller.isHitTestEnabled = true;
                Debug.LogWarning(
                    $"[Mascot] ドラッグ終了を取りこぼしていたのでヒットテストを戻しました（{_restored} 回目）。" +
                    "クリック透過が効かない状態が続いていました");
            }
        }
    }
}
