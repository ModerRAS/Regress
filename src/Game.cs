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
		if (int.TryParse(ArgValue("--view="), out int view)) World.ViewDistance = view;
		if (int.TryParse(ArgValue("--collision="), out int collision)) World.CollisionRadius = collision;
		if (int.TryParse(ArgValue("--budget="), out int budget)) World.ChunkWorkBudgetMs = budget;
		AddChild(World);

		if (HasArg("--freecam"))
		{
			var cam = new Camera3D { Name = "FreeCam", Current = true, Position = new Vector3(0, 42, 46), Fov = 70f };
			AddChild(cam);
			cam.LookAt(new Vector3(0, 2, 0), Vector3.Up);
		}
		else if (HasArg("--map"))
		{
			// Orthographic top-down: an unambiguous look at the loaded world.
			var cam = new Camera3D
			{
				Name = "MapCam",
				Current = true,
				Position = new Vector3(0, 200, 0.01f),
				Projection = Camera3D.ProjectionType.Orthogonal,
				Size = 200f,
				Far = 400f,
			};
			AddChild(cam);
			cam.LookAt(Vector3.Zero, Vector3.Up);
		}
		else
		{
			var spawn = World.FindSpawn();
			World.EnsureAreaAround(spawn, 1); // ground must exist before the player drops in
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
		GD.Print($"diag chunk{c} mesh={(v.Mesh?.Mesh != null)} shape={v.Shape?.Shape?.GetType().Name ?? "none"} "
			+ $"bodyPos={v.Body?.Position.ToString() ?? "none"} shapePos={v.Shape?.Position.ToString() ?? "none"} "
			+ $"tags=[{e.Tags}]");
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
					// Straight down: through the surface chunk into buried chunks that have
					// no visible mesh at all, which is where the player used to fall out.
					state.Pitch = Mathf.DegToRad(-88f);
					_digStartY = Player.GlobalPosition.Y;
					intent.AutoMine = true;
					break;

				case 460:
					intent.AutoMine = false; // stop, then let the player settle at the bottom
					break;

				case 500:
					ReportDigDown();
					GetTree().Quit(0);
					return;
			}
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
		bool blocked = PlayerSystems.BlockedAhead(World, Player.Self, 1.4f);
		bool ok = Player.IsOnFloor() && drop < 20f && (distance > 4f || blocked);
		GD.Print(ok ? $"walk: PASS{(blocked && distance <= 4f ? " (blocked by terrain)" : "")}" : "walk: FAIL");
	}

	private void ReportDigDown()
	{
		float depth = _digStartY - Player.GlobalPosition.Y;
		bool standing = Player.IsOnFloor();
		bool aboveBedrock = Player.GlobalPosition.Y > World.BedrockY;
		GD.Print($"digdown: sank {depth:F1} blocks to y={Player.GlobalPosition.Y:F1}, "
			+ $"onFloor={standing}, aboveBedrock={aboveBedrock}, chunks={World.LoadedChunks}");
		GD.Print(standing && aboveBedrock && depth > 10f ? "DIGDOWN PASS" : "DIGDOWN FAIL");
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
		_hud.Text = $"Regress (16^3 chunks, unbounded Y) — WASD move, Space jump, Shift sprint, F fly, LMB break, RMB place\n"
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
			FogDensity = 0.008f,
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
