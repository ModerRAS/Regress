# Performance

Everything here is measured, not estimated. The numbers come from `--bench`, which is part of
the repository so they can be reproduced or refuted on your own machine.

## How to measure

```bash
godot-mono --path . -- --bench                 # the full run
godot-mono --path . -- --bench --frozen        # engine floor: no game code runs at all
godot-mono --path . -- --bench --view=3        # sweep the resident chunk count
godot-mono --path . -- --bench --collision=1   # sweep the collision radius
godot-mono --path . -- --bench --budget=6      # sweep the per-frame work budget
```

`--bench` disables vsync (`DisplayServer.WindowSetVsyncMode(Disabled)`) and reports five phases.
Each phase prints its frame-time distribution **and a per-frame breakdown of every system**, so a
regression is attributed rather than guessed at. `engine = frame − systems` is the engine's own
cost, which is the number that actually explains most of the frame.

## Results

AMD Radeon 780M (integrated), 1280x720, ~230 chunks resident, view distance 5:

| phase | what it does | avg | 1% low | systems | engine |
| --- | --- | --- | --- | --- | --- |
| warmup | initial world load | 6.2 ms | 91 ms | 3.24 ms | 2.98 ms |
| steady | standing still | 1.4 ms | 2.7 ms | **0.03 ms** | 1.37 ms |
| fly | flying at 22 m/s, streaming | 2.1 ms | 5.3 ms | 0.41 ms | 1.68 ms |
| teleport | respawn 300 blocks away, 6x | 7.3 ms | 12.0 ms | 5.88 ms | 1.39 ms |
| `--frozen` | no game code at all | 1.7 ms | 3.9 ms | 0.00 ms | 1.70 ms |

Per chunk: terrain generation 0.07 ms, mesh build 0.61 ms, collision 0.32 ms.

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

- **`ChunkWorkBudgetMs` (3 ms)** — meshing and collision run until a time budget is spent rather
  than a fixed chunk count. A fixed count was a real hitch: `MeshPerFrame = 6` × 4.4 ms/chunk =
  27 ms/frame = 37 fps during streaming. Any "do N per frame" scheduler over variable-cost work
  is a latent hitch.
- **`CollisionRadius` (2 chunks)** — collision is built only near the player, because
  `Mesh.CreateTrimeshShape()` was 68% of per-chunk cost. `--bench` ends by teleporting around and
  asserting the player is still standing on something, so this proximity gate cannot silently
  drop the player through the world.

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

The mesher allocates ~430 KB per chunk (lists plus `ToArray()` marshalling, now including the
UV and tile-index vertex streams), so streaming a few hundred chunks produces ~100 MB of
garbage. The millisecond budget absorbs the time, but a
two-pass count-then-fill mesher would remove the garbage entirely. Not done because no phase
currently shows GC pauses in the 1% low.

## Where parallelism would and would not help

See [roadmap.md](roadmap.md). Short version: the per-system tick is 0.03 ms, so parallelising
worlds parallelises the wrong thing. The work worth parallelising is the ~1.0 ms of per-chunk CPU
work *inside* one world, and the hard constraint is that Godot node creation must stay on the main
thread.
