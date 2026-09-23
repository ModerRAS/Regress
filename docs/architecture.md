# Architecture

Regress is an ECS (Friflo.Engine.ECS) voxel sandbox on Godot 4.7 / .NET 8. This document
describes the entity, component and system design, and — more usefully — the boundaries between
them and the ones that were deliberately not drawn.

## Entities: four kinds

| entity | count | created by | lifetime |
| --- | --- | --- | --- |
| **chunk entity** | ~98 resident (view distance 3 chunks × 64) | `VoxelWorld.CreateChunk`, driven by the streaming system | enters and leaves the view distance |
| **player entity** | exactly 1 | `Player._Ready` | never destroyed, never changes archetype |
| **mob entity** | ≤ 48 (`MobSystems.MaxMobs`) | `MobSpawner` (factory), one per planned surface cell | spawns on a ring around `Focus`, despawns past `DespawnRadius` or below `BedrockY - 64` |
| **block entity** | one per interactive block that is placed | `BlockEntityRegistry.Sync`, called from `VoxelWorld.SetBlock` | deleted when its byte stops being interactive, or when its chunk unloads |

There is no entity hierarchy, no relation and no prefab. `ChunkCoord` is 3D, so chunk Y is
unbounded.

## Components: fourteen data types, five tags

```csharp
// chunk: data
struct ChunkCoord   { int X, Y, Z; }                        // 3D, unbounded Y
struct ChunkBlocks  { byte[] Value; byte[] Orientation; }   // ChunkSize³ = 262144 bytes each, index = (y*ChunkSize + z)*ChunkSize + x; Orientation: per-voxel 24-rotation, 0 = no rotation
struct ChunkVisual  { MeshInstance3D[] Meshes; StaticBody3D[] Bodies; CollisionShape3D[] Shapes; int Cursor; ulong CollisionDone; }  // one entry per 16³ section; Cursor = next section to mesh, CollisionDone = per-section collision bits

// chunk: tags
struct NeedsMesh      : ITag { }
struct NeedsCollision : ITag { }
struct KeepAlive      : ITag { }

// player: data
struct PlayerBody   { CharacterBody3D Node; Camera3D Camera; }
struct PlayerState  { bool Flying, Creative; Block Selected; float Yaw, Pitch; byte PendingOrientation; float AppliedYaw, AppliedPitch; bool Applied; }
struct PlayerIntent { Vector3 Wish; bool Jump, Up, Down, Sprint, Mining; int Place; bool AutoWalk, AutoSprint, AutoMine; }
struct PlayerMining { Vector3I Target; float Progress; bool Active; }

// player: tags
struct PlayerTag : ITag { }

// mob: data
struct MobXform    { Vector3 Position; float Yaw; }           // feet centre + facing
struct MobVelocity { Vector3 Value; }
struct MobAi       { uint Rng; float TargetX, TargetZ, Retarget; }
struct MobVisual   { MeshInstance3D Node; }

// mob: tag
struct Mob : ITag { }

// block entity: data
struct BlockPos     { int X, Y, Z; }             // position-keyed identity; the entity carries its own cell
struct Interactable { InteractKind Kind; }       // the RMB dispatch key (None, Toggle)
struct ToggleState  { bool Open; }               // per-instance state, never in the ChunkSize³-byte arrays
```

`ChunkBlocks.Orientation` is the 24-element cube rotation group: every block stores one, a block
can take any orientation its type allows, the placement rule supplies the default and the player
cycles the allowed values with `Q`.

Every block type also carries an allowed-orientation policy (`Any`, `Upright`, `Axis`, `Fixed`):
the placement rule snaps a candidate orientation into the allowed set, and the rotate key
iterates only allowed values. `Grass` is currently `Upright` (its turf must stay on top); every
other block is `Any`.

### The rule for what becomes a tag and what becomes a field

> **Does a change to this state change *which systems should look at this entity*?**
> Yes → tag. No → field.

| state | form | why |
| --- | --- | --- |
| mesh stale / collision stale | **tag** | the entity must be picked up by a *different* system. In an archetype, "what work is pending" is a memory-layout fact that a query reads for free — and the tag *is* the state, so it cannot disagree with the work |
| `Flying`, `Selected`, `Yaw/Pitch` | **field** | read and written every frame by the same systems; changing them must not move the entity between archetypes |
| `KeepAlive` | **tag** | set once, only ever used as an unload filter, never read as a value |
| `Break`/`Place`/`Progress` | **field** | continuous or countable values |

Two component details are deliberate deviations from "keep components blittable":

- **`ChunkBlocks` holds `byte[]` references** rather than inline fixed buffers. 262144 bytes
  inline each would inflate every archetype and copy on every archetype move. Friflo returns
  components by `ref`, so the arrays are still mutated in place with no copy.
- **`ChunkVisual` holds Godot node handles** — an engine-aware component, and not the only one
  (`PlayerBody` holds the player node and camera; `PlayerIntent.Wish` and `PlayerMining.Target`
  are engine value types). It is a *handle*, never game state: no system reads gameplay facts out
  of it.

### Block entities: interactive blocks are entities

The chunk byte array stays authoritative for terrain and rendering. An interactive block is a
position-keyed entity carrying `BlockPos` + `Interactable` + a per-kind state component
(`ToggleState` for `Chest`); instance state never enters the ChunkSize³-byte arrays. Lookup is two
dictionary probes — chunk bucket, then cell — never a scan. `VoxelWorld.SetBlock` is the ONE choke
point that reconciles the two sides, so they cannot disagree, and `BlockEntityRegistry.Audit`
checks both directions. RMB ordering is decided by the pure `PlayerSystems.RightClickAction`
(Interact > Place) and used by `RequestPlaceAtCrosshair`, whose placement rules are otherwise
unchanged. No new system was needed — the sync lives in `SetBlock` and dispatch in the existing
build step — so the system counts in this document do not move. The interactive block is
`Block.Chest` (`Placement.Face` + `OrientationPolicy.Upright`, HUD `Chest: open`); `Plank` is no
longer interactive.

## Systems: four chunk systems, five player systems, three mob systems

**Chunks** — run from `VoxelWorld._Process`, in this order:

| system | query | budget | transition |
| --- | --- | --- | --- |
| Streaming | none, reads the focus | 4 chunk creations/frame | create / delete entities |
| Mesh | `Query<ChunkCoord, ChunkVisual>().AllTags(NeedsMesh)`, nearest first | **3 ms, shared** | `−NeedsMesh +NeedsCollision` |
| Collision | `Query<ChunkCoord, ChunkVisual>().AllTags(NeedsCollision)`, filtered by proximity | **3 ms, shared** | `−NeedsCollision` |

The mesh and collision unit is the **16³ section**, not the 64³ chunk: a chunk entity keeps its
`NeedsMesh`/`NeedsCollision` lifecycle, and `ChunkVisual.Cursor` / `ChunkVisual.CollisionDone`
track which of its 64 sections still need work. Generation, mesh and collision share one
per-frame deadline (`ChunkWorkBudgetMs = 3 ms`); every round still does at least one work item,
which is a progress guarantee independent of the deadline. The 16³ section is the same unit the
old 16³ chunk was, so per-unit cost is directly comparable across the scale change
([performance.md](performance.md)).

**Player** — driven from `Game`:

| system | phase | query | in → out |
| --- | --- | --- | --- |
| `PollInput` | `_Process` | `<PlayerIntent, PlayerState, PlayerBody>` | **the only place that reads `Input`** → writes intent |
| `Look` | `_Process` | `<PlayerState, PlayerBody>` | yaw/pitch → node rotations, guarded so an idle frame writes nothing |
| `Mine` | `_Process` | `<PlayerIntent, PlayerState, PlayerBody, PlayerMining>` | held LMB + hardness → break request |
| `Build` | `_Process` | `<PlayerIntent, PlayerState, PlayerBody>` | queued clicks → place request |
| `Move` | `_PhysicsProcess` | `<PlayerIntent, PlayerState, PlayerBody>` | intent + flying → velocity → `MoveAndSlide` |

**Mobs** — run from `VoxelWorld._Process` after the chunk systems:

| system | query | budget | effect |
| --- | --- | --- | --- |
| `Spawn` | `Query<MobXform>` count + the pure `MobSpawner.Plan` | 2 creations/frame, cap 48 | create/delete entities; the node is a handle the factory passes in |
| `Tick` | `Query<MobXform, MobVelocity, MobAi>.AllTags(Mob)` | **1 ms** | pure `MobAiRules.Decide` → gravity + `MobFits` AABB resolution → write state |
| `SyncVisuals` | `Query<MobXform, MobVisual>.AllTags(Mob)` | — | node position + yaw only |

Mobs are the deliberate exception to the player's movement design: no `CharacterBody3D`, no
collision shape, no `MoveAndSlide`. A mob is a few `GetBlock` calls against the same block API
the player uses, so 48 mobs add no bodies to the physics space (`--bench` keeps `physics step`
flat while `nodes` grows by one `MeshInstance3D` per mob).

Three rules apply to all of them:

1. **Query the archetype, do not scan and branch.** Pending work is a set membership test.
2. **Budget in milliseconds, not counts.** Per-chunk cost varies by 10x; any "do N per frame"
   scheduler is a latent hitch.
3. **Collect, then mutate.** Entity handles are gathered during a query and tags changed
   afterwards, because changing an archetype mid-iteration invalidates it.

### Archetype state machine

The chunk lifecycle *is* the data layout:

```
    CreateChunk
         │  Store.CreateEntity(coord, blocks, visual, Tags.Get<NeedsMesh>())
         ▼
 [Coord,Blocks,Visual] + NeedsMesh ──Mesh──► + NeedsCollision ──Collision──► complete
         ▲                                          │
         └────────── SetBlock edit: +KeepAlive ◄────┘
                        (MarkDirty: +NeedsMesh −NeedsCollision)
```

The player entity is created once with all four types and `PlayerTag`, and never migrates: it has
no pending work, only per-frame state.

A chunk leaves `NeedsMesh` only after all 64 sections are meshed. `NeedsCollision` stays until
every section's collision is decided (or is proven all-air), so a player walking can bring
sections that were outside the proximity gate into range and they are built then — dropping the
tag early would leave the player walking through a meshed-but-collisionless section.

## Decoupling: what talks to what

```
  Input ─────────────► [PollInput] ─────► PlayerIntent
  mouse/keyboard ────► Player.cs ───────► PlayerIntent / PlayerState
   (the only adapter)                         │
                        ┌────────────────────┤
                  [Look]│              [Move]│
                        ▼                    ▼
          PlayerBody.Node.Rotation   PlayerBody.Node.Velocity
                        │
                 [Mine]/[Build] ─► world.RequestEdit(EditRequest)
                                            │
                     VoxelWorld.ApplyPendingEdits()   ← the only world mutation
                                            │
                             archetype transition (tag change) — the only
                                 notification mechanism between systems
                                            │
                            [Mesh] ─────────┴────────► ChunkVisual.Mesh
                                            │
                          [Collision] ──────► ChunkVisual.Body/Shape
```

Only two channels exist between systems:

1. **Archetype transitions (tags)** — a notification, not a call.
2. **`VoxelWorld`'s block API** — the single entry point for changing the world.

What does *not* exist: a system holding a reference to another system, a system calling another
system, or a component holding the world. `Mine`/`Build` receive the world as a parameter;
`PlayerBody` holds only nodes; the physics space is asked of the node.

**Ordering is the only coupling, and it is explicit.** `PollInput → Look → Mine → Build` in
`_Process`, `Move` in `_PhysicsProcess`. Systems declare no dependencies; the caller does.

### What is deliberately not decoupled

- **Godot stays at the edges, not outside.** `ChunkVisual` puts engine handles into the
  archetype, and `PollInput` in `PlayerSystems.cs` is the only place that reads gameplay `Input`
  (`Player.cs` only toggles mouse capture). A full Godot/ECS separation
  would need a render-entity registry — indirection with no payoff in a single-threaded game.
- **The world owns both the store and its systems**, and the systems are explicit ordered calls
  rather than `SystemBase` nodes on a scheduler. With three chunk systems whose order *is* their
  budget priority, a scheduler adds indirection and a place for the order to drift.
- **The spatial index lives outside the ECS.** `Dictionary<Vector3I, Entity>` maps chunk
  coordinates to entities; ECS provides no spatial indexing, and every ECS game carries one of
  these alongside.

## Block edits are a request pipeline

Gameplay never mutates the world; it proposes an edit and the world decides.

```
  LMB held ──► PlayerIntent.Mining ──► Mine (S) ──► RequestEdit(Break, cell)
  RMB click ─► PlayerIntent.Place  ──► Build (S) ─► interactable target? interact : RequestEdit(Place, ...)
                                                          │
                          ApplyPendingEdits()  (top of the next frame, before meshing)
                                                          │
                     BlockBehaviors.Collect ──► the blocks actually removed ──► SetBlock
```

- **`BlockBehaviors.Collect`** expands one request into the set of blocks it removes, bounded by
  `MaxBlocksPerBreak` (derived from `TerrainGenerator.MaxTreeBlocks()`). Vein mining, leaf decay
  and falling sand are the same hook. The rule belongs to the block, so every client of the
  pipeline gets it.
- **Tree identity comes from the generator spec, never from block type or connectivity.**
  `TerrainGenerator.TryGetTree(column, out TreeSpec)` is a pure function of the seed and the
  column, and `TreeSpec.Contains(x, y, z)` answers whether a cell belongs to that tree — zero
  storage, no provenance bits on voxels. Inferring a tree from "these cells are `Wood` and
  connected" would chain a player's log cabin into one felling; the spec does not, so breaking one
  cell of a player-built wall removes exactly that cell.
  - **Known boundary:** a player-placed block *inside* a generated tree's spec volume is
    indistinguishable from a generated cell and is taken with the tree. That is accepted, not a
    bug; the rejected alternative — a provenance bit/array per voxel ("this cell was player
    placed") — costs a second per-voxel store and pollutes the block storage layer. Separating
    blocks from drops is orthogonal: drops are the *product* of a break
    (see [roadmap.md](roadmap.md)), not the way its range is decided, so the spec does not block
    them.
  - **Upgrade path (not now):** if a tree ever needs its own state (health, regrowth), it becomes
    a per-tree ECS entity — the same pattern as block entities (`BlockPos` + state, one entity per
    thing). No voxel-store changes.
- **General rule:** when a world-generated structure must be treated as one thing, align it
  through a generator spec (pure function, zero storage) — never by marking voxels with their
  origin. Trees are the first instance; boulders, ore veins and village houses follow the same
  shape.
- **`Blocks.HardnessOf`** gives seconds to break by hand (negative = unbreakable). `PlayerMining`
  accumulates `delta / hardness` on the crosshair block and only emits a request at 1.0.
- **Placement legality lives in exactly one function**, `PlayerSystems.RequestPlaceAtCrosshair`,
  called by both the interactive and the scripted path: `RMB click -> Build -> interactable target? interact : RequestEdit(Place, ...)`.
- Requests are applied before meshing in the same frame, so a multi-block break across chunk
  borders is just N `MarkDirty` calls that the existing systems already handle.
- **Known behaviour: edit application is not budgeted, only the work it triggers is.** One frame
  drains the whole request list (`RequestEdit` only appends; the applier loops it and clears it),
  while generation, meshing and collision run against the shared `ChunkWorkBudgetMs = 3 ms`
  deadline. A script that queues thousands of breaks in one frame therefore empties the blocks in a
  single frame while its chunk is still being re-meshed section by section — **a batch edit can
  outrun the collision rebuild.** Any consumer that digs and then drops something into the hole
  must poll the *post-edit* freshness itself (the demo's dig gate does exactly this: it holds the
  body until every section the shaft spans carries the new collision *and* a ray down the emptied
  shaft hits nothing). This is a test-script extreme; a player breaking blocks one at a time cannot
  reach it.
- **Invariant: the block under the player's feet must be breakable, whether or not its section
  has been meshed.** Collision for a meshless (fully solid or buried) section falls back to a
  `BoxShape3D`, so the player can stand on terrain the mesher has not touched; the break path must
  decide from block data alone and never from `ChunkVisual.Meshes` / `Cursor`. "Stand on it, cannot
  mine it" is a silent regression class and must stay asserted headlessly.

## The ×4 scale conversion rule

The world is built at four times the original linear scale: a 64³ chunk of 16³ sections replaces
the 16³ chunk, and every length, velocity and acceleration in the gameplay layer was multiplied
by four so the world *feels* the same (a jump is the same number of body lengths, a tree the same
number of bodies tall). Times and counts are not scaled: a second is still a second, one break is
still one request.

> **lengths ×4, velocities ×4, accelerations ×4, times ×1, counts ×1.**

| quantity | before | after | note |
| --- | --- | --- | --- |
| chunk / section | 16³ chunk | 64³ chunk = 4³ sections of 16³ | storage/streaming unit vs mesh/collision unit |
| terrain amplitude | 22 | 88 | |
| noise frequency | 0.008 | 0.002 | wavelength ×4 |
| bedrock | −64 | −256 | |
| sand threshold | sea level −4 | sea level −16 | |
| stone band | surface −3 | surface −12 | |
| tree trunk | 1×1×6 | 4×4×24 | `TrunkHalfWidth = 2` |
| tree height / canopy | +2 / radius 1–2 | +8 / radius 4,8 (dy −4..8) | `MaxTreeHeight = 32` |
| tree block budget | 256 | 5578 | `TerrainGenerator.MaxTreeBlocks() * 2`, derived |
| fell radius | 5 | 20 | |
| player capsule | r 0.35 × 1.8 | r 2.0 × 8.0 | 4×4×8 cells |
| player eye / body box | 1.62 / 0.7×1.9×0.7 | 6.48 / 2.8×7.6×2.8 at offset (1.4, 0.4, 1.4) | |
| reach / walk / sprint / fly | 6 / 5.5 / 9 / 14 | 24 / 22 / 36 / 56 | |
| jump / gravity | 8.5 / 26 | 34 / 104 | |
| mob half-width / height | 0.4 / 0.8 | 1.6 / 3.2 | |
| mob speed / react / wander / arrive | 3 / 12 / 8 / 1.2 | 12 / 48 / 32 / 4.8 | |
| mob probe / depenetration | 0.85 / 8 | 3.4 / 32 | |
| mob spawn ring / despawn | 24 + 8 / 56 | 96 + 32 / 224 | |
| camera far | 1200 | 4800 | derived |
| `BlockedAhead` eye offset | 0.9 | 3.6 | derived |
| `TeleportToSurface` landing offset | +0.5 | +2.0 | derived |
| mob step-up / recovery sidestep / near-mob | 1 / 1 / 1.5 | 4 / 4 / 6 | derived |
| bedrock-fall threshold | −16 | −64 | derived |
| `ChunkWorkBudgetMs` / `MobWorkBudgetMs` | 3 / 1.0 ms | 3 / 1.0 ms | **not scaled**: time |
| `RetargetSeconds`, `TargetAttempts`, `Epsilon`, `SelectedPerEight`, max mobs, per-frame counts, hash shifts, 24 orientations, ±0.5 crosshair | — | — | **not scaled**: times and counts |

The 4x4x8 body cannot descend a 1-wide shaft. At the shaft wall (r = 0.5 from the body axis) the
capsule's bottom sits `2 - sqrt(4 - 0.25) = 0.0635` above the feet, so a 1-wide hole lets the body
sink at most 0.064 blocks; sinking one block needs a shaft about 3.46 wide. A shaft exactly as wide
as the body (4) is not enough either: its walls sit at r = 2.0 = the capsule radius,
`2 - sqrt(4 - 4) = 2`, and the body wedges after about two blocks (measured in `--demo`: 40 layers
excavated at 4x4, sank -0.6). "Dig one column straight down" therefore no longer lowers the player
at the ×4 body size: a ×4 consequence, not a bug. The `--demo` dig-down gate digs a 5x5 shaft
(25 cells per layer, walls at r = 2.5) for this reason.

Block hardness was scaled ×0.25 — the inverse of the linear scale — so a hand mines the same
*physical* depth per second: Stone 0.375 s, Dirt 0.125, Grass 0.15, Sand 0.125, Wood 0.5,
Plank 0.5, Leaves 0.05, Chest 0.5, Pumpkin 0.25; Air and Bedrock are unbreakable (−1). Leaves at
0.05 s is near-instant by design, not a bug: 1/4 the linear size makes a single block give way
quicker, which is the accepted look of the finer granularity.

The memory consequence: a 16³ chunk held two 4096-byte arrays (8 KB); a 64³ chunk holds two
`ChunkSize³ = 262144`-byte arrays (512 KB) — 64× per chunk. That factor only becomes the whole
story when the *chunk count* is unchanged (which also means the world's physical size grew ×4 per
axis); the residency arithmetic that turns it into the shipped 27× is in
[performance.md](performance.md).

## Known weaknesses

1. **`ChunkVisual` is on every chunk entity**, including buried chunks that will never render —
   each carries three lazily-allocated 64-entry node arrays for nothing. Splitting rendering into
   its own component, added lazily on first mesh, is the fix.
2. **The player entity is created by the view** (`Player._Ready`). Factory or system should own
   entity creation; the view should only supply node handles. Mobs do not repeat this: `MobSpawner`
   is the factory and the view node is just the handle it stores.
3. **Systems are static methods taking the store as a parameter**, so there is no per-system
   state or configuration.
4. **The mesher allocates ~430 KB per section** (lists plus `ToArray()` marshalling, with the
   per-vertex UV and tile-index streams) — the same 16³ work the old chunk did, now ×64 sections
   per chunk. The millisecond budget absorbs it; a two-pass count-then-fill mesher would remove
   the garbage.
5. **Chunk-border face culling depends on generation timing.** `VoxelWorld.GetBlock` falls back to
   the heightmap (`Air` above the surface) for a not-yet-generated neighbour chunk, and the mesher
   culls border faces against it, so tree canopies and other above-surface art at chunk borders
   differ between two otherwise identical runs. Rendering-level A/B evidence must use a static
   close-up or a region whose neighbours are fully generated; wide-shot deltas at foliage/chunk
   borders are this effect, not flicker of whatever is under test.
