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

            public LoadedClip(Vrm10AnimationInstance instance, Animation animation)
            {
                Instance = instance;
                Animation = animation;
            }

            /// <summary>
            /// 先頭の <c>AnimationState</c>。<b>毎回取り直す。</b>
            ///
            /// ★ 読み込み時に掴んだ参照を持ち回らない（安い。1 件の列挙）。名前で引かないのは
            ///   VRoid 書き出しのアニメーション名が空だから（UniVRM 自身と同じ foreach）。
            /// </summary>
            public AnimationState State
            {
                get
                {
                    foreach (AnimationState s in Animation) return s;
                    return null;
                }
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
        private double _startedAt;

        public VrmMotionPlayer(VrmIdleAnimation idle, MotionParams p)
        {
            _idle = idle;
            _params = p;
        }

        /// <summary>
        /// 全カテゴリ × 全ルートの<b>走査結果</b>。<see cref="LoadAsync"/> の先頭で組まれるので、
        /// 個々のクリップの読み込みが終わる前から非 <c>null</c>。
        ///
        /// ★★ <b>起動ログ（<see cref="Describe"/> / <see cref="AnimationManifest.TotalCount"/>）
        ///   専用。<see cref="Pick"/> にはこちらを使わないこと</b>（#70 レビュー #2）。
        ///   走査は「ファイルが在るか」しか見ておらず、個々の <c>.vrma</c> の読み込みが
        ///   壊れた <c>.vrma</c> やタイミングで失敗しても <see cref="Manifest"/> はそれを知らない
        ///   ——<c>Pick</c> の母集合には <see cref="Loaded"/> を使うこと。
        /// </summary>
        public AnimationManifest Manifest { get; private set; }

        /// <summary>
        /// 実際に読み込めた（<c>_loaded</c> に載った）クリップだけの一覧。読み込み中は <c>null</c>。
        ///
        /// ★★ <b><see cref="Play"/> で再生できる集合と一致させること</b>（#70 レビュー #2）。
        ///   <see cref="Manifest"/> から <c>Pick</c> すると、走査はできたが読み込みに失敗した
        ///   クリップ（壊れた <c>.vrma</c> 等）や、まだプリロードの途中のクリップを選んでしまい、
        ///   <see cref="Play"/> が黙って <see cref="MotionPlayResult.NotLoaded"/> を返す
        ///   （無言の no-op に見える）。設定パネルの一覧（<c>VrmCharacter.MotionClips</c>）も
        ///   ここから組む——一覧に出ているのに押しても再生できない、を無くすため。
        /// ★ <c>LoadAsync</c> の完了直後、<see cref="Manifest"/> の各カテゴリの並びを保って
        ///   <c>_loaded.ContainsKey</c> で絞り込んで組む（<see cref="BuildLoadedManifest"/>）。
        ///   例外・中断で終わっても「そこまでに読めた分」で組む（<c>_disposed</c> のときを除く）。
        /// </summary>
        public AnimationManifest Loaded { get; private set; }

        /// <summary>いま <c>Idle</c> 以外の状態か（フェード中も含む）。</summary>
        public bool IsPlaying => _state != PlayState.Idle;

        /// <summary>いま再生しているのが感情モーションか（<see cref="EmotionMotionTrigger"/> に渡す）。</summary>
        public bool IsPlayingEmotion => IsPlaying && _kind == MotionKind.Emotion;

        /// <summary>
        /// 直前の <see cref="Tick"/> で待機へ戻り切ったら1回だけ <c>true</c>。呼ぶと消費する。
        /// 終わったのがどちらの種別かを <paramref name="kind"/> に返す（<c>false</c> のときの
        /// 値は不定——呼び出し側は戻り値が <c>true</c> のときだけ見ること）。
        /// <see cref="VrmCharacter"/> はこれを見て <c>IdleAccentTimer.Reset</c> /
        /// <c>EmotionMotionTrigger.NotifyEnded</c> を呼ぶ。
        ///
        /// ★★ <b>小ネタ（<see cref="MotionKind.Accent"/>）の終了でも <c>NotifyEnded</c> を
        ///   呼んではいけない</b>（#70 レビュー #3）。<c>EmotionMotionTrigger.NotifyEnded</c> は
        ///   感情モーションのクールダウンの起点で、小ネタの終了はそれとは無関係——
        ///   呼び出し側が <paramref name="kind"/><c> == MotionKind.Emotion</c> を確かめてから呼ぶ。
        /// </summary>
        public bool ConsumeEnded(out MotionKind kind)
        {
            kind = _kind;
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
                finally
                {
                    // ★★ #70 レビュー #2。正常完了・早期 return（_disposed / キャンセル）・例外の
                    //   どの経路でも通る場所に置くこと。「そこまでに読めた分」で Loaded を組んでおかないと、
                    //   1本目の読み込みが失敗しただけで Loaded が永久に null のまま残り、
                    //   MotionClips が「読み込み中」から一生変わらない。
                    // ★ _disposed のときは組まない——Dispose() が既に _loaded を Clear 済みで、
                    //   ここで組んでも空になるだけ（そして誰も読まない）
                    if (!_disposed && Manifest != null) Loaded = BuildLoadedManifest();
                }
            }
        }

        /// <summary>
        /// <see cref="Loaded"/> の組み立て。<see cref="Manifest"/> の各カテゴリを
        /// <see cref="AnimationManifest.Clips"/> の並びのまま回し、<c>_loaded.ContainsKey</c> で
        /// 絞り込む。
        ///
        /// ★ <c>_loaded.Keys</c> を直接 <c>AnimationManifest.FromClips</c> に渡さないこと——
        ///   <c>Dictionary</c> の列挙順は挿入順の保証が無く、設定パネルの一覧がファイル名順から
        ///   崩れうる。
        /// </summary>
        private AnimationManifest BuildLoadedManifest()
        {
            var loadedClips = new List<MotionClip>();
            foreach (var category in MotionCategories.All)
            {
                foreach (var clip in Manifest.Clips(category))
                {
                    if (_loaded.ContainsKey(clip)) loadedClips.Add(clip);
                }
            }
            return AnimationManifest.FromClips(loadedClips);
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

            // ★★ 寝かせるのは Stop() で。enabled = false にしないこと。無効化した legacy Animation は
            //   有効化し直しても最初の更新まで state への操作（time = 0 / Rewind / wrapMode）を捨てる
            //   ——実機で 2 回目の再生が前回の終端から始まり 0.5 秒で待機に戻った（3 回目は time=6.65）。
            //   Stop() は「止めて先頭へ巻き戻す」契約で、止まっている Animation のコストは無い
            animation.Stop();

            var animator = vrma.GetComponent<Animator>();
            if (animator != null) animator.enabled = false;

            if (vrma.ExpressionMap.Count > 0)
            {
                Debug.LogWarning($"[Mascot] モーション {clip.FileName} は表情アニメーションを持つので、" +
                                  "emotion とリップシンクが上書きされます");
            }

            _loaded[clip] = new LoadedClip(vrma, animation);
            return true;
        }

        /// <summary>
        /// 再生を開始する。開始できたら <see cref="MotionPlayResult.Started"/>。
        ///
        /// 拒否する条件と戻り値（#70 レビュー #5。1 対 1 で対応する）:
        /// 既に破棄されている（<see cref="MotionPlayResult.Disposed"/>）／
        /// 待機 VRMA が未読込（<see cref="VrmIdleAnimation.IsLoaded"/>、
        /// <see cref="MotionPlayResult.IdleNotLoaded"/>）／
        /// 設定「待機モーション」が OFF（<see cref="MotionPlayResult.IdleDisabled"/>）／
        /// <paramref name="clip"/> が <c>null</c> か読めていない
        /// （<see cref="MotionPlayResult.NotLoaded"/>）／
        /// <paramref name="kind"/><c> == Emotion</c> で既に感情モーション再生中、または
        /// <paramref name="kind"/><c> == Accent</c> で既に何か再生中
        /// （どちらも <see cref="MotionPlayResult.Busy"/>）。
        ///
        /// ★ <b>感情モーションは小ネタ（<c>Accent</c>）に割り込める。</b> 割り込まれた小ネタの
        ///   クリップは <c>Animation.Stop()</c> で寝かせ直す。感情モーション同士は
        ///   割り込まない（上の拒否条件どおり）。
        /// </summary>
        public MotionPlayResult Play(MotionClip clip, MotionKind kind, double now)
        {
            if (_disposed) return MotionPlayResult.Disposed;
            if (!_idle.IsLoaded) return MotionPlayResult.IdleNotLoaded;
            // ★ 設定「待機モーション」OFF の間は始めない。Present が no-op なので見えないまま
            //   クリップの Animation だけ走り、FadeIn の後に Playing の防御で畳まれる——
            //   1本ぶん無駄に再生するだけで実害は無いが、その経路を最初から作らない
            if (!_idle.Enabled) return MotionPlayResult.IdleDisabled;
            if (clip == null) return MotionPlayResult.NotLoaded;
            if (!_loaded.TryGetValue(clip, out var loaded)) return MotionPlayResult.NotLoaded;
            if (kind == MotionKind.Emotion && IsPlayingEmotion) return MotionPlayResult.Busy;
            if (kind == MotionKind.Accent && IsPlaying) return MotionPlayResult.Busy;

            // ★ ここに来る「_current != null」は、感情モーションが小ネタへ割り込むケースだけ
            //   （上のガードにより、Accent は何も再生中でないときしか開始できない）
            if (_current != null)
            {
                _current.Animation.Stop();
            }

            // ★ Stop() → Play() で先頭から。enabled は触らない（TryAdopt の ★★ 参照）。
            //   wrapMode は Play() の後に毎回立て直す（importer は Loop 固定で、Stop/Play が
            //   state を作り直しても困らないように）
            loaded.Animation.Stop();
            loaded.Animation.Play();
            var state = loaded.State;
            if (state != null)
            {
                state.wrapMode = WrapMode.ClampForever;
                state.time = 0;
            }
            _startedAt = now;
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
            // ★ 1本1行。実機で「出た／出ない」を Player.log から判定する唯一の手がかり
            //   （docs/mascot.md「顔が動かないのが正常と壊れて動かないはログでしか区別できない」と同じ理由）
            // ★★ #70 レビュー #8。上で取った state をそのまま使うこと。loaded.State を
            //   もう一度引くと LoadedClip.State の doc どおり foreach を二重に走らせる無駄がある
            Debug.Log($"[Mascot] モーション開始: {kind} {clip.FileName}（{MotionCategories.DirectoryName(clip.Category)}、{(state != null ? state.length : 0f):F1}s）");
            return MotionPlayResult.Started;
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
                        _current.Animation.Stop();
                        _current = null;
                        _fade = null;
                        _state = PlayState.Idle;
                        return;
                    }

                    // ★ length まで待つと最終フレームを FadeSeconds ぶん保持してから戻る
                    var state = _current.State;
                    if (state == null || state.time >= state.length - _params.FadeSeconds)
                    {
                        if (state != null)
                        {
                            Debug.Log($"[Mascot] モーション: フェードアウト開始 time={state.time:F2} length={state.length:F2} 経過={now - _startedAt:F2}s");
                        }
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
                        if (_current != null) _current.Animation.Stop();
                        Debug.Log($"[Mascot] モーション終了: {_kind} → 待機");
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
                _current.Animation.Stop();
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
