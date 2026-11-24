using UnityEngine;

namespace ChatterMascot.Xr
{
    /// <summary>
    /// マウス（端末のタッチパッド）を最近触ったかの判定。<b>純粋ロジック。</b>
    ///
    /// ★ 端末のタッチパッドは、触っていなくても追跡中と報告され、向きも勝手に変わることがある
    ///   （頭に付いてくる、空間に留まる）。<b>向きの変化は、ワールドから見ても頭から見ても
    ///   変わったときだけ動かしたとみなす</b>——頭を振っただけでは、どちらか一方しか変わらない。
    /// ★ 追跡が外れたら基準の向きを捨てる。戻った直後の向きは、触った結果とは限らない。
    /// ★ テスト asmdef が <c>ChatterMascot.Xr</c> を参照しないので、<see cref="XrGrabRules"/> と
    ///   同じく Runtime に置く。
    /// </summary>
    public sealed class XrMouseActivity
    {
        /// <summary>動かしも押しもしていない状態がこれだけ続いたら、触っていないとみなす（秒）。既定値。</summary>
        public const float IdleSeconds = 3f;

        /// <summary>動かしたとみなす向きの変化（度）。既定値。</summary>
        public const float MoveDegrees = 1f;

        private bool _hasReference;
        private Vector3 _referenceWorld;
        private Vector3 _referenceHead;
        private double _activeAt = double.NegativeInfinity;

        /// <param name="tracked">マウスが追跡されているか。</param>
        /// <param name="worldDirection">aim の向き（ワールド）。</param>
        /// <param name="headDirection">aim の向き（頭から見たもの）。</param>
        /// <param name="pressing">押しているか。</param>
        /// <param name="now">現在時刻（秒）。</param>
        public void Update(bool tracked, Vector3 worldDirection, Vector3 headDirection, bool pressing, double now)
        {
            if (!tracked)
            {
                _hasReference = false;
                return;
            }

            if (!_hasReference)
            {
                Take(worldDirection, headDirection);
                if (pressing) _activeAt = now;
                return;
            }

            var moved = Vector3.Angle(worldDirection, _referenceWorld) > MoveDegrees &&
                        Vector3.Angle(headDirection, _referenceHead) > MoveDegrees;
            if (!moved && !pressing) return;

            _activeAt = now;
            if (moved) Take(worldDirection, headDirection);
        }

        /// <summary>しばらく動かしも押しもしていないか。</summary>
        public bool IsIdle(double now) => now - _activeAt > IdleSeconds;

        private void Take(Vector3 worldDirection, Vector3 headDirection)
        {
            _hasReference = true;
            _referenceWorld = worldDirection;
            _referenceHead = headDirection;
        }
    }
}
