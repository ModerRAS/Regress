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

	// ---- operation volume -------------------------------------------------

	/// <summary>Edge of the default break/place volume, in cells: the 4-voxel old-block grid,
	/// so the default operation is 4x4x4 = 64 cells. Fine mode is 1.</summary>
	public const int OperationGrid = 4;
	public const int OperationVolumeCells = OperationGrid * OperationGrid * OperationGrid;  // 64

	/// <summary>Edge of the operation volume: 4 cells by default, 1 in fine mode.</summary>
	public static int OperationExtent(WorldRules rules) => rules.FineMode ? 1 : OperationGrid;

	/// <summary>The volume origin containing <paramref name="cell"/>: floor-divided to the 4-grid
	/// (so negatives align) in coarse mode, the cell itself in fine mode. Idempotent.</summary>
	public static Vector3I OperationAnchor(VoxelWorld world, Vector3I cell)
	{
		if (OperationExtent(world.Rules) == 1) return cell;
		return new Vector3I(
			VoxelWorld.FloorDiv(cell.X, OperationGrid) * OperationGrid,
			VoxelWorld.FloorDiv(cell.Y, OperationGrid) * OperationGrid,
			VoxelWorld.FloorDiv(cell.Z, OperationGrid) * OperationGrid);
	}

	/// <summary>Break time of one operation: the hardest breakable solid in the volume, so a
	/// mixed volume costs what its slowest block costs. -1 when the hit cell is unbreakable:
	/// the hit cell gates the operation, the volume only spreads it.</summary>
	public static float OperationHardness(VoxelWorld world, Vector3I cell)
	{
		if (!Blocks.IsBreakable(world.GetBlock(cell.X, cell.Y, cell.Z))) return -1f;

		var anchor = OperationAnchor(world, cell);
		int extent = OperationExtent(world.Rules);
		float hardest = 0f;
		for (int dy = 0; dy < extent; dy++)
			for (int dz = 0; dz < extent; dz++)
				for (int dx = 0; dx < extent; dx++)
				{
					float h = Blocks.HardnessOf(world.GetBlock(anchor.X + dx, anchor.Y + dy, anchor.Z + dz));
					if (h > hardest) hardest = h;
				}
		return hardest;
	}

	/// <summary>Bound on one break: the operation volume plus the largest tree the generator can
	/// stamp. Contains accepts exactly the stamped volume, so the tree part is a hard cap, not a
	/// margin. In production it is a silent-truncation safety valve; correctness ("collected ==
	/// the spec's volume") is owned by the SelfTest assertions, never by this cap.</summary>
	public static readonly int MaxBlocksPerBreak = TerrainGenerator.MaxTreeBlocks() + OperationVolumeCells;

	/// <summary>Derived reach of one tree from any of its cells: MaxTreeHeight plus a margin.
	/// The spec decides the fell now; this is only a safety recheck, never the decision.</summary>
	private const int FellRadius = TerrainGenerator.MaxTreeHeight + 2;

	// ponytail: reused scratch buffer, so the returned list is only valid until the next call.
	[System.ThreadStatic] private static List<Vector3I> _found;

	/// <summary>Blocks removed by breaking <paramref name="cell"/>: the operation volume (64 cells
	/// by default, 1 in fine mode) plus the fell set when the hit block is wood or leaves.
	/// Duplicate-free; the fell decision stays the hit cell's.</summary>
	public static List<Vector3I> Collect(VoxelWorld world, Block block, Vector3I cell)
	{
		var found = _found ??= new List<Vector3I>(OperationVolumeCells);
		found.Clear();

		// The volume first: every cell it covers is a candidate, breakable or not. ApplyBreak
		// drops the unbreakable ones (Air, Bedrock), so this stays a pure geometry set.
		var anchor = OperationAnchor(world, cell);
		int extent = OperationExtent(world.Rules);
		for (int dy = 0; dy < extent; dy++)
			for (int dz = 0; dz < extent; dz++)
				for (int dx = 0; dx < extent; dx++)
					found.Add(new Vector3I(anchor.X + dx, anchor.Y + dy, anchor.Z + dz));

		// Tree identity comes from the generator spec, never from the block type: a player's
		// wood house is not a tree and breaks one cell at a time. Structures the world
		// generates as "one thing" are aligned through such a spec (pure function, zero
		// storage) rather than a per-voxel source tag; trees are the first example.
		if (block == Block.Wood || block == Block.Leaves) FellTree(world, cell, found);
		// Vein mining, leaf decay, falling sand: more cases here.
		return found;
	}

	/// <summary>Collect the generated tree that owns this cell, if any (see
	/// <see cref="TerrainGenerator.TryGetTreeAt"/>): every spec cell that is currently Wood
	/// or Leaves is collected. Cells no spec contains are left as the operation volume alone. A player
	/// block placed inside a tree's own volume is indistinguishable from a generated one and
	/// falls with it; a per-tree ECS entity would be the upgrade path if trees ever need
	/// their own state.</summary>
	private static void FellTree(VoxelWorld world, Vector3I origin, List<Vector3I> found)
	{
		if (!world.Terrain.TryGetTreeAt(origin.X, origin.Y, origin.Z, out var spec)) return;

		int reach = TerrainGenerator.CanopyMaxRadius;
		int top = spec.MinY + TerrainGenerator.MaxTreeHeight;
		for (int y = spec.MinY; y <= top; y++)
		{
			for (int z = spec.AnchorZ - reach; z <= spec.AnchorZ + reach; z++)
			{
				for (int x = spec.AnchorX - reach; x <= spec.AnchorX + reach; x++)
				{
					if (x == origin.X && y == origin.Y && z == origin.Z) continue; // already in found
					// Safety recheck only: a spec cell is never this far from the hit cell.
					if (Mathf.Abs(x - origin.X) > FellRadius
						|| Mathf.Abs(y - origin.Y) > FellRadius
						|| Mathf.Abs(z - origin.Z) > FellRadius) continue;
					if (!TerrainGenerator.Contains(spec, x, y, z)) continue;

					var b = world.GetBlock(x, y, z);
					if (b != Block.Wood && b != Block.Leaves) continue;
					var c = new Vector3I(x, y, z);
					if (found.Contains(c)) continue; // already in the operation volume
					found.Add(c);
					if (found.Count >= MaxBlocksPerBreak) return;
				}
			}
		}
	}
}
