using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// 実行中の環境から <see cref="AssetEnv"/> を組む。
    ///
    /// ★ <b><see cref="AssetPath"/> と分けてあるのは、あちらを <c>UnityEngine</c> 非依存の
    ///   純粋関数に保つため。</b> 一緒にすると EditMode テストが Editor の実パス
    ///   （<c>Application.persistentDataPath</c> など）を踏む導線が残る。
    /// </summary>
    public static class AssetEnvFactory
    {
        private static readonly string[] ReadVariables =
        {
            "CHATTER_MASCOT_VRM",
            "CHATTER_MASCOT_VRMA",
            "XDG_CONFIG_HOME",
            "APPDATA",
        };

        public static AssetEnv Current() => new AssetEnv
        {
            CommandLine = ChatterMascot.CommandLine.Args(),
            Variables = ReadEnvironment(),
            PersistentDataPath = Application.persistentDataPath,
            StreamingAssetsPath = Application.streamingAssetsPath,
            // ★ .app を Finder から起動するとシェルを継承しないので環境変数はほぼ空になるが、
            //   HOME だけは launchd が入れる
            HomeDirectory = Home(),
            IsWindows = Application.platform == RuntimePlatform.WindowsPlayer
                        || Application.platform == RuntimePlatform.WindowsEditor,
            HasUserConfigDirectory = HasSharedFileSystem(),
            ListFiles = SafeListFiles,
        };

        /// <summary>
        /// <c>chatter-agent-server</c> と共有できるファイルシステムがあるか。
        ///
        /// ★ <b>許可リストで書くこと。</b> 「Android を除く」と否定で書くと、
        ///   #25 が持ち込む新しい XR プラットフォームが<b>黙って共有 FS を持つ扱い</b>になる。
        /// </summary>
        private static bool HasSharedFileSystem()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.OSXEditor:
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsEditor:
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.LinuxEditor:
                    return true;
                default:
                    return false;
            }
        }

        private static string Home()
        {
            try
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static IReadOnlyDictionary<string, string> ReadEnvironment()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in ReadVariables)
            {
                try
                {
                    var value = Environment.GetEnvironmentVariable(name);
                    if (value != null) map[name] = value;
                }
                catch (Exception)
                {
                    // 読めない環境でも探索順の残りは生かす
                }
            }
            return map;
        }

        /// <summary>
        /// ★ <b>投げないこと。</b> 読めないディレクトリが1つあるだけで起動が止まる。
        /// </summary>
        private static IReadOnlyList<string> SafeListFiles(string directory, string pattern)
        {
            try
            {
                return Directory.Exists(directory)
                    ? Directory.GetFiles(directory, pattern)
                    : Array.Empty<string>();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }
    }
}
