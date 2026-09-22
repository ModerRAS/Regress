using System.Collections.Generic;
using Godot;

namespace Regress;

/// <summary>Turns one 16-cube of voxels into an ArrayMesh: a quad per solid face touching air.</summary>
public static class ChunkMesher
{
	public const int Size = 16;
	public const int Volume = Size * Size * Size;
	private const int Pad = Size + 2;

	private static readonly Vector3I[] Dirs =
	{
		new(1, 0, 0), new(-1, 0, 0),
		new(0, 1, 0), new(0, -1, 0),
		new(0, 0, 1), new(0, 0, -1),
	};

	// Fake directional light baked into vertex colours (material is unshaded).
	private static readonly float[] FaceTint = { 0.72f, 0.72f, 1.00f, 0.45f, 0.86f, 0.86f };

	// 4 corners per face, counter-clockwise as seen from outside the block.
	// The mesher reverses the triangle order when emitting, because Godot's front
	// faces are clockwise.
	private static readonly int[][] FaceCorners =
	{
		new[] { 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 1 }, // +X
		new[] { 0, 0, 0, 0, 0, 1, 0, 1, 1, 0, 1, 0 }, // -X
		new[] { 0, 1, 1, 1, 1, 1, 1, 1, 0, 0, 1, 0 }, // +Y
		new[] { 0, 0, 0, 1, 0, 0, 1, 0, 1, 0, 0, 1 }, // -Y
		new[] { 0, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1 }, // +Z
		new[] { 1, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 0 }, // -Z
	};

	private static int PadIndex(int x, int y, int z) => ((y + 1) * Pad + (z + 1)) * Pad + (x + 1);

	/// <summary>
	/// Tile-local UV. (0,0) is the PNG's top-left; side faces run +v downward from the top
	/// edge, top and bottom run u along +X and v along +Z. v1 art must be direction-agnostic,
	/// so no per-face mirroring or rotation is fixed here (docs/texture-packs.md).
	/// </summary>
	private static Vector2 TileUv(int face, int x, int y, int z) => face switch
	{
		Face.Top or Face.Bottom => new Vector2(x, z),
		Face.PosX or Face.NegX => new Vector2(z, 1 - y),
		_ => new Vector2(x, 1 - y),
	};

	[System.ThreadStatic] private static byte[] _cells;

	public static ArrayMesh Build(VoxelWorld world, ChunkCoord coord, byte[] blocks, out int indexCount)
	{
		indexCount = 0;
		int bx = coord.X * Size;
		int by = coord.Y * Size;
		int bz = coord.Z * Size;

		// One padded copy so neighbour lookups never touch the world dictionary.
		var cells = _cells ??= new byte[Pad * Pad * Pad];
		for (int y = -1; y <= Size; y++)
		{
			for (int z = -1; z <= Size; z++)
			{
				for (int x = -1; x <= Size; x++)
				{
					bool inside = x >= 0 && x < Size && y >= 0 && y < Size && z >= 0 && z < Size;
					cells[PadIndex(x, y, z)] = inside
						? blocks[ChunkBlocks.Index(x, y, z)]
						: (byte)world.GetBlock(bx + x, by + y, bz + z);
				}
			}
		}

		var verts = new List<Vector3>();
		var norms = new List<Vector3>();
		var cols = new List<Color>();
		var uvs = new List<Vector2>();
		var uv2s = new List<Vector2>();
		var idx = new List<int>();

		for (int y = 0; y < Size; y++)
		{
			for (int z = 0; z < Size; z++)
			{
				for (int x = 0; x < Size; x++)
				{
					var b = (Block)cells[PadIndex(x, y, z)];
					if (!Blocks.IsSolid(b)) continue;

					for (int f = 0; f < 6; f++)
					{
						var d = Dirs[f];
						if (Blocks.IsSolid((Block)cells[PadIndex(x + d.X, y + d.Y, z + d.Z)])) continue;

						int start = verts.Count;
						var corner = FaceCorners[f];
						// Texture is the base colour, so COLOR is the face tint alone —
						// Blocks.ColorOf must stay out of this path or block colour lands twice.
						float tint = FaceTint[f];
						var color = new Color(tint, tint, tint);
						var normal = new Vector3(d.X, d.Y, d.Z);
						int tile = TexPack.TileIndex(b, f);
						for (int i = 0; i < 4; i++)
						{
							int cx = corner[i * 3], cy = corner[i * 3 + 1], cz = corner[i * 3 + 2];
							verts.Add(new Vector3(x + cx, y + cy, z + cz));
							norms.Add(normal);
							cols.Add(color);
							uvs.Add(TileUv(f, cx, cy, cz));
							uv2s.Add(new Vector2(tile, 0f));
						}

						// Godot treats clockwise triangles as front-facing, so the CCW corner
						// order above is emitted reversed.
						idx.Add(start); idx.Add(start + 2); idx.Add(start + 1);
						idx.Add(start); idx.Add(start + 3); idx.Add(start + 2);
					}
				}
			}
		}

		indexCount = idx.Count;
		if (indexCount == 0) return null;

		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
		arrays[(int)Mesh.ArrayType.Normal] = norms.ToArray();
		arrays[(int)Mesh.ArrayType.Color] = cols.ToArray();
		arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
		arrays[(int)Mesh.ArrayType.TexUV2] = uv2s.ToArray();
		arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();

		var mesh = new ArrayMesh();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
		return mesh;
	}
}
