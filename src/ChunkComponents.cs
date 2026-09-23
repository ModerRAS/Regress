using Friflo.Engine.ECS;
using Godot;

namespace Regress;

/// <summary>
/// A chunk is a <see cref="VoxelWorld.ChunkSize"/>-cube of blocks at a 3D chunk coordinate.
/// Chunks are entities; the world has no vertical limit, so chunk Y is unbounded.
/// </summary>
public struct ChunkCoord : IComponent
{
	public int X, Y, Z;

	public ChunkCoord(int x, int y, int z) { X = x; Y = y; Z = z; }

	public static ChunkCoord Of(Vector3I c) => new(c.X, c.Y, c.Z);
	public Vector3I Vector => new(X, Y, Z);

	/// <summary>Chebyshev distance in chunk space.</summary>
	public int MaxDistanceTo(ChunkCoord other)
		=> Mathf.Max(Mathf.Abs(X - other.X), Mathf.Max(Mathf.Abs(Y - other.Y), Mathf.Abs(Z - other.Z)));

	public override string ToString() => $"({X},{Y},{Z})";
}

/// <summary>Voxel payload of a chunk: two ChunkSize^3-byte arrays, index = (y * ChunkSize + z) * ChunkSize + x.
/// Every block carries an orientation; byte 0 (<see cref="Orientation.None"/>) is no rotation.</summary>
public struct ChunkBlocks : IComponent
{
	public byte[] Value;
	public byte[] Orientation;

	public static int Index(int x, int y, int z) => (y * VoxelWorld.ChunkSize + z) * VoxelWorld.ChunkSize + x;
}

/// <summary>Godot nodes backing a chunk. One mesh/body per section, created lazily on first
/// build; entries stay null until then. Both work passes wrap from <see cref="Cursor"/>, so a
/// chunk starts at the section the player is in.</summary>
public struct ChunkVisual : IComponent
{
	public const int SectionCount = VoxelWorld.SectionsPerAxis * VoxelWorld.SectionsPerAxis * VoxelWorld.SectionsPerAxis;

	public MeshInstance3D[] Meshes;
	public StaticBody3D[] Bodies;
	public CollisionShape3D[] Shapes;

	/// <summary>Where the next mesh/collision pass starts; passes walk
	/// (Cursor + k) % SectionCount so the player's section is worked first.</summary>
	public int Cursor;

	/// <summary>One bit per section, set once the section is meshed (or found meshless).
	/// Wrapping needs a done-set: a monotonic cursor cannot say "all sections were visited".</summary>
	public ulong MeshDone;

	/// <summary>One bit per section, set once its collision is decided (trimesh, box or all-air).
	/// The collision gate moves with the player, so a section skipped as out of range stays
	/// undecided here and is decided later; it cannot ride the mesh done-set.</summary>
	public ulong CollisionDone;

	// Both masks pack one bit per section, so they only work while SectionCount == 64.
	static ChunkVisual() => System.Diagnostics.Debug.Assert(SectionCount == sizeof(ulong) * 8);

	/// <summary>Section index = (sy * SectionsPerAxis + sz) * SectionsPerAxis + sx; the returned
	/// coordinates are chunk-local 0..SectionsPerAxis-1. A world section coordinate is
	/// chunk * SectionsPerAxis + local.</summary>
	public static Vector3I SectionOf(int index)
		=> new(index % VoxelWorld.SectionsPerAxis,
			index / (VoxelWorld.SectionsPerAxis * VoxelWorld.SectionsPerAxis),
			index / VoxelWorld.SectionsPerAxis % VoxelWorld.SectionsPerAxis);
}

// ---- tags: the archetype an entity sits in *is* its lifecycle state ----------

/// <summary>Present while the chunk's mesh is stale.</summary>
public struct NeedsMesh : ITag { }

/// <summary>Present while the chunk's collision shape is missing or stale. Phrasing the
/// tag as pending work keeps the query an unambiguous AllTags lookup: a "has collision"
/// tag read as the negation of AllTags matches entities that already have it.</summary>
public struct NeedsCollision : ITag { }

/// <summary>Player-built or player-edited: never auto-unloaded.</summary>
public struct KeepAlive : ITag { }
