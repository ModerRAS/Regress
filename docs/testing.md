# Game tests

Scenarios are the game's own entry points (`--selftest`, `--demo`, `--bench`, `--shot`), driven by
`tools/run_game_tests.py`. The runner is stdlib-only, runs scenarios **strictly one at a time**,
and fails if a marker is missing, the exit code is wrong, or an expected artifact is missing.

## 1. The two tiers

| tier | needs | scenarios | CI |
| --- | --- | --- | --- |
| **A** | `--headless`; boots the game logic and the ECS world, no rendering | selftest, demo, bench | **yes** |
| **B** | a real renderer/GPU; renders frames and reads pixels back | shot, and any pixel check | **no — local only** |

CI cannot run tier B because the headless dummy driver does not compile GPU pipelines and
viewport readback returns null (`docs/texture-packs.md`, "Headless limits, stated plainly":
"the dummy driver does not compile GPU pipelines and viewport readback returns null"). A
headless run can catch shader *language* errors, but it can never prove what was drawn, so there
is no pixel evidence and no `--shot` in CI.

## 2. How to run locally

Raw commands:

```bash
godot-mono --headless --path . -- --selftest
godot-mono --headless --path . -- --demo
godot-mono --headless --path . -- --bench
godot-mono --path . -- --shot=build/game-tests/shot.png --shot-frame=90   # tier B: real renderer
```

Runner:

```bash
python tools/run_game_tests.py --tier A        # selftest + demo + bench (default is tier A)
python tools/run_game_tests.py --tier B        # shot; needs a real renderer
python tools/run_game_tests.py --only demo     # one scenario by name
python tools/run_game_tests.py --list          # print the scenario table, run nothing
python tools/run_game_tests.py --selfcheck     # check the evaluator itself, run no Godot
```

Useful extras: `--godot PATH` (or `$GODOT`) to pick the binary, `--build` to run `dotnet build`
first, `--out DIR` for logs/artifacts (default `build/game-tests`), `--timeout SEC` per scenario,
`--verbose`, and `--dry-run` to print the commands without running them. The runner exits 0 only
if every selected scenario passed, 1 on a failed scenario, 2 when no Godot binary is found.

## 3. How CI runs it

`.github/workflows/ci.yml` has a step named **"Game tests (tier A, headless)"**, after
`dotnet build` and the `--import` + self-test step:

```yaml
      - name: Game tests (tier A, headless)
        run: |
          set -o pipefail
          python tools/run_game_tests.py --godot "$HOME/godot/godot" --tier A 2>&1 | tee gametests.log
```

Tier B is never wired into CI; run it locally against a real renderer.

## 4. Scenario table (what exists today)

| name | tier | exact machine-checkable PASS line(s) | what it covers | CI | runtime |
| --- | --- | --- | --- | --- | --- |
| `selftest` | A | `SELFTEST PASS` | 277 headless assertions: terrain, mesher cross-checked against brute force, triangle winding, vertical-world invariants, break/place request pipeline, tree felling, ghost placement, mob spawn/movement/AI, texture variants, per-tile size classes, block entities (byte/entity audit) | yes | 1.1s (lead, local Windows) |
| `demo` | A | `walk: PASS`, `interact: PASS`, ` -> PASS`, `DIGDOWN PASS` (all four are runner markers) | scripted walk, chest place + open, then a scripted dig straight down and settle | yes | 4.1s (lead, local Windows) |
| `bench` | A | `bench landing PASS` | five frame-time phases with a per-system breakdown; ends by teleporting and asserting the player is still on the ground | yes (see note) | 8.4s (lead, local Windows) |
| `shot` | B | `screenshot saved to <path>` + a PNG artifact ≥ 1024 bytes | renders one frame at `--shot-frame` and saves it; proves the renderer survived to the save call | no | 2.9s (lead, local Windows) |

`bench` note: tier A is proven — the lead ran `godot-mono --headless --path . -- --bench`: exit 0
in 8.4s with `bench landing PASS`. CI may assert **only** that the scenario completes and prints
that marker: no timing and no render-column assertion may ever go into CI. Headless
`bench adapter:` is empty, `draw calls=0 primitives=0 video mem=0.0 MB`, and steady is
6.90 ms / 144.9 fps under dummy-loop pacing versus 1.4 ms on a real GPU (`docs/performance.md`).
The gen/mesh/collision averages are reference-only; they carry no thresholds. The other three
PASS lines are exact strings from `src/Game.cs` and `src/Bench.cs`; the runner's `markers` list in
`tools/run_game_tests.py` is the authority.

## 5. How to add a new scenario

1. **Print the verdict.** The game prints exactly one line `scenario <name>: PASS|FAIL <detail>`
   and exits non-zero on FAIL. (The four scenarios above predate this convention and keep their
   own lines; for them the runner's `markers` list is the contract — `walk: PASS (blocked by
   terrain)` is a legitimate variant already matched by the `walk: PASS` marker.)
2. **Add a data entry** in `tools/run_game_tests.py`: `tier`, `argv`, `markers`, `exit`, and
   optionally `artifacts` (with `min_bytes`) and a timeout. Nothing else needs to change — the
   runner's `SCENARIOS` list is the single place scenarios are declared.
3. **Keep it deterministic:** fixed seed, fixed tick/frame count, no wall-clock assertions.
4. **Prove it can fail** by mutation before keeping it — flip the assertion, watch the scenario
   report FAIL, then restore it.

For a tier B scenario, also list the output PNG under `artifacts`; the runner appends the
`screenshot saved to` marker automatically, because Godot can produce **no** PNG while still
exiting 0 (see quirks below).

## 6. What this harness cannot cover

- **No pixel evidence headless.** The dummy driver does not compile GPU pipelines and viewport
  readback returns null, so no rendered pixels, no `--shot` in CI. Pixel checks need a real
  renderer (tier B, local).
- **No cross-session frame-time comparison.** Quoting `docs/performance.md` verbatim:

  > Do not compare frame times across sessions, machines or GPU thermal states.

  The engine floor drifts ±30% with GPU thermal state, so a bench number is only meaningful
  against a floor measured in the same session.
- **Exported builds are smoke-tested, not pixel-tested** (`docs/texture-packs.md`, known
  weakness 1): the release workflow proves the twelve tiles load back as 16×16 images, which
  says nothing about their pixels.

## 7. Environment quirks

- **One Godot process at a time.** The runner runs scenarios strictly sequentially; do not run
  two runners (or a manual Godot session) against the same project at once.
- **Stale-DLL trap.** A `src/*.cs` edit is invisible to Godot until `dotnet build` has run. The
  runner warns when `Regress.dll` is older than the newest source file (or missing).
- **New `res://` textures need an import pass.** Run
  `godot-mono --headless --path . --import` after adding texture files; the runner warns when
  `.godot/imported` is missing.
- **The `--shot-frame` deadline is silent.** Godot must survive to `--shot-frame` (default 90) or
  it produces **no PNG at all** — no error, just an exit. That is why artifact scenarios must also
  print `screenshot saved to ...`, and why the runner checks the PNG size, not only the marker.
- **`--nosky` paints the background pure magenta** (`Colors.Magenta` in `src/Game.cs`). Never
  combine `--nosky` with the zero-magenta pixel check below.

## 8. Planned boundary scenarios (phase 2 — NOT IMPLEMENTED)

**None of these exist yet.** They are gated on `feat/scale-64` merging (64³ chunks built from 16³
sections, 64 sections per chunk); `src/**` is untouched by this document. The history citations
are real: commit hashes from this repo, numbered weaknesses from the docs, and the recorded
scale-64 observations.

Provenance: the `obs-…` ids below name entries in a local LLM wiki, not artifacts in this
repository, so they are not verifiable from a clone. The commit hashes and numbered `docs/`
weaknesses cited alongside them are the repo-verifiable record.

| scenario | tier | boundary/regression covered | history citation |
| --- | --- | --- | --- |
| `cross_section_walk` | A | Walk across a 16³ section boundary inside a 64³ chunk and stay on solid ground. The collision gate retags a dirty chunk (`+NeedsMesh −NeedsCollision`), so on scale-64 the per-section collision-complete bit must be cleared with it — otherwise a rebuilt section keeps no shape and the player walks over a hole. | `84de710` (collision gate + edit retag, `src/VoxelWorld.cs:476-477`); `docs/performance.md` "CollisionRadius (2 chunks)" — the gate "cannot silently drop the player through the world"; scale-64 section decision (wiki observation `obs-2026-09-23-regress-64-chunk-section-budgeted-mesh-collision-decision`) |
| `landing` | A | After `TeleportToSurface`/respawn, the landing section has **both** mesh and collision, the player is on solid ground, and the worst frame during landing stays within a stated budget. This is the `EnsureAreaAround` synchronous-64-sections regression. | `docs/performance.md` "spawn/respawn meshes one chunk, not nine — removed a ~40 ms teleport hitch" and the teleport row (avg 7.3 ms, 1% low 12.0 ms); wiki source `voxel-chunk-streaming-time-budget` ("generating *and* meshing a 3x3 of chunks synchronously (~40 ms hitch)"); scale-64 shared-deadline decision (same wiki observation) |
| `section_coverage` | A | After N frames stationary then moved, every non-air section inside the collision gate has its per-section `CollisionDone` bit set. | scale-64 decision: sections are budgeted work items inside the chunk task, not entities/tags, so a per-section done bit is the only record that a section finished (wiki observation `obs-2026-09-23-regress-64-chunk-section-budgeted-mesh-collision-decision`); `docs/performance.md` collision-gate paragraph |
| `dig_to_bedrock` | A | A scripted dig-down reaches bedrock without falling through and without stopping early. | `84de710` (scripted dig-down); the current `--demo` runs a shallow version — `DIGDOWN PASS` only asserts depth > 10 and above bedrock (`src/Game.cs` frames 252/460/500), so the planned scenario tightens it to bedrock |
| `fell_tree` | A | One break removes trunk **and** canopy (Wood and Leaves both zero in the bounding box) — ties the derived `MaxBlocksPerBreak` to a real assertion. | scale-64 derivation: `MaxTreeBlocks` = 2789 (trunk 384 + canopy 2405), `MaxBlocksPerBreak` = 2× = 5578 (wiki observation `obs-2026-09-23-regress-scale-64-derived-maxtreeblocks-2789-maxblocksperbrea`); today it is `const 256` (`src/EditRequests.cs:111`) and `--selftest` only asserts `>= 64` (`src/SelfTest.cs:183`); `docs/architecture.md` "Felling a tree takes the connected trunk and its canopy" |
| `world_bounds` | A | Place/break at extreme coordinates (x/z ±100000, y −2000) and assert correctness. | `--selftest` already covers a block 3000 above the world (`src/SelfTest.cs:113`), negative coordinates (`:200`), and block entities at (40000, 3000, 40000) (`:2345`); the named extremes are the new envelope. Base commit `84de710` |
| `block_entity_bytes` | A | N random edits, block-entity ↔ byte consistency. **Already asserted inside `--selftest`** (200 deterministic random edits plus a two-way `Audit`, `src/SelfTest.cs` "5. two-way invariant over deterministic random edits"), so it may stay covered rather than duplicated. | `7a35598` "interactive blocks: block entities + O(1) cell registry, RMB dispatch, assertions"; `README.md` `--selftest` row: "byte/entity sync audit" |
| `no_missing_tiles` | B | A real rendered frame has **zero** pure-magenta (255,0,255) pixels — the `missing` tile colour. | `docs/texture-packs.md` procedural table: `missing` = 1.00, 0.00, 1.00 (magenta), and "`missing` (11) is the fallback for an absent or invalid tile"; pack loader/renderer commit `c323a88`. Do not run this with `--nosky` (magenta background) |
| `texture_route` | B | With the demo packs, a rendered frame contains pixels from the intended tiles (e.g. the sizes-demo pack's 64²/256² keys) and two runs of the same static close-up are byte-identical. Static close-up because canopy/far-border jitter is a known pre-existing streaming effect. | `docs/architecture.md` known weakness 5 — "Rendering-level A/B evidence must use a static close-up or a region whose neighbours are fully generated"; sizes-demo pack classes 16/64/256 (wiki observation `obs-2026-09-23-regress-sizes-demo-pack-3-size-classes-16-64-256-uid-pinning`); size-class commit `c597e1d`; note `docs/texture-packs.md` weakness 6: the procedural fallback is ±1/255-quantized, so it is not a zero-diff baseline |
