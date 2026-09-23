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
/// The preview of the pending placement: one Node3D with one MeshInstance3D and no collision
/// shape, so the placement raycast can never hit it. The mesh is
/// <see cref="ChunkMesher.BuildBlock"/> — the chunk path's own block mesh — so the preview
/// shows exactly what will be placed. Re-meshed only when (block, orientation) changes.
/// </summary>
public partial class PlacementGhost : Node3D
{
	private MeshInstance3D _mesh;
	private Block _meshBlock;
	private byte _meshOrientation;
	private bool _hasMesh;

	/// <summary>The single mesh instance; null until <see cref="_Ready"/> or the first update.</summary>
	public MeshInstance3D Mesh => _mesh;

	public override void _Ready()
	{
		EnsureMesh();
		Visible = false;
	}

	/// <summary>Points the ghost at <paramref name="target"/> (null hides it) and makes sure
	/// the mesh is the one for (block, orientation).</summary>
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
			_mesh.Mesh = ChunkMesher.BuildBlock(block, orientation);
			_meshBlock = block;
			_meshOrientation = orientation;
			_hasMesh = true;
		}

		Position = target.Value.Cell;
		Visible = true;
	}

	private void EnsureMesh()
	{
		if (_mesh != null) return;
		_mesh = new MeshInstance3D { Name = "Mesh", MaterialOverride = VoxelWorld.GhostMaterial };
		AddChild(_mesh);
	}
}
