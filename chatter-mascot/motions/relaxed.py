"""relaxed: 手を後ろに回して上半身を左右に軽く振る。右足のつま先で床をゆっくりトンとする。

blender -b --python-exit-code 1 --python relaxed.py -- <出力ディレクトリ>  # ふつうは build.sh から
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mathutils import Vector  # noqa: E402

import vrmalib as v  # noqa: E402

OUT = sys.argv[sys.argv.index("--") + 1]
END = 108

m = v.Motion(END)


# 腰の後ろで手を組む。胴が揺れても手が背中に付いてくるよう、spine からの位置で決める
def behind(side):
    x = 0.035 if side == "left" else -0.035

    def target(W, P):
        return P["spine"] + W["spine"] @ Vector((x, 0.16, -0.07))

    return {"target": target, "pole": (1.0 if side == "left" else -1.0, 0.6, -0.1)}


HANDS = {"left": behind("left"), "right": behind("right")}

# 右足は少し後ろへ引いて、つま先を床に立てる
TAP = {"lift": (-0.02, 0.07, 0.057), "pitch": 40}
TAP_UP = {"lift": (-0.02, 0.07, 0.087), "pitch": 40}
SHIFT = (0.02, 0.0)


def sway(lean):
    return {
        "spine": (("y", lean),),
        "chest": (("y", lean * 0.6),),
        "neck": (("y", lean * 0.4),),
        "head": (("x", -2), ("y", lean)),
    }


m.at(0)
m.at(8, shift=(0.01, 0.0), feet={"right": {"lift": (-0.01, 0.04, 0.06), "pitch": 20}},
     torso=sway(0))
m.at(14, shift=SHIFT, hands=HANDS, feet={"right": TAP}, torso=sway(1))
m.at(26, shift=SHIFT, hands=HANDS, feet={"right": TAP}, torso=sway(4))
# トン（ゆっくり1回）
m.at(32, shift=SHIFT, hands=HANDS, feet={"right": TAP_UP}, torso=sway(3))
m.at(38, shift=SHIFT, hands=HANDS, feet={"right": TAP}, torso=sway(1))
m.at(48, shift=SHIFT, hands=HANDS, feet={"right": TAP}, torso=sway(-4))
m.at(62, shift=SHIFT, hands=HANDS, feet={"right": TAP}, torso=sway(1))
m.at(68, shift=SHIFT, hands=HANDS, feet={"right": TAP_UP}, torso=sway(3))
m.at(74, shift=SHIFT, hands=HANDS, feet={"right": TAP}, torso=sway(4))
m.at(84, shift=SHIFT, hands=HANDS, feet={"right": TAP}, torso=sway(-2))
# 手を戻し、右足を元の位置へ
m.at(93, shift=(0.01, 0.0), feet={"right": {"lift": (-0.01, 0.04, 0.06), "pitch": 20}},
     torso=sway(-1))
m.at(100, feet={})
m.at(END)

m.smooth(3, 98)
m.finish(OUT, "sway", detail=(14, 26, 48, 66, 84), detail_views=("Back",), detail_tile=(400, 480))
