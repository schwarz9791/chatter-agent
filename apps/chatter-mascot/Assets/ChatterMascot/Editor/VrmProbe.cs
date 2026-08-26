using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using ChatterMascot.Vrm;
using UniGLTF;
using UniGLTF.Extensions.VRMC_vrm;
using UnityEditor;
using UnityEngine;
using UniVRM10;

namespace ChatterMascot.EditorTools
{
    /// <summary>
    /// 解決済みのパスから VRM を読んで中身を報告する。
    /// <c>./scripts/run.sh ChatterMascot.EditorTools.VrmProbe.Report</c>
    ///
    /// <b>「モデル側の問題」と「コード側の問題」を先に切り分けるための道具。</b>
    /// メタデータを書き換えた前後の突き合わせ（マテリアル数・シェーダー名・テクスチャ数）と、
    /// 自動フレーミングが使う bounds の確認に使う。
    ///
    /// ★ <b>モデルが無ければスキップして正常終了する。</b> CI（#54）で落とさない。
    /// ★ ログは <c>[VrmProbe]</c> で始めること（<c>scripts/run.sh</c> の grep）。
    /// </summary>
    public static class VrmProbe
    {
        public static void Report()
        {
            var env = ProbeEnv();
            var candidates = AssetPath.Enumerate(env, AssetKind.Vrm);

            var path = candidates.Select(c => c.Path).FirstOrDefault(System.IO.File.Exists);
            if (path == null)
            {
                Log("モデルが見つからないのでスキップします。探した順:" +
                    VrmAssetLoader.DescribeCandidates(candidates));
                EditorApplication.Exit(0);
                return;
            }

            Debug.Log($"[VrmProbe] 読みます: {path}");

            Vrm10Instance instance = null;
            try
            {
                // ★ Editor の batchmode では Play していないので、UniVRM が
                //   ImmediateCaller に自動で倒れる（awaitCaller は null のままでよい）
                var task = Vrm10.LoadPathAsync(
                    path,
                    canLoadVrm0X: false,
                    controlRigGenerationOption: ControlRigGenerationOption.Generate,
                    showMeshes: true,
                    ct: CancellationToken.None);
                task.Wait();
                instance = task.Result;
            }
            catch (System.Exception e)
            {
                Debug.LogError("[VrmProbe] 読み込みに失敗しました: " + e);
                EditorApplication.Exit(1);
                return;
            }

            if (instance == null)
            {
                Debug.LogError("[VrmProbe] 読み込みに失敗しました（null が返りました）");
                EditorApplication.Exit(1);
                return;
            }

            Describe(instance);
            EditorApplication.Exit(0);
        }

        private static void Describe(Vrm10Instance instance)
        {
            var text = new StringBuilder();
            var meta = instance.Vrm != null ? instance.Vrm.Meta : null;
            if (meta != null)
            {
                text.Append("\n  name: ").Append(meta.Name);
                text.Append("\n  authors: ").Append(meta.Authors != null ? string.Join(", ", meta.Authors) : "");
                text.Append("\n  otherLicenseUrl: ").Append(meta.OtherLicenseUrl);
                text.Append("\n  commercialUsage: ").Append(meta.CommercialUsage);
                text.Append("\n  modification: ").Append(meta.Modification);
                text.Append("\n  avatarPermission: ").Append(meta.AvatarPermission);
                text.Append("\n  creditNotation: ").Append(meta.CreditNotation);
                text.Append("\n  redistribution: ").Append(meta.Redistribution);
            }

            var gltf = instance.GetComponent<RuntimeGltfInstance>();
            if (gltf != null)
            {
                var shaders = new SortedSet<string>();
                foreach (var material in gltf.Materials)
                {
                    shaders.Add(material == null || material.shader == null
                        ? "(null)"
                        : material.shader.name);
                }
                text.Append("\n  materials: ").Append(gltf.Materials.Count);
                text.Append("\n  shaders: ").Append(string.Join(" / ", shaders));
                text.Append("\n  renderers: ").Append(gltf.Renderers.Count);

                var bounds = Bounds(gltf);
                text.Append("\n  bounds size: ").Append(bounds.size);
                text.Append("\n  bounds center: ").Append(bounds.center);
                // ★ ウィンドウのアスペクトを決める材料（→ SETUP.md のウィンドウの大きさ）
                text.Append("\n  bounds W/H: ").Append((bounds.size.x / Mathf.Max(bounds.size.y, 1e-6f)).ToString("F3"));
            }

            if (instance.Vrm != null && instance.Vrm.Expression != null)
            {
                // ★ Clips は (Preset, Clip) のタプル列。Clip が null の枠も混ざる
                var keys = instance.Vrm.Expression.Clips
                    .Where(pair => pair.Clip != null)
                    .Select(pair => pair.Preset == ExpressionPreset.custom
                        ? pair.Clip.name
                        : pair.Preset.ToString());
                text.Append("\n  expressions: ").Append(string.Join(", ", keys.OrderBy(k => k)));
            }

            var animator = instance.GetComponent<Animator>();
            if (animator != null && animator.avatar != null)
            {
                var missing = new List<string>();
                foreach (HumanBodyBones bone in System.Enum.GetValues(typeof(HumanBodyBones)))
                {
                    if (bone == HumanBodyBones.LastBone) continue;
                    if (animator.GetBoneTransform(bone) == null) missing.Add(bone.ToString());
                }
                text.Append("\n  humanoid: ")
                    .Append((int)HumanBodyBones.LastBone - missing.Count)
                    .Append(" / ").Append((int)HumanBodyBones.LastBone);
                // 目が無いと #59 の lookAt が効かないので、そこだけ名指しで見る
                text.Append("\n  eyes: ")
                    .Append(animator.GetBoneTransform(HumanBodyBones.LeftEye) != null ? "left " : "")
                    .Append(animator.GetBoneTransform(HumanBodyBones.RightEye) != null ? "right" : "");
            }

            Log(text.ToString());
        }

        /// <summary>
        /// ★ <b>行ごとに <c>[VrmProbe]</c> を付けること。</b> <c>scripts/run.sh</c> は
        ///   <c>grep -E "^\[VrmProbe\]…"</c> で絞るので、複数行のログは
        ///   <b>2行目以降が丸ごと消える</b>（1行目だけ出て中身が空に見える）。
        /// </summary>
        private static void Log(string text)
        {
            Debug.Log("[VrmProbe] " + text.Replace("\n", "\n[VrmProbe] "));
        }

        private static Bounds Bounds(RuntimeGltfInstance instance)
        {
            var bounds = new Bounds();
            var first = true;
            foreach (var renderer in instance.Renderers)
            {
                if (renderer == null || renderer.bounds.size == Vector3.zero) continue;
                if (first)
                {
                    bounds = renderer.bounds;
                    first = false;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }
            return bounds;
        }

        /// <summary>
        /// ★ <b>Editor から呼ぶので <see cref="AssetEnvFactory"/> をそのまま使えない</b> ——
        ///   <c>Application.streamingAssetsPath</c> は Editor でも
        ///   <c>&lt;project&gt;/Assets/StreamingAssets</c> を返すので使えるが、
        ///   <c>persistentDataPath</c> は Editor 固有の場所を指す。
        ///   ここは<b>同梱と起動引数だけ</b>見れば足りる。
        /// </summary>
        private static AssetEnv ProbeEnv()
        {
            var env = AssetEnvFactory.Current();
            env.PersistentDataPath = "";
            return env;
        }
    }
}
