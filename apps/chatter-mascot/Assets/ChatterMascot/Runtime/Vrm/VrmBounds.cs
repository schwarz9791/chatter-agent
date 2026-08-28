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
    ///
    /// ★ <b>この罠は1段上でも起きる。実際に再発した。</b> #59 で <c>VrmStage</c> が
    ///   測り方を <see cref="Of"/>（<c>Renderer</c>）から <see cref="OfBones"/>（ボーン）へ
    ///   切り替えたとき、<b>合成規則はここ1箇所のまま</b>だったのに、
    ///   <b>どのボーンを渡すか</b>が <c>VrmStage</c> と <c>VrmProbe</c> に分かれ、
    ///   <c>VrmProbe</c> だけが古い <see cref="Of"/> を出し続けた
    ///   —— 上の段落が書いているのと寸分違わぬ形（PR #69 のレビューで判明）。
    ///   いまは <c>VrmStage.MeasureBounds</c> を <c>public static</c> にして
    ///   <b>呼び先を1つ</b>にしてある。<b>「合成規則が1箇所」だけでは足りない。
    ///   入力の作り方も1箇所にすること。</b>
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

        /// <summary>
        /// ボーンのワールド位置から、いま実際に見えている大きさのあたりを付ける。
        ///
        /// ★ <b><c>Renderer.bounds</c> では姿勢を反映できない。</b> <c>SkinnedMeshRenderer</c> は
        ///   <c>updateWhenOffscreen == false</c> のとき<b>メッシュに焼かれた静的な bounds</b> を
        ///   返すだけで、ボーンを動かしても縮まない。VRM 1.0 はレストポーズが T ポーズ必須なので、
        ///   その静的 bounds の幅は<b>常に「広げた腕」</b>になる。#59 でアイドルモーションが入って
        ///   腕が下りても<b>支配軸が水平のまま</b>だったのはこれが理由（実機で確認）。
        /// ★ <c>updateWhenOffscreen = true</c> で解決してはいけない。毎フレーム CPU スキニングで
        ///   bounds を測り直すことになり、常駐アプリの電力予算を壊す。
        ///
        /// ★ <b>髪や裾はボーンに含まれないので、この箱は実際の見た目よりわずかに小さい。</b>
        ///   <paramref name="marginMeters"/> で膨らませて吸収する。
        ///
        /// ★ <b><paramref name="marginMeters"/> は各軸に両側効く。</b> <c>Bounds.Expand(amount)</c>
        ///   は <c>size</c> に <c>amount</c> を足すだけ（＝各側には <c>amount / 2</c>）なので、
        ///   両側に <c>marginMeters</c> ぶん足すには <c>2 倍</c>にして渡す必要がある。
        /// </summary>
        public static Bounds OfBones(IEnumerable<Vector3> worldPositions, float marginMeters)
        {
            var result = new Bounds();
            var first = true;
            if (worldPositions == null) return result;

            foreach (var position in worldPositions)
            {
                if (first)
                {
                    result = new Bounds(position, Vector3.zero);
                    first = false;
                }
                else
                {
                    result.Encapsulate(position);
                }
            }

            if (first) return new Bounds();

            result.Expand(marginMeters * 2f);
            return result;
        }

        /// <summary>
        /// 自動フレーミングの箱に入れてよいボーンか。<b>純粋関数。</b>
        ///
        /// ★ <b>腕（<c>UpperArm</c> / <c>LowerArm</c> / <c>Hand</c> と指）を外す。</b>
        ///   VRM 1.0 はレストポーズが T ポーズ必須なので、腕を入れると
        ///   <b>読み込み直後だけ「広げた腕」の幅で測ってしまう</b>。待機モーション（VRMA）が
        ///   効いて腕が下りるのは非同期で数百 ms〜数秒あとなので、その間だけ
        ///   <b>キャラが小さく映り、あとから一段大きくなるのが見える</b>（実機で指摘された）。
        ///   肩を残せば胴の幅は取れるので、姿勢に関わらず値がほぼ変わらない。
        ///
        /// ★ <b>引き換えに、腕を大きく広げる VRMA を置くと腕がフレームからはみ出す。</b>
        ///   余白（<c>boneBoundsMarginMeters</c>）がある程度は吸収するが、限界がある。
        ///
        /// ★ <b><c>HumanBodyBones.LastBone</c> も除く。</b> 実ボーンではなく列挙の終端を示す
        ///   番兵だが、<c>Enum.GetValues</c> には含まれてしまうので明示的に弾く。
        /// </summary>
        public static bool IsFramingBone(HumanBodyBones bone)
        {
            switch (bone)
            {
                case HumanBodyBones.LastBone:
                case HumanBodyBones.LeftUpperArm:
                case HumanBodyBones.RightUpperArm:
                case HumanBodyBones.LeftLowerArm:
                case HumanBodyBones.RightLowerArm:
                case HumanBodyBones.LeftHand:
                case HumanBodyBones.RightHand:
                case HumanBodyBones.LeftThumbProximal:
                case HumanBodyBones.LeftThumbIntermediate:
                case HumanBodyBones.LeftThumbDistal:
                case HumanBodyBones.LeftIndexProximal:
                case HumanBodyBones.LeftIndexIntermediate:
                case HumanBodyBones.LeftIndexDistal:
                case HumanBodyBones.LeftMiddleProximal:
                case HumanBodyBones.LeftMiddleIntermediate:
                case HumanBodyBones.LeftMiddleDistal:
                case HumanBodyBones.LeftRingProximal:
                case HumanBodyBones.LeftRingIntermediate:
                case HumanBodyBones.LeftRingDistal:
                case HumanBodyBones.LeftLittleProximal:
                case HumanBodyBones.LeftLittleIntermediate:
                case HumanBodyBones.LeftLittleDistal:
                case HumanBodyBones.RightThumbProximal:
                case HumanBodyBones.RightThumbIntermediate:
                case HumanBodyBones.RightThumbDistal:
                case HumanBodyBones.RightIndexProximal:
                case HumanBodyBones.RightIndexIntermediate:
                case HumanBodyBones.RightIndexDistal:
                case HumanBodyBones.RightMiddleProximal:
                case HumanBodyBones.RightMiddleIntermediate:
                case HumanBodyBones.RightMiddleDistal:
                case HumanBodyBones.RightRingProximal:
                case HumanBodyBones.RightRingIntermediate:
                case HumanBodyBones.RightRingDistal:
                case HumanBodyBones.RightLittleProximal:
                case HumanBodyBones.RightLittleIntermediate:
                case HumanBodyBones.RightLittleDistal:
                    return false;
                default:
                    return true;
            }
        }
    }
}
