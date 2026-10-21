"""angry: 脇を締めて片足で地面を踏む。

blender -b --python-exit-code 1 --python angry.py -- <出力ディレクトリ>  # ふつうは build.sh から
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mathutils import Vector  # noqa: E402

import vrmalib as v  # noqa: E402

OUT = sys.argv[sys.argv.index("--") + 1]
END = 66

m = v.Motion(END, with_fingers=True)

# 拳。親指は人差し指の上へ畳む
FIST = {**v.fingers(curl=4.0, thumb=(("x", 40), ("z", 30))),
        **v.sym({"leftThumbProximal": (("z", 30),), "leftThumbDistal": (("z", 35),)})}


# 肩をいからせ、肘を曲げて脇を締め、拳を胸元へ。胴が動いても付いてくるよう upperChest からの位置で決める
def tense(shoulder=-8.0, down=0.0):
    def one(side):
        sx = 1 if side == "left" else -1

        def target(W, P):
            return P["upperChest"] + W["upperChest"] @ Vector((sx * 0.09, -0.18, -0.04 - down))

        return {"target": target, "pole": (sx * 0.35, 0.5, -1.0)}

    return {
        "arms": v.sym({"leftShoulder": (("y", shoulder),)}),
        "hands": {"left": one("left"), "right": one("right")},
    }


def torso(spine=5.0, head=2.0, head_turn=0.0, neck_turn=0.0, spine_turn=0.0):
    return {
        "spine": (("x", spine), ("z", spine_turn)),
        "chest": (("x", 3),),
        "neck": (("z", neck_turn),),
        "head": (("x", head), ("z", head_turn)),
    }


m.at(0)
# 力む。体重を左足へ
m.at(5, drop=0.01, shift=(0.025, 0.0), **tense(), fist=FIST, torso=torso(),
     feet={"right": {"lift": (0.0, -0.01, 0.05), "pitch": 5}})
# 1回目
m.at(9, drop=0.01, shift=(0.03, 0.0), **tense(), fist=FIST, torso=torso(head=0),
     feet={"right": {"lift": (0.0, -0.04, 0.16), "pitch": 10}})
m.at(12, drop=0.035, shift=(0.012, 0.0), **tense(shoulder=-4, down=0.03), fist=FIST, torso=torso(spine=7, head=6),
     feet={})
m.at(15, drop=0.015, shift=(0.02, 0.0), **tense(), fist=FIST, torso=torso(head=3), feet={})
# 2回目
m.at(20, drop=0.012, shift=(0.03, 0.0), **tense(), fist=FIST, torso=torso(head=0),
     feet={"right": {"lift": (0.0, -0.03, 0.13), "pitch": 10}})
m.at(23, drop=0.04, shift=(0.012, 0.0), **tense(shoulder=-4, down=0.03), fist=FIST, torso=torso(spine=7, head=7),
     feet={})
m.at(27, drop=0.015, shift=(0.0, 0.0), **tense(), fist=FIST, torso=torso(head=3), feet={})
# ぷいっと顔をそむける
m.at(34, drop=0.01, **tense(shoulder=-6), fist=FIST,
     torso=torso(spine=3, head=-3, head_turn=24, neck_turn=8, spine_turn=5), feet={})
m.at(46, drop=0.01, **tense(shoulder=-5), fist=FIST,
     torso=torso(spine=3, head=-4, head_turn=26, neck_turn=8, spine_turn=5), feet={})
m.at(56, drop=0.003, torso=torso(spine=1, head=0, head_turn=6, neck_turn=2, spine_turn=1), feet={})
m.at(END)

m.smooth(3, 54)
m.finish(OUT, "stomp", detail=(5, 12, 34), detail_views=("Upper", "UpperR"), detail_tile=(420, 480))
