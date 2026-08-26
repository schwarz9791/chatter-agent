using System;
using UnityEngine;

namespace ChatterMascot
{
    /// <summary>
    /// フレームレートの上限を「戻す先」と「一時的に借りる」に分ける。
    ///
    /// VRM の読み込み中だけ上限を上げたい（<c>RuntimeOnlyAwaitCaller</c> の予算が
    /// 1ms/frame なので、30fps だと壁時計で長引き、その間メインスレッドが詰まって
    /// <b>サーバーの ping に pong を返せず接続が切れる</b>）。だが素朴な
    /// save/restore では静かに壊れる。
    ///
    /// ★ <b><c>Application.targetFrameRate</c> を読んで戻す設計にしないこと。</b>
    ///   読む側が <see cref="MascotRunner"/> の <c>Awake</c> より先に走った瞬間、
    ///   保存されるのは Unity 既定の <b>-1（無制限）</b>になり、読み込みが終わった
    ///   ところでそれを復元して<b>上限が恒久的に消える</b>。今は <c>Start()</c> から
    ///   蹴っているので成立しているが、誰かが <c>Awake()</c> に移せば壊れる。
    ///   これは「Cube 1個のシーンで CPU 261% / GPU 93.5%」（実測）の再来で、
    ///   常駐アプリなので気づくまでが長い（→ <c>docs/mascot.md</c>）。
    ///
    /// ★ <b><see cref="MascotRunner"/> のシリアライズ値に戻す設計にもしないこと。</b>
    ///   あれは「希望値」であって「今効いている値」ではない。#25 で Android XR が
    ///   ヘッドセットのリフレッシュレートに合わせたら、30 に戻すのは誤りになる。
    ///
    /// だから<b>戻す先の宣言を1箇所（<see cref="SetBaseline"/>）に集める</b>。
    /// </summary>
    public static class FrameRateBudget
    {
        private const int Unlimited = -1;

        private static int _baseline = Unlimited;
        private static int _depth;

        /// <summary>
        /// ★ <b>Enter Play Mode without domain reload に備えて static を明示的に戻す。</b>
        ///   残っていると、前回の Play で借りたままの深さを引き継いで二度と baseline に戻らない。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _baseline = Unlimited;
            _depth = 0;
        }

        /// <summary>
        /// 戻す先を宣言する。★ <b>呼び出し元は <see cref="MascotRunner"/> だけ。</b>
        /// </summary>
        public static void SetBaseline(int frameRate)
        {
            _baseline = frameRate > 0 ? frameRate : Unlimited;
            // 誰も借りていないときだけ即座に効かせる。借りている最中に baseline を
            // 変えられても、返却時にはこの新しい値へ戻る
            if (_depth == 0) Application.targetFrameRate = _baseline;
        }

        /// <summary>
        /// 借りている間だけ上限を上げる。★ <b>必ず <c>using</c> で使うこと。</b>
        ///
        /// 借り主が複数いてもよい（#59 の VRMA の読み込みが重なる）。
        /// <b>最後の1人が返したときだけ</b> baseline へ戻る —— 素朴な save/restore だと
        /// 2本目が「120」を保存して、そこへ戻してしまう。
        /// </summary>
        public static IDisposable Boost(int frameRate)
        {
            // baseline より低い/等しい「上げ方」は上げていないので、借りたことにしない
            if (frameRate <= 0 || (_baseline > 0 && frameRate <= _baseline)) return Handle.NoOp;

            _depth++;
            Application.targetFrameRate = frameRate;
            return new Handle();
        }

        /// <summary>いま誰かが借りているか。診断とテスト用。</summary>
        public static bool IsBoosted => _depth > 0;

        private sealed class Handle : IDisposable
        {
            public static readonly IDisposable NoOp = new Handle { _released = true };

            private bool _released;

            /// <summary>
            /// ★ <b>冪等にすること。</b> <c>finally</c> と <c>OnDisable</c> の両方から来る
            ///   （<c>finally</c> の継続は、ドメインリロードやシーン破棄では走らない）。
            /// </summary>
            public void Dispose()
            {
                if (_released) return;
                _released = true;

                if (--_depth <= 0)
                {
                    _depth = 0;
                    Application.targetFrameRate = _baseline;
                }
            }
        }
    }
}
