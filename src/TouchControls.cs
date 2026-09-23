using Godot;

namespace Regress;

public enum TouchGesture : byte { Tap, Hold, Look }

public partial class TouchControls : CanvasLayer
{
	public const float StickRadius = 150f;
	public const float StickDeadZone = 24f;
	public const float KnobRadius = 56f;
	public const float LookRadPerPx = 0.0044f;
	public const float TapSlopPx = 16f;
	public const float PitchClamp = 1.5533f;

	// The only calibration knob the scenario sets: 60000 => every release is a Tap, 0 => Hold fires next frame.
	public static float HoldMs = 250f;

	public static bool Active;
	public static Vector2 Move;
	public static bool Jump, Down, Mining;

	public Player Player;

	private readonly Button[] _slots = new Button[Blocks.Palette.Length];
	private Button _fine;

	public static TouchGesture Classify(float elapsedMs, float movedPx) =>
		movedPx > TapSlopPx ? TouchGesture.Look : (elapsedMs >= HoldMs ? TouchGesture.Hold : TouchGesture.Tap);

	public override void _Ready()
	{
		Layer = 2;

		var stick = new TouchStick { Name = "StickArea", Controls = this };
		stick.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		stick.AnchorRight = 0.5f;
		stick.MouseFilter = Control.MouseFilterEnum.Stop;
		AddChild(stick);

		var look = new TouchLook { Name = "LookArea", Controls = this };
		look.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		look.AnchorLeft = 0.5f;
		look.MouseFilter = Control.MouseFilterEnum.Stop;
		var hint = new Label { Name = "LookHint", Text = "look", Position = new Vector2(24, 64) };
		hint.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.85f));
		look.AddChild(hint);
		AddChild(look);

		var jump = GridButton("Jump", "Jump", 0, 0);
		jump.ButtonDown += () => Jump = true;
		jump.ButtonUp += () => Jump = false;

		var down = GridButton("Down", "Down", 1, 0);
		down.ButtonDown += () => Down = true;
		down.ButtonUp += () => Down = false;

		var fly = GridButton("Fly", "Fly", 0, 1);
		fly.ToggleMode = true;
		fly.Toggled += pressed =>
		{
			ref var state = ref Player.Self.GetComponent<PlayerState>();
			state.Flying = pressed;
		};

		_fine = GridButton("Fine", "Fine", 2, 0);
		_fine.ToggleMode = true;
		_fine.Toggled += pressed =>
		{
			Player.World.Rules.FineMode = pressed;
			GetNodeOrNull<Game>("/root/Main")?.RefreshHud();
		};

		var rotateNext = GridButton("RotateNext", "Q", 1, 1);
		rotateNext.Pressed += () =>
		{
			ref var intent = ref Player.Self.GetComponent<PlayerIntent>();
			intent.RotateNext++;
		};

		var rotatePrev = GridButton("RotatePrev", "E", 2, 1);
		rotatePrev.Pressed += () =>
		{
			ref var intent = ref Player.Self.GetComponent<PlayerIntent>();
			intent.RotatePrev++;
		};

		for (int i = 0; i < Blocks.Palette.Length; i++)
		{
			int slot = i;
			var button = new Button
			{
				Name = $"Slot{i}",
				Text = Blocks.NameOf(Blocks.Palette[i]),
				ToggleMode = true,
				FocusMode = Control.FocusModeEnum.None,
				Position = new Vector2(-320f + i * 72f, -84f),
				Size = new Vector2(64, 64),
			};
			button.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
			button.Pressed += () => Select(slot);
			AddChild(button);
			_slots[i] = button;
		}
	}

	private Button GridButton(string name, string text, int col, int row)
	{
		var button = new Button { Name = name, Text = text, FocusMode = Control.FocusModeEnum.None };
		button.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
		button.Position = new Vector2(-140f - col * 120f, -140f - row * 120f);
		button.Size = new Vector2(110, 110);
		AddChild(button);
		return button;
	}

	public void Stick(Vector2 offset) =>
		Move = offset.Length() <= StickDeadZone ? Vector2.Zero : offset / StickRadius;

	public void Look(Vector2 relative)
	{
		if (Player == null || Player.Self.IsNull) return;
		ref var state = ref Player.Self.GetComponent<PlayerState>();
		state.Yaw -= relative.X * LookRadPerPx;
		state.Pitch = Mathf.Clamp(state.Pitch - relative.Y * LookRadPerPx, -PitchClamp, PitchClamp);
	}

	private void Select(int slot)
	{
		ref var state = ref Player.Self.GetComponent<PlayerState>();
		state.Selected = Blocks.Palette[slot];
		GetNodeOrNull<Game>("/root/Main")?.RefreshHud();
	}

	public override void _Process(double delta)
	{
		if (Player == null || Player.Self.IsNull) return;
		ref var state = ref Player.Self.GetComponent<PlayerState>();
		for (int i = 0; i < _slots.Length; i++)
			_slots[i].SetPressedNoSignal(Blocks.Palette[i] == state.Selected);
		_fine.SetPressedNoSignal(Player.World.Rules.FineMode);
	}
}

public partial class TouchStick : Control
{
	public TouchControls Controls;

	private int _touchId = -1;
	private Vector2 _base, _offset;

	public override void _GuiInput(InputEvent @event)
	{
		switch (@event)
		{
			case InputEventScreenTouch { Pressed: true } t when _touchId < 0:
				_touchId = t.Index;
				_base = t.Position;
				_offset = Vector2.Zero;
				Controls.Stick(Vector2.Zero);
				QueueRedraw();
				AcceptEvent();
				break;
			case InputEventScreenDrag d when d.Index == _touchId:
				_offset = (_offset + d.Relative).LimitLength(TouchControls.StickRadius);
				Controls.Stick(_offset);
				QueueRedraw();
				AcceptEvent();
				break;
			case InputEventScreenTouch { Pressed: false } t when t.Index == _touchId:
				_touchId = -1;
				_offset = Vector2.Zero;
				Controls.Stick(Vector2.Zero);
				QueueRedraw();
				AcceptEvent();
				break;
		}
	}

	public override void _Draw()
	{
		Vector2 origin = _touchId < 0
			? new Vector2(TouchControls.StickRadius + 40f, Size.Y - TouchControls.StickRadius - 40f)
			: _base;
		DrawCircle(origin, TouchControls.StickRadius, new Color(1, 1, 1, 0.10f));
		DrawArc(origin, TouchControls.StickRadius, 0, Mathf.Tau, 64, new Color(1, 1, 1, 0.35f), 2f, true);
		Vector2 knob = origin + (_touchId < 0 ? Vector2.Zero : _offset);
		DrawCircle(knob, TouchControls.KnobRadius, new Color(1, 1, 1, 0.22f));
		DrawArc(knob, TouchControls.KnobRadius, 0, Mathf.Tau, 48, new Color(1, 1, 1, 0.5f), 2f, true);
	}
}

public partial class TouchLook : Control
{
	public TouchControls Controls;

	private int _touchId = -1;
	private ulong _startMs;
	private float _moved;
	private bool _hold;

	public override void _GuiInput(InputEvent @event)
	{
		switch (@event)
		{
			case InputEventScreenTouch { Pressed: true } t when _touchId < 0:
				_touchId = t.Index;
				_startMs = Time.GetTicksMsec();
				_moved = 0;
				_hold = false;
				AcceptEvent();
				break;
			case InputEventScreenDrag d when d.Index == _touchId:
				_moved += d.Relative.Length();
				Controls.Look(d.Relative);
				AcceptEvent();
				break;
			case InputEventScreenTouch { Pressed: false } t when t.Index == _touchId:
				Release();
				AcceptEvent();
				break;
		}
	}

	public override void _Process(double delta)
	{
		if (_touchId < 0 || _hold) return;
		if (TouchControls.Classify(Time.GetTicksMsec() - _startMs, _moved) != TouchGesture.Hold) return;
		_hold = true;
		TouchControls.Mining = true;
	}

	private void Release()
	{
		var gesture = TouchControls.Classify(Time.GetTicksMsec() - _startMs, _moved);
		if (_hold || gesture == TouchGesture.Hold)
		{
			TouchControls.Mining = false;
		}
		else if (gesture == TouchGesture.Tap && Controls.Player != null && !Controls.Player.Self.IsNull)
		{
			ref var intent = ref Controls.Player.Self.GetComponent<PlayerIntent>();
			intent.Place++;
		}
		_touchId = -1;
		_hold = false;
		_moved = 0;
	}

	public override void _Draw() =>
		DrawRect(new Rect2(Vector2.One * 2f, Size - Vector2.One * 4f), new Color(1, 1, 1, 0.35f), false, 2f);
}
