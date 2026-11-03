using UnityEngine;

namespace ChatterMascot.Xr
{
    /// <summary>画面を映す板の初期配置。<b>純粋関数。</b></summary>
    public static class XrBoardRules
    {
        /// <summary>板の幅（m）。</summary>
        public const float DefaultWidth = 1.2f;

        /// <summary>キャラクターから見て、視点と反対側へ離す距離（m）。</summary>
        public const float BehindCharacter = 0.5f;

        /// <summary>
        /// 目の高さで、視点→キャラクターの水平線上のキャラクターの奥に置き、視点へ正対させる。
        /// 真上・真下などで水平方向が定まらないときは <paramref name="fallbackForward"/> の水平成分を使う。
        /// </summary>
        public static (Vector3 position, Quaternion rotation) InitialPose(
            Vector3 viewer, Vector3 character, Vector3 fallbackForward)
        {
            var forward = Flatten(character - viewer);
            if (forward == Vector3.zero) forward = Flatten(fallbackForward);
            if (forward == Vector3.zero) forward = Vector3.forward;

            var position = character + forward * BehindCharacter;
            position.y = viewer.y;

            var look = Flatten(position - viewer);
            if (look == Vector3.zero) look = forward;
            return (position, Quaternion.LookRotation(look, Vector3.up));
        }

        private static Vector3 Flatten(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude < 1e-4f ? Vector3.zero : v.normalized;
        }
    }
}
