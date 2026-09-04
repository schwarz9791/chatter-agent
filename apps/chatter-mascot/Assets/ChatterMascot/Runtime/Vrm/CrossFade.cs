using UnityEngine;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// クロスフェードの数式。<b>純粋・<c>static</c>。時計は引数で受け取る。</b>
    ///
    /// ★ 実際に2つの <c>IVrm10Animation</c> を混ぜる <c>CrossFadeAnimation</c>（<c>Vrm/</c>）は
    ///   VRM10 の型（<c>INormalizedPoseProvider</c> / <c>ITPoseProvider</c>）に依存するので
    ///   <c>ChatterMascot.Tests.asmdef</c> から届かない。**判断とブレンドの数式だけ**をここに
    ///   出しておけば、混ぜ方そのものは EditMode テストで固定できる。
    /// </summary>
    public static class CrossFade
    {
        /// <summary>
        /// 経過を 0..1 の進捗にする。
        /// ★ <paramref name="durationSeconds"/><c>&lt;= 0</c> は即 1（フェード無しで完了扱い）。
        ///   <c>MotionParams.FadeSeconds</c> を 0 にしてもゼロ除算しない。
        /// </summary>
        public static float Progress(double startedAt, double now, float durationSeconds)
        {
            if (durationSeconds <= 0f) return 1f;
            var t = (float)((now - startedAt) / durationSeconds);
            return Mathf.Clamp01(t);
        }

        /// <summary>
        /// smoothstep（<c>t*t*(3-2t)</c>）。両端で速度が 0 になるので、
        /// フェードの始点・終点で「折れ」が目立たない（線形だと切り替わりが急）。
        /// </summary>
        public static float Ease(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        /// <summary>ControlRig の各ボーンのローカル回転。<c>Quaternion.Slerp</c> そのもの。</summary>
        public static Quaternion BlendRotation(Quaternion from, Quaternion to, float t)
        {
            return Quaternion.Slerp(from, to, t);
        }

        /// <summary>
        /// hips の<b>正規化済み</b>差分同士を混ぜる。
        ///
        /// ★★ <b>呼び出し側は必ず <see cref="NormalizeHipsDelta"/> を通した値を渡すこと。</b>
        ///   生の hips 位置を直接 Lerp してはいけない理由はそちらの doc を参照。
        /// </summary>
        public static Vector3 BlendHips(Vector3 fromDeltaNormalized, Vector3 toDeltaNormalized, float t)
        {
            return Vector3.Lerp(fromDeltaNormalized, toDeltaNormalized, t);
        }

        /// <summary>
        /// hips の生の位置差分を、T ポーズの hips の高さで正規化する（無次元化）。
        ///
        /// ★★ <b>hips を生の位置で Lerp してはいけない。</b> <c>idle_loop.vrma</c> は
        ///   cm スケールで書き出されていて hips の高さは <c>y≈90</c>、一方 VRoid の書き出しは
        ///   m スケールで <c>y≈0.98</c>——単位が実測で約100倍違う。<c>Vrm10Retarget</c> は
        ///   <c>delta = source.Raw.Hips - source.TPose.Hips</c> を
        ///   <c>sink.TPose.Hips.y / source.TPose.Hips.y</c> で割ってスケールしてから使うので、
        ///   単位系が違う2つの <c>delta</c> をそのまま Lerp すると、フェードの途中で
        ///   <b>腰が瞬間的に大きく飛ぶ</b>（cm 側の delta が m 側よりケタで大きいため）。
        ///   ここで高さ（<c>tposeHips.y</c>）で割って無次元化した値を <see cref="BlendHips"/> に渡す。
        ///   使う側（<c>CrossFadeAnimation</c>）は自分の T ポーズの hips を <c>(0,1,0)</c>（高さ 1）と
        ///   申告して <c>(0,1,0) + 混ぜた差分</c> を返すので、掛け戻しは要らない——
        ///   <c>Vrm10Retarget</c> が <c>sink.TPose.Hips.y / 1</c> でモデルの背丈に戻してくれる。
        ///
        /// ★ <paramref name="tposeHips"/><c>.y</c> が <c>1e-6</c> 未満（実質ゼロ・異常値）なら
        ///   ゼロ除算を避け、正規化せず差分をそのまま返す。
        /// </summary>
        public static Vector3 NormalizeHipsDelta(Vector3 raw, Vector3 tposeHips)
        {
            var delta = raw - tposeHips;
            if (Mathf.Abs(tposeHips.y) < 1e-6f) return delta;
            return delta / tposeHips.y;
        }
    }
}
