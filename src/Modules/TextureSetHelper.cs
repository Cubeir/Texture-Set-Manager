using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Newtonsoft.Json.Linq;
using static Texture_Set_Manager.Modules.Helpers;

namespace Texture_Set_Manager.Modules;

// ══════════════════════════════════════════════════════════════════════════════
//  TextureSetHelper  ──  parsing, resolution, and virtual-bitmap creation
// ══════════════════════════════════════════════════════════════════════════════

public static class TextureSetHelper
{
    public enum TextureKind { Color, Mer, Normal, Heightmap }

    /// <summary>
    /// Discriminated union: either a real file path or an inline colour value.
    /// </summary>
    public sealed class TextureLayerValue
    {
        public string? FilePath { get; }

        public bool IsInline { get; }
        /// <summary>Parsed RGBA components (0-255). Always length 4 internally.</summary>
        public byte[] InlineRgba { get; } = Array.Empty<byte>();
        /// <summary>Number of components as originally written (3 or 4).</summary>
        public int InlineChannels { get; }
        /// <summary>True when the source was a hex string (e.g. "#B48CBE").</summary>
        public bool IsHex { get; }
        public JToken SourceToken { get; }

        private TextureLayerValue(JToken sourceToken, byte[] rgba, int originalChannels, bool isHex)
        {
            IsInline = true;
            SourceToken = sourceToken;
            InlineRgba = rgba;
            InlineChannels = originalChannels;   // the count as it appeared in the file
            IsHex = isHex;
        }

        private TextureLayerValue(string filePath)
        {
            FilePath = filePath;
            SourceToken = JValue.CreateNull();
        }

        public static TextureLayerValue FromFile(string path) => new(path);

        public static TextureLayerValue? TryParseInline(JToken token)
        {
            // Hex string
            if (token.Type == JTokenType.String)
            {
                var s = token.Value<string>()!.Trim();
                if (s.StartsWith('#') && TryParseHex(s, out var rgba, out var originalChannels))
                    return new TextureLayerValue(token, rgba, originalChannels, isHex: true);
                return null;
            }

            // Array of numbers (RGB triplet or RGBA quadruplet)
            if (token is JArray arr && arr.Count is 3 or 4)
            {
                var originalChannels = arr.Count;
                var comps = new byte[originalChannels];
                for (var i = 0; i < originalChannels; i++)
                {
                    if (!TryGetByte(arr[i], out comps[i]))
                        return null;
                }
                // Pad to 4 channels internally, but remember the original count
                var rgba = originalChannels == 4
                    ? comps
                    : new[] { comps[0], comps[1], comps[2], (byte)255 };
                return new TextureLayerValue(token, rgba, originalChannels, isHex: false);
            }

            return null;
        }

        private static bool TryParseHex(string hex, out byte[] rgba, out int originalChannels)
        {
            rgba = Array.Empty<byte>();
            originalChannels = 0;
            hex = hex.TrimStart('#');

            if (hex.Length == 6)
            {
                if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v))
                    return false;
                rgba = new[] { (byte)(v >> 16), (byte)(v >> 8), (byte)v, (byte)255 };
                originalChannels = 3;
                return true;
            }
            if (hex.Length == 8)
            {
                if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v))
                    return false;
                rgba = new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
                originalChannels = 4;
                return true;
            }
            return false;
        }

        private static bool TryGetByte(JToken t, out byte b)
        {
            b = 0;
            double d;
            if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer)
                d = t.Value<double>();
            else if (t.Type == JTokenType.String && double.TryParse(t.Value<string>(), out d))
            { /* ok */ }
            else return false;

            b = (byte)Math.Clamp((int)Math.Round(d), 0, 255);
            return true;
        }

        /// <summary>Creates a 1×1 virtual Bitmap from the inline colour value.</summary>
        public Bitmap ToVirtualBitmap()
        {
            var bmp = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
            bmp.SetPixel(0, 0, Color.FromArgb(InlineRgba[3], InlineRgba[0], InlineRgba[1], InlineRgba[2]));
            return bmp;
        }

        /// <summary>
        /// Serialises the (possibly modified) 1×1 bitmap back to exactly the format
        /// it was originally written in: RGB hex stays RGB hex, RGBA array stays RGBA
        /// array, etc. The alpha channel is always preserved from the bitmap as-is.
        /// </summary>
        public JToken SerializeVirtual(Bitmap bmp)
        {
            var c = bmp.GetPixel(0, 0);
            byte r = c.R, g = c.G, b = c.B, a = c.A;

            if (IsHex)
            {
                return InlineChannels == 3
                    ? new JValue($"#{r:X2}{g:X2}{b:X2}")
                    : new JValue($"#{r:X2}{g:X2}{b:X2}{a:X2}");
            }

            return InlineChannels == 3
                ? new JArray(r, g, b)
                : new JArray(r, g, b, a);
        }

        /// <summary>Human-readable description used in reports and logs.</summary>
        public string Describe()
        {
            if (!IsInline) return Path.GetFileName(FilePath!);

            return IsHex
                ? (InlineChannels == 3
                    ? $"#{InlineRgba[0]:X2}{InlineRgba[1]:X2}{InlineRgba[2]:X2}"
                    : $"#{InlineRgba[0]:X2}{InlineRgba[1]:X2}{InlineRgba[2]:X2}{InlineRgba[3]:X2}")
                : (InlineChannels == 3
                    ? $"[{InlineRgba[0]}, {InlineRgba[1]}, {InlineRgba[2]}]"
                    : $"[{InlineRgba[0]}, {InlineRgba[1]}, {InlineRgba[2]}, {InlineRgba[3]}]");
        }
    }

    public sealed class ResolvedTextureSet
    {
        public string JsonFilePath { get; init; } = "";
        public JObject RootJson { get; init; } = new();
        public JObject SetNode { get; init; } = new();

        public TextureLayerValue Color { get; init; } = null!;
        public TextureLayerValue? Mer { get; init; }
        public TextureLayerValue? NormalOrHeight { get; init; }
        public bool IsHeightmap { get; init; }

        /// <summary>True when the MER layer was written as metalness_emissive_roughness_subsurface.</summary>
        public bool IsSubsurface { get; init; }

        /// <summary>
        /// Layers the JSON declares but that resolve to nothing — a texture name with no matching
        /// file in any supported extension. Without this the layer would just come back null and
        /// be indistinguishable from "not declared at all", which hides a genuine pack error from
        /// anything reporting on the set.
        /// </summary>
        public IReadOnlyList<(string Key, string DeclaredName)> UnresolvedLayers { get; init; }
            = Array.Empty<(string, string)>();
    }

    public sealed class LoadedTextureSet
    {
        public ResolvedTextureSet Resolved { get; init; } = null!;

        public Bitmap ColorBmp { get; set; } = null!;
        public bool ColorIsVirtual { get; init; }

        public Bitmap? MerBmp { get; set; }
        public bool MerIsVirtual { get; init; }

        public Bitmap? NormalBmp { get; set; }
        public bool NormalIsVirtual { get; init; }

        public bool ColorDirty { get; set; }
        public bool MerDirty { get; set; }
        public bool NormalDirty { get; set; }

        public void DisposeBitmaps()
        {
            ColorBmp?.Dispose();
            MerBmp?.Dispose();
            NormalBmp?.Dispose();
        }
    }

    private static readonly string[] SupportedExtensions = { ".tga", ".png", ".jpg", ".jpeg" };

    /// <summary>
    /// Scans a root folder, parses all .texture_set.json files, validates them
    /// per the Minecraft spec, and returns the valid resolved sets.
    /// </summary>
    public static IReadOnlyList<ResolvedTextureSet> ResolveTextureSets(string packRoot, SearchOption searchOption = SearchOption.AllDirectories)
    {
        if (string.IsNullOrEmpty(packRoot) || !Directory.Exists(packRoot))
            return Array.Empty<ResolvedTextureSet>();

        var results = new List<ResolvedTextureSet>();

        foreach (var jsonFile in Directory.GetFiles(packRoot, "*.texture_set.json", searchOption))
        {
            var resolved = ResolveTextureSet(jsonFile);
            if (resolved != null) results.Add(resolved);
        }

        return results;
    }

    /// <summary>
    /// Parses and validates a single .texture_set.json. Returns null (and traces why)
    /// when the file isn't a usable texture set.
    /// </summary>
    public static ResolvedTextureSet? ResolveTextureSet(string jsonFile)
    {
        try
        {
            var text = File.ReadAllText(jsonFile);
            var root = JObject.Parse(text);

            if (root.SelectToken("minecraft:texture_set") is not JObject set)
            {
                Trace.WriteLine($"[TEXTURESET] Skipping '{jsonFile}': missing minecraft:texture_set node.");
                return null;
            }

            var folder = Path.GetDirectoryName(jsonFile)!;

            var colorToken = set["color"];
            if (colorToken == null)
            {
                Trace.WriteLine($"[TEXTURESET] Skipping '{jsonFile}': no color layer defined.");
                return null;
            }

            var colorLayer = ResolveLayer(folder, colorToken);
            if (colorLayer == null)
            {
                Trace.WriteLine($"[TEXTURESET] Skipping '{jsonFile}': color layer could not be resolved.");
                return null;
            }

            var merToken = set["metalness_emissive_roughness"];
            var mersToken = set["metalness_emissive_roughness_subsurface"];

            if (merToken != null && mersToken != null)
            {
                Trace.WriteLine($"[TEXTURESET] Skipping '{jsonFile}': both MER and MERS defined (mutually exclusive).");
                return null;
            }

            var merLayer = ResolveLayer(folder, merToken ?? mersToken);

            var normalToken = set["normal"];
            var heightmapToken = set["heightmap"];

            if (normalToken != null && heightmapToken != null)
            {
                Trace.WriteLine($"[TEXTURESET] Skipping '{jsonFile}': both normal and heightmap defined (mutually exclusive).");
                return null;
            }

            var normalLayer = ResolveLayer(folder, normalToken);
            var heightmapLayer = ResolveLayer(folder, heightmapToken);
            var isHeightmap = heightmapToken != null;

            var unresolved = new List<(string, string)>();
            NoteIfUnresolved(unresolved, merToken != null ? "metalness_emissive_roughness" : "metalness_emissive_roughness_subsurface", merToken ?? mersToken, merLayer);
            NoteIfUnresolved(unresolved, "normal", normalToken, normalLayer);
            NoteIfUnresolved(unresolved, "heightmap", heightmapToken, heightmapLayer);

            return new ResolvedTextureSet
            {
                JsonFilePath = jsonFile,
                RootJson = root,
                SetNode = set,
                Color = colorLayer,
                Mer = merLayer,
                NormalOrHeight = normalLayer ?? heightmapLayer,
                IsHeightmap = isHeightmap,
                IsSubsurface = mersToken != null,
                UnresolvedLayers = unresolved,
            };
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TEXTURESET] Error resolving '{jsonFile}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Loads all bitmaps for a single resolved texture set. Virtual (inline) colours
    /// become 1×1 bitmaps and are flagged accordingly. Returns null (and leaves nothing
    /// allocated) if the color bitmap can't be loaded.
    ///
    /// This is deliberately a single-item operation rather than a batch: decoding an
    /// image from disk is real, sometimes-slow I/O work, so callers can pipeline
    /// load → process → dispose per texture set and report progress at that granularity.
    /// </summary>
    public static LoadedTextureSet? LoadTextureSet(ResolvedTextureSet rs)
    {
        // If the color layer loads fine but the MER or normal layer then *throws* while
        // loading (rather than just returning null), the already-loaded bitmaps would
        // never be disposed — a real (if rare) native GDI+ handle + memory leak. Track
        // everything allocated here and dispose it on any failure path via `finally`.
        Bitmap? colorBmp = null;
        Bitmap? merBmp = null;
        Bitmap? normalBmp = null;
        var success = false;

        try
        {
            colorBmp = LoadLayer(rs.Color);
            if (colorBmp == null)
            {
                Trace.WriteLine($"[TEXTURESET] Skipping texture set '{rs.JsonFilePath}': color bitmap could not be loaded.");
                return null;
            }

            if (rs.Mer != null)
            {
                merBmp = LoadLayer(rs.Mer);
                if (merBmp == null)
                    Trace.WriteLine($"[TEXTURESET] Warning for '{rs.JsonFilePath}': MER layer could not be loaded.");
            }

            if (rs.NormalOrHeight != null)
            {
                normalBmp = LoadLayer(rs.NormalOrHeight);
                if (normalBmp == null)
                    Trace.WriteLine($"[TEXTURESET] Warning for '{rs.JsonFilePath}': normal/heightmap layer could not be loaded.");
            }

            var result = new LoadedTextureSet
            {
                Resolved = rs,
                ColorBmp = colorBmp,
                ColorIsVirtual = rs.Color.IsInline,
                MerBmp = merBmp,
                MerIsVirtual = rs.Mer?.IsInline ?? false,
                NormalBmp = normalBmp,
                NormalIsVirtual = rs.NormalOrHeight?.IsInline ?? false,
            };
            success = true;
            return result;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TEXTURESET] Error loading texture set '{rs.JsonFilePath}': {ex.Message}");
            return null;
        }
        finally
        {
            if (!success)
            {
                colorBmp?.Dispose();
                merBmp?.Dispose();
                normalBmp?.Dispose();
            }
        }
    }

    /// <summary>Batch convenience wrapper — loads every resolved set sequentially.</summary>
    public static IReadOnlyList<LoadedTextureSet> LoadTextureSets(IReadOnlyList<ResolvedTextureSet> resolved)
    {
        var results = new List<LoadedTextureSet>(resolved.Count);
        foreach (var rs in resolved)
        {
            var lts = LoadTextureSet(rs);
            if (lts != null) results.Add(lts);
        }
        return results;
    }

    /// <summary>
    /// Records a layer that the JSON declares by name but that no file on disk satisfies. Only
    /// string-valued tokens qualify: an inline colour that failed to parse isn't a missing file,
    /// and a token that was never written isn't missing at all.
    /// </summary>
    private static void NoteIfUnresolved(List<(string, string)> into, string key, JToken? token, TextureLayerValue? resolved)
    {
        if (token == null || resolved != null) return;
        if (token.Type != JTokenType.String) return;

        var name = token.Value<string>()?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        into.Add((key, name));
    }

    private static TextureLayerValue? ResolveLayer(string folder, JToken? token)
    {
        if (token == null) return null;

        var inline = TextureLayerValue.TryParseInline(token);
        if (inline != null) return inline;

        if (token.Type != JTokenType.String) return null;

        var name = token.Value<string>()!.Trim();
        if (string.IsNullOrEmpty(name)) return null;

        var filePath = FindTextureFile(folder, name);
        return filePath != null ? TextureLayerValue.FromFile(filePath) : null;
    }

    private static Bitmap? LoadLayer(TextureLayerValue layer)
    {
        if (layer.IsInline)
            return layer.ToVirtualBitmap();

        if (!File.Exists(layer.FilePath!))
            return null;

        return ReadImage(layer.FilePath!, false);
    }

    public static string? FindTextureFile(string folder, string textureName)
    {
        foreach (var ext in SupportedExtensions)
        {
            var target = Path.Combine(folder, textureName + ext);
            if (File.Exists(target))
                return target;

            try
            {
                var matches = Directory.GetFiles(folder, textureName + ext, SearchOption.TopDirectoryOnly);
                if (matches.Length > 0) return matches[0];
            }
            catch { /* access denied or directory missing */ }
        }

        return null;
    }

    /// <summary>
    /// Persists a loaded texture set's dirty bitmaps back to disk (or inline JSON).
    /// For real files: writes in the source format (TGA stays TGA, PNG stays PNG, etc.).
    /// For virtual bitmaps: patches the .texture_set.json in place.
    /// </summary>
    public static void SaveDirtyLayers(LoadedTextureSet lts)
    {
        var rs = lts.Resolved;
        var jsonDirty = false;

        if (lts.ColorDirty && lts.ColorBmp != null)
        {
            try
            {
                if (lts.ColorIsVirtual)
                {
                    rs.SetNode["color"] = rs.Color.SerializeVirtual(lts.ColorBmp);
                    jsonDirty = true;
                }
                else
                {
                    WriteBackBitmap(lts.ColorBmp, rs.Color.FilePath!);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[TEXTURESET] Error saving color layer for '{rs.JsonFilePath}': {ex.Message}");
            }
        }

        if (lts.MerDirty && lts.MerBmp != null && rs.Mer != null)
        {
            try
            {
                if (lts.MerIsVirtual)
                {
                    var merKey = rs.SetNode["metalness_emissive_roughness"] != null
                        ? "metalness_emissive_roughness"
                        : "metalness_emissive_roughness_subsurface";
                    rs.SetNode[merKey] = rs.Mer.SerializeVirtual(lts.MerBmp);
                    jsonDirty = true;
                }
                else
                {
                    WriteBackBitmap(lts.MerBmp, rs.Mer.FilePath!);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[TEXTURESET] Error saving MER layer for '{rs.JsonFilePath}': {ex.Message}");
            }
        }

        if (lts.NormalDirty && lts.NormalBmp != null && rs.NormalOrHeight != null)
        {
            try
            {
                if (lts.NormalIsVirtual)
                {
                    var normalKey = rs.IsHeightmap ? "heightmap" : "normal";
                    rs.SetNode[normalKey] = rs.NormalOrHeight.SerializeVirtual(lts.NormalBmp);
                    jsonDirty = true;
                }
                else
                {
                    WriteBackBitmap(lts.NormalBmp, rs.NormalOrHeight.FilePath!);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[TEXTURESET] Error saving normal/heightmap layer for '{rs.JsonFilePath}': {ex.Message}");
            }
        }

        if (jsonDirty)
        {
            try
            {
                File.WriteAllText(rs.JsonFilePath, rs.RootJson.ToString(Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[TEXTURESET] Error writing JSON for '{rs.JsonFilePath}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Writes a bitmap back to disk preserving the original file format.
    /// TGA  → TGA   PNG  → lossless 32-bpp ARGB PNG
    /// JPG  → maximum-quality JPEG   Other → TGA fallback
    /// </summary>
    public static void WriteBackBitmap(Bitmap bmp, string originalPath)
    {
        var ext = Path.GetExtension(originalPath).ToLowerInvariant();

        switch (ext)
        {
            case ".tga":
                WriteImageAsTGA(bmp, originalPath);
                break;

            case ".png":
                {
                    // EnsureArgb32 returns the *same* instance when bmp is already
                    // Format32bppArgb (the common case), so only dispose the canonical
                    // copy when it's actually a new object — otherwise we'd be disposing
                    // the caller's bitmap out from under them.
                    var canonical = EnsureArgb32(bmp);
                    try { canonical.Save(originalPath, ImageFormat.Png); }
                    finally { if (!ReferenceEquals(canonical, bmp)) canonical.Dispose(); }
                    break;
                }

            case ".jpg":
            case ".jpeg":
                {
                    var jpegEncoder = GetEncoder(ImageFormat.Jpeg);
                    if (jpegEncoder == null) goto default;

                    var qualityParam = new EncoderParameters(1);
                    qualityParam.Param[0] = new EncoderParameter(Encoder.Quality, 100L);

                    var canonical = EnsureArgb32(bmp);
                    try { canonical.Save(originalPath, jpegEncoder, qualityParam); }
                    finally { if (!ReferenceEquals(canonical, bmp)) canonical.Dispose(); }
                    break;
                }

            default:
                WriteImageAsTGA(bmp, originalPath);
                break;
        }
    }

    private static Bitmap EnsureArgb32(Bitmap src)
    {
        if (src.PixelFormat == PixelFormat.Format32bppArgb)
            return src;

        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.DrawImage(src, 0, 0);
        return dst;
    }

    private static ImageCodecInfo? GetEncoder(ImageFormat format)
    {
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
            if (codec.FormatID == format.Guid)
                return codec;
        return null;
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  FastBitmap  ──  LockBits-based pixel accessor, drop-in for GetPixel/SetPixel
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Replaces Bitmap.GetPixel/SetPixel for bulk pixel work. Each GetPixel/SetPixel
/// call on a System.Drawing.Bitmap round-trips through native GDI+ with format
/// checks and marshalling on every single pixel; for a 512x512 image that's a
/// quarter million native calls per full pass. FastBitmap instead locks the
/// bitmap once, bulk-copies its raw bytes into a managed buffer with a single
/// Marshal.Copy, and does all reads/writes against that plain byte[] (fast,
/// bounds-checked, no native calls). On Dispose it copies the buffer back
/// (only if opened writable) and unlocks.
///
/// Always requests Format32bppArgb regardless of the bitmap's real pixel
/// format - this exactly mirrors what GetPixel/SetPixel already did (they always
/// hand back/accept a plain ARGB Color regardless of underlying storage), so
/// output is unaffected: GDI+ performs the same implicit conversion on lock/unlock
/// that GetPixel/SetPixel performed internally per call.
///
/// No `unsafe` blocks are required, so no project/csproj changes are needed.
/// </summary>
public sealed class FastBitmap : IDisposable
{
    private readonly Bitmap _bitmap;
    private readonly BitmapData _data;
    private readonly byte[] _buffer;
    private readonly int _stride;
    private readonly bool _writable;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }

    public FastBitmap(Bitmap bitmap, bool writable)
    {
        _bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
        _writable = writable;

        Width = bitmap.Width;
        Height = bitmap.Height;

        _data = bitmap.LockBits(
            new Rectangle(0, 0, Width, Height),
            writable ? ImageLockMode.ReadWrite : ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        _stride = _data.Stride;
        _buffer = new byte[Math.Abs(_stride) * Height];
        System.Runtime.InteropServices.Marshal.Copy(_data.Scan0, _buffer, 0, _buffer.Length);
    }

    /// <summary>Pixel accessor. Reads/writes the managed buffer, never GDI+.</summary>
    public Color this[int x, int y]
    {
        get
        {
            var i = y * _stride + x * 4;
            // GDI+ 32bppArgb is stored little-endian as B, G, R, A
            return Color.FromArgb(_buffer[i + 3], _buffer[i + 2], _buffer[i + 1], _buffer[i + 0]);
        }
        set
        {
            var i = y * _stride + x * 4;
            _buffer[i + 0] = value.B;
            _buffer[i + 1] = value.G;
            _buffer[i + 2] = value.R;
            _buffer[i + 3] = value.A;
        }
    }

    /// <summary>Raw 32-bit ARGB read, avoids constructing a Color when only equality matters.</summary>
    public int GetArgb(int x, int y)
    {
        var i = y * _stride + x * 4;
        return (_buffer[i + 3] << 24) | (_buffer[i + 2] << 16) | (_buffer[i + 1] << 8) | _buffer[i + 0];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_writable)
                System.Runtime.InteropServices.Marshal.Copy(_buffer, 0, _data.Scan0, _buffer.Length);
        }
        finally
        {
            _bitmap.UnlockBits(_data);
        }
    }
}
