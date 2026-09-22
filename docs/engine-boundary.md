# Engine boundary

Is the game logic separate from Godot? **No — and not one line of `src/` compiles without
GodotSharp today.** Every one of the 13 files carries `using Godot;`, so the "logic" half of the
repo cannot be built, tested or reasoned about without booting the engine.

But the interesting answer is the next sentence: **the rules isolate behind one thin seam, and that
was proved by building them without the engine.** The coupling is not architectural depth. It is a
type vocabulary (`Vector3I`, `Mathf`, `Color`), a dead method, and `VoxelWorld` being a `Node3D`.
Those are mechanical, and two of them are free.

This document is an audit. Nothing in `src/` was changed.

## Method

Everything below is a command that was actually run in this checkout.

The **executable probe** is the core evidence — reading alone would have produced an opinion.
`.pi/boundary/probe/` is a plain `Microsoft.NET.Sdk` class library (no `Godot.NET.Sdk`, no
GodotSharp on the reference list) that copies candidate logic files verbatim and asks the C#
compiler what it thinks:

```bash
# harness, no engine SDK
cat .pi/boundary/probe/Probe.csproj          # Microsoft.NET.Sdk + Friflo.Engine.ECS 3.6.0 only
cd .pi/boundary/probe

# empty baseline: proves the harness itself is clean, not the code
DOTNET_CLI_UI_LANGUAGE=en dotnet build Probe.csproj -v:minimal -nologo   # -> 0 errors, 0 warnings
#   captured in .pi/boundary/raw-baseline-empty.txt

# cumulative layers, one file added per step, `cp` only — src/ is never moved or edited
#   .pi/boundary/run-probe.sh, captured in .pi/boundary/raw-step-01..07-*.txt and raw-layer1..3.txt
```

Supporting census and verification:

```bash
# which engine symbols each file touches, and how many lines (full script: .pi/boundary/leadcheck/census.sh)
bash .pi/boundary/leadcheck/census.sh

# the claim "all 13 files need Godot"
grep -c '^using Godot;' src/*.cs

# the seam actually works: ported copies of the rules, compiled with no engine
cd .pi/boundary/seam && DOTNET_CLI_UI_LANGUAGE=en dotnet build Seam.csproj -v:minimal -nologo
#   -> 0 errors, 0 warnings; captured in .pi/boundary/raw-lead-seam-proof.txt

# baseline health of the real project
dotnet build                                    # 0 errors, 0 warnings
godot-mono --headless --path . -- --selftest    # SELFTEST PASS
```

## Inventory

One row per file. "engine lines" counts lines mentioning at least one Godot symbol in the
categories above it, so a line can hit two columns (`ChunkMesher` `Mesh.ArrayType.Vertex` is both
mesh and math-adjacent). Patterns are in `census.sh`; a `.Node` member access is not counted as a
node type.

| file | LOC | nodes | math | mesh | IO | log | input | time | phys | noise | engine lines | eng% |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Bench.cs | 130 | 1 | 5 | 0 | 0 | 9 | 0 | 0 | 0 | 0 | 14 | 11% |
| Blocks.cs | 74 | 0 | 13 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 13 | 18% |
| ChunkComponents.cs | 53 | 3 | 3 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | 6 | 11% |
| ChunkMesher.cs | 139 | 0 | 14 | 11 | 1 | 0 | 0 | 0 | 0 | 0 | 26 | 19% |
| EditRequests.cs | 90 | 0 | 15 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 15 | 17% |
| Game.cs | 273 | 12 | 18 | 2 | 0 | 12 | 0 | 6 | 0 | 0 | 48 | 18% |
| Player.cs | 96 | 6 | 3 | 0 | 0 | 0 | 3 | 0 | 0 | 0 | 12 | 12% |
| PlayerSystems.cs | 299 | 7 | 37 | 2 | 2 | 0 | 10 | 0 | 8 | 0 | 59 | 20% |
| Prof.cs | 25 | 0 | 0 | 0 | 0 | 0 | 0 | 1 | 0 | 0 | 1 | 4% |
| SelfTest.cs | 455 | 0 | 34 | 13 | 0 | 3 | 0 | 0 | 0 | 0 | 42 | 9% |
| TerrainGenerator.cs | 147 | 0 | 3 | 0 | 0 | 0 | 0 | 0 | 0 | 2 | 5 | 3% |
| TexPack.cs | 351 | 0 | 1 | 14 | 18 | 19 | 0 | 0 | 0 | 0 | 49 | 14% |
| VoxelWorld.cs | 564 | 5 | 54 | 4 | 0 | 0 | 0 | 16 | 0 | 0 | 78 | 14% |
| **total** | **2696** | | | | | | | | | | **368** | **14%** |

Classification:

| class | files | why |
| --- | --- | --- |
| **adapter** — engine-facing by nature | `Game.cs`, `Player.cs`, `TexPack.cs`, `Bench.cs`, `Prof.cs`, `SelfTest.cs` | scene bootstrap + env, the mouse/input node, asset import (`FileAccess`/`Image`/`Texture2DArray`), the bench and test harness. These are *supposed* to touch Godot — replacing them is not decoupling, it is reimplementing the engine |
| **mixed** — logic carrying incidental engine types | `VoxelWorld.cs`, `PlayerSystems.cs`, `ChunkMesher.cs`, `ChunkComponents.cs`, `EditRequests.cs`, `TerrainGenerator.cs`, `Blocks.cs` | real game rules (block table, break pipeline, terrain function, mesher geometry, edit requests) expressed in engine types for no reason the rules require |

**Zero files are `pure logic`.** Not one of the 13 compiles without GodotSharp, so the
"engine-free fraction of `src/`" is **0% (0 of 2696 LOC)** — see the probe below for what that
number actually means.

Two files are misclassified by their names, not their content: `TexPack.cs` is 100% asset-import
adapter (37 engine lines of file/JSON/Image IO, correct), and `TerrainGenerator.cs` is 3% engine
surface yet cannot compile — it is the *most* logic and one of the least buildable.

## The executable probe

`.pi/boundary/probe/` is a class library that references only `Friflo.Engine.ECS`. GodotSharp is
not on the path. An **empty** probe builds clean:

```
$ cd .pi/boundary/probe && dotnet build Probe.csproj -v:minimal -nologo
  Probe -> .pi/boundary/probe/bin/Debug/net8.0/Probe.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

So when a file fails, the harness is not the reason. Adding the candidate logic files one at a
time, copying them verbatim from `src/`:

| step | file added | distinct compile-error sites | sites this file added |
| --- | --- | --- | --- |
| 1 | `Blocks.cs` | 2 | 2 |
| 2 | `EditRequests.cs` | 17 | 15 |
| 3 | `TerrainGenerator.cs` | 23 | 6 |
| 4 | `ChunkComponents.cs` | 26 | 3 |
| 5 | `ChunkMesher.cs` | 31 | 5 |
| 6 | `VoxelWorld.cs` | 49 | 18 |
| 7 | `PlayerSystems.cs` | 70 | 21 |

**Result: 0 of 7 candidate logic files compile engine-free, and the union is 70 distinct leak
sites.** Every site is `CS0246`, first error per expression — the verbatim head of step 1, the
smallest possible failure:

```
Blocks.cs(1,7): error CS0246: The type or namespace name 'Godot' could not be found (are you missing a using directive or an assembly reference?)
Blocks.cs(54,19): error CS0246: The type or namespace name 'Color' could not be found (are you missing a using directive or an assembly reference?)
```

and step 7:

```
VoxelWorld.cs(4,7):  error CS0246: The type or namespace name 'Godot' could not be found
PlayerSystems.cs(2,7): error CS0246: The type or namespace name 'Godot' could not be found
ChunkComponents.cs(37,12): error CS0246: The type or namespace name 'MeshInstance3D' could not be found
TerrainGenerator.cs(25,25): error CS0246: The type or namespace name 'FastNoiseLite' could not be found
ChunkMesher.cs(43,17): error CS0246: The type or namespace name 'Vector2' could not be found
```

Full untruncated output is in `.pi/boundary/raw-step-01..07-*.txt` and `.pi/boundary/raw-layer1..3.txt`.

**Honest caveat on the 70.** Roslyn reports the first failure per expression and stops, so once
`Vector3I` is unresolved, `next.X - origin.X` produces nothing further. The 70 is a *lower bound*.
A pure symbol census over the same 7 files (1366 LOC) finds **202 lines carrying engine symbols**:
48 `Vector3`, 46 `Vector3I`, 44 `Mathf.`, 15 `Time.GetTicksUsec`, 14 `Color`, 12 `Input.*`,
plus the node/mesh/physics/Rid stragglers.

The fraction, stated precisely: **0% of logic compiles engine-free today; 202 of 1366 logic LOC
(15%) are engine-typed, and the reported error count of 70 is a floor on the number of distinct
places that must change.**

### The seam was also built, not argued

The same scratch technique was used to test the fix. `.pi/boundary/seam/` contains *copies* of
`Blocks.cs`, `EditRequests.cs`, `TerrainGenerator.cs` and the data half of `ChunkComponents.cs`,
plus two interfaces and one struct:

```
$ cd .pi/boundary/seam && dotnet build Seam.csproj -v:minimal -nologo
  Seam -> .pi/boundary/seam/bin/Debug/net8.0/Seam.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

`grep -n 'Godot\|Mathf\|Vector3\|FastNoiseLite'` over those ported files returns only my own
comments. **331 LOC of the rules core — the block table, the break/fell pipeline, the whole terrain
function, the chunk data — build with no engine, after ~28 mechanical edits and one deletion.**
The edits are itemised in the seam section below.

## Leak taxonomy

Counted over the 7 mixed files. "sites" = lines; "occurrences" = symbol instances.

| category | sites | top symbols | verdict |
| --- | --- | --- | --- |
| **engine math types in logic** | 139 | `Vector3` ×48, `Vector3I` ×46, `Mathf.` ×44, `Color` ×14, `Vector2` ×7, `Basis` ×6, `Aabb` ×2 | **incidental.** The single largest leak. Every one is a plain value that a 20-line struct or `System.Math` replaces. 13 of the 14 `Color` sites are inside one dead method (`Blocks.cs:54`) |
| **engine scene / physics objects** | 21 | `CharacterBody3D` ×5, `MeshInstance3D`/`StaticBody3D`/`CollisionShape3D` ×2 each, `Node3D` ×2, `Camera3D`, `PhysicsRayQueryParameters3D`, `GetWorld3D`, `GetRid` | **essential.** Nodes and physics queries are what this game *is* at its edges. `ChunkComponents.cs:37` (`ChunkVisual`) is the archetype holding handles — deliberate, and documented in `architecture.md` |
| **engine rendering / mesh** | 18 | `Mesh` ×9, `ArrayMesh` ×3, `Rid` ×2, `Texture2DArray` | **essential** where it builds GPU resources, **incidental** where it is only a vertex buffer. `ChunkMesher` earns both labels |
| **engine time / OS** | 16 | `Time.GetTicksUsec` ×15, `OS.GetCmdlineUserArgs` | **incidental.** Every one is a millisecond-budget read. A 1-method `IClock` removes all 16 |
| **input** | 10 | `Input.IsKeyPressed` ×8, `Input.MouseMode`, `Input.IsMouseButtonPressed` | **essential, but misplaced.** Reading input is an adapter's job; here it sits inside `PlayerSystems.PollInput`, so the game's intent layer can only be exercised through the engine's static singleton. All 10 sites are in that one 20-line system |
| **engine noise** | 2 | `FastNoiseLite` field + construction | **incidental.** A `INoise2D { float Sample(float,float); }` is a one-line adapter over Godot's noise, and it is the *only* thing keeping the terrain function unbuildable |
| **engine file / JSON IO** | 3 | `Godot.Collections.Array`, `.Dictionary` | **incidental** in the mesher (marshalling), **essential** in `TexPack.cs` (37 lines of `FileAccess`/`Image`/`ResourceLoader` — correctly an adapter) |
| **engine logging** | 0 | — | not a leak in logic. All 43 `GD.Print`/`GD.PushWarning` sites are in the adapters (`TexPack` 19, `Game` 12, `Bench` 9, `SelfTest` 3), which is where they belong |
| **threading** | 0 | `[System.ThreadStatic]` scratch buffers (`EditRequests.cs:46-47`), `GC.GetAllocatedBytesForCurrentThread()` | already BCL, not engine. No Godot threading anywhere |

Ranked by cost to remove: math types (139 sites, all incidental, all mechanical) → time (16,
incidental) → input placement (10, essential but in the wrong file) → noise (2, incidental).
The essential ~40 sites are not leaks; they are the adapter surface.

## The single-interface answer

**Yes — the calls isolate behind one seam. But "one interface" is not literally one interface, and
the seam is not where the work is.** Say it precisely:

- The **call-direction** seam is genuinely one method. `BlockBehaviors.Collect` and
  `ChunkMesher.Build` each ask the world for exactly one thing — `GetBlock(x, y, z)` — and
  `VoxelWorld` already exposes *exactly* that signature at `src/VoxelWorld.cs:462`. Adding
  `IBlockReader` to the class declaration is the entire cost on the world side.
- The **type-vocabulary** seam is the real work: 46 `Vector3I` + 44 `Mathf` + 14 `Color`
  occurrences must become plain types. That is mechanical but it is not "an interface layer", and
  pretending otherwise would be the same overclaim this document exists to catch.

Smallest credible seam, in four pieces:

| interface | replaces | sites |
| --- | --- | --- |
| `IBlockReader { Block GetBlock(int,int,int); }` | the `VoxelWorld` parameter in `ChunkMesher.cs:52`, `EditRequests.cs:50`, `EditRequests.cs:65` | 3 live, 1 dead (`TerrainGenerator.cs:59`) |
| `INoise2D { float Sample(float,float); }` | `FastNoiseLite` at `TerrainGenerator.cs:25`, `:34`, `:38` | 3 |
| `IClock { ulong TicksUsec { get; } }` | `Time.GetTicksUsec()` — 15× `VoxelWorld.cs`, 1× `Prof.cs` | 16 |
| `Int3` (struct, not interface) | `Vector3I` | 46 |

```csharp
// the whole seam. VoxelWorld already satisfies this verbatim (src/VoxelWorld.cs:462).
public interface IBlockReader { Block GetBlock(int x, int y, int z); }
public interface INoise2D { float Sample(float x, float z); }
public interface IClock { ulong TicksUsec { get; } }

// engine-side: one word added to the declaration, no body changes.
public partial class VoxelWorld : Node3D, IBlockReader { /* GetBlock already matches */ }

// engine-free side: the rules take the interface, not the node.
public static class BlockBehaviors {
    public static List<Int3> Collect(IBlockReader world, Block block, Int3 cell) { ... }
}

// the one adapter that keeps Godot's noise behind the seam.
public sealed class GodotNoise2D : INoise2D {
    private readonly FastNoiseLite _n;
    public float Sample(float x, float z) => _n.GetNoise2D(x, z);
}
```

Effort, in honest S/M/L:

| step | size | what | result |
| --- | --- | --- | --- |
| delete dead code | **S** (~1 h) | remove `Blocks.ColorOf` (`Blocks.cs:54-70`, no call sites) and the unused `VoxelWorld world` parameter at `TerrainGenerator.cs:59` | `Blocks.cs` becomes engine-free outright — **built and verified** |
| port the rules core | **S/M** (~half a day) | `Int3`; `IBlockReader` at 3 sites; `INoise2D` at 3; `Mathf.*` → `Math.*` | **331 LOC engine-free, 0 errors — built and verified** in `.pi/boundary/seam/` |
| split world and player | **M** (~1–2 days) | `ChunkStore` (engine-free: `EntityStore`, `_index`, `GetBlock`/`SetBlock`/`CreateChunk`/`MarkDirty`) + `ChunkRenderer : Node3D` (material, nodes, budget loops); `PlayerRules` (pure) + the node/input adapter; `IClock` at 16 sites | the 69-assertion selftest runs under `dotnet test` with no Godot |

**Overall: M — 2 to 4 days of mechanical work with no gameplay change**, and the first half-session
already makes the block table, the break pipeline and the terrain function engine-free.

What can **never** be behind the seam, and why that is acceptable:

- **Rendering** (`ArrayMesh`, `MeshInstance3D`, `ShaderMaterial`, `Texture2DArray`) — creating GPU
  resources is the engine's job. Hiding it behind an interface buys nothing but a VRAM leak you
  debug at 3am.
- **Physics** (`MoveAndSlide`, `GetWorld3D`, `PhysicsRayQueryParameters3D`) — the solver is the
  engine. What belongs behind the seam is the *decision* (is this cell solid, does the body fit),
  and `BodyFits`/`SurfaceY` already reduce to `GetBlock`.
- **Input** (12 sites, 2 files) — an adapter's job. The seam should let a test *write*
  `PlayerIntent` directly instead of synthesising key presses; `PlayerIntent` is already the right
  shape for that.
- **Resource IO** (`FileAccess`, `Image`, `ResourceLoader`, 37 lines in `TexPack.cs`) — import
  pipelines are engine-adjacent by definition.
- **The frame loop itself** (`_Process`/`_PhysicsProcess`, 5 system calls in `Game.cs`). Driving
  systems from the engine is what a game engine is for.

That is the acceptable boundary: **decisions engine-free, side effects in adapters.** The current
code has the side effects in the right places and the decisions in the wrong ones.

## Verdict

Regress is **not** separated, and it is one thin seam away from being separable: 0% of `src/`
compiles without GodotSharp, but the *calls* that couple logic to the engine are four parameter
sites, and removing them plus swapping one value struct was verified to free 331 LOC of the rules
core. Today the coupling is shallow — a dead method, 46 `Vector3I`s, 44 `Mathf`s, a
`FastNoiseLite` field, a `Time` call loop, 10 `Input` lines inside `PollInput`, and
`VoxelWorld : Node3D` putting the world, its ECS store and its systems on one node. The cheapest
credible path is not a rewrite: delete the dead `Blocks.ColorOf`, add `Int3` and three one-method
interfaces, then split node work out of `VoxelWorld` and `PlayerSystems` — half a session for the
first two steps (S, verified), 1–2 days for the split (M). Development is constrained by the engine
today in a concrete way worth naming: **the 69-assertion selftest cannot run without
`SelfTest.Run(VoxelWorld world)` taking a `Node3D`, so every logic change costs a Godot boot.** If
nothing is done, the rot is quiet and directional: `Mathf` is already the default for clamp and
floor, `Vector3I` is already the default for cell coordinates, and each new system adds a couple
more sites — the engine-free fraction drifts from 0% toward unrecoverable without anyone deciding
it should.

## Doc-vs-code check

`docs/architecture.md` is unusually honest about its coupling — "Godot stays at the edges, not
outside" is a weaker claim than "fully separated", and the doc states `ChunkVisual` holds nodes
deliberately. Its structural claims hold up. Two sentences do not.

**Overclaim 1** — sentence originally at `docs/architecture.md:143` (corrected here, now line 145):

> `ChunkVisual` puts engine handles into the archetype, and `Player.cs` is the only file that
> reads `Input`.

False, and contradicted by the same document's own systems table at line 74 (`PollInput` is "the
only place that reads `Input`"). `PlayerSystems.PollInput` reads it at `src/PlayerSystems.cs:79-90`
— eight `Input.IsKeyPressed`, plus `Input.MouseMode` and `Input.IsMouseButtonPressed`. `Player.cs`
also reads `Input` (`src/Player.cs:48`, `:58`, `:64`), but only for mouse-capture mode, not
gameplay. Two files read `Input`, and the gameplay reads live in `PlayerSystems.cs`.

**Overclaim 2** — sentence at `docs/architecture.md:57`, unchanged line number:

> `ChunkVisual` holds Godot node handles — the only engine-aware component.

False four lines above its own component listing: `PlayerBody` holds `CharacterBody3D Node` and
`Camera3D Camera` (`src/PlayerSystems.cs:12-13`), `PlayerIntent.Wish` is a `Vector3` (`:34`), and
`PlayerMining.Target` is a `Vector3I` (`:50`). Four engine-aware components, not one.

Both corrected in place, minimally (see `git show` for the two-hunk diff):

> `ChunkVisual` holds Godot node handles — an engine-aware component, and not the only one
> (`PlayerBody` holds the player node and camera; `PlayerIntent.Wish` and `PlayerMining.Target`
> are engine value types). It is a *handle*, never game state: no system reads gameplay facts out
> of it.

> `ChunkVisual` puts engine handles into the archetype, and `PollInput` in `PlayerSystems.cs` is
> the only place that reads gameplay `Input` (`Player.cs` only toggles mouse capture).

Every other checked claim held — including the two most load-bearing for this audit: "the only
engine-aware component" is wrong in the way itemised above but the *handle, never game state* rule
it states is upheld (`src/ChunkComponents.cs:37` is the only engine-typed chunk struct), and "the
world owns both the store and its systems" (`src/VoxelWorld.cs:14-19`). The doc's "Known
weaknesses" list misses the largest weakness in this document — that none of it compiles without
the engine — which is exactly the gap this file fills.
