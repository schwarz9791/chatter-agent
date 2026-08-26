using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace ChatterMascot.Vrm
{
    /// <summary>読み込めた1件。</summary>
    public readonly struct LoadedBytes
    {
        public readonly AssetCandidate Candidate;
        public readonly byte[] Bytes;

        public LoadedBytes(AssetCandidate candidate, byte[] bytes)
        {
            Candidate = candidate;
            Bytes = bytes;
        }

        public bool IsEmpty => Bytes == null;
    }

    /// <summary>
    /// <see cref="AssetPath.Enumerate"/> が並べた候補を<b>1件ずつ</b>読む。
    /// <b>どれを採るかは呼び出し側が決める</b>（→ <c>VrmStage.LoadAsync</c>）。
    ///
    /// ★ <b><c>File.ReadAllBytes</c> ではなく <c>UnityWebRequest</c> を使う。</b>
    ///   Android の <c>streamingAssetsPath</c> は APK 内の <c>jar:file://…</c> で、
    ///   パスからは読めない。<c>UnityWebRequest</c> なら <c>file://</c> も jar URL も
    ///   同じコードで読め、#25 で「macOS では出るのに XR で出ない」を踏まずに済む。
    ///
    /// ★ <b>存在確認を先にしないこと。</b> 同じ理由で <c>File.Exists</c> は
    ///   Android の同梱アセットに対して<b>必ず false</b> を返す（→ <see cref="AssetPath"/>）。
    /// </summary>
    public static class VrmAssetLoader
    {
        /// <summary>
        /// ★ <b>0 にしないこと。</b> <c>UnityWebRequest.timeout</c> の 0 は「無制限」。
        /// </summary>
        private const int TimeoutSeconds = 30;

        /// <summary>
        /// 自前の期限。
        ///
        /// ★ <b><c>UnityWebRequest.timeout</c> は <c>file://</c> には効かない。</b> 実測で、
        ///   <b>macOS の TCC で保護されたフォルダ</b>（<c>~/Downloads</c> / <c>~/Desktop</c> /
        ///   <c>~/Documents</c>）のモデルを <c>-vrm</c> で渡すと、リクエストが
        ///   <b>返らず・エラーも出さず・30秒の timeout も発火しない</b>。症状は
        ///   「モデルが出ないままログが1行も増えない」で、同梱モデルへのフォールバックにも
        ///   落ちない —— このリポジトリで言う「動いて見える死体」そのもの。
        ///
        /// ★ <b>だから探索順の各段に自前の期限を置く。</b> 期限で打ち切って次の候補へ進めば、
        ///   最悪でも同梱モデルまでは落ちる。
        /// </summary>
        private const int DeadlineMs = 15000;

        /// <summary>
        /// 候補を<b>1件だけ</b>読む。読めなければ <see cref="LoadedBytes.IsEmpty"/>。
        ///
        /// ★ <b>「読めた」で確定させないこと。</b> ここが返すのは<b>バイト列が取れた</b>という
        ///   事実だけで、VRM 1.0 として解釈できるかはパースするまで分からない。
        ///   <c>-vrm</c> に VRM 0.x や別形式を渡すと<b>読めてパースで落ちる</b>ので、
        ///   確定は<b>パースまで通った時点</b>で呼び出し側が行う。
        ///
        /// ★ <b>探索順のループをここへ戻さないこと。</b> 戻すとパース（UniVRM 依存）を
        ///   このクラスへ引き込むことになり、「読む」と「解釈する」が1つに癒着する。
        /// </summary>
        public static async Task<LoadedBytes> ReadAsync(AssetCandidate candidate, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var bytes = await TryReadAsync(candidate.Path, ct);
            if (bytes == null || bytes.Length == 0) return default;

            // ★ 「読めた」であって「採った」ではない。文言を混ぜないこと
            Debug.Log($"[Mascot] {candidate.Source} から {bytes.Length:N0} バイト読みました: {candidate.Path}");
            return new LoadedBytes(candidate, bytes);
        }

        /// <summary>
        /// 見つからなかったときのログ。★ <b>候補を全部並べること。</b>
        /// 「モデルが出ない」の原因が探索順のどこで外れたかは、これが無いと分からない。
        /// </summary>
        public static string DescribeCandidates(IReadOnlyList<AssetCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0) return "（候補が1つもありません）";

            var text = new StringBuilder();
            foreach (var candidate in candidates)
            {
                text.Append("\n  ").Append(candidate.ToString());
            }
            return text.ToString();
        }

        private static async Task<byte[]> TryReadAsync(string path, CancellationToken ct)
        {
            var url = ToUrl(path);
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = TimeoutSeconds;
                request.downloadHandler = new DownloadHandlerBuffer();

                try
                {
                    var send = SendAsync(request, ct);

                    // ★ **期限は linked CTS で切ること。** `Task.Delay(ms, ct)` を裸で使うと
                    //   送信が先に終わってもタイマーが 15 秒生き残り、候補のぶんだけ積む。
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        var finished = await Task.WhenAny(send, Task.Delay(DeadlineMs, deadline.Token));
                        // using を抜けるときに Cancel されるので、勝った方に関わらずタイマーは片付く
                        deadline.Cancel();

                        // ★ **キャンセルを期限と取り違えないこと。** `Task.Delay(ms, ct)` は
                        //   `Cancel()` の中でその場で Canceled になるのに対し、`send` は
                        //   `ContinueWith(..., FromCurrentSynchronizationContext())` を挟むので
                        //   Unity のメインスレッドのポンプを1回待つ。つまり終了時は
                        //   **必ず Delay が先に返る**。ここを見ないと、読み込み中にアプリを
                        //   閉じただけで「アクセス権で止められている」と出て、
                        //   **存在しない macOS の権限問題を次の担当者に追わせる**
                        if (ct.IsCancellationRequested)
                        {
                            request.Abort();
                            throw new OperationCanceledException(ct);
                        }

                        if (finished != send)
                        {
                            // ★ Abort しないと、この後もダウンロードハンドラが生きたまま残る
                            request.Abort();
                            Debug.LogWarning(
                                $"[Mascot] {path} の読み込みが {DeadlineMs / 1000} 秒で返らないので諦めます。" +
                                "macOS では ~/Downloads / ~/Desktop / ~/Documents のファイルが" +
                                "アクセス権で止められることがあります（システム設定 > プライバシーとセキュリティ）");
                            return null;
                        }
                    }
                    await send;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    Debug.Log($"[Mascot] {path} は読めませんでした: {e.Message}");
                    return null;
                }

                // ★ **responseCode で判定しないこと。** file:// と jar: では 0 になる。
                //   AudioFetcher が 200 を要求しているのは相手が HTTP だから
                if (request.result != UnityWebRequest.Result.Success)
                {
                    // 「無い」は正常な分岐（探索順の途中）なので Log 止まり
                    Debug.Log($"[Mascot] {path} は読めませんでした: {request.error}");
                    return null;
                }
                return request.downloadHandler.data;
            }
        }

        /// <summary>
        /// ★ <b>絶対パスは <c>file://</c> を付ける。</b> 付けないと
        ///   <c>UnityWebRequest</c> が相対 URL として解釈して失敗する。
        ///   すでにスキーム付き（Android の <c>jar:file://…</c>）ならそのまま。
        /// </summary>
        private static string ToUrl(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.Contains("://")) return path;
            return "file://" + path;
        }

        /// <summary>
        /// <c>UnityWebRequest</c> を <c>await</c> できる形にする。
        /// <c>Net/AudioFetcher.cs</c> と同じ形（あちらは HTTP 専用なので別に持っている）。
        ///
        /// ★ <c>completed</c> は Unity のメインスレッドで呼ばれ、await の継続も
        ///   Unity の <c>SynchronizationContext</c> でメインスレッドに戻る。だから
        ///   呼び出し側は <c>GameObject</c> をそのまま触れる。
        /// </summary>
        private static Task SendAsync(UnityWebRequest request, CancellationToken ct)
        {
            var completion = new TaskCompletionSource<bool>();
            var operation = request.SendWebRequest();
            operation.completed += _ => completion.TrySetResult(true);

            if (!ct.CanBeCanceled) return completion.Task;

            // ★ 登録を解除すること。解除しないと、キャンセル済みトークンに
            //   読み込みのたびにハンドラが積まれる
            var registration = ct.Register(() =>
            {
                request.Abort();
                completion.TrySetCanceled(ct);
            });
            return completion.Task.ContinueWith(task =>
            {
                registration.Dispose();
                return task;
            }, TaskScheduler.FromCurrentSynchronizationContext()).Unwrap();
        }
    }
}
