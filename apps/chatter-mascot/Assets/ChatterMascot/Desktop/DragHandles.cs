using System.Collections.Generic;
using Kirurobo;
using UnityEngine;

namespace ChatterMascot.Desktop
{
    /// <summary>
    /// <b>掴める（＝クリック透過で「実体」とみなされる）ものは、ドラッグで動かせる。</b>
    /// その規則をここ1箇所に置く。
    ///
    /// ★ <b>対象を名前で決め打ちにしない。</b> 判定は「<c>Collider</c> を持っているか」——
    ///   クリック透過のヒットテストが <c>Physics.Raycast</c> で見ているのと同じ条件なので、
    ///   <b>掴める領域とドラッグできる領域が定義上ずれない</b>。
    ///
    /// ★ <b>Editor（シーンの修繕）とランタイム（VRM の読み込み）の両方から呼ばれる。</b>
    ///   #12 までは <c>SceneFixups</c> がシーンに焼いていたが、VRM は実行時に生えるので
    ///   同じ規則を両方から使える形にした。片方だけ直すと静かにズレる。
    ///
    /// <c>UniWindowMoveHandle</c> は UniWindowController 同梱（MIT）。自前で書かないのは、
    /// <b>macOS の Retina 座標系の手当てが既に入っている</b>ため
    /// （<c>eventData.position</c> の系とウィンドウ座標系でスケールが一致しなくなる。
    /// このプロジェクトは <c>macRetinaSupport: 1</c>）。
    /// </summary>
    public static class DragHandles
    {
        /// <summary>
        /// <paramref name="root"/> 配下の <c>Collider</c> 持ちに <c>UniWindowMoveHandle</c> を付ける。
        /// 付けた <c>GameObject</c> の名前を返す（Editor 側がログに出す）。
        /// </summary>
        public static IReadOnlyList<string> AttachAll(GameObject root)
        {
            var added = new List<string>();
            if (root == null) return added;

            // ★ ウィンドウ制御が居ないシーンでは付けない（TransparencyProbe など）。
            //   この判定も Editor / ランタイムで1箇所にしておく
            if (Object.FindFirstObjectByType<UniWindowController>() == null) return added;

            // 非アクティブも含める。フォールバックの Cube は読み込み成功で
            // SetActive(false) されるが、失敗すれば出しっぱなしになる
            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
            {
                var go = collider.gameObject;
                if (go.GetComponent<UniWindowMoveHandle>() != null) continue;

                go.AddComponent<UniWindowMoveHandle>();
                added.Add(go.name);
            }
            return added;
        }

        /// <summary>
        /// <see cref="VrmStage"/> の読み込み完了から呼ぶ形。付けた数だけログに出す。
        /// </summary>
        public static void AttachAll(GameObject root, bool log)
        {
            var added = AttachAll(root);
            if (log && added.Count > 0)
            {
                Debug.Log($"[Mascot] ドラッグハンドルを付けました: {string.Join(", ", added)}");
            }
        }
    }
}
