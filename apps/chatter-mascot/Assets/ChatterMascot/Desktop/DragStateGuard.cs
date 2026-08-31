using Kirurobo;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ChatterMascot.Desktop
{
    /// <summary>
    /// <b>ドラッグ終了の取りこぼしから復帰する。</b>
    /// 同時に、<b>それが実際に起きているのかどうかをログで答える。</b>
    ///
    /// <b>何が起きるか。</b> 同梱のドラッグ用ハンドルは、掴んでいる間だけウィンドウの
    /// ヒットテストを切り、離したときに戻す。<b>「離した」を受け取り損ねると、切ったまま残る。</b>
    /// ヒットテストが切れている間はクリック透過の再判定そのものが走らないので、
    /// <b>透過が二度と復活しない。</b> 上流にも、マルチモニタ間の移動で終了通知が
    /// 正しく届かない、というコメントアウトされた懸念が残っている。
    /// → <a href="https://github.com/schwarz9791/chatter-agent/issues/16">#16</a> のコメント1 の症状と一致する。
    ///
    /// ★ <b>上流をフォークしない。</b> 外から状態を見て戻すだけにすれば、
    ///   パッケージを上げても壊れない。
    ///
    /// ★ <b>ヒットテストを自分で書き戻さない。</b> 代わりに<b>ハンドルに「離した」を渡す</b>。
    ///   自分で書き戻すと<b>ハンドルの側は掴んだままだと思い込んだまま</b>になり、
    ///   次に掴んだときヒットテストが切られない。すると掴んでいる最中に透過の再判定が走り、
    ///   透明な部分にカーソルが乗った瞬間に入力が下へ抜けて<b>ドラッグが途中で外れる</b>。
    ///   ハンドルに渡せば、掴んでいる状態も、戻すべきヒットテストの値も、
    ///   <b>上流の作法どおりに戻る</b>。
    ///
    /// ★ <b>この警告が出るかどうかが、そのまま切り分けになる。</b>
    ///   出れば上流の取りこぼし（こちらが救っている）。<b>出ないのにクリック透過が効かないなら
    ///   別の原因</b>。#16 のコメント1 が求めている「まずヒットテストが戻っているか見る」を、
    ///   人が Inspector を覗くのではなくログで答えるためにここに置いている。
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
            /// ★ <b>0 にしないこと。</b> ドラッグの最後には
            ///   「ボタンは離れたが、ハンドルがまだ受け取っていない」という数フレームの窓が
            ///   正常系にもある。そこで割り込むと、普通のドラッグのたびに警告が出る。
            /// </summary>
            private const float StuckSeconds = 2f;

            private UniWindowController _controller;
            private PointerEventData _spentEvent;
            private float _offSince = -1f;
            private int _recovered;

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

                // ★ 新しい入力系のマウス状態は使えない。常駐マスコットは基本フォーカスを
                //   持たない（→ CursorGazeSource と同じ理由）。ウィンドウ制御側から読む
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
                Recover();
            }

            /// <summary>
            /// ★ <b>渡したうえで検算する。</b> 掴んだままのハンドルが1つも見つからないことは
            ///   実際に起きる（読み込み直しでモデルごと消えるなど）。そのときは
            ///   <b>外から戻す以外に復帰手段が無い</b>。
            ///   「何もしない」を選ぶと、切れたままなので入口の早期 return に戻れず、
            ///   <b>警告が永久に繰り返される</b>。
            ///
            /// ★ 掴んだままのハンドルが複数あると、どれが最後に書くかで結果が変わる。
            ///   だから<b>渡した後にもう一度読む</b>。
            /// </summary>
            private void Recover()
            {
                var handed = 0;
                // ★ 非アクティブも含めること。フォールバックの立方体は読み込み成功で
                //   非アクティブになるので、掴んでいる最中に非アクティブ化されたハンドルが残りうる。
                //   ★ 異常時にしか走らないので、ここでの探索コストは問題にならない
                var handles = FindObjectsByType<UniWindowMoveHandle>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                foreach (var handle in handles)
                {
                    if (handle == null || !handle.IsDragging) continue;
                    handle.OnPointerUp(SpentEvent());
                    handed++;
                }

                if (_controller.isHitTestEnabled)
                {
                    _recovered++;
                    Debug.LogWarning(
                        $"[Mascot] ドラッグ終了の取りこぼしから復帰しました（{_recovered} 回目 / " +
                        $"ハンドル {handed} 件）。クリック透過が効かない状態が続いていました");
                    return;
                }

                _controller.isHitTestEnabled = true;
                _recovered++;
                Debug.LogWarning(
                    $"[Mascot] ヒットテストを直接戻しました（{_recovered} 回目 / " +
                    $"掴んだままのハンドル {handed} 件）。ハンドル側では戻せない状態でした");
            }

            /// <summary>
            /// ハンドルに渡す、中身の無いポインタイベント。
            ///
            /// ★ <b><c>null</c> を渡さないこと。</b> 受け手がいま引数を見ないのは
            ///   <b>現在の実装の都合</b>であって契約ではない。参照され始めると
            ///   <b>救済が例外で止まり、症状は「透過が死んだまま」なので気づけない</b>。
            /// </summary>
            private PointerEventData SpentEvent() =>
                _spentEvent ?? (_spentEvent = new PointerEventData(EventSystem.current));
        }
    }
}
