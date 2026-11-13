using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AgentOrange;

/// <summary>
/// Every asset's board has one Deliverable node: the file that makes the asset delivered. It's
/// there from the first time the board is opened, and can be moved but never deleted. Only a
/// person fills it, by double-clicking it and choosing the file.
///
/// It's any file already in the project's folder, such as the .uasset Unreal makes when something's
/// imported. Nothing is copied: the deliverable only says which file it is, and the file goes to
/// other PCs with the project's repo.
/// </summary>
internal sealed partial class NodeCanvas
{
    private const string DeliverableKind = "deliverable";
    private const double DeliverableWidth = 240;
    private const double DeliverablePreviewHeight = 140;

    // A picture deliverable's preview, opened once, and again only when the file changes.
    private readonly Dictionary<string, (DateTime Changed, ImageSource? Picture)> _deliverablePictures = [];

    // The asset whose board this is, when it's an asset's.
    private string? AssetCardId => IsAssetBoard ? $"{_projectId}:{_board.Split('/')[2]}" : null;

    // Named after its asset, so two PCs opening a new asset's board at once make the same one.
    private void EnsureDeliverable()
    {
        if (AssetCardId is not { } cardId || _nodes.Values.Any(node => node.Data.Kind == DeliverableKind)) return;
        var node = AddNodeView($"{cardId}-deliverable", new NodeData(_board, 40, 40, "", DeliverableKind));
        _hint.Visibility = Visibility.Collapsed;
        SaveNode(node);
    }

    private static string SizeOf(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    private void AddDeliverableParts(NodeView view)
    {
        var given = AssetCardId is { } cardId ? _database.GetAssetDeliverable(cardId) : null;
        var folder = _database.GetProjectFolder(_projectId);
        var file = given is not null && folder is not null ? Path.Combine(folder, given.Path) : null;
        var here = file is not null && File.Exists(file);

        var rows = new StackPanel();
        if (here && ImageEndings.Contains(Path.GetExtension(file!).ToLowerInvariant()) && DeliverablePicture(file!) is { } picture)
        {
            rows.Children.Add(new Border
            {
                Height = DeliverablePreviewHeight,
                CornerRadius = new CornerRadius(6.5, 6.5, 0, 0),
                Background = new ImageBrush(picture) { Stretch = Stretch.UniformToFill },
            });
        }

        var said = given is null ? "Double-click to choose the file the game uses"
            : here ? $"{SizeOf(given.Size)} · {given.At.LocalDateTime:d MMM yyyy}"
            : "Not on this PC yet. It comes with the project's repo.";
        rows.Children.Add(new StackPanel
        {
            Margin = new Thickness(12, 9, 12, 11),
            Children =
            {
                new TextBlock { Text = given is null ? "Deliverable" : "Delivered", FontSize = 11.5, FontWeight = FontWeights.Medium, Foreground = (Brush)FindResource(given is null ? "TextMuted" : "Accent") },
                new TextBlock { Text = given is null ? "Not delivered yet" : Path.GetFileName(given.Path), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource(given is null ? "TextMuted" : "TextStrong"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), ToolTip = given?.Path },
                new TextBlock { Text = said, FontSize = 12, Foreground = (Brush)FindResource("TextMuted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) },
            },
        });

        view.Inside.Child = rows;
        view.Box.Width = DeliverableWidth;
        view.Box.Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x27, 0x32));
    }

    private ImageSource? DeliverablePicture(string file)
    {
        var changed = File.GetLastWriteTimeUtc(file);
        if (_deliverablePictures.TryGetValue(file, out var opened) && opened.Changed == changed) return opened.Picture;

        ImageSource? picture = null;
        try
        {
            picture = ProjectImages.ToImage(File.ReadAllBytes(file), (int)(DeliverableWidth * 2));
        }
        catch (IOException)
        {
            // It's being written, or was taken away, so it waits for the next time the board is drawn.
        }
        _deliverablePictures[file] = (changed, picture);
        return picture;
    }

    // Asks which of the project's files it is: any kind, anywhere in its folder. Nothing is copied.
    private void FillDeliverable()
    {
        if (AssetCardId is not { } cardId) return;
        if (_database.GetProjectFolder(_projectId) is not { } folder || !Directory.Exists(folder))
        {
            Dialogs.Notify("This project needs its folder on this PC first, as the deliverable is one of the files in it.");
            return;
        }
        if (Dialogs.PickFile("Choose the deliverable", "All files|*.*", folder) is not { } picked) return;

        var inFolder = Path.GetRelativePath(folder, picked);
        if (inFolder.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(inFolder))
        {
            Dialogs.Notify($"Choose a file in the project's folder.\n\n{Path.GetFileName(picked)} isn't in it, and nothing is copied into it.");
            return;
        }

        try
        {
            string sha256;
            using (var stream = File.OpenRead(picked)) sha256 = Convert.ToHexString(SHA256.HashData(stream));
            _database.SetAssetDeliverable(cardId, new AssetDeliverable(inFolder.Replace('\\', '/'), new FileInfo(picked).Length, sha256, DateTimeOffset.UtcNow));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Dialogs.Notify($"{Path.GetFileName(picked)} couldn't be read.\n\n{error.Message}");
            return;
        }
        _changed();
        Reload();
    }
}
