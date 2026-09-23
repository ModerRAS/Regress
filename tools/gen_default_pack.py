#!/usr/bin/env python3
"""Generate the twelve base 16x16 RGBA8 tiles plus the chest, pumpkin and heartwood override
tiles of the bundled default texture pack.

Python 3 standard library only (zlib/struct/hashlib/os/math/argparse) -- no Pillow, no numpy.
Output bytes are deterministic: re-running the generator reproduces identical files.

    python tools/gen_default_pack.py            # (re)write texturepacks/default/tiles/*.png
    python tools/gen_default_pack.py --verify   # decode the written PNGs and check the contract
    python tools/gen_default_pack.py --wrap     # 1-pixel wrap (tileability) report
    python tools/gen_default_pack.py --ascii    # text view of every tile
    python tools/gen_default_pack.py --sheet    # contact sheet under .pi/texpack/art/
    python tools/gen_default_pack.py --imports  # write the Godot .import companions for every tile

Contract: docs/texture-packs.md. Key order == frozen tile indices 0..11:
stone, dirt, grass_top, grass_side, grass_bottom, sand, wood_side, wood_top, plank, leaves,
bedrock, missing.
Chest overrides (Phase B) == vocabulary cells 48..53 -> no-variant baseline layer 60..65:
chest_posx, chest_negx, chest_top, chest_bottom, chest_posz, chest_negz.
Pumpkin overrides (Phase C) == vocabulary cells 54..59 -> no-variant baseline layer 66..71:
pumpkin_posx, pumpkin_negx, pumpkin_top, pumpkin_bottom, pumpkin_posz, pumpkin_negz.
Heartwood overrides (Phase D) == vocabulary cells 60..65 -> no-variant baseline layer 72..77:
heartwood_posx, heartwood_negx, heartwood_top, heartwood_bottom, heartwood_posz, heartwood_negz.
"""

import argparse
import hashlib
import math
import os
import struct
import sys
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TILES_DIR = os.path.join(ROOT, "texturepacks", "default", "tiles")
SHEET_PATH = os.path.join(ROOT, ".pi", "texpack", "art", "contact_sheet.png")

KEY_ORDER = [
    "stone", "dirt", "grass_top", "grass_side", "grass_bottom", "sand",
    "wood_side", "wood_top", "plank", "leaves", "bedrock", "missing",
]
# Phase B chest overrides: vocabulary cells 48..53 -> no-variant baseline layer 60..65.
# Append-only slots; the base rows above stay frozen.
OVERRIDE_KEYS = [
    "chest_posx", "chest_negx", "chest_top", "chest_bottom", "chest_posz", "chest_negz",
    "pumpkin_posx", "pumpkin_negx", "pumpkin_top", "pumpkin_bottom", "pumpkin_posz", "pumpkin_negz",
    "heartwood_posx", "heartwood_negx", "heartwood_top", "heartwood_bottom",
    "heartwood_posz", "heartwood_negz",
]
ALL_KEYS = KEY_ORDER + OVERRIDE_KEYS
SIZE = 16
# sRGB base colours from the spec table (docs/texture-packs.md "Procedural tiles").
SRGB = {
    "stone": (0.52, 0.52, 0.55),
    "dirt": (0.44, 0.30, 0.20),
    "grass_top": (0.34, 0.62, 0.24),
    "grass_side": (0.36, 0.50, 0.24),
    "grass_bottom": (0.44, 0.30, 0.20),
    "sand": (0.86, 0.80, 0.56),
    "wood_side": (0.36, 0.26, 0.15),
    "wood_top": (0.36, 0.26, 0.15),
    "plank": (0.68, 0.51, 0.31),
    "leaves": (0.20, 0.45, 0.17),
    "bedrock": (0.16, 0.16, 0.18),
    "missing": (1.00, 0.00, 1.00),
}
BAYER4 = ((0, 8, 2, 10), (12, 4, 14, 6), (3, 11, 1, 9), (15, 7, 13, 5))
RAMP = " .:-=+*#%@"


# ---------------------------------------------------------------- deterministic noise

def _hash(x, y, seed):
    """Pure integer hash -> 32-bit. No time, no global RNG, stable across runs."""
    n = (x * 0x9E3779B1 + y * 0x85EBCA77 + seed * 0xC2B2AE3D) & 0xFFFFFFFF
    n ^= n >> 15
    n = (n * 0x2545F491) & 0xFFFFFFFF
    n ^= n >> 13
    n = (n * 0x27D4EB2F) & 0xFFFFFFFF
    n ^= n >> 16
    return n & 0xFFFFFFFF


def rnd(x, y, seed):
    """Deterministic per-pixel value in [0,1]."""
    return _hash(x, y, seed) / 4294967295.0


def pnoise(x, y, lx, ly, seed):
    """Value noise on an lx*ly lattice that wraps over the 16x16 tile (tileable by construction)."""
    cx, cy = SIZE / lx, SIZE / ly
    fx, fy = x / cx, y / cy
    i0, j0 = int(math.floor(fx)), int(math.floor(fy))
    tx, ty = fx - i0, fy - j0
    tx, ty = tx * tx * (3 - 2 * tx), ty * ty * (3 - 2 * ty)
    a = rnd(i0 % lx, j0 % ly, seed)
    b = rnd((i0 + 1) % lx, j0 % ly, seed)
    c = rnd(i0 % lx, (j0 + 1) % ly, seed)
    d = rnd((i0 + 1) % lx, (j0 + 1) % ly, seed)
    return (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty


def spread(n, k):
    """Value noise concentrates around 0.5; widen it so tones actually read at 16px."""
    return max(0.0, min(1.0, 0.5 + k * (n - 0.5)))


def base(key):
    return tuple(int(round(v * 255)) for v in SRGB[key])


def mul(rgb, f):
    return tuple(min(255, max(0, int(round(v * f)))) for v in rgb)


def grid():
    return [[None] * SIZE for _ in range(SIZE)]


# ---------------------------------------------------------------- the twelve tiles

def tile_stone():
    """Grey speckled with darker contour cracks."""
    b, px = base("stone"), grid()
    for y in range(SIZE):
        for x in range(SIZE):
            n = spread(0.55 * pnoise(x, y, 2, 2, 101) + 0.45 * pnoise(x, y, 8, 8, 102), 1.8)
            f = 0.86 + 0.28 * n
            f *= 0.96 + 0.08 * rnd(x, y, 106)   # fine grain, keeps it a rock not a blob
            if abs(pnoise(x, y, 8, 8, 103) - 0.5) < 0.07:
                f *= 0.62                       # crack lines (a contour of the noise field)
            if rnd(x, y, 104) < 0.06:
                f *= 0.82                       # pits
            if spread(pnoise(x, y, 4, 4, 105), 2.2) > 0.78:
                f *= 1.10                       # light plates
            px[y][x] = mul(b, f) + (255,)
    return px


def _dirt(px, seed, tone):
    b = base("dirt")
    for y in range(SIZE):
        for x in range(SIZE):
            n = spread(0.55 * pnoise(x, y, 4, 4, seed) + 0.45 * pnoise(x, y, 8, 8, seed + 1), 2.2)
            f = 0.74 if n < 0.40 else (0.96 if n < 0.62 else 1.14)   # quantized clumps
            if rnd(x, y, seed + 2) < 0.07:
                f *= 0.62                       # dark pebbles
            px[y][x] = mul(b, f * tone) + (255,)
    return px


def tile_dirt():
    """Brown clumpy soil."""
    return _dirt(grid(), 201, 1.0)


def tile_grass_bottom():
    """Soil seen from below -- same clumps, cooler/darker, different seed than `dirt`."""
    return _dirt(grid(), 501, 0.90)


def tile_grass_top():
    """Green tonal dither: smooth tone field quantized through a 4x4 Bayer matrix."""
    b, px = base("grass_top"), grid()
    for y in range(SIZE):
        for x in range(SIZE):
            n = spread(0.60 * pnoise(x, y, 4, 4, 301) + 0.40 * pnoise(x, y, 8, 8, 302), 2.2)
            jitter = (BAYER4[y % 4][x % 4] + 0.5) / 16.0 - 0.5
            lvl = max(0, min(3, int(math.floor(n * 4 + jitter))))
            f = 0.84 + 0.34 * (lvl / 3.0)
            if rnd(x, y, 303) < 0.05:
                f *= 0.82                       # darker blades
            px[y][x] = mul(b, f) + (255,)
    return px


def tile_grass_side():
    """Green band on rows 0-3 (image top, v=0) with an irregular fringe; dirt below."""
    gb, db, px = base("grass_side"), base("dirt"), grid()
    depth = [5 if rnd(x, 7, 401) < 0.28 else (3 if rnd(x, 7, 401) > 0.93 else 4) for x in range(SIZE)]
    for y in range(SIZE):
        for x in range(SIZE):
            if y < depth[x]:
                n = spread(0.60 * pnoise(x, y, 4, 4, 402) + 0.40 * pnoise(x, y, 8, 8, 403), 2.2)
                f = 0.82 + 0.36 * n
                if y == 0:
                    f *= 1.08                   # lit top row
                if y == depth[x] - 1:
                    f *= 0.70                   # dark rim where the grass meets the soil
                if rnd(x, y, 404) < 0.06:
                    f *= 0.88
                px[y][x] = mul(gb, f) + (255,)
            else:
                n = spread(0.55 * pnoise(x, y, 4, 4, 405) + 0.45 * pnoise(x, y, 8, 8, 406), 2.2)
                f = 0.74 if n < 0.40 else (0.96 if n < 0.62 else 1.12)
                if rnd(x, y, 407) < 0.07:
                    f *= 0.62
                px[y][x] = mul(db, f) + (255,)
    return px


def tile_sand():
    """Pale fine dither."""
    b, px = base("sand"), grid()
    for y in range(SIZE):
        for x in range(SIZE):
            n = spread(0.5 * pnoise(x, y, 8, 8, 601) + 0.5 * pnoise(x, y, 16, 16, 602), 1.8)
            jitter = (BAYER4[y % 4][x % 4] + 0.5) / 16.0 - 0.5
            f = 0.93 + 0.14 * n + 0.06 * jitter
            if rnd(x, y, 603) < 0.08:
                f *= 0.91                       # coarser grains
            px[y][x] = mul(b, f) + (255,)
    return px


def tile_wood_side():
    """Vertical bark grain: wavy ridges and dark grooves running down the tile."""
    b, px = base("wood_side"), grid()
    for y in range(SIZE):
        for x in range(SIZE):
            wob = int(round(1.6 * (pnoise(x, y, 4, 4, 701) - 0.5)))
            ridge = spread(0.70 * pnoise((x + wob) % SIZE, y, 8, 2, 702)
                           + 0.30 * pnoise((x + wob) % SIZE, y, 16, 4, 703), 2.4)
            f = 0.62 + 0.70 * ridge
            if ridge < 0.18:
                f *= 0.55                       # groove
            if ridge > 0.85:
                f *= 1.12                       # ridge highlight
            px[y][x] = mul(b, f) + (255,)
    return px


def tile_wood_top():
    """Concentric end-grain rings, toroidal distance so the seams line up."""
    b, px = base("wood_top"), grid()
    period = 2.2
    for y in range(SIZE):
        for x in range(SIZE):
            dx = abs(x - 7.5)
            dy = abs(y - 7.5)
            dx, dy = min(dx, SIZE - dx), min(dy, SIZE - dy)
            r = math.hypot(dx, dy * 0.92) + 0.45 * (pnoise(x, y, 4, 4, 801) - 0.5)
            f = 1.0 + 0.42 * math.sin(r * (2 * math.pi / period))
            if r < 1.6:
                f *= 0.55                       # pith
            f *= 0.93 + 0.12 * pnoise(x, y, 16, 16, 802)
            px[y][x] = mul(b, f) + (255,)
    return px


def tile_plank():
    """Four horizontal boards (4px), dark seams, one butt joint per board."""
    b, px = base("plank"), grid()
    joint = [int(rnd(bd, 3, 901) * SIZE) for bd in range(4)]
    for y in range(SIZE):
        bd = y // 4
        for x in range(SIZE):
            if y % 4 == 0:
                f = 0.62                        # horizontal seam
            else:
                f = (0.96 + 0.08 * rnd(bd, 5, 904)) * (0.92 + 0.16 * pnoise(x, y, 8, 2, 902 + bd))
                f *= 0.97 + 0.06 * pnoise(x, y, 16, 8, 903)
                if x == joint[bd]:
                    f *= 0.68                   # butt joint between planks
            px[y][x] = mul(b, f) + (255,)
    return px


def tile_leaves():
    """Dark green clustered foliage; clustered cutout holes filled from the lowest-density pixels."""
    b, px = base("leaves"), grid()
    dens = [[0.55 * pnoise(x, y, 4, 4, 1001) + 0.45 * pnoise(x, y, 8, 8, 1002)
             for x in range(SIZE)] for y in range(SIZE)]
    order = sorted(dens[y][x] for y in range(SIZE) for x in range(SIZE))
    thr = order[int(0.16 * SIZE * SIZE)]        # exactly ~16% transparent, in clustered blobs
    hole = [[dens[y][x] <= thr for x in range(SIZE)] for y in range(SIZE)]
    for y in range(SIZE):
        for x in range(SIZE):
            n = pnoise(x, y, 16, 16, 1003)
            if hole[y][x]:
                px[y][x] = mul(b, 0.45) + (0,)
                continue
            f = 0.62 + 0.55 * spread(dens[y][x], 2.0) + 0.10 * n
            if any(hole[(y + dy) % SIZE][(x + dx) % SIZE]
                   for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1))):
                f *= 0.80                       # shaded rim around each hole
            if dens[y][x] > 0.72:
                f *= 1.08                       # sunlit leaf clusters
            px[y][x] = mul(b, f) + (255,)
    return px


def tile_bedrock():
    """Coarse dark 2x2 patches -- much darker and blockier than stone."""
    b, px = base("bedrock"), grid()
    for y in range(SIZE):
        for x in range(SIZE):
            bx, by = x // 2, y // 2
            f = 0.62 + 0.90 * rnd(bx, by, 1101)
            if rnd(bx, by, 1102) < 0.16:
                f *= 0.50                       # deep pits
            px[y][x] = mul(b, f) + (255,)
    return px


def tile_missing():
    """Loud magenta / black 8px checker."""
    px = grid()
    for y in range(SIZE):
        for x in range(SIZE):
            px[y][x] = ((255, 0, 255, 255) if (x // 8 + y // 8) % 2 == 0 else (0, 0, 0, 255))
    return px


# ---------------------------------------------------------------- the six chest overrides

CHEST_LATCH = (219, 179, 102)   # sRGB 0.86/0.70/0.40: the plank hue, brightened


def _chest_boards(px, seed):
    """Four 4px boards with seams and per-board joints: the `plank` grain, reseeded."""
    b = base("plank")
    joint = [int(rnd(bd, 3, seed) * SIZE) for bd in range(4)]
    for y in range(SIZE):
        bd = y // 4
        for x in range(SIZE):
            if y % 4 == 0:
                f = 0.64
            else:
                f = (0.95 + 0.10 * rnd(bd, 5, seed + 1)) * (0.90 + 0.18 * pnoise(x, y, 8, 2, seed + 2 + bd))
                if x == joint[bd]:
                    f *= 0.70
            px[y][x] = mul(b, f) + (255,)


def _chest_band(px, seed):
    """Dark two-row lid seam across the face, lit on its top row."""
    b = base("wood_side")
    for y in (6, 7):
        for x in range(SIZE):
            f = 0.90 + 0.14 * pnoise(x, y, 8, 2, seed)
            if y == 6:
                f *= 1.45
            px[y][x] = mul(b, f) + (255,)


def _chest_frame(px, seed):
    """One-pixel darker border: the chest's edge boards."""
    b = base("wood_side")
    for y in range(SIZE):
        for x in range(SIZE):
            if x in (0, SIZE - 1) or y in (0, SIZE - 1):
                px[y][x] = mul(b, 0.80 + 0.24 * rnd(x, y, seed)) + (255,)


def _chest_side(px, seed):
    """Boards + lid seam + two hinges just under the seam."""
    _chest_boards(px, seed)
    _chest_band(px, seed + 1)
    _chest_frame(px, seed + 2)
    b = base("wood_side")
    for x in (3, 4, 11, 12):
        for y in (5, 6):
            px[y][x] = mul(b, 0.62 if y == 5 else 1.30) + (255,)
    return px


def tile_chest_posz():
    """Front (local +Z, player-facing): boards, lid seam, frame, centred latch."""
    seed, px = 1201, grid()
    _chest_boards(px, seed)
    _chest_band(px, seed + 1)
    _chest_frame(px, seed + 2)
    b = base("wood_side")
    for y in range(5, 10):
        for x in range(6, 10):
            if y in (5, 9) or x in (6, 9):
                px[y][x] = mul(b, 0.55) + (255,)                      # latch outline
            else:
                px[y][x] = mul(CHEST_LATCH, 0.94 + 0.10 * pnoise(x, y, 4, 4, seed + 3)) + (255,)
    px[7][7] = px[7][8] = mul(b, 0.40) + (255,)                       # keyhole
    return px


def tile_chest_negz():
    """Back face: boards, lid seam and edge frame."""
    seed, px = 1301, grid()
    _chest_boards(px, seed)
    _chest_band(px, seed + 1)
    _chest_frame(px, seed + 2)
    return px


def tile_chest_posx():
    """Right side: boards, lid seam, frame and two hinge hints."""
    return _chest_side(grid(), 1401)


def tile_chest_negx():
    """Left side: same side pattern, reseeded so its grain differs."""
    return _chest_side(grid(), 1501)


def tile_chest_top():
    """Lid: boards, lid seam and edge frame."""
    seed, px = 1601, grid()
    _chest_boards(px, seed)
    _chest_band(px, seed + 1)
    _chest_frame(px, seed + 2)
    return px


def tile_chest_bottom():
    """Plain underside: boards only, slightly darkened."""
    px = grid()
    _chest_boards(px, 1701)
    for y in range(SIZE):
        for x in range(SIZE):
            px[y][x] = mul(px[y][x][:3], 0.88) + (255,)
    return px


# ---------------------------------------------------------------- the six pumpkin overrides

PUMPKIN_FLESH = (219, 112, 26)   # sRGB ~0.86/0.44/0.10
PUMPKIN_SHADE = (148, 69, 13)    # sRGB ~0.58/0.27/0.05: rib seams
PUMPKIN_CARVE = (41, 18, 5)      # sRGB ~0.16/0.07/0.02: carved features
PUMPKIN_STEM = (89, 115, 31)     # sRGB ~0.35/0.45/0.12


def _pumpkin_ribs(px, seed):
    """Orange rind with vertical rib shading, reseeded per face. Rib seams sit at x=2 and x=9,
    clear of the carved-face signature columns (3..5, 7..8, 10..12); every tone stays >= 0.63x
    flesh, so no pixel of a ribbed face is ever dark."""
    for y in range(SIZE):
        for x in range(SIZE):
            d = min((x - 2) % SIZE, (2 - x) % SIZE, (x - 9) % SIZE, (9 - x) % SIZE)
            f = 0.78 + 0.10 * min(d, 3)
            if d == 0:
                f *= 0.88                       # rib seam shadow
            n = spread(0.6 * pnoise(x, y, 4, 4, seed) + 0.4 * pnoise(x, y, 8, 8, seed + 1), 1.6)
            f *= 0.92 + 0.16 * n
            px[y][x] = mul(PUMPKIN_FLESH, f) + (255,)


def tile_pumpkin_posz():
    """Carved face (local +Z, player-facing): ribbed rind, dark eye patches at rows 5..6 cols
    3..5 and 10..12, the bridge cells (rows 5..6, cols 7..8) left bright orange, and a dark
    mouth block rows 10..12 cols 5..10 with jagged teeth rows 9/13 outside the signature."""
    px = grid()
    _pumpkin_ribs(px, 1701)
    for y in (5, 6):
        for x in list(range(3, 6)) + list(range(10, 13)):
            px[y][x] = PUMPKIN_CARVE + (255,)
    px[4][4] = px[4][11] = PUMPKIN_CARVE + (255,)          # jagged eye tops
    for y in (5, 6):
        for x in (7, 8):
            px[y][x] = mul(PUMPKIN_FLESH, 1.04) + (255,)   # lit bridge between the eyes
    for y in (10, 11, 12):
        for x in range(5, 11):
            px[y][x] = PUMPKIN_CARVE + (255,)
    for x in (5, 7, 9):
        px[9][x] = PUMPKIN_CARVE + (255,)                  # jagged upper teeth
    for x in (6, 8, 10):
        px[13][x] = PUMPKIN_CARVE + (255,)                 # jagged lower teeth
    return px


def tile_pumpkin_negz():
    """Back face: ribbed rind only, no carved features."""
    px = grid()
    _pumpkin_ribs(px, 1801)
    return px


def tile_pumpkin_posx():
    """Right side: ribbed rind, reseeded, no carved features."""
    px = grid()
    _pumpkin_ribs(px, 1901)
    return px


def tile_pumpkin_negx():
    """Left side: ribbed rind, reseeded again, no carved features."""
    px = grid()
    _pumpkin_ribs(px, 2001)
    return px


def tile_pumpkin_top():
    """Flesh seen from above with a small off-centre stem."""
    px = grid()
    _pumpkin_ribs(px, 2101)
    for y in range(2, 6):
        for x in range(9, 12):
            if (y, x) in ((2, 9), (2, 11), (5, 9), (5, 11)):
                continue                       # rounded stem corners
            px[y][x] = mul(PUMPKIN_STEM, 0.82 + 0.36 * rnd(x, y, 2103)) + (255,)
    return px


def tile_pumpkin_bottom():
    """Plain ribbed underside, slightly darkened (mirrors `chest_bottom`)."""
    px = grid()
    _pumpkin_ribs(px, 2201)
    for y in range(SIZE):
        for x in range(SIZE):
            px[y][x] = mul(px[y][x][:3], 0.88) + (255,)
    return px


# ---------------------------------------------------------------- the six heartwood overrides

# Cut cross-section palette: pale pith, red-brown rings, near-black sawn rim. Every tone is
# clearly darker AND redder than both existing wood tiles (wood_side bark 110.9/79.7/45.7,
# wood_top end-grain 89.8/64.4/37.1) -- a trunk's wood_top shell ring and the heartwood core
# behind it are on screen at the same time when the trunk is cut, so they must not read alike.
HEARTWOOD_PITH = (198, 156, 116)   # sRGB ~0.78/0.61/0.45: pale pith at the cut centre
HEARTWOOD_RING = (122, 58, 38)     # sRGB ~0.48/0.23/0.15: lit red-brown ring
HEARTWOOD_BAND = (72, 29, 20)      # sRGB ~0.28/0.11/0.08: dark ring band
HEARTWOOD_DEEP = (46, 18, 13)      # sRGB ~0.18/0.07/0.05: deepest ring band
HEARTWOOD_CRUST = (24, 9, 7)       # sRGB ~0.09/0.04/0.03: near-black sawn rim

# Ring bands as (r2max, tone), ordered outward from the cut centre. `r2 = dx*dx + dy*dy` is an
# integer in half-cell units (cell i's centre sits at 2*i-15, so the tile centre is exactly
# (7.5,7.5) and the four compass cells (8,2),(8,13),(2,8),(13,8) are all exactly r2 = 122).
# Integer thresholds only: no sqrt, no float geometry. [100,146) is a wide dark band so the
# +-7 noise warp can never push a compass cell out of it.
HEARTWOOD_BANDS = (
    (30, HEARTWOOD_PITH),
    (70, HEARTWOOD_RING),
    (100, HEARTWOOD_BAND),
    (146, HEARTWOOD_DEEP),
    (190, HEARTWOOD_RING),
    (250, HEARTWOOD_DEEP),
    (320, HEARTWOOD_RING),
    (1 << 30, HEARTWOOD_DEEP),
)


def _heartwood_cut(px, seed):
    """One cut cross-section, reseeded per face: concentric rings around a pale pith with a
    near-black sawn rim, so the four corners come out near black. The same value noise the other
    tiles use wobbles each ring bucketing -- the `_pumpkin_ribs` recipe, ring-shaped."""
    for y in range(SIZE):
        for x in range(SIZE):
            dy, dx = 2 * y - 15, 2 * x - 15
            r2 = dx * dx + dy * dy + int((pnoise(x, y, 4, 4, seed) - 0.5) * 16.0)
            tone = HEARTWOOD_DEEP
            for r2max, t in HEARTWOOD_BANDS:
                if r2 < r2max:
                    tone = t
                    break
            if x == 0 or y == 0 or x == SIZE - 1 or y == SIZE - 1:
                tone = HEARTWOOD_CRUST       # sawn rim, and so a near-black corner every face
            px[y][x] = mul(tone, 0.94 + 0.12 * rnd(x, y, seed + 1)) + (255,)
    return px


def tile_heartwood_posx():
    """Right face of the heartwood core: cut cross-section, reseeded."""
    return _heartwood_cut(grid(), 2301)


def tile_heartwood_negx():
    """Left face of the heartwood core: cut cross-section, reseeded."""
    return _heartwood_cut(grid(), 2401)


def tile_heartwood_top():
    """Top face of the heartwood core: cut cross-section, reseeded."""
    return _heartwood_cut(grid(), 2501)


def tile_heartwood_bottom():
    """Bottom face of the heartwood core: cut cross-section, reseeded."""
    return _heartwood_cut(grid(), 2601)


def tile_heartwood_posz():
    """Front face of the heartwood core: cut cross-section, reseeded."""
    return _heartwood_cut(grid(), 2701)


def tile_heartwood_negz():
    """Back face of the heartwood core: cut cross-section, reseeded."""
    return _heartwood_cut(grid(), 2801)


TILES = {
    "stone": tile_stone,
    "dirt": tile_dirt,
    "grass_top": tile_grass_top,
    "grass_side": tile_grass_side,
    "grass_bottom": tile_grass_bottom,
    "sand": tile_sand,
    "wood_side": tile_wood_side,
    "wood_top": tile_wood_top,
    "plank": tile_plank,
    "leaves": tile_leaves,
    "bedrock": tile_bedrock,
    "missing": tile_missing,
    "chest_posx": tile_chest_posx,
    "chest_negx": tile_chest_negx,
    "chest_top": tile_chest_top,
    "chest_bottom": tile_chest_bottom,
    "chest_posz": tile_chest_posz,
    "chest_negz": tile_chest_negz,
    "pumpkin_posx": tile_pumpkin_posx,
    "pumpkin_negx": tile_pumpkin_negx,
    "pumpkin_top": tile_pumpkin_top,
    "pumpkin_bottom": tile_pumpkin_bottom,
    "pumpkin_posz": tile_pumpkin_posz,
    "pumpkin_negz": tile_pumpkin_negz,
    "heartwood_posx": tile_heartwood_posx,
    "heartwood_negx": tile_heartwood_negx,
    "heartwood_top": tile_heartwood_top,
    "heartwood_bottom": tile_heartwood_bottom,
    "heartwood_posz": tile_heartwood_posz,
    "heartwood_negz": tile_heartwood_negz,
}


# ---------------------------------------------------------------- PNG in / out

def write_png(path, rows):
    """8-bit RGBA, non-interlaced, filter 0, zlib. Deterministic for a fixed interpreter."""
    h, w = len(rows), len(rows[0])
    raw = bytearray()
    for row in rows:
        raw.append(0)
        for px in row:
            raw += bytes(px)

    def chunk(typ, body):
        return (struct.pack(">I", len(body)) + typ + body
                + struct.pack(">I", zlib.crc32(typ + body) & 0xFFFFFFFF))

    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
           + chunk(b"IEND", b""))
    with open(path, "wb") as fh:
        fh.write(png)
    return len(png)


def read_png(path):
    """Decode one of our own PNGs: zlib.decompress + unfilter (filter 0 is all we emit)."""
    data = open(path, "rb").read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("not a PNG")
    pos, idat, head = 8, b"", {}
    while pos < len(data):
        n = struct.unpack(">I", data[pos:pos + 4])[0]
        typ, body = data[pos + 4:pos + 8], data[pos + 8:pos + 8 + n]
        crc = struct.unpack(">I", data[pos + 8 + n:pos + 12 + n])[0]
        if crc != zlib.crc32(typ + body) & 0xFFFFFFFF:
            raise ValueError("bad chunk CRC in " + os.path.basename(path))
        if typ == b"IHDR":
            w, h, depth, ctype, _comp, _filt, interlace = struct.unpack(">IIBBBBB", body)
            head = {"w": w, "h": h, "depth": depth, "ctype": ctype, "interlace": interlace}
        elif typ == b"IDAT":
            idat += body
        elif typ == b"IEND":
            break
        pos += 12 + n
    if not head:
        raise ValueError("no IHDR")
    raw = zlib.decompress(idat)
    stride = head["w"] * 4
    rows = []
    for y in range(head["h"]):
        off = y * (stride + 1)
        if raw[off] != 0:
            raise ValueError("unexpected filter byte %d" % raw[off])
        line = raw[off + 1:off + 1 + stride]
        rows.append([tuple(line[i * 4:i * 4 + 4]) for i in range(head["w"])])
    head["rows"] = rows
    return head


def tile_path(key):
    return os.path.join(TILES_DIR, key + ".png")


# ---------------------------------------------------------------- modes

def do_generate():
    os.makedirs(TILES_DIR, exist_ok=True)
    for key in ALL_KEYS:
        n = write_png(tile_path(key), TILES[key]())
        print("wrote texturepacks/default/tiles/%s.png (%d bytes)" % (key, n))


def _latch_motif(rows):
    """Front-latch pixel signature: bright plate pixels at (6,7),(6,8),(8,7),(8,8) around a dark
    keyhole at (7,7),(7,8). Coordinate bounds, not means: noise cannot satisfy all six cells."""
    def bright(px):
        return px[0] >= 195 and px[1] >= 155 and px[2] <= 150

    def dark(px):
        return px[0] <= 60 and px[1] <= 50 and px[2] <= 40

    return (all(bright(rows[y][x]) for y, x in ((6, 7), (6, 8), (8, 7), (8, 8)))
            and all(dark(rows[y][x]) for y, x in ((7, 7), (7, 8))))


def _pumpkin_face_motif(rows):
    """Carved-face pixel signature, coordinates not average colour: all six left-eye cells
    (rows 5..6, cols 3..5) and all six right-eye cells (rows 5..6, cols 10..12) dark, the four
    bridge cells (rows 5..6, cols 7..8) bright orange, and every mouth cell (rows 10..12,
    cols 5..10) dark. Ribbed sides must fail at least one of these cells."""
    def dark(px):
        return px[0] <= 90 and px[1] <= 90 and px[2] <= 90

    def bright(px):
        return px[0] >= 150 and 50 <= px[1] <= 170 and px[2] <= 100

    return (all(dark(rows[y][x]) for y in (5, 6) for x in (3, 4, 5))
            and all(dark(rows[y][x]) for y in (5, 6) for x in (10, 11, 12))
            and all(bright(rows[y][x]) for y in (5, 6) for x in (7, 8))
            and all(dark(rows[y][x]) for y in (10, 11, 12) for x in range(5, 11)))


def _heartwood_ring_motif(rows):
    """Cut-cross-section signature, coordinates not average colour: a pale pith pixel at the
    centre cell (8,8), a dark ring-band pixel at all four compass cells (8,2),(8,13),(2,8),
    (13,8) and a near-black crust pixel at all four corners. Coordinate bounds, not means: bark
    and end-grain tiles cannot satisfy a pale centre and black corners at once."""
    def pale(px):
        return px[0] >= 150 and px[1] >= 110 and px[2] >= 80

    def band(px):
        return px[0] <= 95 and px[1] <= 60 and px[2] <= 45

    def crust(px):
        return px[0] <= 45 and px[1] <= 30 and px[2] <= 25

    return (pale(rows[8][8])
            and all(band(rows[y][x]) for y, x in ((8, 2), (8, 13), (2, 8), (13, 8)))
            and all(crust(rows[y][x]) for y, x in ((0, 0), (15, 0), (0, 15), (15, 15))))


def _heartwood_delta_fraction(rows, ref_rows):
    """Fraction of the 256 coordinates whose max-channel delta against the SAME coordinate of
    another tile is >= 16: the cut wood must differ pixel for pixel, not merely on the mean."""
    hits = sum(1 for y in range(SIZE) for x in range(SIZE)
               if _delta(rows[y][x], ref_rows[y][x]) >= 16)
    return hits / float(SIZE * SIZE)


def do_verify():
    ok = True
    # The latch belongs to the player-facing face only (local +Z == chest_posz).
    latch_want = {"chest_posz": True, "chest_negz": False, "chest_posx": False,
                  "chest_negx": False, "chest_top": False, "chest_bottom": False}
    latch_bad = []
    files = sorted(f for f in os.listdir(TILES_DIR) if f.endswith(".png"))
    print("tiles directory: %d PNG file(s)" % len(files))
    for key in ALL_KEYS:
        path = tile_path(key)
        if not os.path.isfile(path):
            print("%-12s MISSING" % key)
            ok = False
            continue
        try:
            img = read_png(path)
        except Exception as exc:
            print("%-12s DECODE FAIL: %s" % (key, exc))
            ok = False
            continue
        alphas = [px[3] for row in img["rows"] for px in row]
        clear = sum(1 for a in alphas if a == 0)
        pct = 100.0 * clear / len(alphas)
        notes, good = [], True
        good &= img["w"] == SIZE and img["h"] == SIZE
        good &= img["depth"] == 8 and img["ctype"] == 6 and img["interlace"] == 0
        if key == "leaves":
            good &= set(alphas) <= {0, 255} and pct >= 5.0
            notes.append("transparent %d/256 (%.1f%%) alpha={%s}"
                         % (clear, pct, ",".join(str(v) for v in sorted(set(alphas)))))
        else:
            good &= all(a == 255 for a in alphas)
            notes.append("opaque")
        print("%-12s %dx%d depth=%d ctype=%d interlace=%d %s %s"
              % (key, img["w"], img["h"], img["depth"], img["ctype"], img["interlace"],
                 "ok " if good else "BAD", " ".join(notes)))
        if key in latch_want and _latch_motif(img["rows"]) != latch_want[key]:
            latch_bad.append("%s %s" % (key[len("chest_"):],
                                        "missing" if latch_want[key] else "present"))
        ok &= good
    if latch_bad:
        print("FAIL chest latch motif: " + " / ".join(latch_bad))
        ok = False
    else:
        print("chest latch motif: ok (chest_posz present, other five absent)")
    # The carved face belongs to the player-facing face only (local +Z == pumpkin_posz).
    pumpkin_want = {"pumpkin_posz": True, "pumpkin_negz": False, "pumpkin_posx": False,
                    "pumpkin_negx": False, "pumpkin_top": False, "pumpkin_bottom": False}
    pumpkin_bad = []
    for key, want in pumpkin_want.items():
        try:
            motif = _pumpkin_face_motif(read_png(tile_path(key))["rows"])
        except Exception:
            pumpkin_bad.append(key[len("pumpkin_"):] + " missing")
            continue
        if motif != want:
            pumpkin_bad.append("%s %s" % (key[len("pumpkin_"):],
                                          "missing" if want else "present"))
    if pumpkin_bad:
        print("FAIL pumpkin face motif: " + " / ".join(pumpkin_bad))
        ok = False
    else:
        print("pumpkin face motif: ok (pumpkin_posz present, other five absent)")
    # Heartwood is only ever seen as a cut cross-section: all six faces must show the pale pith,
    # the dark ring band and the near-black rim (at coordinates, not on average), and must read
    # differently from both existing wood tiles at the very same coordinates.
    hw_bad, hw_fracs = [], []
    hw_refs = {}
    for ref in ("wood_side", "wood_top"):
        try:
            hw_refs[ref] = read_png(tile_path(ref))["rows"]
        except Exception as exc:
            hw_bad.append("%s unreadable (%s)" % (ref, exc))
    for key in [k for k in ALL_KEYS if k.startswith("heartwood_")]:
        try:
            rows = read_png(tile_path(key))["rows"]
        except Exception:
            hw_bad.append("%s missing" % key)
            continue
        if not _heartwood_ring_motif(rows):
            hw_bad.append("%s ring signature missing (want pale pith at (8,8), dark bands at "
                          "(8,2),(8,13),(2,8),(13,8), near-black corners)" % key)
        for ref, ref_rows in hw_refs.items():
            frac = _heartwood_delta_fraction(rows, ref_rows)
            hw_fracs.append(frac)
            if frac < 0.5:
                hw_bad.append("%s differs from %s on only %.3f of the 256 coordinates "
                              "(need >= 0.500)" % (key, ref, frac))
    if hw_bad:
        for line in hw_bad:
            print("FAIL heartwood art: " + line)
        ok = False
    else:
        print("heartwood rings: ok (pale pith + dark compass bands + near-black rim on all six "
              "cut faces; min per-coordinate delta vs wood_side/wood_top = %.3f >= 0.500)"
              % min(hw_fracs or [0.0]))
    print("PASS" if ok else "FAIL")
    return 0 if ok else 1


def _delta(a, b):
    return max(abs(a[i] - b[i]) for i in range(3))


def do_wrap():
    print("1-pixel wrap report (RGB channels, alpha excluded; delta = max channel difference)")
    print("col ref / row ref are in-tile neighbours (col7|col8, row7|row8) -- the noise's own step size")
    print("%-12s %7s %8s %7s %7s %8s %7s"
          % ("tile", "col max", "col mean", "row max", "row mean", "col ref", "row ref"))
    for key in ALL_KEYS:
        img = read_png(tile_path(key))
        rows = img["rows"]
        col = [_delta(rows[y][0], rows[y][SIZE - 1]) for y in range(SIZE)]
        row = [_delta(rows[0][x], rows[SIZE - 1][x]) for x in range(SIZE)]
        cref = sum(_delta(rows[y][7], rows[y][8]) for y in range(SIZE)) / SIZE
        rref = sum(_delta(rows[7][x], rows[8][x]) for x in range(SIZE)) / SIZE
        print("%-12s %7d %8.2f %7d %8.2f %7.2f %7.2f"
              % (key, max(col), sum(col) / len(col), max(row), sum(row) / len(row), cref, rref))


def _lum(px):
    return (0.299 * px[0] + 0.587 * px[1] + 0.114 * px[2]) / 255.0


def hue_class(rgb):
    r, g, b = (v / 255.0 for v in rgb)
    if r > 0.6 and b > 0.6 and g < 0.4:
        return "magenta"
    if g > r * 1.12 and g > b * 1.12:
        return "green"
    if max(r, g, b) < 0.25:
        return "dark"
    if min(r, g, b) > 0.5 and r >= g >= b:
        return "pale"
    if r > b * 1.25 and g > b * 1.05:
        return "brown"
    if abs(r - g) < 0.06 and abs(g - b) < 0.08:
        return "grey"
    return "mixed"


def do_ascii():
    for key in ALL_KEYS:
        img = read_png(tile_path(key))
        rows = img["rows"]
        print("%s:" % key)
        for row in rows:
            line = ""
            for px in row:
                line += " " if px[3] == 0 else RAMP[min(9, int(_lum(px) * 10))]
            print("  " + line)
        for y in (0, SIZE - 1):
            mean = tuple(int(round(sum(rows[y][x][c] for x in range(SIZE)) / SIZE)) for c in range(3))
            print("  row%-2d mean rgb=%s class=%s" % (y, mean, hue_class(mean)))


FONT35 = {
    "0": ("111", "101", "101", "101", "111"),
    "1": ("010", "110", "010", "010", "111"),
    "2": ("111", "001", "111", "100", "111"),
    "3": ("111", "001", "111", "001", "111"),
    "4": ("101", "101", "111", "001", "001"),
    "5": ("111", "100", "111", "001", "111"),
    "6": ("111", "100", "111", "101", "111"),
    "7": ("111", "001", "001", "001", "001"),
    "8": ("111", "101", "111", "101", "111"),
    "9": ("111", "101", "111", "001", "001"),
}


def _blit_digit(rows, ox, oy, digit, scale, colour):
    glyph = FONT35[digit]
    for gy in range(5):
        for gx in range(3):
            if glyph[gy][gx] != "1":
                continue
            for sy in range(scale):
                for sx in range(scale):
                    x, y = ox + gx * scale + sx, oy + gy * scale + sy
                    if 0 <= y < len(rows) and 0 <= x < len(rows[0]):
                        rows[y][x] = colour


def do_sheet(cols=4, cell=128):
    """Contact sheet: tile 0..11 in row-major order, each tile repeated 2x2 magnified 4x."""
    keys = ALL_KEYS
    rows_n = (len(keys) + cols - 1) // cols
    flat = [[(0, 0, 0, 255)] * (cols * cell) for _ in range(rows_n * cell)]
    for i, key in enumerate(keys):
        img = read_png(tile_path(key))
        src = img["rows"]
        cx, cy = (i % cols) * cell, (i // cols) * cell
        for py in range(cell):
            for px in range(cell):
                p = src[(py // 4) % SIZE][(px // 4) % SIZE]
                # holes (alpha 0) drawn over mid-grey, else the sheet itself would be transparent
                flat[cy + py][cx + px] = (p[0], p[1], p[2], 255) if p[3] else (128, 128, 128, 255)
        lum = sum(_lum(p) for row in src for p in row) / (SIZE * SIZE)
        fg = (0, 0, 0, 255) if lum > 0.45 else (255, 255, 255, 255)
        bg = (255, 255, 255, 255) if lum > 0.45 else (0, 0, 0, 255)
        ox, oy = cx + 6, cy + 6
        for d, dx in ((str(i // 10), 0), (str(i % 10), 8)):
            _blit_digit(flat, ox + dx + 1, oy + 1, d, 2, bg)
        for d, dx in ((str(i // 10), 0), (str(i % 10), 8)):
            _blit_digit(flat, ox + dx, oy, d, 2, fg)
    os.makedirs(os.path.dirname(SHEET_PATH), exist_ok=True)
    n = write_png(SHEET_PATH, flat)
    print("contact sheet: .pi/texpack/art/contact_sheet.png (%dx%d, %d bytes) cell order: %s"
          % (cols * cell, rows_n * cell, n, ", ".join(keys)))


IMPORT_PARAMS = """\
[params]

compress/mode=0
compress/high_quality=false
compress/lossy_quality=0.7
compress/uastc_level=0
compress/rdo_quality_loss=0.0
compress/hdr_compression=1
compress/normal_map=0
compress/channel_pack=0
mipmaps/generate=false
mipmaps/limit=-1
roughness/mode=0
roughness/src_normal=""
process/channel_remap/red=0
process/channel_remap/green=1
process/channel_remap/blue=2
process/channel_remap/alpha=3
process/fix_alpha_border=true
process/premult_alpha=false
process/normal_map_invert_y=false
process/hdr_as_srgb=false
process/hdr_clamp_exposure=false
process/size_limit=0
detect_3d/compress_to=0
"""


def do_imports():
    """Mirror Godot 4.7.2's generated default, without uid, pinned to lossless."""
    for key in ALL_KEYS:
        name = key + ".png"
        res = "res://texturepacks/default/tiles/" + name
        dest = "res://.godot/imported/%s-%s.ctex" % (
            name, hashlib.md5(res.encode()).hexdigest())
        text = ('[remap]\n\nimporter="texture"\ntype="CompressedTexture2D"\n'
                'path="%s"\nmetadata={\n"vram_texture": false\n}\n\n'
                '[deps]\n\nsource_file="%s"\ndest_files=["%s"]\n\n%s'
                % (dest, res, dest, IMPORT_PARAMS))
        with open(os.path.join(TILES_DIR, name + ".import"), "w", newline="\n") as fh:
            fh.write(text)
        print("wrote texturepacks/default/tiles/%s.import -> %s" % (name, dest))


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--verify", action="store_true")
    ap.add_argument("--wrap", action="store_true")
    ap.add_argument("--ascii", action="store_true")
    ap.add_argument("--sheet", action="store_true")
    ap.add_argument("--imports", action="store_true")
    args = ap.parse_args(argv)
    chosen = [(name, flag) for name, flag in (("generate", not any(vars(args).values())),
                                              ("verify", args.verify), ("wrap", args.wrap),
                                              ("ascii", args.ascii), ("sheet", args.sheet),
                                              ("imports", args.imports)) if flag]
    rc = 0
    for name, _flag in chosen:
        rc |= {"generate": do_generate, "verify": do_verify, "wrap": do_wrap,
               "ascii": do_ascii, "sheet": do_sheet, "imports": do_imports}[name]() or 0
    return rc


if __name__ == "__main__":
    sys.exit(main())
