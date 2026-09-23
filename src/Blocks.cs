namespace Regress;

public enum Block : byte
{
    Air = 0,
    Stone,
    Dirt,
    Grass,
    Sand,
    Wood,
    Plank,
    Leaves,
    Bedrock,
}

// Face indices, in the order used by ChunkMesher.Dirs.
public static class Face
{
    public const int PosX = 0, NegX = 1, Top = 2, Bottom = 3, PosZ = 4, NegZ = 5;
}

public static class Blocks
{
    public static readonly Block[] Palette =
    {
        Block.Stone, Block.Dirt, Block.Grass, Block.Sand, Block.Wood, Block.Plank, Block.Leaves,
    };

    public static bool IsSolid(Block b) => b != Block.Air;

    /// <summary>Seconds to break by hand. Negative is unbreakable. Indexed by (int)Block.</summary>
    private static readonly float[] Hardness =
    {
        -1f,   // Air
        1.5f,  // Stone
        0.5f,  // Dirt
        0.6f,  // Grass
        0.5f,  // Sand
        2.0f,  // Wood
        2.0f,  // Plank
        0.2f,  // Leaves
        -1f,   // Bedrock
    };

    public static float HardnessOf(Block b) => Hardness[(int)b];

    public static bool IsBreakable(Block b) => HardnessOf(b) >= 0f;

    public static string NameOf(Block b) => b.ToString();
}
