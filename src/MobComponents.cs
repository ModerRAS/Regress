using Friflo.Engine.ECS;
using Godot;

namespace Regress;

/// <summary>Mob species id. <see cref="MobHealth"/> is deliberately omitted: nothing damages
/// mobs yet, so a health field would be a stub. Add it with the damage path.</summary>
public enum MobKind : byte
{
    Slime = 0,
}

/// <summary>Per-kind size and speed. HalfWidth/Height are the mob AABB that MobFits tests;
/// one row per kind, referenced by the movement system and the spawner.</summary>
public static class MobKinds
{
    public static float HalfWidth(MobKind kind) => MobAiRules.HalfWidth;
    public static float Height(MobKind kind) => MobAiRules.Height;
    public static float Speed(MobKind kind) => 3.0f;
}

/// <summary>Feet-centre position + yaw. The visual node is offset to the AABB centre.</summary>
public struct MobXform : IComponent
{
    public Vector3 Position;
    public float Yaw;
}

/// <summary>World-space velocity, written by the movement system each tick.</summary>
public struct MobVelocity : IComponent
{
    public Vector3 Value;
}

/// <summary>AI scratch state. Rng is the per-mob xorshift32 stream; the same state and inputs
/// always produce the same decision.</summary>
public struct MobAi : IComponent
{
    public uint Rng;
    public float TargetX;
    public float TargetZ;
    public float Retarget;
}

/// <summary>View handle owned by the mob systems, never state (the ChunkVisual pattern).</summary>
public struct MobVisual : IComponent
{
    public MeshInstance3D Node;
}

/// <summary>Entity marker for all mobs: the one archetype.</summary>
public struct Mob : ITag
{
}
