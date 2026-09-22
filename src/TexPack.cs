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

    /// <summary>Frozen layer order. Never renumbered.</summary>
    public static readonly string[] Keys =
    {
        "stone", "dirt", "grass_top", "grass_side", "grass_bottom", "sand",
        "wood_side", "wood_top", "plank", "leaves", "bedrock", "missing",
    };

    private const int Missing = 11;

    /// <summary>Rung-4 art, authored in sRGB — the inverse of Blocks.ColorOf's SrgbToLinear.</summary>
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
        public TileSource[] Sources;
    }

    /// <summary>
    /// Frozen Block+Face -> tile index map, over the <see cref="Block"/> enum — never over
    /// <c>Blocks.Palette</c>, which omits Bedrock even though bedrock is meshed.
    /// </summary>
    public static int TileIndex(Block b, int face) => b switch
    {
        Block.Stone => 0,
        Block.Dirt => 1,
        Block.Grass => face switch { Face.Top => 2, Face.Bottom => 4, _ => 3 },
        Block.Sand => 5,
        Block.Wood => face is Face.Top or Face.Bottom ? 7 : 6,
        Block.Plank => 8,
        Block.Leaves => 9,
        Block.Bedrock => 10,
        _ => Missing, // Air is never meshed; magenta marks anything that slips through
    };

    /// <summary>Runs discovery and prints the one success line. Never returns null.</summary>
    public static Pack Load(string cliPack)
    {
        Pack pack = null;
        if (!string.IsNullOrWhiteSpace(cliPack)) pack = TryPack(cliPack.Trim());
        if (pack == null) pack = TrySelected();
        if (pack == null) pack = TryPack("res://texturepacks/default");
        if (pack == null) pack = Procedural();

        GD.Print($"texpack: using '{pack.Name}' ({pack.Source}) tile_size={pack.TileSize} tiles={pack.Resolved}/12");
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

        var images = new Image[KeyCount];
        var sources = new TileSource[KeyCount];
        int resolved = 0;
        if (tiles != null)
        {
            foreach (var key in tiles.Keys)
            {
                string k = key.AsString();
                int index = Array.IndexOf(Keys, k);
                if (index < 0)
                {
                    GD.PushWarning($"texpack: {root}: unknown tile key '{k}' — ignored");
                    continue;
                }
                var value = tiles[key];
                if (value.VariantType != Variant.Type.String || value.AsString().Length == 0)
                {
                    GD.PushWarning($"texpack: {root}: tile '{k}' is not a path — using 'missing'");
                    continue;
                }
                string raw = value.AsString();
                string path = ResolveTilePath(raw, root, rootFull);
                if (path == null)
                {
                    GD.PushWarning($"texpack: {root}: tile '{k}': '{raw}' escapes the pack root — using 'missing'");
                    continue;
                }
                images[index] = ReadTile(path, raw, root, k, tileSize);
                if (images[index] != null)
                {
                    sources[index] = TileSource.Png;
                    resolved++;
                }
            }
        }

        // An absent or rejected tile becomes the pack's `missing` tile; with no usable
        // `missing` tile, that one tile becomes its own procedural colour.
        for (int i = 0; i < KeyCount; i++)
        {
            if (images[i] != null) continue;
            bool useMissing = i != Missing && images[Missing] != null;
            images[i] = useMissing ? images[Missing] : ProceduralTile(i, tileSize);
            sources[i] = useMissing ? TileSource.Missing : TileSource.Procedural;
        }

        var list = new Godot.Collections.Array<Image>();
        for (int i = 0; i < KeyCount; i++) list.Add(images[i]);
        var array = new Texture2DArray();
        Error err = array.CreateFromImages(list);
        if (err != Error.Ok || array.GetLayers() == 0)
        {
            GD.PushWarning($"texpack: {root}: texture array build failed — using procedural tiles");
            return Procedural();
        }

        return new Pack
        {
            Array = array, TileSize = tileSize, Name = name, Source = root, Resolved = resolved, Sources = sources,
        };
    }

    private static Pack Procedural()
    {
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
        };
    }

    // ---- tile reading ----------------------------------------------------

    private static Image ReadTile(string path, string raw, string root, string key, int size)
    {
        Image image;
        if (path.StartsWith("res://"))
        {
            // Imported resources are the only res:// path that survives an export. Existence
            // is checked first so a missing tile warns once instead of logging load errors.
            image = ResourceLoader.Exists(path) ? ResourceLoader.Load<Texture2D>(path)?.GetImage() : null;
            if (image == null)
            {
                GD.PushWarning($"texpack: {root}: tile '{key}': not a decodable PNG: {raw} — using 'missing'");
                return null;
            }
        }
        else
        {
            byte[] bytes = FileAccess.GetFileAsBytes(path);
            if (bytes == null)
            {
                GD.PushWarning($"texpack: {root}: tile '{key}': file not found: {raw} — using 'missing'");
                return null;
            }
            image = new Image();
            if (image.LoadPngFromBuffer(bytes) != Error.Ok)
            {
                GD.PushWarning($"texpack: {root}: tile '{key}': not a decodable PNG: {raw} — using 'missing'");
                return null;
            }
        }

        if (image.GetWidth() != size || image.GetHeight() != size)
        {
            GD.PushWarning($"texpack: {root}: tile '{key}': {image.GetWidth()}x{image.GetHeight()} does not match tile_size {size} — using 'missing'");
            return null;
        }

        // Array layers must share width, height, format and mipmaps: a PNG's own format
        // varies (opaque RGB next to RGBA leaves), so every tile lands on Rgba8.
        image.Convert(Image.Format.Rgba8);
        return image;
    }

    private static Image ProceduralTile(int index, int size)
    {
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        image.Fill(FallbackColors[index]);
        return image;
    }

    // ---- small helpers ---------------------------------------------------

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
