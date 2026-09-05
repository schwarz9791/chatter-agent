using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ChatterMascot.Vrm;
using UniGLTF;
using UnityEditor;
using UnityEngine;
using UniVRM10;

namespace ChatterMascot.EditorTools
{
    /// <summary>
    /// Unity の Humanoid <c>AnimationClip</c>（<c>.anim</c>。muscle カーブ）を <c>.vrma</c> に変換する。
    /// <c>./scripts/run.sh ChatterMascot.EditorTools.VrmaExport.Batch -vrmaClipDir &lt;dir&gt; -vrmaOutDir &lt;dir&gt; [-vrmaClip &lt;name&gt; | -vrmaAll &lt;category&gt;]</c>
    ///
    /// <b>#70 の素材を VRoid Studio から起こすための道具</b>（→ <c>_workspace/vroid-clips/README.md</c>）。
    /// 出力は再配布できないので <c>~/.config/chatter-agent/animations/&lt;category&gt;/</c> にだけ置く。
    /// このスクリプト自体は自作なのでコミットする（あとでモーションを足すときの再現性のため）。
    ///
    /// 雛形は UniVRM の <c>Editor/VrmAnimationMenu.cs</c> の <c>BvhToVrmAnimation</c>（<c>internal</c> なので写経）。
    /// BVH を読む部分を「Humanoid クリップを同梱 <c>vita.vrm</c> に当ててサンプリング」に差し替えてある。
    /// muscle → ボーン回転のデコードは <b>Unity の Avatar が解く</b>ので、ここでは muscle 値を一切解釈しない。
    ///
    /// ★ <b>ログは1行ごとに <c>[VrmaExport]</c> を付けること。</b> <c>scripts/run.sh</c> は
    ///   プレフィックスで grep するので、複数行のログは2行目以降が消える（<c>VrmProbe.Log</c> と同じ）。
    /// </summary>
    public static class VrmaExport
    {
        /// <summary>
        /// クリップ名 → 置き先のカテゴリ。<b>同じクリップを複数カテゴリに置くときは同じバイト列を2箇所に書く。</b>
        /// カテゴリ名は #70 のとおり VRM の表情プリセットと同名（<c>neutral</c> は無い。待機のまま）。
        /// </summary>
        private static readonly (string Clip, string[] Categories)[] Table =
        {
            ("Hub_Idle01", new[] { "idle", "relaxed" }),
            ("Hub_Idle03", new[] { "idle" }),
            ("Hub_Idle04", new[] { "idle" }),
            ("Female_Standby9", new[] { "idle", "relaxed" }),
            ("Female_Standby10", new[] { "idle" }),
            ("Hub_laugh01", new[] { "happy" }),
            ("super_delicious", new[] { "happy" }),
            ("WIN00", new[] { "happy" }),
            ("determined", new[] { "angry" }),
            ("LOSE00", new[] { "sad" }),
            ("REFLESH00", new[] { "sad" }),
            ("elated", new[] { "surprised" }),
            ("DAMAGED01", new[] { "surprised" }),
        };

        /// <summary>
        /// <c>.anim</c> を一時的に置く場所。<b><c>Assets/</c> 配下でないと <c>AssetDatabase</c> で読めない。</b>
        /// <c>~</c> 末尾のフォルダは Unity が丸ごと無視するので使えない。
        /// 終わったら <see cref="AssetDatabase.DeleteAsset"/> で <c>.meta</c> ごと消す。
        /// ★ 途中で落ちたときの保険として <c>.gitignore</c> にもこのフォルダを書いてある。
        /// </summary>
        private const string ClipFolder = "Assets/ChatterMascot/Editor/VRoidClips";

        /// <summary>
        /// 書き出しから外すボーン。<b>視線は #59 の <c>LookAt</c> が持つ</b>ので、VRMA に目を書くと奪い合う。
        /// 同梱 <c>idle_loop.vrma</c> の 22 本にも目・顎は無い。
        /// </summary>
        private static readonly HashSet<HumanBodyBones> ExcludedBones = new HashSet<HumanBodyBones>
        {
            HumanBodyBones.LeftEye, HumanBodyBones.RightEye, HumanBodyBones.Jaw,
        };

        /// <summary>「動いた」とみなす回転角のしきい値（度）。</summary>
        private const float MovedThresholdDegrees = 0.01f;

        public static void Batch()
        {
            var clipDir = ExpandHome(CommandLine.Argument("-vrmaClipDir"));
            var outDir = ExpandHome(CommandLine.Argument("-vrmaOutDir"));
            var only = CommandLine.Argument("-vrmaClip");
            // ★ 対応表を無視して clipDir の .anim を全部 1 つのカテゴリへ書く（素材の棚卸し用。
            //   VRoid Studio の全クリップを idle/ に並べて設定パネルの「モーションを確認」で見る）
            var all = CommandLine.Argument("-vrmaAll");

            if (string.IsNullOrEmpty(clipDir) || string.IsNullOrEmpty(outDir))
            {
                Debug.LogError("[VrmaExport] -vrmaClipDir と -vrmaOutDir の両方を指定してください");
                EditorApplication.Exit(2);
                return;
            }
            if (!Directory.Exists(clipDir))
            {
                Debug.LogError($"[VrmaExport] -vrmaClipDir が見つかりません: {clipDir}");
                EditorApplication.Exit(2);
                return;
            }

            var targets = string.IsNullOrEmpty(all)
                ? Table.Where(row => string.IsNullOrEmpty(only) || row.Clip == only).ToList()
                : Directory.GetFiles(clipDir, "*.anim")
                    .Select(f => (Clip: Path.GetFileNameWithoutExtension(f), Categories: new[] { all }))
                    .OrderBy(row => row.Clip, StringComparer.Ordinal)
                    .ToList();
            if (targets.Count == 0)
            {
                Debug.LogError($"[VrmaExport] -vrmaClip {only} は対応表にありません（-vrmaAll なら .anim が 1 本も無い）");
                EditorApplication.Exit(2);
                return;
            }

            var failed = new List<string>();
            try
            {
                // ★ 先に全部コピーして Refresh を1回で済ませる。1本ごとに Refresh すると遅い
                Directory.CreateDirectory(ClipFolder);
                foreach (var row in targets)
                {
                    var src = Path.Combine(clipDir, row.Clip + ".anim");
                    if (!File.Exists(src))
                    {
                        Debug.LogWarning($"[VrmaExport] {row.Clip}: 入力が無いのでスキップします: {src}");
                        failed.Add(row.Clip);
                        continue;
                    }
                    File.Copy(src, Path.Combine(ClipFolder, row.Clip + ".anim"), overwrite: true);
                }
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

                foreach (var row in targets)
                {
                    if (failed.Contains(row.Clip)) continue;
                    try
                    {
                        Convert(row.Clip, row.Categories, outDir);
                    }
                    catch (Exception e)
                    {
                        // ★ 1本の失敗で残りを止めない。理由は1行にまとめる（run.sh の grep のため）
                        Debug.LogError($"[VrmaExport] {row.Clip}: 変換に失敗しました: {e.GetType().Name}: {e.Message.Replace("\n", " ")}");
                        failed.Add(row.Clip);
                    }
                }
            }
            finally
            {
                // ★ .meta ごと消す。Directory.Delete だと .meta が残って Unity が警告を出し続ける
                AssetDatabase.DeleteAsset(ClipFolder);
            }

            if (failed.Count > 0)
            {
                Debug.LogError($"[VrmaExport] 失敗: {failed.Count} 本 ({string.Join(", ", failed)})");
                EditorApplication.Exit(1);
                return;
            }
            Debug.Log($"[VrmaExport] 完了: {targets.Count} 本");
            EditorApplication.Exit(0);
        }

        /// <summary>
        /// 1クリップを変換して、カテゴリごとに同じバイト列を書く。失敗は投げる（呼び出し側が握る）。
        /// </summary>
        private static void Convert(string clipName, string[] categories, string outDir)
        {
            var assetPath = $"{ClipFolder}/{clipName}.anim";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
            if (clip == null) throw new InvalidOperationException($"{assetPath} を AnimationClip として読めませんでした");
            // ★ ここが最初の関門。AssetRipper が吐いた .anim に muscle カーブが無いと false になる
            if (!clip.humanMotion) throw new InvalidOperationException("Humanoid クリップではありません（muscle カーブが無い）");

            Vrm10Instance instance = null;
            GameObject root = null;
            var animationModeStarted = false;
            try
            {
                instance = LoadRig();
                root = instance.gameObject;
                var animator = root.GetComponent<Animator>();
                if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
                {
                    throw new InvalidOperationException("vita.vrm の Animator に Humanoid Avatar がありません");
                }

                StripToSkeleton(root, animator);
                DedupeNodeNames(root);

                var data = new ExportingGltfData();
                using (var exporter = new VrmAnimationExporter(data, new GltfExportSettings()))
                {
                    // ★★ サンプリングより**前**、T ポーズのまま Prepare すること。Prepare は階層を複製し、
                    //   Export() が先頭で呼ぶ base.Export() の時点の複製のポーズが rest（＝T ポーズ基準）になる
                    //   （読み込み側 Vrm10AnimationInstance.Initialize の「require: transform is T-Pose」）
                    exporter.Prepare(root);

                    AnimationMode.StartAnimationMode();
                    animationModeStarted = true;

                    var frames = 0;
                    var bones = 0;
                    var moved = new HashSet<HumanBodyBones>();

                    exporter.Export(vrma =>
                    {
                        var map = new Dictionary<HumanBodyBones, Transform>();
                        foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
                        {
                            if (bone == HumanBodyBones.LastBone || ExcludedBones.Contains(bone)) continue;
                            var t = animator.GetBoneTransform(bone);
                            if (t == null) continue;
                            map.Add(bone, t);
                        }
                        bones = map.Count;

                        // ★ hips の位置は必須。無いと AddFrame が NRE になる
                        vrma.SetPositionBoneAndParent(map[HumanBodyBones.Hips], root.transform);
                        foreach (var kv in map)
                        {
                            var vrmBone = Vrm10HumanoidBoneSpecification.ConvertFromUnityBone(kv.Key);
                            var parent = GetParentBone(map, vrmBone) ?? root.transform;
                            vrma.AddRotationBoneAndParent(kv.Key, kv.Value, parent);
                        }

                        var fps = clip.frameRate > 0f ? clip.frameRate : 30f;
                        var count = Mathf.CeilToInt(clip.length * fps) + 1;
                        var first = new Dictionary<HumanBodyBones, Quaternion>();
                        for (var i = 0; i < count; ++i)
                        {
                            // ★ 終端 t = length を必ず含める（CeilToInt + 1 で最後のフレームが length を超えるので clamp）
                            var t = Mathf.Min(i / fps, clip.length);
                            AnimationMode.SampleAnimationClip(root, clip, t);

                            foreach (var kv in map)
                            {
                                var q = kv.Value.localRotation;
                                if (i == 0) first[kv.Key] = q;
                                else if (!moved.Contains(kv.Key) && Quaternion.Angle(first[kv.Key], q) > MovedThresholdDegrees) moved.Add(kv.Key);
                            }

                            vrma.AddFrame(TimeSpan.FromSeconds(t));
                            frames++;
                        }
                    });

                    // ★ 1本も動いていなければ Humanoid が当たっていない（Avatar 経由の retarget が効いていない）。
                    //   出力はできてしまうので、ここで失敗にしないと「T ポーズのまま止まった .vrma」が黙って出る
                    if (moved.Count == 0)
                    {
                        throw new InvalidOperationException("ボーンが一度も動きませんでした（Humanoid が当たっていない可能性）");
                    }

                    var bytes = data.ToGlbBytes();
                    var written = new List<string>();
                    foreach (var category in categories)
                    {
                        var dir = Path.Combine(outDir, category);
                        Directory.CreateDirectory(dir);
                        var path = Path.Combine(dir, clipName + ".vrma");
                        File.WriteAllBytes(path, bytes);
                        written.Add(path);
                    }

                    // ★ 「指ボーンかどうか」の判定は FingerPose.IsFinger 1本だけを使う。ここに
                    //   別実装（名前文字列の Contains 判定）を持つと、指の定義を変えたときに
                    //   片側だけ直し忘れて FingerFallbackPoseProvider 側とズレる余地ができる。
                    var movedFingers = moved.Count(FingerPose.IsFinger);
                    Debug.Log($"[VrmaExport] {clipName}: {frames} frames ({clip.length:F3}s @ {clip.frameRate}fps), " +
                              $"{bones} bones ({moved.Count} moved, fingers {movedFingers}), {bytes.Length} bytes → {string.Join(", ", written)}");
                }
            }
            finally
            {
                if (animationModeStarted) AnimationMode.StopAnimationMode();
                // ★ ここは instance ではなく root で判定する。StripToSkeleton が Vrm10Instance を
                //   DestroyImmediate すると、instance（コンポーネント参照）は Unity の疑似 null に
                //   落ちて instance != null が常に false になり、以後は instance.gameObject にも
                //   触れない——結果として GameObject が解放されず、Batch() が回すクリップの数
                //   （13本）ぶん、mesh / material / texture を抱えた階層がエディタのシーンに
                //   溜まり続けていた。root は GameObject そのものへの参照なので影響を受けない。
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
            }
        }

        /// <summary>
        /// 同梱 <c>vita.vrm</c> を読む。batchmode では UniVRM が <c>ImmediateCaller</c> に倒れるので <c>Wait()</c> でよい
        /// （<see cref="VrmProbe"/> と同じ）。
        ///
        /// ★ <b><c>instance.Runtime</c> に触らないこと。</b> 遅延生成で "Runtime Control Rig" の GameObject 群が
        ///   階層に生え、書き出しのノードに混ざる。<c>ControlRigGenerationOption.None</c> にしてあるのは念のため。
        /// </summary>
        private static Vrm10Instance LoadRig()
        {
            var path = Path.Combine(Application.streamingAssetsPath, "vita.vrm");
            var task = Vrm10.LoadPathAsync(
                path,
                canLoadVrm0X: false,
                controlRigGenerationOption: ControlRigGenerationOption.None,
                showMeshes: false,
                ct: CancellationToken.None);
            task.Wait();
            if (task.Result == null) throw new InvalidOperationException($"{path} を読めませんでした");
            return task.Result;
        }

        /// <summary>
        /// <c>Transform</c> とルートの <c>Animator</c> だけを残して、他のコンポーネントを全部外す。
        ///
        /// ★ <b>Renderer を残すと <c>.vrma</c> に VRM 本体（mesh / material / skin）が埋まる。</b>
        ///   <c>gltfExporter.Prepare</c> が階層を複製し、<c>Export</c> が有効な Renderer を書き出すため。
        ///   正解形は <c>meshes: 0</c>（同梱 <c>idle_loop.vrma</c> と同じ）。
        /// ★ <c>Vrm10Instance</c> は <c>[RequireComponent(typeof(Humanoid))]</c> なので、<c>Humanoid</c> より先に
        ///   外さないと「依存されているので外せない」で残る。型で順番を決め打ちせず、<b>消えなくなるまで回す</b>
        ///   （<c>DestroyImmediate</c> は依存があると LogError を出して何もしない）。
        /// </summary>
        private static void StripToSkeleton(GameObject root, Animator keep)
        {
            // まず Vrm10Instance（依存の根）を外す。これが残っていると Humanoid が外せない
            foreach (var vrm in root.GetComponentsInChildren<Vrm10Instance>(true))
            {
                UnityEngine.Object.DestroyImmediate(vrm);
            }

            for (var pass = 0; pass < 8; ++pass)
            {
                var targets = root.GetComponentsInChildren<Component>(true)
                    .Where(c => c != null && !(c is Transform) && c != keep)
                    .ToList();
                if (targets.Count == 0) return;

                // ★ Renderer は MeshFilter より先に。MeshRenderer は MeshFilter を要求しないが、逆順だと稀に警告が出る
                foreach (var c in targets.OrderBy(c => c is Renderer ? 0 : c is MonoBehaviour ? 1 : 2))
                {
                    if (c == null) continue;
                    UnityEngine.Object.DestroyImmediate(c);
                }
            }

            var left = root.GetComponentsInChildren<Component>(true)
                .Where(c => c != null && !(c is Transform) && c != keep)
                .Select(c => c.GetType().Name)
                .Distinct()
                .ToList();
            if (left.Count > 0)
            {
                throw new InvalidOperationException("外せないコンポーネントが残っています: " + string.Join(", ", left));
            }
        }

        /// <summary>
        /// ノード名の重複を潰す。<c>VrmAnimationExporter.Export</c> は channel の対象を
        /// <c>names.IndexOf(node.name)</c> で**名前で逆引き**するので、重複があると別のノードに書かれる。
        /// <c>vita.vrm</c> は重複なし（171 ノード）だが、別モデルで回したときの保険。
        /// </summary>
        private static void DedupeNodeNames(GameObject root)
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            var renamed = 0;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var name = t.name;
                if (seen.TryGetValue(name, out var n))
                {
                    seen[name] = n + 1;
                    t.name = $"{name}#{n + 1}";
                    renamed++;
                }
                else
                {
                    seen[name] = 1;
                }
            }
            if (renamed > 0) Debug.LogWarning($"[VrmaExport] ノード名の重複を {renamed} 件リネームしました");
        }

        /// <summary>
        /// VRM のボーン仕様上の親で、map に存在する最も近いもの。Hips で <c>null</c>。
        /// <c>VrmAnimationMenu.GetParentBone</c> の写し。
        /// </summary>
        private static Transform GetParentBone(Dictionary<HumanBodyBones, Transform> map, Vrm10HumanoidBones bone)
        {
            while (true)
            {
                if (bone == Vrm10HumanoidBones.Hips) break;
                var parentBone = Vrm10HumanoidBoneSpecification.GetDefine(bone).ParentBone.Value;
                var unityParentBone = Vrm10HumanoidBoneSpecification.ConvertToUnityBone(parentBone);
                if (map.TryGetValue(unityParentBone, out var found)) return found;
                bone = parentBone;
            }
            return null;
        }

        /// <summary>先頭の <c>~/</c> だけ展開する（Unity は展開しない）。</summary>
        private static string ExpandHome(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return path == "~" ? home : Path.Combine(home, path.Substring(2));
            }
            return path;
        }
    }
}
