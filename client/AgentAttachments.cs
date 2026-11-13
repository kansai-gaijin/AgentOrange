using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace AgentOrange;

/// <summary>A picture or file sent with a message. Pictures are kept with the conversation; files are only named.</summary>
public sealed record AgentAttachment(string Path, bool IsImage)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Pictures for the agent: pasted, picked, dropped or taken from the screen. Each is made small enough
/// for the model to take as it is (2000 pixels on the longest side, as T3 Code sends them) and
/// kept in the conversation's own folder, so the message still shows it after a restart.
/// </summary>
public static class AgentAttachments
{
    private const int LongestSide = 2000; // As T3 Code sends them.
    private const long LargestImage = 3_500_000;

    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff" };

    /// <summary>PDFs go with the message for the model to read, up to this size, as T3 Code sends them. Bigger ones are only named.</summary>
    public const long LargestPdf = 20_000_000;

    public static bool IsPdf(string path) => System.IO.Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public static bool IsImageFile(string path) => ImageTypes.Contains(System.IO.Path.GetExtension(path));

    public static string MediaType(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/png",
    };

    /// <summary>Saves a picture for sending, or null when it can't be.</summary>
    public static AgentAttachment? SaveImage(BitmapSource image, string folder)
    {
        try
        {
            var longest = Math.Max(image.PixelWidth, image.PixelHeight);
            BitmapSource sized = longest > LongestSide
                ? new TransformedBitmap(image, new ScaleTransform((double)LongestSide / longest, (double)LongestSide / longest))
                : image;

            var png = Encode(new PngBitmapEncoder(), sized);
            var (bytes, extension) = png.Length <= LargestImage ? (png, ".png") : (Encode(new JpegBitmapEncoder { QualityLevel = 88 }, sized), ".jpg");
            Directory.CreateDirectory(folder);
            var path = System.IO.Path.Combine(folder, $"{Guid.NewGuid():N}{extension}");
            File.WriteAllBytes(path, bytes);
            return new AgentAttachment(path, IsImage: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            AppLog.Write($"Couldn't keep a picture for the agent: {error.Message}");
            return null;
        }
    }

    /// <summary>A picture file, saved the same way, or the file itself, named in the message, when it isn't one.</summary>
    public static AgentAttachment? FromFile(string path, string folder)
    {
        if (!File.Exists(path)) return null;
        if (!IsImageFile(path)) return new AgentAttachment(path, IsImage: false);
        return LoadImage(path) is { } image ? SaveImage(image, folder) : new AgentAttachment(path, IsImage: false);
    }

    /// <summary>A picture read without holding the file open, smaller to show when a height is given.</summary>
    public static BitmapSource? LoadImage(string path, int? height = null)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (height is { } tall) image.DecodePixelHeight = tall;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException or COMException)
        {
            return null;
        }
    }

    private static byte[] Encode(BitmapEncoder encoder, BitmapSource image)
    {
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // Taking part of the screen

    /// <summary>
    /// Freezes every screen and lets the person drag out the part they want, as the snipping tool does.
    /// Enter or a click without dragging takes the whole screen under the mouse, and Escape takes nothing.
    /// </summary>
    public static Task<BitmapSource?> TakeScreenshotAsync()
    {
        var done = new TaskCompletionSource<BitmapSource?>();
        var overlays = new List<Window>();

        void Finish(BitmapSource? taken)
        {
            if (done.Task.IsCompleted) return;
            foreach (var overlay in overlays) overlay.Close();
            done.TrySetResult(taken);
        }

        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            if (Capture(screen.Bounds) is not { } frozen) continue;
            overlays.Add(Overlay(screen.Bounds, frozen, Finish));
        }
        if (overlays.Count == 0) Finish(null);
        foreach (var overlay in overlays) overlay.Show();
        overlays.FirstOrDefault()?.Activate();
        return done.Task;
    }

    private static BitmapSource? Capture(System.Drawing.Rectangle bounds)
    {
        try
        {
            using var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
            var handle = bitmap.GetHbitmap();
            try
            {
                var image = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return image;
            }
            finally
            {
                DeleteObject(handle);
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or ArgumentException or ExternalException)
        {
            AppLog.Write($"Couldn't take a picture of the screen: {error.Message}");
            return null;
        }
    }

    // One for each screen, set over it in its own pixels so screens of different scales line up.
    private static Window Overlay(System.Drawing.Rectangle bounds, BitmapSource frozen, Action<BitmapSource?> finish)
    {
        var shade = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0));
        var dim = new System.Windows.Shapes.Path { Fill = shade, IsHitTestVisible = false };
        var edge = new Rectangle { Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x3D)), StrokeThickness = 2, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x10, 0x10, 0x11)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 8, 14, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 28, 0, 0),
            IsHitTestVisible = false,
            Child = new TextBlock { Text = "Drag over what the agent should see   ·   Enter for the whole screen   ·   Esc to cancel", Foreground = new SolidColorBrush(Color.FromRgb(0xE3, 0xE3, 0xE5)), FontSize = 13 },
        };
        var canvas = new Canvas { Background = Brushes.Transparent, Children = { dim, edge } };
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Cursor = Cursors.Cross,
            Background = Brushes.Black,
            Left = -32000,
            Top = -32000,
            Width = 10,
            Height = 10,
            Content = new Grid { Children = { new Image { Source = frozen, Stretch = Stretch.Fill }, canvas, hint } },
        };

        // Placed in pixels once it has a handle, as its size in its own units depends on the screen it's on.
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            SetWindowPos(handle, new IntPtr(-1), bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0040);
        };
        void Shade(Rect? hole)
        {
            var all = new RectangleGeometry(new Rect(0, 0, canvas.ActualWidth, canvas.ActualHeight));
            dim.Data = hole is { } cut ? new CombinedGeometry(GeometryCombineMode.Exclude, all, new RectangleGeometry(cut)) : all;
        }
        canvas.SizeChanged += (_, _) => Shade(null);

        Point? from = null;
        Rect Dragged(Point to) => new(from!.Value, to);

        BitmapSource Cut(Rect picked)
        {
            // From where it was dragged in the window to the frozen screen's own pixels.
            var across = frozen.PixelWidth / canvas.ActualWidth;
            var down = frozen.PixelHeight / canvas.ActualHeight;
            var x = Math.Clamp((int)Math.Round(picked.X * across), 0, frozen.PixelWidth - 1);
            var y = Math.Clamp((int)Math.Round(picked.Y * down), 0, frozen.PixelHeight - 1);
            var width = Math.Clamp((int)Math.Round(picked.Width * across), 1, frozen.PixelWidth - x);
            var height = Math.Clamp((int)Math.Round(picked.Height * down), 1, frozen.PixelHeight - y);
            var cut = new CroppedBitmap(frozen, new Int32Rect(x, y, width, height));
            cut.Freeze();
            return cut;
        }

        canvas.MouseLeftButtonDown += (_, pressed) =>
        {
            from = pressed.GetPosition(canvas);
            canvas.CaptureMouse();
            hint.Visibility = Visibility.Collapsed;
        };
        canvas.MouseMove += (_, moved) =>
        {
            if (from is null) return;
            var picked = Dragged(moved.GetPosition(canvas));
            Canvas.SetLeft(edge, picked.X);
            Canvas.SetTop(edge, picked.Y);
            edge.Width = picked.Width;
            edge.Height = picked.Height;
            edge.Visibility = Visibility.Visible;
            Shade(picked);
        };
        canvas.MouseLeftButtonUp += (_, released) =>
        {
            if (from is null) return;
            var picked = Dragged(released.GetPosition(canvas));
            from = null;
            canvas.ReleaseMouseCapture();
            finish(picked.Width < 6 || picked.Height < 6 ? frozen : Cut(picked));
        };
        window.PreviewKeyDown += (_, pressed) =>
        {
            if (pressed.Key == Key.Escape) finish(null);
            else if (pressed.Key == Key.Enter) finish(frozen);
        };
        window.MouseRightButtonUp += (_, _) => finish(null);
        return window;
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}

/// <summary>
/// A key that works from anywhere, even with another program in front, for taking a screenshot for
/// the agent: Ctrl+Alt+S.
/// </summary>
public sealed class ScreenshotHotkey : IDisposable
{
    public const string Keys = "Ctrl+Alt+S";
    private const int Id = 0x4A4F;
    private const uint Control = 0x0002, Alt = 0x0001, NoRepeat = 0x4000, KeyS = 0x53;
    private const int HotkeyMessage = 0x0312;

    private readonly HwndSource _source;
    private readonly Action _pressed;
    public bool Registered { get; }

    public ScreenshotHotkey(Window window, Action pressed)
    {
        _pressed = pressed;
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle());
        _source.AddHook(Hook);
        Registered = RegisterHotKey(_source.Handle, Id, Control | Alt | NoRepeat, KeyS);
        if (!Registered) AppLog.Write($"Couldn't take {Keys} for screenshots, as something else has it.");
    }

    private IntPtr Hook(IntPtr window, int message, IntPtr id, IntPtr extra, ref bool handled)
    {
        if (message != HotkeyMessage || id.ToInt32() != Id) return IntPtr.Zero;
        handled = true;
        _pressed();
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (Registered) UnregisterHotKey(_source.Handle, Id);
        _source.RemoveHook(Hook);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
