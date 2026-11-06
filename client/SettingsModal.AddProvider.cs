using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>The add provider wizard, and Codex's sign-in.</summary>
public partial class SettingsModal
{
    private static readonly string[] ProviderSteps = ["Provider", "Details"];

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
            CloseWizard,
            "Add provider",
            "Add a provider to use with your agents.",
            Stepper(ProviderSteps, 1),
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
            CloseWizard,
            "Add provider",
            "Add a provider to use with your agents.",
            Stepper(ProviderSteps, 2),
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
            CloseWizard,
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
            CloseWizard,
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
            CloseWizard,
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

    private void WizardLayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CloseWizard();
    }
}
