using Friflo.Engine.ECS;
using Godot;

namespace Regress;

public partial class Game : Node3D
{
	public VoxelWorld World { get; private set; }
	public Player Player { get; private set; }
	public PlacementGhost Ghost { get; private set; }

	private Label _hud;
	private string _shotPath;
	private int _shotFrame = 90;
	private int _frames;
	private bool _selfTest;
	private Vector3 _walkStart;
	private int _walkDelay;
	private bool _walkSettling;
	private int _demoStep;
	private float _digStartY;
	// The dig-down gate: ReportDigDown passes only when the player sinks more than this.
	private const float DigDepthTarget = 40f;
	// The chosen column must be thicker than the gate. The player sinks one block per broken
	// block, so a run exactly as deep as the gate leaves no floor to settle on and no next
	// block inside Reach; the margin keeps the descent fed. Tree canopies are excluded by
	// this run length alone (a canopy layer is 1-3 blocks thick), never by block type.
	private const int DigMargin = 8;
	private int _digColumnX, _digColumnZ, _digRun;
	private bool _digPhase, _digReady, _digQueued;
	private int _digShaftX, _digShaftZ;
	private int _digStartLayer;
	private int _digLayers;
	private int _digBreaks;
	// Ratchet state: the lowest y the body may descend to (the verified frontier), the frame the
	// current step started spending its budget, and the step counter for the log.
	private float _digClearedToY;
	private int _digStepFrame, _digStep;
	// How many layers this run digs, and the layer the gate counts cleared layers from. Both are
	// DERIVED in the readiness step: the dig top is the HIGHEST surface cell in the cross-section
	// (starting lower leaves a top layer inside the shaft) while the count comes from the cell the
	// body's feet rest on, so the floor lands exactly DigLayersTarget + 1 below the body's start face.
	private int _digLayersToDig = DigLayersDug;
	private int _digLayerBase;
	// The shaft floor's top face: exactly DigLayersDug layers below the cell the body starts on.
	private float DigFloorY => _digLayerBase - DigLayersDug + 1f;
	private float _digLowestY;
	private int _digFallFrame, _digClearedFrame, _digFallStopFrame;
	// Dig window 252 -> 2400 (2148 frames, ~14.8 s at the measured ~145 process fps); report at
	// 2450. Two lower bounds, and they are NOT added serially -- the descent overlaps the digging:
	//   break bound   = 1476 breaks for 41 layers; measured, the whole batch applies in ONE frame,
	//                   so it pays one chunk rebuild rather than 1476 incremental applies. The shaft
	//                   is 6x6 = 36 cells per layer (4x4 wedges the body; 5 wide left only 0.5 of
	//                   margin and the measured drift pressed the capsule edge into the wall). 6
	//                   wide puts the aligned axis -- the dug interval's GEOMETRIC centre, cx + 1,
	//                   not the cell centre cx + 0.5 -- a derived 3.0 - 2.0 = 1.0 from each wall.
	//                   41 layers = 1476 breaks.
	//   descent bound = 40 layers x frames per block. A 1-block free fall at g = 104 is 0.14 s
	//                   ~= 20 process frames at 145 fps (40 blocks continuously ~127 frames); it
	//                   overlaps the digging, and the one chunk rebuild (~250 frames) overlaps too.
	// The queue/fall frame evidence lines name how the budget is spent. If a measured run still
	// cannot reach DigLayersTarget inside this window, fall back to "the layers the window can
	// dig" and say so in the log -- never a silent downgrade.
	// The landing poll and the post-batch collision gate share this bound: the 1476-cell edit
	// re-meshes the chunk (64 sections, budgeted 3 ms/frame) and the collision pass follows;
	// 240 frames is ~4x the 64-frame one-section-per-frame lower bound and still leaves the
	// fall room inside the 2148-frame window. If it times out, the measured rebuild time is
	// the number to raise.
	// These are demo-clock frames (`_demoStep = _frames - _walkDelay`), so the walk settle cannot eat the window.
	private const int DigReadyFrames = 240;
	// The walk gate's landing poll: a bounded wait for IsOnFloor after the walk stops. Slow frames
	// accumulate more physics ticks, so a fixed-frame sample can catch the player mid-fall and fail
	// a run whose content is identical (measured: CI red, local green on the same commit).
	private const int WalkSettleFrames = 240;
	private const int DigLayersTarget = 40;   // the gate's physical meaning; one-batch queue reaches it
	// The shaft's cross-section in cells relative to (_digShaftX,_digShaftZ): 6 wide, so the dug
	// interval is [cx + ShaftMin, cx + ShaftMax + 1) and its GEOMETRIC centre is cx + 1. The cell
	// centre (cx + 0.5) is 0.5 off that, which left an asymmetric 0.5/1.5 wall margin; the measured
	// -0.501 lateral drift then pressed the capsule into the nearer wall.
	private const int ShaftMin = -2;
	private const int ShaftMax = 3;
	private const float CapsuleRadius = 2.0f;   // Player.cs CapsuleShape3D radius
	private const float ShaftCentre = (ShaftMin + ShaftMax + 1) / 2f;    // 1.0
	// Derived, never a literal: half the dug width minus the capsule radius = 3.0 - 2.0 = 1.0.
	private const float WallMargin = (ShaftMax + 1 - ShaftMin) / 2f - CapsuleRadius;
	// The shaft is dug ONE layer deeper than the gate needs. The body rests the same 0.04 above
	// whatever face holds it, so its descent equals the number of dug layers below it exactly:
	// start feet -9.96 on the surface face at -10.0, and after 40 layers the landing feet -49.96 on
	// a floor face at -50.0 give depth == 40.0 -- while depthOk is the strict `depth > 40f`. Digging
	// DigLayersTarget + 1 keeps that assertion in its strong form and pays one extra layer
	// (41 x 36 = 1476 breaks, +2.4%) instead of relaxing the threshold.
	private const int DigLayersDug = DigLayersTarget + 1;   // 41
	// The dug cross-section, in columns: ShaftMin..ShaftMax in both x and z, derived from the same
	// bounds the alignment and the margin assertion use so a width change cannot leave it stale.
	private const int ShaftColumns = (ShaftMax - ShaftMin + 1) * (ShaftMax - ShaftMin + 1);   // 36
	// The ratchet's step, in blocks: ONE SECTION. Derived, not chosen -- the collision pass decides
	// one section per work item and rebuilds it only when the player's section is within
	// VoxelWorld.CollisionRadius sections of it (2 sections = 32 blocks of reach). One section per
	// step therefore stays inside that reach by construction, so the frontier ahead of the body can
	// always be rebuilt while the body waits on it; a longer step lets the body outrun the pass
	// (measured: releasing the whole 41-block shaft at once stalled 2 runs of 4 on collision that
	// was still the pre-dig version).
	private const int DigStepSpan = VoxelWorld.SectionSize;   // 16
	// A stale span names its first few columns in the FAIL line; the cap keeps the line readable.
	private const int DigStaleColumnsNamed = 4;
	private Vector3I? _placedCell;
	private int _interactVersion;

	public override void _Ready()
	{
		_selfTest = HasArg("--selftest");
		_shotPath = ArgValue("--shot=");
		if (int.TryParse(ArgValue("--shot-frame="), out int shotFrame)) _shotFrame = shotFrame;
		TouchControls.Active = DisplayServer.IsTouchscreenAvailable() || HasArg("--touch") || HasArg("--touchtest");
		if (TouchControls.Active)
		{
			Input.EmulateMouseFromTouch = false;
			Input.EmulateTouchFromMouse = HasArg("--touch");
		}
		VoxelWorld.UseTiles(TexPack.Load(ArgValue("--pack=")));

		World = new VoxelWorld { Name = "World", Focus = new Vector3(0.5f, 0, 0.5f) };
		if (HasArg("--fine")) World.Rules.FineMode = true;
		// The demo queues single shaft cells and counts breaks as one per cell (~layers*36),
		// so pin the mode it was written for instead of changing its accounting.
		if (HasArg("--demo")) World.Rules.FineMode = true;
		if (int.TryParse(ArgValue("--view="), out int view)) World.ViewDistance = view;
		if (int.TryParse(ArgValue("--collision="), out int collision)) World.CollisionRadius = collision;
		if (int.TryParse(ArgValue("--budget="), out int budget)) World.ChunkWorkBudgetMs = budget;
		AddChild(World);

		if (HasArg("--freecam"))
		{
			var cam = new Camera3D { Name = "FreeCam", Current = true, Position = new Vector3(0, 168, 184), Fov = 70f };
			AddChild(cam);
			cam.LookAt(new Vector3(0, 8, 0), Vector3.Up);
		}
		else if (HasArg("--map"))
		{
			// Orthographic top-down: an unambiguous look at the loaded world.
			var cam = new Camera3D
			{
				Name = "MapCam",
				Current = true,
				Position = new Vector3(0, 800, 0.04f),
				Projection = Camera3D.ProjectionType.Orthogonal,
				Size = 800f,
				Far = 1600f,
			};
			AddChild(cam);
			cam.LookAt(Vector3.Zero, Vector3.Up);
		}
		else
		{
			var spawn = World.FindSpawn();
			World.EnsureAreaAround(spawn, 0); // only the landing chunk is forced; the rest streams
			Player = new Player { Name = "Player", World = World, Position = spawn };
			AddChild(Player);
			Ghost = new PlacementGhost { Name = "PlacementGhost" };
			AddChild(Ghost);
			World.Focus = spawn;
			_walkStart = spawn;
			if (HasArg("--diag")) DiagSpawn(spawn);
		}

		if (TouchControls.Active && Player != null)
		{
			var touch = new TouchControls { Name = "Touch", Player = Player };
			AddChild(touch);
			if (HasArg("--touchtest")) AddChild(new TouchTest(World, Player, touch));
		}

		if (HasArg("--volumetest") && Player != null) AddChild(new VolumeTest(World));

		if (!_selfTest)
		{
			AddWorldEnvironment();
			AddHud();
		}

		if (HasArg("--bench") && Player != null) AddChild(new Bench(World, Player));

		if (_selfTest)
		{
			int failures = SelfTest.Run(World);
			GD.Print(failures == 0 ? "SELFTEST PASS" : $"SELFTEST FAIL ({failures})");
			GetTree().Quit(failures == 0 ? 0 : 1);
		}
	}

	private void DiagSpawn(Vector3 spawn)
	{
		int sy = World.SurfaceY(0, 0);
		var range = World.Terrain.SurfaceRange(0, 0);
		GD.Print($"diag spawn={spawn} surfaceY({sy}) columnRange={range}");
		GD.Print($"diag blocks y-1={World.GetBlock(0, -1, 0)} y0={World.GetBlock(0, 0, 0)} "
			+ $"y1={World.GetBlock(0, 1, 0)} y2={World.GetBlock(0, 2, 0)}");
		if (!World.TryGetChunk(0, 0, 0, out var e)) { GD.Print("diag chunk (0,0,0) MISSING"); return; }
		var c = e.GetComponent<ChunkCoord>();
		var v = e.GetComponent<ChunkVisual>();
		int meshed = 0, shaped = 0;
		if (v.Meshes != null) foreach (var m in v.Meshes) if (m?.Mesh != null) meshed++;
		if (v.Shapes != null) foreach (var s in v.Shapes) if (s?.Shape != null) shaped++;
		GD.Print($"diag chunk{c} meshes={meshed}/{ChunkVisual.SectionCount} shapes={shaped} "
			+ $"cursor={v.Cursor} tags=[{e.Tags}]");
	}

	public override void _Process(double delta)
	{
		if (Player != null && !Player.Self.IsNull)
		{
			ulong p0 = Time.GetTicksUsec();
			PlayerSystems.PollInput(World.Store);
			Prof.Poll += Prof.Since(p0);
			ulong p1 = Time.GetTicksUsec();
			PlayerSystems.Look(World.Store);
			Prof.Look += Prof.Since(p1);
			ulong p2 = Time.GetTicksUsec();
			PlayerSystems.UpdateGhost(World, Player.Self, Ghost);
			PlayerSystems.Mine(World.Store, World, (float)delta);
			PlayerSystems.Build(World.Store, World);
			Prof.Edit += Prof.Since(p2);
			World.Focus = Player.GlobalPosition;
			if (_hud != null && _interactVersion != BlockInteractions.Version)
			{
				_interactVersion = BlockInteractions.Version;
				RefreshHud();
			}
		}

		if (HasArg("--frozen")) return; // measure the engine floor: no game code at all
		if (_selfTest || HasArg("--bench")) return;
		_frames++;

		if (Player != null && HasArg("--demo"))
		{
			ref var intent = ref Player.Self.GetComponent<PlayerIntent>();
			ref var state = ref Player.Self.GetComponent<PlayerState>();
			_demoStep = _frames - _walkDelay; // the demo clock pauses while the walk settles
			switch (_demoStep)
			{
				case 60:
					_walkStart = Player.GlobalPosition;
					intent.AutoWalk = true;
					break;

				case 240 when !_walkSettling:
					intent.AutoWalk = false;
					state.Pitch = Mathf.DegToRad(-42f);
					_walkSettling = true; // the report moves to the landing poll below
					break;

				case 246:
					// Creative so the scripted run is not gated on mining time.
					state.Creative = true;
					state.Selected = Block.Chest;
					// Standing right against a step means the obvious target is inside the
					// player's own box, which is correctly refused. Sweep for a legal one.
					for (int step = 0; step < 8 && _placedCell == null; step++)
					{
						state.Yaw = step * Mathf.Pi * 0.25f;
						state.Pitch = Mathf.DegToRad(-30f);
						PlayerSystems.UpdatePending(ref state, ref intent, null);
						_placedCell = PlayerSystems.RequestPlaceAtCrosshair(World, Player.Self);
					}
					break;

				case 248:
					bool interactOk = _placedCell.HasValue
						&& BlockInteractions.Interact(World, _placedCell.Value, Player.Self);
					bool toggleOpen = _placedCell.HasValue
						&& World.BlockEntities.TryGet(_placedCell.Value, out var blockEntity)
						&& blockEntity.GetComponent<ToggleState>().Open;
					bool stillChest = _placedCell.HasValue
						&& World.GetBlock(_placedCell.Value.X, _placedCell.Value.Y, _placedCell.Value.Z) == Block.Chest;
					GD.Print(interactOk && stillChest && toggleOpen ? "interact: PASS (Chest: open)" : "interact: FAIL");
					RefreshHud();
					break;

				case 252:
					ReportBuild();
					// The mining phase must aim at a column that can really be dug DigDepthTarget+
					// blocks, or this CI gate passes on spawn-point terrain luck. A one-block
					// platform is not enough: mining it drops the player and the ray loses the
					// next block. Pick the footprint column with the longest contiguous solid run
					// (which excludes tree canopies by length, not by block type) and relocate onto
					// it through the normal landing path.
					{
						int px = Mathf.FloorToInt(Player.GlobalPosition.X);
						int py = Mathf.FloorToInt(Player.GlobalPosition.Y);
						int pz = Mathf.FloorToInt(Player.GlobalPosition.Z);
						int bestRun = 0, bestX = px, bestZ = pz, bestTop = int.MinValue;
						string scan = "";
						for (int dz = -2; dz <= 2; dz++)
						{
							for (int dx = -2; dx <= 2; dx++)
							{
								int cx = px + dx, cz = pz + dz;
								var (top, run, loaded) = ColumnStack(cx, cz, py + 40);
								bool nearer = Mathf.Abs(bestX - px) + Mathf.Abs(bestZ - pz) > Mathf.Abs(dx) + Mathf.Abs(dz);
								if (run > bestRun || (run == bestRun && nearer))
								{
									bestRun = run;
									bestX = cx;
									bestZ = cz;
									bestTop = top;
								}
								scan += loaded ? $" ({cx},{cz})top={top}run={run};" : $" ({cx},{cz})unloaded;";
							}
						}
						_digColumnX = bestX;
						_digColumnZ = bestZ;
						_digRun = bestRun;
						GD.Print($"digdown: column=({bestX},{bestZ}) run={bestRun} (need {DigDepthTarget + DigMargin})");
						if (bestRun < DigDepthTarget + DigMargin)
						{
							GD.Print($"DIGDOWN FAIL: no loaded footprint column has {(int)DigDepthTarget + DigMargin} contiguous solid blocks; columns:{scan}");
							GetTree().Quit(1);
							return;
						}
						// The -2 compensates TeleportToSurface's own +2 (the 4-voxel body is centred on its
						// group), so the landing centre is the chosen column instead of two columns past it.
						// It does not change the landing path's semantics.
						Player.GlobalPosition = new Vector3(bestX - 2 + 0.5f, Player.GlobalPosition.Y, bestZ - 2 + 0.5f); // only x/z
						// The existing landing path: SurfaceY + EnsureAreaAround for the landing chunk +
						// landing-section mesh/collision under budget + velocity zero. Never poke Y by
						// hand. It lands the body's centre at input+2 (the 4-voxel group centre).
						PlayerSystems.TeleportToSurface(Player.Self, World);
						GD.Print($"digdown: relocated ({px},{pz})->({bestX},{bestZ})");
						// A shaft the 4x4x8 body can fall through must leave real margin: 4 wide puts the
						// walls exactly at r = 2.0 = the capsule radius and wedges the body; 5 wide left
						// only 0.5 and the measured 0.5 drift pressed the edge into the wall at
						// clearance=0.00. The shaft is therefore 6x6 = 36 cells per layer, 1440 breaks
						// for 40 layers; the aligned axis sits 2.5/3.5 from the walls (margin 0.5).
						_digShaftX = bestX;
						_digShaftZ = bestZ;
						_digStartLayer = bestTop;
						_digLayers = 0;
						_digReady = false;
						_digPhase = true;
						// Aim straight down the shaft centre so the readiness poll resolves a cell
						// inside the footprint; the dig loop re-aims once per footprint cell.
						var eye = Player.GlobalPosition + new Vector3(0, PlayerSystems.EyeHeight, 0);
						var dir = (new Vector3(bestX + 0.5f, bestTop + 0.5f, bestZ + 0.5f) - eye).Normalized();
						state.Yaw = Mathf.Atan2(-dir.X, -dir.Z);
						state.Pitch = Mathf.Asin(dir.Y);
					}
					_digStartY = Player.GlobalPosition.Y;
					intent.AutoMine = true;
					break;

				case 2400:
					_digPhase = false;
					intent.AutoMine = false; // stop digging, then let the player settle at the bottom
					break;

				case 2450:
					if (!ReportDigDown()) return; // DigFail already printed the probe and quit(1)
					GetTree().Quit(0);
					return;
			}

			// walk 的终点是瞬时状态，采样帧与物理节奏耦合 => 门禁会随机器变红. Poll to the landing
			// state (bounded) before asserting; a timeout reports with the diagnostics.
			if (_walkSettling)
			{
				if (Player.IsOnFloor()) { _walkSettling = false; ReportWalk(_walkDelay); }
				else if (_walkDelay >= WalkSettleFrames) { _walkSettling = false; ReportWalk(_walkDelay); }
				else _walkDelay++;
			}

			if (_digPhase && _demoStep > 252) DigStep();
		}

		// 90 frames let streaming/meshing settle; --shot-frame=N captures later states (e.g. the demo interaction HUD line).
		if (_shotPath == null || _frames < _shotFrame) return;

		var image = GetViewport().GetTexture().GetImage();
		var error = image.SavePng(_shotPath);
		if (Player != null)
		{
			GD.Print($"player at {Player.GlobalPosition} onFloor={Player.IsOnFloor()} "
				+ $"chunks={World.LoadedChunks} fps={Engine.GetFramesPerSecond()}");
		}
		GD.Print(error == Error.Ok ? $"screenshot saved to {_shotPath}" : $"screenshot failed: {error}");
		GetTree().Quit(error == Error.Ok ? 0 : 1);
	}

	public override void _PhysicsProcess(double delta)
	{
		if (HasArg("--frozen") || Player == null || Player.Self.IsNull) return;
		ulong p0 = Time.GetTicksUsec();
		PlayerSystems.Move(World.Store, World, (float)delta);
		Prof.Move += Prof.Since(p0);
	}

	private void ReportWalk(int waitedFrames)
	{
		var moved = Player.GlobalPosition - _walkStart;
		float distance = new Vector2(moved.X, moved.Z).Length();
		float drop = _walkStart.Y - Player.GlobalPosition.Y;
		// waited= is permanent (not debug): the CI log shows whether the slow-runner landing poll
		// was actually exercised (waited>0) and that the run still passed after the wait.
		GD.Print($"walk: {_walkStart} -> {Player.GlobalPosition} ({distance:F1} blocks, drop {drop:F1}, "
			+ $"onFloor={Player.IsOnFloor()}, waited={waitedFrames}f, chunks={World.LoadedChunks})");
		// A player cannot walk up a one-block step, so "did not travel" is only a failure if
		// nothing is actually blocking them.
		bool blocked = PlayerSystems.BlockedAhead(World, Player.Self, 5.6f);
		bool ok = Player.IsOnFloor() && drop < 80f && (distance > 16f || blocked);
		GD.Print(ok ? $"walk: PASS{(blocked && distance <= 16f ? " (blocked by terrain)" : "")}" : "walk: FAIL");
	}

	private bool ReportDigDown()
	{
		float depth = _digStartY - Player.GlobalPosition.Y;
		bool standing = Player.IsOnFloor();
		bool aboveBedrock = Player.GlobalPosition.Y > World.BedrockY;
		bool sank = depth >= _digLayers - 1;
		bool dug = _digLayers >= DigLayersTarget;
		bool pass = standing && aboveBedrock && depth > DigDepthTarget && sank && dug;
		GD.Print($"digdown: sank {depth:F1} blocks to y={Player.GlobalPosition.Y:F1}, "
			+ $"onFloor={standing}, aboveBedrock={aboveBedrock}, chunks={World.LoadedChunks}");
		GD.Print($"digdown: excavated layers={_digLayers} (need {DigLayersTarget}), breaks={_digBreaks} "
			+ $"(expect ~{_digLayers * 36}), frames={_demoStep - 252}, depth={depth:F1} (need >= layers-1), "
			+ $"descent ended at frame={_digFallStopFrame} (fall started at {_digFallFrame})");
		GD.Print(pass
			? $"DIGDOWN PASS column=({_digColumnX},{_digColumnZ}) run={_digRun} layers={_digLayers} sank={depth:F1}"
			: $"DIGDOWN FAIL column=({_digColumnX},{_digColumnZ}) run={_digRun} layers={_digLayers} sank={depth:F1} "
				+ $"standing={standing} aboveBedrock={aboveBedrock} depthOk={depth > DigDepthTarget} sankOk={sank} dugOk={dug}");
		if (!pass)
			DigFail($"report: layers={_digLayers} sank={depth:F1} standing={standing} aboveBedrock={aboveBedrock} "
				+ $"depthOk={depth > DigDepthTarget} sankOk={sank} dugOk={dug}");
		return pass;
	}

	/// <summary>One frame of the scripted 4x4 shaft dig. First a bounded, frame-counted
	/// readiness poll (feet chunk loaded, its section collision decided, the crosshair
	/// resolving a cell inside the shaft footprint); then the layer under the feet is cleared
	/// through the same scripted break entry point the demo already uses. Frame counting only:
	/// a wall clock would make the gate non-deterministic.</summary>
	private void DigStep()
	{
		ref var state = ref Player.Self.GetComponent<PlayerState>();
		var feet = Player.GlobalPosition;
		int fx = Mathf.FloorToInt(feet.X), fy = Mathf.FloorToInt(feet.Y), fz = Mathf.FloorToInt(feet.Z);

		if (!_digReady)
		{
			bool loaded = World.TryGetChunk(fx, fy, fz, out var ce);
			bool sectionReady = false;
			if (loaded)
			{
				var cc = ce.GetComponent<ChunkCoord>();
				var cv = ce.GetComponent<ChunkVisual>();
				int lx = fx - cc.X * VoxelWorld.ChunkSize, ly = fy - cc.Y * VoxelWorld.ChunkSize, lz = fz - cc.Z * VoxelWorld.ChunkSize;
				var local = new Vector3I(VoxelWorld.FloorDiv(lx, VoxelWorld.SectionSize),
					VoxelWorld.FloorDiv(ly, VoxelWorld.SectionSize), VoxelWorld.FloorDiv(lz, VoxelWorld.SectionSize));
				int si = (local.Y * VoxelWorld.SectionsPerAxis + local.Z) * VoxelWorld.SectionsPerAxis + local.X;
				sectionReady = ((cv.CollisionDone >> si) & 1UL) != 0 && cv.Shapes?[si]?.Shape != null;
			}
			bool aiming = PlayerSystems.CrosshairBlock(World, Player, state.Yaw, state.Pitch, out var cell)
				&& InShaftFootprint(cell);
			if (loaded && sectionReady && aiming)
			{
				_digReady = true;
				_digColumnX = cell.X;
				_digColumnZ = cell.Z;
				// The shaft is centred on the measured feet, never on the selected column: the
				// landing drifted (feet 0.607 vs bestX 1) and a bestX-centred 5x5 left the body's
				// real footprint on an undug rim (measured: solidCells=1/25, wallClearance=-0.39).
				_digShaftX = fx;
				_digShaftZ = fz;
				// The surface steps, so the cross-section must be measured, not assumed:
				//  - The dig TOP is the HIGHEST surface cell in the cross-section. Starting at one
				//    column's top leaves a neighbouring column's top layer inside the shaft, and that
				//    leftover layer is a real floor the body lands on (measured, 5 runs of 5: the body
				//    settled 0.278 above the leftover's face, on one of its corners -- contact normal
				//    (0, 0.86, -0.51) -- which the physics reads as onFloor, so gravity stops and the
				//    descent freezes for the rest of the window).
				//  - The layer COUNT is derived from the cell the body's feet rest on, so the floor
				//    lands exactly DigLayersTarget + 1 below the face the body starts from:
				//      startFace = bodyTop + 1                 bodyTop = the centred column's top
				//                                              solid cell, i.e. the cell under the feet
				//      floorFace = surfaceTop - layers + 1
				//      drop      = startFace - floorFace = layers - (surfaceTop - bodyTop)
				//    so layers = DigLayersDug + (surfaceTop - bodyTop). bodyTop is a CELL index while
				//    floor(_digStartY) is the FACE value (they differ by one): using the feet's floor
				//    as the cell would leave one layer short, drop = 40.0, and trip the strict
				//    `depth > DigDepthTarget` -- the self-check below prints the real number.
				int surfaceTop = _digStartLayer;
				int bodyTop = _digStartLayer;
				for (int dz = ShaftMin; dz <= ShaftMax; dz++)
				{
					for (int dx = ShaftMin; dx <= ShaftMax; dx++)
					{
						var (top, _, chunkLoaded) = ColumnStack(_digShaftX + dx, _digShaftZ + dz, _digStartLayer + 40);
						if (!chunkLoaded) continue;
						if (top > surfaceTop) surfaceTop = top;
					}
				}
				_digStartLayer = surfaceTop;
				_digLayerBase = bodyTop;
				_digLayersToDig = DigLayersDug + (surfaceTop - bodyTop);
				float startFace = bodyTop + 1f;
				float expectedDepth = startFace - DigFloorY;
				if (_digLayersToDig < DigLayersDug || expectedDepth < DigLayersTarget + 1)
				{
					DigFail($"derived shaft is too shallow: surfaceTop={surfaceTop} bodyTop={bodyTop} "
						+ $"layers={_digLayersToDig} startFace={startFace} floorFace={DigFloorY} "
						+ $"expectedDepth={expectedDepth:F1} (need >= {DigLayersTarget + 1})");
					return;
				}
				GD.Print($"digdown: cross-section top={surfaceTop} bodyTop={bodyTop} feet={_digStartY:F2} "
					+ $"layers={_digLayersToDig} startFace={startFace} floorFace={DigFloorY} "
					+ $"expectedDepth={expectedDepth:F1}");
				// Align to the shaft's GEOMETRIC centre, not the cell centre: the dug cells span
				// cx + ShaftMin .. cx + ShaftMax, i.e. the interval [cx - 2, cx + 4), whose centre is
				// cx + 1 while the cell centre is cx + 0.5. The cell-centre alignment left only 0.5 of
				// margin on the near wall and 1.5 on the far one; the measured -0.501 drift then
				// pressed the capsule 0.001 into the near wall face (the failing run reported 6
				// identical slide contacts at x = -2, normal (1,0,0), at the capsule's widest point)
				// and the body never fell again. ShaftCentre keeps this derived from the shaft width.
				int cx = fx, cz = fz;
				Player.GlobalPosition = new Vector3(cx + ShaftCentre, Player.GlobalPosition.Y, cz + ShaftCentre);
				// Drop any pre-alignment horizontal drift; gravity still pulls the body down.
				Player.Velocity = Vector3.Zero;
				feet = Player.GlobalPosition;
				GD.Print($"digdown: landing probe cell={cell} block={World.GetBlock(cell.X, cell.Y, cell.Z)} "
					+ $"column=({cell.X},{cell.Z}) shaft=({_digShaftX - 2}..{_digShaftX + 3},{_digShaftZ - 2}..{_digShaftZ + 3})");
			}
			else
			{
				if (_demoStep - 252 > DigReadyFrames)
					DigFail($"readiness poll timed out after {DigReadyFrames} frames "
						+ $"(loaded={loaded} sectionReady={sectionReady} aiming={aiming})");
				return;
			}

			// Real margin check, not a guess: the body's footprint cells must be inside the shaft
			// and the body surface must clear the wall by the derived margin. `clearance` below is
			// the physical margin (body surface to wall face), the minimum over BOTH walls of each
			// axis: the capsule radius cancels in both terms, so it is NOT a centre-to-centre
			// offset. With the axis on the geometric centre that margin is half the dug width minus
			// the radius (3.0 - 2.0 = 1.0), so anything below WallMargin is a misalignment and must
			// not silently continue.
			int footMinX = Mathf.FloorToInt(feet.X - 2f), footMaxX = Mathf.CeilToInt(feet.X + 2f) - 1;
			int footMinZ = Mathf.FloorToInt(feet.Z - 2f), footMaxZ = Mathf.CeilToInt(feet.Z + 2f) - 1;
			bool covered = footMinX >= _digShaftX + ShaftMin && footMaxX <= _digShaftX + ShaftMax
				&& footMinZ >= _digShaftZ + ShaftMin && footMaxZ <= _digShaftZ + ShaftMax;
			float clearX = Mathf.Min(feet.X - CapsuleRadius - (_digShaftX + ShaftMin),
				(_digShaftX + ShaftMax + 1) - (feet.X + CapsuleRadius));
			float clearZ = Mathf.Min(feet.Z - CapsuleRadius - (_digShaftZ + ShaftMin),
				(_digShaftZ + ShaftMax + 1) - (feet.Z + CapsuleRadius));
			float clearance = Mathf.Min(clearX, clearZ);
			if (!covered || clearance < WallMargin)
			{
				DigFail($"shaft does not cover the body footprint "
					+ $"(footprint=({footMinX}..{footMaxX},{footMinZ}..{footMaxZ}) "
					+ $"shaft=({_digShaftX - 2}..{_digShaftX + 3},{_digShaftZ - 2}..{_digShaftZ + 3}) clearance={clearance:F2} "
					+ $"need>={WallMargin:F2})");
				return;
			}
			GD.Print($"digdown: aligned to shaft centre ({_digShaftX + ShaftCentre:F1},{_digShaftZ + ShaftCentre:F1}) "
				+ $"clearance={clearance:F2} (derived {WallMargin:F2})");

		}

		// One edit batch for the whole shaft: per-layer batches re-dirtied the 64-section chunk
		// 40 times and the rebuild dominated (measured ~250 frames per layer). The shaft is 6x6
		// = 36 cells per layer, 41 layers = 1476 breaks queued once; the rebuild is paid once and
		// the body falls the pre-dug shaft. RequestEdit is the same edit pipeline mining uses, so
		// the world still decides what a break does; only the aim/raycast layer is skipped.
		if (!_digQueued)
		{
			_digQueued = true;
			// Start the ratchet at the body's standing height: it may not descend until the columns
			// below it are verified clear of collision.
			_digClearedToY = _digStartY;
			_digStepFrame = _frames;
			int cells = 0;
			for (int layer = 0; layer < _digLayersToDig; layer++)
			{
				int y = _digStartLayer - layer;
				for (int dz = -2; dz <= 3; dz++)
				{
					for (int dx = -2; dx <= 3; dx++)
					{
						var cell = new Vector3I(_digShaftX + dx, y, _digShaftZ + dz);
						if (!Blocks.IsBreakable(World.GetBlock(cell.X, cell.Y, cell.Z))) continue;
						World.RequestEdit(EditRequest.Break(cell, Player.Self));
						cells++;
					}
				}
			}
			GD.Print($"digdown: queued {cells} shaft cells at frame={_frames}, {_digLayersToDig} layers at "
				+ $"({_digShaftX - 2}..{_digShaftX + 3},{_digShaftZ - 2}..{_digShaftZ + 3}) from y={_digStartLayer}");
		}
		UpdateDigLayers();
		// Ratcheted release. The batch is queued, but the work it triggers is BUDGETED: the blocks
		// vanish in one frame while the chunk is re-meshed and re-collided at 3 ms/frame, and the
		// collision pass only rebuilds the sections near the player. Releasing the body for the whole
		// shaft races that rebuild: measured, 2 runs of 4 fell onto collision that was still the
		// PRE-dig version -- a stale face INSIDE the dug span, at x = -2 in one run and x = +2 in
		// the other with opposite normals -- which shoved the capsule onto the far wall where the
		// kinematic solver deadlocked (velocity accumulated to -485, position frozen). A single ray
		// down the centre line cannot see such a face (it sits beside the ray), so the release is
		// instead ratcheted: the body falls to the frontier, is held there (Y only; x/z are left
		// alone, and the footprint/margin assertion above already ran), and the frontier advances by
		// ONE SECTION only when every dug column between it and the next frontier is verified free
		// of collision by the same invariant the selftest's support-collision assertion enforces
		// (collision geometry must agree with the block data at the same coordinates). Waiting on a
		// frontier keeps the player's section inside the radius the pass rebuilds, so each step's
		// wait is bounded; a step that cannot clear in DigReadyFrames fails loudly with the stale
		// columns named -- holding forever is never a silent pass.
		if (_digClearedToY > DigFloorY)
		{
			float frontier = _digClearedToY;
			float next = Mathf.Max(frontier - DigStepSpan, DigFloorY);
			if (Player.GlobalPosition.Y < frontier)
			{
				Player.GlobalPosition = new Vector3(Player.GlobalPosition.X, frontier, Player.GlobalPosition.Z);
				Player.Velocity = Vector3.Zero;
			}
			// When the next frontier IS the floor face, stop the probe half a block above it so a
			// correct solid floor is not read as stale collision; mid-shaft the frontier's y is
			// inside the dug volume and the probe must reach it exactly.
			float probeTo = next > DigFloorY ? next : DigFloorY + 0.5f;
			if (ShaftSpanClear(frontier, probeTo, out string stale))
			{
				_digClearedToY = next;
				_digStepFrame = _frames;
				_digStep++;
				ShaftCollisionReady(out int readySections, out int totalSections);
				GD.Print($"digdown: frontier -> y={next:F1} at frame={_frames} (step {_digStep}, "
					+ $"columns clear; {readySections}/{totalSections} sections carry a rebuilt shape)");
			}
			else if (_frames - _digStepFrame > DigReadyFrames)
			{
				DigFail($"the collision below y={frontier:F1} is still the pre-dig version after "
					+ $"{DigReadyFrames} frames: {stale}");
				return;
			}
		}
		// The descent is measured from the first frame the body actually moved DOWN, not from the
		// frame the ground under it was dug: the ratchet holds it at a frontier in between.
		if (_digFallFrame == 0 && Player.GlobalPosition.Y < _digStartY - 0.01f)
		{
			_digFallFrame = _frames;
			GD.Print($"digdown: fall started at frame={_frames} vel={Player.Velocity}");
		}
		// Descent trace: the frame the body last changed height. The report prints it, so a FAIL
		// names how the window was spent (dig frames vs fall frames) instead of guessing.
		if (Mathf.Abs(Player.GlobalPosition.Y - _digLowestY) > 1e-4f)
		{
			_digLowestY = Player.GlobalPosition.Y;
			_digFallStopFrame = _frames;
		}
	}

	/// <summary>True when every section the shaft crosses carries the new collision. The shaft
	/// spans ~3 vertical sections and 2 horizontal ones; the collision pass only decides
	/// sections inside the player's gate, so the poll must cover them all. Reports the count for
	/// the timeout diagnostic.</summary>
	private bool ShaftCollisionReady(out int ready, out int total)
	{
		ready = 0;
		total = 0;
		int minX = _digShaftX - 2, maxX = _digShaftX + 3;
		int minZ = _digShaftZ - 2, maxZ = _digShaftZ + 3;
		int minY = _digStartLayer - (_digLayersToDig - 1), maxY = _digStartLayer;
		for (int sy = VoxelWorld.FloorDiv(minY, VoxelWorld.SectionSize); sy <= VoxelWorld.FloorDiv(maxY, VoxelWorld.SectionSize); sy++)
		{
			for (int sz = VoxelWorld.FloorDiv(minZ, VoxelWorld.SectionSize); sz <= VoxelWorld.FloorDiv(maxZ, VoxelWorld.SectionSize); sz++)
			{
				for (int sx = VoxelWorld.FloorDiv(minX, VoxelWorld.SectionSize); sx <= VoxelWorld.FloorDiv(maxX, VoxelWorld.SectionSize); sx++)
				{
					total++;
					if (!World.TryGetChunk(sx * VoxelWorld.SectionSize, sy * VoxelWorld.SectionSize, sz * VoxelWorld.SectionSize, out var ce)) continue;
					var cc = ce.GetComponent<ChunkCoord>();
					var cv = ce.GetComponent<ChunkVisual>();
					int lx = sx - cc.X * VoxelWorld.SectionsPerAxis;
					int ly = sy - cc.Y * VoxelWorld.SectionsPerAxis;
					int lz = sz - cc.Z * VoxelWorld.SectionsPerAxis;
					if (lx < 0 || lx >= VoxelWorld.SectionsPerAxis || ly < 0 || ly >= VoxelWorld.SectionsPerAxis
						|| lz < 0 || lz >= VoxelWorld.SectionsPerAxis) continue;
					int si = (ly * VoxelWorld.SectionsPerAxis + lz) * VoxelWorld.SectionsPerAxis + lx;
					if (((cv.CollisionDone >> si) & 1UL) != 0 && cv.Shapes?[si]?.Shape != null) ready++;
				}
			}
		}
		return ready == total;
	}

	/// <summary>The invariant the selftest's support-collision assertion enforces, applied to every
	/// column of the dug cross-section: collision geometry must agree with the block data at the
	/// same coordinates, so a collision surface inside [fromY, toY] means that column still carries
	/// the pre-dig trimesh. One ray down the centre line is NOT a proof: the dug cross-section is
	/// ShaftColumns columns wide and the body sweeps four of them, and a measured failure pressed
	/// the capsule onto a stale face at x = cx + 2 while a centre-line ray at cx + 1 reported clear.
	/// HitFromInside must be true -- a stale trimesh is solid where the data is air, so the ray
	/// STARTS inside it, and with the default hit_from_inside = false Godot reports that as no hit
	/// at all: the false "clear" that let the body fall into un-rebuilt collision. Returns false and
	/// names the stale columns and the section of each named hit.</summary>
	private bool ShaftSpanClear(float fromY, float toY, out string stale)
	{
		stale = "";
		int bad = 0;
		string named = "";
		for (int dz = ShaftMin; dz <= ShaftMax; dz++)
		{
			for (int dx = ShaftMin; dx <= ShaftMax; dx++)
			{
				var from = new Vector3(_digShaftX + dx + 0.5f, fromY, _digShaftZ + dz + 0.5f);
				var query = PhysicsRayQueryParameters3D.Create(from, new Vector3(from.X, toY, from.Z));
				query.HitFromInside = true;
				query.Exclude = new Godot.Collections.Array<Rid> { Player.GetRid() };
				var hit = Player.GetWorld3D().DirectSpaceState.IntersectRay(query);
				if (hit.Count == 0) continue;
				bad++;
				if (bad > DigStaleColumnsNamed) continue;
				var point = (Vector3)hit["position"];
				int hx = Mathf.FloorToInt(point.X), hy = Mathf.FloorToInt(point.Y), hz = Mathf.FloorToInt(point.Z);
				named += $" ({_digShaftX + dx},{_digShaftZ + dz})hit={point}"
					+ $"section=({VoxelWorld.FloorDiv(hx, VoxelWorld.SectionSize)},"
					+ $"{VoxelWorld.FloorDiv(hy, VoxelWorld.SectionSize)},"
					+ $"{VoxelWorld.FloorDiv(hz, VoxelWorld.SectionSize)})";
			}
		}
		if (bad == 0) return true;
		stale = $"{bad}/{ShaftColumns} columns carry collision where the data is air:{named}";
		return false;
	}

	private bool InShaftFootprint(Vector3I cell)
		=> cell.X >= _digShaftX - 2 && cell.X <= _digShaftX + 3
			&& cell.Z >= _digShaftZ - 2 && cell.Z <= _digShaftZ + 3;

	private bool LayerCleared(int y)
	{
		for (int dz = -2; dz <= 3; dz++)
			for (int dx = -2; dx <= 3; dx++)
				if (Blocks.IsSolid(World.GetBlock(_digShaftX + dx, y, _digShaftZ + dz))) return false;
		return true;
	}

	private void UpdateDigLayers()
	{
		while (_digLayers < 128 && LayerCleared(_digLayerBase - _digLayers)) _digLayers++;
		if (_digClearedFrame == 0 && _digLayers >= DigLayersTarget)
		{
			_digClearedFrame = _frames;
			GD.Print($"digdown: shaft cleared to {_digLayers} layers at frame={_frames}");
		}
		// Actual applied breaks: the fully cleared layers plus the partial layer being dug.
		// breaks ~= 25 x layers is the evidence that no queued cell was wasted on air.
		int partial = 0;
		if (_digLayers < 128)
		{
			int y = _digLayerBase - _digLayers;
			for (int dz = -2; dz <= 3; dz++)
				for (int dx = -2; dx <= 3; dx++)
					if (!Blocks.IsSolid(World.GetBlock(_digShaftX + dx, y, _digShaftZ + dz))) partial++;
		}
		_digBreaks = _digLayers * 36 + partial;
	}

	/// <summary>Permanent self-explanatory FAIL: the previous DIGDOWN hunt needed five rounds of
	/// temporary probes because the line could not name the cause. Prints the raw ray, the body
	/// state, the shaft footprint and the landing section's mesh/collision bits.</summary>
	private void DigFail(string reason)
	{
		ref var state = ref Player.Self.GetComponent<PlayerState>();
		var feet = Player.GlobalPosition;
		int fx = Mathf.FloorToInt(feet.X), fy = Mathf.FloorToInt(feet.Y), fz = Mathf.FloorToInt(feet.Z);
		bool hit = PlayerSystems.ProbeRay(World, Player, state.Yaw, state.Pitch, out var point, out var normal);
		GD.Print($"DIGDOWN FAIL: {reason}");
		// The actual feet on the same line as the shaft's x/z bounds, so the script-vs-engine
		// discrimination can be read off directly.
		GD.Print($"digdown: feet=({feet.X:F3},{feet.Y:F3},{feet.Z:F3}) "
			+ $"shaft=({_digShaftX - 2}..{_digShaftX + 3},{_digShaftZ - 2}..{_digShaftZ + 3}) "
			+ $"hit={(hit ? "yes" : "no")} point={point} normal={normal} vel={Player.Velocity} "
			+ $"pitchDeg={Mathf.RadToDeg(state.Pitch):F1} yawDeg={Mathf.RadToDeg(state.Yaw):F1} layers={_digLayers}");
		// The body's REAL footprint under the feet, from the actual position: if any cell there
		// is still solid the shaft does not cover the body (script), not a stale on_floor flag.
		int layer = fy - 1;
		int solidCells = 0;
		string solids = "";
		for (int dz = -2; dz <= 3; dz++)
		{
			for (int dx = -2; dx <= 3; dx++)
			{
				if (!Blocks.IsSolid(World.GetBlock(fx + dx, layer, fz + dz))) continue;
				solidCells++;
				solids += $" ({fx + dx},{layer},{fz + dz})";
			}
		}
		// Distance from the body's surface (capsule radius 2) to the shaft wall, from the actual
		// feet, not from bestX: the dug cells span [_digShaftX - 2, _digShaftX + 4) in world x.
		float clearX = Mathf.Min(feet.X - 2f - (_digShaftX - 2), (_digShaftX + 4) - (feet.X + 2f));
		float clearZ = Mathf.Min(feet.Z - 2f - (_digShaftZ - 2), (_digShaftZ + 4) - (feet.Z + 2f));
		GD.Print($"digdown: supportLayer={layer} solidCells={solidCells}/36 solids:{solids} "
			+ $"wallClearance={Mathf.Min(clearX, clearZ):F2} (capsule radius 2)");
		// What the physics engine is really touching. This gate fails with onFloor=true while the
		// footprint scan below the body says air, so print the real contact list (position +
		// normal + collider) instead of inferring a surface from the block data.
		int contacts = Player.GetSlideCollisionCount();
		string contactText = "";
		for (int i = 0; i < contacts; i++)
		{
			var c = Player.GetSlideCollision(i);
			contactText += $" #{i} pos={c.GetPosition()} normal={c.GetNormal()} collider={c.GetCollider()?.GetType().Name}";
		}
		GD.Print($"digdown: contacts={contacts}{contactText}");
		if (World.TryGetChunk(fx, fy, fz, out var ce))
		{
			var cc = ce.GetComponent<ChunkCoord>();
			var cv = ce.GetComponent<ChunkVisual>();
			int lx = fx - cc.X * VoxelWorld.ChunkSize, ly = fy - cc.Y * VoxelWorld.ChunkSize, lz = fz - cc.Z * VoxelWorld.ChunkSize;
			var local = new Vector3I(VoxelWorld.FloorDiv(lx, VoxelWorld.SectionSize),
				VoxelWorld.FloorDiv(ly, VoxelWorld.SectionSize), VoxelWorld.FloorDiv(lz, VoxelWorld.SectionSize));
			int si = (local.Y * VoxelWorld.SectionsPerAxis + local.Z) * VoxelWorld.SectionsPerAxis + local.X;
			GD.Print($"digdown: section chunk={cc} si={si} meshDone={(cv.MeshDone >> si) & 1UL} "
				+ $"collisionDone={(cv.CollisionDone >> si) & 1UL} shape={(cv.Shapes?[si]?.Shape?.GetType().Name ?? "none")}");
		}
		else GD.Print($"digdown: section chunk at feet ({fx},{fy},{fz}) MISSING");
		GetTree().Quit(1);
	}

	/// <summary>Top solid cell of a column and the length of its contiguous solid run below,
	/// counting only cells whose chunk is loaded: GetBlock answers an unloaded chunk with a
	/// heightmap approximation, and a run must never be screened on data the world may not own.
	/// Loaded=false means the scan window hit an unloaded chunk before finding the surface.</summary>
	private (int Top, int Run, bool Loaded) ColumnStack(int cx, int cz, int fromY)
	{
		for (int y = fromY; y > fromY - 70; y--)
		{
			if (!World.TryGetChunk(cx, y, cz, out _)) return (int.MinValue, 0, false);
			if (!Blocks.IsSolid(World.GetBlock(cx, y, cz))) continue;
			int run = 0;
			for (int y2 = y; y2 > y - 256; y2--)
			{
				if (!World.TryGetChunk(cx, y2, cz, out _)) break;
				if (!Blocks.IsSolid(World.GetBlock(cx, y2, cz))) break;
				run++;
			}
			return (y, run, true);
		}
		return (int.MinValue, 0, true);
	}

	/// <summary>Verifies an edit request landed. Requests are applied by the world at the
	/// top of the next frame, so this is deliberately checked a few frames later.</summary>
	private void ReportBuild()
	{
		bool ok = _placedCell.HasValue
			&& World.GetBlock(_placedCell.Value.X, _placedCell.Value.Y, _placedCell.Value.Z) == Block.Chest;
		GD.Print($"build: requested {_placedCell?.ToString() ?? "nothing"} -> {(ok ? "PASS" : "FAIL")}");
	}

	public void RefreshHud()
	{
		if (_hud == null || Player == null) return;
		_hud.Text = $"Regress ({VoxelWorld.ChunkSize}^3 chunks, unbounded Y) — WASD move, Space jump, Shift sprint, F fly, LMB break, RMB place\n"
			+ $"1-8 select block: {Blocks.NameOf(Player.Selected)}   R respawn   V mode: {(World.Rules.FineMode ? "1x1x1 (fine)" : "4x4x4")}   Esc release mouse"
			+ (BlockInteractions.Message == null ? "" : $"\nRMB use: {BlockInteractions.Message}");
	}

	private void AddHud()
	{
		var layer = new CanvasLayer { Name = "Hud" };
		_hud = new Label { Position = new Vector2(14, 10) };
		_hud.AddThemeColorOverride("font_color", Colors.White);
		_hud.AddThemeColorOverride("font_outline_color", Colors.Black);
		_hud.AddThemeConstantOverride("outline_size", 5);
		layer.AddChild(_hud);
		AddChild(layer);
		RefreshHud();
	}

	private void AddWorldEnvironment()
	{
		var haze = Srgb(0.72f, 0.80f, 0.88f);
		var skyMaterial = new ProceduralSkyMaterial
		{
			SkyHorizonColor = haze,
			GroundHorizonColor = haze,
			GroundBottomColor = Srgb(0.52f, 0.58f, 0.64f),
		};
		var env = new Godot.Environment
		{
			BackgroundMode = HasArg("--nosky") ? Godot.Environment.BGMode.Color : Godot.Environment.BGMode.Sky,
			BackgroundColor = Colors.Magenta,
			Sky = new Sky { SkyMaterial = skyMaterial },
			AmbientLightSource = Godot.Environment.AmbientSource.Sky,
			// Hides the world edge at the streaming radius. --nofog is for screenshots
			// and for telling "hazy" apart from "actually darker".
			FogEnabled = !HasArg("--nofog"),
			FogLightColor = haze,
			// Fog hides the world edge at the view distance: 0.008 x 80/192 = 0.0033, so the
			// old 125-unit reach (1.56x the old 80-unit view) becomes ~303 units at the new 192.
			FogDensity = 0.0033f,
		};
		AddChild(new WorldEnvironment { Name = "Environment", Environment = env });
	}

	private static Color Srgb(float r, float g, float b) => new Color(r, g, b).SrgbToLinear();

	private static bool HasArg(string flag) => System.Array.IndexOf(OS.GetCmdlineUserArgs(), flag) >= 0;

	private static string ArgValue(string prefix)
	{
		foreach (var arg in OS.GetCmdlineUserArgs())
			if (arg.StartsWith(prefix)) return arg[prefix.Length..];
		return null;
	}
}
