using Godot;

namespace Regress;

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
    public const int MaxTrunkHeight = 6;

    /// <summary>Highest y a tree can reach above its column's surface.</summary>
    public const int MaxTreeHeight = MaxTrunkHeight + 2;

    public readonly int Seed;
    public readonly int SeaLevel;
    public readonly int HeightAmplitude;
    public readonly int BedrockY;

    private readonly FastNoiseLite _noise;

    public TerrainGenerator(int seed = 1337, float frequency = 0.008f, int seaLevel = 0,
        int heightAmplitude = 22, int bedrockY = -64)
    {
        Seed = seed;
        SeaLevel = seaLevel;
        HeightAmplitude = heightAmplitude;
        BedrockY = bedrockY;
        _noise = new FastNoiseLite { Seed = seed, Frequency = frequency, FractalOctaves = 3 };
    }

    public int HeightAt(int wx, int wz)
        => SeaLevel + Mathf.RoundToInt(_noise.GetNoise2D(wx, wz) * HeightAmplitude);

    /// <summary>Min / max surface height over a 16x16 chunk column, so streaming knows
    /// which vertical chunks can contain blocks.</summary>
    public (int Min, int Max) SurfaceRange(int cx, int cz)
    {
        int min = int.MaxValue, max = int.MinValue;
        int bx = cx * 16, bz = cz * 16;
        for (int z = 0; z < 16; z++)
        {
            for (int x = 0; x < 16; x++)
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
        int bx = coord.X * 16, by = coord.Y * 16, bz = coord.Z * 16;

        for (int z = 0; z < 16; z++)
        {
            for (int x = 0; x < 16; x++)
            {
                int surface = HeightAt(bx + x, bz + z);
                int top = Mathf.Min(15, surface - by);
                for (int y = 0; y <= top; y++)
                {
                    int wy = by + y;
                    Block b;
                    if (wy <= BedrockY) b = Block.Bedrock;
                    else if (wy < surface - 3) b = Block.Stone;
                    else if (wy < surface) b = Block.Dirt;
                    else b = surface <= SeaLevel - 4 ? Block.Sand : Block.Grass;
                    blocks[ChunkBlocks.Index(x, y, z)] = (byte)b;
                }
            }
        }

        // Trees are a pure function of the world column, so stamping a 3-block margin lets
        // trees from neighbouring columns cross chunk borders correctly.
        for (int z = -3; z < 19; z++)
        {
            for (int x = -3; x < 19; x++)
            {
                int trunk = TreeTrunkHeight(bx + x, bz + z);
                if (trunk > 0) StampTree(blocks, coord, bx + x, bz + z, trunk);
            }
        }
    }

    public int TreeTrunkHeight(int wx, int wz)
    {
        uint h = Hash(wx, wz, Seed);
        if (h % 101 >= 5) return 0;
        if (HeightAt(wx, wz) <= SeaLevel - 4) return 0;
        return 4 + (int)((h >> 8) % 3);
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

    private void StampTree(byte[] blocks, ChunkCoord coord, int wx, int wz, int trunkHeight)
    {
        int baseY = HeightAt(wx, wz) + 1;
        int top = baseY + trunkHeight - 1;

        for (int y = baseY; y <= top; y++) Put(blocks, coord, wx, y, wz, Block.Wood, false);

        for (int dy = -1; dy <= 2; dy++)
        {
            int r = dy <= 0 || dy >= 2 ? 1 : 2;
            int y = top + dy;
            for (int dz = -r; dz <= r; dz++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx == 0 && dz == 0 && dy <= 0) continue;
                    if (r == 2 && Mathf.Abs(dx) == 2 && Mathf.Abs(dz) == 2 && dy != 1) continue;
                    Put(blocks, coord, wx + dx, y, wz + dz, Block.Leaves, true);
                }
            }
        }
    }

    private static void Put(byte[] blocks, ChunkCoord coord, int wx, int wy, int wz, Block block, bool allowLeaves)
    {
        int lx = wx - coord.X * 16;
        int ly = wy - coord.Y * 16;
        int lz = wz - coord.Z * 16;
        if (lx < 0 || lx > 15 || ly < 0 || ly > 15 || lz < 0 || lz > 15) return;

        int i = ChunkBlocks.Index(lx, ly, lz);
        var current = (Block)blocks[i];
        if (current != Block.Air && !(allowLeaves && current == Block.Leaves)) return;
        blocks[i] = (byte)block;
    }
}
