using System;
using System.Collections.Generic;

namespace ChatterMascot.Settings
{
    /// <summary>
    /// <see cref="MascotSettings"/> の永続化。<b>ファイル I/O は注入する</b>
    /// （<c>WindowStateStore</c> と同じ規律 —— <c>Application.*</c> をこの型の中で呼ばない）。
    ///
    /// <b>core の <c>createConfigStore</c> と同じ形にしてある:</b>
    /// <list type="number">
    ///   <item>読むたびに <c>mtime:size</c> のスタンプを比べ、<b>変わっていなければ読まない</b>
    ///     （watch はしない。設定 UI が別プロセスから書く未来にも耐える）</item>
    ///   <item>壊れた JSON では<b>直前値を維持する</b>（既定へ戻さない）</item>
    ///   <item>未知キー・不正値は<b>警告して無視／既定に倒す</b>。throw しない</item>
    ///   <item>同じ警告を繰り返さない（<c>warnOnce</c>）</item>
    /// </list>
    ///
    /// ★ <b>core との違いが1つある。</b> あちらは読み取り専用だが、こちらは<b>書く</b>
    ///   （ミュートの状態）。そして<b>読み直しに成功したら警告の履歴を捨てる</b> ——
    ///   ユーザーが設定ファイルを直して、また壊したときに黙っていては困る。
    /// </summary>
    public sealed class SettingsStore
    {
        /// <summary>中身を返す。ファイルが無ければ <c>null</c>。読めなければ throw してよい。</summary>
        private readonly Func<string> _read;

        /// <summary>
        /// <c>mtime:size</c>。ファイルが無ければ <c>null</c>（<b>それが正常</b>）。
        /// ★ 内容のハッシュにしないこと —— 読まずに済ませるための仕組みなのに読むことになる。
        /// </summary>
        private readonly Func<string> _stamp;

        private readonly Action<string> _write;
        private readonly Action<string> _warn;

        private readonly HashSet<string> _warned = new HashSet<string>();

        private MascotSettings _current = MascotSettings.Defaults;
        private string _stampSeen;
        private bool _loaded;

        public SettingsStore(
            Func<string> read, Func<string> stamp, Action<string> write, Action<string> warn)
        {
            _read = read;
            _stamp = stamp;
            _write = write;
            _warn = warn;
        }

        /// <summary>いまの値。<b>読むたびにファイルの更新を確かめる</b>（→ 型の doc）。</summary>
        public MascotSettings Current
        {
            get
            {
                Refresh();
                return _current;
            }
        }

        /// <summary>ファイルが変わっていれば読み直す。<b>変わったら true</b>。</summary>
        public bool Refresh()
        {
            string stamp;
            try
            {
                stamp = _stamp != null ? _stamp() : null;
            }
            catch (Exception e)
            {
                WarnOnce("stamp", "設定ファイルの更新を確かめられませんでした: " + e.Message);
                return false;
            }

            // ★ 初回は必ず読む。 ファイルが無い（stamp が null）ときも「読んだ」ことにして、
            //   後からファイルが作られたら拾えるようにする
            if (_loaded && stamp == _stampSeen) return false;

            _stampSeen = stamp;
            _loaded = true;

            string raw;
            try
            {
                raw = _read != null ? _read() : null;
            }
            catch (Exception e)
            {
                WarnOnce("read", "設定を読めませんでした: " + e.Message);
                return false;
            }

            // ★ ファイルが無いのは正常（初回起動）。警告しない
            if (raw == null)
            {
                var changed = !Equals(_current, MascotSettings.Defaults);
                _current = MascotSettings.Defaults;
                _warned.Clear();
                return changed;
            }

            MascotSettings parsed;
            string error;
            if (!SettingsJson.TryParse(raw, out parsed, out error, OnParseWarning))
            {
                // ★ 直前値を維持する（既定へ戻さない）。 編集中の保存を読んだだけのことがある
                WarnOnce("parse:" + error, $"設定を読み飛ばします（直前の値を使い続けます）: {error}");
                return false;
            }

            _warned.Clear();
            var updated = !Equals(_current, parsed);
            _current = parsed;
            return updated;
        }

        /// <summary>書けたら true。★ <b>失敗しても throw しない</b>（終了処理からも呼ばれる）。</summary>
        public bool Save(MascotSettings settings)
        {
            try
            {
                _write(SettingsJson.Write(settings));
            }
            catch (Exception e)
            {
                WarnOnce("write", "設定を書けませんでした: " + e.Message);
                return false;
            }

            _current = settings;

            // ★ 自分で書いた直後にスタンプを取り直すこと。 取らないと、次の Refresh が
            //   「変わった」と判定して自分の書き込みを読み直す（無害だが、無駄が毎回出る）
            try
            {
                _stampSeen = _stamp != null ? _stamp() : null;
                _loaded = true;
            }
            catch (Exception)
            {
                // 次の Refresh で読み直すだけなので、ここは黙って諦めてよい
                _loaded = false;
            }
            return true;
        }

        private void OnParseWarning(string message)
        {
            WarnOnce("json:" + message, message);
        }

        private void WarnOnce(string key, string message)
        {
            if (!_warned.Add(key)) return;
            if (_warn != null) _warn(message);
        }

        private static bool Equals(MascotSettings a, MascotSettings b)
        {
            return a.Muted == b.Muted && string.Equals(a.MuteHotKey, b.MuteHotKey, StringComparison.Ordinal);
        }
    }
}
