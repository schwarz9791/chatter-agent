using ChatterMascot.Vrm;
using Kirurobo;
using UnityEngine;

namespace ChatterMascot.Desktop
{
    /// <summary>
    /// OS のマウスカーソル座標を「キャラの顔の高さを縦の中心とした正規化値」にして
    /// <see cref="VrmCharacter.CursorProvider"/> へ注入する。
    ///
    /// ★ <b><c>MonoBehaviour</c> にしない。シーンに置かない。</b> <c>VrmDragHandleBinder</c> と
    ///   同じ理由 —— シーンは <c>MonoBehaviour</c> を <c>m_Script</c> の GUID として持つだけで、
    ///   <b>asmdef の <c>includePlatforms</c> と無関係に常にシリアライズされる</b>。このアセンブリは
    ///   Android でコンパイルされないので解決先が無くなり、ビルドエラーではなく<b>シーンロード時の
    ///   "The referenced script on this Behaviour is missing!" が1本出るだけ</b>になる。
    ///   <c>RuntimeInitializeOnLoadMethod</c> なら、Android では<b>アセンブリごと存在しない</b>ので
    ///   属性の走査対象にすらならず、<c>CursorProvider</c> は <c>null</c> のまま
    ///   ＝自律的な漂いへ自動的に倒れる（<c>#if</c> もプラットフォーム分岐も要らない）。
    ///
    /// ★ <b><c>Mouse.current</c> / <c>Input.mousePosition</c> を使わないこと。</b>
    ///   <c>UniWindowController.GetClientCursorPosition()</c> に「New Input System では
    ///   フォーカスが無い場合にマウス座標が取得できないため独自に計算する」というコメントがある。
    ///   <b>常駐マスコットは基本フォーカスを持たない</b>ので、New Input System 経由のマウス座標は
    ///   常に取れない前提で書く必要がある。だから <c>UniWinCore</c>（ネイティブ）経由の
    ///   <see cref="UniWindowController.GetCursorPosition"/> を使う。
    ///
    /// ★ <b><c>Screen.width</c> / <c>Screen.height</c> を混ぜないこと。</b>
    ///   <c>cursorPosition</c> / <c>windowPosition</c> / <c>clientSize</c> は<b>すべて
    ///   LibUniWinC 由来のポイント</b>なので、換算そのものが要らない。<c>Screen.*</c> は
    ///   バッキングストアの px なので、1つでも混ぜると Retina 2x で2倍ずれる
    ///   （<c>Desktop/WindowSizeKeeper.cs</c> が踏んだのと同じ罠）。
    ///
    /// ★ <b>正規化を割る量に <c>clientSize</c>（ウィンドウの実寸）を使わないこと。</b>
    ///   250pt のウィンドウで割ると 3840pt の画面では正規化値が ±15 に達し、<c>GazeAim</c> の
    ///   ±35° の clamp を常に振り切って「追従」ではなく「最大まで曲げて固まる」状態になる
    ///   （実機で確認）。<see cref="ReferenceSpanPoints"/>（固定 800pt。cc-mascot の
    ///   <c>containerSize: 800</c> と同じ趣旨）で割ることで、ウィンドウの実寸に関わらず
    ///   同じ感度になる。<c>clientSize</c> はウィンドウが存在するかの判定にだけ使う。
    ///
    /// ★ <b>縦の基準は「ウィンドウの中心」ではなく「キャラの顔の高さ」。</b>
    ///   ウィンドウの中心はキャラの腰のあたり（<c>VrmFraming</c> が画面内に収める構図の中心）
    ///   なので、そこを基準にするとカーソルを顔の横に置いても正の値になり、上を向いてしまう
    ///   （実機で「顔の横にカーソルを置いてもやや上目線」として指摘された）。
    ///   <c>VrmCharacter.GazeOriginViewportY</c>（cc-mascot の <c>mouse.y - headY</c> と同じ趣旨）で
    ///   縦の中心をずらす。<b>横はウィンドウ中心のまま</b>（ユーザーからは「左右は問題ない」との
    ///   報告で、cc-mascot も横は補正していない）。
    ///
    /// ★ <b>診断ログは「視線の原点が実測で決まった後」の最初の1回まで待つこと。</b>
    ///   <see cref="TryRead"/> は <c>VrmCharacter</c> の <c>Start</c> 直後の1フレーム目から呼ばれるが、
    ///   <see cref="VrmCharacter.GazeOriginViewportY"/> が実測で埋まるのは VRM の読み込みが終わって
    ///   から（実測で 1.6〜2.1 秒後）。ここを待たずにログを1回だけ出すと、
    ///   <b>フォールバック値（0.5）とウィンドウの枠なし化前のサイズが「それらしい数字」として
    ///   永久に記録され</b>、次に確かめる人が「ログで確認した」つもりで誤った値を見ることになる
    ///   （実機で実際に踏んだ ——「動いて見える死体」の形）。
    ///   ★ <b>ただし VRM がどうしても読めなかった場合に永久にログが出ない、という形にもしないこと。</b>
    ///   「出ない」と「正常」が区別できないのが元の問題そのものなので、<see cref="GiveUpSeconds"/>
    ///   を超えても実測が埋まらなければ、フォールバックのままである旨を <c>LogWarning</c> で1回出す。
    ///
    /// ★ <b><c>FindFirstObjectByType</c> は <c>Bind</c> で1回だけ呼ぶこと。</b> <see cref="TryRead"/> は
    ///   <c>VrmCharacter.LateUpdate</c> から毎フレーム（常駐アプリで 30回/秒）呼ばれるので、
    ///   シーン全体を走査する <c>FindFirstObjectByType</c> をここに置くと電力予算に直接効く
    ///   （<c>docs/mascot.md</c> 冒頭の「Cube 1個のシーンで CPU 261%」と同種の罠）。
    ///   <c>VrmDragHandleBinder</c> / <c>WindowSizeKeeper</c> と同じく、参照は起動時に1回だけ引いて
    ///   static に持つ。
    /// </summary>
    public static class CursorGazeSource
    {
        /// <summary>
        /// 正規化の基準幅（ポイント）。★ <b>ウィンドウの大きさ（<c>clientSize</c>）で割らないこと。</b>
        /// 250pt のウィンドウで割ると 3840pt の画面では正規化値が ±15 に達し、<c>GazeAim</c> の
        /// ±35° の clamp を常に振り切って<b>「追従」ではなく「最大まで曲げて固まる」</b>ようになる
        /// （実機で確認）。cc-mascot が固定 800px のコンテナで正規化しているのと同じ趣旨。
        /// </summary>
        private const float ReferenceSpanPoints = 800f;

        /// <summary>
        /// <see cref="VrmCharacter.HasGazeOrigin"/> が実測で立つのを待つ上限（秒）。
        ///
        /// ★ 実測では VRM の読み込みに 1.6〜2.1 秒だが、遅いディスクや <c>-vrm</c> の
        ///   差し替えを考えて余裕を持たせる。これを超えても立たなければ、フォールバックのまま
        ///   である旨を1回だけ <c>LogWarning</c> で出す（<c>WindowSizeKeeper.Keeper.WatchSeconds</c>
        ///   と同じ「見張って諦める」形）。
        /// </summary>
        private const float GiveUpSeconds = 20f;

        private static UniWindowController _controller;

        /// <summary>
        /// 縦の中心（<see cref="VrmCharacter.GazeOriginViewportY"/>）を読む先。
        /// ★ <c>TryRead</c> から毎フレーム参照するので、これも <c>Bind</c> で1回だけ引く。
        /// </summary>
        private static VrmCharacter _character;

        private static bool _loggedOnce;

        /// <summary><see cref="GiveUpSeconds"/> の起点。<c>Bind</c> で毎回引き直す。</summary>
        private static float _bindTime;

        /// <summary>
        /// ★ <b>Enter Play Mode without domain reload に備えて static を明示的に戻す。</b>
        ///   <c>_controller</c> / <c>_character</c> を戻し忘れると、前回の Play で破棄済みの参照を
        ///   引き継いで<b>カーソル追従が黙って死ぬ</b>。<c>_loggedOnce</c> も同じ理由
        ///   （残っているとログのラッチが二度と開かない）。<c>FrameRateBudget.ResetStatics</c> と同じ形。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _controller = null;
            _character = null;
            _loggedOnce = false;
            _bindTime = 0f;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bind()
        {
            var character = Object.FindFirstObjectByType<VrmCharacter>(FindObjectsInactive.Include);
            if (character == null) return;   // VRM を出さないシーンなど

            // ★ 毎フレーム引かないこと。TryRead は LateUpdate から 30回/秒 呼ばれる。
            //   FindFirstObjectByType はシーン全体の走査で、常駐アプリの電力予算に直接効く
            _controller = Object.FindFirstObjectByType<UniWindowController>();
            _character = character;
            _bindTime = Time.realtimeSinceStartup;
            character.CursorProvider = TryRead;
        }

        /// <summary>
        /// 縦はキャラの顔の高さ、横はウィンドウ中心を基準に正規化したカーソル座標。
        /// <b>取れなければ <c>null</c></b>（呼び出し側は自律的な漂いに倒れる）。
        /// </summary>
        private static Vector2? TryRead()
        {
            if (_controller == null) return null;

            var cursor = UniWindowController.GetCursorPosition();
            var origin = _controller.windowPosition;
            var size = _controller.clientSize;
            // ★ ウィンドウが存在するかの判定にはこれまでどおり size を使う。
            //   Editor では枠なし化が起きず 0 になりうる
            if (size.x <= 0f || size.y <= 0f) return null;

            // ★ 縦の基準は「ウィンドウの中心」ではなく「キャラの顔の高さ」。
            //   ウィンドウの中心はキャラの腰のあたりなので、そこを基準にすると
            //   カーソルを顔の横に置いても正の値になり、上を向いてしまう（実機で指摘された）。
            //   cc-mascot の useCursorTracking.ts が mouse.y - headY としているのと同じ趣旨。
            // ★ ビューポート Y は下が 0。cursorPosition / windowPosition も bottom-up なので
            //   向きは揃っている（→ UniWindowController.GetCursorPosition() の Y は bottom-up）。
            // ★ 横は変えないこと。ウィンドウ中心のまま（ユーザーは「左右は大丈夫」、
            //   cc-mascot も横は補正していない）。
            var viewportY = _character != null ? _character.GazeOriginViewportY : 0.5f;
            var centerY = origin.y + size.y * viewportY;
            var center = new Vector2(origin.x + size.x * 0.5f, centerY);

            // ★ 割る量は size ではなく ReferenceSpanPoints（固定）。中心基準にすること
            //   （ウィンドウ幅と基準幅が違うので、左下端基準の *2-1 では中心がずれる）
            var n = (cursor - center) / (ReferenceSpanPoints * 0.5f);

            TryLogOnce(cursor, origin, size, viewportY, center, n);

            return n;
        }

        /// <summary>
        /// 診断ログを最大1回だけ出す。
        ///
        /// ★ <see cref="VrmCharacter.HasGazeOrigin"/> が立つまでは<b>出さない</b>
        ///   （立つ前の値はフォールバックで、いま実際に使われている中立を表さない）。
        /// ★ ただし <see cref="GiveUpSeconds"/> を超えても立たなければ、フォールバックのままである
        ///   旨を <c>LogWarning</c> で1回出す。「出ない」を「正常」と区別できない状態を作らないため。
        /// </summary>
        private static void TryLogOnce(Vector2 cursor, Vector2 origin, Vector2 size, float viewportY, Vector2 center, Vector2 normalized)
        {
            if (_loggedOnce) return;

            if (_character != null && _character.HasGazeOrigin)
            {
                _loggedOnce = true;
                Debug.Log($"[Mascot] カーソル: cursor={cursor} origin={origin} size={size} " +
                          $"gazeOriginViewportY={viewportY} center={center} normalized={normalized}");
                return;
            }

            if (Time.realtimeSinceStartup - _bindTime < GiveUpSeconds) return;

            _loggedOnce = true;
            Debug.LogWarning("[Mascot] 視線の原点が実測で決まらないまま " +
                              $"{GiveUpSeconds:F0} 秒経ちました。中立はウィンドウ中心のままです: " +
                              $"cursor={cursor} origin={origin} size={size} " +
                              $"gazeOriginViewportY={viewportY}（フォールバック） center={center} normalized={normalized}");
        }
    }
}
