# Roadmap

## Done

- **Voxel world** — 64×64×64 chunks of 16³ sections, unbounded vertical extent, negative terrain,
  bedrock floor, heightmap terrain with deterministic trees that cross chunk borders. The ×4
  feel-preserving scale conversion (lengths/velocities/accelerations ×4, times/counts ×1) is
  tabulated in [architecture.md](architecture.md).
- **Streaming** — plan / generate / mesh / unload, nearest first, all budgeted in milliseconds.
- **Collision** — trimesh from the visible mesh near the player, `BoxShape3D` for fully buried
  chunks that have no visible mesh, recovery if the player ends up inside geometry.
- **Player** — walk, sprint, jump, fly, mouse look, respawn.
- **Block edits as a request pipeline** — hardness-based mining duration, tree felling via
  `BlockBehaviors`, placement legality in one place.
- **ECS** — chunk lifecycle expressed as archetypes; see [architecture.md](architecture.md).
- **Test harness** — `--selftest` (count 待测 after the 64³ scale change), `--demo` (scripted walk / build / mine),
  `--bench` (five-phase performance run with per-system breakdown), `--shot`, `--map`,
  `--freecam`, `--frozen`, `--diag`.
- **Texture packs v1** — `pack.json` + twelve keyed tiles, four-rung discovery ending in
  procedural tiles, one shader for every chunk; see [texture-packs.md](texture-packs.md).
- **Mobs** — up to 48 deterministic wanderers. The AI is a pure, engine-free function over
  `IBlockReader` (`MobAiRules.Decide`), movement is gravity + AABB instead of `CharacterBody3D`,
  spawn/despawn follow a seeded square ring around `Focus`.
- **Interactive blocks as entities** — a placed interactive block is a position-keyed ECS entity (`BlockPos` + `Interactable` + per-kind state) found through an O(1) chunk-bucketed registry; the ChunkSize³-byte chunk array stays authoritative for terrain and render, `VoxelWorld.SetBlock` is the single sync point, and RMB on an interactive target interacts instead of placing. `Block.Chest` is delivered (toggle + a HUD line); the append-only vocabulary extension process is documented in [texture-packs.md](texture-packs.md). See [architecture.md](architecture.md).
- **Pumpkin** — the append-only recipe's second worked example: upright-only, a carved front on
  local +Z (`pumpkin_posz`), six frozen fallback cells on `sand`, and no interaction cost; see
  [adding-a-block.md](adding-a-block.md).

## Open work

### 1. Unlimited dimensions

The seam is already in place and is a bug fix as much as a feature:

- `TerrainGenerator` is a **per-world instance** owning its seed, sea level, amplitude and
  bedrock level. It used to be a static noise field, which would have made every dimension
  generate identical terrain.
- `VoxelWorld` owns its own `EntityStore`, chunk index, column cache, focus and terrain. It has
  no knowledge of being "the" world.

```csharp
var nether = new VoxelWorld { Terrain = new TerrainGenerator(seed: 99, seaLevel: 32, heightAmplitude: 12) };
```

Still missing, and both are containers around what exists:

- a manager that owns and ticks several worlds;
- visibility toggling so only the active dimension renders (each world's chunk nodes are its own
  children, so this is a `Visible` toggle per world node);
- per-dimension streaming policy (the Nether wants a shorter view distance and a different
  vertical band), which means making the current constants into `TerrainGenerator`-style
  per-world values.

Scope: small. No redesign.

### 2. Block break parameters, tools and drops

Hardness and break time exist. The natural next steps all fit the existing request pipeline:

- `BlockInfo` gains tool type and drop (a block, or nothing);
- `EditRequest` already carries the `Actor`, so a tool check has what it needs;
- drops become item entities — which is the point at which ECS starts paying for itself
  properly, because items are many, short-lived, and homogeneous.

### 3. Parallel work — and why per-world tick is the wrong axis

Measured: the whole ECS tick is **0.03 ms/frame** in steady state (0.27 ms with 48 mobs, of
which the mob systems are 0.13 ms), while per-chunk streaming work
is ~1.0 ms (mesh 0.61 + collision 0.32 + gen 0.07). Parallelising N *worlds* therefore
parallelises a few hundredths of a millisecond per world, and the cost worth parallelising happens
**inside one world** and stays serial.

If it is ever needed, the seam is per-chunk, not per-world:

1. **Split pure work from scene work.** `ChunkMesher.Build` must produce plain arrays on a worker;
   `ArrayMesh`, `MeshInstance3D`, `StaticBody3D` and `CollisionShape3D` creation must happen on
   the main thread, drained from a completion queue. Godot's scene tree and physics server are not
   thread-safe — this is the hard boundary, and no ECS design removes it.
2. **Make shared state per-world or per-thread.** Done: `TerrainGenerator` is per world,
   `ChunkMesher`'s scratch buffer is `[ThreadStatic]`. Not done: the `Prof` accumulators and the
   `Game`/`Player` singletons.
3. **Leave collision on the main thread.** `Mesh.CreateTrimeshShape()` is a Godot call with the
   least certain thread-safety and the smallest payoff from moving.
4. **Measure first.** The honest trigger for this work is a measured streaming cost that exceeds
   the frame budget — not an architectural preference.

### 4. Known weaknesses to clean up

Carried from [architecture.md](architecture.md#known-weaknesses):

- `ChunkVisual` sits on every chunk entity, including buried chunks that never render.
- The player entity is created by the view (`Player._Ready`) rather than by a factory.
- The mesher allocates ~430 KB per chunk; a two-pass count-then-fill mesh would remove it.
- No persistence: player-built chunks are pinned in memory rather than written to disk, so the
  world resets between sessions.
- **App icon**: iOS currently uses a placeholder game tile and Android keeps the engine default launcher icon. Both need a real 1024×1024 icon from the user before release. A first-pass, repo-generated 1024×1024 icon now ships (`tools/gen_app_icon.py` → `assets/icon_1024x1024.png`, plus native 192×192 / 432×432 Android renders, wired into the iOS and Android presets) and remains user-replaceable.

## Optional / user decides

- **A visible player mesh (third person / arms).** This round specified the player body as
  numbers — capsule 2.0 × 8.0 and body box 2.8 × 7.6 × 2.8 (4×4×8 voxels) — and asserts them
  headlessly; first-person reading is the indirect check. Whether the player should *see* their own
  body is a presentation choice only the user can make. Not started; do not self-assign.
