"""sad: うつむいて肩を落とす。

blender -b --python-exit-code 1 --python sad.py -- <出力ディレクトリ>  # ふつうは build.sh から
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mathutils import Vector  # noqa: E402

import vrmalib as v  # noqa: E402

OUT = sys.argv[sys.argv.index("--") + 1]
END = 96

m = v.Motion(END, with_fingers=True)

# 指を軽く握り込んで、両手を組んだように見せる
GRIP = v.fingers(curl=2.2)


# お腹の前で両手をふわっと組む。胴が丸まっても付いてくるよう spine からの位置で決める
def clasp(close=1.0):
    def one(side):
        sx = 1 if side == "left" else -1
        rest = Vector((sx * 0.30, -0.02, 0.86))

        def target(W, P):
            want = P["spine"] + W["spine"] @ Vector((sx * 0.07, -0.17, -0.0))
            return rest.lerp(want, close)

        return {"target": target, "pole": (sx * 1.0, 0.5, -0.6)}

    return {"left": one("left"), "right": one("right")}


# 肩を落として前へ丸める
def slump(spine, chest, upper, neck, head_x, head_z=0.0, shoulder=7.0, close=1.0):
    return {
        "torso": {
            "spine": (("x", spine),),
            "chest": (("x", chest),),
            "upperChest": (("x", upper),),
            "neck": (("x", neck),),
            "head": (("x", head_x), ("z", head_z)),
        },
        "arms": v.sym({"leftShoulder": (("y", shoulder), ("z", -shoulder))}),
        "grip": GRIP if close > 0.5 else {},
        "hands": clasp(close),
    }


m.at(0)
# 小さく息を吸う。肩を上げたぶん腕を下げて、腕が開かないようにする
m.at(10, arms=v.sym({"leftShoulder": (("y", -5),), "leftUpperArm": (("y", 77), ("x", -4))}),
     torso={"chest": (("x", -3),), "upperChest": (("x", -2),), "head": (("x", -3),)})
# 吐きながら、背中を丸めてうつむく。顔が隠れると sad の表情が見えないので、深くしすぎない
m.at(26, drop=0.015, **slump(5, 5, 3, 7, 10))
m.at(36, drop=0.02, **slump(6, 5, 3, 8, 12))
# うつむいたまま、首をゆっくり小さく振る
m.at(48, drop=0.02, **slump(6, 5, 3, 8, 12, head_z=5))
m.at(60, drop=0.02, **slump(6, 5, 3, 8, 13, head_z=-5))
m.at(70, drop=0.018, **slump(5, 5, 3, 7, 11))
# 立ち直る
m.at(81, drop=0.005, **slump(3, 2, 1, 3, 6, shoulder=2, close=0.35))
m.at(90)
m.at(END)

m.smooth(3, 88)
m.finish(OUT, "droop", detail=(18, 36, 60), detail_views=("Upper", "HandsR"), detail_tile=(420, 480))
