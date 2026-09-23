#!/usr/bin/env python3
"""Deterministic generator for texturepacks/sizes-demo/ (Regress Phase 2: size classes).

Pure Python 3 standard library only. The pack is a single-class demo everywhere except two
tiles, which carry their own native-resolution size class:

    stone, dirt, grass_*, wood_*, plank, bedrock, missing   16x16  (class 0 = tile_size)
    sand                                                    64x64  (class 1)
    leaves                                                 256x256 (class 2)

Class indices follow design.md P2.1: class 0 is the tile_size class, the rest get 1..C-1 in
first-appearance order over the frozen key scan (sand is key 6, leaves key 10 of 12). The
loader's mixed-pack VRAM line should therefore report three classes: 16x16: 10 layers,
64x64: 1 layer, 256x256: 1 layer.

The ten 16x16 tiles are the texturepacks/default art (read-only reuse of tools/gen_default_pack.py);
sand/leaves are generated at their native size so the extra resolution is real detail, not an
upscale.

PNG encoding, the splitmix64 RNG and the contact-sheet helpers are imported from the Phase 1
sibling tools/gen_variants_demo.py (same deterministic contract, no duplicated encoder).

uid handling (design.md P2.5 E14): every `.import` companion is generated with a deterministic
placeholder `uid://` and the canonical `gen_default_pack.IMPORT_PARAMS` block. Godot rewrites the
uid on its first `--import`, so the flow is:

    python tools/gen_sizes_demo.py                 # write pack (placeholder uids)
    godot-mono --path . --import                   # Lead, serial
    python tools/gen_sizes_demo.py --uids          # print the measured uid table
    # paste it into GODOT_UIDS below, then:
    python tools/gen_sizes_demo.py                 # rewrite with the pinned uids
    python tools/gen_sizes_demo.py --verify        # byte-identical

Usage:
    python tools/gen_sizes_demo.py [--out DIR] [--seed N]
    python tools/gen_sizes_demo.py --verify        # regenerate to temp dir, byte-compare
    python tools/gen_sizes_demo.py --uids          # print GODOT_UIDS from the on-disk .import files
    python tools/gen_sizes_demo.py --sheet         # .pi/variants/sizes-contact-sheet.png (4x)
"""

from __future__ import annotations

import argparse
import hashlib
import json
import struct
import sys
import tempfile
import zlib
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
if str(TOOLS) not in sys.path:
    sys.path.insert(0, str(TOOLS))
sys.dont_write_bytecode = True  # keep the tree clean: no tools/__pycache__ from the sibling imports

import gen_default_pack as g  # noqa: E402  canonical IMPORT_PARAMS + the 16x16 art (read-only)
import gen_variants_demo as v  # noqa: E402  splitmix64 + the hand-rolled PNG encoder (Phase 1 sibling)

# --------------------------------------------------------------- contract ---
PACK_VERSION = 1
TILE_SIZE = 16  # the pack's expected/default size -> class 0
SEED = 0x52454752_53535F32  # "REGRSS_2" - fixed by design; never change
KEYS = tuple(g.KEY_ORDER)  # the 12 frozen keys, in frozen (layer) order
CLASS_SIZES = {"sand": 64, "leaves": 256}  # the two non-default size classes
EXPECTED_SIZES = (16, 64, 256)

REPO_ROOT = TOOLS.parent
OUT_DIR = REPO_ROOT / "texturepacks" / "sizes-demo"
RES_PREFIX = "res://texturepacks/sizes-demo/"
SHEET_PATH = REPO_ROOT / ".pi" / "variants" / "sizes-contact-sheet.png"

# Godot-assigned uids measured from the on-disk `.import` files after `godot --import` (2026-09-23).
# Godot rewrites any uid it does not already know (the deterministic placeholders below were
# all replaced), so the generator must emit exactly these to keep the pack self-consistent and
# `--verify` byte-identical. Unknown path = hard error, never a hash guess.
GODOT_UIDS: dict[str, str] = {
    "res://texturepacks/sizes-demo/tiles/stone.png": "uid://hqyltbmdq0q",
    "res://texturepacks/sizes-demo/tiles/dirt.png": "uid://dygffpsqjujqe",
    "res://texturepacks/sizes-demo/tiles/grass_top.png": "uid://d3gmq6aikjh5l",
    "res://texturepacks/sizes-demo/tiles/grass_side.png": "uid://cs8na12lucepm",
    "res://texturepacks/sizes-demo/tiles/grass_bottom.png": "uid://bm8g7i0kakfp2",
    "res://texturepacks/sizes-demo/tiles/sand.png": "uid://bwu4tggr2srda",
    "res://texturepacks/sizes-demo/tiles/wood_side.png": "uid://b143vhg5a3dxe",
    "res://texturepacks/sizes-demo/tiles/wood_top.png": "uid://h7w4rp0venti",
    "res://texturepacks/sizes-demo/tiles/plank.png": "uid://ci7i07n5cx5xd",
    "res://texturepacks/sizes-demo/tiles/leaves.png": "uid://0rao11canf1t",
    "res://texturepacks/sizes-demo/tiles/bedrock.png": "uid://blxsqcmbd4vkl",
    "res://texturepacks/sizes-demo/tiles/missing.png": "uid://nwkv4qjtw00q",
}
UID_ALPHABET = "0123456789abcdefghijklmnopqrstuvwxyz"

RGBA = tuple[int, int, int, int]


def tile_size_for(key: str) -> int:
    return CLASS_SIZES.get(key, TILE_SIZE)


# ------------------------------------------------------------------- art ----
def _smooth(t: float) -> float:
    return t * t * (3.0 - 2.0 * t)


def noise_field(rng: v.Rng, size: int, cells: int) -> list[list[float]]:
    """Tileable bilinear value noise in [0,1) on a wrapped cells x cells lattice."""
    lat = [[rng.next_u64() / 2 ** 64 for _ in range(cells)] for _ in range(cells)]
    step = size / cells
    out = [[0.0] * size for _ in range(size)]
    for y in range(size):
        fy = y / step
        y0 = int(fy) % cells
        y1 = (y0 + 1) % cells
        ty = _smooth(fy - int(fy))
        for x in range(size):
            fx = x / step
            x0 = int(fx) % cells
            x1 = (x0 + 1) % cells
            tx = _smooth(fx - int(fx))
            a = lat[y0][x0] + (lat[y0][x1] - lat[y0][x0]) * tx
            b = lat[y1][x0] + (lat[y1][x1] - lat[y1][x0]) * tx
            out[y][x] = a + (b - a) * ty
    return out


def sand_tile(size: int, seed: int) -> list[list[RGBA]]:
    """Pale fine dither at native resolution (default tile_sand style, one octave finer)."""
    rng = v.Rng(seed)
    macro = noise_field(rng, size, 8)
    fine = noise_field(rng, size, size // 4)
    base = g.base("sand")
    rows = []
    for y in range(size):
        row = []
        for x in range(size):
            f = 0.93 + 0.14 * (0.5 * macro[y][x] + 0.5 * fine[y][x])
            f += 0.06 * ((g.BAYER4[y % 4][x % 4] + 0.5) / 16.0 - 0.5)
            if rng.below(100) < 8:
                f *= 0.91  # coarser grains
            row.append(g.mul(base, f) + (255,))
        rows.append(row)
    return rows


def leaves_tile(size: int, seed: int) -> list[list[RGBA]]:
    """Dark green clustered foliage with ~16% cutout holes (default tile_leaves style, native res)."""
    rng = v.Rng(seed)
    macro = noise_field(rng, size, 4)
    mid = noise_field(rng, size, 16)
    fine = noise_field(rng, size, 64)
    dens = [[0.5 * macro[y][x] + 0.3 * mid[y][x] + 0.2 * fine[y][x] for x in range(size)]
            for y in range(size)]
    thr = sorted(d for row in dens for d in row)[int(0.16 * size * size)]
    hole = [[d <= thr for d in row] for row in dens]
    base = g.base("leaves")
    rows = []
    for y in range(size):
        row = []
        for x in range(size):
            if hole[y][x]:
                row.append(g.mul(base, 0.45) + (0,))  # invisible, same as the default tile
                continue
            f = 0.62 + 0.55 * dens[y][x] + 0.10 * fine[y][x]
            if any(hole[(y + dy) % size][(x + dx) % size]
                   for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1))):
                f *= 0.80  # shaded rim around each hole
            if dens[y][x] > 0.72:
                f *= 1.08  # sunlit leaf clusters
            f *= 1.0 + 0.05 * (rng.next_u64() / 2 ** 64 - 0.5)  # per-pixel grain, only visible at 256
            row.append(g.mul(base, f) + (255,))
        rows.append(row)
    return rows


def tile_rows(key: str, seed: int = SEED) -> list[list[RGBA]]:
    if key == "sand":
        return sand_tile(CLASS_SIZES[key], seed ^ v.stable_hash("sizes-demo", key, "64"))
    if key == "leaves":
        return leaves_tile(CLASS_SIZES[key], seed ^ v.stable_hash("sizes-demo", key, "256"))
    return g.TILES[key]()  # 16x16 art shared with texturepacks/default


# ----------------------------------------------------------- .import files --
def placeholder_uid(res_path: str) -> str:
    """Deterministic uid:// stand-in; Godot replaces it on the first --import, then pin it."""
    n = int.from_bytes(hashlib.sha256(("placeholder|" + res_path).encode("utf-8")).digest()[:16], "big")
    out = ""
    while len(out) < 13:
        out += UID_ALPHABET[n % 36]
        n //= 36
    return "uid://" + out


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
        + g.IMPORT_PARAMS
    )
    return text.encode("utf-8")


# ------------------------------------------------------------ pack build ----
def png_dims(blob: bytes) -> tuple[int, int]:
    return struct.unpack(">II", blob[16:24])


def build_pack(keys: tuple[str, ...] = KEYS, seed: int = SEED) -> dict[str, bytes]:
    """Return {rel_posix_path: bytes} for every file in the pack."""
    sizes = {tile_size_for(k) for k in keys}
    assert sizes <= set(EXPECTED_SIZES), f"unexpected size classes {sorted(sizes)}"
    assert len(sizes) <= 4, f"{len(sizes)} size classes exceeds the design cap of 4"

    files: dict[str, bytes] = {}
    tiles: dict[str, str] = {}
    for key in keys:
        rel = f"tiles/{key}.png"
        blob = v.png_bytes(tile_rows(key, seed))
        got = png_dims(blob)
        want = tile_size_for(key)
        assert got == (want, want), f"{key}: wrote {got[0]}x{got[1]}, expected {want}x{want}"
        files[rel] = blob
        tiles[key] = rel

    pack = {"version": PACK_VERSION, "name": "Sizes Demo", "tile_size": TILE_SIZE, "tiles": tiles}
    files["pack.json"] = (json.dumps(pack, indent=2, ensure_ascii=False) + "\n").encode("utf-8")

    for rel in [r for r in files if r.endswith(".png")]:
        files[rel + ".import"] = import_file(RES_PREFIX + rel)
    return files


# --------------------------------------------------------------- io/verify --
def pack_digest(files: dict[str, bytes]) -> str:
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


def cmd_verify(out_dir: Path, seed: int) -> int:
    if not out_dir.is_dir():
        print(f"verify: FAIL - {out_dir} does not exist", file=sys.stderr)
        return 1
    files = build_pack(KEYS, seed)
    with tempfile.TemporaryDirectory(prefix="sizes-demo-verify-") as tmp:
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
        if any(rel.endswith(".import") for rel in mismatched):
            print("verify: hint - .import uid drift after `godot --import`; "
                  "run --uids and pin GODOT_UIDS", file=sys.stderr)
        print(f"verify: FAIL - {len(missing) + len(extra) + len(mismatched)} problem(s)", file=sys.stderr)
        return 1
    print(f"verify: OK - {len(fresh)} files regenerated byte-identical to {out_dir}")
    print(f"verify: pack digest sha256={pack_digest(files)}")
    return 0


def cmd_uids(out_dir: Path) -> int:
    """Print the GODOT_UIDS table measured from the on-disk .import files."""
    entries: dict[str, str] = {}
    for path in sorted(out_dir.rglob("*.png.import")):
        uid = src = None
        for line in path.read_text(encoding="utf-8").splitlines():
            if line.startswith('uid="'):
                uid = line[len('uid="'):-1]
            elif line.startswith('source_file="'):
                src = line[len('source_file="'):-1]
        if uid and src:
            entries[src] = uid
    print(f"# {len(entries)} .import files under {out_dir} (placeholder match = Godot did not rewrite)")
    print("GODOT_UIDS = {")
    for src in sorted(entries):
        note = "  # placeholder - Godot did not rewrite" if entries[src] == placeholder_uid(src) else ""
        print(f'    "{src}": "{entries[src]}",{note}')
    print("}")
    return 0


# ---------------------------------------------------------- contact sheet ---
def decode_png(path: Path) -> tuple[int, list[list[RGBA]]]:
    """Decode one of our own PNGs (RGBA8, filter type 0, non-interlaced) at any square size."""
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
    if w != h or depth != 8 or ctype != 6 or interlace != 0:
        raise ValueError(f"{path.name}: expected square RGBA8 non-interlaced, got {info}")
    raw = zlib.decompress(idat)
    stride = w * 4
    rows = []
    for y in range(h):
        off = y * (stride + 1)
        if raw[off] != 0:
            raise ValueError(f"{path.name}: row {y} filter {raw[off]} (only filter 0 supported)")
        line = raw[off + 1:off + 1 + stride]
        rows.append([tuple(line[i * 4:i * 4 + 4]) for i in range(w)])
    return w, rows


def cmd_sheet(out_dir: Path) -> int:
    groups = [(size, [k for k in KEYS if tile_size_for(k) == size]) for size in EXPECTED_SIZES]
    tiles: dict[str, list[list[RGBA]]] = {}
    for _size, keys in groups:
        for key in keys:
            path = out_dir / "tiles" / (key + ".png")
            if not path.is_file():
                print(f"sheet: FAIL - {path} missing (run the generator first)", file=sys.stderr)
                return 1
            try:
                got, rows = decode_png(path)
            except (OSError, ValueError) as exc:
                print(f"sheet: FAIL - {exc}", file=sys.stderr)
                return 1
            if got != tile_size_for(key):
                print(f"sheet: FAIL - {path.name} is {got}px, expected {tile_size_for(key)}",
                      file=sys.stderr)
                return 1
            tiles[key] = rows

    pad, gap, label_h = 8, 6, 16
    cell = {k: tile_size_for(k) * v.SHEET_SCALE for k in tiles}
    label = {k: f"{k}_{tile_size_for(k)}x{tile_size_for(k)}" for k in tiles}  # '_' not ' ': FONT has no space glyph
    text_w = {k: 8 * len(label[k]) - 2 for k in tiles}  # FONT advance = 4 * scale(2)
    colw = {k: max(cell[k], text_w[k]) for k in tiles}
    width = pad * 2 + max(sum(colw[k] + gap for k in keys) for _s, keys in groups)
    height = pad * 2 + sum(max(cell[k] for k in keys) + label_h + gap for _s, keys in groups)
    canvas = [[(24, 24, 28, 255)] * width for _ in range(height)]

    y = pad
    for size, keys in groups:
        x = pad
        for key in keys:
            v._blit_tile(canvas, tiles[key], x + (colw[key] - cell[key]) // 2, y, cell[key])
            v._blit_rect(canvas, x + (colw[key] - cell[key]) // 2 - 1, y - 1,
                         cell[key] + 2, cell[key] + 2, (72, 72, 80, 255))
            v._blit_text(canvas, x + (colw[key] - text_w[key]) // 2, y + cell[key] + 3,
                         label[key], 2, (216, 216, 222, 255))
            x += colw[key] + gap
        y += max(cell[k] for k in keys) + label_h + gap

    SHEET_PATH.parent.mkdir(parents=True, exist_ok=True)
    SHEET_PATH.write_bytes(v.png_bytes(canvas))
    print(f"sheet: wrote {SHEET_PATH} ({width}x{height}px, x{v.SHEET_SCALE} nearest)")
    for size, keys in groups:
        print(f"  {size}x{size} class {EXPECTED_SIZES.index(size)}: {', '.join(keys)}")
    return 0


# ------------------------------------------------------------------- main ---
def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--verify", action="store_true",
                        help="regenerate to a temp dir and assert byte-identical to the written pack")
    parser.add_argument("--uids", action="store_true",
                        help="print the GODOT_UIDS table measured from the on-disk .import files")
    parser.add_argument("--sheet", action="store_true",
                        help="build .pi/variants/sizes-contact-sheet.png from the written PNGs")
    parser.add_argument("--out", type=Path, default=OUT_DIR,
                        help="output directory (default: texturepacks/sizes-demo)")
    parser.add_argument("--seed", type=lambda s: int(s, 0), default=SEED,
                        help="deterministic seed (fixed by design; only for experiments)")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    if len(KEYS) != 12:
        print(f"warning: expected 12 frozen tile keys, got {len(KEYS)}", file=sys.stderr)
    if args.verify:
        return cmd_verify(args.out, args.seed)
    if args.uids:
        return cmd_uids(args.out)
    if args.sheet:
        return cmd_sheet(args.out)

    files = build_pack(KEYS, args.seed)
    write_files(args.out, files)
    print(f"wrote {len(files)} files to {args.out} (seed=0x{args.seed:x}, keys={len(KEYS)})")
    print(f"pack digest sha256={pack_digest(files)}")
    for key in KEYS:
        size = tile_size_for(key)
        klass = EXPECTED_SIZES.index(size)
        print(f"  tiles/{key}.png {size}x{size} class {klass}")
    print(f"size classes: {len({tile_size_for(k) for k in KEYS})} "
          f"({', '.join(f'{s}x{s}' for s in EXPECTED_SIZES)})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
