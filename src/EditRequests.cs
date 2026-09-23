using System.Collections.Generic;
using Friflo.Engine.ECS;
using Godot;

namespace Regress;

/// <summary>A proposed world change. Gameplay produces these; the world decides what happens.</summary>
public enum EditKind : byte { Break, Place }

public readonly struct EditRequest
{
	public readonly EditKind Kind;
	public readonly Vector3I Cell;
	public readonly Block Block;
	public readonly Entity Actor;

	/// <summary>Rotation of the placed block; <see cref="Regress.Orientation.None"/> is no rotation.</summary>
	public readonly byte Orientation;

	private EditRequest(EditKind kind, Vector3I cell, Block block, Entity actor, byte orientation)
	{
		Kind = kind;
		Cell = cell;
		Block = block;
		Actor = actor;
		Orientation = orientation;
	}

	public static EditRequest Break(Vector3I cell, Entity actor) => new(EditKind.Break, cell, Block.Air, actor, Regress.Orientation.None);
	public static EditRequest Place(Vector3I cell, Block block, Entity actor, byte orientation = Regress.Orientation.None)
		=> new(EditKind.Place, cell, block, actor, orientation);
}

/// <summary>
/// Expands one requested break into the blocks it actually removes. Mining and felling rules
/// live here, on the world side, so they never leak into the systems that produce requests:
/// a break is not one block, and what it costs and what it takes is a property of the block,
/// not of the player.
/// </summary>
public static class BlockBehaviors
{
	// ---- placement orientation ------------------------------------------

	/// <summary>
	/// Snap a (possibly non-axis) normal to a Face index by its dominant component. Ties go to
	/// the lowest Face index (PosX before Top before PosZ); a zero vector returns Face.Top,
	/// the documented default, so this never throws.
	/// </summary>
	public static int FaceOfNormal(Vector3 n)
	{
		float ax = Mathf.Abs(n.X), ay = Mathf.Abs(n.Y), az = Mathf.Abs(n.Z);
		if (ax == 0f && ay == 0f && az == 0f) return Regress.Face.Top;
		if (ax >= ay && ax >= az) return n.X >= 0f ? Regress.Face.PosX : Regress.Face.NegX;
		if (ay >= az) return n.Y >= 0f ? Regress.Face.Top : Regress.Face.Bottom;
		return n.Z >= 0f ? Regress.Face.PosZ : Regress.Face.NegZ;
	}

	/// <summary>A yaw in quarter turns. Mathf.Floor(x + 0.5) rounds halved quadrants up, and
	/// the &amp; 3 folds any yaw into 0..3, so this is total for every float.</summary>
	private static int YawQuadrant(float yaw)
		=> ((int)Mathf.Floor(yaw / (Mathf.Pi * 0.5f) + 0.5f)) & 3;

	/// <summary>
	/// The orientation a placement starts from. Total for every input: an unknown rule gives
	/// the identity, an unknown clicked face is folded by the mod-free Axis lookup, and a
	/// front-facing block whose up snaps onto its own front axis falls back to the yaw-derived
	/// horizontal so the pick is never degenerate.
	/// </summary>
	public static byte PlaceOrientationFor(Placement kind, int clickedFace, float yaw, float pitch)
	{
		switch (kind)
		{
			case Placement.Axis:
				// A log lies along the clicked axis, rolled by the yaw quadrant.
				return Orientation.Axis(clickedFace, YawQuadrant(yaw));

			case Placement.Face:
			{
				// forward = -Z, so the camera's +Z points back toward the player.
				var basis = PlayerSystems.CameraBasis(yaw, pitch);
				int front = FaceOfNormal(basis.Z);
				int up = FaceOfNormal(basis.Y);
				// Looking along the snapped front axis makes up parallel to front; the camera's
				// X column is always horizontal, so it supplies the yaw-derived up instead.
				if ((front >> 1) == (up >> 1)) up = FaceOfNormal(basis.X);
				return Orientation.Face(front, up);
			}

			default:
				return Orientation.None;
		}
	}

	/// <summary>
	/// The orientation a newly placed block starts from: the placement rule's candidate,
	/// projected onto what the type allows. Always satisfies Blocks.Allows.
	/// </summary>
	public static byte PlaceOrientation(Block b, int clickedFace, float yaw, float pitch)
	{
		byte candidate = PlaceOrientationFor(Blocks.PlacementOf(b), clickedFace, yaw, pitch);
		// Upright has no yaw-free projection from an arbitrary candidate, so build the upright
		// candidate from the yaw quadrant here; Blocks.Snap then keeps it as it stands.
		if (Blocks.PolicyOf(b) == OrientationPolicy.Upright)
			candidate = Orientation.Axis(Regress.Face.Top, YawQuadrant(yaw));
		byte snapped = Blocks.Snap(b, candidate);
		System.Diagnostics.Debug.Assert(Blocks.Allows(b, snapped),
			"PlaceOrientation must never return an orientation the block type disallows");
		return snapped;
	}

	public const int MaxBlocksPerBreak = 256;
	private const int FellRadius = 5;

	private static readonly Vector3I[] Directions =
	{
		new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1),
	};

	// ponytail: reused scratch buffers, so the returned list is only valid until the next call.
	[System.ThreadStatic] private static List<Vector3I> _found;
	[System.ThreadStatic] private static HashSet<Vector3I> _seen;

	/// <summary>Blocks removed by breaking <paramref name="cell"/>.</summary>
	public static List<Vector3I> Collect(VoxelWorld world, Block block, Vector3I cell)
	{
		var found = _found ??= new List<Vector3I>(64);
		var seen = _seen ??= new HashSet<Vector3I>();
		found.Clear();
		seen.Clear();
		found.Add(cell);
		seen.Add(cell);

		if (block == Block.Wood) FellTree(world, cell, found, seen);
		// Vein mining, leaf decay, falling sand: more cases here.
		return found;
	}

	/// <summary>Chop the trunk and take the canopy with it, bounded by radius and block count.</summary>
	private static void FellTree(VoxelWorld world, Vector3I origin, List<Vector3I> found, HashSet<Vector3I> seen)
	{
		var queue = new Queue<Vector3I>();
		queue.Enqueue(origin);

		while (queue.Count > 0)
		{
			var cell = queue.Dequeue();
			foreach (var d in Directions)
			{
				var next = cell + d;
				if (Mathf.Abs(next.X - origin.X) > FellRadius
					|| Mathf.Abs(next.Y - origin.Y) > FellRadius
					|| Mathf.Abs(next.Z - origin.Z) > FellRadius) continue;
				if (!seen.Add(next)) continue;

				var b = world.GetBlock(next.X, next.Y, next.Z);
				if (b != Block.Wood && b != Block.Leaves) continue;

				found.Add(next);
				if (found.Count >= MaxBlocksPerBreak) return;
				if (b == Block.Wood) queue.Enqueue(next); // only wood continues the trunk
			}
		}
	}
}
