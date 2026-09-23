# Touch input (Android/iOS)

## 1. Scope & non-goals

Scope: a touch adapter that feeds the existing input contract. Nothing else.

- No new input architecture: touch writes the same `PlayerState` / `PlayerIntent` fields
  the keyboard path writes.
- No new dependencies.
- No `project.godot` changes. `project.godot` has no `[input]` section at all — the game
  reads keys directly, so there are no input actions to reuse or add.

## 2. Input contract

The full set of fields touch drives. Line numbers cite `master@ba8e162` — `src/PlayerSystems.cs`
independently verified by worker-48, `src/Player.cs` by worker-49.

**Phase 2's first action is to re-pin these line numbers.** After `feat/scale-64` merges, re-run
the greps against the merged master for `src/PlayerSystems.cs` / `src/Game.cs` / `src/Blocks.cs`
and update this table **before any `src/**` edit** — the scale-64 merge shifts
`PlayerSystems.cs` and `Game.cs` lines.

| Field | Def | Writer | Consumer |
|---|---|---|---|
| `PlayerState.Yaw` / `.Pitch` | `src/PlayerSystems.cs:21-22` | `src/Player.cs:65-66` (mouse look) | `Look` (`src/PlayerSystems.cs:127-139`, writes `:133-134`) |
| `PlayerState.Selected` | `src/PlayerSystems.cs:20` | `src/Player.cs:91` | `Game.RefreshHud` (`src/Game.cs:246-252`) |
| `PlayerState.Flying` | `src/PlayerSystems.cs:18` | `src/Player.cs:98` | `Move` (`src/PlayerSystems.cs:149`) |
| `PlayerIntent.Wish` | `src/PlayerSystems.cs:48` | `src/PlayerSystems.cs:104-109` | `PollInput` normalize + `Move` (`:151-158`) |
| `PlayerIntent.Jump` / `.Up` / `.Down` | `src/PlayerSystems.cs:49` | `src/PlayerSystems.cs:110-112` | `src/PlayerSystems.cs:151-153` (Up/Down), `:161` (Jump) |
| `PlayerIntent.Mining` | `src/PlayerSystems.cs:52` | `src/PlayerSystems.cs:114-115` | `src/PlayerSystems.cs:185` |
| `PlayerIntent.Place` | `src/PlayerSystems.cs:55` | `src/Player.cs:71` | `src/PlayerSystems.cs:342-345` |
| `PlayerIntent.RotateNext` / `.RotatePrev` | `src/PlayerSystems.cs:58` | `src/PlayerSystems.cs:117-122` (edge-queued at `:119-120`) | `src/PlayerSystems.cs:267`, applied in `Rotate` `:250-252`, cleared `:284-285` |

Struct declarations: `PlayerState` `:16`, `PlayerIntent` `:46` (`RotateNextHeld`/`RotatePrevHeld`
`:62`, `AutoWalk`/`AutoSprint`/`AutoMine` `:65`). Movement/look consumers: `Move` `:149-161`,
`Mine` `:185`, `Look` `:133-134`, raycasts `:391` and `:410-411`.

`Wish` / `Jump` / `Up` / `Down` / `Sprint` / `Mining` are clobbered every frame by `PollInput`
(`:109-115`), which is exactly why the touch merge state lives inside `PollInput`. `Place` and
the rotate counters are NOT clobbered: `Build` clears `Place` (`:344`) and `UpdatePending`
zeroes the rotate counters (`:284-285`). `Jump` and `Up` read the same key today (`:110`/`:111`),
so one touch Jump button drives both.

Movement/look values:

- Drag uses the same formulas as mouse look (`src/Player.cs:65-66`).
- Touch sensitivity is 0.0044 rad/px — 2x the mouse `LookSensitivity = 0.0022f`
  (`src/Player.cs:12`).
- Same pitch clamp: ±1.5533 rad (`src/Player.cs:66`).

Sprint and Creative are NOT driven by touch. They remain keyboard-only: Sprint is written
by `PollInput` (`src/PlayerSystems.cs:113`), Creative by `src/Player.cs:99`
(see open questions).

### Completeness proof

The only consumers of these fields are `Move` (`src/PlayerSystems.cs:142-177`),
`Mine` (`:180-213`), `Build` (`:337-346`), `UpdateGhost` (`:294-303`) and
`Look` (`:127-139`); all raw `Input` reads live in `src/Player.cs` and `PollInput`
(`src/PlayerSystems.cs:97-124`) — grep finds no other `Input.` use in `src/`. Every field
a consumer reads is listed above, so nothing else is needed.

## 3. Control scheme (frozen numbers)

- Virtual stick: radius 150 px, dead zone 24 px, knob 56 px; floating base placed at
  touch-down in the left half. → one analog axis, no fixed thumb pad to miss.
- Look drag: right half, 0.0044 rad/px. → matches the mouse feel at 2x, same clamp.
- Tap: release with total movement ≤16 px (TapSlopPx) before HoldMs 250 ms → `Place++`.
  → a stray tap must be small; a drag must not place.
- Hold: still pressed at HoldMs 250 ms → `Mining` until release. → break is the sustained
  gesture, place is the instant one; holding past HoldMs *is* the tap boundary, so a
  second time threshold would be dead configuration.
- Movement >16 px = look; no place and no mine on release.
- Interact-beats-place precedence unchanged: `RightClickAction` (`src/PlayerSystems.cs:398-400`),
  used by `RequestPlaceAtCrosshair` (`:406-424`).
- Buttons: Jump (= `Up` while flying; both read the same key today,
  `src/PlayerSystems.cs:110-111`), Down, Fly toggle, hotbar row of `Blocks.Palette.Length`
  buttons (`src/Blocks.cs:40-44`, 9 entries), Q/E rotate. **No** Sprint, Creative or Respawn
  button (rulings in §8). If sprint is ever wanted, the upgrade path is a forward-direction
  double-tap on the stick — zero extra HUD space; do not add a button first.

## 4. HUD plan

- `TouchControls : CanvasLayer`, Layer 2 (above the existing `Hud` layer, `src/Game.cs:256`).
- Created only when `DisplayServer.IsTouchscreenAvailable() || --touch || --touchtest`.
- Children: `StickArea` + `LookArea` (`Control`, `MouseFilter = Stop`, drawn with
  `DrawCircle` — no textures, no assets), `HBoxContainer` of hotbar `Button`s,
  Jump / Down / Fly / rotate buttons.
- Hidden on desktop because the node is never created: zero cost, nothing to hide.
- The layout assumes landscape. That is already handled outside this branch: `project.godot`
  carries `window/handheld/orientation=4` (sensor-landscape) in lead-20's
  `feat/release-5platform` worktree (`Regress-rel5`, uncommitted as of 2026-09-23). This
  branch does not touch `project.godot`.

## 5. Adapter design + traps

- New `src/TouchControls.cs` with static merge state (`Move`, `Jump`, `Down`, `Mining`)
  consumed by `PollInput`. `PollInput` stays the only place that reads `Input`.
- Event-driven writes for `Yaw` / `Pitch` / `Selected` / `Flying` / `Place`.
- When touch UI is active, `Player._UnhandledInput` (`src/Player.cs:56`) early-returns
  (one path, no double handling) and the mouse is NOT captured (`SetMouseCaptured`,
  `src/Player.cs:53-54`).
- `Input.EmulateMouseFromTouch = false` when touch is active. Godot synthesizes emulated
  mouse events from real touches — this kills the Android double-rotation trap.
- `--touch` additionally sets `Input.EmulateTouchFromMouse = true` for desktop smoke
  tests. Cost: a mouse click also emits a screen touch; Godot `BaseButton` ignores
  emulated touches because it checks `InputEvent.DEVICE_ID_EMULATION`.
- No `project.godot` edit.

## 6. Test plan

`--touchtest` scenario, new `src/TouchTest.cs`:

- `ProcessPriority = 1` so it runs after `Game._Process` (`src/Game.cs:100-119`).
- Frame-scripted injection at real node rects, no wall-clock assertions.
- **Phase-2 gate — resolve before writing the scenario:** confirm that headless
  `Viewport.PushInput` reaches `Control._GuiInput`. If it does not, use the fallback
  `Input.ParseInputEvent`. Record the outcome here and in the phase-2 report:
  `injection: <PushInput|ParseInputEvent> (<why>)`. Neither is established until run.
- Checks: `hud` / `stick` / `stick-release` / `drag-yaw` / `tap-place` / `hold-mine`
  (+ real block break in Creative) / `jump` / `fly` / `hotbar` / `classify` (pure
  function).
- Exactly one machine line: `scenario touch: PASS|FAIL <detail>`, exit code 0/1 — the
  repo-wide scenario convention (`docs/testing.md:80-81`).
- `tools/run_game_tests.py` entry: tier A, `argv=["--headless", "--path", ".", "--", "--touchtest"]`,
  `markers=["scenario touch: PASS"]`, `exit=0`, added to `SCENARIOS`
  (`tools/run_game_tests.py:25-61`).
- `Classify(elapsedMs, movedPx)` is a pure function: `movedPx > TapSlopPx (16)` → Look;
  else `elapsedMs >= HoldMs (250)` → Hold; else Tap. On release: never became Hold and
  moved ≤ slop → `Place++`; Hold already started → just end Mining; moved > slop →
  nothing (pure look).
- `HoldMs` is the only public calibration knob the scenario sets: 60000 for the tap check
  (a release can never be a hold, so it is always a Tap), 0 for the hold check (fires next
  frame). No wall-clock assertion anywhere. Justification: holding past `HoldMs` *is* the
  tap boundary, so a second threshold would be dead configuration.
- Mutation proof list. Each mutation must be run and its **raw `scenario touch: FAIL ...`
  line quoted verbatim** in the phase-2 report and pasted into this section — that quote is
  the only evidence the assertions can fail:
  - flip the drag sign → `drag-yaw` red
  - force `Classify` to Look → `tap`/`hold` red
  - drop the `Wish` merge → `stick` red

## 7. Evidence ceiling

Phase 2 WILL prove:

- headless intent-level checks
- one real block break
- one real Place-queue consume
- the tier A suite (selftest/demo/bench) still green
- a tier B screenshot of the HUD, read by the lead

Phase 2 WILL NOT prove:

- real finger / OS touch indices
- DPI / safe-area / notch
- multi-touch ordering on device
- phone performance
- iOS specifics

Device testing is the user's job.

## 8. Rulings (2026-09-23, Boss)

All eight questions are decided; nothing here is open.

| Question | Ruling |
|---|---|
| Sprint button | **No.** Upgrade path if ever wanted: forward-direction double-tap on the stick (zero HUD space) — not a button. |
| Creative toggle | **No** — debug-only, via CLI/debug flag. |
| Respawn button | **No** — the existing auto-unstuck covers it (`Move` depenetration, `src/PlayerSystems.cs:172-175`). |
| Rotate Q/E buttons | **Yes** — 24-orientation placement is a core mechanic and cannot be missing on mobile. |
| Desktop debug flag | **Yes** — `--touch`, which doubles as the HUD screenshot evidence path. |
| Tap/hold assignment | Tap = place, hold = break (MC PE style); drag = pure look, no place and no mine. |
| Hotbar row vs cycle | **Row of 9 buttons** — landscape is locked, the width is there; no cycle key (dead configuration). |
| Android landscape lock | **Already done by lead-20** — `window/handheld/orientation=4` in `project.godot`; `export_presets.cfg` untouched. |

## 9. File inventory + phase-2 diff budget

- New: `src/TouchControls.cs`, `src/TouchTest.cs`
- Edits: `src/Game.cs` ~8 lines, `src/Player.cs` ~3, `src/PlayerSystems.cs` ~6,
  `tools/run_game_tests.py` +1 entry, `docs/touch.md`
- Optional: `docs/testing.md` scenario-table row
