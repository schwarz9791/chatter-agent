using UnityEngine;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// 振幅エンベロープ（生の RMS）を、口の weight（0..1）へ整形する。
    /// <b>純粋。時計と <c>deltaTime</c> は引数で受け取る。</b>
    ///
    /// ★ <b>ここを <c>MonoBehaviour</c> のフィールドとして書かないこと</b>（<see cref="FaceLatch"/> と
    ///   同じ理由）。<c>ChatterMascot.Tests.asmdef</c> は <c>ChatterMascot.Runtime</c> しか
    ///   参照しないので、<c>VrmCharacter</c> に書いた時点で<b>テストが1行も当たらない</b> ——
    ///   しかも区間の始点・ゲイン・減衰は、まさに間違えると静かに壊れる場所。
    ///
    /// ★ <b>区間の始点をここが持つのは、<c>MascotRunner.Mouth</c> を冪等に保つため。</b>
    ///   ドライバ側に持たせると「2回呼ぶと違う値が返る」API になり、呼び出し元の数に
    ///   依存する。さらに <c>MascotRunner.Update</c> は <c>VrmCharacter.LateUpdate</c> より
    ///   前の位相なので、そちらで進めると区間が半フレームずれる。
    ///
    /// ★ <b>ゲインと減衰を <see cref="FacePolicy"/> に入れないこと。</b> <see cref="FaceParams"/> は
    ///   「0 = 無効」で統一されている（<c>PromptSurpriseWeight</c> も <c>BlinkSuppressAboveHappy</c> も）
    ///   が、ゲインはその語彙に乗らない —— 入れると
    ///   <c>FacePolicyTests.AllZeroParamsMakeEvaluateEqualTarget</c> が固定している
    ///   「<see cref="FaceParams"/> を全部 0 にすると <c>Evaluate</c> は <c>Target</c> と一致する」が
    ///   壊れる（<c>gain = 0</c> で口が常に閉じる）。
    /// </summary>
    public sealed class MouthTracker
    {
        /// <summary>
        /// <see cref="From"/> が返す区間の上限（秒）。
        ///
        /// ★ <b>これは緩和策ではなく不変条件の回復。</b> <c>SpeakingSet.Mouth</c> の doc が言う区間は
        ///   「前の<b>表示</b>フレームから今まで」であって「前に描画した時刻から今まで」ではない。
        ///   ハング・Editor の Pause・GameObject の非アクティブ化・オクルージョンによる間引きでは
        ///   <see cref="_prevSampledAt"/> が秒単位で取り残され、復帰フレームで<b>数秒ぶんの
        ///   エンベロープの最大</b>（＝実際の振幅と無関係に ~1.0）を返してしまう。
        ///
        /// ★ <b>0.2 秒の根拠は両側から挟んで決めた。</b> 下限側は実フレーム間隔
        ///   （30fps で 33.3ms。GC ヒッチで 2〜3 倍は普通）、上限側は「区間の最大が音節をまたいで
        ///   口が開きっぱなしに寄る」こと（日本語は 7〜8 モーラ/秒 ≒ 130ms/モーラ）。
        ///   0.1 だと 10fps で余裕が消える。
        /// ★★ <b><c>targetFrameRate</c> を 5fps 近くまで下げるならここも上げること。</b>
        ///   1フレーム間隔がこれを超えると、区間が切られて<b>エンベロープを読み飛ばす</b>。
        /// ★ <b><c>deltaTime</c> から動的に導かないこと。</b> <c>Time.unscaledDeltaTime</c> が
        ///   <c>Time.maximumDeltaTime</c> でクランプされるかは Unity のバージョンに依存する。
        /// </summary>
        public const double MaxSpanSeconds = 0.2;

        /// <summary>前回サンプルした時刻。<b>まだ一度も読んでいなければ <c>NaN</c>。</b></summary>
        private double _prevSampledAt = double.NaN;

        private float _weight;

        /// <summary>直近の出力（テストと診断用）</summary>
        public float Weight
        {
            get { return _weight; }
        }

        /// <summary>
        /// 区間の始まり。<b>初回は <paramref name="now"/> と同値</b>（＝点サンプル1回だけ）。
        /// 前回から <see cref="MaxSpanSeconds"/> 以上空いていたら、そこまで切り上げる。
        ///
        /// ★★ <b><c>double.NegativeInfinity</c> で初期化しないこと。</b>
        ///   <c>Mouth(-∞, now)</c> は<b>エンベロープ全体を走査して全体最大を返す</b>ので、
        ///   最初のフレームで口が全開に飛ぶ。1フレームぶんの点サンプルなら実害ゼロ。
        /// ★ <b>上限はその失敗と同じ根を塞ぐ。</b> 初回だけでなく、フレームが来なかった後の
        ///   復帰フレームでも同じことが起きる（→ <see cref="MaxSpanSeconds"/>）。
        /// </summary>
        public double From(double now)
        {
            if (double.IsNaN(_prevSampledAt)) return now;

            var oldest = now - MaxSpanSeconds;
            return _prevSampledAt < oldest ? oldest : _prevSampledAt;
        }

        /// <summary>
        /// 1フレーム分進める。
        ///
        /// ★ <b>attack は即時、release だけ減衰。</b> 立ち上がりを鈍らせると口の応答が遅れるが、
        ///   落ちるほうを素通しにすると 30fps では音素の谷ごとに口が閉じて階段に見える。
        ///   非対称なので指数緩和（<c>GazeAim.Smooth</c>）ではなく、秒あたりの一定量で落とす
        ///   —— <c>* deltaTime</c> があるのでフレームレート非依存性は保たれる。
        /// ★ <paramref name="gain"/> は cc-mascot の <c>min(1.0, rms * 4)</c> と同じ役割。
        ///   <b>0 にすると口が動かない</b>（「0 = 無効」ではないので注意）。
        /// </summary>
        /// <param name="rawRms"><c>MascotRunner.Mouth</c> が返す生の RMS</param>
        public float Tick(float rawRms, double now, float gain, float releasePerSecond, float deltaTime)
        {
            _prevSampledAt = now;

            var target = Mathf.Clamp01(rawRms * gain);

            if (releasePerSecond > 0f && deltaTime > 0f)
            {
                var decayed = _weight - releasePerSecond * deltaTime;
                if (decayed > target) target = decayed;
            }

            _weight = Mathf.Clamp01(target);
            return _weight;
        }
    }
}
