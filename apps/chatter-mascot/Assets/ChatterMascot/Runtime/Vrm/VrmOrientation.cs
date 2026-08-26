using UnityEngine;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// 読み込んだモデルがカメラの方を向いているかを判定する。<b>純粋関数。</b>
    ///
    /// ★ <b>「VRM 1.0 は必ず正面を向く」と決め打ちにしないこと。</b> #56 の issue 本文は
    ///   「glTF→Unity は Z 反転なので Unity 上ではモデルが −Z を向く → 顔がこちらを向く。
    ///   180°回転は不要」と書いていたが、<b>実機では背中が映った</b>（同梱の vita.vrm）。
    ///   仕様の読みではなく<b>ボーンの並びから実際の向きを出す</b>。
    ///
    /// ★ <b>T ポーズを前提にしてよい。</b> VRM 1.0 はレストポーズが T ポーズ必須なので、
    ///   読み込み直後（アニメーションを当てる前）の肩の並びは必ず横一直線になる。
    /// </summary>
    public static class VrmOrientation
    {
        /// <summary>
        /// 肩の並びからモデルの正面（ワールド）を出す。
        ///
        /// Unity は左手系で、+Z を向いた人物の<b>右手が +X</b> 側に来る。
        /// つまり「右腕 → 左腕」のベクトルは正面が +Z のとき −X を向く。
        /// そこから <c>Cross(up, 右→左)</c> で正面が出る。
        /// </summary>
        public static Vector3 Forward(Vector3 leftUpperArm, Vector3 rightUpperArm)
        {
            var rightToLeft = leftUpperArm - rightUpperArm;
            // 上下成分は肩の高さのぶれなので落とす
            rightToLeft.y = 0f;
            if (rightToLeft.sqrMagnitude <= 1e-8f) return Vector3.zero;

            return Vector3.Cross(Vector3.up, rightToLeft.normalized).normalized;
        }

        /// <summary>
        /// カメラの方を向かせるのに必要な Y 軸の回転（度）。
        ///
        /// カメラは回転なしで <b>+Z を見ている</b>ので、モデルには <b>−Z</b> を
        /// 向いていてほしい。判定できないときは 0（回さない）。
        /// </summary>
        public static float YawToFaceCamera(Vector3 leftUpperArm, Vector3 rightUpperArm)
        {
            var forward = Forward(leftUpperArm, rightUpperArm);
            if (forward == Vector3.zero) return 0f;

            // −Z との角度。真横を向いているモデルでも正面へ向け直せる
            var yaw = Vector3.SignedAngle(forward, Vector3.back, Vector3.up);
            // 1度未満は回さない（浮動小数の誤差で毎回わずかに回るのを避ける）
            return Mathf.Abs(yaw) < 1f ? 0f : yaw;
        }
    }
}
