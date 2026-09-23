using System.Collections.Generic;
using Godot;

namespace Regress;

/// <summary>
/// Scripted touch scenario. Injects real InputEventScreenTouch / InputEventScreenDrag
/// through Viewport.PushInput and asserts the ECS state the adapter writes.
/// Deterministic: TouchControls.HoldMs is the only calibration knob (60000 => every release
/// is a Tap; 0 => Hold fires on the next frame). No wall-clock assertions.
/// </summary>
public partial class TouchTest : Node
{
	private static readonly string[] Buttons =
	{
		"Jump", "Down", "Fly", "RotateNext", "RotatePrev",
		"Slot0", "Slot1", "Slot2", "Slot3", "Slot4", "Slot5", "Slot6", "Slot7", "Slot8",
	};

	private readonly VoxelWorld _world;
	private readonly Player _player;
	private readonly TouchControls _touch;
	private readonly List<string> _passed = new();

	private string _fail;
	private int _frame;
	private int _nextId;
	private int _stickId, _lookId, _jumpId;
	private Vector3I _cell;
	private float _yawBefore;
	private bool _flyingBefore;
	private byte _orientationBefore;

	public TouchTest(VoxelWorld world, Player player, TouchControls touch)
	{
		_world = world;
		_player = player;
		_touch = touch;
		ProcessPriority = 1; // runs after Game._Process
	}

	public override void _Process(double delta)
	{
		if (_fail != null)
		{
			Finish();
			return;
		}
		_frame++;

		ref var state = ref _player.Self.GetComponent<PlayerState>();
		ref var intent = ref _player.Self.GetComponent<PlayerIntent>();

		switch (_frame)
		{
			case 1:
				Check(_touch != null, "hud", "TouchControls missing");
				Check(TouchControls.Active, "hud", "TouchControls.Active is false");
				Check(_touch != null && _touch.Visible, "hud", "touch layer not visible");
				if (_touch != null)
				{
					Check(_touch.GetNodeOrNull<Control>("StickArea") != null, "hud", "StickArea missing");
					Check(_touch.GetNodeOrNull<Control>("LookArea") != null, "hud", "LookArea missing");
					foreach (string name in Buttons)
					{
						var button = _touch.GetNodeOrNull<Button>(name);
						Vector2 size = Vector2.Zero;
						if (button != null) size = button.GetGlobalRect().Size;
						Check(size.X > 0 && size.Y > 0, "hud", $"{name} missing or empty rect");
					}
				}
				var viewport = GetViewport().GetVisibleRect().Size;
				Check(viewport.X > 0 && viewport.Y > 0, "hud", $"degenerate viewport {viewport}");
				Check(TouchControls.Classify(100, 5) == TouchGesture.Tap, "classify", "100ms/5px -> Tap");
				Check(TouchControls.Classify(300, 5) == TouchGesture.Hold, "classify", "300ms/5px -> Hold");
				Check(TouchControls.Classify(100, 40) == TouchGesture.Look, "classify", "100ms/40px -> Look");
				Check(TouchControls.Classify(300, 40) == TouchGesture.Look, "classify", "300ms/40px -> Look");
				break;

			case 2:
				_stickId = Down(_touch.GetNode<Control>("StickArea"));
				Drag(_stickId, new Vector2(0, -100));
				Check(TouchControls.Move.Y < -0.6f, "stick", $"Move={TouchControls.Move}");
				break;

			case 3:
				Check(intent.Wish.Z < -0.9f, "stick", $"Wish.Z={intent.Wish.Z}");
				Check(intent.Wish.Length() > 0.99f, "stick", $"Wish={intent.Wish}");
				Up(_touch.GetNode<Control>("StickArea"), _stickId);
				Check(TouchControls.Move == Vector2.Zero, "stick", $"Move={TouchControls.Move}");
				break;

			case 4:
				Check(intent.Wish == Vector3.Zero, "stick-release", $"Wish={intent.Wish}");
				break;

			case 5:
				_yawBefore = state.Yaw;
				_lookId = Down(_touch.GetNode<Control>("LookArea"));
				Drag(_lookId, new Vector2(100, 0));
				Check(Mathf.Abs(state.Yaw - (_yawBefore - 0.44f)) < 1e-3f, "drag-yaw",
					$"Yaw={state.Yaw} expected {_yawBefore - 0.44f}");
				Up(_touch.GetNode<Control>("LookArea"), _lookId);
				Check(intent.Place == 0, "drag-yaw", $"Place={intent.Place}");
				break;

			case 6:
				TouchControls.HoldMs = 60000f;
				PlayerSystems.TeleportToSurface(_player.Self, _world);
				state.Pitch = Mathf.DegToRad(-30f);
				break;

			case 8:
				bool found = false;
				for (int step = 0; step < 8; step++)
				{
					state.Yaw = step * Mathf.Pi * 0.25f;
					if (PlayerSystems.PendingTarget(_world, _player, state.Yaw, state.Pitch) is { } target)
					{
						_cell = target.Cell;
						found = true;
						break;
					}
				}
				Check(found, "tap-place", "no pending placement target in the yaw sweep");
				Check(_world.GetBlock(_cell.X, _cell.Y, _cell.Z) == Block.Air, "tap-place", "target is not air");
				break;

			case 9:
				int tapId = Down(_touch.GetNode<Control>("LookArea"));
				Up(_touch.GetNode<Control>("LookArea"), tapId);
				Check(intent.Place == 1, "tap-place", $"Place={intent.Place}");
				break;

			case 12:
				Check(_world.GetBlock(_cell.X, _cell.Y, _cell.Z) == state.Selected, "tap-place",
					$"block={_world.GetBlock(_cell.X, _cell.Y, _cell.Z)} selected={state.Selected}");
				Check(intent.Place == 0, "tap-place", $"Place={intent.Place}");
				break;

			case 13:
				state.Creative = true;
				TouchControls.HoldMs = 0f;
				Check(PlayerSystems.CrosshairBlock(_world, _player, state.Yaw, state.Pitch, out _cell),
					"hold-mine", "no crosshair block");
				Check(Blocks.IsBreakable(_world.GetBlock(_cell.X, _cell.Y, _cell.Z)), "hold-mine", "target not breakable");
				_lookId = Down(_touch.GetNode<Control>("LookArea"));
				break;

			case 14:
				Check(TouchControls.Mining, "hold-mine", "Mining not set after HoldMs=0");
				break;

			case 16:
				Check(_world.GetBlock(_cell.X, _cell.Y, _cell.Z) == Block.Air, "hold-mine",
					$"block={_world.GetBlock(_cell.X, _cell.Y, _cell.Z)} still solid");
				Up(_touch.GetNode<Control>("LookArea"), _lookId);
				Check(!TouchControls.Mining, "hold-mine", "Mining still set after release");
				break;

			case 17:
				Check(!intent.Mining, "hold-mine", "intent.Mining still set");
				break;

			case 18:
				// Test-only layout; the production layout is untouched and is verified by the
				// tier-B --touch screenshot, not headless. Button size clamps up to the theme
				// minimum, so Down() raises the target before injecting to keep hit-testing
				// unambiguous.
				for (int i = 0; i < Buttons.Length; i++)
				{
					var button = _touch.GetNode<Button>(Buttons[i]);
					button.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
					button.Position = new Vector2(2 + (i % 4) * 16, 2 + (i / 4) * 16);
					button.Size = new Vector2(12, 12);
				}
				_jumpId = Down(_touch.GetNode<Control>("Jump"));
				Check(TouchControls.Jump, "jump", "Jump flag not set on button down");
				break;

			case 19:
				Check(intent.Jump, "jump", "intent.Jump not set");
				Up(_touch.GetNode<Control>("Jump"), _jumpId);
				Check(!TouchControls.Jump, "jump", "Jump flag still set after release");
				break;

			case 20:
				Check(!intent.Jump, "jump", "intent.Jump still set");
				break;

			case 21:
				_flyingBefore = state.Flying;
				TapButton("Fly");
				Check(state.Flying != _flyingBefore, "fly", $"Flying stayed {state.Flying}");
				break;

			case 22:
				_orientationBefore = state.PendingOrientation;
				TapButton("RotateNext");
				Check(intent.RotateNext == 1, "rotate", $"RotateNext={intent.RotateNext}");
				break;

			case 23:
				Check(intent.RotateNext == 0, "rotate", $"RotateNext={intent.RotateNext}");
				Check(state.PendingOrientation != _orientationBefore, "rotate", "PendingOrientation unchanged");
				break;

			case 24:
				TapButton("Slot3");
				Check(state.Selected == Blocks.Palette[3], "hotbar", $"Selected={state.Selected}");
				break;

			case 25:
				Finish();
				break;
		}
	}

	private void Push(InputEvent e) => GetViewport().PushInput(e, true);

	private int Down(Control control)
	{
		// The target must be topmost: in the 64x64 headless viewport the production HUD rects
		// overlap the gesture areas, and the re-laid test grid can overlap itself when Button
		// sizes clamp up to their theme minimum.
		control.MoveToFront();
		int id = _nextId++;
		Push(new InputEventScreenTouch { Index = id, Pressed = true, Position = control.GetGlobalRect().GetCenter() });
		return id;
	}

	private void Up(Control control, int id) =>
		Push(new InputEventScreenTouch { Index = id, Pressed = false, Position = control.GetGlobalRect().GetCenter() });

	private void Drag(int id, Vector2 relative) =>
		Push(new InputEventScreenDrag { Index = id, Relative = relative });

	private void TapButton(string name)
	{
		var button = _touch.GetNode<Button>(name);
		Up(button, Down(button));
	}

	private void Check(bool ok, string name, string detail)
	{
		if (!ok)
		{
			if (_fail == null) _fail = $"{name}: {detail}";
		}
		else if (!_passed.Contains(name))
		{
			_passed.Add(name);
		}
	}

	private void Finish()
	{
		GD.Print(_fail == null
			? $"scenario touch: PASS {string.Join(" ", _passed)}"
			: $"scenario touch: FAIL {_fail}");
		GetTree().Quit(_fail == null ? 0 : 1);
	}
}
