using System;
using System.Collections.Generic;
using ChatterMascot.Settings;
using UnityEngine;
using UnityEngine.UI;

namespace ChatterMascot.Xr
{
    /// <summary>
    /// 設定パネルのレンダラ。world-space <c>Canvas</c> + legacy uGUI（<c>Image</c> / <c>Text</c>）で
    /// <see cref="SettingSpec"/> の並びを描く。<c>Kind</c> だけを見て行を組む——
    /// デスクトップの <c>CMSettingsPanel.m</c> と同じ規律。
    ///
    /// ★★ <b>このファイルに設定のキー定数や具体的なキー文字列・項目ラベルの日本語を
    ///   1つも書かないこと。</b> キーは <see cref="SettingSpec.Key"/> を不透明に持ち回すだけで、
    ///   意味は <c>XrSettingsBridge</c> だけが知る。パネル自体の文言（「閉じる」）は
    ///   <see cref="Open"/> の引数として外から渡す。
    ///
    /// ★ <b>入力は <c>EventSystem</c> を使わない。</b> <c>XRI</c> も <c>TrackedDeviceRaycaster</c> も
    ///   要らない —— レイとパネル平面（このオブジェクトの <c>RectTransform</c>）の交点を求め、
    ///   行の矩形と自前で当たり判定する（<see cref="TryHover"/> / <see cref="TryPress"/>）。
    ///
    /// ★ <b>同じ項目構成（キーの並び）での更新は行を作り直さない。</b> 値・有効/無効・note だけ
    ///   差し替える。構成そのもの（キー・種類の並び）が変わったときだけ作り直す。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform))]
    [RequireComponent(typeof(Canvas))]
    public sealed class XrSettingsPanel : MonoBehaviour
    {
        // ── 見た目の既定値（実測値ではない。読みやすさ優先の目安） ──────────────
        private const float WidthMeters = 0.35f;
        private const float WidthPixels = 480f;
        private const float PixelsToMeters = WidthMeters / WidthPixels;

        private const float DistanceMeters = 0.5f;

        /// <summary>
        /// パネルの高さの上限（メートル）。項目が増えて超えるときは、パネル全体を縮めて収める。
        /// ★ グラスの表示視野は狭く、下へ伸ばすと視野から外れるうえ、机などの面にめり込む。
        /// </summary>
        private const float MaxHeightMeters = 0.3f;

        /// <summary>正面へ戻すときの寄せの速さ（1/秒）。既定値。</summary>
        private const float FollowRate = 6f;

        private const float PaddingPixels = 20f;
        private const float RowHeightPixels = 44f;
        private const float RowSpacingPixels = 10f;
        private const float SectionGapPixels = 18f;
        private const float NoteHeightPixels = 24f;
        private const float SidePaddingPixels = 16f;

        /// <summary>ラベルと値を分ける横位置（行の幅に対する割合）。Choice はラベルが空なら使わない。</summary>
        private const float ValueColumnStart = 0.58f;

        /// <summary>行の内寸（左右の余白を除いた幅）。Choice の ‹ › の列幅をこの割合で決める。</summary>
        private const float RowContentWidthPixels = WidthPixels - SidePaddingPixels * 2f;

        /// <summary>Choice の ‹ › 1つぶんの幅。既定値——タップしやすい大きさを優先した目安。</summary>
        private const float ChoiceArrowWidthPixels = 32f;

        /// <summary>値欄の中で ‹ › が占める割合（行の幅に対して）。</summary>
        private const float ChoiceArrowFraction = ChoiceArrowWidthPixels / RowContentWidthPixels;

        private const float LabelFontSize = 22f;
        private const float ValueFontSize = 22f;
        private const float NoteFontSize = 15f;
        private const float SectionFontSize = 24f;

        /// <summary>長い文字列を枠に収めるときに縮めてよい下限。既定値。</summary>
        private const int MinFontSize = 12;

        private static readonly Color PanelColor = new Color(0.04f, 0.04f, 0.07f, 0.92f);
        private static readonly Color RowColor = new Color(1f, 1f, 1f, 0.05f);
        private static readonly Color RowHoverColor = new Color(1f, 1f, 1f, 0.16f);
        private static readonly Color LabelColor = Color.white;
        private static readonly Color NoteColor = new Color(0.85f, 0.85f, 0.9f, 0.75f);
        private static readonly Color DisabledLabelColor = new Color(1f, 1f, 1f, 0.4f);
        private static readonly Color DisabledNoteColor = new Color(0.85f, 0.85f, 0.9f, 0.35f);
        private static readonly Color SectionColor = new Color(0.75f, 0.82f, 1f, 1f);
        private static readonly Color CloseRowColor = new Color(1f, 1f, 1f, 0.1f);

        /// <summary>Choice で、ホバーしている側の ‹ / › を目立たせる色。</summary>
        private static readonly Color ChoiceActiveArrowColor = new Color(0.75f, 0.82f, 1f, 1f);

        /// <summary>行1件。<b>作り直さない更新</b>のために、見た目のパーツを持ち回る。</summary>
        private sealed class Row
        {
            public SettingSpec Spec;
            public RectTransform Rect;
            public Image Background;
            public Text Label;
            public Text Value;
            public Text Note;
            public RectTransform NoteRect;

            /// <summary>Choice だけが持つ ‹ / › の表示。<c>Bool</c> / <c>Button</c> では null。</summary>
            public Text ChoicePrev;
            public Text ChoiceNext;

            /// <summary>この行が操作を受け付けるか（<c>Section</c> や <c>Enabled=false</c> は不可）。</summary>
            public bool Interactive;
        }

        /// <summary>行が押されたときの振る舞い。<b>キーの意味はここでは持たない</b>（値の作り方だけ）。</summary>
        private enum RowAction
        {
            None,
            ToggleBool,
            Choice,
            Press,
        }

        public event Action<string, string> SettingChanged;

        /// <summary>パネル自体の「閉じる」が押された（もう閉じ終わっている）。</summary>
        public event Action Closed;

        public bool IsOpen { get; private set; }

        private RectTransform _panelRect;
        private Canvas _canvas;

        private RectTransform _closeRect;
        private Image _closeBackground;
        private Text _closeLabel;

        private readonly List<Row> _rows = new List<Row>();
        private string _layoutSignature = "";

        private Row _hoveredRow;
        private bool _hoveredIsNext;
        private bool _hoveringClose;

        private bool _built;

        /// <summary>パネルの中心を置く位置。</summary>
        private Vector3 _center;

        private Transform _userCamera;

        /// <summary>視線の正面へ戻している最中か（→ <see cref="XrMenuRules.ShouldFollowPanel"/>）。</summary>
        private bool _following;

        public void Open(Transform userCamera, string closeLabel, IReadOnlyList<SettingSpec> items)
        {
            EnsureBuilt();
            _closeLabel.text = closeLabel ?? "";

            IsOpen = true;
            gameObject.SetActive(true);
            Rebuild(items);
            Place(userCamera);
        }

        /// <summary>開いている間に項目を更新する。閉じていれば何もしない。</summary>
        public void Refresh(IReadOnlyList<SettingSpec> items)
        {
            if (!IsOpen) return;
            Rebuild(items);
        }

        public void Close()
        {
            if (!IsOpen) return;

            IsOpen = false;
            ClearHover();
            gameObject.SetActive(false);

            if (Closed != null) Closed();
        }

        /// <summary>
        /// レイの下にある行のハイライトを更新する。<b>毎フレーム、つまんでいなくても呼ぶこと。</b>
        /// </summary>
        /// <returns>レイがパネルの矩形の中に入っているか。</returns>
        public bool TryHover(Ray ray)
        {
            if (!IsOpen) return false;

            if (!TryLocalPoint(ray, out var local))
            {
                ClearHover();
                return false;
            }

            UpdateHover(local);
            return true;
        }

        /// <summary>
        /// レイの下にある行を押す。<see cref="SettingChanged"/> か <see cref="Closed"/> が発火しうる。
        /// </summary>
        /// <returns>レイがパネルの矩形の中に入っていたか（当たっていれば、具体的な操作が無くても
        /// <b>true</b>——呼び出し側はこれでつまみをパネルへ吸わせ、キャラの掴みへ流さない）。</returns>
        public bool TryPress(Ray ray)
        {
            if (!IsOpen) return false;
            if (!TryLocalPoint(ray, out var local)) return false;

            UpdateHover(local);

            if (_hoveringClose)
            {
                Close();
                return true;
            }

            if (_hoveredRow != null && _hoveredRow.Interactive)
            {
                Invoke(_hoveredRow, _hoveredIsNext);
            }
            return true;
        }

        // ── 置き場所 ───────────────────────────────────────────

        /// <summary>視線の正面に、中心を合わせてカメラの方へ向けて置く。</summary>
        private void Place(Transform userCamera)
        {
            _userCamera = userCamera;
            _following = false;
            if (userCamera == null) return;

            _center = GazeTarget(userCamera);
            Face(userCamera);
            Fit();
        }

        /// <summary>
        /// 視線から大きく外れたら、正面へ寄せて戻す（→ <see cref="XrMenuRules.ShouldFollowPanel"/>）。
        /// ★ グラスの表示視野は狭く、固定したままだと少し視線を動かしただけで見失う。
        /// </summary>
        private void LateUpdate()
        {
            if (!IsOpen || _userCamera == null) return;

            var degrees = Vector3.Angle(_userCamera.forward, _center - _userCamera.position);
            _following = XrMenuRules.ShouldFollowPanel(_following, degrees);
            if (!_following) return;

            var t = 1f - Mathf.Exp(-FollowRate * Time.unscaledDeltaTime);
            _center = Vector3.Lerp(_center, GazeTarget(_userCamera), t);
            Face(_userCamera);
            Fit();
        }

        private static Vector3 GazeTarget(Transform userCamera) =>
            userCamera.position + userCamera.forward * DistanceMeters;

        /// <summary>カメラから見て正対させる。傾き（ロール）は持ち込まず、上は常にワールドの上。</summary>
        private void Face(Transform userCamera)
        {
            var toPanel = _center - userCamera.position;
            if (toPanel.sqrMagnitude > 0f) transform.rotation = Quaternion.LookRotation(toPanel, Vector3.up);
        }

        /// <summary>
        /// 高さが <see cref="MaxHeightMeters"/> に収まる縮尺にして、中心を <see cref="_center"/> に合わせる。
        /// ★ pivot が左上（下記 EnsureBuilt）なので、中心から半幅・半高ぶんずらした所に置く。
        /// </summary>
        private void Fit()
        {
            var size = _panelRect.sizeDelta;
            var scale = Mathf.Min(PixelsToMeters, MaxHeightMeters / size.y);
            transform.localScale = Vector3.one * scale;
            transform.position = _center - transform.right * (size.x * scale / 2f) + transform.up * (size.y * scale / 2f);
        }

        // ── 当たり判定 ─────────────────────────────────────────
        // ★ レイとの交点はワールド座標の往復（TransformPoint / InverseTransformPoint）で求める。
        //   pivot・anchor の解釈を自分で追わなくても、実際の Transform 階層がそのまま答えになる。

        /// <summary>レイとパネル平面の交点を、パネル自身のローカル座標で返す。矩形の外なら false。</summary>
        private bool TryLocalPoint(Ray ray, out Vector2 local)
        {
            local = default;

            var plane = new Plane(transform.forward, transform.position);
            if (!plane.Raycast(ray, out var enter)) return false;

            var worldPoint = ray.GetPoint(enter);
            var localPoint = transform.InverseTransformPoint(worldPoint);
            local = new Vector2(localPoint.x, localPoint.y);

            return _panelRect.rect.Contains(local);
        }

        /// <summary>パネルのローカル座標を、ある行のローカル座標へ変換する。行の矩形の外なら false。</summary>
        private bool TryRowLocalPoint(RectTransform rect, Vector2 panelLocal, out Vector2 rowLocal)
        {
            var worldPoint = transform.TransformPoint(new Vector3(panelLocal.x, panelLocal.y, 0f));
            var localPoint = rect.InverseTransformPoint(worldPoint);
            rowLocal = new Vector2(localPoint.x, localPoint.y);
            return rect.rect.Contains(rowLocal);
        }

        private void UpdateHover(Vector2 local)
        {
            var hoveringClose = _closeRect != null && TryRowLocalPoint(_closeRect, local, out _);

            Row hit = null;
            var isNext = false;
            if (!hoveringClose)
            {
                foreach (var row in _rows)
                {
                    if (!row.Interactive) continue;
                    if (!TryRowLocalPoint(row.Rect, local, out var rowLocal)) continue;

                    hit = row;
                    isNext = HoverIsNext(row, rowLocal.x);
                    break;
                }
            }

            if (hit == _hoveredRow && hoveringClose == _hoveringClose && isNext == _hoveredIsNext) return;

            ClearHover();
            _hoveredRow = hit;
            _hoveredIsNext = isNext;
            _hoveringClose = hoveringClose;

            if (_hoveredRow != null)
            {
                _hoveredRow.Background.color = RowHoverColor;
                SetChoiceArrowColors(_hoveredRow, _hoveredIsNext);
            }
            if (_hoveringClose && _closeBackground != null) _closeBackground.color = RowHoverColor;
        }

        /// <summary>
        /// Choice の押す判定: <b>値欄の左半分 = 前、右半分 = 次。ラベル欄を押したら次。</b>
        /// ラベルが空の行は値欄が行の全幅になる（→ <see cref="BuildRow"/>）ので、行全体を
        /// 左右半分に分けるのと同じになる。Choice 以外は常に false（呼び出し側で無視される）。
        /// </summary>
        private static bool HoverIsNext(Row row, float localX)
        {
            if (row.Spec.Kind != SettingKind.Choice) return false;

            var hasLabel = !string.IsNullOrEmpty(row.Spec.Label);
            var valueAreaStartX = hasLabel ? row.Rect.rect.width * ValueColumnStart : 0f;
            if (localX < valueAreaStartX) return true; // ラベル欄

            var valueAreaMidX = (valueAreaStartX + row.Rect.rect.width) / 2f;
            return localX >= valueAreaMidX;
        }

        private void ClearHover()
        {
            if (_hoveredRow != null)
            {
                _hoveredRow.Background.color = RowColor;
                ResetChoiceArrowColors(_hoveredRow);
            }
            if (_hoveringClose && _closeBackground != null) _closeBackground.color = CloseRowColor;
            _hoveredRow = null;
            _hoveringClose = false;
        }

        private static void SetChoiceArrowColors(Row row, bool isNext)
        {
            if (row.ChoicePrev == null || row.ChoiceNext == null) return;
            row.ChoicePrev.color = isNext ? LabelColor : ChoiceActiveArrowColor;
            row.ChoiceNext.color = isNext ? ChoiceActiveArrowColor : LabelColor;
        }

        private static void ResetChoiceArrowColors(Row row)
        {
            if (row.ChoicePrev == null || row.ChoiceNext == null) return;
            var color = row.Spec.Enabled ? LabelColor : DisabledLabelColor;
            row.ChoicePrev.color = color;
            row.ChoiceNext.color = color;
        }

        private void Invoke(Row row, bool isNext)
        {
            var spec = row.Spec;
            switch (RowActionFor(spec))
            {
                case RowAction.ToggleBool:
                    Raise(spec.Key, spec.Value == "true" ? "false" : "true");
                    return;

                case RowAction.Choice:
                {
                    var value = NextChoiceValue(spec, isNext);
                    if (value != null) Raise(spec.Key, value);
                    return;
                }

                case RowAction.Press:
                    Raise(spec.Key, "");
                    return;
            }
        }

        private void Raise(string key, string value)
        {
            if (SettingChanged != null) SettingChanged(key, value);
        }

        private static RowAction RowActionFor(SettingSpec spec)
        {
            switch (spec.Kind)
            {
                case SettingKind.Bool: return RowAction.ToggleBool;
                case SettingKind.Choice: return spec.Choices.Count > 1 ? RowAction.Choice : RowAction.None;
                case SettingKind.Button: return RowAction.Press;
                default: return RowAction.None;
            }
        }

        private static string NextChoiceValue(SettingSpec spec, bool forward)
        {
            var choices = spec.Choices;
            if (choices.Count == 0) return null;

            var index = 0;
            for (var i = 0; i < choices.Count; i++)
            {
                if (choices[i].Value == spec.Value)
                {
                    index = i;
                    break;
                }
            }

            index = forward ? (index + 1) % choices.Count : (index - 1 + choices.Count) % choices.Count;
            return choices[index].Value;
        }

        // ── 組み立て ───────────────────────────────────────────
        //
        // ★ 幅いっぱいのピクセル寸法でレイアウトを組み、Canvas 自身の localScale を
        //   PixelsToMeters まで縮める（世界空間 UI の定番のやり方）。行の当たり判定は
        //   ワールド座標の往復で求める（上記）ので、この縮尺は見た目にしか影響しない。

        private void EnsureBuilt()
        {
            if (_built) return;
            _built = true;

            _panelRect = (RectTransform)transform;
            // ★ pivot は左上。子の anchoredPosition がそのままローカル座標になり、
            //   「上から積む」レイアウトの計算が素直になる（Place で見た目だけ中央へ補正する）
            _panelRect.pivot = new Vector2(0f, 1f);
            _panelRect.sizeDelta = new Vector2(WidthPixels, RowHeightPixels + PaddingPixels * 2f);

            _canvas = GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            transform.localScale = Vector3.one * PixelsToMeters;

            var background = gameObject.AddComponent<Image>();
            background.color = PanelColor;

            BuildCloseRow();

            gameObject.SetActive(false);
        }

        private void BuildCloseRow()
        {
            _closeRect = NewChild(-PaddingPixels, RowHeightPixels);
            _closeBackground = _closeRect.gameObject.AddComponent<Image>();
            _closeBackground.color = CloseRowColor;
            _closeLabel = BuildText(_closeRect, Vector2.zero, Vector2.one, TextAnchor.MiddleCenter, LabelFontSize, LabelColor);
        }

        /// <summary>
        /// 項目を反映する。構成（キー・種類の並び）が変わっていなければ行は作り直さず、
        /// 値・有効/無効・note だけ差し替える。
        /// </summary>
        private void Rebuild(IReadOnlyList<SettingSpec> items)
        {
            var signature = Signature(items);
            if (signature == _layoutSignature)
            {
                UpdateRows(items);
                return;
            }

            _layoutSignature = signature;
            ClearHover();
            foreach (var row in _rows) Destroy(row.Rect.gameObject);
            _rows.Clear();

            var y = -(PaddingPixels + RowHeightPixels) - RowSpacingPixels;
            foreach (var spec in items) y = BuildRow(spec, y);

            _panelRect.sizeDelta = new Vector2(WidthPixels, PaddingPixels - y);
            Fit();
        }

        private static string Signature(IReadOnlyList<SettingSpec> items)
        {
            var parts = new string[items.Count];
            for (var i = 0; i < items.Count; i++)
            {
                var spec = items[i];
                // ★ Section はキーを持たないのでラベルで区別する
                parts[i] = spec.Kind + ":" + (spec.Key ?? spec.Label);
            }
            return string.Join("\u001f", parts);
        }

        /// <returns>次の行の開始 y（上端。負方向へ積む）。</returns>
        private float BuildRow(SettingSpec spec, float y)
        {
            if (spec.Kind == SettingKind.Section)
            {
                y -= SectionGapPixels;
                var headingRect = NewChild(y, RowHeightPixels);
                var heading = BuildText(headingRect, Vector2.zero, Vector2.one, TextAnchor.LowerLeft, SectionFontSize, SectionColor);
                heading.text = spec.Label;
                heading.fontStyle = FontStyle.Bold;
                y -= RowHeightPixels;

                if (!string.IsNullOrEmpty(spec.Note))
                {
                    var sectionNoteRect = NewChild(y, NoteHeightPixels);
                    var sectionNote = InsetText(sectionNoteRect, TextAnchor.UpperLeft, NoteFontSize, NoteColor);
                    sectionNote.text = spec.Note;
                    y -= NoteHeightPixels;
                }
                return y - RowSpacingPixels;
            }

            if (spec.Kind != SettingKind.Bool && spec.Kind != SettingKind.Choice && spec.Kind != SettingKind.Button)
            {
                // ★★ 知らない Kind は描かない
                return y;
            }

            var rowRect = NewChild(y, RowHeightPixels);
            var background = rowRect.gameObject.AddComponent<Image>();
            background.color = RowColor;

            Text label = null;
            Text value = null;
            Text choicePrev = null;
            Text choiceNext = null;

            if (spec.Kind == SettingKind.Button)
            {
                label = BuildText(rowRect, Vector2.zero, Vector2.one, TextAnchor.MiddleCenter, LabelFontSize, LabelColor);
                label.text = spec.Label;
            }
            else if (spec.Kind == SettingKind.Choice)
            {
                // ★ ‹ を値欄の左端、› を値欄の右端に置き、値ラベルはその間に中央寄せ。
                //   ラベルが空なら値欄は行の全幅になる（→ HoverIsNext を同じ境界に揃える）。
                var hasLabel = !string.IsNullOrEmpty(spec.Label);
                if (hasLabel)
                {
                    label = BuildText(rowRect, new Vector2(0f, 0f), new Vector2(ValueColumnStart, 1f),
                        TextAnchor.MiddleLeft, LabelFontSize, LabelColor);
                    label.text = spec.Label;
                    label.rectTransform.offsetMin = new Vector2(SidePaddingPixels, 0f);
                }

                var valueAreaStart = hasLabel ? ValueColumnStart : 0f;
                var prevEnd = valueAreaStart + ChoiceArrowFraction;
                var nextStart = 1f - ChoiceArrowFraction;

                choicePrev = BuildText(rowRect, new Vector2(valueAreaStart, 0f), new Vector2(prevEnd, 1f),
                    TextAnchor.MiddleCenter, ValueFontSize, LabelColor);
                choicePrev.text = "‹";
                if (!hasLabel) choicePrev.rectTransform.offsetMin = new Vector2(SidePaddingPixels, 0f);

                choiceNext = BuildText(rowRect, new Vector2(nextStart, 0f), new Vector2(1f, 1f),
                    TextAnchor.MiddleCenter, ValueFontSize, LabelColor);
                choiceNext.text = "›";
                choiceNext.rectTransform.offsetMax = new Vector2(-SidePaddingPixels, 0f);

                value = BuildText(rowRect, new Vector2(prevEnd, 0f), new Vector2(nextStart, 1f),
                    TextAnchor.MiddleCenter, ValueFontSize, LabelColor);
            }
            else // Bool
            {
                label = BuildText(rowRect, new Vector2(0f, 0f), new Vector2(ValueColumnStart, 1f),
                    TextAnchor.MiddleLeft, LabelFontSize, LabelColor);
                label.text = spec.Label;
                label.rectTransform.offsetMin = new Vector2(SidePaddingPixels, 0f);

                value = BuildText(rowRect, new Vector2(ValueColumnStart, 0f), new Vector2(1f, 1f),
                    TextAnchor.MiddleRight, ValueFontSize, LabelColor);
                value.rectTransform.offsetMax = new Vector2(-SidePaddingPixels, 0f);
            }

            y -= RowHeightPixels;

            Text note = null;
            RectTransform noteRect = null;
            if (!string.IsNullOrEmpty(spec.Note))
            {
                noteRect = NewChild(y, NoteHeightPixels);
                note = InsetText(noteRect, TextAnchor.UpperLeft, NoteFontSize, NoteColor);
                y -= NoteHeightPixels;
            }

            y -= RowSpacingPixels;

            var row = new Row
            {
                Spec = spec,
                Rect = rowRect,
                Background = background,
                Label = label,
                Value = value,
                ChoicePrev = choicePrev,
                ChoiceNext = choiceNext,
                Note = note,
                NoteRect = noteRect,
                Interactive = spec.Enabled && RowActionFor(spec) != RowAction.None,
            };
            _rows.Add(row);
            ApplyRowValues(row);
            return y;
        }

        /// <summary>行を作り直さずに、値・有効/無効・note を反映する。</summary>
        private void UpdateRows(IReadOnlyList<SettingSpec> items)
        {
            var index = 0;
            foreach (var spec in items)
            {
                if (spec.Kind != SettingKind.Bool && spec.Kind != SettingKind.Choice && spec.Kind != SettingKind.Button)
                {
                    continue;
                }
                if (index >= _rows.Count) break;

                var row = _rows[index];
                row.Spec = spec;
                row.Interactive = spec.Enabled && RowActionFor(spec) != RowAction.None;
                ApplyRowValues(row);
                index++;
            }
        }

        private void ApplyRowValues(Row row)
        {
            var spec = row.Spec;
            var labelColor = spec.Enabled ? LabelColor : DisabledLabelColor;
            var noteColor = spec.Enabled ? NoteColor : DisabledNoteColor;

            // ★ Choice でラベルが空の行は Label が無い（値欄が行の全幅）
            if (row.Label != null) row.Label.color = labelColor;
            row.Background.color = row == _hoveredRow ? RowHoverColor : RowColor;

            if (row.Value != null)
            {
                row.Value.text = DisplayValue(spec);
                row.Value.color = labelColor;
            }

            if (row.ChoicePrev != null && row.ChoiceNext != null)
            {
                // ★ ホバー中はそちらの色を保つ（Background と同じ、いま見ている側の表示を作り直しで崩さない）
                if (row == _hoveredRow)
                {
                    SetChoiceArrowColors(row, _hoveredIsNext);
                }
                else
                {
                    row.ChoicePrev.color = labelColor;
                    row.ChoiceNext.color = labelColor;
                }
            }

            if (row.Note != null) row.Note.text = spec.Note;
            if (row.Note != null) row.Note.color = noteColor;
            if (row.NoteRect != null) row.NoteRect.gameObject.SetActive(!string.IsNullOrEmpty(spec.Note));
        }

        /// <summary>Bool は ON/OFF、Choice は値ラベルだけ（‹ › は別の Text）、Button は値を持たない。</summary>
        private static string DisplayValue(SettingSpec spec)
        {
            switch (spec.Kind)
            {
                case SettingKind.Bool:
                    return spec.Value == "true" ? "ON" : "OFF";

                case SettingKind.Choice:
                    return LabelOf(spec);

                default:
                    return "";
            }
        }

        private static string LabelOf(SettingSpec spec)
        {
            foreach (var choice in spec.Choices)
            {
                if (choice.Value == spec.Value) return choice.Label;
            }
            return spec.Value ?? "";
        }

        /// <summary>パネル直下に、上端 y・高さ <paramref name="height"/> の矩形を1つ作る。</summary>
        private RectTransform NewChild(float y, float height)
        {
            var go = new GameObject("Row", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(transform, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(WidthPixels - SidePaddingPixels * 2f, height);
            rect.anchoredPosition = new Vector2(SidePaddingPixels, y);
            return rect;
        }

        private static Text BuildText(
            RectTransform parent, Vector2 anchorMin, Vector2 anchorMax, TextAnchor alignment, float fontSize, Color color)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var text = go.AddComponent<Text>();
            // ★ TMP は使わない（日本語フォントアセットが要る）。組み込みのフォールバックフォントで足りる
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = Mathf.RoundToInt(fontSize);
            text.alignment = alignment;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            // ★ 長いラベル・値は切らずに縮めて収める。枠の大きさは項目によらず一定なので、
            //   どの項目が長いかをレンダラが知らなくても済む
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = MinFontSize;
            text.resizeTextMaxSize = Mathf.RoundToInt(fontSize);
            return text;
        }

        /// <summary>左右に <see cref="SidePaddingPixels"/> の余白を持つ、幅いっぱいのテキスト。</summary>
        private static Text InsetText(RectTransform parent, TextAnchor alignment, float fontSize, Color color)
        {
            var text = BuildText(parent, Vector2.zero, Vector2.one, alignment, fontSize, color);
            text.rectTransform.offsetMin = new Vector2(SidePaddingPixels, 0f);
            text.rectTransform.offsetMax = new Vector2(-SidePaddingPixels, 0f);
            return text;
        }
    }
}
