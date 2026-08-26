using Kirurobo;
using UnityEngine;

namespace ChatterMascot.Desktop
{
    /// <summary>
    /// 枠なし化でウィンドウが<b>起動のたびに縦へ伸びるのを止める</b>。
    ///
    /// ★ <b>これが「いつのまにか窓が縦長になっている」の正体だった。</b> 実測（macOS 26.6.2）:
    ///
    /// 1. Unity が前回終了時のクライアント高さを復元する（<c>Screenmanager Resolution Height</c>）
    /// 2. <c>UniWindowController</c> が枠なし化し、<b>タイトルバーぶん（32）がクライアント領域へ編入される</b>
    ///    —— ウィンドウの外形は縮まないので、クライアントは 32 大きくなる
    /// 3. その大きくなった値が終了時に永続化される
    /// 4. 次の起動で 1 に戻る
    ///
    /// 250x400 → 432 → 464 と、**起動ごとに +32 で伸び続ける**。
    /// 既定を 600x800 にしていた頃に 600x1632 まで育っていた。
    ///
    /// ★ <b>「起動直後の大きさ」を正とする。</b> 1 で復元された値は、ユーザーが最後に
    ///   意図した大きさ（あるいは Player Settings の既定）なので、
    ///   <b>2 が足したぶんだけを取り消せばよい</b>。
    ///
    /// ★ <b><see cref="MonoBehaviour"/> をシーンに置かないこと</b>（→ <see cref="VrmDragHandleBinder"/>）。
    ///   Android ではこのアセンブリごと存在しないので、何も起きない。
    ///
    /// ★ <b>大きさの UI と位置の永続化は #16。</b> ここでやるのは累積の打ち消しだけ。
    /// </summary>
    public static class WindowSizeKeeper
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            // Editor では枠なし化そのものが起きない（UniWindowController の制限）
            if (Application.isEditor) return;
            if (Object.FindFirstObjectByType<UniWindowController>() == null) return;

            var go = new GameObject(nameof(WindowSizeKeeper)) { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(go);
            go.AddComponent<Keeper>();
        }

        private sealed class Keeper : MonoBehaviour
        {
            /// <summary>
            /// 見張る時間。枠なし化は起動直後の数フレームで起きるが、
            /// ★ <b>短くしすぎないこと</b> —— VRM の読み込みでメインスレッドが詰まると
            /// 実時間では後ろへずれる。長い側の害は「起動直後に手で広げても戻される」だけ。
            /// </summary>
            private const float WatchSeconds = 5f;

            private UniWindowController _controller;
            private Vector2Int _intended;
            private float _deadline;

            private void Start()
            {
                _controller = FindFirstObjectByType<UniWindowController>();
                // ★ Start は全 Awake の後・最初の Update の前。枠なし化はこの後に来るので、
                //   ここで見えているのが「復元された大きさ」
                _intended = new Vector2Int(Screen.width, Screen.height);
                _deadline = Time.realtimeSinceStartup + WatchSeconds;
            }

            private void LateUpdate()
            {
                if (_controller == null || Time.realtimeSinceStartup > _deadline)
                {
                    Destroy(gameObject);
                    return;
                }

                if (Screen.width == _intended.x && Screen.height == _intended.y) return;

                // ★ 縮んだ側は追いかけない。ユーザーが手で小さくしたのを戻すと操作を奪う。
                //   打ち消したいのは「勝手に増える」ぶんだけ
                if (Screen.width <= _intended.x && Screen.height <= _intended.y)
                {
                    Destroy(gameObject);
                    return;
                }

                // ★ **px と pt を混ぜないこと。この型で唯一の難所。**
                //   `macRetinaSupport: 1` なので `Screen.*` は**バッキングストアの px**、
                //   `UniWindowController.windowSize` は `LibUniWinC.SetSize` ＝ NSWindow の
                //   フレーム＝**ポイント**。Retina 2x でそのまま渡すと**2倍のウィンドウ**になり、
                //   打ち消すはずが 250x400 → 500x800 → 1000x1600 と**起動ごとに倍**へ育つ。
                //   scale 1 の外部ディスプレイでは px == pt なので**この症状は出ない**
                //   （最初の実測をそこで取ったため見落とした。→ docs/mascot.md）。
                //
                // ★ **`clientSize` で「意図した大きさ」を読み直す形にはできない。**
                //   `UniWinCore.AttachMyWindow` は `UniWindowController.Update()` の中で、
                //   枠なし化も**同じ `Update()` の中**。だから `Start()` では (0,0) で、
                //   最初の `LateUpdate` では**もう膨らんでいる**。捕まえられるのは
                //   `Start()` の `Screen.*` だけ。
                //
                // ★ **`Screen.SetResolution` に寄せない。** styleMask が戻った場合、
                //   `UniWindowController` は `IsActive` が立っている限り**枠を剥がし直さない**
                //   ので、タイトルバーが出たまま常駐する（+32 が残るより悪い）。
                //
                // ★ **換算率は決め打ちにせず、コントローラ自身から実測する。** `clientSize` は
                //   `Screen.*` と同じ矩形を pt で返すので、比がそのまま backingScaleFactor になる。
                //   `#if UNITY_STANDALONE_OSX` も `Screen.dpi` も要らない
                //   （→ `VrmOrientation` と同じ「仕様を読まず実測する」形）。
                var client = _controller.clientSize;

                // ★ **測れないなら何もしない。** scale=1 で代用すると Retina で倍にする。
                //   「+32 が残る」より「倍になる」ほうがはるかに悪い
                if (client.x <= 0f || client.y <= 0f)
                {
                    Debug.LogWarning("[Mascot] ウィンドウの大きさを測れないので直しません: " +
                                     $"Screen={Screen.width}x{Screen.height}px clientSize={client}");
                    Destroy(gameObject);
                    return;
                }

                // ★ **比は丸めること。** 枠なし化の直後は `Screen.*` と `clientSize` の更新が
                //   1フレームずれて 464/200 のような半端な値が出る。macOS の
                //   backingScaleFactor は 1 か 2 なので、丸めれば吸収できる
                var scale = Mathf.Max(1f, Mathf.Round(Screen.height / client.y));

                // ★ **生の数値を全部出すこと。** これが Retina での実測の唯一の観測点。
                //   内蔵パネルで scale=1 と出たら「clientSize も px」＝この前提ごと間違い
                Debug.Log("[Mascot] 枠なし化で広がったウィンドウを戻します: " +
                          $"{Screen.width}x{Screen.height}px → {_intended.x}x{_intended.y}px " +
                          $"(clientSize={client.x}x{client.y}pt scale={scale})");
                _controller.windowSize = new Vector2(_intended.x / scale, _intended.y / scale);
                Destroy(gameObject);
            }
        }
    }
}
