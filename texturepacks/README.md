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
`posx, negx, top, bottom, posz, negz`. A pack that names only the twelve base keys is completely
unaffected; an override's layer is `12 + blockOrdinal*6 + faceIndex`, in the order the spec's
completeness table lists.

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
