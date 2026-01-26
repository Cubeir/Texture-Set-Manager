using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Texture_Set_Manager.Modules;


public static class Generate
{
    public static async Task<string> GenerateTextureSetsAsync()
    {
        Trace.WriteLine("Starting texture set generation process...");

        // Phase 1: Check if files or folders are selected
        if ((EnvironmentVariables.selectedFiles == null || EnvironmentVariables.selectedFiles.Length == 0) &&
            string.IsNullOrEmpty(EnvironmentVariables.selectedFolder))
        {
            return "No files or folders were selected for texture set generation.";
        }

        // Phase 2: Backup files if enabled
        if (EnvironmentVariables.Persistent.CreateBackup)
        {
            Trace.WriteLine("Creating backups...");

            try
            {
                if (EnvironmentVariables.selectedFiles != null && EnvironmentVariables.selectedFiles.Length > 0)
                {
                    await CreateBackupAsync(EnvironmentVariables.selectedFiles, "TSMFiles");
                }

                if (!string.IsNullOrEmpty(EnvironmentVariables.selectedFolder))
                {
                    await CreateBackupAsync(EnvironmentVariables.selectedFolder, Path.GetFileName(EnvironmentVariables.selectedFolder));
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to create backups: {ex.Message}");
                return $"Error creating backups: {ex.Message}";
            }
        }

        // Phase 3: Build files list
        var filesList = new List<string>();

        if (EnvironmentVariables.selectedFiles != null && EnvironmentVariables.selectedFiles.Length > 0)
        {
            filesList.AddRange(EnvironmentVariables.selectedFiles);
        }

        if (!string.IsNullOrEmpty(EnvironmentVariables.selectedFolder))
        {
            var searchOption = EnvironmentVariables.Persistent.ProcessSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var folderFiles = Directory.GetFiles(EnvironmentVariables.selectedFolder, "*", searchOption)
                .Where(file => EnvironmentVariables.supportedFileExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()));

            filesList.AddRange(folderFiles);
        }

        // Phase 4: Smart filtering
        if (EnvironmentVariables.Persistent.SmartFilters)
        {
            Trace.WriteLine("Applying smart filters...");

            // Remove non-existent files
            filesList = filesList.Where(File.Exists).ToList();

            // Filter out special suffixes (SMART FILTER PART 1)
            var filteredFiles = new List<string>();
            foreach (var file in filesList)
            {
                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(file);
                var hasSpecialSuffix = false;

                if (fileNameWithoutExt.EndsWith("_mer", StringComparison.OrdinalIgnoreCase) ||
                    fileNameWithoutExt.EndsWith("_mers", StringComparison.OrdinalIgnoreCase) ||
                    fileNameWithoutExt.EndsWith("_heightmap", StringComparison.OrdinalIgnoreCase) ||
                    fileNameWithoutExt.EndsWith("_normal", StringComparison.OrdinalIgnoreCase))
                {
                    // Special handling for _normal suffix
                    if (fileNameWithoutExt.EndsWith("_normal", StringComparison.OrdinalIgnoreCase))
                    {
                        var baseName = fileNameWithoutExt.Substring(0, fileNameWithoutExt.Length - 6); // Remove "_normal"
                        var normalNormalFileName = $"{baseName}_normal_normal";

                        // Check if the _normal_normal file exists in same directory
                        var normalNormalPath = Path.Combine(Path.GetDirectoryName(file), normalNormalFileName + Path.GetExtension(file));
                        if (File.Exists(normalNormalPath))
                        {
                            // Found a _normal_normal, skip this _normal file
                            continue;
                        }
                    }
                    else
                    {
                        // Regular suffixes - skip the file
                        hasSpecialSuffix = true;
                    }
                }

                if (!hasSpecialSuffix)
                {
                    filteredFiles.Add(file);
                }
            }

            filesList = filteredFiles;

            // Remove files referenced by texture set JSONs (SMART FILTER PART 2)
            try
            {
                var textureSetFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var searchOption = EnvironmentVariables.Persistent.ProcessSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                var jsonFiles = Directory.GetFiles(EnvironmentVariables.selectedFolder, "*.texture_set.json", searchOption);

                foreach (var jsonFile in jsonFiles)
                {
                    try
                    {
                        var text = File.ReadAllText(jsonFile);
                        var root = JObject.Parse(text);
                        if (root.SelectToken("minecraft:texture_set") is not JObject set)
                            continue;

                        // Get all texture names from JSON
                        var textureNames = new List<string>();

                        var colorName = set.Value<string>("color");
                        if (!string.IsNullOrEmpty(colorName))
                            textureNames.Add(colorName);

                        var merName = set.Value<string>("metalness_emissive_roughness") ?? set.Value<string>("metalness_emissive_roughness_subsurface");
                        if (!string.IsNullOrEmpty(merName))
                            textureNames.Add(merName);

                        var normalName = set.Value<string>("normal");
                        if (!string.IsNullOrEmpty(normalName))
                            textureNames.Add(normalName);

                        var heightmapName = set.Value<string>("heightmap");
                        if (!string.IsNullOrEmpty(heightmapName))
                            textureNames.Add(heightmapName);

                        // For each texture name, find the actual file path and add base name to deduction list
                        foreach (var textureName in textureNames)
                        {
                            var folder = Path.GetDirectoryName(jsonFile);
                            foreach (var ext in EnvironmentVariables.supportedFileExtensions)
                            {
                                var targetPath = Path.Combine(folder, textureName + ext);

                                if (File.Exists(targetPath))
                                {
                                    // Add the base file name without extension to deduce files with different extensions
                                    var baseFileName = Path.GetFileNameWithoutExtension(targetPath);
                                    textureSetFileNames.Add(baseFileName);
                                    break; // Found a match with priority extension
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"Failed to parse JSON file {jsonFile}: {ex.Message}");
                    }
                }

                // Remove files that have the same base name as files referenced by texture sets
                filesList = filesList.Where(file =>
                {
                    var baseFileName = Path.GetFileNameWithoutExtension(file);
                    return !textureSetFileNames.Contains(baseFileName);
                }).ToList();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error during texture set filtering: {ex.Message}");
            }
        }

        // Phase 5: Generate texture sets
        var successCount = 0;
        var errorCount = 0;

        foreach (var file in filesList)
        {
            try
            {
                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(file);
                var folder = Path.GetDirectoryName(file);
                var jsonPath = Path.Combine(folder, $"{fileNameWithoutExt}.texture_set.json");

                // Create JSON content
                var jsonObject = new JObject();
                jsonObject["format_version"] = "1.21.30";

                var textureSetObject = new JObject();
                textureSetObject["color"] = fileNameWithoutExt;

                // Add metalness_emissive_roughness or metalness_emissive_roughness_subsurface
                var merName = $"{fileNameWithoutExt}_mer";
                if (EnvironmentVariables.Persistent.enableSSS)
                {
                    textureSetObject["metalness_emissive_roughness_subsurface"] = $"{fileNameWithoutExt}_mers";
                }
                else
                {
                    textureSetObject["metalness_emissive_roughness"] = merName;
                }

                // Add heightmap or normal based on SecondaryPBRMapType
                if (!string.IsNullOrEmpty(EnvironmentVariables.Persistent.SecondaryPBRMapType) &&
                    !EnvironmentVariables.Persistent.SecondaryPBRMapType.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    var secondaryName = EnvironmentVariables.Persistent.SecondaryPBRMapType.Equals("heightmap", StringComparison.OrdinalIgnoreCase) ?
                        $"{fileNameWithoutExt}_heightmap" :
                        $"{fileNameWithoutExt}_normal";

                    if (EnvironmentVariables.Persistent.SecondaryPBRMapType.Equals("heightmap", StringComparison.OrdinalIgnoreCase))
                    {
                        textureSetObject["heightmap"] = secondaryName;
                    }
                    else
                    {
                        textureSetObject["normal"] = secondaryName;
                    }
                }

                jsonObject["minecraft:texture_set"] = textureSetObject;

                // Write JSON file
                File.WriteAllText(jsonPath, jsonObject.ToString());

                // Copy files with proper suffixes
                var extension = Path.GetExtension(file);

                // Copy color file (same as original)
                var colorTargetPath = Path.Combine(folder, $"{fileNameWithoutExt}{extension}");
                if (!File.Exists(colorTargetPath) && File.Exists(file))
                {
                    File.Copy(file, colorTargetPath);
                }

                // Copy MER file
                var merTargetPath = Path.Combine(folder, $"{merName}{extension}");
                if (!File.Exists(merTargetPath))
                {
                    // Create empty file or use placeholder logic here if needed
                    try
                    {
                        File.Create(merTargetPath).Dispose();
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"Failed to create MER file {merTargetPath}: {ex.Message}");
                    }
                }

                // Copy heightmap/normal file based on SecondaryPBRMapType
                if (!string.IsNullOrEmpty(EnvironmentVariables.Persistent.SecondaryPBRMapType) &&
                    !EnvironmentVariables.Persistent.SecondaryPBRMapType.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    var secondaryName = EnvironmentVariables.Persistent.SecondaryPBRMapType.Equals("heightmap", StringComparison.OrdinalIgnoreCase) ?
                        $"{fileNameWithoutExt}_heightmap" :
                        $"{fileNameWithoutExt}_normal";

                    var secondaryTargetPath = Path.Combine(folder, $"{secondaryName}{extension}");
                    if (!File.Exists(secondaryTargetPath))
                    {
                        // Create empty file or use placeholder logic here if needed
                        try
                        {
                            File.Create(secondaryTargetPath).Dispose();
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine($"Failed to create {EnvironmentVariables.Persistent.SecondaryPBRMapType} file {secondaryTargetPath}: {ex.Message}");
                        }
                    }
                }

                successCount++;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to process file {file}: {ex.Message}");
                errorCount++;
            }
        }

        // Phase 6: Convert to TGA if enabled
        if (EnvironmentVariables.Persistent.ConvertToTarga && filesList.Count > 0)
        {
            try
            {
                Helpers.ConvertImagesToTga(filesList.ToArray());
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to convert images to TGA: {ex.Message}");
                return $"Generation completed with errors. Successes: {successCount}, Errors: {errorCount}. Conversion to TGA failed.";
            }
        }

        Trace.WriteLine("Texture set generation completed.");

        if (errorCount > 0)
        {
            return $"Generation completed with errors. Successes: {successCount}, Errors: {errorCount}";
        }

        return $"Successfully generated texture sets for {successCount} files.";
    }

    private static async Task CreateBackupAsync(string[] filePaths, string prefix)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
        var picker = new FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeChoices.Add("ZIP Archive", new List<string>() { ".zip" });
        picker.SuggestedFileName = $"{prefix}_backup_{DateTime.Now:yyyyMMdd_HHmmss}";
        picker.SuggestedStartLocation = PickerLocationId.Desktop;

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        var zipPath = Path.Combine(Path.GetTempPath(), $"temp_backup_{Guid.NewGuid()}.zip");

        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var filePath in filePaths)
                {
                    if (File.Exists(filePath))
                    {
                        var relativePath = Path.GetFileName(filePath);
                        zip.CreateEntryFromFile(filePath, relativePath, CompressionLevel.Optimal);
                    }
                }
            }

            using var destStream = await file.OpenStreamForWriteAsync();
            using var srcStream = File.OpenRead(zipPath);
            await srcStream.CopyToAsync(destStream);
        }
        finally
        {
            try
            {
                if (File.Exists(zipPath))
                    File.Delete(zipPath);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Warning: Couldn't delete temp file: {ex.Message}");
            }
        }
    }

    private static async Task CreateBackupAsync(string folderPath, string folderName)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
        var picker = new FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeChoices.Add("ZIP Archive", new List<string>() { ".zip" });
        picker.SuggestedFileName = $"{folderName}_backup_{DateTime.Now:yyyyMMdd_HHmmss}";
        picker.SuggestedStartLocation = PickerLocationId.Desktop;

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        var zipPath = Path.Combine(Path.GetTempPath(), $"temp_backup_{Guid.NewGuid()}.zip");

        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var filePath in Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(folderPath, filePath);
                    zip.CreateEntryFromFile(filePath, relativePath, CompressionLevel.Optimal);
                }
            }

            using var destStream = await file.OpenStreamForWriteAsync();
            using var srcStream = File.OpenRead(zipPath);
            await srcStream.CopyToAsync(destStream);
        }
        finally
        {
            try
            {
                if (File.Exists(zipPath))
                    File.Delete(zipPath);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Warning: Couldn't delete temp file: {ex.Message}");
            }
        }
    }
}


/// Blueprint:
/// Create backup of files and parent folder?
/// selected files are all bundled into a zipfile and a diaglogue opens where to save them, defautl to desktop
/// Selected folder is wholly backed up the same way, user has the option to choose where to save
/// Once user is done selecting save paths (use the same code as Vanilla RTX App's export saver, its GOOD)
///
/// Process subfolders?
/// If so, GET ALL FILES in the subdirectories that match our supported extensions in the given folder
///
/// Pool both selected FILES and FILES retrieved from the directory (and potentially its subdirs) into one long list
/// 
/// Smart filters?
/// We begin by checking through the list first: if a file ends with _mer, _mers, _heightmap or _normal (but a _normal_normal doesn't exist, which would be the true normal)
/// we remove them
/// Then we parse ALL texture_set.jsons retrieved from the given directory and its subdirectories, get the file paths referenced there
/// if any match up with any files on our list, they are REMOVED. that way textures that belong to an existing texture set don't have it remade.
/// And thus the program can be used to MEND files of PBR resource packs instead of overwriting all.
/// 
/// 
/// For texture set generation:
/// SSS?
/// Normal or Height?
/// Generate the json and copy files with the right suffixes in the same dir as the color texture
///
/// Convert to TGA?
/// If so, call this upon ALL files in the list! this is the last step, everything that WAS ON THE LIST SO FAR, filtered and pure gets passed down to be made into a TGA
/// The Long List.
