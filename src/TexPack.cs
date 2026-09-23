using System;
using System.Collections.Generic;
using Godot;

namespace Regress;

/// <summary>
/// Loads a texture pack — a directory with <c>pack.json</c> and the PNGs it names — into one
/// <see cref="Texture2DArray"/>. Discovery has four rungs and never fails: the last rung
/// synthesises the twelve tiles in memory, so the game always renders. The format, the key
/// order and the failure matrix are frozen in docs/texture-packs.md.
/// </summary>
public static class TexPack
{
    public const int KeyCount = 12;
    public const int DefaultTileSize = 16;

    /// <summary>Most variants one key may declare (design §5). 60 keys x 16 = 960 layers ceiling.</summary>
    public const int MaxVariants = 16;

    /// <summary>Frozen layer order. Never renumbered.</summary>
    public static readonly string[] Keys =
    {
        "stone", "dirt", "grass_top", "grass_side", "grass_bottom", "sand",
        "wood_side", "wood_top", "plank", "leaves", "bedrock", "missing",
    };

    private const int Missing = 11;

    /// <summary>The eight renderable blocks in cell order; cell index = block*6 + face.</summary>
    public static readonly Block[] Renderable =
        { Block.Stone, Block.Dirt, Block.Grass, Block.Sand, Block.Wood, Block.Plank, Block.Leaves, Block.Bedrock };

    /// <summary>Face-constant suffixes in Face index order (PosX=0 … NegZ=5).</summary>
    public static readonly string[] FaceSuffix = { "posx", "negx", "top", "bottom", "posz", "negz" };

    /// <summary>Optional per-face override keys; layer = KeyCount + cell.</summary>
    public static readonly string[] FaceKeys = BuildFaceKeys();

    /// <summary>Array layers the loader may need: 12 base + 48 optional per-face slots.
    /// This stays the frozen no-variant baseline slot space; a variant pack's array is
    /// <see cref="Pack.Layers"/> layers, computed by the uniform slot rule (design §2).</summary>
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
        public Texture2DArray Array;
        public int TileSize;
        public string Name;
        public string Source;
        public int Resolved;
        public bool Procedural;
        public int FaceOverrides;
        public TileSource[] Sources;

        /// <summary>Actual Texture2DArray layer count: the sum of every enumerated key's slots.</summary>
        public int Layers;

        /// <summary>Keys with at least two decodable variants (the new report line's K).</summary>
        public int VariantKeys;

        /// <summary>Slot layers per key, primary first; null = the pack does not enumerate that key.</summary>
        public int[][] KeyLayers;
    }

    /// <summary>
    /// docs/texture-packs.md's 48-cell fallback map, over the <see cref="Block"/> enum — never
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
    };

    /// <summary>The last accepted pack's resolution; the frozen fallback until one loads.</summary>
    private static int[] _resolved = Frozen;

    /// <summary>Slot layers per key, frozen order; null = the last pack does not enumerate it.</summary>
    private static int[][] _keyLayers = FrozenSlots();

    /// <summary>The selectable layers per key — decodable variants only, primary-first order.</summary>
    private static int[][] _validLayers = _keyLayers;

    /// <summary>The `missing` key's primary slot; what Air and shape misses resolve to.</summary>
    private static int _missingLayer = Missing;

    /// <summary>Override keys the last pack declared. A provided-but-bad override still counts:
    /// only these claim their (Block,Face) cell, while the other override cells keep owning
    /// their slots (fixed 60-layer space) but show the frozen base mapping. All false for the
    /// frozen/procedural layout.</summary>
    private static bool[] _provided = new bool[LayerCount];

    /// <summary>The pre-variants single-slot layout: base key k owns layer k, override cells absent.</summary>
    private static int[][] FrozenSlots()
    {
        var slots = new int[LayerCount][];
        for (int k = 0; k < KeyCount; k++) slots[k] = new[] { k };
        return slots;
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

    /// <summary>Key index for a 48-cell cell: the override cell when the pack declares it,
    /// else the frozen base key.</summary>
    private static int LayerKey(int cell) =>
        _provided[KeyCount + cell] ? KeyCount + cell : Frozen[cell];

    /// <summary>Key name for warnings — base keys first, then the 48 override keys.</summary>
    private static string KeyName(int key) => key < KeyCount ? Keys[key] : FaceKeys[key - KeyCount];

    /// <summary>Frozen Block+Face -> primary tile layer (variant 0). Per face: exact override,
    /// else the base fallback. Every existing caller keeps working.</summary>
    public static int TileIndex(Block b, int face)
    {
        int cell = Cell(b, face);
        return cell < 0 ? _missingLayer : _resolved[cell];
    }

    /// <summary>Position-selected tile layer: the primary when the key has one variant, else
    /// <c>Mix(worldX, worldY, worldZ, localFace) % validCount</c> — absolute world coordinates
    /// and the block's LOCAL face (design §3).</summary>
    public static int TileIndex(Block b, int face, int wx, int wy, int wz)
    {
        int cell = Cell(b, face);
        if (cell < 0) return _missingLayer;
        int[] valid = _validLayers[LayerKey(cell)];
        if (valid == null || valid.Length == 0) return _resolved[cell];
        return valid[(int)(Mix(wx, wy, wz, face) % (uint)valid.Length)];
    }

    /// <summary>The slot layers of one (Block,Face) key, primary first, length >= 1 (design §2).
    /// Do not mutate: the returned array is the loader's own table.</summary>
    public static int[] LayersFor(Block b, int face)
    {
        int cell = Cell(b, face);
        return cell < 0 ? new[] { _missingLayer } : _keyLayers[LayerKey(cell)];
    }

    /// <summary>Runs discovery and prints the one success line. Never returns null.</summary>
    public static Pack Load(string cliPack)
    {
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
        // variants survive (design §4).
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
                images[i] = ReadTile(path, raws[i], root, label, tileSize);
            }
            decoded[k] = images;
        }

        // Frozen key order, base keys first: each enumerated key contributes max(1, declared)
        // consecutive slots, one per declared variant; a key with no decodable variant degrades
        // to exactly one slot (today's bad-tile row, design §2/§4). Override cells are only
        // enumerated when the manifest supplies at least one override key.
        var keyLayers = new int[LayerCount][];
        var validLayers = new int[LayerCount][];
        int total = 0, resolved = 0, variantKeys = 0;
        for (int k = 0; k < LayerCount; k++)
        {
            if (k >= KeyCount && faceOverrides == 0) continue;
            var images = decoded[k];
            int n = images?.Length ?? 0;
            int valid = 0;
            if (images != null)
                foreach (var image in images)
                    if (image != null) valid++;
            if (valid >= 2) variantKeys++;
            if (k < KeyCount && valid > 0) resolved++;
            int slots = valid == 0 ? 1 : n;
            var layers = new int[slots];
            var selectable = new int[valid];
            int next = 0;
            for (int j = 0; j < slots; j++)
            {
                layers[j] = total + j;
                if (images != null && j < n && images[j] != null) selectable[next++] = total + j;
            }
            keyLayers[k] = layers;
            validLayers[k] = selectable;
            total += slots;
        }

        // One fixed global fallback: the `missing` key's first decodable variant. Never
        // position-selected; procedural colours only when even `missing` did not decode.
        Image fallback = null;
        if (decoded[Missing] != null)
            foreach (var image in decoded[Missing])
                if (image != null) { fallback = image; break; }

        var packed = new Image[total];
        var sources = new TileSource[total];
        for (int k = 0; k < LayerCount; k++)
        {
            if (keyLayers[k] == null) continue;
            var images = decoded[k];
            int n = images?.Length ?? 0;
            for (int j = 0; j < keyLayers[k].Length; j++)
            {
                int layer = keyLayers[k][j];
                if (images != null && j < n && images[j] != null)
                {
                    packed[layer] = images[j];
                    sources[layer] = TileSource.Png;
                    continue;
                }
                packed[layer] = fallback ?? ProceduralTile(k, tileSize);
                sources[layer] = fallback != null ? TileSource.Missing : TileSource.Procedural;
            }
        }

        var list = new Godot.Collections.Array<Image>();
        for (int i = 0; i < total; i++) list.Add(packed[i]);
        var array = new Texture2DArray();
        Error err = array.CreateFromImages(list);
        if (err != Error.Ok || array.GetLayers() == 0)
        {
            GD.PushWarning($"texpack: {root}: texture array build failed — using procedural tiles");
            return Procedural();
        }

        // The 48-cell table is the frozen base mapping; only a declared override claims its cell.
        // Undeclared override cells still own their fixed slots but resolve to the base key.
        var table = new int[Frozen.Length];
        for (int c = 0; c < table.Length; c++)
            table[c] = keyLayers[provided[KeyCount + c] ? KeyCount + c : Frozen[c]][0];

        _resolved = table;
        _keyLayers = keyLayers;
        _validLayers = validLayers;
        _missingLayer = keyLayers[Missing][0];
        _provided = provided;

        return new Pack
        {
            Array = array, TileSize = tileSize, Name = name, Source = root, Resolved = resolved,
            FaceOverrides = faceOverrides, Sources = sources, Layers = total,
            VariantKeys = variantKeys, KeyLayers = keyLayers,
        };
    }

    private static Pack Procedural()
    {
        _resolved = Frozen;
        _keyLayers = FrozenSlots();
        _validLayers = _keyLayers;
        _missingLayer = Missing;
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

    private static Image ReadTile(string path, string raw, string root, string label, int size)
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

        if (image.GetWidth() != size || image.GetHeight() != size)
        {
            GD.PushWarning($"texpack: {root}: tile {label}: {image.GetWidth()}x{image.GetHeight()} does not match tile_size {size} — using 'missing'");
            return null;
        }

        // Array layers must share width, height, format and mipmaps: a PNG's own format
        // varies (opaque RGB next to RGBA leaves), so every tile lands on Rgba8.
        image.Convert(Image.Format.Rgba8);
        return image;
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
