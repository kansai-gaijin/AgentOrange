using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace AgentOrange;

/// <summary>
/// The agents' window: conversations down the side, the open one in the middle, and the box to write
/// to it at the bottom, which also says what the agent will be told the person is looking at.
///
/// Built the way T3 Code builds its timeline, and in its quiet dark look, with orange for what
/// matters. What the agent did between messages folds into one line, opened to see each tool. Its
/// replies are Markdown. Streamed words are put on screen every 50 ms rather than as each piece
/// arrives, and only the part of the timeline that changed is drawn again. Only the latest turns are
/// drawn, with earlier ones a click away, and it follows the end unless it's been scrolled up.
/// </summary>
public sealed class AgentsWindow : Window
{
    private const int TurnsAtATime = 20;
    private const double FollowsWithin = 40;
    private const double ColumnWidth = 736;
    private const string Icons = "Segoe Fluent Icons, Segoe MDL2 Assets";

    private static readonly (string? Id, string Name)[] Models =
        [(null, "Default model"), ("claude-opus-5-5", "Opus 5.5"), ("claude-sonnet-5-5", "Sonnet 5.5"), ("claude-fable-5-2", "Fable 5.2"), ("claude-haiku-5-5", "Haiku 5.5")];
    private static readonly (string? Id, string Name)[] Efforts =
        [(null, "Default effort"), ("low", "Low effort"), ("medium", "Medium effort"), ("high", "High effort"), ("xhigh", "Extra high effort"), ("max", "Max effort")];

    private readonly MainWindow _main;
    private readonly StackPanel _list = new();
    private readonly StackPanel _timeline = new();
    private readonly Border _earlier = new();
    private readonly Border _working = new();
    private readonly TextBlock _workingText = new();
    private readonly ScrollViewer _scroll;
    private readonly Button _jump;
    private readonly TextBox _input;
    private readonly TextBlock _placeholder = new() { Text = "Ask the agent to do something…", IsHitTestVisible = false, FontSize = 14, Margin = new Thickness(2, 2, 0, 0) };
    private readonly Button _send;
    private readonly Button _modelPill;
    private readonly Button _effortPill;
    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _subtitle = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _sees = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border _note = new() { Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8) };
    private readonly Border _composer;
    private readonly DispatcherTimer _flush = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    private AgentConversation? _open;
    private int _turnsShown = TurnsAtATime;
    private bool _follow = true;
    private readonly HashSet<AgentItem> _changed = [];
    private readonly List<Segment> _segments = [];
    private readonly Dictionary<string, Segment> _segmentOf = [];
    private readonly HashSet<string> _openTools = [];
    private readonly HashSet<Segment> _openGroups = [];
    private readonly Dictionary<string, Dictionary<string, string>> _choices = []; // Answers being picked, by question request.

    // A run of the timeline drawn as one thing: a message, a request, or the tools used between messages.
    private sealed class Segment
    {
        public required AgentItemKind Kind { get; init; }
        public List<AgentItem> Items { get; } = [];
        public FrameworkElement Element { get; set; } = new Border();
        public AgentMarkdown? Reply { get; set; }
    }

    public AgentsWindow(MainWindow main)
    {
        _main = main;
        Resources.MergedDictionaries.Add(Look());
        Title = "Agents";
        Width = 1120;
        Height = 760;
        MinWidth = 680;
        MinHeight = 460;
        Background = Brush("A.Canvas");
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        _title.Foreground = Brush("A.Strong");
        _subtitle.Foreground = Brush("A.Muted");
        _sees.Foreground = Brush("A.Faint");
        _placeholder.Foreground = Brush("A.Faint");
        _workingText.Foreground = Brush("A.Muted");

        // Down the side.
        var add = Make("A.Ghost", IconAndText("", "New conversation", "A.Normal", 13), NewConversation);
        add.Margin = new Thickness(8, 4, 8, 6);
        add.HorizontalAlignment = HorizontalAlignment.Stretch;
        var heading = new TextBlock { Text = "Agents", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brush("A.Strong"), Margin = new Thickness(18, 16, 18, 8) };
        var side = new DockPanel { Background = Brush("A.Raised") };
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(add, Dock.Top);
        side.Children.Add(heading);
        side.Children.Add(add);
        side.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list, Padding = new Thickness(8, 0, 8, 16) });
        var sideEdge = new Border { BorderBrush = Brush("A.Border"), BorderThickness = new Thickness(0, 0, 1, 0), Child = side };

        // The open conversation.
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { _title, _subtitle } };
        var header = new Border { BorderBrush = Brush("A.Border"), BorderThickness = new Thickness(0, 0, 0, 1), Height = 52, Padding = new Thickness(22, 0, 22, 0), Child = titleRow };

        var earlier = Make("A.Ghost", Text("Show earlier messages", "A.Muted", 12.5), () =>
        {
            _turnsShown += TurnsAtATime;
            DrawAll();
        });
        earlier.HorizontalAlignment = HorizontalAlignment.Center;
        _earlier.Child = earlier;
        _earlier.Margin = new Thickness(0, 0, 0, 12);

        _working.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { Pulse(), _workingText } };
        _working.Margin = new Thickness(0, 10, 0, 4);
        _working.Visibility = Visibility.Collapsed;

        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new StackPanel { MaxWidth = ColumnWidth, Margin = new Thickness(28, 24, 28, 28), Children = { _earlier, _timeline, _working } },
        };

        // Scrolled up, it stays put, with a way back to the end. Back near the end, it follows again.
        _jump = Make("A.Round", Glyph("", "A.Normal", 13), () =>
        {
            _follow = true;
            _scroll.ScrollToEnd();
        });
        _jump.Background = Brush("A.Raised");
        _jump.BorderBrush = Brush("A.InputBorder");
        _jump.HorizontalAlignment = HorizontalAlignment.Center;
        _jump.VerticalAlignment = VerticalAlignment.Bottom;
        _jump.Margin = new Thickness(0, 0, 0, 12);
        _jump.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(_jump, "Go to the latest");
        _scroll.ScrollChanged += (_, changed) =>
        {
            if (changed.ExtentHeightChange == 0) _follow = _scroll.VerticalOffset >= _scroll.ScrollableHeight - FollowsWithin;
            _jump.Visibility = _follow || _scroll.ScrollableHeight <= 0 ? Visibility.Collapsed : Visibility.Visible;
        };
        var timelineArea = new Grid { Children = { _scroll, _jump } };

        // Where it's written to.
        _input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 48,
            MaxHeight = 240,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brush("A.Strong"),
            CaretBrush = Brush("A.Strong"),
            SelectionBrush = Brush("A.Accent"),
            FontSize = 14,
            Padding = new Thickness(0),
        };
        AutomationProperties.SetName(_input, "Message for the agent");
        _send = Make("A.Round", Glyph("", "A.OnAccent", 14), SendOrStop);
        _modelPill = Make("A.Pill", Text("", "A.Muted", 12), () => PickModel());
        _effortPill = Make("A.Pill", Text("", "A.Muted", 12), () => PickEffort());
        _effortPill.Margin = new Thickness(6, 0, 0, 0);

        var seesRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Children = { Glyph("", "A.Faint", 12), _sees } };
        ((TextBlock)seesRow.Children[0]).Margin = new Thickness(0, 1, 6, 0);
        var toolbar = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(_send, Dock.Right);
        DockPanel.SetDock(_modelPill, Dock.Left);
        DockPanel.SetDock(_effortPill, Dock.Left);
        toolbar.Children.Add(_send);
        toolbar.Children.Add(_modelPill);
        toolbar.Children.Add(_effortPill);
        toolbar.Children.Add(seesRow);
        var box = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Bottom);
        box.Children.Add(toolbar);
        box.Children.Add(new Grid { Children = { _placeholder, _input } });
        _composer = new Border
        {
            BorderBrush = Brush("A.InputBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Background = Brush("A.Raised"),
            Padding = new Thickness(16, 14, 10, 10),
            Child = box,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 4, Opacity = 0.5 },
        };
        var composerArea = new StackPanel { MaxWidth = ColumnWidth + 32, Margin = new Thickness(20, 0, 20, 18), Children = { _note, _composer } };

        var conversationArea = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(composerArea, Dock.Bottom);
        conversationArea.Children.Add(header);
        conversationArea.Children.Add(composerArea);
        conversationArea.Children.Add(timelineArea);

        var layout = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(268) }, new ColumnDefinition() } };
        Grid.SetColumn(conversationArea, 1);
        layout.Children.Add(sideEdge);
        layout.Children.Add(conversationArea);
        Content = layout;

        _input.PreviewKeyDown += (_, pressed) =>
        {
            // Enter sends, and Shift+Enter starts a new line.
            if (pressed.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
            pressed.Handled = true;
            if (_input.Text.Trim().Length > 0) SendOrStop();
        };
        _input.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowSendButton();
        };
        _flush.Tick += (_, _) => Flush();
        _tick.Tick += (_, _) =>
        {
            ShowSees();
            ShowWorking();
        };
        _tick.Start();
        Activated += (_, _) =>
        {
            ShowSees();
            if (_open is not null) _open.Unseen = false;
        };
        Closed += (_, _) =>
        {
            Watch(null);
            _flush.Stop();
            _tick.Stop();
        };

        ShowSees();
        ShowConversations();
        Show(_main.Conversations.OrderByDescending(conversation => conversation.Updated).FirstOrDefault());
    }

    // The look, apart from the rest of the app's: near-black, quiet borders, and orange for what matters.
    private static ResourceDictionary Look() => (ResourceDictionary)XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <SolidColorBrush x:Key="A.Canvas" Color="#000000" />
          <SolidColorBrush x:Key="A.Raised" Color="#101011" />
          <SolidColorBrush x:Key="A.Surface" Color="#1C1C1E" />
          <SolidColorBrush x:Key="A.Hover" Color="#19191B" />
          <SolidColorBrush x:Key="A.Selected" Color="#1F1F22" />
          <SolidColorBrush x:Key="A.Border" Color="#1E1E21" />
          <SolidColorBrush x:Key="A.InputBorder" Color="#2A2A2E" />
          <SolidColorBrush x:Key="A.Strong" Color="#F5F5F5" />
          <SolidColorBrush x:Key="A.Normal" Color="#E3E3E5" />
          <SolidColorBrush x:Key="A.Muted" Color="#A3A3A8" />
          <SolidColorBrush x:Key="A.Faint" Color="#6D6D73" />
          <SolidColorBrush x:Key="A.Accent" Color="#FF8A3D" />
          <SolidColorBrush x:Key="A.AccentHover" Color="#FF9D5C" />
          <SolidColorBrush x:Key="A.AccentTint" Color="#22FF8A3D" />
          <SolidColorBrush x:Key="A.OnAccent" Color="#1A0B00" />
          <SolidColorBrush x:Key="A.Danger" Color="#F87171" />
          <SolidColorBrush x:Key="A.DangerTint" Color="#1FF87171" />
          <SolidColorBrush x:Key="A.Success" Color="#34D399" />
          <SolidColorBrush x:Key="A.CodeBack" Color="#0C0C0D" />

          <Style x:Key="A.Ghost" TargetType="Button">
            <Setter Property="Foreground" Value="{StaticResource A.Normal}" />
            <Setter Property="Background" Value="Transparent" />
            <Setter Property="BorderBrush" Value="Transparent" />
            <Setter Property="BorderThickness" Value="1" />
            <Setter Property="Padding" Value="10,6" />
            <Setter Property="Cursor" Value="Hand" />
            <Setter Property="FocusVisualStyle" Value="{x:Null}" />
            <Setter Property="HorizontalContentAlignment" Value="Stretch" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="Button">
                  <Border x:Name="Face" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="8" Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center" />
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Face" Property="Background" Value="{StaticResource A.Hover}" /></Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Face" Property="BorderBrush" Value="{StaticResource A.Accent}" /></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45" /></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style x:Key="A.Pill" TargetType="Button" BasedOn="{StaticResource A.Ghost}">
            <Setter Property="Padding" Value="10,4" />
            <Setter Property="BorderBrush" Value="{StaticResource A.InputBorder}" />
            <Setter Property="HorizontalContentAlignment" Value="Left" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="Button">
                  <Border x:Name="Face" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="13" Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center" />
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Face" Property="Background" Value="{StaticResource A.Hover}" /></Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Face" Property="BorderBrush" Value="{StaticResource A.Accent}" /></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45" /></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style x:Key="A.Option" TargetType="Button" BasedOn="{StaticResource A.Ghost}">
            <Setter Property="Padding" Value="12,8" />
            <Setter Property="BorderBrush" Value="{StaticResource A.InputBorder}" />
            <Setter Property="HorizontalContentAlignment" Value="Left" />
          </Style>

          <Style x:Key="A.Primary" TargetType="Button">
            <Setter Property="Foreground" Value="{StaticResource A.OnAccent}" />
            <Setter Property="Background" Value="{StaticResource A.Accent}" />
            <Setter Property="Padding" Value="14,7" />
            <Setter Property="FontWeight" Value="SemiBold" />
            <Setter Property="Cursor" Value="Hand" />
            <Setter Property="FocusVisualStyle" Value="{x:Null}" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="Button">
                  <Border x:Name="Face" Background="{TemplateBinding Background}" CornerRadius="9" Padding="{TemplateBinding Padding}" BorderThickness="2" BorderBrush="Transparent">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Face" Property="Background" Value="{StaticResource A.AccentHover}" /></Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Face" Property="BorderBrush" Value="{StaticResource A.Strong}" /></Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter TargetName="Face" Property="Background" Value="{StaticResource A.Surface}" />
                      <Setter Property="Foreground" Value="{StaticResource A.Faint}" />
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style x:Key="A.Round" TargetType="Button">
            <Setter Property="Width" Value="34" />
            <Setter Property="Height" Value="34" />
            <Setter Property="Background" Value="{StaticResource A.Accent}" />
            <Setter Property="BorderBrush" Value="Transparent" />
            <Setter Property="Cursor" Value="Hand" />
            <Setter Property="FocusVisualStyle" Value="{x:Null}" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="Button">
                  <Border x:Name="Face" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="17">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Face" Property="Opacity" Value="0.88" /></Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Face" Property="BorderBrush" Value="{StaticResource A.Strong}" /></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Face" Property="Background" Value="{StaticResource A.Surface}" /></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
        </ResourceDictionary>
        """);

    private Brush Brush(string key) => (Brush)FindResource(key);

    internal bool IsLookingAt(AgentConversation conversation) => IsActive && _open == conversation;

    // Small parts everything's made of

    private Button Make(string style, object content, Action click)
    {
        var button = new Button { Style = (Style)FindResource(style), Content = content };
        button.Click += (_, _) => click();
        return button;
    }

    private TextBlock Text(string text, string brush, double size, FontWeight? weight = null) => new()
    {
        Text = text,
        Foreground = Brush(brush),
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private TextBlock Glyph(string glyph, string brush, double size) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily(Icons),
        FontSize = size,
        Foreground = Brush(brush),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private StackPanel IconAndText(string glyph, string text, string brush, double size)
    {
        var icon = Glyph(glyph, brush, size - 1);
        icon.Margin = new Thickness(0, 1, 10, 0);
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, Text(text, brush, size) } };
    }

    // Words that can be selected and copied, without looking like a field.
    private TextBox Words(string text, string brush, double size) => new()
    {
        Text = text,
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(0),
        Foreground = Brush(brush),
        SelectionBrush = Brush("A.Accent"),
        FontSize = size,
        Cursor = Cursors.IBeam,
    };

    private TextBox Mono(string text, double maxHeight)
    {
        var shown = Words(text, "A.Muted", 12);
        shown.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        shown.TextWrapping = TextWrapping.NoWrap;
        shown.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        shown.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        shown.MaxHeight = maxHeight;
        return shown;
    }

    private Border Inset(UIElement content, Thickness margin) => new()
    {
        Background = Brush("A.CodeBack"),
        BorderBrush = Brush("A.Border"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(12, 9, 12, 10),
        Margin = margin,
        Child = content,
    };

    // A dot that breathes, for something working.
    private FrameworkElement Pulse()
    {
        var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = Brush("A.Accent"), Margin = new Thickness(1, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
        return dot;
    }

    private Popup Menu(UIElement by, PlacementMode placement, UIElement content) => new()
    {
        PlacementTarget = by,
        Placement = placement,
        VerticalOffset = placement == PlacementMode.Top ? -6 : 4,
        StaysOpen = false,
        AllowsTransparency = true,
        Child = new Border
        {
            Background = Brush("A.Raised"),
            BorderBrush = Brush("A.InputBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(5),
            MinWidth = 200,
            Margin = new Thickness(10),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 18, ShadowDepth = 3, Opacity = 0.6 },
            Child = content,
        },
    };

    private static string Ago(DateTimeOffset when)
    {
        var gone = DateTimeOffset.UtcNow - when;
        if (gone < TimeSpan.FromMinutes(1)) return "now";
        if (gone < TimeSpan.FromHours(1)) return $"{(int)gone.TotalMinutes}m";
        if (gone < TimeSpan.FromDays(1)) return $"{(int)gone.TotalHours}h";
        return gone < TimeSpan.FromDays(7) ? $"{(int)gone.TotalDays}d" : when.LocalDateTime.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture);
    }

    // Down the side

    /// <summary>Every conversation, by project, the latest first, with what each is doing.</summary>
    internal void ShowConversations()
    {
        _list.Children.Clear();
        var conversations = _main.Conversations;
        if (conversations.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = "No conversations yet.", Foreground = Brush("A.Faint"), FontSize = 12.5, Margin = new Thickness(10, 8, 10, 0) });
            return;
        }

        foreach (var project in conversations.GroupBy(conversation => conversation.ProjectId).OrderByDescending(group => group.Max(conversation => conversation.Updated)))
        {
            _list.Children.Add(new TextBlock { Text = _main.ProjectName(project.Key).ToUpperInvariant(), Foreground = Brush("A.Faint"), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 16, 10, 6), TextTrimming = TextTrimming.CharacterEllipsis });
            foreach (var conversation in project.OrderByDescending(conversation => conversation.Updated)) _list.Children.Add(ConversationRow(conversation));
        }
    }

    private FrameworkElement ConversationRow(AgentConversation conversation)
    {
        var name = conversation.Title.Length > 0 ? conversation.Title : "New conversation";
        var selected = conversation == _open;
        var row = new DockPanel();
        FrameworkElement state = conversation.IsWaiting ? Dot("A.Accent", "Needs you")
            : conversation.IsWorking ? Pulse()
            : conversation.Unseen ? Dot("A.Strong", "New")
            : Text(Ago(conversation.Updated), "A.Faint", 11);
        state.Margin = new Thickness(10, 0, 0, 0);
        DockPanel.SetDock(state, Dock.Right);
        row.Children.Add(state);
        row.Children.Add(Text(name, selected ? "A.Strong" : "A.Normal", 13));

        var button = Make("A.Ghost", row, () => Show(conversation));
        button.Padding = new Thickness(10, 7, 10, 7);
        if (selected) button.Background = Brush("A.Selected");
        button.Margin = new Thickness(0, 0, 0, 1);
        AutomationProperties.SetName(button, $"{name}{(conversation.IsWaiting ? ", needs you" : conversation.IsWorking ? ", working" : conversation.Unseen ? ", new" : "")}");
        button.MouseRightButtonUp += (_, click) =>
        {
            click.Handled = true;
            OfferToDelete(button, conversation);
        };
        return button;
    }

    private FrameworkElement Dot(string brush, string meaning)
    {
        var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = Brush(brush), VerticalAlignment = VerticalAlignment.Center, ToolTip = meaning };
        AutomationProperties.SetName(dot, meaning);
        return dot;
    }

    private void OfferToDelete(UIElement by, AgentConversation conversation)
    {
        Popup? menu = null;
        var delete = Make("A.Ghost", IconAndText("", "Delete conversation", "A.Danger", 13), () =>
        {
            menu!.IsOpen = false;
            if (!Dialogs.Confirm("Delete this conversation?\n\nWhat was said in it is gone from Agent Orange. Anything the agent changed stays changed.")) return;
            _main.DeleteConversation(conversation);
            if (_open == conversation) Show(_main.Conversations.OrderByDescending(each => each.Updated).FirstOrDefault());
            ShowConversations();
        });
        menu = Menu(by, PlacementMode.MousePoint, delete);
        menu.IsOpen = true;
    }

    // A conversation about the project open in the main window, or one picked from a list.
    private void NewConversation()
    {
        if (_main.ProjectOnScreen is { } project)
        {
            Show(_main.NewConversation(project));
            ShowConversations();
            return;
        }

        var projects = _main.AgentProjects();
        var rows = new StackPanel();
        Popup? menu = null;
        rows.Children.Add(new TextBlock { Text = projects.Count == 0 ? "Make a project first." : "Which project is it about?", Foreground = Brush("A.Faint"), FontSize = 12, Margin = new Thickness(10, 6, 10, 6) });
        foreach (var each in projects)
        {
            rows.Children.Add(Make("A.Ghost", Text(each.Name, "A.Normal", 13), () =>
            {
                menu!.IsOpen = false;
                Show(_main.NewConversation(each));
                ShowConversations();
            }));
        }
        menu = Menu(_list, PlacementMode.Relative, rows);
        menu.IsOpen = true;
    }

    // The model and effort, from small lists above where they're shown.

    private void PickModel() => Pick(_modelPill, Models, _open?.Model, chosen =>
    {
        if (_open is not null) _open.Model = chosen;
    });

    private void PickEffort() => Pick(_effortPill, Efforts, _open?.Effort, chosen =>
    {
        if (_open is not null) _open.Effort = chosen;
    });

    private void Pick(Button by, (string? Id, string Name)[] options, string? current, Action<string?> chosen)
    {
        var rows = new StackPanel();
        Popup? menu = null;
        foreach (var (id, name) in options)
        {
            var row = new DockPanel();
            var tick = Glyph(id == current ? "" : "", "A.Accent", 12);
            tick.Width = 16;
            DockPanel.SetDock(tick, Dock.Right);
            row.Children.Add(tick);
            row.Children.Add(Text(name, "A.Normal", 13));
            rows.Children.Add(Make("A.Ghost", row, () =>
            {
                menu!.IsOpen = false;
                chosen(id);
                ShowPills();
            }));
        }
        rows.Children.Add(new TextBlock { Text = "Takes effect from the next message.", Foreground = Brush("A.Faint"), FontSize = 11, Margin = new Thickness(10, 4, 10, 4) });
        menu = Menu(by, PlacementMode.Top, rows);
        menu.IsOpen = true;
    }

    private void ShowPills()
    {
        ((TextBlock)_modelPill.Content).Text = Models.FirstOrDefault(model => model.Id == _open?.Model).Name ?? _open?.Model ?? "Default model";
        ((TextBlock)_effortPill.Content).Text = Efforts.FirstOrDefault(effort => effort.Id == _open?.Effort).Name ?? "Default effort";
    }

    // The open conversation

    private void Watch(AgentConversation? conversation)
    {
        if (_open is not null)
        {
            _open.ItemChanged -= ItemChanged;
            _open.StateChanged -= WorkingChanged;
        }
        _open = conversation;
        if (_open is null) return;
        _open.ItemChanged += ItemChanged;
        _open.StateChanged += WorkingChanged;
    }

    private void Show(AgentConversation? conversation)
    {
        Watch(conversation);
        _turnsShown = TurnsAtATime;
        _openTools.Clear();
        _openGroups.Clear();
        if (conversation is not null) conversation.Unseen = false;

        _title.Text = conversation is null ? "No conversation open" : conversation.Title.Length > 0 ? conversation.Title : "New conversation";
        _subtitle.Text = conversation is null ? "" : _main.ProjectName(conversation.ProjectId);
        _composer.IsEnabled = conversation is not null;
        DrawAll();
        ShowSendButton();
        ShowPills();
        ShowWorking();
        ShowConversations();
        ShowNote(null);
        _follow = true;
        ScrollToEnd();
        if (conversation is not null) _ = Dispatcher.InvokeAsync(() => _input.Focus(), DispatcherPriority.Input);
    }

    private void ShowSees() => _sees.Text = _main.ViewLabel();

    private void ShowNote(string? problem)
    {
        _note.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        if (problem is null) return;
        _note.Background = Brush("A.DangerTint");
        _note.Child = IconAndText("", problem, "A.Danger", 13);
        ((TextBlock)((StackPanel)_note.Child).Children[1]).TextWrapping = TextWrapping.Wrap;
        ((TextBlock)((StackPanel)_note.Child).Children[1]).TextTrimming = TextTrimming.None;
    }

    private void ShowSendButton()
    {
        // While it works, typing something sends it on, and with nothing typed the button stops it.
        var stops = _open is { IsWorking: true } && _input.Text.Trim().Length == 0;
        var can = stops || _input.Text.Trim().Length > 0;
        _send.Content = Glyph(stops ? "" : "", can ? "A.OnAccent" : "A.Faint", stops ? 12 : 14);
        _send.IsEnabled = can;
        AutomationProperties.SetName(_send, stops ? "Stop the agent" : "Send");
        _send.ToolTip = stops ? "Stop" : "Send (Enter)";
    }

    private void ShowWorking()
    {
        var working = _open is { IsWorking: true };
        _working.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        if (!working) return;
        var seconds = (int)(DateTimeOffset.UtcNow - _open!.WorkingSince).TotalSeconds;
        _workingText.Text = _open.IsWaiting ? "Waiting for you" : seconds < 60 ? $"Working · {seconds}s" : $"Working · {seconds / 60}m {seconds % 60}s";
    }

    private async void SendOrStop()
    {
        if (_open is not { } conversation) return;
        var text = _input.Text.Trim();
        if (text.Length == 0)
        {
            if (conversation.IsWorking) conversation.Stop();
            return;
        }

        if (await _main.AgentProblemAsync(conversation) is { } problem)
        {
            ShowNote(problem);
            return;
        }
        ShowNote(null);
        _input.Clear();
        _follow = true;
        conversation.Send(text, _main.ViewLabel(), _main.DescribeView(), () => _main.PrepareAgent(conversation));
        _title.Text = conversation.Title;
        ShowConversations();
        ShowWorking();
    }

    private void WorkingChanged()
    {
        ShowSendButton();
        ShowWorking();
        if (_open is not null && IsActive) _open.Unseen = false;
        if (_follow) ScrollToEnd();
    }

    // Changes are put on screen together, every 50 ms, rather than as each piece of a word arrives.
    private void ItemChanged(AgentItem item)
    {
        _changed.Add(item);
        if (!_flush.IsEnabled) _flush.Start();
    }

    private void Flush()
    {
        _flush.Stop();
        if (_open is null)
        {
            _changed.Clear();
            return;
        }
        var redraw = new HashSet<Segment>();
        foreach (var item in _open.Items.Where(_changed.Contains))
        {
            if (_segmentOf.TryGetValue(item.Id, out var segment))
            {
                // Only the words, for words still arriving, and of those only the block they're arriving in.
                if (segment.Reply is { } reply && item.Kind == AgentItemKind.Assistant) reply.Show(item.Text);
                else redraw.Add(segment);
            }
            else
            {
                Append(item, redraw);
            }
        }
        _changed.Clear();
        foreach (var segment in redraw) Draw(segment);
        ShowWorking();
        if (_follow) ScrollToEnd();
    }

    private void ScrollToEnd() => _ = Dispatcher.InvokeAsync(_scroll.ScrollToEnd, DispatcherPriority.Loaded);

    // The latest turns, and a way to the earlier ones.
    private void DrawAll()
    {
        _timeline.Children.Clear();
        _segments.Clear();
        _segmentOf.Clear();
        _changed.Clear();
        if (_open is null)
        {
            _earlier.Visibility = Visibility.Collapsed;
            _timeline.Children.Add(Welcome("No conversation open", "Start one with New conversation. It's about the project open in the main window."));
            return;
        }

        var turns = _open.Items.Select((item, at) => (item, at)).Where(each => each.item.Kind == AgentItemKind.User).Select(each => each.at).ToList();
        var from = turns.Count > _turnsShown ? turns[^_turnsShown] : 0;
        _earlier.Visibility = from > 0 ? Visibility.Visible : Visibility.Collapsed;
        var redraw = new HashSet<Segment>();
        foreach (var item in _open.Items.Skip(from)) Append(item, redraw);
        foreach (var segment in redraw) Draw(segment);

        if (_open.Items.Count == 0)
        {
            _timeline.Children.Add(Welcome("What should we make?", $"Ask for anything about {_main.ProjectName(_open.ProjectId)}. The agent does the work in the tools, and each time you send something it's told what you're looking at in Agent Orange."));
        }
    }

    private FrameworkElement Welcome(string title, string said)
    {
        var words = Text(said, "A.Muted", 14);
        words.TextWrapping = TextWrapping.Wrap;
        words.TextTrimming = TextTrimming.None;
        words.TextAlignment = TextAlignment.Center;
        words.MaxWidth = 460;
        words.Margin = new Thickness(0, 10, 0, 0);
        var heading = Text(title, "A.Strong", 24, FontWeights.SemiBold);
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        return new StackPanel { Margin = new Thickness(0, 120, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Children = { heading, words } };
    }

    // Tools used one after another go together, between whatever comes before and after them.
    private void Append(AgentItem item, HashSet<Segment> redraw)
    {
        if (_segments.Count == 0 && _timeline.Children.Count > 0) _timeline.Children.Clear(); // The welcome.
        if (item.Kind == AgentItemKind.Tool && _segments.LastOrDefault() is { Kind: AgentItemKind.Tool } tools)
        {
            tools.Items.Add(item);
            _segmentOf[item.Id] = tools;
            redraw.Add(tools);
            return;
        }

        var segment = new Segment { Kind = item.Kind };
        segment.Items.Add(item);
        _segments.Add(segment);
        _segmentOf[item.Id] = segment;
        _timeline.Children.Add(segment.Element);
        redraw.Add(segment);
    }

    private void Draw(Segment segment)
    {
        var at = _timeline.Children.IndexOf(segment.Element);
        var item = segment.Items[0];
        if (segment.Kind == AgentItemKind.Assistant && segment.Reply is { } reply)
        {
            reply.Show(item.Text);
            return;
        }
        segment.Element = segment.Kind switch
        {
            AgentItemKind.User => UserMessage(item),
            AgentItemKind.Assistant => AssistantMessage(segment, item),
            AgentItemKind.Tool => ToolGroup(segment),
            AgentItemKind.Approval => Approval(item),
            AgentItemKind.Question => Questions(item),
            AgentItemKind.Problem => Problem(item.Text),
            _ => Notice(item.Text),
        };
        if (at < 0) return;
        _timeline.Children.RemoveAt(at);
        _timeline.Children.Insert(at, segment.Element);
    }

    private FrameworkElement UserMessage(AgentItem item)
    {
        var words = Words(item.Text, "A.Strong", 14);
        var bubble = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = ColumnWidth * 0.8,
            Background = Brush("A.Surface"),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(16, 10, 16, 11),
            Child = words,
        };
        var message = new StackPanel { Margin = new Thickness(0, 26, 0, 6), Children = { bubble } };
        if (item.View is { Length: > 0 } view)
        {
            var seen = IconAndText("", view, "A.Faint", 11.5);
            seen.HorizontalAlignment = HorizontalAlignment.Right;
            seen.Margin = new Thickness(0, 6, 8, 0);
            seen.ToolTip = "What you were looking at in Agent Orange, which the agent was told";
            message.Children.Add(seen);
        }
        return message;
    }

    private FrameworkElement AssistantMessage(Segment segment, AgentItem item)
    {
        var reply = new AgentMarkdown(new AgentMarkdown.Look(
            Brush("A.Normal"), Brush("A.Strong"), Brush("A.Muted"), Brush("A.Normal"), Brush("A.CodeBack"), Brush("A.Surface"), Brush("A.Border"), Brush("A.Accent"),
            FontFamily, new FontFamily("Cascadia Mono, Consolas"), 14, 23));
        reply.View.SelectionBrush = Brush("A.Accent");
        reply.Show(item.Text);
        segment.Reply = reply;
        return new Border { Margin = new Thickness(0, 10, 0, 10), Child = reply.View };
    }

    private static string ToolGlyph(string? name) => name switch
    {
        "Read" => "",
        "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => "",
        "Bash" or "PowerShell" => "",
        "Grep" or "Glob" => "",
        "WebFetch" or "WebSearch" => "",
        "TodoWrite" => "",
        _ when name?.StartsWith("mcp__", StringComparison.Ordinal) == true => "",
        _ => "",
    };

    // One line for the lot while it's done, or the tool working now while it isn't. Opened, a line each.
    private FrameworkElement ToolGroup(Segment segment)
    {
        var running = _open?.IsWorking == true ? segment.Items.LastOrDefault(tool => tool.ToolState == AgentToolState.Running) : null;
        var opened = _openGroups.Contains(segment);
        var failed = segment.Items.Count(tool => tool.ToolState == AgentToolState.Failed);
        var said = running is not null ? running.Text : Summary(segment.Items);

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        if (running is not null) line.Children.Add(Pulse());
        else line.Children.Add(new TextBlock { Text = opened ? "" : "", FontFamily = new FontFamily(Icons), FontSize = 9, Foreground = Brush("A.Faint"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 1, 10, 0) });
        var words = Text(said, running is not null ? "A.Normal" : "A.Muted", 13);
        words.MaxWidth = ColumnWidth - 60;
        line.Children.Add(words);
        if (failed > 0) line.Children.Add(Text($"  ·  {failed} failed", "A.Danger", 13));

        var header = Make("A.Ghost", line, () =>
        {
            if (!_openGroups.Remove(segment)) _openGroups.Add(segment);
            Draw(segment);
        });
        header.HorizontalAlignment = HorizontalAlignment.Left;
        header.Padding = new Thickness(8, 5, 10, 5);
        AutomationProperties.SetName(header, $"{said}, {(opened ? "showing each tool" : "click to see each tool")}");

        var group = new StackPanel { Margin = new Thickness(-8, 4, 0, 4), Children = { header } };
        if (!opened) return group;
        var list = new StackPanel { Margin = new Thickness(19, 2, 0, 4) };
        foreach (var tool in segment.Items) list.Children.Add(ToolLine(segment, tool));
        group.Children.Add(new Border { BorderBrush = Brush("A.Border"), BorderThickness = new Thickness(1, 0, 0, 0), Margin = new Thickness(13, 0, 0, 0), Child = list });
        return group;
    }

    private FrameworkElement ToolLine(Segment segment, AgentItem tool)
    {
        var row = new DockPanel();
        var mark = tool.ToolState switch
        {
            AgentToolState.Failed => Glyph("", "A.Danger", 11),
            AgentToolState.Running => Glyph("", "A.Accent", 11),
            _ => Glyph("", "A.Faint", 11),
        };
        mark.Margin = new Thickness(10, 0, 0, 0);
        DockPanel.SetDock(mark, Dock.Right);
        var icon = Glyph(ToolGlyph(tool.Tool), "A.Faint", 12);
        icon.Margin = new Thickness(0, 1, 10, 0);
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(mark);
        row.Children.Add(icon);
        row.Children.Add(Text(tool.Text, "A.Normal", 12.5));

        var line = Make("A.Ghost", row, () =>
        {
            if (!_openTools.Remove(tool.Id)) _openTools.Add(tool.Id);
            Draw(segment);
        });
        line.Padding = new Thickness(8, 4, 8, 4);
        line.MaxWidth = ColumnWidth - 30;
        line.HorizontalAlignment = HorizontalAlignment.Left;
        if (!_openTools.Contains(tool.Id) || tool.Detail is not { Length: > 0 } detail) return line;
        return new StackPanel { Children = { line, Inset(Mono(detail, 260), new Thickness(30, 2, 0, 8)) } };
    }

    // Such as "Read 3 files, ran 2 commands", from the two kinds of tool used most.
    private static string Summary(List<AgentItem> tools)
    {
        static string KindOf(string name) => name switch
        {
            "Read" => "read",
            "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => "edit",
            "Bash" or "PowerShell" => "command",
            "Grep" or "Glob" => "search",
            "WebFetch" or "WebSearch" => "web",
            "TodoWrite" => "plan",
            _ when name.StartsWith("mcp__", StringComparison.Ordinal) && name.Split("__") is [_, var server, ..] => $"mcp:{server}",
            _ => $"other:{name}",
        };
        static string Said(string kind, int count) => kind switch
        {
            "read" => count == 1 ? "read a file" : $"read {count} files",
            "edit" => count == 1 ? "edited a file" : $"made {count} edits",
            "command" => count == 1 ? "ran a command" : $"ran {count} commands",
            "search" => count == 1 ? "searched once" : $"searched {count} times",
            "web" => count == 1 ? "looked something up" : $"looked {count} things up",
            "plan" => "updated its plan",
            _ when kind.StartsWith("mcp:", StringComparison.Ordinal) => $"used {kind[4..]}{(count == 1 ? "" : $" {count} times")}",
            _ => $"used {kind[6..]}{(count == 1 ? "" : $" {count} times")}",
        };

        var kinds = tools.GroupBy(tool => KindOf(tool.Tool ?? "")).OrderByDescending(kind => kind.Count()).ToList();
        var said = string.Join(", ", kinds.Take(2).Select(kind => Said(kind.Key, kind.Count())));
        if (kinds.Count > 2) said += $" and {kinds.Skip(2).Sum(kind => kind.Count())} more";
        return char.ToUpperInvariant(said[0]) + said[1..];
    }

    private Border Card(UIElement content, bool waiting) => new()
    {
        Background = Brush("A.Raised"),
        BorderBrush = Brush(waiting ? "A.Accent" : "A.InputBorder"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(14),
        Padding = new Thickness(18, 14, 18, 16),
        Margin = new Thickness(0, 10, 0, 10),
        Child = content,
    };

    private FrameworkElement Problem(string text)
    {
        var said = IconAndText("", text, "A.Danger", 13);
        var words = (TextBlock)said.Children[1];
        words.TextWrapping = TextWrapping.Wrap;
        words.TextTrimming = TextTrimming.None;
        words.MaxWidth = ColumnWidth - 60;
        return new Border { Background = Brush("A.DangerTint"), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 8, 0, 8), Child = said };
    }

    private FrameworkElement Notice(string text)
    {
        var said = Text(text, "A.Faint", 12.5);
        said.Margin = new Thickness(0, 6, 0, 6);
        return said;
    }

    // Something it wants to do that needs the person's yes first. Once answered, it's one quiet line.
    private FrameworkElement Approval(AgentItem item)
    {
        if (item.RequestState != AgentRequestState.Waiting)
        {
            var (glyph, said, brush) = item.RequestState switch
            {
                AgentRequestState.Allowed => ("", "Allowed", "A.Faint"),
                AgentRequestState.Denied => ("", "Not allowed", "A.Danger"),
                _ => ("", "No longer waiting", "A.Faint"),
            };
            var line = IconAndText(glyph, $"{said}: {item.Text}", brush, 12.5);
            ((TextBlock)line.Children[1]).MaxWidth = ColumnWidth - 40;
            line.Margin = new Thickness(0, 6, 0, 6);
            return line;
        }

        var rows = new StackPanel();
        var title = IconAndText("", $"Allow {item.Tool}?", "A.Strong", 14);
        ((TextBlock)title.Children[1]).FontWeight = FontWeights.SemiBold;
        ((TextBlock)title.Children[0]).Foreground = Brush("A.Accent");
        rows.Children.Add(title);
        var what = Words(item.Text, "A.Normal", 13);
        what.Margin = new Thickness(0, 8, 0, 0);
        rows.Children.Add(what);
        if (item.Detail is { Length: > 0 } detail && _openTools.Contains(item.Id)) rows.Children.Add(Inset(Mono(detail, 240), new Thickness(0, 10, 0, 0)));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var allow = Make("A.Primary", "Allow", () => _open?.Answer(item, allow: true));
        var deny = Make("A.Pill", Text("Don't allow", "A.Normal", 13), () => _open?.Answer(item, allow: false));
        deny.Margin = new Thickness(8, 0, 0, 0);
        deny.Padding = new Thickness(14, 6, 14, 6);
        buttons.Children.Add(allow);
        buttons.Children.Add(deny);
        if (item.Detail is { Length: > 0 })
        {
            var details = Make("A.Ghost", Text(_openTools.Contains(item.Id) ? "Hide details" : "Details", "A.Muted", 13), () =>
            {
                if (!_openTools.Remove(item.Id)) _openTools.Add(item.Id);
                if (_segmentOf.TryGetValue(item.Id, out var segment)) Draw(segment);
            });
            details.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(details);
        }
        rows.Children.Add(buttons);
        return Card(rows, waiting: true);
    }

    // Its questions, each answered with one of its choices, several where it says so, or the person's own words.
    private FrameworkElement Questions(AgentItem item)
    {
        var rows = new StackPanel();
        var questions = item.Questions ?? [];
        if (item.RequestState != AgentRequestState.Waiting)
        {
            rows.Children.Add(Text(item.RequestState == AgentRequestState.Answered ? "You answered" : "It asked, but is no longer waiting", "A.Faint", 12));
            foreach (var question in questions)
            {
                var asked = Words(question.Question, "A.Muted", 13);
                asked.Margin = new Thickness(0, 8, 0, 2);
                rows.Children.Add(asked);
                if (item.Answers?.GetValueOrDefault(question.Question) is { } answer) rows.Children.Add(Words(answer, "A.Strong", 13.5));
            }
            return Card(rows, waiting: false);
        }

        if (!_choices.TryGetValue(item.Id, out var chosen)) chosen = _choices[item.Id] = [];
        var send = Make("A.Primary", "Send answers", () =>
        {
            _open?.AnswerQuestions(item, new Dictionary<string, string>(chosen));
            _choices.Remove(item.Id);
        });
        send.HorizontalAlignment = HorizontalAlignment.Left;
        send.Margin = new Thickness(0, 16, 0, 0);
        void ShowReady() => send.IsEnabled = questions.All(question => chosen.GetValueOrDefault(question.Question) is { Length: > 0 });

        foreach (var question in questions)
        {
            var first = rows.Children.Count == 0;
            if (question.Header.Length > 0)
            {
                var chip = Text(question.Header.ToUpperInvariant(), "A.Accent", 10.5, FontWeights.SemiBold);
                chip.Margin = new Thickness(0, first ? 0 : 18, 0, 4);
                rows.Children.Add(chip);
            }
            var asked = Words(question.Question, "A.Strong", 14.5);
            asked.FontWeight = FontWeights.SemiBold;
            rows.Children.Add(asked);
            if (question.MultiSelect) rows.Children.Add(Text("Choose any", "A.Faint", 11.5));

            var picked = (chosen.GetValueOrDefault(question.Question) ?? "").Split(", ", StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var options = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var option in question.Options)
            {
                var on = picked.Contains(option.Label);
                var label = Text(option.Label, "A.Strong", 13, FontWeights.SemiBold);
                label.TextWrapping = TextWrapping.Wrap;
                label.TextTrimming = TextTrimming.None;
                var face = new StackPanel { MaxWidth = 280, Children = { label } };
                if (option.Description.Length > 0)
                {
                    var about = Text(option.Description, "A.Muted", 12);
                    about.TextWrapping = TextWrapping.Wrap;
                    about.TextTrimming = TextTrimming.None;
                    about.Margin = new Thickness(0, 2, 0, 0);
                    face.Children.Add(about);
                }
                var choice = Make("A.Option", face, () =>
                {
                    if (question.MultiSelect)
                    {
                        if (!picked.Remove(option.Label)) picked.Add(option.Label);
                        chosen[question.Question] = string.Join(", ", question.Options.Select(each => each.Label).Where(picked.Contains));
                    }
                    else
                    {
                        chosen[question.Question] = option.Label;
                    }
                    if (_segmentOf.TryGetValue(item.Id, out var segment)) Draw(segment);
                });
                choice.Margin = new Thickness(0, 0, 8, 8);
                if (on)
                {
                    choice.BorderBrush = Brush("A.Accent");
                    choice.Background = Brush("A.AccentTint");
                }
                AutomationProperties.SetName(choice, on ? $"{option.Label}, chosen" : option.Label);
                options.Children.Add(choice);
            }
            rows.Children.Add(options);

            // Or in the person's own words.
            var own = new TextBox
            {
                Text = picked.Count == 0 ? chosen.GetValueOrDefault(question.Question) ?? "" : "",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Brush("A.Strong"),
                CaretBrush = Brush("A.Strong"),
                SelectionBrush = Brush("A.Accent"),
                FontSize = 13,
                Padding = new Thickness(0),
            };
            AutomationProperties.SetName(own, $"Your own answer to: {question.Question}");
            var ownHint = Text("Or say something else…", "A.Faint", 13);
            ownHint.IsHitTestVisible = false;
            ownHint.Visibility = own.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            own.TextChanged += (_, _) =>
            {
                ownHint.Visibility = own.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (own.Text.Trim().Length > 0) chosen[question.Question] = own.Text.Trim();
                else if (picked.Count == 0) chosen.Remove(question.Question);
                ShowReady();
            };
            rows.Children.Add(new Border
            {
                BorderBrush = Brush("A.InputBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(12, 8, 12, 8),
                Background = Brush("A.Canvas"),
                Child = new Grid { Children = { ownHint, own } },
            });
        }
        rows.Children.Add(send);
        ShowReady();
        return Card(rows, waiting: true);
    }
}
