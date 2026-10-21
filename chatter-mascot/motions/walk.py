"""walk: その場で歩くループ。少しモデル歩きに寄せる（足を中心線の近くに着き、腰を大きめに揺らす）。

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

STANCE = 0.58  # 1周期のうち足が床に着いている割合
TRAVEL = SPEED * CYCLE * STANCE  # 接地中に足が後ろへ流れる距離
CLEAR = 0.075  # 振り出す足の上がる高さ
INWARD = 0.035  # 着地点を中心線へ寄せる量（モデル歩き）
OUTWARD = 0.02  # 振り出す途中は外へ逃がす（軸足とぶつからないように）

m = v.Motion(END)
rig = m.rig
rig._hierarchy()
ANKLE = {s: rig._rest_pos[f"{s}Foot"] for s in ("left", "right")}
# かかと・母指球の位置（足首から）。足を傾けるときの支点
HEEL = Vector((0.0, 0.035, -0.12))
BALL = Vector((0.0, -0.105, -0.115))


def smooth(t):
    return t * t * (3 - 2 * t)


def pivot_lift(pitch_deg, around):
    """足を around（足首から見た支点）まわりに pitch だけ倒したとき、足首が動く量。"""
    q = v.rot(("x", pitch_deg))
    return -(q @ around) + around


def foot(side, p):
    """p: その足のかかとが着く瞬間を 0 とした位相（0〜1）。足首のずれ・足の傾き・向きを返す。"""
    sx = 1 if side == "left" else -1
    p %= 1.0
    if p < STANCE:
        t = p / STANCE
        y = -TRAVEL / 2 + TRAVEL * t  # 前（-Y）から後ろへ流れる
        x = -sx * INWARD
        if t < 0.12:  # かかとで着いて、足裏を下ろす
            pitch = -14 * (1 - smooth(t / 0.12))
            lift = pivot_lift(pitch, HEEL)
        elif t > 0.7:  # かかとを上げて、母指球で蹴る
            pitch = 32 * smooth((t - 0.7) / 0.3)
            lift = pivot_lift(pitch, BALL)
        else:
            pitch, lift = 0.0, Vector()
        z = 0.0
    else:
        t = (p - STANCE) / (1 - STANCE)
        e = smooth(t)
        y = TRAVEL / 2 - TRAVEL * e
        x = -sx * INWARD + sx * OUTWARD * math.sin(math.pi * t)
        z = CLEAR * math.sin(math.pi * min(1.0, t * 1.1))
        pitch = 32 * (1 - smooth(min(1.0, t / 0.45))) - 14 * smooth(max(0.0, (t - 0.55) / 0.45))
        lift = pivot_lift(32, BALL) * (1 - smooth(min(1.0, t / 0.3))) + pivot_lift(-14, HEEL) * smooth(max(0.0, (t - 0.7) / 0.3))
    pos = Vector((x, y, z)) + lift
    return {"lift": tuple(pos), "pitch": pitch, "yaw": sx * 5}


for f in range(END + 1):
    p = f / END
    pl, pr = p, p - 0.5
    # 腰: 軸足の側へ寄せて持ち上げる（モデル歩きの腰の振り）、振り出す脚の側を前へ回す
    sway = math.sin(2 * math.pi * p)  # 左足が軸のとき正
    roll = -7.0 * sway
    yaw = -7.0 * math.cos(2 * math.pi * p)
    # 両足が前後に開く瞬間がいちばん低く、軸脚が真下に来て伸びる瞬間がいちばん高い
    drop = 0.035 + 0.025 * math.cos(4 * math.pi * p)
    # 腕は脚と逆に振る。左足が前（p=0）のとき左腕は後ろ
    swing = 13.0 * math.cos(2 * math.pi * p)
    parts = {
        "hips": {"hips": (("y", roll), ("z", yaw))},
        # 肩は腰と逆に回す（前に出た足と反対側の肩が前へ）。頭はそのぶん戻して正面を向ける
        "torso": {
            "spine": (("y", -roll * 0.7), ("z", -yaw * 0.9)),
            "chest": (("x", -1), ("z", -yaw * 1.4)),
            "neck": (("y", -roll * 0.2), ("z", yaw * 0.6)),
            "head": (("x", -1), ("z", yaw * 0.7)),
        },
        "arms": {
            "leftShoulder": (("y", 2),),
            "rightShoulder": (("y", -2),),
            "leftUpperArm": (("y", 78), ("x", swing - 2)),
            "rightUpperArm": (("y", -78), ("x", -swing - 2)),
            "leftLowerArm": (("z", -12 - max(0.0, -swing) * 0.6),),
            "rightLowerArm": (("z", 12 + max(0.0, swing) * 0.6),),
        },
    }
    m.at(f, drop=drop, shift=(0.045 * sway, 0.0), lag=None,
         feet={"left": foot("left", pl), "right": foot("right", pr)}, **parts)

# 検算: 足首が狙った位置に来ているか（脚が届かないと浮く）と、接地中の足が一定の速さで流れているか
import bpy  # noqa: E402

arm = rig.arm
worst = {"left": 0.0, "right": 0.0}
for f in range(END + 1):
    bpy.context.scene.frame_set(f)
    p = f / END
    for side, ph in (("left", p), ("right", p - 0.5)):
        want = ANKLE[side] + Vector(foot(side, ph)["lift"])
        got = arm.matrix_world @ arm.pose.bones[rig.bone[f"{side}Foot"]].head
        worst[side] = max(worst[side], (got - want).length)
print("ik worst error (mm):", {k: round(val * 1000, 2) for k, val in worst.items()})
print("ground speed (m/s):", round(SPEED, 3), "stance travel (m):", round(TRAVEL, 3))

m.finish(OUT, "model-walk", detail=range(0, END + 1, 3), detail_views=("Side", "Front"))
