using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AgentOrange;

/// <summary>
/// An asset's board shows its deliverable in the top right corner, in a block with a dotted
/// outline that stays put as the board moves: the file that makes the asset delivered. Only a
/// person fills it, by clicking it and choosing the file.
///
/// It's any file already in the project's folder, such as the .uasset Unreal makes when something's
/// imported. Nothing is copied: the deliverable only says which file it is, and the file goes to
/// other PCs with the project's repo.
/// </summary>
internal sealed partial class NodeCanvas
{
    private const string DeliverableKind = "deliverable"; // What it was saved as when it was a node.
    private const double DeliverableWidth = 240;
    private const double DeliverablePreviewHeight = 140;

    private readonly Button _deliverable;

    // A picture deliverable's preview, opened once, and again only when the file changes.
    private readonly Dictionary<string, (DateTime Changed, ImageSource? Picture)> _deliverablePictures = [];

    // The asset whose board this is, when it's an asset's.
    private string? AssetCardId => IsAssetBoard ? $"{_projectId}:{_board.Split('/')[2]}" : null;

    private static string SizeOf(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    private Button NewDeliverableBlock()
    {
        var block = new Button
        {
            Style = (Style)FindResource("ProjectCard"),
            Width = DeliverableWidth,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 16, 16, 0),
            Visibility = Visibility.Collapsed,
        };
        block.Click += (_, _) => FillDeliverable();
        block.MouseEnter += (_, _) => ShowDeliverableActive(block);
        block.MouseLeave += (_, _) => ShowDeliverableActive(block);
        block.IsKeyboardFocusWithinChanged += (_, _) => ShowDeliverableActive(block);
        return block;
    }

    private void ShowDeliverableActive(Button block)
    {
        if (block.Content is Grid { Children: [Rectangle edge, ..] }) edge.Stroke = (Brush)FindResource(block.IsMouseOver || block.IsKeyboardFocusWithin ? "Accent" : "Line");
    }

    // Drawn again with the board, as the file can arrive or change.
    private void ShowDeliverable()
    {
        if (AssetCardId is not { } cardId)
        {
            _deliverable.Visibility = Visibility.Collapsed;
            return;
        }

        var given = _database.GetAssetDeliverable(cardId);
        var folder = _database.GetProjectFolder(_projectId);
        var file = given is not null && folder is not null ? System.IO.Path.Combine(folder, given.Path) : null;
        var here = file is not null && File.Exists(file);

        var rows = new StackPanel();
        if (here && ImageEndings.Contains(System.IO.Path.GetExtension(file!).ToLowerInvariant()) && DeliverablePicture(file!) is { } picture)
        {
            rows.Children.Add(new Border
            {
                Height = DeliverablePreviewHeight,
                Margin = new Thickness(2, 2, 2, 0),
                CornerRadius = new CornerRadius(8, 8, 0, 0),
                Background = new ImageBrush(picture) { Stretch = Stretch.UniformToFill },
            });
        }

        var said = given is null ? "Click to choose the file in the project's folder"
            : here ? $"{SizeOf(given.Size)} · {given.At.LocalDateTime:d MMM yyyy}"
            : "Not on this PC yet. It comes with the project's repo.";
        rows.Children.Add(new StackPanel
        {
            Margin = new Thickness(14, 11, 14, 13),
            Children =
            {
                new TextBlock { Text = given is null ? "Deliverable" : "Delivered", FontSize = 11.5, FontWeight = FontWeights.Medium, Foreground = (Brush)FindResource(given is null ? "TextMuted" : "Accent") },
                new TextBlock { Text = given is null ? "Not delivered yet" : System.IO.Path.GetFileName(given.Path), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource(given is null ? "TextMuted" : "TextStrong"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), ToolTip = given?.Path },
                new TextBlock { Text = said, FontSize = 12, Foreground = (Brush)FindResource("TextMuted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) },
            },
        });

        var edge = new Rectangle { RadiusX = 10, RadiusY = 10, Stroke = (Brush)FindResource("Line"), StrokeThickness = 2, StrokeDashArray = [4, 3], Fill = new SolidColorBrush(Color.FromRgb(0x13, 0x1B, 0x24)) };
        _deliverable.Content = new Grid { Children = { edge, rows } };
        AutomationProperties.SetName(_deliverable, given is null ? "Deliverable, not delivered yet" : $"Delivered: {System.IO.Path.GetFileName(given.Path)}");
        _deliverable.Visibility = Visibility.Visible;
        ShowDeliverableActive(_deliverable);
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

        var inFolder = System.IO.Path.GetRelativePath(folder, picked);
        if (inFolder.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(inFolder))
        {
            Dialogs.Notify($"Choose a file in the project's folder.\n\n{System.IO.Path.GetFileName(picked)} isn't in it, and nothing is copied into it.");
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
            Dialogs.Notify($"{System.IO.Path.GetFileName(picked)} couldn't be read.\n\n{error.Message}");
            return;
        }
        _changed();
        ShowDeliverable();
    }
}
