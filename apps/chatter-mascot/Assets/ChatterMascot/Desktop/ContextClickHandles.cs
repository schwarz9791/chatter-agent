#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
using System.Collections.Generic;
using ChatterMascot.Vrm;
using Kirurobo;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ChatterMascot.Desktop
{
    /// <summary>
    /// キャラクターの<b>右クリック</b>で設定パネルを開閉する（#76）。
    ///
    /// ★ <b>左ドラッグと衝突しない。</b> 同梱の <c>UniWindowMoveHandle</c> は
    ///   <c>OnBeginDrag</c> の先頭で <c>eventData.button != Left</c> を早期 return するので、
    ///   右ボタンでは <c>_isDragging</c> が立たず、窓は動かない（上流のソースで確認済み）。
    ///
    /// ★ <b>ポインタイベントの配線は既にある。</b> <c>SceneFixups</c> が
    ///   <c>EventSystem</c> / <c>InputSystemUIInputModule</c> / <c>PhysicsRaycaster</c> を
    ///   面倒みており、<c>InputSystem_Actions</c> の <c>RightClick</c> もバインド済み。
    ///   ここで新しい配線は要らない（→ <c>docs/mascot.md</c>「EventSystem があっても
    ///   ポインタイベントは配送されない」）。
    /// </summary>
    [DisallowMultipleComponent]
    internal sealed class MascotContextClick : MonoBehaviour, IPointerClickHandler
    {
        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData == null || eventData.button != PointerEventData.InputButton.Right) return;
            StatusItemBridge.ToggleSettings();
        }
    }

    /// <summary>
    /// 右クリックのハンドルを付ける規則。<b><see cref="DragHandles"/> と同じ判定</b>にしてある。
    ///
    /// ★ <b>対象を名前で決め打ちにしない。</b> 判定は「<c>Collider</c> を持っているか」——
    ///   クリック透過のヒットテストが <c>Physics.Raycast</c> で見ているのと同じ条件なので、
    ///   <b>掴める領域と右クリックできる領域が定義上ずれない</b>。
    /// </summary>
    internal static class ContextClickHandles
    {
        public static IReadOnlyList<string> AttachAll(GameObject root)
        {
            var added = new List<string>();
            if (root == null) return added;

            // ★ ウィンドウ制御が居ないシーンでは付けない（TransparencyProbe など）
            if (Object.FindFirstObjectByType<UniWindowController>() == null) return added;

            // 非アクティブも含める。フォールバックの Cube は読み込み成功で
            // SetActive(false) されるが、失敗すれば出しっぱなしになる
            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
            {
                var go = collider.gameObject;
                if (go.GetComponent<MascotContextClick>() != null) continue;

                go.AddComponent<MascotContextClick>();
                added.Add(go.name);
            }
            return added;
        }
    }

    /// <summary>
    /// 右クリックのハンドルを、シーンの当たり判定と読み込んだ VRM の両方へ付ける配線。
    ///
    /// ★★ <b><c>MonoBehaviour</c> をシーンに焼かないこと。</b> シーンは
    ///   <c>m_Script</c> の GUID を asmdef の <c>includePlatforms</c> と無関係に
    ///   シリアライズするので、Android ではこのアセンブリが無く
    ///   「The referenced script on this Behaviour is missing!」が1本出るだけになる
    ///   （→ <c>docs/mascot.md</c>）。<c>SceneFixups</c> がドラッグハンドルを焼いているのは
    ///   あれが<b>パッケージ側</b>の <c>MonoBehaviour</c> だから。
    ///
    /// ★ <b><c>VrmDragHandleBinder</c> と別のクラスにしてある。</b> あちらは
    ///   「掴める＝動かせる」の規則、こちらは「掴める＝右クリックできる」の規則で、
    ///   片方だけ止めたくなることがある（<c>DragStateGuard</c> の救済対象はドラッグだけ）。
    /// </summary>
    public static class ContextClickBinder
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bind()
        {
            var stage = Object.FindFirstObjectByType<VrmStage>(FindObjectsInactive.Include);
            if (stage == null) return;   // TransparencyProbe.unity など VRM を出さないシーン

            // ★ シーンに最初から居る当たり判定（ModelPlaceholder）にも付けること。
            //   VRM の読み込みに失敗したときは、そちらが唯一の掴める領域になる
            Log(ContextClickHandles.AttachAll(stage.gameObject));

            // ★ sticky。もう読み終わっていたら即座に呼ばれる
            stage.AddLoadedHandler(model => Log(ContextClickHandles.AttachAll(model)));
        }

        /// <summary>
        /// ★ <b>付けたことを1行残すこと。</b> 右クリックが効かないとき、
        ///   「ハンドルが付いていない」のか「ポインタイベントが届いていない」のかで
        ///   手当てが全く違う（→ <c>docs/mascot.md</c>「EventSystem があっても
        ///   ポインタイベントは配送されない」）。
        /// </summary>
        private static void Log(IReadOnlyList<string> names)
        {
            if (names == null || names.Count == 0) return;
            Debug.Log("[Mascot] 右クリックのハンドルを付けました: " + string.Join(", ", names));
        }
    }
}
#endif
