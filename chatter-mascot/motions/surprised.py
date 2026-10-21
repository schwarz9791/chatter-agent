"""surprised: やや足を広げて立ち、両手を肩の横くらいの位置に上げて手を開く。

blender -b --python-exit-code 1 --python surprised.py -- <出力ディレクトリ>  # ふつうは build.sh から
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mathutils import Vector  # noqa: E402

import vrmalib as v  # noqa: E402

OUT = sys.argv[sys.argv.index("--") + 1]
END = 66

m = v.Motion(END, with_fingers=True)

OPEN = v.fingers(curl=0.1, spread=12)


# 肩の横で、掌を前に向けて指を上へ。胴が反っても付いてくるよう upperChest からの位置で決める
def hands_up(lift=0.0, out=0.0):
    def one(side):
        sx = 1 if side == "left" else -1

        def target(W, P):
            return P["upperChest"] + W["upperChest"] @ Vector((sx * (0.28 + out), -0.07, 0.08 + lift))

        return {"target": target, "pole": (sx * 0.6, 0.3, -1.0),
                "hand": (("x", -90), ("y", -sx * 75))}

    return {"left": one("left"), "right": one("right")}


WIDE = {"left": {"lift": (0.055, 0.0, 0.0), "yaw": 8},
        "right": {"lift": (-0.055, 0.0, 0.0), "yaw": -8}}


def torso(back=4.0, head=-6.0, shoulder=-8.0):
    return {
        "spine": (("x", -back),),
        "chest": (("x", -back),),
        "head": (("x", head),),
        **v.sym({"leftShoulder": (("y", shoulder),)}),
    }


m.at(0)
# ビクッ
m.at(3, drop=-0.012, feet={"left": {"lift": (0.025, 0.0, 0.025)}, "right": {"lift": (-0.025, 0.0, 0.025)}},
     torso=torso(back=2, head=-3, shoulder=-6), hands=hands_up(lift=-0.12, out=-0.05), open=OPEN)
m.at(7, drop=0.03, feet=WIDE, torso=torso(), hands=hands_up(), open=OPEN)
m.at(11, drop=0.018, feet=WIDE, torso=torso(back=5, head=-8), hands=hands_up(lift=0.015), open=OPEN)
# 固まったまま小さく震える
m.at(16, drop=0.02, feet=WIDE, torso=torso(back=4, head=-7), hands=hands_up(lift=0.0, out=0.01), open=OPEN)
m.at(21, drop=0.019, feet=WIDE, torso=torso(back=4, head=-7), hands=hands_up(lift=0.01), open=OPEN)
m.at(26, drop=0.02, feet=WIDE, torso=torso(back=4, head=-6), hands=hands_up(lift=0.0, out=0.01), open=OPEN)
m.at(36, drop=0.02, feet=WIDE, torso=torso(back=3, head=-5), hands=hands_up(lift=-0.01), open=OPEN)
# 手を下ろし、片足ずつ戻す
m.at(44, drop=0.02, feet=WIDE, torso=torso(back=1, head=-2, shoulder=-2), hands=hands_up(lift=-0.18, out=-0.04))
m.at(49, drop=0.015, feet={"left": WIDE["left"], "right": {"lift": (-0.03, 0.0, 0.035)}}, shift=(0.012, 0.0))
m.at(53, drop=0.012, feet={"left": WIDE["left"]}, shift=(0.0, 0.0))
m.at(57, drop=0.008, feet={"left": {"lift": (0.03, 0.0, 0.035)}}, shift=(-0.01, 0.0))
m.at(61, feet={})
m.at(END)

m.smooth(3, 46)
m.finish(OUT, "startle", detail=(7, 21, 44), detail_views=("Face",))
