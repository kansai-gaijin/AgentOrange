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
    private const int JpegQuality = 85;

    /// <summary>Crops and resizes a picture to fit. Returns null if the file isn't a usable image.</summary>
    public static byte[]? Prepare(string path, ProjectImageKind kind)
    {
        var (width, height) = kind == ProjectImageKind.Thumbnail ? (600, 900) : (1920, 620);

        try
        {
            var source = new BitmapImage();
            source.BeginInit();
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.UriSource = new Uri(path);
            source.EndInit();
            source.Freeze();

            // Scale until the picture covers the target, then cut off what's left from the middle.
            var scale = Math.Max((double)width / source.PixelWidth, (double)height / source.PixelHeight);
            var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            var x = Math.Max(0, (scaled.PixelWidth - width) / 2);
            var y = Math.Max(0, (scaled.PixelHeight - height) / 2);
            var cropped = new CroppedBitmap(scaled, new System.Windows.Int32Rect(x, y, Math.Min(width, scaled.PixelWidth), Math.Min(height, scaled.PixelHeight)));

            var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(cropped));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch (Exception error) when (error is NotSupportedException or FileFormatException or IOException or ArgumentException or UriFormatException or InvalidOperationException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't use that image: {error.Message}");
            return null;
        }
    }

    public static ImageSource? ToImage(byte[]? bytes)
    {
        if (bytes is null) return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
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
