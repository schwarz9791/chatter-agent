using System;
using System.Globalization;

namespace ChatterMascot.Window
{
    /// <summary>
    /// <b>ポイント</b>で表した矩形。<c>X</c> / <c>Y</c> は<b>最小コーナー（左下）</b>。
    ///
    /// ★ <b>単位を型名に持たせるためだけに存在する。</b> 素の <c>Rect</c> / <c>Vector2</c> を
    ///   使うと、<c>Screen.width</c>（バッキング px）と <c>UniWindowController.windowSize</c>
    ///   （NSWindow のポイント）が同じ型になり、混ぜても誰も気づかない。
    ///   <c>WindowSizeKeeper</c> はこれで<b>「打ち消し」を「倍化」に変えた</b>
    ///   （Retina 2x で窓が起動ごとに倍へ育った。→ <c>docs/mascot.md</c>）。
    ///
    /// ★ <b>Y は bottom-up。</b> 原点はメインディスプレイのフルフレームの左下。
    ///   実測で確定している（→ <c>docs/mascot.md</c>「ウィンドウの座標系は bottom-up・左下基準」）。
    /// </summary>
    public readonly struct PointRect : IEquatable<PointRect>
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Width;
        public readonly float Height;

        public PointRect(float x, float y, float width, float height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public float MaxX => X + Width;
        public float MaxY => Y + Height;

        /// <summary>
        /// 大きさが正で、どの成分も NaN / Inf でないこと。
        ///
        /// ★ <b>保存ファイルは人が編集しうる</b>ので、読んだ値をそのまま信じない。
        /// </summary>
        public bool IsValid =>
            Width > 0f && Height > 0f &&
            IsFinite(X) && IsFinite(Y) && IsFinite(Width) && IsFinite(Height);

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        /// <summary>共通部分。重なりが無ければ大きさ 0 の矩形を返す。</summary>
        public PointRect Intersect(PointRect other)
        {
            var x = Math.Max(X, other.X);
            var y = Math.Max(Y, other.Y);
            var maxX = Math.Min(MaxX, other.MaxX);
            var maxY = Math.Min(MaxY, other.MaxY);
            if (maxX <= x || maxY <= y) return new PointRect(x, y, 0f, 0f);
            return new PointRect(x, y, maxX - x, maxY - y);
        }

        public float Area => Width <= 0f || Height <= 0f ? 0f : Width * Height;

        public PointRect WithSize(float width, float height) => new PointRect(X, Y, width, height);

        public PointRect WithPosition(float x, float y) => new PointRect(x, y, Width, Height);

        /// <summary>
        /// <b>位置だけ</b>動かして <paramref name="bounds"/> に収める。大きさは変えない。
        ///
        /// ★ <b>入りきらない軸は最小コーナーに寄せる。</b> はみ出す向きを選べないので、
        ///   「上にはみ出す」（メニューバーの下に潜って掴めない）より
        ///   「下にはみ出す」（頭が見えていて掴める）を採る。
        /// </summary>
        public PointRect ClampInto(PointRect bounds)
        {
            var x = Width >= bounds.Width
                ? bounds.X
                : Math.Min(Math.Max(X, bounds.X), bounds.MaxX - Width);
            var y = Height >= bounds.Height
                ? bounds.Y
                : Math.Min(Math.Max(Y, bounds.Y), bounds.MaxY - Height);
            return WithPosition(x, y);
        }

        /// <summary>
        /// <paramref name="bounds"/> の<b>下端中央</b>へ置く。
        ///
        /// ★ cc-mascot の既定位置と同じ。マスコットは足元が下端に接している方が自然で、
        ///   かつ<b>作業領域の下端はメニューバーから最も遠い</b>ので、掴めなくなりにくい。
        /// </summary>
        public PointRect AtBottomCenterOf(PointRect bounds)
        {
            return WithPosition(bounds.X + (bounds.Width - Width) * 0.5f, bounds.Y);
        }

        public bool Equals(PointRect other) =>
            X.Equals(other.X) && Y.Equals(other.Y) && Width.Equals(other.Width) && Height.Equals(other.Height);

        public override bool Equals(object obj) => obj is PointRect other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = X.GetHashCode();
                hash = (hash * 397) ^ Y.GetHashCode();
                hash = (hash * 397) ^ Width.GetHashCode();
                hash = (hash * 397) ^ Height.GetHashCode();
                return hash;
            }
        }

        /// <summary>★ ロケールで <c>0,5</c> にならないよう固定書式で出す。</summary>
        public override string ToString() =>
            X.ToString("F0", CultureInfo.InvariantCulture) + "," +
            Y.ToString("F0", CultureInfo.InvariantCulture) + " " +
            Width.ToString("F0", CultureInfo.InvariantCulture) + "x" +
            Height.ToString("F0", CultureInfo.InvariantCulture);
    }
}
