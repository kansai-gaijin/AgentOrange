using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AgentOrange;

/// <summary>The Providers page: agent programs and their logins.</summary>
public partial class SettingsModal
{
    private readonly Dictionary<string, ProviderStatus> _providerStatuses = [];
    private string? _selectedProviderId;
    private Action? _refreshProviderDetails;

    private UIElement[] AgentProvidersPage()
    {
        _refreshProviderDetails = null;
        var details = new ContentControl();
        var list = new StackPanel();
        var providers = Providers.List();
        var itemRefreshers = new Dictionary<string, Action>();

        RadioButton? selected = null;
        foreach (var provider in providers)
        {
            var (item, refresh) = AgentProviderItem(provider);
            itemRefreshers[provider.Id] = refresh;
            item.Checked += (_, _) =>
            {
                _selectedProviderId = provider.Id;
                var host = new StackPanel();
                details.Content = host;
                _refreshProviderDetails = () => FillProviderDetails(host, provider.Id);
                _refreshProviderDetails();
            };
            if (selected is null || provider.Id == _selectedProviderId) selected = item;
            list.Children.Add(item);
        }
        if (selected is not null) selected.IsChecked = true;
        else details.Content = Row("No providers yet", "Add Claude Code or Codex to use it here.");

        var addProvider = new Button
        {
            Style = (Style)FindResource("AddRow"),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 12, Foreground = (Brush)FindResource("TextNormal"), Margin = new Thickness(2, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "Add provider", FontSize = 14, FontWeight = FontWeights.Medium, Foreground = (Brush)FindResource("TextStrong") },
                },
            },
        };
        AutomationProperties.SetName(addProvider, "Add provider");
        addProvider.Click += (_, _) => OpenAddProvider();
        list.Children.Add(addProvider);

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var listPanel = new Border { BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(0, 0, 1, 0), Child = list };
        Grid.SetColumn(details, 1);
        layout.Children.Add(listPanel);
        layout.Children.Add(details);

        foreach (var provider in providers) _ = CheckProviderAsync(provider, itemRefreshers);

        return
        [
            Section("Providers"),
            new Border { BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = layout },
        ];
    }

    private async Task CheckProviderAsync(AgentProvider provider, Dictionary<string, Action> itemRefreshers)
    {
        var status = await Providers.CheckAsync(provider);
        _providerStatuses[provider.Id] = status;
        if (itemRefreshers.TryGetValue(provider.Id, out var refresh)) refresh();
        if (_selectedProviderId == provider.Id) _refreshProviderDetails?.Invoke();
    }

    private (RadioButton Item, Action Refresh) AgentProviderItem(AgentProvider provider)
    {
        var version = new TextBlock { FontSize = 11, FontFamily = new FontFamily("Consolas"), Foreground = (Brush)FindResource("TextNormal"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var name = new StackPanel { Orientation = Orientation.Horizontal };
        name.Children.Add(new TextBlock { Text = provider.Label, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextStrong"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 110 });
        name.Children.Add(version);

        var detail = new TextBlock { FontSize = 12.5, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        var text = new StackPanel { Margin = new Thickness(12, 0, 8, 0) };
        text.Children.Add(name);
        text.Children.Add(detail);

        void Refresh()
        {
            var status = _providerStatuses.GetValueOrDefault(provider.Id);
            version.Text = status?.Version is { } number ? $"v{number}" : "";
            detail.Text = status is null ? "Checking…" : ProviderStatusLine(status);
        }
        Refresh();

        var icon = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 15, Foreground = (Brush)FindResource("TextNormal") };
        var toggle = Toggle(provider.Enabled);
        toggle.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(toggle, $"{provider.Label} on or off");
        toggle.Checked += (_, _) => SetProviderEnabled(provider.Id, true);
        toggle.Unchecked += (_, _) => SetProviderEnabled(provider.Id, false);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(toggle, 2);
        row.Children.Add(icon);
        row.Children.Add(text);
        row.Children.Add(toggle);

        var item = new RadioButton { Style = (Style)FindResource("ListItem"), GroupName = "AgentProviders", Content = row };
        AutomationProperties.SetName(item, provider.Label);
        return (item, Refresh);
    }

    private void SetProviderEnabled(string id, bool enabled)
    {
        var provider = Providers.List().FirstOrDefault(existing => existing.Id == id);
        if (provider is not null) Providers.Save(provider with { Enabled = enabled });
    }

    private static string ProviderStatusLine(ProviderStatus status) =>
        status.SignedIn ? $"Signed in · {status.Detail}" : status.Detail;

    private void FillProviderDetails(StackPanel host, string id)
    {
        var provider = Providers.List().FirstOrDefault(existing => existing.Id == id);
        if (provider is null) return;

        var status = _providerStatuses.GetValueOrDefault(id);
        host.Children.Clear();

        var header = new DockPanel { Margin = new Thickness(16, 14, 16, 12) };
        var version = new TextBlock { Text = status?.Version is { } number ? $"v{number}" : "", FontSize = 11, FontFamily = new FontFamily("Consolas"), Foreground = (Brush)FindResource("TextNormal"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(version, Dock.Right);
        header.Children.Add(version);
        header.Children.Add(new TextBlock { Text = provider.Label, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextStrong"), TextTrimming = TextTrimming.CharacterEllipsis });

        host.Children.Add(header);
        host.Children.Add(Divider());
        host.Children.Add(ProviderStatusRow(provider, status));
        host.Children.Add(Divider());
        host.Children.Add(ProviderFieldRow("Display name", null, provider.Label, value => provider with { Label = value }));
        host.Children.Add(Divider());
        host.Children.Add(ProviderFieldRow("Program", "The command used to start this agent.", provider.Program, value => provider with { Program = value }));
        host.Children.Add(Divider());
        host.Children.Add(ProviderFieldRow("Login folder", "Keeps this login separate from your normal setup.", provider.LoginFolder, value => provider with { LoginFolder = value }));
        host.Children.Add(Divider());
        host.Children.Add(Row("Remove", "The login folder stays on this PC.", TwoClickAction("Remove", () =>
        {
            Providers.Remove(provider.Id);
            _providerStatuses.Remove(provider.Id);
            _selectedProviderId = null;
            ShowPage(_currentPage);
            return Task.CompletedTask;
        })));
    }

    private Grid ProviderStatusRow(AgentProvider provider, ProviderStatus? status)
    {
        if (status is null) return Row("Status", null, Status("Checking…", false));
        if (!status.ProgramFound) return Row("Status", status.Detail, Status("Not found", false), CheckAgainButton());

        if (status.SignedIn)
        {
            return Row("Status", status.Detail, Status("Signed in", true), TwoClickAction("Sign out", async () =>
            {
                await Providers.SignOutAsync(provider);
                ShowPage(_currentPage);
            }));
        }

        var signIn = Action("Sign in");
        signIn.Click += (_, _) =>
        {
            if (provider.Kind == ProviderKind.Codex)
            {
                OpenCodexSignIn(provider);
            }
            else
            {
                AgentProviders.OpenClaudeSignIn(provider);
            }
        };
        return Row("Status", provider.Kind == ProviderKind.Claude ? "Sign in opens a terminal. Check again afterward." : status.Detail, Status(status.Detail, false), signIn, CheckAgainButton());
    }

    private Button CheckAgainButton()
    {
        var button = Action("Check again");
        button.Click += (_, _) => ShowPage(_currentPage);
        return button;
    }

    private Grid ProviderFieldRow(string title, string? description, string value, Func<string, AgentProvider> withValue)
    {
        var field = Field(value);
        field.ToolTip = value;
        var saved = value;

        void Apply()
        {
            var text = field.Text.Trim();
            if (text.Length == 0)
            {
                field.Text = saved;
                return;
            }
            if (text == saved) return;

            saved = text;
            Providers.Save(withValue(text));
            ShowPage(_currentPage);
        }

        field.LostKeyboardFocus += (_, _) => Apply();
        field.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Apply();
        };
        return Row(title, description, field);
    }
}
