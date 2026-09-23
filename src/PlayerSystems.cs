using Friflo.Engine.ECS;
using Godot;

namespace Regress;

/// <summary>Marks the single-player entity.</summary>
public struct PlayerTag : ITag { }

/// <summary>Handles to the Godot nodes that back the player.</summary>
public struct PlayerBody : IComponent
{
	public CharacterBody3D Node;
	public Camera3D Camera;
}

public struct PlayerState : IComponent
{
	public bool Flying;
	public bool Creative;
	public Block Selected;
	public float Yaw;
	public float Pitch;

	/// <summary>Orientation byte used by the next placement. Invariant: canonical (0..24, never
	/// the duplicate byte 9) and always <see cref="Blocks.Allows"/> for <see cref="Selected"/>.
	/// The placement rule seeds it while nothing is remembered; Q/E and accepted placements
	/// own every write.</summary>
	public byte PendingOrientation;

	/// <summary>The last orientation the player chose (rotated, or the byte an accepted click
	/// sent). Always a canonical allowed byte; never a sentinel.</summary>
	public byte StickyOrientation;

	/// <summary>The block type <see cref="StickyOrientation"/> belongs to.
	/// <see cref="Block.Air"/> means "nothing remembered yet" - no 255 sentinel.</summary>
	public Block StickyBlock;

	/// <summary>Last values pushed to the scene graph. Node3D.Rotation is derived from the
	/// basis, so comparing against it is not bit-exact and would re-write every frame.</summary>
	public float AppliedYaw;
	public float AppliedPitch;
	public bool Applied;
}

/// <summary>Per-frame intent. Written by the input system, consumed by move / mine / build.</summary>
public struct PlayerIntent : IComponent
{
	public Vector3 Wish;
	public bool Jump, Up, Down, Sprint;

	/// <summary>Left mouse held. Breaking is a duration, not a click.</summary>
	public bool Mining;

	/// <summary>Queued right-clicks. A counter, because a frame can contain several.</summary>
	public int Place;

	/// <summary>Edge-triggered rotate presses queued this frame: Q = next, E = previous.</summary>
	public int RotateNext, RotatePrev;

	/// <summary>Last frame's key state, for the edge detection in <see cref="PlayerSystems.PollInput"/>.
	/// Polled, not event-based, so a scripted run can feed the counters directly.</summary>
	public bool RotateNextHeld, RotatePrevHeld;

	/// <summary>Scripted input for --demo and --bench, OR-ed with the keyboard.</summary>
	public bool AutoWalk, AutoSprint, AutoMine;
}

/// <summary>Break progress on the block currently under the crosshair.</summary>
public struct PlayerMining : IComponent
{
	public Vector3I Target;
	public float Progress;
	public bool Active;
}

/// <summary>What a right click does at the crosshair. Interaction wins over building: a usable
/// block entity handles the click instead of a block being placed next to it.</summary>
public enum ClickAction : byte { None, Interact, Place }

/// <summary>
/// Player systems. They work on component data rather than on the scene graph, so the ray
/// used for digging comes from the yaw/pitch components instead of from a camera transform
/// that may not have propagated yet. Nothing here mutates the world: mining and building
/// only *request* edits, and the world decides what a break does.
/// </summary>
public static class PlayerSystems
{
	public const float Reach = 6f;
	public const float EyeHeight = 1.62f;
	private const float WalkSpeed = 5.5f;
	private const float SprintSpeed = 9f;
	private const float FlySpeed = 14f;
	private const float Gravity = 26f;
	private const float JumpVelocity = 8.5f;

	/// <summary>Keyboard and mouse buttons -> intent. The only place that reads Input.</summary>
	public static void PollInput(EntityStore store)
	{
		store.Query<PlayerIntent, PlayerState, PlayerBody>().ForEachEntity(
			(ref PlayerIntent intent, ref PlayerState state, ref PlayerBody body, Entity _) =>
		{
			var yaw = Basis.FromEuler(new Vector3(0, state.Yaw, 0));
			var wish = Vector3.Zero;
			if (intent.AutoWalk || Input.IsKeyPressed(Key.W)) wish -= yaw.Z;
			if (Input.IsKeyPressed(Key.S)) wish += yaw.Z;
			if (Input.IsKeyPressed(Key.A)) wish -= yaw.X;
			if (Input.IsKeyPressed(Key.D)) wish += yaw.X;

			intent.Wish = wish.Normalized();
			intent.Jump = Input.IsKeyPressed(Key.Space);
			intent.Up = Input.IsKeyPressed(Key.Space);
			intent.Down = Input.IsKeyPressed(Key.Ctrl);
			intent.Sprint = intent.AutoSprint || Input.IsKeyPressed(Key.Shift);
			intent.Mining = intent.AutoMine
				|| (Input.MouseMode == Input.MouseModeEnum.Captured && Input.IsMouseButtonPressed(MouseButton.Left));

			bool rotateNext = Input.IsKeyPressed(Key.Q);
			bool rotatePrev = Input.IsKeyPressed(Key.E);
			if (rotateNext && !intent.RotateNextHeld) intent.RotateNext++;
			if (rotatePrev && !intent.RotatePrevHeld) intent.RotatePrev++;
			intent.RotateNextHeld = rotateNext;
			intent.RotatePrevHeld = rotatePrev;
		});
	}

	/// <summary>yaw / pitch components -> node rotations.</summary>
	public static void Look(EntityStore store)
	{
		store.Query<PlayerState, PlayerBody>().ForEachEntity((ref PlayerState state, ref PlayerBody body, Entity _) =>
		{
			if (state.Applied && state.AppliedYaw == state.Yaw && state.AppliedPitch == state.Pitch) return;

			body.Node.Rotation = new Vector3(0, state.Yaw, 0);
			body.Camera.Rotation = new Vector3(state.Pitch, 0, 0);
			state.AppliedYaw = state.Yaw;
			state.AppliedPitch = state.Pitch;
			state.Applied = true;
		});
	}

	/// <summary>intent -> velocity -> Godot physics.</summary>
	public static void Move(EntityStore store, VoxelWorld world, float delta)
	{
		store.Query<PlayerIntent, PlayerState, PlayerBody>().ForEachEntity(
			(ref PlayerIntent intent, ref PlayerState state, ref PlayerBody body, Entity entity) =>
		{
			var node = body.Node;

			if (state.Flying)
			{
				float up = (intent.Up ? 1f : 0f) - (intent.Down ? 1f : 0f);
				float speed = intent.Sprint ? FlySpeed * 1.6f : FlySpeed;
				node.Velocity = (intent.Wish + Vector3.Up * up).Normalized() * speed;
			}
			else
			{
				float speed = intent.Sprint ? SprintSpeed : WalkSpeed;
				node.Velocity = new Vector3(intent.Wish.X * speed, node.Velocity.Y, intent.Wish.Z * speed);
				if (node.IsOnFloor())
				{
					if (intent.Jump) node.Velocity = new Vector3(node.Velocity.X, JumpVelocity, node.Velocity.Z);
				}
				else
				{
					node.Velocity = new Vector3(node.Velocity.X, node.Velocity.Y - Gravity * delta, node.Velocity.Z);
				}
			}

			node.MoveAndSlide();

			// A kinematic body that ends up inside solid geometry has no way out: MoveAndSlide
			// accumulates velocity but cannot depenetrate. Put the player back on the surface.
			var feet = node.GlobalPosition + new Vector3(0, 0.1f, 0);
			if (Blocks.IsSolid(world.GetBlock(Mathf.FloorToInt(feet.X), Mathf.FloorToInt(feet.Y), Mathf.FloorToInt(feet.Z)))
				|| node.GlobalPosition.Y < world.BedrockY - 16f) TeleportToSurface(entity, world);
		});
	}

	/// <summary>Accumulates break progress and emits a request when the block gives way.</summary>
	public static void Mine(EntityStore store, VoxelWorld world, float delta)
	{
		store.Query<PlayerIntent, PlayerState, PlayerBody, PlayerMining>().ForEachEntity(
			(ref PlayerIntent intent, ref PlayerState state, ref PlayerBody body, ref PlayerMining mining, Entity entity) =>
		{
			if (!intent.Mining || !CrosshairBlock(world, body.Node, state.Yaw, state.Pitch, out var cell))
			{
				StopMining(ref mining);
				return;
			}

			var block = world.GetBlock(cell.X, cell.Y, cell.Z);
			float hardness = Blocks.HardnessOf(block);
			if (hardness < 0f)
			{
				StopMining(ref mining);
				return;
			}

			if (!mining.Active || mining.Target != cell)
			{
				mining.Target = cell;
				mining.Progress = 0f;
				mining.Active = true;
			}

			mining.Progress = state.Creative || hardness <= 0f ? 1f : mining.Progress + delta / hardness;
			if (mining.Progress < 1f) return;

			world.RequestEdit(EditRequest.Break(cell, entity));
			mining.Progress = 0f;
		});
	}

	/// <summary>Canonical allowed value list per block, ordered by <see cref="Orientation.ToIndex"/>
	/// starting at the identity row (row 8): 0, 10..24, 1..8 for Any, so Upright reads as the
	/// 0 -> 10 -> 11 -> 12 yaw spin. Static: the rotate keys never allocate.</summary>
	private static readonly byte[][] RotateOrder = BuildRotateOrder();

	private static byte[][] BuildRotateOrder()
	{
		var canonical = new byte[Orientation.Count];
		int n = 0;
		for (int value = 0; value <= Orientation.Count; value++)
			if (value != Orientation.IdentityDuplicate) canonical[n++] = (byte)value;
		System.Array.Sort(canonical, (a, b) => Orientation.ToIndex(a).CompareTo(Orientation.ToIndex(b)));
		int start = System.Array.IndexOf(canonical, Orientation.None);
		var ordered = new byte[canonical.Length];
		for (int i = 0; i < canonical.Length; i++) ordered[i] = canonical[(start + i) % canonical.Length];

		var order = new byte[System.Enum.GetValues<Block>().Length][];
		for (int i = 0; i < order.Length; i++)
		{
			var block = (Block)i;
			int count = 0;
			foreach (byte value in ordered) if (Blocks.Allows(block, value)) count++;
			var allowed = new byte[count];
			int k = 0;
			foreach (byte value in ordered) if (Blocks.Allows(block, value)) allowed[k++] = value;
			order[i] = allowed;
		}
		return order;
	}

	/// <summary>Q/E presses -> the next/previous allowed orientation. Rotation writes the
	/// pending state and the memory, never an already-placed block.</summary>
	private static void Rotate(ref PlayerState state, ref PlayerIntent intent)
	{
		byte[] allowed = RotateOrder[(int)state.Selected];
		int index = System.Array.IndexOf(allowed, state.PendingOrientation);
		if (index < 0) index = intent.RotateNext > 0 ? allowed.Length - 1 : 0; // stale byte: first step lands on the list start
		for (int i = 0; i < intent.RotateNext; i++) index = (index + 1) % allowed.Length;
		for (int i = 0; i < intent.RotatePrev; i++) index = (index - 1 + allowed.Length) % allowed.Length;
		state.PendingOrientation = allowed[index];
		state.StickyOrientation = allowed[index];
		state.StickyBlock = state.Selected;
	}

	/// <summary>
	/// Applies this frame's rotate presses and the sticky memory to the pending orientation.
	/// Pure: no scene graph and no physics, so the state machine is testable with a synthetic
	/// target. <paramref name="target"/> supplies the clicked face the placement rule reads.
	/// </summary>
	public static void UpdatePending(ref PlayerState state, ref PlayerIntent intent, PlacementTarget? target)
	{
		int clickedFace = target?.Face ?? Face.Top;

		if (intent.RotateNext > 0 || intent.RotatePrev > 0) Rotate(ref state, ref intent);

		if (state.StickyBlock == Block.Air)
		{
			// Nothing remembered yet: the placement rule seeds the pending orientation every
			// frame, so looking around moves the ghost.
			state.PendingOrientation = BlockBehaviors.PlaceOrientation(state.Selected, clickedFace, state.Yaw, state.Pitch);
		}
		else if (state.StickyBlock != state.Selected)
		{
			// The memory belongs to another type: snap the remembered byte onto the new type,
			// never discard it.
			state.StickyOrientation = Blocks.Snap(state.Selected, state.StickyOrientation);
			state.StickyBlock = state.Selected;
			state.PendingOrientation = state.StickyOrientation;
		}

		intent.RotateNext = 0;
		intent.RotatePrev = 0;
	}

	/// <summary>
	/// Per-frame placement preview. Sits between input and build so a click in this frame
	/// places the byte the ghost showed: rotate presses land first, then the sticky memory
	/// decides the pending orientation, then the ghost renders it. The ghost is hidden when
	/// the click would be refused (no hit, occupied cell, player-box overlap).
	/// </summary>
	public static void UpdateGhost(VoxelWorld world, Entity player, PlacementGhost ghost)
	{
		ref var state = ref player.GetComponent<PlayerState>();
		ref var body = ref player.GetComponent<PlayerBody>();
		ref var intent = ref player.GetComponent<PlayerIntent>();

		var target = PendingTarget(world, body.Node, state.Yaw, state.Pitch);
		UpdatePending(ref state, ref intent, target);
		ghost.Update(world, target, state.Selected, state.PendingOrientation);
	}

	/// <summary>The cell and clicked face the next accepted click would use, or null when the
	/// click would be refused. This is the one place that assembles the ghost's target.</summary>
	public static PlacementTarget? PendingTarget(VoxelWorld world, CharacterBody3D node, float yaw, float pitch)
	{
		if (!PlaceTarget(world, node, yaw, pitch, out var cell, out int clickedFace)) return null;
		if (PlacementRefused(world, node, cell)) return null;
		return new PlacementTarget(cell, clickedFace);
	}

	/// <summary>True when a placement into this cell would be refused: occupied, or the
	/// player's own box is in the way.</summary>
	public static bool PlacementRefused(VoxelWorld world, CharacterBody3D node, Vector3I cell)
		=> world.GetBlock(cell.X, cell.Y, cell.Z) != Block.Air || PlayerBox(node).Intersects(BlockBox(cell));

	/// <summary>Writes an accepted placement and advances the memory to the byte actually
	/// sent. Split out of <see cref="RequestPlaceAtCrosshair"/> so the write path is testable
	/// without a physics raycast.</summary>
	public static byte PlaceAt(VoxelWorld world, Entity player, Vector3I cell)
	{
		ref var state = ref player.GetComponent<PlayerState>();
		// Defensive normaliser at the write site: even a pending byte that is one frame stale
		// (the --demo path changes Selected and clicks in the same frame) cannot reach a voxel
		// without passing the selected type's policy.
		byte orientation = Blocks.Snap(state.Selected, state.PendingOrientation);
		state.PendingOrientation = orientation;
		world.RequestEdit(EditRequest.Place(cell, state.Selected, player, orientation));
		state.StickyOrientation = orientation; // the byte actually sent becomes the memory
		state.StickyBlock = state.Selected;
		return orientation;
	}

	/// <summary>Queued place clicks -> place requests.</summary>
	public static void Build(EntityStore store, VoxelWorld world)
	{
		store.Query<PlayerIntent, PlayerState, PlayerBody>().ForEachEntity(
			(ref PlayerIntent intent, ref PlayerState state, ref PlayerBody body, Entity entity) =>
		{
			for (int i = 0; i < intent.Place; i++)
				if (RequestPlaceAtCrosshair(world, entity) == null) break;
			intent.Place = 0;
		});
	}

	public static void TeleportToSurface(Entity player, VoxelWorld world)
	{
		ref var body = ref player.GetComponent<PlayerBody>();
		var node = body.Node;
		int wx = Mathf.FloorToInt(node.GlobalPosition.X);
		int wz = Mathf.FloorToInt(node.GlobalPosition.Z);
		int y = world.SurfaceY(wx, wz);
		world.EnsureAreaAround(new Vector3(wx + 0.5f, y, wz + 0.5f), 1);
		node.GlobalPosition = new Vector3(wx + 0.5f, y + 0.5f, wz + 0.5f);
		node.Velocity = Vector3.Zero;
	}

	// ---- helpers shared by the systems and the scripted tests ------------

	/// <summary>Block under the crosshair, if it is solid.</summary>
	public static bool CrosshairBlock(VoxelWorld world, CharacterBody3D node, float yaw, float pitch, out Vector3I cell)
	{
		cell = default;
		if (!Raycast(world, node, yaw, pitch, out var point, out var normal)) return false;
		var block = BlockAt(point - normal * 0.5f);
		if (!Blocks.IsSolid(world.GetBlock(block.X, block.Y, block.Z))) return false;
		cell = block;
		return true;
	}

	/// <summary>Empty cell the crosshair is aiming at, if any. <paramref name="clickedFace"/> is
	/// the snapped face of the surface that was hit, or Face.Top when nothing was hit.</summary>
	public static bool PlaceTarget(VoxelWorld world, CharacterBody3D node, float yaw, float pitch,
		out Vector3I cell, out int clickedFace)
	{
		cell = default;
		clickedFace = Regress.Face.Top;
		if (!Raycast(world, node, yaw, pitch, out var point, out var normal)) return false;
		cell = BlockAt(point + normal * 0.5f);
		clickedFace = BlockBehaviors.FaceOfNormal(normal);
		return world.GetBlock(cell.X, cell.Y, cell.Z) == Block.Air;
	}

	/// <summary>Immediate break request for scripted runs, bypassing mining time.</summary>
	public static Vector3I? RequestBreakAtCrosshair(VoxelWorld world, Entity player)
	{
		ref var state = ref player.GetComponent<PlayerState>();
		ref var body = ref player.GetComponent<PlayerBody>();
		if (!CrosshairBlock(world, body.Node, state.Yaw, state.Pitch, out var cell)) return null;
		if (!Blocks.IsBreakable(world.GetBlock(cell.X, cell.Y, cell.Z))) return null;
		world.RequestEdit(EditRequest.Break(cell, player));
		return cell;
	}

	/// <summary>Pure precedence: interaction beats building. Testable without a physics raycast.</summary>
	public static ClickAction RightClickAction(VoxelWorld world, bool hasTarget, Vector3I targetCell, bool hasPlacement)
		=> hasTarget && world.BlockEntities.Contains(targetCell) ? ClickAction.Interact
			: hasPlacement ? ClickAction.Place : ClickAction.None;

	/// <summary>The one place that decides whether a placement is legal, so the interactive
	/// path and the scripted path cannot drift apart. Interaction now takes precedence: a
	/// usable block entity at the crosshair handles the click instead of a block being
	/// placed next to it.</summary>
	public static Vector3I? RequestPlaceAtCrosshair(VoxelWorld world, Entity player)
	{
		ref var state = ref player.GetComponent<PlayerState>();
		ref var body = ref player.GetComponent<PlayerBody>();
		bool hasTarget = CrosshairBlock(world, body.Node, state.Yaw, state.Pitch, out var target);
		bool hasPlacement = PlaceTarget(world, body.Node, state.Yaw, state.Pitch, out var cell, out _)
			&& !PlacementRefused(world, body.Node, cell); // never inside the player
		switch (RightClickAction(world, hasTarget, target, hasPlacement))
		{
			case ClickAction.Interact:
				BlockInteractions.Interact(world, target, player);
				return target;
			case ClickAction.Place:
				PlaceAt(world, player, cell);
				return cell;
			default:
				return null;
		}
	}

	/// <summary>True if something solid is directly in front of the player. Used to tell
	/// "cannot walk" apart from "blocked by a step", which are very different failures.</summary>
	public static bool BlockedAhead(VoxelWorld world, Entity player, float distance)
	{
		ref var state = ref player.GetComponent<PlayerState>();
		ref var body = ref player.GetComponent<PlayerBody>();
		var node = body.Node;
		var eye = node.GlobalPosition + new Vector3(0, 0.9f, 0);
		var dir = -Basis.FromEuler(new Vector3(0, state.Yaw, 0)).Z;
		var query = PhysicsRayQueryParameters3D.Create(eye, eye + dir * distance);
		query.Exclude = new Godot.Collections.Array<Rid> { node.GetRid() };
		return node.GetWorld3D().DirectSpaceState.IntersectRay(query).Count > 0;
	}

	public static Basis CameraBasis(float yaw, float pitch)
		=> Basis.FromEuler(new Vector3(pitch, yaw, 0));

	private static void StopMining(ref PlayerMining mining)
	{
		mining.Active = false;
		mining.Progress = 0f;
	}

	private static bool Raycast(VoxelWorld world, CharacterBody3D node, float yaw, float pitch,
		out Vector3 point, out Vector3 normal)
	{
		point = Vector3.Zero;
		normal = Vector3.Zero;

		var eye = node.GlobalPosition + new Vector3(0, EyeHeight, 0);
		var dir = -CameraBasis(yaw, pitch).Z;
		var query = PhysicsRayQueryParameters3D.Create(eye, eye + dir * Reach);
		query.Exclude = new Godot.Collections.Array<Rid> { node.GetRid() };
		var hit = node.GetWorld3D().DirectSpaceState.IntersectRay(query);
		if (hit.Count == 0) return false;

		point = (Vector3)hit["position"];
		normal = (Vector3)hit["normal"];
		return true;
	}

	private static Vector3I BlockAt(Vector3 p)
		=> new(Mathf.FloorToInt(p.X), Mathf.FloorToInt(p.Y), Mathf.FloorToInt(p.Z));

	private static Aabb PlayerBox(CharacterBody3D node)
		=> new(node.GlobalPosition - new Vector3(0.35f, 0.1f, 0.35f), new Vector3(0.7f, 1.9f, 0.7f));

	private static Aabb BlockBox(Vector3I cell) => new(new Vector3(cell.X, cell.Y, cell.Z), Vector3.One);
}
