# Texture packs

A texture pack is a **directory** containing a `pack.json` manifest and the PNGs it names — no
archive, no second manifest name, no in-engine download. Twelve tile keys are the entire
vocabulary, and their indices are frozen because they are the layer order the renderer receives.
The pack supplies art; the game supplies the mapping from `Block` + `Face` to a key, and nothing
else.

v1 renders every chunk through a single `Texture2DArray` and a single `ShaderMaterial`. There is
no textured/untextured toggle: when no pack loads, the loader synthesises twelve flat tiles in
memory from `Blocks.ColorOf`. That is why "no pack" cannot fail — it never reads a file.

## In one screen

- A pack is a directory: `pack.json` plus PNGs under it. Twelve keys, fixed indices `0..11`.
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

## The contract: twelve keys, frozen indices

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
arrays[(int)Mesh.ArrayType.TexUV2] = uv2s.ToArray();  // (tileIndex, 0), the frozen index above
// COLOR becomes FaceTint[face] only — never Blocks.ColorOf
```

- `UV` addresses a point **inside** one tile. `repeat_disable` + `filter_nearest` mean a
  coordinate of exactly `0.0` or `1.0` samples the edge texel, with no wrap and no atlas bleed
  (verified on this build: `u == 1.0` clamps to the last texel under `repeat_disable`, and wraps
  to the first under `repeat_enable` — the hint is load-bearing).
- `UV2.x` carries the canonical tile index as a float. It is the only channel that selects a
  tile, which is what makes the atlas fallback (below) a loader-side change with no mesher
  change.
- v1 fixes only the UV origin (top-left of the PNG) and that side faces have `+v` downward from
  their top edge. The per-face `u` axis is the mesher's to choose, because **v1 art must be
  direction-agnostic** — no tile may depend on being mirrored or rotated. Adding a per-face UV
  basis is a v2 change.

## Completeness: Block × Face → tile key

Eight renderable `Block` enum members (Air excluded) × six faces = **48 cells**, each mapped
exactly once. Enumerated from `src/Blocks.cs`, cross-checked against `ChunkMesher.Dirs`
(`PosX=0, NegX=1, Top=2, Bottom=3, PosZ=4, NegZ=5`). `Wood.Bottom → wood_top` is a spec
convention (log end-grain), not a fact derivable from `Blocks.ColorOf`, which has per-face
branches only for Grass.

| Block | FaceName | TileKey | TileIndex |
| --- | --- | --- | ---: |
| Stone | PosX | `stone` | 0 |
| Stone | NegX | `stone` | 0 |
| Stone | Top | `stone` | 0 |
| Stone | Bottom | `stone` | 0 |
| Stone | PosZ | `stone` | 0 |
| Stone | NegZ | `stone` | 0 |
| Dirt | PosX | `dirt` | 1 |
| Dirt | NegX | `dirt` | 1 |
| Dirt | Top | `dirt` | 1 |
| Dirt | Bottom | `dirt` | 1 |
| Dirt | PosZ | `dirt` | 1 |
| Dirt | NegZ | `dirt` | 1 |
| Grass | PosX | `grass_side` | 3 |
| Grass | NegX | `grass_side` | 3 |
| Grass | Top | `grass_top` | 2 |
| Grass | Bottom | `grass_bottom` | 4 |
| Grass | PosZ | `grass_side` | 3 |
| Grass | NegZ | `grass_side` | 3 |
| Sand | PosX | `sand` | 5 |
| Sand | NegX | `sand` | 5 |
| Sand | Top | `sand` | 5 |
| Sand | Bottom | `sand` | 5 |
| Sand | PosZ | `sand` | 5 |
| Sand | NegZ | `sand` | 5 |
| Wood | PosX | `wood_side` | 6 |
| Wood | NegX | `wood_side` | 6 |
| Wood | Top | `wood_top` | 7 |
| Wood | Bottom | `wood_top` | 7 |
| Wood | PosZ | `wood_side` | 6 |
| Wood | NegZ | `wood_side` | 6 |
| Plank | PosX | `plank` | 8 |
| Plank | NegX | `plank` | 8 |
| Plank | Top | `plank` | 8 |
| Plank | Bottom | `plank` | 8 |
| Plank | PosZ | `plank` | 8 |
| Plank | NegZ | `plank` | 8 |
| Leaves | PosX | `leaves` | 9 |
| Leaves | NegX | `leaves` | 9 |
| Leaves | Top | `leaves` | 9 |
| Leaves | Bottom | `leaves` | 9 |
| Leaves | PosZ | `leaves` | 9 |
| Leaves | NegZ | `leaves` | 9 |
| Bedrock | PosX | `bedrock` | 10 |
| Bedrock | NegX | `bedrock` | 10 |
| Bedrock | Top | `bedrock` | 10 |
| Bedrock | Bottom | `bedrock` | 10 |
| Bedrock | PosZ | `bedrock` | 10 |
| Bedrock | NegZ | `bedrock` | 10 |

Key → cells: `stone` 6, `dirt` 6, `grass_top` 1, `grass_side` 4, `grass_bottom` 1, `sand` 6,
`wood_side` 4, `wood_top` 2, `plank` 6, `leaves` 6, `bedrock` 6, `missing` 0
(6+6+1+4+1+6+4+2+6+6+6 = 48).

## pack.json

Parsed with Godot's `Json.ParseString`; a new JSON dependency for four fields is not worth it.
Numbers arrive as `double`, so numeric rules are comparisons, not type identity.

| field | type | required | default | rule |
| --- | --- | --- | --- | --- |
| `version` | number, integer | **yes** | — | must equal `1`; anything else **rejects the pack** |
| `name` | string | no | directory name | cosmetic; wrong type warns and takes the directory name |
| `tile_size` | number, integer | **yes** | — | `>= 1`; the width **and** height of every tile; missing or invalid **rejects the pack** |
| `tiles` | object, key → string | no | `{}` | keys from the twelve; values are pack-relative paths (below) |

`tile_size` is required because guessing it silently misaligns every tile. `name` is optional
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

A clean load prints one line:

```
texpack: using 'Default' (res://texturepacks/default) tile_size=16 tiles=12/12
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
success line through `GD.Print`. Noise is bounded: at most 12 tile lines + 1 per unknown field +
1 per discovery source.

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
| tile file missing | `tile '<key>': file not found: <rel> — using 'missing'` | that tile → `missing` |
| tile not a decodable PNG | `tile '<key>': not a decodable PNG: <rel> — using 'missing'` | that tile → `missing` |
| tile size ≠ `tile_size` | `tile '<key>': <w>x<h> does not match tile_size <n> — using 'missing'` | that tile → `missing` |
| `Texture2DArray` build returns non-`OK` | `texture array build failed — using procedural tiles` | procedural tiles for the whole pack |
| `missing` itself unavailable | same lines, tail `— using procedural colour` | procedural tile |
| unknown tile key / manifest field | `unknown … — ignored` | ignored |
| duplicate JSON key | *(none)* | last occurrence wins |
| extra files in the pack | *(none)* | ignored; the loader never enumerates |
| `selected.txt` missing / invalid | *(silent)* / `'<raw>' is not a pack name — ignoring` | next rung |
| rung 3 missing / invalid | same rows, `<source>` = `res://texturepacks/default` | procedural tiles; the rung-4 success line prints |

## Rendering: one material, one mesh path

There is no pack-present branch in the renderer. `VoxelWorld` replaces its shared
`StandardMaterial3D` with one shared `ShaderMaterial`; the loader builds a `Texture2DArray` and
calls `material.SetShaderParameter("tiles", array)`. That uniform and the three mesh arrays above
are the whole interface between the loader and the renderer.

```glsl
shader_type spatial;
render_mode unshaded, cull_back, depth_draw_opaque;

uniform sampler2DArray tiles : source_color, filter_nearest, repeat_disable;

varying float v_tile;

void vertex() {
    v_tile = UV2.x;                  // canonical tile index, written by the mesher
}

void fragment() {
    vec4 t = texture(tiles, vec3(UV, v_tile));
    ALBEDO = t.rgb * COLOR.rgb;      // COLOR is tint only
    ALPHA  = t.a;
    ALPHA_SCISSOR_THRESHOLD = 0.5;   // cutout: no blending, no sorting, depth stays opaque
}
```

- **Per-face selection** is `UV2.x` → array layer. A block's six faces may name six different
  keys (Grass does), and the mesher emits one quad per face with that face's index.
- **`COLOR` is tint only.** The mesher writes `FaceTint[face]` — `0.72, 0.72, 1.00, 0.45, 0.86,
  0.86`, the same linear numbers used today — and block identity lives *only* in the tile. For
  every block, including Bedrock and blocks adjacent to Air, `Blocks.ColorOf` must not appear in
  the mesh colour path; leaving any block-colour term in double-applies the colour. The mesh
  format stores vertex colour as RGBA8, so the tint reaches the shader quantized to 1/255.
- **`Blocks.ColorOf` is demoted to fallback-art source.** It still defines what the game looks
  like with no pack, but only as the colour table for the procedural tiles.
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

The loader synthesises the twelve tiles as 16×16 **sRGB 8-bit** images — the inverse of the
`SrgbToLinear()` that `Blocks.ColorOf` applies — so the sampler's `source_color` conversion lands
back on today's linear values:

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

**Chosen: `Texture2DArray`.** Twelve images, one sampler, per-vertex layer index, no UV rects, no
atlas packing, no bleeding — a tile is a file.

**Fallback: a single atlas image.** If array sampling ever fails on a target, the loader builds a
4×4 atlas of the same twelve images and the fragment shader maps `UV2.x` → cell rectangle
(`cell = floor(vec2(mod(idx,4), floor(idx/4)))`), then samples with `UV/4 + cell/4`. The mesher
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
| non-uniform tile sizes | per-tile size + one array per size class |
| PBR (normal/roughness/emission) maps | extra arrays and sampler inputs per key |
| mipmaps and anisotropic filtering | `generate_mipmaps` + sampler hints when minification shimmers |
| block definitions in packs (hardness, drops, new blocks) | a block registry file; art-only packs are the seam |
| pack archives / downloads | unpack outside the game; the loader reads directories |
| hot reload | reload on window focus once the texture build can rebuild |
| per-face UV orientation, mirroring, rotation | a per-face basis table; v1 art is direction-agnostic |
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
