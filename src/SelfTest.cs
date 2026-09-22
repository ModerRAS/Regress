using System;
using Friflo.Engine.ECS;
using Godot;

namespace Regress;

/// <summary>Headless logic checks. Run with: godot --headless -- --selftest</summary>
public static class SelfTest
{
    private static int _failures;

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

        return _failures;
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

    private static void Check(bool condition, string what)
    {
        GD.Print(condition ? $"  ok    {what}" : $"  FAIL  {what}");
        if (!condition) _failures++;
    }
}
