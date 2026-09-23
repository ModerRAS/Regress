# Six-Face Demo pack

Every cube face gets its own visibly different 16x16 tile: the 12 frozen base keys plus the
per-face overrides (`<block>_posx` … `<block>_negz`). Nine of the twelve base tiles are
byte-identical to `texturepacks/default`; the overrides change them face by face.

## Key-space collision (57 files, not 60)

The 12 base keys and the 48 face keys share one JSON namespace, and `src/TexPack.cs` resolves
base keys **first** (`Array.IndexOf(Keys, k)` before `FaceKeys`). Three face names are also
frozen base names — `grass_top`, `grass_bottom`, `wood_top` — so they can never address their
face cell. The pack therefore ships **57 distinct PNGs** (12 base + 45 standalone overrides);
the loader prints **45/48 per-face overrides**, and a 48/48 count is impossible without a
`src/` naming change (not made here). The three shadowed base PNGs are generated **with the
tone step and motif of the face they render on** (grass top = `top` treatment, grass bottom =
`bottom`, wood top = `top`), so all 48 block+face cells still look distinct.

Also, because base lookup wins, these three keys were omitted from the override section of
`pack.json`; their base entries carry the art.

## Art scheme (v1, direction-agnostic)

Per-face UV orientation is **not** controlled by the renderer, so the art uses no letters,
arrows or readable glyphs — a corner mark would arrive mirrored. Each face instead gets:

1. **A tone step** (primary signal) — the base tile's RGB multiplied by a clearly separated
   factor: `posx 0.62`, `negx 0.78`, `top 1.30`, `bottom 0.50`, `posz 0.95`, `negz 1.12`.
   Alpha is untouched, so leaves keep their ~16% transparent holes.
2. **A D4-tolerant motif** (secondary signal) — recognisable under mirroring and 90-degree
   rotation: `posx` 1 px border frame, `negx` centre cross, `top` centre dot, `bottom`
   4-dot grid, `posz` 8x8 quadrant checker, `negz` corner squares. Motifs are drawn lighter
   on the dark tone steps and darker on the light ones so they stay legible either way.

Every block keeps its material look (stone grey, sand pale, wood brown, …): the face tiles
are derived from the very tile that block+face shows in the default pack.

## Regenerate

```sh
python tools/gen_six_face_demo.py            # 57 PNGs (12 base + 45 overrides) + pack.json
python tools/gen_six_face_demo.py --verify   # decode, assert 16x16 RGBA8 + opacity + keys
python tools/gen_six_face_demo.py --imports  # the 57 Godot .import companions
python tools/gen_six_face_demo.py --sheet    # contact sheet under .pi/sixface/ (uncommitted)
```
