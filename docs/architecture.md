# Architecture

Regress is an ECS (Friflo.Engine.ECS) voxel sandbox on Godot 4.7 / .NET 8. This document
describes the entity, component and system design, and — more usefully — the boundaries between
them and the ones that were deliberately not drawn.

## Entities: three kinds

| entity | count | created by | lifetime |
| --- | --- | --- | --- |
| **chunk entity** | ~230 resident | `VoxelWorld.CreateChunk`, driven by the streaming system | enters and leaves the view distance |
| **player entity** | exactly 1 | `Player._Ready` | never destroyed, never changes archetype |
| **mob entity** | ≤ 48 (`MobSystems.MaxMobs`) | `MobSpawner` (factory), one per planned surface cell | spawns on a ring around `Focus`, despawns past `DespawnRadius` or below `BedrockY - 16` |

There is no entity hierarchy, no relation and no prefab. `ChunkCoord` is 3D, so chunk Y is
unbounded.

## Components: eleven data types, five tags

```csharp
// chunk: data
struct ChunkCoord   { int X, Y, Z; }                        // 3D, unbounded Y
struct ChunkBlocks  { byte[] Value; byte[] Orientation; }   // 4096 bytes each, index = (y*16 + z)*16 + x; Orientation: per-voxel 24-rotation, 0 = no rotation
struct ChunkVisual  { MeshInstance3D Mesh; StaticBody3D Body; CollisionShape3D Shape; }

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

- **`ChunkBlocks` holds `byte[]` references** rather than inline fixed buffers. 4096 bytes
  inline each would inflate every archetype and copy on every archetype move. Friflo returns
  components by `ref`, so the arrays are still mutated in place with no copy.
- **`ChunkVisual` holds Godot node handles** — an engine-aware component, and not the only one
  (`PlayerBody` holds the player node and camera; `PlayerIntent.Wish` and `PlayerMining.Target`
  are engine value types). It is a *handle*, never game state: no system reads gameplay facts out
  of it.

## Systems: four chunk systems, five player systems, three mob systems

**Chunks** — run from `VoxelWorld._Process`, in this order:

| system | query | budget | transition |
| --- | --- | --- | --- |
| Streaming | none, reads the focus | 4 chunk creations/frame | create / delete entities |
| Mesh | `Query<ChunkCoord, ChunkVisual>().AllTags(NeedsMesh)`, nearest first | **3 ms** | `−NeedsMesh +NeedsCollision` |
| Collision | `Query<ChunkCoord, ChunkVisual>().AllTags(NeedsCollision)`, filtered by proximity | **3 ms** | `−NeedsCollision` |

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
  RMB click ─► PlayerIntent.Place  ──► Build (S) ─► RequestEdit(Place, cell, block)
                                                          │
                          ApplyPendingEdits()  (top of the next frame, before meshing)
                                                          │
                     BlockBehaviors.Collect ──► the blocks actually removed ──► SetBlock
```

- **`BlockBehaviors.Collect`** expands one request into the set of blocks it removes, bounded by
  both radius and `MaxBlocksPerBreak`. Felling a tree takes the connected trunk and its canopy.
  Vein mining, leaf decay and falling sand are the same hook. The rule belongs to the block, so
  every client of the pipeline gets it.
- **`Blocks.HardnessOf`** gives seconds to break by hand (negative = unbreakable). `PlayerMining`
  accumulates `delta / hardness` on the crosshair block and only emits a request at 1.0.
- **Placement legality lives in exactly one function**, `PlayerSystems.RequestPlaceAtCrosshair`,
  called by both the interactive and the scripted path.
- Requests are applied before meshing in the same frame, so a multi-block break across chunk
  borders is just N `MarkDirty` calls that the existing systems already handle.

## Known weaknesses

1. **`ChunkVisual` is on every chunk entity**, including buried chunks that will never render —
   each carries three node pointers for nothing. Splitting rendering into its own component,
   added lazily on first mesh, is the fix.
2. **The player entity is created by the view** (`Player._Ready`). Factory or system should own
   entity creation; the view should only supply node handles. Mobs do not repeat this: `MobSpawner`
   is the factory and the view node is just the handle it stores.
3. **Systems are static methods taking the store as a parameter**, so there is no per-system
   state or configuration.
4. **The mesher allocates ~430 KB per chunk** (lists plus `ToArray()` marshalling, now with the
   per-vertex UV and tile-index streams). The millisecond budget absorbs it; a two-pass
   count-then-fill mesher would remove the garbage.
