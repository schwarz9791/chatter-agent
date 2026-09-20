using ChatterMascot.Vrm;
using UnityEngine;

namespace ChatterMascot.Desktop
{
    /// <summary>
    /// 読み込まれた VRM にドラッグハンドルを付ける配線。
    ///
    /// ★ <b><c>MonoBehaviour</c> にしない。シーンに置かない。</b>
    ///   シーンは <c>MonoBehaviour</c> を <c>m_Script</c> の GUID として持つだけで、
    ///   <b>asmdef の <c>includePlatforms</c> と無関係に常にシリアライズされる</b>。
    ///   このアセンブリは Android でコンパイルされないので解決先が無くなり、
    ///   ビルドエラーではなく<b>シーンロード時の
    ///   "The referenced script on this Behaviour is missing!" が1本出るだけ</b>になる。
    ///   症状は「Android で掴めない」、原因は <c>Player.log</c> の1行 ——
    ///   このリポジトリが繰り返し潰してきた「動いて見える死体」の形。
    ///
    ///   <c>RuntimeInitializeOnLoadMethod</c> なら、Android では<b>アセンブリごと存在しない</b>ので
    ///   属性の走査対象にすらならない。<c>#if</c> もプラットフォーム分岐も要らず、
    ///   切り分けが asmdef 1箇所に閉じる。
    ///
    /// ★ <b><c>AfterSceneLoad</c> の順序に寄りかからないこと。</b> これは全 <c>Awake</c> の後・
    ///   最初の <c>Start</c> の前に走るので <see cref="VrmStage"/> の読み込み開始には
    ///   間に合うが、その保証に依存せず <c>AddLoadedHandler</c> 側を sticky にしてある。
    /// </summary>
    public static class VrmDragHandleBinder
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bind()
        {
            var stage = Object.FindFirstObjectByType<VrmStage>(FindObjectsInactive.Include);
            if (stage == null) return;   // TransparencyProbe.unity など VRM を出さないシーン

            stage.AddLoadedHandler(model => DragHandles.AttachAll(model, true));
        }
    }
}
