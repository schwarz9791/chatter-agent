using System;
using System.Threading;
using System.Threading.Tasks;

namespace ChatterMascot.Audio
{
    /// <summary>
    /// 一時ミュートを実現する <see cref="ISpeechPlayer"/> のデコレータ。
    /// <b>音を出さないだけで、他は何も変えない。</b>
    ///
    /// ★★ <b><c>ack</c> は必ず出すこと。</b> <see cref="PlayAsync"/> が成功を返せば
    ///   <c>Played</c> → <c>Finish</c> → <c>ConsumeHead</c> → <c>EmitAck</c> の
    ///   <b>通常経路そのまま</b>が走る。止めるとキューが <c>speechQueueMaxEntries</c>（500）まで
    ///   溜まって古い方から捨てられ、解除後に<b>歯抜けで喋り出す</b>
    ///   （→ <c>docs/protocol.md</c> のクライアント側の責務2・3）。
    ///   <b><c>PlaybackQueue</c> には1行も触らないこと</b>（触ると EditMode のコマンド列比較が全部壊れる）。
    ///
    /// ★ <b>長さぶん待つ。</b> 即座に返すと、溜まっていた発話が数百 ms で全部消化されて
    ///   <b>表情が高速で切り替わる</b>。「声だけ消す」という決定は、実時間を消費して初めて成立する。
    ///
    /// ★ <b>口は別のところで止める。</b> ここは音の担当で、リップシンクは
    ///   <c>MascotRunner.BeginSpeaking</c> がエンベロープを落とすことで止める。
    ///   <c>_speaking</c> への登録そのものは飛ばさない —— 飛ばすと表情と体の動きまで止まる
    ///   （→ <see cref="SpeakingSet.Begin"/> の doc）。
    ///
    /// ★ <b><c>Prepare</c> は本物に委譲する。</b> WAV の検証もエンベロープ生成も走るので、
    ///   ミュートの有無で<b>ログの見え方が変わらない</b>。無音の原因を切り分けるとき、
    ///   ミュートかどうかで診断の出方が変わるのはいちばん困る。
    /// </summary>
    public sealed class MutedSpeechPlayer : ISpeechPlayer, IDisposable
    {
        private readonly ISpeechPlayer _inner;
        private readonly MuteState _mute;

        /// <summary>ミュート中に「鳴っている扱い」で待っている本数（→ <see cref="ActiveCount"/>）</summary>
        private int _waiting;

        private CancellationTokenSource _cancel = new CancellationTokenSource();
        private bool _disposed;

        public MutedSpeechPlayer(ISpeechPlayer inner, MuteState mute)
        {
            if (inner == null) throw new ArgumentNullException(nameof(inner));
            if (mute == null) throw new ArgumentNullException(nameof(mute));

            _inner = inner;
            _mute = mute;
            _mute.Changed += OnMuteChanged;
        }

        /// <summary>★ 中継するだけ。診断を握り潰さない</summary>
        public event Action<string> Warn
        {
            add { _inner.Warn += value; }
            remove { _inner.Warn -= value; }
        }

        /// <summary>
        /// ★ <b>待っている本数を足すこと。</b> 足さないと、ミュート中は本物の
        /// <c>ActiveCount</c> が常に 0 になり、<c>AudioIdleGate</c> が「鳴っていない」と判定して
        /// 出力デバイスを手放す。macOS は <c>CanSuspendOutput == false</c> なので無害だが、
        /// <b>Android では実際に手放してしまう</b>（→ #25）。
        /// </summary>
        public int ActiveCount
        {
            get { return _inner.ActiveCount + Volatile.Read(ref _waiting); }
        }

        public bool CanSuspendOutput
        {
            get { return _inner.CanSuspendOutput; }
        }

        public object Prepare(byte[] wav, string name, out string error)
        {
            return _inner.Prepare(wav, name, out error);
        }

        public async Task<string> PlayAsync(object audio)
        {
            // ★ 開始時点のミュートで決める。 再生の途中で切り替わっても、
            //   始まった1件は始まった形のまま終える（口の判断＝BeginSpeaking と揃える）
            if (!_mute.Muted)
            {
                var error = await _inner.PlayAsync(audio);

                // ★ 再生中にミュートされたなら、止めたのは自分（OnMuteChanged の StopAll）。
                //   失敗として数えると、押した本人に向かって警告が出る
                if (error != null && _mute.Muted) return null;
                return error;
            }

            var duration = audio as IAudioDuration;
            var durationMs = duration != null ? duration.DurationMs : 0;

            // ★ 0 は「長さ 0」ではなく「不明」。 待つ根拠が無いので待たない
            if (durationMs <= 0) return null;

            var token = _cancel.Token;
            Interlocked.Increment(ref _waiting);
            try
            {
                await Task.Delay(durationMs, token);
            }
            catch (OperationCanceledException)
            {
                // StopAll された。★ 失敗にしないこと —— ack が出なくなる
            }
            finally
            {
                Interlocked.Decrement(ref _waiting);
            }
            return null;
        }

        public void Discard(object audio)
        {
            _inner.Discard(audio);
        }

        public void StopAll()
        {
            CancelWaiting();
            _inner.StopAll();
        }

        public void SuspendOutput()
        {
            _inner.SuspendOutput();
        }

        public void ResumeOutput()
        {
            _inner.ResumeOutput();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _mute.Changed -= OnMuteChanged;
            CancelWaiting();
        }

        /// <summary>
        /// ★ <b>ミュートにした瞬間、鳴っているものを止める。</b> 押す動機は
        ///   「いま喋っているのを黙らせたい」なので、次の発話から効くのでは遅い。
        /// ★ 解除では何もしない（止めるものが無い）。
        /// </summary>
        private void OnMuteChanged(bool muted)
        {
            if (!muted) return;
            _inner.StopAll();
        }

        /// <summary>
        /// ★ <b><c>CancellationTokenSource</c> を <c>Dispose</c> しないこと。</b>
        ///   キャンセルした時点でまだ <c>Task.Delay</c> が token を掴んでおり、
        ///   捨てると <c>ObjectDisposedException</c> が
        ///   <b><c>_ = PlayAsync(...)</c> の未観測な Task の中で</b>起きる ——
        ///   つまりどこにも出ないまま <c>_speaking.End</c> だけが飛ぶ。
        ///   タイマーは <c>Cancel</c> で解放されるので、捨てないことの実害は無い。
        /// </summary>
        private void CancelWaiting()
        {
            var previous = _cancel;
            _cancel = new CancellationTokenSource();
            previous.Cancel();
        }
    }
}
