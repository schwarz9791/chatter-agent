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
        private const string AudioManagerPath = "ProjectSettings/AudioManager.asset";

        /// <summary>
        /// <c>m_DisableAudio</c> の出荷値。<b>OFF</b> —— Android は Unity 内蔵オーディオで鳴らし、
        /// <c>AudioSettings.Mobile.StopAudioOutput()</c> で手放すため。
        /// </summary>
        private const bool ShippedDisableAudio = false;

        /// <summary>
        /// ビルドのあいだだけ <c>Disable Unity Audio</c> を ON にする。戻す処理を返す。
        ///
        /// ★ <b>これが無いと省電力にならない。</b> macOS では音を外部プロセス（<c>afplay</c>）で
        ///   鳴らすが、Unity 内蔵オーディオが有効なままだと、<c>AudioSource</c> を1つも
        ///   鳴らさなくても<b>Unity 側が出力デバイスを掴み続ける</b>（実測: 起動から終了までずっと。
        ///   → <c>docs/mascot.md</c>）。ON にしたビルドでは CoreAudio がプロセスを認識すらしない。
        ///
        /// ★ <b>プロジェクト設定はプラットフォーム別に持てない</b>ので、コミットされた値は
        ///   Android 側の要求（<see cref="ShippedDisableAudio"/> = OFF）に合わせてある。
        ///
        /// ★ <b>Editor の GUI からビルドすると、この切り替えは走らない。</b>
        ///   ビルドは <c>scripts/build.sh</c> から行うこと（→ <c>SETUP.md</c>）。
        ///
        /// ★ <b>中断すると git 管理下のファイルに ON が残る。</b> ここは3段構えの2段目で、
        ///   1段目は <c>scripts/build.sh</c> の <c>trap</c>、3段目は CI の assert。
        ///   残ったままコミットされると、**Android ビルドが Unity 内蔵オーディオごと無効**になり、
        ///   <c>AudioClipPlayer</c> が鳴らないのに <c>isPlaying</c> が即 false → 全部 ack されて
        ///   キューから消える。
        /// </summary>
        private static Action DisableUnityAudioDuringBuild()
        {
            bool previous;
            if (!TrySetDisableAudio(true, out previous))
            {
                Debug.LogWarning("[Build] Disable Unity Audio を切り替えられません。" +
                                 "無音時に出力デバイスを掴み続けるビルドになります");
                return () => { };
            }

            if (previous)
            {
                // ★ 前回のビルドが中断した痕跡。**隠さずに直す。**
                //   「既に ON なら何もしない」にすると、中断が残ったまま次のビルドを通してしまう
                Debug.LogWarning("[Build] Disable Unity Audio が既に ON でした" +
                                 "（前回のビルドが中断した可能性）。ビルド後に出荷値へ戻します");
            }
            else
            {
                Debug.Log("[Build] Disable Unity Audio を ON にしました（ビルド後に戻します）");
            }

            // ★ <b>クロージャに UnityEngine.Object を持たせないこと。</b> ビルド中の
            //   スクリプト再コンパイル → domain reload で destroyed object になりうる。
            //   finally の中から例外が飛ぶと、本来のビルド例外が差し替わり、
            //   EditorApplication.Exit も飛び、**m_DisableAudio: 1 がディスクに残る**。
            //   パスから読み直す
            return () =>
            {
                bool ignored;
                // ★ **常に出荷値へ戻す**（previous ではなく）。前回の中断が残っていてもここで直る
                if (!TrySetDisableAudio(ShippedDisableAudio, out ignored))
                {
                    Debug.LogError("[Build] Disable Unity Audio を戻せませんでした。" +
                                   "ProjectSettings/AudioManager.asset の m_DisableAudio を " +
                                   "手で 0 に直してください");
                    return;
                }
                Debug.Log("[Build] Disable Unity Audio を戻しました");
            };
        }

        /// <summary>
        /// <c>m_DisableAudio</c> を書き換える。成否を返す。
        ///
        /// ★ <b>例外を投げないこと。</b> <c>finally</c> の中から呼ばれるので、
        ///   ここから例外が出ると本来のビルド例外を差し替えてしまう。
        /// </summary>
        private static bool TrySetDisableAudio(bool value, out bool previous)
        {
            previous = false;
            try
            {
                var assets = AssetDatabase.LoadAllAssetsAtPath(AudioManagerPath);
                if (assets == null || assets.Length == 0 || assets[0] == null) return false;

                var serialized = new SerializedObject(assets[0]);
                var property = serialized.FindProperty("m_DisableAudio");
                if (property == null) return false;

                previous = property.boolValue;
                // 既に目的の値なら書かない（AssetDatabase.SaveAssets の再シリアライズを減らす）
                if (previous == value) return true;

                property.boolValue = value;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Build] AudioManager.asset を操作できませんでした: " + e.Message);
                return false;
            }
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
