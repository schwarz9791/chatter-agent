using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace ChatterMascot.EditorTools
{
    /// <summary>
    /// <c>Assets/ChatterMascot/Icon/AppIcon.png</c> を Player Settings のアプリアイコンへ登録する（#93）。
    ///
    /// ★★ <b>なぜ手作業ではなくスクリプトか。</b> Player Settings の Icon は
    ///   <c>ProjectSettings.asset</c> に配列で書かれるが、要求されるサイズの数は
    ///   Unity のバージョンと <c>NamedBuildTarget</c> で変わる。Inspector の Icon タブへ
    ///   1枚ずつドラッグする手順書は、バージョンが上がった瞬間に欄の数が合わなくなって
    ///   陳腐化する。<see cref="PlayerSettings.GetIconSizes"/> に聞いて配列を作れば、
    ///   何が要求されているかを機械的に追従できる（<c>NativePluginSettings</c> /
    ///   <c>SceneFixups</c> と同じ立ち位置）。
    ///
    ///   ./scripts/run.sh ChatterMascot.EditorTools.IconSettings.FixAll
    ///
    /// ★ <b><see cref="PlayerSettings.SetIconsForTargetGroup"/> は使わないこと。</b>
    ///   deprecated で、後継が <see cref="NamedBuildTarget"/> を取る
    ///   <see cref="PlayerSettings.SetIcons"/>。
    ///
    /// ★ <b><see cref="IconKind"/> は <see cref="IconKind.Application"/> だけを使うこと。</b>
    ///   他の値（<c>Setting</c> など）は iOS 専用で、macOS スタンドアロンには存在しない。
    ///
    /// ★ <b><c>macAppStoreCategory</c> はここに入れないこと。</b> あちらは
    ///   <c>ProjectSettings.asset</c> を直接編集した値が権威で、権威を2つ持たない
    ///   （→ #93 の PR、Game Mode のロケット対策）。
    ///
    /// ★ <b>再実行が要るとき</b>: アイコン画像を差し替えたとき。加えて、
    ///   <b>将来 macOS が iOS とアイコンの geometry（角丸・パディング）を共通化したら</b>
    ///   書き出し直しが要る —— <c>AppIcon.png</c> は静止画なので、OS 側の描画規約が
    ///   変わっても中身までは追従しない。
    /// </summary>
    public static class IconSettings
    {
        private const string IconPath = "Assets/ChatterMascot/Icon/AppIcon.png";

        public static void FixAll()
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(IconPath);
            if (texture == null)
            {
                // ★ ここで黙って空のアイコンを設定すると「ビルドしたのにアイコンが無い」に
                //   戻ってしまい、原因（このアセットが無い）が見えなくなる。はっきり落とす
                Debug.LogError($"[Icon] AppIcon.png が見つかりません: {IconPath}");
                EditorApplication.Exit(1);
                return;
            }

            var sizes = PlayerSettings.GetIconSizes(NamedBuildTarget.Standalone, IconKind.Application);

            // ★ 何サイズ要求されたかは Unity のバージョンで変わるので、実測として毎回残す
            //   （docs/mascot.md）。1行に収めること —— scripts の grep は2行目以降を落とす
            Debug.Log($"[Icon] GetIconSizes(Standalone, Application) = [{string.Join(", ", sizes)}]");

            // ★ 配列長は GetIconSizes の戻り値と同じ長さでなければならない（SetIcons のドキュメントに明記）。
            //   要求サイズごとに別画像を用意していないので、同じテクスチャを全枠に敷く
            //   （Unity 側がビルド時に各サイズへリサイズする）
            var icons = new Texture2D[sizes.Length];
            for (var i = 0; i < icons.Length; i++) icons[i] = texture;

            PlayerSettings.SetIcons(NamedBuildTarget.Standalone, icons, IconKind.Application);
            AssetDatabase.SaveAssets();

            Debug.Log("[Icon] Player Settings のアプリアイコンを設定しました");
        }
    }
}
