using Friflo.Engine.ECS;
using Godot;

namespace Regress;

/// <summary>
/// A chunk is a 16 x 16 x 16 cube of blocks at a 3D chunk coordinate.
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

/// <summary>Voxel payload of a chunk: two 4096-byte arrays, index = (y * 16 + z) * 16 + x.
/// Every block carries an orientation; byte 0 (<see cref="Orientation.None"/>) is no rotation.</summary>
public struct ChunkBlocks : IComponent
{
    public byte[] Value;
    public byte[] Orientation;

    public static int Index(int x, int y, int z) => (y * 16 + z) * 16 + x;
}

/// <summary>Godot nodes backing a chunk. Nodes are created lazily on first mesh.</summary>
public struct ChunkVisual : IComponent
{
    public MeshInstance3D Mesh;
    public StaticBody3D Body;
    public CollisionShape3D Shape;
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
