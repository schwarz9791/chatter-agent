using System;
using System.Collections.Generic;
using System.Globalization;
using ChatterMascot.Protocol;
using UnityEngine;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// <see cref="StallProbe.Observe"/> への1フレームぶんの入力。時計・<c>deltaTime</c>・骨の位置は
    /// すべて呼び出し側（<see cref="VrmCharacter"/>）が測ってから渡す——このクラス自身は
    /// <c>Time.*</c> も <c>Transform</c> も読まない（<see cref="FaceLatch"/> / <see cref="MouthTracker"/> と同じ流儀）。
    /// </summary>
    public struct StallSample
    {
        public int Frame;
        public double Now;
        public float DeltaTime;
        public float UnscaledDeltaTime;

        /// <summary>実ボーン hips のワールド位置。取れなければ <c>null</c>。</summary>
        public Vector3? HipsWorld;

        /// <summary>実ボーン Head のワールド位置。取れなければ <c>null</c>。</summary>
        public Vector3? HeadWorld;

        /// <summary>いまのモーション状態の文字列表現（<see cref="VrmMotionPlayer.StateName"/>）。</summary>
        public string MotionState;

        /// <summary>このフレームで起きたモーションの遷移。無ければ <c>null</c>。</summary>
        public string MotionEvent;

        public bool Speaking;
        public SpeechKind Kind;
    }

    /// <summary>
    /// メインスレッドのストールと骨（実ボーン）の飛びを検知し、閾値を超えたフレームだけ
    /// <c>Player.log</c> 用の行を返す。<b>純粋。<c>Time.*</c> を直接読まない</b>
    ///   —— <c>ChatterMascot.Tests.asmdef</c> は <c>ChatterMascot.Runtime</c> しか参照しないので、
    ///   ここに判定を置くとテストが当たる。
    ///
    /// ★ SpringBone（<c>FastSpringBoneService.LateUpdate</c>）は <c>deltaTime</c> をクランプ無しで
    ///   積分する（→ docs/mascot.md）。髪が飛ぶ瞬間が本当にストール由来か
    ///   （<see cref="StallDeltaSeconds"/>）、姿勢そのものが飛んでいるだけか
    ///   （<see cref="HipsJumpMeters"/>）を、同じフレームの <c>Player.log</c> だけで
    ///   切り分けられるようにするためのプローブ（#103）。
    ///
    /// ★ 閾値を超えない飛びもある（小ネタの自然終了 → 待機 → 直後に感情モーション開始、
    ///   のような並びで <c>stall:</c> / <c>hipsJump:</c> のどちらも出ないまま髪だけ動く）。
    ///   <see cref="MotionEdgeFrames"/> は遷移の直後を閾値に関係なく無条件に出す窓で、
    ///   これを補う。
    ///
    /// ★ 位置の NaN は Unity が警告しない（回転の NaN と違う）。hips / head のどちらかが
    ///   NaN のフレームは <c>nanPose:</c> を窓や閾値に関係なく毎フレーム出す。
    /// </summary>
    public sealed class StallProbe
    {
        /// <summary><c>DeltaTime</c>、または実時間の差（<c>gap</c>）がこれ以上なら <c>stall:</c> 行を出す。</summary>
        public const double StallDeltaSeconds = 0.1;

        /// <summary>hips（実ボーン）の1フレームでの移動量がこれ以上なら <c>hipsJump:</c> 行を出す。</summary>
        public const float HipsJumpMeters = 0.3f;

        /// <summary>
        /// モーションイベントが起きたフレームと、その後これだけのフレームは
        /// <c>motionEdge:</c> 行を無条件に出す（切り替え起因の飛びを、閾値未満でも直接見るための窓）。
        /// </summary>
        public const int MotionEdgeFrames = 3;

        private static readonly string[] NoLines = Array.Empty<string>();

        private double _prevNow = double.NaN;
        private Vector3? _prevHips;
        private Vector3? _prevHead;
        private int _lastMotionEventFrame;
        private string _lastMotionEventText;

        /// <summary>
        /// 1フレーム分進めて判定する。<b>閾値未満のフレームでは空を返し、何も確保しない</b>
        ///   ——常駐アプリで毎フレーム呼ぶので、確保するのは行が出るときだけ。
        /// </summary>
        public IReadOnlyList<string> Observe(in StallSample sample)
        {
            // ★ gap は前フレームの Now が無ければ判定できない（0 に倒し、dt 単独の判定だけ生かす）。
            var hasPrevNow = !double.IsNaN(_prevNow);
            var gap = hasPrevNow ? sample.Now - _prevNow : 0.0;

            // ★ 前フレームの値が無い初回はどちらも判定しない（Vector3? 同士の距離が取れるときだけ）。
            //   同じ移動量を stall: / motionEdge: の hips= / head= にも使い回す。
            var hipsMoved = Moved(_prevHips, sample.HipsWorld);
            var headMoved = Moved(_prevHead, sample.HeadWorld);

            // ★ 行を組む前に更新すること。同じフレームにモーションイベントと stall / motionEdge が
            //   重なったら「0 フレーム前＝自分自身」として出す。motionEdge の窓もここで開き直す。
            if (sample.MotionEvent != null)
            {
                _lastMotionEventFrame = sample.Frame;
                _lastMotionEventText = sample.MotionEvent;
            }

            List<string> lines = null;

            if (sample.DeltaTime >= StallDeltaSeconds || gap >= StallDeltaSeconds)
            {
                lines = new List<string> { FormatStall(sample, gap, hipsMoved, headMoved) };
            }

            if (hipsMoved.HasValue && hipsMoved.Value >= HipsJumpMeters)
            {
                if (lines == null) lines = new List<string>();
                lines.Add(FormatHipsJump(sample, hipsMoved.Value));
            }

            // ★ 窓や閾値に関係なく無条件（NaN が続く間は毎フレーム出る）。位置の NaN は
            //   Unity が警告しないので、ここで拾わないと気づけない
            var hipsIsNaN = sample.HipsWorld.HasValue && HasNaN(sample.HipsWorld.Value);
            var headIsNaN = sample.HeadWorld.HasValue && HasNaN(sample.HeadWorld.Value);
            if (hipsIsNaN || headIsNaN)
            {
                if (lines == null) lines = new List<string>();
                lines.Add(FormatNanPose(sample, hipsIsNaN, headIsNaN));
            }

            if (_lastMotionEventText != null)
            {
                var age = sample.Frame - _lastMotionEventFrame;
                if (age >= 0 && age <= MotionEdgeFrames)
                {
                    if (lines == null) lines = new List<string>();
                    lines.Add(FormatMotionEdge(sample, age, gap, hipsMoved, headMoved));
                }
            }

            _prevNow = sample.Now;
            _prevHips = sample.HipsWorld;
            _prevHead = sample.HeadWorld;

            return lines != null ? (IReadOnlyList<string>)lines : NoLines;
        }

        private static float? Moved(Vector3? prev, Vector3? current)
        {
            return prev.HasValue && current.HasValue ? Vector3.Distance(prev.Value, current.Value) : (float?)null;
        }

        private string FormatStall(in StallSample sample, double gap, float? hipsMoved, float? headMoved)
        {
            return "[Mascot] stall: frame=" + sample.Frame.ToString(CultureInfo.InvariantCulture) +
                   " dt=" + sample.DeltaTime.ToString("F3", CultureInfo.InvariantCulture) +
                   " udt=" + sample.UnscaledDeltaTime.ToString("F3", CultureInfo.InvariantCulture) +
                   " gap=" + gap.ToString("F3", CultureInfo.InvariantCulture) +
                   " hips=" + FormatMoved(hipsMoved) +
                   " head=" + FormatMoved(headMoved) +
                   " motion=" + sample.MotionState +
                   " lastMotionEvent=" + FormatLastMotionEvent(sample.Frame) +
                   " speaking=" + (sample.Speaking ? "true" : "false") +
                   " kind=" + (sample.Kind == SpeechKind.Prompt ? "prompt" : "assistant");
        }

        private string FormatHipsJump(in StallSample sample, float hipsMoved)
        {
            return "[Mascot] hipsJump: frame=" + sample.Frame.ToString(CultureInfo.InvariantCulture) +
                   " moved=" + hipsMoved.ToString("F3", CultureInfo.InvariantCulture) + "m" +
                   " dt=" + sample.DeltaTime.ToString("F3", CultureInfo.InvariantCulture) +
                   " motion=" + sample.MotionState;
        }

        private string FormatMotionEdge(in StallSample sample, int age, double gap, float? hipsMoved, float? headMoved)
        {
            return "[Mascot] motionEdge: frame=" + sample.Frame.ToString(CultureInfo.InvariantCulture) +
                   " age=" + age.ToString(CultureInfo.InvariantCulture) +
                   " dt=" + sample.DeltaTime.ToString("F3", CultureInfo.InvariantCulture) +
                   " gap=" + gap.ToString("F3", CultureInfo.InvariantCulture) +
                   " hips=" + FormatMoved(hipsMoved) +
                   " head=" + FormatMoved(headMoved) +
                   " motion=" + sample.MotionState +
                   " event=" + (sample.MotionEvent ?? "-");
        }

        private string FormatNanPose(in StallSample sample, bool hipsIsNaN, bool headIsNaN)
        {
            return "[Mascot] nanPose: frame=" + sample.Frame.ToString(CultureInfo.InvariantCulture) +
                   " hips=" + (hipsIsNaN ? "nan" : "ok") +
                   " head=" + (headIsNaN ? "nan" : "ok") +
                   " dt=" + sample.DeltaTime.ToString("F3", CultureInfo.InvariantCulture) +
                   " motion=" + sample.MotionState +
                   " lastMotionEvent=" + FormatLastMotionEvent(sample.Frame);
        }

        private static bool HasNaN(Vector3 v)
        {
            return float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z);
        }

        private static string FormatMoved(float? moved)
        {
            return moved.HasValue
                ? moved.Value.ToString("F3", CultureInfo.InvariantCulture) + "m"
                : "n/a";
        }

        private string FormatLastMotionEvent(int frame)
        {
            if (_lastMotionEventText == null) return "none";
            var age = frame - _lastMotionEventFrame;
            return age.ToString(CultureInfo.InvariantCulture) + "(" + _lastMotionEventText + ")";
        }
    }
}
