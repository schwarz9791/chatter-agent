using System;
using System.Collections.Generic;

namespace ChatterMascot.Vrm
{
    /// <summary>読み込む対象。<c>.vrm</c>（モデル）と <c>.vrma</c>（アニメーション）。</summary>
    public enum AssetKind
    {
        Vrm,
        Vrma,
    }

    /// <summary>候補がどこから来たか。ログで「どれを採ってどれを無視したか」を言うのに要る。</summary>
    public enum AssetSource
    {
        CommandLine,
        EnvironmentVariable,

        /// <summary>
        /// 設定パネル（#76）が置いたモデル。<c>models/</c> の<b>固定名</b>を指す
        /// （<see cref="AssetPath.SelectedVrmFile"/>）。
        ///
        /// ★ <b><c>UserConfig</c>（<c>models/*.vrm</c> の走査）より前に置くこと。</b>
        ///   あちらは <c>Ordinal</c> の先頭が勝つので、これが無いと
        ///   <b>2つ目のモデルを選んでも反映されない</b>（実機で踏んだ）。
        /// </summary>
        Settings,

        PersistentData,
        UserConfig,
        StreamingAssets,
    }

    /// <summary>探索順の1件。</summary>
    public readonly struct AssetCandidate
    {
        public readonly AssetSource Source;
        public readonly string Path;

        public AssetCandidate(AssetSource source, string path)
        {
            Source = source;
            Path = path;
        }

        public override string ToString() => Describe(Source) + ": " + Path;

        private static string Describe(AssetSource source)
        {
            switch (source)
            {
                case AssetSource.CommandLine: return "起動引数";
                case AssetSource.EnvironmentVariable: return "環境変数";
                case AssetSource.Settings: return "設定";
                case AssetSource.PersistentData: return "persistentDataPath";
                case AssetSource.UserConfig: return "ユーザー設定";
                case AssetSource.StreamingAssets: return "同梱";
                default: return source.ToString();
            }
        }
    }

    /// <summary>
    /// パス解決に必要な環境。<c>core/src/core/paths.ts</c> の <c>PathEnv</c> にあたる。
    ///
    /// ★ <b>プラットフォーム判定は <c>#if</c> ではなくデータとして持つ。</b>
    ///   純粋関数の中にコンパイル時分岐が入ると、macOS の EditMode テストから
    ///   Android の枝を固定できなくなる。
    /// </summary>
    public sealed class AssetEnv
    {
        public IReadOnlyList<string> CommandLine { get; set; } = Array.Empty<string>();

        public IReadOnlyDictionary<string, string> Variables { get; set; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public string PersistentDataPath { get; set; } = "";
        public string StreamingAssetsPath { get; set; } = "";
        public string HomeDirectory { get; set; } = "";
        public bool IsWindows { get; set; }

        /// <summary>
        /// サーバーと共有できるファイルシステムがあるか。
        /// ★ <b>Android は <c>false</c>。</b> 許可リストで組み立てること。
        /// </summary>
        public bool HasUserConfigDirectory { get; set; }

        /// <summary>
        /// (ディレクトリ, <c>"*.vrm"</c>) → パス列。
        /// ★ <b>例外を投げず、読めなければ空を返す実装にすること。</b>
        /// </summary>
        public Func<string, string, IReadOnlyList<string>> ListFiles { get; set; } =
            (_, __) => Array.Empty<string>();
    }

    /// <summary>
    /// <c>.vrm</c> / <c>.vrma</c> をどこから探すかを決める。<b>純粋関数。</b>
    ///
    /// | 順 | 出どころ | <c>.vrm</c> | <c>.vrma</c> | 対象 |
    /// |---|---|---|---|---|
    /// | 順 | 出どころ | <c>.vrm</c> | <c>.vrma</c> | 対象 |
    /// |---|---|---|---|---|
    /// | 1 | 起動引数 | <c>-vrm</c> | <c>-vrma</c> | 全 |
    /// | 2 | 環境変数 | <c>CHATTER_MASCOT_VRM</c> | <c>CHATTER_MASCOT_VRMA</c> | 全 |
    /// | 3 | <b>設定</b>（#76） | <c>models/&lt;選んだ名前&gt;</c> | —— | デスクトップのみ |
    /// | 4 | <c>persistentDataPath/</c> | <c>model.vrm</c> | <c>idle.vrma</c> | 全 |
    /// | 5 | <c>${XDG_CONFIG_HOME:-~/.config}/chatter-agent/</c> | <c>models/*.vrm</c> | <c>animations/*.vrma</c> | デスクトップのみ |
    /// | 6 | <c>streamingAssetsPath/</c>（同梱） | <c>vita.vrm</c> | <c>idle_loop.vrma</c> | 全 |
    ///
    /// ★ <b>設定は起動引数・環境変数より<u>下</u>。</b> <c>-vrm</c> は切り分けの逃げ道
    ///   （「設定が壊れていても、この引数を付ければ必ず出る」）なので、設定より優先を保つ。
    ///
    /// ★ <b>存在確認をしない。</b> 「存在する候補だけ返す」形（<c>exists</c> の注入）は
    ///   <b>Android で破綻する</b> —— <c>streamingAssetsPath</c> は APK 内の
    ///   <c>jar:file://…</c> なので <c>File.Exists</c> が<b>必ず false</b> を返し、
    ///   <b>同梱モデルだけが常に「無い」判定</b>になってフォールバック先が消える。
    ///   <c>Vrm10.LoadPathAsync</c> ではなく <c>LoadBytesAsync</c> に統一したのと同じ理由で、
    ///   <b>「読めたか」を唯一の判定にする</b>。
    ///
    /// ★ <b>設定 UI（#76）はこの表に乗った</b>（3段目）。ここは探索順だけを決める ——
    ///   ファイルのコピーも、名前の検証（区切り文字を通さない）も、呼び出し側の仕事。
    /// </summary>
    public static class AssetPath
    {
        /// <summary><c>.vrm</c> と <c>.vrma</c> の違いはこの表だけ。探索の骨格は共有する。</summary>
        private readonly struct Spec
        {
            public readonly string Argument;
            public readonly string Variable;
            public readonly string PersistentFile;
            public readonly string UserDirectory;
            public readonly string Pattern;
            public readonly string BundledFile;

            public Spec(string argument, string variable, string persistentFile,
                        string userDirectory, string pattern, string bundledFile)
            {
                Argument = argument;
                Variable = variable;
                PersistentFile = persistentFile;
                UserDirectory = userDirectory;
                Pattern = pattern;
                BundledFile = bundledFile;
            }
        }

        private static Spec Of(AssetKind kind) => kind == AssetKind.Vrm
            ? new Spec("-vrm", "CHATTER_MASCOT_VRM", "model.vrm", "models", "*.vrm", "vita.vrm")
            : new Spec("-vrma", "CHATTER_MASCOT_VRMA", "idle.vrma", "animations", "*.vrma", "idle_loop.vrma");

        /// <summary>
        /// 設定パネルが選んだモデルを置く<b>固定のファイル名</b>（<c>models/</c> 配下）。
        ///
        /// ★★ <b>選んだファイルの名前をそのまま使わないこと。</b> 選び直すたびに
        ///   <c>models/</c> にファイルが積み上がり、消す責任が誰にも無くなる。
        ///   固定名なら <c>File.Copy(overwrite: true)</c> が前の1本を必ず置き換える。
        ///
        /// ★ <b>元の名前は表示のためだけに覚える</b>（<c>MascotSettings.VrmFileName</c>）。
        ///   「どのモデルを選んだか」を画面に出すのに要るが、<b>探索には使わない</b>。
        ///
        /// ★ <b><c>.vrma</c> には対応する仕組みが無い。</b> モーションを選ばせる UI を
        ///   作っていないため（#70 が入るまで、選ばせる中身が同梱の1本しか無い）。
        ///   ここが <c>.vrm</c> 専用なのは意図的な非対称。
        /// </summary>
        public const string SelectedVrmFile = "mascot.vrm";

        /// <summary>
        /// 候補を優先順に並べる。
        ///
        /// ★ <b>遅延列挙にしないこと。</b> 呼び出し側は、全部読めなかったときの
        ///   エラーメッセージのためにもう一度舐める。<c>IEnumerable</c> だと
        ///   <see cref="AssetEnv.ListFiles"/> が二度走る。
        /// </summary>
        public static IReadOnlyList<AssetCandidate> Enumerate(AssetEnv env, AssetKind kind)
        {
            var result = new List<AssetCandidate>(4);
            if (env == null) return result;

            var spec = Of(kind);

            Add(result, AssetSource.CommandLine, env, CommandLine.Argument(env.CommandLine, spec.Argument));
            Add(result, AssetSource.EnvironmentVariable, env, Variable(env, spec.Variable));

            // ★ 共有ファイルシステムがある環境でだけ models/ を舐める。
            //   ★ Android には無いのでこの段ごと落ちる
            //   ★ Join は基準が空なら null を返す。ここで弾かずに ListFiles(null, ...) を
            //     呼ぶと、注入された実装が null をどう扱うかに結果が左右される
            //     （AssetEnvFactory.SafeListFiles は Directory.Exists(null) で false に
            //     なるので実害は無いが、それはこの実装の詳細であって契約ではない）
            var userDirectory = env.HasUserConfigDirectory
                ? Join(RuntimeDirectory(env), spec.UserDirectory)
                : null;

            var userFiles = new List<string>();
            if (!string.IsNullOrEmpty(userDirectory))
            {
                userFiles.AddRange(env.ListFiles(userDirectory, spec.Pattern) ?? Array.Empty<string>());
                // ★ Ordinal で並べること。既定の比較はカルチャ依存で、
                //   マシンによって「どのモデルが出るか」が変わる
                userFiles.Sort(StringComparer.Ordinal);
            }

            // ★★ 設定パネルが置いたモデルを名指しで先に出す（#76）。走査は Ordinal の
            //   先頭が勝つので、これが無いと**2つ目を選んでも反映されない**。
            //
            // ★★ **設定を見ないこと。** 以前は settings.json のファイル名を
            //   `AssetEnv.SelectedVrmFileName` に渡す作りだったが、**本番コードが誰も渡して
            //   いなかった**（テストだけが設定していた）ので候補が一度も出ず、走査の先頭が
            //   勝ち続けていた。置き場所を固定名にして、**設定と実ファイルがズレる余地ごと**
            //   無くしてある。
            //
            // ★ **走査の結果から引き上げること。** 無条件に足すと、パネルで一度も選んで
            //   いない人の起動ログに毎回「読めませんでした」が1本増える。
            if (kind == AssetKind.Vrm)
            {
                var at = userFiles.FindIndex(IsSelectedVrm);
                if (at >= 0)
                {
                    var chosen = userFiles[at];
                    userFiles.RemoveAt(at);
                    Add(result, AssetSource.Settings, env, chosen);
                }
            }

            Add(result, AssetSource.PersistentData, env, Join(env.PersistentDataPath, spec.PersistentFile));

            foreach (var file in userFiles) Add(result, AssetSource.UserConfig, env, file);

            Add(result, AssetSource.StreamingAssets, env, Join(env.StreamingAssetsPath, spec.BundledFile));
            return result;
        }

        /// <summary>
        /// <c>models/</c> の走査結果が、設定パネルが置いたモデルか。
        ///
        /// ★ <b>末尾で見ること。</b> <c>ListFiles</c> が返すパスの区切りや前置は
        ///   注入された実装の都合で決まるので、こちらで組み立てた文字列と
        ///   完全一致させにいかない。
        /// </summary>
        private static bool IsSelectedVrm(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (string.Equals(path, SelectedVrmFile, StringComparison.Ordinal)) return true;
            return path.EndsWith("/" + SelectedVrmFile, StringComparison.Ordinal)
                || path.EndsWith("\\" + SelectedVrmFile, StringComparison.Ordinal);
        }

        /// <summary>
        /// <c>core/src/core/paths.ts</c> の <c>getRuntimeDir</c> と同じ規則。
        /// ユーザーから見て「chatter-agent の設定はここ1箇所」が保てるようにする。
        ///
        /// ★ <b>空文字でもフォールバックすること。</b> TS 側は
        ///   <c>e.env.XDG_CONFIG_HOME || path.join(homedir, ".config")</c> で、
        ///   JS の <c>||</c> は <c>""</c> も falsy。<c>string.IsNullOrEmpty</c> で揃える。
        ///
        /// ★ <b><c>IsNullOrWhiteSpace</c> にしないこと。</b> TS 側は <c>" "</c> を通すので、
        ///   そこで bash hook / Node / C# の3実装が静かにズレる。
        ///
        /// ★ <b>ここは TS 側とわざと違う。</b> <c>Join</c> は基準（<c>HomeDirectory</c> など）が
        ///   空だと <c>null</c> を返すので、<c>HomeDirectory</c> が空のときこの関数も
        ///   <c>null</c>（または空）を返しうる。<c>getRuntimeDir</c> が同条件で何を返すかは
        ///   ここでは断定しない —— こちら側は「相対パスを作るくらいなら候補を出さない」を選んだ、
        ///   という意図的な差として書いてある。次に触る人が「TS に合わせる」つもりで
        ///   相対パスを返す実装に戻さないこと。
        /// </summary>
        public static string RuntimeDirectory(AssetEnv env)
        {
            string basePath;
            if (env.IsWindows)
            {
                var appData = Variable(env, "APPDATA");
                basePath = string.IsNullOrEmpty(appData) ? Join(env.HomeDirectory, "AppData/Roaming") : appData;
            }
            else
            {
                var xdg = Variable(env, "XDG_CONFIG_HOME");
                basePath = string.IsNullOrEmpty(xdg) ? Join(env.HomeDirectory, ".config") : xdg;
            }
            return Join(basePath, "chatter-agent");
        }

        private static string Variable(AssetEnv env, string name)
        {
            if (env.Variables == null) return null;
            return env.Variables.TryGetValue(name, out var value) ? value : null;
        }

        /// <summary>
        /// ★ <b><c>Path.Combine</c> を使わないこと。</b> 区切り文字が実行マシン依存になり、
        ///   EditMode テストの期待値が Windows で崩れる。Unity 自身も
        ///   <c>persistentDataPath</c> を <c>/</c> で返す。
        ///
        /// ★ <b>空の基準からパスを作らないこと。</b> 左辺が空のときに右辺をそのまま返すと、
        ///   絶対パスのつもりの候補が<b>カレントディレクトリ基準の相対パス</b>に化ける。
        ///   <c>VrmProbe.ProbeEnv</c> が段を潰すために立てる
        ///   <c>PersistentDataPath = ""</c>（「この段を消す」つもりの1行）が、
        ///   <b>消すどころか基準を変えるだけ</b>になっていた（PR #69 の再レビューで判明。
        ///   プロジェクトルートに <c>model.vrm</c> を置いて再現済み）。
        ///   <see cref="Add"/> の空文字チェックと同じ思想を、こちらの左辺にも当てる。
        /// </summary>
        private static string Join(string left, string right)
        {
            if (string.IsNullOrEmpty(left)) return null;
            if (string.IsNullOrEmpty(right)) return left;
            return left.TrimEnd('/', '\\') + "/" + right.TrimStart('/', '\\');
        }

        /// <summary>
        /// 先頭の <c>~/</c> だけ展開して積む。<c>CHATTER_MASCOT_VRM=~/x.vrm</c> や
        /// #16 の設定 UI から必ず来る形。純粋なままでいられるのは
        /// <see cref="AssetEnv.HomeDirectory"/> が注入だから。
        /// </summary>
        private static void Add(List<AssetCandidate> into, AssetSource source, AssetEnv env, string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            if (path == "~")
            {
                path = env.HomeDirectory;
            }
            else if (path.StartsWith("~/", StringComparison.Ordinal))
            {
                path = Join(env.HomeDirectory, path.Substring(2));
            }

            if (string.IsNullOrEmpty(path)) return;
            into.Add(new AssetCandidate(source, path));
        }
    }
}
