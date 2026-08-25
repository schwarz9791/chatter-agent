using System;
using System.Threading.Tasks;

namespace ChatterMascot.Audio
{
    /// <summary>
    /// 発話1件を鳴らす実体。<b>順序の判断は持たない</b>
    /// （→ <c>ChatterMascot.Playback.PlaybackQueue</c> が head しか再生しない）。
    ///
    /// ★ <b>なぜインターフェースにするか。</b> Unity 内蔵オーディオは<b>無音でも OS の
    ///   出力デバイスを掴み続ける</b>。実測（macOS 26.6.2）では、サーバーに繋がらない
    ///   ビルドで <c>AudioSource.Play()</c> を一度も呼ばなくても、起動から終了まで
    ///   <c>kAudioProcessPropertyIsRunningOutput</c> が 1 のままだった。Bluetooth では
    ///   A2DP リンクが張られたままになり、イヤホンの電池を食う。
    ///
    ///   Unity 側に解放する API は無い（<c>Enable Output Suspension</c> は Editor 専用、
    ///   <c>AudioSettings.Reset()</c> は再初期化であって解放ではない、
    ///   <c>AudioListener.pause</c> は DSP を止めるだけ）。**エンジンごと差し替えられる**
    ///   ようにしてあるのはそのため。
    /// </summary>
    public interface ISpeechPlayer
    {
        /// <summary>診断。<c>MascotRunner</c> が <c>Debug.LogWarning</c> に繋ぐ。</summary>
        event Action<string> Warn;

        /// <summary>
        /// WAV を再生できるハンドルにする。読めなければ <c>null</c> と <paramref name="error"/>。
        ///
        /// ★ <b>これが <c>PlaybackState.Audio</c>（<c>object</c>）に入る値を作る唯一の場所。</b>
        ///   状態機械は中身を知らない不透明なハンドルとして扱うので、実装ごとに違う型でよい。
        /// </summary>
        object Prepare(byte[] wav, string name, out string error);

        /// <summary>
        /// 鳴らし終えたら戻る。例外は投げず、失敗の理由を返す（<c>null</c> なら成功）。
        /// 呼び出し側は成功も失敗も同じ経路（<c>Played</c> / <c>PlaybackFailed</c>）へ落とす。
        /// </summary>
        Task<string> PlayAsync(object audio);

        /// <summary>
        /// 使い終わった（あるいは捨てる）ハンドルを解放する。
        /// <c>DiscardAudio</c> コマンドの実体。
        /// </summary>
        void Discard(object audio);

        /// <summary>今鳴っている音を全部止める（終了処理用）。</summary>
        void StopAll();

        /// <summary>
        /// 今この瞬間に鳴っている本数（<b>孤児を含む</b>）。
        ///
        /// ★ アイドル判定の一次情報。<b>0 でなければ出力デバイスを手放してはいけない</b> ——
        ///   採番のやり直しで孤児になった音は「最後まで鳴らし切る」契約なので、
        ///   鳴っている最中に手放すとその音が凍る。
        /// </summary>
        int ActiveCount { get; }

        /// <summary>
        /// オーディオ出力デバイスを手放す。持たない実装は no-op でよい。
        ///
        /// ★ <b><see cref="ActiveCount"/> が 0 のときだけ呼ぶこと。</b>
        /// ★ <b><see cref="ResumeOutput"/> と同じスレッドから呼ぶこと</b>（Unity のメインスレッド）。
        /// </summary>
        void SuspendOutput();

        /// <summary>
        /// 出力デバイスを掴み直す。<b>べき等</b>。
        ///
        /// ★ 再生の直前ではなく<b>音声を取りに行く時点</b>で呼ぶこと。デバイスの掴み直しは
        ///   Bluetooth だと A2DP の張り直しで時間がかかるが、その裏でサーバー側の合成が
        ///   走っているので、待ち時間に隠れる。
        /// </summary>
        void ResumeOutput();
    }
}
