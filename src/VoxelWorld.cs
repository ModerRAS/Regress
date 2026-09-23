using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;
using Godot;

namespace Regress;

/// <summary>
/// Owns the ECS store and the chunk systems. A chunk is an entity whose archetype encodes
/// its lifecycle: NeedsMesh, then NeedsCollision, then neither (complete, resident).
/// Systems query those archetypes and are budgeted in milliseconds so no frame can spin
/// on an arbitrary number of variable-cost chunks.
/// </summary>
public partial class VoxelWorld : Node3D, IBlockReader
{
    public const int ChunkSize = 16;

    public EntityStore Store { get; } = new();

    /// <summary>Terrain shape for this world. Replace before the first frame to get a
    /// different dimension; nothing about it is static.</summary>
    public TerrainGenerator Terrain = new();

    public int SeaLevel => Terrain.SeaLevel;
    public int BedrockY => Terrain.BedrockY;

    public int HeightAt(int wx, int wz) => Terrain.HeightAt(wx, wz);
    public int ViewDistance = 5;          // chunks, horizontally
    public int CollisionRadius = 2;       // chunks, in 3D around the player
    public int ChunkWorkBudgetMs = 3;
    public int ChunksPerFrame = 4;
    public Vector3 Focus;

    // Mob spawn plan cursor for the current focus cell; MobSpawner.Plan itself is pure.
    internal int MobCursor;
    internal int MobFocusX = int.MinValue;
    internal int MobFocusZ = int.MinValue;

    // Timings for --bench.
    public double GenMsTotal { get; private set; }
    public double GenMsMax { get; private set; }
    public int GenCount { get; private set; }
    public double MeshMsTotal { get; private set; }
    public double MeshMsMax { get; private set; }
    public long MeshAllocBytes { get; private set; }
    public double CollisionMsTotal { get; private set; }
    public double CollisionMsMax { get; private set; }
    public long CollisionAllocBytes { get; private set; }
    public int MeshCount { get; private set; }
    public int CollisionCount { get; private set; }

    // One shared material for every chunk: the tile texture is the base colour and vertex
    // colour is tint only. TexPack.Load injects the tile array once at startup, so the
    // renderer has no textured/untextured branch (docs/texture-packs.md).
    private static readonly ShaderMaterial Material = new()
    {
        Shader = GD.Load<Shader>("res://src/voxel_tiles.gdshader"),
    };

    private static Texture2DArray _tiles;
    private static ShaderMaterial _ghostMaterial;

    /// <summary>The shared chunk material. Public so the ghost's own instance can be told apart.</summary>
    public static ShaderMaterial ChunkMaterial => Material;

    /// <summary>Injects the loaded tile array into the shared chunk material.</summary>
    public static void UseTiles(Texture2DArray tiles)
    {
        _tiles = tiles;
        Material.SetShaderParameter("tiles", tiles);
    }

    /// <summary>Translucent copy of the chunk tiles for the placement preview. Created lazily,
    /// so a run that never shows a ghost never pays for it. Deliberately a different shader:
    /// the chunk shader writes ALPHA_SCISSOR_THRESHOLD, which forces the opaque alpha-tested
    /// pipeline and ignores ALPHA.</summary>
    public static ShaderMaterial GhostMaterial
    {
        get
        {
            if (_ghostMaterial == null)
            {
                _ghostMaterial = new ShaderMaterial
                {
                    Shader = GD.Load<Shader>("res://src/ghost_tiles.gdshader"),
                };
                if (_tiles != null) _ghostMaterial.SetShaderParameter("tiles", _tiles);
            }
            return _ghostMaterial;
        }
    }

    /// <summary>Chunk coordinate -> entity. ECS has no spatial index, so the world keeps one.</summary>
    private readonly Dictionary<Vector3I, Entity> _index = new();

    /// <summary>Interactive blocks: cell -> entity. The byte arrays stay authoritative; this index keeps the entity side in step with them.</summary>
    public BlockEntityRegistry BlockEntities { get; } = new();

    /// <summary>Per chunk-column surface range, computed once (~256 noise samples).</summary>
    private readonly Dictionary<Vector2I, (int Min, int Max)> _columns = new();

    private readonly List<(float Distance, Entity Entity)> _work = new();
    private readonly List<Entity> _unload = new();
    private readonly List<Vector3I> _pending = new();

    private Vector3I _lastFocus = new(int.MinValue, int.MinValue, int.MinValue);
    private int _pendingCursor;
    private double _meshWorkMs;
    private double _colWorkMs;

    public int StreamPlans { get; private set; }
    public int UnloadScans { get; private set; }

    public int LoadedChunks => _index.Count;

    // ---- frame schedule -------------------------------------------------

    public override void _Process(double delta)
    {
        var focus = ChunkKeyOf(Mathf.FloorToInt(Focus.X), Mathf.FloorToInt(Focus.Y), Mathf.FloorToInt(Focus.Z));

        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--frozen") >= 0) return;
        Prof.Frames++;
        ulong e0 = Time.GetTicksUsec();
        ApplyPendingEdits();
        Prof.EditApply += Prof.Since(e0);
        if (focus != _lastFocus)
        {
            ulong p0 = Time.GetTicksUsec();
            _lastFocus = focus;
            PlanStreaming(focus);
            UnloadOutside(focus);
            Prof.Plan += Prof.Since(p0);
        }

        ulong t0 = Time.GetTicksUsec();
        RunGenerationSystem(focus);
        Prof.Gen += Prof.Since(t0);

        ulong t1 = Time.GetTicksUsec();
        RunMeshSystem(focus);
        double meshTotal = Prof.Since(t1);
        Prof.MeshQuery += meshTotal - _meshWorkMs;
        Prof.MeshWork += _meshWorkMs;

        ulong t2 = Time.GetTicksUsec();
        RunCollisionSystem(focus);
        double colTotal = Prof.Since(t2);
        Prof.ColQuery += colTotal - _colWorkMs;
        Prof.ColWork += _colWorkMs;

        Prof.Mobs += MobSystems.Spawn(this)
            + MobSystems.Tick(this, (float)delta)
            + MobSystems.SyncVisuals(Store);
    }

    // ---- system 1: streaming --------------------------------------------

    private void PlanStreaming(Vector3I focus)
    {
        StreamPlans++;
        _pending.Clear();
        for (int dz = -ViewDistance; dz <= ViewDistance; dz++)
        {
            for (int dx = -ViewDistance; dx <= ViewDistance; dx++)
            {
                int cx = focus.X + dx, cz = focus.Z + dz;
                var range = ColumnSurfaces(cx, cz);
                // Only chunks that can contain blocks: from the deepest surface to the
                // tallest tree top. Buried chunks are skipped entirely - the mesher treats
                // them as solid, and nothing below the surface is ever visible.
                int firstY = FloorDiv(range.Min, ChunkSize);
                int lastY = FloorDiv(range.Max + TerrainGenerator.MaxTreeHeight, ChunkSize);
                for (int cy = firstY; cy <= lastY; cy++)
                    _pending.Add(new Vector3I(cx, cy, cz));
            }
        }

        // The player can dig down out of the surface band, so keep a small bubble of
        // chunks around their own Y resident as well.
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
                for (int cy = focus.Y - 2; cy <= focus.Y + 2; cy++)
                    _pending.Add(new Vector3I(focus.X + dx, cy, focus.Z + dz));

        var origin = new Vector3(Focus.X, Focus.Y, Focus.Z);
        _pending.Sort((a, b) => SquaredDistance(a, origin).CompareTo(SquaredDistance(b, origin)));
        _pendingCursor = 0;
    }

    private void RunGenerationSystem(Vector3I focus)
    {
        int budget = ChunksPerFrame;
        while (budget-- > 0 && _pendingCursor < _pending.Count)
        {
            var key = _pending[_pendingCursor++];
            if (!_index.ContainsKey(key)) CreateChunk(key);
        }
        if (_pendingCursor >= _pending.Count) _pending.Clear();
    }

    private void UnloadOutside(Vector3I focus)
    {
        UnloadScans++;
        _unload.Clear();
        var query = Store.Query<ChunkCoord>();
        query.ForEachEntity((ref ChunkCoord coord, Entity entity) =>
        {
            if (entity.Tags.Has<KeepAlive>()) return;
            bool inRange = Mathf.Abs(coord.X - focus.X) <= ViewDistance
                && Mathf.Abs(coord.Z - focus.Z) <= ViewDistance;
            if (!inRange) _unload.Add(entity);
        });

        foreach (var entity in _unload)
        {
            var coord = entity.GetComponent<ChunkCoord>();
            _index.Remove(coord.Vector);
            ref var visual = ref entity.GetComponent<ChunkVisual>();
            visual.Mesh?.QueueFree();
            visual.Body?.QueueFree();
            BlockEntities.DropChunk(Store, coord.Vector); // unloading takes its block entities with it
            entity.DeleteEntity();
        }
    }

    private (int Min, int Max) ColumnSurfaces(int cx, int cz)
    {
        var key = new Vector2I(cx, cz);
        if (_columns.TryGetValue(key, out var cached)) return cached;
        var range = Terrain.SurfaceRange(cx, cz);
        _columns[key] = range;
        return range;
    }

    // ---- system 2: mesh -------------------------------------------------

    private void RunMeshSystem(Vector3I focus)
    {
        _work.Clear();
        var query = Store.Query<ChunkCoord, ChunkVisual>().AllTags(Tags.Get<NeedsMesh>());
        query.ForEachEntity((ref ChunkCoord coord, ref ChunkVisual visual, Entity entity) =>
            _work.Add((SquaredDistance(coord.Vector, new Vector3(Focus.X, Focus.Y, Focus.Z)), entity)));

        if (_work.Count == 0) { _meshWorkMs = 0; return; }
        _work.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        ulong w0 = Time.GetTicksUsec();
        ProcessWithinBudget(_work, RebuildMesh);
        _meshWorkMs = Prof.Since(w0);
    }

    private void RebuildMesh(Entity entity)
    {
        entity.RemoveTag<NeedsMesh>();
        entity.AddTag<NeedsCollision>();

        var coord = entity.GetComponent<ChunkCoord>();
        var blocks = entity.GetComponent<ChunkBlocks>().Value;

        ulong started = Time.GetTicksUsec();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var mesh = ChunkMesher.Build(this, coord, blocks, out _);
        MeshAllocBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
        double meshMs = (Time.GetTicksUsec() - started) / 1000.0;
        MeshCount++;
        MeshMsTotal += meshMs;
        MeshMsMax = Mathf.Max(MeshMsMax, meshMs);

        ref var visual = ref entity.GetComponent<ChunkVisual>();
        if (mesh == null)
        {
            visual.Mesh?.QueueFree();
            visual.Body?.QueueFree();
            visual.Mesh = null;
            visual.Body = null;
            visual.Shape = null;
            return;
        }

        if (visual.Mesh == null)
        {
            visual.Mesh = new MeshInstance3D
            {
                Name = $"Chunk_{coord.X}_{coord.Y}_{coord.Z}",
                MaterialOverride = Material,
                Position = new Vector3(coord.X * ChunkSize, coord.Y * ChunkSize, coord.Z * ChunkSize),
            };
            AddChild(visual.Mesh);
        }
        visual.Mesh.Mesh = mesh;
    }

    // ---- system 3: collision --------------------------------------------

    private void RunCollisionSystem(Vector3I focus)
    {
        _work.Clear();
        var query = Store.Query<ChunkCoord, ChunkVisual>().AllTags(Tags.Get<NeedsCollision>());
        query.ForEachEntity((ref ChunkCoord coord, ref ChunkVisual visual, Entity entity) =>
        {
            // Collision shapes are the expensive half of chunk work and are only needed
            // near the player, so distant chunks render without one.
            if (Mathf.Abs(coord.X - focus.X) > CollisionRadius) return;
            if (Mathf.Abs(coord.Y - focus.Y) > CollisionRadius) return;
            if (Mathf.Abs(coord.Z - focus.Z) > CollisionRadius) return;
            _work.Add((SquaredDistance(coord.Vector, new Vector3(Focus.X, Focus.Y, Focus.Z)), entity));
        });

        if (_work.Count == 0) { _colWorkMs = 0; return; }
        _work.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        ulong w0 = Time.GetTicksUsec();
        ProcessWithinBudget(_work, UpdateCollision);
        _colWorkMs = Prof.Since(w0);
    }

    private void UpdateCollision(Entity entity)
    {
        entity.RemoveTag<NeedsCollision>();
        ref var visual = ref entity.GetComponent<ChunkVisual>();
        var mesh = visual.Mesh?.Mesh;

        // An empty mesh means the chunk is all air or all solid: if every face of every
        // block is hidden there is nothing to render, but a fully buried chunk still has
        // to stop a player digging down through it.
        Shape3D shape;
        Vector3 shapeOffset;
        if (mesh != null)
        {
            shape = mesh.CreateTrimeshShape();
            shapeOffset = Vector3.Zero;
        }
        else if (HasSolidBlocks(entity.GetComponent<ChunkBlocks>().Value))
        {
            shape = new BoxShape3D { Size = Vector3.One * ChunkSize };
            shapeOffset = Vector3.One * (ChunkSize * 0.5f);
        }
        else
        {
            return; // all air
        }

        var coord = entity.GetComponent<ChunkCoord>();
        ulong started = Time.GetTicksUsec();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        if (visual.Body == null)
        {
            visual.Body = new StaticBody3D { Name = $"Body_{coord.X}_{coord.Y}_{coord.Z}" };
            visual.Shape = new CollisionShape3D();
            visual.Body.AddChild(visual.Shape);
            AddChild(visual.Body);
        }
        visual.Body.Position = new Vector3(coord.X * ChunkSize, coord.Y * ChunkSize, coord.Z * ChunkSize);
        visual.Shape.Position = shapeOffset;
        visual.Shape.Shape = shape;
        CollisionAllocBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;

        double collisionMs = (Time.GetTicksUsec() - started) / 1000.0;
        CollisionCount++;
        CollisionMsTotal += collisionMs;
        CollisionMsMax = Mathf.Max(CollisionMsMax, collisionMs);
    }

    private static bool HasSolidBlocks(byte[] blocks)
    {
        for (int i = 0; i < blocks.Length; i++)
            if (blocks[i] != (byte)Block.Air) return true;
        return false;
    }

    /// <summary>Runs work items until the millisecond budget is spent, always doing at least one.</summary>
    private void ProcessWithinBudget(List<(float Distance, Entity Entity)> items, Action<Entity> work)
    {
        ulong started = Time.GetTicksUsec();
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0 && (Time.GetTicksUsec() - started) / 1000.0 >= ChunkWorkBudgetMs) return;
            work(items[i].Entity);
        }
    }

    // ---- chunk lifecycle ------------------------------------------------

    // ---- system 0: block edits ------------------------------------------

    private readonly List<EditRequest> _edits = new();

    /// <summary>Gameplay proposes a change; the world decides what it does. Requests are
    /// applied at the top of the next frame, before meshing, so a break shows up immediately.</summary>
    public void RequestEdit(in EditRequest request) => _edits.Add(request);

    /// <summary>Applies queued edits and returns how many took effect. Public so headless
    /// tests can run the system without a frame.</summary>
    public int ApplyPendingEdits()
    {
        if (_edits.Count == 0) return 0;

        int applied = 0;
        foreach (var request in _edits)
        {
            bool ok = request.Kind == EditKind.Break ? ApplyBreak(request) : ApplyPlace(request);
            if (ok) applied++;
        }
        _edits.Clear();
        return applied;
    }

    private bool ApplyBreak(in EditRequest request)
    {
        var block = GetBlock(request.Cell.X, request.Cell.Y, request.Cell.Z);
        if (!Blocks.IsBreakable(block)) return false;

        // One break is not one block: the block type decides what it takes with it.
        var cells = BlockBehaviors.Collect(this, block, request.Cell);
        bool removed = false;
        for (int i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (SetBlock(cell.X, cell.Y, cell.Z, Block.Air)) removed = true;
        }
        return removed;
    }

    private bool ApplyPlace(in EditRequest request)
    {
        var cell = request.Cell;
        if (GetBlock(cell.X, cell.Y, cell.Z) != Block.Air) return false;
        return SetBlock(cell.X, cell.Y, cell.Z, request.Block, request.Orientation);
    }

    public Entity CreateChunk(Vector3I key)
    {
        if (_index.TryGetValue(key, out var existing)) return existing;

        ulong started = Time.GetTicksUsec();
        var coord = ChunkCoord.Of(key);
        var blocks = new byte[ChunkMesher.Volume];
        Terrain.Fill(coord, blocks);
        // Terrain never rotates anything, so every generated cell starts at None.
        var orientations = new byte[ChunkMesher.Volume];
        double genMs = (Time.GetTicksUsec() - started) / 1000.0;
        GenCount++;
        GenMsTotal += genMs;
        GenMsMax = Mathf.Max(GenMsMax, genMs);

        var entity = Store.CreateEntity(coord, new ChunkBlocks { Value = blocks, Orientation = orientations },
            default(ChunkVisual), Tags.Get<NeedsMesh>());
        _index[key] = entity;

        // Neighbours were meshed against "unloaded chunk" and must be rebuilt.
        for (int axis = 0; axis < 3; axis++)
        {
            for (int sign = -1; sign <= 1; sign += 2)
            {
                var n = key;
                if (axis == 0) n.X += sign;
                else if (axis == 1) n.Y += sign;
                else n.Z += sign;
                MarkDirty(n);
            }
        }
        return entity;
    }

    private void MarkDirty(Vector3I key)
    {
        if (!_index.TryGetValue(key, out var entity)) return;
        entity.AddTag<NeedsMesh>();
        entity.RemoveTag<NeedsCollision>();
    }

    /// <summary>Generates the chunks a spawn or respawn needs, but meshes only the one the
    /// player is standing in so the teleport never costs more than a single chunk.</summary>
    public void EnsureAreaAround(Vector3 position, int radius)
    {
        int cx = FloorDiv(Mathf.FloorToInt(position.X), ChunkSize);
        int cz = FloorDiv(Mathf.FloorToInt(position.Z), ChunkSize);
        var range = ColumnSurfaces(cx, cz);

        for (int dz = -radius; dz <= radius; dz++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                var neighbour = ColumnSurfaces(cx + dx, cz + dz);
                int firstY = FloorDiv(neighbour.Min, ChunkSize);
                int lastY = FloorDiv(neighbour.Max + TerrainGenerator.MaxTreeHeight, ChunkSize);
                for (int cy = firstY; cy <= lastY; cy++) CreateChunk(new Vector3I(cx + dx, cy, cz + dz));
            }
        }

        var ground = ChunkKeyOf(Mathf.FloorToInt(position.X), range.Max + 1, Mathf.FloorToInt(position.Z));
        var entity = CreateChunk(ground);
        if (entity.Tags.Has<NeedsMesh>()) RebuildMesh(entity);
        UpdateCollision(entity);
    }

    // ---- block access ---------------------------------------------------

    public static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

    public static Vector3I ChunkKeyOf(int x, int y, int z)
        => new(FloorDiv(x, ChunkSize), FloorDiv(y, ChunkSize), FloorDiv(z, ChunkSize));

    public bool TryGetChunk(int x, int y, int z, out Entity entity)
        => _index.TryGetValue(ChunkKeyOf(x, y, z), out entity);

    public Block GetBlock(int x, int y, int z)
    {
        var key = ChunkKeyOf(x, y, z);
        if (_index.TryGetValue(key, out var entity))
        {
            var blocks = entity.GetComponent<ChunkBlocks>().Value;
            return (Block)blocks[ChunkBlocks.Index(x - key.X * ChunkSize, y - key.Y * ChunkSize, z - key.Z * ChunkSize)];
        }

        // No chunk here. Terrain is a heightmap, so above the surface is air (nothing to
        // hide) and below it is solid (hides the streaming frontier). This is also what
        // lets buried chunks never be generated at all.
        if (y <= BedrockY) return Block.Bedrock;
        return y > Terrain.HeightAt(x, z) ? Block.Air : Block.Stone;
    }

    /// <summary>Rotation stored in a cell. An unloaded chunk has no cells and reads as None.</summary>
    public byte GetOrientation(int x, int y, int z)
    {
        var key = ChunkKeyOf(x, y, z);
        if (_index.TryGetValue(key, out var entity))
        {
            var orientation = entity.GetComponent<ChunkBlocks>().Orientation;
            return orientation[ChunkBlocks.Index(x - key.X * ChunkSize, y - key.Y * ChunkSize, z - key.Z * ChunkSize)];
        }
        return Orientation.None;
    }

    /// <summary>Writes one cell. A write that changes neither block nor orientation is a no-op,
    /// but re-placing the same block with a different orientation still applies.</summary>
    public bool SetBlock(int x, int y, int z, Block block, byte orientation = Orientation.None)
    {
        var key = ChunkKeyOf(x, y, z);
        if (!_index.TryGetValue(key, out var entity))
        {
            // Clearing air is a no-op, but digging into an unloaded buried chunk must
            // work: the block there is stone and the chunk gets created on demand.
            if (block == Block.Air && y > Terrain.HeightAt(x, z)) return false;
            entity = CreateChunk(key);
        }

        ref var data = ref entity.GetComponent<ChunkBlocks>();
        int index = ChunkBlocks.Index(x - key.X * ChunkSize, y - key.Y * ChunkSize, z - key.Z * ChunkSize);
        // ponytail: same block + same orientation is a true no-op (nothing to sync either).
        if (data.Value[index] == (byte)block && data.Orientation[index] == orientation) return false;

        data.Value[index] = (byte)block;
        data.Orientation[index] = orientation;
        // The one choke point where a cell byte changes, so byte and entity cannot drift.
        BlockEntities.Sync(Store, new Vector3I(x, y, z), block);
        entity.AddTag<KeepAlive>(); // player edits are never auto-unloaded
        MarkDirty(key);

        if (x - key.X * ChunkSize == 0) MarkDirty(key + new Vector3I(-1, 0, 0));
        if (x - key.X * ChunkSize == ChunkSize - 1) MarkDirty(key + new Vector3I(1, 0, 0));
        if (y - key.Y * ChunkSize == 0) MarkDirty(key + new Vector3I(0, -1, 0));
        if (y - key.Y * ChunkSize == ChunkSize - 1) MarkDirty(key + new Vector3I(0, 1, 0));
        if (z - key.Z * ChunkSize == 0) MarkDirty(key + new Vector3I(0, 0, -1));
        if (z - key.Z * ChunkSize == ChunkSize - 1) MarkDirty(key + new Vector3I(0, 0, 1));
        return true;
    }

    /// <summary>
    /// Spawn point. The only hard requirement is that the player's body is not inside solid
    /// geometry — standing face to face with a trunk is fine. So this tests the body box
    /// directly and moves to the next column when one does not fit, instead of guessing from
    /// terrain shape (flatness, tree clearance) which are preferences, not correctness.
    /// </summary>
    public Vector3 FindSpawn(int searchRadius = 4)
    {
        for (int r = 0; r <= searchRadius; r++)
        {
            for (int dz = -r; dz <= r; dz++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != r) continue;
                    var candidate = new Vector3(dx + 0.5f, SurfaceY(dx, dz), dz + 0.5f);
                    if (BodyFits(candidate)) return candidate;
                }
            }
        }

        // Nothing nearby fits: climb the origin column until the body does.
        var fallback = new Vector3(0.5f, SurfaceY(0, 0), 0.5f);
        for (int i = 0; i < 64 && !BodyFits(fallback); i++) fallback.Y += 1f;
        return fallback;
    }

    /// <summary>True if the player's collision box at this feet position clears every solid
    /// block it would occupy. Capsule is radius 0.35, height 1.8, origin at the feet.</summary>
    public bool BodyFits(Vector3 feet)
    {
        int x0 = Mathf.FloorToInt(feet.X - 0.35f), x1 = Mathf.FloorToInt(feet.X + 0.35f);
        int y0 = Mathf.FloorToInt(feet.Y), y1 = Mathf.FloorToInt(feet.Y + 1.8f);
        int z0 = Mathf.FloorToInt(feet.Z - 0.35f), z1 = Mathf.FloorToInt(feet.Z + 0.35f);

        for (int y = y0; y <= y1; y++)
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                    if (Blocks.IsSolid(GetBlock(x, y, z))) return false;
        return true;
    }

    /// <summary>Highest y to stand on in a column: the ground, or a tree canopy, or the
    /// player's own build. There is no world ceiling to scan down from.</summary>
    public int SurfaceY(int wx, int wz)
    {
        int top = SeaLevel + Terrain.HeightAmplitude + TerrainGenerator.MaxTreeHeight + 2;
        for (int y = top; y >= BedrockY; y--)
            if (Blocks.IsSolid(GetBlock(wx, y, wz))) return y + 1;
        return SeaLevel + 1;
    }

    private static float SquaredDistance(Vector3I chunk, Vector3 point)
    {
        float dx = chunk.X * ChunkSize + ChunkSize * 0.5f - point.X;
        float dy = chunk.Y * ChunkSize + ChunkSize * 0.5f - point.Y;
        float dz = chunk.Z * ChunkSize + ChunkSize * 0.5f - point.Z;
        return dx * dx + dy * dy + dz * dz;
    }
}
