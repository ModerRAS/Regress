using Godot;

namespace Regress;

/// <summary>One tree's shape and position: a pure function of the seed. Anything the world
/// generates as "one thing" is aligned through such a spec (pure function, zero storage)
/// instead of a per-voxel source tag; trees are the first example, boulders, veins and
/// village houses would follow the same route. StampTree, Contains and the fell collector
/// share this definition.</summary>
public readonly struct TreeSpec
{
	public readonly int AnchorX, AnchorZ; // tree anchor column
	public readonly int MinY;             // first trunk cell
	public readonly int TrunkHeight;      // 16/20/24

	public TreeSpec(int anchorX, int anchorZ, int minY, int trunkHeight)
	{
		AnchorX = anchorX;
		AnchorZ = anchorZ;
		MinY = minY;
		TrunkHeight = trunkHeight;
	}
}

/// <summary>
/// Heightmap terrain over an unbounded vertical world. The surface is a pure function of
/// the column, which is what lets the mesher decide what an unloaded chunk contains without
/// generating it.
///
/// One instance per world: the generator owns its seed and shape parameters, so a second
/// world (dimension) can have completely different terrain. Nothing here is static.
/// </summary>
public sealed class TerrainGenerator
{
	public const int MaxTrunkHeight = 24;

	/// <summary>Highest y a tree can reach above its column's surface.</summary>
	public const int MaxTreeHeight = MaxTrunkHeight + 8;

	/// <summary>Half the trunk width: the trunk spans -TrunkHalfWidth .. TrunkHalfWidth-1
	/// voxels around its anchor column.</summary>
	public const int TrunkHalfWidth = 2;

	public readonly int Seed;
	public readonly int SeaLevel;
	public readonly int HeightAmplitude;
	public readonly int BedrockY;

	private readonly FastNoiseLite _noise;

	public TerrainGenerator(int seed = 1337, float frequency = 0.002f, int seaLevel = 0,
		int heightAmplitude = 88, int bedrockY = -256)
	{
		Seed = seed;
		SeaLevel = seaLevel;
		HeightAmplitude = heightAmplitude;
		BedrockY = bedrockY;
		_noise = new FastNoiseLite { Seed = seed, Frequency = frequency, FractalOctaves = 3 };
	}

	public int HeightAt(int wx, int wz)
		=> SeaLevel + Mathf.RoundToInt(_noise.GetNoise2D(wx, wz) * HeightAmplitude);

	/// <summary>Min / max surface height over a chunk column, so streaming knows
	/// which vertical chunks can contain blocks.</summary>
	public (int Min, int Max) SurfaceRange(int cx, int cz)
	{
		int min = int.MaxValue, max = int.MinValue;
		int bx = cx * VoxelWorld.ChunkSize, bz = cz * VoxelWorld.ChunkSize;
		for (int z = 0; z < VoxelWorld.ChunkSize; z++)
		{
			for (int x = 0; x < VoxelWorld.ChunkSize; x++)
			{
				int h = HeightAt(bx + x, bz + z);
				if (h < min) min = h;
				if (h > max) max = h;
			}
		}
		return (min, max);
	}

	/// <summary>Fills the part of a chunk that is at or below the surface, then stamps trees.</summary>
	public void Fill(ChunkCoord coord, byte[] blocks)
	{
		int bx = coord.X * VoxelWorld.ChunkSize, by = coord.Y * VoxelWorld.ChunkSize, bz = coord.Z * VoxelWorld.ChunkSize;

		for (int z = 0; z < VoxelWorld.ChunkSize; z++)
		{
			for (int x = 0; x < VoxelWorld.ChunkSize; x++)
			{
				int surface = HeightAt(bx + x, bz + z);
				int top = Mathf.Min(VoxelWorld.ChunkSize - 1, surface - by);
				for (int y = 0; y <= top; y++)
				{
					int wy = by + y;
					Block b;
					if (wy <= BedrockY) b = Block.Bedrock;
					else if (wy < surface - 12) b = Block.Stone;
					else if (wy < surface) b = Block.Dirt;
					else b = surface <= SeaLevel - 16 ? Block.Sand : Block.Grass;
					blocks[ChunkBlocks.Index(x, y, z)] = (byte)b;
				}
			}
		}

		// Trees are a pure function of the world column, so stamping a 12-voxel margin lets
		// trees from neighbouring columns cross chunk borders correctly.
		for (int z = -12; z < VoxelWorld.ChunkSize + 12; z++)
		{
			for (int x = -12; x < VoxelWorld.ChunkSize + 12; x++)
			{
				if (TryGetTree(bx + x, bz + z, out var spec)) StampTree(blocks, coord, spec);
			}
		}
	}

	public int TreeTrunkHeight(int wx, int wz)
	{
		uint h = Hash(wx, wz, Seed);
		// Tree density shrinks by area x16: columns are 1/4 the old edge, so 16 columns
		// occupy one old block; 5/1616 keeps the same trees per physical area.
		if (h % 1616 >= 5) return 0;
		if (HeightAt(wx, wz) <= SeaLevel - 16) return 0;
		return 16 + (int)((h >> 8) % 3) * 4;
	}

	/// <summary>The tree anchored at this column, if any: the same TreeTrunkHeight predicate
	/// StampTree stamps from, so identity comes from the seed and never from the blocks.</summary>
	public bool TryGetTree(int wx, int wz, out TreeSpec spec)
	{
		int trunk = TreeTrunkHeight(wx, wz);
		if (trunk <= 0) { spec = default; return false; }
		spec = new TreeSpec(wx, wz, HeightAt(wx, wz) + 1, trunk);
		return true;
	}

	/// <summary>True when this world cell belongs to the spec: trunk box or canopy layer.
	/// Same CanopyRadius/CanopyCell rules StampTree stamps with, so the two cannot drift.</summary>
	public static bool Contains(in TreeSpec spec, int x, int y, int z)
	{
		int dx = x - spec.AnchorX, dz = z - spec.AnchorZ;
		if (y >= spec.MinY && y < spec.MinY + spec.TrunkHeight && InFootprint(dx) && InFootprint(dz))
			return true;

		int dy = y - (spec.MinY + spec.TrunkHeight - 1);
		if (dy < CanopyBottomDy || dy > CanopyTopDy) return false;
		return CanopyCell(dy, dx, dz);
	}

	/// <summary>The tree that owns this world cell, if any: anchor columns within one canopy
	/// radius are searched and the first spec that contains the cell wins. Fixed scan order
	/// keeps the choice deterministic when two canopies overlap. Geometry only, no provenance.
	/// Collect and tests share this one entry point.</summary>
	public bool TryGetTreeAt(int x, int y, int z, out TreeSpec spec)
	{
		for (int ax = x - CanopyMaxRadius; ax <= x + CanopyMaxRadius; ax++)
		{
			for (int az = z - CanopyMaxRadius; az <= z + CanopyMaxRadius; az++)
			{
				if (!TryGetTree(ax, az, out var candidate)) continue;
				if (!Contains(candidate, x, y, z)) continue;
				spec = candidate;
				return true;
			}
		}
		spec = default;
		return false;
	}

	/// <summary>Widest canopy layer radius; a fell box reaches this far past a tree's trunk.</summary>
	public const int CanopyMaxRadius = 8;

	private const int CanopyBottomDy = -4;
	private const int CanopyTopDy = 8;

	/// <summary>Canopy radius of one layer relative to the trunk top: narrow at the base and
	/// the tip, wide in between.</summary>
	private static int CanopyRadius(int dy) => dy <= 0 || dy >= CanopyTopDy ? 4 : CanopyMaxRadius;

	/// <summary>Trunk footprint around an anchor: -TrunkHalfWidth .. TrunkHalfWidth-1.</summary>
	private static bool InFootprint(int offset) => offset >= -TrunkHalfWidth && offset < TrunkHalfWidth;

	/// <summary>True when canopy layer dy places a leaf at (dx, dz) relative to the anchor:
	/// inside the layer radius, not on a cut corner of a wide layer, and not in the trunk
	/// hollow. The one definition of the canopy shape, shared by StampTree, Contains and
	/// MaxTreeBlocks.</summary>
	private static bool CanopyCell(int dy, int dx, int dz)
	{
		int r = CanopyRadius(dy);
		if (Mathf.Abs(dx) > r || Mathf.Abs(dz) > r) return false;
		if (r == CanopyMaxRadius && Mathf.Abs(dx) == r && Mathf.Abs(dz) == r && dy != 4) return false;
		return !(dy <= 0 && InFootprint(dx) && InFootprint(dz));
	}

	/// <summary>Upper bound on the blocks one tree stamps: a full-height trunk plus every
	/// canopy cell, summed with the same CanopyCell rules StampTree uses. EditRequests
	/// derives its per-fell bound from this.</summary>
	public static int MaxTreeBlocks()
	{
		int blocks = 2 * TrunkHalfWidth * 2 * TrunkHalfWidth * MaxTrunkHeight;
		for (int dy = CanopyBottomDy; dy <= CanopyTopDy; dy++)
		{
			int r = CanopyRadius(dy);
			for (int dz = -r; dz <= r; dz++)
			{
				for (int dx = -r; dx <= r; dx++)
				{
					if (CanopyCell(dy, dx, dz)) blocks++;
				}
			}
		}
		return blocks;
	}

	private static uint Hash(int x, int z, int seed)
	{
		unchecked
		{
			uint h = (uint)(x * 374761393 + z * 668265263 + seed * 2654435761);
			h = (h ^ (h >> 13)) * 1274126177u;
			return h ^ (h >> 16);
		}
	}

	private void StampTree(byte[] blocks, ChunkCoord coord, in TreeSpec spec)
	{
		int top = spec.MinY + spec.TrunkHeight - 1;

		for (int y = spec.MinY; y <= top; y++)
			for (int ox = -TrunkHalfWidth; ox < TrunkHalfWidth; ox++)
				for (int oz = -TrunkHalfWidth; oz < TrunkHalfWidth; oz++)
					Put(blocks, coord, spec.AnchorX + ox, y, spec.AnchorZ + oz, Block.Wood, false);

		for (int dy = CanopyBottomDy; dy <= CanopyTopDy; dy++)
		{
			int r = CanopyRadius(dy);
			int y = top + dy;
			for (int dz = -r; dz <= r; dz++)
			{
				for (int dx = -r; dx <= r; dx++)
				{
					if (!CanopyCell(dy, dx, dz)) continue;
					Put(blocks, coord, spec.AnchorX + dx, y, spec.AnchorZ + dz, Block.Leaves, true);
				}
			}
		}
	}

	private static void Put(byte[] blocks, ChunkCoord coord, int wx, int wy, int wz, Block block, bool allowLeaves)
	{
		int lx = wx - coord.X * VoxelWorld.ChunkSize;
		int ly = wy - coord.Y * VoxelWorld.ChunkSize;
		int lz = wz - coord.Z * VoxelWorld.ChunkSize;
		if (lx < 0 || lx >= VoxelWorld.ChunkSize || ly < 0 || ly >= VoxelWorld.ChunkSize
			|| lz < 0 || lz >= VoxelWorld.ChunkSize) return;

		int i = ChunkBlocks.Index(lx, ly, lz);
		var current = (Block)blocks[i];
		if (current != Block.Air && !(allowLeaves && current == Block.Leaves)) return;
		blocks[i] = (byte)block;
	}
}
