using System.Collections.Generic;
using Friflo.Engine.ECS;
using Godot;

namespace Regress;

/// <summary>A proposed world change. Gameplay produces these; the world decides what happens.</summary>
public enum EditKind : byte { Break, Place }

public readonly struct EditRequest
{
    public readonly EditKind Kind;
    public readonly Vector3I Cell;
    public readonly Block Block;
    public readonly Entity Actor;

    private EditRequest(EditKind kind, Vector3I cell, Block block, Entity actor)
    {
        Kind = kind;
        Cell = cell;
        Block = block;
        Actor = actor;
    }

    public static EditRequest Break(Vector3I cell, Entity actor) => new(EditKind.Break, cell, Block.Air, actor);
    public static EditRequest Place(Vector3I cell, Block block, Entity actor) => new(EditKind.Place, cell, block, actor);
}

/// <summary>
/// Expands one requested break into the blocks it actually removes. Mining and felling rules
/// live here, on the world side, so they never leak into the systems that produce requests:
/// a break is not one block, and what it costs and what it takes is a property of the block,
/// not of the player.
/// </summary>
public static class BlockBehaviors
{
    public const int MaxBlocksPerBreak = 256;
    private const int FellRadius = 5;

    private static readonly Vector3I[] Directions =
    {
        new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1),
    };

    // ponytail: reused scratch buffers, so the returned list is only valid until the next call.
    [System.ThreadStatic] private static List<Vector3I> _found;
    [System.ThreadStatic] private static HashSet<Vector3I> _seen;

    /// <summary>Blocks removed by breaking <paramref name="cell"/>.</summary>
    public static List<Vector3I> Collect(VoxelWorld world, Block block, Vector3I cell)
    {
        var found = _found ??= new List<Vector3I>(64);
        var seen = _seen ??= new HashSet<Vector3I>();
        found.Clear();
        seen.Clear();
        found.Add(cell);
        seen.Add(cell);

        if (block == Block.Wood) FellTree(world, cell, found, seen);
        // Vein mining, leaf decay, falling sand: more cases here.
        return found;
    }

    /// <summary>Chop the trunk and take the canopy with it, bounded by radius and block count.</summary>
    private static void FellTree(VoxelWorld world, Vector3I origin, List<Vector3I> found, HashSet<Vector3I> seen)
    {
        var queue = new Queue<Vector3I>();
        queue.Enqueue(origin);

        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            foreach (var d in Directions)
            {
                var next = cell + d;
                if (Mathf.Abs(next.X - origin.X) > FellRadius
                    || Mathf.Abs(next.Y - origin.Y) > FellRadius
                    || Mathf.Abs(next.Z - origin.Z) > FellRadius) continue;
                if (!seen.Add(next)) continue;

                var b = world.GetBlock(next.X, next.Y, next.Z);
                if (b != Block.Wood && b != Block.Leaves) continue;

                found.Add(next);
                if (found.Count >= MaxBlocksPerBreak) return;
                if (b == Block.Wood) queue.Enqueue(next); // only wood continues the trunk
            }
        }
    }
}
