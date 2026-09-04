using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Texture_Set_Manager.Modules;

/// <summary>
/// Audits a folder's texture sets and flags PBR layers that are pixel-for-pixel identical
/// to their own color texture.
///
/// Why that matters: this app (and every other template generator) seeds MER/normal/heightmap
/// files as straight copies of the color texture. A layer that is *still* a byte-identical copy
/// long after generation is almost certainly one the artist never got around to painting — it's
/// dead weight in the pack and produces nonsense PBR data in-game. Rather than eyeballing
/// hundreds of files, this hands the author a list of exactly which ones to go after.
/// </summary>
public static class TextureSetAnalyzer
{
    public enum LayerRole { Mer, Mers, Normal, Heightmap }

    public sealed class LayerFinding
    {
        public LayerRole Role { get; init; }
        public string Label { get; init; } = "";
        public string Description { get; init; } = "";
        public bool IsIdenticalToColor { get; init; }
        public bool IsMissing { get; init; }
    }

    public sealed class SetFinding
    {
        public string JsonFilePath { get; init; } = "";
        public string RelativePath { get; init; } = "";
        public string ColorDescription { get; init; } = "";
        public List<LayerFinding> Layers { get; } = new();

        public IEnumerable<LayerFinding> Duplicates => Layers.Where(l => l.IsIdenticalToColor);
        public IEnumerable<LayerFinding> Missing => Layers.Where(l => l.IsMissing);
    }

    public sealed class AnalysisReport
    {
        public string RootFolder { get; init; } = "";
        public int JsonFilesFound { get; init; }
        public int SetsResolved { get; init; }
        public int SetsUnreadable { get; init; }
        public List<SetFinding> Findings { get; } = new();

        public IEnumerable<SetFinding> FlaggedSets => Findings.Where(f => f.Duplicates.Any());
        public IEnumerable<SetFinding> SetsWithMissingLayers => Findings.Where(f => f.Missing.Any());

        public int DuplicateLayerCount => Findings.Sum(f => f.Duplicates.Count());
        public int Count(LayerRole role) => Findings.SelectMany(f => f.Duplicates).Count(l => l.Role == role);
    }

    /// <summary>
    /// Walks every *.texture_set.json under <paramref name="rootFolder"/>, resolves it through
    /// TextureSetHelper, and compares each PBR layer against the set's own color layer.
    /// Runs entirely off the calling thread.
    /// </summary>
    public static Task<AnalysisReport> AnalyzeAsync(string rootFolder, CancellationToken token = default)
        => Task.Run(() => Analyze(rootFolder, token), token);

    public static AnalysisReport Analyze(string rootFolder, CancellationToken token = default)
    {
        var jsonFiles = Directory.Exists(rootFolder)
            ? Directory.GetFiles(rootFolder, "*.texture_set.json", SearchOption.AllDirectories)
            : Array.Empty<string>();

        var resolved = new List<TextureSetHelper.ResolvedTextureSet>();
        foreach (var jsonFile in jsonFiles)
        {
            token.ThrowIfCancellationRequested();
            var rs = TextureSetHelper.ResolveTextureSet(jsonFile);
            if (rs != null) resolved.Add(rs);
        }

        var report = new AnalysisReport
        {
            RootFolder = rootFolder,
            JsonFilesFound = jsonFiles.Length,
            SetsResolved = resolved.Count,
            SetsUnreadable = jsonFiles.Length - resolved.Count,
        };

        foreach (var rs in resolved)
        {
            token.ThrowIfCancellationRequested();

            var finding = new SetFinding
            {
                JsonFilePath = rs.JsonFilePath,
                RelativePath = MakeRelative(rootFolder, rs.JsonFilePath),
                ColorDescription = rs.Color.Describe(),
            };

            // Layers are loaded lazily and disposed as soon as the comparison is done — a
            // deep pack can hold thousands of texture sets and we never want more than one
            // set's worth of bitmaps alive at a time.
            TextureSetHelper.LoadedTextureSet? lts = null;
            try
            {
                lts = TextureSetHelper.LoadTextureSet(rs);
                if (lts == null)
                {
                    Trace.WriteLine($"[ANALYZER] Could not load '{rs.JsonFilePath}', skipping.");
                    continue;
                }

                if (rs.Mer != null)
                {
                    finding.Layers.Add(Evaluate(
                        rs.IsSubsurface ? LayerRole.Mers : LayerRole.Mer,
                        rs.IsSubsurface ? "MERS" : "MER",
                        rs.Mer, lts.MerBmp, rs.Color, lts.ColorBmp));
                }

                if (rs.NormalOrHeight != null)
                {
                    finding.Layers.Add(Evaluate(
                        rs.IsHeightmap ? LayerRole.Heightmap : LayerRole.Normal,
                        rs.IsHeightmap ? "Heightmap" : "Normal",
                        rs.NormalOrHeight, lts.NormalBmp, rs.Color, lts.ColorBmp));
                }

                // Layers the set names but that have no file behind them. Nothing to compare,
                // but very much worth telling the author about.
                foreach (var (key, declaredName) in rs.UnresolvedLayers)
                {
                    finding.Layers.Add(new LayerFinding
                    {
                        Role = RoleForKey(key),
                        Label = LabelForKey(key),
                        Description = declaredName,
                        IsMissing = true,
                    });
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ANALYZER] Error analyzing '{rs.JsonFilePath}': {ex.Message}");
            }
            finally
            {
                lts?.DisposeBitmaps();
            }

            report.Findings.Add(finding);
        }

        return report;
    }

    private static LayerRole RoleForKey(string key) => key switch
    {
        "metalness_emissive_roughness_subsurface" => LayerRole.Mers,
        "normal" => LayerRole.Normal,
        "heightmap" => LayerRole.Heightmap,
        _ => LayerRole.Mer,
    };

    private static string LabelForKey(string key) => key switch
    {
        "metalness_emissive_roughness_subsurface" => "MERS",
        "normal" => "Normal",
        "heightmap" => "Heightmap",
        _ => "MER",
    };

    private static LayerFinding Evaluate(
        LayerRole role, string label,
        TextureSetHelper.TextureLayerValue layer, Bitmap? layerBmp,
        TextureSetHelper.TextureLayerValue colorLayer, Bitmap colorBmp)
    {
        // A layer whose file couldn't be found or decoded is worth reporting in its own right,
        // but it can't be compared against anything.
        if (layerBmp == null)
        {
            return new LayerFinding
            {
                Role = role,
                Label = label,
                Description = layer.Describe(),
                IsMissing = true,
            };
        }

        // Same file referenced twice is identical by definition — no need to decode-compare.
        var samePath = !layer.IsInline && !colorLayer.IsInline &&
                       string.Equals(layer.FilePath, colorLayer.FilePath, StringComparison.OrdinalIgnoreCase);

        return new LayerFinding
        {
            Role = role,
            Label = label,
            Description = layer.Describe(),
            IsIdenticalToColor = samePath || AreIdentical(colorBmp, layerBmp),
        };
    }

    /// <summary>Pixel-for-pixel ARGB equality. Differing dimensions short-circuit to false.</summary>
    public static bool AreIdentical(Bitmap a, Bitmap b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Width != b.Width || a.Height != b.Height) return false;

        using var fa = new FastBitmap(a, writable: false);
        using var fb = new FastBitmap(b, writable: false);

        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
                if (fa.GetArgb(x, y) != fb.GetArgb(x, y))
                    return false;

        return true;
    }

    /// <summary>
    /// Renders the report as the block of text that lands in the sidebar log. Kept here rather
    /// than in the window so the wording lives next to the analysis that produced it.
    /// </summary>
    public static string BuildLogReport(AnalysisReport report, int maxListedSets = 40)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Texture set report for: {report.RootFolder}");
        sb.AppendLine();
        sb.Append($"{report.JsonFilesFound} texture set file(s) found, {report.SetsResolved} readable");
        if (report.SetsUnreadable > 0)
            sb.Append($", {report.SetsUnreadable} skipped as invalid or incomplete");
        sb.AppendLine(".");

        var flagged = report.FlaggedSets.ToList();
        if (flagged.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("No PBR texture was found to be identical to its color texture – nothing looks left over from a template.");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine($"{flagged.Count} texture set(s) carry {report.DuplicateLayerCount} PBR texture(s) identical to their own color texture:");
            sb.AppendLine();

            foreach (var set in flagged.Take(maxListedSets))
            {
                var layers = string.Join(", ", set.Duplicates.Select(l => $"{l.Label} ({l.Description})"));
                sb.AppendLine($"• {set.RelativePath} → {layers}");
            }

            if (flagged.Count > maxListedSets)
                sb.AppendLine($"…and {flagged.Count - maxListedSets} more (full list is in the debug logs).");

            sb.AppendLine();
            var breakdown = new List<string>();
            foreach (var role in new[] { LayerRole.Mer, LayerRole.Mers, LayerRole.Normal, LayerRole.Heightmap })
            {
                var count = report.Count(role);
                if (count > 0) breakdown.Add($"{count} {role.ToString().ToUpperInvariant()}");
            }
            sb.AppendLine($"Breakdown: {string.Join(", ", breakdown)}.");
            sb.AppendLine("These are most likely untouched template copies — worth painting or removing.");
        }

        var missing = report.SetsWithMissingLayers.ToList();
        if (missing.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{missing.Count} texture set(s) reference a PBR texture that couldn't be found or decoded:");
            foreach (var set in missing.Take(maxListedSets))
            {
                var layers = string.Join(", ", set.Missing.Select(l => $"{l.Label} ({l.Description})"));
                sb.AppendLine($"• {set.RelativePath} → {layers}");
            }
            if (missing.Count > maxListedSets)
                sb.AppendLine($"…and {missing.Count - maxListedSets} more.");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Full, uncapped listing — goes to the trace log so the sidebar stays readable.</summary>
    public static string BuildTraceReport(AnalysisReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"===== Texture Set Analysis: {report.RootFolder}");
        sb.AppendLine($"JSON files: {report.JsonFilesFound}, resolved: {report.SetsResolved}, unreadable: {report.SetsUnreadable}");

        foreach (var set in report.Findings)
        {
            var duplicates = set.Duplicates.ToList();
            var missing = set.Missing.ToList();
            if (duplicates.Count == 0 && missing.Count == 0) continue;

            sb.AppendLine($"  {set.RelativePath}  (color: {set.ColorDescription})");
            foreach (var layer in duplicates)
                sb.AppendLine($"    IDENTICAL TO COLOR  {layer.Label}: {layer.Description}");
            foreach (var layer in missing)
                sb.AppendLine($"    MISSING/UNREADABLE  {layer.Label}: {layer.Description}");
        }

        return sb.ToString();
    }

    private static string MakeRelative(string root, string fullPath)
    {
        try
        {
            return Path.GetRelativePath(root, fullPath);
        }
        catch
        {
            return fullPath;
        }
    }
}
