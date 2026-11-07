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

    private const string OrientationQuery = "/app1/ifd/{ushort=274}";

    // The dark blue behind project pages, which is where these are shown.
    private const byte BackgroundRed = 0x17;
    private const byte BackgroundGreen = 0x21;
    private const byte BackgroundBlue = 0x2B;

    /// <summary>Crops and resizes a picture to fit. Returns null if the file isn't a usable image.</summary>
    public static byte[]? Prepare(string path, ProjectImageKind kind)
    {
        var (width, height) = kind == ProjectImageKind.Thumbnail ? (600, 900) : (1920, 620);

        try
        {
            // Reading the size first means a huge picture is decoded small, not in full.
            var decodeWidth = 0;
            int orientation;
            using (var file = File.OpenRead(path))
            {
                var frame = BitmapDecoder.Create(file, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                orientation = ReadOrientation(frame);
                var (shownWidth, shownHeight) = orientation >= 5 ? (frame.PixelHeight, frame.PixelWidth) : (frame.PixelWidth, frame.PixelHeight);
                var fit = Math.Max((double)width / shownWidth, (double)height / shownHeight);
                if (fit < 1) decodeWidth = (int)Math.Ceiling(frame.PixelWidth * fit);
            }

            using var picture = File.OpenRead(path);
            var source = new BitmapImage();
            source.BeginInit();
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.StreamSource = picture;
            source.DecodePixelWidth = decodeWidth;
            source.EndInit();
            source.Freeze();

            // Phones store photos sideways, with a note saying which way is up.
            BitmapSource upright = orientation > 1 ? new TransformedBitmap(source, UprightTransform(orientation)) : source;

            // Scale until the picture covers the target, then cut off what's left from the middle.
            var scale = Math.Max((double)width / upright.PixelWidth, (double)height / upright.PixelHeight);
            var scaled = new TransformedBitmap(upright, new ScaleTransform(scale, scale));
            var x = Math.Max(0, (scaled.PixelWidth - width) / 2);
            var y = Math.Max(0, (scaled.PixelHeight - height) / 2);
            var cropped = OverBackground(new CroppedBitmap(scaled, new System.Windows.Int32Rect(x, y, Math.Min(width, scaled.PixelWidth), Math.Min(height, scaled.PixelHeight))));

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
        catch (Exception error) when (IsImageFailure(error))
        {
            AppLog.Write($"Couldn't use that image: {error.Message}");
            return null;
        }
    }

    private static bool IsImageFailure(Exception error) =>
        error is NotSupportedException or FileFormatException or IOException or ArgumentException or UriFormatException or InvalidOperationException or UnauthorizedAccessException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException;

    private static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            return frame.Metadata is BitmapMetadata metadata
                && metadata.ContainsQuery(OrientationQuery)
                && metadata.GetQuery(OrientationQuery) is ushort orientation and >= 1 and <= 8
                ? orientation
                : 1;
        }
        catch (Exception error) when (error is NotSupportedException or ArgumentException or InvalidOperationException or IOException or System.Runtime.InteropServices.ExternalException)
        {
            return 1; // Plenty of formats don't have this at all.
        }
    }

    // The eight values EXIF defines, as a flip and then a clockwise turn.
    private static Transform UprightTransform(int orientation)
    {
        var (flipX, flipY, turn) = orientation switch
        {
            2 => (true, false, 0),
            3 => (false, false, 180),
            4 => (false, true, 0),
            5 => (true, false, 270),
            6 => (false, false, 90),
            7 => (true, false, 90),
            8 => (false, false, 270),
            _ => (false, false, 0),
        };

        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(flipX ? -1 : 1, flipY ? -1 : 1));
        transform.Children.Add(new RotateTransform(turn));
        return transform;
    }

    // JPEG can't be see-through, and see-through parts would otherwise turn black.
    private static BitmapSource OverBackground(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha == 255) continue;
            pixels[i] = Blend(pixels[i], BackgroundBlue, alpha);
            pixels[i + 1] = Blend(pixels[i + 1], BackgroundGreen, alpha);
            pixels[i + 2] = Blend(pixels[i + 2], BackgroundRed, alpha);
            pixels[i + 3] = 255;
        }

        return BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
    }

    private static byte Blend(byte color, byte background, byte alpha) =>
        (byte)((color * alpha + background * (255 - alpha)) / 255);

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
        catch (Exception error) when (IsImageFailure(error))
        {
            // These arrive from other PCs, and one bad picture can't be allowed to stop the app opening.
            AppLog.Write($"Skipped an unreadable image: {error.Message}");
            return null;
        }
    }
}
