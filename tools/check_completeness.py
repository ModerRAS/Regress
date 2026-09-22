#!/usr/bin/env python3
"""Completeness check for the v1 texture-pack Block+Face -> tile-key spec.

Parses the block enum and the face constants out of src/Blocks.cs, parses the mapping
table out of completeness.md, and asserts:

  (a) every Block except Air x every Face index 0..5 appears exactly once in the table
  (b) every tile key used is one of the 12 frozen canonical keys
  (c) table row count == 48
  (d) the TileIndex column equals the frozen index for that key
  (e) the key matches the frozen per-block mapping rule
  (f) ChunkMesher.Dirs order matches the Face constants (see EXPECTED_DIRS)

Python 3 stdlib only.  Exit 0 = exhaustive; non-zero + message on stderr otherwise.

    python tools/check_completeness.py [repo_root] [--table path/to/doc.md]

`--table` points the checker at the file holding the 4-column Block+Face mapping table;
it defaults to docs/texture-packs.md.  Other tables in the same file are ignored.
"""

import re
import sys
from pathlib import Path

# Frozen spec.  Keys are never renumbered.
TILE_KEYS = {
    "stone": 0, "dirt": 1, "grass_top": 2, "grass_side": 3, "grass_bottom": 4, "sand": 5,
    "wood_side": 6, "wood_top": 7, "plank": 8, "leaves": 9, "bedrock": 10, "missing": 11,
}
# A face name means a cube side, so it pins the direction vector the mesher must iterate.
EXPECTED_DIRS = {
    "PosX": (1, 0, 0), "NegX": (-1, 0, 0), "Top": (0, 1, 0),
    "Bottom": (0, -1, 0), "PosZ": (0, 0, 1), "NegZ": (0, 0, -1),
}
UNIFORM = {"Stone": "stone", "Dirt": "dirt", "Sand": "sand",
           "Plank": "plank", "Leaves": "leaves", "Bedrock": "bedrock"}
EXPECTED_ROWS = 48

FAILS = []


def check(cond, msg):
    if not cond:
        FAILS.append(msg)
    return cond


def rule_key(block, face_name):
    """The frozen mapping rule, expressed over face names rather than indices."""
    if block == "Grass":
        return {"Top": "grass_top", "Bottom": "grass_bottom"}.get(face_name, "grass_side")
    if block == "Wood":
        return "wood_top" if face_name in ("Top", "Bottom") else "wood_side"
    return UNIFORM.get(block)


def parse_blocks_cs(path):
    src = path.read_text(encoding="utf-8")
    src = re.sub(r"//[^\n]*", "", src)  # strip line comments before regexing

    m = re.search(r"enum\s+Block\s*:\s*byte\s*\{(.*?)\}", src, re.S)
    if not m:
        sys.exit(f"FATAL: no `enum Block : byte {{...}}` found in {path}")
    blocks = []
    for line in m.group(1).splitlines():
        line = line.strip().rstrip(",").strip()
        if not line:
            continue
        name = re.match(r"(\w+)\s*(?:=\s*(\d+))?$", line)
        if not name:
            sys.exit(f"FATAL: cannot parse Block member {line!r}")
        value = int(name.group(2)) if name.group(2) is not None else len(blocks)
        check(value == len(blocks),
              f"BLOCK VALUES: Block.{name.group(1)} is {value}, expected {len(blocks)} "
              f"(mapping assumes contiguous enum from 0)")
        blocks.append(name.group(1))

    m = re.search(r"class\s+Face\s*\{(.*?)\}", src, re.S)
    if not m:
        sys.exit(f"FATAL: no `class Face {{...}}` found in {path}")
    faces = {n: int(v) for n, v in re.findall(r"(\w+)\s*=\s*(\d+)", m.group(1))}
    check(len(faces) == 6, f"FACE CONSTS: found {len(faces)} face constants, expected 6")
    check(sorted(faces.values()) == list(range(6)),
          f"FACE CONSTS: indices must be 0..5, got {sorted(faces.values())}")
    check(set(faces) == set(EXPECTED_DIRS),
          f"FACE CONSTS: names {sorted(faces)} != expected {sorted(EXPECTED_DIRS)}")

    return blocks, faces


def parse_dirs(path):
    src = re.sub(r"//[^\n]*", "", path.read_text(encoding="utf-8"))
    m = re.search(r"Vector3I\[\]\s+Dirs\s*=\s*\{(.*?)\}\s*;", src, re.S)
    if not m:
        sys.exit(f"FATAL: no `Vector3I[] Dirs = {{...}}` found in {path}")
    return [(int(a), int(b), int(c))
            for a, b, c in re.findall(r"new\(\s*(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)\s*\)", m.group(1))]


def parse_table(md_path, block_names, face_names):
    """Rows are `| Block | FaceName | TileKey | TileIndex |`; other tables are ignored."""
    rows = []
    for lineno, line in enumerate(md_path.read_text(encoding="utf-8").splitlines(), 1):
        if not line.startswith("|"):
            continue
        cells = [c.strip().strip("`").strip() for c in line.strip().strip("|").split("|")]
        if len(cells) != 4 or set(cells[1]) <= {"-"} or cells[0] == "Block":
            continue
        if cells[0] not in block_names and cells[0] not in face_names:
            continue
        rows.append((cells[0], cells[1], cells[2], cells[3], lineno))
    return rows


def main():
    args = sys.argv[1:]
    table = None
    if "--table" in args:
        i = args.index("--table")
        if i + 1 >= len(args):
            sys.exit("FATAL: --table needs a path")
        table = Path(args[i + 1])
        del args[i:i + 2]
    root = Path(args[0]).resolve() if args else Path(__file__).resolve().parents[1]
    blocks_cs, mesher_cs = root / "src" / "Blocks.cs", root / "src" / "ChunkMesher.cs"
    md = (table if table and table.is_absolute() else root / (table or "docs/texture-packs.md"))
    for p in (blocks_cs, mesher_cs, md):
        if not p.is_file():
            sys.exit(f"FATAL: missing {p}")

    blocks, faces = parse_blocks_cs(blocks_cs)
    dirs = parse_dirs(mesher_cs)

    # (f) the mesher iterates faces in the order the Face constants declare.
    check(len(dirs) == 6, f"DIRS: ChunkMesher.Dirs has {len(dirs)} entries, expected 6")
    for name, idx in faces.items():
        if idx < len(dirs) and dirs[idx] != EXPECTED_DIRS[name]:
            FAILS.append(f"FACE ORDER: ChunkMesher.Dirs[{idx}] is {dirs[idx]} but "
                         f"Face.{name} == {idx} requires {EXPECTED_DIRS[name]}")

    renderable = [b for b in blocks if b != "Air"]
    seen = {}
    for block, face_name, tile_key, tile_idx, lineno in parse_table(md, blocks, faces):
        where = f"{md.name}:{lineno} ({block}.{face_name})"
        if block == "Air":
            FAILS.append(f"AIR ROW: {where} - Air is never rendered and must not appear")
            continue
        if block not in blocks:
            FAILS.append(f"UNKNOWN BLOCK: {where} is not in the Block enum")
            continue
        if face_name not in faces:
            FAILS.append(f"UNKNOWN FACE: {where} is not a Face constant")
            continue
        # (a) uniqueness on the *source* face index, not the printed name.
        cell = (block, faces[face_name])
        seen[cell] = seen.get(cell, 0) + 1
        # (b) canonical keys only.
        if tile_key not in TILE_KEYS:
            FAILS.append(f"NON-CANONICAL KEY: {where} uses {tile_key!r}, "
                         f"which is not one of the 12 frozen keys")
            continue
        # (d) index column must equal the frozen index.
        if not re.fullmatch(r"\d+", tile_idx or ""):
            FAILS.append(f"BAD INDEX: {where} tile index {tile_idx!r} is not an integer")
        elif int(tile_idx) != TILE_KEYS[tile_key]:
            FAILS.append(f"WRONG INDEX: {where} says {tile_idx} for {tile_key!r}, "
                         f"frozen index is {TILE_KEYS[tile_key]}")
        # (e) rule check.
        want = rule_key(block, face_name)
        if want != tile_key:
            FAILS.append(f"RULE VIOLATION: {where} maps to {tile_key!r}, "
                         f"frozen rule requires {want!r}")

    rows = parse_table(md, blocks, faces)
    # (c) row count.
    check(len(rows) == EXPECTED_ROWS,
          f"ROW COUNT: parsed {len(rows)} rows from {md.name}, expected {EXPECTED_ROWS}")

    for block in renderable:
        for name, idx in faces.items():
            n = seen.get((block, idx), 0)
            if n != 1:
                FAILS.append(f"{'MISSING' if n == 0 else 'DUPLICATE'} CELL: {block}.{name} "
                             f"(Face index {idx}) appears {n} time(s), expected exactly 1")

    used = {k for _, _, k, _, _ in rows if k in TILE_KEYS}
    check("missing" not in used,
          "RESERVED KEY: `missing` is the fallback tile and must not be referenced by any row")
    unused = [k for k, _ in TILE_KEYS.items() if k not in used]
    check(unused == ["missing"],
          f"UNUSED KEYS: expected only ['missing'] to be unused, got {unused}")

    print(f"source      : {blocks_cs}")
    print(f"mesher      : {mesher_cs}")
    print(f"table       : {md}")
    print(f"blocks      : {len(blocks)} ({', '.join(blocks)})")
    print(f"renderable  : {len(renderable)} ({', '.join(renderable)})")
    print(f"faces       : {', '.join(f'{n}={i}' for n, i in sorted(faces.items(), key=lambda x: x[1]))}")
    print(f"cells       : {len(rows)} rows / {len(renderable) * 6} required")
    print(f"keys        : {len(TILE_KEYS)} canonical, {len(used)} referenced, "
          f"unused={unused or 'none'}")
    if FAILS:
        print(f"\nFAIL ({len(FAILS)} problem(s)):")
        for f in FAILS:
            print(f"  - {f}")
        return 1
    print("\nPASS: every renderable Block x Face 0..5 maps to exactly one canonical tile key "
          "with the frozen index.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
