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
    /// unity build chatter-mascot --target StandaloneOSX \
    ///   --execute-method ChatterMascot.EditorTools.BuildScript.BuildMacOS \
    ///   -o "$PWD/chatter-mascot/Build/TransparencyProbe.app" \
    ///   --args "-buildScene 'Assets/Scenes/TransparencyProbe.unity'"
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
        ///   → <c>docs/knowledge/mascot-speech.md</c>）。ON にしたビルドでは CoreAudio がプロセスを認識すらしない。
        ///
        /// ★ <b>プロジェクト設定はプラットフォーム別に持てない</b>ので、コミットされた値は
        ///   Android 側の要求（<see cref="ShippedDisableAudio"/> = OFF）に合わせてある。
        ///
        /// ★ <b>Editor の GUI からビルドすると、この切り替えは走らない。</b>
        ///   ビルドは <c>scripts/build.sh</c> から行うこと（→ <c>docs/mascot.md</c>）。
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
                Debug.LogWarning("[Build] Cannot toggle Disable Unity Audio. " +
                                 "The build will keep holding the output device while silent");
                return () => { };
            }

            if (previous)
            {
                // ★ 前回のビルドが中断した痕跡。**隠さずに直す。**
                //   「既に ON なら何もしない」にすると、中断が残ったまま次のビルドを通してしまう
                Debug.LogWarning("[Build] Disable Unity Audio was already ON" +
                                 " (the previous build may have been interrupted). It will be reset to the shipping value after the build");
            }
            else
            {
                Debug.Log("[Build] Set Disable Unity Audio to ON (will be restored after the build)");
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
                    Debug.LogError("[Build] Could not restore Disable Unity Audio. " +
                                   "Set m_DisableAudio in ProjectSettings/AudioManager.asset " +
                                   "to 0 by hand");
                    return;
                }
                Debug.Log("[Build] Restored Disable Unity Audio");
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
                Debug.LogWarning("[Build] Could not modify AudioManager.asset: " + e.Message);
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

            // ★ 版を決められなければ手元の版で配らず止める。入れた設定はこのプロセスでは戻さない
            //   （ProjectSettings.asset は build.sh が git から戻す）。
            //   Exit は下の try の外に置くこと（理由は下の ★）。
            var releaseTag = Environment.GetEnvironmentVariable(ReleaseTagEnv);
            var release = !string.IsNullOrEmpty(releaseTag);
            if (release)
            {
                if (!TryParseReleaseTag(releaseTag, out var releaseVersion, out _))
                {
                    Debug.LogError($"[Build] Release tag {ReleaseTagEnv}='{releaseTag}' is not in the form " +
                                   "mascot-v<MAJOR>.<MINOR>.<PATCH>. Aborting");
                    EditorApplication.Exit(1);
                    return;
                }
                PlayerSettings.bundleVersion = releaseVersion;
            }
            Debug.Log($"[Build] version={PlayerSettings.bundleVersion}" + (release ? $" tag={releaseTag}" : ""));

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
            if (Failed(summary, release))
            {
                EditorApplication.Exit(1);
                return;
            }
            EditorApplication.Exit(0);
        }

        public static void BuildAndroid()
        {
            var scene = Argument("-buildScene") ?? "Assets/Scenes/Mascot.unity";
            var output = Argument("-buildOutput") ?? "Build/ChatterMascot.apk";

            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            var absolute = Path.IsPathRooted(output) ? output : Path.Combine(projectRoot, output);

            EditorUserBuildSettings.buildAppBundle = false;

            var options = new BuildPlayerOptions
            {
                scenes = new[] { scene },
                locationPathName = absolute,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
            };

            Debug.Log($"[Build] scene={scene} output={absolute}");

            // ★ **DisableUnityAudioDuringBuild はここでは呼ばないこと。** Android は
            //   Unity 内蔵オーディオで鳴らす（AudioClipPlayer + AudioSettings.Mobile.
            //   StopAudioOutput() で無音時に手放す）ので、コミットされた m_DisableAudio: 0 が
            //   そのまま Android の出荷値そのもの。ここで ON にすると、鳴らす手段ごと
            //   潰した無音ビルドができあがる。
            ExcludeDesktopWindowPluginsFromBuild();

            var keystorePath = Environment.GetEnvironmentVariable(KeystoreEnv);
            var release = !string.IsNullOrEmpty(keystorePath);
            var releaseTag = Environment.GetEnvironmentVariable(ReleaseTagEnv);

            // ★ **署名や版が決まらなければ debug keystore・手元の版へ落とさず止める。** 配るつもりの
            //   ビルドが debug 鍵で出ると、端末では上書きできない別アプリになる。
            //   入れた設定はこのプロセスでは戻さない。ProjectSettings.asset は
            //   build-android.sh が git から戻す（中断でも戻る）。
            if (release && !TryUseReleaseSettings(keystorePath, releaseTag))
            {
                EditorApplication.Exit(1);
                return;
            }

            Debug.Log($"[Build] version={PlayerSettings.bundleVersion} " +
                      $"versionCode={PlayerSettings.Android.bundleVersionCode} " +
                      $"signing={(release ? "release" : "debug")}" +
                      (release ? $" tag={releaseTag} keystore={keystorePath}" : ""));

            var summary = BuildPipeline.BuildPlayer(options).summary;

            Debug.Log($"[Build] result={summary.result} errors={summary.totalErrors} " +
                      $"time={(int)summary.totalTime.TotalSeconds}s size={summary.totalSize}");

            if (Failed(summary, release))
            {
                EditorApplication.Exit(1);
                return;
            }
            EditorApplication.Exit(0);
        }

        /// <summary>
        /// ビルドを失敗として扱うか。配るビルドはエラーが1つでもあれば失敗にする ——
        /// シェーダーのコンパイルに失敗しても <c>BuildPipeline</c> は <c>Succeeded</c> を返すので、
        /// 結果だけを見ると、見た目の欠けたビルドを配りうる。
        /// </summary>
        private static bool Failed(BuildSummary summary, bool release)
        {
            if (summary.result != BuildResult.Succeeded) return true;
            if (!release || summary.totalErrors == 0) return false;
            Debug.LogError($"[Build] Not producing a distributable build, errors={summary.totalErrors}");
            return true;
        }

        private const string KeystoreEnv = "CHATTER_MASCOT_KEYSTORE";
        private const string KeystorePassEnv = "CHATTER_MASCOT_KEYSTORE_PASS";
        private const string KeyAlias = "chattermascot";
        private const string ReleaseTagEnv = "CHATTER_MASCOT_RELEASE_TAG";

        /// <summary>
        /// release タグ <c>mascot-v&lt;MAJOR&gt;.&lt;MINOR&gt;.&lt;PATCH&gt;</c> から版を決める。
        /// 各数は 0〜99・先頭ゼロ不可で、プレリリース接尾辞は持たない。
        /// <c>versionCode = MAJOR*10000 + MINOR*100 + PATCH</c> で、Android が要る 1 以上に
        /// ならない 0.0.0 は受けない。版の規則はここ1か所にだけ置く。
        /// </summary>
        public static bool TryParseReleaseTag(string tag, out string version, out int versionCode)
        {
            version = null;
            versionCode = 0;
            if (tag == null) return false;

            var match = System.Text.RegularExpressions.Regex.Match(
                tag, @"\Amascot-v(0|[1-9][0-9]?)\.(0|[1-9][0-9]?)\.(0|[1-9][0-9]?)\z");
            if (!match.Success) return false;

            var major = int.Parse(match.Groups[1].Value);
            var minor = int.Parse(match.Groups[2].Value);
            var patch = int.Parse(match.Groups[3].Value);
            versionCode = major * 10000 + minor * 100 + patch;
            if (versionCode == 0) return false;

            version = $"{major}.{minor}.{patch}";
            return true;
        }

        /// <summary>
        /// release 用に署名と版を PlayerSettings へ入れる。署名は環境変数の keystore、版は
        /// release タグから決める。パスワードは argv に出さないため環境変数から読む。PKCS12 は
        /// ストアと鍵のパスワードが同一なので、同じ値を両方に入れる。
        ///
        /// 検証はすべて設定に触る前に行う。入れた値は ProjectSettings.asset に永続化されるが、
        /// ここでは戻さない（build-android.sh が git から戻す）。
        /// </summary>
        private static bool TryUseReleaseSettings(string keystorePath, string tag)
        {
            if (!TryParseReleaseTag(tag, out var version, out var versionCode))
            {
                Debug.LogError($"[Build] Release tag {ReleaseTagEnv}='{tag}' is not in the form " +
                               "mascot-v<MAJOR>.<MINOR>.<PATCH>. Aborting");
                return false;
            }
            var password = Environment.GetEnvironmentVariable(KeystorePassEnv);
            if (string.IsNullOrEmpty(password))
            {
                Debug.LogError($"[Build] {KeystoreEnv} is set but {KeystorePassEnv} is empty. " +
                               "Aborting without falling back to the debug keystore");
                return false;
            }
            if (!File.Exists(keystorePath))
            {
                Debug.LogError($"[Build] Keystore not found: {keystorePath}. " +
                               "Aborting without falling back to the debug keystore");
                return false;
            }

            PlayerSettings.bundleVersion = version;
            PlayerSettings.Android.bundleVersionCode = versionCode;
            PlayerSettings.Android.useCustomKeystore = true;
            PlayerSettings.Android.keystoreName = keystorePath;
            PlayerSettings.Android.keyaliasName = KeyAlias;
            PlayerSettings.Android.keystorePass = password;
            PlayerSettings.Android.keyaliasPass = password;
            return true;
        }

        /// <summary>
        /// <c>com.kirurobo.uniwinc</c>（UniWindowController）が同梱するネイティブプラグインを
        /// すべて、この呼び出しのビルドから外す。デスクトップ専用パッケージで、
        /// Android が読み込むものは1つも無い。
        ///
        /// ★★ <b>パッケージの <c>PluginImporter</c> 設定そのものを直さないこと。</b>
        ///   このパッケージは <c>git</c> 参照（<c>Packages/manifest.json</c>）で解決されており
        ///   Unity 側で読み取り専用扱いになる。<c>SetCompatibleWithAnyPlatform</c> +
        ///   <c>SaveAndReimport</c> は成功したように見えて、実際にはメタファイルへ書き込まれない
        ///   （インポーターの設定を書き換える手当ては効かない）。
        ///
        /// ★ <b>だから <see cref="PluginImporter.SetIncludeInBuildDelegate"/> で外す。</b>
        ///   メタファイルを書き換えず、このビルド呼び出しの間だけ「ビルドに含めるか」を
        ///   差し替えるコールバックなので、読み取り専用のパッケージにも効く。
        ///
        /// ★ <b>GUID やファイルパスを決め打ちにしないこと。</b> <c>Library/PackageCache</c> の
        ///   パスはパッケージのリビジョンハッシュを含みバージョンが上がるたびに変わるうえ、
        ///   同梱プラグインの構成自体もパッケージの改版で増減しうる。
        ///   <see cref="PluginImporter.GetAllImporters"/> を舐めて
        ///   <c>assetPath</c> がこのパッケージ配下かどうかで判定する。
        /// </summary>
        private static void ExcludeDesktopWindowPluginsFromBuild()
        {
            const string packagePrefix = "Packages/com.kirurobo.uniwinc/";

            var excluded = 0;
            foreach (var importer in PluginImporter.GetAllImporters())
            {
                if (!importer.assetPath.StartsWith(packagePrefix, StringComparison.Ordinal)) continue;

                importer.SetIncludeInBuildDelegate(_ => false);
                excluded++;
            }

            if (excluded == 0)
            {
                Debug.LogWarning($"[Build] No native plugins found under {packagePrefix}");
                return;
            }
            Debug.Log($"[Build] Excluded com.kirurobo.uniwinc native plugins from this build, count={excluded}");
        }

        /// <summary>
        /// 起動引数。★ <b>読み方を <see cref="ChatterMascot.CommandLine"/> に寄せてある</b> ——
        /// ランタイム側（<c>-serverUrl</c> / <c>-vrm</c>）と規則が食い違うと、
        /// 検証手順の書き方が2通りになる。
        /// </summary>
        private static string Argument(string name) => CommandLine.Argument(name);
    }
}
