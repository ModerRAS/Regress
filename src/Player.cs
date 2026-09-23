using Friflo.Engine.ECS;
using Godot;

namespace Regress;

/// <summary>
/// Godot side of the player: owns the physics body, camera and input callbacks, and turns
/// them into ECS component writes. All behaviour lives in <see cref="PlayerSystems"/>.
/// </summary>
public partial class Player : CharacterBody3D
{
	private const float LookSensitivity = 0.0022f;

	public VoxelWorld World;
	public Entity Self { get; private set; }
	public Camera3D Camera { get; private set; }

	public override void _Ready()
	{
		AddChild(new CollisionShape3D
		{
			Name = "Body",
			Shape = new CapsuleShape3D { Radius = 2.0f, Height = 8.0f },
			Position = new Vector3(0, 4.0f, 0),
		});

		Camera = new Camera3D
		{
			Name = "Camera",
			Fov = 75f,
			Far = 4800f,
			Current = true,
			Position = new Vector3(0, PlayerSystems.EyeHeight, 0),
		};
		AddChild(Camera);

		Self = World.Store.CreateEntity(
			new PlayerBody { Node = this, Camera = Camera },
			new PlayerState
			{
				Flying = false, Creative = false, Selected = Block.Stone, Yaw = 0, Pitch = 0,
				// The placement rule seeds the pending orientation each frame while nothing is
				// remembered; Q/E steer it from here.
				PendingOrientation = BlockBehaviors.PlaceOrientation(Block.Stone, Face.Top, 0f, 0f),
			},
			default(PlayerIntent),
			default(PlayerMining),
			Tags.Get<PlayerTag>());

		SetMouseCaptured(true);
	}

	private void SetMouseCaptured(bool captured)
		=> Input.MouseMode = captured ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;

	public override void _UnhandledInput(InputEvent @event)
	{
		if (Self.IsNull) return;
		ref var state = ref Self.GetComponent<PlayerState>();
		ref var intent = ref Self.GetComponent<PlayerIntent>();

		switch (@event)
		{
			case InputEventMouseMotion motion when Input.MouseMode == Input.MouseModeEnum.Captured:
				state.Yaw -= motion.Relative.X * LookSensitivity;
				state.Pitch = Mathf.Clamp(state.Pitch - motion.Relative.Y * LookSensitivity, -1.5533f, 1.5533f);
				break;

			case InputEventMouseButton { Pressed: true } button:
				if (Input.MouseMode != Input.MouseModeEnum.Captured) SetMouseCaptured(true);
				else if (button.ButtonIndex == MouseButton.Right) intent.Place++;
				break;

			case InputEventKey { Pressed: true, Echo: false } key:
				HandleKey(key.Keycode, ref state);
				break;
		}
	}

	private void HandleKey(Key key, ref PlayerState state)
	{
		if (key == Key.Escape)
		{
			SetMouseCaptured(false);
			return;
		}

		int slot = (int)key - (int)Key.Key1;
		if (slot >= 0 && slot < Blocks.Palette.Length)
		{
			state.Selected = Blocks.Palette[slot];
			// The memory (if any) is snapped onto the new type by the ghost update; the rule
			// seeds the pending orientation while nothing is remembered.
			GetNodeOrNull<Game>("/root/Main")?.RefreshHud();
			return;
		}

		if (key == Key.F) state.Flying = !state.Flying;
		if (key == Key.G) state.Creative = !state.Creative;
		if (key == Key.R) PlayerSystems.TeleportToSurface(Self, World);
	}

	public Block Selected => Self.IsNull ? Block.Stone : Self.GetComponent<PlayerState>().Selected;
}
