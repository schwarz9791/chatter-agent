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
        /// <summary>
        /// ビルドのあいだだけ <c>Disable Unity Audio</c> を ON にする。戻す処理を返す。
        ///
        /// ★ <b>これが無いと省電力にならない。</b> macOS では音を外部プロセス（<c>afplay</c>）で
        ///   鳴らすが、Unity 内蔵オーディオが有効なままだと、<c>AudioSource</c> を1つも
        ///   鳴らさなくても<b>Unity 側が出力デバイスを掴み続ける</b>（実測: 起動から終了までずっと。
        ///   → <c>docs/mascot.md</c>）。ON にしたビルドでは CoreAudio がプロセスを認識すらしない。
        ///
        /// ★ <b>プロジェクト設定はプラットフォーム別に持てない</b>ので、コミットされた値
        ///   （<c>m_DisableAudio: 0</c>）は Android 側の要求に合わせてある。Android は
        ///   Unity 内蔵オーディオで鳴らし、<c>AudioSettings.Mobile.StopAudioOutput()</c> で手放す。
        ///
        /// ★ <b>Editor の GUI からビルドすると、この切り替えは走らない。</b>
        ///   ビルドは <c>scripts/build.sh</c> から行うこと（→ <c>SETUP.md</c>）。
        /// </summary>
        private static Action DisableUnityAudioDuringBuild()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/AudioManager.asset");
            if (assets == null || assets.Length == 0)
            {
                Debug.LogWarning("[Build] AudioManager.asset を開けません。Disable Unity Audio を切り替えません");
                return () => { };
            }

            var serialized = new SerializedObject(assets[0]);
            var property = serialized.FindProperty("m_DisableAudio");
            if (property == null)
            {
                Debug.LogWarning("[Build] m_DisableAudio が見つかりません。切り替えません");
                return () => { };
            }

            var previous = property.boolValue;
            if (previous)
            {
                Debug.Log("[Build] Disable Unity Audio は既に ON です");
                return () => { };
            }

            property.boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
            Debug.Log("[Build] Disable Unity Audio を ON にしました（ビルド後に戻します）");

            return () =>
            {
                var restore = new SerializedObject(assets[0]);
                restore.FindProperty("m_DisableAudio").boolValue = previous;
                restore.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
                Debug.Log("[Build] Disable Unity Audio を戻しました");
            };
        }

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

            // ★ **EditorApplication.Exit を try の中に置かないこと。** 即座にプロセスが落ちるので
            //   finally が走らず、Disable Unity Audio を ON にしたままリポジトリに残る
            var restoreAudioSetting = DisableUnityAudioDuringBuild();
            BuildSummary summary;
            try
            {
                summary = BuildPipeline.BuildPlayer(options).summary;
            }
            finally
            {
                restoreAudioSetting();
            }

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
