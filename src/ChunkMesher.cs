using System.Collections.Generic;
using Godot;

namespace Regress;

/// <summary>Turns one <see cref="SectionSize"/>-cube of voxels into an ArrayMesh: a quad per solid face touching air.</summary>
public static class ChunkMesher
{
	public const int SectionSize = VoxelWorld.SectionSize;
	public const int Volume = SectionSize * SectionSize * SectionSize;
	private const int Pad = SectionSize + 2;

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
	/// Tile-local UV of one corner of a face. (0,0) is the PNG's top-left; on every face u x v
	/// equals the outward normal's reverse (u x v = -n), so v runs downward from the top edge
	/// of the art and the six faces share one handedness. The block's rotation moves the tile
	/// choice and these in-face UVs together: the tile comes from the LOCAL face and the UVs
	/// are this function at the LOCAL corner (docs/texture-packs.md).
	/// </summary>
	internal static Vector2 TileUv(int face, int x, int y, int z) => face switch
	{
		Face.Top => new Vector2(x, z),
		Face.Bottom => new Vector2(x, 1 - z),
		Face.PosX => new Vector2(1 - z, 1 - y),
		Face.NegX => new Vector2(z, 1 - y),
		Face.PosZ => new Vector2(x, 1 - y),
		_ => new Vector2(1 - x, 1 - y), // NegZ
	};

	[System.ThreadStatic] private static byte[] _cells;
	[System.ThreadStatic] private static byte[] _orientations;

	/// <summary>The chunk's own orientation array, resolved from the entity once when the caller
	/// does not pass it.</summary>
	private static byte[] OrientationsOf(VoxelWorld world, int bx, int by, int bz, byte[] orientations)
		=> orientations ?? (world.TryGetChunk(bx, by, bz, out var entity)
			? entity.GetComponent<ChunkBlocks>().Orientation : null);

	public static ArrayMesh Build(VoxelWorld world, ChunkCoord chunk, Vector3I section, byte[] blocks, out int indexCount,
		byte[] orientations = null)
	{
		indexCount = 0;
		int ox = section.X * SectionSize;
		int oy = section.Y * SectionSize;
		int oz = section.Z * SectionSize;
		int bx = chunk.X * VoxelWorld.ChunkSize; // the chunk's world origin
		int by = chunk.Y * VoxelWorld.ChunkSize;
		int bz = chunk.Z * VoxelWorld.ChunkSize;
		int wx = bx + ox, wy = by + oy, wz = bz + oz; // the section's world origin (tile/variant hash)

		orientations = OrientationsOf(world, bx, by, bz, orientations);

		// One padded copy so neighbour lookups never touch the world dictionary.
		var cells = _cells ??= new byte[Pad * Pad * Pad];
		var orient = _orientations ??= new byte[Pad * Pad * Pad];
		for (int y = -1; y <= SectionSize; y++)
		{
			for (int z = -1; z <= SectionSize; z++)
			{
				for (int x = -1; x <= SectionSize; x++)
				{
					bool inside = x >= 0 && x < SectionSize && y >= 0 && y < SectionSize && z >= 0 && z < SectionSize;
					// Cells inside this chunk are read straight from its own arrays; only cells
					// outside the chunk go through the world (terrain approximation or a neighbour).
					int cx = ox + x, cy = oy + y, cz = oz + z;
					bool inChunk = cx >= 0 && cx < VoxelWorld.ChunkSize
						&& cy >= 0 && cy < VoxelWorld.ChunkSize
						&& cz >= 0 && cz < VoxelWorld.ChunkSize;
					cells[PadIndex(x, y, z)] = inChunk
						? blocks[ChunkBlocks.Index(cx, cy, cz)]
						: (byte)world.GetBlock(bx + cx, by + cy, bz + cz);
					// Only the section's own cells are ever read: the face loop below runs over 0..15,
					// and a neighbour's rotation never changes THIS section's own faces — the
					// neighbour's mesh is built by its own section. Filling the border would cost up
					// to 1736 world lookups per section for nothing. ponytail: resolve the <=8
					// neighbour chunks once if a later pass ever needs neighbour rotations.
					if (inside)
						orient[PadIndex(x, y, z)] = orientations == null
							? Orientation.None : orientations[ChunkBlocks.Index(cx, cy, cz)];
				}
			}
		}

		var verts = new List<Vector3>();
		var norms = new List<Vector3>();
		var cols = new List<Color>();
		var uvs = new List<Vector2>();
		var uv2s = new List<Vector2>();
		var idx = new List<int>();

		for (int y = 0; y < SectionSize; y++)
		{
			for (int z = 0; z < SectionSize; z++)
			{
				for (int x = 0; x < SectionSize; x++)
				{
					var b = (Block)cells[PadIndex(x, y, z)];
					if (!Blocks.IsSolid(b)) continue;
					byte o = orient[PadIndex(x, y, z)];

					for (int f = 0; f < 6; f++)
					{
						var d = Dirs[f];
						if (Blocks.IsSolid((Block)cells[PadIndex(x + d.X, y + d.Y, z + d.Z)])) continue;
						EmitFace(verts, norms, cols, uvs, uv2s, idx, b, o, f, x, y, z, wx, wy, wz);
					}
				}
			}
		}

		indexCount = idx.Count;
		if (indexCount == 0) return null;
		return Emit(verts, norms, cols, uvs, uv2s, idx);
	}

	/// <summary>
	/// The six faces of one block alone at the origin, in cell-local space, with no neighbour
	/// occlusion test. Same emitter as <see cref="Build"/>, so for a section whose only solid block
	/// is at (0, 0, 0) with air around it the two meshes are equal per element.
	/// </summary>
	public static ArrayMesh BuildBlock(Block block, byte orientation)
	{
		var verts = new List<Vector3>(24);
		var norms = new List<Vector3>(24);
		var cols = new List<Color>(24);
		var uvs = new List<Vector2>(24);
		var uv2s = new List<Vector2>(24);
		var idx = new List<int>(36);
		for (int f = 0; f < 6; f++)
			EmitFace(verts, norms, cols, uvs, uv2s, idx, block, orientation, f, 0, 0, 0, 0, 0, 0);
		return Emit(verts, norms, cols, uvs, uv2s, idx);
	}

	/// <summary>One face quad of one block at cell (x, y, z) plus the section's world origin
	/// (wx, wy, wz), rotation applied. Both the chunk path and <see cref="BuildBlock"/> go through
	/// here, so a preview cannot drift from the world. The variant hash uses the block's absolute
	/// world position and the LOCAL face (design §3).</summary>
	private static void EmitFace(List<Vector3> verts, List<Vector3> norms, List<Color> cols,
		List<Vector2> uvs, List<Vector2> uv2s, List<int> idx, Block block, byte orientation, int face,
		int x, int y, int z, int wx, int wy, int wz)
	{
		int start = verts.Count;
		var corner = FaceCorners[face];
		var d = Dirs[face];
		// Texture is the base colour, so COLOR is the face tint alone — no block-colour
		// term here, or block colour lands twice. The tint is keyed to the WORLD face.
		float tint = FaceTint[face];
		var color = new Color(tint, tint, tint);
		var normal = new Vector3(d.X, d.Y, d.Z);
		// The tile is the one for the LOCAL face the rotation puts here, not the world face; the
		// variant is chosen from the block's absolute world position and the size class travels
		// with the layer, so UV2 = (layer, class) (design §3, P2.2).
		var tile = TexPack.TileAt(block, Orientation.LocalFace(orientation, face), wx + x, wy + y, wz + z);
		for (int i = 0; i < 4; i++)
		{
			int cx = corner[i * 3], cy = corner[i * 3 + 1], cz = corner[i * 3 + 2];
			verts.Add(new Vector3(x + cx, y + cy, z + cz));
			norms.Add(normal);
			cols.Add(color);
			uvs.Add(Orientation.Uv(orientation, face, cx, cy, cz));
			uv2s.Add(new Vector2(tile.Layer, tile.Class));
		}

		// Godot treats clockwise triangles as front-facing, so the CCW corner order above
		// is emitted reversed.
		idx.Add(start); idx.Add(start + 2); idx.Add(start + 1);
		idx.Add(start); idx.Add(start + 3); idx.Add(start + 2);
	}

	private static ArrayMesh Emit(List<Vector3> verts, List<Vector3> norms, List<Color> cols,
		List<Vector2> uvs, List<Vector2> uv2s, List<int> idx)
	{
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
