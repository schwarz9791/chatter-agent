#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ChatterMascot.Audio
{
    /// <summary>
    /// 外部プロセスで鳴らすときのハンドル。
    ///
    /// ★ <see cref="ILipSyncSource"/> のぶんだけ自動プロパティなのは、C# が
    ///   インターフェースのメンバーをフィールドで実装できないため（→ <see cref="ILipSyncSource"/>）。
    /// </summary>
    public sealed class AfplayAudioHandle : ILipSyncSource, IAudioDuration
    {
        public string Path;

        /// <summary>再生時間（ミリ秒）。<b>0 は「長さ 0」ではなく「不明」</b></summary>
        public int DurationMs { get; set; }

        /// <summary><c>null</c> 可（口が動かないだけ。発話は落とさない）</summary>
        public float[] Envelope { get; set; }

        public int EnvelopeFrameMs { get; set; }
    }

    /// <summary>
    /// WAV を一時ファイルに落として <c>afplay</c> で鳴らす。<b>参照実装（<c>core/src/player/audioPlayer.ts</c>）と同じ形</b>。
    ///
    /// ★ <b>なぜ Unity の <see cref="AudioSource"/> を使わないか。</b> Unity 内蔵オーディオは
    ///   <b>無音でも macOS の出力デバイスを掴み続ける</b>（実測: <c>AudioSource.Play()</c> を
    ///   一度も呼ばなくても起動から終了までずっと）。手放す API は macOS には無い。
    ///   1発話 = 1プロセスにすると、<b>プロセスが消えた時点で OS がデバイスを解放する</b> ——
    ///   参照実装で実測すると発話終了から <b>0.5〜1秒</b>で解放されている。
    ///
    /// ★ <b>これだけでは足りない。</b> Unity 内蔵オーディオが有効なままだと、
    ///   <see cref="AudioSource"/> を1つも鳴らさなくても Unity 側がデバイスを掴む。
    ///   <b><c>Disable Unity Audio</c> を ON にしたビルドでのみ意味がある</b>
    ///   （→ <c>BuildScript.BuildMacOS</c> がビルド時だけ切り替える）。
    ///
    /// ★ <b>並行再生（孤児を鳴らし切る契約）は無料で成立する。</b> 1発話 = 1プロセスなので
    ///   <c>Kill()</c> の効果が自分のプロセスに閉じる。<see cref="AudioClipPlayer"/> が
    ///   voice プールで人工的に作っていた性質が、ここでは構造的にそうなる。
    /// </summary>
    public sealed class AfplaySpeechPlayer : ISpeechPlayer
    {
        /// <summary>→ <see cref="AudioClipPlayer"/> と同じ根拠。実長に比例させる</summary>
        private const float PlaybackGraceSeconds = 5f;

        /// <summary>WAV の長さが読めなかったときの期限（参照実装の <c>FALLBACK_TIMEOUT_MS</c>）</summary>
        private const float FallbackTimeoutSeconds = 120f;

        /// <summary>同時に鳴っている本数がこれを超えたら警告する（診断のみ）</summary>
        private const int ProcessWarnThreshold = 8;

        private readonly string _command;
        private readonly string _tmpDir;
        private readonly List<Process> _running = new List<Process>();
        private bool _warnedProcessCount;

        /// <summary>エンベロープを作れなかったことの警告は1回だけ（読めない WAV は同じ形が続く）</summary>
        private bool _warnedEnvelope;

        /// <summary>起動ラグの較正ログは1回だけ（→ <see cref="PlayAsync"/>）</summary>
        private bool _measuredStartLag;

        public event Action<string> Warn;

        /// <param name="command">再生コマンド。テストで差し替えられるようにしてある</param>
        public AfplaySpeechPlayer(string command = "/usr/bin/afplay")
        {
            _command = command;

            // ★ 起動時に作り直す。前回の残骸（異常終了で消し損ねた WAV）を溜めない。
            //   参照実装の reset() と同じ
            //
            // ★ <b>ディレクトリ名にプロセス ID を混ぜること。</b> `forceSingleInstance` が防ぐのは
            //   **同じ `.app` の二重起動だけ**で（→ docs/mascot.md）、**Editor の Play Mode と
            //   ビルド済み `.app` の同時起動は防げない**。共通の名前にすると、後から起動した方の
            //   下の Delete が**先行インスタンスの再生中の WAV ごと消す**。
            //   他インスタンスの残骸は OS のキャッシュパージに任せる
            _tmpDir = Path.Combine(
                Application.temporaryCachePath,
                "speech-" + Process.GetCurrentProcess().Id);
            try
            {
                if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, true);
            }
            catch (Exception e)
            {
                // 消せなくても致命的ではない（同名ファイルは上書きする）
                Debug.LogWarning("[Mascot] 一時ディレクトリを消せませんでした: " + e.Message);
            }
            Directory.CreateDirectory(_tmpDir);
        }

        public int ActiveCount
        {
            get { return _running.Count; }
        }

        /// <summary>
        /// WAV を一時ファイルに書く。
        ///
        /// ★ <b>ファイル名は呼び出し側の <paramref name="name"/> をそのまま使う</b>
        ///   （<c>speech-{epoch}-{seq}</c>）。<c>seq</c> だけだと採番のやり直しで同じ名前が戻り、
        ///   <b>孤児の afplay が読んでいる最中のファイルを truncate する</b>。
        /// </summary>
        public object Prepare(byte[] wav, string name, out string error)
        {
            WavHeader header;
            if (!WavDecoder.TryReadHeader(wav, out header, out error)) return null;

            var path = Path.Combine(_tmpDir, name + ".wav");
            try
            {
                // ★ **毎回作る（べき等で安価）。** `Application.temporaryCachePath` は macOS では
                //   `$TMPDIR`（`/var/folders/…/T/<company>/<product>`）を指し、**OS が定期的に掃除する**
                //   （3日以上触られていないものを消す periodic / 再起動時のクリア）。
                //   実行中にディレクトリごと消えうる。消えたあとは書き込みが全部
                //   DirectoryNotFoundException になり、AudioFailed → skip + ack で
                //   **プロセスが終わるまで全発話が黙って落ちる**
                Directory.CreateDirectory(_tmpDir);
                File.WriteAllBytes(path, wav);
            }
            catch (Exception e)
            {
                error = "WAV を書けませんでした: " + e.Message;
                return null;
            }

            return new AfplayAudioHandle
            {
                Path = path,
                DurationMs = header.DurationMs,
                // ★ **作れなくても Prepare は成功させる**（→ LipSyncEnvelope.BuildOrWarn）。
                //   null を返すと AudioFailed → skip + ack で、サーバーのキューから物理削除される
                Envelope = LipSyncEnvelope.BuildOrWarn(
                    wav, header, LipSyncEnvelope.DefaultFrameMs, ref _warnedEnvelope, Warn),
                EnvelopeFrameMs = LipSyncEnvelope.DefaultFrameMs,
            };
        }

        public async Task<string> PlayAsync(object audio)
        {
            var handle = audio as AfplayAudioHandle;
            if (handle == null) return "音声のハンドルがありません";
            if (string.IsNullOrEmpty(handle.Path) || !File.Exists(handle.Path)) return "WAV がありません";

            // 保険。手放していれば掴み直す（この実装では no-op だが契約として呼ぶ）
            ResumeOutput();

            Process process;
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = _command,
                    // ★ shell を噛ませないこと。ファイル名に空白や引用符が入っても壊れない
                    UseShellExecute = false,
                    RedirectStandardError = true,
                };
                info.ArgumentList.Add(handle.Path);

                process = Process.Start(info);
            }
            catch (Exception e)
            {
                return "再生プロセスを起動できませんでした: " + e.Message;
            }

            if (process == null) return "再生プロセスを起動できませんでした";

            _running.Add(process);
            if (_running.Count > ProcessWarnThreshold && !_warnedProcessCount)
            {
                _warnedProcessCount = true;
                var warn = Warn;
                if (warn != null)
                {
                    warn("同時に鳴らすプロセスが " + _running.Count + " 本になりました" +
                         "（採番のやり直しが、音が鳴り終わる前に繰り返されている可能性）");
                }
            }

            try
            {
                var limit = TimeoutSecondsFor(handle.DurationMs);
                var startedAt = Time.realtimeSinceStartupAsDouble;
                var deadline = startedAt + limit;

                // ★ **Process.Exited を使わないこと。** あれは ThreadPool スレッドで発火するので、
                //   そこから PlaybackEvent を投げると Unity の API をメインスレッド外から触る。
                //   await Task.Yield() は Unity の SynchronizationContext でメインスレッドに戻るので、
                //   ポーリングにするだけでスレッドの問題が全部消える（AudioClipPlayer と同じ形）
                while (!process.HasExited && Time.realtimeSinceStartupAsDouble < deadline)
                {
                    await Task.Yield();
                }

                if (!process.HasExited)
                {
                    try
                    {
                        // ★ .NET の Kill() は SIGKILL 相当。参照実装は SIGTERM → 1秒後 SIGKILL の
                        //   2段だが、そちらは常駐プロセスとして長く生きるための配慮。
                        //   ここは Unity の寿命に閉じるので1段でよい
                        process.Kill();
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[Mascot] 再生プロセスを止められませんでした: " + e.Message);
                    }
                    return limit.ToString("F1") + " 秒で終わりませんでした";
                }

                // ★ **lipSyncOffsetMs の較正材料。1回だけ出す。**
                //   Process.Start は音が出るより前に返るので、その起動ラグぶん口が先に動く。
                // ★★ **これは「起動ラグ + 終了処理」の合計＝上界であって、起動ラグそのものではない。**
                //   しかも完了の検出は上のポーリング（毎フレーム = 30fps なら 33ms 刻み）なので、
                //   **測定誤差が測ろうとしている量と同じオーダー**。値を仕様として扱わないこと。
                //   使い道は「桁の確認」（100ms オーダーなら設計を疑う）で、既定値は実機で目で見て決める。
                // ★ 差だけでなく両方の生の値を出すこと。差だけだと後から切り分けられない。
                if (!_measuredStartLag)
                {
                    _measuredStartLag = true;
                    var elapsedMs = (Time.realtimeSinceStartupAsDouble - startedAt) * 1000.0;
                    Debug.Log(
                        $"[Mascot] afplay の実時間 {elapsedMs:F0}ms / WAV の長さ {handle.DurationMs}ms " +
                        $"（差 {elapsedMs - handle.DurationMs:F0}ms = 起動ラグ + 終了処理 + ポーリング誤差）");
                }

                if (process.ExitCode == 0) return null;

                // ★ stderr はプロセスが終わってから読む。走っている最中に読むと、
                //   ポーリングと競合するうえ、読まないままバッファが埋まると相手が止まる。
                //   afplay の stderr は1行程度なので終了後で間に合う
                var detail = string.Empty;
                try
                {
                    detail = process.StandardError.ReadToEnd().Trim();
                }
                catch (Exception)
                {
                    // 読めなくても終了コードは返せる
                }

                return string.IsNullOrEmpty(detail)
                    ? "再生に失敗しました (exit=" + process.ExitCode + ")"
                    : "再生に失敗しました (exit=" + process.ExitCode + "): " + detail;
            }
            finally
            {
                _running.Remove(process);
                process.Dispose();
            }
        }

        /// <summary>一時ファイルを消す。存在しなくても黙って戻る。</summary>
        public void Discard(object audio)
        {
            var handle = audio as AfplayAudioHandle;
            if (handle == null || string.IsNullOrEmpty(handle.Path)) return;
            try
            {
                File.Delete(handle.Path);
            }
            catch (Exception)
            {
                // 消せなくても次の起動で作り直す
            }
            handle.Path = null;
        }

        /// <summary>
        /// 鳴っているプロセスを全部止める（終了処理用）。
        ///
        /// ★ <b>必ず呼ぶこと。</b> 参照実装のコメントどおり「親が exit しても afplay は死なない」。
        ///   呼ばないと、アプリを閉じた後も音が鳴り続ける。
        /// </summary>
        public void StopAll()
        {
            // PlayAsync の finally が _running を触るので、コピーしてから回す
            foreach (var process in _running.ToArray())
            {
                try
                {
                    if (!process.HasExited) process.Kill();
                }
                catch (Exception)
                {
                    // 終了処理なので握りつぶす
                }
            }
        }

        /// <summary>
        /// ★ <b>常に <c>false</c>。</b> 1発話 = 1プロセスなので鳴り終われば OS が解放する ——
        ///   <b>手放すものが残っていない</b>。これによりアイドル判定ごと止まるので、
        ///   「手放しました」という嘘のログも出ない。
        /// </summary>
        public bool CanSuspendOutput
        {
            get { return false; }
        }

        /// <summary>
        /// ★ <b>no-op でよい。</b> 1発話 = 1プロセスなので、鳴り終われば
        ///   OS がデバイスを解放する（実測 0.5〜1秒）。掴みっぱなしになる相手がいない。
        /// </summary>
        public void SuspendOutput()
        {
        }

        /// <summary>★ 同上。掴んでいないので掴み直す必要も無い。</summary>
        public void ResumeOutput()
        {
        }

        /// <summary>
        /// 再生を諦めるまでの秒数。参照実装の <c>playbackTimeoutMs</c> と同じ形。
        ///
        /// ★ <c>public</c> なのはテストで固定するため（<c>MascotRunner.IsParked</c> と同じ扱い）。
        /// </summary>
        public static float TimeoutSecondsFor(int durationMs)
        {
            // ★ 0 は「長さ 0」ではなく「不明」（→ WavHeader.DurationMs）
            if (durationMs <= 0) return FallbackTimeoutSeconds;
            return durationMs / 1000f * 2f + PlaybackGraceSeconds;
        }
    }
}
#endif
