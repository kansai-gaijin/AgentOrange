using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace AgentOrange;

/// <summary>The add provider wizard, and Codex's sign-in.</summary>
public partial class SettingsModal
{
    private ProviderKind _wizardKind = ProviderKind.Claude;
    private CancellationTokenSource? _signIn;

    private void OpenAddProvider()
    {
        _wizardKind = ProviderKind.Claude;
        ShowWizard();
        ShowProviderStep();
    }

    private void OpenCodexSignIn(AgentProvider provider)
    {
        _wizardKind = provider.Kind;
        ShowWizard();
        ShowSignInStep(provider);
    }

    private void ShowWizard()
    {
        SettingsCard.Effect = new BlurEffect { Radius = 6 };
        WizardLayer.Visibility = Visibility.Visible;
    }

    private void CloseWizard()
    {
        _signIn?.Cancel();
        WizardLayer.Visibility = Visibility.Collapsed;
        SettingsCard.Effect = null;
        Focus();
    }

    private void ShowProviderStep()
    {
        var tiles = new UniformGrid { Rows = 1, Margin = new Thickness(-5, 0, -5, 0) };
        foreach (var kind in new[] { ProviderKind.Claude, ProviderKind.Codex })
        {
            var tile = new RadioButton
            {
                Style = (Style)FindResource("ProviderTile"),
                GroupName = "WizardProvider",
                Content = AgentProviders.KindName(kind),
                IsChecked = kind == _wizardKind,
                Margin = new Thickness(5, 0, 5, 0),
            };
            tile.Checked += (_, _) =>
            {
                _wizardKind = kind;
                ShowProviderStep();
            };
            tiles.Children.Add(tile);
        }

        var body = new StackPanel { Margin = new Thickness(24, 16, 24, 20) };
        body.Children.Add(new TextBlock { Text = "Provider", FontSize = 13, FontWeight = FontWeights.Medium, Foreground = (Brush)FindResource("TextStrong"), Margin = new Thickness(0, 0, 0, 10) });
        body.Children.Add(tiles);

        // The app can run Codex's sign-in directly. Claude Code signs in from a terminal.
        Button[] footer = _wizardKind == ProviderKind.Codex
            ?
            [
                TextButton("Cancel", CloseWizard),
                ControlButton("Configure manually", ShowDetailsStep),
                PrimaryButton("Continue with ChatGPT", ShowAccountStep),
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
        var label = Field(AgentProviders.KindName(_wizardKind));
        var program = Field(AgentProviders.DefaultProgram(_wizardKind));
        var folder = Field(Providers.NewLoginFolder(_wizardKind));
        folder.ToolTip = folder.Text;
        var problem = ProblemText();

        var body = new StackPanel
        {
            Margin = new Thickness(8, 8, 8, 12),
            Children =
            {
                Row("Label", "Shown in the provider list.", label),
                Row("Program", "The command used to start this agent.", program),
                Row("Login folder", "Keeps this login separate from your normal setup.", folder),
                problem,
            },
        };

        WizardCard.Child = WizardShell(
            "Add provider",
            "Add a provider to use with your agents.",
            Stepper(current: 2),
            body,
            ControlButton("Back", ShowProviderStep),
            PrimaryButton("Add provider", () =>
            {
                if (string.IsNullOrWhiteSpace(label.Text) || string.IsNullOrWhiteSpace(program.Text) || string.IsNullOrWhiteSpace(folder.Text))
                {
                    ShowProblem(problem, "Fill in all three.");
                    return;
                }

                var provider = new AgentProvider(Guid.NewGuid().ToString(), _wizardKind, label.Text.Trim(), program.Text.Trim(), folder.Text.Trim(), true);
                Providers.Save(provider);
                _selectedProviderId = provider.Id;
                CloseWizard();
                ShowPage(_currentPage);
            }));
    }

    private void ShowAccountStep()
    {
        var name = Field("Personal");
        var problem = ProblemText();
        var body = new StackPanel
        {
            Margin = new Thickness(8, 8, 8, 12),
            Children =
            {
                Row("Account name", "Shown in the provider list.", name),
                problem,
            },
        };

        WizardCard.Child = WizardShell(
            "Add ChatGPT account",
            "Each account has its own sign-in. Choose the account on the sign-in page.",
            null,
            body,
            ControlButton("Back", ShowProviderStep),
            PrimaryButton("Continue", () =>
            {
                if (string.IsNullOrWhiteSpace(name.Text))
                {
                    ShowProblem(problem, "Enter an account name.");
                    return;
                }

                ShowSignInStep(new AgentProvider(
                    Guid.NewGuid().ToString(),
                    ProviderKind.Codex,
                    name.Text.Trim(),
                    AgentProviders.DefaultProgram(ProviderKind.Codex),
                    Providers.NewLoginFolder(ProviderKind.Codex),
                    true));
            }));
    }

    private void ShowSignInStep(AgentProvider provider)
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
            "Sign in to ChatGPT",
            "The sign-in page opens in your browser.",
            null,
            body,
            TextButton("Cancel", CloseWizard));

        _ = RunSignInAsync(provider);
    }

    private async Task RunSignInAsync(AgentProvider provider)
    {
        _signIn?.Cancel();
        using var signIn = new CancellationTokenSource();
        _signIn = signIn;

        var worked = await Providers.SignInCodexAsync(provider, signIn.Token);
        if (_signIn == signIn) _signIn = null;
        if (signIn.IsCancellationRequested) return;

        if (!worked)
        {
            WizardCard.Child = WizardShell(
                "Sign in to ChatGPT",
                "That didn't finish.",
                null,
                new Border { Height = 8 },
                TextButton("Cancel", CloseWizard),
                PrimaryButton("Try again", () => ShowSignInStep(provider)));
            return;
        }

        Providers.Save(provider);
        _selectedProviderId = provider.Id;
        _providerStatuses.Remove(provider.Id);
        CloseWizard();
        ShowPage(_currentPage);
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
