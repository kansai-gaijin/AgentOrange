using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace AgentOrange;

/// <summary>
/// What T3 Code shows over a whole window: a dialog, as a glass card on the window dimmed, and toasts
/// in its bottom right corner. A window puts both layers over everything else it shows.
/// </summary>
internal sealed class Overlays
{
    public Grid Dialogs { get; } = new() { Visibility = Visibility.Collapsed };
    public StackPanel Toasts { get; } = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16), Width = 356 };

    /// <summary>Called as a dialog opens, for anything of the window's that shouldn't stay open under it.</summary>
    public Action? Opening { get; set; }

    public void ShowDialog(UIElement card, Color? shade = null)
    {
        Dialogs.Children.Clear();
        var dim = new Border { Background = new SolidColorBrush(shade ?? Color.FromArgb(0x80, 0x00, 0x00, 0x00)) };
        // A click that began on it, so the release of the click that opened the dialog doesn't close it.
        var pressedHere = false;
        dim.MouseLeftButtonDown += (_, _) => pressedHere = true;
        dim.MouseLeftButtonUp += (_, _) =>
        {
            if (pressedHere) CloseDialog();
            pressedHere = false;
        };
        Dialogs.Children.Add(dim);
        Dialogs.Children.Add(card);
        Dialogs.Visibility = Visibility.Visible;
        Opening?.Invoke();
    }

    public void CloseDialog()
    {
        Dialogs.Children.Clear();
        Dialogs.Visibility = Visibility.Collapsed;
    }

    public static Border DialogCard(double maxWidth, string title, string description, UIElement? panel, Action close, params UIElement[] buttons) =>
        DialogCard(maxWidth, title, description, panel, close, firstOnLeft: false, buttons);

    // T3's footer wraps its buttons when they don't fit, and can keep the first at the left, as its
    // default-branch dialog does with Abort (sm:mr-auto).
    public static Border DialogCard(double maxWidth, string title, string description, UIElement? panel, Action close, bool firstOnLeft, params UIElement[] buttons)
    {
        var closer = Ocean.IconButton("x", close, "Close", 28, 16, Ocean.MutedText);
        closer.HorizontalAlignment = HorizontalAlignment.Right;
        closer.VerticalAlignment = VerticalAlignment.Top;
        closer.Margin = new Thickness(0, 8, 8, 0);
        var heading = Ocean.Wrapped(title, Ocean.Foreground, 20, 26);
        heading.FontWeight = FontWeights.SemiBold;
        var header = new StackPanel { Margin = new Thickness(24, 24, 24, 12), Children = { heading } };
        var said = Ocean.Wrapped(description, Ocean.MutedText, 14, 20);
        said.Margin = new Thickness(0, 8, 0, 0);
        header.Children.Add(said);
        var footer = new DockPanel { Margin = new Thickness(24, 16, 16, 16), LastChildFill = true };
        var rest = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        for (var at = 0; at < buttons.Length; at++)
        {
            if (buttons[at] is FrameworkElement each) each.Margin = new Thickness(0, 4, 8, 4);
            if (at == 0 && firstOnLeft)
            {
                DockPanel.SetDock(buttons[at], Dock.Left);
                footer.Children.Add(buttons[at]);
            }
            else
            {
                rest.Children.Add(buttons[at]);
            }
        }
        footer.Children.Add(rest);
        var stack = new StackPanel { Children = { header } };
        if (panel is not null) stack.Children.Add(panel);
        stack.Children.Add(footer);
        return new Border
        {
            MaxWidth = maxWidth,
            Margin = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Ocean.Glass,
            BorderBrush = Ocean.GlassBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Child = new Grid { Children = { stack, closer } },
        };
    }

    // T3's buttons: outline, and primary.
    public static Button DialogButton(string words, Action click, bool primary, bool enabled = true)
    {
        var text = Ocean.Text(words, primary ? Ocean.OnPrimary : Ocean.Foreground, 14, FontWeights.Medium);
        var button = Ocean.Ghost(text, click, 8, new Thickness(12, 0, 12, 0), primary ? Ocean.PrimaryHover : Ocean.RowHover, primary ? Ocean.Primary : null);
        button.Height = 32;
        if (!primary)
        {
            button.BorderBrush = Ocean.Border;
            button.BorderThickness = new Thickness(1);
        }
        button.IsEnabled = enabled;
        button.Opacity = enabled ? 1 : 0.64;
        AutomationProperties.SetName(button, words);
        return button;
    }

    public void Toast(string title, string? description, bool error)
    {
        var words = new StackPanel { Children = { Ocean.Text(title, Ocean.Foreground, 14, FontWeights.Medium) } };
        if (description is { Length: > 0 }) words.Children.Add(Ocean.Wrapped(description, Ocean.MutedText, 13, 18));
        var icon = AgentIcons.Line(error ? "circle-alert" : "circle-check", 16, error ? Ocean.Error : Ocean.Success);
        icon.Margin = new Thickness(0, 2, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        Border? toast = null;
        var close = Ocean.IconButton("x", () => Toasts.Children.Remove(toast), "Close", 24, 14, Ocean.MutedText);
        close.VerticalAlignment = VerticalAlignment.Top;
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        row.Children.Add(icon);
        row.Children.Add(close);
        row.Children.Add(words);
        toast = new Border
        {
            Background = Ocean.Popover,
            BorderBrush = Ocean.GlassBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 8, 12),
            Margin = new Thickness(0, 8, 0, 0),
            Child = row,
        };
        AutomationProperties.SetName(toast, description is null ? title : $"{title}: {description}");
        Toasts.Children.Add(toast);
        if (!error) _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ => Toasts.Dispatcher.Invoke(() => Toasts.Children.Remove(toast)), TaskScheduler.Default);
    }
}
