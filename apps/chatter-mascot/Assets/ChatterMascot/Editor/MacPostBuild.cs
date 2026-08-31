using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using Debug = UnityEngine.Debug;

namespace ChatterMascot.EditorTools
{
    /// <summary>
    /// ビルドした <c>.app</c> の <c>Info.plist</c> に <c>LSUIElement</c> を書き、
    /// <b>Dock にアイコンを出さない</b>（#75）。
    ///
    /// ★ <b><c>UnityEditor.iOS.Xcode.PlistDocument</c> を使わないこと。</b>
    ///   あれは iOS Build Support に入っているので、<b>モジュールを入れていない環境では
    ///   コンパイルすら通らない</b>。macOS ビルドのために iOS のモジュールを要求するのは筋が悪い。
    ///
    /// ★★ <b><c>System.Xml.Linq</c> でも書かないこと（実測で潰した）。</b>
    ///   plist は普通の XML に見えるが、<c>XDocument</c> で読んで保存すると<b>2箇所壊れる</b>:
    ///   <list type="number">
    ///     <item><c>XDocumentType.InternalSubset</c> が空文字列になり、DOCTYPE の末尾に
    ///       <c>[]</c> が出力される。<c>PlistBuddy</c> が
    ///       <c>Encountered unexpected character [ on line 2 while parsing DTD</c> で読めなくなる</item>
    ///     <item><c>Save(path)</c> が UTF-8 BOM を付ける</item>
    ///   </list>
    ///   どちらも「XML としては直せる」が、<b>直し続ける理由が無い</b> ——
    ///   plist を壊さずに書き換える道具が OS に入っている。
    ///
    /// ★ <b>失敗してもビルドを落とさないこと。</b> ここが転んで得られる損失は
    ///   「Dock にアイコンが出る」だけで、マスコットは動く。
    ///   例外を投げると <c>BuildPipeline</c> がビルドごと失敗させる。
    ///
    /// ★ 実行時の切り替えは <c>CM_SetActivationPolicy</c>（<c>StatusItemBridge</c>）。
    ///   ここが書くのは<b>起動時の既定</b>だけ。
    /// </summary>
    public sealed class MacPostBuild : IPostprocessBuildWithReport
    {
        private const string PlistBuddy = "/usr/libexec/PlistBuddy";

        public int callbackOrder
        {
            get { return 0; }
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report == null) return;
            if (report.summary.platform != BuildTarget.StandaloneOSX) return;

            var plistPath = Path.Combine(report.summary.outputPath, "Contents", "Info.plist");
            try
            {
                if (!File.Exists(plistPath))
                {
                    Debug.LogWarning($"[Native] Info.plist がありません。Dock に出ます: {plistPath}");
                    return;
                }

                // ★ Set を先に試すこと。 既にあるキーを Add すると失敗する（逆も同じ）ので、
                //   2つで1組にして「あってもなくても同じ結果」にする
                var message = Run("Set :LSUIElement true", plistPath);
                if (message != null) message = Run("Add :LSUIElement bool true", plistPath);

                if (message != null)
                {
                    Debug.LogWarning("[Native] LSUIElement を書けませんでした。Dock に出ます: " + message);
                    return;
                }
                Debug.Log("[Native] LSUIElement を書きました（Dock に出ません）");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Native] LSUIElement を書けませんでした。Dock に出ます: " + OneLine(e.Message));
            }
        }

        /// <summary>成功したら <c>null</c>、失敗したら理由（1行）。</summary>
        private static string Run(string command, string plistPath)
        {
            var info = new ProcessStartInfo
            {
                FileName = PlistBuddy,
                // ★ shell を噛ませないこと（出力先のパスに空白が入る）
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(command);
            info.ArgumentList.Add(plistPath);

            using (var process = Process.Start(info))
            {
                if (process == null) return PlistBuddy + " を起動できませんでした";

                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode == 0) return null;
                return OneLine(string.IsNullOrEmpty(stderr) ? stdout : stderr);
            }
        }

        /// <summary>★ 複数行のログは scripts の grep で2行目以降が消える</summary>
        private static string OneLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "(理由なし)";
            return text.Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }
}
