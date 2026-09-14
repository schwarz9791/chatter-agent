using ChatterMascot.Vrm;

namespace ChatterMascot.Settings
{
    /// <summary>
    /// <c>settings.json</c> の置き場所を決める。<b>純粋関数</b>（<c>AssetPath</c> と同じ規律）。
    ///
    /// | 環境 | 置き場所 |
    /// |---|---|
    /// | デスクトップ（<see cref="AssetEnv.HasUserConfigDirectory"/>） | <c>{RuntimeDirectory}/mascot/settings.json</c>（<c>window.json</c> と同じディレクトリ） |
    /// | それ以外（Android） | <c>{PersistentDataPath}/settings.json</c> |
    ///
    /// ★ <b><c>Path.Combine</c> を使わないこと</b>（→ <see cref="AssetPath.Join"/> の doc と同じ理由）。
    /// </summary>
    public static class SettingsLocation
    {
        private const string SettingsDirectory = "mascot";
        private const string SettingsFile = "settings.json";

        /// <summary>解決できなければ <c>null</c>（基準になるパスが無い）。</summary>
        public static string Resolve(AssetEnv env)
        {
            if (env == null) return null;

            if (env.HasUserConfigDirectory)
            {
                var root = AssetPath.RuntimeDirectory(env);
                return AssetPath.Join(AssetPath.Join(root, SettingsDirectory), SettingsFile);
            }

            return AssetPath.Join(env.PersistentDataPath, SettingsFile);
        }
    }
}
