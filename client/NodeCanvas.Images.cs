using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgentOrange;

/// <summary>
/// The Image node: a picture, dragged to any size and always in its own shape. The picture is a
/// record of its own, apart from the node's, so moving the node doesn't send the picture again.
/// </summary>
internal sealed partial class NodeCanvas
{
    private const string ImageKind = "image";
    private const string ImageFiles = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff";
    private static readonly string[] ImageEndings = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"];
    private const double ImageWidth = 320;
    private const double SmallestImage = 40;
    private const double WidestImage = 4000;
    private const double ShapeWhenUnknown = 0.75; // Height over width, for a node whose picture hasn't arrived.

    // Every picture on the board as it's stored, read again with the board, and each one opened once.
    private Dictionary<string, string> _storedPictures = [];
    private readonly Dictionary<string, (string Stored, ImageSource? Picture)> _pictures = [];

    private void ReadPictures()
    {
        _storedPictures = _database.ListProjectRecords(LocalDatabase.NodeImagesCollection, _projectId).ToDictionary(one => one.Id, one => one.Data);
        foreach (var gone in _pictures.Keys.Where(id => !_storedPictures.ContainsKey(id)).ToList()) _pictures.Remove(gone);
    }

    private ImageSource? PictureOf(string id)
    {
        if (!_storedPictures.TryGetValue(id, out var stored)) return null;
        if (_pictures.TryGetValue(id, out var opened) && opened.Stored == stored) return opened.Picture;

        ImageSource? picture = null;
        try
        {
            picture = ProjectImages.ToImage(Convert.FromBase64String(stored));
        }
        catch (FormatException)
        {
            // It arrived from another PC, and isn't a picture.
        }
        _pictures[id] = (stored, picture);
        return picture;
    }

    private static double ShapeOf(NodeData data) => Math.Clamp(data.Aspect ?? ShapeWhenUnknown, 0.02, 50);

    private void AddImageParts(NodeView view, Grid parts)
    {
        var picture = new Image { Stretch = Stretch.Fill, Source = PictureOf(view.Id), IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);

        // Until the picture arrives from the PC it was added on.
        var missing = new Border
        {
            Background = (Brush)FindResource("ControlBackground"),
            CornerRadius = new CornerRadius(6),
            IsHitTestVisible = false,
            Visibility = picture.Source is null ? Visibility.Visible : Visibility.Collapsed,
            Child = new TextBlock { Text = "Image", Foreground = (Brush)FindResource("TextMuted"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        view.Picture = picture;
        view.Inside.Child = new Grid { Children = { missing, picture } };
        SizeImage(view, view.Data.Width ?? ImageWidth);
        AddGrips(parts);
    }

    private static void SizeImage(NodeView node, double width)
    {
        width = Math.Clamp(Math.Round(width), SmallestImage, WidestImage);
        node.Inside.Width = width;
        node.Inside.Height = Math.Round(width * ShapeOf(node.Data));
    }

    // Any edge makes it bigger or smaller, as its shape never changes.
    private void ResizeImage(NodeView node, Point inWorld)
    {
        var width = _heldEdge == ResizePart + "-h"
            ? (_heldFrom.Y + inWorld.Y - _pressedAtInWorld.Y) / ShapeOf(node.Data)
            : _heldFrom.X + inWorld.X - _pressedAtInWorld.X;
        width = Math.Clamp(Math.Round(width), SmallestImage, WidestImage);
        node.Data = node.Data with { Width = width };
        SizeImage(node, width);
    }

    // Adding and changing

    private void AddImageFromFile(Point where)
    {
        if (Dialogs.PickFile("Choose a picture", ImageFiles) is { } path) AddImageFromFile(where, path);
    }

    private bool AddImageFromFile(Point where, string path)
    {
        if (ProjectImages.PrepareForBoard(path) is { } image && AddImageNode(where, image)) return true;
        Dialogs.Notify($"{Path.GetFileName(path)} couldn't be used as a picture.");
        return false;
    }

    private bool AddImageNode(Point where, byte[] image)
    {
        if (ProjectImages.ToImage(image) is not BitmapSource picture || picture.PixelWidth == 0) return false;

        var shape = (double)picture.PixelHeight / picture.PixelWidth;
        var width = Math.Clamp(Math.Min(ImageWidth, picture.PixelWidth), SmallestImage, WidestImage);
        var id = NewId();
        var stored = Convert.ToBase64String(image);
        _database.SaveRecord(LocalDatabase.NodeImagesCollection, id, stored);
        _storedPictures[id] = stored;
        _pictures[id] = (stored, picture);

        var node = AddNodeView(id, new NodeData(_board, Math.Round(where.X - width / 2), Math.Round(where.Y - width * shape / 2), "", ImageKind, width, Aspect: shape));
        _hint.Visibility = Visibility.Collapsed;
        SaveNode(node);
        Select(node.Id);
        return true;
    }

    // What a double-click on one does: puts another picture in its place, at the same width.
    private void ReplaceImage(NodeView node)
    {
        if (Dialogs.PickFile("Choose a picture", ImageFiles) is not { } path) return;
        if (ProjectImages.PrepareForBoard(path) is not { } image || ProjectImages.ToImage(image) is not BitmapSource picture || picture.PixelWidth == 0)
        {
            Dialogs.Notify($"{Path.GetFileName(path)} couldn't be used as a picture.");
            return;
        }

        var stored = Convert.ToBase64String(image);
        _database.SaveRecord(LocalDatabase.NodeImagesCollection, node.Id, stored);
        _storedPictures[node.Id] = stored;
        _pictures[node.Id] = (stored, picture);

        node.Data = node.Data with { Aspect = (double)picture.PixelHeight / picture.PixelWidth };
        node.Picture!.Source = picture;
        if (node.Inside.Child is Grid { Children: [Border missing, ..] }) missing.Visibility = Visibility.Collapsed;
        SizeImage(node, node.Data.Width ?? ImageWidth);
        SaveNode(node);
    }

    private void ForgetPicture(string id)
    {
        _database.SaveRecord(LocalDatabase.NodeImagesCollection, id, "", deleted: true);
        _storedPictures.Remove(id);
        _pictures.Remove(id);
    }

    // Where the mouse is, or the middle if it's somewhere else.
    private Point MouseInWorld()
    {
        var mouse = Mouse.GetPosition(this);
        return ToWorld(new Rect(0, 0, ActualWidth, ActualHeight).Contains(mouse) ? mouse : new Point(ActualWidth / 2, ActualHeight / 2));
    }

    private static bool IsImageFile(string path) => ImageEndings.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    // Ctrl+V with a picture copied, or picture files copied in Explorer. True when there was one.
    private bool PasteImage()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().Where(IsImageFile).ToList();
                AddImagesFromFiles(MouseInWorld(), files);
                return files.Count > 0;
            }
            if (!Clipboard.ContainsImage() || Clipboard.GetImage() is not { } copied) return false;

            // What's see-through in a copied picture can't be trusted: a screenshot often says all of it is.
            if (ProjectImages.PrepareForBoard(copied, keepSeeThrough: false) is not { } image || !AddImageNode(MouseInWorld(), image)) Dialogs.Notify("What was copied couldn't be used as a picture.");
            return true;
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        {
            AppLog.Write($"Couldn't read a copied picture: {error.Message}");
            return false;
        }
    }

    // Each a little down and right of the last, so they don't hide each other.
    private void AddImagesFromFiles(Point where, IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            if (AddImageFromFile(where, file)) where.Offset(28, 28);
        }
    }

    private void FilesDraggedOver(object sender, DragEventArgs drag)
    {
        drag.Effects = drag.Data.GetData(DataFormats.FileDrop) is string[] files && files.Any(IsImageFile) ? DragDropEffects.Copy : DragDropEffects.None;
        drag.Handled = true;
    }

    private void FilesDropped(object sender, DragEventArgs drop)
    {
        if (drop.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        drop.Handled = true;
        StopEditing(keep: true);
        AddImagesFromFiles(ToWorld(drop.GetPosition(this)), files.Where(IsImageFile));
        Focus();
    }
}
