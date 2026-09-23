using System;
using System.Collections.Generic;
using Godot;

namespace Regress;

/// <summary>
/// Loads a texture pack — a directory with <c>pack.json</c> and the PNGs it names — into one
/// <see cref="Texture2DArray"/> per size class. Discovery has four rungs and never fails: the
/// last rung synthesises the twelve tiles in memory, so the game always renders. The format,
/// the key order and the failure matrix are frozen in docs/texture-packs.md; the size-class
/// model (class 0 baseline, cap 4, per-class layer numbering) is frozen in design.md P2.1.
/// </summary>
public static class TexPack
{
	public const int KeyCount = 12;
	public const int DefaultTileSize = 16;

	/// <summary>Most variants one key may declare (design §5). 72 keys x 16 = 1152 layers ceiling.</summary>
	public const int MaxVariants = 16;

	/// <summary>Most size classes one pack may use (design P2.1). A 5th distinct size degrades
	/// that tile to `missing` in class 0; it never rejects the pack.</summary>
	public const int MaxSizeClasses = 4;

	/// <summary>Frozen layer order. Never renumbered.</summary>
	public static readonly string[] Keys =
	{
		"stone", "dirt", "grass_top", "grass_side", "grass_bottom", "sand",
		"wood_side", "wood_top", "plank", "leaves", "bedrock", "missing",
	};

	private const int Missing = 11;

	/// <summary>The ten renderable blocks in cell order; cell index = block*6 + face.</summary>
	public static readonly Block[] Renderable =
		{ Block.Stone, Block.Dirt, Block.Grass, Block.Sand, Block.Wood, Block.Plank, Block.Leaves, Block.Bedrock, Block.Chest, Block.Pumpkin };

	/// <summary>Face-constant suffixes in Face index order (PosX=0 … NegZ=5).</summary>
	public static readonly string[] FaceSuffix = { "posx", "negx", "top", "bottom", "posz", "negz" };

	/// <summary>Optional per-face override keys; layer = KeyCount + cell.</summary>
	public static readonly string[] FaceKeys = BuildFaceKeys();

	/// <summary>Array layers the loader may need: 12 base + 60 optional per-face slots.
	/// This stays the frozen no-variant baseline slot space; a variant or mixed-size pack's
	/// arrays are <see cref="Pack.Layers"/> / <see cref="Pack.ClassLayers"/> layers, computed
	/// by the uniform slot rule (design §2) inside each size class (design P2.1).</summary>
	public static readonly int LayerCount = KeyCount + FaceKeys.Length;

	/// <summary>Rung-4 art, authored in sRGB — the sampler's source_color conversion yields linear.</summary>
	private static readonly Color[] FallbackColors =
	{
		new(0.52f, 0.52f, 0.55f), new(0.44f, 0.30f, 0.20f), new(0.34f, 0.62f, 0.24f),
		new(0.36f, 0.50f, 0.24f), new(0.44f, 0.30f, 0.20f), new(0.86f, 0.80f, 0.56f),
		new(0.36f, 0.26f, 0.15f), new(0.36f, 0.26f, 0.15f), new(0.68f, 0.51f, 0.31f),
		new(0.20f, 0.45f, 0.17f), new(0.16f, 0.16f, 0.18f), new(1f, 0f, 1f),
	};

	/// <summary>Where a layer's pixels came from — the loader's own account of the fallback matrix.</summary>
	public enum TileSource : byte { Png, Missing, Procedural }

	public sealed class Pack
	{
		/// <summary>Class 0's array — the only array of a single-class pack.</summary>
		public Texture2DArray Array;

		/// <summary>One array per size class, class 0 first. <c>Array == Arrays[0]</c>.</summary>
		public Texture2DArray[] Arrays;

		/// <summary>Number of size classes, 1..<see cref="MaxSizeClasses"/>.</summary>
		public int Classes;

		/// <summary>Square edge per class; ClassSizes[0] is the baseline class.</summary>
		public int[] ClassSizes;

		/// <summary>Array layer count per class.</summary>
		public int[] ClassLayers;

		/// <summary>The mixed-pack VRAM report line, or null when Classes == 1 (design P2.4).</summary>
		public string VramLine;

		public int TileSize;
		public string Name;
		public string Source;
		public int Resolved;
		public bool Procedural;
		public int FaceOverrides;

		/// <summary>Where class 0's layers came from — the loader's own account of the fallback matrix.</summary>
		public TileSource[] Sources;

		/// <summary>Total Texture2DArray layers across every class.</summary>
		public int Layers;

		/// <summary>Keys with at least two decodable variants (the new report line's K).</summary>
		public int VariantKeys;

		/// <summary>Class-0 slot layers per key, primary first; null = the pack does not
		/// enumerate that key. A key whose variants all live in another class has none here —
		/// use <see cref="TileAt"/> for class-aware routing.</summary>
		public int[][] KeyLayers;
	}

	/// <summary>
	/// docs/texture-packs.md's 60-cell fallback map, over the <see cref="Block"/> enum — never
	/// over <c>Blocks.Palette</c>, which omits Bedrock even though bedrock is meshed. Never
	/// renumbered.
	/// </summary>
	private static readonly int[] Frozen = {
		0, 0, 0, 0, 0, 0,        // Stone
		1, 1, 1, 1, 1, 1,        // Dirt
		3, 3, 2, 4, 3, 3,        // Grass: sides, top, bottom
		5, 5, 5, 5, 5, 5,        // Sand
		6, 6, 7, 7, 6, 6,        // Wood: top and bottom share the end grain
		8, 8, 8, 8, 8, 8,        // Plank
		9, 9, 9, 9, 9, 9,        // Leaves
		10, 10, 10, 10, 10, 10,  // Bedrock
		// Chest (ordinal 8): frozen fallback is plank; its art ships as the six chest_* overrides.
		8, 8, 8, 8, 8, 8,
		// Pumpkin (ordinal 9): frozen fallback is sand (5) — the warmest/lightest of the 12 keys,
		// closest in hue to pumpkin orange; its art ships as the six pumpkin_* overrides.
		5, 5, 5, 5, 5, 5,
	};

	/// <summary>The last accepted pack's resolution; the frozen fallback until one loads.</summary>
	private static int[] _resolved = Frozen;

	/// <summary>Class-0 slot layers per key, frozen order; null = the last pack does not enumerate it.</summary>
	private static int[][] _keyLayers = FrozenSlots();

	/// <summary>Decodable (layer, class) pairs per key, declaration order, primary first — the
	/// variant hash indexes this table (design P2.2).</summary>
	private static (int Layer, int Class)[][] _validSlots = EmptySlots();

	/// <summary>Every enumerated key's primary (layer, class): the first declared variant, or
	/// the single degraded slot. Air and shape misses resolve to <c>_missingSlot</c>.</summary>
	private static (int Layer, int Class)[] _primary = FrozenPrimary();

	/// <summary>The `missing` key's primary slot; what Air and shape misses resolve to.</summary>
	private static (int Layer, int Class) _missingSlot = (Missing, 0);

	/// <summary>Override keys the last pack declared. A provided-but-bad override still counts:
	/// only these claim their (Block,Face) cell, while the other override cells keep owning
	/// their slots (fixed 72-layer space) but show the frozen base mapping. All false for the
	/// frozen/procedural layout.</summary>
	private static bool[] _provided = new bool[LayerCount];

	/// <summary>Size-class warnings of the last <see cref="Load"/>, also pushed to stderr.
	/// E12/E13 assert the exact wording here; the other warnings keep their Phase-1 call sites.</summary>
	private static readonly List<string> _warnings = new();
	public static IReadOnlyList<string> Warnings => _warnings;

	private static void Warn(string message)
	{
		_warnings.Add(message);
		GD.PushWarning(message);
	}

	/// <summary>The pre-variants single-slot layout: base key k owns layer k, override cells absent.</summary>
	private static int[][] FrozenSlots()
	{
		var slots = new int[LayerCount][];
		for (int k = 0; k < KeyCount; k++) slots[k] = new[] { k };
		return slots;
	}

	/// <summary>Frozen routing table: every base key is one class-0 slot on its own layer.</summary>
	private static (int Layer, int Class)[][] EmptySlots()
	{
		var slots = new (int Layer, int Class)[LayerCount][];
		for (int k = 0; k < KeyCount; k++) slots[k] = new[] { (k, 0) };
		return slots;
	}

	private static (int Layer, int Class)[] FrozenPrimary()
	{
		var primary = new (int Layer, int Class)[LayerCount];
		for (int k = 0; k < KeyCount; k++) primary[k] = (k, 0);
		return primary;
	}

	/// <summary>
	/// FNV-1a over the four ints: the variant choice is a pure function of absolute world
	/// position and the LOCAL face (design §3). Deterministic, no RNG, no time, no state.
	/// </summary>
	public static uint Mix(int x, int y, int z, int face)
	{
		unchecked
		{
			uint h = 2166136261u;
			h = (h ^ (uint)x) * 16777619u;
			h = (h ^ (uint)y) * 16777619u;
			h = (h ^ (uint)z) * 16777619u;
			h = (h ^ (uint)face) * 16777619u;
			return h;
		}
	}

	/// <summary>Cell for a renderable Block+Face, or -1 for Air / an out-of-range face.</summary>
	private static int Cell(Block b, int face)
	{
		if ((uint)face > 5) return -1;
		for (int i = 0; i < Renderable.Length; i++)
			if (Renderable[i] == b) return i * 6 + face;
		return -1;
	}

	/// <summary>Key index for a 60-cell cell: the override cell when the pack declares it,
	/// else the frozen base key.</summary>
	private static int LayerKey(int cell) =>
		_provided[KeyCount + cell] ? KeyCount + cell : Frozen[cell];

	/// <summary>Key name for warnings — base keys first, then the 60 override keys.</summary>
	private static string KeyName(int key) => key < KeyCount ? Keys[key] : FaceKeys[key - KeyCount];

	/// <summary>Frozen Block+Face -> primary tile layer (variant 0). Per face: exact override,
	/// else the base fallback. Every existing caller keeps working.</summary>
	public static int TileIndex(Block b, int face)
	{
		int cell = Cell(b, face);
		return cell < 0 ? _missingSlot.Layer : _resolved[cell];
	}

	/// <summary>Position-selected tile as <c>(layer, class)</c>: the primary when the key has
	/// one valid slot, else <c>Mix(worldX, worldY, worldZ, localFace) % validCount</c> — absolute
	/// world coordinates and the block's LOCAL face (design §3). The class rides in the same
	/// precomputed table, so the mesher writes <c>UV2 = (layer, class)</c> with no added
	/// arithmetic (design P2.2).</summary>
	public static (int Layer, int Class) TileAt(Block b, int face, int wx, int wy, int wz)
	{
		int cell = Cell(b, face);
		if (cell < 0) return _missingSlot;
		int key = LayerKey(cell);
		var valid = _validSlots[key];
		if (valid == null || valid.Length == 0) return _primary[key];
		return valid[(int)(Mix(wx, wy, wz, face) % (uint)valid.Length)];
	}

	/// <summary>The class-0 slot layers of one (Block,Face) key, primary first (design §2).
	/// Do not mutate: the returned array is the loader's own table.</summary>
	public static int[] LayersFor(Block b, int face)
	{
		int cell = Cell(b, face);
		return cell < 0 ? new[] { _missingSlot.Layer } : _keyLayers[LayerKey(cell)];
	}

	/// <summary>Runs discovery and prints the success line, plus the overrides/variants/VRAM
	/// lines when they apply. Never returns null.</summary>
	public static Pack Load(string cliPack)
	{
		_warnings.Clear();
		Pack pack = null;
		if (!string.IsNullOrWhiteSpace(cliPack)) pack = TryPack(cliPack.Trim());
		if (pack == null) pack = TrySelected();
		if (pack == null) pack = TryPack("res://texturepacks/default");
		if (pack == null) pack = Procedural();

		GD.Print($"texpack: using '{pack.Name}' ({pack.Source}) tile_size={pack.TileSize} tiles={pack.Resolved}/12");
		if (pack.FaceOverrides > 0)
			GD.Print($"texpack: {pack.Source}: {pack.FaceOverrides}/{FaceKeys.Length} per-face overrides");
		if (pack.VariantKeys > 0)
			GD.Print($"texpack: {pack.Source}: {pack.Layers} layers, {pack.VariantKeys} keys with variants (cap {MaxVariants})");
		if (pack.Classes > 1)
			GD.Print(pack.VramLine);
		return pack;
	}

	/// <summary>
	/// Resolves one manifest tile path against a pack root. Returns the path to hand to
	/// FileAccess, or <c>null</c> if the entry is rejected. Lexical and strict: no filesystem
	/// probes, any <c>..</c> segment is rejected outright, and GetFullPath containment backs
	/// the string rule up.
	/// </summary>
	public static string ResolveTilePath(string raw, string root, string rootFull)
	{
		if (string.IsNullOrWhiteSpace(raw)) return null;
		raw = raw.Replace('\\', '/');
		if (raw[0] == '/') return null;
		if (raw.Contains(':')) return null;

		var segs = new List<string>();
		foreach (var seg in raw.Split('/'))
		{
			if (seg.Length == 0 || seg == ".") continue;
			if (seg.IndexOf('\0') >= 0) return null;
			if (seg == "..") return null;
			segs.Add(seg);
		}
		if (segs.Count == 0) return null;

		string rel = string.Join('/', segs);
		try
		{
			string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(rootFull, rel));
			var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			if (!full.StartsWith(rootFull + System.IO.Path.DirectorySeparatorChar, cmp)) return null;
		}
		catch (Exception) { return null; }

		return root + "/" + rel;
	}

	// ---- discovery rungs -------------------------------------------------

	private static Pack TrySelected()
	{
		const string File = "user://texturepacks/selected.txt";
		if (!FileAccess.FileExists(File)) return null;
		string raw = FileAccess.GetFileAsString(File)?.Split('\n')[0].Trim() ?? "";
		if (raw.Length == 0 || raw.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || raw == "." || raw == "..")
		{
			GD.PushWarning($"texpack: {File}: '{(raw.Length == 0 ? "<empty>" : raw)}' is not a pack name — ignoring");
			return null;
		}
		return TryPack($"user://texturepacks/{raw}");
	}

	private static Pack TryPack(string root)
	{
		root = root.TrimEnd('/', '\\');
		if (!DirAccess.DirExistsAbsolute(root))
		{
			GD.PushWarning($"texpack: {root}: not a directory — skipping");
			return null;
		}

		byte[] json = FileAccess.GetFileAsBytes(root + "/pack.json");
		if (json == null)
		{
			GD.PushWarning($"texpack: {root}: pack.json not found or could not be read — skipping pack");
			return null;
		}

		var parsed = Json.ParseString(System.Text.Encoding.UTF8.GetString(json));
		if (parsed.VariantType != Variant.Type.Dictionary)
		{
			GD.PushWarning($"texpack: {root}: pack.json is not a JSON object — skipping pack");
			return null;
		}
		var manifest = parsed.AsGodotDictionary();

		if (!manifest.ContainsKey("version") || !IsInt(manifest["version"], 1))
		{
			string v = manifest.ContainsKey("version") ? manifest["version"].ToString() : "<missing>";
			GD.PushWarning($"texpack: {root}: version {v} is not supported (expected 1) — skipping pack");
			return null;
		}

		string name = DirName(root);
		if (manifest.ContainsKey("name"))
		{
			if (manifest["name"].VariantType == Variant.Type.String) name = manifest["name"].AsString();
			else GD.PushWarning($"texpack: {root}: name is not a string — using '{name}'");
		}

		if (!manifest.ContainsKey("tile_size") || !IsInt(manifest["tile_size"]) || manifest["tile_size"].AsDouble() < 1)
		{
			string s = manifest.ContainsKey("tile_size") ? manifest["tile_size"].ToString() : "<missing>";
			GD.PushWarning($"texpack: {root}: tile_size {s} is not a positive integer — skipping pack");
			return null;
		}
		int tileSize = (int)manifest["tile_size"].AsDouble();

		foreach (var key in manifest.Keys)
		{
			string k = key.AsString();
			if (k is not ("version" or "name" or "tile_size" or "tiles"))
				GD.PushWarning($"texpack: {root}: unknown manifest field '{k}' — ignored");
		}

		Godot.Collections.Dictionary tiles = null;
		if (manifest.ContainsKey("tiles"))
		{
			if (manifest["tiles"].VariantType == Variant.Type.Dictionary) tiles = manifest["tiles"].AsGodotDictionary();
			else GD.PushWarning($"texpack: {root}: tiles is not an object — ignoring");
		}

		string rootFull;
		try
		{
			string native = ProjectSettings.GlobalizePath(root);
			if (string.IsNullOrEmpty(native)) native = root; // a CLI path is already native
			rootFull = System.IO.Path.GetFullPath(native);
		}
		catch (Exception) { rootFull = System.IO.Path.GetFullPath("."); }

		// Parse every declared value first: a string is one variant, an array is the variants
		// in manifest order (first = primary). `declared[k]` holds up to MaxVariants raw paths;
		// a null element is a rejected entry that still owns its slot as `missing` (design §4).
		var declared = new string[LayerCount][];
		var provided = new bool[LayerCount];
		int faceOverrides = 0;
		if (tiles != null)
		{
			foreach (var key in tiles.Keys)
			{
				string k = key.AsString();
				int index = Array.IndexOf(Keys, k);
				if (index < 0)
				{
					int cell = Array.IndexOf(FaceKeys, k);
					if (cell >= 0)
					{
						index = KeyCount + cell;
						faceOverrides++;
					}
				}
				if (index < 0)
				{
					GD.PushWarning($"texpack: {root}: unknown tile key '{k}' — ignored");
					continue;
				}
				// Declared, before any value validation: a provided-but-bad override still
				// claims its cell and shows `missing` (today's documented behaviour).
				if (index >= KeyCount) provided[index] = true;

				var value = tiles[key];
				if (value.VariantType == Variant.Type.String)
				{
					if (value.AsString().Length > 0) declared[index] = new[] { value.AsString() };
					else GD.PushWarning($"texpack: {root}: tile '{k}' is not a path or a list of paths — using 'missing'");
					continue;
				}
				if (value.VariantType == Variant.Type.Array)
				{
					var entries = value.AsGodotArray();
					if (entries.Count == 0)
					{
						GD.PushWarning($"texpack: {root}: tile '{k}': empty variant list — using 'missing'");
						continue;
					}
					int n = entries.Count;
					if (n > MaxVariants)
					{
						GD.PushWarning($"texpack: {root}: tile '{k}': {n} variants, cap is {MaxVariants} — only the first {MaxVariants} are used");
						n = MaxVariants;
					}
					var paths = new string[n];
					for (int i = 0; i < n; i++)
					{
						var entry = entries[i];
						if (entry.VariantType != Variant.Type.String || entry.AsString().Length == 0)
						{
							GD.PushWarning($"texpack: {root}: tile '{k}'[{i + 1}/{n}]: not a path — using 'missing'");
							continue;
						}
						paths[i] = entry.AsString();
					}
					declared[index] = paths;
					continue;
				}
				GD.PushWarning($"texpack: {root}: tile '{k}' is not a path or a list of paths — using 'missing'");
			}
		}

		// Decode each declared variant. A failed variant keeps its slot as the pack's `missing`
		// image (or its procedural colour when there is no `missing`), so the key's other
		// variants survive (design §4). The size class is inferred from each PNG's own square
		// edge (design P2.1): non-square art has no inferable size and degrades to class 0.
		var decoded = new Image[LayerCount][];
		for (int k = 0; k < LayerCount; k++)
		{
			var raws = declared[k];
			if (raws == null) continue;
			string keyName = KeyName(k);
			var images = new Image[raws.Length];
			for (int i = 0; i < raws.Length; i++)
			{
				if (raws[i] == null) continue;
				string label = raws.Length > 1 ? $"'{keyName}'[{i + 1}/{raws.Length}]" : $"'{keyName}'";
				string path = ResolveTilePath(raws[i], root, rootFull);
				if (path == null)
				{
					GD.PushWarning($"texpack: {root}: tile {label}: '{raws[i]}' escapes the pack root — using 'missing'");
					continue;
				}
				var image = ReadTile(path, raws[i], root, label);
				if (image == null) continue;
				if (image.GetWidth() != image.GetHeight())
				{
					Warn($"texpack: {root}: tile {label}: {image.GetWidth()}x{image.GetHeight()} is not square — using 'missing'");
					continue;
				}
				images[i] = image;
			}
			decoded[k] = images;
		}

		// Size classes (design P2.1). Class 0 is the baseline: the tile_size class when any
		// slot has that size, else the first square size in frozen key order. The other classes
		// get 1..C-1 in first-appearance order. `ordered` keeps every distinct size so an
		// over-cap slot can name the class it would have had; `classSizes` is the capped list.
		var distinct = new List<int>();
		for (int k = 0; k < LayerCount; k++)
		{
			if (k >= KeyCount && faceOverrides == 0) continue;
			var images = decoded[k];
			if (images == null) continue;
			foreach (var image in images)
				if (image != null && !distinct.Contains(image.GetWidth())) distinct.Add(image.GetWidth());
		}
		int baseline = distinct.Contains(tileSize) ? tileSize : distinct.Count > 0 ? distinct[0] : tileSize;
		var ordered = new List<int> { baseline };
		foreach (int size in distinct)
			if (size != baseline) ordered.Add(size);
		int classCount = Math.Min(ordered.Count, MaxSizeClasses);
		var classSizes = new int[classCount];
		for (int c = 0; c < classCount; c++) classSizes[c] = ordered[c];

		// Assign every declared variant to a class; a failed or over-cap variant joins class 0
		// as a degraded slot. A key with zero usable variants collapses to exactly one degraded
		// class-0 slot (today's bad-tile row, design §4).
		var slotClass = new int[LayerCount][];
		var slotValid = new bool[LayerCount][];
		var slotLayer = new int[LayerCount][];
		for (int k = 0; k < LayerCount; k++)
		{
			if (k >= KeyCount && faceOverrides == 0) continue;
			var images = decoded[k];
			int n = images?.Length ?? 0;
			int usable = 0;
			if (images != null)
				foreach (var image in images)
					if (image != null) usable++;
			if (usable == 0)
			{
				slotClass[k] = new[] { 0 };
				slotValid[k] = new[] { false };
				slotLayer[k] = new int[1];
				continue;
			}
			var classes = new int[n];
			var valid = new bool[n];
			for (int j = 0; j < n; j++)
			{
				if (images[j] == null) continue;
				int c = Array.IndexOf(classSizes, images[j].GetWidth());
				if (c >= 0)
				{
					classes[j] = c;
					valid[j] = true;
					continue;
				}
				string label = n > 1 ? $"'{KeyName(k)}'[{j + 1}/{n}]" : $"'{KeyName(k)}'";
				Warn($"texpack: {root}: tile {label}: size {images[j].GetWidth()} would be class {ordered.IndexOf(images[j].GetWidth()) + 1} of {MaxSizeClasses} — using 'missing'");
			}
			slotClass[k] = classes;
			slotValid[k] = valid;
			slotLayer[k] = new int[n];
		}

		// Per-class arrays, numbered independently (design P2.1): frozen key order inside each
		// class, each key's class slots consecutive. A key whose variants span classes has
		// slots in several arrays.
		var classLayers = new int[classCount];
		for (int c = 0; c < classCount; c++)
		{
			int total = 0;
			for (int k = 0; k < LayerCount; k++)
			{
				if (slotClass[k] == null) continue;
				for (int j = 0; j < slotClass[k].Length; j++)
					if (slotClass[k][j] == c) slotLayer[k][j] = total++;
			}
			classLayers[c] = total;
		}

		// One fixed global fallback: the `missing` key's first decodable variant. Never
		// position-selected; procedural colours only when even `missing` did not decode.
		// A degraded slot in class C gets that image resized nearest to C's edge (P2.4).
		Image fallback = null;
		if (decoded[Missing] != null)
			foreach (var image in decoded[Missing])
				if (image != null) { fallback = image; break; }

		var arrays = new Texture2DArray[classCount];
		var classSources = new TileSource[classCount][];
		int totalLayers = 0;
		for (int c = 0; c < classCount; c++)
		{
			var packed = new Image[classLayers[c]];
			var sources = new TileSource[classLayers[c]];
			for (int k = 0; k < LayerCount; k++)
			{
				if (slotClass[k] == null) continue;
				for (int j = 0; j < slotClass[k].Length; j++)
				{
					if (slotClass[k][j] != c) continue;
					int layer = slotLayer[k][j];
					if (slotValid[k][j] && decoded[k][j] != null)
					{
						packed[layer] = decoded[k][j];
						sources[layer] = TileSource.Png;
						continue;
					}
					packed[layer] = fallback == null ? ProceduralTile(k, classSizes[c]) : Fit(fallback, classSizes[c]);
					sources[layer] = fallback != null ? TileSource.Missing : TileSource.Procedural;
				}
			}

			var list = new Godot.Collections.Array<Image>();
			for (int i = 0; i < packed.Length; i++) list.Add(packed[i]);
			var array = new Texture2DArray();
			Error err = array.CreateFromImages(list);
			if (err != Error.Ok || array.GetLayers() == 0)
			{
				GD.PushWarning($"texpack: {root}: texture array build failed — using procedural tiles");
				return Procedural();
			}
			arrays[c] = array;
			classSources[c] = sources;
			totalLayers += packed.Length;
		}

		// Per-key routing tables: the class-0 slot list (LayersFor / frozen assertions), the
		// decodable (layer, class) pairs the hash indexes, and the primary slot per key.
		var primary = new (int Layer, int Class)[LayerCount];
		var keyLayers = new int[LayerCount][];
		var validSlots = new (int Layer, int Class)[LayerCount][];
		int resolved = 0, variantKeys = 0;
		for (int k = 0; k < LayerCount; k++)
		{
			if (slotClass[k] == null) continue;
			primary[k] = (slotLayer[k][0], slotClass[k][0]);
			var zero = new List<int>();
			var valid = new List<(int, int)>();
			for (int j = 0; j < slotClass[k].Length; j++)
			{
				if (slotClass[k][j] == 0) zero.Add(slotLayer[k][j]);
				if (slotValid[k][j]) valid.Add((slotLayer[k][j], slotClass[k][j]));
			}
			keyLayers[k] = zero.ToArray();
			validSlots[k] = valid.ToArray();
			if (valid.Count >= 2) variantKeys++;
			if (k < KeyCount && valid.Count > 0) resolved++;
		}

		// The 60-cell table is the frozen base mapping; only a declared override claims its cell.
		// Undeclared override cells still own their fixed slots but resolve to the base key.
		var table = new int[Frozen.Length];
		for (int c = 0; c < table.Length; c++)
			table[c] = primary[provided[KeyCount + c] ? KeyCount + c : Frozen[c]].Layer;

		_resolved = table;
		_keyLayers = keyLayers;
		_validSlots = validSlots;
		_primary = primary;
		_missingSlot = primary[Missing];
		_provided = provided;

		// VRAM report (design P2.4): printed only for mixed packs, RGBA8 = 4 B/texel.
		string vramLine = null;
		if (classCount > 1)
		{
			var parts = new List<string>(classCount);
			long bytesTotal = 0;
			for (int c = 0; c < classCount; c++)
			{
				long bytes = (long)classSizes[c] * classSizes[c] * 4 * classLayers[c];
				bytesTotal += bytes;
				parts.Add($"{classSizes[c]}x{classSizes[c]}: {classLayers[c]} {(classLayers[c] == 1 ? "layer" : "layers")} ({bytes / 1024.0:0.###} KiB)");
			}
			vramLine = $"texpack: {root}: {classCount} size classes — {string.Join(", ", parts)}, total {bytesTotal / 1024.0:0.###} KiB";
		}

		return new Pack
		{
			Array = arrays[0], Arrays = arrays, Classes = classCount, ClassSizes = classSizes,
			ClassLayers = classLayers, VramLine = vramLine,
			TileSize = tileSize, Name = name, Source = root, Resolved = resolved,
			FaceOverrides = faceOverrides, Sources = classSources[0], Layers = totalLayers,
			VariantKeys = variantKeys, KeyLayers = keyLayers,
		};
	}

	private static Pack Procedural()
	{
		_resolved = Frozen;
		_keyLayers = FrozenSlots();
		_validSlots = EmptySlots();
		_primary = FrozenPrimary();
		_missingSlot = (Missing, 0);
		_provided = new bool[LayerCount];
		var list = new Godot.Collections.Array<Image>();
		var sources = new TileSource[KeyCount];
		for (int i = 0; i < KeyCount; i++)
		{
			list.Add(ProceduralTile(i, DefaultTileSize));
			sources[i] = TileSource.Procedural;
		}
		var array = new Texture2DArray();
		Error err = array.CreateFromImages(list);
		if (err != Error.Ok)
			GD.PushWarning("texpack: procedural tile array failed to build — rendering will be blank");
		return new Pack
		{
			Array = array,
			Arrays = new[] { array },
			Classes = 1,
			ClassSizes = new[] { DefaultTileSize },
			ClassLayers = new[] { KeyCount },
			TileSize = DefaultTileSize,
			Name = "procedural",
			Source = "built-in",
			Resolved = KeyCount,
			Procedural = true,
			Sources = sources,
			Layers = KeyCount,
			KeyLayers = _keyLayers,
		};
	}

	// ---- tile reading ----------------------------------------------------

	private static Image ReadTile(string path, string raw, string root, string label)
	{
		Image image;
		if (path.StartsWith("res://"))
		{
			// Imported resources are the only res:// path that survives an export. Existence
			// is checked first so a missing tile warns once instead of logging load errors.
			image = ResourceLoader.Exists(path) ? ResourceLoader.Load<Texture2D>(path)?.GetImage() : null;
			if (image == null)
			{
				GD.PushWarning($"texpack: {root}: tile {label}: not a decodable PNG: {raw} — using 'missing'");
				return null;
			}
		}
		else
		{
			byte[] bytes = FileAccess.GetFileAsBytes(path);
			if (bytes == null)
			{
				GD.PushWarning($"texpack: {root}: tile {label}: file not found: {raw} — using 'missing'");
				return null;
			}
			image = new Image();
			if (image.LoadPngFromBuffer(bytes) != Error.Ok)
			{
				GD.PushWarning($"texpack: {root}: tile {label}: not a decodable PNG: {raw} — using 'missing'");
				return null;
			}
		}

		// Array layers must share width, height, format and mipmaps: a PNG's own format
		// varies (opaque RGB next to RGBA leaves), so every tile lands on Rgba8. Size itself
		// is the caller's business: a square PNG that is not `tile_size` is its own class.
		image.Convert(Image.Format.Rgba8);
		return image;
	}

	/// <summary>`missing` at class C's edge: nearest-neighbour resize, never a cross-size copy.</summary>
	private static Image Fit(Image missing, int edge)
	{
		if (missing.GetWidth() == edge) return missing;
		var copy = (Image)missing.Duplicate();
		copy.Resize(edge, edge, Image.Interpolation.Nearest);
		return copy;
	}

	private static Image ProceduralTile(int index, int size)
	{
		// Variant slots can outnumber the 12 fallback colours; base keys keep theirs.
		var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
		image.Fill(FallbackColors[index % FallbackColors.Length]);
		return image;
	}

	// ---- small helpers ---------------------------------------------------

	private static string[] BuildFaceKeys()
	{
		var keys = new string[Renderable.Length * FaceSuffix.Length];
		for (int b = 0; b < Renderable.Length; b++)
			for (int f = 0; f < FaceSuffix.Length; f++)
				keys[b * 6 + f] = $"{Renderable[b].ToString().ToLowerInvariant()}_{FaceSuffix[f]}";
		return keys;
	}

	private static bool IsInt(Variant v) =>
		(v.VariantType == Variant.Type.Float || v.VariantType == Variant.Type.Int)
		&& v.AsDouble() == Math.Floor(v.AsDouble());

	private static bool IsInt(Variant v, double expected) => IsInt(v) && v.AsDouble() == expected;

	private static string DirName(string root)
	{
		string r = root.TrimEnd('/', '\\');
		int cut = r.LastIndexOfAny(new[] { '/', '\\' });
		return cut < 0 ? r : r[(cut + 1)..];
	}
}
