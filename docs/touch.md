# Touch input (Android/iOS)

## 1. Scope & non-goals

Scope: a touch adapter that feeds the existing input contract. Nothing else.

- No new input architecture: touch writes the same `PlayerState` / `PlayerIntent` fields
  the keyboard path writes.
- No new dependencies.
- No `project.godot` changes. `project.godot` has no `[input]` section at all — the game
  reads keys directly, so there are no input actions to reuse or add.

## 2. Input contract

The full set of fields touch drives. Line numbers cite `master@e966356` (release-5platform merged).

| Field | Def | Writer | Consumer |
|---|---|---|---|
| `PlayerState.Yaw` / `.Pitch` | `src/PlayerSystems.cs:21-22` | `src/Player.cs:65-66` (mouse look) | `Look` (`src/PlayerSystems.cs:127-139`, writes `:133-134`) |
| `PlayerState.Selected` | `src/PlayerSystems.cs:20` | `src/Player.cs:91` | `Game.RefreshHud` (`src/Game.cs:821-827`) |
| `PlayerState.Flying` | `src/PlayerSystems.cs:18` | `src/Player.cs:98` | `Move` (`src/PlayerSystems.cs:149`) |
| `PlayerIntent.Wish` | `src/PlayerSystems.cs:48` | `src/PlayerSystems.cs:104-109` | `PollInput` normalize + `Move` (`:151-158`) |
| `PlayerIntent.Jump` / `.Up` / `.Down` | `src/PlayerSystems.cs:49` | `src/PlayerSystems.cs:110-112` | `src/PlayerSystems.cs:151-153` (Up/Down), `:161` (Jump) |
| `PlayerIntent.Mining` | `src/PlayerSystems.cs:52` | `src/PlayerSystems.cs:114-115` | `src/PlayerSystems.cs:185` |
| `PlayerIntent.Place` | `src/PlayerSystems.cs:55` | `src/Player.cs:71` | `src/PlayerSystems.cs:343-346` |
| `PlayerIntent.RotateNext` / `.RotatePrev` | `src/PlayerSystems.cs:58` | `src/PlayerSystems.cs:117-122` (edge-queued at `:119-120`) | `src/PlayerSystems.cs:268`, applied in `Rotate` `:252-254`, cleared `:285-286` |

Struct declarations: `PlayerState` `:16`, `PlayerIntent` `:46` (`RotateNextHeld`/`RotatePrevHeld`
`:62`, `AutoWalk`/`AutoSprint`/`AutoMine` `:65`). Movement/look consumers: `Move` `:149-161`,
`Mine` `:185`, `Look` `:133-134`, `Raycast` call sites `:367` and `:387` (helper `:456`).

`Wish` / `Jump` / `Up` / `Down` / `Sprint` / `Mining` are clobbered every frame by `PollInput`
(`:109-115`), which is exactly why the touch merge state lives inside `PollInput`. `Place` and
the rotate counters are NOT clobbered: `Build` clears `Place` (`:345`) and `UpdatePending`
zeroes the rotate counters (`:285-286`). `Jump` and `Up` read the same key today (`:110`/`:111`),
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
`Mine` (`:180-212`), `Build` (`:338-347`), `UpdateGhost` (`:295-304`) and
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
- Interact-beats-place precedence unchanged: `RightClickAction` (`src/PlayerSystems.cs:405-407`),
  used by `RequestPlaceAtCrosshair` (`:413-431`).
- Buttons: Jump (= `Up` while flying; both read the same key today,
  `src/PlayerSystems.cs:110-111`), Down, Fly toggle, hotbar row of `Blocks.Palette.Length`
  buttons (`src/Blocks.cs:40-44`, 9 entries), Q/E rotate. **No** Sprint, Creative or Respawn
  button (rulings in §8). If sprint is ever wanted, the upgrade path is a forward-direction
  double-tap on the stick — zero extra HUD space; do not add a button first.

## 4. HUD plan

- `TouchControls : CanvasLayer`, Layer 2 (above the existing `Hud` layer, `src/Game.cs:831`).
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
- When touch UI is active, `Player._UnhandledInput` (`src/Player.cs:56`) returns early for
  mouse events only (`if (TouchControls.Active && @event is InputEventMouse) return; // touch
  drives look/place; the emulated mouse must not double-handle it`). Keyboard stays live on
  hybrid touchscreen desktops (Escape/1-9/F/G/R). The mouse is NOT captured (`SetMouseCaptured`,
  `src/Player.cs:53-54`).
- `Input.EmulateMouseFromTouch = false` when touch is active. Godot synthesizes emulated
  mouse events from real touches — this kills the Android double-rotation trap.
- `--touch` additionally sets `Input.EmulateTouchFromMouse = true` for desktop smoke
  tests. Cost: a mouse click also emits a screen touch; Godot `BaseButton` ignores
  emulated touches because it checks `InputEvent.DEVICE_ID_EMULATION`.
- No `project.godot` edit.

## 6. Test plan

`--touchtest` scenario, new `src/TouchTest.cs`:

- `ProcessPriority = 1` so it runs after `Game._Process` (`src/Game.cs:181-350`).
- Frame-scripted injection at real node rects, no wall-clock assertions.
- `injection: PushInput (Viewport.PushInput(ev, true); headless down/drag/up reach Control._GuiInput, verified 4.7.2). Gesture checks inject at the HUD's real rects; button checks re-lay the 14 buttons into a 64x64 test grid first (headless viewport is 64x64 and --resolution is ignored there). The HUD's pixel layout is NOT verified headless — only by the tier-B --touch screenshot.`
- Checks: `hud` / `stick` / `stick-release` / `drag-yaw` / `tap-place` / `hold-mine`
  (+ real block break in Creative) / `jump` / `fly` / `hotbar` / `rotate` / `classify`
  (pure function).
- Exactly one machine line: `scenario touch: PASS|FAIL <detail>`, exit code 0/1 — the
  repo-wide scenario convention (`docs/testing.md:81-82`).
- `tools/run_game_tests.py` entry: tier A, `argv=["--headless", "--path", ".", "--", "--touchtest"]`,
  `markers=["scenario touch: PASS"]`, `exit=0`, added to `SCENARIOS`
  (`tools/run_game_tests.py:25-69`).
- `Classify(elapsedMs, movedPx)` is a pure function: `movedPx > TapSlopPx (16)` → Look;
  else `elapsedMs >= HoldMs (250)` → Hold; else Tap. On release the classified gesture is
  the single decision point: Tap → `Place++`; Hold (or already mining) → just end Mining;
  Look → nothing (pure look).
- `HoldMs` is the only public calibration knob the scenario sets: 60000 for the tap check
  (a release can never be a hold, so it is always a Tap), 0 for the hold check (fires next
  frame). No wall-clock assertion anywhere. Justification: holding past `HoldMs` *is* the
  tap boundary, so a second threshold would be dead configuration.
- Mutation proof list. Each mutation must be run and its **raw `scenario touch: FAIL ...`
  line quoted verbatim** in the phase-2 report and pasted into this section — that quote is
  the only evidence the assertions can fail:
  - flip the drag sign → `drag-yaw` red:
    `scenario touch: FAIL drag-yaw: Yaw=0.44 expected -0.44`
  - force `Classify` to Look → `tap`/`hold` red. The scenario reports only the first
    failure, so each layer was exposed with the earlier checks temporarily neutralised in
    the working tree only (all restored; `git diff` clean afterwards):
    `scenario touch: FAIL classify: 100ms/5px -> Tap`
    `scenario touch: FAIL tap-place: Place=0`
    `scenario touch: FAIL hold-mine: Mining not set after HoldMs=0`
  - drop the `Wish` merge → `stick` red:
    `scenario touch: FAIL stick: Wish.Z=0`

  All runs exited 1 (lead-24, worktree `Regress-touch`, 2026-09-23).

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

## 9. File inventory (phase-2 diff)

- New: `src/TouchControls.cs` (242 lines), `src/TouchTest.cs` (263 lines), plus the
  generated `src/TouchControls.cs.uid` / `src/TouchTest.cs.uid` (tracked like every other
  script uid).
- Edits: `src/Game.cs` +13, `src/Player.cs` +3/-1, `src/PlayerSystems.cs` +13/-4,
  `tools/run_game_tests.py` +8 (one `SCENARIOS` entry), `docs/touch.md`,
  `docs/testing.md` (+1 scenario-table row).
- No new dependencies, no `project.godot`, no `export_presets.cfg`, no `Regress.csproj` change.
