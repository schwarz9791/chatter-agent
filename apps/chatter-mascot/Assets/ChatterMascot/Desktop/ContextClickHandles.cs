#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
using Kirurobo;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ChatterMascot.Desktop
{
    /// <summary>
    /// キャラクターの<b>右クリック</b>で設定パネルを開閉する（#76）。
    ///
    /// ★★ <b><c>IPointerClickHandler</c> では成立しない。</b> 実測で潰した前提なので戻さないこと:
    ///   常駐マスコットは<b>基本フォーカスを持たない</b>が、macOS が <c>mouseMoved</c> を配送するのは
    ///   前面のアプリだけなので、<c>Mouse.current.position</c> が<b>古い座標のまま止まる</b>。
    ///   右ボタンのイベント自体は届くのに、UI のレイキャストが<b>別の場所</b>を撃つので
    ///   <c>OnPointerClick</c> がそもそも呼ばれない（実測: 窓は 1770,1680 に居るのに
    ///   <c>pos=(588,156)</c>）。左クリックを1回挟むと座標が更新されて動き出す、という
    ///   <b>「たまに効く」いちばん悪い壊れ方</b>をする。
    ///   <c>backgroundBehavior = IgnoreFocus</c> はデバイスを生かすだけで、OS の配送先は変えられない。
    ///   同じ理由がパッケージ側にも書いてある（<c>UniWindowController.GetClientCursorPosition</c> の
    ///   「New Input System ではフォーカスが無い場合にマウス座標が取得できないため独自に計算する」）。
    ///
    /// ★★ <b><c>⌃ + 左クリック</c>も受け付けない。</b> macOS の慣習ではあるが、
    ///   <b>非アクティブなアプリへの最初の左クリックはアクティブ化に食われる</b>ので、
    ///   常駐マスコットでは必ず2回クリックが要る。右クリックはアクティブ化しないのでそのまま届く。
    ///   <b>押しても何も起きない操作を残さない</b>（→ <c>SettingsSchema</c> と同じ方針）。
    ///   副ボタンを出せない環境の逃げ道は<b>メニューバーの「設定を開く…」</b>で足りている。
    ///
    /// ★★ <b>押下はイベント、位置はクリック透過の状態、と分けること。</b> どちらか片方では足りない:
    ///   <list type="bullet">
    ///     <item><b>押下をポーリングで取ると短いタップを落とす。</b>
    ///       <c>UniWindowController.GetMouseButtons()</c> は <c>NSEvent.pressedMouseButtons</c> を
    ///       毎フレーム覗くだけなので、30fps（<c>MascotRunner.targetFrameRate</c>）では
    ///       <b>1フレーム（33ms）より短い押下が丸ごと消える</b>。トラックパッドの2本指タップは
    ///       まさにそれで、実測でも合成した 60ms の右クリックを 1/4 しか拾えなかった</item>
    ///     <item><b>位置をイベントから取ると古い座標を掴む</b>（→ 上記）</item>
    ///   </list>
    ///   イベント側の取りこぼしに備えてポーリングも併せて見るが、<b>同じ押下で2回開閉しない</b>
    ///   ように押しっぱなし扱いにして畳む。
    ///
    /// ★ <b>当たり判定はクリック透過の状態をそのまま使う</b>（<c>isClickThrough == false</c>）。
    ///   これは<b>グローバルなカーソル座標</b>から毎フレーム計算されていて（→ 上記）、
    ///   フォーカスに依存しない。<b>掴める領域と右クリックできる領域が定義上ずれない</b>のも
    ///   同じ判定を使うからで、コライダーを自分で数える必要が無い。
    ///
    /// ★ <b>既知の穴。</b> 設定パネルがキャラクターに重なっているとき、
    ///   <b>パネルの上での右クリックでも閉じる</b>（マスコット側から見ると
    ///   「不透明な画素の上で右ボタンが押された」と区別がつかない）。
    ///   パネルには右クリックで何かが出る部品が無いので実害は「閉じる」だけ。
    ///   潰すにはネイティブ側にパネルの矩形を問い合わせる口が要るので、割に合わないと判断した。
    /// </summary>
    [DisallowMultipleComponent]
    internal sealed class MascotContextClick : MonoBehaviour
    {
        private UniWindowController _controller;
        private bool _wasDown;

        private void Start()
        {
            _controller = Object.FindFirstObjectByType<UniWindowController>();
            if (_controller == null) { enabled = false; return; }
            Debug.Log("[Mascot] 右クリックを見張ります");
        }

        private void Update()
        {
            // ★ 新しい入力系から取れるのは「押されたか」だけ。座標は当てにしない（→ 型の doc）
            var mouse = Mouse.current;
            var buttons = UniWindowController.GetMouseButtons();
            var downNow = (buttons & UniWindowController.MouseButton.Right) != UniWindowController.MouseButton.None;

            var pressed = (mouse != null && mouse.rightButton.wasPressedThisFrame) || (downNow && !_wasDown);
            _wasDown = downNow || pressed;
            if (!pressed) return;

            // ★ 左ドラッグ中は見送る。掴んでいる間はヒットテストが止まっていて
            //   isClickThrough が古い（UniWindowController が isHitTestEnabled を落とす）
            if ((buttons & UniWindowController.MouseButton.Left) != UniWindowController.MouseButton.None) return;
            if (!_controller.isHitTestEnabled) return;

            // ★ 不透明な画素の上＝キャラクターの上。窓の外なら true のままなのでここで弾かれる
            if (_controller.isClickThrough) return;

            StatusItemBridge.ToggleSettings();
        }
    }

    /// <summary>
    /// 右クリックの見張りをシーンへ据える配線。
    ///
    /// ★★ <b><c>MonoBehaviour</c> をシーンに焼かないこと。</b> シーンは
    ///   <c>m_Script</c> の GUID を asmdef の <c>includePlatforms</c> と無関係に
    ///   シリアライズするので、Android ではこのアセンブリが無く
    ///   「The referenced script on this Behaviour is missing!」が1本出るだけになる
    ///   （→ <c>docs/mascot.md</c>）。
    ///
    /// ★ <b>VRM の読み込みを待たない。</b> 判定はクリック透過の状態だけを見るので、
    ///   モデルが差し替わっても付け直しが要らない（以前はコライダー1つ1つに付けていた）。
    /// </summary>
    public static class ContextClickBinder
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bind()
        {
            // ★ ウィンドウ制御が居ないシーンでは据えない（TransparencyProbe など）
            if (Object.FindFirstObjectByType<UniWindowController>() == null) return;

            var go = new GameObject("MascotContextClick");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<MascotContextClick>();
        }
    }
}
#endif
