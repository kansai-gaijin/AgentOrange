using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace AgentOrange;

/// <summary>
/// T3 Code's image lightbox, copied from its code. The window goes 75% black and the picture
/// shows as large as fits: within 92% of the width less 96, and the smaller of 86% of the height
/// or the height less 160, never past its own size. Its caption sits under it, with a close
/// button in the corner and Previous and Next arrows when the reply has several pictures.
/// A click zooms to 2× where it was clicked, and another goes back to fit. Ctrl+scroll zooms up
/// to 8×, dragging or scrolling pans, and Enter, Space, plus, minus, 0, arrows and Escape work as
/// in T3. Clicking anywhere but the picture closes it.
/// </summary>
public sealed partial class AgentsWindow
{
    private const double MostZoom = 8;

    private void ShowLightbox(IReadOnlyList<(string Path, string Alt)> pictures, int index)
    {
        if (pictures.Count == 0) return;
        index = Math.Clamp(index, 0, pictures.Count - 1);
        var zoom = 1.0;
        BitmapImage? picture = null;
        var image = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var region = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = image,
            Focusable = true,
            Cursor = Cursors.Hand,
        };
        AutomationProperties.SetHelpText(region, "Click to zoom in or return to fit. Ctrl+scroll to zoom, scroll or drag to pan. Use Enter to toggle zoom, plus or minus to zoom, and 0 to fit.");
        var frame = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = Ocean.Canvas,
            BorderBrush = Ocean.Border70,
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = region,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 40, ShadowDepth = 12, Direction = 270, Opacity = 0.6 },
        };
        var caption = new TextBlock { Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { frame, caption } };

        // The box the picture is fitted in: 92% of the window, or its height less 160 when that's smaller.
        double Across() => Math.Max(1, ActualWidth * 0.92 - (ActualWidth >= 640 ? 96 : 0));
        double High() => Math.Max(1, Math.Min(ActualHeight * 0.86, ActualHeight - 160));
        double Fit() => picture is null ? 1 : Math.Min(1, Math.Min(Across() / picture.PixelWidth, High() / picture.PixelHeight));

        void Size(Point? anchor = null)
        {
            if (picture is null) return;
            var before = image.Width;
            image.Width = picture.PixelWidth * Fit() * zoom;
            image.Height = picture.PixelHeight * Fit() * zoom;
            region.MaxWidth = Across();
            region.MaxHeight = High();
            region.Cursor = zoom > 1 ? Cursors.SizeAll : Cursors.Hand;

            // The point under the pointer stays where it is.
            if (anchor is { } at && before > 0 && !double.IsNaN(before))
            {
                var ratio = image.Width / before;
                region.UpdateLayout();
                region.ScrollToHorizontalOffset((region.HorizontalOffset + at.X) * ratio - at.X);
                region.ScrollToVerticalOffset((region.VerticalOffset + at.Y) * ratio - at.Y);
            }
        }

        void Load()
        {
            var (path, alt) = pictures[index];
            zoom = 1;
            try
            {
                picture = new BitmapImage();
                picture.BeginInit();
                picture.CacheOption = BitmapCacheOption.OnLoad;
                picture.UriSource = new Uri(path);
                picture.EndInit();
                picture.Freeze();
                image.Source = picture;
                frame.Child = region;
                Size();
            }
            catch (Exception error) when (error is System.IO.IOException or NotSupportedException or UriFormatException)
            {
                picture = null;
                frame.Child = new Border
                {
                    Height = 192,
                    Width = Math.Min(Across(), 512),
                    Background = Brushes.Black,
                    Padding = new Thickness(24),
                    Child = new TextBlock { Text = "Image unavailable. The file may have been moved or deleted.", Foreground = Brushes.White, FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                };
            }
            caption.Text = (alt.Trim().Length > 0 ? alt.Trim() : "image") + (pictures.Count > 1 ? $" ({index + 1}/{pictures.Count})" : "");
            AutomationProperties.SetName(region, $"Preview {caption.Text}");
        }

        void Zoom(double to, Point? anchor)
        {
            zoom = Math.Clamp(to, 1, MostZoom);
            Size(anchor);
        }

        void Step(int by)
        {
            if (pictures.Count < 2) return;
            index = ((index + by) % pictures.Count + pictures.Count) % pictures.Count;
            Load();
        }

        // A click toggles 1× and 2× where it lands; a drag of more than 4 px pans instead.
        Point? pressed = null;
        Point pressedOffset = default;
        var dragged = false;
        region.PreviewMouseLeftButtonDown += (_, press) =>
        {
            if (press.OriginalSource is FrameworkElement { TemplatedParent: System.Windows.Controls.Primitives.ScrollBar } or System.Windows.Controls.Primitives.Thumb or System.Windows.Controls.Primitives.RepeatButton) return;
            pressed = press.GetPosition(region);
            pressedOffset = new Point(region.HorizontalOffset, region.VerticalOffset);
            dragged = false;
            region.CaptureMouse();
            press.Handled = true;
        };
        region.PreviewMouseMove += (_, move) =>
        {
            if (pressed is not { } from || move.LeftButton != MouseButtonState.Pressed) return;
            var now = move.GetPosition(region);
            if (!dragged && (now - from).Length <= 4) return;
            dragged = true;
            if (zoom <= 1) return;
            region.Cursor = Cursors.ScrollAll;
            region.ScrollToHorizontalOffset(pressedOffset.X - (now.X - from.X));
            region.ScrollToVerticalOffset(pressedOffset.Y - (now.Y - from.Y));
        };
        region.PreviewMouseLeftButtonUp += (_, release) =>
        {
            if (pressed is not { } from) return;
            region.ReleaseMouseCapture();
            pressed = null;
            region.Cursor = zoom > 1 ? Cursors.SizeAll : Cursors.Hand;
            if (dragged || release.ClickCount > 1) return;
            Zoom(zoom > 1 ? 1 : 2, release.GetPosition(region));
            release.Handled = true;
        };
        region.PreviewMouseRightButtonUp += (_, press) =>
        {
            press.Handled = true;
            PictureMenu(pictures[index].Path, region);
        };
        region.PreviewMouseWheel += (_, wheel) =>
        {
            if (KeyState.Modifiers != ModifierKeys.Control) return;
            wheel.Handled = true;
            Zoom(zoom * Math.Exp(wheel.Delta / 120.0 * 100 * 0.01), wheel.GetPosition(region));
        };

        // Buttons: close in the box's corner, and Previous and Next at its sides.
        var close = new Button
        {
            Width = 28,
            Height = 28,
            Background = new SolidColorBrush(Color.FromArgb(0xA6, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Content = AgentIcons.Line("x", 16, Brushes.White),
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Template = Ocean.Template(8, new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0))),
        };
        AutomationProperties.SetName(close, "Close image preview");
        close.Click += (_, _) => _overlays.CloseDialog();
        var box = new Grid { Children = { stack, close } };
        if (pictures.Count > 1)
        {
            foreach (var (icon, by, side, name) in new[] { ("chevron-left", -1, HorizontalAlignment.Left, "Previous media"), ("chevron-right", 1, HorizontalAlignment.Right, "Next media") })
            {
                var arrow = Ocean.Ghost(AgentIcons.Line(icon, 20, new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF))), () => Step(by), 8, null, new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)));
                arrow.Width = 32;
                arrow.Height = 32;
                arrow.HorizontalAlignment = side;
                arrow.VerticalAlignment = VerticalAlignment.Center;
                AutomationProperties.SetName(arrow, name);
                box.Children.Add(arrow);
            }
        }

        // Clicking anything but the picture, its caption or the buttons closes it.
        var surface = new Grid { Background = Brushes.Transparent, Margin = new Thickness(ActualWidth * 0.04, 24, ActualWidth * 0.04, 24), Children = { box } };
        var pressedOutside = false;
        surface.MouseLeftButtonDown += (_, press) => pressedOutside = press.OriginalSource == surface || press.OriginalSource == box;
        surface.MouseLeftButtonUp += (_, click) =>
        {
            if (pressedOutside && (click.OriginalSource == surface || click.OriginalSource == box)) _overlays.CloseDialog();
            pressedOutside = false;
        };
        stack.MouseLeftButtonUp += (_, click) => click.Handled = true;

        surface.PreviewKeyDown += (_, key) =>
        {
            var handled = true;
            switch (key.Key)
            {
                case Key.Escape: _overlays.CloseDialog(); break;
                case Key.Enter or Key.Space: Zoom(zoom > 1 ? 1 : 2, null); break;
                case Key.OemPlus or Key.Add: Zoom(zoom * 1.5, null); break;
                case Key.OemMinus or Key.Subtract: Zoom(zoom / 1.5, null); break;
                case Key.D0 or Key.NumPad0: Zoom(1, null); break;
                case Key.Up: region.ScrollToVerticalOffset(region.VerticalOffset - 40); break;
                case Key.Down: region.ScrollToVerticalOffset(region.VerticalOffset + 40); break;
                case Key.Left when region.ExtentWidth > region.ViewportWidth: region.ScrollToHorizontalOffset(region.HorizontalOffset - 40); break;
                case Key.Right when region.ExtentWidth > region.ViewportWidth: region.ScrollToHorizontalOffset(region.HorizontalOffset + 40); break;
                case Key.Left: Step(-1); break;
                case Key.Right: Step(1); break;
                default: handled = false; break;
            }
            key.Handled = handled;
        };
        SizeChanged += Refit;
        void Refit(object sender, SizeChangedEventArgs changed)
        {
            if (!surface.IsLoaded)
            {
                SizeChanged -= Refit;
                return;
            }
            zoom = 1;
            surface.Margin = new Thickness(ActualWidth * 0.04, 24, ActualWidth * 0.04, 24);
            Size();
        }

        Load();
        _overlays.ShowDialog(surface, shade: Color.FromArgb(0xBF, 0, 0, 0));

        // T3: the picture comes in from 98% and clear over 200 ms.
        var scale = new ScaleTransform(0.98, 0.98);
        box.RenderTransformOrigin = new Point(0.5, 0.5);
        box.RenderTransform = scale;
        var grow = new DoubleAnimation(0.98, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        box.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        Dispatcher.InvokeAsync(() => Keyboard.Focus(close), System.Windows.Threading.DispatcherPriority.Input);
    }
}
