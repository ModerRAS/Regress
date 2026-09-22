using Godot;

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

    // ponytail: no texture atlas, per-face baked vertex colours keep the mesher single-pass.
    // Swap in an atlas + UV pass here when textures actually matter.
    // Colours are authored in sRGB; the renderer treats vertex colours as linear.
    public static Color ColorOf(Block b, int face)
    {
        Color srgb = b switch
        {
            Block.Stone => new Color(0.52f, 0.52f, 0.55f),
            Block.Dirt => new Color(0.44f, 0.30f, 0.20f),
            Block.Grass when face == Face.Top => new Color(0.34f, 0.62f, 0.24f),
            Block.Grass when face == Face.Bottom => new Color(0.44f, 0.30f, 0.20f),
            Block.Grass => new Color(0.36f, 0.50f, 0.24f),
            Block.Sand => new Color(0.86f, 0.80f, 0.56f),
            Block.Wood => new Color(0.36f, 0.26f, 0.15f),
            Block.Plank => new Color(0.68f, 0.51f, 0.31f),
            Block.Leaves => new Color(0.20f, 0.45f, 0.17f),
            Block.Bedrock => new Color(0.16f, 0.16f, 0.18f),
            _ => Colors.Magenta,
        };
        return srgb.SrgbToLinear();
    }

    public static string NameOf(Block b) => b.ToString();
}
