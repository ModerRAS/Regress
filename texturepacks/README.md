# Texture packs

The bundled default pack lives here, and this directory is the format's home in the repo.
The v1 format is specified in [`docs/texture-packs.md`](../docs/texture-packs.md).

- `default/pack.json` — the manifest the game loads with no configuration.
- `default/tiles/` — the twelve PNGs the manifest names, all committed. The default pack loads
  12/12; regenerate them with `python tools/gen_default_pack.py` (Python 3 stdlib only,
  deterministic bytes). Tiles are imported lossless (`compress/mode=0`).

## Pack author quick start

A pack is a directory with a `pack.json` and the PNGs it names. Twelve base keys, fixed indices
— see the spec's completeness table. Every tile is the same `tile_size × tile_size`, RGBA, and
`missing` is the fallback for anything absent or invalid.

Packs may also override individual cube faces with optional `<block>_<suffix>` keys
(`stone_posx`, `leaves_negz`, …), where `<block>` is a renderable block and `<suffix>` is one of
`posx, negx, top, bottom, posz, negz`. A pack that names only the twelve base keys needs no
changes; in the no-variant baseline an override's layer is `12 + blockOrdinal*6 + faceIndex`, in
the order the spec's completeness table lists — a variant value shifts later layers, so never
hard-code absolute layer numbers (see below).

A tile value may also be an array of up to 16 strings — **variants** of that tile, first entry
primary, e.g. `"stone": ["tiles/stone_0.png", "tiles/stone_1.png"]` — and the pack then picks one
per absolute world position and local face with a deterministic hash, breaking up visible
repetition. Each key occupies one consecutive layer per declared variant (a bad variant keeps its
slot and shows `missing`; only a key with no decodable variant falls back to one slot), so
a manifest with no arrays keeps the byte-identical old layout while `LayerCount = 60` remains the
no-variant baseline, not an array size; only `missing`'s first valid variant is the one fixed
fallback image for degraded slots, and the worst case (all 60 keys × 16 variants) is 960 layers
≈ 960 KiB. See "Texture variants" in the spec.

Per-face tiles name a per **local** face, and a placed block now carries one of the 24 cube
rotations, so its tiles and their in-face UVs rotate with the block: a letter or arrow stays
upright and un-mirrored on every face. The three v1 bases that were mirrored as seen from
outside (`posx` and `negz` in `u`, `bottom` in `v`) were normalised, so pixels on those three
faces are mirrored against earlier builds.

```json
{
  "version": 1,
  "name": "My Pack",
  "tile_size": 16,
  "tiles": { "stone": "tiles/stone.png", "missing": "tiles/missing.png" }
}
```

## Install and select

1. Copy the pack directory to `%APPDATA%\Godot\app_userdata\Regress\texturepacks\<name>\`.
2. Put `<name>` on line 1 of
   `%APPDATA%\Godot\app_userdata\Regress\texturepacks\selected.txt`.
3. Launch. The startup line names the pack actually used.

For a one-off, `--pack=<dir>` overrides the file. Pack changes need a restart — v1 has no hot
reload. Invalid data never crashes the game: a bad tile falls back to `missing`, a bad manifest
falls back to the next source, and the last rung is procedural colours.
