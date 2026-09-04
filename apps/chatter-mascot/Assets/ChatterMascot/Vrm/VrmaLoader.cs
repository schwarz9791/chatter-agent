using System;
using System.Threading;
using System.Threading.Tasks;
using UniGLTF;
using UnityEngine;
using UniVRM10;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// バイト列を VRMA として解釈し、<c>Vrm10AnimationInstance</c> まで確定させる。
    ///
    /// ★ #70 で <see cref="VrmIdleAnimation"/> の <c>ParseAsync</c> を本体ごとここへ移した。
    ///   同梱の待機モーション（<see cref="VrmIdleAnimation"/>）と、感情モーション / 小ネタの
    ///   プリロード（<see cref="VrmMotionPlayer"/>）の両方が同じパース処理を要るため。
    ///
    /// <b>失敗しても投げず <c>null</c> を返す</b>（呼び出し側は普通の <c>continue</c> で次へ進める）。
    ///
    /// ★ <b><c>OperationCanceledException</c> だけは通すこと。</b> 握ると、終了時に
    ///   残りの候補を舐め直したうえで「1つも読めませんでした」と誤ったログを出す
    ///   （<c>VrmStage</c> の候補パースと同じ理由）。
    ///
    /// ★ <b>Animation コンポーネントの有無も、この時点（＝呼び出し側が Runtime へ組み込む前）で
    ///   確認する。</b> 組み込んでから気づくと、途中まで進めた配線（<c>VrmAnimation</c> への代入・
    ///   シーンへの reparent）を巻き戻す必要が出る。
    ///
    /// ★ <b>引数の <paramref name="ct"/> は本体で一度も参照しない。</b> <c>awaitCaller.Run(...)</c> も
    ///   <c>loader.LoadAsync(awaitCaller)</c> も <c>CancellationToken</c> を取らないため、
    ///   <b>キャンセル済みでもパースは最後まで走り切るのが常態</b>である。呼び出し側
    ///   （<see cref="VrmIdleAnimation.LoadAsync"/> / <see cref="VrmMotionPlayer.LoadAsync"/>）が
    ///   組み込みの直前で <c>_disposed</c> / キャンセルを再確認しているのは、狭い競合への
    ///   保険ではなく、<b>読み込み中に終了すれば普通に通る経路</b>だからである。
    /// </summary>
    internal static class VrmaLoader
    {
        internal static async Task<Vrm10AnimationInstance> ParseAsync(LoadedBytes loaded, CancellationToken ct)
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
    }
}
