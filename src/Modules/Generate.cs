using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static Texture_Set_Manager.EnvironmentVariables;

namespace Texture_Set_Manager.Modules;

public static class Generate
{
    /// <summary>
    /// Main texture set template generator. Creates PBR texture set templates based on color textures.
    /// </summary>
    /// <returns>Tuple of (success, message) where success indicates operation outcome</returns>
    public static async Task<(bool success, string message)> GenerateTextureSetsAsync()
    {
        try
        {
            // ============================================
            // PHASE 1: Validate Input
            // ============================================
            Trace.WriteLine("=== PHASE 1: Input Validation ===");

            bool hasFiles = selectedFiles != null && selectedFiles.Length > 0;
            bool hasFolder = !string.IsNullOrWhiteSpace(selectedFolder);

            if (!hasFiles && !hasFolder)
            {
                Trace.WriteLine("No files or folders selected.");
                return (false, "No files or folders were selected for texture set generation.");
            }

            Trace.WriteLine($"Has files: {hasFiles} ({selectedFiles?.Length ?? 0} files)");
            Trace.WriteLine($"Has folder: {hasFolder} ({selectedFolder ?? "null"})");

            // ============================================
            // PHASE 2: Backup (if enabled)
            // ============================================
            if (Persistent.CreateBackup)
            {
                Trace.WriteLine("=== PHASE 2: Creating Backups ===");

                // Backup selected files
                if (hasFiles)
                {
                    bool fileBackupSuccess = await BackupFilesAsync(selectedFiles);
                    if (!fileBackupSuccess)
                    {
                        Trace.WriteLine("File backup was cancelled or failed.");
                    }
                }

                // Backup selected folder
                if (hasFolder && Directory.Exists(selectedFolder))
                {
                    bool folderBackupSuccess = await BackupFolderAsync(selectedFolder);
                    if (!folderBackupSuccess)
                    {
                        Trace.WriteLine("Folder backup was cancelled or failed.");
                    }
                }
            }
            else
            {
                Trace.WriteLine("=== PHASE 2: Backups Skipped (disabled) ===");
            }

            // ============================================
            // PHASE 3: Build Files List
            // ============================================
            Trace.WriteLine("=== PHASE 3: Building Files List ===");

            var filesList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Add selected files
            if (hasFiles)
            {
                foreach (var file in selectedFiles)
                {
                    if (File.Exists(file) && IsSupportedExtension(file))
                    {
                        filesList.Add(file);
                        Trace.WriteLine($"Added from selection: {file}");
                    }
                }
            }

            // Add files from selected folder
            if (hasFolder && Directory.Exists(selectedFolder))
            {
                SearchOption searchOption = Persistent.ProcessSubfolders
                    ? SearchOption.AllDirectories
                    : SearchOption.TopDirectoryOnly;

                var folderFiles = Directory.GetFiles(selectedFolder, "*.*", searchOption)
                    .Where(f => IsSupportedExtension(f));

                foreach (var file in folderFiles)
                {
                    filesList.Add(file);
                    Trace.WriteLine($"Added from folder: {file}");
                }
            }

            Trace.WriteLine($"Total files collected: {filesList.Count}");

            if (filesList.Count == 0)
            {
                return (false, "No valid image files found in the selected locations.");
            }

            // ============================================
            // PHASE 4: Smart Filters (if enabled)
            // ============================================
            if (Persistent.SmartFilters)
            {
                Trace.WriteLine("=== PHASE 4: Applying Smart Filters ===");

                // Part 1: Filter by suffix
                var toRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var file in filesList)
                {
                    string nameWithoutExt = Path.GetFileNameWithoutExtension(file);
                    string directory = Path.GetDirectoryName(file);

                    // Check for _mer, _mers, _heightmap suffixes
                    if (nameWithoutExt.EndsWith("_mer", StringComparison.OrdinalIgnoreCase) ||
                        nameWithoutExt.EndsWith("_mers", StringComparison.OrdinalIgnoreCase) ||
                        nameWithoutExt.EndsWith("_heightmap", StringComparison.OrdinalIgnoreCase))
                    {
                        toRemove.Add(file);
                        Trace.WriteLine($"Smart filter: Removing {Path.GetFileName(file)} (PBR suffix detected)");
                        continue;
                    }

                    // Special handling for _normal suffix
                    if (nameWithoutExt.EndsWith("_normal", StringComparison.OrdinalIgnoreCase))
                    {
                        // Check if a _normal_normal variant exists
                        string normalNormalBaseName = nameWithoutExt + "_normal";
                        bool foundNormalNormal = false;

                        foreach (var ext in supportedFileExtensions)
                        {
                            string normalNormalPath = Path.Combine(directory, normalNormalBaseName + ext);
                            if (File.Exists(normalNormalPath))
                            {
                                foundNormalNormal = true;
                                // Remove the _normal_normal file instead
                                toRemove.Add(normalNormalPath);
                                Trace.WriteLine($"Smart filter: Found {Path.GetFileName(normalNormalPath)}, keeping {Path.GetFileName(file)} as color texture");
                                break;
                            }
                        }

                        // If no _normal_normal exists, this is a true normal map
                        if (!foundNormalNormal)
                        {
                            toRemove.Add(file);
                            Trace.WriteLine($"Smart filter: Removing {Path.GetFileName(file)} (true normal map)");
                        }
                    }
                }

                // Apply suffix-based removals
                foreach (var file in toRemove)
                {
                    filesList.Remove(file);
                }

                Trace.WriteLine($"Files after suffix filtering: {filesList.Count}");

                // Part 2: Parse existing texture set JSONs and exclude referenced textures
                if (hasFolder && Directory.Exists(selectedFolder))
                {
                    SearchOption searchOption = Persistent.ProcessSubfolders
                        ? SearchOption.AllDirectories
                        : SearchOption.TopDirectoryOnly;

                    var textureSetJsons = Directory.GetFiles(selectedFolder, "*.texture_set.json", searchOption);
                    Trace.WriteLine($"Found {textureSetJsons.Length} existing texture set JSONs");

                    var referencedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var jsonFile in textureSetJsons)
                    {
                        try
                        {
                            string jsonText = File.ReadAllText(jsonFile);
                            var root = JObject.Parse(jsonText);

                            if (root.SelectToken("minecraft:texture_set") is JObject textureSet)
                            {
                                string jsonDir = Path.GetDirectoryName(jsonFile);

                                // Extract all texture references
                                var textureNames = new List<string>();

                                if (textureSet.Value<string>("color") is string color)
                                    textureNames.Add(color);

                                if (textureSet.Value<string>("metalness_emissive_roughness") is string mer)
                                    textureNames.Add(mer);

                                if (textureSet.Value<string>("metalness_emissive_roughness_subsurface") is string mers)
                                    textureNames.Add(mers);

                                if (textureSet.Value<string>("normal") is string normal)
                                    textureNames.Add(normal);

                                if (textureSet.Value<string>("heightmap") is string heightmap)
                                    textureNames.Add(heightmap);

                                // For each texture name, check all possible extensions
                                foreach (var textureName in textureNames)
                                {
                                    foreach (var ext in supportedFileExtensions)
                                    {
                                        string texturePath = Path.Combine(jsonDir, textureName + ext);

                                        // Case-insensitive file existence check
                                        if (FileExistsCaseInsensitive(texturePath))
                                        {
                                            referencedFiles.Add(texturePath);
                                            Trace.WriteLine($"Texture set references: {texturePath}");
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine($"Failed to parse {jsonFile}: {ex.Message}");
                        }
                    }

                    // Remove all referenced files from the list
                    foreach (var referencedFile in referencedFiles)
                    {
                        if (filesList.Remove(referencedFile))
                        {
                            Trace.WriteLine($"Smart filter: Removed {referencedFile} (already in texture set)");
                        }
                    }
                }

                Trace.WriteLine($"Files after smart filtering: {filesList.Count}");
            }
            else
            {
                Trace.WriteLine("=== PHASE 4: Smart Filters Skipped (disabled) ===");
            }

            if (filesList.Count == 0)
            {
                return (false, "No files remain after filtering. All files may already have texture sets.");
            }

            // ============================================
            // PHASE 4.5: Convert to TGA (if enabled, before template generation)
            // ============================================
            if (Persistent.ConvertToTarga)
            {
                Trace.WriteLine("=== PHASE 4.5: Converting to TGA ===");

                var filesArray = filesList.ToArray();
                Helpers.ConvertImagesToTga(filesArray);

                // Update filesList with new .tga paths and delete old files
                var newFilesList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in filesArray)
                {
                    string tgaPath = Path.ChangeExtension(file, ".tga");

                    if (File.Exists(tgaPath))
                    {
                        newFilesList.Add(tgaPath);

                        // Delete original file if it's not already a TGA
                        if (!file.EndsWith(".tga", StringComparison.OrdinalIgnoreCase) && File.Exists(file))
                        {
                            try
                            {
                                File.Delete(file);
                                Trace.WriteLine($"Deleted original: {file}");
                            }
                            catch (Exception ex)
                            {
                                Trace.WriteLine($"Warning: Could not delete {file}: {ex.Message}");
                            }
                        }
                    }
                    else if (File.Exists(file))
                    {
                        // Conversion may have failed for this file, keep original
                        newFilesList.Add(file);
                    }
                }
                filesList = newFilesList;

                Trace.WriteLine($"Files after TGA conversion: {filesList.Count}");
            }
            else
            {
                Trace.WriteLine("=== PHASE 4.5: TGA Conversion Skipped (disabled) ===");
            }

            // ============================================
            // PHASE 5: Generate Texture Set Templates
            // ============================================
            Trace.WriteLine("=== PHASE 5: Generating Texture Set Templates ===");

            int successCount = 0;
            int failCount = 0;

            foreach (var colorTexturePath in filesList)
            {
                try
                {
                    string directory = Path.GetDirectoryName(colorTexturePath);
                    string fileNameWithoutExt = Path.GetFileNameWithoutExtension(colorTexturePath);
                    string extension = Path.GetExtension(colorTexturePath);

                    Trace.WriteLine($"Processing: {colorTexturePath}");

                    // Build texture set JSON
                    var textureSetObj = new JObject();
                    textureSetObj["format_version"] = "1.21.30";

                    var minecraftTextureSet = new JObject();
                    minecraftTextureSet["color"] = fileNameWithoutExt;

                    // Add MER or MERS
                    if (Persistent.enableSSS)
                    {
                        minecraftTextureSet["metalness_emissive_roughness_subsurface"] = fileNameWithoutExt + "_mers";
                    }
                    else
                    {
                        minecraftTextureSet["metalness_emissive_roughness"] = fileNameWithoutExt + "_mer";
                    }

                    // Add secondary PBR map (normal or heightmap)
                    if (Persistent.SecondaryPBRMapType == "normalmap")
                    {
                        minecraftTextureSet["normal"] = fileNameWithoutExt + "_normal";
                    }
                    else if (Persistent.SecondaryPBRMapType == "heightmap")
                    {
                        minecraftTextureSet["heightmap"] = fileNameWithoutExt + "_heightmap";
                    }
                    // If "none", we don't add any secondary map

                    textureSetObj["minecraft:texture_set"] = minecraftTextureSet;

                    // Write JSON file
                    string jsonPath = Path.Combine(directory, fileNameWithoutExt + ".texture_set.json");
                    string jsonContent = JsonConvert.SerializeObject(textureSetObj, Formatting.Indented);
                    File.WriteAllText(jsonPath, jsonContent);
                    Trace.WriteLine($"Created: {jsonPath}");

                    // Copy color texture to create template files
                    // MER or MERS
                    string merSuffix = Persistent.enableSSS ? "_mers" : "_mer";
                    string merPath = Path.Combine(directory, fileNameWithoutExt + merSuffix + extension);
                    File.Copy(colorTexturePath, merPath, overwrite: true);
                    Trace.WriteLine($"Created template: {merPath}");

                    // Secondary PBR map
                    if (Persistent.SecondaryPBRMapType == "normalmap")
                    {
                        string normalPath = Path.Combine(directory, fileNameWithoutExt + "_normal" + extension);
                        File.Copy(colorTexturePath, normalPath, overwrite: true);
                        Trace.WriteLine($"Created template: {normalPath}");
                    }
                    else if (Persistent.SecondaryPBRMapType == "heightmap")
                    {
                        string heightmapPath = Path.Combine(directory, fileNameWithoutExt + "_heightmap" + extension);
                        File.Copy(colorTexturePath, heightmapPath, overwrite: true);
                        Trace.WriteLine($"Created template: {heightmapPath}");
                    }

                    successCount++;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Failed to generate texture set for {colorTexturePath}: {ex.Message}");
                    failCount++;
                }
            }

            Trace.WriteLine($"=== GENERATION COMPLETE ===");
            Trace.WriteLine($"Success: {successCount}, Failed: {failCount}");

            // Return status
            if (failCount == 0)
            {
                return (true, $"Successfully generated {successCount} texture set template(s).");
            }
            else if (successCount == 0)
            {
                return (false, "Failed to generate texture sets.");
            }
            else
            {
                return (true, $"Generated {successCount} texture set(s) with {failCount} failure(s).");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"FATAL ERROR: {ex}");
            return (false, "An unexpected error occurred during generation.");
        }
    }

    #region Helper Methods

    /// <summary>
    /// Checks if a file has a supported image extension.
    /// </summary>
    private static bool IsSupportedExtension(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return supportedFileExtensions.Contains(ext);
    }

    /// <summary>
    /// Case-insensitive file existence check.
    /// </summary>
    private static bool FileExistsCaseInsensitive(string filePath)
    {
        if (File.Exists(filePath))
            return true;

        try
        {
            string directory = Path.GetDirectoryName(filePath);
            string fileName = Path.GetFileName(filePath);

            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return false;

            return Directory.GetFiles(directory, fileName, SearchOption.TopDirectoryOnly).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Backs up selected files to a ZIP archive.
    /// </summary>
    private static async Task<bool> BackupFilesAsync(string[] files)
    {
        try
        {
            Trace.WriteLine($"Backing up {files.Length} file(s)...");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
            var picker = new FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            picker.FileTypeChoices.Add("ZIP Archive", new List<string> { ".zip" });
            picker.SuggestedStartLocation = PickerLocationId.Desktop;
            picker.SuggestedFileName = $"TSMFiles_backup_{GetTimestamp()}";

            var file = await picker.PickSaveFileAsync();
            if (file == null)
            {
                Trace.WriteLine("File backup cancelled by user.");
                return false;
            }

            var tempZipPath = Path.Combine(Path.GetTempPath(), $"temp_{Guid.NewGuid()}.zip");

            try
            {
                using (var zip = ZipFile.Open(tempZipPath, ZipArchiveMode.Create))
                {
                    // Group files by their root directory to preserve structure intelligently
                    var filesByRoot = GroupFilesByCommonRoot(files);

                    foreach (var kvp in filesByRoot)
                    {
                        string commonRoot = kvp.Key;
                        var filesInRoot = kvp.Value;

                        foreach (var filePath in filesInRoot)
                        {
                            if (!File.Exists(filePath))
                                continue;

                            // Preserve directory structure relative to common root
                            string relativePath = string.IsNullOrEmpty(commonRoot)
                                ? Path.GetFileName(filePath)
                                : Path.GetRelativePath(commonRoot, filePath);

                            zip.CreateEntryFromFile(filePath, relativePath, CompressionLevel.Optimal);
                            Trace.WriteLine($"Backed up: {relativePath}");
                        }
                    }
                }

                if (!File.Exists(tempZipPath))
                {
                    Trace.WriteLine("Temporary backup archive was deleted before writing.");
                    return false;
                }

                using var destStream = await file.OpenStreamForWriteAsync();
                using var srcStream = File.OpenRead(tempZipPath);
                await srcStream.CopyToAsync(destStream);

                Trace.WriteLine($"File backup completed: {file.Path}");
                return true;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempZipPath))
                        File.Delete(tempZipPath);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Warning: Couldn't delete temp file: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"File backup failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Backs up an entire folder to a ZIP archive.
    /// </summary>
    private static async Task<bool> BackupFolderAsync(string folderPath)
    {
        try
        {
            Trace.WriteLine($"Backing up folder: {folderPath}");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
            var picker = new FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            picker.FileTypeChoices.Add("ZIP Archive", new List<string> { ".zip" });
            picker.SuggestedStartLocation = PickerLocationId.Desktop;

            string folderName = new DirectoryInfo(folderPath).Name;
            picker.SuggestedFileName = $"{folderName}_backup_{GetTimestamp()}";

            var file = await picker.PickSaveFileAsync();
            if (file == null)
            {
                Trace.WriteLine("Folder backup cancelled by user.");
                return false;
            }

            var tempZipPath = Path.Combine(Path.GetTempPath(), $"temp_{Guid.NewGuid()}.zip");

            try
            {
                using (var zip = ZipFile.Open(tempZipPath, ZipArchiveMode.Create))
                {
                    var allFiles = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);

                    foreach (var filePath in allFiles)
                    {
                        var relativePath = Path.GetRelativePath(folderPath, filePath);
                        zip.CreateEntryFromFile(filePath, relativePath, CompressionLevel.Optimal);
                        Trace.WriteLine($"Backed up: {relativePath}");
                    }
                }

                if (!File.Exists(tempZipPath))
                {
                    Trace.WriteLine("Temporary backup archive was deleted before writing.");
                    return false;
                }

                using var destStream = await file.OpenStreamForWriteAsync();
                using var srcStream = File.OpenRead(tempZipPath);
                await srcStream.CopyToAsync(destStream);

                Trace.WriteLine($"Folder backup completed: {file.Path}");
                return true;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempZipPath))
                        File.Delete(tempZipPath);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Warning: Couldn't delete temp file: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Folder backup failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Groups files by their common root directory to preserve structure intelligently.
    /// </summary>
    private static Dictionary<string, List<string>> GroupFilesByCommonRoot(string[] files)
    {
        var result = new Dictionary<string, List<string>>();

        if (files.Length == 0)
            return result;

        // If all files share a common root, use it
        string commonRoot = FindCommonRoot(files);

        if (!string.IsNullOrEmpty(commonRoot))
        {
            result[commonRoot] = files.ToList();
        }
        else
        {
            // Files are scattered, group by drive or use empty root
            result[string.Empty] = files.ToList();
        }

        return result;
    }

    /// <summary>
    /// Finds the common root directory for a set of file paths.
    /// </summary>
    private static string FindCommonRoot(string[] paths)
    {
        if (paths.Length == 0)
            return string.Empty;

        if (paths.Length == 1)
            return Path.GetDirectoryName(paths[0]) ?? string.Empty;

        var directories = paths.Select(p => Path.GetDirectoryName(p) ?? string.Empty).ToArray();
        var firstDir = directories[0];

        if (string.IsNullOrEmpty(firstDir))
            return string.Empty;

        var commonParts = firstDir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToList();

        foreach (var dir in directories.Skip(1))
        {
            if (string.IsNullOrEmpty(dir))
                return string.Empty;

            var parts = dir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            for (int i = 0; i < commonParts.Count; i++)
            {
                if (i >= parts.Length || !commonParts[i].Equals(parts[i], StringComparison.OrdinalIgnoreCase))
                {
                    commonParts = commonParts.Take(i).ToList();
                    break;
                }
            }

            if (commonParts.Count == 0)
                return string.Empty;
        }

        return string.Join(Path.DirectorySeparatorChar.ToString(), commonParts);
    }

    /// <summary>
    /// Generates a timestamp string for backup file naming.
    /// </summary>
    private static string GetTimestamp()
    {
        return DateTime.Now.ToString("yyyyMMdd_HHmmss");
    }

    #endregion
}
