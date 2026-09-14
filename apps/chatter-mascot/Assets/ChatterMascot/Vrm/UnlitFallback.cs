using System.Collections.Generic;
using UniGLTF.UniUnlit;
using UnityEngine;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// 読み込み済みモデルの MToon10 マテリアルを UniUnlit へ差し替える。
    ///
    /// ★ <b>Android では MToon10 の陰影計算が白飛びするので、陰影計算を持たない
    ///   UniUnlit で描く。</b>
    /// ★ <b>光学シースルーのグラスでは、暗い色は背景光にどのみち洗われる。</b>
    ///   陰影を失ってフラットな発色になっても、その環境では違和感になりにくい。
    /// ★ <b>表情（ブレンドシェイプ）は影響を受けない。</b> シェーダーを跨いでも
    ///   メッシュの頂点変形自体は変わらない。
    /// ★ <b>失うもの：アウトライン・陰の階調・リムライト（matcap を含む）。</b> UniUnlit には
    ///   いずれの機能も無い。
    /// </summary>
    public static class UnlitFallback
    {
        // MToon10 URP シェーダー（VrmMaterialCheck.MToonUrpShaderName）のプロパティ名。
        // ★ 文字列で直に触るのは VrmMaterialCheck と同じ形。MToon10 の C# 型
        //   （VRM10.MToon10.*）に依存すると、参照するアセンブリが増える
        private const string MToonAlphaModeProperty = "_AlphaMode";
        private const string MToonCutoffProperty = "_Cutoff";
        private const string MToonColorProperty = "_Color";
        private const string MToonMainTexProperty = "_MainTex";

        // MToon10Prop.TransparentWithZWrite（MToon10Properties.ToUnityShaderLabName）。
        // Transparent で ZWrite を維持するかどうかのフラグ
        private const string MToonTransparentWithZWriteProperty = "_TransparentWithZWrite";

        /// <summary>
        /// MToon10 の実効カリングモード。<c>_DoubleSided</c> から
        /// <c>MToonValidator.Validate</c>（読み込み時に既に呼ばれている）が
        /// 解決した値がここに入る。<b>値は UniUnlit の <see cref="UniUnlitCullMode"/> と
        /// 同じ並び（Off=0 / Back=2）なので、素通しできる。</b>
        /// </summary>
        private const string MToonCullModeProperty = "_M_CullMode";

        /// <summary>
        /// <paramref name="root"/> 配下の Renderer が持つ MToon10 マテリアルを
        /// UniUnlit に差し替える。<b>同じマテリアルを複数の Renderer が共有していても
        /// 1回しか変換しない。</b>
        /// </summary>
        /// <returns>差し替えたマテリアルの数。</returns>
        public static int Apply(GameObject root)
        {
            if (root == null) return 0;

            var unlitShader = Shader.Find(VrmMaterialCheck.UniUnlitShaderName);
            if (unlitShader == null)
            {
                Debug.LogWarning(
                    $"[Mascot] {VrmMaterialCheck.UniUnlitShaderName} が見つからないので MToon の差し替えをスキップしました");
                return 0;
            }

            var converted = new HashSet<Material>();

            // ★ 非アクティブも含めること。表情差分などで無効化されているメッシュが
            //   後から有効化されても、その時点でもう MToon10 のまま白飛びしないように
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null) continue;
                    if (material.shader == null) continue;
                    if (material.shader.name != VrmMaterialCheck.MToonUrpShaderName) continue;
                    if (!converted.Add(material)) continue;

                    Convert(material, unlitShader);
                }
            }

            return converted.Count;
        }

        private static void Convert(Material material, Shader unlitShader)
        {
            // ★ シェーダーを差し替える前に読むこと。差し替えた後は MToon10 のプロパティが
            //   マテリアルから引けるとは限らない
            var mainTexture = material.GetTexture(MToonMainTexProperty);
            var mainTextureOffset = material.GetTextureOffset(MToonMainTexProperty);
            var mainTextureScale = material.GetTextureScale(MToonMainTexProperty);
            var color = material.GetColor(MToonColorProperty);
            var cutoff = material.GetFloat(MToonCutoffProperty);
            var blend = UnlitFallbackPolicy.MapAlphaMode(material.GetInt(MToonAlphaModeProperty));
            var cullMode = (UniUnlitCullMode)material.GetInt(MToonCullModeProperty);
            var renderQueue = material.renderQueue;
            var zWrite = material.GetInt(MToonTransparentWithZWriteProperty) != 0;

            material.shader = unlitShader;

            var context = new UniUnlitContext(material)
            {
                // ★ 1プロパティごとに ValidateProperties を走らせない。
                //   最後に Validate() を1回呼べば同じ結果になる
                UnsafeEditMode = true,
                MainTexture = mainTexture,
                MainColorSrgb = color,
                Cutoff = cutoff,
                RenderMode = ToUniUnlitRenderMode(blend),
                CullMode = cullMode,
            };
            material.SetTextureOffset(UniUnlitUtil.PropNameMainTex, mainTextureOffset);
            material.SetTextureScale(UniUnlitUtil.PropNameMainTex, mainTextureScale);
            context.Validate();

            // ★ Validate() はブレンドモードごとに renderQueue と ZWrite を固定値へ揃える
            //   （UniUnlitUtil.ValidateProperties の isRenderModeChangedByUser: true）。
            //   モデル側が意図した描画順（同一メッシュ内のサブメッシュの重なり順）を保つため、
            //   差し替え前の値をここで書き戻す。
            material.renderQueue = renderQueue;
            if (zWrite) material.SetInt(UniUnlitUtil.PropNameZWrite, 1);
        }

        private static UniUnlitRenderMode ToUniUnlitRenderMode(UnlitFallbackPolicy.UnlitBlend blend)
        {
            switch (blend)
            {
                case UnlitFallbackPolicy.UnlitBlend.Cutout: return UniUnlitRenderMode.Cutout;
                case UnlitFallbackPolicy.UnlitBlend.Transparent: return UniUnlitRenderMode.Transparent;
                default: return UniUnlitRenderMode.Opaque;
            }
        }
    }
}
