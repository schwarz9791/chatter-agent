using System;

namespace ChatterMascot.Window
{
    /// <summary>
    /// <see cref="WindowState"/> の永続化。<b>ファイル I/O は注入する。</b>
    ///
    /// ★ <b><c>Application.persistentDataPath</c> などをこの型の中で呼ばないこと</b>
    ///   （<c>AssetPath</c> / <c>AssetEnv</c> と同じ規律）。呼ぶと EditMode テストが
    ///   実マシンのパスを踏む導線が残る。
    ///
    /// ★ <b>読めなくても throw しない。</b> 設定ファイルが1文字壊れただけで
    ///   マスコットが出ないのは割に合わない。警告して「保存が無い」に倒す。
    /// </summary>
    public sealed class WindowStateStore
    {
        /// <summary>中身を返す。ファイルが無ければ <c>null</c>。読めなければ throw してよい。</summary>
        private readonly Func<string> _read;

        private readonly Action<string> _write;
        private readonly Action<string> _warn;

        public WindowStateStore(Func<string> read, Action<string> write, Action<string> warn)
        {
            _read = read;
            _write = write;
            _warn = warn;
        }

        public WindowState Load()
        {
            string raw;
            try
            {
                raw = _read();
            }
            catch (Exception e)
            {
                _warn?.Invoke("ウィンドウの保存を読めませんでした: " + e.Message);
                return WindowState.None;
            }

            // ★ ファイルが無いのは正常（初回起動）。警告しない
            if (raw == null) return WindowState.None;

            if (WindowStateJson.TryParse(raw, out var state, out var error)) return state;

            _warn?.Invoke($"ウィンドウの保存を読み飛ばします: {error}");
            return WindowState.None;
        }

        /// <summary>書けたら true。★ 失敗しても throw しない（終了処理からも呼ばれる）。</summary>
        public bool Save(WindowState state)
        {
            try
            {
                _write(WindowStateJson.Write(state));
                return true;
            }
            catch (Exception e)
            {
                _warn?.Invoke("ウィンドウの保存を書けませんでした: " + e.Message);
                return false;
            }
        }
    }
}
