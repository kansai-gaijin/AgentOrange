using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AgentOrange;

/// <summary>
/// T3 Code's sidebar, copied from its code. Its edge drags to resize it, between 208 px and the
/// window less 640, and a double-click puts it back to 256. Ctrl+B, or the button at the top left,
/// tucks it away. In the list, the Settled section follows the conversations, collapsed until
/// opened, and is pushed to the bottom when the list is short. It shows ten settled
/// conversations, and "Show N more" adds 25 at a time. All of it is remembered on this PC.
/// </summary>
public sealed partial class AgentsWindow
{
    private const double SidebarMin = 208;
    private const int SettledFirst = 10;
    private const int SettledMore = 25;

    private sealed class SidebarPrefs
    {
        public double Width { get; set; } = SidebarWidth;
        public bool Open { get; set; } = true;
        public bool SettledExpanded { get; set; }
    }

    private SidebarPrefs _sidebar = new();
    private ColumnDefinition? _sideColumn;
    private int _settledShown = SettledFirst;
    private readonly Grid _listArea = new();
    private Border? _sidePanel;

    private string SidebarFile => Path.Combine(_main.AgentFolder, "sidebar.json");

    private void LoadSidebar()
    {
        try
        {
            if (File.Exists(SidebarFile)) _sidebar = JsonSerializer.Deserialize<SidebarPrefs>(File.ReadAllText(SidebarFile)) ?? new();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't read the sidebar's settings: {error.Message}");
        }
        _showSettled = _sidebar.SettledExpanded;
    }

    private void SaveSidebar()
    {
        _sidebar.SettledExpanded = _showSettled;
        try
        {
            File.WriteAllText(SidebarFile, JsonSerializer.Serialize(_sidebar));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't save the sidebar's settings: {error.Message}");
        }
    }

    private double MostSidebar() => Math.Max(SidebarMin, Math.Floor(ActualWidth) - 640);

    private void ApplySidebar()
    {
        if (_sideColumn is null || _sidePanel is null) return;
        _sideColumn.Width = new GridLength(_sidebar.Open ? Math.Clamp(_sidebar.Width, SidebarMin, MostSidebar()) : 0);
        _sidePanel.Visibility = _sidebar.Open ? Visibility.Visible : Visibility.Collapsed;
        if (_headerBar is not null) _headerBar.Padding = new Thickness(_sidebar.Open ? 20 : 52, 0, 150, 0);
    }

    private void ToggleSidebar()
    {
        _sidebar.Open = !_sidebar.Open;
        ApplySidebar();
        SaveSidebar();
    }

    // The list: the conversations, then the Settled section, which sits at the bottom when they don't fill it.
    private ScrollViewer BuildList()
    {
        _listArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _listArea.RowDefinitions.Add(new RowDefinition());
        _listArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_settledList, 2);
        _listArea.Children.Add(_list);
        _listArea.Children.Add(_settledList);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _listArea, Padding = new Thickness(8, 4, 8, 4) };
        scroll.SizeChanged += (_, _) => _listArea.MinHeight = Math.Max(0, scroll.ViewportHeight - 8);
        scroll.ScrollChanged += (_, _) => _listArea.MinHeight = Math.Max(0, scroll.ViewportHeight - 8);
        return scroll;
    }

    // T3's Settled header: the word, the count and a chevron, with no wash under the mouse.
    private void ShowSettledShelf(List<AgentConversation> settled)
    {
        _settledList.Children.Clear();
        var header = new DockPanel();
        var chevron = AgentIcons.Line("chevron-down", 12, Ocean.SidebarMuted);
        chevron.RenderTransformOrigin = new Point(0.5, 0.5);
        chevron.RenderTransform = new RotateTransform(_showSettled ? 0 : -90);
        chevron.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(chevron, Dock.Right);
        var count = Ocean.Text($"{settled.Count}", Ocean.SidebarMuted, 12);
        DockPanel.SetDock(count, Dock.Right);
        header.Children.Add(chevron);
        header.Children.Add(count);
        header.Children.Add(Ocean.Text("Settled", Ocean.SidebarMuted, 12, FontWeights.Medium));
        var toggle = Ocean.Ghost(header, () =>
        {
            _showSettled = !_showSettled;
            _settledShown = SettledFirst;
            SaveSidebar();
            ShowConversations();
        }, 6, new Thickness(8, 0, 8, 0), Brushes.Transparent);
        toggle.Height = 32;
        toggle.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        toggle.Margin = new Thickness(2, 0, 2, 0);
        AutomationProperties.SetName(toggle, $"Settled, {settled.Count}, {(_showSettled ? "showing" : "click to show")}");
        _settledList.Children.Add(toggle);

        // Collapsed, only the open one shows under it, if it's settled.
        var shown = _showSettled ? settled.Take(_settledShown).ToList() : settled.Where(each => each == _open).ToList();
        foreach (var conversation in shown) _settledList.Children.Add(SettledRow(conversation));
        var hidden = settled.Count - _settledShown;
        if (!_showSettled || hidden <= 0) return;
        var more = new StackPanel { Orientation = Orientation.Horizontal };
        var down = AgentIcons.Line("chevron-down", 16, Ocean.SidebarMuted);
        down.Margin = new Thickness(0, 0, 10, 0);
        down.VerticalAlignment = VerticalAlignment.Center;
        var words = Ocean.Text($"Show {Math.Min(hidden, SettledMore)} more", Ocean.SidebarMuted, 14);
        more.Children.Add(down);
        more.Children.Add(words);
        var button = Ocean.Ghost(more, () =>
        {
            _settledShown += SettledMore;
            ShowConversations();
        }, 6, new Thickness(10, 0, 10, 0), Ocean.RowHover);
        button.Height = 36;
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.MouseEnter += (_, _) => words.Foreground = Ocean.Foreground;
        button.MouseLeave += (_, _) => words.Foreground = Ocean.SidebarMuted;
        AutomationProperties.SetName(button, words.Text);
        _settledList.Children.Add(button);
    }

    // The edge: a 16 px handle across it, a 2 px line when pointed at, dragged to resize.
    private void BuildSidebarEdge(Grid layout, Border side, ColumnDefinition column)
    {
        _sideColumn = column;
        _sidePanel = side;
        var line = new Border { Width = 2, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Center };
        var handle = new Border
        {
            Width = 16,
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, -8, 0),
            Cursor = Cursors.SizeWE,
            Child = line,
            ToolTip = "Drag to resize sidebar",
        };
        ToolTipService.SetPlacement(handle, System.Windows.Controls.Primitives.PlacementMode.Right);
        AutomationProperties.SetName(handle, "Resize Sidebar");
        Panel.SetZIndex(handle, 20);
        handle.MouseEnter += (_, _) => line.Background = Ocean.SidebarDivider;
        handle.MouseLeave += (_, _) => { if (!handle.IsMouseCaptured) line.Background = Brushes.Transparent; };
        double? startX = null;
        var startWidth = 0.0;
        handle.MouseLeftButtonDown += (_, press) =>
        {
            if (press.ClickCount == 2)
            {
                // Back to T3's default.
                _sidebar.Width = Math.Min(SidebarWidth, Math.Max(SidebarMin, ActualWidth - 640));
                ApplySidebar();
                SaveSidebar();
                press.Handled = true;
                return;
            }
            startX = press.GetPosition(this).X;
            startWidth = column.ActualWidth;
            handle.CaptureMouse();
            press.Handled = true;
        };
        handle.MouseMove += (_, move) =>
        {
            if (startX is not { } from || !handle.IsMouseCaptured) return;
            var now = move.GetPosition(this).X;
            if (Math.Abs(now - from) < 2) return;
            _sidebar.Width = Math.Clamp(startWidth + now - from, SidebarMin, MostSidebar());
            column.Width = new GridLength(_sidebar.Width);
        };
        handle.LostMouseCapture += (_, _) =>
        {
            if (startX is null) return;
            startX = null;
            line.Background = handle.IsMouseOver ? Ocean.SidebarDivider : Brushes.Transparent;
            SaveSidebar();
        };
        handle.MouseLeftButtonUp += (_, _) => handle.ReleaseMouseCapture();
        var host = new Grid { Children = { handle } };
        host.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(Visibility)) { Source = side });
        layout.Children.Add(host);

        // T3's toggle, always at the top left: Ctrl+B or a click tucks the sidebar away and back.
        var toggle = Ocean.IconButton(_sidebar.Open ? "panel-left-close" : "panel-left", ToggleSidebar, "Toggle main sidebar (Ctrl+B)", 28, 16, Ocean.SidebarIcon, Ocean.RowHover);
        toggle.HorizontalAlignment = HorizontalAlignment.Left;
        toggle.VerticalAlignment = VerticalAlignment.Top;
        toggle.Margin = new Thickness(12, (TopBar - 28) / 2, 0, 0);
        Grid.SetColumnSpan(toggle, 2);
        Panel.SetZIndex(toggle, 30);
        layout.Children.Add(toggle);
        side.IsVisibleChanged += (_, _) => toggle.Content = AgentIcons.Line(_sidebar.Open ? "panel-left-close" : "panel-left", 16, Ocean.SidebarIcon);
        PreviewKeyDown += (_, key) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control || key.Key != Key.B) return;
            key.Handled = true;
            ToggleSidebar();
        };
        SizeChanged += (_, _) => ApplySidebar();
        ApplySidebar();
    }
}
