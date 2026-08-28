using ChatterMascot.Protocol;
using UnityEngine;
using UniVRM10;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// 視線由来の頭の向きと、<c>prompt</c>（応答待ち通知）の前傾を<b>実ボーン</b>に乗せる。
    ///
    /// <b>実行順を <c>[DefaultExecutionOrder(11005)]</c> に固定する。このリポジトリで
    /// 最初に <c>DefaultExecutionOrder</c> を使うクラス。</b>
    ///
    /// ```
    /// 11000  Vrm10Instance.LateUpdate → Retarget(VRMA) → ControlRig.Process() → LookAt → Expression → SpringBone
    /// 11005  VrmPoseAccent.LateUpdate   ← ここ
    /// 11010  FastSpringBoneService.LateUpdate（揺れもののジョブ）
    /// ```
    ///
    /// ★ <b>11000 より後でなければならない。</b> <c>ControlRig</c> に書くと VRMA が有効なとき
    ///   <c>Vrm10Retarget.Retarget</c>（11000 の内部）に毎フレーム上書きされて消える。
    ///   だからここでは <b>ControlRig.Process() が書き終わった後の実ボーン</b>
    ///   （<see cref="Vrm10Instance.TryGetBoneTransform"/>）を触る。
    /// ★ <b>11010 より前でなければならない。</b> 揺れもの（髪など）は「このフレームの頭が
    ///   どこにあるか」を読んで揺れを計算する。11010 より後で頭を動かすと、
    ///   揺れものが1フレーム遅れた古い頭の位置を基準に揺れてしまう。
    ///
    /// ★ <b>回す枠は「カメラ空間」。「モデル空間」ではない。</b> <c>VrmCharacter.HeadYawDegrees</c> /
    ///   <c>HeadPitchDegrees</c> は「カーソルが画面のどこにあるか」から作った<b>画面空間の量</b>。
    ///   モデルは <c>VrmStage.FaceCamera</c> で 180°回ってカメラを向いているので、
    ///   <b>モデルの <c>right</c> はカメラから見た <c>left</c></b> になる。以前はここを
    ///   モデルルートの <c>up</c> / <c>right</c> で回していたが、それは<b>枠の選び方自体が誤り</b>
    ///   だった（実機で「カーソルと逆を見る」＝上下左右すべて鏡像として発覚）。
    ///   ボーンのローカル軸を避けたのは正しいが、避けた先が<b>モデル空間</b>だったのが誤りで、
    ///   入力（カーソル由来）が画面空間である以上、正しい枠は<b>カメラ空間</b>。
    ///
    /// ★ <b><see cref="VrmCharacter.NeutralAimFraction"/> と <c>VrmCharacter.headSensitivity</c> は
    ///   別物。</b> <c>headSensitivity</c>（cursor 追従の感度）は「動きの好み」のパラメータで、
    ///   <c>GazeAim.Evaluate</c> がカーソル位置から作る <c>HeadPitchDegrees</c> に効く。
    ///   <c>NeutralAimFraction</c> は「カメラが bounds の中心（腰のあたり）にあり、
    ///   目線より低い」という<b>配置の事実そのもの</b>から毎フレーム幾何的に導く、
    ///   カーソルの有無に関わらず常に乗る基準の下向き。混ぜて同じ変数で調整すると、
    ///   どちらかを触ったときにもう片方が意図せず動く。
    ///
    /// ★ <b>このクラス自身の <c>[SerializeField]</c>（<c>headTiltDegrees</c> / <c>leanDegrees</c> /
    ///   <c>accentLerpSeconds</c>）は Inspector から編集できない。</b> このコンポーネントは
    ///   <c>VrmCharacter.OnLoaded</c> が読み込み後に <c>AddComponent</c> で実行時に生やすので、
    ///   シーンにシリアライズされず、フィールド初期化子の値が常に使われる
    ///   （<c>neutralAimFraction</c> を <c>VrmCharacter</c> 側へ移した理由と同じ）。
    ///   調整可能にしたい値が今後出たら、<c>VrmCharacter</c> 側にプロパティで持たせること。
    /// </summary>
    [DefaultExecutionOrder(11005)]
    public sealed class VrmPoseAccent : MonoBehaviour
    {
        [Header("prompt の前傾")]
        [Tooltip("prompt で乗る頭の傾き（度）。正 = 顎を引く向き。★ 符号は実機のスクリーンショットで確定済み。安易に変えないこと")]
        [SerializeField] private float headTiltDegrees = 8f;

        [Tooltip("prompt で乗る前傾（度）。正 = カメラ側へ前傾。★ 符号は実機のスクリーンショットで確定済み。安易に変えないこと")]
        [SerializeField] private float leanDegrees = 5f;

        [Tooltip("prompt の重みが 0→1 / 1→0 に緩和されるのにかかる時定数（秒）")]
        [SerializeField] private float accentLerpSeconds = 0.2f;

        private Vrm10Instance _instance;
        private VrmCharacter _character;

        /// <summary>
        /// カメラ空間の基準。
        ///
        /// ★ <b>ここで <c>Camera.main</c> を引かないこと。</b> <see cref="VrmCharacter"/> が
        ///   <c>Start</c> で1回だけ引いた同じカメラを <see cref="Bind"/> で受け取る —— 別々に
        ///   引くと <c>MainCamera</c> タグの付け替えで2つのコンポーネントが違うカメラを
        ///   掴みうる。探索も「無い」ときの警告も <see cref="VrmCharacter.Start"/> 側に一本化した。
        /// </summary>
        private Transform _camera;

        private float _weight;

        /// <summary>
        /// <paramref name="character"/> が <c>Start</c> で1回だけ引いた <see cref="Camera"/>
        /// （<see cref="VrmCharacter.Camera"/>）を受け取る。
        ///
        /// ★ <b>ここで <c>Camera.main</c> を探し直さないこと。</b> 探索は
        ///   <see cref="VrmCharacter.Start"/> の1箇所に寄せてある。<c>Camera.main</c> が
        ///   無いときの警告もそちらが出す（ここで二重に出さない）。
        ///   <c>character.Camera</c> が <c>null</c> なら <c>_camera</c> も <c>null</c> のままにし、
        ///   <see cref="LateUpdate"/> の <c>_camera == null</c> ガードで頭の回転を丸ごと飛ばす。
        /// </summary>
        public void Bind(Vrm10Instance instance, VrmCharacter character)
        {
            _instance = instance;
            _character = character;
            _camera = character.Camera != null ? character.Camera.transform : null;
        }

        private void LateUpdate()
        {
            if (_instance == null || _character == null) return;

            var targetWeight = _character.Kind == SpeechKind.Prompt ? 1f : 0f;
            // ★ スナップさせないこと。姿勢が瞬間移動すると spring bone が跳ねる
            // ★ Time.deltaTime ではなく Time.unscaledDeltaTime を使うこと。位相
            //   （Time.realtimeSinceStartupAsDouble）と緩和は同じ時間軸で回す必要がある。
            //   Time.deltaTime を混ぜると Time.timeScale = 0 で緩和だけが凍り、timeScale が
            //   戻った瞬間に姿勢が飛ぶ（spring bone を跳ねさせないために避けている失敗そのもの）。
            _weight = GazeAim.Smooth(_weight, targetWeight, Time.unscaledDeltaTime, accentLerpSeconds);

            // ★ カメラが無ければ丸ごと飛ばす。ワールド軸やモデル軸で代用しないこと
            if (_camera == null) return;

            var right = _camera.right;   // 画面の右
            var up = _camera.up;         // 画面の上

            // ★ 「基準の下向き」= カメラ（見る人）が bounds の中心（腰のあたり）に置かれている
            //   ぶん、頭を下へ向ける幾何的な補正。cursor 追従（VrmCharacter.headSensitivity）とは
            //   別物 —— あちらは「動きの好み」、こちらは「カメラが低い」という配置の事実から
            //   導く角度で、prompt / cursor の有無に関わらず常に乗る。
            //   毎フレーム計算するのは、モデルの背丈・ウィンドウの縦横比・フレーミング距離が
            //   変わっても追随させるため（固定値にしない）。
            //   ★ 自分で TryGetBoneTransform を引き直さないこと。目ボーンは頭の子なので、
            //   ここ（実行順 11005）で引き直すと ControlRig.Process()（実行順 11000）が
            //   書き戻した後＝アクセント抜きの位置になり、VrmCharacter.LateUpdate（実行順 0）が
            //   測った「前フレームのアクセント込み」の位置とは別の点になる。だから
            //   VrmCharacter が1フレームに1回だけ測ってキャッシュした値を読む。
            var neutralPitchDegrees = 0f;
            if (_character.TryGetCachedGazeOrigin(out var eyeWorld))
            {
                var toEye = eyeWorld - _camera.position;
                var horizontalDistance = Vector3.ProjectOnPlane(toEye, Vector3.up).magnitude;
                // ★ 水平距離が 0 に近い（カメラの真上/真下）ときは atan2 が破綻するので 0 度に倒す
                if (horizontalDistance > 1e-4f)
                {
                    neutralPitchDegrees = Mathf.Atan2(toEye.y, horizontalDistance) * Mathf.Rad2Deg * _character.NeutralAimFraction;
                }
            }

            // ★ 下の符号は実機のスクリーンショットで決めたもの。導出で書き換えないこと。
            //   「カーソル右 → 頭が左」「カーソル上 → 顎を引く」という反転が実測で出たので、
            //   HeadYaw / headTilt / lean は反転させ、HeadPitch は正のまま使う。
            //   回転方向を頭の中で追って符号を決め直すと、このファイルで3回目の間違いになる。
            //   ★ 基準の下向きも同じ枠（既存 pitch の正 = 上）に乗せる。neutralPitchDegrees は
            //   「カメラを見るのに必要な下向きの量」を正の数で持っているので、下へ向けるには
            //   HeadPitchDegrees から引く（＝ AngleAxis に渡す角度としては負）。
            if (_instance.TryGetBoneTransform(HumanBodyBones.Head, out var head))
            {
                // ★ 代入せず乗算すること。ControlRig.Process() が既に書いた値の上に重ねる。
                // ★ HeadPitchDegrees と基準の下向きは同じ軸（right）まわりの回転なので、
                //   角度を足し合わせてから1回の AngleAxis にまとめてよい（同じ軸まわりの回転の
                //   合成は可換）。GazeParams.HeadPitchRangeDegrees（±25°）の clamp は cursor 由来の
                //   HeadPitchDegrees にしか掛かっていない —— 基準の下向きはそことは別枠の値なので、
                //   ここで合算しても cursor 側の可動域そのものを書き換えることにはならない。
                //   ★ 合計に別途上限は置いていない。NeutralAimFraction 自体が実機で調整できる
                //   逃げ道であり、根拠のない安全マージンをここで固定値として足すと、この修正が
                //   直そうとしている「頭が見る人を向いていない」を再び別の形で作ってしまう。
                head.rotation = Quaternion.AngleAxis(-_character.HeadYawDegrees, up)
                              * Quaternion.AngleAxis(_character.HeadPitchDegrees - neutralPitchDegrees, right)
                              * Quaternion.AngleAxis(-headTiltDegrees * _weight, right)
                              * head.rotation;
            }

            if (_instance.TryGetBoneTransform(HumanBodyBones.Spine, out var spine))
            {
                spine.rotation = Quaternion.AngleAxis(-leanDegrees * _weight, right) * spine.rotation;
            }
        }
    }
}
