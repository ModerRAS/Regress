#!/usr/bin/env python3
"""Deterministic generator for texturepacks/variants-demo/ (Regress texture-pack v1 demo).

Pure Python 3 standard library only (no PIL / no numpy): every PNG is encoded by
hand with zlib + struct so the output bytes are fully deterministic and portable.

Pack schema (design.md 1/2):
    version   1
    tile_size 16
    tiles     {
        "stone":    ["tiles/stone_0.png", ..., "tiles/stone_3.png"],  # 4 distinct variants
        "dirt":     ["tiles/dirt_0.png",  ..., "tiles/dirt_2.png"],   # 3 distinct variants
        "<other>":  "tiles/<other>.png",                              # single string, 10 keys
    }
    no "override" key.

Every PNG gets a Godot 4 `.import` companion (same pattern as
tools/gen_six_face_demo.py) so the editor can import the pack.

The ten single keys reuse tools/gen_default_pack.py tile art (identical style to the default
pack); stone and dirt get the four/three distinct procedural variants.

Usage:
    python tools/gen_variants_demo.py
    python tools/gen_variants_demo.py --verify     # regenerate to temp dir, byte-compare
    python tools/gen_variants_demo.py --sheet      # .pi/variants/tiles-contact-sheet.png (4x)
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import struct
import sys
import tempfile
import zlib
from pathlib import Path

# --------------------------------------------------------------- contract ---
PACK_VERSION = 1
TILE_SIZE = 16
SEED = 0x52454752_53535F31  # "REGRSS_1" - fixed by design; never change

STONE_VARIANTS = 4
DIRT_VARIANTS = 3
VARIANT_KEYS = ("stone", "dirt")

REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_PACK = REPO_ROOT / "texturepacks" / "default" / "pack.json"
FALLBACK_PACK = Path("C:/WorkSpace/Godot/Regress/texturepacks/default/pack.json")
OUT_DIR = REPO_ROOT / "texturepacks" / "variants-demo"
RES_PREFIX = "res://texturepacks/variants-demo/"
SHEET_PATH = REPO_ROOT / ".pi" / "variants" / "tiles-contact-sheet.png"

FALLBACK_KEYS = (
    "stone", "dirt", "grass", "sand", "gravel", "cobblestone",
    "planks", "log", "leaves", "water", "glass", "bedrock",
)

STONE_BASE = (126, 126, 130)
DIRT_BASE = (124, 88, 60)

RGBA = "tuple[int, int, int, int]"


# ------------------------------------------------------------------ PRNG ----
MASK64 = (1 << 64) - 1


def stable_hash(*parts: str) -> int:
    """Version-independent 64-bit hash used for per-asset seeds."""
    return int.from_bytes(hashlib.sha256("|".join(parts).encode("utf-8")).digest()[:8], "big")


class Rng:
    """splitmix64: deterministic across Python versions and platforms."""

    def __init__(self, seed: int) -> None:
        self.state = seed & MASK64

    def next_u64(self) -> int:
        self.state = (self.state + 0x9E3779B97F4A7C15) & MASK64
        z = self.state
        z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & MASK64
        z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & MASK64
        return z ^ (z >> 31)

    def below(self, n: int) -> int:
        return self.next_u64() % n

    def between(self, lo: int, hi: int) -> int:
        return lo + self.below(hi - lo + 1)

    def signed(self, mag: int) -> int:
        return self.between(-mag, mag)


# --------------------------------------------------------------- pixels -----
def _clamp8(v: int) -> int:
    return 0 if v < 0 else (255 if v > 255 else v)


def _shade(base: tuple[int, int, int], delta: int, alpha: int = 255) -> tuple[int, int, int, int]:
    return (_clamp8(base[0] + delta), _clamp8(base[1] + delta), _clamp8(base[2] + delta), alpha)


def _shade_rgb(base: tuple[int, int, int], dr: int, dg: int, db: int,
               alpha: int = 255) -> tuple[int, int, int, int]:
    return (_clamp8(base[0] + dr), _clamp8(base[1] + dg), _clamp8(base[2] + db), alpha)


def _smooth(t: float) -> float:
    return t * t * (3.0 - 2.0 * t)


def wrapped_noise(rng: Rng, cells: int, lo: int, hi: int) -> list[list[int]]:
    """Tileable bilinear value noise on a 16x16 grid from a wrapped cells x cells lattice."""
    lattice = [[rng.between(lo, hi) for _ in range(cells)] for _ in range(cells)]
    step = TILE_SIZE / cells
    out = [[0] * TILE_SIZE for _ in range(TILE_SIZE)]
    for y in range(TILE_SIZE):
        fy = y / step
        y0 = int(fy) % cells
        y1 = (y0 + 1) % cells
        ty = _smooth(fy - int(fy))
        for x in range(TILE_SIZE):
            fx = x / step
            x0 = int(fx) % cells
            x1 = (x0 + 1) % cells
            tx = _smooth(fx - int(fx))
            a = lattice[y0][x0] + (lattice[y0][x1] - lattice[y0][x0]) * tx
            b = lattice[y1][x0] + (lattice[y1][x1] - lattice[y1][x0]) * tx
            out[y][x] = int(round(a + (b - a) * ty))
    return out


# --------------------------------------------------------- pattern families -
def pat_speckle(rng: Rng, base: tuple[int, int, int], alpha: int = 255) -> list[list[RGBA]]:
    rows = []
    for _y in range(TILE_SIZE):
        row = []
        for _x in range(TILE_SIZE):
            d = rng.signed(11)
            roll = rng.below(100)
            if roll < 6:
                d -= 20
            elif roll > 94:
                d += 13
            row.append(_shade(base, d, alpha))
        rows.append(row)
    return rows


def pat_bricks(rng: Rng, base: tuple[int, int, int], alpha: int = 255,
               course: int = 8, height: int = 4) -> list[list[RGBA]]:
    rows = []
    for y in range(TILE_SIZE):
        offset = (course // 2) if (y // height) % 2 else 0
        row = []
        for x in range(TILE_SIZE):
            seam = (y % height == height - 1) or ((x + offset) % course == course - 1)
            row.append(_shade(base, -32 if seam else rng.signed(9), alpha))
        rows.append(row)
    return rows


def pat_pebbles(rng: Rng, base: tuple[int, int, int], alpha: int = 255,
                count: int = 8) -> list[list[RGBA]]:
    pebbles = [
        (rng.below(TILE_SIZE), rng.below(TILE_SIZE), rng.between(2, 3), rng.between(-14, 18))
        for _ in range(count)
    ]
    rows = []
    for y in range(TILE_SIZE):
        row = []
        for x in range(TILE_SIZE):
            d = rng.signed(6)
            for cx, cy, radius, tone in pebbles:
                dx = abs(x - cx)
                dx = min(dx, TILE_SIZE - dx)
                dy = abs(y - cy)
                dy = min(dy, TILE_SIZE - dy)
                dist = dx * dx + dy * dy
                if dist <= radius * radius:
                    d = tone
                elif dist <= (radius + 1) * (radius + 1):
                    d = -26
            row.append(_shade(base, d, alpha))
        rows.append(row)
    return rows


def pat_strata(rng: Rng, base: tuple[int, int, int], alpha: int = 255,
               period: int = 5, width: int = 2) -> list[list[RGBA]]:
    rows = []
    for y in range(TILE_SIZE):
        row = []
        for x in range(TILE_SIZE):
            band = (x + y) % period
            d = -17 if band < width else (9 if band == period - 1 else 0)
            row.append(_shade(base, d + rng.signed(6), alpha))
        rows.append(row)
    return rows


def pat_dirt_strata(rng: Rng, base: tuple[int, int, int], alpha: int = 255) -> list[list[RGBA]]:
    rows = []
    for y in range(TILE_SIZE):
        band = -10 if (y // 3) % 2 == 0 else 9
        row = []
        for _x in range(TILE_SIZE):
            d = band + rng.signed(7)
            if rng.below(100) < 5:
                d -= 24
            row.append(_shade(base, d, alpha))
        rows.append(row)
    return rows


def pat_clumps(rng: Rng, base: tuple[int, int, int], alpha: int = 255,
               cells: int = 4, lo: int = -16, hi: int = 12) -> list[list[RGBA]]:
    field = wrapped_noise(rng, cells, lo, hi)
    rows = []
    for y in range(TILE_SIZE):
        row = []
        for x in range(TILE_SIZE):
            d = field[y][x] + rng.signed(4)
            if rng.below(100) < 5:
                d -= 22
            row.append(_shade(base, d, alpha))
        rows.append(row)
    return rows


def pat_boards(rng: Rng, base: tuple[int, int, int], alpha: int = 255,
               board: int = 4) -> list[list[RGBA]]:
    rows = []
    for y in range(TILE_SIZE):
        seam = y % board == board - 1
        row = []
        for x in range(TILE_SIZE):
            d = -30 if seam else rng.signed(7)
            if not seam and x % 7 == 0:
                d -= 8
            row.append(_shade(base, d, alpha))
        rows.append(row)
    return rows


def pat_grain(rng: Rng, base: tuple[int, int, int], alpha: int = 255,
              period: int = 5) -> list[list[RGBA]]:
    rows = []
    for _y in range(TILE_SIZE):
        row = []
        for x in range(TILE_SIZE):
            row.append(_shade(base, -14 if x % period == 0 else rng.signed(8), alpha))
        rows.append(row)
    return rows


def pat_tufts(rng: Rng, base: tuple[int, int, int], alpha: int = 255) -> list[list[RGBA]]:
    grid = [[_shade(base, rng.signed(13), alpha) for _ in range(TILE_SIZE)] for _ in range(TILE_SIZE)]
    for x in range(0, TILE_SIZE, 2):
        start = rng.below(TILE_SIZE)
        for i in range(rng.between(3, 7)):
            y = (start + i) % TILE_SIZE
            dr, dg, db = (12, 16, 6) if i % 2 == 0 else (-10, -14, -6)
            grid[y][x] = _shade_rgb(base, dr, dg, db, alpha)
    return grid


def pat_foliage(rng: Rng, base: tuple[int, int, int], alpha: int = 255) -> list[list[RGBA]]:
    rows = []
    for _y in range(TILE_SIZE):
        row = []
        for _x in range(TILE_SIZE):
            roll = rng.below(100)
            d = rng.signed(20)
            if roll < 8:
                d -= 30
            elif roll > 93:
                d += 18
            row.append(_shade(base, d, alpha))
        rows.append(row)
    return rows


def pat_ripple(rng: Rng, base: tuple[int, int, int], alpha: int = 255) -> list[list[RGBA]]:
    rows = []
    for y in range(TILE_SIZE):
        row = []
        for x in range(TILE_SIZE):
            angle = 2.0 * math.pi * (x / 8.0 + y / 8.0)
            d = int(round(8.0 * math.sin(angle)))
            if ((x // 4) + (y // 4)) % 2 == 0:
                d += 7
            row.append(_shade(base, d + rng.signed(4), alpha))
        rows.append(row)
    return rows


def pat_glass(rng: Rng, base: tuple[int, int, int], alpha: int = 110) -> list[list[RGBA]]:
    rows = []
    for y in range(TILE_SIZE):
        row = []
        for x in range(TILE_SIZE):
            border = x == 0 or y == 0 or x == TILE_SIZE - 1 or y == TILE_SIZE - 1
            if border:
                row.append(_shade(base, 26, 255))
            elif (x + y) % TILE_SIZE == 7:
                row.append(_shade(base, 22, min(230, alpha + 90)))
            else:
                row.append(_shade(base, rng.signed(5), alpha))
        rows.append(row)
    return rows


PATTERNS = {
    "speckle": pat_speckle,
    "bricks": pat_bricks,
    "pebbles": pat_pebbles,
    "strata": pat_strata,
    "dirt_strata": pat_dirt_strata,
    "clumps": pat_clumps,
    "boards": pat_boards,
    "grain": pat_grain,
    "tufts": pat_tufts,
    "foliage": pat_foliage,
    "ripple": pat_ripple,
    "glass": pat_glass,
}

KEY_STYLES = {
    "grass":       ((92, 150, 66),   "tufts"),
    "sand":        ((198, 180, 122), "speckle"),
    "gravel":      ((132, 126, 120), "pebbles"),
    "cobblestone": ((120, 120, 124), "bricks"),
    "planks":      ((158, 112, 68),  "boards"),
    "log":         ((112, 82, 52),   "grain"),
    "leaves":      ((62, 122, 50),   "foliage"),
    "water":       ((58, 104, 182),  "ripple"),
    "glass":       ((190, 214, 228), "glass"),
    "bedrock":     ((74, 74, 78),    "pebbles"),
    "snow":        ((226, 232, 240), "speckle"),
    "ice":         ((150, 202, 226), "glass"),
    "brick":       ((150, 82, 68),   "bricks"),
    "clay":        ((158, 132, 116), "clumps"),
    "moss":        ((78, 122, 58),   "foliage"),
    "obsidian":    ((52, 46, 70),    "clumps"),
}


def stone_variant(index: int, seed: int = SEED) -> list[list[RGBA]]:
    rng = Rng(seed ^ stable_hash("stone", str(index)))
    if index == 0:
        return pat_speckle(rng, STONE_BASE)
    if index == 1:
        return pat_bricks(rng, STONE_BASE)
    if index == 2:
        return pat_strata(rng, STONE_BASE)
    return pat_pebbles(rng, STONE_BASE)


def dirt_variant(index: int, seed: int = SEED) -> list[list[RGBA]]:
    rng = Rng(seed ^ stable_hash("dirt", str(index)))
    if index == 0:
        return pat_speckle(rng, DIRT_BASE)
    if index == 1:
        return pat_dirt_strata(rng, DIRT_BASE)
    return pat_clumps(rng, DIRT_BASE, cells=4, lo=-16, hi=10)


def other_texture(key: str, seed: int = SEED) -> list[list[RGBA]]:
    g = default_pack_module()
    if g is not None and key in g.TILES:
        return g.TILES[key]()  # reuse texturepacks/default art -> identical style, fixed palette
    rng = Rng(seed ^ stable_hash("tile", key))
    style = KEY_STYLES.get(key)
    if style is None:
        digest = hashlib.sha256(key.encode("utf-8")).digest()
        base = (96 + digest[0] % 80, 96 + digest[1] % 80, 96 + digest[2] % 80)
        family = "speckle"
    else:
        base, family = style
    alpha = 110 if family == "glass" else (205 if key == "water" else 255)
    return PATTERNS[family](rng, base, alpha)


# -------------------------------------------------------------------- PNG ---
def _chunk(tag: bytes, data: bytes) -> bytes:
    crc = zlib.crc32(tag + data) & 0xFFFFFFFF
    return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", crc)


def png_bytes(rows: list[list[RGBA]]) -> bytes:
    """Encode RGBA8 rows as a PNG (filter type 0, single IDAT). Deterministic."""
    height = len(rows)
    width = len(rows[0]) if height else 0
    raw = bytearray()
    for row in rows:
        raw.append(0)
        for px in row:
            raw += bytes(px)
    ihdr = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    return (
        b"\x89PNG\r\n\x1a\n"
        + _chunk(b"IHDR", ihdr)
        + _chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        + _chunk(b"IEND", b"")
    )


# ----------------------------------------------------------- .import files --
# Canonical Godot 4.7.2 param block (lossless). Prefer the repo's own constant so the
# companions stay byte-identical to tools/gen_default_pack.py --imports / gen_six_face_demo.py
# (Godot --import then leaves them alone instead of rewriting them).
IMPORT_PARAMS_FALLBACK = (
    "[params]\n\n"
    "compress/mode=0\n"
    "compress/high_quality=false\n"
    "compress/lossy_quality=0.7\n"
    "compress/uastc_level=0\n"
    "compress/rdo_quality_loss=0.0\n"
    "compress/hdr_compression=1\n"
    "compress/normal_map=0\n"
    "compress/channel_pack=0\n"
    "mipmaps/generate=false\n"
    "mipmaps/limit=-1\n"
    "roughness/mode=0\n"
    'roughness/src_normal=""\n'
    "process/channel_remap/red=0\n"
    "process/channel_remap/green=1\n"
    "process/channel_remap/blue=2\n"
    "process/channel_remap/alpha=3\n"
    "process/fix_alpha_border=true\n"
    "process/premult_alpha=false\n"
    "process/normal_map_invert_y=false\n"
    "process/hdr_as_srgb=false\n"
    "process/hdr_clamp_exposure=false\n"
    "process/size_limit=0\n"
    "detect_3d/compress_to=0\n"
)


def default_pack_module():
    """tools/gen_default_pack.py (read-only sibling tool) or None when unavailable."""
    tools_dir = str(Path(__file__).resolve().parent)
    if tools_dir not in sys.path:
        sys.path.insert(0, tools_dir)
    try:
        import gen_default_pack as g  # noqa: N816  (stdlib-only sibling tool)
        return g
    except Exception:
        return None


def canonical_import_params() -> str:
    """gen_default_pack.IMPORT_PARAMS when available, else the identical literal above."""
    g = default_pack_module()
    return g.IMPORT_PARAMS if g is not None else IMPORT_PARAMS_FALLBACK


IMPORT_PARAMS = canonical_import_params()


# Godot-assigned uids after `godot --import` wrote this pack (measured 2026-09-23). Godot
# rewrites any uid it does not already know, so the generator must emit exactly these to
# keep the committed .import files self-consistent (and --verify byte-identical).
GODOT_UIDS = {
    "res://texturepacks/variants-demo/tiles/bedrock.png": "uid://dk5l15e15gm7e",
    "res://texturepacks/variants-demo/tiles/dirt_0.png": "uid://717tu82560qp",
    "res://texturepacks/variants-demo/tiles/dirt_1.png": "uid://7lwvf7r5xgkg",
    "res://texturepacks/variants-demo/tiles/dirt_2.png": "uid://dso5c64p8elve",
    "res://texturepacks/variants-demo/tiles/grass_bottom.png": "uid://cw41ialc1kim4",
    "res://texturepacks/variants-demo/tiles/grass_side.png": "uid://be1mg1l7i1hvk",
    "res://texturepacks/variants-demo/tiles/grass_top.png": "uid://cu41hvl50qfnr",
    "res://texturepacks/variants-demo/tiles/leaves.png": "uid://cuho7krbgudld",
    "res://texturepacks/variants-demo/tiles/missing.png": "uid://bfwh5cdi3hdku",
    "res://texturepacks/variants-demo/tiles/plank.png": "uid://caki6e6pvopp1",
    "res://texturepacks/variants-demo/tiles/sand.png": "uid://dcnnxbocnu6jx",
    "res://texturepacks/variants-demo/tiles/stone_0.png": "uid://ck0j0ncaw44il",
    "res://texturepacks/variants-demo/tiles/stone_1.png": "uid://c4s6orcepojeh",
    "res://texturepacks/variants-demo/tiles/stone_2.png": "uid://dxvs8hcnij68",
    "res://texturepacks/variants-demo/tiles/stone_3.png": "uid://cfn2mv8q6yfog",
    "res://texturepacks/variants-demo/tiles/wood_side.png": "uid://btprmobldm4vf",
    "res://texturepacks/variants-demo/tiles/wood_top.png": "uid://b6qiuwpgpo1bn",
}


def uid_for(res_path: str) -> str:
    """The uid Godot actually assigned; unknown path is a hard error (never a hash guess)."""
    try:
        return GODOT_UIDS[res_path]
    except KeyError:
        raise SystemExit(
            f"uid_for: no Godot uid recorded for {res_path}; "
            "run --import and add the measured uid to GODOT_UIDS"
        ) from None


def import_file(res_path: str) -> bytes:
    file_name = res_path.rsplit("/", 1)[-1]
    digest = hashlib.md5(res_path.encode("utf-8")).hexdigest()
    dest = f"res://.godot/imported/{file_name}-{digest}.ctex"
    text = (
        "[remap]\n\n"
        'importer="texture"\n'
        'type="CompressedTexture2D"\n'
        f'uid="{uid_for(res_path)}"\n'
        f'path="{dest}"\n'
        "metadata={\n"
        '"vram_texture": false\n'
        "}\n\n"
        "[deps]\n\n"
        f'source_file="{res_path}"\n'
        f'dest_files=["{dest}"]\n\n'
        + IMPORT_PARAMS
    )
    return text.encode("utf-8")


# ------------------------------------------------------------ pack build ----
def build_pack(keys: list[str], seed: int = SEED) -> dict[str, bytes]:
    """Return {rel_posix_path: bytes} for every file in the pack."""
    files: dict[str, bytes] = {}
    tiles: dict[str, object] = {}

    ordered = [k for k in VARIANT_KEYS if k in keys]
    ordered += [k for k in sorted(keys) if k not in VARIANT_KEYS]

    for key in ordered:
        if key == "stone":
            paths = []
            for i in range(STONE_VARIANTS):
                rel = f"tiles/stone_{i}.png"
                files[rel] = png_bytes(stone_variant(i, seed))
                paths.append(rel)
            tiles[key] = paths
        elif key == "dirt":
            paths = []
            for i in range(DIRT_VARIANTS):
                rel = f"tiles/dirt_{i}.png"
                files[rel] = png_bytes(dirt_variant(i, seed))
                paths.append(rel)
            tiles[key] = paths
        else:
            rel = f"tiles/{key}.png"
            files[rel] = png_bytes(other_texture(key, seed))
            tiles[key] = rel

    pack = {"version": PACK_VERSION, "tile_size": TILE_SIZE, "tiles": tiles}
    files["pack.json"] = (json.dumps(pack, indent=2, ensure_ascii=False) + "\n").encode("utf-8")

    for rel in [r for r in files if r.endswith(".png")]:
        files[rel + ".import"] = import_file(RES_PREFIX + rel)
    return files


def load_keys() -> list[str]:
    for candidate in (DEFAULT_PACK, FALLBACK_PACK):
        if candidate.is_file():
            try:
                data = json.loads(candidate.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                continue
            tiles = data.get("tiles") if isinstance(data, dict) else None
            if isinstance(tiles, dict) and tiles:
                return list(tiles.keys())
    return list(FALLBACK_KEYS)


# --------------------------------------------------------------- io/verify --
def pack_digest(files: dict[str, bytes]) -> str:
    """One hash over the sorted (path, content-hash) list of every pack file."""
    digest = hashlib.sha256()
    for rel in sorted(files):
        digest.update(rel.encode("utf-8") + b"\0" + hashlib.sha256(files[rel]).digest())
    return digest.hexdigest()


def write_files(root: Path, files: dict[str, bytes]) -> None:
    for rel, blob in files.items():
        path = root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(blob)


def relative_files(root: Path) -> list[str]:
    return sorted(p.relative_to(root).as_posix() for p in root.rglob("*") if p.is_file())


def cmd_verify(out_dir: Path, keys: list[str], seed: int) -> int:
    if not out_dir.is_dir():
        print(f"verify: FAIL - {out_dir} does not exist", file=sys.stderr)
        return 1
    files = build_pack(keys, seed)
    with tempfile.TemporaryDirectory(prefix="variants-demo-verify-") as tmp:
        tmp_dir = Path(tmp)
        write_files(tmp_dir, files)
        existing = relative_files(out_dir)
        fresh = relative_files(tmp_dir)
        missing = sorted(set(fresh) - set(existing))
        extra = sorted(set(existing) - set(fresh))
        mismatched = [
            rel for rel in fresh
            if rel not in missing and (out_dir / rel).read_bytes() != (tmp_dir / rel).read_bytes()
        ]
    if missing or extra or mismatched:
        for rel in missing:
            print(f"verify: MISSING {rel}", file=sys.stderr)
        for rel in extra:
            print(f"verify: EXTRA   {rel}", file=sys.stderr)
        for rel in mismatched:
            print(f"verify: DIFFER  {rel}", file=sys.stderr)
        print(f"verify: FAIL - {len(missing) + len(extra) + len(mismatched)} problem(s)", file=sys.stderr)
        return 1
    print(f"verify: OK - {len(fresh)} files regenerated byte-identical to {out_dir}")
    print(f"verify: pack digest sha256={pack_digest(files)}")
    return 0


# ---------------------------------------------------------- contact sheet ---
SHEET_SCALE = 4  # nearest-neighbour magnification of every 16x16 tile
SHEET_PAD = 8
SHEET_CELL_W = 104
SHEET_CELL_H = 90
SHEET_BG = (24, 24, 28, 255)
SHEET_BORDER = (72, 72, 80, 255)
SHEET_LABEL = (216, 216, 222, 255)
SHEET_INDEX = (255, 236, 120, 255)
SHEET_SHADOW = (0, 0, 0, 255)
SHEET_CHECKER = ((108, 108, 114, 255), (148, 148, 154, 255))

# frozen key order (layer order): stone 0-3, dirt 4-6, then the ten singles.
SHEET_LAYOUT = (
    ("stone_0", "stone_1", "stone_2", "stone_3"),
    ("dirt_0", "dirt_1", "dirt_2"),
    ("grass_top", "grass_side", "grass_bottom", "sand", "wood_side", "wood_top"),
    ("plank", "leaves", "bedrock", "missing"),
)

FONT = {
    "0": ("111", "101", "101", "101", "111"),
    "1": ("010", "110", "010", "010", "111"),
    "2": ("111", "001", "111", "100", "111"),
    "3": ("111", "001", "111", "001", "111"),
    "4": ("101", "101", "111", "001", "001"),
    "5": ("111", "100", "111", "001", "111"),
    "6": ("111", "100", "111", "101", "111"),
    "7": ("111", "001", "001", "001", "001"),
    "8": ("111", "101", "111", "101", "111"),
    "9": ("111", "101", "111", "001", "111"),
    "a": ("011", "001", "011", "101", "011"),
    "b": ("100", "110", "101", "101", "110"),
    "c": ("011", "100", "100", "100", "011"),
    "d": ("001", "011", "101", "101", "011"),
    "e": ("011", "100", "110", "100", "011"),
    "f": ("011", "100", "110", "100", "100"),
    "g": ("011", "101", "011", "001", "110"),
    "h": ("100", "110", "101", "101", "101"),
    "i": ("010", "000", "010", "010", "010"),
    "j": ("001", "000", "001", "001", "010"),
    "k": ("100", "101", "110", "101", "101"),
    "l": ("110", "010", "010", "010", "011"),
    "m": ("000", "101", "111", "101", "101"),
    "n": ("000", "110", "101", "101", "101"),
    "o": ("000", "010", "101", "101", "010"),
    "p": ("000", "110", "101", "110", "100"),
    "q": ("000", "011", "101", "011", "001"),
    "r": ("000", "101", "110", "100", "100"),
    "s": ("000", "011", "100", "001", "110"),
    "t": ("010", "111", "010", "010", "011"),
    "u": ("000", "101", "101", "101", "011"),
    "v": ("000", "101", "101", "010", "010"),
    "w": ("000", "101", "101", "111", "101"),
    "x": ("101", "101", "010", "101", "101"),
    "y": ("000", "101", "101", "011", "110"),
    "z": ("000", "111", "001", "110", "111"),
    "_": ("000", "000", "000", "000", "111"),
    "?": ("111", "001", "011", "000", "010"),
}


def read_png_rows(path: Path) -> list[list[RGBA]]:
    """Decode one of our own PNGs (RGBA8, filter 0) back to pixel rows."""
    data = path.read_bytes()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError(f"{path.name}: not a PNG")
    pos, idat, info = 8, b"", None
    while pos + 12 <= len(data):
        size = struct.unpack(">I", data[pos:pos + 4])[0]
        tag = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + size]
        crc = struct.unpack(">I", data[pos + 8 + size:pos + 12 + size])[0]
        if crc != zlib.crc32(tag + body) & 0xFFFFFFFF:
            raise ValueError(f"{path.name}: bad CRC in {tag!r}")
        if tag == b"IHDR":
            info = struct.unpack(">IIBBBBB", body)
        elif tag == b"IDAT":
            idat += body
        elif tag == b"IEND":
            break
        pos += 12 + size
    if info is None:
        raise ValueError(f"{path.name}: no IHDR")
    w, h, depth, ctype, _comp, _filt, interlace = info
    if (w, h, depth, ctype, interlace) != (TILE_SIZE, TILE_SIZE, 8, 6, 0):
        raise ValueError(f"{path.name}: expected 16x16 RGBA8 non-interlaced, got {info}")
    raw = zlib.decompress(idat)
    stride = w * 4
    rows = []
    for y in range(h):
        off = y * (stride + 1)
        if raw[off] != 0:
            raise ValueError(f"{path.name}: row {y} filter {raw[off]} (only filter 0 supported)")
        line = raw[off + 1:off + 1 + stride]
        rows.append([tuple(line[i * 4:i * 4 + 4]) for i in range(w)])
    return rows


def _put(canvas, x: int, y: int, colour) -> None:
    if 0 <= y < len(canvas) and 0 <= x < len(canvas[y]):
        canvas[y][x] = colour


def _blit_text(canvas, x: int, y: int, text: str, scale: int, colour) -> None:
    cx = x
    for ch in text:
        glyph = FONT.get(ch, FONT["?"])
        for gy in range(5):
            for gx in range(3):
                if glyph[gy][gx] != "1":
                    continue
                for sy in range(scale):
                    for sx in range(scale):
                        _put(canvas, cx + gx * scale + sx, y + gy * scale + sy, colour)
        cx += 4 * scale


def _blit_rect(canvas, x: int, y: int, w: int, h: int, colour) -> None:
    for i in range(w):
        _put(canvas, x + i, y, colour)
        _put(canvas, x + i, y + h - 1, colour)
    for i in range(h):
        _put(canvas, x, y + i, colour)
        _put(canvas, x + w - 1, y + i, colour)


def _composite(px, bg):
    alpha = px[3]
    if alpha == 255:
        return px
    if alpha == 0:
        return bg
    return tuple((px[i] * alpha + bg[i] * (255 - alpha)) // 255 for i in range(3)) + (255,)


def _blit_tile(canvas, rows, x0: int, y0: int, tile_px: int) -> None:
    for py in range(tile_px):
        src = rows[py // SHEET_SCALE]
        for px in range(tile_px):
            background = SHEET_CHECKER[((px // SHEET_SCALE) // 4 + (py // SHEET_SCALE) // 4) % 2]
            canvas[y0 + py][x0 + px] = _composite(src[px // SHEET_SCALE], background)


def cmd_sheet(out_dir: Path) -> int:
    tiles = {}
    for row in SHEET_LAYOUT:
        for key in row:
            path = out_dir / "tiles" / (key + ".png")
            if not path.is_file():
                print(f"sheet: FAIL - {path} missing (run the generator first)", file=sys.stderr)
                return 1
            try:
                tiles[key] = read_png_rows(path)
            except (OSError, ValueError, zlib.error) as exc:
                print(f"sheet: FAIL - {exc}", file=sys.stderr)
                return 1

    cols = max(len(row) for row in SHEET_LAYOUT)
    tile_px = TILE_SIZE * SHEET_SCALE
    width = SHEET_PAD * 2 + cols * SHEET_CELL_W
    height = SHEET_PAD * 2 + len(SHEET_LAYOUT) * SHEET_CELL_H
    canvas = [[SHEET_BG] * width for _ in range(height)]

    layer = 0
    for r, row in enumerate(SHEET_LAYOUT):
        for c, key in enumerate(row):
            x0 = SHEET_PAD + c * SHEET_CELL_W + (SHEET_CELL_W - tile_px) // 2
            y0 = SHEET_PAD + r * SHEET_CELL_H
            _blit_tile(canvas, tiles[key], x0, y0, tile_px)
            _blit_rect(canvas, x0 - 1, y0 - 1, tile_px + 2, tile_px + 2, SHEET_BORDER)
            label = key
            label_w = 4 * SHEET_SCALE * len(label) - SHEET_SCALE
            _blit_text(canvas, x0 + (tile_px - label_w) // 2, y0 + tile_px + 5,
                       label, SHEET_SCALE, SHEET_LABEL)
            _blit_text(canvas, x0 + 2, y0 + 2, str(layer), 2, SHEET_SHADOW)
            _blit_text(canvas, x0 + 1, y0 + 1, str(layer), 2, SHEET_INDEX)
            layer += 1

    SHEET_PATH.parent.mkdir(parents=True, exist_ok=True)
    SHEET_PATH.write_bytes(png_bytes(canvas))
    print(f"sheet: wrote {SHEET_PATH} ({width}x{height}px, {len(tiles)} tiles, x{SHEET_SCALE} nearest)")
    print("layout = layer order (corner number = array layer under the design slot rule):")
    for row in SHEET_LAYOUT:
        print("  " + "  ".join(row))
    return 0


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--verify", action="store_true",
                        help="regenerate to a temp dir and assert byte-identical to existing pack")
    parser.add_argument("--sheet", action="store_true",
                        help="build .pi/variants/tiles-contact-sheet.png from the written PNGs")
    parser.add_argument("--out", type=Path, default=OUT_DIR,
                        help="output directory (default: texturepacks/variants-demo)")
    parser.add_argument("--seed", type=lambda s: int(s, 0), default=SEED,
                        help="deterministic seed (fixed by design; only for experiments)")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    keys = load_keys()
    if "stone" not in keys or "dirt" not in keys:
        print(f"error: frozen key set must contain stone+dirt; got {keys}", file=sys.stderr)
        return 2
    if len(keys) != 12:
        print(f"warning: expected 12 frozen tile keys, got {len(keys)}", file=sys.stderr)

    if args.verify:
        return cmd_verify(args.out, keys, args.seed)
    if args.sheet:
        return cmd_sheet(args.out)

    files = build_pack(keys, args.seed)
    write_files(args.out, files)
    print(f"wrote {len(files)} files to {args.out} (seed=0x{args.seed:x}, keys={len(keys)})")
    print(f"pack digest sha256={pack_digest(files)}")
    for rel in sorted(r for r in files if r.endswith(".png")):
        print(f"  {rel} sha256={hashlib.sha256(files[rel]).hexdigest()[:16]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
