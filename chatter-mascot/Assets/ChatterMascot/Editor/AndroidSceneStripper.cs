using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ChatterMascot.EditorTools
{
    /// <summary>
    /// Android ビルドのときだけ、デスクトップ専用のコンポーネントをシーンから外す（#97）。
    ///
    /// ★ <b>なぜ要るか。</b> シーンはコンポーネントを GUID で直列化するので、
    ///   asmdef の <c>includePlatforms</c> に関わらず参照は残る。デスクトップ専用パッケージ
    ///   （<c>UniWindowController</c> のプレハブ、<c>UniWindowMoveHandle</c>）は
    ///   Android ではコンパイルされないので、Android の Player は
    ///   「The referenced script on this Behaviour is missing!」を吐く ——
    ///   スクリプトが本当に消えたのではなく、参照だけが解決できなくなる。
    ///
    /// ★ <b>Editor では <c>GetType()</c> が普通に解決できてしまう。</b> Editor のドメインには
    ///   デスクトップ専用アセンブリもコンパイルされて載っているので、ビルドを回している
    ///   最中（まだ Editor プロセス）はコンポーネントの型を正しく引ける。壊れるのは
    ///   Android の実機だけ、というズレがこのフックの存在理由。
    ///
    /// ★ <b>規則は機械的（asmdef のプラットフォーム）で、型の決め打ちリストではない。</b>
    ///   デスクトップ専用パッケージが増えても、このクラスを変更しないで済む
    ///   （→ <see cref="AsmdefPlatformFilter"/>）。
    ///
    /// ★★ <b>元から Transform だけの GameObject は消さないこと。</b>
    ///   <c>GazeTarget</c> のような「視線の目標」は、それ自体が仕様として
    ///   コンポーネント無しの空オブジェクト。<b>このパスで実際に何かコンポーネントを
    ///   外した結果として空になったもの</b>だけを片付ける対象にする
    ///   （そうしないと、意図して置いた空オブジェクトまで巻き込んで消してしまう）。
    /// </summary>
    public sealed class AndroidSceneStripper : IProcessSceneWithReport
    {
        private const string Platform = "Android";

        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            // report == null は Play Mode（ビルドではない）
            if (report == null) return;
            if (report.summary.platform != BuildTarget.Android) return;

            foreach (var root in scene.GetRootGameObjects())
            {
                Strip(root);
            }
        }

        /// <summary>子から先に処理する（post-order）。</summary>
        private static void Strip(GameObject go)
        {
            // ★ 子の削除で go.transform の列挙が壊れないよう、先にスナップショットを取る
            var children = new List<GameObject>();
            foreach (Transform child in go.transform) children.Add(child.gameObject);
            foreach (var child in children) Strip(child);

            // ★ 削除しながら舐めるので、GetComponents のスナップショット配列に対して回す
            var removed = 0;
            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null) continue; // 元から壊れている参照はここでは扱わない
                if (component is Transform) continue;
                if (IsAndroidIncluded(component)) continue;

                Debug.Log($"[Build] Android 非対応のコンポーネントを外しました: {HierarchyPath(go)}/{component.GetType().Name}");
                Object.DestroyImmediate(component);
                removed++;
            }

            // ★★ ここで何も外していなければ触らない。<c>GazeTarget</c> のように
            //   最初から Transform しか持たない GameObject は仕様どおりの空オブジェクトで、
            //   「空だから消してよい」対象ではない
            if (removed > 0 && go.transform.childCount == 0 && go.GetComponents<Component>().Length == 1)
            {
                Debug.Log($"[Build] 空になった GameObject を削除しました: {HierarchyPath(go)}");
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>Android で使える型なら true。読めない／組み込みなら「残す」に倒す。</summary>
        private static bool IsAndroidIncluded(Component component)
        {
            var assemblyName = component.GetType().Assembly.GetName().Name;
            var asmdefPath = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assemblyName);
            // 組み込み（UnityEngine 等）や precompiled dll は asmdef を持たない
            if (string.IsNullOrEmpty(asmdefPath)) return true;

            string json;
            try
            {
                json = File.ReadAllText(asmdefPath);
            }
            catch
            {
                return true;
            }

            return AsmdefPlatformFilter.IsIncluded(json, Platform);
        }

        private static string HierarchyPath(GameObject go)
        {
            var segments = new List<string>();
            for (var t = go.transform; t != null; t = t.parent) segments.Insert(0, t.name);
            return string.Join("/", segments);
        }
    }
}
