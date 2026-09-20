using System;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// sin 系の手続き的アニメーション（<see cref="IdlePose"/> の呼吸・重心移動・首の微動、
    /// <see cref="GazeAim"/> の自律的な視線の漂い）が共有する位相計算。
    ///
    /// ★ <b>ここを2箇所に書き写さないこと。</b> 以前 <see cref="IdlePose"/> と
    ///   <see cref="GazeAim"/> に本体もコメントも逐語で重複していて、片方だけ直しても
    ///   <b>テストは両方とも独立に緑のまま通る</b>状態だった（PR #69 のレビューで指摘）。
    /// </summary>
    public static class Oscillator
    {
        /// <summary>
        /// 2π t / period。周期が 0 以下なら止まっているものとして 0 を返す。
        ///
        /// ★ <b>float へ落とす前に周期で畳むこと。</b> このアプリは常駐するので
        ///   <c>now</c>（<c>Time.realtimeSinceStartupAsDouble</c>）は日単位まで伸びる。
        ///   畳まずに <c>(float)(2π·now/period)</c> とすると、7日で位相が 95万に達し、
        ///   そこでの float の刻み幅（約 0.0625 rad ＝ 3.6°）が1フレームぶんの位相差を
        ///   上回る。症状は「何日か点けっぱなしにすると呼吸や視線の漂いがカクつく／
        ///   止まって見える」で、<b>エラーは出ない</b>。
        /// </summary>
        public static float Phase(double now, float periodSeconds)
        {
            if (periodSeconds <= 0f) return 0f;
            var wrapped = now - Math.Floor(now / periodSeconds) * periodSeconds;
            return (float)(2.0 * Math.PI * wrapped / periodSeconds);
        }
    }
}
