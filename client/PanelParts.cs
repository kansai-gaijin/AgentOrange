using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace AgentOrange;

/// <summary>
/// The rows T3 Code's thread details panel is made of: its 32 px buttons, an icon beside words, the
/// split row with a chevron, and menus kept inside what shows them. The agents window's panel and the
/// project page's git actions are both drawn from these.
/// </summary>
internal static class PanelParts
{
    // T3's panel colours over the glass: foreground at 80% for rows, white at 8% under the mouse.
    public static readonly SolidColorBrush RowWords = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFA, 0xFF)));
    public static readonly SolidColorBrush RowHover = Freeze(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)));
    public static readonly SolidColorBrush Rule65 = Freeze(new SolidColorBrush(Color.FromArgb(0xA6, 0x40, 0x55, 0x67)));

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    // T3's Cw button in panel mode: 32 high, 8 round, 13 px medium words at 80%, a wash under the mouse.
    public static Button PanelButton(UIElement content, Action click, string name, Thickness? padding = null, CornerRadius? corners = null)
    {
        var button = Ocean.Ghost(content, click, 8, padding ?? new Thickness(10, 0, 10, 0), RowHover);
        if (corners is { } round) button.Template = Ocean.Template(round, RowHover);
        button.Height = 32;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(button, name);
        return button;
    }

    public static TextBlock RowText(string words, Brush? brush = null)
    {
        var text = Ocean.Text(words, brush ?? RowWords, 13, FontWeights.Medium);
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        return text;
    }

    public static FrameworkElement RowIcon(string icon, double size = 16)
    {
        var glyph = AgentIcons.Line(icon, size, Ocean.MutedText);
        glyph.Margin = new Thickness(0, 0, 10, 0);
        glyph.VerticalAlignment = VerticalAlignment.Center;
        return glyph;
    }

    public static DockPanel IconRow(FrameworkElement icon, UIElement words)
    {
        var row = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(words);
        return row;
    }

    public static FrameworkElement WithGap(FrameworkElement icon, double gap)
    {
        icon.Margin = new Thickness(0, 0, gap, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        return icon;
    }

    // T3's split row: the main part, a divider that shows on hover, and a 32 px chevron.
    public static Grid SplitRow(Button primary, Button secondary)
    {
        primary.Template = Ocean.Template(new CornerRadius(8, 0, 0, 8), RowHover);
        secondary.Template = Ocean.Template(new CornerRadius(0, 8, 8, 0), RowHover);
        secondary.Width = 32;
        secondary.Padding = new Thickness(0);
        secondary.HorizontalContentAlignment = HorizontalAlignment.Center;
        var divider = new Border { Width = 1, Height = 16, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
        var group = new Grid { Background = Brushes.Transparent };
        group.ColumnDefinitions.Add(new ColumnDefinition());
        group.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        group.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(divider, 1);
        Grid.SetColumn(secondary, 2);
        group.Children.Add(primary);
        group.Children.Add(divider);
        group.Children.Add(secondary);
        group.MouseEnter += (_, _) => divider.Background = Rule65;
        group.MouseLeave += (_, _) => divider.Background = Brushes.Transparent;
        return group;
    }

    public static FrameworkElement Chevron() => AgentIcons.Line("chevron-down", 16, Ocean.MutedText);

    // A menu over the panel, put inside it so opening it doesn't count as clicking away.
    public static Popup PanelMenu(Panel layers, UIElement by, UIElement content, double width)
    {
        var menu = Ocean.Menu(by, PlacementMode.Bottom, content, width, 4);
        ((Border)menu.Child).Width = width + 2;
        layers.Children.Add(menu);
        menu.Closed += (_, _) => layers.Children.Remove(menu);
        return menu;
    }
}
