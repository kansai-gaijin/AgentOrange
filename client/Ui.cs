using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace AgentOrange;

/// <summary>Building blocks shared by the settings screen and the wizards. Styles are in Theme.xaml.</summary>
public static class Ui
{
    private static object FindResource(string key) => Application.Current.FindResource(key);

    public static TextBlock ProblemText() => new()
    {
        FontSize = 12.5,
        Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0x6B, 0x6B)),
        Margin = new Thickness(16, 8, 16, 0),
        Visibility = Visibility.Collapsed,
    };

    public static void ShowProblem(TextBlock problem, string message)
    {
        problem.Text = message;
        problem.Visibility = Visibility.Visible;
    }

    public static TextBlock GroupLabel(string title) => new()
    {
        Text = title,
        FontSize = 12,
        FontWeight = FontWeights.Medium,
        Foreground = (Brush)FindResource("TextMuted"),
        Margin = new Thickness(10, 14, 0, 4),
    };

    public static TextBlock Section(string title) => new()
    {
        Text = title,
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
        Foreground = (Brush)FindResource("TextStrong"),
        Margin = new Thickness(16, 24, 0, 10),
    };

    public static Border Card(params FrameworkElement[] rows)
    {
        var stack = new StackPanel();
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0) stack.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Line") });
            stack.Children.Add(rows[i]);
        }

        return new Border
        {
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = stack,
        };
    }

    public static Grid Row(string title, string? description, params FrameworkElement[] controls)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.Medium,
            Foreground = (Brush)FindResource("TextStrong"),
        });
        if (description is not null)
        {
            text.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 12.5,
                Foreground = (Brush)FindResource("TextMuted"),
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 0, 0) };
        for (var i = 0; i < controls.Length; i++)
        {
            controls[i].VerticalAlignment = VerticalAlignment.Center;
            if (i > 0) controls[i].Margin = new Thickness(12, 0, 0, 0);
            right.Children.Add(controls[i]);
        }

        var row = new Grid { Margin = new Thickness(16, 12, 16, 12), MinHeight = 28 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(right, 1);
        row.Children.Add(text);
        row.Children.Add(right);
        return row;
    }

    public static Border Divider() => new() { Height = 1, Background = (Brush)FindResource("Line") };

    public static TextBox Field(string value) => new()
    {
        Style = (Style)FindResource("Field"),
        Text = value,
    };

    public static CheckBox Toggle(bool on, bool enabled = true) => new()
    {
        Style = (Style)FindResource("Toggle"),
        IsChecked = on,
        IsEnabled = enabled,
    };

    public static ComboBox Choice(string[] options, string selected, Action<string> changed)
    {
        var choice = new ComboBox
        {
            Style = (Style)FindResource("Choice"),
            ItemsSource = options,
            SelectedItem = selected,
        };
        choice.SelectionChanged += (_, _) =>
        {
            if (choice.SelectedItem is string value) changed(value);
        };
        return choice;
    }

    // Takes two clicks, for things that are a nuisance to undo.
    // The one waiting on its second click, so a page drawn again, such as after a sync, still asks.
    // Only for one that says what it acts on, as the same label can come back meaning something else.
    private static (string Key, DateTime Until)? _armedTwoClick;
    private static readonly TimeSpan TwoClickStaysArmed = TimeSpan.FromSeconds(15);

    public static Button TwoClickAction(string label, Func<Task> action, string? actsOn = null)
    {
        var button = ActionButton(label);
        var key = actsOn is null ? null : $"{label}|{actsOn}";
        var confirming = key is not null && _armedTwoClick is { } armed && armed.Key == key && DateTime.UtcNow < armed.Until;
        if (confirming) button.Content = "Are you sure?";
        button.Click += async (_, _) =>
        {
            if (!confirming)
            {
                confirming = true;
                button.Content = "Are you sure?";
                if (key is not null) _armedTwoClick = (key, DateTime.UtcNow + TwoClickStaysArmed);
                return;
            }

            confirming = false;
            button.Content = label;
            if (key is not null && _armedTwoClick?.Key == key) _armedTwoClick = null;
            await action();
        };
        return button;
    }

    public static Button ActionButton(string label) => new()
    {
        Style = (Style)FindResource("ControlButton"),
        Content = label,
    };

    public static TextBlock Value(string value) => new()
    {
        Text = value,
        FontSize = 13,
        FontWeight = FontWeights.Medium,
        Foreground = (Brush)FindResource("TextNormal"),
    };

    public static StackPanel Status(string label, bool good) => new()
    {
        Orientation = Orientation.Horizontal,
        Children =
        {
            new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(3.5),
                Background = good ? (Brush)FindResource("Accent") : (Brush)FindResource("TextMuted"),
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
            },
            new TextBlock { Text = label, FontSize = 13, Foreground = (Brush)FindResource("TextNormal") },
        },
    };

    public static DockPanel WizardShell(Action close, string title, string subtitle, UIElement? stepper, UIElement body, params Button[] footerButtons)
    {
        var closeButton = new Button
        {
            Style = (Style)FindResource("HeaderButton"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 10, 0),
            Content = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 11 },
        };
        AutomationProperties.SetName(closeButton, "Close");
        closeButton.Click += (_, _) => close();

        var heading = new StackPanel { Margin = new Thickness(24, 22, 24, 16) };
        heading.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextStrong") });
        heading.Children.Add(new TextBlock { Text = subtitle, FontSize = 13.5, Foreground = (Brush)FindResource("TextNormal"), Margin = new Thickness(0, 7, 0, 0), TextWrapping = TextWrapping.Wrap });
        if (stepper is not null) heading.Children.Add(stepper);

        var top = new Grid();
        top.Children.Add(heading);
        top.Children.Add(closeButton);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in footerButtons)
        {
            button.Margin = new Thickness(8, 0, 0, 0);
            footer.Children.Add(button);
        }

        var shell = new DockPanel();
        var topBorder = new Border { BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Child = top };
        var footerBorder = new Border { BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(24, 14, 24, 14), Child = footer };
        DockPanel.SetDock(topBorder, Dock.Top);
        DockPanel.SetDock(footerBorder, Dock.Bottom);
        shell.Children.Add(topBorder);
        shell.Children.Add(footerBorder);
        shell.Children.Add(body);
        return shell;
    }

    public static Border Stepper(string[] names, int current)
    {
        var grid = new Grid();
        for (var i = 0; i < names.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var step = StepItem(i + 1, names[i], current);
            Grid.SetColumn(step, i);
            grid.Children.Add(step);
        }

        return new Border
        {
            Background = (Brush)FindResource("HoverBackground"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            Margin = new Thickness(0, 14, 0, 0),
            Child = grid,
        };
    }

    public static Border StepItem(int number, string name, int current)
    {
        var done = number < current;
        var circle = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = done ? (Brush)FindResource("Accent") : (Brush)FindResource("ControlBackground"),
            Margin = new Thickness(0, 0, 8, 0),
            Child = new TextBlock
            {
                Text = done ? "" : number.ToString(),
                FontFamily = done ? (FontFamily)FindResource("Icons") : SystemFonts.MessageFontFamily,
                FontSize = done ? 10 : 12,
                FontWeight = FontWeights.Medium,
                Foreground = done ? new SolidColorBrush(Color.FromRgb(0x10, 0x20, 0x2A)) : number == current ? (Brush)FindResource("Accent") : (Brush)FindResource("TextStrong"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        return new Border
        {
            Background = number == current ? (Brush)FindResource("ContentBackground") : Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    circle,
                    new TextBlock { Text = name, FontSize = 13.5, FontWeight = FontWeights.Medium, Foreground = (Brush)FindResource("TextStrong"), VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
    }

    public static Button PrimaryButton(string label, Action onClick) => Clickable("PrimaryButton", label, onClick);

    public static Button ControlButton(string label, Action onClick) => Clickable("ControlButton", label, onClick);

    public static Button TextButton(string label, Action onClick) => Clickable("HeaderButton", label, onClick);

    public static Button Clickable(string style, string label, Action onClick)
    {
        var button = new Button { Style = (Style)FindResource(style), Content = label, VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => onClick();
        return button;
    }
}
