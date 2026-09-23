using System.Collections.Generic;
using Friflo.Engine.ECS;
using Godot;

namespace Regress;

/// <summary>World cell of a block entity. The entity carries its own position so a system can
/// check the byte under it and the registry can be audited without a reverse lookup.</summary>
public struct BlockPos : IComponent
{
    public int X, Y, Z;
    public BlockPos(int x, int y, int z) { X = x; Y = y; Z = z; }
    public static BlockPos Of(Vector3I cell) => new(cell.X, cell.Y, cell.Z);
    public Vector3I Vector => new(X, Y, Z);
    public override string ToString() => $"({X},{Y},{Z})";
}

/// <summary>What a right click does to an interactive block. The component on the entity is the
/// dispatch key: a new kind adds one case, not a branch in the caller. None = not interactive.</summary>
public enum InteractKind : byte { None, Toggle }

/// <summary>Marks a block entity as interactive and names its interaction.</summary>
public struct Interactable : IComponent
{
    public InteractKind Kind;
    public Interactable(InteractKind kind) { Kind = kind; }
}

/// <summary>Per-instance state of a Toggle interactive block. Lives on the entity, never in the
/// 4096-byte chunk arrays: instance state is per block, not per cell byte.</summary>
public struct ToggleState : IComponent
{
    public bool Open;
}

/// <summary>Block type -> interaction kind. One comparison, nobody allocates.</summary>
public static class BlockInteractions
{
    public static InteractKind KindOf(Block b) => b == Block.Chest ? InteractKind.Toggle : InteractKind.None;

    public static bool IsInteractable(Block b) => KindOf(b) != InteractKind.None;

    /// <summary>Last interaction result, for the HUD. null until something was used.</summary>
    public static string Message { get; private set; }

    /// <summary>Bumped on every interaction, so the HUD can poll instead of being called.</summary>
    public static int Version { get; private set; }

    /// <summary>RMB on a cell: true when a block entity was there and it handled the click.
    /// Dispatch is by component, so a new kind is a new component + a new case here.</summary>
    public static bool Interact(VoxelWorld world, Vector3I cell, Entity actor)
    {
        // actor is unused for Toggle; it stays because later kinds (Phase B) need the clicker.
        if (!world.BlockEntities.TryGet(cell, out var entity)) return false;
        if (entity.HasComponent<ToggleState>())
        {
            ref var state = ref entity.GetComponent<ToggleState>();
            state.Open = !state.Open;
            Message = $"{Blocks.NameOf(world.GetBlock(cell.X, cell.Y, cell.Z))}: {(state.Open ? "open" : "closed")}";
            Version++;
            return true;
        }
        return false;
    }

    /// <summary>Clears the HUD line and the counter. For tests and for a fresh run.</summary>
    public static void Reset() { Message = null; Version = 0; }
}

/// <summary>Cell -> entity index for interactive blocks: chunk key -> (cell -> entity). Two
/// dictionary probes, never a scan, and the chunk bucket is what makes dropping a whole chunk
/// cheap. Entries exist only for interactive blocks, so a world with none pays nothing.</summary>
public sealed class BlockEntityRegistry
{
    private readonly Dictionary<Vector3I, Dictionary<Vector3I, Entity>> _buckets = new();

    public int Count { get; private set; }
    public int ChunkBucketCount { get; private set; }

    /// <summary>Dictionary probes performed so far. Two per lookup (bucket + cell); a linear
    /// scan would increment it once per visited entry, so a test can prove lookup is O(1).</summary>
    public int Probes { get; private set; }

    public bool TryGet(Vector3I cell, out Entity entity)
    {
        Probes++;
        if (!_buckets.TryGetValue(VoxelWorld.ChunkKeyOf(cell.X, cell.Y, cell.Z), out var bucket))
        {
            entity = default;
            return false;
        }
        Probes++;
        return bucket.TryGetValue(cell, out entity);
    }

    public bool Contains(Vector3I cell) => TryGet(cell, out _);

    public void Visit(System.Action<Vector3I, Entity> visit)
    {
        foreach (var bucket in _buckets.Values)
            foreach (var entry in bucket)
                visit(entry.Key, entry.Value);
    }

    /// <summary>Reconciles the registry with one byte write. Called from VoxelWorld.SetBlock,
    /// the single place a cell byte changes, so the byte and the entity set cannot drift:
    /// a non-interactive block destroys any entity here, an interactive one creates it.</summary>
    public void Sync(EntityStore store, Vector3I cell, Block block)
    {
        var kind = BlockInteractions.KindOf(block);
        if (kind == InteractKind.None) { Destroy(store, cell); return; }
        if (TryGet(cell, out _)) return;

        Entity entity;
        switch (kind)
        {
            case InteractKind.Toggle:
                entity = store.CreateEntity(new BlockPos(cell.X, cell.Y, cell.Z), new Interactable(kind), new ToggleState());
                break;
            default:
                return; // unknown kind: no state component to attach, so no entity
        }

        var key = VoxelWorld.ChunkKeyOf(cell.X, cell.Y, cell.Z);
        if (!_buckets.TryGetValue(key, out var bucket))
        {
            bucket = new Dictionary<Vector3I, Entity>();
            _buckets[key] = bucket;
            ChunkBucketCount++;
        }
        bucket[cell] = entity;
        Count++;
    }

    /// <summary>Drops every block entity of one chunk, for chunk unload.</summary>
    public void DropChunk(EntityStore store, Vector3I chunkKey)
    {
        if (!_buckets.TryGetValue(chunkKey, out var bucket)) return;
        foreach (var entity in bucket.Values) entity.DeleteEntity();
        Count -= bucket.Count;
        _buckets.Remove(chunkKey);
        ChunkBucketCount--;
    }

    /// <summary>Drops everything (tests, world teardown).</summary>
    public void Clear(EntityStore store)
    {
        foreach (var bucket in _buckets.Values)
            foreach (var entity in bucket.Values)
                entity.DeleteEntity();
        _buckets.Clear();
        Count = 0;
        ChunkBucketCount = 0;
    }

    /// <summary>Two-way invariant audit over the given chunks: cells whose byte is interactive
    /// but have no entity, and entities whose byte is not an interactive block. Both counters
    /// are 0 when the byte array and the registry agree.</summary>
    public void Audit(VoxelWorld world, IReadOnlyList<Vector3I> chunkKeys,
                      out int bytesWithoutEntity, out int entitiesWithoutByte)
    {
        bytesWithoutEntity = 0;
        entitiesWithoutByte = 0;

        foreach (var chunkKey in chunkKeys)
        {
            var origin = chunkKey * VoxelWorld.ChunkSize;
            for (var x = 0; x < VoxelWorld.ChunkSize; x++)
            for (var y = 0; y < VoxelWorld.ChunkSize; y++)
            for (var z = 0; z < VoxelWorld.ChunkSize; z++)
            {
                var cell = origin + new Vector3I(x, y, z);
                if (BlockInteractions.IsInteractable(world.GetBlock(cell.X, cell.Y, cell.Z)) && !Contains(cell))
                    bytesWithoutEntity++;
            }
        }

        foreach (var chunkKey in chunkKeys)
        {
            if (!_buckets.TryGetValue(chunkKey, out var bucket)) continue;
            foreach (var cell in bucket.Keys)
                if (!BlockInteractions.IsInteractable(world.GetBlock(cell.X, cell.Y, cell.Z)))
                    entitiesWithoutByte++;
        }
    }

    private void Destroy(EntityStore store, Vector3I cell)
    {
        if (!TryGet(cell, out var entity)) return;
        var key = VoxelWorld.ChunkKeyOf(cell.X, cell.Y, cell.Z);
        if (_buckets.TryGetValue(key, out var bucket))
        {
            bucket.Remove(cell);
            if (bucket.Count == 0) { _buckets.Remove(key); ChunkBucketCount--; }
        }
        Count--;
        entity.DeleteEntity();
    }
}
