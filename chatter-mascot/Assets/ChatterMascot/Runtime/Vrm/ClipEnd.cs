using UnityEngine;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// 提示中の legacy Animation を、クリップの終端より先まで進ませないための閾値（#103）。
    ///
    /// ★ VRMA の末尾に重複キー（同じ時刻のキーが連続する）があると、インポータが作る接線が
    ///   0 除算で NaN になる。範囲内の評価では使われないが、クリップ長を越えた時刻で評価すると
    ///   その NaN の接線で外挿され、hips の位置が NaN になる。ここは「ファイルを直す」のではなく
    ///   「終端より先を評価させない」側で無害化する。
    /// </summary>
    public static class ClipEnd
    {
        /// <summary>
        /// 終端の手前に取る余白（秒）。終端ちょうどの評価が外挿扱いになるかは Unity の実装次第
        /// なので、最後の区間の内側に留める。
        /// </summary>
        public const float MarginSeconds = 0.001f;

        public static float Limit(float length)
        {
            return Mathf.Max(0f, length - MarginSeconds);
        }

        public static bool Overshoots(float time, float length)
        {
            return time > Limit(length);
        }

        /// <summary>
        /// 折り返した時刻。<paramref name="time"/> が <paramref name="length"/> を何周分
        /// 超えていても <c>[0, Limit(length)]</c> に入る。
        ///
        /// ★ 周回するモーション（歩行）を <c>wrapMode</c> の <c>Loop</c> ではなくこの関数で
        ///   折り返すのは、<c>ClampForever</c> のまま終端の手前で止め続けるのと同じ理由
        ///   （クラスの doc 参照）——<c>state.time</c> 自身を終端の先へ進ませない。
        /// </summary>
        public static float Wrap(float time, float length)
        {
            if (!float.IsFinite(length) || length <= 0f) return 0f;
            if (!float.IsFinite(time)) return 0f;

            var wrapped = time % length;
            if (wrapped < 0f) wrapped += length;
            return Mathf.Min(wrapped, Limit(length));
        }
    }
}
