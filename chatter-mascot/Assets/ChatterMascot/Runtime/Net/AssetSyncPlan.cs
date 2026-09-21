using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using ChatterMascot.Vrm;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChatterMascot.Net
{
    /// <summary>サーバーのマニフェスト1件（<c>GET /v1/assets</c> の <c>files[]</c>）。<b>不変。</b></summary>
    public readonly struct AssetManifestEntry
    {
        /// <summary><c>synced/</c> からの相対パス。<see cref="AssetSyncPlan"/> が受け付ける3形のみ</summary>
        public readonly string Path;

        public readonly long Size;

        /// <summary>小文字16進64桁。</summary>
        public readonly string Sha256;

        public AssetManifestEntry(string path, long size, string sha256)
        {
            Path = path;
            Size = size;
            Sha256 = sha256;
        }
    }

    /// <summary>
    /// サーバーのマニフェストと、ローカル <c>synced/</c> の実ファイルを突き合わせて、
    /// 取得（<see cref="Fetch"/>）と削除（<see cref="Delete"/>）を決める。<b>純粋関数。</b>
    ///
    /// ★★ <b>台帳ファイルを持たない。</b> ローカルの状態は<paramref name="local"/>で渡す
    ///   実ファイルのハッシュだけを見る——記録用のファイルを別に持つと、途中で切れた
    ///   ダウンロードで記録と実体がズレる余地が生まれる。実ファイルだけを見ていれば、
    ///   切れたファイルは次回のスキャンで<b>自然に取得対象へ戻る</b>。
    /// </summary>
    public sealed class AssetSyncPlan
    {
        /// <summary>マニフェストが読めたか。<c>false</c> なら <see cref="Fetch"/> / <see cref="Delete"/> は空。</summary>
        public readonly bool ManifestOk;

        /// <summary>取得すべきエントリ（ローカルに無い、またはハッシュが違う）。</summary>
        public readonly IReadOnlyList<AssetManifestEntry> Fetch;

        /// <summary>ローカルにあるがマニフェストに無いファイル（<c>synced/</c> 相対パス）。</summary>
        public readonly IReadOnlyList<string> Delete;

        /// <summary>読めたマニフェストの全件。<c>AssetSyncClient</c> が孤児の <c>.part</c> を掃除するのに使う。</summary>
        public readonly IReadOnlyList<AssetManifestEntry> Manifest;

        private AssetSyncPlan(
            bool manifestOk,
            IReadOnlyList<AssetManifestEntry> fetch,
            IReadOnlyList<string> delete,
            IReadOnlyList<AssetManifestEntry> manifest)
        {
            ManifestOk = manifestOk;
            Fetch = fetch;
            Delete = delete;
            Manifest = manifest;
        }

        /// <summary>
        /// マニフェスト JSON とローカルの走査結果（<c>synced/</c> 相対パス → sha256）から計画を組む。
        ///
        /// ★ <b>マニフェストが読めなかったら <see cref="Delete"/> を1件も出さないこと。</b>
        ///   取得に失敗したときは前回のキャッシュを消してはいけない——差分を計算する前に
        ///   打ち切っておけば、呼び出し側は <see cref="ManifestOk"/> だけ見ればよい。
        ///
        /// ★ <b>読めた <c>entries</c> が0件でも <see cref="Delete"/> を1件も出さないこと。</b>
        ///   「空」と「サーバーの設定ミス」は区別できない——別のランタイムルートで起動した、
        ///   素材を一時的に退避した、というだけで端末のキャッシュが丸ごと消え、細い経路で
        ///   数十 MB を取り直すことになる。消さずに残す側に倒すと古いファイルが残るだけで、
        ///   コストが釣り合わない。★ 1件でも載っていれば従来どおり差分削除は効く。
        /// </summary>
        public static AssetSyncPlan Build(string manifestJson, IReadOnlyDictionary<string, string> local)
        {
            List<AssetManifestEntry> entries;
            if (!TryParseManifest(manifestJson, out entries))
            {
                return new AssetSyncPlan(
                    false, Array.Empty<AssetManifestEntry>(), Array.Empty<string>(), Array.Empty<AssetManifestEntry>());
            }

            if (entries.Count == 0)
            {
                return new AssetSyncPlan(true, Array.Empty<AssetManifestEntry>(), Array.Empty<string>(), entries);
            }

            var localFiles = local ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var fetch = new List<AssetManifestEntry>();
            // ★ マニフェストに出てきた path を消していく。残ったものがローカルだけにあるファイル
            var remaining = new HashSet<string>(localFiles.Keys, StringComparer.Ordinal);

            foreach (var entry in entries)
            {
                remaining.Remove(entry.Path);

                string hash;
                var upToDate = localFiles.TryGetValue(entry.Path, out hash)
                    && string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase);
                if (!upToDate) fetch.Add(entry);
            }

            var delete = new List<string>(remaining);
            // ★ Ordinal でソート。他の走査（AssetPath / AnimationManifest）と同じ理由——
            //   ログや削除の順序がマシンで揺れないようにする
            delete.Sort(StringComparer.Ordinal);

            return new AssetSyncPlan(true, fetch, delete, entries);
        }

        // ── マニフェストのパース ────────────────────────────────

        private static readonly Regex Sha256Pattern = new Regex(@"\A[0-9a-fA-F]{64}\z", RegexOptions.None);

        /// <summary><c>models/mascot.vrm</c>。<see cref="AssetPath"/> の定数から組み立てる——ここだけの二重管理にしない。</summary>
        private static readonly string ModelPath = AssetPath.Join(AssetPath.ModelsDirectory, AssetPath.SelectedVrmFile);

        /// <summary>
        /// <c>animations/idle.vrma</c>。★ <c>AssetPath.Of(AssetKind.Vrma)</c> の固定名と同じだが、
        /// あちらは <c>private</c> なのでここに写している（値は待機ループの固定名という契約そのもの）。
        /// </summary>
        private const string IdleAnimationFile = "idle.vrma";

        private static readonly string IdleAnimationPath = AssetPath.Join(AssetPath.AnimationsDirectory, IdleAnimationFile);

        private static readonly HashSet<string> CategoryDirectories = BuildCategoryDirectories();

        private static HashSet<string> BuildCategoryDirectories()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var category in MotionCategories.All) set.Add(MotionCategories.DirectoryName(category));
            return set;
        }

        /// <summary>
        /// <c>GET /v1/assets</c> の応答を読む。<b>壊れていたら例外を投げず false。</b>
        ///
        /// ★ <b>1件でも契約の3形から外れたら全体を読めなかったことにする。</b> このマニフェストは
        ///   ローカルの書き込み先（<c>synced/&lt;path&gt;</c>）とサーバーへの GET パスの両方の材料になる
        ///   ——部分的に信用すると <c>../</c> のような経路挿入の入り口になりうる。件数を絞って
        ///   出せるものだけ出す（<c>CoreConfigClient.ReadSpeakers</c>）作法はここでは採らない。
        /// </summary>
        private static bool TryParseManifest(string raw, out List<AssetManifestEntry> entries)
        {
            entries = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            JObject root;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(raw)))
                {
                    reader.DateParseHandling = DateParseHandling.None;
                    root = JToken.Load(reader) as JObject;
                }
            }
            catch (Exception)
            {
                return false;
            }
            if (root == null) return false;

            var files = root["files"] as JArray;
            if (files == null) return false;

            var list = new List<AssetManifestEntry>(files.Count);
            foreach (var item in files)
            {
                var obj = item as JObject;
                if (obj == null) return false;

                var path = obj["path"];
                var size = obj["size"];
                var sha256 = obj["sha256"];
                if (path == null || path.Type != JTokenType.String) return false;
                if (size == null || size.Type != JTokenType.Integer) return false;
                if (sha256 == null || sha256.Type != JTokenType.String) return false;

                var pathText = path.Value<string>();
                var shaText = sha256.Value<string>();
                if (!IsAllowedPath(pathText)) return false;
                if (!Sha256Pattern.IsMatch(shaText)) return false;

                long sizeValue;
                try
                {
                    sizeValue = size.Value<long>();
                }
                catch (Exception)
                {
                    return false;
                }
                if (sizeValue < 0) return false;

                list.Add(new AssetManifestEntry(pathText, sizeValue, shaText));
            }

            entries = list;
            return true;
        }

        /// <summary>
        /// <c>models/mascot.vrm</c> / <c>animations/idle.vrma</c> / <c>animations/&lt;category&gt;/&lt;name&gt;.vrma</c>
        /// の3形だけを通す（サーバーとの契約）。
        /// </summary>
        private static bool IsAllowedPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (string.Equals(path, ModelPath, StringComparison.Ordinal)) return true;
            if (string.Equals(path, IdleAnimationPath, StringComparison.Ordinal)) return true;

            var segments = path.Split('/');
            if (segments.Length != 3) return false;
            if (!string.Equals(segments[0], AssetPath.AnimationsDirectory, StringComparison.Ordinal)) return false;
            if (!CategoryDirectories.Contains(segments[1])) return false;
            return AnimationFilePattern.IsMatch(segments[2]);
        }

        /// <summary>
        /// <c>animations/&lt;category&gt;/</c> のファイル名。
        ///
        /// ★★ <b>サーバー側（<c>core/src/core/assetPath.ts</c>）の文字集合と同じにすること。</b>
        ///   この値はローカルの<b>書き込み先</b>と、サーバーへ投げる<b>URL</b>の両方の材料になる。
        ///   緩めるとサーバーが決して配らない名前を書き込み先として受け入れることになり、
        ///   厳しくすると配られたものを取りこぼす。
        ///
        /// ★ <c>\A</c> / <c>\z</c> で括ること。<b>.NET の <c>$</c> は末尾の改行を通す</b>ので、
        ///   <c>$</c> で書くと JS 側の正規表現より緩くなる。
        /// </summary>
        private static readonly Regex AnimationFilePattern =
            new Regex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\.vrma\z", RegexOptions.None);
    }
}
