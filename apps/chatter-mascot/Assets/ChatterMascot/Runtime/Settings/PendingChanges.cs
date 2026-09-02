using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ChatterMascot.Settings
{
    /// <summary>
    /// 設定パネルの<b>保留中の変更</b>。デバウンスの締め切りと、まだ適用していない値を持つ。
    ///
    /// ★★ <b>スライダーを1ティック動かすたびに保存・適用しないこと。</b> #76 の初版は
    ///   毎ティックで <c>settings.json</c> の書き込み・シーンへの反映・メニューの更新を
    ///   走らせていて、実機で「重い」と言われた。特にウィンドウのリサイズは
    ///   <c>WindowGeometry</c> が最大5回書き直して追従するので効く。
    ///
    /// ★★ <b>変更の起点は「保留があるならそれ」（<see cref="Base"/>）。</b>
    ///   ここが #85 のレビュー A-3 で踏んだ穴 —— 保留は <see cref="MascotSettings"/> を
    ///   <b>構造体まるごと</b>写すので、起点を「いまホストが持っている値」にすると、
    ///   デバウンス中に確定した別の項目を<b>あとから巻き戻す</b>:
    ///   <list type="number">
    ///     <item>音量スライダーを離す → 保留に <c>{volume=0.5, blink=true}</c></item>
    ///     <item>300ms 以内に「まばたき」を外す → ホストの値から <c>blink=false</c> を作って即座に確定</item>
    ///     <item>締め切りが来て保留が着地 → <b><c>blink</c> が <c>true</c> に戻る</b></item>
    ///   </list>
    ///   チェックボックスは意図的にパネルを作り直さないので、症状は
    ///   <b>「外れて見えるのに実際はオン」</b>という気づけない形になる。
    ///
    /// ★★ <b>既定へ戻す前には <see cref="Clear"/> を呼ぶこと。</b> 同じ理由で、
    ///   「すべての設定をリセット」「位置と大きさをリセット」の<b>後ろ</b>に
    ///   古い値が着地する（レビューは挙げていないが同型）。
    ///
    /// ★ <b><c>ChatterMascot.Runtime</c> に置く。</b> 判断は Runtime、配線は Desktop
    ///   （<c>SavePolicy</c> / <c>ShutdownPolicy</c> / <c>WindowPlacement</c> と同じ扱い）。
    ///   <c>ChatterMascot.Tests</c> は <c>ChatterMascot.Desktop</c> を参照していないので、
    ///   あちらに置いたままだと<b>ここに書いた不変条件を1行も固定できない</b>。
    ///
    /// ★ <b>時計を持たない。</b> <c>now</c> を引数で受ける（<c>Time.realtimeSinceStartup</c> は
    ///   テストから進められない）。<c>SavePolicy</c> と同じ形。
    /// </summary>
    public sealed class PendingChanges
    {
        /// <summary>
        /// スライダーを動かしてから確定するまでの猶予（秒）。
        ///
        /// ★ core への <c>PATCH</c>・Unity 側の保存・ウィンドウのリサイズを<b>同じ締め切りに
        ///   まとめてある</b>。別々に持つと、1回のスライダー操作で締め切りが3つ走る。
        /// </summary>
        public const float DebounceSeconds = 0.3f;

        private static readonly List<KeyValuePair<string, JToken>> NoCore =
            new List<KeyValuePair<string, JToken>>();

        /// <summary>
        /// 保留中の core への変更。key は core の設定キー。
        ///
        /// ★ 同じキーは<b>上書きする</b>。矢印キーの連打で同じキーが何度も届くので、
        ///   溜め込むと1操作で何度も <c>PATCH</c> することになる。
        /// </summary>
        private readonly Dictionary<string, JToken> _core = new Dictionary<string, JToken>(StringComparer.Ordinal);

        private MascotSettings? _settings;
        private float? _scale;

        /// <summary>★ 空のときは <c>PositiveInfinity</c>。<see cref="Due"/> が常に false になる</summary>
        private float _dueAt = float.PositiveInfinity;

        public bool IsEmpty
        {
            get { return _core.Count == 0 && _settings == null && _scale == null; }
        }

        /// <summary>ウィンドウの倍率が保留中か（→ 追いつきの見送り判定）</summary>
        public bool HasScale
        {
            get { return _scale != null; }
        }

        /// <summary>
        /// 変更を積む起点。<b>保留があるならそれ、無ければホストの現在値</b>（→ 型の doc の ★★）。
        /// </summary>
        public MascotSettings Base(MascotSettings host)
        {
            return _settings ?? host;
        }

        /// <summary>Unity 側の変更を保留する</summary>
        public void Defer(MascotSettings next, float now)
        {
            _settings = next;
            Postpone(now);
        }

        /// <summary>ウィンドウの倍率を保留する（→ <c>ISettingsHost.SetWindowSize</c>）</summary>
        public void DeferScale(float scale, float now)
        {
            _scale = scale;
            Postpone(now);
        }

        /// <summary>
        /// core への変更を保留する。
        ///
        /// ★ <b>スライダーの1操作で何度も PATCH しないこと。</b> ネイティブ側は
        ///   ドラッグ中を投げないようにしてあるが、矢印キーの連打はそのまま届く。
        ///   ここが本命の間引き（→ <c>docs/protocol.md</c> の「制御 API」）。
        /// </summary>
        public void Queue(string coreKey, JToken value, float now)
        {
            if (string.IsNullOrEmpty(coreKey)) return;
            _core[coreKey] = value;
            Postpone(now);
        }

        /// <summary>締め切りに届いたか。<b>空なら常に false</b></summary>
        public bool Due(float now)
        {
            return !IsEmpty && now >= _dueAt;
        }

        /// <summary>
        /// 保留を取り出して空にする。<b>取り出した側が適用の責任を持つ</b>。
        ///
        /// ★ <b>適用の前に空にすること。</b> 適用の途中で新しい操作が届いても、
        ///   いま取り出したぶんと混ざらない。
        /// </summary>
        public void Take(
            out MascotSettings? settings, out float? scale, out List<KeyValuePair<string, JToken>> core)
        {
            settings = _settings;
            scale = _scale;
            core = _core.Count == 0 ? NoCore : new List<KeyValuePair<string, JToken>>(_core);
            Clear();
        }

        /// <summary>
        /// 保留を捨てる。
        ///
        /// ★★ <b>既定へ戻す操作の手前で呼ぶこと</b>（→ 型の doc の ★★）。
        ///   呼ばないと、リセットした<b>後ろ</b>に古い値が着地する。
        /// </summary>
        public void Clear()
        {
            _core.Clear();
            _settings = null;
            _scale = null;
            _dueAt = float.PositiveInfinity;
        }

        /// <summary>
        /// ウィンドウの倍率の保留<b>だけ</b>捨てる（「位置と大きさをリセット」用）。
        ///
        /// ★ <see cref="Clear"/> にしないこと —— リセットするのは窓だけなので、
        ///   同じ 300ms の窓に入っていた音量や話す速さまで道連れにしない。
        /// </summary>
        public void ClearScale()
        {
            _scale = null;
        }

        /// <summary>★ 触るたびに延ばす（＝最後の操作から数える）</summary>
        private void Postpone(float now)
        {
            _dueAt = now + DebounceSeconds;
        }
    }
}
