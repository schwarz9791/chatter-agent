using System.Collections.Generic;
using System.Text;
using UniGLTF;
using UnityEngine;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// 読み込んだモデルのシェーダーが生きているかを見る。
    ///
    /// ★ <b>これが無いと、シェーダーストリッピングの症状が診断できない。</b>
    ///   <c>UrpVrm10MToon10MaterialImporter</c> は <c>Shader.Find</c> でシェーダーを引くが、
    ///   <b>シーンのマテリアルから参照されないシェーダーはビルドから落ちる</b>ので、
    ///   ランタイムロードでは確実に踏む。症状は「モデルは読めるのに真っ黒/ピンク」で
    ///   <b>例外が1つも出ない</b>。
    ///
    /// 手当ては <c>SceneFixups.EnsureAlwaysIncludedShaders()</c>。ここは検査だけ。
    /// </summary>
    public static class VrmMaterialCheck
    {
        public const string MToonUrpShaderName = "VRM10/Universal Render Pipeline/MToon10";
        public const string UniUnlitShaderName = "UniGLTF/UniUnlit";

        /// <summary>
        /// ★ <b>読み込みより前に呼ぶこと。</b> 読み込んでから気づくと、
        ///   「読めたのに真っ黒」と「そもそもシェーダーが無い」の区別がつかない。
        /// </summary>
        public static void WarnIfShadersStripped()
        {
            if (Shader.Find(MToonUrpShaderName) != null) return;

            Debug.LogError(
                $"[Mascot] {MToonUrpShaderName} がビルドに含まれていません。" +
                "モデルは読めても真っ黒／ピンクになります。" +
                "Always Included Shaders を確認してください" +
                "（./scripts/run.sh ChatterMascot.EditorTools.SceneFixups.FixAll）");
        }

        /// <summary>
        /// 読み込み直後のマテリアルを検査する。壊れていたら名前を並べて <c>LogError</c>。
        /// </summary>
        public static void Inspect(RuntimeGltfInstance instance)
        {
            if (instance == null) return;

            var broken = new List<string>();
            var names = new HashSet<string>();

            foreach (var material in instance.Materials)
            {
                if (material == null)
                {
                    broken.Add("(null)");
                    continue;
                }

                var shader = material.shader;
                if (shader == null || !shader.isSupported || shader.name == "Hidden/InternalErrorShader")
                {
                    broken.Add(material.name + " → " + (shader == null ? "(null)" : shader.name));
                }
                else
                {
                    names.Add(shader.name);
                }
            }

            if (broken.Count == 0)
            {
                Debug.Log($"[Mascot] マテリアル {instance.Materials.Count} 件、シェーダー: " +
                          string.Join(" / ", names));
                return;
            }

            var text = new StringBuilder();
            foreach (var one in broken) text.Append("\n  ").Append(one);
            Debug.LogError(
                $"[Mascot] シェーダーが解決できていないマテリアルが {broken.Count} 件あります。" +
                "Always Included Shaders を確認してください:" + text);
        }
    }
}
