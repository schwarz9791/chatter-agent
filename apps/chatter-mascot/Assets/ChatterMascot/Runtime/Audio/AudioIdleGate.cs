namespace ChatterMascot.Audio
{
    /// <summary><see cref="AudioIdleGate"/> が返す指示。</summary>
    public enum IdleAction
    {
        None,

        /// <summary>オーディオ出力デバイスを手放してよい</summary>
        Suspend,

        /// <summary>掴み直すこと</summary>
        Resume,
    }

    /// <summary>
    /// 「喋っていない期間」を判定する。<b>純粋</b>（時計もオーディオも触らない）。
    ///
    /// ★ <b>これを作るまで、アイドルはコードのどこにも存在しなかった。</b>
    ///   <c>PlaybackQueue.HeadItem(state) == null</c> と <c>ActiveCount == 0</c> の
    ///   副次的な帰結としてしか観測できず、名前が無かった。デバイスを手放す判断は
    ///   間違えると<b>孤児の音が凍る</b>（契約: 採番のやり直しで孤児になった音は
    ///   最後まで鳴らし切る）ので、テストで固定できる形に切り出してある。
    ///
    /// ★ <b><c>PlaybackQueue</c> には手を入れない。</b> <c>PlaybackState.Items</c> /
    ///   <c>.Orphans</c> は public なのでドライバから<b>読むだけ</b>で足りる。
    ///   状態機械にコマンドを増やすと、EditMode テストのコマンド列比較が全部壊れる。
    /// </summary>
    public sealed class AudioIdleGate
    {
        private readonly long _suspendAfterMs;
        private bool _suspended;

        /// <summary>仕事が無くなった時刻。まだ測っていなければ -1。</summary>
        private long _idleSince = -1;

        /// <param name="suspendAfterMs">
        /// 仕事が無い状態がこれだけ続いたら手放す。
        ///
        /// ★ <b>短くしすぎないこと。</b> 文と文の間で往復すると、Bluetooth では
        ///   A2DP の張り直しが毎文入って<b>かえって悪化する</b>。
        ///   長すぎる害は省電力が薄れるだけ（無害側）。
        /// </param>
        public AudioIdleGate(long suspendAfterMs)
        {
            _suspendAfterMs = suspendAfterMs;
        }

        /// <summary>キルスイッチ。<c>false</c> にすると手放さない（掴んでいれば掴み直す）。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>今デバイスを手放しているか。</summary>
        public bool IsSuspended
        {
            get { return _suspended; }
        }

        /// <summary>
        /// 「これから鳴らす予定がある」と告げる。<b>再生の直前ではなく、音声を取りに行く時点で呼ぶ</b>。
        ///
        /// ★ そこで呼ぶと、デバイスの掴み直しが<b>サーバー側の合成待ちの裏に隠れる</b>。
        ///   <c>GET /audio/…</c> はサーバーに合成させるので数百ms〜数秒かかり、
        ///   先読みのぶんだけ再生よりさらに手前で走る。
        /// </summary>
        public IdleAction NoteWorkIncoming(long now)
        {
            _idleSince = -1;
            if (!_suspended) return IdleAction.None;
            _suspended = false;
            return IdleAction.Resume;
        }

        /// <param name="activeVoices">今鳴っている本数（<b>孤児を含む</b>）</param>
        /// <param name="itemsInFlight">キューに残っている件数（<c>Items</c> + <c>Orphans</c>）</param>
        public IdleAction Tick(long now, int activeVoices, int itemsInFlight)
        {
            if (!Enabled) return Wake();

            // ★ 鳴っている最中は絶対に手放さない。孤児を鳴らし切る契約の防衛線
            // ★ Items が残っているのは「合成待ちで、まもなく鳴る」状態。ここで手放すと
            //   掴み直しが再生に間に合わず1文目の頭が切れる
            if (activeVoices > 0 || itemsInFlight > 0)
            {
                _idleSince = -1;
                return Wake();
            }

            if (_suspended) return IdleAction.None;

            if (_idleSince < 0)
            {
                _idleSince = now;
                return IdleAction.None;
            }

            if (now - _idleSince < _suspendAfterMs) return IdleAction.None;

            _suspended = true;
            return IdleAction.Suspend;
        }

        private IdleAction Wake()
        {
            if (!_suspended) return IdleAction.None;
            _suspended = false;
            return IdleAction.Resume;
        }
    }
}
