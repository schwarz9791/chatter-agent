namespace ChatterMascot.Settings
{
    /// <summary>
    /// 設定パネルからの変更イベントの値を読む。
    /// </summary>
    public static class SettingsPanelJson
    {
        /// <summary>
        /// 変更イベントの値を <c>bool</c> として読む。
        ///
        /// ★ パネルは <c>"true"</c> / <c>"false"</c> を送る契約だが、
        ///   <c>"1"</c> / <c>"0"</c> も受ける（core の <c>parseBoolean</c> と同じ寛容さ）。
        /// </summary>
        public static bool ParseBool(string value, bool fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            var text = value.Trim().ToLowerInvariant();
            if (text == "true" || text == "1" || text == "yes" || text == "on") return true;
            if (text == "false" || text == "0" || text == "no" || text == "off") return false;
            return fallback;
        }
    }
}
