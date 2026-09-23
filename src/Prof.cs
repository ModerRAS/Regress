using Godot;

namespace Regress;

/// <summary>
/// Per-frame accumulators for the --bench breakdown. Each entry is milliseconds of wall
/// clock spent in one system; whatever is left over after summing them is the engine's own
/// render + physics + main loop cost.
/// </summary>
public static class Prof
{
    public static double Plan, Gen, MeshQuery, MeshWork, ColQuery, ColWork, Poll, Look, Edit, EditApply, Move, Mobs;
    public static int Frames;

    public static double Since(ulong startTicks) => (Time.GetTicksUsec() - startTicks) / 1000.0;

    public static double[] Snapshot() =>
        new[] { Plan, Gen, MeshQuery, MeshWork, ColQuery, ColWork, Poll, Look, Edit, EditApply, Move, Mobs };

    public static void Reset()
    {
        Plan = Gen = MeshQuery = MeshWork = ColQuery = ColWork = Poll = Look = Edit = EditApply = Move = Mobs = 0;
        Frames = 0;
    }
}
