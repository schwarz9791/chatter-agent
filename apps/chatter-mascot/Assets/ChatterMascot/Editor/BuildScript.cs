using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace ChatterMascot.EditorTools
{
    /// <summary>
    /// <c>-batchmode</c> から呼ぶビルド。
    ///
    /// ★ <b>Editor の GUI からビルドしないこと</b>（自動化する場合）。MCP 経由で
    ///   <c>BuildPipeline.BuildPlayer</c> を呼ぶと、<b>保存確認などのモーダルダイアログが出た瞬間に
    ///   応答が返らなくなる</b>。ダイアログを人が閉じるまで、呼び出し側からは
    ///   「ハングした」としか見えない（実際に30分待って気づけなかった）。
    ///   <c>-batchmode</c> はダイアログを出さないので、この失敗の仕方をしない。
    ///
    /// <code>
    /// /Applications/Unity/Hub/Editor/6000.5.8f1/Unity.app/Contents/MacOS/Unity \
    ///   -batchmode -quit -nographics \
    ///   -projectPath apps/chatter-mascot \
    ///   -executeMethod ChatterMascot.EditorTools.BuildScript.BuildMacOS \
    ///   -logFile - \
    ///   -buildScene Assets/Scenes/TransparencyProbe.unity \
    ///   -buildOutput Build/TransparencyProbe.app
    /// </code>
    ///
    /// ★ <b>Editor を開いたままだと失敗する。</b> Unity はプロジェクトを排他ロックする。
    /// </summary>
    public static class BuildScript
    {
        public static void BuildMacOS()
        {
            var scene = Argument("-buildScene") ?? "Assets/Scenes/Mascot.unity";
            var output = Argument("-buildOutput") ?? "Build/ChatterMascot.app";

            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            var absolute = Path.IsPathRooted(output) ? output : Path.Combine(projectRoot, output);

            var options = new BuildPlayerOptions
            {
                scenes = new[] { scene },
                locationPathName = absolute,
                target = BuildTarget.StandaloneOSX,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            };

            Debug.Log($"[Build] scene={scene} output={absolute}");
            var summary = BuildPipeline.BuildPlayer(options).summary;
            Debug.Log($"[Build] result={summary.result} errors={summary.totalErrors} " +
                      $"time={(int)summary.totalTime.TotalSeconds}s size={summary.totalSize}");

            // ★ batchmode では終了コードで結果を伝える。-quit だけだと常に 0 になり、
            //   CI からもシェルからも失敗に気づけない
            if (summary.result != BuildResult.Succeeded)
            {
                EditorApplication.Exit(1);
                return;
            }
            EditorApplication.Exit(0);
        }

        private static string Argument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name) return args[i + 1];
            }
            return null;
        }
    }
}
