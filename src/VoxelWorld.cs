using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;
using Godot;

namespace Regress;

/// <summary>
/// Owns the ECS store and the chunk systems. A chunk is an entity whose archetype encodes
/// its lifecycle: NeedsMesh, then NeedsCollision, then neither (complete, resident).
/// Systems query those archetypes and work one section at a time under a shared millisecond
/// budget, so no frame can spin on an arbitrary number of variable-cost sections.
/// </summary>
public partial class VoxelWorld : Node3D, IBlockReader
{
	public const int ChunkSize = 64;
	public const int SectionSize = 16;
	public const int SectionsPerAxis = ChunkSize / SectionSize;

	public EntityStore Store { get; } = new();

	/// <summary>Terrain shape for this world. Replace before the first frame to get a
	/// different dimension; nothing about it is static.</summary>
	public TerrainGenerator Terrain = new();

	/// <summary>Per-world rule switches (host-authoritative in v1). Never null.</summary>
	public WorldRules Rules { get; } = new();

	public int SeaLevel => Terrain.SeaLevel;
	public int BedrockY => Terrain.BedrockY;

	public int HeightAt(int wx, int wz) => Terrain.HeightAt(wx, wz);
	public int ViewDistance = 3;          // chunks, horizontally
	public int CollisionRadius = 2;       // sections, in 3D around the player's section
	public int ChunkWorkBudgetMs = 3;
	public int ChunksPerFrame = 4;        // hard cap per frame; the 3 ms deadline usually bites first
	public Vector3 Focus;

	// Mob spawn plan cursor for the current focus cell; MobSpawner.Plan itself is pure.
	internal int MobCursor;
	internal int MobFocusX = int.MinValue;
	internal int MobFocusZ = int.MinValue;

	// Timings for --bench. Gen* counts chunks; Mesh* and Collision* count SECTIONS.
	public double GenMsTotal { get; private set; }
	public double GenMsMax { get; private set; }
	public int GenCount { get; private set; }
	public double MeshMsTotal { get; private set; }
	public double MeshMsMax { get; private set; }
	public long MeshAllocBytes { get; private set; }
	public double CollisionMsTotal { get; private set; }
	public double CollisionMsMax { get; private set; }
	public long CollisionAllocBytes { get; private set; }
	public int MeshCount { get; private set; }      // sections
	public int CollisionCount { get; private set; } // sections

	// One shared material for every chunk: the tile texture is the base colour and vertex
	// colour is tint only. TexPack.Load injects the tile array once at startup, so the
	// renderer has no textured/untextured branch (docs/texture-packs.md).
	private static readonly ShaderMaterial Material = new()
	{
		Shader = GD.Load<Shader>("res://src/voxel_tiles.gdshader"),
	};

	private static TexPack.Pack _pack;
	private static ShaderMaterial _ghostMaterial;

	/// <summary>The shared chunk material. Public so the ghost's own instance can be told apart.</summary>
	public static ShaderMaterial ChunkMaterial => Material;

	/// <summary>Injects the loaded pack into the shared chunk material: one sampler per size
	/// class (design P2.3). Absent classes bind class 0's array — nothing references them.</summary>
	public static void UseTiles(TexPack.Pack pack)
	{
		_pack = pack;
		BindClasses(Material, pack);
	}

	/// <summary>One sampler per size class, absent classes bound to class 0's array. The ghost
	/// uses the same binding, so its preview cannot sample another class's layers.</summary>
	private static void BindClasses(ShaderMaterial material, TexPack.Pack pack)
	{
		for (int c = 0; c < TexPack.MaxSizeClasses; c++)
			material.SetShaderParameter($"tiles{c}", pack.Arrays[Mathf.Min(c, pack.Classes - 1)]);
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
				if (_pack != null) BindClasses(_ghostMaterial, _pack);
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
	private Vector3I _lastPlayerSection = new(int.MinValue, int.MinValue, int.MinValue);
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

		// The collision gate is centred on the player's section, so crossing a section
		// boundary can bring sections into range that were skipped as out of range before.
		var playerSection = PlayerSection();
		if (playerSection != _lastPlayerSection)
		{
			_lastPlayerSection = playerSection;
			RetagCollisionSections(focus, playerSection);
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

	/// <summary>Generates pending chunks under the shared deadline and resumes from
	/// <see cref="_pendingCursor"/> next frame. The first chunk is always built even when the
	/// deadline is already spent: that is a progress guarantee, separate from the budget — do not
	/// remove it. <see cref="ChunksPerFrame"/> is only a hard per-frame upper bound, not the budget.</summary>
	private void RunGenerationSystem(Vector3I focus)
	{
		long deadline = (long)Time.GetTicksUsec() + ChunkWorkBudgetMs * 1000L;
		int max = ChunksPerFrame;
		while (_pendingCursor < _pending.Count && max-- > 0)
		{
			var key = _pending[_pendingCursor++];
			if (!_index.ContainsKey(key)) CreateChunk(key);
			if ((long)Time.GetTicksUsec() >= deadline) break;
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
			if (visual.Meshes != null) foreach (var node in visual.Meshes) node?.QueueFree();
			if (visual.Bodies != null) foreach (var body in visual.Bodies) body?.QueueFree();
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
		ProcessWithinBudget(_work, RebuildMesh, (long)w0 + ChunkWorkBudgetMs * 1000L);
		_meshWorkMs = Prof.Since(w0);
	}

	/// <summary>Meshes sections starting at <see cref="ChunkVisual.Cursor"/> and wrapping, until
	/// the shared deadline is spent; always does at least one. The tag stays while any section is
	/// unmeshed; only a fully meshed chunk moves to the collision phase.</summary>
	private void RebuildMesh(Entity entity, long deadline)
	{
		var coord = entity.GetComponent<ChunkCoord>();
		ref var data = ref entity.GetComponent<ChunkBlocks>();
		ref var visual = ref entity.GetComponent<ChunkVisual>();
		EnsureSectionArrays(ref visual);

		int count = ChunkVisual.SectionCount;
		int start = visual.Cursor;
		for (int k = 0; k < count; k++)
		{
			int si = (start + k) % count;
			ulong bit = 1UL << si;
			if ((visual.MeshDone & bit) != 0) continue;
			var section = ChunkVisual.SectionOf(si);

			ulong started = Time.GetTicksUsec();
			long allocated = GC.GetAllocatedBytesForCurrentThread();
			var mesh = ChunkMesher.Build(this, coord, section, data.Value, out _, data.Orientation);
			MeshAllocBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
			double meshMs = (Time.GetTicksUsec() - started) / 1000.0;
			MeshCount++;
			MeshMsTotal += meshMs;
			MeshMsMax = Mathf.Max(MeshMsMax, meshMs);

			if (mesh == null)
			{
				visual.Meshes[si]?.QueueFree();
				visual.Meshes[si] = null;
			}
			else
			{
				var node = visual.Meshes[si];
				if (node == null)
				{
					node = new MeshInstance3D
					{
						Name = $"Chunk_{coord.X}_{coord.Y}_{coord.Z}_{si}",
						MaterialOverride = Material,
					};
					AddChild(node);
					visual.Meshes[si] = node;
				}
				node.Position = SectionOrigin(coord, section);
				node.Mesh = mesh;
			}

			visual.MeshDone |= bit;
			visual.Cursor = (si + 1) % count;
			if ((long)Time.GetTicksUsec() >= deadline) break;
		}

		if (visual.MeshDone == ulong.MaxValue)
		{
			entity.RemoveTag<NeedsMesh>();
			entity.AddTag<NeedsCollision>();
		}
	}

	// ---- system 3: collision --------------------------------------------

	private void RunCollisionSystem(Vector3I focus)
	{
		_work.Clear();
		// Collision is needed only near the player, and a section is SectionSize blocks wide, so
		// the player's chunk and its neighbours cover every section within CollisionRadius.
		int reach = (CollisionRadius + SectionSize - 1) / SectionSize;
		var query = Store.Query<ChunkCoord, ChunkVisual>().AllTags(Tags.Get<NeedsCollision>());
		query.ForEachEntity((ref ChunkCoord coord, ref ChunkVisual visual, Entity entity) =>
		{
			if (Mathf.Abs(coord.X - focus.X) > reach) return;
			if (Mathf.Abs(coord.Y - focus.Y) > reach) return;
			if (Mathf.Abs(coord.Z - focus.Z) > reach) return;
			_work.Add((SquaredDistance(coord.Vector, new Vector3(Focus.X, Focus.Y, Focus.Z)), entity));
		});

		if (_work.Count == 0) { _colWorkMs = 0; return; }
		_work.Sort((a, b) => a.Distance.CompareTo(b.Distance));
		ulong w0 = Time.GetTicksUsec();
		ProcessWithinBudget(_work, UpdateCollision, (long)w0 + ChunkWorkBudgetMs * 1000L);
		_colWorkMs = Prof.Since(w0);
	}

	/// <summary>Builds collision for the sections whose centre is within CollisionRadius sections
	/// of the player's section, starting at <see cref="ChunkVisual.Cursor"/> and wrapping. Decided
	/// sections are remembered in <see cref="ChunkVisual.CollisionDone"/>; the tag is cleared once a
	/// full pass finds nothing left to decide, so a later gate move can re-queue the chunk. Always
	/// does at least one section, stops at the shared deadline.</summary>
	private void UpdateCollision(Entity entity, long deadline)
	{
		var coord = entity.GetComponent<ChunkCoord>();
		ref var data = ref entity.GetComponent<ChunkBlocks>();
		ref var visual = ref entity.GetComponent<ChunkVisual>();
		EnsureSectionArrays(ref visual);
		var player = PlayerSection();

		int count = ChunkVisual.SectionCount;
		int start = visual.Cursor;
		bool complete = true;
		for (int k = 0; k < count; k++)
		{
			int si = (start + k) % count;
			ulong bit = 1UL << si;
			if ((visual.CollisionDone & bit) != 0) continue;
			// A section still waiting for its mesh is decided once the mesh phase is done with it.
			if ((visual.MeshDone & bit) == 0) continue;
			var section = ChunkVisual.SectionOf(si);
			// SectionOf is chunk-local 0..3; the player is a world section index, so convert
			// before comparing: world section = coord * SectionsPerAxis + local.
			if (Mathf.Abs(coord.X * SectionsPerAxis + section.X - player.X) > CollisionRadius) continue;
			if (Mathf.Abs(coord.Y * SectionsPerAxis + section.Y - player.Y) > CollisionRadius) continue;
			if (Mathf.Abs(coord.Z * SectionsPerAxis + section.Z - player.Z) > CollisionRadius) continue;

			ulong started = Time.GetTicksUsec();
			long allocated = GC.GetAllocatedBytesForCurrentThread();
			BuildSectionCollision(ref visual, coord, section, si, data.Value);
			CollisionAllocBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
			visual.CollisionDone |= bit;
			visual.Cursor = (si + 1) % count;

			double collisionMs = (Time.GetTicksUsec() - started) / 1000.0;
			CollisionCount++;
			CollisionMsTotal += collisionMs;
			CollisionMsMax = Mathf.Max(CollisionMsMax, collisionMs);

			if ((long)Time.GetTicksUsec() >= deadline) { complete = false; break; }
		}

		if (complete) entity.RemoveTag<NeedsCollision>();
	}

	/// <summary>Crossing a section boundary moves the collision gate, so a chunk whose tag was
	/// already cleared may now have undecided sections inside the gate: re-tag it (never while it
	/// is still meshing — collision needs the section's mesh). Called once per section crossing.</summary>
	private void RetagCollisionSections(Vector3I focus, Vector3I playerSection)
	{
		int reach = (CollisionRadius + SectionSize - 1) / SectionSize;
		for (int dy = -reach; dy <= reach; dy++)
		{
			for (int dz = -reach; dz <= reach; dz++)
			{
				for (int dx = -reach; dx <= reach; dx++)
				{
					var chunk = focus + new Vector3I(dx, dy, dz);
					if (!_index.TryGetValue(chunk, out var entity)) continue;
					if (entity.Tags.Has<NeedsMesh>() || entity.Tags.Has<NeedsCollision>()) continue;
					bool needed = false;
					{
						ref var visual = ref entity.GetComponent<ChunkVisual>();
						for (int si = 0; si < ChunkVisual.SectionCount && !needed; si++)
						{
							if ((visual.CollisionDone & (1UL << si)) != 0) continue;
							var section = ChunkVisual.SectionOf(si); // chunk-local 0..3
							needed = Mathf.Abs(chunk.X * SectionsPerAxis + section.X - playerSection.X) <= CollisionRadius
								&& Mathf.Abs(chunk.Y * SectionsPerAxis + section.Y - playerSection.Y) <= CollisionRadius
								&& Mathf.Abs(chunk.Z * SectionsPerAxis + section.Z - playerSection.Z) <= CollisionRadius;
						}
					}
					if (needed) entity.AddTag<NeedsCollision>();
				}
			}
		}
	}

	/// <summary>One section's collision: the section's own mesh as a trimesh, a box when the
	/// section is solid but meshless (buried), and no body at all when it is all air.</summary>
	private void BuildSectionCollision(ref ChunkVisual visual, ChunkCoord coord, Vector3I section, int si, byte[] blocks)
	{
		var mesh = visual.Meshes[si]?.Mesh;
		Shape3D shape;
		Vector3 shapeOffset;
		if (mesh != null)
		{
			shape = mesh.CreateTrimeshShape();
			shapeOffset = Vector3.Zero;
		}
		else if (HasSolidBlocks(blocks, section.X, section.Y, section.Z))
		{
			shape = new BoxShape3D { Size = Vector3.One * SectionSize };
			shapeOffset = Vector3.One * (SectionSize * 0.5f);
		}
		else
		{
			// all air: drop any stale body left from before an edit
			visual.Bodies[si]?.QueueFree();
			visual.Bodies[si] = null;
			visual.Shapes[si] = null;
			return;
		}

		if (visual.Bodies[si] == null)
		{
			visual.Bodies[si] = new StaticBody3D { Name = $"Body_{coord.X}_{coord.Y}_{coord.Z}_{si}" };
			visual.Shapes[si] = new CollisionShape3D();
			visual.Bodies[si].AddChild(visual.Shapes[si]);
			AddChild(visual.Bodies[si]);
		}
		visual.Bodies[si].Position = SectionOrigin(coord, section);
		visual.Shapes[si].Position = shapeOffset;
		visual.Shapes[si].Shape = shape;
	}

	/// <summary>True if this section's cells contain any non-air block.</summary>
	private static bool HasSolidBlocks(byte[] blocks, int sx, int sy, int sz)
	{
		int x0 = sx * SectionSize, y0 = sy * SectionSize, z0 = sz * SectionSize;
		for (int y = y0; y < y0 + SectionSize; y++)
			for (int z = z0; z < z0 + SectionSize; z++)
				for (int x = x0; x < x0 + SectionSize; x++)
					if (blocks[ChunkBlocks.Index(x, y, z)] != (byte)Block.Air) return true;
		return false;
	}

	/// <summary>The player's WORLD section coordinate, the centre of the collision gate.
	/// Compare with <see cref="ChunkVisual.SectionOf"/> only after
	/// chunk * SectionsPerAxis + local.</summary>
	private Vector3I PlayerSection() => new(
		FloorDiv(Mathf.FloorToInt(Focus.X), SectionSize),
		FloorDiv(Mathf.FloorToInt(Focus.Y), SectionSize),
		FloorDiv(Mathf.FloorToInt(Focus.Z), SectionSize));

	/// <summary>World origin of one section's cube.</summary>
	private static Vector3 SectionOrigin(ChunkCoord coord, Vector3I section) => new(
		coord.X * ChunkSize + section.X * SectionSize,
		coord.Y * ChunkSize + section.Y * SectionSize,
		coord.Z * ChunkSize + section.Z * SectionSize);

	/// <summary>The section index of a world position inside a chunk, clamped to the chunk.</summary>
	private static int SectionIndexAt(Vector3 position, ChunkCoord coord)
	{
		int sx = Mathf.Clamp(FloorDiv(Mathf.FloorToInt(position.X) - coord.X * ChunkSize, SectionSize), 0, SectionsPerAxis - 1);
		int sy = Mathf.Clamp(FloorDiv(Mathf.FloorToInt(position.Y) - coord.Y * ChunkSize, SectionSize), 0, SectionsPerAxis - 1);
		int sz = Mathf.Clamp(FloorDiv(Mathf.FloorToInt(position.Z) - coord.Z * ChunkSize, SectionSize), 0, SectionsPerAxis - 1);
		return (sy * SectionsPerAxis + sz) * SectionsPerAxis + sx;
	}

	private static void EnsureSectionArrays(ref ChunkVisual visual)
	{
		visual.Meshes ??= new MeshInstance3D[ChunkVisual.SectionCount];
		visual.Bodies ??= new StaticBody3D[ChunkVisual.SectionCount];
		visual.Shapes ??= new CollisionShape3D[ChunkVisual.SectionCount];
	}

	/// <summary>Runs work items until the shared millisecond deadline is spent, always doing at
	/// least one. The deadline is computed once per system so one slow chunk cannot extend it.</summary>
	private void ProcessWithinBudget(List<(float Distance, Entity Entity)> items, Action<Entity, long> work, long deadline)
	{
		for (int i = 0; i < items.Count; i++)
		{
			if (i > 0 && (long)Time.GetTicksUsec() >= deadline) return;
			work(items[i].Entity, deadline);
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

		// One break is not one block: the block type decides what it takes with it, and the
		// operation volume spreads the break. Air and Bedrock inside it are skipped, not removed.
		var cells = BlockBehaviors.Collect(this, block, request.Cell);
		bool removed = false;
		for (int i = 0; i < cells.Count; i++)
		{
			var cell = cells[i];
			if (!Blocks.IsBreakable(GetBlock(cell.X, cell.Y, cell.Z))) continue;
			if (SetBlock(cell.X, cell.Y, cell.Z, Block.Air)) removed = true;
		}
		return removed;
	}

	private bool ApplyPlace(in EditRequest request)
	{
		var anchor = BlockBehaviors.OperationAnchor(this, request.Cell);
		int extent = BlockBehaviors.OperationExtent(Rules);
		bool placed = false;
		for (int dy = 0; dy < extent; dy++)
			for (int dz = 0; dz < extent; dz++)
				for (int dx = 0; dx < extent; dx++)
				{
					int x = anchor.X + dx, y = anchor.Y + dy, z = anchor.Z + dz;
					if (GetBlock(x, y, z) != Block.Air) continue; // never overwrite a non-air cell
					if (SetBlock(x, y, z, request.Block, request.Orientation)) placed = true;
				}
		return placed;
	}

	public Entity CreateChunk(Vector3I key)
	{
		if (_index.TryGetValue(key, out var existing)) return existing;

		ulong started = Time.GetTicksUsec();
		var coord = ChunkCoord.Of(key);
		var blocks = new byte[ChunkSize * ChunkSize * ChunkSize];
		Terrain.Fill(coord, blocks);
		// Terrain never rotates anything, so every generated cell starts at None.
		var orientations = new byte[ChunkSize * ChunkSize * ChunkSize];
		double genMs = (Time.GetTicksUsec() - started) / 1000.0;
		GenCount++;
		GenMsTotal += genMs;
		GenMsMax = Mathf.Max(GenMsMax, genMs);

		var entity = Store.CreateEntity(coord, new ChunkBlocks { Value = blocks, Orientation = orientations },
			default(ChunkVisual), Tags.Get<NeedsMesh>());
		_index[key] = entity;
		// The section nearest the player is meshed first: that is what the player looks at.
		entity.GetComponent<ChunkVisual>().Cursor = SectionIndexAt(Focus, coord);

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
		ref var visual = ref entity.GetComponent<ChunkVisual>();
		visual.Cursor = SectionIndexAt(Focus, ChunkCoord.Of(key)); // start near the player
		visual.MeshDone = 0;
		visual.CollisionDone = 0;
		entity.AddTag<NeedsMesh>();
		entity.RemoveTag<NeedsCollision>();
	}

	/// <summary>Generates the chunks a spawn or respawn needs, but builds only the chunk the
	/// player is standing in, landing section first, under the normal frame budget: the rest
	/// streams in. ponytail: radius is in 64^3 chunks now, so radius 1 covers 192x192 units
	/// (16x the old 48x48 area) — each extra step is 16x the area.</summary>
	public void EnsureAreaAround(Vector3 position, int radius)
	{
		// The collision gate must be centred on the landing point, not on wherever the player was.
		Focus = position;
		int cx = FloorDiv(Mathf.FloorToInt(position.X), ChunkSize);
		int cz = FloorDiv(Mathf.FloorToInt(position.Z), ChunkSize);

		// radius 0 forces only the landing chunk (landing section first); everything else
		// streams under the 3 ms budget. The full column is 2-4 chunks x ~1.97 ms measured, so
		// creating it here is a deterministic 4-8 ms teleport hitch. radius > 0 keeps its old
		// meaning: every chunk around the landing point exists and is complete.
		if (radius > 0)
		{
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
		}

		// position.Y is the surface block's y by caller convention (FindSpawn/TeleportToSurface);
		// range.Max + 1 could put the forced chunk above the surface (range.Max on a chunk
		// boundary, or the player's column a chunk below the column max) and drop the player.
		var ground = ChunkKeyOf(Mathf.FloorToInt(position.X), Mathf.FloorToInt(position.Y), Mathf.FloorToInt(position.Z));
		var entity = CreateChunk(ground);
		// Build the landing chunk under the normal budget, landing section first: the spawn must
		// stand immediately, but a teleport must not freeze the frame meshing 64 sections.
		int landing = SectionIndexAt(position, entity.GetComponent<ChunkCoord>());
		entity.GetComponent<ChunkVisual>().Cursor = landing;
		long deadline = (long)Time.GetTicksUsec() + ChunkWorkBudgetMs * 1000L;
		if (entity.Tags.Has<NeedsMesh>()) RebuildMesh(entity, deadline);
		entity.GetComponent<ChunkVisual>().Cursor = landing;
		UpdateCollision(entity, deadline);
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
