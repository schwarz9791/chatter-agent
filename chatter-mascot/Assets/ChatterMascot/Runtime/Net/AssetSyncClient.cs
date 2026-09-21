using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ChatterMascot.Vrm;
using UnityEngine.Networking;

namespace ChatterMascot.Net
{
    /// <summary>
    /// <c>GET /v1/assets</c> と <c>GET /v1/assets/&lt;path&gt;</c> を叩いて、<c>synced/</c> を
    /// サーバーの内容へ合わせる（#117。Android / XR が <c>adb push</c> 無しでモデル・モーションを
    /// 手に入れる経路）。
    ///
    /// ★ <c>AudioFetcher</c> と同じ形にしてある（<c>UnityWebRequest</c> + <c>Authorization: Bearer</c>、
    ///   秒単位のタイムアウト）。★ <b>発話経路と独立に走らせること。</b> 失敗はログだけにして、
    ///   テキストと音声の配信を止めない——呼び出し側は <see cref="SyncAsync"/> を
    ///   <c>_ = client.SyncAsync()</c> の fire-and-forget で起こす。
    /// </summary>
    public sealed class AssetSyncClient
    {
        private const string ManifestPath = "/v1/assets";
        private const string AssetPathPrefix = "/v1/assets/";

        /// <summary>ダウンロード中のファイルの置き場所（<c>synced/.parts/&lt;sha256&gt;.part</c>）。</summary>
        private const string PartsDirectory = ".parts";
        private const string PartExtension = ".part";

        /// <summary>受け取ったログを流す先。<c>SpeechClient</c> と同じ「呼び出し側が Debug.Log へ流す」形。</summary>
        public event Action<string> Log;

        public event Action<string> Warn;

        /// <summary>
        /// 1回ぶんの同期が終わった。<c>(取得できた件数, 取得しようとした件数, 消した件数)</c>。
        ///
        /// ★ <b>出す文面をここで決めないこと。</b> 何に使うか（ログ / 端末の通知）は
        ///   呼び出し側の都合なので、判断できる材料だけ渡す（→ <see cref="DescribeResult"/>）。
        /// </summary>
        public event Action<int, int, int> Completed;

        /// <summary>
        /// マニフェストの取得・解釈・中身のいずれかで躓き、この回の同期を諦めた。文面は
        /// <see cref="ManifestUnreachableMessage"/> / <see cref="ManifestUnreadableMessage"/> /
        /// <see cref="ManifestEmptyMessage"/> のいずれか。
        ///
        /// ★ <b>サーバーが落ちている・端末が別の Wi-Fi にいる・トークンが古い、という
        ///   一番踏む失敗が無音にならないよう、ここで端末に届ける。</b> <see cref="Completed"/> と
        ///   違って文面はここで決め切る——判断材料ではなく、そのまま出す1行を渡す。
        /// </summary>
        public event Action<string> Failed;

        // ★ 端末に出す文面の改行は**リテラルに書く**（DescribeResult も同じ）。トーストは幅が
        //   狭いので折り返したいが、切る位置は文面ごとに違う——「。」で機械的に折ると、
        //   切りたくない文まで巻き込む。
        //
        // ★ 3つとも2行目を揃える。躓いた理由は違っても**次にやることは同じ**（何もしなくてよい）
        //   なので、違う言い方をすると対処が違うように読める。

        /// <summary>マニフェストを取得できなかった（接続できない・応答が無い）ときの文面。</summary>
        internal const string ManifestUnreachableMessage =
            "サーバーに繋がりません。\n以前に設定されたモデルとモーションを使用します。";

        /// <summary>マニフェストは取得できたが読めなかった（契約から外れている）ときの文面。</summary>
        internal const string ManifestUnreadableMessage =
            "サーバーの応答を読めませんでした。\n以前に設定されたモデルとモーションを使用します。";

        /// <summary>マニフェストは読めたが素材が1件も載っていなかったときの文面。</summary>
        internal const string ManifestEmptyMessage =
            "サーバーにモデルとモーションがありません。\n以前に設定されたモデルとモーションを使用します。";

        public readonly string BaseUrl;
        private readonly int _timeoutSeconds;
        private readonly string _token;
        private readonly string _syncedRoot;

        /// <param name="baseUrl"><c>http(s)://host:port</c>（→ <see cref="ServerUrl.ToHttpBase"/>）</param>
        /// <param name="timeoutMs">1リクエストの上限。モデルの取得は数十 MB になりうるので、音声より長く取ること。</param>
        /// <param name="token">非ループバックの接続に要る共有トークン。空か <c>null</c> なら付けない。</param>
        /// <param name="syncedRoot"><c>persistentDataPath/synced</c>（<see cref="AssetPath.SyncedDirectory"/>）。</param>
        public AssetSyncClient(string baseUrl, int timeoutMs, string token, string syncedRoot)
        {
            BaseUrl = baseUrl;
            // UnityWebRequest.timeout は秒単位の int。0 は「無制限」なので必ず 1 以上にする
            _timeoutSeconds = Math.Max(1, (int)Math.Ceiling(timeoutMs / 1000.0));
            _token = token;
            _syncedRoot = syncedRoot;
        }

        /// <summary>
        /// 1回ぶんの同期。★ <b>反映は次回の起動から</b>——<c>VrmStage</c> / <c>VrmIdleAnimation</c> /
        /// <c>VrmMotionPlayer</c> は起動時に1回だけ読むので、ここで取得しても今のセッションの
        /// 見た目は変わらない。
        ///
        /// ★ 呼び出し側は <c>_ = client.SyncAsync()</c> の fire-and-forget で起こす想定
        ///   （<c>MascotRunner.StartAssetSyncIfNeeded</c>）。<b>ここで例外を漏らさないこと。</b>
        ///   誰も <c>await</c> しない <c>Task</c> が fault すると、その例外は
        ///   <b>誰にも観測されずに捨てられる</b>（<c>SpeechClient.RunAsync</c> と同じ理由）。
        /// </summary>
        public async Task SyncAsync()
        {
            try
            {
                await SyncCoreAsync();
            }
            catch (Exception e)
            {
                // ★ 最後の受け皿。個々の失敗しうる処理は自分で握っているので、
                //   ここに来るのは想定外だけ——可視化のためだけに置く
                Warn?.Invoke("[AssetSync] 同期が異常終了しました: " + e.Message);
            }
        }

        private async Task SyncCoreAsync()
        {
            string manifestJson;
            try
            {
                manifestJson = await FetchManifestAsync();
            }
            catch (Exception e)
            {
                // ★ 取得に失敗したら何もしない。前回のキャッシュを消さない
                Warn?.Invoke("[AssetSync] マニフェストを取得できませんでした: " + e.Message);
                Failed?.Invoke(ManifestUnreachableMessage);
                return;
            }

            if (manifestJson == null)
            {
                Warn?.Invoke("[AssetSync] マニフェストを取得できませんでした");
                Failed?.Invoke(ManifestUnreachableMessage);
                return;
            }

            var plan = AssetSyncPlan.Build(manifestJson, ScanLocal());
            if (!plan.ManifestOk)
            {
                Warn?.Invoke("[AssetSync] マニフェストを読めませんでした。前回の内容のまま使います");
                Failed?.Invoke(ManifestUnreadableMessage);
                return;
            }

            // ★★ **空のマニフェストでは何も片付けないこと。** `AssetSyncPlan.Build` が
            //   `Delete` を出さないのと同じ理由——「空」と「サーバーの設定ミス」は区別できない。
            //   ここで `CleanupOrphanParts` まで進むと、**取りかけの `.part` が全部「マニフェストに
            //   無いもの」になって消える**ので、細い経路で落としかけていた数十 MB が最初からになる。
            if (plan.Manifest.Count == 0)
            {
                Warn?.Invoke("[AssetSync] サーバーに素材がありません。前回の内容のまま使います");
                // ★ ここも端末に届ける。素材の置き忘れは**ユーザーに手の打てる状態**なので、
                //   繋がらなかったときと同じく無音にしない
                Failed?.Invoke(ManifestEmptyMessage);
                return;
            }

            var fetched = 0;
            foreach (var entry in plan.Fetch)
            {
                if (await FetchOneAsync(entry)) fetched++;
            }

            // ★ 取得の後に消すこと。先に消すと、取得が失敗したときに
            //   さっきまで動いていたものまで失う
            foreach (var path in plan.Delete) DeleteLocal(path);

            CleanupOrphanParts(plan.Manifest);

            Log?.Invoke($"[AssetSync] 完了: 取得 {fetched}/{plan.Fetch.Count} 件、削除 {plan.Delete.Count} 件。" +
                        "反映は次回の起動からです");
            Completed?.Invoke(fetched, plan.Fetch.Count, plan.Delete.Count);
        }

        /// <summary>
        /// 端末に出す1行。<b>言うことが無ければ <c>null</c>。</b>
        ///
        /// ★ <b>何も変わらなかった起動では出さない。</b> 定常状態ではマニフェストしか流れないので、
        ///   毎回出すと「変わっていない」ことを知らせるだけの通知が起動のたびに出る。
        ///
        /// ★ <b>取りきれなかったことを隠さない。</b> 細い回線では1回の起動で終わらない。
        ///   「次の起動で続きを取る」と言えば、もう一度立ち上げればよいと分かる。
        /// </summary>
        public static string DescribeResult(int fetched, int planned, int deleted)
        {
            // 取りに行くものも消すものも無かった＝定常状態
            if (planned <= 0 && deleted <= 0) return null;

            // ★ 1件も取れなかったときに「更新した」と言わないこと。取りに行って
            //   全部こぼしたのと、取って反映待ちなのは、次にやることが違う
            if (planned > 0 && fetched <= 0)
            {
                return "モデルとモーションを取得できませんでした。\n次に起動したときにやり直します。";
            }

            if (fetched < planned)
            {
                return $"モデルとモーションの一部を更新しました（{fetched}/{planned} 件）。\n" +
                       "次に起動したときに続きを取りに行きます。";
            }

            return "モデルとモーションを更新しました。\n次に起動したときから反映されます。";
        }

        private async Task<string> FetchManifestAsync()
        {
            using (var request = UnityWebRequest.Get(BaseUrl + ManifestPath))
            {
                request.timeout = _timeoutSeconds;
                request.downloadHandler = new DownloadHandlerBuffer();
                SetAuthHeader(request);

                await SendAsync(request);

                if (request.result != UnityWebRequest.Result.Success || request.responseCode != 200) return null;
                return request.downloadHandler.text;
            }
        }

        /// <summary>
        /// 1本取得する。揃ってハッシュが一致したときだけ <c>synced/&lt;path&gt;</c> へ置き換える。
        /// </summary>
        private async Task<bool> FetchOneAsync(AssetManifestEntry entry)
        {
            var partsDir = AssetPath.Join(_syncedRoot, PartsDirectory);
            try
            {
                Directory.CreateDirectory(partsDir);
            }
            catch (Exception e)
            {
                Warn?.Invoke($"[AssetSync] {entry.Path}: 保存先を用意できませんでした: {e.Message}");
                return false;
            }
            var partPath = AssetPath.Join(partsDir, entry.Sha256 + PartExtension);

            var existingLength = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

            // ★★ **.part が entry.Size ぶん以上あるときは HTTP をまったく叩かないこと。**
            //   ちょうど entry.Size ぶん届いている .part に Range: bytes=<size>- を投げると、
            //   満たせる範囲が無いのでサーバーは 416 を返し、下の resumable 判定が false になって
            //   すぐ下の ★★ に反して完全に正しいファイルを消して0から引き直すことになる。
            //   検証（ハッシュ照合）の段へそのまま合流させる——一致すれば採用、不一致なら
            //   （サイズ超過の壊れた .part も含めて）そこで片付く。届いた経路によらず検証は同じでよい。
            //
            // ★★ **existingLength > 0 を条件から落とさないこと。** entry.Size が 0 の資産で
            //   「存在しない .part をハッシュしようとして毎回失敗する」ループに落ちる。
            if (!(existingLength > 0 && existingLength >= entry.Size))
            {
                var append = existingLength > 0;

                using (var request = UnityWebRequest.Get(BaseUrl + AssetPathPrefix + entry.Path))
                {
                    request.timeout = _timeoutSeconds;
                    SetAuthHeader(request);
                    if (append) request.SetRequestHeader("Range", "bytes=" + existingLength + "-");

                    // ★ メモリに載せずディスクへ流す。モデルは数十 MB になるので DownloadHandlerBuffer は使わない
                    request.downloadHandler = new DownloadHandlerFile(partPath, append) { removeFileOnAbort = false };

                    try
                    {
                        await SendAsync(request);
                    }
                    catch (Exception e)
                    {
                        // ★ 例外はここまで届いた分の .part を残す。次回、続きから取り直せる
                        Warn?.Invoke($"[AssetSync] {entry.Path}: 取得できませんでした（続きから再試行します）: {e.Message}");
                        return false;
                    }

                    var expected = append ? 206 : 200;
                    if (request.result != UnityWebRequest.Result.Success || request.responseCode != expected)
                    {
                        // ★★ **途中まで届いた .part を捨てるのは、中身が信用できないと分かったときだけ。**
                        //   細い経路を前提にした仕組みなので、転送が途中で切れるのは普通のこと。ここで
                        //   一律に消すと毎回ゼロからやり直しになり、**再開できる作りにした意味が消える**。
                        //
                        //   ・期待どおりの応答（206 / 200）だった → 届いた分は正しい続き。残す
                        //   ・応答そのものが無い（responseCode == 0）→ 何も書かれていない。残す
                        //   ・それ以外 → 範囲指定を無視した全体やエラー本文が .part に混ざった可能性が
                        //     ある。捨てて次回また最初から
                        var resumable = request.responseCode == expected || request.responseCode == 0;
                        if (!resumable) TryDelete(partPath);
                        Warn?.Invoke($"[AssetSync] {entry.Path}: 取得に失敗しました (HTTP {request.responseCode})" +
                                     (resumable ? "。次回は続きから取り直します" : ""));
                        return false;
                    }
                }
            }

            string actualHash;
            try
            {
                actualHash = ComputeSha256(partPath);
            }
            catch (Exception e)
            {
                Warn?.Invoke($"[AssetSync] {entry.Path}: 検証できませんでした: {e.Message}");
                return false;
            }

            // ★ 揃ってから置き換える。一致しなければ .part を捨てる
            if (!string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(partPath);
                Warn?.Invoke($"[AssetSync] {entry.Path}: 取得した内容が一致しませんでした");
                return false;
            }

            var finalPath = AssetPath.Join(_syncedRoot, entry.Path);
            try
            {
                var finalDir = DirectoryOf(finalPath);
                if (finalDir != null) Directory.CreateDirectory(finalDir);

                // ★★ **置き換えは1回の rename で済ませること。** 同期は Awake から起こされ、
                //   モデル・モーションの読み込み（VrmStage / VrmMotionPlayer）は Start から走る
                //   ——**両者は並走する**。消してから書く形にすると、その隙間に読んだ側が
                //   ファイルを見失い、同梱のモデルに落ちる。
                //
                // ★ `File.Move` に overwrite 付きの多重定義は無い（このプロジェクトの
                //   API 互換レベルは .NET Standard 2.0）。宛先があるときは `File.Replace` を使う
                if (File.Exists(finalPath)) File.Replace(partPath, finalPath, null);
                else File.Move(partPath, finalPath);
            }
            catch (Exception e)
            {
                Warn?.Invoke($"[AssetSync] {entry.Path}: 置き換えられませんでした: {e.Message}");
                return false;
            }

            return true;
        }

        /// <summary>マニフェストに無い <c>.part</c> を掃除する。サーバー側が差し替わったときの残骸。</summary>
        private void CleanupOrphanParts(IReadOnlyList<AssetManifestEntry> manifest)
        {
            var partsDir = AssetPath.Join(_syncedRoot, PartsDirectory);
            if (string.IsNullOrEmpty(partsDir) || !Directory.Exists(partsDir)) return;

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in manifest) known.Add(entry.Sha256);

            string[] files;
            try
            {
                files = Directory.GetFiles(partsDir);
            }
            catch (Exception)
            {
                return;
            }

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (string.IsNullOrEmpty(name) || !name.EndsWith(PartExtension, StringComparison.Ordinal)) continue;

                var hash = name.Substring(0, name.Length - PartExtension.Length);
                if (!known.Contains(hash)) TryDelete(file);
            }
        }

        private void DeleteLocal(string relativePath)
        {
            TryDelete(AssetPath.Join(_syncedRoot, relativePath));
        }

        /// <summary><c>synced/</c> を走査してハッシュを取る。<b>台帳は持たない</b>（→ <see cref="AssetSyncPlan"/> の doc）。</summary>
        private IReadOnlyDictionary<string, string> ScanLocal()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(_syncedRoot) || !Directory.Exists(_syncedRoot)) return result;

            string[] files;
            try
            {
                files = Directory.GetFiles(_syncedRoot, "*", SearchOption.AllDirectories);
            }
            catch (Exception)
            {
                return result;
            }

            var partsPrefix = PartsDirectory + "/";
            foreach (var file in files)
            {
                var rel = RelativePath(file);
                if (rel == null || rel.StartsWith(partsPrefix, StringComparison.Ordinal)) continue;

                try
                {
                    result[rel] = ComputeSha256(file);
                }
                catch (Exception)
                {
                    // ★ 読めないファイルはハッシュが分からないまま = 常に取得対象になる。捨てない
                }
            }
            return result;
        }

        private string RelativePath(string absolute)
        {
            var normalized = absolute.Replace('\\', '/');
            var root = _syncedRoot.Replace('\\', '/').TrimEnd('/') + "/";
            if (!normalized.StartsWith(root, StringComparison.Ordinal)) return null;
            return normalized.Substring(root.Length);
        }

        private static string DirectoryOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var slash = path.LastIndexOf('/');
            return slash < 0 ? null : path.Substring(0, slash);
        }

        private static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                var hash = sha.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
                // 消せなくても致命ではない。次のスキャン・掃除でまた候補になる
            }
        }

        private void SetAuthHeader(UnityWebRequest request)
        {
            if (!string.IsNullOrEmpty(_token))
            {
                request.SetRequestHeader("Authorization", "Bearer " + _token);
            }
        }

        /// <summary><c>UnityWebRequest</c> を <c>await</c> できる形にする（<c>AudioFetcher</c> と同じ）。</summary>
        private static Task SendAsync(UnityWebRequest request)
        {
            var tcs = new TaskCompletionSource<bool>();
            var operation = request.SendWebRequest();
            operation.completed += _ => tcs.TrySetResult(true);
            return tcs.Task;
        }
    }
}
