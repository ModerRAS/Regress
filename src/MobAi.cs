using System;

namespace Regress;

/// <summary>Read-only voxel access so AI rules stay engine-free: no Godot type crosses
/// <see cref="MobAiRules.Decide"/>, and the selftest can drive the rules without a world.</summary>
public interface IBlockReader
{
	Block GetBlock(int x, int y, int z);
}

/// <summary>Plain move values. Direction is normalized (or zero); StepUp asks the adapter to
/// try the same horizontal move one block higher.</summary>
public struct MobMove
{
	public float DirX;
	public float DirZ;
	public bool StepUp;
}

/// <summary>
/// Pure, deterministic mob decision rules. Identical inputs (including the MobAi state) always
/// produce identical output; the only randomness is xorshift32 over <see cref="MobAi.Rng"/>.
/// </summary>
public static class MobAiRules
{
	public const float ReactRadius = 12f;     // approach the player inside this radius
	public const float WanderRadius = 8f;
	public const float RetargetSeconds = 2.5f;
	public const float ArriveRadius = 1.2f;

	// The mob AABB. MobKinds is the per-kind table and reads these.
	public const float HalfWidth = 0.4f;
	public const float Height = 0.8f;

	private const float Probe = 0.85f;        // HalfWidth + a step: sees the cell the mob will enter
	private const float Epsilon = 0.0001f;
	private const int TargetAttempts = 8;
	private const uint FallbackRng = 0x9E3779B9; // xorshift32 can never leave zero

	// Fixed compass order for recovery and for the axis-aligned sidestep: +X, -X, +Z, -Z.
	private static readonly float[] SideX = { 1f, -1f, 0f, 0f };
	private static readonly float[] SideZ = { 0f, 0f, 1f, -1f };

	public static MobMove Decide(IBlockReader world, ref MobAi ai, float x, float y, float z,
		float playerX, float playerY, float playerZ, float delta)
	{
		var move = default(MobMove);
		if (ai.Rng == 0) ai.Rng = FallbackRng;

		// 1. Recovery: feet or head inside a block -> first free compass direction. All four
		// blocked returns Dir = 0; the adapter then depenetrates upward.
		if (!MobFits(world, x, y, z))
		{
			for (int i = 0; i < SideX.Length; i++)
			{
				if (!MobFits(world, x + SideX[i], y, z + SideZ[i])) continue;
				move.DirX = SideX[i];
				move.DirZ = SideZ[i];
				return move;
			}
			return move;
		}

		// 2. React: horizontal distance to the player inside ReactRadius -> approach.
		bool wandering = false;
		float dx = playerX - x, dz = playerZ - z;
		float dist2 = dx * dx + dz * dz;
		if (dist2 <= ReactRadius * ReactRadius)
		{
			if (dist2 <= Epsilon) return move; // standing on the player: stand still
			float inv = 1f / MathF.Sqrt(dist2);
			move.DirX = dx * inv;
			move.DirZ = dz * inv;
		}
		else
		{
			// 3. Wander: seeded target, re-picked on arrival, on timer expiry, or when the
			// target ended up inside a block.
			wandering = true;
			ai.Retarget -= delta;
			float tdx = ai.TargetX - x, tdz = ai.TargetZ - z;
			float targetDist2 = tdx * tdx + tdz * tdz;
			if (ai.Retarget <= 0f || targetDist2 <= ArriveRadius * ArriveRadius
				|| !MobFits(world, ai.TargetX, y, ai.TargetZ))
			{
				PickTarget(world, ref ai, x, y, z);
				tdx = ai.TargetX - x;
				tdz = ai.TargetZ - z;
				targetDist2 = tdx * tdx + tdz * tdz;
			}
			if (targetDist2 > Epsilon)
			{
				float inv = 1f / MathF.Sqrt(targetDist2);
				move.DirX = tdx * inv;
				move.DirZ = tdz * inv;
			}
		}

		if (move.DirX == 0f && move.DirZ == 0f) return move;

		// 4. Never walk into a block: one-cell step over it, axis-aligned sidestep around it,
		// otherwise stop and (while wandering) pick another target.
		int feetY = Floor(y);
		int fx = Floor(x + move.DirX * Probe), fz = Floor(z + move.DirZ * Probe);
		if (!Solid(world.GetBlock(fx, feetY, fz))) return move;
		if (!Solid(world.GetBlock(fx, feetY + 1, fz)))
		{
			move.StepUp = true;
			return move;
		}

		for (int i = 0; i < SideX.Length; i++)
		{
			int sx = Floor(x + SideX[i] * Probe), sz = Floor(z + SideZ[i] * Probe);
			if (Solid(world.GetBlock(sx, feetY, sz)) || Solid(world.GetBlock(sx, feetY + 1, sz))) continue;
			move.DirX = SideX[i];
			move.DirZ = SideZ[i];
			return move;
		}

		move.DirX = 0f;
		move.DirZ = 0f;
		if (wandering) ai.Retarget = 0f; // next Decide picks a new target
		return move;
	}

	/// <summary>Mob-sized analogue of VoxelWorld.BodyFits: the AABB at this feet position
	/// clears every solid block it would occupy. Engine-free so the rules stay testable.</summary>
	public static bool MobFits(IBlockReader world, float x, float y, float z)
	{
		int x0 = Floor(x - HalfWidth), x1 = Floor(x + HalfWidth);
		int y0 = Floor(y), y1 = Floor(y + Height);
		int z0 = Floor(z - HalfWidth), z1 = Floor(z + HalfWidth);
		for (int cy = y0; cy <= y1; cy++)
			for (int cz = z0; cz <= z1; cz++)
				for (int cx = x0; cx <= x1; cx++)
					if (Solid(world.GetBlock(cx, cy, cz))) return false;
		return true;
	}

	private static void PickTarget(IBlockReader world, ref MobAi ai, float x, float y, float z)
	{
		ai.Retarget = RetargetSeconds;
		for (int attempt = 0; attempt < TargetAttempts; attempt++)
		{
			float angle = Next01(ref ai.Rng) * (2f * MathF.PI);
			float radius = MathF.Sqrt(Next01(ref ai.Rng)) * WanderRadius;
			float tx = x + MathF.Cos(angle) * radius;
			float tz = z + MathF.Sin(angle) * radius;
			if (!MobFits(world, tx, y, tz)) continue;
			ai.TargetX = tx;
			ai.TargetZ = tz;
			return;
		}
		// Every attempt landed in geometry: stand still until the next retarget tick.
		ai.TargetX = x;
		ai.TargetZ = z;
	}

	private static bool Solid(Block block) => block != Block.Air;

	private static int Floor(float value) => (int)MathF.Floor(value);

	private static uint Next(ref uint state)
	{
		uint x = state;
		x ^= x << 13;
		x ^= x >> 17;
		x ^= x << 5;
		return state = x;
	}

	private static float Next01(ref uint state) => (Next(ref state) >> 8) * (1f / 16777216f);
}
