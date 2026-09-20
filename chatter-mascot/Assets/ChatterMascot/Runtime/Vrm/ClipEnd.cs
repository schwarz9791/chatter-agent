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
    }
}
