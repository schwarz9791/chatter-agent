using System;
using UnityEngine;

namespace ChatterMascot.Xr
{
    /// <summary>徘徊の状態。</summary>
    public enum WanderPhase
    {
        /// <summary>次の目的地を待っている。</summary>
        Resting,

        /// <summary>目的地へ歩いている。</summary>
        Walking,

        /// <summary>到着後、カメラの方へ向き直っている。</summary>
        Facing,
    }

    /// <summary>
    /// 歩行範囲の円の中を徘徊する状態機械。<b>純粋。</b> 時計は引数 <c>now</c> で受け、
    /// <c>UnityEngine.Time</c> は読まない。位置とヨーの適用（<c>ModelAnchor</c> への反映）・
    /// 発話や把持による停止条件の合成は呼び出し側（<c>XrWalk</c>）の仕事。
    ///
    /// ★ テスト asmdef が <c>ChatterMascot.Xr</c> を参照しないので、<see cref="XrGrabRules"/> と
    ///   同じく Runtime に置く。
    /// </summary>
    public sealed class Wander
    {
        /// <summary>発話終了から次の目的地を抽選するまでの待ち（秒）の下限・上限。</summary>
        public const float MinRestSeconds = 40f;
        public const float MaxRestSeconds = 90f;

        /// <summary>
        /// 抽選した目的地までの移動がこれ（秒）より短ければ引き直す。フェードの出入りだけで
        /// 終わる移動は、歩いたように見えないため。
        /// </summary>
        public const float MinTravelSeconds = 1f;

        /// <summary>
        /// 移動の下限は半径のこの割合で頭打ちにする。速いほど下限が伸びて一度も歩けなくなるのを防ぐ。
        /// </summary>
        public const float MinTravelRadiusCap = 0.5f;

        /// <summary>1回の抽選で目的地を引き直す上限。すべて短ければ待ちからやり直す。</summary>
        private const int DestinationDraws = 3;

        /// <summary>目的地への到着判定。</summary>
        public const float ArrivalMeters = 0.02f;

        /// <summary>歩行がこれを超えたら打ち切る保険。</summary>
        public const float MaxWalkSeconds = 12f;

        /// <summary>ヨーを寄せる速さ。</summary>
        public const float TurnDegreesPerSecond = 120f;


        /// <summary>カメラへ向き直ったとみなす、ヨーの残差の許容。</summary>
        private const float FacingToleranceDegrees = 0.5f;

        public WanderPhase Phase { get; private set; }
        public Vector2 Position { get; private set; }
        public float YawDegrees { get; private set; }

        private Vector2 _target;
        private double _walkStartedAt;

        private bool _nextWanderArmed;
        private double _nextWanderAt;

        private bool _hasLastUpdate;
        private double _lastUpdateAt;

        public Wander(Vector2 position, float yawDegrees)
        {
            Position = position;
            YawDegrees = yawDegrees;
            Phase = WanderPhase.Resting;
        }

        /// <summary>
        /// 現在地とヨーを置き直し、待ち時間の抽選もやり直す。
        /// キャラクターを手で置き直したとき（<c>XrWalk.PlaceAt</c>）に呼ぶ。
        /// </summary>
        public void Reset(Vector2 position, float yawDegrees)
        {
            Position = position;
            YawDegrees = yawDegrees;
            Phase = WanderPhase.Resting;
            _nextWanderArmed = false;
            _hasLastUpdate = false;
        }

        /// <summary>
        /// 外部の事情（感情モーションへの割り込みなど）で歩行を続けられなくなったら、
        /// <see cref="WanderPhase.Facing"/> へ進める。歩行中でなければ何もしない。
        /// </summary>
        public void Stop()
        {
            if (Phase == WanderPhase.Walking) Phase = WanderPhase.Facing;
        }

        /// <summary>
        /// 目的地を外から指定して歩き出す（歩行範囲の中を指して「そこへ行け」と言う操作）。
        ///
        /// ★ <b>短すぎる移動の見送り（<see cref="MinTravelSeconds"/>）は掛けない。</b> 抽選と違って
        ///   指された移動は長さに関わらず意図されたもの。
        /// ★ 円の中かどうかは呼び出し側が確かめる（この型は円の状態を持たない）。
        /// </summary>
        public void GoTo(double now, Vector2 destination)
        {
            _target = destination;
            Phase = WanderPhase.Walking;
            _walkStartedAt = now;
            _nextWanderArmed = false;
        }

        /// <summary>1フレームぶん状態を進める。</summary>
        /// <param name="speaking">発話中か。歩行中に true になったら即 <see cref="WanderPhase.Facing"/> へ</param>
        /// <param name="canWalk">歩行モーションを再生できる状態か。false の間は完全に動かない</param>
        /// <param name="center">歩行範囲の中心（xz）</param>
        /// <param name="radius">歩行範囲の半径</param>
        /// <param name="speed">移動速度（m/s）。<see cref="Speed"/> で求める</param>
        /// <param name="cameraPositionXz">向き直る先（カメラの xz）</param>
        /// <param name="random">0以上1未満の乱数源</param>
        public void Update(
            double now, bool speaking, bool canWalk,
            Vector2 center, float radius, float speed,
            Vector2 cameraPositionXz, Func<double> random)
        {
            var dt = _hasLastUpdate ? Mathf.Max(0f, (float)(now - _lastUpdateAt)) : 0f;
            _lastUpdateAt = now;
            _hasLastUpdate = true;

            // ★ 歩けない事情（発話中・モーションが無い・つまみ中）はどれも歩行を打ち切る。
            //   ただし向き直りはクリップを使わないので最後まで進める —— 喋っている間ずっと
            //   背を向けたまま固まらないように。
            if (speaking || !canWalk)
            {
                if (Phase == WanderPhase.Walking) Phase = WanderPhase.Facing;
                if (Phase == WanderPhase.Resting)
                {
                    // ★ 待ちを引き直す起点は「動けなくなった」ではなく「動けるようになった」瞬間。
                    //   ここでは腕を折るだけにして、次に Resting を評価するときに引き直させる。
                    _nextWanderArmed = false;
                    return;
                }
            }

            switch (Phase)
            {
                case WanderPhase.Resting:
                    UpdateResting(now, center, radius, speed, random);
                    break;
                case WanderPhase.Walking:
                    UpdateWalking(now, dt, center, radius, speed);
                    break;
                case WanderPhase.Facing:
                    UpdateFacing(dt, cameraPositionXz);
                    break;
            }
        }

        private void UpdateResting(double now, Vector2 center, float radius, float speed, Func<double> random)
        {
            if (!_nextWanderArmed)
            {
                _nextWanderAt = now + RandomWaitSeconds(random);
                _nextWanderArmed = true;
                return;
            }

            if (now < _nextWanderAt) return;

            var minTravel = Mathf.Min(speed * MinTravelSeconds, radius * MinTravelRadiusCap);

            for (var draw = 0; draw < DestinationDraws; draw++)
            {
                var destination = PickDestination(center, radius, random);
                if (Vector2.Distance(destination, Position) < minTravel) continue;

                _target = destination;
                Phase = WanderPhase.Walking;
                _walkStartedAt = now;
                _nextWanderArmed = false;
                return;
            }

            _nextWanderAt = now + RandomWaitSeconds(random);
        }

        private void UpdateWalking(double now, float dt, Vector2 center, float radius, float speed)
        {
            if (now - _walkStartedAt > MaxWalkSeconds)
            {
                Phase = WanderPhase.Facing;
                return;
            }

            // ★★ <b>進むのは「目的地の方向」ではなく「体の正面」。</b> 目的地へ直接寄せると、
            //   向き直っている間は正面と進行方向がずれて横へ滑って見える。正面へ進めば、
            //   回りながら歩いてもカーブを描くだけで滑らない。
            // ★ <b>一歩の速さは旋回速度×残りの距離で頭打ちにする。</b> 並進で生じる方位角の
            //   変化を旋回の速さ以下に抑えることで、向きが方位に追いつき続ける
            //   ——真横・背後の目的地では歩幅が縮んでその場に近い足踏みになり
            //   （歩行クリップは流し続ける）、向き直りが済むにつれて歩幅も伸びる。
            if (TryHorizontalYaw(Position, _target, out var targetYaw))
            {
                YawDegrees = Mathf.MoveTowardsAngle(YawDegrees, targetYaw, TurnDegreesPerSecond * dt);
                var bearing = Mathf.DeltaAngle(YawDegrees, targetYaw) * Mathf.Deg2Rad;
                var distance = Vector2.Distance(Position, _target);
                var pace = Mathf.Min(speed, TurnDegreesPerSecond * Mathf.Deg2Rad * distance) * Mathf.Max(0f, Mathf.Cos(bearing));
                Position = Advance(Position, Position + Forward(YawDegrees) * (pace * dt), center, radius);
            }

            if (Vector2.Distance(Position, _target) <= ArrivalMeters) Phase = WanderPhase.Facing;
        }

        private void UpdateFacing(float dt, Vector2 cameraPositionXz)
        {
            if (!TryHorizontalYaw(Position, cameraPositionXz, out var targetYaw))
            {
                EnterResting();
                return;
            }

            YawDegrees = Mathf.MoveTowardsAngle(YawDegrees, targetYaw, TurnDegreesPerSecond * dt);
            if (Mathf.Abs(Mathf.DeltaAngle(YawDegrees, targetYaw)) <= FacingToleranceDegrees) EnterResting();
        }

        private void EnterResting()
        {
            Phase = WanderPhase.Resting;
            _nextWanderArmed = false;
        }

        private static double RandomWaitSeconds(Func<double> random) =>
            MinRestSeconds + random() * (MaxRestSeconds - MinRestSeconds);

        /// <summary>ヨーから、キャラクターの正面（ローカル −Z）の水平方向。</summary>
        public static Vector2 Forward(float yawDegrees)
        {
            var radians = yawDegrees * Mathf.Deg2Rad;
            return new Vector2(-Mathf.Sin(radians), -Mathf.Cos(radians));
        }

        private static bool TryHorizontalYaw(Vector2 from, Vector2 to, out float yawDegrees) =>
            XrGrabRules.TryYawToFace(new Vector3(from.x, 0f, from.y), new Vector3(to.x, 0f, to.y), out yawDegrees);

        /// <summary>歩幅・1周期の歩数・周期・モデルの縮尺から移動速度（m/s）を求める。フットスライドの
        /// 唯一の出どころ —— この式と歩行クリップの実際の歩幅がずれると足が地面を擦る。</summary>
        public static float Speed(float strideMeters, int stepsPerCycle, float cycleSeconds, float modelScale)
        {
            if (!float.IsFinite(cycleSeconds) || cycleSeconds <= 0f) return 0f;
            return strideMeters * stepsPerCycle * modelScale / cycleSeconds;
        }

        /// <summary>円内一様（角度一様、半径は <c>radius * sqrt(u)</c>）に目的地を1点選ぶ。
        /// 結果は必ず円内（境界を含む）。</summary>
        public static Vector2 PickDestination(Vector2 center, float radius, Func<double> random)
        {
            var angle = (float)(random() * (Math.PI * 2.0));
            var fraction = Mathf.Clamp01((float)random());
            var r = radius * Mathf.Sqrt(fraction);
            return center + new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)) * r;
        }

        /// <summary>
        /// <paramref name="from"/> から <paramref name="to"/> への一歩を、円の外へは出さずに返す。
        /// 円の外へ出る一歩は<b>丸ごと捨てて</b> <paramref name="from"/> をそのまま返す。
        ///
        /// ★ <b>径方向へ吸い戻さない。</b> 縁へ引き戻す動きは正面に対して横へずれる
        ///   ——それ自体がフットスライドになる。一歩を通すか丸ごと捨てるかだけにすれば、
        ///   通った一歩は常に正面（<see cref="Forward"/>）の向きのまま。
        /// ★ <b>単純に半径へクランプしない。</b> 半径を縮めた直後は <paramref name="from"/> が既に
        ///   円の外にあり得るので、縁へクランプすると瞬間移動になる。上限は
        ///   <c>max(radius, 進む前の中心からの距離)</c> —— 外から中へ戻る一歩は通り、
        ///   中から外へ出る一歩だけ止まる。
        /// </summary>
        public static Vector2 Advance(Vector2 from, Vector2 to, Vector2 center, float radius)
        {
            var maxDistance = Mathf.Max(radius, Vector2.Distance(from, center));
            if (Vector2.Distance(to, center) > maxDistance) return from;
            return to;
        }
    }
}
