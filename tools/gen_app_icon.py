#!/usr/bin/env python3
"""Generate the Regress app icons -- first pass, drawn from the repo's own tiles.

Concept: a grass-topped block with a trunk + small canopy rising from it.
Why: it is the game's core idiom, and two chunky masses stay readable at 64x64,
unlike a letterform.

Model: a 64x64 logical voxel grid. `paint(size)` renders that one model at any
size natively -- voxel v covers the integer pixel range [v*size//64,
(v+1)*size//64) on both axes, so every voxel edge is an integer pixel and no
resampling (and therefore no anti-aliasing) ever happens:

    assets/icon_1024x1024.png  -> iOS   icons/icon_1024x1024
    assets/icon_192x192.png    -> Android launcher_icons/main_192x192
    assets/icon_432x432.png    -> Android launcher_icons/adaptive_foreground_432x432

Each voxel is one flat square whose colour is the source tile's pixel at
(vx % 16, vy % 16) -- nearest-neighbour, so the noise in the icon is the game's
own tile noise. No anti-aliasing, no gradients, alpha = 255 on every pixel.

Stdlib only. PNG read/write is reused from tools/gen_default_pack.py.

Modes:
    (no args)   write all three PNGs deterministically
    --verify    decode the files on disk and re-check every invariant
    --ascii     print a 64x64 character view of the voxel model
    --sheet     write a nearest-neighbour contact sheet under .pi/icon/art/
"""

import hashlib
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from gen_default_pack import read_png, write_png  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TILES = os.path.join(ROOT, "texturepacks", "default", "tiles")
SHEET = os.path.join(ROOT, ".pi", "icon", "art", "sheet.png")

VOX = 16          # px per voxel in the tile art
GRID = 64         # voxels per side of the model
SIZE = 1024       # iOS size

TARGETS = (
    ("assets/icon_1024x1024.png", 1024),
    ("assets/icon_192x192.png", 192),
    ("assets/icon_432x432.png", 432),
)

USE = ("stone", "grass_top", "grass_side", "dirt", "wood_side", "leaves")
MOTIF_TILES = USE[1:]

# ---------------------------------------------------------------- the model
# All coordinates are voxel coordinates, [x0, y0, x1, y1) exclusive.
# Every region edge is on a 4-voxel (64 px) boundary and the whole motif lives
# in x 12..52 / y 12..52 -- the central 62.5%, inside the 66% safe zone of an
# Android adaptive icon mask. Only the stone field bleeds to the canvas edges.
CANOPY = (16, 12, 48, 28)     # 32 x 16 leaves mass
CANOPY_CUT = 4                # 4x4 voxel squares cut out of the canopy corners
TRUNK = (30, 24, 34, 40)      # 4 voxels wide, centred on the grid centre line (x = 32)
BASE = (12, 40, 52, 52)       # 40 x 12 block
GRASS_TOP_H = 4               # grass_top greens on the top 4 voxels
GRASS_SIDE_H = 4              # grass_side fringe hanging below the greens

MOTIF_BOX = (12, 12, 51, 51)  # expected motif bounding box, inclusive, in voxels

# ASCII glyph per region.
GLYPH = {"stone": ".", "grass_top": "G", "grass_side": "g",
         "dirt": "d", "wood_side": "W", "leaves": "L"}


def load_tiles():
    """Read every tile used. name -> rows of (r, g, b, a)."""
    out = {}
    for name in USE:
        img = read_png(os.path.join(TILES, name + ".png"))
        out[name] = img["rows"]
    return out


def pick(tiles, name, vx, vy):
    """Nearest-neighbour tile lookup: tile pixel (vx % 16, vy % 16)."""
    rows = tiles[name]
    return rows[vy % len(rows)][vx % len(rows[0])]


def _paint(region, colour, tiles, name, box):
    x0, y0, x1, y1 = box
    for vy in range(y0, y1):
        for vx in range(x0, x1):
            px = pick(tiles, name, vx, vy)
            if px[3] == 0:
                continue  # leaves holes: keep whatever is behind, never invent a colour
            region[vy][vx] = name
            colour[vy][vx] = px[:3]


def model():
    """Return (region, colour): two GRID x GRID grids of names and (r, g, b)."""
    tiles = load_tiles()
    region = [["stone"] * GRID for _ in range(GRID)]
    colour = [[pick(tiles, "stone", vx, vy)[:3] for vx in range(GRID)]
              for vy in range(GRID)]

    bx0, by0, bx1, by1 = BASE
    _paint(region, colour, tiles, "grass_top", (bx0, by0, bx1, by0 + GRASS_TOP_H))
    _paint(region, colour, tiles, "grass_side",
           (bx0, by0 + GRASS_TOP_H, bx1, by0 + GRASS_TOP_H + GRASS_SIDE_H))
    _paint(region, colour, tiles, "dirt",
           (bx0, by0 + GRASS_TOP_H + GRASS_SIDE_H, bx1, by1))

    cx0, cy0, cx1, cy1 = CANOPY
    for vy in range(cy0, cy1):
        for vx in range(cx0, cx1):
            if (min(vx - cx0, cx1 - 1 - vx) < CANOPY_CUT
                    and min(vy - cy0, cy1 - 1 - vy) < CANOPY_CUT):
                continue  # cut the 4x4 corner squares
            px = pick(tiles, "leaves", vx, vy)
            if px[3] == 0:
                continue
            region[vy][vx] = "leaves"
            colour[vy][vx] = px[:3]

    _paint(region, colour, tiles, "wood_side", TRUNK)
    return region, colour


def edges(size):
    """Voxel edge -> pixel coordinate. Voxel v spans [edges[v], edges[v + 1])."""
    return [v * size // GRID for v in range(GRID + 1)]


def paint(size):
    """Render the 64x64 model at size x size px, natively (no resampling)."""
    _region, colour = model()
    ed = edges(size)
    rows = []
    for vy in range(GRID):
        row = []
        for vx in range(GRID):
            r, g, b = colour[vy][vx]
            row.extend([(r, g, b, 255)] * (ed[vx + 1] - ed[vx]))
        rows.extend([row] * (ed[vy + 1] - ed[vy]))
    return rows


def tile_palettes():
    tiles = load_tiles()
    return {n: {px[:3] for row in tiles[n] for px in row if px[3] == 255}
            for n in USE}


# ---------------------------------------------------------------- modes

def do_generate():
    for rel, size in TARGETS:
        path = os.path.join(ROOT, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        write_png(path, paint(size))
        data = open(path, "rb").read()
        print("wrote %s (%dx%d, %d bytes)" % (rel, size, size, len(data)))
        print("  sha256 %s" % hashlib.sha256(data).hexdigest())


def do_verify():
    pal = tile_palettes()
    union = set().union(*pal.values())
    motif_pal = set().union(*[pal[n] for n in MOTIF_TILES])
    errs = []
    grids = {}

    def need(cond, msg):
        if not cond:
            errs.append(msg)

    for rel, size in TARGETS:
        path = os.path.join(ROOT, rel)
        tag = os.path.basename(rel)
        img = read_png(path)
        rows = img["rows"]
        ed = edges(size)

        need(img["w"] == size and img["h"] == size,
             "%s: size is %dx%d, expected %dx%d" % (tag, img["w"], img["h"], size, size))
        need(img["depth"] == 8 and img["ctype"] == 6,
             "%s: not 8-bit RGBA (depth=%d ctype=%d)" % (tag, img["depth"], img["ctype"]))

        for y, row in enumerate(rows):
            for x, px in enumerate(row):
                if px[3] != 255:
                    need(False, "%s: alpha %d != 255 at (%d,%d)" % (tag, px[3], x, y))
                    break

        grid = [[rows[(ed[vy] + ed[vy + 1]) // 2][(ed[vx] + ed[vx + 1]) // 2][:3]
                 for vx in range(GRID)] for vy in range(GRID)]
        grids[size] = grid

        distinct = set()
        for vy in range(GRID):
            for vx in range(GRID):
                c = rows[ed[vy]][ed[vx]]
                distinct.add(c[:3])
                if c[:3] not in union:
                    need(False, "%s: colour %r at voxel (%d,%d) is not from any tile"
                         % (tag, c[:3], vx, vy))
                for py in range(ed[vy], ed[vy + 1]):
                    for px in range(ed[vx], ed[vx + 1]):
                        if rows[py][px] != c:
                            need(False, "%s: voxel (%d,%d) is not a flat block (%d,%d is %r)"
                                 % (tag, vx, vy, px, py, rows[py][px]))
                            break
        need(len(distinct) >= 8,
             "%s: only %d distinct colours, need >= 8" % (tag, len(distinct)))

        def vox(vx, vy, g=grid):
            return g[vy][vx]

        def in_tile(name, vx, vy, g=grid):
            return vox(vx, vy, g) in pal[name]

        # --- motif, at the exact designed coordinates ---------------------
        need(in_tile("stone", 4, 4),
             "%s: background (4,4) is not stone: %r" % (tag, vox(4, 4)))

        # canopy: 32 x 16 at x 16..48, y 12..28, 4x4 corners cut
        for vx, vy in ((32, 14), (16, 18), (47, 18), (16, 16), (47, 16), (21, 12)):
            need(in_tile("leaves", vx, vy),
                 "%s: canopy voxel (%d,%d) is not leaves: %r" % (tag, vx, vy, vox(vx, vy)))
        need(in_tile("stone", 32, 11),
             "%s: canopy does not start at y=12: %r" % (tag, vox(32, 11)))
        for vx in (15, 48):
            need(in_tile("stone", vx, 18),
                 "%s: canopy does not end at x=16..48 (vx=%d): %r" % (tag, vx, vox(vx, 18)))
        for vx, vy in ((16, 12), (47, 12), (16, 27), (47, 27)):
            need(in_tile("stone", vx, vy),
                 "%s: canopy corner (%d,%d) is not cut: %r" % (tag, vx, vy, vox(vx, vy)))

        # trunk: exactly 4 voxels wide at x 30..34, from y=24 down to y=39
        for vx in (30, 31, 32, 33):
            need(in_tile("wood_side", vx, 34),
                 "%s: trunk column %d at y=34 is not wood_side: %r" % (tag, vx, vox(vx, 34)))
        for vx in (29, 34):
            need(in_tile("stone", vx, 34),
                 "%s: trunk is not exactly 4 voxels wide at y=34 (vx=%d): %r"
                 % (tag, vx, vox(vx, 34)))
        need(in_tile("leaves", 32, 23),
             "%s: trunk starts too high: %r" % (tag, vox(32, 23)))
        need(in_tile("wood_side", 32, 39),
             "%s: trunk does not reach y=39: %r" % (tag, vox(32, 39)))
        need(in_tile("grass_top", 32, 40),
             "%s: base top (32,40) is not grass_top: %r" % (tag, vox(32, 40)))

        # base block: 40 x 12 at x 12..52, y 40..52
        for vx in (11, 52):
            need(in_tile("stone", vx, 50),
                 "%s: base does not end at x=12..52 (vx=%d): %r" % (tag, vx, vox(vx, 50)))
        for vx in (12, 51):
            need(in_tile("dirt", vx, 50),
                 "%s: base does not start at x=12..52 (vx=%d): %r" % (tag, vx, vox(vx, 50)))
        for vy, name in ((42, "grass_top"), (46, "grass_side"), (50, "dirt")):
            need(in_tile(name, 20, vy),
                 "%s: base band y=%d is not %s: %r" % (tag, vy, name, vox(20, vy)))
        need(in_tile("dirt", 32, 51),
             "%s: base does not reach y=51: %r" % (tag, vox(32, 51)))
        need(in_tile("stone", 32, 52),
             "%s: base does not end at y=52: %r" % (tag, vox(32, 52)))

        # --- motif bounding box, from the pixels alone --------------------
        xs = [vx for vy in range(GRID) for vx in range(GRID) if vox(vx, vy) in motif_pal]
        ys = [vy for vy in range(GRID) for vx in range(GRID) if vox(vx, vy) in motif_pal]
        got = (min(xs), min(ys), max(xs), max(ys))
        need(got == MOTIF_BOX,
             "%s: motif bbox is %r, expected %r" % (tag, got, MOTIF_BOX))

    # same model at every size, not a resample
    for size in (192, 432):
        need(grids[size] == grids[SIZE],
             "icon_%dx%d.png is not the same 64x64 voxel model as icon_%dx%d.png"
             % (size, size, SIZE, SIZE))

    if errs:
        for e in errs[:20]:
            sys.stderr.write("FAIL: %s\n" % e)
        if len(errs) > 20:
            sys.stderr.write("... and %d more\n" % (len(errs) - 20))
        return 1
    print("APP-ICON VERIFY PASS")
    return 0


def do_ascii():
    region, _colour = model()
    for vy in range(GRID):
        print("".join(GLYPH[region[vy][vx]] for vx in range(GRID)))
    print("# legend: %s" % " ".join("%s=%s" % (v, k) for k, v in GLYPH.items()))
    return 0


def do_sheet():
    """Contact sheet: the native 432/192 renders, plain and behind a round mask."""
    tiles = load_tiles()
    tiles["bedrock"] = read_png(os.path.join(TILES, "bedrock.png"))["rows"]
    bg = pick(tiles, "bedrock", 0, 0)[:3]
    gap = 16
    slots = [(432, False), (432, True), (192, False), (192, True)]
    height = 432
    width = sum(w for w, _ in slots) + gap * (len(slots) - 1)
    rows = [[(bg[0], bg[1], bg[2], 255)] * width for _ in range(height)]

    x = 0
    for size, masked in slots:
        art = paint(size)
        rad = size * 61 // 200  # Android adaptive safe circle: 61% of the canvas
        for py in range(size):
            for px in range(size):
                if masked:
                    dx, dy = px - size // 2, py - size // 2
                    if dx * dx + dy * dy > rad * rad:
                        continue
                rows[py][x + px] = art[py][px]
        x += size + gap

    os.makedirs(os.path.dirname(SHEET), exist_ok=True)
    write_png(SHEET, rows)
    print("wrote %s (%dx%d)" % (os.path.relpath(SHEET, ROOT), width, height))
    return 0


def main(argv):
    modes = [a for a in argv if a in ("--verify", "--ascii", "--sheet")]
    unknown = [a for a in argv if a not in ("--verify", "--ascii", "--sheet")]
    if unknown:
        sys.stderr.write("unknown argument(s): %s\n" % " ".join(unknown))
        return 2
    if not modes:
        do_generate()
        return 0
    rc = 0
    for mode in modes:
        rc |= {"--verify": do_verify, "--ascii": do_ascii, "--sheet": do_sheet}[mode]()
    return rc


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
