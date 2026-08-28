using ChatterMascot.Protocol;
using UnityEngine;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// 視線の3状態（カーソル追従 / 自律的な漂い / prompt で正面を見る）のパラメータ。
    ///
    /// ★ <c>readonly struct</c>。テストが感度 0 の恒等入力を作れるよう、
    ///   全フィールドを取るコンストラクタを持つ。
    /// </summary>
    public readonly struct GazeParams
    {
        /// <summary>漂いの周期X（秒）。<see cref="WanderSecondsY"/> と互いに素に近い値にして非周期に見せる</summary>
        public readonly float WanderSecondsX;

        /// <summary>漂いの周期Y（秒）</summary>
        public readonly float WanderSecondsY;

        /// <summary>漂いの振幅X（メートル）</summary>
        public readonly float WanderMetersX;

        /// <summary>漂いの振幅Y（メートル）</summary>
        public readonly float WanderMetersY;

        /// <summary>カーソル方向へ目標位置を動かす感度。cc-mascot の既定値（0.4）を踏襲</summary>
        public readonly float EyeSensitivity;

        /// <summary>カーソル方向へ頭を向ける感度。cc-mascot の既定値（0.1）を踏襲</summary>
        public readonly float HeadSensitivity;

        /// <summary>
        /// 正規化カーソルの縦成分 1.0 あたり何度、頭を上下に向けるかの<b>係数</b>
        /// （<see cref="HeadSensitivity"/> と掛け合わさる）。<b>同時に clamp の上限
        /// （±この値）</b> でもある。cc-mascot の既定値（25）を踏襲。
        ///
        /// ★ <b>出荷値（<c>HeadSensitivity = 0.1</c>）では、実効の可動域を決めるのは
        ///   clamp ではなく<b>ディスプレイの広さ</b>。</b> clamp に到達するには正規化カーソル
        ///   <c>|c| &gt;= 10</c>（<c>CursorGazeSource</c> の固定 800pt 正規化で中心から約 4000pt）が
        ///   要る。実測では画面の右端で <c>|c| = 8.29</c>（＝ヨー約 29°）まで届いていて
        ///   （<c>docs/mascot.md</c>「カーソルの正規化をウィンドウの大きさで割らない」）、
        ///   <b>clamp まで2割ほどしか余裕がない</b>。
        /// ★ <b>だから clamp は死んでいない。</b> より広い構成（マルチディスプレイなど）では
        ///   実際に働き、「追従」ではなく「最大まで曲げて固まる」のを防ぐ。
        ///   <b>この値を下げると、いま追従している範囲がそのまま頭打ちになる</b>ので、
        ///   「上限だから安全側」と考えて下げないこと。
        /// </summary>
        public readonly float HeadPitchRangeDegrees;

        /// <summary>
        /// 正規化カーソルの横成分 1.0 あたり何度、頭を左右に向けるかの<b>係数</b>
        /// （<see cref="HeadSensitivity"/> と掛け合わさる）。<b>同時に clamp の上限
        /// （±この値）</b> でもある。cc-mascot の既定値（35）を踏襲。
        ///
        /// ★ <see cref="HeadPitchRangeDegrees"/> と同じく、出荷値（<c>HeadSensitivity = 0.1</c>）
        ///   では実効の可動域を決めるのは clamp ではなくディスプレイの広さ（実測で画面右端が
        ///   ヨー約 29°、clamp は 35°）。詳細は <see cref="HeadPitchRangeDegrees"/> の doc を参照。
        /// </summary>
        public readonly float HeadYawRangeDegrees;

        /// <summary>目標位置（<c>TargetLocalPosition</c>）の長さの上限（メートル）</summary>
        public readonly float EyeReachMeters;

        /// <summary><see cref="GazeAim.Smooth"/> に渡す追従の時定数（秒）</summary>
        public readonly float FollowSeconds;

        public GazeParams(
            float wanderSecondsX,
            float wanderSecondsY,
            float wanderMetersX,
            float wanderMetersY,
            float eyeSensitivity,
            float headSensitivity,
            float headPitchRangeDegrees,
            float headYawRangeDegrees,
            float eyeReachMeters,
            float followSeconds)
        {
            WanderSecondsX = wanderSecondsX;
            WanderSecondsY = wanderSecondsY;
            WanderMetersX = wanderMetersX;
            WanderMetersY = wanderMetersY;
            EyeSensitivity = eyeSensitivity;
            HeadSensitivity = headSensitivity;
            HeadPitchRangeDegrees = headPitchRangeDegrees;
            HeadYawRangeDegrees = headYawRangeDegrees;
            EyeReachMeters = eyeReachMeters;
            FollowSeconds = followSeconds;
        }

        public static GazeParams Default => new GazeParams(
            wanderSecondsX: 5.3f,
            wanderSecondsY: 8.7f,
            wanderMetersX: 0.25f,
            wanderMetersY: 0.15f,
            eyeSensitivity: 0.4f,
            headSensitivity: 0.1f,
            headPitchRangeDegrees: 25f,
            headYawRangeDegrees: 35f,
            eyeReachMeters: 0.6f,
            followSeconds: 0.15f);
    }

    /// <summary>ある瞬間の視線の目標。</summary>
    public readonly struct GazeSample
    {
        /// <summary>
        /// <c>gazeTarget.localPosition</c> に書く値（Main Camera のローカル座標）。
        /// <b>z は常に 0</b>（カメラ位置の平面上に置く）。
        /// </summary>
        public readonly Vector3 TargetLocalPosition;

        /// <summary>
        /// 頭ボーンに乗せる上下方向の角度（度）。
        ///
        /// ★ <b>符号の意味（正が上か下か）はここでは決めていない。</b> 実ボーンへ適用する
        ///   <c>VrmPoseAccent</c>（別担当）と、モデルの向き（<see cref="VrmOrientation"/> が決めた
        ///   正面）の組み合わせで決まるので、実機のログで確かめる。「pitch が正なら上を向く」と
        ///   決め打ちで読まないこと。
        /// </summary>
        public readonly float HeadPitchDegrees;

        /// <summary>
        /// 頭ボーンに乗せる左右方向の角度（度）。<c>HeadPitchDegrees</c> と同じく符号の意味は
        /// ここでは決めていない。
        /// </summary>
        public readonly float HeadYawDegrees;

        public GazeSample(Vector3 targetLocalPosition, float headPitchDegrees, float headYawDegrees)
        {
            TargetLocalPosition = targetLocalPosition;
            HeadPitchDegrees = headPitchDegrees;
            HeadYawDegrees = headYawDegrees;
        }
    }

    /// <summary>
    /// 視線を3状態（カーソル追従 / 自律的な漂い / prompt）で決める。<b>純粋関数。</b>
    ///
    /// | 状態 | 目 | 頭 |
    /// |---|---|---|
    /// | <c>kind == Prompt</c> | 常に <see cref="Vector3.zero"/>（cursor の有無に関わらず） | 0 / 0 |
    /// | <c>cursor == null</c> | sin 2本で漂う | 0 / 0 |
    /// | <c>cursor != null</c> | カーソル方向。<c>EyeSensitivity</c> を掛け <c>EyeReachMeters</c> で clamp | cursor 成分 × 感度 × Range を ±Range で clamp |
    /// </summary>
    public static class GazeAim
    {
        /// <param name="cursor">
        /// ウィンドウ基準で -1..1 に正規化したカーソル。取れなければ <c>null</c>
        /// （Android XR にカーソルは無い / Desktop アセンブリごと存在しない）。
        /// </param>
        public static GazeSample Evaluate(double now, in GazeParams p, SpeechKind kind, Vector2? cursor)
        {
            if (kind == SpeechKind.Prompt)
            {
                // ★ cursor の有無に関わらず常に正面（カメラ＝ユーザー）を見る。
                //   前傾は VrmPoseAccent の役目なので、ここでは目と頭を戻すだけ。
                return new GazeSample(Vector3.zero, 0f, 0f);
            }

            if (cursor.HasValue)
            {
                var c = cursor.Value;

                var target = new Vector3(c.x, c.y, 0f) * p.EyeSensitivity;
                target = Vector3.ClampMagnitude(target, p.EyeReachMeters);

                var pitch = Mathf.Clamp(c.y * p.HeadSensitivity * p.HeadPitchRangeDegrees,
                    -p.HeadPitchRangeDegrees, p.HeadPitchRangeDegrees);
                var yaw = Mathf.Clamp(c.x * p.HeadSensitivity * p.HeadYawRangeDegrees,
                    -p.HeadYawRangeDegrees, p.HeadYawRangeDegrees);

                return new GazeSample(target, pitch, yaw);
            }

            // 自律的な漂い: 周期の違う2本の sin（X/Y）を独立に動かして非周期に見せる。
            // 頭の微動は IdlePose の首（NeckEuler / HeadEuler）が持つので、ここは 0 のまま。
            var wanderX = Mathf.Sin(Oscillator.Phase(now, p.WanderSecondsX)) * p.WanderMetersX;
            var wanderY = Mathf.Sin(Oscillator.Phase(now, p.WanderSecondsY)) * p.WanderMetersY;
            return new GazeSample(new Vector3(wanderX, wanderY, 0f), 0f, 0f);
        }

        /// <summary>
        /// フレームレート非依存の指数緩和。<c>tau</c>（時定数、秒）で書く。
        ///
        /// ★ cc-mascot の <c>LERP_FACTOR = 0.08</c> は<b>毎フレーム適用</b>するので、
        ///   30fps では 60fps の半分の速さになってしまう。ここでは
        ///   <c>1 - exp(-deltaTime / tau)</c> を係数にすることで、
        ///   <c>deltaTime</c> を分割して複数回呼んでも合計の効果が変わらないようにしてある。
        /// </summary>
        public static float Smooth(float current, float target, float deltaTime, float tau)
        {
            if (tau <= 0f) return target;
            if (deltaTime <= 0f) return current;

            var t = 1f - Mathf.Exp(-deltaTime / tau);
            return Mathf.Lerp(current, target, t);
        }
    }
}
