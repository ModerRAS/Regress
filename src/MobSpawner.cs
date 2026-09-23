using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;
using Godot;

namespace Regress;

/// <summary>
/// Deterministic spawn candidates and the mob entity factory. The plan depends only on
/// (world seed, focus cell), never on time or on a process-random hash, so the same world
/// always grows the same mobs in the same order.
/// </summary>
public static class MobSpawner
{
	/// <summary>Square ring around the focus cell, outside the mob reaction radius.</summary>
	public const float SpawnRadius = 96f;
	public const float SpawnRingWidth = 32f;
	public const float SpawnJitter = 1.2f;

	private const int SelectedPerEight = 8; // hash(seed, cell) % 8 == 0 selects a cell

	// Collect-then-delete scratch, reused; never carries state between frames.
	private static readonly List<Entity> Doomed = new();

	private static BoxMesh _mesh;
	private static StandardMaterial3D _material;

	private static BoxMesh Box => _mesh ??= new BoxMesh { Size = Vector3.One * (MobAiRules.HalfWidth * 2f) };

	private static StandardMaterial3D SlimeMaterial => _material ??= new StandardMaterial3D
	{
		// The world has no directional light (sky ambient only) and the chunk shader is
		// unshaded, so a lit material would go dark. Flat slime green matches that look.
		ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		AlbedoColor = new Color(0.36f, 0.79f, 0.31f),
	};

	/// <summary>Candidate cells of the spawn ring, nearest to the focus first. Pure: no engine
	/// state and no time, so calling it twice returns the same list.</summary>
	public static List<(int X, int Z)> Plan(int seed, int focusX, int focusZ)
	{
		int inner = (int)SpawnRadius;
		int outer = inner + (int)SpawnRingWidth;
		var cells = new List<(int X, int Z)>();
		for (int dz = -outer; dz <= outer; dz++)
		{
			for (int dx = -outer; dx <= outer; dx++)
			{
				int ring = Math.Max(Math.Abs(dx), Math.Abs(dz));
				if (ring < inner || ring > outer) continue;
				int x = focusX + dx, z = focusZ + dz;
				if (CellHash(seed, x, z) % SelectedPerEight != 0) continue;
				cells.Add((x, z));
			}
		}
		cells.Sort((a, b) =>
		{
			int da = (a.X - focusX) * (a.X - focusX) + (a.Z - focusZ) * (a.Z - focusZ);
			int db = (b.X - focusX) * (b.X - focusX) + (b.Z - focusZ) * (b.Z - focusZ);
			return da.CompareTo(db);
		});
		return cells;
	}

	/// <summary>Hand-mixed per-cell hash: identical across processes. Deliberately not
	/// string.GetHashCode or System.Random, which are seeded per process.</summary>
	public static uint CellHash(int seed, int x, int z)
	{
		unchecked
		{
			uint h = (uint)seed + 0x9E3779B9u;
			h ^= (uint)x * 0x85EBCA6Bu;
			h = (h << 13) | (h >> 19);
			h ^= (uint)z * 0xC2B2AE35u;
			h ^= h >> 16;
			h *= 0x7FEB352Du;
			return h ^ (h >> 15);
		}
	}

	/// <summary>Despawn out-of-range mobs, then create up to MobsPerFrame near the focus.
	/// Returns the milliseconds spent; no work means no timestamp is taken.</summary>
	public static double Run(VoxelWorld world)
	{
		ulong started = Time.GetTicksUsec();
		Despawn(world);
		int count = Count(world.Store);
		if (count >= MobSystems.MaxMobs) return Prof.Since(started);

		int focusX = Mathf.FloorToInt(world.Focus.X);
		int focusZ = Mathf.FloorToInt(world.Focus.Z);
		if (focusX != world.MobFocusX || focusZ != world.MobFocusZ)
		{
			world.MobFocusX = focusX;
			world.MobFocusZ = focusZ;
			world.MobCursor = 0;
		}

		var plan = Plan(world.Terrain.Seed, focusX, focusZ);
		int spawned = 0;
		int scanned = 0;
		while (spawned < MobSystems.MobsPerFrame
			&& world.MobCursor < plan.Count
			&& scanned < plan.Count)
		{
			var cell = plan[world.MobCursor];
			scanned++;
			var outcome = TrySpawn(world, cell.X, cell.Z);
			if (outcome == SpawnOutcome.Retry) break; // surface chunk not resident: same cell next frame
			world.MobCursor++;                         // decided: spawned, or blocked geometry
			if (outcome == SpawnOutcome.Spawned) spawned++;
		}
		return Prof.Since(started);
	}

	/// <summary>Mob entities in the store. Public so the selftest can count the population.</summary>
	public static int Count(EntityStore store)
		=> store.Query<MobXform>().AllTags(Tags.Get<Mob>()).Count;

	/// <summary>Frees the node before deleting the entity; deleting while iterating the query
	/// is not allowed, so the doomed entities are collected first.</summary>
	private static void Despawn(VoxelWorld world)
	{
		if (Count(world.Store) == 0) return;
		Doomed.Clear();
		var query = world.Store.Query<MobXform, MobVisual>().AllTags(Tags.Get<Mob>());
		query.ForEachEntity((ref MobXform xform, ref MobVisual visual, Entity entity) =>
		{
			float dx = xform.Position.X - world.Focus.X;
			float dz = xform.Position.Z - world.Focus.Z;
			float far = dx * dx + dz * dz - MobSystems.DespawnRadius * MobSystems.DespawnRadius;
			if (far > 0f || xform.Position.Y < world.BedrockY - 64f) Doomed.Add(entity);
		});
		for (int i = 0; i < Doomed.Count; i++)
		{
			if (Doomed[i].IsNull) continue;
			Doomed[i].GetComponent<MobVisual>().Node?.QueueFree();
			Doomed[i].DeleteEntity();
		}
	}

	private enum SpawnOutcome { Spawned, Retry, Skip }

	private static SpawnOutcome TrySpawn(VoxelWorld world, int cellX, int cellZ)
	{
		int y = world.HeightAt(cellX, cellZ) + 1;
		// Only spawn where the surface chunk is real data: an unloaded chunk would hide trees.
		if (!world.TryGetChunk(cellX, y, cellZ, out _)) return SpawnOutcome.Retry;

		uint hash = CellHash(world.Terrain.Seed, cellX, cellZ);
		float x = cellX + Jitter(hash, 8);
		float z = cellZ + Jitter(hash, 16);
		if (!MobAiRules.MobFits(world, x, y, z)) return SpawnOutcome.Skip;
		if (NearMob(world, x, y, z)) return SpawnOutcome.Skip;

		var node = new MeshInstance3D
		{
			Name = $"Mob_{cellX}_{cellZ}",
			Mesh = Box,
			MaterialOverride = SlimeMaterial,
		};
		world.AddChild(node);
		world.Store.CreateEntity(
			new MobXform { Position = new Vector3(x, y, z), Yaw = 0f },
			new MobVelocity(),
			new MobAi { Rng = hash, TargetX = x, TargetZ = z, Retarget = 0f },
			new MobVisual { Node = node },
			Tags.Get<Mob>());
		return SpawnOutcome.Spawned;
	}

	/// <summary>Jitter in ±SpawnJitter from one byte of the cell hash.</summary>
	private static float Jitter(uint hash, int shift)
		=> ((hash >> shift) & 0xFF) * (2f * SpawnJitter / 255f) - SpawnJitter;

	/// <summary>True when a mob already stands within a body length: never stack two mobs on
	/// one candidate (the same cell can be revisited after a re-plan).</summary>
	private static bool NearMob(VoxelWorld world, float x, float y, float z)
	{
		bool near = false;
		var query = world.Store.Query<MobXform>().AllTags(Tags.Get<Mob>());
		query.ForEachEntity((ref MobXform xform, Entity entity) =>
		{
			if (near) return;
			float dx = xform.Position.X - x;
			float dy = xform.Position.Y - y;
			float dz = xform.Position.Z - z;
			if (dx * dx + dy * dy + dz * dz < 6f * 6f) near = true;
		});
		return near;
	}
}
