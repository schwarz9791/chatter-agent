"""VRMA（glTF-Binary）の中身を要約する: 長さ・humanBones・チャンネルと、各ボーンの回転の振れ幅。

回転の振れ幅は「そのボーンのレスト（ノードの rotation）からどれだけ離れるか」の最大角（度）。
hips の移動はレスト位置からの最大変位（メートル）。
"""
import json
import math
import struct
import sys


def read_glb(path):
    raw = open(path, "rb").read()
    magic, version, total = struct.unpack_from("<III", raw, 0)
    assert magic == 0x46546C67, "not GLB"
    off = 12
    js, binc = None, None
    while off < total:
        length, kind = struct.unpack_from("<II", raw, off)
        chunk = raw[off + 8 : off + 8 + length]
        if kind == 0x4E4F534A:
            js = json.loads(chunk)
        elif kind == 0x004E4942:
            binc = chunk
        off += 8 + length
    return js, binc


def accessor(js, binc, idx):
    acc = js["accessors"][idx]
    bv = js["bufferViews"][acc["bufferView"]]
    ncomp = {"SCALAR": 1, "VEC3": 3, "VEC4": 4}[acc["type"]]
    start = bv.get("byteOffset", 0) + acc.get("byteOffset", 0)
    vals = struct.unpack_from("<" + "f" * (ncomp * acc["count"]), binc, start)
    return [vals[i : i + ncomp] for i in range(0, len(vals), ncomp)] if ncomp > 1 else list(vals)


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (
        aw * bx + ax * bw + ay * bz - az * by,
        aw * by - ax * bz + ay * bw + az * bx,
        aw * bz + ax * by - ay * bx + az * bw,
        aw * bw - ax * bx - ay * by - az * bz,
    )


def qangle_deg(q):
    w = max(-1.0, min(1.0, abs(q[3])))
    return math.degrees(2 * math.acos(w))


def main(path):
    js, binc = read_glb(path)
    ext = js.get("extensions", {}).get("VRMC_vrm_animation", {})
    bones = ext.get("humanoid", {}).get("humanBones", {})
    node_to_bone = {v["node"]: k for k, v in bones.items()}
    exprs = ext.get("expressions", {})
    n_expr = sum(len(v) for v in exprs.values()) if isinstance(exprs, dict) else 0
    duration = 0.0
    rows = []
    for anim in js.get("animations", []):
        for ch in anim["channels"]:
            s = anim["samplers"][ch["sampler"]]
            times = accessor(js, binc, s["input"])
            duration = max(duration, max(times))
            node = ch["target"].get("node")
            name = node_to_bone.get(node, js["nodes"][node].get("name", f"node{node}"))
            vals = accessor(js, binc, s["output"])
            rest_node = js["nodes"][node]
            if ch["target"]["path"] == "rotation":
                rest = rest_node.get("rotation", [0, 0, 0, 1])
                inv = (-rest[0], -rest[1], -rest[2], rest[3])
                amp = max(qangle_deg(qmul(inv, v)) for v in vals)
                rows.append((name, "rot", amp, len(vals)))
            elif ch["target"]["path"] == "translation":
                rest = rest_node.get("translation", [0, 0, 0])
                amp = max(math.dist(rest, v) for v in vals)
                ys = [v[1] for v in vals]
                rows.append((name, f"pos(y {min(ys):.3f}..{max(ys):.3f})", amp, len(vals)))
    print(f"{path}")
    print(f"  duration={duration:.3f}s humanBones={len(bones)} channels={len(rows)} expressions={n_expr}")
    for name, kind, amp, n in sorted(rows, key=lambda r: -r[2]):
        unit = "m" if kind.startswith("pos") else "deg"
        print(f"    {name:24s} {kind:28s} max={amp:7.3f}{unit} keys={n}")


if __name__ == "__main__":
    for p in sys.argv[1:]:
        main(p)
