using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Hands.OpenXR;

namespace ChatterMascot.Xr
{
    /// <summary>
    /// <c>XRHandSubsystem</c> から手の関節姿勢を読む唯一の場所。<see cref="XrSettingsBridge"/> が
    /// 毎フレーム <see cref="Tick"/> を呼び、手のひらを自分（頭）へ向けたかどうかを判定する。
    ///
    /// ★ <b>つまみ判定にはここを使わない。</b> <see cref="XrGrab"/> が読む aim レイ・pinchValue の
    ///   ままにする——ここが読むのは手のひらの向きだけ。
    ///
    /// ★ <b>subsystem は許可が下りるまで走らせない。</b> <c>HandTracking.
    ///   automaticallyInitializeSubsystem</c> を <c>SubsystemRegistration</c> の時点で false にし、
    ///   <c>HAND_TRACKING</c> の許可（<see cref="XrGrab"/> が起動時にまとめて要求する）が下りてから
    ///   <c>EnsureSubsystemInitialized()</c> を呼ぶ。未許可のまま走らせるとランタイムが毎フレーム
    ///   エラーを出す。
    /// </summary>
    internal sealed class XrHandTracking
    {
        /// <summary>
        /// 手のひらの法線（ローカル -Y）。<c>Unity.XR.Hands</c> の
        /// <c>Gestures.XRHandOrientationUtility.GetHandAxisDirection</c> が Palm Direction を
        /// <c>rootRotation * (0,-1,0)</c> として求めており、Thumb Direction と違って左右で
        /// 符号を変えていない——関節ごとの姿勢もプロバイダが同じ規約へ変換したものなので、
        /// Palm / Wrist の関節姿勢にもそのまま使える。
        /// </summary>
        private static readonly Vector3 PalmNormalLocal = Vector3.down;

        /// <summary>手のひらボタンを、手のひらから頭側へ離す距離（メートル）。既定値。</summary>
        private const float ButtonOffsetMeters = 0.06f;

        private bool _subsystemRequested;
        /// <summary>左右それぞれの「手のひらを自分に向けているか」（ヒステリシスの状態）。</summary>
        private readonly bool[] _facing = new bool[2];
        private readonly double[] _lastJointTrackedAt = { double.NegativeInfinity, double.NegativeInfinity };

        /// <summary>手のひらの向きの判定に使える状態か（<see cref="XrMenuRules.HandTrackingAvailable"/>）。</summary>
        public bool TrackingAvailable { get; private set; }

        /// <summary>手のひらを自分（頭）へ向けているか。</summary>
        public bool PalmFacingSelf { get; private set; }

        /// <summary>手のひらボタンのワールド位置。<see cref="PalmFacingSelf"/> のときだけ意味を持つ。</summary>
        public Vector3 ButtonPosition { get; private set; }

        /// <summary>手のひらボタンのワールド回転。</summary>
        public Quaternion ButtonRotation { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void SuppressAutomaticInitialization()
        {
            // ★ 許可が下りるまで subsystem を走らせない（docs の既定パターン）。ここは対象外の
            //   プラットフォームではアセンブリごと存在しないので、常にこの分岐だけでよい。
            HandTracking.automaticallyInitializeSubsystem = false;
        }

        /// <summary>毎フレーム呼ぶ。関節が読めた・読めなかったに関わらず、判定結果を更新する。</summary>
        public void Tick(XROrigin origin, double now)
        {
            EnsureSubsystem();

            var subsystem = HandTracking.subsystem;
            var running = subsystem != null && subsystem.running;

            if (running && origin != null && origin.CameraFloorOffsetObject != null && origin.Camera != null)
            {
                var offset = origin.CameraFloorOffsetObject.transform;
                var head = origin.Camera.transform;

                // ★ 左右を別々に判定する。片手の手のひらを向け、もう片方の手でつまんで押すのが
                //   本来の使い方なので、どちらかを優先すると向けた側を見落とす
                UpdateHand(0, subsystem.leftHand, offset, head, now);
                UpdateHand(1, subsystem.rightHand, offset, head, now);
            }

            // ★ 見失いは手ごとに見る。もう片方の手が見えているだけで、消えた手の
            //   「向けている」が残り続けないようにする
            var available = false;
            var facing = false;
            for (var i = 0; i < _facing.Length; i++)
            {
                if (!XrMenuRules.HandTrackingAvailable(now, _lastJointTrackedAt[i])) _facing[i] = false;
                else available = true;
                facing |= _facing[i];
            }
            TrackingAvailable = available;
            PalmFacingSelf = facing;
        }

        private void UpdateHand(int index, XRHand hand, Transform offset, Transform head, double now)
        {
            // ★ 関節が取れなかった手は、猶予のあいだ直前の向きをそのまま保つ
            if (!TryGetPalmPose(hand, offset, out var pose)) return;

            _lastJointTrackedAt[index] = now;

            var normal = pose.rotation * PalmNormalLocal;
            var toHead = (head.position - pose.position).normalized;
            _facing[index] = XrMenuRules.IsPalmFacingSelf(_facing[index], Vector3.Dot(normal, toHead));

            if (!_facing[index]) return;
            ButtonPosition = pose.position + toHead * ButtonOffsetMeters;
            // ★ 歯車と同じく、見た目はカメラの向きへ揃えるだけ（常にユーザーへ正対させる）
            ButtonRotation = head.rotation;
        }

        private void EnsureSubsystem()
        {
            if (_subsystemRequested) return;
            // ★ XrGrab.Start と同じ理由でプラットフォームを絞る（Editor では許可の概念が無い）
            if (Application.platform != RuntimePlatform.Android) return;
            if (!Permission.HasUserAuthorizedPermission(XrGrab.HandTrackingPermission)) return;

            _subsystemRequested = true;
            HandTracking.EnsureSubsystemInitialized();
            Debug.Log("[Mascot] XR: 手のひらメニュー用の HandTracking subsystem を起動しました");
        }

        /// <summary>Palm の関節姿勢（取れなければ Wrist）をワールド空間で返す。手が追跡されていない・
        /// どちらの関節も取れなければ false。</summary>
        private static bool TryGetPalmPose(XRHand hand, Transform offset, out Pose worldPose)
        {
            worldPose = default;
            if (!hand.isTracked) return false;

            if (!hand.GetJoint(XRHandJointID.Palm).TryGetPose(out var localPose) &&
                !hand.GetJoint(XRHandJointID.Wrist).TryGetPose(out localPose))
            {
                return false;
            }

            // ★ offset（CameraFloorOffsetObject）基準の変換。XrGrab.ReadAimRay の
            //   TransformPoint/TransformDirection と同じ空間の扱い
            worldPose = offset.TransformPose(localPose);
            return true;
        }
    }
}
