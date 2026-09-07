#!/usr/bin/env python3
"""PNG から必須でないチャンクを落とす（画素は無劣化）。

    ./scripts/strip-png-metadata.py Assets/StreamingAssets/trayTemplate.png
    ./scripts/strip-png-metadata.py --check Assets/StreamingAssets/trayTemplate*.png

メニューバーのトレイ画像（``trayTemplate.png`` / ``@2x``）を差し替えるときに使う。
画像編集ツール（Affinity など）は書き出し時に XMP（``iTXt``）と ICC プロファイル
（``iCCP``）を埋め、そこに**実名・作成時刻・オーサリングツール**が入る。#93 で
実際にこれを公開リポジトリと ``.app`` に載せてしまった。

★ **``IDAT`` には触らない。** 残すチャンクは長さ・型・データ・CRC をバイト列のまま
  コピーするので、**画素は無劣化**。展開後のピクセル列が元と一致することは
  ``--check`` ではなく、差し替え後の ``./scripts/test.sh``（``TrayIconTests``）が見る。

★★ **pngquant を使わないこと。** ``--strip`` でメタデータも落ちるので一見よさそうだが、
  **減色してパレット形式（colour type 3）に変える**。``TrayIconTests`` の自前デコーダは
  colour type 6（RGBA）しか読めないので、そのままコミットするとテストが落ちる。
  ここでやりたいのは「小さくする」ことではなく「メタデータだけを落とす」こと。
  → ``docs/mascot.md`` の「トレイ画像を差し替えるとき」

★ アプリアイコン（``Assets/ChatterMascot/Icon/AppIcon.png``）は話が別で、あちらは
  pngquant を通してある。ビルド時のアイコン生成がソース PNG を読むだけで、
  このデコーダの対象ではないため。
"""

import argparse
import struct
import sys

SIGNATURE = b"\x89PNG\r\n\x1a\n"

# ★ IHDR/IEND は PNG の構造上必須、IDAT が画素データ。PLTE/tRNS（パレット）は
#   意図的に残さない —— 残す必要があるならその PNG は colour type 3 で、
#   TrayIconTests のデコーダが読めない
KEEP = (b"IHDR", b"IDAT", b"IEND")


def chunks(raw):
    """(型, チャンク全体のバイト列) を先頭から順に返す。"""
    if raw[:8] != SIGNATURE:
        raise ValueError("PNG ではありません（シグネチャが一致しない）")
    pos = 8
    while pos + 8 <= len(raw):
        length = struct.unpack_from(">I", raw, pos)[0]
        kind = raw[pos + 4 : pos + 8]
        # 長さ(4) + 型(4) + データ + CRC(4) をまとめて持つ。CRC を作り直さないので
        # 残すチャンクは元のまま通る
        yield kind, raw[pos : pos + 12 + length]
        pos += 12 + length
        if kind == b"IEND":
            break


def strip(raw):
    """落としたチャンクの型と、書き換えたバイト列を返す。"""
    kept, dropped = bytearray(SIGNATURE), []
    for kind, chunk in chunks(raw):
        if kind in KEEP:
            kept += chunk
        else:
            dropped.append(kind.decode("ascii", "replace"))
    return dropped, bytes(kept)


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("paths", nargs="+", help="対象の PNG")
    parser.add_argument(
        "--check",
        action="store_true",
        help="書き換えず、余計なチャンクがあれば終了コード 1 で報告する",
    )
    args = parser.parse_args()

    failed = False
    for path in args.paths:
        with open(path, "rb") as f:
            raw = f.read()
        try:
            dropped, out = strip(raw)
        except ValueError as e:
            print(f"{path}: {e}", file=sys.stderr)
            failed = True
            continue

        if not dropped:
            print(f"{path}: 落とすものはありません")
            continue

        if args.check:
            print(f"{path}: 余計なチャンクがあります: {', '.join(dropped)}", file=sys.stderr)
            failed = True
            continue

        with open(path, "wb") as f:
            f.write(out)
        print(f"{path}: {', '.join(dropped)} を落としました（{len(raw)} → {len(out)} B）")

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
