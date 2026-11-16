using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shell;

namespace AgentOrange;

/// <summary>
/// T3 Code's "ocean" dark theme, as its web app resolves it (packages/shared/src/themePalettes.ts and
/// apps/web/src/index.css), with the colours it mixes flattened over the canvas, and the parts its
/// screens are made of: ghost buttons, composer controls, menus in its frosted glass, tooltips,
/// scroll bars and its shimmering "working" text.
/// </summary>
internal static class Ocean
{
    private static SolidColorBrush Hex(string hex, double opacity = 1)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    public static readonly SolidColorBrush Canvas = Hex("#17212B");
    public static readonly SolidColorBrush Sidebar = Hex("#1E2D3B");
    public static readonly SolidColorBrush SidebarDivider = Hex("#303D4B");
    public static readonly SolidColorBrush RowHover = Hex("#283E50");
    public static readonly SolidColorBrush RowActive = Hex("#2F495F");
    public static readonly SolidColorBrush Accent = Hex("#324E66"); // Hover and pressed washes.
    public static readonly SolidColorBrush Control = Hex("#293F52");
    public static readonly SolidColorBrush Muted = Hex("#233544"); // Tracks.
    public static readonly SolidColorBrush Composer = Hex("#2D3640");
    public static readonly SolidColorBrush ComposerOutline = Hex("#283643");
    public static readonly SolidColorBrush Glass = Hex("#3A424B");
    public static readonly SolidColorBrush GlassBorder = Hex("#4E545D");
    public static readonly SolidColorBrush Popover = Hex("#414851");
    public static readonly SolidColorBrush Message = Hex("#375871");
    public static readonly SolidColorBrush CodeBack = Hex("#252E38");
    public static readonly SolidColorBrush InlineCode = Hex("#2E3740");
    public static readonly SolidColorBrush Card = Hex("#222F3B");

    public static readonly SolidColorBrush Foreground = Hex("#FFFAFF");
    public static readonly SolidColorBrush Body = Hex("#E8E4EA"); // Foreground at 90%, as replies are.
    public static readonly SolidColorBrush Soft = Hex("#CCC9D0"); // Foreground at 80%, as composer controls are.
    public static readonly SolidColorBrush MutedText = Hex("#969CA6");
    public static readonly SolidColorBrush Secondary = Hex("#8D8F97");
    public static readonly SolidColorBrush Placeholder = Hex("#A4A4AC");
    public static readonly SolidColorBrush SidebarMuted = Hex("#989CA5");
    public static readonly SolidColorBrush SidebarIcon = Hex("#868B95");

    public static readonly SolidColorBrush Border = Hex("#405567");
    public static readonly SolidColorBrush Border60 = Hex("#30404F");
    public static readonly SolidColorBrush Border70 = Hex("#344555");
    public static readonly SolidColorBrush Border80 = Hex("#3A4D5E");
    public static readonly SolidColorBrush Input = Hex("#4F677B");
    public static readonly SolidColorBrush Dashed = Hex("#4A525C");

    public static readonly SolidColorBrush Primary = Hex("#5BD0D6"); // The send button.
    public static readonly SolidColorBrush PrimaryHover = Hex("#6FD6DB");
    public static readonly SolidColorBrush OnPrimary = Hex("#241523");
    public static readonly SolidColorBrush SendDisabled = Hex("#3E464F");
    public static readonly SolidColorBrush Destructive = Hex("#E73F48");
    public static readonly SolidColorBrush DestructiveHover = Hex("#FB414A");
    public static readonly SolidColorBrush Focus = Hex("#70B9EE");

    public static readonly SolidColorBrush Info = Hex("#51A2FF");
    public static readonly SolidColorBrush Success = Hex("#00D492");
    public static readonly SolidColorBrush Error = Hex("#FF6467");
    public static readonly SolidColorBrush ErrorSurface = Hex("#3C2630");
    public static readonly SolidColorBrush Warning = Hex("#FFB900");
    public static readonly SolidColorBrush WarningStrong = Hex("#FE9A00");
    public static readonly SolidColorBrush WarningSurface = Hex("#2A2B2A");
    public static readonly SolidColorBrush Indigo = Hex("#A3B3FF");
    public static readonly SolidColorBrush Claude = Hex("#D97757");

    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    // Building blocks

    /// <summary>A button that's only its content, with T3 Code's rounded wash on hover, press and focus.</summary>
    public static Button Ghost(object content, Action click, double radius = 8, Thickness? padding = null, Brush? hover = null, Brush? background = null)
    {
        var button = new Button
        {
            Content = content,
            Background = background ?? Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Foreground,
            Padding = padding ?? new Thickness(0),
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Template = Template(radius, hover ?? Accent),
        };
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A square ghost icon button, as T3 Code's icon-sm (28 px) and icon (32 px) buttons.</summary>
    public static Button IconButton(string icon, Action click, string name, double size = 28, double glyph = 16, Brush? brush = null, Brush? hover = null)
    {
        var button = Ghost(AgentIcons.Line(icon, glyph, brush ?? MutedText), click, size >= 32 ? 8 : 8, hover: hover);
        button.Width = size;
        button.Height = size;
        button.ToolTip = name;
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        return button;
    }

    public static ControlTemplate Template(double radius, Brush hover, Brush? pressed = null) => Template(new CornerRadius(radius), hover, pressed);

    public static ControlTemplate Template(CornerRadius corners, Brush hover, Brush? pressed = null)
    {
        var face = new FrameworkElementFactory(typeof(System.Windows.Controls.Border), "Face");
        face.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, corners);
        face.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
        face.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BorderBrushProperty));
        face.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BorderThicknessProperty));
        face.SetValue(System.Windows.Controls.Border.PaddingProperty, new TemplateBindingExtension(System.Windows.Controls.Control.PaddingProperty));
        face.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(System.Windows.Controls.Control.HorizontalContentAlignmentProperty));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, new TemplateBindingExtension(System.Windows.Controls.Control.VerticalContentAlignmentProperty));
        face.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = face };

        var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, hover, "Face"));
        template.Triggers.Add(over);
        var down = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        down.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, pressed ?? hover, "Face"));
        down.Setters.Add(new Setter(UIElement.RenderTransformProperty, new ScaleTransform(0.97, 0.97), "Face"));
        down.Setters.Add(new Setter(UIElement.RenderTransformOriginProperty, new Point(0.5, 0.5), "Face"));
        template.Triggers.Add(down);
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, Focus, "Face"));
        focus.Setters.Add(new Setter(System.Windows.Controls.Border.BorderThicknessProperty, new Thickness(1), "Face"));
        template.Triggers.Add(focus);
        var off = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        off.Setters.Add(new Setter(UIElement.OpacityProperty, 0.64, "Face"));
        template.Triggers.Add(off);
        return template;
    }

    public static TextBlock Text(string text, Brush brush, double size = 14, FontWeight? weight = null, double? lineHeight = null)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (lineHeight is { } height)
        {
            block.LineHeight = height;
            block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        }
        return block;
    }

    public static TextBlock Wrapped(string text, Brush brush, double size = 14, double? lineHeight = null)
    {
        var block = Text(text, brush, size, lineHeight: lineHeight ?? Math.Round(size * 1.625, 2));
        block.TextWrapping = TextWrapping.Wrap;
        block.TextTrimming = TextTrimming.None;
        return block;
    }

    /// <summary>An icon and words side by side, 6 px apart, as T3 Code's rows are.</summary>
    public static StackPanel IconText(string icon, string text, Brush brush, double size = 14, double iconSize = 16, FontWeight? weight = null, double gap = 6)
    {
        var glyph = AgentIcons.Line(icon, iconSize, brush);
        glyph.Margin = new Thickness(0, 0, gap, 0);
        glyph.VerticalAlignment = VerticalAlignment.Center;
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { glyph, Text(text, brush, size, weight) } };
    }

    /// <summary>
    /// The light that runs across words while something works, as T3 Code's live-tool-shine: a
    /// 72 px band of the foreground colour over the quieter one, 2.2 seconds a sweep.
    /// </summary>
    public static void Shine(TextBlock words, Brush quiet)
    {
        var base_ = (quiet as SolidColorBrush)?.Color ?? Colors.Gray;
        var shine = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            GradientStops =
            {
                new GradientStop(base_, 0),
                new GradientStop(base_, 0.4),
                new GradientStop(Foreground.Color, 0.5),
                new GradientStop(base_, 0.6),
                new GradientStop(base_, 1),
            },
        };
        var move = new TranslateTransform();
        shine.RelativeTransform = move;
        words.Foreground = shine;
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-1, 1, TimeSpan.FromSeconds(2.2)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    /// <summary>A pop-up in T3 Code's frosted glass, as its menus and popovers are.</summary>
    public static Popup Menu(UIElement by, PlacementMode placement, UIElement content, double minWidth = 160, double offset = 6)
    {
        var menu = NewMenu(by, placement, content, minWidth, offset);

        // Escape closes it, as it does T3 Code's, from wherever in it has focus.
        menu.Child.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape || e.Handled) return;
            menu.IsOpen = false;
            e.Handled = true;
        };

        // A pop-up is a window of its own, so one whose button is hidden or taken away, as when the page
        // under it is drawn again or left, would float on over everything. It goes with its button.
        void Gone(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!by.IsVisible) menu.IsOpen = false;
        }
        menu.Opened += (_, _) => by.IsVisibleChanged += Gone;
        menu.Closed += (_, _) => by.IsVisibleChanged -= Gone;
        return menu;
    }

    private static Popup NewMenu(UIElement by, PlacementMode placement, UIElement content, double minWidth, double offset)
    {
        return new Popup
        {
            PlacementTarget = by,
            Placement = placement,
            VerticalOffset = placement == PlacementMode.Top ? -offset : placement == PlacementMode.Bottom ? offset : 0,
            HorizontalOffset = placement == PlacementMode.Right ? offset : 0,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = new System.Windows.Controls.Border
            {
                Background = Glass,
                BorderBrush = GlassBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(4),
                MinWidth = minWidth,
                Margin = new Thickness(14, 6, 14, 22),
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 32, ShadowDepth = 10, Direction = 270, Opacity = 0.55 },
                Child = content,
            },
        };
    }

    /// <summary>A row of a menu: 28 px, 14 px words, the accent wash when highlighted.</summary>
    public static Button MenuRow(UIElement content, Action click)
    {
        var row = Ghost(content, click, 4, new Thickness(8, 0, 8, 0));
        row.MinHeight = 28;
        row.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        return row;
    }

    public static ResourceDictionary Resources() => (ResourceDictionary)XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <!-- T3 Code's tooltip: the popover colour, a 1 px border, 6 px corners, 12 px words. -->
          <Style TargetType="ToolTip">
            <Setter Property="OverridesDefaultStyle" Value="True" />
            <Setter Property="HasDropShadow" Value="False" />
            <Setter Property="Foreground" Value="#FFFAFF" />
            <Setter Property="FontSize" Value="12" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ToolTip">
                  <Border Background="#414851" BorderBrush="#405567" BorderThickness="1" CornerRadius="6" Padding="8,4" MaxWidth="320" Margin="0,0,8,8">
                    <Border.Effect><DropShadowEffect Color="Black" BlurRadius="10" ShadowDepth="2" Direction="270" Opacity="0.3" /></Border.Effect>
                    <ContentPresenter>
                      <ContentPresenter.Resources>
                        <Style TargetType="TextBlock"><Setter Property="TextWrapping" Value="Wrap" /><Setter Property="LineHeight" Value="16.5" /></Style>
                      </ContentPresenter.Resources>
                    </ContentPresenter>
                  </Border>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <!-- Its scroll bar: 6 px, a thumb of white at 8%, 12% under the mouse, with no track or arrows. -->
          <Style x:Key="Thumb" TargetType="Thumb">
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="Thumb">
                  <Border x:Name="Face" Background="#14FFFFFF" CornerRadius="3" />
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Face" Property="Background" Value="#1FFFFFFF" /></Trigger>
                    <Trigger Property="IsDragging" Value="True"><Setter TargetName="Face" Property="Background" Value="#29FFFFFF" /></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style TargetType="ScrollBar">
            <Setter Property="Background" Value="Transparent" />
            <Setter Property="Width" Value="6" />
            <Setter Property="MinWidth" Value="6" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ScrollBar">
                  <Track x:Name="PART_Track" IsDirectionReversed="True">
                    <Track.Thumb><Thumb Style="{StaticResource Thumb}" /></Track.Thumb>
                  </Track>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
            <Style.Triggers>
              <Trigger Property="Orientation" Value="Horizontal">
                <Setter Property="Width" Value="Auto" />
                <Setter Property="MinWidth" Value="0" />
                <Setter Property="Height" Value="6" />
                <Setter Property="MinHeight" Value="6" />
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="ScrollBar">
                      <Track x:Name="PART_Track" IsDirectionReversed="False">
                        <Track.Thumb><Thumb Style="{StaticResource Thumb}" /></Track.Thumb>
                      </Track>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Trigger>
            </Style.Triggers>
          </Style>
        </ResourceDictionary>
        """);
}
