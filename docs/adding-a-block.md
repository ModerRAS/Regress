# Adding a block

The block vocabulary is **append-only**: existing `Block` values, the 12 canonical tile keys and
the front of `Frozen` never move. Adding a block is "one line of policy + six images", plus the
append-only rows and the derived counts that follow. Chest is the worked example (Phase B);
Pumpkin is the next one.

## The two traps Phase B paid for

### 1. Front art goes on `<block>_posz` (local +Z), never `negz`

- `Orientation.Face(frontDir, upDir)` returns the rotation whose **local +Z image is
  `Dirs[frontDir]`** and whose local +Y image is `Dirs[upDir]` (`src/Orientation.cs`, doc comment).
- `Placement.Face` sets `front = FaceOfNormal(CameraBasis(yaw, pitch).Z)` — the camera's +Z
  points back at the player, so `front` is the world face that faces the player
  (`BlockBehaviors.PlaceOrientationFor`, `src/EditRequests.cs`).
- The mesher picks the tile for a world face by `Orientation.LocalFace(orientation, face)`
  (`src/ChunkMesher.cs`), so the player-facing quad resolves to local +Z.

Conclusion: the visible face samples `<block>_posz`. Chest paints the latch in
`tile_chest_posz`; `tile_chest_negz` is the plain back. Painting the front on `negz` shows the
player the back.

For `OrientationPolicy.Upright` types (Chest, Pumpkin), `PlaceOrientation` replaces the candidate
with `Orientation.Axis(Face.Top, YawQuadrant(yaw))`, which also puts local +Z back toward the
camera. Same conclusion.

**Math-side assertion (selftest).** With `placed = PlaceOrientation(block, clickedFace, yaw, pitch)`
and `cameraFacing = FaceOfNormal(CameraBasis(yaw, pitch).Z)`:

```csharp
Check(Orientation.LocalFace(placed, cameraFacing) == Face.PosZ,
    "the front art is the tile the player sees");
```

Never write `LocalFace(placed, cameraFacing) == Face.NegZ`: that is always false, because
`Orientation.Face`'s contract is local +Z → `Dirs[frontDir]` and `frontDir` is the player-facing
world face. A search for local `NegZ` finds the back face.

**Art-side assertion (generator `--verify`).** The selftest asserts geometry, not pixels, so
`--verify` must assert the PNGs: `<block>_posz` contains the front motif (latch/face),
`<block>_negz` must not, and the other four faces must not. Self-check the check by temporarily
swapping the two painter functions in `tools/gen_default_pack.py` — `--verify` must then FAIL.
Do not merge a front-facing block without this.

Why both: every previous `Upright` block (Grass) is yaw-symmetric, so a wrong front is invisible
to the eye and to any rotation-only assertion. A front motif is the first thing that exposes the
error, so the geometry net and the pixel net are independent and both required.

### 2. `TileIndex` / `LayersFor` are resolved; the docs table is the fallback

`TexPack` computes `_resolved[cell] = provided[KeyCount + cell] ? KeyCount + cell : Frozen[cell]`
(`src/TexPack.cs`). Both layers are real:

- **Resolved**: with a pack that declares the override (the shipped default pack does), for the
  new block's cell `c = blockOrdinal * 6 + face`:
  `TexPack.TileIndex(block, face) == 12 + c` and `LayersFor(block, face) == [12 + c]`.
- **Fallback**: with a pack that does not declare that cell, and in the procedural/no-pack state
  (`_resolved = Frozen`), `TileIndex(block, face) == Frozen[c]`. Chest = 8 (`plank`) on all six
  faces.

The completeness table in `docs/texture-packs.md` documents the **fallback** (`TileKey` /
`TileIndex`); at runtime the bundled pack resolves those cells to the override layers. Assert
both: the fallback row against a frozen literal table in a base-only state (Chest:
`CheckTileIndexMap`/`CheckFaceOverrides`), then `TexPack.Load("res://texturepacks/default")` and
the resolved layers (`12 + c`).

## Derived counts (keep them honest)

| quantity | formula | Chest (now) | next block |
| --- | --- | --- | --- |
| renderable blocks | `TexPack.Renderable.Length` | 9 | 10 |
| cells | `6 × renderable` | 54 | 60 |
| override layers | `12 .. 12 + cells - 1` | 12..65 | 12..71 |
| `TexPack.LayerCount` | `KeyCount + FaceKeys.Length` = `12 + cells` | 66 | 72 |

`OVERRIDE_LAYER_BASE = len(TILE_KEYS) = 12`. Adding a 13th canonical key shifts every override
layer — that is a reorder, not an append. Never do it; pick one of the 12 keys as the fallback
(wood-like → `plank`, stone-like → `stone`).

## Steps

1. **Pick the fallback canonical key.** One of the 12 in `TILE_KEYS`/`TexPack.Keys`; wood-like →
   `plank`, stone-like → `stone`. Never add a canonical key.
2. **`src/Blocks.cs`.** Append the enum member at the **end** of `Block` (byte values only grow;
   ordinal == enum position == cell base). Add one row at the end of each `(int)Block`-indexed
   table: `Hardness` (seconds; negative = unbreakable), `PlacementRules`, `Policies`. A missing
   row is either an out-of-range crash or the wrong behaviour. If the block is selectable, append
   it to `Palette` — key bindings are derived (`Player.HandleKey` checks
   `slot < Blocks.Palette.Length`), so the next digit key appears automatically. Front-facing
   blocks use `Placement.Face` + `OrientationPolicy.Upright`.
3. **Sweep enum-sized allocations and loops.** Any `new byte[N]` / `new int[N]` sized by the block
   count, and any literal old-last bound, must grow. Phase B's hard-coded case:
   `PlayerSystems.BuildRotateOrder` used `new byte[9][]`, so Q/E on Chest threw
   `IndexOutOfRangeException`; it now uses
   `new byte[System.Enum.GetValues<Block>().Length][]`. `SelfTest.LastBlockIndex` is derived
   (`(int)Enum.GetValues<Block>()[^1]`) — keep it derived.
   ```bash
   grep -rn "new byte\[[0-9]\|new int\[[0-9]" src tools
   grep -rn "(int)Block\.\(Bedrock\|Chest\)" src tools   # literal old-last bounds
   ```
   A `for (b = 1; b <= (int)Block.Bedrock; b++)` loop silently skips the new block's coverage.
4. **`src/TexPack.cs`.** Append to `Renderable` (order == cell order, never reorder). Append
   **exactly 6** fallback indices to `Frozen`, in face order `posx, negx, top, bottom, posz,
   negz`; the first N values stay byte-identical (append-only). `KeyCount`, `FaceSuffix`,
   `FaceKeys` and `LayerCount = KeyCount + FaceKeys.Length` are derived: verify they follow,
   never hardcode them.
5. **`tools/check_completeness.py`.** Append to `RENDERABLE_BLOCKS`; add the fallback to
   `UNIFORM`, or a `rule_key` branch for per-face fallback (Grass/Wood style); `EXPECTED_ROWS += 6`.
   `OVERRIDE_LAYER_BASE`, `FACE_KEYS` and `LAYER_COUNT` derive from `RENDERABLE_BLOCKS` — verify,
   do not hardcode.
6. **`docs/texture-packs.md`.** Add 6 rows after the previous last block, in table face order,
   `TileKey`/`TileIndex` = fallback, `FaceKey` = `<block>_<suffix>`. Fix every derived number:
   cell count, `LayerCount`, both `N/M per-face overrides` lines, the hostile ceiling
   (`keys × 16` layers and KiB), and VRAM numbers derived from the override count. Use the
   formulas above (Chest: 54 / 66; next block: 60 / 72). The checker cross-checks the table
   against `Frozen` and `FaceKeys`.
7. **Art.** `tools/gen_default_pack.py`: add the 6 keys to `OVERRIDE_KEYS` and 6 painters to
   `TILES`; deterministic integer hash only (`_hash`/`rnd`/`pnoise`; no time, no RNG). All modes
   iterate `ALL_KEYS`, so `generate`/`verify`/`wrap`/`ascii`/`sheet`/`imports` pick the new keys up.
   `--verify` must include the motif signature check from trap 1. Add the 6 entries to
   `texturepacks/default/pack.json` too. The 12 base PNGs stay byte-identical. Ship each PNG at
   `tile_size` (16×16 → size class 0); any other size adds a size class (cap 4) and shifts VRAM.
8. **Prove determinism.** Run the generator twice and compare the sha256 of the 6 new PNGs, then:
   ```bash
   python tools/gen_default_pack.py && git diff --exit-code -- texturepacks/default/tiles
   ```
9. **Selftest assertions (`src/SelfTest.cs`).**
   - Append-only proof: the old fallback literals are unchanged cell by cell, and the new block's
     6 cells equal its fallback index (Chest: 8).
   - Override wins: with the default pack, `LayersFor(block, f) == [12 + c]` and
     `Sources[12 + c] == TileSource.Png`.
   - Resolved vs fallback: `TileIndex` equals the fallback only in a pack/state that does not
     declare the cell (trap 2).
   - Do not treat "the default pack has no override" as a proxy for "no stale override residue".
     A test that expresses "the previous pack's overrides are gone" as "every cell == the pure
     base frozen table" false-fails once the shipped default pack legitimately carries the new
     block's six overrides (Phase B: `a pack-less fall-through clears the override`). Assert the
     two facts separately: (a) stale residue is gone (`TileIndex(oldBlock, Face.Top) == its base
     index`), and (b) the current pack's expected resolution — default pack = frozen table plus
     the new block's `12 + c` cells, `Procedural()` = the pure frozen table. When landing a block,
     grep for assertions that assume the default pack's override count is 0.
   - Front-face orientation: the `Face.PosZ` assertion from trap 1.
   - If interactive: `KindOf(block) != InteractKind.None` plus one `Interact` state flip, and
     `KindOf(old stand-in) == None` so the stand-in cannot linger.
10. **Interactive block.** One line in `BlockInteractions.KindOf` (plus components only if the
    block needs new state). The registry, `Sync`, audit and RMB dispatch are unchanged.
11. **Orientation.** Front-facing → `Placement.Face` + `OrientationPolicy.Upright`. The local
    face the art goes on is decided by the trap-1 assertion, not by eye.
12. **Guards.**
    ```bash
    python tools/check_completeness.py --table docs/texture-packs.md
    # cells: N rows / N required
    # overrides: M keys, layers 12..12+N-1, LayerCount=12+N
    # PASS
    dotnet build                                  # 0 warnings, 0 errors
    godot-mono --headless --path . -- --selftest  # PASS
    git diff --exit-code -- .github/workflows     # release smoke lines unchanged
    ```
    The release smoke grep counts base keys only (`tiles=12/12`); overrides never move it.
13. **Import hygiene.** If `godot --import` leaves whitespace-only `.cs` changes, restore them
    with `git checkout --` before committing. `python tools/gen_default_pack.py --imports` strips
    the `uid=` line from tracked base `.import` files — `git checkout --` those too. Commit only
    your own new files' `.uid` companions, never the ones for untouched files.

## Worked example: Chest (Phase B)

| file | change |
| --- | --- |
| `src/Blocks.cs` | `Chest` appended after `Bedrock`; `Hardness` 2.0; `Placement.Face`; `OrientationPolicy.Upright`; appended to `Palette` |
| `src/PlayerSystems.cs` | `BuildRotateOrder`: `new byte[9][]` → `new byte[System.Enum.GetValues<Block>().Length][]` |
| `src/TexPack.cs` | `Renderable` += `Block.Chest` (ordinal 8); `Frozen` += six `8` |
| `tools/check_completeness.py` | `RENDERABLE_BLOCKS` += `Chest`; `UNIFORM["Chest"] = "plank"`; `EXPECTED_ROWS = 54` |
| `docs/texture-packs.md` | 6 Chest rows; counts 54 / 66; hostile ceiling 66×16 = 1056 |
| `tools/gen_default_pack.py` | 6 `tile_chest_*` painters + `OVERRIDE_KEYS` + `TILES`; motif check in `--verify` |
| `texturepacks/default/pack.json` | 6 `chest_*` entries |
| `src/SelfTest.cs` | V-series append-only/resolved/art checks, C-series interaction, C7 front-face + art nets |

Result: `cells: 54 rows / 54 required`, `overrides: 54 keys, layers 12..65, LayerCount=66`,
PASS; `dotnet build` clean; `--selftest` PASS.
