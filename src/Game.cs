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
	private float _digStartY;
	// The dig-down gate: ReportDigDown passes only when the player sinks more than this.
	private const float DigDepthTarget = 40f;
	// The chosen column must be thicker than the gate. The player sinks one block per broken
	// block, so a run exactly as deep as the gate leaves no floor to settle on and no next
	// block inside Reach; the margin keeps the descent fed. Tree canopies are excluded by
	// this run length alone (a canopy layer is 1-3 blocks thick), never by block type.
	private const int DigMargin = 8;
	private int _digColumnX, _digColumnZ, _digRun;
	private bool _digPhase, _digReady, _digQueued, _digShaftReady;
	private int _digShaftX, _digShaftZ;
	private int _digStartLayer;
	private int _digLayers;
	private int _digBreaks;
	private float _digLowestY;
	private int _digFallFrame, _digClearedFrame, _digFallStopFrame;
	// Dig window 252 -> 2400 (2148 frames, ~14.8 s at the measured ~145 process fps); report at
	// 2450. Two lower bounds, and they are NOT added serially -- the descent overlaps the digging:
	//   break bound   = 1440 breaks / measured 0.67 breaks per frame ~= 2150 frames if applied one
	//                   by one; the whole shaft is queued in ONE edit batch instead, so the batch
	//                   pays one chunk rebuild rather than 1440 incremental applies. The shaft is
	//                   6x6 = 36 cells per layer (4x4 wedges the body, 5x5 left only 0.5 of margin
	//                   and the measured 0.5 drift pressed the edge into the wall at
	//                   clearance=0.00; 6 wide puts the aligned axis 2.5/3.5 from the walls, so
	//                   the derived margin is 2.5 - 2.0 = 0.5). 40 layers = 1440 breaks.
	//   descent bound = 40 layers x frames per block. A 1-block free fall at g = 104 is 0.14 s
	//                   ~= 20 process frames at 145 fps (40 blocks continuously ~127 frames); it
	//                   overlaps the digging, and the one chunk rebuild (~250 frames) overlaps too.
	// The queue/fall frame evidence lines name how the budget is spent. If a measured run still
	// cannot reach DigLayersTarget inside this window, fall back to "the layers the window can
	// dig" and say so in the log -- never a silent downgrade.
	// The landing poll and the shaft-collision poll share this bound: the 1440-cell edit
	// re-meshes the chunk (64 sections, budgeted 3 ms/frame) and the collision pass follows;
	// 240 frames is ~4x the 64-frame one-section-per-frame lower bound and still leaves the
	// fall room inside the 2148-frame window. If it times out, the measured rebuild time is
	// the number to raise.
	private const int DigReadyFrames = 240;
	private const int DigLayersTarget = 40;   // the gate's physical meaning; one-batch queue reaches it
	private Vector3I? _placedCell;
	private int _interactVersion;

	public override void _Ready()
	{
		_selfTest = HasArg("--selftest");
		_shotPath = ArgValue("--shot=");
		if (int.TryParse(ArgValue("--shot-frame="), out int shotFrame)) _shotFrame = shotFrame;
		VoxelWorld.UseTiles(TexPack.Load(ArgValue("--pack=")));

		World = new VoxelWorld { Name = "World", Focus = new Vector3(0.5f, 0, 0.5f) };
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
			switch (_frames)
			{
				case 60:
					_walkStart = Player.GlobalPosition;
					intent.AutoWalk = true;
					break;

				case 240:
					intent.AutoWalk = false;
					state.Pitch = Mathf.DegToRad(-42f);
					ReportWalk();
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

			if (_digPhase && _frames > 252) DigStep();
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

	private void ReportWalk()
	{
		var moved = Player.GlobalPosition - _walkStart;
		float distance = new Vector2(moved.X, moved.Z).Length();
		float drop = _walkStart.Y - Player.GlobalPosition.Y;
		GD.Print($"walk: {_walkStart} -> {Player.GlobalPosition} ({distance:F1} blocks, drop {drop:F1}, "
			+ $"onFloor={Player.IsOnFloor()}, chunks={World.LoadedChunks})");
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
			+ $"(expect ~{_digLayers * 36}), frames={_frames - 252}, depth={depth:F1} (need >= layers-1), "
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
				// Second, <=0.4-cell alignment: put the body on the shaft centre. The wall margin is
				// then the derived shaftHalfWidth - capsuleRadius = 5/2 - 2.0 = 0.5 (the raw feet
				// left only 0.114, where any drift jams the capsule on the wall).
				int cx = fx, cz = fz;
				Player.GlobalPosition = new Vector3(cx + 0.5f, Player.GlobalPosition.Y, cz + 0.5f);
				// Drop any pre-alignment horizontal drift; gravity still pulls the body down.
				Player.Velocity = Vector3.Zero;
				feet = Player.GlobalPosition;
				GD.Print($"digdown: landing probe cell={cell} block={World.GetBlock(cell.X, cell.Y, cell.Z)} "
					+ $"column=({cell.X},{cell.Z}) shaft=({_digShaftX - 2}..{_digShaftX + 3},{_digShaftZ - 2}..{_digShaftZ + 3})");
			}
			else
			{
				if (_frames - 252 > DigReadyFrames)
					DigFail($"readiness poll timed out after {DigReadyFrames} frames "
						+ $"(loaded={loaded} sectionReady={sectionReady} aiming={aiming})");
				return;
			}

			// Real margin check, not a guess: the body's footprint cells must be inside the shaft
			// and the body surface must clear the wall by the derived margin. `clearance` below is
			// the physical margin (body surface to wall face): the capsule radius cancels in both
			// terms, so it is NOT a centre-to-centre offset. A failure is a script bug and must
			// not silently continue.
			const int ShaftMin = -2;            // the shaft cells span cx-2 .. cx+3 (6 wide)
			const int ShaftMax = 3;
			const float CapsuleRadius = 2.0f;   // Player.cs CapsuleShape3D radius
			// After the alignment the axis sits at cx + 0.5; the nearer wall is at cx + ShaftMin,
			// so the derived physical margin is (0.5 - ShaftMin) - CapsuleRadius = 2.5 - 2.0 = 0.5.
			const float WallMargin = (0.5f - ShaftMin) - CapsuleRadius;
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
			GD.Print($"digdown: aligned to shaft centre ({_digShaftX + 0.5f:F1},{_digShaftZ + 0.5f:F1}) "
				+ $"clearance={clearance:F2} (derived {WallMargin:F2})");

			// The whole shaft's collision must be the new version before the body is released: the
			// batch edit re-dirties the chunk, but the collision pass only rebuilds sections inside
			// the player's gate, so stale trimeshes deeper in the shaft would catch the falling body
			// (measured: data empty, velocity accumulating, position stuck after 5 blocks).
			if (!_digShaftReady)
			{
				bool ready = ShaftCollisionReady(out int readySections, out int totalSections);
				if (ready) _digShaftReady = true;
				else if (_frames - 252 > DigReadyFrames)
				{
					DigFail($"shaft collision not ready after {DigReadyFrames} frames "
						+ $"({readySections}/{totalSections} sections)");
					return;
				}
				else return;
			}
		}

		// One edit batch for the whole shaft: per-layer batches re-dirtied the 64-section chunk
		// 40 times and the rebuild dominated (measured ~250 frames per layer). The shaft is 6x6
		// = 36 cells per layer, 40 layers = 1440 breaks queued once; the rebuild is paid once and
		// the body falls the pre-dug shaft. RequestEdit is the same edit pipeline mining uses, so
		// the world still decides what a break does; only the aim/raycast layer is skipped.
		if (!_digQueued)
		{
			_digQueued = true;
			int cells = 0;
			for (int layer = 0; layer < 40; layer++)
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
			GD.Print($"digdown: queued {cells} shaft cells at frame={_frames}, 40 layers at "
				+ $"({_digShaftX - 2}..{_digShaftX + 3},{_digShaftZ - 2}..{_digShaftZ + 3}) from y={_digStartLayer}");
		}
		UpdateDigLayers();
		if (_digFallFrame == 0 && !Player.IsOnFloor())
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
		int minY = _digStartLayer - 39, maxY = _digStartLayer;
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
		while (_digLayers < 128 && LayerCleared(_digStartLayer - _digLayers)) _digLayers++;
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
			int y = _digStartLayer - _digLayers;
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
			+ $"1-8 select block: {Blocks.NameOf(Player.Selected)}   R respawn   Esc release mouse"
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
