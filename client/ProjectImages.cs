using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgentOrange;

public enum ProjectImageKind { Thumbnail, Header }

/// <summary>
/// Project images are resized and stored as small JPEGs, so they can sync with
/// the project. The sizes follow a Steam library: a portrait cover and a wide hero.
/// </summary>
public static class ProjectImages
{
    // Stays well under what one sync request can carry, once encoded as text.
    private const int MaxImageBytes = 900_000;
    private static readonly int[] JpegQualities = [85, 70, 55, 40];

    /// <summary>Crops and resizes a picture to fit. Returns null if the file isn't a usable image.</summary>
    public static byte[]? Prepare(string path, ProjectImageKind kind)
    {
        var (width, height) = kind == ProjectImageKind.Thumbnail ? (600, 900) : (1920, 620);

        try
        {
            // Reading the size first means a huge picture is decoded small, not in full.
            var fit = 1.0;
            var decodeWidth = 0;
            using (var file = File.OpenRead(path))
            {
                var frame = BitmapDecoder.Create(file, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                fit = Math.Max((double)width / frame.PixelWidth, (double)height / frame.PixelHeight);
                if (fit < 1) decodeWidth = (int)Math.Ceiling(frame.PixelWidth * fit);
            }

            var source = new BitmapImage();
            source.BeginInit();
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.UriSource = new Uri(path);
            source.DecodePixelWidth = decodeWidth;
            source.EndInit();
            source.Freeze();

            // Scale until the picture covers the target, then cut off what's left from the middle.
            var scale = Math.Max((double)width / source.PixelWidth, (double)height / source.PixelHeight);
            var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            var x = Math.Max(0, (scaled.PixelWidth - width) / 2);
            var y = Math.Max(0, (scaled.PixelHeight - height) / 2);
            var cropped = new CroppedBitmap(scaled, new System.Windows.Int32Rect(x, y, Math.Min(width, scaled.PixelWidth), Math.Min(height, scaled.PixelHeight)));

            foreach (var quality in JpegQualities)
            {
                var encoder = new JpegBitmapEncoder { QualityLevel = quality };
                encoder.Frames.Add(BitmapFrame.Create(cropped));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                if (stream.Length <= MaxImageBytes) return stream.ToArray();
            }

            AppLog.Write("That image was too detailed to fit, even at low quality.");
            return null;
        }
        catch (Exception error) when (error is NotSupportedException or FileFormatException or IOException or ArgumentException or UriFormatException or InvalidOperationException or UnauthorizedAccessException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        {
            AppLog.Write($"Couldn't use that image: {error.Message}");
            return null;
        }
    }

    /// <summary>Decoding at the size it's shown keeps a library of covers light on memory.</summary>
    public static ImageSource? ToImage(byte[]? bytes, int decodeWidth = 0)
    {
        if (bytes is null) return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.DecodePixelWidth = decodeWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is NotSupportedException or FileFormatException or IOException or ArgumentException)
        {
            return null;
        }
    }
}
