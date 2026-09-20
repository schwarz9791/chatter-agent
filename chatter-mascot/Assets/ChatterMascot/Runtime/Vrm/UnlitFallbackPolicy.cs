using UnityEngine;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// MToon10 を UniUnlit に差し替えるかどうかの判定と、差し替え時の描画モードの写像。
    ///
    /// <b>実際の材質の書き換えは <c>ChatterMascot.Vrm.UnlitFallback</c>（Vrm アセンブリ）が行う。</b>
    /// ここは <c>UnityEngine.Material</c> に触れない純粋なロジックだけを持ち、EditMode テストの対象にする。
    /// </summary>
    public static class UnlitFallbackPolicy
    {
        /// <summary>
        /// UniUnlit の描画モード（<c>UniGLTF.UniUnlit.UniUnlitRenderMode</c> と同じ並び）。
        /// ここに複製してあるのは、このクラスを <c>UnityEngine</c> 以外の依存無しに保つため。
        /// </summary>
        public enum UnlitBlend
        {
            Opaque,
            Cutout,
            Transparent,
        }

        /// <summary>
        /// ★ <b>許可リストで書くこと。</b> 「Android 以外」のような否定形にすると、
        ///   これから増える XR プラットフォームが確かめないまま巻き込まれる
        ///   （<c>AssetEnvFactory.HasSharedFileSystem</c> と同じ考え方）。
        ///   対象を広げるときは実機で症状を確かめてから1つずつ足す。
        /// </summary>
        public static bool AppliesTo(RuntimePlatform platform)
        {
            switch (platform)
            {
                case RuntimePlatform.Android:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// MToon10 の <c>_AlphaMode</c>（0: Opaque / 1: Cutout / 2: Transparent）を
        /// <see cref="UnlitBlend"/> へ写す。未知の値は <see cref="UnlitBlend.Opaque"/> に倒す。
        /// </summary>
        public static UnlitBlend MapAlphaMode(int mtoonAlphaMode)
        {
            switch (mtoonAlphaMode)
            {
                case 0: return UnlitBlend.Opaque;
                case 1: return UnlitBlend.Cutout;
                case 2: return UnlitBlend.Transparent;
                default: return UnlitBlend.Opaque;
            }
        }
    }
}
