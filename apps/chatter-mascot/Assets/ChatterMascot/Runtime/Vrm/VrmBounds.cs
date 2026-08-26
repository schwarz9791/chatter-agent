using System.Collections.Generic;
using UnityEngine;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// モデルを構成する複数の箱を1つに合成する。<b>純粋関数。</b>
    ///
    /// ★ <b><c>new Bounds()</c> から <c>Encapsulate</c> を始めないこと。</b> 既定の
    ///   <c>Bounds</c> は中心 (0,0,0) / サイズ 0 の「原点の点」なので、そこから広げると
    ///   <b>必ず原点を含む箱</b>になる。中心が下へ引きずられ、自動フレーミングが
    ///   「小さく映る」。だから<b>最初の1つは代入、2つ目以降が <c>Encapsulate</c></b>。
    ///   同じ理由で<b>サイズ 0 の箱は混ぜない</b>（空の <c>Renderer</c> がそれ）。
    ///
    /// ★ <b>このファイルは <c>Runtime/Vrm/</c> に置くこと。</b> 名前空間
    ///   <c>ChatterMascot.Vrm</c> は <c>ChatterMascot.Runtime</c> と
    ///   <c>ChatterMascot.Vrm</c> の2つの asmdef にまたがっているので、
    ///   <c>Vrm/</c> へ動かしても <c>using</c> は1文字も変わらないまま
    ///   <b>EditMode テストからだけ見えなくなる</b>（<c>ChatterMascot.Tests</c> は
    ///   <c>ChatterMascot.Runtime</c> しか参照していない）。
    ///   <c>UnityEngine.Renderer</c> / <c>Bounds</c> は UniVRM 非依存なのでここに置ける。
    ///
    /// ★ <b>合成の規則を書き足すときはここだけにすること。</b> 以前は
    ///   <c>VrmStage</c> と <c>VrmProbe</c> に同じものが逐語で2つあり、
    ///   <b>片方だけ null チェックがある</b>状態が実際に発生していた。
    ///   <c>VrmFramingTests</c> の定数は <c>VrmProbe</c> の出力を貼ったものなので、
    ///   2つがズレるとテストは<b>ランタイムがもう生成しない数値</b>に対して通り続ける。
    /// </summary>
    public static class VrmBounds
    {
        /// <summary>
        /// <c>Renderer</c> のワールド bounds を合成する。
        /// <c>RuntimeGltfInstance.Renderers</c> をそのまま渡せる。
        ///
        /// ★ <b>ここに判定を増やさないこと。</b> この層の役割は Unity の fake-null
        ///   （<c>Destroy</c> 済みの <c>Renderer</c>）を吸うことだけ。規則は
        ///   <see cref="Combine"/> 側に置いて EditMode テストで固定する。
        /// </summary>
        public static Bounds Of(IEnumerable<Renderer> renderers)
        {
            var result = new Bounds();
            var first = true;
            if (renderers == null) return result;

            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                Add(ref result, ref first, renderer.bounds);
            }
            return result;
        }

        /// <summary>合成の規則そのもの。<b>テストはここを叩く。</b></summary>
        public static Bounds Combine(IEnumerable<Bounds> parts)
        {
            var result = new Bounds();
            var first = true;
            if (parts == null) return result;

            foreach (var part in parts) Add(ref result, ref first, part);
            return result;
        }

        private static void Add(ref Bounds into, ref bool first, Bounds part)
        {
            // ★ 空の箱を混ぜると原点まで bounds が伸びる
            if (part.size == Vector3.zero) return;

            if (first)
            {
                into = part;
                first = false;
            }
            else
            {
                into.Encapsulate(part);
            }
        }
    }
}
