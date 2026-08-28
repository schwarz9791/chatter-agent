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

            // ★ 測る前に VrmStage.FaceCamera を通すこと。MeasureBounds を共有していても、
            //   回す前に測れば別の箱になる——VrmStage.Adopt は FaceCamera → MeasureBounds の
            //   順で、ここもそれに合わせないと「同じ関数」でも「同じ箱」にならない
            //   （PR #69 の再レビューで判明）。180° では size が不変なので**気づきにくい**
            //   （size の一致は「同じ staging を通した」ことの証拠にならない）。90° 回るモデルでは
            //   x と z が入れ替わり、90°の倍数でないヨーでは size 自体が変わる。
            // ★ instance.Runtime に触れて ControlRig の生成順序を固定する（Adopt がやっている
            //   `_ = instance.Runtime;`）のはここでは不要。あれは回した後に作ると腕の上下が
            //   反転するのを避けるためで、probe は ControlRig を駆動しないため関係ない。
            //   **ただし probe がアニメーションを扱うようになったら Adopt と同じ順序を再現すること**
            var yaw = VrmStage.FaceCamera(instance);
            Debug.Log($"[VrmProbe] faceCamera yaw: {yaw:F0} 度");

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

                // ★ この Renderer 由来の bounds は T ポーズの静的な値で、**ランタイムの自動フレーミング
                //   （VrmStage.MeasureBounds）はもうこれを使わない**（#59 で切り替わった）。
                //   SkinnedMeshRenderer.bounds はメッシュに焼かれた静的な値を返すだけで姿勢を反映しないので、
                //   アイドルモーションで腕が下りても支配軸が水平のまま縮まない（実機で確認済み。
                //   詳細は VrmBounds.OfBones のコメント参照）。それでも出しているのは、
                //   モデルそのものの素の大きさ（T ポーズでの寸法）を見る参考値として意味があるため
                var bounds = VrmBounds.Of(gltf.Renderers);
                text.Append("\n  bounds size: ").Append(bounds.size);
                text.Append("\n  bounds center: ").Append(bounds.center);
                // ★ ウィンドウのアスペクトを決める材料（→ SETUP.md のウィンドウの大きさ）
                text.Append("\n  bounds W/H: ").Append((bounds.size.x / Mathf.Max(bounds.size.y, 1e-6f)).ToString("F3"));

                // ★ ここが出す数値は VrmStage が実行時に使うのと**同じ関数**の出力。
                //   Tests/Editor/VrmFramingTests.cs の定数はこの出力をそのまま貼ったものなので、
                //   別実装に分岐させると**ランタイムがもう生成しない数値**をテストが守り始める
                //   （#59 で VrmBounds.Of から切り替えたときに実際に起きた）。
                // ★ **ボーンを集めるループをここに書き写さないこと。** 書き写した時点で
                //   「同じ関数の出力」が「いまのところ同じ結果になる別実装」に変わり、
                //   IsFramingBone の除外リストやマージンを片方だけ直したときに黙ってズレる。
                //   VrmStage.MeasureBounds を public static にしてあるのはそのため
                // ★ マージンは VrmStage.DefaultBoneBoundsMarginMeters を使う。probe はシーンを
                //   経由しないので [SerializeField] の値は取れない。**シーンで既定から変えたら
                //   この出力は実行時の箱と食い違う**（VrmStage 側のコメント参照）
                var frameBounds = VrmStage.MeasureBounds(instance, VrmStage.DefaultBoneBoundsMarginMeters);
                text.Append("\n  frame bounds size: ").Append(frameBounds.size);
                text.Append("\n  frame bounds center: ").Append(frameBounds.center);
                text.Append("\n  frame bounds W/H: ").Append((frameBounds.size.x / Mathf.Max(frameBounds.size.y, 1e-6f)).ToString("F3"));
            }

            if (instance.Vrm != null && instance.Vrm.Expression != null)
            {
                // ★ Clips は (Preset, Clip) のタプル列。Clip が null の枠も混ざる
                var clips = instance.Vrm.Expression.Clips
                    .Where(pair => pair.Clip != null)
                    .ToList();
                // ★ **「一覧に載っている」＝「顔が動く」ではない。** UniVRM の importer は、
                //   モデルが宣言していない preset にも**中身が空のクリップを作る**
                //   （vita.vrm は glTF に preset が 14 個しか無いのに Clips は 18 個で、
                //   lookUp / lookDown / lookLeft / lookRight が bind ゼロで生えている）。
                //   SetWeight は通るのに何も起きないので、**空は空と書く**こと
                var keys = clips.Select(pair =>
                {
                    var name = pair.Preset == ExpressionPreset.custom ? pair.Clip.name : pair.Preset.ToString();
                    var binds = (pair.Clip.MorphTargetBindings?.Length ?? 0)
                                + (pair.Clip.MaterialColorBindings?.Length ?? 0)
                                + (pair.Clip.MaterialUVBindings?.Length ?? 0);
                    return binds > 0 ? name : name + "(空)";
                });
                text.Append("\n  expressions: ").Append(string.Join(", ", keys.OrderBy(k => k)));

                // ★ #57: 表情が瞬き / 口 / 視線をブロックするか、二値かは**この静的な定義**で決まる。
                //   ランタイムの BlinkOverrideRate / MouthOverrideRate / LookAtOverrideRate は
                //   「いま立てている weight」に依存する動的な値で、neutral が支配的なこのアプリでは
                //   実質いつも 0 になるので判定には使えない（→ VrmCharacter.WarnAboutOverrides）。
                //   同梱 vita.vrm は preset 14個すべて isBinary=false / override=none。
                var quirks = clips
                    .Where(pair => pair.Clip.IsBinary
                                   || pair.Clip.OverrideBlink != ExpressionOverrideType.none
                                   || pair.Clip.OverrideLookAt != ExpressionOverrideType.none
                                   || pair.Clip.OverrideMouth != ExpressionOverrideType.none)
                    .Select(pair => (pair.Preset == ExpressionPreset.custom ? pair.Clip.name : pair.Preset.ToString())
                                    + $"(binary={pair.Clip.IsBinary}"
                                    + $" blink={pair.Clip.OverrideBlink}"
                                    + $" lookAt={pair.Clip.OverrideLookAt}"
                                    + $" mouth={pair.Clip.OverrideMouth})")
                    .OrderBy(k => k)
                    .ToList();
                text.Append("\n  expression の特記: ")
                    .Append(quirks.Count == 0
                        ? "なし（全部 isBinary=false / override=none）"
                        : string.Join(", ", quirks));
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

        /// <summary>
        /// ★ <b>Editor から呼ぶので <see cref="AssetEnvFactory"/> をそのまま使えない</b> ——
        ///   <c>Application.streamingAssetsPath</c> は Editor でも
        ///   <c>&lt;project&gt;/Assets/StreamingAssets</c> を返すので使えるが、
        ///   <c>persistentDataPath</c> は Editor 固有の場所を指す。
        ///   ここは<b>同梱（探索順5）と起動引数（探索順1）だけ</b>見れば足りる。
        ///
        /// ★ <b>「足りる」と書いたら、残り（2 / 3 / 4）を実際に全部潰すこと。</b>
        ///   この doc は最初からこう書いてあったのに、コードが潰していたのは 3 だけだった。
        ///   4 を <a href="https://github.com/schwarz9791/chatter-agent/issues/64">#64</a> で、
        ///   2 をその直後に踏んだ —— <b>doc の主張とコードのズレが、そのまま2回のバグになった</b>。
        ///   探索順（<see cref="AssetPath"/> の表）に段を足したら、ここも見直すこと。
        ///
        /// ★ <b>起動引数（1）は残すこと。</b> <c>-vrm &lt;path&gt;</c> は
        ///   <b>その実行に対して明示的に渡すもの</b>で、probe を別モデルで回すための
        ///   意図的な口。周囲の状態に左右されない点が 2〜4 と決定的に違う。
        /// </summary>
        private static AssetEnv ProbeEnv()
        {
            var env = AssetEnvFactory.Current();
            // ★ **この行は長らく「消す」つもりで「基準ディレクトリを変える」だけになっていた。**
            //   `AssetPath.Join` は左辺（基準）が空だと、以前は右辺をそのまま返していたので、
            //   `Join("", "model.vrm")` は `"model.vrm"`（相対パス）になり、探索順3の候補として
            //   積まれ続けていた。`File.Exists` はプロジェクトルート基準で評価されるので、
            //   プロジェクトルートに `model.vrm` を置くと**同梱（探索順5）より上位で当たる**
            //   （PR #69 の再レビューで判明。再現手順:
            //   `cp Assets/StreamingAssets/vita.vrm ./model.vrm` → probe を回すと
            //   `[VrmProbe] 読みます: model.vrm` と出て同梱を読まない）。
            //   `Join` を「空の基準からは候補を作らない（null を返す）」に直したことで、
            //   この行は宣言どおり「この段を消す」という意味になった。
            env.PersistentDataPath = "";
            // ★ **HasUserConfigDirectory も落とすこと（#64）。** AssetEnvFactory.Current() は
            //   OSXEditor で true を立てるので、これを残すと探索順に
            //   ~/.config/chatter-agent/models/*.vrm が生きたままになる。probe の出力は
            //   Tests/Editor/VrmFramingTests.cs の定数の出所なので、そこに自分のモデルを
            //   1つ置いている開発者は**マシンによって基準値が変わるのに、変わったことに
            //   気づけない**。PR #69 の対応中に実際に踏んだ —— 事故ではなく、
            //   **自分のモデルを置いて動作確認する**というごく普通の使い方で発火する。
            //   ★ 潰すのは probe だけ。アプリ側の探索順（AssetEnvFactory.Current()）は
            //   そのままなので、~/.config/chatter-agent/models/ に置いたモデルは
            //   これまでどおりアプリが読む。
            env.HasUserConfigDirectory = false;
            // ★ **環境変数（探索順2）も潰すこと。** #64 と同じ穴がここにも空いていた。
            //   AssetEnvFactory.Current() は Variables = ReadEnvironment() を入れるので、
            //   CHATTER_MASCOT_VRM が生きたままになる。scripts/run.sh は開発者のシェルから
            //   Unity を起動するので **export しっぱなしの値をそのまま継承する**。
            //   ★ HasUserConfigDirectory との違いは「置いたファイル」ではなく
            //   「シェルに残った状態」だという点で、**気づきにくさはこちらが上**。
            //   SETUP.md は「.app を Finder から起動すると環境変数は空（シェルを継承しない）」
            //   と書いているが、**probe は Editor をシェルから起動するので効く** ——
            //   「アプリでは効かないが probe では効く」といういちばん見つけにくい向き。
            //   ★ Variable() は env.Variables == null で早期 return するので、これで
            //   CHATTER_MASCOT_VRM も XDG_CONFIG_HOME も読まれなくなる（後者は
            //   HasUserConfigDirectory = false でブロックごと通らないので、いずれにせよ無関係）。
            env.Variables = null;
            return env;
        }
    }
}
