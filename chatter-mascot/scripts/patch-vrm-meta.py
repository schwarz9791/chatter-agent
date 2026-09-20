#!/usr/bin/env python3
"""VRM 1.0（glTF-Binary）の meta を書き換える。

    ./scripts/patch-vrm-meta.py 入力.vrm 出力.vrm \
        --set commercialUsage=corporation \
        --set modification=allowModificationRedistribution

同梱している ``Assets/StreamingAssets/vita.vrm`` を作るのに使った。素材
（VRoid Studio の旧ベータ版サンプル ``AvatarSample_F``）は CC0 だが、VRoid Studio の
VRM1.0 エクスポートに CC0 プリセットが無いため ``commercialUsage`` と ``modification``
に変換時の既定値が入っていて、素材のライセンスと食い違う。それを直すためのもの。

★ **JSON 全体を再シリアライズしないこと。** 対象キーのバイト列だけを差し替える。
  ``json.dumps`` で書き直すと、同じ2フィールドの変更でも浮動小数点の表記などが
  変わって -24 バイトずれた（実測）。「メタ以外は一切変えていない」と NOTICE に
  書く以上、それを機械的に示せる形でなければ意味がない。

★ **BIN チャンクには触らない。** ジオメトリとテクスチャがバイト単位で同一であることを
  終わりに検証している。UniVRM で再インポート→再エクスポートする方法だと
  ``UrpVrm10MaterialExporter`` を通ってマテリアルとテクスチャが変換されるので採らなかった。
"""

import argparse
import json
import struct
import sys

GLB_MAGIC = b"glTF"
JSON_CHUNK = b"JSON"
# glTF-Binary の JSON チャンクはスペース（0x20）で 4 バイト境界まで詰める
JSON_PAD = b" "


def split_chunks(raw):
    """GLB を (JSON チャンクの本体, それ以降のバイト列) に割る。"""
    if len(raw) < 20:
        raise ValueError("GLB として短すぎます")
    magic, version, total = struct.unpack_from("<4sII", raw, 0)
    if magic != GLB_MAGIC:
        raise ValueError(f"glTF-Binary ではありません: magic={magic!r}")
    if version != 2:
        raise ValueError(f"glTF 2.0 ではありません: version={version}")
    if total != len(raw):
        raise ValueError(f"ヘッダの総長 {total} がファイル長 {len(raw)} と違います")

    length, kind = struct.unpack_from("<I4s", raw, 12)
    if kind != JSON_CHUNK:
        raise ValueError(f"先頭チャンクが JSON ではありません: {kind!r}")
    return raw[20:20 + length], raw[20 + length:]


def replace_string_value(body, key, before, after):
    """``"key":"before"`` を ``"key":"after"`` に差し替える。

    ★ 一意でなければ失敗させること。同じ値が別のキーにも入っている VRM は
      実在する（``avatarPermission`` と ``commercialUsage`` など）ので、
      キーごと照合したうえで出現数まで見る。
    """
    old = f'"{key}":"{before}"'.encode()
    new = f'"{key}":"{after}"'.encode()
    found = body.count(old)
    if found == 0:
        spaced = f'"{key}": "{before}"'.encode()
        if body.count(spaced) > 0:
            raise ValueError(
                f"{key} が空白入りの書式で見つかりました。このスクリプトは"
                "コンパクト形式（VRoid Studio の出力）だけを想定しています"
            )
        raise ValueError(f"{key} に {before!r} が見つかりません")
    if found > 1:
        raise ValueError(f"{key} の {before!r} が {found} 箇所あります（一意ではない）")
    return body.replace(old, new)


def build(json_body, tail):
    """JSON チャンクを詰め直し、チャンク長とファイル総長を書き直す。"""
    padded = json_body.rstrip(JSON_PAD)
    padded += JSON_PAD * (-len(padded) % 4)

    out = bytearray()
    out += GLB_MAGIC + struct.pack("<II", 2, 0)          # 総長はあとで埋める
    out += struct.pack("<I", len(padded)) + JSON_CHUNK + padded
    out += tail
    struct.pack_into("<I", out, 8, len(out))
    return bytes(out)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("source", help="入力の .vrm")
    parser.add_argument("destination", help="出力の .vrm")
    parser.add_argument("--set", dest="assignments", action="append", default=[],
                        metavar="KEY=VALUE", required=True,
                        help="VRMC_vrm.meta の文字列フィールドを差し替える（複数可）")
    args = parser.parse_args(argv)

    raw = open(args.source, "rb").read()
    json_body, tail = split_chunks(raw)
    meta = json.loads(json_body.decode("utf-8"))["extensions"]["VRMC_vrm"]["meta"]

    patched = json_body
    for assignment in args.assignments:
        if "=" not in assignment:
            parser.error(f"KEY=VALUE の形で指定してください: {assignment}")
        key, value = assignment.split("=", 1)
        if key not in meta:
            parser.error(f"VRMC_vrm.meta に {key} がありません")
        if not isinstance(meta[key], str):
            parser.error(f"{key} は文字列ではありません（このスクリプトは文字列だけ扱う）")
        print(f"  {key}: {meta[key]} -> {value}")
        patched = replace_string_value(patched, key, meta[key], value)

    result = build(patched, tail)

    # --- 検証。ここを通らないものは書かない ---
    new_json, new_tail = split_chunks(result)
    if new_tail != tail:
        raise SystemExit("BIN チャンクが変わっています")
    after = json.loads(new_json.decode("utf-8"))
    before = json.loads(json_body.decode("utf-8"))
    for assignment in args.assignments:
        key, value = assignment.split("=", 1)
        before["extensions"]["VRMC_vrm"]["meta"][key] = value
    if before != after:
        raise SystemExit("meta 以外にも差分が出ています")

    open(args.destination, "wb").write(result)
    print(f"  BIN チャンク {len(tail) - 8} バイトはバイト単位で同一")
    print(f"  {len(raw)} -> {len(result)} バイト（{len(result) - len(raw):+d}）")
    print(f"できました: {args.destination}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
