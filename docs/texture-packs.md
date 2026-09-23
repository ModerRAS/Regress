# Texture packs

A texture pack is a **directory** containing a `pack.json` manifest and the PNGs it names — no
archive, no second manifest name, no in-engine download. Twelve base tile keys and 54 optional
per-face override keys are the entire vocabulary, and their order is frozen because it fixes the
baseline layer layout the renderer receives (variants shift layers, never key order — see
"Texture variants").
The pack supplies art; the game supplies the mapping from `Block` + `Face` to a key, and nothing
else.

Adding a new block: see [adding-a-block.md](adding-a-block.md).

v1 renders every chunk through a single `Texture2DArray` and a single `ShaderMaterial`. There is
no textured/untextured toggle: when no pack loads, the loader synthesises twelve flat tiles in
memory from the sRGB table in `src/TexPack.cs`. That is why "no pack" cannot fail — it never
reads a file.

## In one screen

- A pack is a directory: `pack.json` plus PNGs under it. Twelve base keys, fixed indices `0..11`.
- Optional **per-face overrides**: `<block>_<suffix>` keys (`stone_posx`, `leaves_negz`, …), suffix
  order `posx, negx, top, bottom, posz, negz`, baseline layer `12 + block*6 + face` (the layout
  when no key declares variants). A provided override
  wins for exactly that cell; every other cell keeps the frozen base map. A pack with only the
  twelve base keys needs no changes; an override whose PNG is missing or bad falls back to `missing`
  like any other tile. One override enumerates all 66 keys — 66 layers in the no-variant baseline,
  more when a key declares variants (see "Texture variants").
- Discovery: `--pack=<dir>` → `user://texturepacks/<selected.txt>` → `res://texturepacks/default`
  → procedural tiles. The first loadable pack wins; the last rung reads no files. Every run
  prints one `texpack: using …` success line, including rung 4.
- A **manifest** fault rejects the whole pack and moves to the next source. A **tile** fault falls
  back to that pack's `missing` tile. Unknown fields and unknown keys warn once and are ignored.
  Nothing throws.
- Tile paths are relative, lexically normalised, and rejected the moment they leave the pack root.
- One material for every chunk. The texture is the base colour; vertex `COLOR` is tint only.
- v1: `filter_nearest`, no mipmaps, no alpha blending, no PBR, no animation, no block
  definitions in packs.

## The contract: twelve base keys, frozen indices

| # | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| key | `stone` | `dirt` | `grass_top` | `grass_side` | `grass_bottom` | `sand` | `wood_side` | `wood_top` | `plank` | `leaves` | `bedrock` | `missing` |

Indices are never renumbered. `missing` (11) is the fallback for an absent or invalid tile and is
referenced by no Block+Face row. A loader must enumerate the `Block` enum — **not**
`Blocks.Palette`, which omits `Bedrock` even though bedrock is placed, meshed and therefore needs
a tile.

The mesher's side of the contract is three arrays:

```csharp
arrays[(int)Mesh.ArrayType.TexUV]  = uvs.ToArray();   // tile-local [0,1], (0,0) = PNG top-left
arrays[(int)Mesh.ArrayType.TexUV2] = uv2s.ToArray();  // (layer, class): layer = the frozen index above in the one-class baseline
// COLOR becomes FaceTint[face] only — never block colour
```

- `UV` addresses a point **inside** one tile. `repeat_disable` + `filter_nearest` mean a
  coordinate of exactly `0.0` or `1.0` samples the edge texel, with no wrap and no atlas bleed
  (verified on this build: `u == 1.0` clamps to the last texel under `repeat_disable`, and wraps
  to the first under `repeat_enable` — the hint is load-bearing).
- `UV2.x` carries the tile's layer index inside its class's array as a float: the canonical key
  index in the no-variant baseline, and the position-selected variant's layer once variants exist.
  `UV2.y` carries the size class — always `0` for a one-class pack (see "Per-tile sizes (size
  classes)"). `UV2` is the only channel that selects a tile, which is what makes the atlas
  fallback (below) a loader-side change with no mesher change.
- Art is authored **along the block's own up direction**. All six face bases are right-handed as
  seen from outside (`u × v = -n`), so a directional glyph reads upright and un-mirrored on every
  face. A block's stored rotation rotates both the tile choice (its local face) and the in-face
  UVs, so the glyph turns with the block. The UV origin stays the PNG top-left.

### Optional per-face overrides

Fifty-four optional keys extend the base vocabulary without touching it. A key is
`<block>_<suffix>`, block in `TexPack.Renderable` order, suffix in `Face` order:

| suffix | `posx` | `negx` | `top` | `bottom` | `posz` | `negz` |
| --- | --- | --- | --- | --- | --- | --- |
| `Face` | 0 | 1 | 2 | 3 | 4 | 5 |

`cell = blockOrdinal * 6 + faceIndex`, and in the **no-variant baseline** the override's layer is
**`12 + cell`** (12..65). `TexPack.LayerCount` is 66: the twelve base keys then the 54 override
slots, appended and never renumbered — the baseline slot space, not necessarily the array size
once variants exist (see "Texture variants").

Resolution per `(Block, Face)`: if the manifest provides that cell's exact override key, the cell
samples that key's primary layer — the **no-variant baseline** layer `12 + cell`; otherwise it
keeps the frozen base mapping in the completeness table below. A pack that ships only the twelve
base keys resolves every cell through the base map — overrides are additive.

Three override spellings are also base keys, and a manifest key is read as a **base** key when the
name exists in both vocabularies: `grass_top` (Grass.Top), `grass_bottom` (Grass.Bottom) and
`wood_top` (Wood.Top). For those cells the spelling is an alias for the base key, so they sample
layers **2, 4 and 7** rather than `12 + cell`, and no cell ever samples the matching override
slots (26, 27, 38). No capability is lost: for Grass the base keys already give top and bottom
their own tiles, so the alias is behaviourally identical to the override; for Wood `wood_top`
keeps today's shared log-end on both ends, with `wood_bottom` (layer 39, not shadowed) available
to make the bottom distinct.

A provided override whose PNG is missing, undecodable, root-escaping or non-square is not a
manifest fault: like a base key it claims its slot and the pixels become the pack's `missing`
tile. The pack is never rejected for it, and an unknown key still warns once and is ignored.

Array allocation is all-or-nothing: with no override the array has today's exactly 12 layers; as
soon as one override key is provided the array has all 66, unused slots holding the `missing`
tile and referenced by no cell. (Variants change the layer count, never the enumerated or
frozen key order — see "Texture variants".) `tiles=12/12` in the success line still counts base keys only;
the override count travels on its own line, printed once when at least one override is provided:

```
texpack: <source>: N/54 per-face overrides
```

## Block orientation

Every placed block stores one of the 24 cube rotations in `ChunkBlocks.Orientation`, a byte
array parallel to `Value` by the same index; byte `0` is identity, the default, so an un-oriented
block is drawn in its authored orientation. (The three face bases normalised in this build are a
separate, deliberate change — see Known weaknesses.) The mesher maps each world face back to the
block's **local** face to choose the tile, then rotates the in-face UVs by the same rotation, so
a directional glyph stays upright and is never mirrored. The tile vocabulary (12 base keys + 54
per-face slots), the index table and the pack format are unchanged: a pack still names per local
face.

Orientation is the 24-element cube rotation group: a block can take any orientation its type
allows. The block type carries an allowed-orientation policy (see docs/architecture.md), the
placement rule snaps the candidate into that set, and the player cycles the allowed values with
`Q` (next) and `E` (previous), wrapping within the type's allowed set.

Placement rules are deterministic and total: any clicked face, yaw and pitch yields a defined
default orientation.

- Log-like blocks take their axis from the clicked face. Clicking top or bottom gives a vertical
  log; clicking a side gives a horizontal log along that axis. The roll around the axis comes
  from the player's yaw quadrant.
- Other directional blocks put their front (local +Z) toward the player, with up = world up.
  When the player looks straight up or down, a deterministic horizontal fallback defines up.
- Non-directional blocks default to identity; the type decides the default and the allowed set.

The log-like rule is surjective: the six clicked faces at the four yaw quadrants reach all 24
rotations, and `Q`/`E` iterate every orientation the block's policy allows, in both directions
(24 for an `Any` block, 4 for an `Upright` block such as `Grass`), so no allowed orientation is
out of reach.

## Completeness: Block × Face → tile key

Nine renderable `Block` enum members (Air excluded) × six faces = **54 cells**, each mapped
exactly once. Enumerated from `src/Blocks.cs`, cross-checked against `ChunkMesher.Dirs`
(`PosX=0, NegX=1, Top=2, Bottom=3, PosZ=4, NegZ=5`). `Wood.Bottom → wood_top` is a spec
convention (log end-grain), not a fact derivable from the procedural colour table in
`src/TexPack.cs`, which has per-face entries only for Grass.

`TileKey`/`TileIndex` are the frozen **fallback** for a cell — what it samples when no override
is provided — while `FaceKey` is the optional override for that same cell, at layer `12 + cell`.
All 54 cells now resolve through the chain: exact override when the manifest provides it, else
the fallback.

| Block | FaceName | TileKey | TileIndex | FaceKey |
| --- | --- | --- | ---: | --- |
| Stone | PosX | `stone` | 0 | `stone_posx` |
| Stone | NegX | `stone` | 0 | `stone_negx` |
| Stone | Top | `stone` | 0 | `stone_top` |
| Stone | Bottom | `stone` | 0 | `stone_bottom` |
| Stone | PosZ | `stone` | 0 | `stone_posz` |
| Stone | NegZ | `stone` | 0 | `stone_negz` |
| Dirt | PosX | `dirt` | 1 | `dirt_posx` |
| Dirt | NegX | `dirt` | 1 | `dirt_negx` |
| Dirt | Top | `dirt` | 1 | `dirt_top` |
| Dirt | Bottom | `dirt` | 1 | `dirt_bottom` |
| Dirt | PosZ | `dirt` | 1 | `dirt_posz` |
| Dirt | NegZ | `dirt` | 1 | `dirt_negz` |
| Grass | PosX | `grass_side` | 3 | `grass_posx` |
| Grass | NegX | `grass_side` | 3 | `grass_negx` |
| Grass | Top | `grass_top` | 2 | `grass_top` |
| Grass | Bottom | `grass_bottom` | 4 | `grass_bottom` |
| Grass | PosZ | `grass_side` | 3 | `grass_posz` |
| Grass | NegZ | `grass_side` | 3 | `grass_negz` |
| Sand | PosX | `sand` | 5 | `sand_posx` |
| Sand | NegX | `sand` | 5 | `sand_negx` |
| Sand | Top | `sand` | 5 | `sand_top` |
| Sand | Bottom | `sand` | 5 | `sand_bottom` |
| Sand | PosZ | `sand` | 5 | `sand_posz` |
| Sand | NegZ | `sand` | 5 | `sand_negz` |
| Wood | PosX | `wood_side` | 6 | `wood_posx` |
| Wood | NegX | `wood_side` | 6 | `wood_negx` |
| Wood | Top | `wood_top` | 7 | `wood_top` |
| Wood | Bottom | `wood_top` | 7 | `wood_bottom` |
| Wood | PosZ | `wood_side` | 6 | `wood_posz` |
| Wood | NegZ | `wood_side` | 6 | `wood_negz` |
| Plank | PosX | `plank` | 8 | `plank_posx` |
| Plank | NegX | `plank` | 8 | `plank_negx` |
| Plank | Top | `plank` | 8 | `plank_top` |
| Plank | Bottom | `plank` | 8 | `plank_bottom` |
| Plank | PosZ | `plank` | 8 | `plank_posz` |
| Plank | NegZ | `plank` | 8 | `plank_negz` |
| Leaves | PosX | `leaves` | 9 | `leaves_posx` |
| Leaves | NegX | `leaves` | 9 | `leaves_negx` |
| Leaves | Top | `leaves` | 9 | `leaves_top` |
| Leaves | Bottom | `leaves` | 9 | `leaves_bottom` |
| Leaves | PosZ | `leaves` | 9 | `leaves_posz` |
| Leaves | NegZ | `leaves` | 9 | `leaves_negz` |
| Bedrock | PosX | `bedrock` | 10 | `bedrock_posx` |
| Bedrock | NegX | `bedrock` | 10 | `bedrock_negx` |
| Bedrock | Top | `bedrock` | 10 | `bedrock_top` |
| Bedrock | Bottom | `bedrock` | 10 | `bedrock_bottom` |
| Bedrock | PosZ | `bedrock` | 10 | `bedrock_posz` |
| Bedrock | NegZ | `bedrock` | 10 | `bedrock_negz` |
| Chest | PosX | `plank` | 8 | `chest_posx` |
| Chest | NegX | `plank` | 8 | `chest_negx` |
| Chest | Top | `plank` | 8 | `chest_top` |
| Chest | Bottom | `plank` | 8 | `chest_bottom` |
| Chest | PosZ | `plank` | 8 | `chest_posz` |
| Chest | NegZ | `plank` | 8 | `chest_negz` |

Key → cells: `stone` 6, `dirt` 6, `grass_top` 1, `grass_side` 4, `grass_bottom` 1, `sand` 6,
`wood_side` 4, `wood_top` 2, `plank` 12, `leaves` 6, `bedrock` 6, `missing` 0
(6+6+1+4+1+6+4+2+12+6+6 = 54). These are the fallback counts. Of the 54 `FaceKey` names in the
table, 51 are override-only and cover exactly one cell each; the three colliding names
(`grass_top`, `grass_bottom`, `wood_top`) are base-key aliases — two cover one cell each and
`wood_top` covers two, per the alias rule in "Optional per-face overrides".

## pack.json

Parsed with Godot's `Json.ParseString`; a new JSON dependency for four fields is not worth it.
Numbers arrive as `double`, so numeric rules are comparisons, not type identity.

| field | type | required | default | rule |
| --- | --- | --- | --- | --- |
| `version` | number, integer | **yes** | — | must equal `1`; anything else **rejects the pack** |
| `name` | string | no | directory name | cosmetic; wrong type warns and takes the directory name |
| `tile_size` | number, integer | **yes** | — | `>= 1`; the pack's expected/default square size and the class-0 preference — a tile PNG may ship its own square size (see "Per-tile sizes (size classes)"); missing or invalid **rejects the pack** |
| `tiles` | object, key → string or array of strings | no | `{}` | keys from the twelve base keys or the 54 override keys; a value is one pack-relative path or a list of them (up to 16 variants, first = primary) |

`tile_size` is required because it is the pack's declared default size and names the class-0
preference; a missing or invalid value rejects the pack rather than guessing. `name` is optional
because nothing renders it. Unknown top-level fields and unknown tile keys warn once and are
ignored. Duplicate JSON keys are not detected — Godot's parser keeps the last occurrence.

`texturepacks/default/pack.json`, all twelve keys:

```json
{
  "version": 1,
  "name": "Default",
  "tile_size": 16,
  "tiles": {
    "stone": "tiles/stone.png",
    "dirt": "tiles/dirt.png",
    "grass_top": "tiles/grass_top.png",
    "grass_side": "tiles/grass_side.png",
    "grass_bottom": "tiles/grass_bottom.png",
    "sand": "tiles/sand.png",
    "wood_side": "tiles/wood_side.png",
    "wood_top": "tiles/wood_top.png",
    "plank": "tiles/plank.png",
    "leaves": "tiles/leaves.png",
    "bedrock": "tiles/bedrock.png",
    "missing": "tiles/missing.png"
  }
}
```

The same manifest with one override — everything else keeps the base map (two tile keys, so
`tiles=1/12`):

```json
{
  "version": 1,
  "name": "Fancy",
  "tile_size": 16,
  "tiles": {
    "stone": "tiles/stone.png",
    "stone_top": "tiles/stone_top.png"
  }
}
```

A clean load prints one line:

```
texpack: using 'Default' (res://texturepacks/default) tile_size=16 tiles=12/12
```

A pack that also provides override keys prints one extra line, after the success line:

```
texpack: using 'Fancy' (user://texturepacks/fancy) tile_size=16 tiles=1/12
texpack: user://texturepacks/fancy: 1/54 per-face overrides
```

An unsupported version is never half-loaded:

```
texpack: user://texturepacks/future: version 2 is not supported (expected 1) — skipping pack
texpack: using 'Default' (res://texturepacks/default) tile_size=16 tiles=12/12
```

One bad path, one missing file, one unknown key — the pack still loads:

```
texpack: user://texturepacks/broken: unknown manifest field 'author' — ignored
texpack: user://texturepacks/broken: unknown tile key 'gravel' — ignored
texpack: user://texturepacks/broken: tile 'stone': '../../../etc/passwd' escapes the pack root — using 'missing'
texpack: user://texturepacks/broken: tile 'dirt': file not found: textures/dirt.png — using 'missing'
texpack: user://texturepacks/broken: using 'Broken' (user://texturepacks/broken) tile_size=16 tiles=10/12
```

## Path validation: the one trust boundary

Strict and lexical, never best-effort. The validator touches no filesystem — no `File.Exists`,
no `ResourceLoader`. Existence is decided later, by the read. Only `System.IO.Path` may be used
for path math; Godot supplies `ProjectSettings.GlobalizePath`, `FileAccess` and `Image`:

```csharp
// src/TexPack.cs — ResolveTilePath(raw, root, rootFull) -> path to hand to FileAccess, or null.
static string ResolveTilePath(string raw, string root, string rootFull)
{
    if (string.IsNullOrWhiteSpace(raw)) return null;          // 1. empty
    raw = raw.Replace('\\', '/');                             // 2. separators first
    if (raw[0] == '/') return null;                           // 3. absolute / UNC
    if (raw.Contains(':')) return null;                       // 4. drive letter, ADS, scheme

    var segs = new List<string>();                            // 5. one pass, no stacking
    foreach (var seg in raw.Split('/'))
    {
        if (seg.Length == 0 || seg == ".") continue;          // 'a//b', 'a/./b', trailing '/'
        if (seg.IndexOf('\0') >= 0) return null;              // control byte
        if (seg == "..") return null;                         // never legal, inside or out
        segs.Add(seg);
    }
    if (segs.Count == 0) return null;                         // 6. normalises to the root

    string rel = string.Join('/', segs);                      // 7. join
    try                                                       // 8. belt and braces
    {
        string full = Path.GetFullPath(Path.Combine(rootFull, rel));
        var cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, cmp)) return null;
    }
    catch (Exception) { return null; }                        // a throw is a crash; this spec forbids crashes

    return root + "/" + rel;                                  // 9. FileAccess reads res:// or user://
}
```

Every `..` segment is rejected outright — `a/../stone.png` does not load. The lexical rule is
easier to state, strictly stronger, and no legitimate pack needs it. The `GetFullPath`
containment check stays as a second line of defence, because the first one is string handling.

Consequences, stated once:

- **A rejected tile** → that tile becomes `missing`, one warning, the pack still loads.
- **A missing or invalid `pack.json`** → the whole pack is rejected, one warning, discovery
  continues. There is no partial manifest.
- **Symlinks are not resolved.** A link inside the root pointing outside is followed. Accepted
  while packs are local user content; resolve links and re-check containment if packs ever become
  downloadable.

## Discovery: four rungs

| # | source | path |
| --- | --- | --- |
| 1 | CLI | `--pack=<dir>` |
| 2 | user install | `user://texturepacks/<name>`, `<name>` from line 1 of `user://texturepacks/selected.txt` |
| 3 | bundled default | `res://texturepacks/default` |
| 4 | procedural tiles | twelve tiles synthesised in memory — no file I/O; prints `texpack: using 'procedural' (built-in) tile_size=16 tiles=12/12` |

A rung that fails warns once and is skipped. `--pack=` accepts an OS path or a Godot path and
suppresses `selected.txt` entirely; on failure it warns and falls through rather than quitting,
so a typo shows up in the log as the next rung's success line. `selected.txt` is a one-line text
file because one string does not need `ConfigFile`: missing is silent (the default state), and a
value containing `/`, `\`, `:`, or equal to `.`/`..` warns and falls through. On Windows,
`user://` is `%APPDATA%\Godot\app_userdata\Regress\`.

Rung 3 is special: a `.png` under `res://` is an **imported** resource, not a file that survives
into an exported `.pck`, so its tiles load through the importer
(`ResourceLoader.Load<Texture2D>().GetImage()`) instead of the raw-bytes path. The import must
stay lossless — a VRAM-compressed mode would quantise the flat colours before the game ever sees
them. The raw-bytes path applies to OS and `user://` roots only.

## What happens on bad data

`texpack: <source>: <problem> — <consequence>`. Problems go through `GD.PushWarning`, the one
success line through `GD.Print`. Noise is bounded: at most one tile line per provided base key +
one per provided override key + 1 per unknown field + 1 per discovery source.

Every tile row below applies to a base key and to an override key alike; for an override the
`<key>` in the message is the `<block>_<face>` name. For single-string values nothing below
changes: an override claims its slot, and a failed read makes that slot's pixels the pack's
`missing` tile — `missing`'s first valid variant, one fixed image for every degraded slot, never
position-selected. Variant values add the list rows below.

| failure | warn | fallback |
| --- | --- | --- |
| pack dir missing / not a directory | `not a directory — skipping` | next rung |
| `pack.json` missing / unreadable | `pack.json not found` / `could not be read — skipping pack` | next rung |
| JSON unparsable or not an object | `pack.json: <parser message> — skipping pack` | next rung |
| `version` missing / ≠ 1 | `version <v> is not supported (expected 1) — skipping pack` | next rung |
| `name` wrong type | `name is not a string — using '<dir>'` | directory name |
| `tile_size` missing / non-integral / `< 1` | `tile_size … — skipping pack` | next rung |
| `tiles` absent / not an object | `tiles is not an object — ignoring` (absent: silent) | all → `missing` |
| tile value not a string / empty | `tile '<key>' is not a path — using 'missing'` | that tile → `missing` |
| path escapes the root | `tile '<key>': '<raw>' escapes the pack root — using 'missing'` | that tile → `missing` |
| tile file missing | `tile '<key>': file not found: <rel> — using 'missing'` | that tile → class 0, `missing` |
| tile not a decodable PNG | `tile '<key>': not a decodable PNG: <rel> — using 'missing'` | that tile → class 0, `missing` |
| PNG not square (64×32) | `tile '<key>'[i/n]: 64x32 is not square — using 'missing'` | that tile → class 0, `missing` |
| size ≠ `tile_size` | *(none — no longer a failure)* | that tile takes its own class (within the cap of 4) |
| 5th size class | `tile '<key>'[i/n]: size 512 would be class 5 of 4 — using 'missing'` | that tile → class 0, `missing` |
| degraded slot inside class C | (the per-tile line) | `missing` tile **resized nearest to C's size**; procedural if no `missing` |
| tile value is an object / number / bool (neither string nor array) | `tile '<key>' is not a path or a list of paths — using 'missing'` | key → 1 slot, `missing` |
| empty variants array `[]` | `tile '<key>': empty variant list — using 'missing'` | key → 1 slot, `missing` |
| more than 16 variants | `tile '<key>': <n> variants, cap is 16 — only the first 16 are used` | first 16 kept, the rest dropped |
| one entry of a variants array is invalid (not a string / empty / escapes root / file missing / undecodable / non-square / over-cap) | the matching row above with the variant index `[i/n]` | that variant's slot → class 0, `missing` image; the key keeps its other variants |
| every variant of a key invalid | the per-variant lines, one per bad variant | key → 1 slot, `missing` |
| `Texture2DArray` build returns non-`OK` | `texture array build failed — using procedural tiles` | procedural tiles for the whole pack |
| `missing` itself unavailable | same lines, tail `— using procedural colour` | procedural tile |
| unknown tile key / manifest field | `unknown … — ignored` | ignored |
| duplicate JSON key | *(none)* | last occurrence wins |
| extra files in the pack | *(none)* | ignored; the loader never enumerates |
| `selected.txt` missing / invalid | *(silent)* / `'<raw>' is not a pack name — ignoring` | next rung |
| rung 3 missing / invalid | same rows, `<source>` = `res://texturepacks/default` | procedural tiles; the rung-4 success line prints |

## Rendering: one material, one mesh path

There is no pack-present branch in the renderer. `VoxelWorld` replaces its shared
`StandardMaterial3D` with one shared `ShaderMaterial`; the loader builds one `Texture2DArray` per
size class and binds each to `tiles0..tiles3`. Those uniforms and the three mesh arrays above are
the whole interface between the loader and the renderer.

```glsl
shader_type spatial;
render_mode unshaded, cull_back, depth_draw_opaque;

uniform sampler2DArray tiles0 : source_color, filter_nearest, repeat_disable;
uniform sampler2DArray tiles1 : source_color, filter_nearest, repeat_disable;
uniform sampler2DArray tiles2 : source_color, filter_nearest, repeat_disable;
uniform sampler2DArray tiles3 : source_color, filter_nearest, repeat_disable;

varying float v_tile;
varying float v_class;

void vertex() {
    v_tile  = UV2.x;                 // layer inside its class's array, written by the mesher
    v_class = UV2.y;                 // size class; 0 for every one-class pack
}

void fragment() {
    // GLSL cannot index a sampler array dynamically: uniform branch on the class.
    vec4 t = v_class < 0.5 ? texture(tiles0, vec3(UV, v_tile))
           : v_class < 1.5 ? texture(tiles1, vec3(UV, v_tile))
           : v_class < 2.5 ? texture(tiles2, vec3(UV, v_tile))
                           : texture(tiles3, vec3(UV, v_tile));
    ALBEDO = t.rgb * COLOR.rgb;      // COLOR is tint only
    ALPHA  = t.a;
    ALPHA_SCISSOR_THRESHOLD = 0.5;   // cutout: no blending, no sorting, depth stays opaque
}
```

- **Per-face selection** is `UV2` → (class array, layer inside it). A block's six faces may name
  six different keys (Grass does), and the mesher emits one quad per face with that face's
  `(layer, class)`.
- **`COLOR` is tint only.** The mesher writes `FaceTint[face]` — `0.72, 0.72, 1.00, 0.45, 0.86,
  0.86`, the same linear numbers used today — and block identity lives *only* in the tile. For
  every block, including Bedrock and blocks adjacent to Air, no block-colour term may appear in
  the mesh colour path; leaving one in double-applies the colour. The mesh
  format stores vertex colour as RGBA8, so the tint reaches the shader quantized to 1/255.
- **The sRGB table in `src/TexPack.cs` is the fallback-art source.** It defines what the game
  looks like with no pack, as the colour table for the procedural tiles.
- **Leaves alpha, v1:** binary cutout via scissor. Leaf tiles are RGBA; a pixel with alpha < 0.5
  is discarded. There is no blended transparency in v1 — no sort order, no per-face two-sided
  rendering, so a leaf cube keeps `cull_back` exactly like stone.
- **Every decoded tile is converted to `Image.Format.Rgba8`** after the size check. The array
  requires identical width, height, **format** and mipmap setting across layers, and a PNG's own
  format varies (an opaque RGB tile next to an RGBA leaf tile), so without the conversion a
  normal mixed pack would fail the array build and fall to procedural tiles.
- **Nearest, no mipmaps.** `filter_nearest` and `repeat_disable`; images load with mipmaps off,
  so there is no minification shimmer budget in v1 and no atlas padding.

### Procedural tiles (rung 4)

The loader synthesises the twelve tiles as 16×16 **sRGB 8-bit** images from the procedural table
in `src/TexPack.cs` — so the sampler's `source_color` conversion lands back on today's linear
values:

| key | sRGB | key | sRGB |
| --- | --- | --- | --- |
| `stone` | 0.52, 0.52, 0.55 | `plank` | 0.68, 0.51, 0.31 |
| `dirt` | 0.44, 0.30, 0.20 | `leaves` | 0.20, 0.45, 0.17 |
| `grass_top` | 0.34, 0.62, 0.24 | `bedrock` | 0.16, 0.16, 0.18 |
| `grass_side` | 0.36, 0.50, 0.24 | `wood_side` / `wood_top` | 0.36, 0.26, 0.15 |
| `grass_bottom` | 0.44, 0.30, 0.20 | `missing` | 1.00, 0.00, 1.00 (magenta) |
| `sand` | 0.86, 0.80, 0.56 | | |

The result is **the same colours, 8-bit quantized (±1/255 per channel)** — not bit-identical to
the float colour path it replaces. Do not use `--shot` frames rendered before this change as a
zero-diff regression baseline; a 1/255 shift will read as a regression.

### Chosen path and the one fallback

**Chosen: `Texture2DArray`.** Twelve images for a base-only pack, 66 once any override is used
and no variants are declared (the unused slots hold the `missing` tile; variants add layers — see
"Texture variants"), one sampler per size class (up to four), per-vertex `(layer, class)`, no UV rects, no atlas packing, no
bleeding — a tile is a file.

**Fallback: a single atlas image.** If array sampling ever fails on a target, the loader builds a
4×4 atlas of the same twelve images — 9×9 when the 66 slots are in use — and the fragment shader
maps `UV2.x` → cell rectangle (`cell = floor(vec2(mod(idx,G), floor(idx/G)))`), then samples with
`UV/G + cell/G`, with `G = 4` for a base-only pack and `G = 9` for the 66-slot array. The mesher
does not change — the index already travels in `UV2.x`. Only the loader's texture build and the
fragment shader change. `repeat_disable` stays in the fallback too: at a tile's `u == 1.0` a
repeat sampler would wrap into the neighbouring atlas cell.

## Feasibility: what was actually run

Executed as a disposable spike (not committed) against this repo's exact engine,
`godot-mono 4.7.2.stable.mono.official.ed1daf0bf`, on an AMD Radeon 780M / Vulkan, before this
spec was frozen:

- **user:// PNG decode passes**: `FileAccess.GetFileAsBytes` + `Image.LoadPngFromBuffer` returns
  a correct 16×16 image. On this build `Image.LoadPngFromBuffer` is an **instance** method
  returning `Error` — that return value is the failure signal, there is no static factory.
- **`Texture2DArray.CreateFromImages` passes** with twelve same-size layers. On this build it is
  an **instance** method taking only the images — there is no static factory and no mipmap
  argument; uniform size is required, so the loader validates `tile_size` itself before calling,
  and a mismatched image is rejected cleanly (`ERR_INVALID_PARAMETER`, no corruption). **The
  loader must check the returned `Error`**: a failed array is empty (0 layers) and samples black,
  so a non-`OK` array is discarded in favour of the procedural tiles, never bound.
- **Front faces are clockwise.** The spike's first real-render readback was all black because its
  quad was CCW under `cull_back`; `ChunkMesher` already emits reversed (CW) triangles, so the
  existing winding is correct — any new test quad must match it.
- **Shader + pixel readback pass under the real renderer**: the shader above compiles with zero
  errors and the sampled tile × `COLOR` tint is readable back from a viewport. A deliberately
  broken shader is reported loudly, so "zero errors" is a real signal.
- **`filter_nearest` is proven, not assumed.** At 1:1 texel centres bilinear also yields exact
  colours, so the spike magnified (quarter-UV): `filter_linear` produced 11 blended tones where
  `filter_nearest` produced exactly 128 red + 128 blue pixels.
- **Tile-edge sampling is proven under `repeat_disable`**: at `u == 1.0` it clamps to the last
  texel (blue) while `repeat_enable` wraps to the first (red), so the sampler hint is load-bearing
  and the mesher may emit exact `0.0`/`1.0` UVs.
- **Headless limits, stated plainly**: the dummy driver does not compile GPU pipelines and
  viewport readback returns null, so CI can catch shader *language* errors headlessly but cannot
  verify pixels. Pixel evidence above came from a real-renderer run.
- **Bundled `res://` PNG bytes are readable in dev runs, but the engine warns the raw read
  "will not work on export."** The spec therefore routes `res://` tiles through Godot's importer
  (`ResourceLoader.Load<Texture2D>().GetImage()`) — export-safe by construction — while OS and
  `user://` roots keep the raw-bytes path. The release workflow now exports a Linux build and
  loads all twelve tiles through this path; see Known weaknesses.

## Install and select a pack

1. Copy the pack directory to `%APPDATA%\Godot\app_userdata\Regress\texturepacks\<name>\`.
2. Write `<name>` on line 1 of `user://texturepacks/selected.txt`.
3. Launch. The startup line always prints; with no pack at all it reads
   `texpack: using 'procedural' (built-in) tile_size=16 tiles=12/12`. If you expected a different
   line, a warning above explains which rung failed.

For a one-off, `--pack=<dir>` overrides the file and accepts a plain path. For testing against
the bundled pack, `--pack=texturepacks/default`. Restart to change packs — v1 has no hot reload.

## Non-goals for v1

| not in v1 | upgrade path |
| --- | --- |
| animated textures | a `frames` list per key + time-based UV offset in the shader |
| non-uniform tile sizes | **implemented (size classes)** — class inferred per tile, one `sampler2DArray` per class (cap 4); cost is the VRAM table and the per-class arrays (see "Per-tile sizes (size classes)") |
| PBR (normal/roughness/emission) maps | extra arrays and sampler inputs per key |
| mipmaps and anisotropic filtering | `generate_mipmaps` + sampler hints when minification shimmers |
| block definitions in packs (hardness, drops, new blocks) | a block registry file; art-only packs are the seam |
| pack archives / downloads | unpack outside the game; the loader reads directories |
| hot reload | reload on window focus once the texture build can rebuild |
| per-face UV orientation, mirroring, rotation | **implemented** — all six bases are right-handed; cost is the three-face UV normalisation (see Known weaknesses) |
| biome/global tinting | `COLOR` is already a tint multiplier; add a tint source and multiply |
| translucent or emissive blocks (glass, glowstone) | material classes per block; v1 has one material |

## Known weaknesses

1. **Exported builds are smoke-tested, not pixel-tested.** Since 2026-09-22 the release workflow
   exports a Linux build and runs `--selftest` headless inside it, asserting
   `texpack: using 'Default' (res://texturepacks/default) tile_size=16 tiles=12/12` and
   `SELFTEST PASS` (run: https://github.com/ModerRAS/Regress/actions/runs/35738397514). The PCK
   ships each tile as `res://.godot/imported/<tile>.png-<hash>.ctex` plus its `.import`
   companion, and those imports are lossless (`compress/mode=0`). That proves the twelve tiles
   load back as 16×16 images in the export; it says nothing about pixel content or a GPU render,
   so an importer-changed pixel would still pass. Upgrade path: assert one known pixel per tile
   in `SelfTest`, then assert that in the release workflow too.
2. **Duplicate JSON keys are undetected.** Godot's parser keeps the last occurrence; four fields
   do not justify a second parser.
3. **Symlinks are not resolved** — the trust boundary is lexical.
4. **No pixel cap on `tile_size`.** A hostile local pack can ship a 16384² PNG that is decoded
   before the size check. Accepted while packs are local content; a cap belongs at the download
   boundary, which does not exist yet.
5. **Leaves are cutout with back-face culling.** Canopy edges at grazing angles can show through
   cutout holes. Upgrade path: two-sided leaves or per-block materials.
6. **The procedural fallback is quantized** (±1/255), so it is not a zero-diff baseline.
7. **No hot reload.** A pack change requires a restart.
8. **Per-face overrides are all-or-nothing in VRAM.** A pack that provides even one override
   carries at least the full 66-slot array — 5.5× the base art, more with variants — because the
   fixed override indices leave no room for a partial array.
9. **The per-face UV basis was normalised in this build.** The v1 bases mirrored `posx` and
   `negz` in `u` and `bottom` in `v`; all six faces now satisfy `u × v = -n`, so those three
   faces render mirrored against any earlier build. The bundled art is direction-agnostic
   procedural noise, so "imperceptible" here is a perception-level statement about that
   direction-agnostic art — not "zero pixels changed": in controlled head-on pairs the
   normalisation moved 1.09% of the pixels of a sky-like frame (27.88% of the frame) and 69.58%
   of a vegetation-like frame (59.97% of the frame), mean per-pixel |Δ| ≈ 19/255. Any
   render/pixel-level baseline captured before this change is void (this weakness).

## Texture variants (string or array)

A `tiles` value is **either a string** — one pack-relative path, the v1 form — **or an array of
strings**: that key's **variants**, in manifest order. The first entry is the key's **primary**
layer, the one the two-argument `TileIndex(Block, Face)` returns. The cap is **16 variants per
key**; a longer array keeps only the first 16 (warning below). The rule applies uniformly to the
twelve base keys and the 54 `<block>_<suffix>` override keys, and `version` stays `1`: a manifest
with no arrays is unchanged.

Schema row for `tiles` (see "pack.json"): a value is one pack-relative path or a list of them,
up to 16 variants, first = primary.

```json
{
  "version": 1,
  "name": "Varied",
  "tile_size": 16,
  "tiles": {
    "stone": ["tiles/stone_0.png", "tiles/stone_1.png", "tiles/stone_2.png", "tiles/stone_3.png"],
    "dirt": ["tiles/dirt_0.png", "tiles/dirt_1.png", "tiles/dirt_2.png"]
  }
}
```

### The corrected layer invariant

- **Key order is frozen** — the twelve base keys, then the 54 face cells, never reordered. Each
  key contributes one **consecutive** layer per **declared** variant (capped at 16), in manifest
  order: a declared variant whose PNG is missing, undecodable or non-square keeps its slot and
  degrades to the `missing` image, so `stone = [ok, corrupt, ok]` allocates 3 layers with the
  middle one `missing`. Only a key with **zero decodable variants** — an empty list, a non-list
  value, or every variant bad — contributes exactly one slot, today's degradation.
- **A no-variant pack is byte-identical to the pre-variants build**: base key `k → layer k`,
  override cell `c → layer 12 + c`, same `Sources`, same table. `12 + cell` is the **no-variant
  baseline only**.
- **`TexPack.LayerCount = 66` is the no-variant baseline slot space, not an array size.** The
  real array count grows with variants: each key before a variant key shifts every later key by
  its extra slots. Once any key declares variants, "an override is always at layer `12 + cell`"
  is false — pack authors must not hard-code absolute layer numbers.
- Hostile-pack ceiling: all 66 keys × 16 variants = **1056 layers**; at 16×16 RGBA8 (1 KiB per
  layer) that is **≈ 1056 KiB** of texture memory, plus decode time.

Worked layer counts:

| pack | keys | slots | layers |
| --- | --- | --- | --- |
| default (twelve single strings, no overrides) | 12 | 12 × 1 | **12** |
| any pack with one override, no variants | 66 | 66 × 1 | **66** (today) |
| `variants-demo` (stone 4, dirt 3, ten singles, no overrides) | 12 | 4 + 3 + 10 × 1 | **17** |
| hostile pack: all 66 keys × 16 variants | 66 | 66 × 16 | **1056** ceiling |

### Selection rule

```
variant = FNV-1a(worldX, worldY, worldZ, localFace) % validCount
```

`validCount` is that key's **decodable** variant count, so selection only ever lands on a slot
whose variant decoded — a degraded slot is never position-selected.

- **Absolute world coordinates** — `coord * 16 + local` in the mesher, never chunk-local, so the
  same world position gets the same variant whichever chunk meshes it.
- **Local face** — the `0..5` face index that names the key, after the block's rotation is mapped;
  a rotated block keeps each local face's variant.
- Deterministic: no RNG, no time, no state. Rebuilding after any edit yields the same `UV2.x`.

Failure handling for variant values — wrong value type, empty list, over-cap list, one bad
variant, all-bad key — is specified by the list rows in "What happens on bad data".

The `missing` key's **first valid variant** is the single global fallback image copied into every
degraded slot; it is **never position-selected**, so a debug run shows one fixed tile, not a
random one. Extra variants declared on `missing` still occupy their layers per the uniform rule
but are referenced by no Block+Face cell (Air is never meshed).

### Reporting

The success line is untouched, byte for byte — release smoke-test greps and this quote stay
valid:

```
texpack: using 'Default' (res://texturepacks/default) tile_size=16 tiles=12/12
```

The overrides line is untouched too. One new line is printed after the success line, and only
when at least one key has ≥ 2 valid variants:

```
texpack: <source>: <Layers> layers, <K> keys with variants (cap 16)
```

`<Layers>` is the actual `Texture2DArray` layer count and `<K>` the number of keys with two or
more valid variants.

## Per-tile sizes (size classes)

`tile_size` has always existed and has always been a **whole-pack uniform size** — a uniformly
16/32/64/256 pack loads today (the bundled default happens to be 16). The gap was never "tile size
support"; it was **mixing sizes inside one pack**, which v1 listed as a non-goal. A
`Texture2DArray` requires every layer to share size and format, so one array holds exactly one
size; mixed sizes therefore need **one array per size class**.

### The class model

- **A class is inferred from each PNG's own square edge** — self-describing, no new manifest
  field. `tile_size` stays the pack's expected/default size — the class-0 preference, i.e. the
  class for slots whose size can never be inferred.
- **Class 0 is the baseline class**: the `tile_size` class when any slot has that size, otherwise
  the first square size in frozen key order. Slots that never decoded (missing file, corrupt PNG,
  non-square, over-cap) always join class 0. Other classes get indices `1..C-1` in
  first-appearance order over the frozen key scan; a pack's indices are stable. A uniform pack —
  whatever its size — has exactly one class, class 0, so class 0 is never an empty leftover.
- **Cap: four classes** (`MaxSizeClasses = 4`). A fifth distinct size degrades **that tile** to
  `missing` in class 0 — never a pack rejection, never a crash:

  ```
  tile '<key>'[i/n]: size 512 would be class 5 of 4 — using 'missing'
  ```

  A non-square PNG is likewise not a pack fault; that slot becomes `missing` in class 0:

  ```
  tile '<key>'[i/n]: 64x32 is not square — using 'missing'
  ```

- **`size != tile_size` is no longer a failure.** The old "PNG size must equal `tile_size`" rule is
  gone: a tile that differs takes its own class (within the cap). A uniform pack is unaffected —
  its PNGs equal `tile_size`.

### Layer layout: one array per class

Layer numbering is **per class array**: within class C, filtered to the keys whose slots live in
C, frozen key order still applies and each key contributes `max(1, validCount)` consecutive
layers. A key whose variants span classes has slots in several arrays; there is no single global
layer number for it.

`TexPack.LayerCount = KeyCount + FaceKeys.Length = 66` (`KeyCount` stays 12) remains the
**no-variant, no-class baseline slot space** anchor, and size classes do **not** change it. A
class array's actual layer count travels in a different carrier — `Pack.Layers` / each class's own
count — never in `LayerCount`. Every Phase 1 rule still holds in Phase 2: a pack whose every PNG
equals `tile_size` has exactly one class, class index `0` on every face, and layer numbers, layout
and `Sources` byte-identical to Phase 1 and the pre-variants build. Asserted (E10), not claimed.

### VRAM

RGBA8 = 4 B/texel:

| edge | bytes/layer | ×12 base keys | note |
| --- | --- | --- | --- |
| 16 | 1 KiB | 12 KiB | default pack |
| 32 | 4 KiB | 48 KiB | |
| 64 | 16 KiB | 192 KiB | |
| 256 | 256 KiB | 3 MiB | |
| 1024 | 4 MiB | 48 MiB | |
| 4096 | 64 MiB | **768 MiB** | 66-layer override layout = **4.125 GiB** — whole-pack 4096 is not realistic; only faces that need it should pay |

Not free: each class occupies its own `sampler2DArray` slot (four uniforms total) and adds the
uniform branch that selects it. The win is that a pack pays per class, not for its largest tile
across every key.

### Contract: `UV2 = (layer, class)`

`UV2` is now a tuple instead of a scalar: `UV2.x` is the layer inside that class's array, `UV2.y`
is the class index. The class is a per-key constant carried in the same precomputed slot table the
variant hash already indexes, so the mesher adds no per-face arithmetic and writes
`new Vector2(tile.Layer, tile.Class)`. `TileIndex(Block, face)` still returns the primary slot's
layer for the frozen/class-0 assertions.

`UV2.y == 0` for every pack that has one class — today's meshes already carry `UV2.y = 0`, so the
single-class path is unchanged. The shader gains one `sampler2DArray` per class (`tiles0..tiles3`),
all `source_color, filter_nearest, repeat_disable`, selected by a uniform branch on `UV2.y` (GLSL
cannot index sampler arrays dynamically); absent classes bind the first array, and nothing
references them. `VoxelWorld.UseTiles(Texture2DArray)` becomes `UseTiles(TexPack.Pack)`, with
`Game.cs` still the only call site.

### No mipmaps: the shimmer warning

v1 still loads images with mipmaps off. A high-resolution tile minified in the distance therefore
**shimmers** — size classes make this easier to see, because a sharp 256² or larger face can now
sit next to 16² art. The fix remains the non-goal upgrade path (`generate_mipmaps` plus sampler
hints); mipmaps stay a non-goal.

### Reporting

The success line is untouched, byte for byte — release smoke-test greps and this quote stay valid:

```
texpack: using 'Default' (res://texturepacks/default) tile_size=16 tiles=12/12
```

Mixed packs add one VRAM line, printed only when `Classes > 1`:

```
texpack: <source>: <C> size classes — 16x16: 12 layers (12 KiB), 256x256: 1 layer (256 KiB), total 268 KiB
```
