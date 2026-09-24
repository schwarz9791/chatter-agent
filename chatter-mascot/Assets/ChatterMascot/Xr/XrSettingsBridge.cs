using System.Collections.Generic;
using ChatterMascot.Settings;
using ChatterMascot.Vrm;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.UI;

namespace ChatterMascot.Xr
{
    /// <summary>
    /// XR の設定パネルで、<b>キーの意味を知る唯一の場所</b>（デスクトップの
    /// <c>SettingsPanelBridge</c> と同じ役回り）。<see cref="SettingsSchema.Build"/> で並びを組み、
    /// <see cref="XrSettingsPanel"/> へ渡す。パネルからのイベントは <c>key</c> の
    /// <c>switch</c> で振り分ける。
    ///
    /// ★ <b>呼び出し口（頭上の歯車 / 手のひらのボタン）もここが持つ。</b> 関節が取れている間は
    ///   <see cref="XrHandTracking"/>（唯一の関節読み取り口）が手のひらの向きを判定し、
    ///   取れていなければ歯車へ落ちる——どちらを出すかは <see cref="XrMenuRules"/> が決める。
    ///   aim レイがキャラ・歯車・手のひらボタンのどれに当たっているかは <see cref="UpdateHover"/>
    ///   が毎フレーム判定し、開くかどうかは <see cref="TryHandlePinch"/> が pinch の入りだけを
    ///   見る——入力そのものの読み取りは <see cref="XrGrab"/> に一本化したまま、ここはレイと
    ///   関節姿勢を受け取って判定するだけ。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class XrSettingsBridge : MonoBehaviour
    {
        /// <summary>呼び出し口のどちらを出しているか。切り替わったときだけログを出す。</summary>
        private enum InvokerMode { None, Gear, Palm }

        // ── 見た目の既定値（実測値ではない） ─────────────────────
        private const float InvokerCanvasPixels = 96f;
        private const float InvokerWorldSizeMeters = 0.05f;
        private const float GearAboveHeadMeters = 0.06f;
        private const float InvokerHitRadiusMeters = 0.05f;

        /// <summary>「すべての設定をリセット」の確認待ちの猶予（秒）。既定値。</summary>
        private const float ResetAllConfirmSeconds = 4f;

        private const string CloseLabel = "閉じる";
        private const string ResetAllConfirmNote = "もう一度押すとすべての設定をリセットします";
        private const string NoMotionToPlayNote = "選べるモーションがありません";

        private VrmStage _stage;
        private XROrigin _origin;
        private XrGrab _grab;
        private XrWalk _walk;

        private XrSettingsPanel _panel;
        private GameObject _gear;
        private SphereCollider _gearCollider;
        private GameObject _palmButton;
        private SphereCollider _palmButtonCollider;

        /// <summary>関節姿勢を読む唯一の場所（<see cref="XrHandTracking"/> の doc 参照）。</summary>
        private readonly XrHandTracking _hands = new XrHandTracking();

        private InvokerMode _invokerMode = InvokerMode.None;

        private readonly SettingsContext _context = new SettingsContext();
        private readonly Dictionary<string, string> _notices = new Dictionary<string, string>();

        /// <summary>aim レイがキャラか歯車に最後に当たった時刻（<see cref="XrMenuRules"/> が見る）。</summary>
        private double _lastHitAt = double.NegativeInfinity;

        /// <summary>この時刻までは「すべての設定をリセット」がもう一押しで確定する。</summary>
        private double _resetAllArmedUntil = double.NegativeInfinity;

        /// <summary>パネルの行にレイが当たったフレーム。</summary>
        private int _hoverHitFrame = -1;

        public void Begin(VrmStage stage, XROrigin origin, XrGrab grab, XrWalk walk)
        {
            _stage = stage;
            _origin = origin;
            _grab = grab;
            _walk = walk;

            var panelGo = new GameObject("XR Settings Panel");
            // ★ Canvas は RequireComponent で即座に付くので、組み上がるまで（EnsureBuilt/Open の
            //   前）は非表示にしておく——既定の ScreenSpaceOverlay のまま一瞬でも有効化させない
            panelGo.SetActive(false);
            _panel = panelGo.AddComponent<XrSettingsPanel>();
            _panel.SettingChanged += HandleSetting;
            _panel.Closed += OnPanelClosed;

            _gear = BuildIconButton("XR Settings Gear", out _gearCollider);
            _palmButton = BuildIconButton("XR Palm Menu Button", out _palmButtonCollider);

            var host = MascotSettingsHost.Instance;
            if (host != null) host.ChangedExternally += OnSettingsChangedExternally;
            // ★ Apply 経由の自分起点の変更は ChangedExternally が来ないので、起動時の値は
            //   ここで一度だけ直接伝える（→ HandleSetting の Walk の case も同様に直接伝える）
            _walk.SetEnabled(host != null ? host.Current.Walk : MascotSettings.Defaults.Walk);
        }

        private void OnDestroy()
        {
            var host = MascotSettingsHost.Instance;
            if (host != null) host.ChangedExternally -= OnSettingsChangedExternally;
        }

        private void Update()
        {
            var now = Time.unscaledTimeAsDouble;
            _hands.Tick(_origin, now);
            UpdateInvokers(now);
        }

        // ── XrGrab から毎フレーム渡される入力 ───────────────────────

        /// <summary>
        /// つまんでいなくても毎フレーム渡す。パネルが開いていれば行のホバーを、
        /// 閉じていればキャラ・歯車に当たっているかを判定する。
        /// </summary>
        public void UpdateHover(Ray ray)
        {
            if (_panel.IsOpen)
            {
                // ★ 手ごとに呼ばれるので、先の手が当てたハイライトを後の手の空振りで消さない
                if (_hoverHitFrame == Time.frameCount) return;
                if (_panel.TryHover(ray)) _hoverHitFrame = Time.frameCount;
                return;
            }

            if (HitsCharacterOrGear(ray)) _lastHitAt = Time.unscaledTimeAsDouble;
        }

        /// <summary>
        /// pinch に入った瞬間に呼ぶ（<c>XrGrab.TryGrab</c> の先頭）。パネル・手のひらボタン・歯車の
        /// どれかに当たっていればそちらを押して <b>true</b> を返す——呼び出し側はキャラ・歩行範囲の
        /// 掴みへ進まないこと。
        ///
        /// ★ <b>手のひらボタンを出している手自身のレイは特別扱いしない。</b> 手のひらを自分へ
        ///   向けたときの aim レイは通常ボタンを指さないので、区別しなくても実用上問題にならない。
        /// </summary>
        public bool TryHandlePinch(Ray ray)
        {
            if (_panel.IsOpen) return _panel.TryPress(ray);

            if (TryPressButton(_palmButton, _palmButtonCollider, ray)) { OpenPanel(); return true; }
            if (TryPressButton(_gear, _gearCollider, ray)) { OpenPanel(); return true; }
            return false;
        }

        private static bool TryPressButton(GameObject button, SphereCollider collider, Ray ray)
        {
            if (button == null || !button.activeSelf || collider == null) return false;

            // ★ 直前のフレームで動かしていると古い当たり判定を見る（autoSyncTransforms オフ）
            Physics.SyncTransforms();
            return collider.Raycast(ray, out _, float.PositiveInfinity);
        }

        private bool HitsCharacterOrGear(Ray ray)
        {
            if (_gear != null && _gear.activeSelf && _gearCollider != null &&
                _gearCollider.Raycast(ray, out _, float.PositiveInfinity))
            {
                return true;
            }

            var collider = CharacterCollider();
            if (collider == null) return false;

            // ★ 直前のフレームでキャラを動かしていると古い当たり判定を見る（autoSyncTransforms オフ）
            Physics.SyncTransforms();
            return collider.Raycast(ray, out _, float.PositiveInfinity);
        }

        // ── パネルの開閉 ───────────────────────────────────────

        private void OpenPanel()
        {
            _notices.Clear();
            _resetAllArmedUntil = double.NegativeInfinity;
            RebuildContext();
            _panel.Open(_origin.Camera.transform, CloseLabel, BuildItems());
        }

        private void OnPanelClosed()
        {
            _notices.Clear();
            _resetAllArmedUntil = double.NegativeInfinity;
        }

        private void OnSettingsChangedExternally(MascotSettings previous, MascotSettings next)
        {
            _walk.SetEnabled(next.Walk);
            Refresh();
        }

        private void Refresh()
        {
            if (!_panel.IsOpen) return;
            RebuildContext();
            _panel.Refresh(BuildItems());
        }

        private void RebuildContext()
        {
            var host = MascotSettingsHost.Instance;
            _context.Platform = SettingsPlatform.Xr;
            _context.Settings = host != null ? host.Current : MascotSettings.Defaults;

            var realCm = _stage.RealHeightCm;
            _context.XrHeightChoices = realCm.HasValue
                ? SettingsMapping.XrHeightChoices(SettingsMapping.XrHeightSteps(realCm.Value))
                : null;

            var character = CharacterComponent();
            _context.MotionClips = SettingsSchema.MotionPreviewChoices(character != null ? character.MotionClips : null);
        }

        private IReadOnlyList<SettingSpec> BuildItems()
        {
            var items = SettingsSchema.Build(_context);
            items = ApplyResetAllConfirmState(items);
            items = ApplyNotices(items);
            return items;
        }

        /// <summary>
        /// 「すべての設定をリセット」の確認待ちを note へ差し込む。<b>パネルにキーを書かずに
        /// 実現する</b>——ここ（キーの意味を知る側）が、出来上がった並びの該当行だけ差し替える。
        /// </summary>
        private IReadOnlyList<SettingSpec> ApplyResetAllConfirmState(IReadOnlyList<SettingSpec> items)
        {
            if (Time.unscaledTimeAsDouble >= _resetAllArmedUntil) return items;

            var result = new List<SettingSpec>(items.Count);
            foreach (var spec in items)
            {
                result.Add(spec.Key == SettingKeys.ResetAll ? SettingSpec.WithNote(spec, ResetAllConfirmNote) : spec);
            }
            return result;
        }

        private IReadOnlyList<SettingSpec> ApplyNotices(IReadOnlyList<SettingSpec> items)
        {
            if (_notices.Count == 0) return items;

            var result = new List<SettingSpec>(items.Count);
            foreach (var spec in items)
            {
                if (spec.Key != null && _notices.TryGetValue(spec.Key, out var notice) && !string.IsNullOrEmpty(notice))
                {
                    result.Add(SettingSpec.WithNote(spec, notice));
                    continue;
                }
                result.Add(spec);
            }
            return result;
        }

        private void Notice(string key, string message)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (string.IsNullOrEmpty(message)) _notices.Remove(key);
            else _notices[key] = message;
        }

        // ── 変更の振り分け ─────────────────────────────────────

        private void HandleSetting(string key, string value)
        {
            var host = MascotSettingsHost.Instance;
            if (host == null) return;

            switch (key)
            {
                case SettingKeys.XrHeight:
                    SetHeightCm(SettingsMapping.Parse(value, host.Current.XrHeight));
                    Refresh();
                    return;

                case SettingKeys.AssetSync:
                {
                    var on = SettingsPanelJson.ParseBool(value, host.Current.AssetSync != SettingsMapping.AssetSyncOff);
                    host.Apply(host.Current.WithAssetSync(on ? SettingsMapping.AssetSyncAuto : SettingsMapping.AssetSyncOff));
                    Refresh();
                    return;
                }

                case SettingKeys.Walk:
                {
                    var on = SettingsPanelJson.ParseBool(value, host.Current.Walk);
                    host.Apply(host.Current.WithWalk(on));
                    // ★ Apply は自分起点の変更なので ChangedExternally が来ない。ここで直接伝える
                    _walk.SetEnabled(on);
                    Refresh();
                    return;
                }

                case SettingKeys.CursorGaze:
                    host.Apply(host.Current.WithCursorGaze(SettingsPanelJson.ParseBool(value, host.Current.CursorGaze)));
                    Refresh();
                    return;

                case SettingKeys.Blink:
                    host.Apply(host.Current.WithBlink(SettingsPanelJson.ParseBool(value, host.Current.Blink)));
                    Refresh();
                    return;

                // ★ #70 派生。保存しない一時的な選択（→ SettingsContext.MotionPreview の doc）
                case SettingKeys.MotionPreview:
                    _context.MotionPreview = value;
                    Refresh();
                    return;

                case SettingKeys.MotionPreviewPlay:
                    PlayMotionPreview();
                    return;

                case SettingKeys.ResetPosition:
                    _grab.ResetPosition();
                    Refresh();
                    return;

                case SettingKeys.ResetAll:
                    HandleResetAll();
                    return;

                default:
                    Debug.LogWarning($"[Mascot] XR settings: 知らない設定のキーです: \"{key}\"");
                    return;
            }
        }

        /// <summary>
        /// 大きさをその場で変える。段に寄せて縮尺を合わせ、設定へ保存する。
        /// </summary>
        private void SetHeightCm(float cm)
        {
            var realCm = _stage.RealHeightCm;
            if (!realCm.HasValue || !(realCm.Value > 0f)) return;

            var targetCm = SettingsMapping.NearestXrHeight(cm, SettingsMapping.XrHeightSteps(realCm.Value));
            _stage.Rescale(targetCm / realCm.Value);

            var host = MascotSettingsHost.Instance;
            if (host != null) host.Apply(host.Current.WithXrHeight(targetCm));

            Debug.Log($"[Mascot] XR settings: 大きさを {targetCm:F0}cm に変えました");
        }

        /// <summary>
        /// 「モーションを確認」の「再生」。デスクトップの
        /// <c>SettingsPanelBridge.PlayMotionPreview</c> と同じ経路（<c>VrmCharacter.PreviewMotion</c>）。
        /// </summary>
        private void PlayMotionPreview()
        {
            var character = CharacterComponent();
            var id = SettingsSchema.EffectiveMotionPreview(_context.MotionClips, _context.MotionPreview);
            var clip = character != null ? FindMotionClip(character.MotionClips, id) : null;

            if (character == null || clip == null)
            {
                Notice(SettingKeys.MotionPreviewPlay, NoMotionToPlayNote);
                Refresh();
                return;
            }

            var result = character.PreviewMotion(clip);
            Notice(SettingKeys.MotionPreviewPlay, SettingsSchema.MotionPlayNotice(result, id));
            Refresh();
        }

        private static MotionClip FindMotionClip(IReadOnlyList<MotionClip> clips, string id)
        {
            if (clips == null || string.IsNullOrEmpty(id)) return null;
            foreach (var clip in clips)
            {
                if (SettingsSchema.MotionPreviewId(clip) == id) return clip;
            }
            return null;
        }

        /// <summary>
        /// 「すべての設定をリセット」。<b>確認を1段挟む。</b> 1回目は確認待ちにするだけで、
        /// <see cref="ResetAllConfirmSeconds"/> 以内の2回目で確定する
        /// （パネル側にキーを書かずに <see cref="ApplyResetAllConfirmState"/> が note を差し替える）。
        /// </summary>
        private void HandleResetAll()
        {
            var host = MascotSettingsHost.Instance;
            if (host == null) return;

            var now = Time.unscaledTimeAsDouble;
            if (now >= _resetAllArmedUntil)
            {
                _resetAllArmedUntil = now + ResetAllConfirmSeconds;
                Refresh();
                return;
            }

            _resetAllArmedUntil = double.NegativeInfinity;

            var next = host.Current.ResetKeepingConnection();
            host.Apply(next);
            // ★ Apply は自分起点の変更なので ChangedExternally が来ない。ここで直接伝える
            _walk.SetEnabled(next.Walk);

            var realCm = _stage.RealHeightCm;
            if (realCm.HasValue && realCm.Value > 0f)
            {
                var targetCm = SettingsMapping.NearestXrHeight(next.XrHeight, SettingsMapping.XrHeightSteps(realCm.Value));
                _stage.Rescale(targetCm / realCm.Value);
            }
            _grab.ResetPosition();

            Refresh();
        }

        // ── 呼び出し口（歯車 / 手のひらのボタン） ───────────────────

        /// <summary>
        /// 歯車・手のひらボタンの表示・非表示と位置を毎フレーム進める。<b>どちらを出すかの判定
        /// そのものは <see cref="XrMenuRules"/>（純粋関数）に任せる。</b>切り替わったときだけ
        /// 1行ログを出す。
        /// </summary>
        private void UpdateInvokers(double now)
        {
            if (_palmButton == null || _gear == null) return;

            var palmVisible = _stage.Model != null &&
                               XrMenuRules.ShowPalmButton(_panel.IsOpen, _hands.TrackingAvailable, _hands.PalmFacingSelf);
            _palmButton.SetActive(palmVisible);
            if (palmVisible) _palmButton.transform.SetPositionAndRotation(_hands.ButtonPosition, _hands.ButtonRotation);

            var gearVisible = _stage.Model != null &&
                               XrMenuRules.ShowGear(_panel.IsOpen, _hands.TrackingAvailable, now, _lastHitAt);
            _gear.SetActive(gearVisible);
            if (gearVisible) PositionGear();

            var mode = palmVisible ? InvokerMode.Palm : gearVisible ? InvokerMode.Gear : InvokerMode.None;
            if (mode == _invokerMode) return;
            _invokerMode = mode;
            Debug.Log($"[Mascot] XR: 呼び出し口 → {InvokerModeLabel(mode)}");
        }

        private static string InvokerModeLabel(InvokerMode mode)
        {
            switch (mode)
            {
                case InvokerMode.Palm: return "手のひら";
                case InvokerMode.Gear: return "歯車";
                default: return "なし";
            }
        }

        private void PositionGear()
        {
            var collider = CharacterCollider();
            var anchorPosition = _stage.ModelAnchor.position;
            var topY = collider != null ? collider.bounds.max.y : anchorPosition.y;
            var position = new Vector3(anchorPosition.x, topY + GearAboveHeadMeters, anchorPosition.z);
            _gear.transform.SetPositionAndRotation(position, _origin.Camera.transform.rotation);
        }

        /// <summary>
        /// 呼び出し口そのもの（歯車・手のひらボタンで共用）。uGUI の <c>Text</c> 1文字。
        /// <b>キャラの縮尺に連動しない一定の見た目の大きさ</b>にする——世界に直接置く
        /// （<c>ModelAnchor</c> の子にしない）ので、<c>ModelAnchor.localScale</c> を変えても
        /// 大きさは変わらない。
        /// </summary>
        private static GameObject BuildIconButton(string name, out SphereCollider collider)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.sizeDelta = new Vector2(InvokerCanvasPixels, InvokerCanvasPixels);

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var scale = InvokerWorldSizeMeters / InvokerCanvasPixels;
            go.transform.localScale = Vector3.one * scale;

            var iconGo = new GameObject("Icon", typeof(RectTransform));
            var iconRect = (RectTransform)iconGo.transform;
            iconRect.SetParent(rect, false);
            iconRect.anchorMin = Vector2.zero;
            iconRect.anchorMax = Vector2.one;
            iconRect.offsetMin = Vector2.zero;
            iconRect.offsetMax = Vector2.zero;

            var text = iconGo.AddComponent<Text>();
            // ★ TMP は使わない（日本語フォントアセットが要る）
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = "⚙";
            text.alignment = TextAnchor.MiddleCenter;
            text.fontSize = 72;
            text.color = Color.white;

            // ★ 当たり判定は見た目より大きく取る（XrWalkAreaView のハンドルと同じ理由）
            var sphereCollider = go.AddComponent<SphereCollider>();
            sphereCollider.radius = InvokerHitRadiusMeters / scale;
            collider = sphereCollider;

            go.SetActive(false);
            return go;
        }

        private Collider CharacterCollider()
        {
            return _stage.Model != null ? _stage.Model.GetComponentInChildren<Collider>() : null;
        }

        private VrmCharacter CharacterComponent()
        {
            return _stage.ModelAnchor != null ? _stage.ModelAnchor.GetComponent<VrmCharacter>() : null;
        }
    }
}
