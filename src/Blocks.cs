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

/// <summary>How a block is oriented when it is first placed. None means the default
/// orientation is the identity; every block can still be rotated afterwards.</summary>
public enum Placement : byte { None, Axis, Face }

/// <summary>
/// Which orientations a block TYPE is allowed to be placed in. This is a property of the type,
/// not of a voxel: the type table holds one entry per block and a voxel stores only the chosen
/// byte, so 4096 cells never repeat the same policy. <see cref="Placement"/> is a different
/// concept - it is where a placement's first orientation comes from, not what is allowed.
/// </summary>
public enum OrientationPolicy : byte { Any, Upright, Axis, Fixed }

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

    /// <summary>Placement rule per block, indexed by (int)Block. A front-facing block
    /// (furnace, chest) becomes one Placement.Face row here.</summary>
    private static readonly Placement[] PlacementRules =
    {
        Placement.None, // Air
        Placement.None, // Stone
        Placement.None, // Dirt
        Placement.None, // Grass
        Placement.None, // Sand
        Placement.Axis, // Wood: log-like, axis taken from the clicked face
        Placement.None, // Plank
        Placement.None, // Leaves
        Placement.None, // Bedrock
    };

    /// <summary>Allowed-orientation policy per block, indexed by (int)Block. Upright is used by
    /// grass, Axis is reserved for log-like blocks and Fixed for furniture.</summary>
    private static readonly OrientationPolicy[] Policies =
    {
        OrientationPolicy.Any,     // Air
        OrientationPolicy.Any,     // Stone
        OrientationPolicy.Any,     // Dirt
        OrientationPolicy.Upright, // Grass: its top face must stay up
        OrientationPolicy.Any,     // Sand
        OrientationPolicy.Any,     // Wood
        OrientationPolicy.Any,     // Plank
        OrientationPolicy.Any,     // Leaves
        OrientationPolicy.Any,     // Bedrock
    };

    // A policy is stored twice on purpose. The semantic mask has one bit per byte value 0..24
    // and mirrors bit 9 (the identity's duplicate table row) from bit 0, so Allows agrees on the
    // two identity bytes. Iteration must NOT use that mask: AllowedOrientations is the canonical
    // space {None} u ({1..24} minus byte 9) in order, which is exactly 24 values.
    private static readonly uint[] OrientationMasks = new uint[4];
    private static readonly byte[][] AllowedOrientations = new byte[4][];

    static Blocks()
    {
        var any = new byte[Orientation.Count];
        int next = 0;
        for (int value = 0; value <= Orientation.Count; value++)
            if (value != Orientation.IdentityDuplicate) any[next++] = (byte)value;
        SetAllowed(OrientationPolicy.Any, any);

        var upright = new byte[4];
        for (int roll = 0; roll < 4; roll++) upright[roll] = Orientation.Axis(Face.Top, roll);
        System.Array.Sort(upright);
        SetAllowed(OrientationPolicy.Upright, upright);

        var axis = new byte[6];
        for (int dir = 0; dir < 6; dir++) axis[dir] = Orientation.Axis(dir, 0);
        System.Array.Sort(axis);
        SetAllowed(OrientationPolicy.Axis, axis);

        SetAllowed(OrientationPolicy.Fixed, new[] { Orientation.None });
    }

    /// <summary>Records one policy: its semantic mask and its canonical ordered allowed values.</summary>
    private static void SetAllowed(OrientationPolicy policy, byte[] allowed)
    {
        uint mask = 0;
        foreach (byte orientation in allowed) mask |= 1u << orientation;
        mask |= (mask & 1u) << Orientation.IdentityDuplicate;
        OrientationMasks[(int)policy] = mask;
        AllowedOrientations[(int)policy] = allowed;
    }

    public static Placement PlacementOf(Block b) => PlacementRules[(int)b];

    public static OrientationPolicy PolicyOf(Block b) => Policies[(int)b];

    /// <summary>Semantic bit set of the allowed byte values 0..24, for <see cref="Allows"/>.</summary>
    public static uint OrientationMask(Block b) => OrientationMasks[(int)Policies[(int)b]];

    /// <summary>True when this block type may be placed in this orientation. Out-of-range
    /// bytes read as None, so callers may pass a sentinel such as 255.</summary>
    public static bool Allows(Block b, byte orientation)
    {
        if (orientation > Orientation.Count) orientation = Orientation.None;
        return (OrientationMask(b) & (1u << orientation)) != 0;
    }

    /// <summary>The rotate key's step: the next allowed value of the type's canonical space,
    /// wrapping to the lowest one. Total, and the result always satisfies <see cref="Allows"/>.</summary>
    public static byte NextAllowed(Block b, byte current)
    {
        byte[] allowed = AllowedOrientations[(int)Policies[(int)b]];
        for (int i = 0; i < allowed.Length; i++)
            if (allowed[i] > current) return allowed[i];
        return allowed[0];
    }

    /// <summary>Projects a candidate onto what the type allows. Pure, yaw-free, deterministic
    /// and total; the result always satisfies <see cref="Allows"/> and the identity comes back
    /// as None rather than its duplicate byte 9.</summary>
    public static byte Snap(Block b, byte orientation)
    {
        if (orientation > Orientation.Count || orientation == Orientation.IdentityDuplicate)
            orientation = Orientation.None;

        switch (PolicyOf(b))
        {
            case OrientationPolicy.Upright:
                // Keep an already upright candidate (the yaw-aware rule picks one), else fall back.
                return Orientation.ImageOfLocalAxis(orientation, 1) == Vector3I.Up ? orientation : Orientation.None;

            case OrientationPolicy.Axis:
                // A log lies along an axis: keep the candidate's local +Y axis and drop the roll.
                var image = Orientation.ImageOfLocalAxis(orientation, 1);
                return Orientation.Axis(BlockBehaviors.FaceOfNormal(new Vector3(image.X, image.Y, image.Z)), 0);

            case OrientationPolicy.Fixed:
                return Orientation.None;

            default:
                return orientation;
        }
    }
}
