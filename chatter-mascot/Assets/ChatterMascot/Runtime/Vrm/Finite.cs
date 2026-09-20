using UnityEngine;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// 「有限か」の判定（#103）。<b><c>float.IsNaN</c> だけでは足りない</b>——重複キーの接線は
    /// 0/0（NaN）だけでなく x/0（±Infinity）にもなるので、外挿された姿勢を漏らさず拾うには
    /// <c>float.IsFinite</c> で両方を弾く。
    /// </summary>
    public static class Finite
    {
        public static bool IsFinite(Vector3 v)
        {
            return float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        }
    }
}
