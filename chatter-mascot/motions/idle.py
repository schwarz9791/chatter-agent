"""idle（待機の小ネタ）: 片手を頬に添え、もう片方の手で反対の肘を支えて考え込む。

blender -b --python-exit-code 1 --python idle.py -- <出力ディレクトリ>  # ふつうは build.sh から
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mathutils import Vector  # noqa: E402

import vrmalib as v  # noqa: E402

OUT = sys.argv[sys.argv.index("--") + 1]
END = 150

m = v.Motion(END)


# 右手は頬へ。頭が傾いても手が付いてくるよう head からの位置で決める。掌を頬に向けて指を上へ。
# 左手は掌を上に向けて右肘の手前で前腕を支え、指先は後ろへ向ける。肘の真下や横向きにすると、
# 腕の長いモデルでは手が肘の先へ突き出す。右腕を先に解き、その肘の位置から決める（hands は書いた順に解く）
CHEEK = {"target": lambda W, P: P["head"] + W["head"] @ Vector((-0.085, -0.095, -0.055)),
         "pole": (-0.3, -0.1, -1.0), "hand": (("y", 90), ("z", 180))}
UNDER_ELBOW = {"target": lambda W, P: P["rightLowerArm"] + Vector((0.10, -0.02, -0.04)),
               "pole": (1.0, 0.3, -0.6), "hand": (("y", 180), ("z", -80))}


# 手を上げ下ろしする途中は、手を体の前へ出して肘を外へ張る。補間に任せると肘が脇に付いたまま
# 体の後ろを回る。offset は肩からの手首の位置
def passing(side, offset, out=0.6):
    sx = 1 if side == "left" else -1
    return {"target": lambda W, P: P[f"{side}UpperArm"] + Vector((sx * offset[0], offset[1], offset[2])),
            "pole": (sx * out, 0.1, -1.0)}


R_LOW = passing("right", (0.16, -0.20, -0.30))
R_HIGH = passing("right", (0.05, -0.22, -0.12))
L_LOW = passing("left", (0.12, -0.22, -0.36))

THINK = {"right": CHEEK, "left": UNDER_ELBOW}


def head(tilt=-5.0, nod=-3.0, turn=0.0):
    return {
        "neck": (("y", tilt * 0.4),),
        "head": (("x", nod), ("y", tilt), ("z", turn)),
        "spine": (("y", 1.5),),
    }


m.at(0)
# 右手が先に上がり、左手は遅れて肘を迎えに行く
m.at(7, shift=(0.005, 0.0), hands={"right": R_LOW}, torso=head(tilt=-1, nod=0))
m.at(14, shift=(0.01, 0.0), hands={"right": R_HIGH, "left": L_LOW}, torso=head(tilt=-3, nod=-1))
m.at(22, shift=(0.015, 0.0), hands=THINK, torso=head())
m.at(45, shift=(0.015, 0.0), hands=THINK, torso=head(tilt=-7, nod=-5, turn=-4))
m.at(70, shift=(0.015, 0.0), hands=THINK, torso=head(tilt=-3, nod=-1, turn=3))
m.at(95, shift=(0.015, 0.0), hands=THINK, torso=head(tilt=-6, nod=-4, turn=-2))
m.at(118, shift=(0.015, 0.0), hands=THINK, torso=head(tilt=-4, nod=-2))
# 左手を先に離し、右手は頬から前へ下ろす
m.at(125, shift=(0.012, 0.0), hands={"right": R_HIGH, "left": L_LOW}, torso=head(tilt=-2, nod=-1))
m.at(132, shift=(0.006, 0.0), hands={"right": R_LOW}, torso=head(tilt=-1, nod=0))
m.at(140, feet={})
m.at(END)

ARMS = [h for h in v.UPPER if "Arm" in h or "Hand" in h or "Shoulder" in h]
m.smooth(3, 138, humans=[h for h in v.UPPER if h not in ARMS])
# 腕は頬と肘に着いたところで止める。通り抜けさせると、着いた先へ行き過ぎて手が体にめり込む
m.smooth(3, 22, humans=ARMS)
m.smooth(120, 138, humans=ARMS)
m.finish(OUT, "think", detail=(7, 14, 22, 125, 132), detail_views=("Upper", "UpperR"), detail_tile=(420, 480))
