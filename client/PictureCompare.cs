using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgentOrange;

/// <summary>
/// Measures a picture, alone or beside a reference, so an agent matching a look has numbers as well as
/// its eye: how light it is and how much contrast it has, how strong and how warm its colours are, how
/// much fine detail it has, what's burnt out, black or empty, its main colours, and which part of the
/// picture each difference is in. Two frames from a still camera can also be compared for what moved.
/// Colours are measured in Oklab, where equal steps look about equal, so a number means the same in
/// the shadows as in the light. It measures and never judges: what a difference matters is the
/// agent's and the person's to say.
/// </summary>
internal static class PictureCompare
{
    private const int MeasuredWidth = 480;   // Both pictures are measured at this size, so their own sizes don't count as detail.
    private const int TallestShown = 640;
    private const int MostColors = 5;
    private const int MostDifferences = 8;
    private const int MovedLevels = 10;      // Of 255, past what re-encoding or film grain changes.

    private static readonly string[] Rows = ["top", "middle", "bottom"];
    private static readonly string[] Columns = ["left", "centre", "right"];

    /// <summary>A picture's pixels, red, green and blue from 0 to 255, averaged down to a size.</summary>
    internal sealed record Pixels(int Width, int Height, float[] Rgb);

    internal sealed record Part(double Lightness, double Contrast, double Strength, double Warmth, double Detail, double Burnt, double Black, double Empty);

    /// <summary>What a picture's like in a region, as shares of it: its left, top, right and bottom from 0 to 1.</summary>
    internal sealed record Region(double Left, double Top, double Right, double Bottom)
    {
        public static readonly Region Whole = new(0, 0, 1, 1);
    }

    /// <summary>What was measured, in words, and the pictures side by side.</summary>
    internal sealed record Result(string Said, byte[] Sheet);

    /// <summary>Measures a picture, beside a reference when there's one.</summary>
    public static Result Measure(BitmapSource picture, BitmapSource? reference, Region region, bool moved)
    {
        var said = new StringBuilder();
        var wide = Frame(picture);
        var wideReference = reference is null ? null : Frame(reference);
        if (wideReference is not null && Math.Abs(Shape(wide) / Shape(wideReference) - 1) > 0.02)
        {
            said.AppendLine(CultureInfo.InvariantCulture, $"Their shapes differ ({wide.Width} × {wide.Height}, and the reference {wideReference.Width} × {wideReference.Height}), so the middle of the picture in the reference's shape was measured. Take it at the same shape for a fair comparison.").AppendLine();
            wide = Cropped(wide, Shape(wideReference));
        }
        // At the smaller one's size, as a small picture made bigger would read as soft.
        var measured = Shrunk(wide, Math.Min(MeasuredWidth, wideReference?.Width ?? MeasuredWidth));
        var pixels = Cut(measured, region);
        var referencePixels = wideReference is null ? null : Cut(Shrunk(wideReference, measured.Width, pixelsLike: measured), region);
        if (moved && referencePixels is not null) SayMoved(said, pixels, referencePixels);
        else SayLook(said, pixels, referencePixels);
        return new Result(said.ToString().TrimEnd(), Sheet(Cut(wide, region), wideReference is null ? null : Cut(wideReference, region), moved));
    }

    private static double Shape(Pixels pixels) => (double)pixels.Width / pixels.Height;

    // What each number is, then the numbers, then where the biggest differences are.
    private static void SayLook(StringBuilder said, Pixels pixels, Pixels? reference)
    {
        var (lab, labOf) = (Lab(pixels), reference is null ? null : Lab(reference));
        var (whole, wholeOf) = (Look(pixels, lab, 0, 0, 1, 1), reference is null ? null : Look(reference, labOf!, 0, 0, 1, 1));

        (string Name, Func<Part, double> Of, string Format, string About)[] lines =
        [
            ("lightness", part => part.Lightness, "0.00", "0 black to 1 white, as it looks"),
            ("contrast", part => part.Contrast, "0.000", "how far lightness spreads"),
            ("colour strength", part => part.Strength, "0.000", "0 grey; about 0.1 is a clear colour"),
            ("warmth", part => part.Warmth, "+0.000;-0.000;0.000", "above 0 yellow, below 0 blue"),
            ("fine detail", part => part.Detail * 100, "0.00", "small changes from point to point"),
            ("burnt out", part => part.Burnt, "0.0%", "pure white"),
            ("black", part => part.Black, "0.0%", "pure black"),
            ("empty", part => part.Empty, "0.0%", "flat, with no texture or shading at all"),
        ];
        said.AppendLine(wholeOf is null ? "| | picture | |\n|---|---|---|" : "| | reference | picture | difference | |\n|---|---|---|---|---|");
        foreach (var (name, of, format, about) in lines)
        {
            string In(double value) => value.ToString(format, CultureInfo.InvariantCulture);
            said.AppendLine(wholeOf is null
                ? $"| {name} | {In(of(whole))} | {about} |"
                : $"| {name} | {In(of(wholeOf))} | {In(of(whole))} | {(of(whole) - of(wholeOf) >= 0 ? "+" : "-")}{In(Math.Abs(of(whole) - of(wholeOf))).TrimStart('+')} | {about} |");
        }

        said.AppendLine();
        if (wholeOf is not null) said.AppendLine($"Main colours of the reference: {MainColors(reference!, labOf!)}.");
        said.AppendLine($"Main colours{(wholeOf is null ? "" : " of the picture")}: {MainColors(pixels, lab)}.");

        if (reference is null) return;
        var found = new List<(double Size, string Words)>();
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                var part = Look(pixels, lab, column / 3.0, row / 3.0, (column + 1) / 3.0, (row + 1) / 3.0);
                var of = Look(reference, labOf!, column / 3.0, row / 3.0, (column + 1) / 3.0, (row + 1) / 3.0);
                var where = row == 1 && column == 1 ? "the middle" : $"{Rows[row]} {Columns[column]}";
                found.AddRange(Differences(part, of).Select(each => (each.Size, $"- {where}: {each.Words}")));
            }
        }
        said.AppendLine().AppendLine("Where they differ most, the biggest first:");
        var biggest = found.Where(each => each.Size >= 1).OrderByDescending(each => each.Size).Take(MostDifferences).ToList();
        foreach (var (_, words) in biggest) said.AppendLine(words);
        if (biggest.Count == 0) said.AppendLine("- nowhere by much: each ninth of the picture is close to the reference's on every measure. They can still differ in shapes, which this doesn't measure.");
    }

    // A ninth's differences in words, each with its size as a multiple of one that's easy to see.
    private static IEnumerable<(double Size, string Words)> Differences(Part part, Part of)
    {
        string By(double change, string format) => change.ToString(format, CultureInfo.InvariantCulture);
        if (part.Lightness - of.Lightness is var light) yield return (Math.Abs(light) / 0.06, $"{(light > 0 ? "lighter" : "darker")} by {By(Math.Abs(light), "0.00")}");
        if (part.Contrast - of.Contrast is var contrast) yield return (Math.Abs(contrast) / 0.05, $"{(contrast > 0 ? "more" : "less")} contrast by {By(Math.Abs(contrast), "0.000")}");
        if (part.Strength - of.Strength is var strength) yield return (Math.Abs(strength) / 0.03, $"{(strength > 0 ? "stronger colours" : "greyer")} by {By(Math.Abs(strength), "0.000")}");
        if (part.Warmth - of.Warmth is var warmth) yield return (Math.Abs(warmth) / 0.03, $"{(warmth > 0 ? "warmer" : "cooler")} by {By(Math.Abs(warmth), "0.000")}");

        // Detail is compared as a ratio, as busy and calm pictures are on such different scales.
        var ratio = (part.Detail + 0.001) / (of.Detail + 0.001);
        yield return (Math.Abs(Math.Log2(ratio)) / 0.7, ratio > 1 ? $"busier or sharper, {By(ratio, "0.0")} times the fine detail" : $"softer or emptier, {By(ratio, "0%")} of the fine detail");
        if (part.Empty - of.Empty is var empty and > 0) yield return (empty / 0.15, $"{By(empty, "0%")} more of it is flat and empty");
        if (part.Burnt - of.Burnt is var burnt and > 0) yield return (burnt / 0.03, $"{By(burnt, "0.0%")} more is burnt out");
        if (part.Black - of.Black is var black and > 0) yield return (black / 0.05, $"{By(black, "0.0%")} more is pure black");
    }

    private static void SayMoved(StringBuilder said, Pixels pixels, Pixels first)
    {
        var changed = Changed(pixels, first);
        said.AppendLine(CultureInfo.InvariantCulture, $"{changed.Average(each => each ? 1.0 : 0.0):0.0%} of the picture changed. This only says what moved in the world if the camera was still and both frames were taken at the same settings.").AppendLine();
        said.AppendLine("| | left | centre | right |\n|---|---|---|---|");
        for (var row = 0; row < 3; row++)
        {
            said.Append($"| {Rows[row]} |");
            for (var column = 0; column < 3; column++)
            {
                var (left, right) = (column * pixels.Width / 3, (column + 1) * pixels.Width / 3);
                var (top, bottom) = (row * pixels.Height / 3, (row + 1) * pixels.Height / 3);
                var (count, total) = (0, Math.Max(1, (right - left) * (bottom - top)));
                for (var y = top; y < bottom; y++) for (var x = left; x < right; x++) if (changed[y * pixels.Width + x]) count++;
                said.Append(CultureInfo.InvariantCulture, $" {(double)count / total:0.0%} |");
            }
            said.AppendLine();
        }
        said.AppendLine().AppendLine("What should move and shows 0% is frozen. In the picture, what changed is white on the right.");
    }

    private static bool[] Changed(Pixels pixels, Pixels first)
    {
        var changed = new bool[pixels.Width * pixels.Height];
        for (var i = 0; i < changed.Length; i++)
        {
            changed[i] = Math.Abs(pixels.Rgb[i * 3] - first.Rgb[i * 3]) > MovedLevels
                || Math.Abs(pixels.Rgb[i * 3 + 1] - first.Rgb[i * 3 + 1]) > MovedLevels
                || Math.Abs(pixels.Rgb[i * 3 + 2] - first.Rgb[i * 3 + 2]) > MovedLevels;
        }
        return changed;
    }

    // Measuring

    private static Part Look(Pixels pixels, float[] lab, double fromX, double fromY, double toX, double toY)
    {
        var (left, right) = ((int)(fromX * pixels.Width), Math.Max((int)(fromX * pixels.Width) + 1, (int)(toX * pixels.Width)));
        var (top, bottom) = ((int)(fromY * pixels.Height), Math.Max((int)(fromY * pixels.Height) + 1, (int)(toY * pixels.Height)));
        double light = 0, lightSquared = 0, strength = 0, warmth = 0, detail = 0;
        int burnt = 0, black = 0, count = 0, inner = 0;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var i = (y * pixels.Width + x) * 3;
                double l = lab[i], a = lab[i + 1], b = lab[i + 2];
                light += l;
                lightSquared += l * l;
                strength += Math.Sqrt(a * a + b * b);
                warmth += b;
                if (pixels.Rgb[i] > 252 && pixels.Rgb[i + 1] > 252 && pixels.Rgb[i + 2] > 252) burnt++;
                if (pixels.Rgb[i] < 4 && pixels.Rgb[i + 1] < 4 && pixels.Rgb[i + 2] < 4) black++;
                count++;

                // How far a point is from the points around it, which is texture and edges, not broad shading.
                if (x == 0 || y == 0 || x == pixels.Width - 1 || y == pixels.Height - 1) continue;
                var around = (lab[i - 3] + lab[i + 3] + lab[i - pixels.Width * 3] + lab[i + pixels.Width * 3]) / 4;
                detail += Math.Abs(l - around);
                inner++;
            }
        }
        var mean = light / count;
        return new Part(mean, Math.Sqrt(Math.Max(0, lightSquared / count - mean * mean)), strength / count, warmth / count, inner == 0 ? 0 : detail / inner,
            (double)burnt / count, (double)black / count, Empty(pixels, lab, left, top, right, bottom));
    }

    // The share in small blocks with next to no change in lightness or colour across them.
    private static double Empty(Pixels pixels, float[] lab, int left, int top, int right, int bottom)
    {
        const int Block = 12;
        int empty = 0, blocks = 0;
        for (var y = top; y + Block <= bottom; y += Block)
        {
            for (var x = left; x + Block <= right; x += Block)
            {
                blocks++;
                var flat = true;
                for (var channel = 0; channel < 3 && flat; channel++)
                {
                    float low = float.MaxValue, high = float.MinValue;
                    for (var by = y; by < y + Block; by++)
                    {
                        for (var bx = x; bx < x + Block; bx++)
                        {
                            var value = lab[(by * pixels.Width + bx) * 3 + channel];
                            low = Math.Min(low, value);
                            high = Math.Max(high, value);
                        }
                    }
                    flat = high - low < 0.012f;
                }
                if (flat) empty++;
            }
        }
        return blocks == 0 ? 0 : (double)empty / blocks;
    }

    // The main colours with their shares, as a person would list them: started from colours far apart,
    // so the same picture always gives the same ones, then each moved to the middle of its own.
    private static string MainColors(Pixels pixels, float[] lab)
    {
        var step = Math.Max(1, pixels.Width * pixels.Height / 6000);
        var points = Enumerable.Range(0, pixels.Width * pixels.Height / step).Select(n => n * step * 3).ToArray();
        double Apart(int point, (double L, double A, double B) color) =>
            Math.Pow(lab[point] - color.L, 2) + Math.Pow(lab[point + 1] - color.A, 2) + Math.Pow(lab[point + 2] - color.B, 2);

        var centres = new List<(double L, double A, double B)> { (points.Average(p => lab[p]), points.Average(p => lab[p + 1]), points.Average(p => lab[p + 2])) };
        while (centres.Count < MostColors)
        {
            // Far, but not the very furthest, which is often a speck such as a label.
            var far = points.OrderBy(point => centres.Min(centre => Apart(point, centre))).ElementAt(points.Length * 9 / 10);
            centres.Add((lab[far], lab[far + 1], lab[far + 2]));
        }
        var counts = new int[MostColors];
        for (var round = 0; round < 12; round++)
        {
            var sums = new (double L, double A, double B)[MostColors];
            counts = new int[MostColors];
            foreach (var point in points)
            {
                var nearest = 0;
                for (var n = 1; n < MostColors; n++) if (Apart(point, centres[n]) < Apart(point, centres[nearest])) nearest = n;
                counts[nearest]++;
                sums[nearest] = (sums[nearest].L + lab[point], sums[nearest].A + lab[point + 1], sums[nearest].B + lab[point + 2]);
            }
            for (var n = 0; n < MostColors; n++) if (counts[n] > 0) centres[n] = (sums[n].L / counts[n], sums[n].A / counts[n], sums[n].B / counts[n]);
        }
        return string.Join(", ", Enumerable.Range(0, MostColors).Where(n => counts[n] * 100 >= points.Length).OrderByDescending(n => counts[n])
            .Select(n => string.Create(CultureInfo.InvariantCulture, $"{Hex(centres[n])} {(double)counts[n] / points.Length:0%}")));
    }

    // Colour

    // Oklab (Björn Ottosson's, public domain): lightness, green to red and blue to yellow.
    private static float[] Lab(Pixels pixels)
    {
        var lab = new float[pixels.Rgb.Length];
        for (var i = 0; i < lab.Length; i += 3)
        {
            double r = Linear(pixels.Rgb[i] / 255), g = Linear(pixels.Rgb[i + 1] / 255), b = Linear(pixels.Rgb[i + 2] / 255);
            var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
            var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
            var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
            lab[i] = (float)(0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s);
            lab[i + 1] = (float)(1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s);
            lab[i + 2] = (float)(0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
        }
        return lab;
    }

    private static string Hex((double L, double A, double B) color)
    {
        var l = Math.Pow(color.L + 0.3963377774 * color.A + 0.2158037573 * color.B, 3);
        var m = Math.Pow(color.L - 0.1055613458 * color.A - 0.0638541728 * color.B, 3);
        var s = Math.Pow(color.L - 0.0894841775 * color.A - 1.2914855480 * color.B, 3);
        static int Level(double linear) => (int)Math.Round(255 * Math.Clamp(linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055, 0, 1));
        return $"#{Level(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s):x2}{Level(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s):x2}{Level(-0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s):x2}";
    }

    private static double Linear(double level) => level <= 0.04045 ? level / 12.92 : Math.Pow((level + 0.055) / 1.055, 2.4);

    // Pixels

    private static Pixels Frame(BitmapSource source)
    {
        var plain = source.Format == PixelFormats.Bgr24 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        var stride = (plain.PixelWidth * 3 + 3) & ~3;
        var bytes = new byte[stride * plain.PixelHeight];
        plain.CopyPixels(bytes, stride, 0);
        var rgb = new float[plain.PixelWidth * plain.PixelHeight * 3];
        for (var y = 0; y < plain.PixelHeight; y++)
        {
            for (var x = 0; x < plain.PixelWidth; x++)
            {
                var (from, to) = (y * stride + x * 3, (y * plain.PixelWidth + x) * 3);
                (rgb[to], rgb[to + 1], rgb[to + 2]) = (bytes[from + 2], bytes[from + 1], bytes[from]);
            }
        }
        return new Pixels(plain.PixelWidth, plain.PixelHeight, rgb);
    }

    // Every pixel counts towards the one it ends in, so a big picture isn't sharper or noisier for its size.
    private static Pixels Shrunk(Pixels pixels, int width, Pixels? pixelsLike = null)
    {
        var (toWidth, toHeight) = pixelsLike is null
            ? (Math.Min(width, pixels.Width), Math.Max(1, (int)Math.Round(Math.Min(width, pixels.Width) / Shape(pixels))))
            : (pixelsLike.Width, pixelsLike.Height);
        if (toWidth == pixels.Width && toHeight == pixels.Height) return pixels;
        var rgb = new float[toWidth * toHeight * 3];
        for (var y = 0; y < toHeight; y++)
        {
            var (top, bottom) = (y * pixels.Height / toHeight, Math.Max(y * pixels.Height / toHeight + 1, (y + 1) * pixels.Height / toHeight));
            for (var x = 0; x < toWidth; x++)
            {
                var (left, right) = (x * pixels.Width / toWidth, Math.Max(x * pixels.Width / toWidth + 1, (x + 1) * pixels.Width / toWidth));
                float r = 0, g = 0, b = 0;
                for (var fromY = top; fromY < bottom; fromY++)
                {
                    for (var fromX = left; fromX < right; fromX++)
                    {
                        var i = (fromY * pixels.Width + fromX) * 3;
                        (r, g, b) = (r + pixels.Rgb[i], g + pixels.Rgb[i + 1], b + pixels.Rgb[i + 2]);
                    }
                }
                var (count, to) = ((bottom - top) * (right - left), (y * toWidth + x) * 3);
                (rgb[to], rgb[to + 1], rgb[to + 2]) = (r / count, g / count, b / count);
            }
        }
        return new Pixels(toWidth, toHeight, rgb);
    }

    // The middle of it, in another shape.
    private static Pixels Cropped(Pixels pixels, double shape)
    {
        var (width, height) = Shape(pixels) > shape ? ((int)Math.Round(pixels.Height * shape), pixels.Height) : (pixels.Width, (int)Math.Round(pixels.Width / shape));
        var (left, top) = ((pixels.Width - width) / 2.0 / pixels.Width, (pixels.Height - height) / 2.0 / pixels.Height);
        return Cut(pixels, new Region(left, top, left + (double)width / pixels.Width, top + (double)height / pixels.Height));
    }

    private static Pixels Cut(Pixels pixels, Region region)
    {
        if (region == Region.Whole) return pixels;
        var (left, top) = ((int)Math.Round(region.Left * pixels.Width), (int)Math.Round(region.Top * pixels.Height));
        var (width, height) = (Math.Max(1, Math.Min(pixels.Width, (int)Math.Round(region.Right * pixels.Width)) - left), Math.Max(1, Math.Min(pixels.Height, (int)Math.Round(region.Bottom * pixels.Height)) - top));
        var rgb = new float[width * height * 3];
        for (var y = 0; y < height; y++) Array.Copy(pixels.Rgb, ((top + y) * pixels.Width + left) * 3, rgb, y * width * 3, width * 3);
        return new Pixels(width, height, rgb);
    }

    // The reference on the left and the picture on the right, at one height; for what moved, what changed is third.
    private static byte[] Sheet(Pixels picture, Pixels? reference, bool moved)
    {
        const int Gap = 8;
        var height = Math.Min(TallestShown, Math.Max(picture.Height, reference?.Height ?? 0));
        Pixels At(Pixels pixels) => Shrunk(pixels, 0, new Pixels(Math.Max(1, (int)Math.Round(height * Shape(pixels))), height, []));
        var shown = new List<Pixels>();
        if (reference is not null) shown.Add(At(reference));
        shown.Add(At(picture));
        if (moved && reference is not null && shown[0].Width == shown[1].Width)
        {
            var changed = Changed(shown[1], shown[0]);
            shown.Add(new Pixels(shown[1].Width, height, [.. changed.SelectMany(each => Enumerable.Repeat(each ? 255f : 0f, 3))]));
        }

        var width = shown.Sum(each => each.Width) + Gap * (shown.Count - 1);
        var stride = (width * 3 + 3) & ~3;
        var bytes = new byte[stride * height];
        Array.Fill(bytes, (byte)24);
        var left = 0;
        foreach (var each in shown)
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < each.Width; x++)
                {
                    var (from, to) = ((y * each.Width + x) * 3, y * stride + (left + x) * 3);
                    (bytes[to], bytes[to + 1], bytes[to + 2]) = ((byte)Math.Round(each.Rgb[from + 2]), (byte)Math.Round(each.Rgb[from + 1]), (byte)Math.Round(each.Rgb[from]));
                }
            }
            left += each.Width + Gap;
        }
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, bytes, stride)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
