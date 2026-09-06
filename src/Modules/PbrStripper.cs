using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Texture_Set_Manager.Modules;

/// <summary>
/// Strips a folder back to color-only: deletes every MER/MERS/normal/heightmap texture that a
/// .texture_set.json references, then every .texture_set.json itself. Color textures are
/// never touched.
///
/// How targets are chosen - this part matters, and the obvious shortcut is wrong:
///
///   * What gets deleted is strictly what TextureSetHelper RESOLVED as a set's MER/MERS or
///     normal/heightmap layer. Never a name-pattern sweep. Plenty of legitimate *color*
///     textures end in "_normal" - sandstone_normal, rail_turned_normal - where "normal"
///     means the ordinary variant of a block, not a normal map. Guessing by suffix would
///     delete those outright, in packs that did nothing wrong.
///
///   * On top of each resolved path, the same name under every other supported extension is
///     deleted too. TextureSetHelper hands back only the one file the game would actually
///     load (.tga > .png > .jpg > .jpeg), so a "foo_mer.png" sitting next to the
///     "foo_mer.tga" it resolved would otherwise survive as an orphan and get picked up as
///     a color texture on a later generation pass. Those extra three paths are pure guesses
///     that usually don't exist, which is exactly why this is safe: the name came from a
///     real resolved PBR layer either way.
///
///   * Texture sets TextureSetHelper couldn't resolve (malformed JSON, MER and MERS both
///     declared, an unresolvable color layer) still get their .json deleted, but their
///     textures are left alone - there's no trustworthy way to know what they pointed at.
///     A folder in that state may keep some orphans; that's strictly better than deleting a
///     file on a guess.
///
/// Every layer path resolves inside the texture set's own folder by construction, so there's
/// no way for any of this to reach outside the folder the user picked.
///
/// This is destructive and irreversible, so the caller must confirm with the user first - in
/// this app that's the scope dialog, which doubles as the confirmation step.
/// </summary>
public static class PbrStripper
{
    public readonly record struct Result(int TextureSetsDeleted, int TexturesDeleted, int Failed);

    /// <param name="root">Folder to strip.</param>
    /// <param name="searchOption">Whether to descend into subfolders.</param>
    public static Result Strip(string root, SearchOption searchOption = SearchOption.AllDirectories)
    {
        if (!Directory.Exists(root)) return new Result(0, 0, 0);

        var failed = 0;

        // ── Pass 1: resolve everything before deleting anything ──────────────
        // The color list has to be complete before the first delete: a folder could name one
        // texture as another set's normal/MER layer, and art wins over cleanup.
        var pbrPaths = new List<string>();
        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var resolved = TextureSetHelper.ResolveTextureSets(root, searchOption);

        foreach (var set in resolved)
        {
            // Inline layers (a hex string or an RGB array) have no file behind them -
            // nothing to delete, and the texture set is going away regardless.
            if (set.Color is { IsInline: false, FilePath: not null } color)
            {
                foreach (var variant in ExtensionVariants(color.FilePath))
                    protectedPaths.Add(variant);
            }

            if (set.Mer is { IsInline: false, FilePath: not null } mer)
                pbrPaths.Add(mer.FilePath);

            if (set.NormalOrHeight is { IsInline: false, FilePath: not null } normalOrHeight)
                pbrPaths.Add(normalOrHeight.FilePath);
        }

        // ── Pass 2: delete the PBR textures ──────────────────────────────────
        var texturesDeleted = 0;

        foreach (var pbrPath in pbrPaths)
        {
            foreach (var candidate in ExtensionVariants(pbrPath))
            {
                if (protectedPaths.Contains(candidate)) continue;
                if (!File.Exists(candidate)) continue;

                try
                {
                    File.Delete(candidate);
                    texturesDeleted++;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[STRIPPER] Couldn't delete '{candidate}': {ex.Message}");
                    failed++;
                }
            }
        }

        // ── Pass 3: delete the descriptors ───────────────────────────────────
        // A plain scan rather than the resolved list, so unresolvable sets go too - leaving one
        // behind would make a later generation pass skip its color texture as "already has a
        // texture set", which is the one outcome this whole operation exists to avoid.
        var setsDeleted = 0;

        try
        {
            foreach (var jsonPath in Directory.GetFiles(root, "*.texture_set.json", searchOption))
            {
                try
                {
                    File.Delete(jsonPath);
                    setsDeleted++;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[STRIPPER] Couldn't delete '{jsonPath}': {ex.Message}");
                    failed++;
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[STRIPPER] Couldn't scan '{root}' for texture sets: {ex.Message}");
            failed++;
        }

        Trace.WriteLine($"[STRIPPER] Removed {setsDeleted} texture set(s) and {texturesDeleted} PBR texture(s) from '{root}' ({failed} failure(s)).");
        return new Result(setsDeleted, texturesDeleted, failed);
    }

    /// <summary>The given file path under every extension the game (and this app) supports,
    /// the path's own included - see the class comment for why the ones that don't exist are
    /// worth trying anyway.</summary>
    private static IEnumerable<string> ExtensionVariants(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var nameNoExt = Path.GetFileNameWithoutExtension(path);

        foreach (var ext in EnvironmentVariables.supportedFileExtensions)
            yield return Path.Combine(folder, nameNoExt + ext);
    }
}
