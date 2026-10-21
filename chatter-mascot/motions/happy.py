"""happy: 小さく跳ねて両手を上げる。

blender -b --python-exit-code 1 --python happy.py -- <出力ディレクトリ>  # ふつうは build.sh から
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import vrmalib as v  # noqa: E402

OUT = sys.argv[sys.argv.index("--") + 1]
END = 72

m = v.Motion(END)

ARMS_BACK = v.sym({
    "leftUpperArm": (("y", 76), ("x", 14)),
    "leftLowerArm": (("z", -16),),
})

# 振り上げの途中。腕を前へ出し、掌を向かい合わせる（横を通すと一瞬 T ポーズになる）
ARMS_SWING = v.sym({
    "leftUpperArm": (("y", 72), ("x", -28)),
    "leftLowerArm": (("z", -14),),
})
ARMS_FORWARD = v.sym({
    "leftShoulder": (("y", -6),),
    "leftUpperArm": (("x", -90), ("z", -65), ("x", -35)),
    "leftLowerArm": (("z", -10),),
})

# 下ろす途中。ひねりを半分戻してから下ろす（一気に戻すと手首がねじれて見える）
ARMS_LOWERING = v.sym({
    "leftUpperArm": (("x", -35), ("y", 35), ("x", -6)),
    "leftLowerArm": (("z", -12),),
})


# 掌を前に向けて、斜め上へ V 字に上げる。lift で片腕ずつ高さを、wave で手首の振りを変える
def arms_up(lift_l=0.0, lift_r=0.0, wave_l=0.0, wave_r=0.0):
    def one(lift, wave):
        return {
            "leftShoulder": (("y", -12),),
            "leftUpperArm": (("x", -80), ("y", -50 - lift), ("x", 12)),
            "leftLowerArm": (("z", -12),),
            "leftHand": (("z", 8 + wave),),
        }

    left = one(lift_l, wave_l)
    right = {k: val for k, val in v.sym(one(lift_r, wave_r)).items() if k.startswith("right")}
    return {**left, **right}


ARMS_UP = arms_up()

m.at(0)
m.at(7, drop=0.05, arms=ARMS_BACK,
     torso={"spine": (("x", 6),), "chest": (("x", 3),), "head": (("x", 3),)})
m.at(11, drop=0.02, foot=6, arms=ARMS_SWING,
     torso={"spine": (("x", 2),)})
m.at(14, drop=-0.03, foot=18, arms=ARMS_FORWARD,
     torso={"spine": (("x", -3),), "head": (("x", -4),)})
m.at(17, drop=-0.06, foot=24, arms=ARMS_UP,
     torso={"spine": (("x", -5),), "chest": (("x", -5),), "upperChest": (("x", -3),),
            "neck": (("x", -3),), "head": (("x", -10),)})
m.at(22, drop=0.045, arms=ARMS_UP,
     torso={"spine": (("x", 2),), "head": (("x", -6),)})
m.at(27, drop=0.01, arms=ARMS_UP,
     torso={"spine": (("x", -3),), "chest": (("x", -2),), "head": (("x", -8),)})
# 胴を左右に揺らす。頭は胴と同じ側へ傾け、上がる側の腕を少し高く、手首を振る
m.at(33, drop=0.02, arms=arms_up(lift_l=6, lift_r=-4, wave_l=18),
     torso={"spine": (("x", -3), ("y", 5)), "chest": (("y", 3),),
            "neck": (("y", 2),), "head": (("x", -8), ("y", 5))})
m.at(40, drop=0.0, arms=arms_up(lift_l=-4, lift_r=6, wave_r=18),
     torso={"spine": (("x", -3), ("y", -5)), "chest": (("y", -3),),
            "neck": (("y", -2),), "head": (("x", -8), ("y", -5))})
m.at(47, drop=0.012, arms=arms_up(lift_l=2, wave_l=6),
     torso={"spine": (("x", -3), ("y", 2)), "neck": (("y", 1),), "head": (("x", -6), ("y", 2))})
m.at(57, drop=0.005, arms=ARMS_LOWERING,
     torso={"head": (("x", -2),)})
m.at(64)
m.at(END)

# 上半身は区間の途中のキーで止まらずに通り抜ける。始まりと終わりの立ち姿では止める
m.smooth(3, 60)
m.finish(OUT, "cheer", detail=range(24, 52, 2), detail_views=("Front",))
