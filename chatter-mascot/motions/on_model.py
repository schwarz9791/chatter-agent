"""書き出した VRMA を別のモデルに当てて描く（アプリでの見え方に近づけるため）。

blender -b --python-exit-code 1 --python on_model.py -- <model.vrm> <motion.vrma> <出力ディレクトリ> <views> <frames>
views: カンマ区切りのカメラ名（vrmalib の _stage）。frames: カンマ区切りのフレーム番号
読み込んだアクションは 1 フレーム目から始まるので、書き出したときのフレームに 1 を足して指す
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bpy  # noqa: E402

import vrmalib as v  # noqa: E402

model, vrma, out, views, frames = sys.argv[sys.argv.index("--") + 1:]
rig = v.Rig(model)
res = bpy.ops.import_scene.vrma(filepath=vrma, armature_object_name=rig.arm.name)
assert res == {"FINISHED"}, res
action = rig.arm.animation_data.action
print("frame range:", tuple(action.frame_range))
frames = [int(f) for f in frames.split(",")]
v.sheet(out, frames, name="on_model", views=tuple(views.split(",")), tile=(420, 480), per_row=min(7, len(frames)))
print("DONE", out)
