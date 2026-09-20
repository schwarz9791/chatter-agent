using System.Collections.Generic;
using ChatterMascot.Protocol;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// ワンショットで再生するモーションの分類（#70）。<c>Emotion</c> の5値に、
    /// 発話が続かない間の小ネタ用に <see cref="Idle"/> を足したもの。
    ///
    /// ★ <c>Emotion.Neutral</c> に対応するカテゴリは無い。<see cref="MotionCategories.FromEmotion"/>
    ///   参照。
    /// </summary>
    public enum MotionCategory
    {
        Idle,
        Happy,
        Angry,
        Sad,
        Relaxed,
        Surprised,
    }

    /// <summary>
    /// <see cref="MotionCategory"/> の付随情報。<c>AnimationManifest</c> の走査と
    /// <c>EmotionMotionTrigger</c> の判定がここに寄りかかる。
    /// </summary>
    public static class MotionCategories
    {
        /// <summary>
        /// 全カテゴリ。<see cref="AnimationManifest.Build"/> がルートごとにこの順で走査する。
        ///
        /// ★ <c>IReadOnlyList</c> に固定した配列。呼び出し側が書き換えて全体の走査順が
        ///   崩れないようにする。
        /// </summary>
        public static readonly IReadOnlyList<MotionCategory> All = new[]
        {
            MotionCategory.Idle,
            MotionCategory.Happy,
            MotionCategory.Angry,
            MotionCategory.Sad,
            MotionCategory.Relaxed,
            MotionCategory.Surprised,
        };

        /// <summary>
        /// <c>animations/&lt;ここ&gt;/*.vrma</c> のディレクトリ名（小文字）。
        ///
        /// ★ <b>カテゴリはこの名前で聞いたディレクトリから決める</b>（<c>AnimationManifest.Build</c>）。
        ///   <c>ListFiles</c> はフルパスを返すので、返ってきたパス文字列からカテゴリを逆算しない
        ///   ——OS によって区切り文字が違い、パース側にまた <c>Path.*</c> 相当の分岐が要る。
        /// </summary>
        public static string DirectoryName(MotionCategory category)
        {
            switch (category)
            {
                case MotionCategory.Idle: return "idle";
                case MotionCategory.Happy: return "happy";
                case MotionCategory.Angry: return "angry";
                case MotionCategory.Sad: return "sad";
                case MotionCategory.Relaxed: return "relaxed";
                case MotionCategory.Surprised: return "surprised";
                default: return category.ToString();
            }
        }

        /// <summary>
        /// 文の <c>emotion</c> から発火すべきカテゴリへ。<c>Neutral</c> は <c>null</c>
        /// （感情モーションを起こさない）。
        /// </summary>
        public static MotionCategory? FromEmotion(Emotion emotion)
        {
            switch (emotion)
            {
                case Emotion.Happy: return MotionCategory.Happy;
                case Emotion.Angry: return MotionCategory.Angry;
                case Emotion.Sad: return MotionCategory.Sad;
                case Emotion.Relaxed: return MotionCategory.Relaxed;
                case Emotion.Surprised: return MotionCategory.Surprised;
                default: return null;
            }
        }
    }
}
