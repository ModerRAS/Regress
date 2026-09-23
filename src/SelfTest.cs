using System;
using System.Collections.Generic;
using System.IO;
using Friflo.Engine.ECS;
using Godot;

namespace Regress;

/// <summary>Headless logic checks. Run with: godot --headless -- --selftest</summary>
public static class SelfTest
{
    private static int _failures;

    /// <summary>Highest Block value, derived so a new enum tail can never be silently skipped
    /// by the all-block sweeps below.</summary>
    private static readonly int LastBlockIndex = (int)Enum.GetValues<Block>()[^1];

    public static int Run(VoxelWorld world)
    {
        _failures = 0;
        GD.Print("Regress selftest");

        // ---- terrain ----------------------------------------------------
        Check(world.LoadedChunks > 0, $"spawn area generated ({world.LoadedChunks} chunks)");

        int surface = world.HeightAt(0, 0);
        Check(surface is > -30 and < 30, $"height in range ({surface})");
        Check(world.HeightAt(3, 7) == world.HeightAt(3, 7), "height function is deterministic");
        Check(world.GetBlock(0, surface, 0) is Block.Grass or Block.Sand, $"surface block ({world.GetBlock(0, surface, 0)})");
        Check(world.GetBlock(0, surface - 6, 0) == Block.Stone, "stone under the surface");
        Check(world.GetBlock(0, surface + 40, 0) == Block.Air, "air high above the surface");
        Check(world.GetBlock(0, world.BedrockY, 0) == Block.Bedrock, "bedrock at the bottom");
        Check(!Blocks.IsBreakable(Block.Bedrock), "bedrock is unbreakable");

        int min = int.MaxValue, max = int.MinValue;
        double sum = 0;
        int samples = 0;
        for (int z = -64; z < 64; z += 2)
        {
            for (int x = -64; x < 64; x += 2)
            {
                int h = world.HeightAt(x, z);
                min = Mathf.Min(min, h);
                max = Mathf.Max(max, h);
                sum += h;
                samples++;
            }
        }
        GD.Print($"  info  height min={min} max={max} avg={sum / samples:F1}");
        Check(max - min >= 6, $"terrain has relief ({max - min} blocks)");
        Check(min < 0 && max > 0, $"terrain spans negative and positive Y ({min}..{max})");

        int wood = 0;
        for (int z = -16; z < 32; z++)
            for (int x = -16; x < 32; x++)
                for (int y = -20; y < 40; y++)
                    if (world.GetBlock(x, y, z) == Block.Wood) wood++;
        Check(wood > 0, $"trees generated ({wood} wood blocks in the spawn chunks)");

        // ---- mesher -----------------------------------------------------
        var isolated = world.CreateChunk(new Vector3I(50, 50, 50));
        Array.Clear(isolated.GetComponent<ChunkBlocks>().Value);
        int before = CountIndices(world, isolated);
        var isolatedBlocks = isolated.GetComponent<ChunkBlocks>().Value;
        isolatedBlocks[ChunkBlocks.Index(8, 8, 8)] = (byte)Block.Stone;
        int after = CountIndices(world, isolated);
        Check(before == 0, $"isolated chunk starts empty ({before} indices)");
        Check(after == 36, $"single block makes 6 faces ({after} indices)");

        var terrainEntity = world.CreateChunk(VoxelWorld.ChunkKeyOf(0, surface, 0));
        int terrainIndices = CountIndices(world, terrainEntity);
        Check(terrainIndices > 36, $"terrain chunk has geometry ({terrainIndices / 6} faces)");
        Check(terrainIndices == ExpectedIndices(world, terrainEntity),
            $"mesher matches brute force ({terrainIndices / 6} vs {ExpectedIndices(world, terrainEntity) / 6} faces)");

        // Slab high above the terrain, where every neighbour reads as air:
        // 256 top + 256 bottom + 4 * 16 * 11 sides = 1216 faces.
        var slab = world.CreateChunk(new Vector3I(70, 20, 70));
        var slabBlocks = slab.GetComponent<ChunkBlocks>().Value;
        Array.Clear(slabBlocks);
        for (int y = 0; y <= 10; y++)
            for (int z = 0; z < 16; z++)
                for (int x = 0; x < 16; x++) slabBlocks[ChunkBlocks.Index(x, y, z)] = (byte)Block.Stone;
        int slabIndices = CountIndices(world, slab);
        Check(slabIndices == 1216 * 6, $"solid slab has all faces ({slabIndices / 6} vs 1216)");

        var slabMesh = ChunkMesher.Build(world, slab.GetComponent<ChunkCoord>(), slabBlocks, out _);
        var arrays = slabMesh.SurfaceGetArrays(0);
        var verts = (Vector3[])arrays[(int)Mesh.ArrayType.Vertex];
        var norms = (Vector3[])arrays[(int)Mesh.ArrayType.Normal];
        var idx = (int[])arrays[(int)Mesh.ArrayType.Index];

        int topVerts = 0;
        for (int i = 0; i < verts.Length; i++)
            if (Mathf.IsEqualApprox(verts[i].Y, 11f) && norms[i].Y > 0.99f) topVerts++;
        Check(topVerts == 256 * 4, $"slab top layer is fully covered ({topVerts / 4} quads)");

        int badWinding = 0;
        for (int t = 0; t < idx.Length; t += 3)
        {
            var a = verts[idx[t]];
            var winding = (verts[idx[t + 1]] - a).Cross(verts[idx[t + 2]] - a);
            if (winding.Normalized().Dot(norms[idx[t]]) > -0.5f) badWinding++;
        }
        Check(badWinding == 0, $"all {idx.Length / 3} slab triangles wind clockwise for Godot ({badWinding} wrong)");

        var terrainMesh = ChunkMesher.Build(world, terrainEntity.GetComponent<ChunkCoord>(),
            terrainEntity.GetComponent<ChunkBlocks>().Value, out _);
        Check(WindingErrors(terrainMesh) == 0, "terrain triangles wind clockwise too");

        // ---- unbounded vertical ----------------------------------------
        var sky = new Vector3I(5, 3000, 5);
        Check(world.SetBlock(sky.X, sky.Y, sky.Z, Block.Plank), "place a block 3000 above the world");
        Check(world.GetBlock(sky.X, sky.Y, sky.Z) == Block.Plank, "read it back");
        Check(world.GetBlock(sky.X, sky.Y + 1, sky.Z) == Block.Air, "sky above it is still air");
        Check(world.TryGetChunk(sky.X, sky.Y, sky.Z, out var skyChunk)
            && skyChunk.Tags.Has<KeepAlive>(), "player-built chunk is pinned against unloading");

        var deep = new Vector3I(-3, -40, 7);
        Check(!world.TryGetChunk(deep.X, deep.Y, deep.Z, out _), "buried chunks are never generated");
        Check(world.GetBlock(deep.X, deep.Y, deep.Z) == Block.Stone, "buried unloaded chunk still reads as stone");
        Check(world.GetBlock(deep.X, world.BedrockY - 500, deep.Z) == Block.Bedrock,
            "far below bedrock reads as bedrock");
        Check(world.SetBlock(deep.X, deep.Y, deep.Z, Block.Air), "digging into a buried chunk creates it");
        Check(world.TryGetChunk(deep.X, deep.Y, deep.Z, out _), "dug chunk now exists");

        // ---- spawn placement ---------------------------------------------
        var spawn = world.FindSpawn();
        Check(world.BodyFits(spawn), $"spawn body is clear of solid blocks ({spawn})");
        Check(!Blocks.IsSolid(world.GetBlock(Mathf.FloorToInt(spawn.X), Mathf.FloorToInt(spawn.Y),
            Mathf.FloorToInt(spawn.Z))), "spawn feet are not inside a block");
        Check(!Blocks.IsSolid(world.GetBlock(Mathf.FloorToInt(spawn.X), Mathf.FloorToInt(spawn.Y) + 1,
            Mathf.FloorToInt(spawn.Z))), "spawn head room is not inside a block");
        Check(!world.BodyFits(new Vector3(spawn.X, VoxelWorld.ChunkSize * -1 + 0.5f, spawn.Z)),
            "the fit test rejects a position inside stone");
        Check(!world.BodyFits(new Vector3(0.5f, world.BedrockY, 0.5f)), "the fit test rejects bedrock");

        // Walled in at the origin, the search still has to return somewhere the body fits.
        int originY = world.SurfaceY(0, 0);
        world.SetBlock(0, originY, 0, Block.Stone);
        world.SetBlock(0, originY + 1, 0, Block.Stone);
        Check(!world.BodyFits(new Vector3(0.5f, originY, 0.5f)), "the blocked position is rejected");
        Check(world.BodyFits(world.FindSpawn()), $"a walled-off origin still spawns clear ({world.FindSpawn()})");
        world.SetBlock(0, originY, 0, Block.Air);
        world.SetBlock(0, originY + 1, 0, Block.Air);

        // ---- block break parameters + the edit request pipeline -----------
        Check(Blocks.HardnessOf(Block.Bedrock) < 0f && !Blocks.IsBreakable(Block.Bedrock),
            "bedrock has no break time and is unbreakable");
        Check(Blocks.HardnessOf(Block.Stone) > Blocks.HardnessOf(Block.Dirt),
            $"stone is harder than dirt ({Blocks.HardnessOf(Block.Stone)}s vs {Blocks.HardnessOf(Block.Dirt)}s)");
        Check(Blocks.HardnessOf(Block.Leaves) < Blocks.HardnessOf(Block.Wood),
            "leaves are softer than wood");

        world.RequestEdit(EditRequest.Break(new Vector3I(0, world.BedrockY, 0), default));
        Check(world.ApplyPendingEdits() == 0, "a bedrock break request is refused");

        var air = new Vector3I(4, world.HeightAt(4, 4) + 30, 4);
        Check(world.GetBlock(air.X, air.Y, air.Z) == Block.Air, "target cell starts as air");
        world.RequestEdit(EditRequest.Place(air, Block.Plank, default));
        Check(world.ApplyPendingEdits() == 1, "a place request is applied");
        Check(world.GetBlock(air.X, air.Y, air.Z) == Block.Plank, "the placed block is there");
        world.RequestEdit(EditRequest.Place(air, Block.Stone, default));
        Check(world.ApplyPendingEdits() == 0, "placing into an occupied cell is refused");

        // One break is not one block: a wood block takes the tree with it.
        Vector3I? trunk = null;
        for (int y = -20; y < 40 && trunk == null; y++)
            for (int z = -16; z < 32 && trunk == null; z++)
                for (int x = -16; x < 32 && trunk == null; x++)
                    if (world.GetBlock(x, y, z) == Block.Wood) trunk = new Vector3I(x, y, z);
        Check(trunk.HasValue, "found a tree to fell");
        if (trunk.HasValue)
        {
            int woodBefore = CountBlocks(world, Block.Wood);
            int leavesBefore = CountBlocks(world, Block.Leaves);
            world.RequestEdit(EditRequest.Break(trunk.Value, default));
            Check(world.ApplyPendingEdits() == 1, "the fell request is applied");
            int woodGone = woodBefore - CountBlocks(world, Block.Wood);
            int leavesGone = leavesBefore - CountBlocks(world, Block.Leaves);
            Check(woodGone > 1, $"felling removes the whole trunk ({woodGone} wood blocks)");
            Check(leavesGone > 0, $"felling takes the canopy too ({leavesGone} leaves)");
            Check(BlockBehaviors.MaxBlocksPerBreak >= 64, "the fell budget is bounded");
        }

        // A second world must be able to have completely different terrain.
        var other = new VoxelWorld { Terrain = new TerrainGenerator(seed: 99, heightAmplitude: 8) };
        Check(other.HeightAt(0, 0) != world.HeightAt(0, 0) || other.Terrain.HeightAmplitude != world.Terrain.HeightAmplitude,
            "terrain parameters are per world, not static");
        other.Free();

        // ---- coordinates -------------------------------------------------
        var target = new Vector3I(1 * 16, 40, 1 * 16);
        Check(world.SetBlock(target.X, target.Y, target.Z, Block.Plank), "set block across chunk coords");
        Check(world.GetBlock(target.X, target.Y, target.Z) == Block.Plank, "read back the placed block");
        Check(world.SetBlock(target.X, target.Y, target.Z, Block.Air), "clear the test block");

        // Negative coordinates on all three axes must floor-divide, not truncate.
        var placed = new Vector3I(-1, -20, -17);
        Check(world.SetBlock(placed.X, placed.Y, placed.Z, Block.Wood), "set block at negative x/y/z");
        Check(world.GetBlock(placed.X, placed.Y, placed.Z) == Block.Wood, "read back at negative x/y/z");
        Check(world.SetBlock(placed.X, placed.Y, placed.Z, Block.Air), "clear negative-coord block");

        // ---- block orientation -------------------------------------------
        CheckOrientation(world);
        CheckOrientationByteDomain();

        // ---- texture pack + mesher texture attributes ---------------------
        CheckTileIndexMap();
        CheckFaceOverrides();
        CheckResolveTilePath();
        CheckPackLoading();
        CheckMesherTextureArrays(world);
        CheckMesherOrientation(world);
        CheckPlacementPreview(world);
        // ---- block entities (interactive blocks) ------
        CheckBlockEntities(world);
        CheckMobs(world);
        CheckTextureVariants(world);
        CheckSizeClasses(world);

        return _failures;
    }

    /// <summary>Mobs: deterministic spawning, lightweight AABB movement and pure AI rules.
    /// Every check here can fail.</summary>
    private static void CheckMobs(VoxelWorld world)
    {
        GD.Print("  ---- mobs ----");

        Check(MobSystems.Tick(world, 1f / 60f) == 0.0, "zero mobs: Tick returns exactly 0.0 ms");

        // -- the plan is pure and deterministic ---------------------------
        var planA = MobSpawner.Plan(1337, 5, -3);
        var planB = MobSpawner.Plan(1337, 5, -3);
        bool samePlan = planA.Count == planB.Count && planA.Count > 0;
        for (int i = 0; samePlan && i < planA.Count; i++) samePlan = planA[i] == planB[i];
        Check(samePlan, $"spawn plan is deterministic ({planA.Count} candidates)");

        int offRing = 0, unsorted = 0;
        long previous = long.MinValue;
        for (int i = 0; i < planA.Count; i++)
        {
            int dx = planA[i].X - 5, dz = planA[i].Z + 3;
            int ring = Math.Max(Math.Abs(dx), Math.Abs(dz));
            if (ring < MobSpawner.SpawnRadius || ring > MobSpawner.SpawnRadius + MobSpawner.SpawnRingWidth) offRing++;
            if (MobSpawner.CellHash(1337, planA[i].X, planA[i].Z) % 8 != 0) offRing++;
            long distance = (long)dx * dx + (long)dz * dz;
            if (distance < previous) unsorted++;
            previous = distance;
        }
        Check(offRing == 0, $"{planA.Count}/{planA.Count} candidates sit on selected ring cells");
        Check(unsorted == 0, "plan is sorted nearest-first");

        // -- two worlds, same seed and focus, same mobs -------------------
        var worldA = MakeMobWorld();
        var worldB = MakeMobWorld();
        var positionsA = SimulateMobs(worldA, 30);
        var positionsB = SimulateMobs(worldB, 30);
        bool identical = positionsA.Length == positionsB.Length && positionsA.Length > 0;
        for (int i = 0; identical && i < positionsA.Length; i++) identical = positionsA[i] == positionsB[i];
        Check(identical, $"two worlds, same seed + focus: {positionsA.Length} mobs at identical positions after 30 ticks");
        worldA.Free();
        worldB.Free();

        // -- cap, node wiring and a 300-tick population -------------------
        var capWorld = MakeMobWorld();
        capWorld.EnsureAreaAround(capWorld.FindSpawn(), 3);
        capWorld.Focus = new Vector3(0.5f, 0f, 0.5f);
        for (int i = 0; i < 200; i++)
        {
            MobSystems.Spawn(capWorld);
            MobSystems.Tick(capWorld, 1f / 60f);
        }
        int spawned = MobSpawner.Count(capWorld.Store);
        Check(spawned == MobSystems.MaxMobs, $"cap respected after 200 spawn ticks ({spawned}/{MobSystems.MaxMobs})");

        bool parented = true, sharedView = true;
        MeshInstance3D firstNode = null;
        capWorld.Store.Query<MobVisual>().AllTags(Tags.Get<Mob>())
            .ForEachEntity((ref MobVisual visual, Entity entity) =>
            {
                if (visual.Node == null) { parented = false; return; }
                if (firstNode == null) firstNode = visual.Node;
                else if (visual.Node.Mesh != firstNode.Mesh
                    || visual.Node.MaterialOverride != firstNode.MaterialOverride) sharedView = false;
                if (visual.Node.GetParent() != capWorld) parented = false;
            });
        Check(parented && sharedView, "mob nodes are children of the world and share one mesh + material");

        for (int i = 0; i < 300; i++) MobSystems.Tick(capWorld, 1f / 60f);
        int after = MobSpawner.Count(capWorld.Store), grounded = 0, stillInside = 0;
        capWorld.Store.Query<MobXform, MobVelocity>().AllTags(Tags.Get<Mob>())
            .ForEachEntity((ref MobXform xform, ref MobVelocity velocity, Entity entity) =>
            {
                if (!MobAiRules.MobFits(capWorld, xform.Position.X, xform.Position.Y, xform.Position.Z)) stillInside++;
                if (velocity.Value.Y == 0f) grounded++;
            });
        GD.Print($"mobs: {after} spawned, {grounded} grounded, {stillInside} inside geometry");
        Check(after == spawned && stillInside == 0,
            $"300 ticks: 0 mobs inside geometry ({after} mobs, {stillInside} inside)");
        capWorld.Free();

        // -- a dropped mob lands and stays on the ground ------------------
        var dropWorld = MakeMobWorld();
        dropWorld.EnsureAreaAround(dropWorld.FindSpawn(), 0);
        dropWorld.Focus = new Vector3(0.5f, 0f, 0.5f);
        int surface = dropWorld.HeightAt(0, 0);
        var dropped = dropWorld.Store.CreateEntity(
            new MobXform { Position = new Vector3(0.5f, surface + 6f, 0.5f) },
            new MobVelocity(),
            new MobAi { Rng = 77, TargetX = 0.5f, TargetZ = 0.5f, Retarget = 0f },
            new MobVisual(),
            Tags.Get<Mob>());
        for (int i = 0; i < 180; i++) MobSystems.Tick(dropWorld, 1f / 60f);
        ref var droppedXform = ref dropped.GetComponent<MobXform>();
        var droppedVelocity = dropped.GetComponent<MobVelocity>();
        int feetX = Mathf.FloorToInt(droppedXform.Position.X);
        int feetY = Mathf.FloorToInt(droppedXform.Position.Y);
        int feetZ = Mathf.FloorToInt(droppedXform.Position.Z);
        Check(!Blocks.IsSolid(dropWorld.GetBlock(feetX, feetY, feetZ))
            && Blocks.IsSolid(dropWorld.GetBlock(feetX, feetY - 1, feetZ))
            && droppedVelocity.Value.Y == 0f,
            $"a dropped mob settles on the surface (y={droppedXform.Position.Y:F2}, surface={surface})");
        dropWorld.Free();

        // -- despawn: out of range or out of the world --------------------
        var leaveWorld = MakeMobWorld();
        leaveWorld.EnsureAreaAround(leaveWorld.FindSpawn(), 2);
        leaveWorld.Focus = new Vector3(0.5f, 0f, 0.5f);
        for (int i = 0; i < 10 && MobSpawner.Count(leaveWorld.Store) == 0; i++) MobSystems.Spawn(leaveWorld);
        Entity leaver = FirstMob(leaveWorld.Store);
        var leaverNode = leaver.GetComponent<MobVisual>().Node;
        leaver.GetComponent<MobXform>().Position = new Vector3(100.5f, 50f, 0.5f);
        int beforeLeave = MobSpawner.Count(leaveWorld.Store);
        MobSystems.Spawn(leaveWorld);
        Check(beforeLeave >= 1 && leaver.IsNull && leaverNode.IsQueuedForDeletion(),
            $"a mob past DespawnRadius is deleted with its node ({beforeLeave} before)");

        var faller = leaveWorld.Store.CreateEntity(
            new MobXform { Position = new Vector3(0.5f, leaveWorld.BedrockY - 17f, 0.5f) },
            new MobVelocity(),
            new MobAi { Rng = 11 },
            new MobVisual(),
            Tags.Get<Mob>());
        MobSystems.Spawn(leaveWorld);
        Check(faller.IsNull, $"a mob below BedrockY - 16 is deleted");
        leaveWorld.Free();

        // -- AI is a pure function ----------------------------------------
        var air = new TestBlocks();

        var wall = new TestBlocks();
        wall.Solid.Add((2, 0, 0));
        var aiTarget = new MobAi { Rng = 12345, TargetX = 2.5f, TargetZ = 0.5f, Retarget = 100f };
        MobAiRules.Decide(wall, ref aiTarget, 0.5f, 0f, 0.5f, 100f, 0f, 0.5f, 1f / 60f);
        bool repicked = aiTarget.TargetX != 2.5f || aiTarget.TargetZ != 0.5f;
        bool targetFree = !wall.Solid.Contains((Mathf.FloorToInt(aiTarget.TargetX), 0, Mathf.FloorToInt(aiTarget.TargetZ)));
        Check(repicked && targetFree, $"a wander target inside a block is re-picked ({aiTarget.TargetX:F2}, {aiTarget.TargetZ:F2})");

        var aiApproach = new MobAi { Rng = 3 };
        var approach = MobAiRules.Decide(air, ref aiApproach, 0.5f, 0f, 0.5f, 5.5f, 0f, 0.5f, 1f / 60f);
        Check(approach.DirX > 0.99f && MathF.Abs(approach.DirZ) < 0.01f,
            $"inside ReactRadius the mob approaches the player ({approach.DirX:F2}, {approach.DirZ:F2})");

        var aiDiagonal = new MobAi { Rng = 4 };
        var diagonal = MobAiRules.Decide(air, ref aiDiagonal, 0.5f, 0f, 0.5f, 8.5f, 0f, 6.5f, 1f / 60f);
        float dot = diagonal.DirX * 8f + diagonal.DirZ * 6f;
        Check(dot > 0f, $"off-axis approach points at the player (dot={dot:F2})");

        var aiWander = new MobAi { Rng = 777 };
        var wander = MobAiRules.Decide(air, ref aiWander, 0.5f, 0f, 0.5f, 100f, 0f, 100f, 1f / 60f);
        float targetDistance = new Vector2(aiWander.TargetX - 0.5f, aiWander.TargetZ - 0.5f).Length();
        Check(targetDistance > 0f && targetDistance <= MobAiRules.WanderRadius + 0.001f,
            $"far from the player a fresh target stays inside WanderRadius ({targetDistance:F2})");
        Check(aiWander.Retarget == MobAiRules.RetargetSeconds, "a new wander target resets the retarget timer");

        var copyWander = new MobAi
        {
            Rng = aiWander.Rng,
            TargetX = aiWander.TargetX,
            TargetZ = aiWander.TargetZ,
            Retarget = aiWander.Retarget,
        };
        var wanderAgain = MobAiRules.Decide(air, ref copyWander, 0.5f, 0f, 0.5f, 100f, 0f, 100f, 1f / 60f);
        Check(wanderAgain.DirX == wander.DirX && wanderAgain.DirZ == wander.DirZ
            && copyWander.TargetX == aiWander.TargetX && copyWander.TargetZ == aiWander.TargetZ,
            "identical AI state and inputs give an identical move");

        var step = new TestBlocks();
        step.Solid.Add((1, 0, 0));
        var aiStep = new MobAi { Rng = 1, TargetX = 8.5f, TargetZ = 0.5f, Retarget = 100f };
        var stepMove = MobAiRules.Decide(step, ref aiStep, 0.5f, 0f, 0.5f, 100f, 0f, 0.5f, 1f / 60f);
        Check(stepMove.StepUp && stepMove.DirX > 0.99f, "a one-block step sets StepUp and keeps the direction");

        var blocked = new TestBlocks();
        blocked.Solid.Add((1, 0, 0));
        blocked.Solid.Add((1, 1, 0));
        var aiBlocked = new MobAi { Rng = 1, TargetX = 8.5f, TargetZ = 0.5f, Retarget = 100f };
        var blockedMove = MobAiRules.Decide(blocked, ref aiBlocked, 0.5f, 0f, 0.5f, 100f, 0f, 0.5f, 1f / 60f);
        Check(blockedMove.DirX == -1f && blockedMove.DirZ == 0f && !blockedMove.StepUp,
            $"a two-high wall turns the mob onto the free axis ({blockedMove.DirX}, {blockedMove.DirZ})");

        var sealedBox = new TestBlocks();
        sealedBox.Solid.Add((1, 0, 0));
        sealedBox.Solid.Add((1, 1, 0));
        sealedBox.Solid.Add((-1, 0, 0));
        sealedBox.Solid.Add((-1, 1, 0));
        sealedBox.Solid.Add((0, 0, 1));
        sealedBox.Solid.Add((0, 1, 1));
        sealedBox.Solid.Add((0, 0, -1));
        sealedBox.Solid.Add((0, 1, -1));
        var aiSealed = new MobAi { Rng = 2, TargetX = 8.5f, TargetZ = 0.5f, Retarget = 100f };
        var sealedMove = MobAiRules.Decide(sealedBox, ref aiSealed, 0.5f, 0f, 0.5f, 100f, 0f, 0.5f, 1f / 60f);
        Check(sealedMove.DirX == 0f && sealedMove.DirZ == 0f && aiSealed.Retarget == 0f,
            "a sealed box stops the mob and forces a retarget");

        var rngA = new MobAi { Rng = 424242 };
        var rngB = new MobAi { Rng = 424242 };
        bool sequence = true;
        for (int i = 0; i < 64; i++)
        {
            var moveA = MobAiRules.Decide(air, ref rngA, 0.5f, 0f, 0.5f, 100f, 0f, 100f, 1f / 60f);
            var moveB = MobAiRules.Decide(air, ref rngB, 0.5f, 0f, 0.5f, 100f, 0f, 100f, 1f / 60f);
            if (moveA.DirX != moveB.DirX || moveA.DirZ != moveB.DirZ || rngA.Rng != rngB.Rng
                || rngA.TargetX != rngB.TargetX || rngA.TargetZ != rngB.TargetZ)
            {
                sequence = false;
                break;
            }
        }
        Check(sequence && rngA.Rng != 424242, "the same Rng seed yields the same 64-decision sequence");

        Check(MobSpawner.SpawnRadius > MobAiRules.ReactRadius
            && MobSystems.DespawnRadius > MobSpawner.SpawnRadius,
            $"mob radii stay ordered: despawn {MobSystems.DespawnRadius} > spawn {MobSpawner.SpawnRadius} > react {MobAiRules.ReactRadius}");

        // -- forced inside solid geometry: one Tick frees the mob ---------
        var stuckWorld = MakeMobWorld();
        stuckWorld.EnsureAreaAround(stuckWorld.FindSpawn(), 0);
        stuckWorld.Focus = new Vector3(0.5f, 0f, 0.5f);
        int stuckY = stuckWorld.SurfaceY(0, 0);
        stuckWorld.SetBlock(0, stuckY, 0, Block.Stone);
        stuckWorld.SetBlock(0, stuckY + 1, 0, Block.Stone);
        var stuck = stuckWorld.Store.CreateEntity(
            new MobXform { Position = new Vector3(0.5f, stuckY + 0.2f, 0.5f) },
            new MobVelocity(),
            new MobAi { Rng = 55, TargetX = 0.5f, TargetZ = 0.5f, Retarget = 100f },
            new MobVisual(),
            Tags.Get<Mob>());
        MobSystems.Tick(stuckWorld, 1f / 60f);
        ref var stuckXform = ref stuck.GetComponent<MobXform>();
        Check(MobAiRules.MobFits(stuckWorld, stuckXform.Position.X, stuckXform.Position.Y, stuckXform.Position.Z),
            $"a mob forced inside solid is free after one Tick (y={stuckXform.Position.Y:F1})");
        stuckWorld.Free();
    }

    private static VoxelWorld MakeMobWorld()
        => new() { Terrain = new TerrainGenerator(seed: 4242, heightAmplitude: 12) };

    /// <summary>Loads the spawn ring's chunks, then runs spawn/tick/sync for N frames and
    /// returns mob positions in creation order.</summary>
    private static Vector3[] SimulateMobs(VoxelWorld world, int ticks)
    {
        world.EnsureAreaAround(world.FindSpawn(), 2);
        world.Focus = new Vector3(0.5f, 0f, 0.5f);
        for (int i = 0; i < ticks; i++)
        {
            MobSystems.Spawn(world);
            MobSystems.Tick(world, 1f / 60f);
            MobSystems.SyncVisuals(world.Store);
        }
        var positions = new List<Vector3>();
        world.Store.Query<MobXform>().AllTags(Tags.Get<Mob>())
            .ForEachEntity((ref MobXform xform, Entity entity) => positions.Add(xform.Position));
        return positions.ToArray();
    }

    private static Entity FirstMob(EntityStore store)
    {
        Entity first = default;
        bool seen = false;
        store.Query<MobXform>().AllTags(Tags.Get<Mob>())
            .ForEachEntity((ref MobXform xform, Entity entity) =>
            {
                if (seen) return;
                first = entity;
                seen = true;
            });
        return first;
    }

    /// <summary>Minimal IBlockReader for the pure AI tests: a set of solid cells, air elsewhere.</summary>
    private sealed class TestBlocks : IBlockReader
    {
        public readonly HashSet<(int X, int Y, int Z)> Solid = new();

        public Block GetBlock(int x, int y, int z)
            => Solid.Contains((x, y, z)) ? Block.Stone : Block.Air;
    }

    private static int CountBlocks(VoxelWorld world, Block block)
    {
        int count = 0;
        for (int y = -25; y < 45; y++)
            for (int z = -20; z < 36; z++)
                for (int x = -20; x < 36; x++)
                    if (world.GetBlock(x, y, z) == block) count++;
        return count;
    }

    private static int WindingErrors(ArrayMesh mesh)
    {
        if (mesh == null) return 0;
        var arrays = mesh.SurfaceGetArrays(0);
        var verts = (Vector3[])arrays[(int)Mesh.ArrayType.Vertex];
        var norms = (Vector3[])arrays[(int)Mesh.ArrayType.Normal];
        var idx = (int[])arrays[(int)Mesh.ArrayType.Index];
        int bad = 0;
        for (int t = 0; t < idx.Length; t += 3)
        {
            var a = verts[idx[t]];
            var winding = (verts[idx[t + 1]] - a).Cross(verts[idx[t + 2]] - a);
            if (winding.Normalized().Dot(norms[idx[t]]) > -0.5f) bad++;
        }
        return bad;
    }

    private static int CountIndices(VoxelWorld world, Entity chunk)
        => CountIndices(world, chunk.GetComponent<ChunkCoord>(), chunk.GetComponent<ChunkBlocks>().Value);

    private static int CountIndices(VoxelWorld world, ChunkCoord coord, byte[] blocks)
    {
        ChunkMesher.Build(world, coord, blocks, out int indices);
        return indices;
    }

    /// <summary>Independent face count straight from the block data, to cross-check the mesher.</summary>
    private static int ExpectedIndices(VoxelWorld world, Entity chunk)
    {
        var coord = chunk.GetComponent<ChunkCoord>();
        var blocks = chunk.GetComponent<ChunkBlocks>().Value;
        int bx = coord.X * 16, by = coord.Y * 16, bz = coord.Z * 16;
        int faces = 0;
        for (int y = 0; y < 16; y++)
        {
            for (int z = 0; z < 16; z++)
            {
                for (int x = 0; x < 16; x++)
                {
                    if (!Blocks.IsSolid((Block)blocks[ChunkBlocks.Index(x, y, z)])) continue;
                    int wx = bx + x, wy = by + y, wz = bz + z;
                    if (!Blocks.IsSolid(world.GetBlock(wx + 1, wy, wz))) faces++;
                    if (!Blocks.IsSolid(world.GetBlock(wx - 1, wy, wz))) faces++;
                    if (!Blocks.IsSolid(world.GetBlock(wx, wy + 1, wz))) faces++;
                    if (!Blocks.IsSolid(world.GetBlock(wx, wy - 1, wz))) faces++;
                    if (!Blocks.IsSolid(world.GetBlock(wx, wy, wz + 1))) faces++;
                    if (!Blocks.IsSolid(world.GetBlock(wx, wy, wz - 1))) faces++;
                }
            }
        }
        return faces * 6;
    }

    // ---- block orientation ------------------------------------------------

    /// <summary>4 corners per face, copied from ChunkMesher.FaceCorners: (x, y, z) triples.</summary>
    private static readonly int[][] FaceCorners =
    {
        new[] { 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 1 }, // +X
        new[] { 0, 0, 0, 0, 0, 1, 0, 1, 1, 0, 1, 0 }, // -X
        new[] { 0, 1, 1, 1, 1, 1, 1, 1, 0, 0, 1, 0 }, // +Y
        new[] { 0, 0, 0, 1, 0, 0, 1, 0, 1, 0, 0, 1 }, // -Y
        new[] { 0, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1 }, // +Z
        new[] { 1, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 0 }, // -Z
    };

    private static readonly Vector3I[] FaceDirs =
    {
        new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1),
    };

    private static float YawAt(int step) => -Mathf.Pi + step * (2f * Mathf.Pi / 16f);
    private static float PitchAt(int step) => -Mathf.Pi * 0.5f + step * (Mathf.Pi / 8f);

    /// <summary>Per-voxel rotation: storage, the 24-rotation group, the placement rules and the
    /// per-type orientation policy. Every check here can fail.</summary>
    private static void CheckOrientation(VoxelWorld world)
    {
        GD.Print("  ---- block orientation ----");

        // -- the type tables -------------------------------------------------
        int aliasWrong = 0, anyBlocks = 0, aliasUnmirrored = 0;
        int grassSize = 0, grassUpright = 0, chestSize = 0, chestUpright = 0;
        for (int b = 1; b <= LastBlockIndex; b++)
        {
            var block = (Block)b;
            if (Blocks.Allows(block, Orientation.IdentityDuplicate) != Blocks.Allows(block, Orientation.None)) aliasWrong++;
            if ((Blocks.OrientationMask(block) & (1u << Orientation.IdentityDuplicate)) != 0
                && (Blocks.OrientationMask(block) & 1u) == 0) aliasUnmirrored++;
            int size = AllowedCount(block);
            if (block == Block.Grass || block == Block.Chest)
            {
                int upright = 0;
                for (int value = 0; value <= Orientation.Count; value++)
                    if (value != Orientation.IdentityDuplicate && Blocks.Allows(block, (byte)value)
                        && Orientation.ImageOfLocalAxis((byte)value, 1) == Vector3I.Up) upright++;
                if (block == Block.Grass) { grassSize = size; grassUpright = upright; }
                else { chestSize = size; chestUpright = upright; }
            }
            else if (Blocks.PolicyOf(block) == OrientationPolicy.Any && size == Orientation.Count) anyBlocks++;
        }
        Check(grassSize == 4 && grassUpright == 4 && Blocks.PolicyOf(Block.Grass) == OrientationPolicy.Upright,
            $"Grass allows exactly 4 upright orientations ({grassSize} allowed, {grassUpright} upright)");
        Check(chestSize == 4 && chestUpright == 4 && Blocks.PolicyOf(Block.Chest) == OrientationPolicy.Upright,
            $"Chest allows exactly 4 upright orientations ({chestSize} allowed, {chestUpright} upright)");
        Check(anyBlocks == 7, $"{anyBlocks}/7 non-Grass, non-Chest blocks are Any(24)");
        Check(aliasWrong == 0, $"{LastBlockIndex - aliasWrong}/{LastBlockIndex} blocks answer Allows identically for the identity's two bytes");
        Check(aliasUnmirrored == 0, "the semantic mask always mirrors byte 9 from byte 0");
        Check(Blocks.OrientationMask(Block.Stone) == (1u << (Orientation.Count + 1)) - 1u,
            "Any's semantic mask is all 25 bytes");

        // -- policy totality and snapping ------------------------------------
        int snapTotal = 0, snapTotalWrong = 0;
        for (int b = 1; b <= LastBlockIndex; b++)
            for (int value = 0; value <= Orientation.Count; value++)
            {
                snapTotal++;
                if (!Blocks.Allows((Block)b, Blocks.Snap((Block)b, (byte)value))) snapTotalWrong++;
            }
        Check(snapTotalWrong == 0, $"{snapTotal}/{snapTotal} Snap results satisfy Allows over {LastBlockIndex} blocks x 25 bytes");

        byte[] strangers = { 25, 26, 200, 255 };
        int strangerTotal = 0, strangerWrong = 0;
        for (int b = 1; b <= LastBlockIndex; b++)
            foreach (byte stranger in strangers)
            {
                strangerTotal++;
                // An out-of-range byte reads as None: no overflow, no throw.
                if (!Blocks.Allows((Block)b, stranger)) strangerWrong++;
                if (!Blocks.Allows((Block)b, Blocks.Snap((Block)b, stranger))) strangerWrong++;
            }
        Check(strangerWrong == 0, $"{strangerTotal}/{strangerTotal} out-of-range inputs read as None and snap inside the policy");

        int snapPoses = 0, snapWrong = 0;
        for (int f = 0; f < 6; f++)
            for (int yi = 0; yi < 16; yi++)
                for (int pi = 0; pi < 9; pi++)
                {
                    snapPoses++;
                    byte o = BlockBehaviors.PlaceOrientation(Block.Grass, f, YawAt(yi), PitchAt(pi));
                    if (!Blocks.Allows(Block.Grass, o) || Orientation.ImageOfLocalAxis(o, 1) != Vector3I.Up) snapWrong++;
                }
        Check(snapWrong == 0, $"{snapPoses}/{snapPoses} sampled poses snap to an allowed Grass orientation");

        int defaults = 0, defaultsWrong = 0;
        for (int b = 1; b <= LastBlockIndex; b++)
            for (int f = 0; f < 6; f++)
                for (int yi = 0; yi < 16; yi++)
                    for (int pi = 0; pi < 9; pi++)
                    {
                        defaults++;
                        if (!Blocks.Allows((Block)b, BlockBehaviors.PlaceOrientation((Block)b, f, YawAt(yi), PitchAt(pi)))) defaultsWrong++;
                    }
        Check(defaultsWrong == 0, $"{defaults}/{defaults} per-block placement defaults are allowed orientations");

        // -- the rotate key's model-layer cycle -------------------------------
        int cycleBad = 0;
        for (int i = 0; i <= Orientation.Count; i++)
        {
            if (i == Orientation.IdentityDuplicate) continue;
            byte start = (byte)i;
            var seen = new HashSet<int> { start };
            byte value = start;
            for (int step = 0; step < Orientation.Count; step++)
            {
                value = Orientation.Next(value);
                seen.Add(value);
            }
            if (seen.Count != Orientation.Count || value != start) cycleBad++;
        }
        Check(cycleBad == 0, $"{24 - cycleBad}/24 canonical starts cycle through all 24 values and return");

        int nextBad = 0;
        for (int b = 0; b < 256; b++)
        {
            byte next = Orientation.Next((byte)b);
            if (next == Orientation.IdentityDuplicate || !Orientation.IsValid(next)) nextBad++;
        }
        Check(nextBad == 0, "Orientation.Next is total over 256 bytes, valid and never the alias byte 9");

        int sameStep = 0;
        for (int value = 0; value <= Orientation.Count; value++)
            if (Blocks.NextAllowed(Block.Stone, (byte)value) != Orientation.Next((byte)value)) sameStep++;
        Check(sameStep == 0, $"{Orientation.Count + 1 - sameStep}/{Orientation.Count + 1} NextAllowed(Any) and Orientation.Next agree, alias 9 included");

        int grassCycle = WalkAllowed(Block.Grass, BlockBehaviors.PlaceOrientation(Block.Grass, Face.Top, 0.32f, 0.21f));
        int stoneCycle = WalkAllowed(Block.Stone, BlockBehaviors.PlaceOrientation(Block.Stone, 3, -1.1f, 0.5f));
        Check(grassCycle == 4, $"4/4 allowed Grass orientations are reachable from its rule default ({grassCycle}/4)");
        Check(stoneCycle == 24, $"24/24 allowed Stone orientations are reachable from its rule default ({stoneCycle}/24)");

        int blockCycles = 0;
        for (int b = 1; b <= LastBlockIndex; b++)
            if (WalkAllowed((Block)b, Blocks.NextAllowed((Block)b, Orientation.None)) == AllowedCount((Block)b)) blockCycles++;
        Check(blockCycles == LastBlockIndex, $"{blockCycles}/{LastBlockIndex} blocks cycle through exactly their allowed set");

        // -- the placement rule -----------------------------------------------
        int placementWrong = 0;
        for (int b = 0; b <= LastBlockIndex; b++)
        {
            var want = (Block)b == Block.Wood ? Placement.Axis : (Block)b == Block.Chest ? Placement.Face : Placement.None;
            if (Blocks.PlacementOf((Block)b) != want) placementWrong++;
        }
        Check(placementWrong == 0, "PlacementOf is Axis for Wood, Face for Chest, and None for the other 8 types");

        int noneDefault = 0;
        for (int f = 0; f < 6; f++)
            for (int yi = 0; yi < 16; yi++)
                for (int pi = 0; pi < 9; pi++)
                    if (BlockBehaviors.PlaceOrientationFor(Placement.None, f, YawAt(yi), PitchAt(pi)) != Orientation.None) noneDefault++;
        Check(noneDefault == 0, $"Placement.None defaults to identity over 864 poses, it does not forbid rotation ({noneDefault} wrong)");

        int ruleInvalid = 0;
        for (int f = 0; f < 6; f++)
            for (int yi = 0; yi < 16; yi++)
                for (int pi = 0; pi < 9; pi++)
                {
                    if (!Orientation.IsValid(BlockBehaviors.PlaceOrientationFor(Placement.Axis, f, YawAt(yi), PitchAt(pi)))) ruleInvalid++;
                    if (!Orientation.IsValid(BlockBehaviors.PlaceOrientationFor(Placement.Face, f, YawAt(yi), PitchAt(pi)))) ruleInvalid++;
                }
        Check(ruleInvalid == 0, $"1728 rule placements over 864 poses are valid ({ruleInvalid} invalid)");

        int axisRuleWrong = 0;
        for (int yi = 0; yi < 16; yi++)
            for (int pi = 0; pi < 9; pi++)
            {
                if (Orientation.ImageOfLocalAxis(BlockBehaviors.PlaceOrientationFor(Placement.Axis, Face.Top, YawAt(yi), PitchAt(pi)), 1) != Vector3I.Up) axisRuleWrong++;
                if (Orientation.ImageOfLocalAxis(BlockBehaviors.PlaceOrientationFor(Placement.Axis, Face.Bottom, YawAt(yi), PitchAt(pi)), 1) != Vector3I.Down) axisRuleWrong++;
                if (!AlongAxis(BlockBehaviors.PlaceOrientationFor(Placement.Axis, Face.PosX, YawAt(yi), PitchAt(pi)), 1, 0)) axisRuleWrong++;
                if (!AlongAxis(BlockBehaviors.PlaceOrientationFor(Placement.Axis, Face.PosZ, YawAt(yi), PitchAt(pi)), 1, 2)) axisRuleWrong++;
            }
        Check(axisRuleWrong == 0, $"clicked face is the log axis: Top/Bottom vertical, PosX along X, PosZ along Z ({axisRuleWrong} wrong)");

        var axisReach = new HashSet<int>();
        for (int f = 0; f < 6; f++)
            for (int q = 0; q < 4; q++)
                axisReach.Add(BlockBehaviors.PlaceOrientationFor(Placement.Axis, f, q * Mathf.Pi * 0.5f, 0f));
        Check(axisReach.Count == 24, $"24/24 axis rotations reachable from 6 faces x 4 yaw quadrants ({axisReach.Count}/24)");

        int faceRuleWrong = 0;
        for (int q = 0; q < 4; q++)
        {
            float yaw = q * Mathf.Pi * 0.5f;
            int toward = BlockBehaviors.FaceOfNormal(PlayerSystems.CameraBasis(yaw, 0f).Z);
            for (int f = 0; f < 6; f++)
            {
                byte o = BlockBehaviors.PlaceOrientationFor(Placement.Face, f, yaw, 0f);
                if (Orientation.ImageOfLocalAxis(o, 2) != FaceDirs[toward]) faceRuleWrong++;
                if (!Perpendicular(Orientation.ImageOfLocalAxis(o, 1), Orientation.ImageOfLocalAxis(o, 2))) faceRuleWrong++;
            }
        }
        Check(faceRuleWrong == 0, $"a front-facing block turns its +Z back toward the player with a perpendicular up ({faceRuleWrong} wrong)");

        byte diagonal = BlockBehaviors.PlaceOrientationFor(Placement.Face, Face.PosX, Mathf.Pi * 0.25f, -Mathf.Pi * 0.25f);
        Check(Orientation.IsValid(diagonal)
            && Perpendicular(Orientation.ImageOfLocalAxis(diagonal, 1), Orientation.ImageOfLocalAxis(diagonal, 2)),
            "front and up snapping onto one axis still yields a real rotation");
        Check(Orientation.IsValid(BlockBehaviors.PlaceOrientationFor(Placement.Face, Face.Top, 0.3f, Mathf.Pi * 0.5f))
            && Orientation.IsValid(BlockBehaviors.PlaceOrientationFor(Placement.Face, Face.Top, -1.2f, -Mathf.Pi * 0.5f)),
            "looking straight up and straight down still yields valid rotations");

        int normalWrong = 0;
        for (int f = 0; f < 6; f++)
            if (BlockBehaviors.FaceOfNormal(new Vector3(FaceDirs[f].X, FaceDirs[f].Y, FaceDirs[f].Z)) != f) normalWrong++;
        Check(normalWrong == 0, $"6/6 axis normals snap to their own face ({normalWrong} wrong)");
        Check(BlockBehaviors.FaceOfNormal(new Vector3(0.6f, 0.6f, 0.2f)) == Face.PosX
            && BlockBehaviors.FaceOfNormal(new Vector3(-0.6f, 0.2f, 0.2f)) == Face.NegX
            && BlockBehaviors.FaceOfNormal(new Vector3(0.2f, -0.9f, 0.1f)) == Face.Bottom
            && BlockBehaviors.FaceOfNormal(new Vector3(0.25f, 0.2f, -0.5f)) == Face.NegZ,
            "off-axis normals snap to their dominant axis, ties to the lowest face");
        Check(BlockBehaviors.FaceOfNormal(Vector3.Zero) == Face.Top, "a zero normal returns the documented Face.Top default");

        // -- the 24-rotation group --------------------------------------------
        int distinct = 0;
        for (int a = 0; a < Orientation.Count; a++)
        {
            bool seen = false;
            for (int b = 0; b < a && !seen; b++) seen = SameRotation((byte)(a + 1), (byte)(b + 1));
            if (!seen) distinct++;
        }
        Check(distinct == 24, $"24/24 table rotations are distinct ({distinct} distinct)");

        int detWrong = 0, identity = 0, inverseWrong = 0;
        for (int i = 0; i < Orientation.Count; i++)
        {
            byte o = (byte)(i + 1);
            if (Determinant(o) != 1) detWrong++;
            if (IsIdentity(o)) identity++;
            byte inv = Orientation.Inverse(o);
            if (!Orientation.IsValid(inv) || !Inverts(o, inv) || Orientation.Inverse(inv) != o) inverseWrong++;
        }
        Check(detWrong == 0, $"24/24 rotations have determinant +1 ({detWrong} wrong)");
        Check(identity == 1, $"the identity rotation is in the table exactly once ({identity})");
        Check(inverseWrong == 0, $"24/24 rotations are closed under Inverse and it is an involution ({inverseWrong} wrong)");

        int noneWrong = 0;
        for (int axis = 0; axis < 3; axis++)
            if (Orientation.ImageOfLocalAxis(Orientation.None, axis) != FaceDirs[axis * 2]) noneWrong++;
        for (int f = 0; f < 6; f++)
            if (Orientation.LocalFace(Orientation.None, f) != f) noneWrong++;
        for (int c = 0; c < 8; c++)
            if (Orientation.LocalCorner(Orientation.None, c & 1, (c >> 1) & 1, (c >> 2) & 1) != c) noneWrong++;
        Check(noneWrong == 0, $"None is the identity: 3 axes, 6 faces and 8 corners unchanged ({noneWrong} wrong)");

        int axisTableWrong = 0, identityAlias = 0;
        for (int dir = 0; dir < 6; dir++)
            for (int roll = 0; roll < 4; roll++)
            {
                int row = dir * 4 + roll;
                byte o = Orientation.Axis(dir, roll);
                if (o != (row == 8 ? Orientation.None : (byte)(row + 1)) || !Orientation.IsValid(o)) axisTableWrong++;
                if (o == Orientation.IdentityDuplicate) identityAlias++;
            }
        Check(axisTableWrong == 0, $"Axis(dir, roll) is the table order dir * 4 + roll with identity normalized ({axisTableWrong} wrong)");
        Check(identityAlias == 0 && Orientation.Axis(Face.Top, 0) == Orientation.None
            && Orientation.ImageOfLocalAxis(Orientation.Axis(Face.Top, 0), 1) == Vector3I.Up,
            "Axis(Top, 0) is the identity and comes back as None, never byte 9");
        Check(Orientation.Face(Face.PosZ, Face.Top) == Orientation.None
            && Orientation.ToIndex(Orientation.None) == Orientation.ToIndex(Orientation.IdentityDuplicate),
            "the identity pair of Face is None too, and both identity bytes share one table row");

        int faceWrong = 0, faceUpWrong = 0;
        var faceResults = new HashSet<int>();
        for (int front = 0; front < 6; front++)
            for (int up = 0; up < 6; up++)
            {
                byte o = Orientation.Face(front, up);
                if (!Orientation.IsValid(o) || Orientation.ImageOfLocalAxis(o, 2) != FaceDirs[front]) faceWrong++;
                if ((front >> 1) != (up >> 1) && Orientation.ImageOfLocalAxis(o, 1) != FaceDirs[up]) faceUpWrong++;
                faceResults.Add(o);
            }
        Check(faceWrong == 0, $"Face(front, up) points local +Z at front for all 36 inputs ({faceWrong} wrong)");
        Check(faceUpWrong == 0, $"Face(front, up) honours up when it is perpendicular ({faceUpWrong} wrong)");
        Check(faceResults.Count == 24, $"Face(front, up) reaches all 24 rotations from 36 inputs ({faceResults.Count}/24)");
        Check(Orientation.IsValid(Orientation.Face(9, 42)) && Orientation.IsValid(Orientation.Face(Face.Top, Face.Top)),
            "Face is total for out-of-range and parallel inputs");

        int nonBijections = 0;
        for (int i = 0; i < Orientation.Count; i++)
        {
            byte o = (byte)(i + 1);
            int seen = 0;
            for (int w = 0; w < 6; w++)
            {
                int local = Orientation.LocalFace(o, w);
                if ((uint)local > 5 || (seen & (1 << local)) != 0) { seen = -1; break; }
                seen |= 1 << local;
            }
            if (seen != 0x3F) nonBijections++;
        }
        Check(nonBijections == 0, $"24/24 world-face -> local-face maps are bijections ({24 - nonBijections}/24)");

        int cornerWrong = 0;
        for (int i = 0; i < Orientation.Count; i++)
            for (int w = 0; w < 6; w++)
            {
                var local = LocalCornersOf((byte)(i + 1), FaceCorners[w]);
                var expected = PackedCorners(FaceCorners[Orientation.LocalFace((byte)(i + 1), w)]);
                Array.Sort(local);
                Array.Sort(expected);
                for (int k = 0; k < 4; k++)
                    if (local[k] != expected[k]) { cornerWrong++; break; }
            }
        Check(cornerWrong == 0, $"144/144 rotation x face corner maps match the mesher table ({144 - cornerWrong}/144)");

        int mapWrong = 0;
        for (int i = 0; i < Orientation.Count; i++)
            for (int w = 0; w < 6; w++)
            {
                byte o = (byte)(i + 1);
                var v = FaceDirs[w];
                int axis = -1, sign = 0;
                for (int a = 0; a < 3; a++)
                {
                    var col = Orientation.ImageOfLocalAxis(o, a);
                    int dot = col.X * v.X + col.Y * v.Y + col.Z * v.Z;
                    if (dot != 0) { axis = a; sign = dot; }
                }
                if (axis * 2 + (sign < 0 ? 1 : 0) != Orientation.LocalFace(o, w)) mapWrong++;
            }
        Check(mapWrong == 0, $"144/144 rotation face map cross-checked against a transposed matrix ({mapWrong} wrong)");

        // Independent hand-written oracle for the six Axis(dir, 0) rotations: world face ->
        // local face, derived from the contract's Base(dir) columns.
        int[][] literal =
        {
            new[] { 2, 3, 4, 5, 0, 1 }, // Axis(PosX, 0)
            new[] { 3, 2, 4, 5, 1, 0 }, // Axis(NegX, 0)
            new[] { 0, 1, 2, 3, 4, 5 }, // Axis(Top, 0), the identity
            new[] { 1, 0, 3, 2, 4, 5 }, // Axis(Bottom, 0)
            new[] { 1, 0, 4, 5, 2, 3 }, // Axis(PosZ, 0)
            new[] { 0, 1, 4, 5, 3, 2 }, // Axis(NegZ, 0)
        };
        int literalWrong = 0;
        for (int dir = 0; dir < 6; dir++)
            for (int w = 0; w < 6; w++)
                if (Orientation.LocalFace(Orientation.Axis(dir, 0), w) != literal[dir][w]) literalWrong++;
        Check(literalWrong == 0, $"36/36 literal axis-rotation face table ({36 - literalWrong}/36)");

        // Two fixed quarter turns generate the whole group; one alone reaches only 4 values.
        byte genY = Orientation.Axis(Face.Top, 1);
        byte genZ = Orientation.Face(Face.PosZ, Face.NegX);
        int generated = 0, generatedStarts = 0;
        for (int i = 0; i <= Orientation.Count; i++)
        {
            if (i == Orientation.IdentityDuplicate) continue; // the alias is not a distinct rotation
            generatedStarts++;
            if (ClosureSize((byte)i, genY, genZ) == Orientation.Count) generated++;
        }
        Check(generated == generatedStarts && generatedStarts == 24,
            $"24/24 canonical starts generate the whole group from two fixed quarter turns ({generated}/24)");
        Check(ClosureSize(genY, genY) == 4, $"one quarter turn alone only reaches 4/24, so the check above can fail ({ClosureSize(genY, genY)}/24)");

        // -- storage ----------------------------------------------------------
        int stored = 0, storageWrong = 0;
        var storeCell = new Vector3I(6, 3001, 6);
        for (int b = 1; b <= LastBlockIndex; b++)
            for (int i = 0; i < Orientation.Count; i++)
            {
                stored++;
                byte o = (byte)(i + 1);
                world.SetBlock(storeCell.X, storeCell.Y, storeCell.Z, (Block)b, o);
                if (world.GetBlock(storeCell.X, storeCell.Y, storeCell.Z) != (Block)b
                    || world.GetOrientation(storeCell.X, storeCell.Y, storeCell.Z) != o) storageWrong++;
            }
        Check(storageWrong == 0, $"{stored}/{stored} block x orientation combinations store and read back ({storageWrong} wrong)");
        Check(world.SetBlock(storeCell.X, storeCell.Y, storeCell.Z, Block.Wood, Orientation.None)
            && world.GetOrientation(storeCell.X, storeCell.Y, storeCell.Z) == Orientation.None,
            "an orientation can be written back to None");
        Check(world.SetBlock(storeCell.X, storeCell.Y, storeCell.Z, Block.Grass, 13)
            && world.GetOrientation(storeCell.X, storeCell.Y, storeCell.Z) == 13,
            "storage stays permissive: a Upright block still stores any requested byte");
        world.SetBlock(storeCell.X, storeCell.Y, storeCell.Z, Block.Air);

        var fresh = world.CreateChunk(new Vector3I(90, 40, 90));
        var freshOrientation = fresh.GetComponent<ChunkBlocks>().Orientation;
        int stray = 0;
        for (int i = 0; i < freshOrientation.Length; i++)
            if (freshOrientation[i] != Orientation.None) stray++;
        Check(freshOrientation.Length == ChunkMesher.Volume && stray == 0,
            $"a fresh chunk is {ChunkMesher.Volume} x None ({stray} stray)");
        Check(world.GetOrientation(500, 3000, 500) == Orientation.None, "an unloaded chunk reads as None");
        Check(world.GetOrientation(-5, -80, 9) == Orientation.None, "an unloaded buried chunk reads as None");

        var same = new Vector3I(7, 3002, 7);
        Check(world.SetBlock(same.X, same.Y, same.Z, Block.Wood, 5), "place wood with orientation 5");
        Check(!world.SetBlock(same.X, same.Y, same.Z, Block.Wood, 5), "the identical block + orientation is a no-op");
        Check(world.SetBlock(same.X, same.Y, same.Z, Block.Wood, 7)
            && world.GetOrientation(same.X, same.Y, same.Z) == 7, "same block, new orientation still applies");
        Check(world.TryGetChunk(same.X, same.Y, same.Z, out var sameChunk) && sameChunk.Tags.Has<NeedsMesh>(),
            "an orientation-only edit still marks the chunk dirty");
        world.SetBlock(same.X, same.Y, same.Z, Block.Air);

        var requestCell = new Vector3I(8, 3003, 8);
        world.RequestEdit(EditRequest.Place(requestCell, Block.Wood, default, 13));
        Check(world.ApplyPendingEdits() == 1 && world.GetOrientation(requestCell.X, requestCell.Y, requestCell.Z) == 13,
            "the edit request pipeline carries the orientation");
        world.SetBlock(requestCell.X, requestCell.Y, requestCell.Z, Block.Air);
    }

    /// <summary>How many values of the canonical space {None} u ({1..24} minus byte 9) the
    /// type allows. Allows() answers for the alias byte 9 too, so counting must skip it.</summary>
    private static int AllowedCount(Block b)
    {
        int count = 0;
        for (int value = 0; value <= Orientation.Count; value++)
            if (value != Orientation.IdentityDuplicate && Blocks.Allows(b, (byte)value)) count++;
        return count;
    }

    /// <summary>Distinct allowed orientations the rotate key visits from <paramref name="start"/>
    /// before returning to it.</summary>
    private static int WalkAllowed(Block b, byte start)
    {
        var seen = new HashSet<int> { start };
        byte value = start;
        for (int step = 0; step < 64; step++)
        {
            value = Blocks.NextAllowed(b, value);
            seen.Add(value);
            if (value == start) break;
        }
        return seen.Count;
    }

    /// <summary>Distinct rotations reachable from <paramref name="start"/> by composing the
    /// generators on the left, i.e. the size of the generated subgroup.</summary>
    private static int ClosureSize(byte start, params byte[] generators)
    {
        var closure = new HashSet<int> { start };
        var queue = new Queue<int>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            foreach (byte g in generators)
            {
                int next = Orientation.Compose(g, (byte)current);
                if (closure.Add(next)) queue.Enqueue(next);
            }
        }
        return closure.Count;
    }

    private static bool SameRotation(byte a, byte b)
        => Orientation.ImageOfLocalAxis(a, 0) == Orientation.ImageOfLocalAxis(b, 0)
        && Orientation.ImageOfLocalAxis(a, 1) == Orientation.ImageOfLocalAxis(b, 1)
        && Orientation.ImageOfLocalAxis(a, 2) == Orientation.ImageOfLocalAxis(b, 2);

    /// <summary>Determinant of the matrix whose columns are the images of the local axes.</summary>
    private static int Determinant(byte orientation)
    {
        var x = Orientation.ImageOfLocalAxis(orientation, 0);
        var y = Orientation.ImageOfLocalAxis(orientation, 1);
        var z = Orientation.ImageOfLocalAxis(orientation, 2);
        return x.X * (y.Y * z.Z - y.Z * z.Y)
            - x.Y * (y.X * z.Z - y.Z * z.X)
            + x.Z * (y.X * z.Y - y.Y * z.X);
    }

    private static bool IsIdentity(byte orientation)
        => Orientation.ImageOfLocalAxis(orientation, 0) == FaceDirs[0]
        && Orientation.ImageOfLocalAxis(orientation, 1) == FaceDirs[2]
        && Orientation.ImageOfLocalAxis(orientation, 2) == FaceDirs[4];

    /// <summary>True when applying <paramref name="inv"/> to each image of <paramref name="o"/>
    /// returns the local axis again, i.e. inv is o's inverse.</summary>
    private static bool Inverts(byte o, byte inv)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            var image = Orientation.ImageOfLocalAxis(o, axis);
            int j = image.X != 0 ? 0 : image.Y != 0 ? 1 : 2;
            int sign = image.X + image.Y + image.Z;
            var back = Orientation.ImageOfLocalAxis(inv, j);
            if (back.X * sign != (axis == 0 ? 1 : 0)
                || back.Y * sign != (axis == 1 ? 1 : 0)
                || back.Z * sign != (axis == 2 ? 1 : 0)) return false;
        }
        return true;
    }

    /// <summary>True when the image of local <paramref name="localAxis"/> is exactly one world axis.</summary>
    private static bool AlongAxis(byte orientation, int localAxis, int worldAxis)
    {
        var image = Orientation.ImageOfLocalAxis(orientation, localAxis);
        int along = worldAxis == 0 ? image.X : worldAxis == 1 ? image.Y : image.Z;
        return Mathf.Abs(along) == 1
            && (worldAxis == 0 ? image.Y == 0 && image.Z == 0
                : worldAxis == 1 ? image.X == 0 && image.Z == 0
                : image.X == 0 && image.Y == 0);
    }

    private static bool Perpendicular(Vector3I a, Vector3I b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z == 0;

    private static int[] PackedCorners(int[] corners)
    {
        var packed = new int[4];
        for (int i = 0; i < 4; i++)
            packed[i] = corners[i * 3] | (corners[i * 3 + 1] << 1) | (corners[i * 3 + 2] << 2);
        return packed;
    }

    private static int[] LocalCornersOf(byte orientation, int[] corners)
    {
        var packed = new int[4];
        for (int i = 0; i < 4; i++)
            packed[i] = Orientation.LocalCorner(orientation, corners[i * 3], corners[i * 3 + 1], corners[i * 3 + 2]);
        return packed;
    }

    // ---- texture pack ----------------------------------------------------

    private static void CheckTileIndexMap()
    {
        // The default pack is what Game loads before Run (unless --pack= is passed), and it now
        // declares the six chest overrides. Load it explicitly so this map check is deterministic.
        TexPack.Load("res://texturepacks/default");

        // The frozen 54-cell fallback table from docs/texture-packs.md, rows by (int)Block,
        // cols by Face. The six chest cells' frozen fallback is the plank row (8); the default
        // pack's declared overrides resolve those cells to 60..65 instead (resolved != fallback).
        int[,] expected =
        {
            { 11, 11, 11, 11, 11, 11 }, // Air: never meshed -> missing (magenta)
            { 0, 0, 0, 0, 0, 0 },       // Stone
            { 1, 1, 1, 1, 1, 1 },       // Dirt
            { 3, 3, 2, 4, 3, 3 },       // Grass: sides, top, bottom
            { 5, 5, 5, 5, 5, 5 },       // Sand
            { 6, 6, 7, 7, 6, 6 },       // Wood: top and bottom share the end grain
            { 8, 8, 8, 8, 8, 8 },       // Plank
            { 9, 9, 9, 9, 9, 9 },       // Leaves
            { 10, 10, 10, 10, 10, 10 }, // Bedrock
            { 8, 8, 8, 8, 8, 8 },       // Chest: fallback is the plank row, overridden to 60..65 by the default pack
        };
        int bad = 0;
        for (int b = 0; b <= LastBlockIndex; b++)
            for (int f = 0; f < 6; f++)
            {
                // The frozen row is what a pack without chest overrides resolves to (asserted by
                // CheckFaceOverrides / CheckTextureVariants' base-only packs); the default pack's
                // declared override wins, so the resolved cell is KeyCount + 48 + f.
                int want = (Block)b == Block.Chest ? TexPack.KeyCount + 48 + f : expected[b, f];
                if (TexPack.TileIndex((Block)b, f) != want) bad++;
            }
        Check(bad == 0, $"54-cell Block+Face map: 48 frozen cells + the 6 default-pack chest overrides at 60..65 ({bad} wrong)");
        Check(TexPack.TileIndex(Block.Bedrock, Face.Top) == 10,
            "bedrock is covered from the Block enum, not Blocks.Palette");
    }

    private static void CheckFaceOverrides()
    {
        string baseDir = ProjectSettings.GlobalizePath("user://texpack_selftest");
        if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);

        // The frozen 54-cell table, rows by Renderable order, cols by Face (PosX..NegZ).
        int[] frozen =
        {
            0, 0, 0, 0, 0, 0,        // Stone
            1, 1, 1, 1, 1, 1,        // Dirt
            3, 3, 2, 4, 3, 3,        // Grass: sides, top, bottom
            5, 5, 5, 5, 5, 5,        // Sand
            6, 6, 7, 7, 6, 6,        // Wood: top and bottom share the end grain
            8, 8, 8, 8, 8, 8,        // Plank
            9, 9, 9, 9, 9, 9,        // Leaves
            10, 10, 10, 10, 10, 10,  // Bedrock
            8, 8, 8, 8, 8, 8,        // Chest: frozen fallback is the plank row
        };

        // (a) A base-only pack resolves every cell to the frozen table.
        string baseOnly = baseDir + "/facebase";
        WritePack(baseOnly, 1, AllTiles());
        for (int i = 0; i < TexPack.KeyCount; i++) WriteTile(baseOnly, $"tiles/{TexPack.Keys[i]}.png", 16, KeyColor(i));
        var plain = TexPack.Load(baseOnly);
        int wrong = FrozenMismatches(frozen);
        Check(plain.Source == baseOnly && plain.FaceOverrides == 0 && plain.Array.GetLayers() == TexPack.KeyCount
            && wrong == 0,
            $"base-only pack keeps the frozen 54-cell map in {TexPack.KeyCount} layers ({wrong} wrong)");
        Check(TexPack.TileIndex(Block.Air, 0) == 11, "Air still resolves to the missing tile (11)");

        // (b) `stone_top` claims cell 2 as layer KeyCount + 2; the other 53 cells hold.
        string faceOne = baseDir + "/faceone";
        WritePack(faceOne, 1, AllTiles() + ", \"stone_top\": \"tiles/stone_top.png\"");
        for (int i = 0; i < TexPack.KeyCount; i++) WriteTile(faceOne, $"tiles/{TexPack.Keys[i]}.png", 16, KeyColor(i));
        WriteTile(faceOne, "tiles/stone_top.png", 16, Colors.Red);
        var over = TexPack.Load(faceOne);
        wrong = 0;
        for (int b = 0; b < TexPack.Renderable.Length; b++)
            for (int f = 0; f < 6; f++)
                if (TexPack.TileIndex(TexPack.Renderable[b], f)
                    != (b == 0 && f == Face.Top ? TexPack.KeyCount + 2 : frozen[b * 6 + f])) wrong++;
        Check(over.Source == faceOne && over.FaceOverrides == 1 && over.Array.GetLayers() == TexPack.LayerCount
            && wrong == 0 && TexPack.TileIndex(Block.Stone, Face.Top) == TexPack.KeyCount + 2
            && TexPack.TileIndex(Block.Stone, Face.PosX) == 0 && TexPack.TileIndex(Block.Stone, Face.NegX) == 0
            && TexPack.TileIndex(Block.Stone, Face.Bottom) == 0 && TexPack.TileIndex(Block.Stone, Face.PosZ) == 0
            && TexPack.TileIndex(Block.Stone, Face.NegZ) == 0,
            $"stone_top claims layer {TexPack.KeyCount + 2} in a {TexPack.LayerCount}-layer array ({wrong} wrong)");
        Check(over.Sources[TexPack.KeyCount + 2] == TexPack.TileSource.Png, "the override layer came from its PNG");

        // (c) A provided override whose PNG is missing still claims the slot, as `missing`.
        string broken = baseDir + "/facebroken";
        WritePack(broken, 1, AllTiles() + ", \"stone_top\": \"tiles/gone.png\"");
        for (int i = 0; i < TexPack.KeyCount; i++) WriteTile(broken, $"tiles/{TexPack.Keys[i]}.png", 16, KeyColor(i));
        var brokenPack = TexPack.Load(broken);
        wrong = 0;
        for (int f = 0; f < 6; f++)
            if (f != Face.Top && TexPack.TileIndex(Block.Stone, f) != 0) wrong++;
        Check(brokenPack.Source == broken && brokenPack.FaceOverrides == 1
            && TexPack.TileIndex(Block.Stone, Face.Top) == TexPack.KeyCount + 2
            && brokenPack.Sources[TexPack.KeyCount + 2] == TexPack.TileSource.Missing && wrong == 0,
            $"a broken override PNG claims its slot as `missing` ({wrong} stone faces changed)");

        // (iv) A rejected (version 2) pack commits nothing: its override never reaches `_resolved`.
        string future = baseDir + "/facefuture";
        WritePack(future, 2, AllTiles() + ", \"stone_top\": \"tiles/stone_top.png\"");
        for (int i = 0; i < TexPack.KeyCount; i++) WriteTile(future, $"tiles/{TexPack.Keys[i]}.png", 16, KeyColor(i));
        WriteTile(future, "tiles/stone_top.png", 16, Colors.Red);
        var rejected = TexPack.Load(future);
        Check(rejected.Source != future && TexPack.TileIndex(Block.Stone, Face.Top) == 0,
            $"a rejected pack commits no override (landed on {rejected.Source})");

        // (ii) A later base-only pack is a complete table again: no override survives it.
        var again = TexPack.Load(baseOnly);
        wrong = FrozenMismatches(frozen);
        Check(again.Source == baseOnly && again.FaceOverrides == 0 && again.Array.GetLayers() == TexPack.KeyCount
            && wrong == 0,
            $"a later base-only pack clears the override ({wrong} wrong)");

        // (iii) The pack-less fall-through commits the fall-through pack's own table. Rung 3 is
        // res://texturepacks/default here, which since Chest ships its own six overrides resolves
        // those six cells at KeyCount + cell; every other cell is the frozen base. What this pins
        // is that no override from the previously loaded pack survives.
        var fallback = TexPack.Load(baseDir + "/does_not_exist");
        int stale = 0;
        for (int b = 0; b < TexPack.Renderable.Length; b++)
            for (int f = 0; f < 6; f++)
            {
                bool chest = TexPack.Renderable[b] == Block.Chest;
                int want = fallback.Procedural || !chest
                    ? frozen[b * 6 + f]
                    : TexPack.KeyCount + 48 + f;
                if (TexPack.TileIndex(TexPack.Renderable[b], f) != want) stale++;
            }
        Check((fallback.Procedural || fallback.Source == "res://texturepacks/default")
            && stale == 0 && TexPack.TileIndex(Block.Stone, Face.Top) == 0,
            $"a pack-less fall-through clears the stale override ({fallback.Source}, {stale} wrong)");

        if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);
    }

    /// <summary>How many of the 54 (Block,Face) cells disagree with the frozen literal table.</summary>
    private static int FrozenMismatches(int[] frozen)
    {
        int wrong = 0;
        for (int b = 0; b < TexPack.Renderable.Length; b++)
            for (int f = 0; f < 6; f++)
                if (TexPack.TileIndex(TexPack.Renderable[b], f) != frozen[b * 6 + f]) wrong++;
        return wrong;
    }

    private static void CheckResolveTilePath()
    {
        const string root = "user://texpack_selftest";
        Directory.CreateDirectory(ProjectSettings.GlobalizePath(root));
        string rootFull = System.IO.Path.GetFullPath(ProjectSettings.GlobalizePath(root));
        string[] rejected =
        {
            "", "   ", "../stone.png", "a/../b.png", "/abs.png", "C:/abs.png",
            "\\\\server\\share\\x.png", "..", "tiles/../..",
        };
        int accepted = 0;
        foreach (var raw in rejected)
            if (TexPack.ResolveTilePath(raw, root, rootFull) != null) accepted++;
        Check(accepted == 0, $"ResolveTilePath rejects {rejected.Length} traversal/absolute/empty paths ({accepted} accepted)");
        Check(TexPack.ResolveTilePath("tiles/stone.png", root, rootFull) == root + "/tiles/stone.png",
            "ResolveTilePath accepts a normal relative path");
    }

    private static void CheckPackLoading()
    {
        string baseDir = ProjectSettings.GlobalizePath("user://texpack_selftest");
        if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);

        // A legal pack: twelve 16x16 tiles, rung 1 hit.
        string legal = baseDir + "/legal";
        WritePack(legal, 1, AllTiles());
        for (int i = 0; i < TexPack.KeyCount; i++) WriteTile(legal, $"tiles/{TexPack.Keys[i]}.png", 16, KeyColor(i));
        var pack = TexPack.Load(legal);
        int pngLayers = 0;
        foreach (var s in pack.Sources) if (s == TexPack.TileSource.Png) pngLayers++;
        Check(pack.Source == legal && pack.Resolved == 12 && pack.Array != null && pack.Array.GetLayers() == 12
            && pngLayers == 12,
            $"legal pack loads all twelve tiles from rung 1 ({pack.Resolved}/12)");

        // An unsupported version rejects the whole pack and discovery moves on.
        string future = baseDir + "/future";
        WritePack(future, 2, AllTiles());
        for (int i = 0; i < TexPack.KeyCount; i++) WriteTile(future, $"tiles/{TexPack.Keys[i]}.png", 16, KeyColor(i));
        var rejected = TexPack.Load(future);
        Check(rejected.Source != future, $"version 2 pack is skipped whole (using {rejected.Source})");

        // A path escape loses only that tile, which becomes the missing tile.
        string escape = baseDir + "/escape";
        WritePack(escape, 1, AllTiles() + ", \"stone\": \"../escape.png\"");
        for (int i = 0; i < TexPack.KeyCount; i++) WriteTile(escape, $"tiles/{TexPack.Keys[i]}.png", 16, KeyColor(i));
        var escaped = TexPack.Load(escape);
        Check(escaped.Source == escape && escaped.Resolved == 11
            && escaped.Sources[0] == TexPack.TileSource.Missing && escaped.Sources[11] == TexPack.TileSource.Png,
            $"escaped stone path falls back to the missing tile ({escaped.Resolved}/12)");

        // A square tile that is not `tile_size` is no longer a failure: it gets its own class
        // (design P2.4). 11x16² + stone 8² -> class 0 = 16, class 1 = 8.
        string wrongSize = baseDir + "/size";
        WritePack(wrongSize, 1, AllTiles());
        for (int i = 0; i < TexPack.KeyCount; i++) WriteTile(wrongSize, $"tiles/{TexPack.Keys[i]}.png", 16, KeyColor(i));
        WriteTile(wrongSize, "tiles/stone.png", 8, Colors.Red);
        var sized = TexPack.Load(wrongSize);
        var sizedStone = TexPack.TileAt(Block.Stone, Face.Top, 0, 0, 0);
        Check(sized.Source == wrongSize && sized.Resolved == 12 && sized.Layers == 12 && sized.Classes == 2
            && sized.ClassSizes[1] == 8 && sized.ClassLayers[1] == 1 && sizedStone.Class == 1
            && sized.Sources[0] == TexPack.TileSource.Png,
            $"an 8x8 tile gets its own size class, not a fallback ({sized.Classes} classes, stone class {sizedStone.Class})");

        // Unknown key and unknown field are ignored with warnings; the pack still loads.
        string unknown = baseDir + "/unknown";
        WritePack(unknown, 1,
            AllTiles() + ", \"gravel\": \"tiles/gravel.png\", \"stone_topx\": \"tiles/stone_topx.png\"",
            extra: ", \"author\": \"nobody\"");
        for (int i = 0; i < TexPack.KeyCount; i++) WriteTile(unknown, $"tiles/{TexPack.Keys[i]}.png", 16, KeyColor(i));
        var ignored = TexPack.Load(unknown);
        Check(ignored.Source == unknown && ignored.Resolved == 12
            && TexPack.TileIndex(Block.Stone, Face.Top) == 0,
            $"unknown keys and fields are ignored, the pack still loads ({ignored.Resolved}/12)");

        // With no usable `missing` tile, a broken tile takes its own procedural colour.
        string noMissing = baseDir + "/nomissing";
        WritePack(noMissing, 1, AllTiles(includeMissing: false) + ", \"stone\": \"tiles/gone.png\"");
        for (int i = 0; i < TexPack.KeyCount - 1; i++) WriteTile(noMissing, $"tiles/{TexPack.Keys[i]}.png", 16, KeyColor(i));
        var last = TexPack.Load(noMissing);
        Check(last.Source == noMissing && last.Resolved == 10
            && last.Sources[0] == TexPack.TileSource.Procedural && last.Sources[11] == TexPack.TileSource.Procedural,
            "without a missing tile: broken tile uses its procedural colour, missing is magenta");

        if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);
    }

    /// <summary>Storage is deliberately loose (SetBlock keeps any byte) and the next stage's
    /// no-memory sentinel is 255, so every byte-indexed Orientation table must answer for the
    /// whole byte domain instead of throwing. Bytes 25..255 read as the identity.</summary>
    private static void CheckOrientationByteDomain()
    {
        GD.Print("  ---- orientation over the whole byte domain ----");
        int[] probes = { 25, 26, 100, 200, 255 };

        int identityWrong = 0, uvWrong = 0, composeWrong = 0;
        foreach (int probe in probes)
        {
            byte o = (byte)probe;
            for (int w = 0; w < 6; w++)
                if (Orientation.LocalFace(o, w) != w) identityWrong++;
            for (int c = 0; c < 8; c++)
                if (Orientation.LocalCorner(o, c & 1, (c >> 1) & 1, (c >> 2) & 1) != c) identityWrong++;
            if (Orientation.Inverse(o) != Orientation.None) identityWrong++;
            if (Orientation.ToIndex(o) != -1) identityWrong++;
            if (Orientation.ImageOfLocalAxis(o, 1) != Vector3I.Up) identityWrong++;
            for (int f = 0; f < 6; f++)
                for (int c = 0; c < 8; c++)
                    if (Orientation.Uv(o, f, c & 1, (c >> 1) & 1, (c >> 2) & 1)
                        != Orientation.Uv(Orientation.None, f, c & 1, (c >> 1) & 1, (c >> 2) & 1)) uvWrong++;
            for (int b = 0; b < 256; b++)
            {
                if (Orientation.Compose(o, (byte)b) != Orientation.Compose(Orientation.None, (byte)b)) composeWrong++;
                if (Orientation.Compose((byte)b, o) != Orientation.Compose((byte)b, Orientation.None)) composeWrong++;
            }
        }
        Check(identityWrong == 0,
            $"the 5 out-of-range bytes read as the identity rotation ({identityWrong} wrong over 5 bytes)");
        Check(uvWrong == 0, $"out-of-range bytes keep the identity tile UVs on all six faces ({uvWrong} wrong)");
        Check(composeWrong == 0, $"Compose with an out-of-range byte changes nothing and never throws ({composeWrong} wrong)");

        // The whole domain, not just the probes: 256 bytes x (6 faces + 8 corners + inverse).
        int reads = 0;
        for (int b = 0; b < 256; b++)
        {
            for (int w = 0; w < 6; w++) { _ = Orientation.LocalFace((byte)b, w); reads++; }
            for (int c = 0; c < 8; c++) { _ = Orientation.LocalCorner((byte)b, c & 1, (c >> 1) & 1, (c >> 2) & 1); reads++; }
            _ = Orientation.Inverse((byte)b);
            reads++;
        }
        Check(reads == 256 * 15, $"{reads}/{256 * 15} byte-domain reads answered without throwing");

        // Uv is the mesher's entry point and takes the stored byte directly: 256 x 6 x 8 reads.
        int uvReads = 0;
        for (int b = 0; b < 256; b++)
            for (int f = 0; f < 6; f++)
                for (int c = 0; c < 8; c++)
                {
                    _ = Orientation.Uv((byte)b, f, c & 1, (c >> 1) & 1, (c >> 2) & 1);
                    uvReads++;
                }
        Check(uvReads == 256 * 48, $"{uvReads}/{256 * 48} Uv reads over the byte domain answered without throwing");
    }

    private static void CheckMesherOrientation(VoxelWorld world)
    {
        GD.Print("  ---- mesher orientation ----");

        var entity = world.CreateChunk(new Vector3I(97, 97, 97));
        var coord = entity.GetComponent<ChunkCoord>();
        var blocks = entity.GetComponent<ChunkBlocks>().Value;
        var stored = entity.GetComponent<ChunkBlocks>().Orientation;
        Array.Clear(blocks);
        Array.Clear(stored);
        blocks[ChunkBlocks.Index(0, 0, 0)] = (byte)Block.Grass;

        // The 48 identity UVs, pinned literally in Face order and FaceCorners order: the art
        // starts at the top-left of every face and v runs downward.
        float[,,] pinned =
        {
            { { 0, 1 }, { 1, 1 }, { 1, 0 }, { 0, 0 } }, // PosX
            { { 0, 1 }, { 1, 1 }, { 1, 0 }, { 0, 0 } }, // NegX
            { { 0, 1 }, { 1, 1 }, { 1, 0 }, { 0, 0 } }, // Top
            { { 0, 1 }, { 1, 1 }, { 1, 0 }, { 0, 0 } }, // Bottom
            { { 0, 1 }, { 1, 1 }, { 1, 0 }, { 0, 0 } }, // PosZ
            { { 0, 1 }, { 1, 1 }, { 1, 0 }, { 0, 0 } }, // NegZ
        };
        // The v1 table the normalisation replaced, same corner order. PosX, Bottom and NegZ are
        // the three faces the right-handed rule mirrors (u for PosX/NegZ, v for Bottom).
        float[,,] v1 =
        {
            { { 1, 1 }, { 0, 1 }, { 0, 0 }, { 1, 0 } }, // PosX, was (z, 1 - y)
            { { 0, 1 }, { 1, 1 }, { 1, 0 }, { 0, 0 } }, // NegX, unchanged
            { { 0, 1 }, { 1, 1 }, { 1, 0 }, { 0, 0 } }, // Top, unchanged
            { { 0, 0 }, { 1, 0 }, { 1, 1 }, { 0, 1 } }, // Bottom, was (x, z)
            { { 0, 1 }, { 1, 1 }, { 1, 0 }, { 0, 0 } }, // PosZ, unchanged
            { { 1, 1 }, { 0, 1 }, { 0, 0 }, { 1, 0 } }, // NegZ, was (x, 1 - y)
        };

        stored[0] = Orientation.None;
        var identity = ChunkMesher.Build(world, coord, blocks, out _).SurfaceGetArrays(0);
        var identityUv = (Vector2[])identity[(int)Mesh.ArrayType.TexUV];
        var identityNorm = (Vector3[])identity[(int)Mesh.ArrayType.Normal];

        int pinnedWrong = 0, changeWrong = 0, closureWrong = 0;
        var quads = new int[6][];
        var identityU = new Vector3[6];
        var identityV = new Vector3[6];
        for (int f = 0; f < 6; f++)
        {
            quads[f] = FaceVertices(f, identityNorm);
            if (quads[f] == null || !UvAxes(identity, quads[f], out identityU[f], out identityV[f]))
            {
                pinnedWrong++;
                changeWrong++;
                closureWrong++;
                continue;
            }
            for (int i = 0; i < 4; i++)
            {
                var got = identityUv[quads[f][i]];
                if (got != new Vector2(pinned[f, i, 0], pinned[f, i, 1])) pinnedWrong++;

                bool uMirrored = f is Face.PosX or Face.NegZ;
                bool vMirrored = f == Face.Bottom;
                var old = new Vector2(v1[f, i, 0], v1[f, i, 1]);
                if (got != new Vector2(uMirrored ? 1 - old.X : old.X, vMirrored ? 1 - old.Y : old.Y))
                    changeWrong++;

                int cx = FaceCorners[f][i * 3], cy = FaceCorners[f][i * 3 + 1], cz = FaceCorners[f][i * 3 + 2];
                if (Orientation.Uv(Orientation.None, f, cx, cy, cz) != ChunkMesher.TileUv(f, cx, cy, cz))
                    closureWrong++;
            }
        }
        Check(pinnedWrong == 0, $"{48 - pinnedWrong}/48 identity UVs match the pinned normalised table");
        Check(changeWrong == 0,
            $"the 3 normalised faces are the v1 table exactly mirrored, the other 3 unchanged ({48 - changeWrong}/48)");
        Check(closureWrong == 0,
            $"identity orientation keeps the mesher's own TileUv on 48/48 corners ({closureWrong} wrong)");

        int bases = 0;
        for (int f = 0; f < 6; f++)
            if (RightHanded(identity, quads[f])) bases++;
        Check(bases == 6, $"{bases}/6 face UV bases are right-handed, u x v = -n ({6 - bases} wrong)");

        int pairs = 0, pairsWrong = 0, pure = 0, flips = 0, setWrong = 0, seamWrong = 0, rotated = 0, rotatedWrong = 0;
        for (int i = 0; i <= Orientation.Count; i++)
        {
            if (i == Orientation.IdentityDuplicate) continue; // byte 9 is the identity again
            byte o = (byte)i;
            stored[0] = o;
            var arrays = ChunkMesher.Build(world, coord, blocks, out _).SurfaceGetArrays(0);
            var norms = (Vector3[])arrays[(int)Mesh.ArrayType.Normal];
            var uvs = (Vector2[])arrays[(int)Mesh.ArrayType.TexUV];
            var uv2s = (Vector2[])arrays[(int)Mesh.ArrayType.TexUV2];

            // The seam the ghost preview renders through: same emitter, so equal per element.
            var preview = ChunkMesher.BuildBlock(Block.Grass, o).SurfaceGetArrays(0);
            seamWrong += SameBlockArrays(arrays, preview);

            for (int f = 0; f < 6; f++)
            {
                var q = FaceVertices(f, norms);
                if (q == null) { pairsWrong++; flips++; continue; }
                int want = TexPack.TileIndex(Block.Grass, Orientation.LocalFace(o, f));
                bool tileOk = true;
                var got = new Vector2[4];
                var id = new Vector2[4];
                for (int k = 0; k < 4; k++)
                {
                    if (uv2s[q[k]] != new Vector2(want, 0f)) tileOk = false;
                    got[k] = uvs[q[k]];
                    id[k] = identityUv[quads[f][k]];
                }
                pairs++;
                if (!tileOk) pairsWrong++;

                // The tile's own u/v axes must be the LOCAL face's identity axes carried by
                // the block's rotation. Orientation's axis images are the oracle, so this does
                // not read the UV table back at itself.
                int localFace = Orientation.LocalFace(o, f);
                if (UvAxes(arrays, q, out var u3, out var v3)
                    && u3 == Rotated(o, identityU[localFace]) && v3 == Rotated(o, identityV[localFace]))
                    rotated++;
                else
                    rotatedWrong++;

                var gotSorted = SortedUv(got);
                var idSorted = SortedUv(id);
                bool sameSet = true;
                for (int k = 0; k < 4; k++)
                    if (gotSorted[k] != idSorted[k]) sameSet = false;
                if (!sameSet) setWrong++;
                else if (UvArea(got) * UvArea(id) <= 0f) flips++;
                else pure++;
            }
        }
        Check(pairs == 144 && pairsWrong == 0,
            $"{pairs}/144 rotation x world-face pairs pick the LOCAL face's tile ({pairsWrong} wrong)");
        Check(pure == 144 && flips == 0 && setWrong == 0,
            $"{pure}/144 rotated face UVs are a pure rotation of identity, {flips} mirrored, {setWrong} changed corner set");
        Check(rotated == 144 && rotatedWrong == 0,
            $"{rotated}/144 face UV bases are the local axes carried by that rotation ({rotatedWrong} wrong)");
        Check(seamWrong == 0,
            $"BuildBlock equals Build per element over 24/24 orientations x 6 attributes ({seamWrong} mismatched)");
    }

    /// <summary>Canonical and policy-legal for the type: 0..24 and never the duplicate byte 9.</summary>
    private static bool Canonical(Block b, byte orientation)
        => orientation <= Orientation.Count
        && orientation != Orientation.IdentityDuplicate
        && Blocks.Allows(b, orientation);

    /// <summary>Placement preview: rotate keys, sticky memory, the write-site guard and the
    /// ghost node's own mesh / visibility (design.md section 5).</summary>
    private static void CheckPlacementPreview(VoxelWorld world)
    {
        GD.Print("  ---- placement preview ----");

        int canonical = 0;
        for (int value = 0; value <= Orientation.Count; value++)
            if (value != Orientation.IdentityDuplicate) canonical++;
        Check(canonical == 24, $"the canonical space holds 24 values (byte 9 excluded) ({canonical})");

        int countWrong = 0;
        for (int b = 1; b <= LastBlockIndex; b++)
        {
            int expected = Blocks.PolicyOf((Block)b) switch
            {
                OrientationPolicy.Any => 24,
                OrientationPolicy.Upright => 4,
                OrientationPolicy.Axis => 6,
                _ => 1,
            };
            if (AllowedCount((Block)b) != expected) countWrong++;
        }
        Check(countWrong == 0,
            $"policy counts match the canonical space (Any 24, Upright 4, Axis 6, Fixed 1); {countWrong} wrong");

        var probe = new CharacterBody3D { Name = "PreviewProbe", Position = new Vector3(100.5f, 3000f, 100.5f) };
        world.AddChild(probe);
        var entity = world.Store.CreateEntity(
            new PlayerBody { Node = probe },
            new PlayerState { Selected = Block.Stone },
            default(PlayerIntent));
        ref var state = ref entity.GetComponent<PlayerState>();
        ref var intent = ref entity.GetComponent<PlayerIntent>();

        // -- the rotate keys reach exactly the allowed set -------------------
        int distinctWrong = 0, illegalWrong = 0, wrapWrong = 0, inverseWrong = 0;
        var seen = new HashSet<int>();
        for (int b = 1; b <= LastBlockIndex; b++)
        {
            var block = (Block)b;
            int allowed = AllowedCount(block);
            state.Selected = block;
            state.StickyBlock = Block.Air;
            state.StickyOrientation = Orientation.None;
            state.Yaw = 0f;
            state.Pitch = 0f;
            state.PendingOrientation = BlockBehaviors.PlaceOrientation(block, Face.Top, 0f, 0f);
            byte start = state.PendingOrientation;

            seen.Clear();
            for (int press = 0; press < allowed; press++)
            {
                intent.RotateNext = 1;
                PlayerSystems.UpdatePending(ref state, ref intent, null);
                byte value = state.PendingOrientation;
                seen.Add(value);
                if (!Canonical(block, value)) illegalWrong++;

                // Q immediately followed by E is the identity on every state.
                byte before = value;
                intent.RotateNext = 1;
                intent.RotatePrev = 1;
                PlayerSystems.UpdatePending(ref state, ref intent, null);
                if (state.PendingOrientation != before) inverseWrong++;
            }
            if (seen.Count != allowed) distinctWrong++;
            if (state.PendingOrientation != start) wrapWrong++;
        }
        Check(distinctWrong == 0, $"{LastBlockIndex - distinctWrong}/{LastBlockIndex} blocks visit exactly |allowed| distinct states (24/24 Any, 4/4 Grass, 4/4 Chest)");
        Check(illegalWrong == 0, $"every state the rotate keys produce is canonical and Allows-legal ({illegalWrong} bad)");
        Check(wrapWrong == 0, $"|allowed| presses wrap back to the start on all {LastBlockIndex} blocks ({wrapWrong} wrong)");
        Check(inverseWrong == 0, $"Q then E returns to the previous state ({inverseWrong} wrong)");

        state.Selected = Block.Stone;
        state.StickyBlock = Block.Air;
        state.PendingOrientation = Orientation.None;
        seen.Clear();
        for (int press = 0; press < 24; press++)
        {
            intent.RotateNext = 1;
            PlayerSystems.UpdatePending(ref state, ref intent, null);
            seen.Add(state.PendingOrientation);
        }
        Check(seen.Count == 24 && state.PendingOrientation == Orientation.None,
            $"Stone (Any) reaches 24/24 distinct states and wraps ({seen.Count})");

        state.Selected = Block.Grass;
        state.StickyBlock = Block.Air;
        state.PendingOrientation = Orientation.None;
        var spin = new List<byte>();
        for (int press = 0; press < 4; press++)
        {
            intent.RotateNext = 1;
            PlayerSystems.UpdatePending(ref state, ref intent, null);
            spin.Add(state.PendingOrientation);
        }
        Check(spin.Count == 4 && spin[0] == 10 && spin[1] == 11 && spin[2] == 12 && spin[3] == Orientation.None,
            $"Grass (Upright) Q spin is 0 -> 10 -> 11 -> 12 -> 0 ([{string.Join(", ", spin)}])");

        // -- sticky memory ---------------------------------------------------
        state.Selected = Block.Wood;
        state.StickyBlock = Block.Air;
        state.StickyOrientation = 17; // a leftover value that must not be copied
        state.PendingOrientation = 5;
        state.Yaw = Mathf.Pi * 0.5f;
        state.Pitch = 0f;
        var face = new PlacementTarget(new Vector3I(0, 3000, 0), Face.NegZ);
        PlayerSystems.UpdatePending(ref state, ref intent, face);
        byte shown = state.PendingOrientation;
        Check(shown == 22 && state.StickyBlock == Block.Air,
            $"no memory: the rule seeds the clicked face + yaw ({shown}, want 22, not the stale 17)");

        byte placed = PlayerSystems.PlaceAt(world, entity, new Vector3I(7, 3006, 7));
        Check(placed == shown && state.StickyBlock == Block.Wood && state.StickyOrientation == shown,
            $"an accepted placement remembers exactly the byte the ghost showed ({placed})");
        Check(world.ApplyPendingEdits() == 1 && world.GetOrientation(7, 3006, 7) == placed,
            "the remembered byte is the byte that reached the voxel");
        world.SetBlock(7, 3006, 7, Block.Air);

        state.Yaw = Mathf.Pi; // the rule would now derive a different byte
        PlayerSystems.UpdatePending(ref state, ref intent, face);
        byte ruleNow = BlockBehaviors.PlaceOrientation(Block.Wood, Face.NegZ, state.Yaw, state.Pitch);
        Check(state.PendingOrientation == placed && ruleNow != placed,
            $"the memory overrides the rule after a yaw change (placed {placed}, rule would be {ruleNow})");

        intent.RotateNext = 1;
        PlayerSystems.UpdatePending(ref state, ref intent, face);
        Check(state.StickyBlock == Block.Wood && state.StickyOrientation == state.PendingOrientation
            && state.PendingOrientation != placed && Blocks.Allows(Block.Wood, state.PendingOrientation),
            $"a rotate press writes both pending and memory ({state.PendingOrientation})");

        state.Selected = Block.Grass;
        state.StickyBlock = Block.Wood;
        state.StickyOrientation = 10; // upright: Grass keeps it
        state.PendingOrientation = 10;
        PlayerSystems.UpdatePending(ref state, ref intent, face);
        Check(state.StickyBlock == Block.Grass && state.StickyOrientation == 10 && state.PendingOrientation == 10,
            $"a type change snaps instead of discarding (Grass keeps {state.StickyOrientation})");

        state.Selected = Block.Grass;
        state.StickyBlock = Block.Wood;
        state.StickyOrientation = 1; // not upright for Grass
        state.PendingOrientation = 1;
        PlayerSystems.UpdatePending(ref state, ref intent, face);
        Check(state.StickyOrientation == Blocks.Snap(Block.Grass, 1) && state.StickyOrientation == Orientation.None
            && Blocks.Allows(Block.Grass, state.PendingOrientation),
            $"a memory illegal for the new type is projected, not dropped ({state.StickyOrientation})");

        // -- no out-of-range byte escapes ------------------------------------
        int escapes = 0, placements = 0, updates = 0;
        var rng = new Random(20260923);
        for (int step = 0; step < 400; step++)
        {
            state.Selected = Blocks.Palette[rng.Next(Blocks.Palette.Length)];
            if (rng.Next(3) == 0)
            {
                intent.RotateNext = rng.Next(5);
                intent.RotatePrev = rng.Next(5);
            }
            if (rng.Next(3) == 0) state.Yaw = (float)(rng.NextDouble() * 12.0 - 6.0);
            if (rng.Next(3) == 0) state.Pitch = (float)(rng.NextDouble() * 3.0 - 1.5);
            PlacementTarget? target = rng.Next(2) == 0
                ? null
                : new PlacementTarget(new Vector3I(0, 3000, 0), rng.Next(6));
            PlayerSystems.UpdatePending(ref state, ref intent, target);
            updates++;

            if (!Canonical(state.Selected, state.PendingOrientation)) escapes++;
            if (state.StickyBlock != Block.Air && !Canonical(state.StickyBlock, state.StickyOrientation)) escapes++;

            if (rng.Next(2) == 0)
            {
                var cell = new Vector3I(21, 3008, 21);
                byte sent = PlayerSystems.PlaceAt(world, entity, cell);
                placements++;
                if (!Canonical(state.Selected, sent)
                    || state.StickyOrientation != sent || state.StickyBlock != state.Selected) escapes++;
                world.ApplyPendingEdits();
                if (world.GetOrientation(cell.X, cell.Y, cell.Z) != sent) escapes++;
                world.SetBlock(cell.X, cell.Y, cell.Z, Block.Air);
            }
        }
        Check(escapes == 0,
            $"{updates} updates and {placements} placements leave no out-of-range byte (pending, memory and world bytes all canonical)");

        // -- the ghost node renders the pending (block, orientation) ----------
        var ghost = new PlacementGhost { Name = "PreviewGhost" };
        world.AddChild(ghost);
        var ghostCell = new Vector3I(6, 3007, 6);
        byte[] meshOrientations = { Orientation.None, 10, 13 };
        int meshWrong = 0, transformWrong = 0;
        foreach (byte o in meshOrientations)
        {
            ghost.Update(world, new PlacementTarget(ghostCell, Face.Top), Block.Grass, o);
            var got = ghost.Mesh.Mesh.SurfaceGetArrays(0);
            var want = ChunkMesher.BuildBlock(Block.Grass, o).SurfaceGetArrays(0);
            meshWrong += SameBlockArrays(got, want);
            if (ghost.Position != (Vector3)ghostCell || ghost.Scale != Vector3.One || ghost.Rotation != Vector3.Zero)
                transformWrong++;
        }
        Check(meshWrong == 0, $"the ghost's own mesh equals BuildBlock over 3 orientations ({meshWrong} mismatched)");
        Check(transformWrong == 0, $"the ghost transform is a pure translation to the cell ({transformWrong} wrong)");

        var ghostMaterial = ghost.Mesh.MaterialOverride as ShaderMaterial;
        Check(ghostMaterial != null && !ReferenceEquals(ghostMaterial, VoxelWorld.ChunkMaterial),
            "the ghost draws through its own material instance, not the chunk material");
        Check(ghostMaterial != null && ghostMaterial.Shader != VoxelWorld.ChunkMaterial.Shader,
            "the ghost uses the transparent ghost shader, not the chunk shader");

        // -- hidden when the click would be refused, visible otherwise -------
        ghost.Update(world, null, Block.Stone, Orientation.None);
        Check(!ghost.Visible, "ghost hidden with no target");

        var insidePlayer = new Vector3I(Mathf.FloorToInt(probe.Position.X), Mathf.FloorToInt(probe.Position.Y),
            Mathf.FloorToInt(probe.Position.Z));
        Check(PlayerSystems.PlacementRefused(world, probe, insidePlayer), "a cell overlapping the player box is refused");
        var occupied = new Vector3I(11, 3009, 11);
        world.SetBlock(occupied.X, occupied.Y, occupied.Z, Block.Stone);
        Check(PlayerSystems.PlacementRefused(world, probe, occupied), "an occupied cell is refused");
        var clear = new Vector3I(11, 3010, 11);
        Check(!PlayerSystems.PlacementRefused(world, probe, clear), "a clear cell outside the player box is allowed");

        PlacementTarget? skyTarget = null;
        try { skyTarget = PlayerSystems.PendingTarget(world, probe, 0f, Mathf.Pi * 0.5f); }
        catch (Exception e) { GD.Print($"  info  PendingTarget skipped (no physics under --headless): {e.GetType().Name}"); }
        Check(skyTarget == null, "no target looking straight up (sky)");
        ghost.Update(world, skyTarget, Block.Stone, Orientation.None);
        Check(!ghost.Visible, "ghost hidden when the integrated target is null");
        ghost.Update(world, new PlacementTarget(clear, Face.Top), Block.Stone, Orientation.None);
        Check(ghost.Visible && ghost.Mesh.Mesh != null && ghost.Position == (Vector3)clear,
            "ghost visible on a legal target, positioned at the cell");
        world.SetBlock(occupied.X, occupied.Y, occupied.Z, Block.Air);

        entity.DeleteEntity();
        ghost.Free();
        probe.Free();
    }

    private static void CheckMesherTextureArrays(VoxelWorld world)
    {
        var entity = world.CreateChunk(new Vector3I(90, 90, 90));
        var blocks = entity.GetComponent<ChunkBlocks>().Value;
        Array.Clear(blocks);
        blocks[ChunkBlocks.Index(8, 8, 8)] = (byte)Block.Grass;
        var mesh = ChunkMesher.Build(world, entity.GetComponent<ChunkCoord>(), blocks, out _);
        var arrays = mesh.SurfaceGetArrays(0);
        var norms = (Vector3[])arrays[(int)Mesh.ArrayType.Normal];
        var colors = (Color[])arrays[(int)Mesh.ArrayType.Color];
        var uv = (Vector2[])arrays[(int)Mesh.ArrayType.TexUV];
        var uv2 = (Vector2[])arrays[(int)Mesh.ArrayType.TexUV2];
        float[] tint = { 0.72f, 0.72f, 1.00f, 0.45f, 0.86f, 0.86f };

        int wrongTile = 0, wrongColor = 0, outOfRange = 0;
        for (int i = 0; i < norms.Length; i++)
        {
            int face = FaceOfNormal(norms[i]);
            if (uv2[i].X != TexPack.TileIndex(Block.Grass, face) || uv2[i].Y != 0f) wrongTile++;
            if (!TintClose(colors[i].R, tint[face]) || !TintClose(colors[i].G, tint[face])
                || !TintClose(colors[i].B, tint[face])) wrongColor++;
            if (uv[i].X < 0f || uv[i].X > 1f || uv[i].Y < 0f || uv[i].Y > 1f) outOfRange++;
        }
        Check(norms.Length == 24 && wrongTile == 0,
            $"mesher writes the frozen tile index to UV2.x on all six faces ({wrongTile} wrong)");
        Check(wrongColor == 0, $"mesher COLOR is FaceTint only, not block colour x tint ({wrongColor} wrong)");
        Check(outOfRange == 0, $"tile-local UV stays inside [0,1] ({outOfRange} outside)");
        Check(TexPack.TileIndex(Block.Grass, Face.Top) != TexPack.TileIndex(Block.Grass, Face.PosX),
            "grass top and side use different tile layers");
    }

    // ---- texture variants ------------------------------------------------

    /// <summary>Design §2 (slot allocation), §3 (position hash), §4 (failure matrix) and §6
    /// (E1-E9). Every check can fail; the distribution histogram is printed raw.</summary>
    private static void CheckTextureVariants(VoxelWorld world)
    {
        GD.Print("  ---- texture variants ----");
        string baseDir = ProjectSettings.GlobalizePath("user://texpack_selftest");
        if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);

        // E4: no-variant packs keep the pre-variants layout — layer numbers, not "it renders".
        // 54 cells: the Chest row's fallback is the plank row (8), same as the frozen table.
        int[] frozen =
        {
            0, 0, 0, 0, 0, 0,        // Stone
            1, 1, 1, 1, 1, 1,        // Dirt
            3, 3, 2, 4, 3, 3,        // Grass: sides, top, bottom
            5, 5, 5, 5, 5, 5,        // Sand
            6, 6, 7, 7, 6, 6,        // Wood: top and bottom share the end grain
            8, 8, 8, 8, 8, 8,        // Plank
            9, 9, 9, 9, 9, 9,        // Leaves
            10, 10, 10, 10, 10, 10,  // Bedrock
            8, 8, 8, 8, 8, 8,        // Chest: frozen fallback is the plank row
        };
        string baseOnly = baseDir + "/varbase";
        WritePack(baseOnly, 1, AllTiles());
        for (int i = 0; i < TexPack.KeyCount; i++) WriteTile(baseOnly, $"tiles/{TexPack.Keys[i]}.png", 16, KeyColor(i));
        var plain = TexPack.Load(baseOnly);
        int wrong = 0;
        for (int b = 0; b < TexPack.Renderable.Length; b++)
            for (int f = 0; f < 6; f++)
            {
                var got = TexPack.LayersFor(TexPack.Renderable[b], f);
                if (got.Length != 1 || got[0] != frozen[b * 6 + f]) wrong++;
            }
        int nonPng = 0;
        foreach (var source in plain.Sources) if (source != TexPack.TileSource.Png) nonPng++;
        Check(wrong == 0 && nonPng == 0 && plain.Array.GetLayers() == TexPack.KeyCount
            && plain.Layers == TexPack.KeyCount,
            $"E4 no-variant pack: frozen one-layer map in {plain.Layers} all-Png layers ({wrong} cells wrong, {nonPng} non-Png)");

        string oneOverride = baseDir + "/varone";
        WritePack(oneOverride, 1, AllTiles() + ", \"stone_top\": \"tiles/stone_top.png\"");
        for (int i = 0; i < TexPack.KeyCount; i++) WriteTile(oneOverride, $"tiles/{TexPack.Keys[i]}.png", 16, KeyColor(i));
        WriteTile(oneOverride, "tiles/stone_top.png", 16, Colors.Red);
        var over = TexPack.Load(oneOverride);
        wrong = 0;
        for (int b = 0; b < TexPack.Renderable.Length; b++)
            for (int f = 0; f < 6; f++)
            {
                int want = b == 0 && f == Face.Top ? TexPack.KeyCount + 2 : frozen[b * 6 + f];
                var got = TexPack.LayersFor(TexPack.Renderable[b], f);
                if (got.Length != 1 || got[0] != want) wrong++;
            }
        Check(wrong == 0 && over.Array.GetLayers() == TexPack.LayerCount && over.Layers == TexPack.LayerCount,
            $"E4 override pack: stone_top owns layer {TexPack.KeyCount + 2}, the other 53 cells stay frozen ({wrong} wrong)");

        // A variant pack changes the array size: stone x4 + 11 singles = 15 layers.
        string varied = baseDir + "/var4";
        WriteVariantPack(varied, stoneCount: 4, dirtCount: 1);
        var stone4 = TexPack.Load(varied);
        var stoneLayers = TexPack.LayersFor(Block.Stone, Face.Top);
        string stoneList = string.Join(", ", stoneLayers);
        Check(stone4.Source == varied && stone4.Layers == 15 && stone4.VariantKeys == 1
            && stone4.Array.GetLayers() == 15 && stoneLayers.Length == 4 && stoneLayers[0] == 0 && stoneLayers[3] == 3,
            $"allocation: stone×4 pack is {stone4.Layers} layers, {stone4.VariantKeys} key with variants, stone owns [{stoneList}]");

        // E1/E6: a real all-stone chunk is deterministic and stays inside LayersFor.
        var chunk = world.CreateChunk(new Vector3I(120, 70, 120));
        var blocks = chunk.GetComponent<ChunkBlocks>().Value;
        Array.Fill(blocks, (byte)Block.Stone);
        var coord = chunk.GetComponent<ChunkCoord>();
        var first = ChunkMesher.Build(world, coord, blocks, out _).SurfaceGetArrays(0);
        var second = ChunkMesher.Build(world, coord, blocks, out _).SurfaceGetArrays(0);
        var norms = (Vector3[])first[(int)Mesh.ArrayType.Normal];
        var uv2 = (Vector2[])first[(int)Mesh.ArrayType.TexUV2];
        var uv2b = (Vector2[])second[(int)Mesh.ArrayType.TexUV2];
        int outside = 0;
        for (int i = 0; i < uv2.Length; i++)
            if (uv2[i].Y != 0f
                || Array.IndexOf(TexPack.LayersFor(Block.Stone, FaceOfNormal(norms[i])), (int)uv2[i].X) < 0) outside++;
        Check(uv2.Length == 6144 && outside == 0 && SameValues(uv2, uv2b),
            $"E1 16³ stone chunk: {uv2.Length} UV2 verts all inside LayersFor, rebuild byte-identical ({outside} outside)");

        // E2: an edit outside the chunk forces the remesh path; the mesh must not move.
        int bx = coord.X * 16, by = coord.Y * 16, bz = coord.Z * 16;
        bool placed = world.SetBlock(bx + 18, by + 8, bz + 8, Block.Dirt);
        var third = ChunkMesher.Build(world, coord, blocks, out _).SurfaceGetArrays(0);
        Check(placed && SameValues(uv2, (Vector2[])third[(int)Mesh.ArrayType.TexUV2]),
            "E2 remesh after an edit two cells outside the chunk keeps UV2 identical");
        world.SetBlock(bx + 18, by + 8, bz + 8, Block.Air);

        // E3: same local cell, different chunks — a chunk-local hash fails all six faces.
        Check(CrossContextFaces(world, new Vector3I(130, 70, 130)) == 0
            && CrossContextFaces(world, new Vector3I(150, 70, 130)) == 0,
            "E3 two chunks, same local cell: UV2 == absolute-coordinate TileIndex on all six faces");

        // E5/E6: hash spread + bounds over 256 samples, histogram printed raw.
        int[] hist = new int[stoneLayers.Length];
        int outsideSample = 0;
        for (int z = 0; z < 16; z++)
            for (int x = 0; x < 16; x++)
            {
                int layer = TexPack.TileAt(Block.Stone, Face.Top, x, 7, z).Layer;
                int slot = Array.IndexOf(stoneLayers, layer);
                if (slot < 0) outsideSample++; else hist[slot]++;
            }
        int used = 0, low = int.MaxValue, high = 0;
        foreach (int count in hist)
        {
            if (count > 0) used++;
            low = Math.Min(low, count);
            high = Math.Max(high, count);
        }
        string histLine = string.Join(" ", hist);
        GD.Print($"  hist  stone×4: {histLine}");
        Check(used == 4 && low >= 32 && high <= 128 && outsideSample == 0,
            $"E5/E6 FNV-1a spreads stone×4 over 256 samples ({used}/4 used, {low}..{high} per variant, {outsideSample} outside LayersFor)");

        // E7: [ok, corrupt, ok] keeps three slots, the corrupt one is `missing`, never picked.
        string badVariant = baseDir + "/varbad";
        WriteVariantPack(badVariant, stoneCount: 3, dirtCount: 1, corruptAt: 2);
        var mixed = TexPack.Load(badVariant);
        var mixedLayers = TexPack.LayersFor(Block.Stone, Face.Top);
        int picks = 0;
        if (mixedLayers.Length == 3)
            for (int z = 0; z < 16; z++)
                for (int x = 0; x < 16; x++)
                    if (TexPack.TileAt(Block.Stone, Face.Top, x, 7, z).Layer == mixedLayers[1]) picks++;
        Check(mixedLayers.Length == 3 && mixed.Layers == 14
            && mixed.Sources[mixedLayers[0]] == TexPack.TileSource.Png
            && mixed.Sources[mixedLayers[1]] == TexPack.TileSource.Missing
            && mixed.Sources[mixedLayers[2]] == TexPack.TileSource.Png && picks == 0,
            $"E7 [ok, corrupt, ok]: 3 slots, middle is `missing`, never selected ({picks} picks, {mixed.Layers} layers)");

        // E8: the cap keeps the first 16 entries (the warning text goes to stderr).
        string capped = baseDir + "/varcap";
        WriteVariantPack(capped, stoneCount: 20, dirtCount: 1);
        var huge = TexPack.Load(capped);
        var hugeLayers = TexPack.LayersFor(Block.Stone, Face.Top);
        Check(hugeLayers.Length == TexPack.MaxVariants && huge.Layers == TexPack.MaxVariants + 11
            && hugeLayers[TexPack.MaxVariants - 1] - hugeLayers[0] == TexPack.MaxVariants - 1,
            $"E8 20-entry array allocates {hugeLayers.Length} slots ({huge.Layers} layers; cap warning on stderr)");

        // E9: the demo pack worker B ships; an equivalent inline pack when it is absent.
        const string demo = "res://texturepacks/variants-demo";
        bool haveDemo = DirAccess.DirExistsAbsolute(demo);
        string which = haveDemo ? demo : baseDir + "/vareq";
        if (!haveDemo) WriteVariantPack(which, stoneCount: 4, dirtCount: 3);
        var demoPack = TexPack.Load(which);
        Check(demoPack.Layers == 17 && demoPack.VariantKeys == 2 && demoPack.Array.GetLayers() == 17,
            $"E9 variants-demo{(haveDemo ? "" : " (equivalent, res:// pack absent)")}: {demoPack.Layers} layers, {demoPack.VariantKeys} keys with variants");

        if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);
    }

    // ---- size classes ----------------------------------------------------

    /// <summary>Design P2.5 (E10-E15): single-class equivalence, class routing, the cap, the
    /// non-square row, the sizes-demo pack and the mixed-pack VRAM arithmetic.</summary>
    private static void CheckSizeClasses(VoxelWorld world)
    {
        GD.Print("  ---- size classes ----");
        string baseDir = ProjectSettings.GlobalizePath("user://texpack_selftest");
        if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);

        // The frozen 54-cell table, rows by Renderable order, cols by Face (PosX..NegZ).
        int[] frozen =
        {
            0, 0, 0, 0, 0, 0,        // Stone
            1, 1, 1, 1, 1, 1,        // Dirt
            3, 3, 2, 4, 3, 3,        // Grass: sides, top, bottom
            5, 5, 5, 5, 5, 5,        // Sand
            6, 6, 7, 7, 6, 6,        // Wood: top and bottom share the end grain
            8, 8, 8, 8, 8, 8,        // Plank
            9, 9, 9, 9, 9, 9,        // Leaves
            10, 10, 10, 10, 10, 10,  // Bedrock
            8, 8, 8, 8, 8, 8,        // Chest: frozen fallback is the plank row
        };

        // E10: the real default pack is one class; the old cells are byte-identical to the
        // Phase-1 layout and the six chest cells resolve through the pack's declared overrides.
        const string defaultPack = "res://texturepacks/default";
        var def = TexPack.Load(defaultPack);
        int classWrong = 0, layerWrong = 0, mapWrong = 0;
        for (int b = 0; b < TexPack.Renderable.Length; b++)
            for (int f = 0; f < 6; f++)
            {
                bool chest = TexPack.Renderable[b] == Block.Chest;
                int want = chest ? TexPack.KeyCount + 48 + f : frozen[b * 6 + f];
                if (TexPack.TileAt(TexPack.Renderable[b], f, 0, 0, 0).Class != 0) classWrong++;
                if (TexPack.TileIndex(TexPack.Renderable[b], f) != want) mapWrong++;
                var got = TexPack.LayersFor(TexPack.Renderable[b], f);
                if (got.Length != 1 || got[0] != want) layerWrong++;
            }
        int defPng = 0;
        foreach (var source in def.Sources) if (source == TexPack.TileSource.Png) defPng++;
        Check(def.Source == defaultPack && def.Classes == 1 && def.Arrays.Length == 1
            && def.Array.GetLayers() == TexPack.LayerCount && def.Layers == TexPack.LayerCount
            && def.ClassLayers[0] == TexPack.LayerCount && def.ClassSizes[0] == def.TileSize
            && def.FaceOverrides == 6
            && classWrong == 0 && mapWrong == 0 && layerWrong == 0 && defPng == TexPack.KeyCount + 6,
            $"E10 default pack: 1 size class, {TexPack.LayerCount} layers, {defPng} Png, 6/54 chest overrides "
            + $"({mapWrong} map, {layerWrong} layer)");

        // E11: 3 classes (16/32/64). Every face routes to its key's class, the class edge is the
        // PNG edge, and the layer exists in that class's array.
        string mixed = baseDir + "/sizeclasses";
        WritePack(mixed, 1, AllTiles());
        for (int i = 0; i < TexPack.KeyCount; i++)
            WriteTile(mixed, $"tiles/{TexPack.Keys[i]}.png", i == 0 ? 32 : i == 1 ? 64 : 16, KeyColor(i));
        var mix = TexPack.Load(mixed);
        int routeWrong = 0, edgeWrong = 0, existsWrong = 0;
        for (int b = 0; b < TexPack.Renderable.Length; b++)
            for (int f = 0; f < 6; f++)
                for (int p = 0; p < 4; p++)
                {
                    int key = frozen[b * 6 + f];
                    int wantClass = key == 0 ? 1 : key == 1 ? 2 : 0;
                    int wantEdge = key == 0 ? 32 : key == 1 ? 64 : 16;
                    var tile = TexPack.TileAt(TexPack.Renderable[b], f, p * 7, 3, p * 5);
                    if (tile.Class != wantClass) routeWrong++;
                    if (mix.ClassSizes[tile.Class] != wantEdge) edgeWrong++;
                    if (tile.Layer >= mix.Arrays[tile.Class].GetLayers()) existsWrong++;
                }
        // The mesher (and the ghost preview, same emitter) writes that (layer, class) to UV2.
        var chunk = world.CreateChunk(new Vector3I(160, 70, 160));
        var blocks = chunk.GetComponent<ChunkBlocks>().Value;
        Array.Clear(blocks);
        blocks[ChunkBlocks.Index(3, 4, 5)] = (byte)Block.Stone;
        var arrays = ChunkMesher.Build(world, chunk.GetComponent<ChunkCoord>(), blocks, out _).SurfaceGetArrays(0);
        var norms = (Vector3[])arrays[(int)Mesh.ArrayType.Normal];
        var uv2 = (Vector2[])arrays[(int)Mesh.ArrayType.TexUV2];
        int wx = 160 * 16 + 3, wy = 70 * 16 + 4, wz = 160 * 16 + 5;
        int meshWrong = 0;
        for (int i = 0; i < norms.Length; i++)
        {
            var tile = TexPack.TileAt(Block.Stone, FaceOfNormal(norms[i]), wx, wy, wz);
            if (uv2[i].X != tile.Layer || uv2[i].Y != tile.Class || tile.Class != 1) meshWrong++;
        }
        var ghostMesh = ChunkMesher.BuildBlock(Block.Stone, Orientation.None);
        var ghostUv2 = (Vector2[])ghostMesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.TexUV2];
        int ghostWrong = 0;
        for (int i = 0; i < ghostUv2.Length; i++)
            if (ghostUv2[i].Y != 1f) ghostWrong++;

        var ghostMaterial = VoxelWorld.GhostMaterial;
        bool ghostBound = ghostMaterial.GetShaderParameter("tiles0").As<Texture2DArray>() != null
            && ghostMaterial.GetShaderParameter("tiles1").As<Texture2DArray>() != null
            && ghostMaterial.GetShaderParameter("tiles2").As<Texture2DArray>() != null
            && ghostMaterial.GetShaderParameter("tiles3").As<Texture2DArray>() != null;

        Check(mix.Source == mixed && mix.Classes == 3 && mix.ClassSizes[0] == 16
            && mix.ClassSizes[1] == 32 && mix.ClassSizes[2] == 64 && mix.Layers == 12
            && routeWrong == 0 && edgeWrong == 0 && existsWrong == 0
            && norms.Length == 24 && meshWrong == 0 && ghostUv2.Length == 24 && ghostWrong == 0 && ghostBound,
            $"E11 mixed pack: {mix.Classes} classes route class/edge/array ({routeWrong}/{edgeWrong}/{existsWrong} wrong), "
            + $"mesher + ghost UV2 carry class 1 ({meshWrong}/{ghostWrong} wrong), ghost tiles0..3 bound");

        // E12: five distinct sizes -> cap 4; the 5th (plank 256²) degrades to `missing` in class 0.
        string cap = baseDir + "/sizecap";
        WritePack(cap, 1, AllTiles());
        for (int i = 0; i < TexPack.KeyCount; i++)
        {
            int size = i == 0 ? 32 : i == 1 ? 64 : i == 5 ? 128 : i == 8 ? 256 : 16;
            WriteTile(cap, $"tiles/{TexPack.Keys[i]}.png", size, KeyColor(i));
        }
        var capped = TexPack.Load(cap);
        var plankSlot = TexPack.TileAt(Block.Plank, Face.Top, 0, 0, 0);
        var plankLayers = TexPack.LayersFor(Block.Plank, Face.Top);
        string capWarning = $"texpack: {cap}: tile 'plank': size 256 would be class 5 of 4 — using 'missing'";
        Check(capped.Classes == 4 && capped.ClassSizes[0] == 16 && capped.ClassSizes[3] == 128
            && plankSlot.Class == 0 && plankLayers.Length == 1
            && capped.Sources[plankLayers[0]] == TexPack.TileSource.Missing
            && HasWarning(capWarning),
            $"E12 5 sizes: cap is {capped.Classes}, the 5th is `missing` in class 0 (warning quoted)");

        // E13: a 64x32 PNG has no inferable size -> exact warning, class-0 `missing`, pack loads.
        string nonSquare = baseDir + "/sizenonsquare";
        WritePack(nonSquare, 1, AllTiles());
        for (int i = 0; i < TexPack.KeyCount; i++) WriteTile(nonSquare, $"tiles/{TexPack.Keys[i]}.png", 16, KeyColor(i));
        WriteRectTile(nonSquare, "tiles/stone.png", 64, 32, Colors.Red);
        var squared = TexPack.Load(nonSquare);
        string squareWarning = $"texpack: {nonSquare}: tile 'stone': 64x32 is not square — using 'missing'";
        Check(squared.Source == nonSquare && squared.Resolved == 11 && squared.Classes == 1
            && squared.Sources[0] == TexPack.TileSource.Missing && HasWarning(squareWarning),
            $"E13 64x32 tile: exact non-square warning, class-0 `missing`, pack still loads ({squared.Resolved}/12)");

        // E14/E15: sizes-demo (worker B2) or its inline equivalent — sand 64², leaves 256².
        const string demo = "res://texturepacks/sizes-demo";
        bool haveDemo = DirAccess.DirExistsAbsolute(demo);
        string which = haveDemo ? demo : baseDir + "/sizeeq";
        if (!haveDemo)
        {
            WritePack(which, 1, AllTiles());
            for (int i = 0; i < TexPack.KeyCount; i++)
                WriteTile(which, $"tiles/{TexPack.Keys[i]}.png", i == 5 ? 64 : i == 9 ? 256 : 16, KeyColor(i));
        }
        var demoPack = TexPack.Load(which);
        var sand = TexPack.TileAt(Block.Sand, Face.Top, 0, 0, 0);
        var leaves = TexPack.TileAt(Block.Leaves, Face.Top, 0, 0, 0);
        Check(demoPack.Source == which && demoPack.Classes == 3
            && demoPack.ClassSizes[0] == 16 && demoPack.ClassSizes[1] == 64 && demoPack.ClassSizes[2] == 256
            && demoPack.ClassLayers[0] == 10 && demoPack.ClassLayers[1] == 1 && demoPack.ClassLayers[2] == 1
            && demoPack.Layers == 12 && demoPack.Arrays[2].GetLayers() == 1
            && sand.Class == 1 && leaves.Class == 2,
            $"E14 sizes-demo{(haveDemo ? "" : " (equivalent, res:// pack absent)")}: {demoPack.Classes} classes, layers [{string.Join(", ", demoPack.ClassLayers)}], 256² edge {demoPack.ClassSizes[2]}");

        string wantVram = $"texpack: {which}: 3 size classes — 16x16: 10 layers (10 KiB), 64x64: 1 layer (16 KiB), 256x256: 1 layer (256 KiB), total 282 KiB";
        long vramBytes = 0;
        int vramWrong = 0;
        for (int c = 0; c < demoPack.Classes; c++)
        {
            long bytes = (long)demoPack.ClassSizes[c] * demoPack.ClassSizes[c] * 4 * demoPack.ClassLayers[c];
            vramBytes += bytes;
            if (bytes != (c == 0 ? 10L : c == 1 ? 16L : 256L) * 1024) vramWrong++;
        }
        Check(demoPack.VramLine == wantVram && vramBytes == 282 * 1024 && vramWrong == 0,
            $"E15 VRAM line arithmetic matches RGBA8 4 B/texel ({demoPack.VramLine})");

        if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);
    }

    /// <summary>True when the last <see cref="TexPack.Load"/> recorded this exact size-class warning.</summary>
    private static bool HasWarning(string want)
    {
        foreach (var warning in TexPack.Warnings)
            if (warning == want) return true;
        return false;
    }

    private static void WriteRectTile(string packDir, string rel, int width, int height, Color color)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(packDir + "/" + rel));
        var image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        image.Fill(color);
        image.SavePng(packDir + "/" + rel);
    }

    /// <summary>Builds a chunk whose only solid block sits at local (3, 4, 5) and counts the
    /// vertices whose UV2.x disagrees with the absolute-coordinate tile index (0 = every one
    /// of the six faces agrees).</summary>
    private static int CrossContextFaces(VoxelWorld world, Vector3I chunkKey)
    {
        var entity = world.CreateChunk(chunkKey);
        var blocks = entity.GetComponent<ChunkBlocks>().Value;
        Array.Clear(blocks);
        blocks[ChunkBlocks.Index(3, 4, 5)] = (byte)Block.Stone;
        var arrays = ChunkMesher.Build(world, entity.GetComponent<ChunkCoord>(), blocks, out _).SurfaceGetArrays(0);
        var norms = (Vector3[])arrays[(int)Mesh.ArrayType.Normal];
        var uv2 = (Vector2[])arrays[(int)Mesh.ArrayType.TexUV2];
        int wx = chunkKey.X * 16 + 3, wy = chunkKey.Y * 16 + 4, wz = chunkKey.Z * 16 + 5;
        int wrong = 0;
        for (int i = 0; i < norms.Length; i++)
        {
            var tile = TexPack.TileAt(Block.Stone, FaceOfNormal(norms[i]), wx, wy, wz);
            if (uv2[i].X != tile.Layer || uv2[i].Y != tile.Class) wrong++;
        }
        return wrong;
    }

    private static void WritePack(string dir, int version, string tilesJson, string extra = "")    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(dir + "/pack.json",
            $"{{ \"version\": {version}, \"name\": \"selftest\", \"tile_size\": 16{extra}, \"tiles\": {{ {tilesJson} }} }}");
    }

    private static void WriteTile(string packDir, string rel, int size, Color color)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(packDir + "/" + rel));
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        image.Fill(color);
        image.SavePng(packDir + "/" + rel);
    }

    /// <summary>Writes a 12-key pack whose `stone` (and optionally `dirt`) declare several
    /// variants; every other key is one string. `corruptAt` (1-based) writes engineered-invalid
    /// bytes instead of stone's PNG at that variant slot.</summary>
    private static void WriteVariantPack(string dir, int stoneCount, int dirtCount, int corruptAt = 0)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < TexPack.KeyCount; i++)
        {
            string key = TexPack.Keys[i];
            int count = key == "stone" ? stoneCount : key == "dirt" ? dirtCount : 1;
            if (sb.Length > 0) sb.Append(", ");
            if (count == 1) { sb.Append($"\"{key}\": \"tiles/{key}.png\""); continue; }
            sb.Append($"\"{key}\": [");
            for (int v = 1; v <= count; v++) sb.Append(v > 1 ? ", " : "").Append($"\"tiles/{key}_{v}.png\"");
            sb.Append(']');
        }
        WritePack(dir, 1, sb.ToString());

        for (int i = 0; i < TexPack.KeyCount; i++)
        {
            string key = TexPack.Keys[i];
            int count = key == "stone" ? stoneCount : key == "dirt" ? dirtCount : 1;
            for (int v = 1; v <= count; v++)
            {
                string rel = count == 1 ? $"tiles/{key}.png" : $"tiles/{key}_{v}.png";
                if (key == "stone" && v == corruptAt)
                {
                    Directory.CreateDirectory(dir + "/tiles");
                    File.WriteAllBytes(dir + "/" + rel, new byte[] { 0x6e, 0x6f, 0x74, 0x70, 0x6e, 0x67 });
                }
                else WriteTile(dir, rel, 16, new Color(v / (float)(count + 1), key == "stone" ? 0.35f : 0.65f, 0.55f));
            }
        }
    }

    private static string AllTiles(bool includeMissing = true)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < TexPack.KeyCount; i++)
        {
            if (!includeMissing && i == TexPack.KeyCount - 1) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append($"\"{TexPack.Keys[i]}\": \"tiles/{TexPack.Keys[i]}.png\"");
        }
        return sb.ToString();
    }

    private static Color KeyColor(int index) => new((index + 1) / 13f, 0.5f, 0.25f);

    // Mesh COLOR is stored as RGBA8, so the written tint comes back within half a step.
    private static bool TintClose(float actual, float expected) => Mathf.Abs(actual - expected) <= 1f / 255f;

    private static int FaceOfNormal(Vector3 n)
    {
        if (n.X > 0.5f) return Face.PosX;
        if (n.X < -0.5f) return Face.NegX;
        if (n.Y > 0.5f) return Face.Top;
        if (n.Y < -0.5f) return Face.Bottom;
        if (n.Z > 0.5f) return Face.PosZ;
        return Face.NegZ;
    }

    /// <summary>The four emitted vertex slots of one world face, in FaceCorners order, or null
    /// when the face is missing or carries more or fewer than one quad.</summary>
    private static int[] FaceVertices(int face, Vector3[] norms)
    {
        var found = new List<int>(4);
        for (int i = 0; i < norms.Length; i++)
            if (FaceOfNormal(norms[i]) == face) found.Add(i);
        return found.Count == 4 ? found.ToArray() : null;
    }

    /// <summary>The 3D in-plane directions the tile's u and v axes point along, solved from two
    /// emitted edges: e1/e3 are the face's 3D edges and d1/d3 the UV deltas of the same corners.</summary>
    private static bool UvAxes(Godot.Collections.Array arrays, int[] quad, out Vector3 u3, out Vector3 v3)
    {
        u3 = v3 = Vector3.Zero;
        if (quad == null) return false;
        var verts = (Vector3[])arrays[(int)Mesh.ArrayType.Vertex];
        var uvs = (Vector2[])arrays[(int)Mesh.ArrayType.TexUV];
        Vector3 e1 = verts[quad[1]] - verts[quad[0]];
        Vector3 e3 = verts[quad[3]] - verts[quad[0]];
        Vector2 d1 = uvs[quad[1]] - uvs[quad[0]];
        Vector2 d3 = uvs[quad[3]] - uvs[quad[0]];
        float det = d1.X * d3.Y - d3.X * d1.Y;
        if (det == 0f) return false;
        // Invert [d1 d3] to get the 3D vector each UV axis points along, in the (e1, e3) basis.
        Vector3 Along(Vector2 target)
            => e1 * ((target.X * d3.Y - d3.X * target.Y) / det)
             + e3 * ((d1.X * target.Y - target.X * d1.Y) / det);
        u3 = Along(new Vector2(1, 0));
        v3 = Along(new Vector2(0, 1));
        return true;
    }

    /// <summary>True when the tile's own u and v axes point along 3D directions whose cross
    /// product is the inward normal — the u x v = -n rule.</summary>
    private static bool RightHanded(Godot.Collections.Array arrays, int[] quad)
    {
        if (!UvAxes(arrays, quad, out var u3, out var v3)) return false;
        var norms = (Vector3[])arrays[(int)Mesh.ArrayType.Normal];
        return u3.Cross(v3).Dot(norms[quad[0]]) < -0.5f;
    }

    /// <summary>The world direction of a local axis-aligned unit vector under the rotation, via
    /// the group's own axis images — an oracle that does not read the UV table.</summary>
    private static Vector3 Rotated(byte orientation, Vector3 v)
    {
        int axis = v.X != 0f ? 0 : v.Y != 0f ? 1 : 2;
        var image = Orientation.ImageOfLocalAxis(orientation, axis);
        float sign = axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
        return new Vector3(image.X * sign, image.Y * sign, image.Z * sign);
    }

    /// <summary>Twice the signed UV area of the emitted corner order; its sign is the quad's
    /// winding in tile space.</summary>
    private static float UvArea(Vector2[] uv)
        => (uv[1].X - uv[0].X) * (uv[3].Y - uv[0].Y) - (uv[3].X - uv[0].X) * (uv[1].Y - uv[0].Y);

    private static Vector2[] SortedUv(Vector2[] uv)
    {
        var copy = (Vector2[])uv.Clone();
        Array.Sort(copy, (a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        return copy;
    }

    /// <summary>Per-element equality of the six attributes Build and BuildBlock emit.</summary>
    private static int SameBlockArrays(Godot.Collections.Array a, Godot.Collections.Array b)
    {
        int wrong = 0;
        if (!SameValues((Vector3[])a[(int)Mesh.ArrayType.Vertex], (Vector3[])b[(int)Mesh.ArrayType.Vertex])) wrong++;
        if (!SameValues((Vector3[])a[(int)Mesh.ArrayType.Normal], (Vector3[])b[(int)Mesh.ArrayType.Normal])) wrong++;
        if (!SameValues((Color[])a[(int)Mesh.ArrayType.Color], (Color[])b[(int)Mesh.ArrayType.Color])) wrong++;
        if (!SameValues((Vector2[])a[(int)Mesh.ArrayType.TexUV], (Vector2[])b[(int)Mesh.ArrayType.TexUV])) wrong++;
        if (!SameValues((Vector2[])a[(int)Mesh.ArrayType.TexUV2], (Vector2[])b[(int)Mesh.ArrayType.TexUV2])) wrong++;
        if (!SameValues((int[])a[(int)Mesh.ArrayType.Index], (int[])b[(int)Mesh.ArrayType.Index])) wrong++;
        return wrong;
    }

    /// <summary>Element-wise equality of two mesh attribute arrays of the same type.</summary>
    private static bool SameValues(object a, object b)
    {
        var x = (Array)a;
        var y = (Array)b;
        if (x.Length != y.Length) return false;
        for (int i = 0; i < x.Length; i++)
            if (!x.GetValue(i).Equals(y.GetValue(i))) return false;
        return true;
    }

    private static void Check(bool condition, string what)
    {
        GD.Print(condition ? $"  ok    {what}" : $"  FAIL  {what}");
        if (!condition) _failures++;
    }
    /// <summary>Interactive blocks: the chunk byte array stays authoritative and the entity
    /// side is reconciled at the single write choke point. Deltas and a far-away region only,
    /// because earlier sections already placed planks near the spawn.</summary>
    private static void CheckBlockEntities(VoxelWorld world)
    {
        GD.Print("  ---- block entities ----");
        var registry = world.BlockEntities;

        const int bx = 40000, by = 3000, bz = 40000;
        var cell = new Vector3I(bx, by, bz);
        var chunkA = VoxelWorld.ChunkKeyOf(bx, by, bz);
        var chunkB = VoxelWorld.ChunkKeyOf(bx + 16, by, bz);

        static int VisitCount(BlockEntityRegistry r)
        {
            int n = 0;
            r.Visit((_, _) => n++);
            return n;
        }

        // 1. predicate/table ---------------------------------------------------
        Check(BlockInteractions.IsInteractable(Block.Chest) && BlockInteractions.KindOf(Block.Chest) == InteractKind.Toggle,
            "Chest is interactive and its dispatch kind is Toggle");
        Check(!BlockInteractions.IsInteractable(Block.Stone) && !BlockInteractions.IsInteractable(Block.Air),
            "Stone and Air are not interactive");

        // 2. the real request pipeline creates the entity ----------------------
        world.RequestEdit(EditRequest.Place(cell, Block.Chest, default));
        Check(world.ApplyPendingEdits() == 1, "a chest place request through the pipeline is applied");
        Check(registry.Contains(cell), "the placed chest has a block entity");
        bool hasEntity = registry.TryGet(cell, out var placedChest);
        Check(hasEntity && placedChest.GetComponent<BlockPos>().Vector == cell
              && placedChest.GetComponent<Interactable>().Kind == InteractKind.Toggle,
            "the entity carries its cell in BlockPos and the Toggle dispatch key");

        // 3. non-interactive blocks cost nothing -------------------------------
        int countBefore = registry.Count, bucketsBefore = registry.ChunkBucketCount, visitedBefore = VisitCount(registry);
        var plainCell = new Vector3I(bx + 16, by, bz);
        world.SetBlock(plainCell.X, plainCell.Y, plainCell.Z, Block.Stone);
        world.SetBlock(plainCell.X, plainCell.Y, plainCell.Z + 1, Block.Dirt);
        world.SetBlock(plainCell.X, plainCell.Y, plainCell.Z, Block.Air); // Air write over a byte with history
        Check(registry.Count == countBefore && registry.ChunkBucketCount == bucketsBefore && VisitCount(registry) == visitedBefore,
            $"non-interactive blocks create no entity ({registry.Count} entities, {registry.ChunkBucketCount} buckets unchanged)");
        Check(!registry.Contains(plainCell) && !registry.Contains(plainCell + new Vector3I(0, 0, 1)),
            "no entity for a Stone/Dirt/Air cell");

        // 4. breaking removes the entity ---------------------------------------
        int visitedBeforeBreak = VisitCount(registry);
        int bucketsBeforeBreak = registry.ChunkBucketCount;
        world.SetBlock(cell.X, cell.Y, cell.Z, Block.Air);
        Check(!registry.Contains(cell) && VisitCount(registry) == visitedBeforeBreak - 1,
            "breaking the chest removes its block entity");
        Check(registry.ChunkBucketCount == bucketsBeforeBreak - 1, "the emptied chunk bucket is gone, not stale");

        // ...and the same once more through the request pipeline.
        var pipeCell = new Vector3I(bx + 2, by, bz);
        world.RequestEdit(EditRequest.Place(pipeCell, Block.Chest, default));
        world.ApplyPendingEdits();
        Check(registry.Contains(pipeCell), "a pipeline place recreates the entity");
        world.RequestEdit(EditRequest.Break(pipeCell, default));
        Check(world.ApplyPendingEdits() == 1, "a break request through the pipeline is applied");
        Check(!registry.Contains(pipeCell), "the pipeline break dropped the entity too");

        // 5. two-way invariant over deterministic random edits ------------------
        uint rng = 0x5EED2024u;
        for (int i = 0; i < 200; i++)
        {
            rng = rng * 1664525u + 1013904223u;
            int chunkOffset = (int)(rng & 1u);
            int lx = (int)((rng >> 8) & 15u);
            int lz = (int)((rng >> 20) & 15u);
            int pick = (int)((rng >> 16) % 3u);
            var block = pick == 0 ? Block.Chest : pick == 1 ? Block.Stone : Block.Air;
            var random = new Vector3I(bx + chunkOffset * 16 + lx, by, bz + lz);
            world.SetBlock(random.X, random.Y, random.Z, block);
        }
        var touched = new List<Vector3I> { chunkA, chunkB };
        registry.Audit(world, touched, out int bytesWithoutEntity, out int entitiesWithoutByte);
        Check(bytesWithoutEntity == 0 && entitiesWithoutByte == 0,
            $"audit agrees after 200 random edits (bytesWithoutEntity={bytesWithoutEntity}, entitiesWithoutByte={entitiesWithoutByte})");

        // A second, non-Audit opinion: count the interactive bytes by hand.
        int byHand = 0;
        for (int x = bx; x < bx + 32; x++)
            for (int y = by; y < by + 16; y++)
                for (int z = bz; z < bz + 16; z++)
                    if (BlockInteractions.IsInteractable(world.GetBlock(x, y, z))) byHand++;
        int visitedRegion = 0;
        registry.Visit((c, _) =>
        {
            if (c.X >= bx && c.X < bx + 32 && c.Y >= by && c.Y < by + 16 && c.Z >= bz && c.Z < bz + 16) visitedRegion++;
        });
        Check(byHand == visitedRegion, $"hand count of interactive cells matches the registry ({byHand} vs {visitedRegion})");

        // 6. the audit can fail -------------------------------------------------
        var rawCell = new Vector3I(bx + 64, by, bz);
        world.SetBlock(rawCell.X, rawCell.Y, rawCell.Z, Block.Stone);
        bool haveRawChunk = world.TryGetChunk(rawCell.X, rawCell.Y, rawCell.Z, out var rawChunk);
        Check(haveRawChunk, "the raw-audit chunk is loaded");
        if (haveRawChunk)
        {
            var rawKey = VoxelWorld.ChunkKeyOf(rawCell.X, rawCell.Y, rawCell.Z);
            var bytes = rawChunk.GetComponent<ChunkBlocks>().Value;
            int rawIndex = ChunkBlocks.Index(rawCell.X - rawKey.X * VoxelWorld.ChunkSize,
                rawCell.Y - rawKey.Y * VoxelWorld.ChunkSize, rawCell.Z - rawKey.Z * VoxelWorld.ChunkSize);
            bytes[rawIndex] = (byte)Block.Chest; // bypasses SetBlock: the entity side is deliberately not synced
            registry.Audit(world, new[] { rawKey }, out int rawBytes, out int rawEntities);
            Check(rawBytes == 1 && rawEntities == 0,
                $"a raw chest byte with no entity is caught (bytesWithoutEntity={rawBytes}, entitiesWithoutByte={rawEntities})");
            bytes[rawIndex] = (byte)Block.Stone;
            registry.Audit(world, new[] { rawKey }, out rawBytes, out rawEntities);
            Check(rawBytes == 0 && rawEntities == 0, "restoring the byte restores the clean audit");
        }

        // 7. O(1) lookup: a 513-entity chunk plus a 1-entity neighbour ----------
        // BlockEntityRegistry.TryGet is bucket[chunkKey] then cell - two dictionary probes, never a scan.
        var bigBase = new Vector3I(bx + 32, by, bz);
        for (int i = 0; i < 512; i++)
            world.SetBlock(bigBase.X + (i & 15), bigBase.Y + (i >> 8), bigBase.Z + ((i >> 4) & 15), Block.Chest);
        var loneCell = new Vector3I(bx + 48, by, bz);
        world.SetBlock(loneCell.X, loneCell.Y, loneCell.Z, Block.Chest);

        int probes = registry.Probes;
        bool hitBig = registry.TryGet(new Vector3I(bigBase.X + 5, bigBase.Y, bigBase.Z + 5), out _);
        int probesBig = registry.Probes - probes;
        probes = registry.Probes;
        bool hitLone = registry.TryGet(loneCell, out _);
        int probesLone = registry.Probes - probes;
        Check(hitBig && hitLone && probesBig == probesLone && probesBig <= 2,
            $"512-entity and 1-entity buckets cost the same {probesBig}/{probesLone} dictionary probes (Count={registry.Count})");

        // 8. RMB precedence is pure and headless --------------------------------
        var plainLoaded = rawCell; // Stone, no entity
        Check(PlayerSystems.RightClickAction(world, true, loneCell, true) == ClickAction.Interact,
            "RMB on an interactive block interacts instead of placing (target + placement)");
        Check(PlayerSystems.RightClickAction(world, true, plainLoaded, true) == ClickAction.Place,
            "RMB on a plain block still places (target + placement)");
        Check(PlayerSystems.RightClickAction(world, true, loneCell, false) == ClickAction.Interact,
            "RMB on an interactive block interacts even with no placement cell");
        Check(PlayerSystems.RightClickAction(world, true, plainLoaded, false) == ClickAction.None,
            "RMB on a plain block with no placement cell does nothing");
        Check(PlayerSystems.RightClickAction(world, false, default, true) == ClickAction.Place,
            "RMB with no target still places when a placement cell exists");

        // 9. interaction behaviour ----------------------------------------------
        var toggleCell = new Vector3I(bx + 49, by, bz);
        world.SetBlock(toggleCell.X, toggleCell.Y, toggleCell.Z, Block.Chest);
        BlockInteractions.Reset();
        int version = BlockInteractions.Version;
        bool interacted = BlockInteractions.Interact(world, toggleCell, default);
        bool openFirst = registry.TryGet(toggleCell, out var toggleEntity) && toggleEntity.GetComponent<ToggleState>().Open;
        Check(interacted && openFirst, "interacting returns true and flips ToggleState.Open on");
        Check(world.GetBlock(toggleCell.X, toggleCell.Y, toggleCell.Z) == Block.Chest,
            "interacting leaves the block byte untouched");
        Check(BlockInteractions.Message != null && BlockInteractions.Message.Contains("Chest"),
            $"the HUD message names the block (\"{BlockInteractions.Message}\")");
        int versionAfterFirst = BlockInteractions.Version;
        Check(versionAfterFirst > version, $"the interaction bumped Version ({version} -> {versionAfterFirst})");

        BlockInteractions.Interact(world, toggleCell, default);
        bool closed = registry.TryGet(toggleCell, out toggleEntity) && !toggleEntity.GetComponent<ToggleState>().Open;
        Check(closed && BlockInteractions.Version > versionAfterFirst,
            "interacting again closes the toggle and bumps Version again");

        var emptyCell = new Vector3I(bx + 50, by, bz);
        Check(world.GetBlock(emptyCell.X, emptyCell.Y, emptyCell.Z) == Block.Air, "the neighbour cell starts as air");
        Check(!BlockInteractions.Interact(world, emptyCell, default),
            "interacting with a cell that has no entity returns false, so the click falls through to placement");
        Check(world.GetBlock(emptyCell.X, emptyCell.Y, emptyCell.Z) == Block.Air && !registry.Contains(emptyCell),
            "the neighbouring placement cell is still Air with no entity");

        // 10. DropChunk (chunk unload) ------------------------------------------
        var dropA = new Vector3I(bx + 65, by, bz);
        var dropB = new Vector3I(bx + 66, by, bz);
        world.SetBlock(dropA.X, dropA.Y, dropA.Z, Block.Chest);
        world.SetBlock(dropB.X, dropB.Y, dropB.Z, Block.Chest);
        Check(registry.Contains(dropA) && registry.Contains(dropB), "two chests in one chunk have entities");
        int bucketsBeforeDrop = registry.ChunkBucketCount;
        registry.DropChunk(world.Store, VoxelWorld.ChunkKeyOf(dropA.X, dropA.Y, dropA.Z));
        Check(!registry.Contains(dropA) && !registry.Contains(dropB), "DropChunk removes every entity of the chunk");
        Check(registry.ChunkBucketCount == bucketsBeforeDrop - 1, "DropChunk removes the chunk bucket");

        // DropChunk is only correct when the chunk entity itself is going away, which is what
        // unload does. The bytes here are still Chest, and a plain re-place is a no-op write,
        // so clear the byte first to make the re-place a real transition.
        world.SetBlock(dropA.X, dropA.Y, dropA.Z, Block.Air);
        world.SetBlock(dropA.X, dropA.Y, dropA.Z, Block.Chest);
        world.SetBlock(dropB.X, dropB.Y, dropB.Z, Block.Air);
        world.SetBlock(dropB.X, dropB.Y, dropB.Z, Block.Chest);
        Check(registry.Contains(dropA) && registry.Contains(dropB),
            "re-placing restores the entities, so the world is left consistent");

        // 11. isolated cost, no GPU involved -------------------------------------
        var cycleCell = new Vector3I(bx + 67, by, bz);
        var plainCycleCell = new Vector3I(bx + 68, by, bz);
        Check(world.GetBlock(cycleCell.X, cycleCell.Y, cycleCell.Z) == Block.Air, "the cycle cell starts as air");
        int entitiesBeforeCycles = registry.Count;
        ulong started = Time.GetTicksUsec();
        for (int i = 0; i < 2000; i++)
        {
            world.SetBlock(cycleCell.X, cycleCell.Y, cycleCell.Z, Block.Chest);
            world.SetBlock(cycleCell.X, cycleCell.Y, cycleCell.Z, Block.Air);
        }
        ulong elapsedUs = Time.GetTicksUsec() - started;
        int afterCycles = registry.Count;
        int leftOver = afterCycles - entitiesBeforeCycles;
        for (int i = 0; i < 2000; i++)
        {
            world.SetBlock(plainCycleCell.X, plainCycleCell.Y, plainCycleCell.Z, Block.Stone);
            world.SetBlock(plainCycleCell.X, plainCycleCell.Y, plainCycleCell.Z, Block.Air);
        }
        int plainLeft = registry.Count - afterCycles;
        Check(leftOver == 0 && plainLeft == 0 && elapsedUs < 5_000_000,
            $"ok  block entity: 2000 chest place+break cycles in {elapsedUs / 1000.0:F1} ms ({elapsedUs / 2000.0:F2} us/cycle), {leftOver} entities left; 2000 non-interactive placements left {plainLeft}");

        // ---- Phase B: Chest + the append-only vocabulary -----------------------
        GD.Print("  ---- chest + vocabulary ----");

        // V1: the 12 canonical keys, frozen order (plank stays index 8).
        string[] canonicalKeys =
        {
            "stone", "dirt", "grass_top", "grass_side", "grass_bottom", "sand",
            "wood_side", "wood_top", "plank", "leaves", "bedrock", "missing",
        };
        bool keysMatch = TexPack.KeyCount == canonicalKeys.Length && TexPack.Keys.Length == canonicalKeys.Length;
        for (int i = 0; keysMatch && i < canonicalKeys.Length; i++) keysMatch = TexPack.Keys[i] == canonicalKeys[i];
        Check(keysMatch, $"the 12 canonical keys are unchanged and in frozen order (KeyCount={TexPack.KeyCount})");

        // V2: Renderable is append-only: the 8 old blocks first, Chest at ordinal 8.
        Block[] oldRenderable = { Block.Stone, Block.Dirt, Block.Grass, Block.Sand, Block.Wood, Block.Plank, Block.Leaves, Block.Bedrock };
        bool renderableMatch = TexPack.Renderable.Length == 9 && TexPack.Renderable[8] == Block.Chest;
        for (int i = 0; renderableMatch && i < oldRenderable.Length; i++) renderableMatch = TexPack.Renderable[i] == oldRenderable[i];
        Check(renderableMatch, $"Renderable appends Chest at ordinal 8 after the old 8 in order (length={TexPack.Renderable.Length})");

        // V3: the 54-cell frozen fallback table lives once, in CheckTileIndexMap; its Chest row
        // is 8 (plank). The default pack declares the six chest overrides, so the resolved
        // indices are 60..65 — the base-only packs in CheckFaceOverrides/CheckTextureVariants
        // assert the fallback row itself.
        var chestPack = TexPack.Load("res://texturepacks/default");
        bool chestResolved = true;
        for (int f = 0; f < 6; f++)
            if (TexPack.TileIndex(Block.Chest, f) != TexPack.KeyCount + 48 + f) chestResolved = false;
        Check(chestResolved,
            $"resolved default pack: the six chest overrides win at layers {TexPack.KeyCount + 48}..{TexPack.KeyCount + 53} (fallback row is 8)");

        // V4: the vocabulary grows by six face keys; layer space is 66.
        string[] chestFaceKeys = { "chest_posx", "chest_negx", "chest_top", "chest_bottom", "chest_posz", "chest_negz" };
        bool faceKeysMatch = TexPack.FaceKeys.Length == 54 && TexPack.LayerCount == 66;
        for (int f = 0; faceKeysMatch && f < 6; f++) faceKeysMatch = TexPack.FaceKeys[48 + f] == chestFaceKeys[f];
        Check(faceKeysMatch, $"FaceKeys appends the six chest keys at 48..53 and LayerCount is 66 (len={TexPack.FaceKeys.Length}, layers={TexPack.LayerCount})");

        // V5: the shipped default pack carries the chest art.
        bool chestArtLoaded = chestPack.FaceOverrides == 6 && chestPack.Layers == 66;
        for (int f = 0; chestArtLoaded && f < 6; f++)
            chestArtLoaded = chestPack.Sources[60 + f] == TexPack.TileSource.Png;
        Check(chestArtLoaded, $"the default pack ships 6/54 chest overrides as Png in class 0 ({chestPack.FaceOverrides} overrides, {chestPack.Layers} layers)");

        // V6: an override really wins over the fallback; {8} here would be the bug.
        string chestLayers = "";
        bool overrideWins = true;
        for (int f = 0; f < 6; f++)
        {
            var layers = TexPack.LayersFor(Block.Chest, f);
            chestLayers += (f == 0 ? "" : ",") + (layers.Length == 1 ? layers[0].ToString() : $"[{layers.Length}]");
            if (layers.Length != 1 || layers[0] != 60 + f) overrideWins = false;
        }
        Check(overrideWins, $"chest_<face> overrides win over the fallback: LayersFor(Chest, f) == 60+f (got {chestLayers})");

        // ---- C1..C7: Chest behaviour -------------------------------------------
        // C1: the type table entry.
        Check(Blocks.PolicyOf(Block.Chest) == OrientationPolicy.Upright
              && Blocks.PlacementOf(Block.Chest) == Placement.Face
              && Blocks.HardnessOf(Block.Chest) > 0f
              && Blocks.Palette[^1] == Block.Chest,
            "C1 Chest is Upright + Face-placement, breakable, and the last Palette entry");

        // C2: the real request pipeline creates the entity.
        var chestCell = new Vector3I(bx + 81, by, bz);
        world.RequestEdit(EditRequest.Place(chestCell, Block.Chest, default));
        Check(world.ApplyPendingEdits() == 1, "C2 a chest place request through the pipeline is applied");
        bool chestEntity = registry.TryGet(chestCell, out var chest);
        Check(chestEntity && chest.GetComponent<Interactable>().Kind == InteractKind.Toggle
              && chest.GetComponent<BlockPos>().Vector == chestCell,
            "C2 the pipeline created a Chest block entity with BlockPos + Toggle");

        // C3: breaking the chest removes the entity and the emptied bucket.
        var brokenChest = new Vector3I(bx + 96, by, bz);
        world.SetBlock(brokenChest.X, brokenChest.Y, brokenChest.Z, Block.Chest);
        Check(registry.Contains(brokenChest), "C3 a chest in a fresh chunk has an entity");
        int bucketsBeforeChestBreak = registry.ChunkBucketCount;
        world.SetBlock(brokenChest.X, brokenChest.Y, brokenChest.Z, Block.Air);
        Check(!registry.Contains(brokenChest) && registry.ChunkBucketCount == bucketsBeforeChestBreak - 1,
            "C3 breaking the chest removes its entity and leaves no stale bucket");

        // C4: RMB on a chest interacts and the byte is untouched.
        BlockInteractions.Reset();
        int chestVersion = BlockInteractions.Version;
        Check(PlayerSystems.RightClickAction(world, true, chestCell, true) == ClickAction.Interact,
            "C4 RMB on a Chest interacts instead of placing");
        bool chestInteracted = BlockInteractions.Interact(world, chestCell, default);
        bool chestOpen = registry.TryGet(chestCell, out chest) && chest.GetComponent<ToggleState>().Open;
        Check(chestInteracted && chestOpen, "C4 chest interaction returns true and flips ToggleState.Open");
        Check(world.GetBlock(chestCell.X, chestCell.Y, chestCell.Z) == Block.Chest,
            "C4 chest interaction leaves the block byte untouched");
        Check(BlockInteractions.Message != null && BlockInteractions.Message.Contains("Chest"),
            $"C4 the chest HUD message names the block (\"{BlockInteractions.Message}\")");
        Check(BlockInteractions.Version > chestVersion,
            $"C4 the chest interaction bumped Version ({chestVersion} -> {BlockInteractions.Version})");

        // C5: the Phase A stand-in is really gone.
        Check(BlockInteractions.KindOf(Block.Plank) == InteractKind.None && !BlockInteractions.IsInteractable(Block.Plank),
            "C5 the Plank stand-in is gone: Plank is no longer interactive");

        // C6: non-interactive blocks still cost nothing.
        int inertCount = registry.Count, inertBuckets = registry.ChunkBucketCount, inertVisited = VisitCount(registry);
        var inertCell = new Vector3I(bx + 112, by, bz);
        world.SetBlock(inertCell.X, inertCell.Y, inertCell.Z, Block.Stone);
        world.SetBlock(inertCell.X, inertCell.Y, inertCell.Z, Block.Dirt);
        world.SetBlock(inertCell.X, inertCell.Y, inertCell.Z, Block.Air);
        Check(registry.Count == inertCount && registry.ChunkBucketCount == inertBuckets && VisitCount(registry) == inertVisited,
            $"C6 Stone/Dirt/Air still create no entity ({registry.Count} entities, {registry.ChunkBucketCount} buckets unchanged)");

        // C7: Chest is always upright and the facing convention maps local +Z to the player.
        float yaw = 0f, pitch = -Mathf.Pi / 6f; // a known pose: pitch -30 degrees
        byte chestPlaced = BlockBehaviors.PlaceOrientation(Block.Chest, Regress.Face.Top, yaw, pitch);
        Check(Blocks.Allows(Block.Chest, chestPlaced) && Orientation.ImageOfLocalAxis(chestPlaced, 1) == Vector3I.Up,
            "C7 PlaceOrientation(Chest) is allowed and its up image is Up");
        // C7: the placement convention puts the block's local +Z on the face toward the player.
        // (The art-side fact — the latch motif is painted on chest_posz, not negz — is asserted
        //  by the generator's --verify latch check; this assertion is the orientation math.)
        int cameraFacing = BlockBehaviors.FaceOfNormal(PlayerSystems.CameraBasis(yaw, pitch).Z);
        Check(Orientation.LocalFace(chestPlaced, cameraFacing) == Regress.Face.PosZ,
            $"C7 the placement convention maps local +Z onto the player-facing face (local face {Orientation.LocalFace(chestPlaced, cameraFacing)} vs PosZ)");
        byte nonUpright = Orientation.Axis(Regress.Face.PosX, 0);
        byte snappedChest = Blocks.Snap(Block.Chest, nonUpright);
        Check(Orientation.ImageOfLocalAxis(nonUpright, 1) != Vector3I.Up && Blocks.Allows(Block.Chest, snappedChest),
            $"C7 Snap(Chest, a non-upright candidate) still yields an allowed orientation ({snappedChest})");
    }
}
