using System;
using System.Collections.Generic;
using System.Text;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// モーションクリップの見た目のバリエーション。
    ///
    /// ★ <b>分類だけ。フィルタ設定は作らない。</b> ユーザーが同じカテゴリに複数のスタイルの
    ///   <c>.vrma</c> を置いたとき、ファイル名の <c>__cool</c> / <c>__cute</c> サフィックスで
    ///   ここを立てておくが、「Cool だけ再生する」設定キーは #70 では足さない
    ///   （<c>SettingsSchemaTests.DoesNotOfferFeaturesThatDoNotExistYet</c> は触らない）。
    /// </summary>
    public enum MotionStyle
    {
        Natural,
        Cool,
        Cute,
    }

    /// <summary>1本の <c>.vrma</c>。<b>不変。</b></summary>
    public sealed class MotionClip
    {
        /// <summary>どのカテゴリで見つかった／宣言されたか。</summary>
        public readonly MotionCategory Category;

        /// <summary><c>ListFiles</c> が返したままのフルパス。読み込みに使う。</summary>
        public readonly string Path;

        /// <summary><see cref="Path"/> の最後の <c>/</c> または <c>\</c> の後ろ。同名判定のキー。</summary>
        public readonly string FileName;

        /// <summary><see cref="FileName"/> のサフィックスから決まる見た目のバリエーション。</summary>
        public readonly MotionStyle Style;

        public MotionClip(MotionCategory category, string path, string fileName, MotionStyle style)
        {
            Category = category;
            Path = path;
            FileName = fileName;
            Style = style;
        }
    }

    /// <summary>
    /// 全ルート × 全カテゴリの <c>.vrma</c> 一覧。<b>純粋。</b>
    ///
    /// ★ <c>ChatterMascot.Tests.asmdef</c> は <c>ChatterMascot.Runtime</c> しか参照しないので、
    ///   走査の合成ロジック（同名ファイルの勝敗・<c>Style</c> の判定・<c>Pick</c> の乱数）は
    ///   ここに置いてテストを当てる。VRM10 に依存する実際の読み込み（<c>.vrma</c> を開いて
    ///   <c>IVrm10Animation</c> にする）は <c>Vrm/VrmMotionPlayer</c> の仕事で、ここは
    ///   「何をどの優先順で読むべきか」だけを決める。
    /// </summary>
    public sealed class AnimationManifest
    {
        private const string Extension = ".vrma";
        private const string CoolSuffix = "__cool";
        private const string CuteSuffix = "__cute";

        private readonly Dictionary<MotionCategory, List<MotionClip>> _clips;

        private AnimationManifest(Dictionary<MotionCategory, List<MotionClip>> clips)
        {
            _clips = clips;
        }

        /// <summary>
        /// <see cref="AssetPath.AnimationRoots"/> の各ルート × <see cref="MotionCategories.All"/> を
        /// <c>env.ListFiles(Join(root, カテゴリ名), "*.vrma")</c> で舐めて組み立てる。
        ///
        /// ★★ <b>同じ <see cref="MotionClip.FileName"/> は先のルートが勝つ。</b> ルートの優先順は
        ///   <c>persistentDataPath</c> → ユーザー設定 → 同梱（<see cref="AssetPath.AnimationRoots"/>）
        ///   なので、ユーザー拡張が同梱の同名ファイルを覆せる。後のルートで見つかった同名は捨てる。
        /// ★ 同じルート・同じカテゴリ内は <c>Ordinal</c> でソートしてから採る
        ///   （既存の <c>models/*.vrm</c> 走査と同じ理由——既定の比較はカルチャ依存でマシンによって
        ///   選ばれるファイルが変わる）。
        /// ★ <paramref name="bundled"/> は最後のルート（同梱）の<b>後ろ</b>に同じ規則
        ///   （先勝ち・<c>FileName</c> で同名判定）でマージする。<b>今回は誰も渡さない</b>
        ///   （#25 で Android 向けの同梱マニフェスト JSON が入るまで空）——それでも
        ///   引数だけは先に空けておく設計。
        /// </summary>
        public static AnimationManifest Build(AssetEnv env, IReadOnlyList<MotionClip> bundled = null)
        {
            var clips = new Dictionary<MotionCategory, List<MotionClip>>();
            var seenFileNames = new Dictionary<MotionCategory, HashSet<string>>();
            foreach (var category in MotionCategories.All)
            {
                clips[category] = new List<MotionClip>();
                seenFileNames[category] = new HashSet<string>(StringComparer.Ordinal);
            }

            foreach (var root in AssetPath.AnimationRoots(env))
            {
                foreach (var category in MotionCategories.All)
                {
                    var dir = AssetPath.Join(root, MotionCategories.DirectoryName(category));
                    var files = env.ListFiles(dir, "*.vrma") ?? Array.Empty<string>();

                    var sorted = new List<string>(files);
                    sorted.Sort(StringComparer.Ordinal);

                    foreach (var path in sorted)
                    {
                        AddScannedIfNew(clips, seenFileNames, category, path);
                    }
                }
            }

            if (bundled != null)
            {
                foreach (var clip in bundled)
                {
                    AddBundledIfNew(clips, seenFileNames, clip);
                }
            }

            return new AnimationManifest(clips);
        }

        private static void AddScannedIfNew(
            Dictionary<MotionCategory, List<MotionClip>> clips,
            Dictionary<MotionCategory, HashSet<string>> seenFileNames,
            MotionCategory category,
            string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            var fileName = FileNameOf(path);
            if (string.IsNullOrEmpty(fileName)) return;

            // ★ 先のルートで既に採用済みなら捨てる（先勝ち）
            if (!seenFileNames[category].Add(fileName)) return;

            clips[category].Add(new MotionClip(category, path, fileName, StyleOf(fileName)));
        }

        private static void AddBundledIfNew(
            Dictionary<MotionCategory, List<MotionClip>> clips,
            Dictionary<MotionCategory, HashSet<string>> seenFileNames,
            MotionClip clip)
        {
            if (clip == null || string.IsNullOrEmpty(clip.FileName)) return;
            if (!seenFileNames.TryGetValue(clip.Category, out var seen)) return;
            if (!seen.Add(clip.FileName)) return;

            clips[clip.Category].Add(clip);
        }

        /// <summary>
        /// 最後の <c>/</c> または <c>\</c> の後ろ。
        /// ★ <b><c>Path.GetFileName</c> を使わないこと</b>（<c>AssetPath.cs</c> の <c>Join</c> と
        ///   同じ理由——実行マシンの区切り文字に結果が左右され、EditMode テストの期待値が
        ///   Windows で崩れる）。
        /// </summary>
        private static string FileNameOf(string path)
        {
            var slash = path.LastIndexOf('/');
            var backslash = path.LastIndexOf('\\');
            var cut = Math.Max(slash, backslash);
            return cut < 0 ? path : path.Substring(cut + 1);
        }

        /// <summary>
        /// 拡張子を除いた名前が <c>__cool</c> / <c>__cute</c> で終わるかで見た目を決める。
        /// ★ <b>小文字固定・<c>Ordinal</c>（大文字小文字を区別する）。</b> <c>OrdinalIgnoreCase</c>
        ///   にしないこと——ファイル名の規約を「小文字のサフィックス」に決め打つことで、
        ///   ユーザーが偶然 <c>...Cool.vrma</c>（無関係な命名）を置いても誤分類しない。
        /// </summary>
        private static MotionStyle StyleOf(string fileName)
        {
            var stem = fileName.EndsWith(Extension, StringComparison.Ordinal)
                ? fileName.Substring(0, fileName.Length - Extension.Length)
                : fileName;

            if (stem.EndsWith(CoolSuffix, StringComparison.Ordinal)) return MotionStyle.Cool;
            if (stem.EndsWith(CuteSuffix, StringComparison.Ordinal)) return MotionStyle.Cute;
            return MotionStyle.Natural;
        }

        /// <summary>
        /// 読み込みが終わった集合から組み直す（<see cref="VrmMotionPlayer.Loaded"/> 用、#70 レビュー #2）。
        ///
        /// ★★ <b><see cref="Build"/> と違い、ルートの優先順位・同名ファイルの勝敗は判定しない。</b>
        ///   渡された <paramref name="clips"/> をカテゴリで仕分けるだけ——呼び出し側
        ///   （<c>VrmMotionPlayer</c>）が既に <see cref="Clips"/> の並びで渡してくる前提で、
        ///   ここでは並び替えない（同一カテゴリ内の順序をそのまま保つ）。
        /// ★ <c>ChatterMascot.Tests.asmdef</c> に <c>InternalsVisibleTo</c> が無いので
        ///   <c>public</c>（本来のスコープは <c>VrmMotionPlayer</c> だけで足りる）。
        /// </summary>
        public static AnimationManifest FromClips(IEnumerable<MotionClip> clips)
        {
            var byCategory = new Dictionary<MotionCategory, List<MotionClip>>();
            foreach (var category in MotionCategories.All) byCategory[category] = new List<MotionClip>();

            if (clips != null)
            {
                foreach (var clip in clips)
                {
                    if (clip == null) continue;
                    if (!byCategory.TryGetValue(clip.Category, out var list))
                    {
                        list = new List<MotionClip>();
                        byCategory[clip.Category] = list;
                    }
                    list.Add(clip);
                }
            }

            return new AnimationManifest(byCategory);
        }

        /// <summary>指定カテゴリのクリップ一覧。無ければ空（<c>null</c> を返さない）。</summary>
        public IReadOnlyList<MotionClip> Clips(MotionCategory category)
        {
            return _clips.TryGetValue(category, out var list) ? list : Array.Empty<MotionClip>();
        }

        public int Count(MotionCategory category)
        {
            return Clips(category).Count;
        }

        /// <summary>全カテゴリ合計の本数。起動ログの1行に使う。</summary>
        public int TotalCount
        {
            get
            {
                var total = 0;
                foreach (var category in MotionCategories.All) total += Count(category);
                return total;
            }
        }

        /// <summary>
        /// 乱数で1本選ぶ。空なら <c>null</c>。
        /// ★ <paramref name="random"/> は <c>[0,1)</c> を返す関数。
        ///   <c>index = (int)(random() * count)</c> を <c>count-1</c> でクランプする——
        ///   契約が守られていて理論上不要でも、浮動小数の丸めで <c>random()</c> が <c>1.0</c> に
        ///   届いた場合に配列外参照へ落ちないようにする保険。
        /// </summary>
        public MotionClip Pick(MotionCategory category, Func<double> random)
        {
            var list = Clips(category);
            if (list.Count == 0 || random == null) return null;

            var index = (int)(random() * list.Count);
            if (index < 0) index = 0;
            if (index > list.Count - 1) index = list.Count - 1;
            return list[index];
        }

        /// <summary>
        /// ログ用。<c>"idle=5 happy=3 angry=1 sad=2 relaxed=2 surprised=2"</c> の形
        /// （<see cref="MotionCategories.All"/> の順）。
        /// </summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            var first = true;
            foreach (var category in MotionCategories.All)
            {
                if (!first) sb.Append(' ');
                first = false;
                sb.Append(MotionCategories.DirectoryName(category));
                sb.Append('=');
                sb.Append(Count(category));
            }
            return sb.ToString();
        }
    }
}
