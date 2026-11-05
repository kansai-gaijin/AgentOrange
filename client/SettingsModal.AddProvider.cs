using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace AgentOrange;

/// <summary>A mock of the add provider wizard. Nothing is really added or signed in.</summary>
public partial class SettingsModal
{
    // SignInAccount is null when the app can't sign in for that provider, as with Claude Code.
    private sealed record ProviderChoice(string Name, string? SignInAccount, string Program, string LoginFolder);

    private static readonly ProviderChoice[] ProviderChoices =
    [
        new("Claude Code", null, "claude", "~/.claude-agent-orange-2"),
        new("Codex", "ChatGPT", "codex", "~/.codex-agent-orange-2"),
    ];

    private ProviderChoice _wizardChoice = ProviderChoices[0];

    private void OpenAddProvider()
    {
        _wizardChoice = ProviderChoices[0];
        SettingsCard.Effect = new BlurEffect { Radius = 6 };
        WizardLayer.Visibility = Visibility.Visible;
        ShowProviderStep();
    }

    private void CloseWizard()
    {
        WizardLayer.Visibility = Visibility.Collapsed;
        SettingsCard.Effect = null;
        Focus();
    }

    private void ShowProviderStep()
    {
        var tiles = new UniformGrid { Rows = 1, Margin = new Thickness(-5, 0, -5, 0) };
        foreach (var choice in ProviderChoices)
        {
            var tile = new RadioButton
            {
                Style = (Style)FindResource("ProviderTile"),
                GroupName = "WizardProvider",
                Content = choice.Name,
                IsChecked = choice == _wizardChoice,
                Margin = new Thickness(5, 0, 5, 0),
            };
            tile.Checked += (_, _) =>
            {
                _wizardChoice = choice;
                ShowProviderStep();
            };
            tiles.Children.Add(tile);
        }

        var body = new StackPanel { Margin = new Thickness(24, 16, 24, 20) };
        body.Children.Add(new TextBlock { Text = "Provider", FontSize = 13, FontWeight = FontWeights.Medium, Foreground = (Brush)FindResource("TextStrong"), Margin = new Thickness(0, 0, 0, 10) });
        body.Children.Add(tiles);

        Button[] footer = _wizardChoice.SignInAccount is { } account
            ?
            [
                TextButton("Cancel", CloseWizard),
                ControlButton("Configure manually", ShowDetailsStep),
                PrimaryButton($"Continue with {account}", ShowAccountStep),
            ]
            :
            [
                TextButton("Cancel", CloseWizard),
                PrimaryButton("Next", ShowDetailsStep),
            ];

        WizardCard.Child = WizardShell(
            "Add provider",
            "Add a provider to use with your agents.",
            Stepper(current: 1),
            body,
            footer);
    }

    private void ShowDetailsStep()
    {
        var body = new StackPanel
        {
            Margin = new Thickness(8, 8, 8, 12),
            Children =
            {
                Row("Label", "Shown in the provider list.", Field(_wizardChoice.Name)),
                Row("Program", "The command used to start this agent.", Field(_wizardChoice.Program)),
                Row("Login folder", "Keeps this login separate from your normal setup.", Field(_wizardChoice.LoginFolder)),
            },
        };

        WizardCard.Child = WizardShell(
            "Add provider",
            "Add a provider to use with your agents.",
            Stepper(current: 2),
            body,
            ControlButton("Back", ShowProviderStep),
            PrimaryButton("Add provider", CloseWizard));
    }

    private void ShowAccountStep()
    {
        var body = new StackPanel
        {
            Margin = new Thickness(8, 8, 8, 12),
            Children =
            {
                Row("Account name", "Shown in the provider list.", Field("Personal")),
            },
        };

        WizardCard.Child = WizardShell(
            $"Add {_wizardChoice.SignInAccount} account",
            "Each account has its own sign-in. Choose the account on the sign-in page.",
            null,
            body,
            ControlButton("Back", ShowProviderStep),
            PrimaryButton("Continue", ShowSignInStep));
    }

    private void ShowSignInStep()
    {
        var body = new StackPanel
        {
            Margin = new Thickness(24, 12, 24, 24),
            Children =
            {
                new ProgressBar { IsIndeterminate = true, Height = 4, BorderThickness = new Thickness(0), Background = (Brush)FindResource("ControlBackground"), Foreground = (Brush)FindResource("Accent") },
                new TextBlock { Text = "Waiting for you to finish signing in…", FontSize = 13, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(0, 12, 0, 0) },
            },
        };

        WizardCard.Child = WizardShell(
            $"Sign in to {_wizardChoice.SignInAccount}",
            "We opened the sign-in page in your browser.",
            null,
            body,
            TextButton("Cancel", CloseWizard),
            ControlButton("Open the page again", () => { }));
    }

    // Wizard pieces

    private DockPanel WizardShell(string title, string subtitle, UIElement? stepper, UIElement body, params Button[] footerButtons)
    {
        var close = new Button
        {
            Style = (Style)FindResource("HeaderButton"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 10, 0),
            Content = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 11 },
        };
        AutomationProperties.SetName(close, "Close");
        close.Click += (_, _) => CloseWizard();

        var heading = new StackPanel { Margin = new Thickness(24, 22, 24, 16) };
        heading.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextStrong") });
        heading.Children.Add(new TextBlock { Text = subtitle, FontSize = 13.5, Foreground = (Brush)FindResource("TextNormal"), Margin = new Thickness(0, 7, 0, 0), TextWrapping = TextWrapping.Wrap });
        if (stepper is not null) heading.Children.Add(stepper);

        var top = new Grid();
        top.Children.Add(heading);
        top.Children.Add(close);

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

    private Border Stepper(int current)
    {
        var grid = new Grid();
        string[] names = ["Provider", "Details"];
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

    private Border StepItem(int number, string name, int current)
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
                FontFamily = done ? (FontFamily)FindResource("Icons") : FontFamily,
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

    private Button PrimaryButton(string label, Action onClick) => Clickable("PrimaryButton", label, onClick);

    private Button ControlButton(string label, Action onClick) => Clickable("ControlButton", label, onClick);

    private Button TextButton(string label, Action onClick) => Clickable("HeaderButton", label, onClick);

    private Button Clickable(string style, string label, Action onClick)
    {
        var button = new Button { Style = (Style)FindResource(style), Content = label, VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void WizardLayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CloseWizard();
    }
}
