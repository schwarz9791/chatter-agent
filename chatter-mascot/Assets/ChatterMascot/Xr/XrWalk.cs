using ChatterMascot.Vrm;
using Unity.XR.CoreUtils;
using UnityEngine;

namespace ChatterMascot.Xr
{
    /// <summary><see cref="XrWalk.TryWalkTo"/> の結果。</summary>
    public enum WalkToResult
    {
        /// <summary>歩き出した。</summary>
        Started,

        /// <summary>指した点が歩行範囲の外だった。</summary>
        OutOfRange,

        /// <summary>範囲内かどうかによらず、歩き出さなかった（未配置・床と交わらない・発話中・他のモーション中など）。</summary>
        Rejected,
    }

    /// <summary>
    /// 歩行範囲（円）の状態を持つ唯一の場所。<see cref="Wander"/> を毎フレーム回し、
    /// 結果を <c>ModelAnchor</c> の xz とヨーへ反映する。円の描画は <see cref="XrWalkAreaView"/> に任せる。
    ///
    /// ★ <b>シーンに置かない。</b> <see cref="XrStage"/> が配置の直後に <c>AddComponent</c> で生やす
    ///   （<see cref="XrGrab"/> と同じ理由）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class XrWalk : MonoBehaviour
    {
        public const float MinRadius = 0.10f;
        public const float DefaultRadius = 0.20f;
        public const float MaxRadius = 0.60f;

        private const float CircleVisibleSeconds = 15f;
        private const float OutOfRangeCircleSeconds = 3f;
        private const float HandleAzimuthOffsetDegrees = 45f;

        /// <summary>
        /// 歩行クリップに合わせた既定の歩幅・1周期の歩数・周期。<b>足が滑るならここを合わせる</b>
        /// —— 接地判定でも IK でもなく、速度と歩幅のズレだけがフットスライドの原因
        /// （<see cref="Wander.Speed"/>）。同梱モデルと歩行クリップに合わせた値で、
        /// 他のモデル・クリップに一般化する導出は別課題。
        /// </summary>
        private const float StrideMeters = 0.53f;
        private const int StepsPerCycle = 2;
        private const float CycleSeconds = 1.30f;

        private XROrigin _origin;
        private VrmStage _stage;
        private VrmCharacter _character;
        private XrWalkAreaView _view;

        private Vector2 _center;
        private float _floorY;
        private float _radius = DefaultRadius;
        private float _handleAngleDegrees;
        private bool _placed;

        private double _hideCircleAt;
        private bool _handleHeld;
        private bool _modelHeld;

        private Wander _wander;

        public void Begin(XROrigin origin, VrmStage stage)
        {
            _origin = origin;
            _stage = stage;
            _character = stage.ModelAnchor.GetComponent<VrmCharacter>();
            if (_character == null)
            {
                Debug.LogWarning("[Mascot] XR walk: ModelAnchor に VrmCharacter が無いので歩行を組めません");
            }

            var viewGo = new GameObject("Walk Area");
            _view = viewGo.AddComponent<XrWalkAreaView>();
        }

        /// <summary>
        /// キャラクターを平面へ着地させた（<c>XrGrab.Release</c>）直後に呼ぶ。中心・接地高さ・
        /// ハンドルの位置を決め、円を出す。<b>これが呼ばれるまで歩行は無効。</b>
        ///
        /// ★ <b>半径は変えない。</b> つまんで置き直すたびに既定へ戻すと、広げた歩行範囲が
        ///   毎回失われる —— 半径を変えるのは <see cref="DragHandle"/> と、既定へ戻す
        ///   <see cref="ResetArea"/> だけにすること。
        /// </summary>
        public void PlaceAt(Vector3 feetWorld, float groundY, float characterYawDegrees)
        {
            _center = new Vector2(feetWorld.x, feetWorld.z);
            _floorY = groundY;
            _handleAngleDegrees = characterYawDegrees + HandleAzimuthOffsetDegrees;

            if (_wander == null) _wander = new Wander(_center, characterYawDegrees);
            else _wander.Reset(_center, characterYawDegrees);

            _hideCircleAt = Time.realtimeSinceStartupAsDouble + CircleVisibleSeconds;
            _placed = true;

            // ★ 1回1行。実機で「円が出た／出ない」を logcat から判定する唯一の手がかり
            Debug.Log($"[Mascot] XR walk: 歩行範囲 center=({_center.x:F2}, {_center.y:F2}) floor={_floorY:F2} " +
                      $"radius={_radius:F2} 歩行={(_character != null && _character.CanWalk ? "可" : "不可")}");
        }

        /// <summary>
        /// 平面が見つからず着地できなかった（<c>XrGrab.Release</c>）ときに呼ぶ。歩行を無効にし、
        /// 円をフェードアウトへ回す。<b>これを呼ばずに次の <see cref="Update"/> を迎えると、
        /// 古い中心へアンカーが引き戻される。</b>
        /// </summary>
        public void Unplace()
        {
            _placed = false;
            if (_character != null && _character.IsWalking) _character.StopWalking();
        }

        /// <summary>
        /// 「位置をリセット」の一部。半径を既定へ戻し、未配置にする。次に <see cref="PlaceAt"/> が
        /// 呼ばれるまで歩行は無効のまま。
        /// </summary>
        public void ResetArea()
        {
            _radius = DefaultRadius;
            Unplace();
        }

        /// <summary>
        /// aim レイがハンドルに当たっていれば掴む。
        /// ★ <b>円が消えている間は掴ませない。</b> 見えていないものを掴めると、キャラクターを
        ///   掴もうとしたつまみがハンドルに吸われる。
        /// </summary>
        public bool TryGrabHandle(Ray ray)
        {
            if (!_placed || !CircleVisible || _view == null || _view.HandleCollider == null) return false;

            // ★ ハンドルは毎フレーム動かしているので、Raycast の前に当たり判定を取り直すこと
            //   （autoSyncTransforms はオフ。XrGrab.SyncedModelCollider と同じ理由）
            Physics.SyncTransforms();
            if (!_view.HandleCollider.Raycast(ray, out _, float.PositiveInfinity)) return false;

            _handleHeld = true;
            return true;
        }

        /// <summary>
        /// aim レイと床平面（<c>y = floorY</c>）の交点までの水平距離を、<see cref="MinRadius"/>〜
        /// <see cref="MaxRadius"/> にクランプして半径にする。
        ///
        /// ★ レイが床と交わらない（上を向いた／平行、または交点がレイの後ろ）ときは半径を変えない。
        /// </summary>
        public void DragHandle(Ray ray)
        {
            if (!_handleHeld) return;
            if (!TryFloorPoint(ray, out var point)) return;

            _radius = Mathf.Clamp(Vector2.Distance(point, _center), MinRadius, MaxRadius);
        }

        /// <summary>
        /// aim レイの先が歩行範囲の中なら、そこへ歩かせる。床と交わらないとき・範囲の外を指した
        /// ときは歩き出さない。円を出すかどうかは呼び出し側が結果を見て決める
        /// （<see cref="ShowAreaBriefly"/>）。つまみがハンドルにもキャラクターにも当たらなかった
        /// ときに呼ぶ。
        ///
        /// ★ <b>歩き出せる状態か、<c>Wander.GoTo</c> の前に確かめる。</b> 発話中や他のモーション
        ///   （idle の小ネタ・感情表現）が再生中なら歩き出さない —— <c>Wander</c> だけを
        ///   Walking へ進めても、モーションが始められなければ <c>Update</c> が即座に
        ///   <c>Wander.Stop()</c> で畳むだけになる。
        /// </summary>
        public WalkToResult TryWalkTo(Ray ray)
        {
            if (!_placed || _wander == null || _character == null) return WalkToResult.Rejected;
            if (!TryFloorPoint(ray, out var point)) return WalkToResult.Rejected;
            if (Vector2.Distance(point, _center) > _radius) return WalkToResult.OutOfRange;

            if (_character.Speaking)
            {
                Debug.Log("[Mascot] XR walk: 歩き出せません（発話中）");
                return WalkToResult.Rejected;
            }

            if (!_character.IsWalking && !_character.TryStartWalking())
            {
                Debug.Log("[Mascot] XR walk: 歩き出せません（他のモーション再生中）");
                return WalkToResult.Rejected;
            }

            var now = Time.realtimeSinceStartupAsDouble;
            _wander.GoTo(now, point);
            _hideCircleAt = now + CircleVisibleSeconds;
            Debug.Log($"[Mascot] XR walk: 指された先へ歩きます ({point.x:F2}, {point.y:F2})");
            return WalkToResult.Started;
        }

        /// <summary>
        /// 検知した水平面を指したのに歩行範囲の外だったときに、範囲を短く出す。反応が無いと
        /// 平面を見失ったように見えるため。既に出ている円の残り時間は縮めない。未配置なら何もしない。
        /// </summary>
        public void ShowAreaBriefly()
        {
            if (!_placed) return;

            _hideCircleAt = System.Math.Max(_hideCircleAt, Time.realtimeSinceStartupAsDouble + OutOfRangeCircleSeconds);
            Debug.Log("[Mascot] XR walk: 範囲外を指したので歩行範囲を出します");
        }

        /// <summary>
        /// aim レイと床（<c>y = floorY</c>）の交点。
        /// ★ レイが床と交わらない（上を向いた／平行、または交点がレイの後ろ）ときは false。
        /// </summary>
        private bool TryFloorPoint(Ray ray, out Vector2 point)
        {
            point = default;

            var floor = new Plane(Vector3.up, new Vector3(0f, _floorY, 0f));
            if (!floor.Raycast(ray, out var enter) || enter < 0f) return false;

            var hit = ray.GetPoint(enter);
            point = new Vector2(hit.x, hit.z);
            return true;
        }

        public void ReleaseHandle()
        {
            _handleHeld = false;
            _hideCircleAt = Time.realtimeSinceStartupAsDouble + CircleVisibleSeconds;
        }

        /// <summary><c>XrGrab</c> がモデル本体を掴んでいる／離した。掴んでいる間は歩かない。</summary>
        public void SetModelGrabbed(bool grabbed)
        {
            _modelHeld = grabbed;
        }

        private void Update()
        {
            if (!_placed)
            {
                // ★ 平面が無くて置けなかった／Unplace 済み。円は最後の位置のまま
                //   フェードアウトへ回す —— ここで Sync を止めると消える途中で固まって見える
                SyncCircle(false);
                return;
            }

            if (_wander == null || _character == null) return;

            var now = Time.realtimeSinceStartupAsDouble;

            var held = _handleHeld || _modelHeld;
            var speed = Wander.Speed(StrideMeters, StepsPerCycle, CycleSeconds, _stage.ModelAnchor.localScale.x);
            var cameraXz = CameraPositionXz();

            _wander.Update(
                now, _character.Speaking, _character.CanWalk && !held,
                _center, _radius, speed, cameraXz, RandomDouble);

            // ★★ <b>状態とモーションは毎フレーム突き合わせる。遷移した瞬間だけを見ない。</b>
            //   遷移だけを見ると、外から目的地を指された回（Wander.GoTo）を取りこぼし、
            //   畳み損ねたループも残る（ループは自分からは終わらない）。
            //   始められない（感情モーションが再生中など）なら歩くのをやめる
            if (_wander.Phase == WanderPhase.Walking)
            {
                if (!_character.IsWalking && !_character.TryStartWalking()) _wander.Stop();
            }
            else if (_character.IsWalking)
            {
                _character.StopWalking();
            }

            SyncCircle(CircleVisible);

            // ★ XrGrab がモデルの位置を握っている間は上書きしない
            if (_modelHeld) return;

            var anchor = _stage.ModelAnchor;
            anchor.position = new Vector3(_wander.Position.x, _floorY, _wander.Position.y);
            anchor.rotation = Quaternion.Euler(0f, _wander.YawDegrees, 0f);
        }

        private Vector2 CameraPositionXz()
        {
            var position = _origin.Camera.transform.position;
            return new Vector2(position.x, position.z);
        }

        /// <summary>円が出ている間か。ハンドルをつまんでいる間は消さない。</summary>
        private bool CircleVisible => _handleHeld || Time.realtimeSinceStartupAsDouble < _hideCircleAt;

        private void SyncCircle(bool visible)
        {
            if (_view == null) return;

            _view.Sync(new Vector3(_center.x, 0f, _center.y), _floorY, _radius, _handleAngleDegrees, visible);
        }

        private static double RandomDouble() => UnityEngine.Random.value;
    }
}
