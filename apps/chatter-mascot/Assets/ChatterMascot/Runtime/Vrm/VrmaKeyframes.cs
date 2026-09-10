using System.Collections.Generic;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// VRMA の時刻キー（<c>animations[].samplers[].input</c>）の判定（#103）。<b>純粋</b>。
    ///
    /// ★ <c>IReadOnlyList&lt;float&gt;</c> で受ける。呼び出し側（<see cref="VrmaLoader"/>）は
    ///   <c>GltfData.GetArrayFromAccessor</c> が返す <c>NativeArray&lt;float&gt;</c> を
    ///   <c>ToArray()</c> で写してから渡す——<c>ChatterMascot.Runtime</c> asmdef に
    ///   <c>Unity.Collections</c> を足さないための境界。
    /// </summary>
    public static class VrmaKeyframes
    {
        /// <summary>
        /// 単調増加でない箇所（<c>times[i] &lt;= times[i-1]</c>）の個数。末尾の重複キーはここに数えられる。
        /// </summary>
        public static int CountNonIncreasing(IReadOnlyList<float> times)
        {
            if (times == null) return 0;

            var count = 0;
            for (var i = 1; i < times.Count; i++)
            {
                if (times[i] <= times[i - 1]) count++;
            }
            return count;
        }

        /// <summary>
        /// 読み込み時の警告文を組む。<paramref name="count"/> は 1 以上のときだけ呼ぶこと
        /// （0 件は呼び出し側で出さない判断をする）。
        /// </summary>
        public static string Describe(string fileName, int count, int keyCount)
        {
            return $"{fileName} の時刻キーに単調増加でない箇所が {count} つあります（{keyCount} キー）。" +
                   "終端の外挿で NaN になるので ClipEnd が終端の手前で止めます";
        }
    }
}
