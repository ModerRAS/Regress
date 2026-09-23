# Multiplayer

Server-authoritative state sync, designed now because the chunk layout and the save format are
being decided now. The full-state question is mostly one question: **what is the world, exactly,
as bytes?** Once that is settled, the save file and the join snapshot are the same encoder, and the
server's ability to run the ECS headlessly is a mechanical split the repo already audited and
proved.

This document designs that. It implements nothing.

## In one screen

| question | answer | where |
| --- | --- | --- |
| who owns the world | the server. Clients send player intent, never state | §1, §6 |
| tick model | fixed 60 Hz sim with fixed `dt`; state published at 20 Hz; client render stays frame-rate | §1 |
| can the ECS run headless today | no — 19 of 20 `src/*.cs` carry `using Godot;`. The seam is small, and half of it already landed | §2 |
| what crosses the wire | voxels + orientation, block-entity instance state, mobs, players, world rules, world seed | §3 |
| what never crosses | node handles, meshes, textures, tile arrays, HUD, prof counters, lifecycle tags | §3 |
| save vs full sync | **one** binary format, two selection policies — not two serializers | §4 |
| format dependency | written against the in-flight 64³ layout; chunk and section dimensions are header fields, so the sectioning decision is a value, not a redesign | §4 |
| join | full snapshot, streamed in bounded batches — the correctness backstop | §5 |
| steady state | reliable block/entity deltas + unreliable positions, ~6.4 KB/s per receiver for 20 players | §5 |
| transport | byte-payload `ITransport`; v1 pick = BCL sockets (TCP + UDP) because the target host is the engine-free .NET process. Flips to ENet in one file if v1 keeps the Godot host | §6 |
| v1 deliberately omits | prediction, reconciliation, interpolation, compression, auth | §5, §9 |
| first honest risk | 64³ at `ViewDistance = 5` is a 115 MiB join. View distance must become world-space before multiplayer | §5, §9 |

## 1. Model: server-authoritative, fixed tick

The server runs the simulation. The client runs the *same* rules for presentation, but owns no
truth: it reads input, ships `PlayerIntent`, and renders what the server publishes. There is no
second rule set, because the repo already funnels world mutation through one place — `SetBlock`
(`src/VoxelWorld.cs:545-574`) — and a client fork of that is how the two halves start disagreeing.

**What runs where**

| state / work | server | client | evidence |
| --- | --- | --- | --- |
| terrain generation, chunk streaming | authoritative | replica | `CreateChunk` `src/VoxelWorld.cs:439`, plan `:168` |
| block edits, chain felling | authoritative | replica | `ApplyPendingEdits` `:402`, `BlockBehaviors.Collect` `src/EditRequests.cs:124` |
| block-entity instance state | authoritative | replica | registry `src/BlockEntities.cs:73`, `Sync` `:108` |
| mobs (spawn + AI + movement) | authoritative | replica | `MobSpawner.Plan` `src/MobSpawner.cs:40`, `MobAiRules.Decide` `src/MobAi.cs:45` |
| player movement | authoritative | local replica only | `PlayerSystems.Move` `src/PlayerSystems.cs:142` |
| input reading | — | adapter | `PollInput` `:97`, `src/Player.cs:64` |
| mesh / collision / rendering | adapter only | present | `ChunkMesher` `src/ChunkMesher.cs:64`, `src/VoxelWorld.cs:291`, `:338` |
| HUD / settings UI | publishes rule state | displays it | `src/Game.cs:246` |

**Tick model.** The server owns a fixed sim tick at 60 Hz (`dt = 1/60 s`), independent of any render
frame, driven by `IClock` — the audit's one-method replacement for the 25 `Time.GetTicksUsec()`
sites (`docs/engine-boundary.md:238-240`, sites now in `src/VoxelWorld.cs` ×15, `src/Game.cs` ×4,
`src/MobSystems.cs` ×2, `src/SelfTest.cs` ×2, `src/MobSpawner.cs:85`, `src/Prof.cs:16`). State is
published at **20 Hz**: every third sim tick becomes a delta. 60 Hz is not a new number — it is
Godot's physics rate today (`_PhysicsProcess` `src/Game.cs:205`), where `PlayerSystems.Move`
already assumes that cadence, and `MoveAndSlide` (`src/PlayerSystems.cs:169`) is a physics call
that wants a fixed step. The server has no render frame at all; "fixed tick" means the loop, not
the engine's callback.

**What the tick loop is.** Today there is no tick: chunk systems run per rendered frame
(`VoxelWorld._Process` `src/VoxelWorld.cs:127`: edits `:134`, plan/unload `:136-139`, generation
`:145`, mesh `:147`, collision `:150`, mobs `:160-163`), and player systems split across
`Game._Process` (`src/Game.cs:100`: PollInput `:105`, Look `:108`, ghost `:111`, Mine `:112`, Build
`:113`) and `Game._PhysicsProcess` (`:205`: Move `:209`). The tick loop replaces that with one
ordered pass — edits → streaming plan → generation → mob spawn/tick → move → publish — and the
adapters keep mesh, collision, and visuals. This is a *deliberate coupling*: the tick order is the
existing `_Process` order, kept because that order is already the budget priority
(`docs/architecture.md` systems table). A scheduler is not added.

**Frame-rate independence is the point, not a nicety.** Two clients at different frame rates must
end in the same world; today the same seed on two machines does not guarantee that, because work
budgets and `delta` are wall-clock dependent (§7). One fixed rate on one authoritative machine
removes the class of bug instead of detecting it.

## 2. The headless server: reuse the audit, finish the seam

`docs/engine-boundary.md` is the audit; this section only carries it forward. At audit time
(13 files, 2696 LOC), **0% of `src/` compiled without GodotSharp**, and the cheapest credible path
was measured as S/M for the type vocabulary plus M for the split — overall **M, 2–4 days of
mechanical work with no gameplay change** (`docs/engine-boundary.md:240-244`).

**What changed since the audit** (master @42f0b07): 20 files / 7128 LOC, 19 of 20 still carry
`using Godot;`. One piece of the seam **landed**: `IBlockReader` is defined at `src/MobAi.cs:7` and
implemented by `VoxelWorld` at `src/VoxelWorld.cs:14` (`public partial class VoxelWorld : Node3D,
IBlockReader`). The other three pieces are absent: `INoise2D` (0 hits; `FastNoiseLite` still
constructed at `src/TerrainGenerator.cs:34`), `IClock` (0 hits; 25 time sites), `Int3` (0 hits;
`Vector3I` is still the cell type, e.g. `src/ChunkComponents.cs:16`, `src/BlockEntities.cs:84`).
The audit's estimate holds; the new files added a handful of `Time` and `Vector3I` sites, and the
node-free `BlockEntities` registry de-risks the split.

**The three splits**

1. **`VoxelWorld` → store + adapter.** The pure half is `EntityStore`, `_index`
   (`src/VoxelWorld.cs:103`), the block-entity registry (`:106`), block CRUD (`GetBlock` `:515`,
   `GetOrientation` `:532`, `SetBlock` `:545`), the edit queue (`:402`), and the streaming plan
   (`:168`, `:200`, `:211`). The adapter is nodes and GPU resources: material statics (`:55-64`),
   `RebuildMesh` → `MeshInstance3D` (`:261`, `:291`), `UpdateCollision` → `CreateTrimeshShape()`,
   `StaticBody3D`, `CollisionShape3D` (`:325`, `:338`, `:356-357`). The mixed driver to be replaced
   is `_Process` (`:127`).
2. **`ChunkMesher` → arrays + GPU.** `Build` (`src/ChunkMesher.cs:64`) fills lists; only `Emit`
   (`:185`, `:198` `AddSurfaceFromArrays`) touches the engine. The server never calls either.
3. **`PlayerSystems` → rules + adapter.** `Move` (`src/PlayerSystems.cs:142`) is the interesting
   one: gravity and intent are plain math, `MoveAndSlide` (`:169`) is the engine, and mine/build
   read the world through physics rays (`CrosshairBlock` `src/PlayerSystems.cs:363` → `:457`,
   `PlaceTarget` `:375`). `BodyFits` (`src/VoxelWorld.cs:606`)
   is already pure and is the decision half the headless server can own; the raycast becomes a voxel
   DDA if headless gameplay (not just headless world) is in scope.

**Ordering and effort** (anchored on `docs/engine-boundary.md:240-244`)

| # | step | size | what it unlocks |
| --- | --- | --- | --- |
| 1 | `Int3`, `INoise2D`, `IClock`; `FastNoiseLite` behind the noise adapter | **S** | terrain + timing become engine-free |
| 2 | extract `ChunkStore` (store, index, CRUD, edits, plan) from `VoxelWorld : Node3D` | **S/M** | the world tick without a scene tree |
| 3 | split `ChunkMesher.Emit` from `Build` | **S** | the server skips all GPU work |
| 4 | `PlayerRules` (pure move decision + `BodyFits`) beside the node adapter | **M** | headless movement |
| 5 | fixed tick loop through `IClock`; adapters keep mesh/collision/visuals | **M** | the server tick itself |
| 6 | `WorldRules` singleton (the chain-fell toggle, §3/§6.5) | **S** | configurable, persistable rules |

Overall **M**, matching the audit's 2–4 days. The first two steps are the ones that make a
standalone `.NET` server host possible at all.

**What stays engine-bound by nature, and why that is fine** (`docs/engine-boundary.md:247-260`):
rendering (`ArrayMesh`, `MeshInstance3D`, `ShaderMaterial`, `Texture2DArray`), physics
(`MoveAndSlide`, `GetWorld3D`, `PhysicsRayQueryParameters3D`), input reading, resource/asset import,
and the render loop. The rule the audit set — *decisions engine-free, side effects in adapters* —
is exactly the split between the server (decisions) and the client (side effects).

**The cost of not doing it is measurable.** `SelfTest.Run(VoxelWorld world)` takes a `Node3D`
(`src/SelfTest.cs:18`, called at `src/Game.cs:79`), so every logic check today pays a Godot boot
(`docs/engine-boundary.md:276-277`). That is also why the server cannot be a plain process: today
the only way to run the whole ECS is a Godot process. A `godot --headless` host works meanwhile —
it is a legitimate intermediate — but it still runs the scene/adapter path, so it is compatible
rather than fast. The split is what makes "headless" mean "engine-free".

## 3. State inventory

Two questions decide every row: does the server own it, and does a fresh client need it?

**Synced — server state, sent or reproduced**

| state | owner / producer | cite | note |
| --- | --- | --- | --- |
| chunk existence + `ChunkCoord` | server store | `src/ChunkComponents.cs:10`; `_index` `src/VoxelWorld.cs:103`; `CreateChunk` `:439` | 3D, unbounded Y |
| voxels `ChunkBlocks.Value` + `.Orientation` | server | `src/ChunkComponents.cs:28-33`; written only via `SetBlock` `src/VoxelWorld.cs:545` | two byte arrays per chunk; index `(y*16+z)*16+x` today |
| `KeepAlive` | server | tag `src/ChunkComponents.cs:55`; set `src/VoxelWorld.cs:565`; unload filter `:218` | edited chunks are permanent world state |
| world identity: seed, sea level, amplitude, bedrock, frequency | world config | `src/TerrainGenerator.cs:20-23`, ctor `:27-34` | seed **must** be in the snapshot; `frequency` is not a stored field today — the header carries it |
| block entity: `BlockPos` + `Interactable.Kind` + `ToggleState.Open` | server | `src/BlockEntities.cs:9`, `:23`, `:31`; choke point `:108` called from `src/VoxelWorld.cs:564` | per-instance state deliberately never lives in the chunk arrays (`src/BlockEntities.cs:29-30`) |
| mobs: `MobXform`, `MobVelocity`, `MobAi` | server | `src/MobComponents.cs:23`, `:30`, `:37`; tick `src/MobSystems.cs:26` | spawn plan is seed-derived (`src/MobSpawner.cs:40`, `:67`) and need not be sent; live mobs do |
| player state: `Flying`, `Creative`, `Selected`, `Yaw`, `Pitch` | split | `src/PlayerSystems.cs:16-44` | `Flying`/`Creative`/`Selected` server-owned; aim is client input, replicated back |
| player position | **new field** | today it lives on the `CharacterBody3D` (`src/PlayerSystems.cs:10-14`, written `:348-357`) | the server needs it in its own store, not on a node; the record and the save both carry it |
| player intent | client → server | `src/PlayerSystems.cs:46-67` | `AutoWalk`/`AutoSprint`/`AutoMine` (`:65`) are scripted-input flags and are stripped |
| `PlayerMining` | server (transient) | `src/PlayerSystems.cs:69-73`; `Mine` `:180` | send only if a progress bar is shown |
| **`WorldRules`** | server, **read by the settings UI** | does not exist yet — chain-fell is unconditional at `src/EditRequests.cs:133` | to-be-added per-world singleton: configurable (CLI + in-game UI), authoritative, persisted, replicated. It is synchronized state, not a server-internal field |

**Never synced — machine-local handles, derived data, tooling**

| state | owner | cite | why never |
| --- | --- | --- | --- |
| `ChunkVisual.Mesh/Body/Shape` | render adapter | `src/ChunkComponents.cs:37-41` | scene handles for this machine's copy |
| `PlayerBody.Node/Camera`, `MobVisual.Node` | view adapters | `src/PlayerSystems.cs:10-14`; `src/MobComponents.cs:46` | engine nodes; the components are protocol-side |
| material, tile pack, `Texture2DArray` | import/render adapter | `src/VoxelWorld.cs:55-64`; `src/TexPack.cs:66`, `:69`, `:604-605` | GPU resources; clients load their own pack |
| `ArrayMesh` from `ChunkMesher` | render adapter | `src/ChunkMesher.cs:64`, `:136`, `:185` | derived; each client regenerates from voxels |
| `PlacementGhost`, HUD, screenshot tooling | client view | `src/PlacementGhost.cs:24`; `src/Game.cs:246`, `:194` | local presentation |
| `Prof` counters | bench tooling | `src/Prof.cs:11-16` | local instrumentation |
| lifecycle tags `NeedsMesh`/`NeedsCollision` | chunk store | `src/ChunkComponents.cs:47`, `:52` | "pending work on this machine", not world state |
| scratch lists, `_columns`, `_work`, `_pending`, `_edits` | chunk store | `src/VoxelWorld.cs:109-115`, `:394` | reused buffers |

**Float state needs an encoding decision, not a default.** `Vector3`/`float` fields cross a byte
boundary, so §4 must pick quantized fixed point or raw IEEE bits: `PlayerState.Yaw/Pitch`
(`src/PlayerSystems.cs:21-22`, client-produced), `PlayerIntent.Wish` (`:47`; prefer key-bitmask +
yaw and recompute server-side), `MobXform.Position/Yaw` and `MobAi.TargetX/TargetZ/Retarget`
(`src/MobComponents.cs:25-26`, `:40-42`, server-produced), `MobVelocity` (`:30-32`, derivable),
`PlayerMining.Progress` (`:71-73`), and `TerrainGenerator`'s frequency (ctor `:27`). v1: raw IEEE
bits for server-produced mobile state (no determinism promise across platforms yet) and quantized
fixed point for anything a client sends. The decision is per-field and lives in §4's codec, not in
the components.

## 4. One snapshot format, two consumers

**Rule:** the on-disk save and the network full sync emit the same bytes. One encoder, one decoder,
one golden fixture. The only difference is the selection policy the caller applies — disk writes
the edited chunks, network writes the joining player's bubble.

**Assumption, stated loudly: the chunk layout is changing right now.** `feat/scale-64` exists and
has no commits (master @42f0b07 at the time of writing); today's truth is `ChunkSize = 16`
(`src/VoxelWorld.cs:16`), one flat 4096-byte `Value` and 4096-byte `Orientation` per chunk with
index `(y*16+z)*16+x` (`src/ChunkComponents.cs:28-33`). The design target is **64³: 262,144 cells
per chunk, 524,288 B per array, ~1 MiB per chunk with orientation**. This format is written against
that target. If the in-flight refactor lands a different granularity, the header absorbs it:
chunk and section dimensions are data, not code paths.

**Header**

| field | type | note |
| --- | --- | --- |
| magic | 4 B `"RGSV"` | reject on mismatch |
| formatVersion | u16 = 1 | no forward compatibility in v1 |
| chunkX / chunkY / chunkZ | u16 ×3 | today 16/16/16; 64/64/64 target |
| sectionX / sectionY / sectionZ | u16 ×3 | 0 = flat chunk; e.g. 16/16/16 for 16³ sections; a 4×4×8 grid is expressible |
| flags | u16 | bit0 orientation array present, bit1 sectioned records |
| worldSeed, seaLevel, heightAmplitude, bedrockY | i32 ×4 | `src/TerrainGenerator.cs:20-23` |
| noiseFrequency | f32 | ctor input not stored on the generator today (`src/TerrainGenerator.cs:27`) — without it the world cannot be regenerated, so the format carries it |
| tick | u64 | snapshot tick (0 for a save) |
| chunkCount / entityCount / mobCount / playerCount | u32 ×4 | counts before payload, so a reader allocates and validates |

Rejection contract: bad magic; unknown `formatVersion`; any chunk dimension not a power of two;
any `section` dimension that is neither 0 nor a divisor of the matching chunk dimension.

**Flat vs sectioned.** v1 records are **flat** — fewest moving parts, and the existing mesher and
`SetBlock` neighbour logic are unchanged. The header still expresses sections so a later encoder
adds them without a new magic: if a section is all air it is one byte; if uniform it is two bytes;
otherwise it is the section's cells. Flat vs sectioned changes the per-chunk record shape, not the
format identity, and the `flags` bit tells a decoder which shape follows. If the refactor's
published decision is sectioned, v1 should still ship flat: the section win is remesh cost, not
sync correctness, and it is the renderer's problem.

**Records, deterministic order** — header; chunks ascending (x, y, z); block entities ascending
cell; mobs by stable id; players by id; `WorldRules` last. Any dictionary order is sorted on
encode, never trusted (`BlockEntityRegistry.Visit` enumerates buckets: `src/BlockEntities.cs:98-101`).

| record | fields | source |
| --- | --- | --- |
| chunk | coord 3×i32, `edited` u8 (1 = `KeepAlive`), blocks `byte[chunkX*chunkY*chunkZ]`, orientations same size | `src/ChunkComponents.cs:10`, `:28-33`, `:55` |
| block entity | cell 3×i32, kind u8, per-kind state (Toggle → `Open` u8) | `src/BlockEntities.cs:9`, `:20`, `:31-33` |
| mob | id u32, kind u8, position/yaw f32×4, `MobAi` rng u32 + targetX/Z + retarget, velocity f32×3 | `src/MobComponents.cs:8-10`, `:23-26`, `:30-32`, `:37-42` |
| player | id, position/velocity, flags, selected, yaw/pitch, mining state | position is new (§3); `src/PlayerSystems.cs:16-44`, `:69-73` |
| WorldRules | count u8; per rule id u8 + value u8 | v1: one rule — `ChainTreeFelling` (§6.5) |

**Serialization: hand-rolled `BinaryReader`/`BinaryWriter`.** It is in net8.0
(`Regress.csproj:3`), little-endian by default, deterministic byte-for-byte, and carries no engine
type — the same reason the seam exists (`docs/engine-boundary.md:240-244`). Godot's
`FileAccess`/`Variant` loses because it re-couples the save path to GodotSharp and asset IO is
already classified as adapter-side (`docs/engine-boundary.md:258`). `System.Text.Json` loses on
`byte[]` → base64 (+33%, a 524 KB chunk becomes ~699 KB of text), on megabyte-array parse cost, and
on formatting drift against byte-exact fixtures.

**Validation.** Write→read equality of every field and count; re-encode and compare bytes; a golden
fixture for a tiny world (1 chunk, a few oriented blocks, 1 open chest, 1 mob, 1 player, rules off)
under `tests/Regress.Core.Tests/golden/` with a manifest naming expected header values; one test per
rejection rule. Until §2 lands, these assertions run inside `--selftest`, which currently costs a
Godot boot (`src/SelfTest.cs:18`); after the `ChunkStore` split they move to `dotnet test` with no
engine. No new test framework.

**Dependency summary.** If the scale refactor keeps 16³, nothing changes (the header already
describes it). If it lands 64³ flat, the header values change and the payload grows 64×. If it
lands sectioned, the `flags` bit selects section records and the chunk record carries a section
table. None of those is a new format, and the version field rejects anything a reader cannot
handle.

## 5. Full sync + deltas

**Join = full snapshot.** The server sends the joining player the §4 snapshot of their interest
set, streamed over the reliable channel in bounded batches (a few chunks per tick) so one join
never stalls the tick. The client is "in the world" only when the snapshot completes — v1 has no
partial join. Every reconnect after a gap starts from a fresh snapshot; there is no incremental
catch-up in v1.

**Steady state = deltas.** One batched reliable message per client per publish tick (20 Hz) plus
unreliable position updates; each carries `seq` and the current `snapshotTick`.

| record | fields | size | channel |
| --- | --- | --- | --- |
| block edit | cell 3×i32 + block u8 + orientation u8 + tick u32 | 18 B | reliable |
| entity spawn | id u32 + kind u8 + position 3×f32 | 17-20 B | reliable |
| entity despawn | id u32 | 4-8 B | reliable |
| position | id u32 + 3×f32 (3×i16 quantized later) | 16 B (10 B quantized) | unreliable |
| WorldRules change | rule id u8 + value u8 | 2-4 B | reliable |
| player state | id + flags/selected | 8-16 B | reliable |

The delta tap is `VoxelWorld.SetBlock` (`src/VoxelWorld.cs:545-574`, the "one choke point" comment
at `:563`) — not `RequestEdit` (`:398`), which is unvalidated intent. One break can expand server-
side into up to 256 cell edits (`MaxBlocksPerBreak` `src/EditRequests.cs:111`, chain felling
`FellRadius = 5` `:112`) → a ~4.6 KB reliable burst from one click.

**Bytes, from this repo's constants.** `ViewDistance = 5` chunks (`src/VoxelWorld.cs:28`);
`ChunksPerFrame = 4` (`:31`); vertical band from `SurfaceRange` (`src/TerrainGenerator.cs:42`) with
`HeightAmplitude = 22` (`:22`) and `MaxTreeHeight = 8` (`:15`, `:18`). Resident chunks ≈ 230 at
16³ (`docs/architecture.md:11`). Payload: 8,192 B per 16³ chunk, 524,288 B per 64³ chunk, doubled
with orientation in both cases.

| case | bytes |
| --- | --- |
| join @16³, V=5 | ~230 × 8,192 B ≈ **1.84 MiB** |
| join @64³, V=5 (same constant = 4× world radius) | ~230 × 524,288 B ≈ **115 MiB** |
| join @64³, V≈2 (same world radius) | ~25-50 chunks ≈ **13-26 MiB** |
| full-every-tick @16³, 10 / 20 Hz | 18.4 / 36.8 MiB/s per player |
| 20 players, positions @20 Hz | 6.4 KB/s per receiver; 64 KB/s server egress |
| one tree fell | ~4.6 KB once |

Two honest numbers fall out of that table. First, **full-every-tick is 3,000× the steady-state
cost and is rejected**; the join snapshot alone is ~290 s of steady-state traffic at 20 Hz.
Second, **the 64³ change turns `ViewDistance` into a world-radius change**: the constant is in
chunks, so the same value means a 4× larger world radius on top of the 64× per-chunk payload. The
join snapshot must not be allowed to follow that curve — the view distance has to become
world-space (or be set to ~2) before multiplayer, and the snapshot is streamed in batches with
compression (deferred, §9) as the follow-up. `KeepAlive` chunks are also unbounded by
`ViewDistance` (`src/VoxelWorld.cs:218`), so the join set grows with every edit in the world's
life; an edit-log/region save is required before that stays honest.

**Interest, priority, ack.** Interest = the surface band plus `KeepAlive` chunks within the
player's radius (not all of them). Priority = nearest first, mirroring the existing sorts
(`src/VoxelWorld.cs:196` generation, `:255` mesh, `:319` collision). Reliable-ordered carries the
snapshot, edits, spawn/despawn, and rule changes; unreliable carries positions, latest-wins, with a
full position keyframe every 20 ticks (1 s) as the loss backstop. Reliability itself is the
transport's job (§6), so there is **no hand-rolled ack layer** — `seq` only detects a stream reset
or a baseline gap. Re-snapshot triggers: sequence gap, a delta naming a chunk/entity outside the
local baseline, or no accepted snapshot tick for 5 s.

**v1 scope and deferrals**

| in v1 | deferred | upgrade path |
| --- | --- | --- |
| full snapshot on join (streamed batches) | chunk cache / content hash | skip unchanged chunks by hash |
| reliable deltas + unreliable positions | compression | RLE/palette on block arrays (mostly air/stone) |
| server sim from intent | client prediction | predict `Move` locally, reconcile at `snapshotTick` |
| keyframe every 1 s | interpolation | 100 ms buffer, render one snapshot behind |
| WorldRules in snapshot + change event | per-rule permissions | §6.5 role gate |
| trust-on-join | auth / identity | tokens |

**Honest weakness:** with no prediction and no interpolation, remote players update at 20 Hz and
every correction is a visible snap — remote motion is up to ~50 ms plus RTT stale, and rubber-bands
on corrections. Accepted for v1; the first two deferrals are the fix. The v1 tick rate is 60 Hz sim
/ 20 Hz publication; the upgrade when server CPU or bandwidth matters is decoupling the sim rate
(fixed substeps per publish tick), not raising the publish rate.

## 6. Transport and authority

**The transport is swappable and never leaks into the simulation.** Its interface exchanges plain
bytes; no Godot type crosses it, which is what keeps the standalone `.NET` host option open.

| option | runtime | reliability | dependency | headless server |
| --- | --- | --- | --- | --- |
| Godot `ENetMultiplayerPeer` / `PacketPeerUDP` | Godot only | channels built in | none (ships with Godot 4.7 .NET) | requires a Godot host |
| **BCL `System.Net.Sockets` (TCP + UDP) — picked** | any .NET | TCP reliable-ordered, UDP unreliable | none (net8.0, `Regress.csproj:3`) | no Godot at all |

**Why the pick.** The seam plan's destination is an engine-free core (`docs/engine-boundary.md:242`)
and the named pain is that every logic run costs a Godot boot (`:276-277`). ENet pins the server
inside Godot and reintroduces that cost. v1 is therefore `TcpListener` for the reliable stream
(join, snapshot, edits, spawn/despawn, rules) and `UdpClient` for positions, framed as
`u32 length + payload`; TCP supplies ordering and resend, so no ack layer is written.

**The one decision that flips.** If the team chooses to ship networking *before* the §2 split and
keep the v1 server as a Godot headless process, ENet becomes cheaper — free reliable/unreliable
channels, no framing — and the seam makes it a one-file `ITransport` swap. Everything above the
transport is unchanged.

```csharp
// design sketch — plain bytes only, no Godot type crosses this line
public interface ITransport {
    event Action<PlayerId, ReadOnlyMemory<byte>> Received;
    void SendReliable(PlayerId to, ReadOnlyMemory<byte> payload);
    void SendUnreliable(PlayerId to, ReadOnlyMemory<byte> payload);
    void BroadcastReliable(ReadOnlyMemory<byte> payload);
}
```

**Which end owns what.** The client sends `PlayerIntent` fields — `Wish`, `Jump/Up/Down/Sprint`,
`Mining`, `Place`, rotate (`src/PlayerSystems.cs:46-67`) — and nothing else. Auto-input flags
(`AutoWalk`, `AutoSprint`, `AutoMine` `:65`) are demo-only and stripped by the server. Position,
`Flying`, `Creative`, mobs, and block edits are never client-written. The existing pipeline is the
validator: reach 6 (`:88`) + a raycast from server-owned yaw/pitch (`:457`) → `PlacementRefused`
(`:316`) → `PlaceTarget` air check (`:375`) → `Blocks.Snap`/`StickyOrientation` normalization (`:328-331`) →
`SetBlock` (`src/VoxelWorld.cs:545-574`). Mining uses the existing hardness path (`:180-225`).

**Knowingly accepted in v1:** wallhack/x-ray inside the received bubble; intent flooding (add an
8-per-tick server cap, drop the rest); spoofed aim (validated only through what it produces); no
identity/auth (tokens deferred); plaintext transport; a lying `Selected` block (the server
validates the byte it writes, not ownership). One defense is not negotiable: **the server simulates
movement from intent; the client never sets position** (`src/PlayerSystems.cs:142-170`). Without
it every validation above runs against a position the cheater chose.

### 6.5 WorldRules authority (`ChainTreeFelling`)

Today the rule is unconditional: `if (block == Block.Wood) FellTree(...)`
(`src/EditRequests.cs:133`), bounded by `FellRadius = 5` (`:112`) and `MaxBlocksPerBreak = 256`
(`:111`). It becomes a per-world singleton, `WorldRules { bool ChainTreeFelling; }` — v1 has this
one rule — configured by CLI (`--no-chain-fell`; `HasArg` `src/Game.cs:292`, `ArgValue` `:294`) and
by an in-game settings UI. It is server-authoritative state that persists (§4) and replicates (§5).
It is **not** a server-internal field: the settings UI must be able to display it.

- **(a) Who may change it: the host only** (the Minecraft gamerule model). One break can remove up
  to 256 cells (`src/EditRequests.cs:111`) and the world state is shared and persisted; a per-client
  rule would make the same break produce different worlds for different clients and diverge the
  save. Non-host clients may *request* a change; the server owns the value.
- **(b) How changes propagate.** The join snapshot carries `WorldRules`; a change is a reliable
  `WorldRulesChanged` event broadcast to every connected client. Each client keeps a replicated
  copy, and the settings UI renders that copy.
- **(c) A refused local change is shown honestly.** The UI shows the server value with an explicit
  read-only / "server controlled" state — disabled toggle, or a revert to the authoritative value
  when the server's response arrives. It must never display the local tentative value as applied.
  The existing HUD pattern (`BlockInteractions.Version`/`Message` `src/BlockEntities.cs:44-47`,
  polled at `src/Game.cs:116-119`) is the cheap precedent for a UI that re-reads replicated state
  instead of owning it.

Upgrade path: per-rule permissions/roles. The replicated-value + server-owned-change plumbing above
is exactly what a role gate needs, so the upgrade is one server-side check.

## 7. Determinism

The repo already has real determinism guarantees to build on, and one class of guarantee it does
not have.

**Already guaranteed**

| guarantee | mechanism | cite |
| --- | --- | --- |
| per-world seeded terrain | one generator per world, seeded noise + fixed octaves | `src/TerrainGenerator.cs:20-23`, `:27-34`, `HeightAt` `:37` |
| tree placement | integer hash of (x, z, seed), no RNG | `src/TerrainGenerator.cs:102`, `:112`, `:135` |
| mob AI | pure function over `IBlockReader`, xorshift32 state | `src/MobAi.cs:25`, `:45`, `:165` |
| mob spawn plan | pure `(seed, focus)` + integer cell hash | `src/MobSpawner.cs:40`, `:67` |
| placement / orientation | total pure functions, assertion after snap | `src/EditRequests.cs:49`, `:69`, `:98`, `:106` |
| edit application | insertion order through one `SetBlock` choke point | `src/VoxelWorld.cs:402`, `:545` |

**What would break it, and which §2 step contains it**

| breakage | cite | effect | fix |
| --- | --- | --- | --- |
| no tick exists; cadence = render/physics rate | `src/Game.cs:100`, `:205`; `src/VoxelWorld.cs:127` | per-machine system order and count | §2 step 5 |
| wall-clock reads + millisecond budgets | 25 `Time.GetTicksUsec` sites; `ProcessWithinBudget` `src/VoxelWorld.cs:380-385`; `ChunksPerFrame` `:31`; `src/MobSystems.cs:31` | work per tick and mid-tick cutoffs differ | §2 step 1 (`IClock`) + step 5 (fixed work units) |
| float accumulation across variable `delta` | `src/PlayerSystems.cs:165`, `:169`; `src/MobSystems.cs:68` | different trajectories | §2 step 4 + step 5 fixed `dt` |
| iteration / storage order | Friflo unload query `src/VoxelWorld.cs:216`; `Visit` over buckets `src/BlockEntities.cs:98-101`; unstable sort ties `src/VoxelWorld.cs:196`, `:255`, `:319` | unload/visit/tie order differs | §2 step 2: server owns order; snapshot keyed and sorted by coordinate |
| chunk-border generation timing | heightmap fallback `src/VoxelWorld.cs:515`, `:528`; mesher pad `src/ChunkMesher.cs:74`; `docs/architecture.md:244-250` | border faces differ between runs/clients | §2 step 3 + generation-before-mesh; border bytes must come from generated server data, not load order |
| mob population tied to chunk residency | `src/MobSpawner.cs:148`; cap `src/MobSystems.cs:13` | same seed, different population | §2 step 5: mobs are server-only live state |

The design does not promise cross-platform float determinism in v1. It promises one authoritative
simulation, fixed `dt`, and a wall-clock-free decision path — which is what "the server runs the
world" requires. Bit-exact lockstep replay is the version that would need the quantized encoding in
§3, and it is not v1.

## 8. Verification before any socket exists

Most of this is testable with no network, which is the point of putting the format before the
transport.

| test | what it proves | when |
| --- | --- | --- |
| snapshot round-trip: encode → decode → re-encode, byte-identical | the format and the codec | with §4, inside `--selftest`, then `dotnet test` |
| golden fixture (`tests/Regress.Core.Tests/golden/`) | the format does not drift silently | same |
| header rejection cases | bad input is rejected, not misread | same |
| headless server tick: fixed intents for N ticks → encode → compare | the sim runs without a scene and produces a stable result | after §2 steps 2 + 5 |
| two-world divergence: same seed + same intent script on two stores | determinism §7 (terrain, mobs, edits) | after §2 step 2 |
| client replica test: apply snapshot + delta stream to an empty store, compare to server store | the delta semantics actually reconstruct the server state | after §4 + §5 codec, still no socket |

What genuinely needs a real transport: packet loss and reordering on the unreliable position
channel (does the keyframe converge?), the join under bandwidth limits (does batching hold the
tick budget?), ack/stream-reset behavior, and the human-visible feel of 20 Hz motion. Those get a
loopback test first, two processes second, and never a determinism claim before a real link.

## 9. Sequence and risks

Ordered against the live queue; each step is merged before the next depends on it.

| # | step | depends on | main risk |
| --- | --- | --- | --- |
| 0 | **64³ / scale refactor (in flight)** | — | `ViewDistance` is chunk-based: 64³ turns V=5 into a ~115 MiB join. The refactor should express view distance in world units (or set V≈2) or multiplayer inherits the blow-up |
| 1 | chain-fell `WorldRules` toggle: CLI + settings UI | — | the settings UI must read replicated state from day one, or it owns truth and must be rewritten at step 4 |
| 2 | snapshot substrate: §4 format + save + round-trip/golden tests | 0 (header values) | the saved world must be re-generatable from seed + edited chunks; any per-cell state not in the format is lost silently |
| 3 | headless server tick: §2 splits + fixed loop | 2 (the store it ticks) | the split is mechanical but touches everything; risk is scope creep into renderer refactors. Keep mesh/collision out of the server path and stop |
| 4 | transport: join snapshot + deltas on `ITransport` | 2, 3 | 64³ join size and `KeepAlive` growth; batching and (soon) compression are not optional knobs |
| 5 | chest/inventory UI | 2, 4 | block-entity state is already entity-side and already in the format; the risk is a second local copy of it in the UI. It must render the replicated store |
| 6 | repo-wide format cleanup | 5 | none beyond diff size |

The persistence item in [roadmap.md](roadmap.md#4-known-weaknesses-to-clean-up) ("no persistence:
player-built chunks are pinned in memory", `docs/roadmap.md:92`) is solved by step 2 — the save
file is the snapshot. Do not build a second saver first.

### Non-goals, each with its upgrade path

| non-goal in v1 | upgrade path |
| --- | --- |
| client-side prediction / reconciliation | predict `Move` locally; reconcile against `snapshotTick` (the delta stream already carries it) |
| entity interpolation | render one snapshot (~100 ms) behind; positions are already timestamped by tick |
| delta compression / palette | RLE or palette over block arrays before the 64³ join makes it mandatory |
| chunk cache / content hashing | hash per chunk record; skip unchanged chunks on join |
| dedicated-server orchestration, NAT traversal, matchmaking | out of scope; the transport is a byte interface, so it is a hosting concern |
| auth / identity / TLS | token on join, then the transport wraps the same byte payload |
| per-rule permissions / roles | one server-side check on the `WorldRulesChanged` path (§6.5) |
| anti-cheat beyond intent validation | server-owned positions first; then rate limits, then reach/aim heuristics |
| cross-dimension / multi-world sync | the snapshot is per world; a manager that owns several worlds is already an open roadmap item |

Not decided here, on purpose: whether the server is a dedicated process or one player hosts
(the byte interface and the tick loop are identical), the exact quantized encoding until §4 is
built, and the sectioning answer until the scale refactor publishes one.
