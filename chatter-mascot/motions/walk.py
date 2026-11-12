"""walk: その場で歩くループ。ゆっくり歩く。足は中心線の近くに着く。

アプリはモデルを動かす速さを「歩幅 × 1周期の歩数 ÷ 周期」で決める（XrWalk の StrideMeters /
StepsPerCycle / CycleSeconds）。接地中の足がその速さで後ろへ流れれば足は滑らないので、同じ値で作る。

blender -b --python-exit-code 1 --python walk.py -- <出力ディレクトリ>  # ふつうは build.sh から
"""
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mathutils import Vector  # noqa: E402

import vrmalib as v  # noqa: E402

OUT = sys.argv[sys.argv.index("--") + 1]

STRIDE = 0.53  # 1歩で進む距離（m）
CYCLE = 1.30  # 2歩の周期（秒）
END = round(CYCLE * v.FPS)  # 最後のフレームは最初と同じポーズ（ループのつなぎ目）
SPEED = STRIDE * 2 / CYCLE

STANCE = 0.60  # 1周期のうち足が床に着いている割合
TRAVEL = SPEED * CYCLE * STANCE  # 接地中に足が後ろへ流れる距離
# かかとが着く位置（立ち姿の足から前へ）。残りは後ろへ流れる。前を短くして、体が足より先に進むようにする
FRONT = 0.275
FLAT = 0.14  # 接地のうち、足裏が着き切るまでの割合
HEEL_UP = 14.0  # かかとで着くときにつま先を上げる角度
TRAIL_KNEE = 7.0  # 後ろへ流れる脚は膝がここまで伸びたら、あとはかかとを上げて届かせる
SOFT = 0.001  # 届かなくなる手前から、かかとを上げ始める幅（m）。膝が TRAIL_KNEE に当たって急に止まらないように
PUSH = 0.44  # 後ろの脚が膝を曲げて蹴り始める位相（もう片方の足が着く少し前）
PUSH_KNEE = 30.0  # 床を離れる瞬間の膝の曲げ
KICK = 0.03  # 振り出しの前半に、足をさらに上げる高さ
INWARD = 0.035  # 着地点を中心線へ寄せる量
OUTWARD = 0.02  # 振り出す途中は外へ逃がす（軸足とぶつからないように）
TOE_OUT = 5.0  # つま先を外へ向ける角度

# 腰の高さ: 前の足が着いた直後がいちばん低く、軸脚の真上を過ぎた頃がいちばん高い。
# 着いた脚が伸びきらず、曲がりすぎもしない高さに合わせる（下の検算の膝の角度で見る）
HIP_LOW = 0.024  # いちばん低いときに、立ち姿から下げる量
HIP_RISE = 0.0145  # そこから上がる量
HIP_PHASE = 0.03  # いちばん低くなる位相
SWAY = 0.02  # 腰を軸足の側へ寄せる量
ROLL = 4.0  # 腰の傾き（軸足の側が上がる）
YAW = 5.0  # 腰の回り（振り出す脚の側が前へ）
LEAN = 1.5  # 腰と背をそれぞれ前へ倒す角度
ARM = 12.0  # 腕の振り

SIDES = ("left", "right")
m = v.Motion(END)
rig = m.rig
rig._hierarchy()
ANKLE = {s: rig._rest_pos[f"{s}Foot"] for s in SIDES}
# かかと・つま先の、床に着く点（足首から）。足を傾けるときの支点
HEEL = Vector((0.0, 0.04, -ANKLE["left"].z))
TOE = Vector((0.0, -0.14, -ANKLE["left"].z))


def smooth(t):
    return t * t * (3 - 2 * t)


def hermite(t, a, da, b, db):
    """a から b へ。両端の速さ（t あたり）を da・db に合わせる。"""
    return ((2 * t - 3) * t * t + 1) * a + ((t - 2) * t + 1) * t * da + (3 - 2 * t) * t * t * b + (t - 1) * t * t * db


def pivot_lift(pitch_deg, around):
    """足を around（足首から見た支点）まわりに pitch だけ倒したとき、足首が動く量。"""
    q = v.rot(("x", pitch_deg))
    return -(q @ around) + around


def reach(knee_deg):
    """膝を knee だけ曲げたときの、股関節から足首までの長さ。"""
    return math.sqrt(rig.thigh ** 2 + rig.shin ** 2 + 2 * rig.thigh * rig.shin * math.cos(math.radians(knee_deg)))


def hips(p):
    """位相 p（左足のかかとが着く瞬間が 0）の腰の傾き・回りと、横への寄り・下げる量。"""
    roll = -ROLL * math.sin(2 * math.pi * (p + 0.03))
    yaw = -YAW * math.cos(2 * math.pi * p)
    sway = SWAY * math.sin(2 * math.pi * p)  # 左足が軸のとき正
    drop = HIP_LOW - HIP_RISE * (1 - math.cos(4 * math.pi * (p - HIP_PHASE))) / 2
    return roll, yaw, sway, drop


def hips_steps(roll, yaw):
    return (("x", LEAN), ("y", roll), ("z", yaw))


def hip_joint(side, p):
    """位相 p での股関節の位置（立ち姿の足首から）。"""
    roll, yaw, sway, drop = hips(p)
    _, P = rig.fk({"hips": v.rot(*hips_steps(roll, yaw))}, (sway, 0.0, -drop))
    return P[f"{side}UpperLeg"] - ANKLE[side]


def stance(side, t):
    """接地中（t: 0〜1）の足首のずれと足の傾き。床に着いた点（かかと → 足裏 → つま先）は床と一緒に流れる。"""
    sx = 1 if side == "left" else -1
    flat = Vector((-sx * INWARD, -FRONT + TRAVEL * t, 0.0))
    # 足は外へ向けてから傾けるので、支点まわりのずれも同じ向きに回す（回さないと床に着いた点が横へ滑る）
    turn = v.rot(("z", sx * TOE_OUT))
    if t < FLAT:  # かかとで着いて、すぐ足裏を下ろす
        pitch = -HEEL_UP * (1 - t / FLAT) ** 2
        return flat + turn @ pivot_lift(pitch, HEEL), pitch
    # 後ろへ流れた足に脚が届かなくなったぶんだけ、かかとを上げる。終わりは膝を曲げて蹴る
    u = max(0.0, (t * STANCE - PUSH) / (STANCE - PUSH))
    far = reach(TRAIL_KNEE + (PUSH_KNEE - TRAIL_KNEE) * (2 - u) * u * u)
    hip = hip_joint(side, t * STANCE + (0.0 if side == "left" else 0.5))
    d0 = (flat - hip).length
    if d0 > far - 8 * SOFT:  # 届かなくなる境目を滑らかにする（十分届くあいだは元のまま）
        far = min(d0, far) - SOFT * math.log(1 + math.exp(-abs(d0 - far) / SOFT))
    lo, hi = 0.0, 80.0
    for _ in range(24):
        pitch = (lo + hi) / 2
        if (flat + turn @ pivot_lift(pitch, TOE) - hip).length > far:
            lo = pitch
        else:
            hi = pitch
    return flat + turn @ pivot_lift(hi, TOE), hi


def edge(side, t, dt):
    """接地の端 t での足首・傾きと、その速さ（振り出しの t あたり）。"""
    (a, pa), (b, pb) = stance(side, t), stance(side, t + dt)
    k = (1 - STANCE) / STANCE / dt
    return a, (b - a) * k, pa, (pb - pa) * k


OFF = {s: edge(s, 1.0, -1e-3) for s in SIDES}  # 床を離れる瞬間
ON = {s: edge(s, 0.0, 1e-3) for s in SIDES}  # 床に着く瞬間


def foot(side, p):
    """p: その足のかかとが着く瞬間を 0 とした位相（0〜1）。足首のずれ・足の傾き・向きを返す。"""
    sx = 1 if side == "left" else -1
    p %= 1.0
    if p < STANCE:
        pos, pitch = stance(side, p / STANCE)
    else:
        t = (p - STANCE) / (1 - STANCE)
        (a, da, pa, dpa), (b, db, pb, _) = OFF[side], ON[side]
        # 振り出しは、離れる瞬間の動きを引き継いで始め、着く瞬間の動きへつないで終える
        pos = hermite(t, a, da, b, db)
        # 足は後ろで上げて、前へは低く運ぶ（山を前半に寄せる）
        pos.z += KICK * math.sin(math.pi * 2.5 * t / (1 + 1.5 * t)) ** 2
        pos.x += sx * OUTWARD * math.sin(math.pi * t) ** 2
        pitch = (pa + dpa * t) * (1 - smooth(min(1.0, t / 0.6))) + pb * smooth(max(0.0, (t - 0.4) / 0.6))
    return {"lift": tuple(pos), "pitch": pitch, "yaw": sx * TOE_OUT}


for f in range(END + 1):
    p = f / END
    roll, yaw, sway, drop = hips(p)
    # 腕は脚と逆に、少し遅れて振る。左足が前（p=0）のとき左腕は後ろ。前へ振るときだけ肘を曲げる
    swing = ARM * math.cos(2 * math.pi * (p - 0.03))
    bend = ARM * math.cos(2 * math.pi * (p - 0.06))
    parts = {
        "hips": {"hips": hips_steps(roll, yaw)},
        # 肩は腰と逆に小さく回す（前に出た足と反対側の肩が前へ）。頭はそのぶん戻して正面を向ける
        "torso": {
            "spine": (("x", LEAN), ("y", -roll * 0.6), ("z", -yaw * 0.6)),
            "chest": (("y", -roll * 0.5), ("z", -yaw * 1.1)),
            "neck": (("y", roll * 0.1), ("z", yaw * 0.3)),
            "head": (("x", -LEAN), ("z", yaw * 0.4)),
        },
        # 前へ倒した胴に付いていかず、腕はまっすぐ下がったところを中心に振る
        "arms": {
            "leftShoulder": (("y", 2),),
            "rightShoulder": (("y", -2),),
            "leftUpperArm": (("y", 78), ("x", swing - 2 * LEAN)),
            "rightUpperArm": (("y", -78), ("x", -swing - 2 * LEAN)),
            "leftLowerArm": (("z", -12 - max(0.0, -bend) * 0.9),),
            "rightLowerArm": (("z", 12 + max(0.0, bend) * 0.9),),
        },
    }
    m.at(f, drop=drop, shift=(sway, 0.0), lag=None,
         feet={"left": foot("left", p), "right": foot("right", p - 0.5)}, **parts)

# 検算: 足首が狙った位置に来ているか（脚が届かないと浮く）と、膝の角度の範囲。崩れていたら書き出さない
import bpy  # noqa: E402

arm = rig.arm
worst = {s: 0.0 for s in SIDES}
knee = {s: [] for s in SIDES}
for f in range(END + 1):
    bpy.context.scene.frame_set(f)
    p = f / END
    for side, ph in (("left", p), ("right", p - 0.5)):
        want = ANKLE[side] + Vector(foot(side, ph)["lift"])
        got = arm.matrix_world @ arm.pose.bones[rig.bone[f"{side}Foot"]].head
        worst[side] = max(worst[side], (got - want).length)
        hip, mid, end = (arm.pose.bones[rig.bone[f"{side}{b}"]].head for b in ("UpperLeg", "LowerLeg", "Foot"))
        knee[side].append(math.degrees((mid - hip).angle(end - mid)))
low = min(min(k) for k in knee.values())
print("ik worst error (mm):", {k: round(val * 1000, 2) for k, val in worst.items()})
print("knee (deg): at heel strike", round(knee["left"][0], 1), "min", round(low, 1),
      "max", round(max(max(k) for k in knee.values()), 1))
print("ground speed (m/s):", round(SPEED, 3), "stance travel (m):", round(TRAVEL, 3))
assert max(worst.values()) < 1e-4, f"a leg does not reach its ankle target: {worst}"
assert low > 3.0, f"a knee locks straight: {low:.1f} deg"

m.finish(OUT, "model-walk", detail=range(0, END + 1, 3), detail_views=("Side", "Front"))
