#!/usr/bin/env python3
"""Generate the 16x16 RGBA8 tiles of the six-face demo texture pack.

Python 3 standard library only. The PNG writer, the twelve frozen base tiles and the
Godot import-parameter block are reused from tools/gen_default_pack.py by import --
nothing is copied, and this tool never writes under texturepacks/default/.

    python tools/gen_six_face_demo.py            # (re)write the PNGs + pack.json
    python tools/gen_six_face_demo.py --verify   # decode and check the contract
    python tools/gen_six_face_demo.py --sheet    # contact sheet under .pi/sixface/
    python tools/gen_six_face_demo.py --imports  # the Godot .import companions

Key-space reality (loader v1 as merged): the 12 frozen base keys and the 48
`<block>_<suffix>` face keys share one JSON object, and the loader resolves base keys
first. Three face names collide with base names -- grass_top, grass_bottom, wood_top --
so the pack ships 57 distinct PNGs: 12 base + 45 standalone overrides. The three shared
base tiles are generated with their face's tone step and motif, which is what those block
faces render with, so all 48 block+face cells still look distinct (the loader prints
45/48 overrides; a 48/48 count is impossible without a src/ naming change).
"""

import argparse
import hashlib
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gen_default_pack as g  # noqa: E402  (stdlib-only sibling tool)

ROOT = g.ROOT
PACK_DIR = os.path.join(ROOT, "texturepacks", "six-face-demo")
TILES_DIR = os.path.join(PACK_DIR, "tiles")
PACK_JSON = os.path.join(PACK_DIR, "pack.json")
SHEET_PATH = os.path.join(ROOT, ".pi", "sixface", "contact_sheet.png")
SIZE = g.SIZE

BLOCKS = ["stone", "dirt", "grass", "sand", "wood", "plank", "leaves", "bedrock"]
SUFFIXES = ["posx", "negx", "top", "bottom", "posz", "negz"]
FACE_KEYS = ["%s_%s" % (b, s) for b in BLOCKS for s in SUFFIXES]
# Face names shadowed by a frozen base key (loader: Array.IndexOf(Keys, k) wins).
COLLIDING_BASE = {"grass_top": ("grass", "top"),
                  "grass_bottom": ("grass", "bottom"),
                  "wood_top": ("wood", "top")}
assert set(COLLIDING_BASE) == set(FACE_KEYS) & set(g.KEY_ORDER)
OVERRIDE_KEYS = [k for k in FACE_KEYS if k not in g.KEY_ORDER]
TILE_KEYS = list(g.KEY_ORDER) + OVERRIDE_KEYS
assert len(FACE_KEYS) == 48 and len(OVERRIDE_KEYS) == 45 and len(TILE_KEYS) == 57

# The frozen base tile a block+face shows without an override: the look we keep.
_SIDE = {"grass": "grass_side", "wood": "wood_side"}
_TOP = {"grass": "grass_top", "wood": "wood_top"}
_BOTTOM = {"grass": "grass_bottom", "wood": "wood_top"}


def base_key_for(block, suffix):
    if suffix == "top":
        return _TOP.get(block, block)
    if suffix == "bottom":
        return _BOTTOM.get(block, block)
    return _SIDE.get(block, block)


# Six clearly separated tone steps (frozen Face order). Primary per-face signal.
TONE = {"posx": 0.62, "negx": 0.78, "top": 1.30, "bottom": 0.50, "posz": 0.95, "negz": 1.12}


def motif_pixels(suffix):
    """D4-tolerant motif pixel set (mirror/90-degree-rotation recognisable, no glyphs)."""
    if suffix == "posx":          # 1 px border frame
        return {(x, y) for y in range(SIZE) for x in range(SIZE)
                if x in (0, SIZE - 1) or y in (0, SIZE - 1)}
    if suffix == "negx":          # centre cross
        return {(x, y) for y in range(SIZE) for x in range(SIZE)
                if (x in (7, 8) and 3 <= y <= 12) or (y in (7, 8) and 3 <= x <= 12)}
    if suffix == "top":           # centre dot
        return {(x, y) for y in range(SIZE) for x in range(SIZE) if 6 <= x <= 9 and 6 <= y <= 9}
    if suffix == "bottom":        # 4-dot grid (2x2 dots on a 4-flip-symmetric lattice)
        return {(x, y) for y in range(SIZE) for x in range(SIZE)
                if x in (3, 4, 11, 12) and y in (3, 4, 11, 12)}
    if suffix == "posz":          # 8x8 quadrant checker
        return {(x, y) for y in range(SIZE) for x in range(SIZE) if (x // 8 + y // 8) % 2 == 1}
    if suffix == "negz":          # corner squares
        return {(x, y) for y in range(SIZE) for x in range(SIZE)
                if (x < 3 or x > 12) and (y < 3 or y > 12)}
    raise ValueError(suffix)


def tile_for(block, suffix):
    """Base tile RGB * tone step, motif brighter on dark tones and darker on light ones."""
    src = g.TILES[base_key_for(block, suffix)]()
    tone = TONE[suffix]
    motif = motif_pixels(suffix)
    mf = 1.35 if tone < 1.0 else 0.68
    out = []
    for y in range(SIZE):
        row = []
        for x in range(SIZE):
            r, gr, b, a = src[y][x]
            r, gr, b = g.mul((r, gr, b), tone)
            if (x, y) in motif:
                r, gr, b = g.mul((r, gr, b), mf)
            row.append((r, gr, b, a))  # alpha of the base tile is preserved untouched
        out.append(row)
    return out


def tile_path(key):
    return os.path.join(TILES_DIR, key + ".png")


def manifest():
    return {"version": 1, "name": "Six-Face Demo", "tile_size": SIZE,
            "tiles": {k: "tiles/%s.png" % k for k in TILE_KEYS}}


# ---------------------------------------------------------------- modes

def do_generate():
    os.makedirs(TILES_DIR, exist_ok=True)
    for key in g.KEY_ORDER:
        block_suffix = COLLIDING_BASE.get(key)
        rows = tile_for(*block_suffix) if block_suffix else g.TILES[key]()
        g.write_png(tile_path(key), rows)
    for block in BLOCKS:
        for suffix in SUFFIXES:
            key = block + "_" + suffix
            if key in COLLIDING_BASE:
                continue  # already written as the treated base tile above
            g.write_png(tile_path(key), tile_for(block, suffix))
    with open(PACK_JSON, "w", newline="\n") as fh:
        fh.write(json.dumps(manifest(), indent=2) + "\n")
    print("wrote %d PNGs (12 base + 45 overrides) + pack.json under texturepacks/six-face-demo/"
          % len(TILE_KEYS))
    print("note: grass_top / grass_bottom / wood_top are shadowed base keys -> loader sees 45/48")


def do_verify():
    ok = True

    def fail(msg):
        nonlocal ok
        ok = False
        print("FAIL " + msg)

    have = sorted(f for f in os.listdir(TILES_DIR) if f.endswith(".png"))
    want = sorted("%s.png" % k for k in TILE_KEYS)
    if have != want:
        fail("tile set is %d file(s), expected 57 (missing=%s extra=%s)"
             % (len(have), sorted(set(want) - set(have)), sorted(set(have) - set(want))))

    rows = {}
    for key in TILE_KEYS:
        path = tile_path(key)
        if not os.path.isfile(path):
            fail("%s: file missing" % key)
            continue
        img = g.read_png(path)
        rows[key] = img["rows"]
        if (img["w"], img["h"], img["depth"], img["ctype"], img["interlace"]) != (16, 16, 8, 6, 0):
            fail("%s: not 16x16 RGBA8 non-interlaced (got %dx%d depth=%d type=%d)"
                 % (key, img["w"], img["h"], img["depth"], img["ctype"]))
        alphas = {p[3] for row in img["rows"] for p in row}
        if key.startswith("leaves"):
            if not alphas <= {0, 255}:
                fail("%s: partial alpha values %s" % (key, sorted(alphas - {0, 255})))
            if 0 not in alphas:
                fail("%s: alpha-0 holes were filled" % key)
        elif alphas != {255}:
            fail("%s: not fully opaque (alpha values %s)" % (key, sorted(alphas)))
        if key in g.KEY_ORDER:
            ref = g.read_png(os.path.join(g.TILES_DIR, key + ".png"))["rows"]
            if key in COLLIDING_BASE:
                if img["rows"] == ref:
                    fail("%s: shared base/face tile still equals the default base art" % key)
            elif img["rows"] != ref:
                fail("%s: base tile drifted from texturepacks/default" % key)

    # All 48 block+face cells must exist (as override file or treated base file) and be
    # pairwise distinct inside each block.
    for block in BLOCKS:
        seen = {}
        for suffix in SUFFIXES:
            key = block + "_" + suffix
            if key not in rows:
                fail("%s: cell has no tile" % key)
                continue
            if key != base_key_for(block, suffix) and rows[key] == rows.get(base_key_for(block, suffix)):
                fail("%s: identical to the default base tile it replaces" % key)
            for prev in seen:
                if rows[key] == rows[prev]:
                    fail("%s: identical to %s" % (key, prev))
            seen[key] = key

    try:
        with open(PACK_JSON) as fh:
            man = json.load(fh)
        if man.get("version") != 1:
            fail("pack.json: version is not 1")
        if man.get("tile_size") != 16:
            fail("pack.json: tile_size is not 16")
        if set(man.get("tiles", {})) != set(TILE_KEYS):
            fail("pack.json: tile keys do not match the 12 base + 45 override contract")
        for k, rel in man.get("tiles", {}).items():
            if not os.path.isfile(os.path.join(PACK_DIR, rel)):
                fail("pack.json: %s -> %s does not exist" % (k, rel))
    except Exception as exc:
        fail("pack.json: %s" % exc)

    print("verify: %s (%d files; 48/48 block+face cells distinct, 45/48 addressable overrides)"
          % ("PASS" if ok else "FAIL", len(TILE_KEYS)))
    return 0 if ok else 1


def do_sheet(cols=8, cell=64):
    keys = TILE_KEYS
    rows_n = (len(keys) + cols - 1) // cols
    flat = [[(24, 24, 28, 255)] * (cols * cell) for _ in range(rows_n * cell)]
    for i, key in enumerate(keys):
        src = g.read_png(tile_path(key))["rows"]
        cx, cy = (i % cols) * cell, (i // cols) * cell
        for py in range(cell):
            for px in range(cell):
                p = src[(py * SIZE) // cell][(px * SIZE) // cell]
                flat[cy + py][cx + px] = (p[0], p[1], p[2], 255) if p[3] else (128, 128, 128, 255)
        lum = sum(g._lum(p) for row in src for p in row) / (SIZE * SIZE)
        fg = (0, 0, 0, 255) if lum > 0.45 else (255, 255, 255, 255)
        bg = (255, 255, 255, 255) if lum > 0.45 else (0, 0, 0, 255)
        ox, oy = cx + 4, cy + 4
        for d, dx in ((str(i // 10), 0), (str(i % 10), 8)):
            g._blit_digit(flat, ox + dx + 1, oy + 1, d, 2, bg)
        for d, dx in ((str(i // 10), 0), (str(i % 10), 8)):
            g._blit_digit(flat, ox + dx, oy, d, 2, fg)
    os.makedirs(os.path.dirname(SHEET_PATH), exist_ok=True)
    n = g.write_png(SHEET_PATH, flat)
    print("contact sheet: .pi/sixface/contact_sheet.png (%dx%d, %d bytes) 8 cols"
          % (cols * cell, rows_n * cell, n))
    for i in range(0, len(keys), 6):
        print("  " + "  ".join("%2d %s" % (j, keys[j]) for j in range(i, min(i + 6, len(keys)))))
    return 0


def do_imports():
    """Mirror gen_default_pack.do_imports() for this pack's tiles."""
    for key in TILE_KEYS:
        name = key + ".png"
        res = "res://texturepacks/six-face-demo/tiles/" + name
        dest = "res://.godot/imported/%s-%s.ctex" % (
            name, hashlib.md5(res.encode()).hexdigest())
        text = ('[remap]\n\nimporter="texture"\ntype="CompressedTexture2D"\n'
                'path="%s"\nmetadata={\n"vram_texture": false\n}\n\n'
                '[deps]\n\nsource_file="%s"\ndest_files=["%s"]\n\n%s'
                % (dest, res, dest, g.IMPORT_PARAMS))
        with open(os.path.join(TILES_DIR, name + ".import"), "w", newline="\n") as fh:
            fh.write(text)
    print("wrote %d .import companions under texturepacks/six-face-demo/tiles/" % len(TILE_KEYS))
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--verify", action="store_true")
    ap.add_argument("--sheet", action="store_true")
    ap.add_argument("--imports", action="store_true")
    args = ap.parse_args(argv)
    chosen = [(name, flag) for name, flag in (("generate", not any(vars(args).values())),
                                              ("verify", args.verify), ("sheet", args.sheet),
                                              ("imports", args.imports)) if flag]
    rc = 0
    for name, _flag in chosen:
        rc |= {"generate": do_generate, "verify": do_verify,
               "sheet": do_sheet, "imports": do_imports}[name]() or 0
    return rc


if __name__ == "__main__":
    sys.exit(main())
