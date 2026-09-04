using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UniVRM10;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// ワンショットで再生するモーションの種別。<see cref="EmotionMotionTrigger"/> が発火させるのが
    /// <see cref="Emotion"/>、<see cref="IdleAccentTimer"/> が発火させるのが <see cref="Accent"/>。
    ///
    /// ★ <see cref="VrmMotionPlayer.Play"/> の割り込み規則がこの2値で変わる——
    ///   <see cref="Emotion"/> は <see cref="Accent"/> に割り込めるが、<see cref="Emotion"/> 同士は
    ///   割り込まない（最後まで見せる。ユーザーと決めたこと。2026-09-04）。
    /// </summary>
    public enum MotionKind
    {
        Emotion,
        Accent,
    }

    /// <summary>
    /// 感情モーション・小ネタ（<see cref="MotionCategory"/>）を読み込み、ワンショットで再生する。
    /// <b><c>IDisposable</c>。<c>MonoBehaviour</c> ではない。</b> <see cref="VrmCharacter"/> が所有する。
    ///
    /// 状態機械は <c>Idle → FadeIn → Playing → FadeOut → Idle</c>。<c>Idle</c> 以外の間、
    /// <see cref="IsPlaying"/> は <c>true</c>。<see cref="Kind"/>（<see cref="MotionKind"/>）で
    /// 「今のはどっちの発火か」を覚え、<see cref="Play"/> の割り込み判定に使う。
    ///
    /// ★ <b>待機（<see cref="VrmIdleAnimation"/>）とスロットを奪い合う。</b> <c>Vrm10Runtime</c> の
    ///   <c>VrmAnimation</c> スロットは1つしか無いので、感情モーションを差している間は
    ///   待機 VRMA そのものは差さっていない——フェード元・フェード先として<b>読み込みは</b>
    ///   保持したまま、<see cref="VrmIdleAnimation.Present"/> を通じてスロットを一時的に借りる。
    ///
    /// ★ <b>時計は引数の <c>now</c>（<see cref="VrmCharacter"/> が渡す
    ///   <c>Time.realtimeSinceStartupAsDouble</c>）。</b> <c>Time.*</c> をこのクラスで直接読まない
    ///   ——テストと実機で起点がずれないようにする、他の純粋クラスと同じ流儀
    ///   （<c>ChatterMascot.Vrm</c> は VRM10 に依存するので EditMode テストは当たらないが、
    ///   流儀は揃えておく）。
    /// </summary>
    internal sealed class VrmMotionPlayer : IDisposable
    {
        private enum PlayState
        {
            Idle,
            FadeIn,
            Playing,
            FadeOut,
        }

        /// <summary>読み込み済みの1本。<see cref="VrmMotionPlayer"/> の外へは出さない。</summary>
        private sealed class LoadedClip
        {
            public readonly Vrm10AnimationInstance Instance;
            public readonly Animation Animation;
            public readonly AnimationState State;

            public LoadedClip(Vrm10AnimationInstance instance, Animation animation, AnimationState state)
            {
                Instance = instance;
                Animation = animation;
                State = state;
            }
        }

        private readonly VrmIdleAnimation _idle;
        private readonly MotionParams _params;
        private readonly Dictionary<MotionClip, LoadedClip> _loaded = new Dictionary<MotionClip, LoadedClip>();

        private bool _disposed;
        private PlayState _state = PlayState.Idle;
        private MotionKind _kind;
        private LoadedClip _current;
        private CrossFadeAnimation _fade;
        private bool _ended;

        public VrmMotionPlayer(VrmIdleAnimation idle, MotionParams p)
        {
            _idle = idle;
            _params = p;
        }

        /// <summary>全カテゴリ × 全ルートの一覧。<see cref="LoadAsync"/> の完了後は非 <c>null</c>。</summary>
        public AnimationManifest Manifest { get; private set; }

        /// <summary>いま <c>Idle</c> 以外の状態か（フェード中も含む）。</summary>
        public bool IsPlaying => _state != PlayState.Idle;

        /// <summary>いま再生しているのが感情モーションか（<see cref="EmotionMotionTrigger"/> に渡す）。</summary>
        public bool IsPlayingEmotion => IsPlaying && _kind == MotionKind.Emotion;

        /// <summary>
        /// 直前の <see cref="Tick"/> で待機へ戻り切ったら1回だけ <c>true</c>。呼ぶと消費する。
        /// <see cref="VrmCharacter"/> はこれを見て <c>IdleAccentTimer.Reset</c> /
        /// <c>EmotionMotionTrigger.NotifyEnded</c> を呼ぶ。
        /// </summary>
        public bool ConsumeEnded()
        {
            if (!_ended) return false;
            _ended = false;
            return true;
        }

        /// <summary>
        /// 読み込み中だけ上げるフレームレート。<see cref="VrmIdleAnimation"/> と同じ 120。
        /// </summary>
        private const int LoadFrameRate = 120;

        /// <summary>
        /// 全クリップをプリロードする。<b>例外は内側で全部握る</b>
        /// （<see cref="VrmIdleAnimation.LoadAsync"/> と同型——<c>_ = motion.LoadAsync(...)</c> の
        /// 形で投げっぱなしにされるので、ここから漏らすと未観測の Task として消える）。
        /// </summary>
        public async Task LoadAsync(Transform parent, CancellationToken ct)
        {
            using (FrameRateBudget.Boost(LoadFrameRate))
            {
                try
                {
                    Manifest = AnimationManifest.Build(AssetEnvFactory.Current());

                    var loadedCount = 0;
                    foreach (var category in MotionCategories.All)
                    {
                        foreach (var clip in Manifest.Clips(category))
                        {
                            // ★ MotionClip はどのルートから来たか（Settings / PersistentData / …）を
                            //   持たない（ファイル名の勝敗だけで組む AnimationManifest の設計）ので、
                            //   ログ用の Source は UserConfig 固定。実際の読み口は clip.Path そのもの
                            var candidate = new AssetCandidate(AssetSource.UserConfig, clip.Path);
                            var loaded = await VrmAssetLoader.ReadAsync(candidate, ct);

                            // ★ 毎 await の後に破棄とキャンセルを確認すること（VrmIdleAnimation.LoadAsync
                            //   と同じ理由）。ここではまだ GameObject が無いので Destroy は要らない
                            if (_disposed || ct.IsCancellationRequested) return;

                            if (loaded.IsEmpty)
                            {
                                Debug.LogWarning($"[Mascot] モーション {clip.Path} を読めませんでした。飛ばします");
                                continue;
                            }

                            var vrma = await VrmaLoader.ParseAsync(loaded, ct);

                            // ★ こちらは GameObject を取れているかもしれないので Destroy して抜ける
                            if (_disposed || ct.IsCancellationRequested)
                            {
                                if (vrma != null) UnityEngine.Object.Destroy(vrma.gameObject);
                                return;
                            }

                            if (vrma == null)
                            {
                                Debug.LogWarning($"[Mascot] モーション {clip.Path} は VRMA として解釈できませんでした。飛ばします");
                                continue;
                            }

                            if (TryAdopt(clip, parent, vrma)) loadedCount++;
                        }
                    }

                    // ★ 0本は正常（ユーザーが同梱以外に何も置いていない）。LogError にしない
                    Debug.Log($"[Mascot] モーション: {Manifest.Describe()}（読めた {loadedCount}/{Manifest.TotalCount} 本）");
                }
                catch (OperationCanceledException)
                {
                    // 終了経路。ログを出さない
                }
                catch (Exception e)
                {
                    // ★ 握ること。ここから漏らすと理由が残らない（VrmIdleAnimation.LoadAsync と同じ理由）
                    Debug.LogError("[Mascot] モーションの読み込みで例外が出ました: " + e);
                }
            }
        }

        /// <summary>
        /// 1本を再生できる状態に組み込む（プリロード）。<b>失敗しても投げない</b>
        /// （呼び出し側は次のクリップへ進める）。
        /// </summary>
        private bool TryAdopt(MotionClip clip, Transform parent, Vrm10AnimationInstance vrma)
        {
            vrma.ShowBoxMan(false);
            vrma.transform.SetParent(parent, false);

            // ★ #88 と同じ理由。ファイルに無い指ボーンは Retarget が identity（T ポーズ＝伸びた指）を
            //   要求してくる。FingerFallbackPoseProvider.Wrap の doc 参照
            // ★ VRoid 由来の 13 本は指を持つので 0 本＝黙る。1本ずつ出すと起動ログが 13 行伸びる
            var supplied = FingerFallbackPoseProvider.Wrap(vrma);
            if (supplied > 0)
            {
                Debug.Log($"[Mascot] モーション {clip.FileName} に無い指ボーン {supplied} 本を既定の丸めで補います");
            }

            var animation = vrma.GetComponent<Animation>();

            // ★ UniVRM 自身と同じ foreach で先頭 state を取る。animation[clip.name] は使わない
            //   ——VRoid 書き出しはアニメーション名が空のことがある（実物で確認済み）
            AnimationState state = null;
            foreach (AnimationState s in animation)
            {
                state = s;
                break;
            }

            if (state == null)
            {
                Debug.LogWarning($"[Mascot] モーション {clip.FileName} に AnimationState がありません。飛ばします");
                UnityEngine.Object.Destroy(vrma.gameObject);
                return false;
            }

            // ★ importer は wrapMode を Loop 固定で書き出す（AnimationImporterUtil.cs）。
            //   ワンショットで最終フレームを保持するには ClampForever に上書きする（Once は rest に戻る）
            state.wrapMode = WrapMode.ClampForever;

            // ★ 読み込み直後は寝かせておく。再生は Play() が明示的に起こす
            animation.enabled = false;

            var animator = vrma.GetComponent<Animator>();
            if (animator != null) animator.enabled = false;

            if (vrma.ExpressionMap.Count > 0)
            {
                Debug.LogWarning($"[Mascot] モーション {clip.FileName} は表情アニメーションを持つので、" +
                                  "emotion とリップシンクが上書きされます");
            }

            _loaded[clip] = new LoadedClip(vrma, animation, state);
            return true;
        }

        /// <summary>
        /// 再生を開始する。開始できたら <c>true</c>。
        ///
        /// 拒否する条件: 待機 VRMA が未読込（<see cref="VrmIdleAnimation.IsLoaded"/>）／
        /// <paramref name="clip"/> が <c>null</c> か読めていない／
        /// <paramref name="kind"/><c> == Emotion</c> で既に感情モーション再生中／
        /// <paramref name="kind"/><c> == Accent</c> で既に何か再生中。
        ///
        /// ★ <b>感情モーションは小ネタ（<c>Accent</c>）に割り込める。</b> 割り込まれた小ネタの
        ///   クリップは <c>Animation.enabled = false</c> に戻す（寝かせ直す）。感情モーション同士は
        ///   割り込まない（上の拒否条件どおり）。
        /// </summary>
        public bool Play(MotionClip clip, MotionKind kind, double now)
        {
            if (_disposed) return false;
            if (!_idle.IsLoaded) return false;
            // ★ 設定「待機モーション」OFF の間は始めない。Present が no-op なので見えないまま
            //   クリップの Animation だけ走り、FadeIn の後に Playing の防御で畳まれる——
            //   1本ぶん無駄に再生するだけで実害は無いが、その経路を最初から作らない
            if (!_idle.Enabled) return false;
            if (clip == null) return false;
            if (!_loaded.TryGetValue(clip, out var loaded)) return false;
            if (kind == MotionKind.Emotion && IsPlayingEmotion) return false;
            if (kind == MotionKind.Accent && IsPlaying) return false;

            // ★ ここに来る「_current != null」は、感情モーションが小ネタへ割り込むケースだけ
            //   （上のガードにより、Accent は何も再生中でないときしか開始できない）
            if (_current != null)
            {
                _current.Animation.enabled = false;
            }

            loaded.State.time = 0;
            loaded.Animation.enabled = true;
            loaded.Animation.Play();
            // ★ legacy Animation の更新は LateUpdate より前なので、差した最初のフレームは
            //   前回の最終姿勢／T ポーズを Retarget が読んでしまう。Sample() で即座に反映させる
            loaded.Animation.Sample();

            // ★ いま実際に差さっているものから（idle.Current が null なら、無効化中などで
            //   何も差さっていないということ——その場合でも内部の状態機械は進めてよいので、
            //   フェード元として待機そのものの ControlRig にフォールバックする）
            var from = _idle.Current != null ? _idle.Current.ControlRig : _idle.Idle.ControlRig;
            _fade = new CrossFadeAnimation(from, loaded.Instance.ControlRig, now, _params.FadeSeconds);
            _idle.Present(_fade);

            _current = loaded;
            _kind = kind;
            _state = PlayState.FadeIn;
            return true;
        }

        /// <summary>
        /// 毎フレーム呼ぶ。<c>Idle</c> のときは何もしない。
        /// </summary>
        public void Tick(double now)
        {
            if (_disposed) return;

            switch (_state)
            {
                case PlayState.Idle:
                    return;

                case PlayState.FadeIn:
                    _fade.Tick(now);
                    if (_fade.IsDone)
                    {
                        _idle.Present(_current.Instance);
                        _fade = null;
                        _state = PlayState.Playing;
                    }
                    return;

                case PlayState.Playing:
                    // ★ 防御: idle.Current が自分の差したものでなくなっていたら
                    //   （Enabled のトグルで Apply() に上書きされた等）Idle に戻してクリップを寝かせる
                    if (!ReferenceEquals(_idle.Current, _current.Instance))
                    {
                        _current.Animation.enabled = false;
                        _current = null;
                        _fade = null;
                        _state = PlayState.Idle;
                        return;
                    }

                    // ★ length まで待つと最終フレームを FadeSeconds ぶん保持してから戻る
                    if (_current.State.time >= _current.State.length - _params.FadeSeconds)
                    {
                        _fade = new CrossFadeAnimation(
                            _current.Instance.ControlRig, _idle.Idle.ControlRig, now, _params.FadeSeconds);
                        _idle.Present(_fade);
                        _state = PlayState.FadeOut;
                    }
                    return;

                case PlayState.FadeOut:
                    _fade.Tick(now);
                    if (_fade.IsDone)
                    {
                        _idle.PresentIdle();
                        if (_current != null) _current.Animation.enabled = false;
                        _current = null;
                        _fade = null;
                        _state = PlayState.Idle;
                        _ended = true;
                    }
                    return;
            }
        }

        /// <summary>
        /// 再生中でも即 <see cref="VrmIdleAnimation.PresentIdle"/> して寝かせる（設定 OFF 用）。
        /// ★ <see cref="ConsumeEnded"/> は立てない——ここで終わるのは「自然に最後まで見せ切った」
        ///   ではなく強制停止なので、<c>IdleAccentTimer</c> / <c>EmotionMotionTrigger</c> の
        ///   起点を動かす理由が無い。
        /// </summary>
        public void Stop()
        {
            if (_current != null)
            {
                _current.Animation.enabled = false;
            }
            _current = null;
            _fade = null;
            _state = PlayState.Idle;
            _idle.PresentIdle();
        }

        /// <summary>
        /// <see cref="Stop"/> → 読み込んだ全 GameObject を <c>Destroy</c>。<b>冪等。</b>
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            Stop();
            _disposed = true;

            foreach (var loaded in _loaded.Values)
            {
                if (loaded.Instance != null) UnityEngine.Object.Destroy(loaded.Instance.gameObject);
            }
            _loaded.Clear();
        }
    }
}
