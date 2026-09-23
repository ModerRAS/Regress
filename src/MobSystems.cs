using Friflo.Engine.ECS;
using Godot;

namespace Regress;

/// <summary>
/// Mob systems: Spawn (through MobSpawner), Tick (AI -> gravity -> AABB resolution) and
/// SyncVisuals. Mobs are not CharacterBody3D: a mob is a few GetBlock calls, so no body,
/// no collision shape and no MoveAndSlide query is registered in the physics space.
/// </summary>
public static class MobSystems
{
	public const int MaxMobs = 48;
	public const int MobsPerFrame = 2;
	/// <summary>Per-frame mob work cap in ms. Mutable so the selftest can lift the wall-clock
	/// throttle and compare two worlds deterministically; the game keeps the 1 ms default.</summary>
	public static double MobWorkBudgetMs = 1.0;
	public const float DespawnRadius = 224f;

	private const float Gravity = 104f;
	private const float MaxDepenetration = 32f;

	/// <summary>Create up to MobsPerFrame mobs and drop out-of-range ones. Returns ms spent.</summary>
	public static double Spawn(VoxelWorld world) => MobSpawner.Run(world);

	/// <summary>AI + gravity + AABB for every mob. With zero mobs this returns exactly 0.0:
	/// the early return happens before any timestamp is taken.</summary>
	public static double Tick(VoxelWorld world, float delta)
	{
		var query = world.Store.Query<MobXform, MobVelocity, MobAi>().AllTags(Tags.Get<Mob>());
		if (query.Count == 0) return 0.0;

		ulong started = Time.GetTicksUsec();
		int processed = 0;
		query.ForEachEntity((ref MobXform xform, ref MobVelocity velocity, ref MobAi ai, Entity entity) =>
		{
			if (processed > 0 && Prof.Since(started) >= MobWorkBudgetMs) return;
			processed++;
			Move(world, ref xform, ref velocity, ref ai, delta);
		});
		return Prof.Since(started);
	}

	/// <summary>Writes node position and yaw only; the components hold the state.</summary>
	public static double SyncVisuals(EntityStore store)
	{
		var query = store.Query<MobXform, MobVisual>().AllTags(Tags.Get<Mob>());
		if (query.Count == 0) return 0.0;

		ulong started = Time.GetTicksUsec();
		query.ForEachEntity((ref MobXform xform, ref MobVisual visual, Entity entity) =>
		{
			if (visual.Node == null) return;
			visual.Node.Position = xform.Position + new Vector3(0f, MobAiRules.Height * 0.5f, 0f);
			visual.Node.Rotation = new Vector3(0f, xform.Yaw, 0f);
		});
		return Prof.Since(started);
	}

	private static void Move(VoxelWorld world, ref MobXform xform, ref MobVelocity velocity,
		ref MobAi ai, float delta)
	{
		var move = MobAiRules.Decide(world, ref ai, xform.Position.X, xform.Position.Y, xform.Position.Z,
			world.Focus.X, world.Focus.Y, world.Focus.Z, delta);

		var v = velocity.Value;
		float speed = MobKinds.Speed(MobKind.Slime);
		v.X = move.DirX * speed;
		v.Z = move.DirZ * speed;
		v.Y -= Gravity * delta;

		var position = xform.Position;
		float nextX = position.X + v.X * delta;
		float nextZ = position.Z + v.Z * delta;
		float nextY = position.Y + v.Y * delta;

		// Horizontal: fit at the current height first, then one block higher when the AI
		// reports a step; otherwise hold position (the wall is solid).
		if (MobAiRules.MobFits(world, nextX, position.Y, nextZ))
		{
			position.X = nextX;
			position.Z = nextZ;
		}
		else if (move.StepUp && MobAiRules.MobFits(world, nextX, position.Y + 4f, nextZ))
		{
			position.X = nextX;
			position.Y += 4f;
			position.Z = nextZ;
		}
		else
		{
			v.X = 0f;
			v.Z = 0f;
		}

		// Vertical: fall to the new height, land on the first solid cell, then depenetrate
		// upward (bounded) while anything still overlaps the AABB.
		position.Y = nextY;
		if (!MobAiRules.MobFits(world, position.X, position.Y, position.Z))
		{
			if (v.Y <= 0f)
			{
				position.Y = Mathf.Floor(position.Y) + 1f;
				v.Y = 0f;
			}
			for (int i = 0; i < MaxDepenetration
				&& !MobAiRules.MobFits(world, position.X, position.Y, position.Z); i++)
			{
				position.Y += 1f;
				v.Y = 0f;
			}
		}

		if (move.DirX != 0f || move.DirZ != 0f)
			xform.Yaw = Mathf.Atan2(move.DirX, move.DirZ);

		xform.Position = position;
		velocity.Value = v;
	}
}
