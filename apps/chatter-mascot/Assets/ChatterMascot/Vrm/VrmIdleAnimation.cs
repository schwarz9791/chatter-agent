using System;
using System.Threading;
using System.Threading.Tasks;
using UniGLTF;
using UnityEngine;
using UniVRM10;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// 同梱 <c>.vrma</c>（VRMA）を読み込んで再生する。
    ///
    /// <b><c>MonoBehaviour</c> ではない、素の <see cref="IDisposable"/>。</b> <see cref="VrmCharacter"/> が所有する。
    ///
    /// 読み込みの流儀は <see cref="VrmStage.LoadAsync"/> と同じ:
    /// 候補を1件ずつ試し、<b>パースまで通って初めて確定</b>する。失敗は <c>LogWarning</c> で
    /// 次の候補へ進み、<b>全滅は1本だけログを出す</b>。
    ///
    /// ★ <b>全滅は <c>LogError</c> にしない。</b> このリポジトリの一般則（「全滅だけ <c>LogError</c>」）の
    ///   例外。VRM 本体が読めない <see cref="VrmStage"/> と違い、VRMA が無くても
    ///   手続き的アイドル（<see cref="ChatterMascot.Vrm.IdlePose"/>）へ正常にフォールバックできる。
    ///   <c>-vrma</c> を指定せず同梱も消した構成は<b>正常な分岐</b>。
    /// </summary>
    public sealed class VrmIdleAnimation : IDisposable
    {
        /// <summary>
        /// 読み込み中だけ上げるフレームレート。<see cref="VrmStage"/> と同じ 120。
        ///
        /// ★ <see cref="FrameRateBudget.Boost"/> は多重に借りられる（VRM 本体と VRMA が
        ///   同時に読み込み中でも、最後の1人が返すまで上限は戻らない）。
        /// </summary>
        private const int LoadFrameRate = 120;

        private Vrm10Instance _target;
        private GameObject _instanceRoot;
        private Vrm10AnimationInstance _animation;
        private bool _disposed;
        private bool _enabled = true;

        /// <summary>
        /// VRMA を再生中か。<c>false</c> のままなら、呼び出し側（<see cref="VrmCharacter"/>）は
        /// 手続き的アイドルへフォールバックする。
        /// </summary>
        public bool IsPlaying { get; private set; }

        /// <summary>
        /// 待機モーションを流すか（設定パネル / #76）。
        ///
        /// ★★ <b>設定の「待機モーション」は VRMA と手続き的アイドルの<u>両方</u>を止める。</b>
        ///   ユーザーから見て「待機モーション」は1つの概念で、2実装は
        ///   「片方が読めないときのフォールバック」でしかない。ここだけ止めて
        ///   <c>proceduralIdle</c> を放置すると、**チェックを外したのに動き続ける**。
        ///
        /// ★ <b><c>Dispose</c> で止めないこと。</b> 破棄すると読み込み直しが要る。
        ///   <c>Runtime.VrmAnimation</c> を外すだけなら、戻すのも1行で済む。
        ///
        /// ★ <b>止めた姿勢はその場で固まる。</b> ニュートラルへ戻す処理は入れていない ——
        ///   「待機モーションを止める」の意味として、最後の姿勢のまま静止するのは素直な帰結で、
        ///   戻す先（T ポーズ）の方がむしろ不自然に見える。
        /// </summary>
        public bool Enabled
        {
            get { return _enabled; }
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                Apply();
            }
        }

        private void Apply()
        {
            if (_disposed || _target == null || _target.Runtime == null || _animation == null) return;

            _target.Runtime.VrmAnimation = _enabled ? _animation : null;

            // ★ Animation そのものも止める。外しただけだと、見えない VRMA を
            //   毎フレーム再生し続けることになる
            var animation = _instanceRoot != null ? _instanceRoot.GetComponent<Animation>() : null;
            if (animation != null) animation.enabled = _enabled;

            IsPlaying = _enabled;
        }

        /// <summary>
        /// ★ <b>例外は内側で全部握る。</b> <c>_ = idle.LoadAsync(...)</c> の形で投げっぱなしにされる
        ///   （<c>VrmCharacter.OnLoaded</c>）ので、ここから漏らすと未観測の Task として消え、
        ///   手続き的アイドルへ倒れた理由がどこにも残らない。
        /// </summary>
        public async Task LoadAsync(Vrm10Instance target, Transform parent, CancellationToken ct)
        {
            using (FrameRateBudget.Boost(LoadFrameRate))
            {
                try
                {
                    var env = AssetEnvFactory.Current();
                    var candidates = AssetPath.Enumerate(env, AssetKind.Vrma);

                    foreach (var candidate in candidates)
                    {
                        var loaded = await VrmAssetLoader.ReadAsync(candidate, ct);
                        if (loaded.IsEmpty) continue;

                        var vrma = await ParseAsync(loaded, ct);
                        if (vrma == null) continue;

                        // ★ await の後にキャンセルと破棄を再確認すること。ParseAsync が返るまでの間に
                        //   VrmCharacter.OnDisable（Cancel → Dispose → _idle = null）が走ると、
                        //   破棄済みのつもりのインスタンスへ VRMA が組み込まれる。Dispose は _disposed で
                        //   no-op になり、_idle は null なので所有者からも辿れず、VRMA の GameObject が
                        //   破棄済みモデルを駆動したままリークする。
                        // ★ ここで捨てるのは自分の責任。まだ Adopt していないので所有者が居ない
                        if (_disposed || ct.IsCancellationRequested)
                        {
                            UnityEngine.Object.Destroy(vrma.gameObject);
                            return;
                        }

                        Adopt(target, parent, vrma);
                        // ★ 採った候補を出すこと。VrmStage と同じ理由 ——
                        //   「いま何が再生されているか」がこの1行でしか分からない
                        Debug.Log($"[Mascot] VRMA の読み込み: {candidate}");
                        return;
                    }

                    // ★ VRMA が無いのは異常ではない。ここは Log 止まり（LogError にしない）
                    Debug.Log("[Mascot] 読めて VRMA として解釈できた候補が1つもなかったので、" +
                              "手続き的アイドルにフォールバックします。探した順:" +
                              VrmAssetLoader.DescribeCandidates(candidates));
                }
                catch (OperationCanceledException)
                {
                    // 終了経路。ログを出さない
                }
                catch (Exception e)
                {
                    // ★ 握ること。ここから漏らすと理由が残らない（VrmStage.LoadAsync と同じ理由）
                    Debug.LogError("[Mascot] VRMA の読み込みで例外が出ました: " + e);
                }
            }
        }

        /// <summary>
        /// バイト列を VRMA として解釈し、<c>Vrm10AnimationInstance</c> まで確定させる。
        /// <b>失敗しても投げず <c>null</c> を返す</b>（呼び出し側は普通の <c>continue</c> で次へ進める）。
        ///
        /// ★ <b><c>OperationCanceledException</c> だけは通すこと。</b> 握ると、終了時に
        ///   残りの候補を舐め直したうえで「1つも読めませんでした」と誤ったログを出す
        ///   （<see cref="VrmStage"/> の候補パースと同じ理由）。
        ///
        /// ★ <b>Animation コンポーネントの有無も、この時点（＝ <c>target.Runtime</c> へ組み込む前）で
        ///   確認する。</b> <see cref="Adopt"/> で組み込んでから気づくと、途中まで進めた配線
        ///   （<c>VrmAnimation</c> への代入・シーンへの reparent）を巻き戻す必要が出る。
        ///
        /// ★ <b>引数の <paramref name="ct"/> は本体で一度も参照しない。</b> <c>awaitCaller.Run(...)</c> も
        ///   <c>loader.LoadAsync(awaitCaller)</c> も <c>CancellationToken</c> を取らないため、
        ///   <b>キャンセル済みでもパースは最後まで走り切るのが常態</b>である。<see cref="LoadAsync"/> が
        ///   <c>Adopt</c> の直前で <c>_disposed</c> / キャンセルを再確認しているのは、狭い競合への
        ///   保険ではなく、<b>読み込み中に終了すれば普通に通る経路</b>だからである。
        /// </summary>
        private static async Task<Vrm10AnimationInstance> ParseAsync(LoadedBytes loaded, CancellationToken ct)
        {
            try
            {
                var awaitCaller = new RuntimeOnlyAwaitCaller();
                using (var data = await awaitCaller.Run(() =>
                           new GlbBinaryParser(loaded.Bytes, loaded.Candidate.Path).Parse()))
                {
                    var vrmaData = new VrmAnimationData(data);
                    using (var loader = new VrmAnimationImporter(vrmaData))
                    {
                        var instance = await loader.LoadAsync(awaitCaller);

                        var vrma = instance != null ? instance.GetComponent<Vrm10AnimationInstance>() : null;
                        if (vrma == null)
                        {
                            Debug.LogWarning($"[Mascot] {loaded.Candidate.Path} は VRMA として解釈できませんでした" +
                                              "（Vrm10AnimationInstance がありません）。次の候補へ進みます");
                            if (instance != null) UnityEngine.Object.Destroy(instance.gameObject);
                            return null;
                        }

                        if (vrma.GetComponent<Animation>() == null)
                        {
                            Debug.LogWarning($"[Mascot] {loaded.Candidate.Path} に Animation コンポーネントが" +
                                              "ありません。次の候補へ進みます");
                            UnityEngine.Object.Destroy(instance.gameObject);
                            return null;
                        }

                        return vrma;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Mascot] {loaded.Candidate.Path} は VRMA として解釈できませんでした: " +
                                  $"{e.Message}。次の候補へ進みます");
                return null;
            }
        }

        /// <summary>
        /// 確定した VRMA をモデルへ組み込む。
        ///
        /// ★ <b><c>ShowBoxMan(false)</c> を忘れると箱人間が画面に出る。</b>
        /// ★ <b><c>SetActive(false)</c> にしないこと。</b> <c>Animation</c> が止まる。
        ///   見えなくするのは <c>ShowBoxMan(false)</c> の側。
        /// </summary>
        private void Adopt(Vrm10Instance target, Transform parent, Vrm10AnimationInstance vrma)
        {
            vrma.ShowBoxMan(false);

            // ★ 弾かないこと（意図して表情アニメーションを乗せた VRMA かもしれない）。
            //   同梱の idle_loop.vrma は Count == 0 になるはず
            var expressionCount = vrma.ExpressionMap.Count;
            Debug.Log($"[Mascot] VRMA の ExpressionMap: {expressionCount} 件");
            if (expressionCount > 0)
            {
                Debug.LogWarning("[Mascot] この VRMA は表情アニメーションを持つので、emotion とリップシンクが上書きされます");
            }

            vrma.transform.SetParent(parent, false);

            target.Runtime.VrmAnimation = vrma;
            vrma.GetComponent<Animation>().Play();

            _target = target;
            _instanceRoot = vrma.gameObject;
            _animation = vrma;
            IsPlaying = true;

            // ★ 読み込みが終わる前に設定で止められていることがある（読み込みは非同期）。
            //   ここで一度適用しないと、チェックを外してあるのに再生が始まる
            Apply();
        }

        /// <summary>
        /// ★ <b><c>target.Runtime.VrmAnimation = null</c> を先にやってから <c>Destroy</c> すること。</b>
        ///   逆順だと <c>Vrm10Runtime.Process()</c> が破棄済みインスタンスの
        ///   <c>ControlRig</c> / <c>ExpressionMap</c> を触る。
        /// ★ <b>冪等にすること。</b> <c>FrameRateBudget.Handle.Dispose</c> と同じ理由
        ///   （<c>OnDisable</c> と <c>OnDestroy</c> の両方から来うる）。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_target != null && _target.Runtime != null)
            {
                _target.Runtime.VrmAnimation = null;
            }

            if (_instanceRoot != null) UnityEngine.Object.Destroy(_instanceRoot);
            _instanceRoot = null;
            _animation = null;
            _target = null;
            IsPlaying = false;
        }
    }
}
