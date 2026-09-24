using System;
using System.Collections;
using System.IO;
using ChatterMascot.Settings;
using ChatterMascot.Vrm;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;
using UnityEngine.XR.Management;

namespace ChatterMascot.Xr
{
    /// <summary>
    /// Android XR で、キャラクターを空間に固定し頭部トラッキングへ対応させる。
    ///
    /// ★ <b><c>MonoBehaviour</c> にしない。シーンに置かない。</b> <c>Desktop/CursorGazeSource</c> と
    ///   同じ理由 —— このアセンブリは Editor と Android でしかコンパイルされない
    ///   （<c>includePlatforms</c>）。<c>RuntimeInitializeOnLoadMethod</c> なら、対象外の
    ///   プラットフォームでは<b>アセンブリごと存在しない</b>ので属性の走査対象にすらならず、
    ///   <c>#if</c> もプラットフォーム分岐も要らない。
    ///
    /// ★ <b>起動時の配置が動かすのはキャラクターではなく XR Origin。</b> <c>VrmStage.FaceCamera</c> が
    ///   読み込み時にモデルをワールド−Zへ向けるので、そのタイミングで <c>ModelAnchor</c> を
    ///   回すと打ち消される（→ <see cref="XrPlacement"/> の doc）。
    ///
    /// ★ <b>起動時の空間固定は1回きり。</b> 頭が追跡状態になるのを待って
    ///   <see cref="XrPlacement"/> で Origin の位置とヨーを決めたら、以後 Origin は触らない。
    ///   以後のキャラの置き直しは <see cref="XrGrab"/> が <c>ModelAnchor</c> を動かして行う
    ///   —— 読み込み後の操作なので FaceCamera には打ち消されない。
    /// </summary>
    public static class XrStage
    {
        /// <summary>原点が切り替わってから、頭が追跡状態になるのを待つ上限（秒）。超えたら、そのときのローカル姿勢で置く。</summary>
        private const float TrackingWaitTimeoutSeconds = 5f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bind()
        {
            if (XRGeneralSettings.Instance?.Manager?.activeLoader == null)
            {
                Debug.Log("[Mascot] XR: 起動していないので平面表示のまま");
                return;
            }

            var stage = UnityEngine.Object.FindFirstObjectByType<VrmStage>(FindObjectsInactive.Include);
            if (stage == null || stage.ModelAnchor == null)
            {
                Debug.LogWarning("[Mascot] XR: VrmStage / ModelAnchor が見つからないので空間固定を組めません");
                return;
            }

            var camera = Camera.main;
            if (camera == null)
            {
                Debug.LogWarning("[Mascot] XR: Camera.main が無いので空間固定を組めません");
                return;
            }

            var settings = ReadSettings();

            // デスクトップ向けのオートフレーミングはカメラを動かすので、頭部トラッキングと競合する
            stage.AutoFrame = false;

            // ★ VRM の読み込み（VrmStage.Start から）より前に拡縮しておくこと。読み込み時に
            //   VrmStage が spring bone へ縮尺を焼き込む（→ VrmStage.BakeSpringBoneScale）。
            //   実寸はまだ分からないので、legacy scale があればそれ、無ければ目標 cm からの
            //   見積もりで置く。モデルが読めたら OnModelLoaded が実寸から縮尺を出し直す
            var guessScale = settings.XrLegacyScale != 0f
                ? settings.XrLegacyScale
                : SettingsMapping.XrEstimatedScale(settings.XrHeight);
            stage.Rescale(guessScale);
            stage.AddLoadedHandler(_ => OnModelLoaded(stage, settings));

            var origin = BuildOrigin(camera);

            // 頭が追跡状態になるまで待ってから配置する。static からの非同期待ちなので、
            // ここだけ動的に生やす内部 MonoBehaviour でコルーチンを回す
            var runner = origin.gameObject.AddComponent<TrackingWaiter>();
            runner.Begin(origin, stage, settings);
        }

        /// <summary>
        /// モデルが読み込めた（実寸が測れた）ときに、目標の cm へ縮尺を合わせ直す。
        ///
        /// ★ <c>xr.scale</c> しか無い（<see cref="MascotSettings.XrLegacyScale"/> が非0）ときは、
        ///   ここで初めて実寸が分かるので cm へ換算して確定させ、保存して移行を終える。
        /// </summary>
        private static void OnModelLoaded(VrmStage stage, MascotSettings settings)
        {
            var realCm = stage.RealHeightCm;
            if (!realCm.HasValue || !(realCm.Value > 0f))
            {
                Debug.LogWarning("[Mascot] XR: モデルの実寸を測れなかったので大きさの調整を見送ります");
                return;
            }

            var steps = SettingsMapping.XrHeightSteps(realCm.Value);
            float targetCm;
            if (settings.XrLegacyScale != 0f)
            {
                targetCm = SettingsMapping.NearestXrHeight(
                    SettingsMapping.XrLegacyScaleToCm(settings.XrLegacyScale, realCm.Value), steps);
                // ★ 起動時に読んだ settings ではなく Host の現在値に重ねる。読み込みまでの間に
                //   変わった設定を古い値で上書きしないため
                var host = MascotSettingsHost.Instance;
                if (host != null) host.Apply(host.Current.WithXrHeight(targetCm).WithXrLegacyScale(0f));
            }
            else
            {
                targetCm = SettingsMapping.NearestXrHeight(settings.XrHeight, steps);
            }

            stage.Rescale(targetCm / realCm.Value);
            Debug.Log($"[Mascot] XR: 実寸 {realCm.Value:F1}cm → 目標 {targetCm:F0}cm へ縮尺を合わせました");
        }

        /// <summary>
        /// ★ <c>MascotSettingsHost.Instance</c> には頼らない。あちらも <c>AfterSceneLoad</c> で
        ///   生成される別クラスで、<b>同じタイミング同士の前後関係は保証されない</b>
        ///   （→ <c>MascotRunner.ResolveServerUrl</c> の doc と同じ理由）。<c>MascotRunner</c> の
        ///   <c>ReadConnectionSettings</c> と同じパターンで自分で1回だけファイルを読む。
        /// </summary>
        private static MascotSettings ReadSettings()
        {
            var path = SettingsLocation.Resolve(AssetEnvFactory.Current());
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return MascotSettings.Defaults;

            string raw;
            try
            {
                raw = File.ReadAllText(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Mascot] XR: settings.json を読めませんでした: " + e.Message);
                return MascotSettings.Defaults;
            }

            MascotSettings parsed;
            string error;
            // ★ キー単位の警告は渡さない。同じファイルを MascotSettingsHost も読んで警告するので、
            //   ここでも出すと起動のたびに同じ行が2回ずつ並ぶ（→ MascotRunner.ReadConnectionSettings と同じ判断）
            if (!SettingsJson.TryParse(raw, out parsed, out error, null)) return MascotSettings.Defaults;
            return parsed;
        }

        /// <summary>
        /// 非アクティブの GameObject に XROrigin を組み立てる。<b>この順序を守ること</b>
        ///   —— <c>Camera.main</c> を非アクティブ階層の下へ一時的に付け替えてから
        ///   <c>TrackedPoseDriver</c> を足し、最後に Origin を有効化する。
        /// </summary>
        private static XROrigin BuildOrigin(Camera camera)
        {
            // ★ HideFlags を付けないこと。HideAndDontSave のオブジェクトは FindFirstObjectByType から
            //   見えず、XROrigin を探す側（AR Foundation の各 Manager など）が見つけられなくなる
            var originGo = new GameObject("XR Origin");
            originGo.SetActive(false);

            var cameraOffsetGo = new GameObject("Camera Offset");
            cameraOffsetGo.transform.SetParent(originGo.transform, false);

            var origin = originGo.AddComponent<XROrigin>();
            origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Device;
            origin.CameraYOffset = 0f;

            // ★ ローカル姿勢は明示的にゼロにすること。SetParent(…, false) はローカルの値を
            //   引き継ぐだけで、シーンに置いたデスクトップ用のカメラ位置がそのまま残る
            camera.transform.SetParent(cameraOffsetGo.transform, false);
            camera.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            // シーンの値はデスクトップの距離向けで、手元のキャラを覗き込むと切れる
            camera.nearClipPlane = 0.01f;

            var driver = camera.gameObject.AddComponent<TrackedPoseDriver>();
            driver.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
            driver.positionInput = new InputActionProperty(new InputAction(binding: "<XRHMD>/centerEyePosition"));
            driver.rotationInput = new InputActionProperty(new InputAction(binding: "<XRHMD>/centerEyeRotation"));

            origin.Camera = camera;
            origin.CameraFloorOffsetObject = cameraOffsetGo;

            // ここで初めて Awake / OnEnable が走る
            originGo.SetActive(true);
            return origin;
        }

        /// <summary>
        /// 頭が追跡状態になるのを待ってから <see cref="XrPlacement"/> で1回だけ配置する。
        /// ★ <c>XrStage</c> が動的に生成するだけで、シーンには置かない。
        /// </summary>
        private sealed class TrackingWaiter : MonoBehaviour
        {
            private XROrigin _origin;
            private VrmStage _stage;
            private MascotSettings _settings;

            public void Begin(XROrigin origin, VrmStage stage, MascotSettings settings)
            {
                _origin = origin;
                _stage = stage;
                _settings = settings;
                StartCoroutine(WaitThenPlace());
            }

            private IEnumerator WaitThenPlace()
            {
                var startTime = Time.realtimeSinceStartup;
                var switchWarned = false;
                float? deadline = null;
                var framesSinceOriginApplied = 0;

                while (true)
                {
                    // ★ XROrigin が要求したトラッキング原点に切り替わるまで読まないこと。切り替わる前は
                    //   ランタイム既定の原点（床基準）の値が返り、切り替えで原点が頭へ移ると、
                    //   その値で置いたキャラが頭の上へ外れる。切り替えたフレームの値も揃っていない
                    //   ことがあるので1フレーム置く（要求しているモードは BuildOrigin の Device）
                    // ★ 切り替えは期限なしで待つ（切り替え前の原点で置くと同じ外れ方をする）
                    if (_origin.CurrentTrackingOriginMode != TrackingOriginModeFlags.Device)
                    {
                        framesSinceOriginApplied = 0;
                        if (!switchWarned && Time.realtimeSinceStartup - startTime > TrackingWaitTimeoutSeconds)
                        {
                            switchWarned = true;
                            Debug.LogWarning($"[Mascot] XR: トラッキング原点が {TrackingWaitTimeoutSeconds:F0} 秒たっても " +
                                              "Device に切り替わりません。切り替わるまで配置を待ちます");
                        }
                        yield return null;
                        continue;
                    }
                    if (framesSinceOriginApplied++ < 1)
                    {
                        yield return null;
                        continue;
                    }

                    deadline ??= Time.realtimeSinceStartup + TrackingWaitTimeoutSeconds;

                    // ★ 頭の姿勢は追跡状態を確かめたのと同じデバイスから読むこと。カメラの transform を
                    //   読むと、TrackedPoseDriver がまだ書き込んでいないフレームの値（原点）を拾う
                    // ★ CommonUsages は UnityEngine.InputSystem にも同名の型があるので完全修飾する
                    var device = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
                    if (device.isValid &&
                        device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.trackingState, out var state) &&
                        (state & (InputTrackingState.Position | InputTrackingState.Rotation)) ==
                        (InputTrackingState.Position | InputTrackingState.Rotation) &&
                        device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.centerEyePosition, out var position) &&
                        device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.centerEyeRotation, out var rotation))
                    {
                        Place(position, rotation);
                        Destroy(this);
                        yield break;
                    }

                    if (Time.realtimeSinceStartup >= deadline.Value) break;
                    yield return null;
                }

                Debug.LogWarning($"[Mascot] XR: トラッキング原点の切り替え後、頭の追跡が {TrackingWaitTimeoutSeconds:F0} 秒以内に揃いませんでした。" +
                                  "そのときのカメラのローカル姿勢で空間固定します");
                var head = _origin.Camera.transform;
                Place(head.localPosition, head.localRotation);
                Destroy(this);
            }

            private void Place(Vector3 headLocalPosition, Quaternion headLocalRotation)
            {
                XrPlacement.HeadYawPitch(headLocalRotation, out var headLocalYaw, out var headLocalPitch);
                XrPlacement.TiltByHeadPitch(
                    _settings.XrDistance, _settings.XrFeetBelowEye, headLocalPitch,
                    out var distance, out var feetBelowEye);
                XrPlacement.Solve(
                    headLocalPosition, headLocalYaw, _stage.ModelAnchor.position,
                    distance, _settings.XrAzimuth, feetBelowEye,
                    out var originPosition, out var originYawDegrees);

                _origin.transform.SetPositionAndRotation(originPosition, Quaternion.Euler(0f, originYawDegrees, 0f));

                Debug.Log("[Mascot] XR: 空間固定 " +
                          $"headLocalPosition={headLocalPosition} headLocalYaw={headLocalYaw:F1} headLocalPitch={headLocalPitch:F1} " +
                          $"distance={_settings.XrDistance:F2}→{distance:F2} azimuth={_settings.XrAzimuth:F1} feetBelowEye={_settings.XrFeetBelowEye:F2}→{feetBelowEye:F2} → " +
                          $"originPosition={originPosition} originYaw={originYawDegrees:F1}");

                // ★ 配置の後にすること。配置前につまむと、XrPlacement.Solve が動く前の
                //   （まだ正しくない）アンカーを掴ませてしまう
                var walk = _origin.gameObject.AddComponent<XrWalk>();
                walk.Begin(_origin, _stage);
                var grab = _origin.gameObject.AddComponent<XrGrab>();
                var settings = _origin.gameObject.AddComponent<XrSettingsBridge>();
                settings.Begin(_stage, _origin, grab);
                grab.Begin(_origin, _stage, walk, settings);

                // ★ 目で追う（XR 版）。CursorProvider は Desktop 側と同じ注入の形——
                //   手を追跡できていなければ null を返すだけで、ON/OFF の分岐はここに書かない
                //   （VrmCharacter.UpdateGaze が character.cursorGaze を見て判断する）
                var character = _stage.ModelAnchor.GetComponent<VrmCharacter>();
                if (character != null)
                {
                    var origin = _origin;
                    var stage = _stage;
                    character.CursorProvider = () => XrCursorGazeSource.TryRead(grab, origin, stage, character);
                }
                else
                {
                    Debug.LogWarning("[Mascot] XR: ModelAnchor に VrmCharacter が無いので目で追うを組めません");
                }
            }
        }
    }
}
