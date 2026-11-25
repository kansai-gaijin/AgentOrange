using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;

namespace AgentOrange;

/// <summary>
/// compare_pictures for agents: what a screenshot or render measures beside its reference, such as the
/// card's concept art, in any engine or tool, and what moved between two frames. An agent matching a
/// look had only its eye, which drifts towards thinking its own work is close.
/// </summary>
public partial class MainWindow
{
    private List<McpTool> PictureTools() =>
    [
        new("compare_pictures", "Measures a picture, such as a screenshot or a render, beside a reference, and shows them side by side: lightness, contrast, colour strength, warmth, fine detail, what's burnt out, black or empty, the main colours, and where in the picture they differ most. "
            + "Without a reference it measures the picture alone. With moved, the reference is an earlier frame from the same still camera, and it says what changed between them and where. It measures how pictures look, not what's in them, and it doesn't judge: look at them too.",
            new JsonObject
            {
                ["picture"] = Text("The picture: its path, in the project's folder or whole, or picture: and an id, as documents and read_card give it."),
                ["reference"] = Text("What to set it beside, given the same way, such as the concept art or the last round's screenshot. Left out, the picture is measured alone."),
                ["region"] = Text("Only part of both, as left, top, right and bottom in hundredths of the picture, such as 0,50,100,100 for the lower half. The whole picture if not given."),
                ["moved"] = Flag("Say what changed from reference to picture instead, for two frames some seconds apart from a camera that didn't move."),
            },
            ["picture"], (_, _) => "", ComparePicturesForAgentAsync),
    ];

    private async Task<object> ComparePicturesForAgentAsync(Project project, JsonObject given)
    {
        var reference = Given(given, "reference") is { Length: > 0 } asked ? asked : null;
        var moved = given["moved"]?.GetValue<bool>() ?? false;
        if (moved && reference is null) throw new McpException(-32602, "compare_pictures with moved needs reference: the earlier frame, from the same still camera.");
        var region = AskedRegion(Given(given, "region"));
        var picture = PictureGiven(project, Required(given, "picture"), "picture");
        var other = reference is null ? null : PictureGiven(project, reference, "reference");

        // Away from the window, as a 4K picture takes a moment.
        var result = await Task.Run(() => PictureCompare.Measure(picture, other, region, moved));
        var about = reference is null ? "" : moved ? "In the picture, the earlier frame is on the left, the later one beside it and what changed on the right.\n\n" : "In the picture, the reference is on the left.\n\n";
        return new JsonObject
        {
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = about + result.Said },
                new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(result.Sheet), ["mimeType"] = "image/jpeg" }),
        };
    }

    // A picture file, or one the project keeps, read whole.
    private BitmapSource PictureGiven(Project project, string asked, string argument)
    {
        if (asked.StartsWith("picture:", StringComparison.Ordinal))
        {
            var kept = ReadPicture(project, asked)["content"]?[0]?["data"]?.GetValue<string>() ?? "";
            return PictureFrom(Convert.FromBase64String(kept)) ?? throw new McpException(-32602, $"{argument}: {asked} couldn't be read as a picture.");
        }
        var file = ReviewFile(project, asked, argument);
        return AgentAttachments.LoadImage(file) ?? throw new McpException(-32602, $"{argument}: {asked} couldn't be read as a picture. Give a PNG or JPEG.");
    }

    private static BitmapSource? PictureFrom(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static PictureCompare.Region AskedRegion(string? asked)
    {
        if (string.IsNullOrEmpty(asked)) return PictureCompare.Region.Whole;
        var edges = asked.Split(',', StringSplitOptions.TrimEntries).Select(each => double.TryParse(each, NumberStyles.Float, CultureInfo.InvariantCulture, out var edge) && edge is >= 0 and <= 100 ? edge / 100 : (double?)null).ToList();
        if (edges.Count != 4 || edges.Any(edge => edge is null) || edges[2] - edges[0] < 0.05 || edges[3] - edges[1] < 0.05)
        {
            throw new McpException(-32602, $"region: it's left, top, right and bottom in hundredths of the picture, at least 5 apart, such as 0,50,100,100 for the lower half; it was {asked}.");
        }
        return new PictureCompare.Region(edges[0]!.Value, edges[1]!.Value, edges[2]!.Value, edges[3]!.Value);
    }
}
