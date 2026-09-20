using System;
using System.Collections.Generic;
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
                    // ★ #103。読み込み時に1回だけ検査する——フェード中の毎フレーム診断だと、
                    //   常駐アプリの寿命中ずっと provider を呼び出すコストが乗り続けるうえ、
                    //   FadeIn 側は to クリップが time≈0 なので原理的に終端の重複キーを捕まえない
                    WarnIfKeyframesAreNonIncreasing(data, loaded.Candidate.Path);

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
        /// <c>animations[].samplers[].input</c>（時刻キー）に単調増加でない箇所が無いかを検査し、
        /// あれば1ファイル1回だけ警告する（#103）。VRMA の末尾に重複キーがあると、クリップ長を
        /// 越えた評価で hips が非有限になる（<see cref="ClipEnd"/> の doc）——ファイルは直さず、
        /// 該当を持つことだけを起動時に知らせる。
        ///
        /// ★ <b>失敗しても読み込みを止めない。</b> 検査自体の例外は握って警告1行に落とす
        ///   （<see cref="ParseAsync"/> 本体と同じ方針。<c>OperationCanceledException</c> だけは通す）。
        /// ★ 複数の <c>sampler</c> が同じ <c>input</c> アクセッサを共有することがある
        ///   （全ボーンで同じ時刻配列を使い回す書き出し）ので、アクセッサ単位で重複読みを避ける
        ///   ——避けないと同じ重複キーを何十倍にも数えて報告することになる。
        /// </summary>
        private static void WarnIfKeyframesAreNonIncreasing(GltfData data, string path)
        {
            try
            {
                var animations = data.GLTF.animations;
                if (animations == null) return;

                var seenAccessors = new HashSet<int>();
                var nonIncreasing = 0;
                var keyCount = 0;

                foreach (var animation in animations)
                {
                    if (animation.samplers == null) continue;
                    foreach (var sampler in animation.samplers)
                    {
                        if (!seenAccessors.Add(sampler.input)) continue;

                        var times = data.GetArrayFromAccessor<float>(sampler.input);
                        nonIncreasing += VrmaKeyframes.CountNonIncreasing(times.ToArray());
                        keyCount += times.Length;
                    }
                }

                if (nonIncreasing > 0)
                {
                    Debug.LogWarning("[Mascot] " + VrmaKeyframes.Describe(path, nonIncreasing, keyCount));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Mascot] {path} の時刻キー検査に失敗しました: {e.Message}");
            }
        }
    }
}
