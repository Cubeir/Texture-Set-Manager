using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImageMagick;


namespace Texture_Set_Manager.Modules;

public static class Helpers
{
    public static Bitmap ReadImage(string imagePath, bool maxOpacity = false)
    {
        try
        {
            using var sourceImage = new MagickImage(imagePath);
            var width = (int)sourceImage.Width;
            var height = (int)sourceImage.Height;


            var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            using (var sourcePixels = sourceImage.GetPixels())
            {

                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var pixelData = sourcePixels.GetPixel(x, y);

                        byte r, g, b, a;

                        var hasAlpha = sourceImage.HasAlpha || sourceImage.ColorType == ColorType.GrayscaleAlpha || sourceImage.ColorType == ColorType.TrueColorAlpha;

                        if (sourceImage.ColorType == ColorType.Grayscale)
                        {
                            var gray = (byte)(pixelData[0] >> 8);
                            r = g = b = gray;
                            a = 255;
                        }
                        else if (sourceImage.ColorType == ColorType.GrayscaleAlpha)
                        {
                            var gray = (byte)(pixelData[0] >> 8);
                            r = g = b = gray;
                            var originalAlpha = (byte)(pixelData[1] >> 8);
                            a = maxOpacity ? (byte)255 : originalAlpha;
                        }
                        else if (sourceImage.ColorType == ColorType.TrueColor)
                        {
                            r = (byte)(pixelData[0] >> 8);
                            g = (byte)(pixelData[1] >> 8);
                            b = (byte)(pixelData[2] >> 8);
                            a = 255;
                        }
                        else if (sourceImage.ColorType == ColorType.TrueColorAlpha)
                        {
                            r = (byte)(pixelData[0] >> 8);
                            g = (byte)(pixelData[1] >> 8);
                            b = (byte)(pixelData[2] >> 8);
                            var originalAlpha = (byte)(pixelData[3] >> 8);
                            a = maxOpacity ? (byte)255 : originalAlpha;
                        }
                        else if (sourceImage.ColorType == ColorType.Palette)
                        {
                            r = (byte)(pixelData[0] >> 8);
                            g = (byte)(pixelData[1] >> 8);
                            b = (byte)(pixelData[2] >> 8);

                            if (hasAlpha && sourceImage.ChannelCount > 3)
                            {
                                var originalAlpha = (byte)(pixelData[3] >> 8);
                                a = maxOpacity ? (byte)255 : originalAlpha;
                            }
                            else
                            {
                                a = 255;
                            }
                        }
                        else
                        {
                            var channels = (int)sourceImage.ChannelCount;

                            r = channels > 0 ? (byte)(pixelData[0] >> 8) : (byte)0;
                            g = channels > 1 ? (byte)(pixelData[1] >> 8) : r;
                            b = channels > 2 ? (byte)(pixelData[2] >> 8) : r;

                            if (hasAlpha && channels > 3)
                            {
                                var originalAlpha = (byte)(pixelData[3] >> 8);
                                a = maxOpacity ? (byte)255 : originalAlpha;
                            }
                            else
                            {
                                a = 255;
                            }
                        }
                        var pixelColor = Color.FromArgb(a, r, g, b);
                        bitmap.SetPixel(x, y, pixelColor);
                    }
                }
            }

            return bitmap;
        }
        catch (Exception)
        {
            var errorBitmap = new Bitmap(512, 512, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(errorBitmap))
            {
                g.Clear(Color.Transparent);
                var squareSize = 256;
                g.FillRectangle(new SolidBrush(Color.FromArgb(255, 77, 172, 255)), 0, 0, squareSize, squareSize);
                g.FillRectangle(new SolidBrush(Color.FromArgb(255, 0, 35, 66)), squareSize, 0, squareSize, squareSize);
                g.FillRectangle(new SolidBrush(Color.FromArgb(255, 0, 35, 66)), 0, squareSize, squareSize, squareSize);
                g.FillRectangle(new SolidBrush(Color.FromArgb(255, 77, 172, 255)), squareSize, squareSize, squareSize, squareSize);
            }
            return errorBitmap;
        }
    }
    public static void WriteImageAsTGA(Bitmap bitmap, string outputPath)
    {
        try
        {
            var width = bitmap.Width;
            var height = bitmap.Height;

            // Write TGA file format manually for absolute control
            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(fs);
            // TGA
            writer.Write((byte)0);    // ID Length
            writer.Write((byte)0);    // Color Map Type (0 = no color map)
            writer.Write((byte)2);    // Image Type (2 = uncompressed RGB)
            writer.Write((ushort)0);  // Color Map First Entry Index
            writer.Write((ushort)0);  // Color Map Length
            writer.Write((byte)0);    // Color Map Entry Size
            writer.Write((ushort)0);  // X-origin
            writer.Write((ushort)0);  // Y-origin
            writer.Write((ushort)width);  // Width
            writer.Write((ushort)height); // Height
            writer.Write((byte)32);       // Pixel Depth (32-bit RGBA)
            writer.Write((byte)8);        // Image Descriptor (default origin, 8-bit alpha)

            for (var y = height - 1; y >= 0; y--) // TGA is bottom-up by default
            {
                for (var x = 0; x < width; x++)
                {
                    var pixel = bitmap.GetPixel(x, y);

                    writer.Write(pixel.B);
                    writer.Write(pixel.G);
                    writer.Write(pixel.R);
                    writer.Write(pixel.A);
                }
            }
        }
        catch (Exception ex)
        {
            // Log($"Error writing direct TGA to {outputPath}: {ex.Message}");
            throw;
        }
    }

    public static void ConvertImagesToTga(string[] filePaths)
    {
        foreach (string originalPath in filePaths)
        {
            try
            {
                // Safety
                string extension = Path.GetExtension(originalPath);
                if (!EnvironmentVariables.supportedFileExtensions.Contains(extension))
                {
                    Trace.WriteLine($"Skipping file {originalPath}: Unsupported extension");
                    continue;
                }
                if (!File.Exists(originalPath))
                {
                    Trace.WriteLine($"Skipping file {originalPath}: File does not exist");
                    continue;
                }

                // Read the original image
                Bitmap bmp = ReadImage(originalPath, maxOpacity: false);

                // Get original timestamp
                DateTime origTime = File.GetLastWriteTime(originalPath);

                // Create new path with .tga extension
                string newPath = Path.ChangeExtension(originalPath, ".tga");

                // Write as TGA
                WriteImageAsTGA(bmp, newPath);

                // Restore original timestamp
                File.SetLastWriteTime(newPath, origTime);

                bmp.Dispose();

                Trace.WriteLine($"Successfully converted: {originalPath} -> {newPath}");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to convert file {originalPath}: {ex.Message}");
                continue;
            }
        }
    }
}


/// <summary>
/// Additional helper to do a thing only once per runtime, use RanOnceFlag.Set("key") to set a flag with a unique key.
/// </summary>
public static class RuntimeFlags
{
    private static readonly HashSet<string> _flags = new();

    public static bool Has(string key) => _flags.Contains(key); // Below does the same as this one if already set

    public static bool Set(string key)
    {
        if (_flags.Contains(key))
            return false;

        _flags.Add(key);
        return true;
    }

    public static bool Unset(string key) => _flags.Remove(key);
}
