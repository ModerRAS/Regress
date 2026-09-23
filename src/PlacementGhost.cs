using Godot;

namespace Regress;

/// <summary>A cell the crosshair would place into, plus the face that was clicked.</summary>
public readonly struct PlacementTarget
{
	public readonly Vector3I Cell;
	public readonly int Face;

	public PlacementTarget(Vector3I cell, int face)
	{
		Cell = cell;
		Face = face;
	}
}

/// <summary>
/// The preview of the pending placement: a grid of MeshInstance3D cells showing exactly the
/// operation volume the click will fill — one cell in fine mode, <see cref="BlockBehaviors.OperationGrid"/>
/// cubed in coarse. No collision shape, so the placement raycast can never hit it. The cells
/// share one <see cref="ChunkMesher.BuildBlock"/> mesh — the chunk path's own block mesh — so
/// the preview shows exactly what will be placed. Re-meshed only when (block, orientation) changes.
/// </summary>
public partial class PlacementGhost : Node3D
{
	private MeshInstance3D _mesh;
	private readonly MeshInstance3D[] _cells =
		new MeshInstance3D[BlockBehaviors.OperationGrid * BlockBehaviors.OperationGrid * BlockBehaviors.OperationGrid];
	private Block _meshBlock;
	private byte _meshOrientation;
	private bool _hasMesh;

	/// <summary>The first cell mesh; null until <see cref="_Ready"/> or the first update.</summary>
	public MeshInstance3D Mesh => _mesh;

	/// <summary>Local-space union AABB of the cells currently rendered, zero-size while hidden.
	/// Derived from the children, never from a stored extent, so a rendering regression (the
	/// wrong visible cell count for the mode) fails the assertion instead of being papered over.</summary>
	public Aabb VolumeBounds
	{
		get
		{
			if (!Visible) return new Aabb(Vector3.Zero, Vector3.Zero);
			Aabb bounds = default;
			bool any = false;
			foreach (var cell in _cells)
			{
				if (cell == null || !cell.Visible) continue;
				var box = new Aabb(cell.Position, Vector3.One);
				bounds = any ? bounds.Merge(box) : box;
				any = true;
			}
			return any ? bounds : new Aabb(Vector3.Zero, Vector3.Zero);
		}
	}

	public override void _Ready()
	{
		EnsureMesh();
		Visible = false;
	}

	/// <summary>Points the ghost at <paramref name="target"/> (null hides it), snaps it to the
	/// operation anchor and shows exactly the cells inside the current operation volume.</summary>
	public void Update(VoxelWorld world, in PlacementTarget? target, Block block, byte orientation)
	{
		if (target == null)
		{
			Visible = false;
			return;
		}

		EnsureMesh();
		if (!_hasMesh || _meshBlock != block || _meshOrientation != orientation)
		{
			var mesh = ChunkMesher.BuildBlock(block, orientation);
			foreach (var cell in _cells) cell.Mesh = mesh;
			_meshBlock = block;
			_meshOrientation = orientation;
			_hasMesh = true;
		}

		Position = (Vector3)BlockBehaviors.OperationAnchor(world, target.Value.Cell);
		int extent = BlockBehaviors.OperationExtent(world.Rules);
		int i = 0;
		for (int dx = 0; dx < BlockBehaviors.OperationGrid; dx++)
		for (int dy = 0; dy < BlockBehaviors.OperationGrid; dy++)
		for (int dz = 0; dz < BlockBehaviors.OperationGrid; dz++)
			_cells[i++].Visible = dx < extent && dy < extent && dz < extent;

		Visible = true;
	}

	private void EnsureMesh()
	{
		if (_mesh != null) return;
		int i = 0;
		for (int dx = 0; dx < BlockBehaviors.OperationGrid; dx++)
		for (int dy = 0; dy < BlockBehaviors.OperationGrid; dy++)
		for (int dz = 0; dz < BlockBehaviors.OperationGrid; dz++)
		{
			var cell = new MeshInstance3D
			{
				Name = $"Cell{dx}{dy}{dz}",
				Position = new Vector3(dx, dy, dz),
				MaterialOverride = VoxelWorld.GhostMaterial,
			};
			AddChild(cell);
			_cells[i++] = cell;
			_mesh ??= cell;
		}
	}
}
