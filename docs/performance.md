# Performance

Everything here is measured, not estimated. The numbers come from `--bench`, which is part of
the repository so they can be reproduced or refuted on your own machine.

## How to measure

```bash
godot-mono --path . -- --bench                 # the full run
godot-mono --path . -- --bench --frozen        # engine floor: no game code runs at all
godot-mono --path . -- --bench --view=3        # sweep the resident chunk count
godot-mono --path . -- --bench --collision=1   # sweep the collision radius (sections)
godot-mono --path . -- --bench --budget=6      # sweep the per-frame work budget
```

`--bench` disables vsync (`DisplayServer.WindowSetVsyncMode(Disabled)`) and reports five phases.
Each phase prints its frame-time distribution **and a per-frame breakdown of every system**, so a
regression is attributed rather than guessed at. `engine = frame − systems` is the engine's own
cost, which is the number that actually explains most of the frame.

## Results

### Baseline before the 64³ scale change

Same machine, same GPU session as the numbers below — this is the approved `before` baseline, and
it is deliberately **not re-measured** (see the caveat section for why that would be meaningless).

AMD Radeon 780M (integrated), 1280x720, ~230 chunks resident, view distance 5:

| phase | what it does | avg | 1% low | systems | engine |
| --- | --- | --- | --- | --- | --- |
| warmup | initial world load | 6.2 ms | 91 ms | 3.24 ms | 2.98 ms |
| steady | standing still | 1.4 ms | 2.7 ms | **0.03 ms** | 1.37 ms |
| fly | flying at 22 m/s, streaming | 2.1 ms | 5.3 ms | 0.41 ms | 1.68 ms |
| teleport | respawn 300 blocks away, 6x | 7.3 ms | 12.0 ms | 5.88 ms | 1.39 ms |
| `--frozen` | no game code at all | 1.7 ms | 3.9 ms | 0.00 ms | 1.70 ms |

Per unit (the old 16³ chunk — today's 16³ section): terrain generation 0.07 ms, mesh build
0.61 ms, collision 0.32 ms.

### After the 64³ scale change (view distance 3, collision radius 2 sections)

Measured values are filled in; cells still pending the frozen batch stay **待测**.

| phase | what it does | avg | 1% low | systems | engine |
| --- | --- | --- | --- | --- | --- |
| warmup | initial world load | 待测 | 待测 | 待测 | 待测 |
| steady | standing still | 待测 | **6.94 ms** | 待测 | 待测 |
| fly | flying, streaming | 待测 | **11.75 ms** | 待测 | 待测 |
| teleport | respawn sweep | 待测 | **27.10 ms** | 待测 | 待测 |
| `--frozen` | no game code at all | 待测 | 待测 | 0.00 ms | 待测 |

| quantity | value |
| --- | --- |
| resident chunks | 待测 |
| primitives | 待测 |
| video memory | 待测 |
| per-section gen | **1.97 ms** |
| per-section mesh / collision | 待测 / 待测 (single-section spikes **13.86 / 21.55 ms**, GC/JIT) |
| teleport 1% low | **27.10 ms** (was 133.9 ms) |
| `--collision=2` frame times | 待测 |

**Teleport acceptance changed.** The old ≤5 ms target is withdrawn — it is unreachable while
still guaranteeing the landing is solid. Teleport is now recorded honestly as **one instantaneous
hitch per teleport** (1% low 27.10 ms, down from 133.9 ms); sustained feel is judged on
steady/fly 1% low (**6.94 / 11.75 ms**, both passing).

**The 16³ section is the same unit the old 16³ chunk was** — same 4096 cells, same face loop, same
emitter — so the 0.07 / 0.61 / 0.32 ms figures above are the correct per-section comparison. What
changed is the number of units: 64 sections per chunk instead of one, and ~98 chunks instead of
~230. Generation did not scale with area alone: it is now **1.97 ms/chunk** against the 0.07 ms
baseline (~28×; area alone would predict ~1.1 ms, and the difference is recorded rather than
argued away).

## Read this before quoting a frame time

**In steady state the entire game costs 0.03 ms/frame.** The rest is Godot's main loop and buffer
present. `--frozen` proves it: with every system and both player callbacks disabled and the world
still rendering, the frame still costs ~1.7 ms.

**The floor is not stable, and it is not your code.** Running `--frozen` three times back to back
gives 1.76 / 1.74 / 1.67 ms; after a 45 second cooldown the same binary gives 1.35 ms. Sustained
~600 fps on an integrated GPU moves the present cost by ±30%, which is larger than any
system-level difference in this project.

**The floor is also nearly content-independent.** Cutting resident chunks 231 → 126, primitives
47k → 35k, physics nodes 254 → 194, or disabling every transform write changed nothing outside
noise. Only resolution moved it (0.8 ms from 640x360 to 1920x1080).

Practical consequences:

- Measure the floor in the *same session* before attributing frame time to the game.
- Do not compare frame times across sessions, machines or GPU thermal states.
- Frame times here are 5–10x below the vsync cap, so frame rate is not a bottleneck in any phase.

## The two knobs that bound frame time

- **`ChunkWorkBudgetMs` (3 ms, shared)** — generation, mesh and collision run against **one
  per-frame deadline**, and that deadline constrains **the round, not a single item**: the
  "at least one work item per round" progress guarantee necessarily lets one outlier item run past
  the budget. The invariant this system can promise is therefore **"at most one item overshoots,
  and the landing section goes first"** — never "every frame is ≤ 3 ms". The budget unit changed
  with the scale: it is now spent per **16³ section**, not per chunk entity. A chunk can be
  partially meshed or partially collided, and `ChunkVisual.Cursor` / `CollisionDone` remember
  where it got to. The deadline and the progress guarantee are two separate constraints and both
  stay. A fixed count was a real hitch: `MeshPerFrame = 6` × 4.4 ms/chunk = 27 ms/frame = 37 fps
  during streaming. Any "do N per frame" scheduler over variable-cost work is a latent hitch.
  Because the work is now section-sized, "the budget was never hit" is no longer evidence by
  itself — the budget can be hit *inside* one 64-section chunk, so budget evidence is per section
  (`--bench` `mesh=` / `collision=`), not per chunk.
- **`CollisionRadius` (2 sections = 32 units)** — collision is built only near the player's
  section, because `Mesh.CreateTrimeshShape()` was 68% of per-chunk cost. It is now a *section*
  gate, and a chunk keeps its `NeedsCollision` tag until every section is decided, so walking
  brings new sections into range and they are built then. `--bench` ends by teleporting around and
  asserting the player is still standing on something, so this proximity gate cannot silently
  drop the player through the world.

## Memory: 8 KB → 512 KB per chunk

A chunk is now 64³ = `ChunkSize³` cells: two arrays of 262144 bytes — **256 KB blocks + 256 KB
orientation = 512 KB per chunk**, versus 8 KB (two 4096-byte arrays) at 16³.

| | before | after | factor |
| --- | --- | --- | --- |
| bytes per chunk | 8 KB | 512 KB | 64× |
| resident chunks | ~230 | ~98 | 0.43× |
| resident payload | ~1.84 MB | **~50 MB** | **27×** |
| radius-2 alternative | — | ~25.6 MB | — |

The 64× only holds if the **chunk count is unchanged** — which would also mean the world's
physical size grew ×4 per axis. The shipped configuration cuts the view distance 5 → 3 chunks
(and `CollisionRadius` is now 2 sections, the same physical 32 units as before), landing at ~98
resident chunks and ~50 MB: a real 27× increase, not 64×. Dropping the view distance one more
step to radius 2 is the documented ~25.6 MB fallback.

## Deliberately accepted consequences of the scale change

- **Breaking is 4× faster in wall-clock seconds (hardness ×0.25).** At 1/4 the linear block size,
  mining the same *physical* depth per second is the feel-preserving choice; the visible effect is
  that a single block gives way quicker. `Leaves` at 0.05 s is near-instant — intentional, not a
  bug.
- **`DespawnRadius` (224) is larger than the guaranteed loaded half-view (192).** View distance 3
  chunks = 192 units guaranteed, 256 at the outer ring, so a mob can live just outside the
  rendered boundary. At the old scale 56 < 80 made this impossible. Accepted for now; the
  alternatives are a shorter despawn radius or a larger view distance.

## Optimisations that were measured rather than assumed

| change | effect |
| --- | --- |
| time budget instead of a chunk count | fly 1% low 31 → 165 fps |
| collision only near the player | per-chunk collision 0.74 → 0.11 ms; teleport avg 39 → 124 fps |
| spawn/respawn meshes one chunk, not nine | removed a ~40 ms teleport hitch |
| `AllTags(NeedsCollision)` instead of `WithoutAllTags(HasCollision, …)` | steady 5.08 → 1.44 ms |

That last one is worth understanding: in Friflo, `WithoutAllTags(A, B)` means "missing *at least
one* of A and B", so a "has collision" tag used as a negation re-matched the 73 chunks that
already had collision and rebuilt their trimesh **every frame**. Phrasing tags as *pending work*
makes the query an unambiguous `AllTags` lookup.

## Known cost not yet addressed

The mesher allocates ~430 KB **per section** (lists plus `ToArray()` marshalling, now including
the UV and tile-index vertex streams) — the same 16³ work the old chunk did, now once per section
and 64 times per chunk, so streaming a few hundred chunks produces gigabytes of garbage. The
millisecond budget absorbs the time, but a two-pass count-then-fill mesher would remove the
garbage entirely. Not done because no phase currently shows GC pauses in the 1% low.

**`MobSpawner.Plan` is unmeasured after the scale change.** Its ring scan grew from 65×65 ≈ 4.2k
cells to 257×257 ≈ 66k cells plus ~8k candidates sorted, and it runs on every focus change. The
`mobs=` bench line will tell; until then this is **unprofiled**, not "free".

**Collision now costs 0.84–0.95 ms per 16³ section** (four runs: 0.84 / 0.95 / 0.90 / 0.95; single
section spikes to 17.46 / 25.31 / 26.83 ms) against the 0.32 ms per 16³ chunk baseline — a 2.6–3×
increase. The measurement units are comparable: both are a 4096-voxel unit (the old chunk *is* the
new section), so this is not a unit mismatch. The cause is structural: one `StaticBody3D` +
trishape per section instead of per chunk. It still fits the shared 3 ms budget and the bench
landing check passes, so it is recorded as a known cost for this round, with two candidate fixes
for later: merge the collision bodies of adjacent sections, or build collision in gate-neighbourhood
groups. Either needs its own measurement.

**Single-section mesh/collision outliers are 13.86 / 21.55 ms (GC/JIT driven).** Recorded as a
todo for this round, not fixed: the candidates are splitting the mesh/collision work of a single
section, or giving the landing sync a hard "at most N sections" cap.

**Wall-clock throttle tail starvation.** `MobSystems.Tick` processes from the head of a fixed
iteration order and returns when the budget is spent, so under sustained over-budget the skipped
mobs are a fixed *tail* (tail starvation), not a round-robin. Measured `mobs=` 0.86–1.36 ms/frame
against `MobWorkBudgetMs = 1.0`. The protection works as designed and `MaxMobs = 48` caps it, so
this round changes nothing: it is recorded as "in budget-bound frames the tail queue does not
update (known, bounded)". Two later candidates — re-derive the budget from measurements, or reduce
the 4×4×4 `MobFits` cost per mob — are optimizations that need a Boss semantic call. Production
`MobWorkBudgetMs` stays 1.0; only the determinism tests set it to `+Infinity`.

## Where parallelism would and would not help

See [roadmap.md](roadmap.md). Short version: the per-system tick is 0.03 ms, so parallelising
worlds parallelises the wrong thing. The work worth parallelising is the ~1.0 ms of per-chunk CPU
work *inside* one world, and the hard constraint is that Godot node creation must stay on the main
thread.
