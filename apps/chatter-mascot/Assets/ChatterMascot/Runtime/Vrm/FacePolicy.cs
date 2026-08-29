using ChatterMascot.Protocol;
using UnityEngine;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// 表情チャンネル（emotion / 瞬き / 口）の振る舞いを決めるパラメータ。
    ///
    /// ★ <c>readonly struct</c>。テストが「全部 0 の恒等入力」を作れるよう、
    ///   全フィールドを取るコンストラクタを持つ（<see cref="IdleParams"/> /
    ///   <see cref="GazeParams"/> と同じ形）。
    /// </summary>
    public readonly struct FaceParams
    {
        /// <summary>
        /// 表情の片道の指数緩和の時定数（秒）。0 以下で補間なし（即時切り替え）。
        ///
        /// ★ <b>0 にしないこと。</b> 契約上 emotion は<b>文ごとに変わりうる</b>
        ///   （要約は既定 OFF で、感情判定は文単位 —— <c>core/src/cli/worker.ts</c> の
        ///   <c>processMessage</c>）。即時切り替えだと1メッセージの中で顔がパタパタする。
        /// ★ 既定 0.15 秒は cc-mascot の実効値と一致する。あちらは
        ///   <c>MathUtils.lerp(cur, tgt, 0.1)</c> を<b>毎フレーム</b>適用しているので
        ///   フレームレート依存だが、60fps 換算の時定数は <c>(1/60) / ln(1/0.9) ≒ 0.158 秒</c>。
        /// </summary>
        public readonly float ExpressionLerpSeconds;

        /// <summary>
        /// 発話が止まってから Neutral へ戻し始めるまでの猶予（秒）。
        ///
        /// ★ <b>これは「余韻」ではない。文と文の分断を埋めるためのもの。</b>
        ///   1文＝1レコード＝1音声ファイルなので、<c>PlaybackQueue</c> は文の切れ目で必ず
        ///   head を <c>Done</c> にして次を <c>Playing</c> にする＝<b>その間 <c>Speaking</c> が
        ///   false になる</b>。猶予が無いと、メッセージの途中で毎文 Neutral に落ちる
        ///   （cc-mascot は hold を持たないので実際にそうなっている）。
        /// ★ <b>短くしないこと。</b> 合成が詰まっていると文の間隔は秒単位まで伸びる。
        /// </summary>
        public readonly float HoldSeconds;

        /// <summary>
        /// <c>kind: "prompt"</c> のときに <c>surprised</c> へ上乗せする量。<b>既定 0（何もしない）。</b>
        ///
        /// ★ <b><c>prompt</c> を表情で区別しないこと。</b> <c>prompt</c> フレームにも emotion が
        ///   載っていて（<c>promptEventFormatter</c> が生成した文を <c>classify</c> にかけている）、
        ///   表情チャンネルは emotion が使っている。重ねると<b>「怒りながら驚いている顔」</b>になる。
        /// ★ 実際 <c>AskUserQuestion</c> の質問文は <c>？</c> で終わるので、分類器の文末パターンで
        ///   <b>ほぼ確実に <c>surprised</c> になる</b>。<c>prompt</c> の区別は #59 の視線（カメラに固定）と
        ///   姿勢（前傾）、それにここで足す瞬きの3チャンネルで足りている。
        /// </summary>
        public readonly float PromptSurpriseWeight;

        /// <summary>
        /// <c>happy</c> の実効 weight がこの値を超えている間、瞬きを止める。<b>0 以下で無効。</b>
        ///
        /// ★ <b>モデル側は守ってくれない。</b> 同梱 <c>vita.vrm</c> の <c>happy</c> は
        ///   <c>Fcl_ALL_Joy</c>（VRoid の Joy は<b>目を細める形を含む</b>）に bind されているのに、
        ///   preset 14個すべて <c>overrideBlink: none</c> なので、UniVRM の
        ///   <c>DefaultExpressionValidator</c> は <c>blink</c> を一切減衰させない。
        ///   笑顔と瞬きが素のまま加算される。
        /// ★ cc-mascot が実測で入れているガード（<c>useBlink.ts</c> の
        ///   <c>HAPPY_EXPRESSION_THRESHOLD = 0.1</c>）と同じ値・同じ判定。
        /// </summary>
        public readonly float BlinkSuppressAboveHappy;

        /// <summary>
        /// <c>Neutral</c> を「<c>neutral</c> expression の weight 1.0」で表すか。<b>既定 false（全部 0）。</b>
        ///
        /// ★ <b>既定では立てないこと。</b> <c>vita.vrm</c> の <c>neutral</c> は
        ///   <c>Fcl_ALL_Neutral</c> に weight 1.0 で bind されている。VRoid では実質恒等モーフだが、
        ///   <b>モデルによっては別の顔になる</b>し、<c>neutral</c> を立てたまま <c>happy</c> を
        ///   重ねると<b>二重にブレンドされる</b>。
        /// ★ cc-mascot は逆に <c>neutral</c> を 1.0 で立てている（6本すべてに毎フレーム
        ///   <c>setValue</c> する形）。踏襲していないのはここ。
        /// </summary>
        public readonly bool UseNeutralExpression;

        public FaceParams(
            float expressionLerpSeconds,
            float holdSeconds,
            float promptSurpriseWeight,
            float blinkSuppressAboveHappy,
            bool useNeutralExpression)
        {
            ExpressionLerpSeconds = expressionLerpSeconds;
            HoldSeconds = holdSeconds;
            PromptSurpriseWeight = promptSurpriseWeight;
            BlinkSuppressAboveHappy = blinkSuppressAboveHappy;
            UseNeutralExpression = useNeutralExpression;
        }

        public static FaceParams Default => new FaceParams(
            expressionLerpSeconds: 0.15f,
            holdSeconds: 1.5f,
            promptSurpriseWeight: 0f,
            blinkSuppressAboveHappy: 0.1f,
            useNeutralExpression: false);
    }

    /// <summary>
    /// <see cref="FacePolicy"/> の入力。<b>1フレーム分の観測値だけ</b>を持つ。
    /// </summary>
    public readonly struct FaceInput
    {
        /// <summary>いま音が鳴っているか（<c>SpeakingView.TryRead</c> の返り値）</summary>
        public readonly bool Speaking;

        /// <summary>
        /// 表情に使う emotion。
        ///
        /// ★ <b>発話中に読んだ最後の値をラッチして渡すこと。</b>
        ///   <c>SpeakingView.TryRead</c> は false のとき <c>Neutral</c> に倒す契約なので
        ///   （<c>SpeakingViewTests</c> の4本が固定している）、生の値をそのまま渡すと
        ///   <b>喋り終わった瞬間に目標が Neutral になり、<see cref="FaceParams.HoldSeconds"/> が
        ///   まったく機能しない</b>。ラッチは <c>VrmCharacter</c> の側で持つ。
        /// </summary>
        public readonly Emotion Emotion;

        /// <summary>発話の種別。<c>prompt</c> の上乗せ（既定 0）にだけ使う</summary>
        public readonly SpeechKind Kind;

        /// <summary>口の開き（0..1）。<b>#58 のリップシンクが埋める。ここでは常に 0。</b></summary>
        public readonly float Mouth;

        /// <summary>瞬き（0..1）。<see cref="BlinkTimer.Tick"/> の出力</summary>
        public readonly float Blink;

        /// <summary>
        /// 現在時刻（秒）。
        /// ★ <b><c>Time.realtimeSinceStartupAsDouble</c> を渡すこと。</b>
        ///   <c>DateTimeOffset.UtcNow</c> だと時計の巻き戻しで顔が凍る
        ///   （<c>AudioIdleGate</c> / <see cref="IdlePose"/> / <see cref="GazeAim"/> と同じ理由）。
        /// </summary>
        public readonly double Now;

        /// <summary>
        /// 直近で <see cref="Speaking"/> が false に落ちた時刻。
        /// ★ 一度も喋っていない間は <c>double.NegativeInfinity</c> を渡すこと
        ///   （差が <c>+∞</c> になり、猶予の外＝Neutral に確定する）。
        /// </summary>
        public readonly double SpeechEndedAt;

        public FaceInput(
            bool speaking,
            Emotion emotion,
            SpeechKind kind,
            float mouth,
            float blink,
            double now,
            double speechEndedAt)
        {
            Speaking = speaking;
            Emotion = emotion;
            Kind = kind;
            Mouth = mouth;
            Blink = blink;
            Now = now;
            SpeechEndedAt = speechEndedAt;
        }
    }

    /// <summary>
    /// VRM の expression に流し込む weight。
    ///
    /// ★ <b>ここには <c>ExpressionKey</c> を持ち込まない。</b> UniVRM 依存にすると
    ///   <c>ChatterMascot.Tests.asmdef</c>（<c>overrideReferences: true</c>）の
    ///   <c>precompiledReferences</c> をいじる必要が出る。対応表は
    ///   <c>VrmCharacter</c>（<c>ChatterMascot.Vrm</c> アセンブリ）に1箇所だけ置く。
    /// </summary>
    public readonly struct FaceWeights
    {
        public readonly float Happy;
        public readonly float Angry;
        public readonly float Sad;
        public readonly float Relaxed;
        public readonly float Surprised;
        public readonly float Neutral;
        public readonly float Aa;
        public readonly float Blink;

        public FaceWeights(
            float happy, float angry, float sad, float relaxed,
            float surprised, float neutral, float aa, float blink)
        {
            Happy = happy;
            Angry = angry;
            Sad = sad;
            Relaxed = relaxed;
            Surprised = surprised;
            Neutral = neutral;
            Aa = aa;
            Blink = blink;
        }

        public static FaceWeights Zero => default;
    }

    /// <summary>
    /// emotion / kind / 時間 → VRM の expression weight。<b>純粋。UniVRM に依存しない。</b>
    ///
    /// ★ <b><see cref="Evaluate"/> が前フレームの weight と <c>deltaTime</c> を受け取るのは、
    ///   補間が原理的に状態を要求するから。</b> 指数緩和の開始値は「いまの weight」で、
    ///   emotion が2回続けて変わると（実運用では文ごとに変わる）入力だけからは決まらない。
    ///   状態を引数で渡すことで関数としての純粋性は保っている ——
    ///   <c>VrmCharacter</c> が <c>HeadPitchDegrees</c> を自分で保持して
    ///   <see cref="GazeAim.Smooth"/> に渡している既存の形と同じ。
    ///
    /// ★ <b>emotion → weight の対応表そのものは <see cref="Target"/> に閉じている。</b>
    ///   テストはそちらを直接見る（補間を挟むと1.0 に到達しないので等値比較が書けない）。
    /// </summary>
    public static class FacePolicy
    {
        /// <summary>
        /// 補間前の目標 weight。
        ///
        /// ★ <b>表情は one-hot。</b> 1つを 1.0、他を 0 にする。強度（分類器のスコア）は
        ///   使わない —— <c>SpeechRecord.emotion</c> は名前しか運ばない（<c>docs/protocol.md</c>）。
        /// ★ <b><c>Neutral</c> は「全部 0」で表す。</b> 理由は
        ///   <see cref="FaceParams.UseNeutralExpression"/> を参照。
        /// ★ <b><c>Aa</c> は喋っていなければ即 0。</b> UniVRM の weight は自動でゼロに戻らない
        ///   （<c>Vrm10RuntimeExpression._inputWeights</c> は保持され続ける）ので、
        ///   放置すると<b>喋り終わっても口が開きっぱなしになる</b>。
        /// </summary>
        public static FaceWeights Target(in FaceInput input, in FaceParams p)
        {
            // 「表情を出してよい期間」。喋っている間と、止まってから猶予のあいだ。
            // ★ SpeechEndedAt が double.NegativeInfinity のとき差は +∞ になり、
            //   HoldSeconds をどう設定しても false になる（起動直後の意図した挙動）。
            var active = input.Speaking || input.Now - input.SpeechEndedAt < p.HoldSeconds;

            float happy = 0f, angry = 0f, sad = 0f, relaxed = 0f, surprised = 0f, neutral = 0f;

            if (active)
            {
                switch (input.Emotion)
                {
                    case Emotion.Happy: happy = 1f; break;
                    case Emotion.Angry: angry = 1f; break;
                    case Emotion.Sad: sad = 1f; break;
                    case Emotion.Relaxed: relaxed = 1f; break;
                    case Emotion.Surprised: surprised = 1f; break;
                    default:
                        // Emotion.Neutral。★ 既定では何も立てない
                        if (p.UseNeutralExpression) neutral = 1f;
                        break;
                }

                // ★ 既定では通らない（PromptSurpriseWeight = 0）。上の doc を参照
                if (input.Kind == SpeechKind.Prompt && p.PromptSurpriseWeight > 0f)
                {
                    surprised = Mathf.Clamp01(surprised + p.PromptSurpriseWeight);
                }
            }
            else if (p.UseNeutralExpression)
            {
                neutral = 1f;
            }

            var aa = input.Speaking ? Mathf.Clamp01(input.Mouth) : 0f;
            var blink = Mathf.Clamp01(input.Blink);

            return new FaceWeights(happy, angry, sad, relaxed, surprised, neutral, aa, blink);
        }

        /// <summary>
        /// <paramref name="current"/> から <see cref="Target"/> へ片道の指数緩和をかけた結果。
        ///
        /// ★ <b><c>Aa</c> と <c>Blink</c> は補間しない。</b> <c>Aa</c> を鈍らせると口の応答が遅れ、
        ///   <c>Blink</c> は <see cref="BlinkTimer"/> が既に閉→保持→開の曲線を作っているので、
        ///   重ねて緩和すると瞬きが潰れる。
        /// ★ <b>瞬きの抑制は「緩和後の <c>happy</c>」で判定する。</b> 目が細まっているかどうかは
        ///   目標ではなく実際に適用される weight で決まる（cc-mascot も
        ///   <c>currentEmotionValues</c>＝lerp 後の値を見ている）。
        /// ★ <b>ただし<u>始まっていない</u>瞬きだけを止める。</b> cc-mascot がやっているのは
        ///   「happy のときは瞬きを<b>飛ばす</b>」（<c>performBlink</c> の入口で <c>return</c>）で、
        ///   <b>進行中の瞬きを途中で切ってはいない</b>。出力を無条件に 0 にすると、
        ///   閉じ切っている最中（<c>blink = 1.0</c>）に happy が立った瞬間に
        ///   <b>1フレームで目が開く段差</b>が入る —— <c>Blink</c> は意図的に補間していないので
        ///   吸収するものが無い。<c>current.Blink</c> が 0 のとき（＝まだ閉じ始めていないとき）
        ///   だけ止めれば、始まった瞬きは最後まで走り、以後の瞬きが飛ばされる。
        /// ★ <b><see cref="FaceParams"/> を全部 0 にすると <see cref="Target"/> と一致する</b>
        ///   （<c>tau &lt;= 0</c> で <see cref="GazeAim.Smooth"/> が目標を返し、
        ///   猶予も上乗せも抑制も無効になる）。テストが恒等入力を作れるようにするため。
        /// </summary>
        public static FaceWeights Evaluate(in FaceInput input, in FaceWeights current, float deltaTime, in FaceParams p)
        {
            var target = Target(input, p);
            var tau = p.ExpressionLerpSeconds;

            // ★ GazeAim.Smooth を使い回すこと（書き写さない）。フレームレート非依存の
            //   指数緩和はこのリポジトリに1つしかない —— 逐語で重複させると、片方だけ直しても
            //   テストは両方とも独立に緑のまま通る（Oscillator を切り出した経緯と同じ。PR #69）
            var happy = GazeAim.Smooth(current.Happy, target.Happy, deltaTime, tau);
            var angry = GazeAim.Smooth(current.Angry, target.Angry, deltaTime, tau);
            var sad = GazeAim.Smooth(current.Sad, target.Sad, deltaTime, tau);
            var relaxed = GazeAim.Smooth(current.Relaxed, target.Relaxed, deltaTime, tau);
            var surprised = GazeAim.Smooth(current.Surprised, target.Surprised, deltaTime, tau);
            var neutral = GazeAim.Smooth(current.Neutral, target.Neutral, deltaTime, tau);

            var blink = target.Blink;
            if (p.BlinkSuppressAboveHappy > 0f && happy > p.BlinkSuppressAboveHappy && current.Blink <= 0f)
            {
                blink = 0f;
            }

            return new FaceWeights(happy, angry, sad, relaxed, surprised, neutral, target.Aa, blink);
        }
    }
}
