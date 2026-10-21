"""VRMA を手で組むための共通部品（Blender の中で動かす）。

ポーズは「T ポーズからの回転」をアーマチュアの軸（X = モデルの左、Y = 後ろ、Z = 上）で書く。
子の回転は親の回転の上に乗る（VRM の正規化ポーズと同じ考え方）。ボーンごとのロールの違いは
ここで吸収するので、モーションの定義側はボーンの向きを気にしなくてよい。
"""
import json
import math
import os
import struct

import bpy
import numpy as np
from mathutils import Matrix, Quaternion, Vector

# 同梱モデル。モーションはこのモデルの骨格で組む
VRM = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Assets", "StreamingAssets", "vita.vrm")
FPS = 30

FINGER_WORDS = ("Thumb", "Index", "Middle", "Ring", "Little")
LEFT_RIGHT = (("left", "right"), ("Left", "Right"))


# ---------------------------------------------------------------- 回転の組み立て

def rot(*steps):
    """("x", 度) の並びを、先に書いたものから順にアーマチュアの軸で回した回転にする。"""
    q = Quaternion()
    for axis, deg in steps:
        v = {"x": (1, 0, 0), "y": (0, 1, 0), "z": (0, 0, 1)}[axis]
        q = Quaternion(v, math.radians(deg)) @ q
    return q


def mirror_steps(steps):
    """左右反転（YZ 平面で鏡映）。X まわりはそのまま、Y・Z まわりは符号が反転する。"""
    return tuple((a, d if a == "x" else -d) for a, d in steps)


def sym(left_pose):
    """左側だけ書いたポーズを、右側にも鏡映して足す。値は rot() に渡す steps のタプル。"""
    out = {}
    for name, steps in left_pose.items():
        out[name] = steps
        if name.startswith("left"):
            out["right" + name[4:]] = mirror_steps(steps)
    return out


def _frame_map(v0, w0, v1, w1):
    """v0→v1 にぴったり、w0→w1 をできるだけ合わせる回転。"""
    def frame(v, w):
        x = v.normalized()
        y = (w - x * w.dot(x)).normalized()
        return Matrix((x, y, x.cross(y))).transposed()

    return (frame(v1, w1) @ frame(v0, w0).transposed()).to_quaternion()


# 指の軽い丸め。アプリが指を持たない VRMA に補う角度と同じ（FingerPose.RelaxedCurl）
FINGER_CURL = {"Proximal": 18, "Intermediate": 22, "Distal": 15}


def fingers(curl=1.0, spread=0.0, thumb=None):
    """両手の指。curl は丸めの倍率（0 で伸ばす）、spread は指を広げる角度（度）。

    thumb: 親指の付け根（Metacarpal）の steps（左手）。None なら立ち姿のまま。
    """
    left = {}
    for i, name in enumerate(("Index", "Middle", "Ring", "Little")):
        fan = (-1.5 + i) * spread / 1.5  # 人差し指を前へ、小指を後ろへ
        for seg, deg in FINGER_CURL.items():
            steps = (("y", deg * curl),)
            if seg == "Proximal" and fan:
                steps = (("z", fan),) + steps
            left[f"left{name}{seg}"] = steps
    # 親指も毎回キーにする。曲げたキーにしか値が無いと、その値が前後のフレームへそのまま伸びる
    left["leftThumbMetacarpal"] = thumb if thumb is not None else ()
    left["leftThumbProximal"] = ()
    left["leftThumbDistal"] = ()
    return sym(left)


# ---------------------------------------------------------------- 読み込み

class Rig:
    def __init__(self, vrm=VRM):
        bpy.ops.wm.read_homefile(use_empty=True)
        bpy.ops.import_scene.vrm(filepath=vrm)
        self.arm = next(o for o in bpy.context.scene.objects if o.type == "ARMATURE")
        hb = self.arm.data.vrm_addon_extension.vrm1.humanoid.human_bones
        self.bone = {
            n.value: b.node.bone_name
            for n, b in hb.human_bone_name_to_human_bone().items()
            if b.node.bone_name
        }
        self.rest_q = {
            h: self.arm.data.bones[b].matrix_local.to_quaternion() for h, b in self.bone.items()
        }
        self._prev = {}
        for pb in self.arm.pose.bones:
            pb.rotation_mode = "QUATERNION"
        scene = bpy.context.scene
        scene.render.fps = FPS
        scene.render.fps_base = 1.0
        self.arm.animation_data_create()

        # 脚の長さ（股関節→膝→足首）。しゃがみの IK に使う
        def head(h):
            return self.arm.data.bones[self.bone[h]].head_local

        self.thigh = (head("leftLowerLeg") - head("leftUpperLeg")).length
        self.shin = (head("leftFoot") - head("leftLowerLeg")).length
        self.leg0 = (head("leftFoot") - head("leftUpperLeg")).length

    # ------------------------------------------------------------ FK / IK

    def _hierarchy(self):
        """humanBone の親（間に humanBone でないボーンがあれば飛ばす）とレストの位置。"""
        if hasattr(self, "_parent"):
            return
        names = {b: h for h, b in self.bone.items()}
        self._parent = {}
        for h, b in self.bone.items():
            p = self.arm.data.bones[b].parent
            while p is not None and p.name not in names:
                p = p.parent
            self._parent[h] = names[p.name] if p is not None else None
        mw = self.arm.matrix_world
        self._rest_pos = {h: mw @ self.arm.data.bones[b].head_local for h, b in self.bone.items()}
        order, seen = [], set()

        def visit(h):
            if h in seen:
                return
            if self._parent[h]:
                visit(self._parent[h])
            seen.add(h)
            order.append(h)

        for h in self.bone:
            visit(h)
        self._order = order

    def fk(self, pose_q, hips_offset):
        """正規化ポーズ（humanBone → ローカル回転）から、各ボーンの向き W と根元の位置 P を出す。"""
        self._hierarchy()
        W, P = {}, {}
        for h in self._order:
            q = pose_q.get(h, Quaternion())
            par = self._parent[h]
            if par is None:
                W[h] = q
                P[h] = self._rest_pos[h] + Vector(hips_offset)
            else:
                W[h] = W[par] @ q
                P[h] = P[par] + W[par] @ (self._rest_pos[h] - self._rest_pos[par])
        return W, P

    def solve_limb(self, pose_q, W, P, root, mid, end, target, pole, flex, end_world=None, twist=0.0):
        """root→mid→end の2関節を、end の根元が target に来るよう解いて pose_q に書く。

        pole: 関節（肘・膝）を向けたい向き。flex: レストで下の骨が曲がる向き（肘は前、膝は後ろ）。
        end_world: end の向き（ワールド）。None なら下の骨にそのまま付いていく。
        twist: 下の骨を自分の軸まわりにひねる角度（度。前腕の回内・回外）。
        """
        r0, m0, e0 = self._rest_pos[root], self._rest_pos[mid], self._rest_pos[end]
        lu, ll = (m0 - r0).length, (e0 - m0).length
        u0, l0 = (m0 - r0).normalized(), (e0 - m0).normalized()
        start = P[root]
        d_vec = Vector(target) - start
        d = min(max(d_vec.length, abs(lu - ll) + 1e-4), lu + ll - 1e-4)
        u = d_vec.normalized()
        a = (lu * lu - ll * ll + d * d) / (2 * d)
        h = math.sqrt(max(lu * lu - a * a, 0.0))
        pole = Vector(pole)
        perp = (pole - u * pole.dot(u)).normalized()
        elbow = start + u * a + perp * h
        tip = start + u * d
        a1 = (elbow - start).normalized()
        b1 = (tip - elbow).normalized()
        f1 = b1 - a1 * b1.dot(a1)
        f1 = f1.normalized() if f1.length > 1e-5 else -perp
        n1 = a1.cross(f1).normalized()
        flex = Vector(flex)
        n0 = u0.cross((flex - u0 * flex.dot(u0)).normalized()).normalized()
        w_upper = _frame_map(u0, n0, a1, n1)
        w_lower = _frame_map(l0, n0, b1, n1)
        if twist:
            w_lower = Quaternion(b1, math.radians(twist)) @ w_lower
        parent = self._parent[root]
        pose_q[root] = W[parent].inverted() @ w_upper
        pose_q[mid] = w_upper.inverted() @ w_lower
        if end_world is not None:
            pose_q[end] = w_lower.inverted() @ end_world

    def _basis(self, human, q_arm):
        r = self.rest_q[human]
        return r.inverted() @ q_arm @ r

    def key(self, frame, pose, hips=(0.0, 0.0, 0.0), lag=None):
        """pose: {humanBone: steps}。hips: hips の移動（メートル、アーマチュアの軸）。

        lag: {humanBone: フレーム数}。そのボーンだけキーを遅らせて打ち、胴に遅れて追従させる。
        """
        lag = lag or {}
        for human, steps in pose.items():
            pb = self.arm.pose.bones[self.bone[human]]
            q = self._basis(human, steps if isinstance(steps, Quaternion) else rot(*steps))
            prev = self._prev.get(human)
            if prev is not None and prev.dot(q) < 0:
                q = -q
            self._prev[human] = q
            pb.rotation_quaternion = q
            pb.keyframe_insert("rotation_quaternion", frame=frame + lag.get(human, 0))
        pb = self.arm.pose.bones[self.bone["hips"]]
        r = self.rest_q["hips"]
        pb.location = r.inverted() @ Vector(hips)
        pb.keyframe_insert("location", frame=frame)

    def legs(self, drop, foot_extra=0.0):
        """hips を drop だけ下げても足裏が床に残るよう、太もも・すね・足首の角度を返す。

        drop が負（跳んでいる）のときは脚を伸ばしたまま。foot_extra はつま先を下げる角度。
        """
        d = max(drop, 0.0)
        t, s = self.thigh, self.shin

        def angles(dist):
            a = math.acos(max(-1, min(1, (t * t + dist * dist - s * s) / (2 * t * dist))))
            b = math.acos(max(-1, min(1, (t * t + s * s - dist * dist) / (2 * t * s))))
            return a, b

        a0, b0 = angles(self.leg0)
        a1, b1 = angles(self.leg0 - d)
        hip = math.degrees(a1 - a0)  # 太ももを前へ
        knee = math.degrees(b0 - b1)  # 膝を曲げる
        ankle = knee - hip  # 足の裏を床と平行に戻す
        left = {
            "leftUpperLeg": (("x", -hip),),
            "leftLowerLeg": (("x", knee),),
            "leftFoot": (("x", -ankle + foot_extra),),
        }
        return sym(left)

    def _fcurves(self):
        ad = self.arm.animation_data
        action = ad.action
        if getattr(action, "fcurves", None):
            return action.fcurves
        from bpy_extras import anim_utils

        return anim_utils.action_get_channelbag_for_slot(action, ad.action_slot).fcurves

    def smooth(self, humans, start, end):
        """start〜end の間のキーで止まらず、速度を保ったまま通り抜けるようにする。

        既定の補間はキーごとに減速して止まるので、キーの多い区間がカクついて見える。
        脚と hips は足が床から浮き沈みしないよう、既定（行き過ぎない補間）のまま残す。
        """
        names = {f'"{self.bone[h]}"' for h in humans}
        for fc in self._fcurves():
            if not any(n in fc.data_path for n in names):
                continue
            for kp in fc.keyframe_points:
                if start < kp.co.x < end:
                    kp.handle_left_type = "AUTO"
                    kp.handle_right_type = "AUTO"
            fc.update()

    # ------------------------------------------------------------ 書き出し

    def export(self, path, frame_end, keep_fingers=False):
        scene = bpy.context.scene
        scene.frame_start = 0
        scene.frame_end = frame_end
        bpy.context.view_layer.objects.active = self.arm
        res = bpy.ops.export_scene.vrma(filepath=path, armature_object_name=self.arm.name)
        assert res == {"FINISHED"}, res
        strip_bones(path, keep_fingers)


# ---------------------------------------------------------------- モーションの組み立て

# 待機の立ち姿。始まりと終わりをここに揃えて、待機とのクロスフェードでつなげる
NEUTRAL = {
    "hips": (),
    "spine": (),
    "chest": (),
    "upperChest": (),
    "neck": (),
    "head": (),
    **sym({
        "leftShoulder": (),
        "leftUpperArm": (("y", 72), ("x", -4)),
        "leftLowerArm": (("z", -10),),
        "leftHand": (),
        "leftToes": (),
    }),
}

# 頭・首・手首は胴に遅れて追従させる
LAG = {"neck": 1, "head": 2, "leftHand": 2, "rightHand": 2}

UPPER = ["spine", "chest", "upperChest", "neck", "head",
         "leftShoulder", "rightShoulder", "leftUpperArm", "rightUpperArm",
         "leftLowerArm", "rightLowerArm", "leftHand", "rightHand"]


class Motion:
    """キーを打つ口。at() に部位ごとの辞書を渡すと、立ち姿に重ねてキーにする。

    with_fingers: 指のキーも打つ（手を開くなど）。打たないときは書き出しで指を外し、アプリの
    既定の丸めに任せる。
    """

    def __init__(self, end, with_fingers=False):
        self.rig = Rig()
        self.end = end
        self.with_fingers = with_fingers

    def at(self, frame, drop=0.0, foot=0.0, lag=LAG, shift=(0.0, 0.0), feet=None, hands=None, **parts):
        """drop: hips を下げる量（m、負で跳ぶ）。shift: hips の横・前後の移動（m）。

        feet: {"left"/"right": {"lift": (x, y, z), "pitch": 度, "yaw": 度}}。足首をレストの位置から
              lift だけ動かし、足の向きを pitch（正でつま先が下がる）・yaw で決める。feet か shift
              か hips の回転があるときは両脚を IK で解く（書かなかった足は床に置いたまま）。
        hands: {"left"/"right": {"target": 位置 or (W, P) -> 位置, "pole": 肘を向ける向き,
               "hand": 手の向き（ワールド）, "twist": 前腕のひねり}}。手首を target に置く。
        """
        p = dict(NEUTRAL)
        if self.with_fingers:
            p.update(fingers())
        for part in parts.values():
            p.update(part)
        hips_rot = p.get("hips", ())
        use_ik = feet is not None or tuple(shift) != (0.0, 0.0) or bool(hips_rot)
        if not use_ik:
            for k, val in self.rig.legs(drop, foot).items():
                p.setdefault(k, val)
        hips_off = (shift[0], shift[1], -drop)
        if use_ik or hands:
            q = {h: (val if isinstance(val, Quaternion) else rot(*val)) for h, val in p.items()}
            rig = self.rig
            W, P = rig.fk(q, hips_off)
            if use_ik:
                for side in ("left", "right"):
                    spec = (feet or {}).get(side, {})
                    target = rig._rest_pos[f"{side}Foot"] + Vector(spec.get("lift", (0, 0, 0)))
                    sole = rot(("x", spec.get("pitch", foot)), ("z", spec.get("yaw", 0.0)))
                    rig.solve_limb(q, W, P, f"{side}UpperLeg", f"{side}LowerLeg", f"{side}Foot",
                                   target, pole=(0, -1, 0), flex=(0, 1, 0), end_world=sole)
                W, P = rig.fk(q, hips_off)
            for side, spec in (hands or {}).items():
                target = spec["target"](W, P) if callable(spec["target"]) else spec["target"]
                hand = spec.get("hand")
                if hand is not None and not isinstance(hand, Quaternion):
                    hand = rot(*hand)
                rig.solve_limb(q, W, P, f"{side}UpperArm", f"{side}LowerArm", f"{side}Hand",
                               target, pole=spec["pole"], flex=(0, -1, 0), end_world=hand,
                               twist=spec.get("twist", 0.0))
                # 先に解いた腕の位置を、後の腕の目標に使えるようにする（書いた順に解く）
                W, P = rig.fk(q, hips_off)
            p = q
        if frame in (0, self.end):
            lag = None
        self.rig.key(frame, p, hips_off, lag=lag)

    def smooth(self, start, end, humans=UPPER):
        self.rig.smooth(humans, start, end)

    def finish(self, out, name, detail=None, detail_views=("Side", "Front"), detail_tile=(260, 360)):
        os.makedirs(out, exist_ok=True)
        self.rig.export(os.path.join(out, f"{name}.vrma"), self.end, keep_fingers=self.with_fingers)
        frames = list(range(0, self.end + 1, 6))
        if frames[-1] != self.end:
            frames.append(self.end)
        sheet(out, frames)
        if detail:
            sheet(out, list(detail), name="detail", views=detail_views, tile=detail_tile)
        video(out, self.end)
        print("DONE", out)


# ---------------------------------------------------------------- VRMA の後処理

def _read_glb(path):
    raw = open(path, "rb").read()
    _, _, total = struct.unpack_from("<III", raw, 0)
    off, chunks = 12, []
    while off < total:
        length, kind = struct.unpack_from("<II", raw, off)
        chunks.append([kind, raw[off + 8 : off + 8 + length]])
        off += 8 + length
    return chunks


def _write_glb(path, chunks):
    body = b""
    for kind, data in chunks:
        pad = (b" " if kind == 0x4E4F534A else b"\0") * ((4 - len(data) % 4) % 4)
        data = data + pad
        body += struct.pack("<II", len(data), kind) + data
    open(path, "wb").write(struct.pack("<III", 0x46546C67, 2, 12 + len(body)) + body)


def strip_bones(path, keep_fingers=False):
    """目（と、指を動かさないなら指）の humanBones とそのチャンネルを外す。

    アプリは指が無いときだけ既定の丸めで補い、目は視線の制御が動かす。
    """
    chunks = _read_glb(path)
    js = json.loads(chunks[0][1])
    bones = js["extensions"]["VRMC_vrm_animation"]["humanoid"]["humanBones"]
    words = ("Eye", "jaw") + (() if keep_fingers else FINGER_WORDS)

    def drop(k):
        return any(w in k for w in words)

    finger_nodes = {v["node"] for k, v in bones.items() if drop(k)}
    for k in [k for k in bones if drop(k)]:
        del bones[k]
    for anim in js.get("animations", []):
        keep = [c for c in anim["channels"] if c["target"].get("node") not in finger_nodes]
        used = sorted({c["sampler"] for c in keep})
        remap = {old: new for new, old in enumerate(used)}
        anim["samplers"] = [anim["samplers"][i] for i in used]
        for c in keep:
            c["sampler"] = remap[c["sampler"]]
        anim["channels"] = keep
    chunks[0][1] = json.dumps(js, separators=(",", ":")).encode()
    _write_glb(path, chunks)


# ---------------------------------------------------------------- プレビュー

def _setup_stage():
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    shading = scene.display.shading
    shading.light = "STUDIO"
    shading.color_type = "TEXTURE"
    shading.show_shadows = True
    shading.show_cavity = False
    scene.world = scene.world or bpy.data.worlds.new("World")
    scene.world.color = (0.82, 0.84, 0.88)
    scene.render.film_transparent = False
    bpy.ops.mesh.primitive_plane_add(size=3, location=(0, 0, 0))
    floor = bpy.context.active_object
    mat = bpy.data.materials.new("Floor")
    mat.diffuse_color = (0.55, 0.57, 0.6, 1)
    floor.data.materials.append(mat)
    scene.display.shading.light = "STUDIO"


def _camera(name, location, rotation_deg, ortho=None):
    cam = bpy.data.cameras.new(name)
    if ortho:
        cam.type = "ORTHO"
        cam.ortho_scale = ortho
    obj = bpy.data.objects.new(name, cam)
    bpy.context.scene.collection.objects.link(obj)
    obj.location = location
    obj.rotation_euler = [math.radians(d) for d in rotation_deg]
    return obj


def _render_still(camera, frame, path, w, h):
    scene = bpy.context.scene
    scene.camera = camera
    scene.frame_set(frame)
    scene.render.resolution_x = w
    scene.render.resolution_y = h
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)


def _load_rgba(path):
    img = bpy.data.images.load(path)
    w, h = img.size
    px = np.array(img.pixels[:], dtype=np.float32).reshape(h, w, 4)
    bpy.data.images.remove(img)
    return px


def _save_rgba(px, path):
    h, w, _ = px.shape
    img = bpy.data.images.new("sheet", w, h, alpha=True)
    img.pixels = px.ravel().tolist()
    img.filepath_raw = path
    img.file_format = "PNG"
    img.save()
    bpy.data.images.remove(img)


_STAGE = {}


def _stage():
    if _STAGE:
        return _STAGE
    _setup_stage()
    # 少し見下ろして床を写す（足が浮いているか・沈んでいるかを見るため）
    _STAGE["Front"] = _camera("Front", (0, -6, 2.0), (80, 0, 0), ortho=2.4)
    _STAGE["Side"] = _camera("Side", (6, 0, 2.0), (80, 0, 90), ortho=2.4)
    # アプリのカメラに近い、腰の高さから見上げる上半身（顔が見えるかを見るため）
    face = _camera("Face", (0, -2.4, 1.0), (101, 0, 0))
    face.data.lens = 50
    _STAGE["Face"] = face
    # 手元の寄り（拳・指の向きを見るため）。正面と、モデルの右斜め前から
    hands = _camera("Hands", (0, -1.6, 0.85), (90, 0, 0))
    hands.data.lens = 40
    _STAGE["Hands"] = hands
    hands_r = _camera("HandsR", (-0.75, -0.8, 0.82), (90, 0, -35))
    hands_r.data.lens = 50
    _STAGE["HandsR"] = hands_r
    # お腹の前で組んだ手の寄り（指が重なっていないかを見るため）
    belly = _camera("Belly", (0, -0.75, 0.9), (90, 0, 0))
    belly.data.lens = 50
    _STAGE["Belly"] = belly
    back = _camera("Back", (0, 1.8, 1.0), (90, 0, 180))
    back.data.lens = 40
    _STAGE["Back"] = back
    # 上半身の寄り。正面と、モデルの右斜め前から
    upper = _camera("Upper", (0, -1.5, 1.25), (90, 0, 0))
    upper.data.lens = 50
    _STAGE["Upper"] = upper
    upper_r = _camera("UpperR", (-0.95, -1.15, 1.25), (90, 0, -40))
    upper_r.data.lens = 50
    _STAGE["UpperR"] = upper_r
    return _STAGE


def sheet(out_dir, frames, name="sheet", per_row=7, tile=(260, 360), views=("Front", "Side")):
    """指定したコマを正面・横から描いて1枚に並べる。左上から右へ frames の順。"""
    os.makedirs(out_dir, exist_ok=True)
    cams = _stage()
    w, h = tile
    rows = []
    for view in views:
        tiles = []
        for f in frames:
            p = os.path.join(out_dir, f"_{view}_{f:03d}.png")
            _render_still(cams[view], f, p, w, h)
            tiles.append(_load_rgba(p))
            os.remove(p)
        blank = np.ones_like(tiles[0])
        for i in range(0, len(tiles), per_row):
            chunk = tiles[i : i + per_row]
            chunk += [blank] * (per_row - len(chunk))
            rows.append(np.concatenate(chunk, axis=1))
    # Blender の pixels は下から上。行を上から並べるため、行の順を逆にして縦に積む
    _save_rgba(np.concatenate(list(reversed(rows)), axis=0), os.path.join(out_dir, f"{name}.png"))
    print(name, "frames:", frames)


def video(out_dir, frame_end, name="preview"):
    """斜め前から見た動画（mp4）。"""
    _stage()
    scene = bpy.context.scene
    cam = _camera("Three", (2.6, -4.2, 1.35), (82, 0, 32))
    cam.data.lens = 50
    scene.camera = cam
    scene.frame_start = 0
    scene.frame_end = frame_end
    scene.render.resolution_x = 540
    scene.render.resolution_y = 720
    settings = scene.render.image_settings
    if hasattr(settings, "media_type"):
        settings.media_type = "VIDEO"
    settings.file_format = "FFMPEG"
    scene.render.ffmpeg.format = "MPEG4"
    scene.render.ffmpeg.codec = "H264"
    scene.render.ffmpeg.constant_rate_factor = "HIGH"
    scene.render.filepath = os.path.join(out_dir, f"{name}.mp4")
    bpy.ops.render.render(animation=True)
