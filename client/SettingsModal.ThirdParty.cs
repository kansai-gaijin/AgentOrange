using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>The Third Party page: the generation services workflows can use, their keys, and what each is for.</summary>
public partial class SettingsModal
{
    private readonly Dictionary<string, ServiceCheck> _serviceChecks = [];
    private string? _selectedServiceId;

    public ThirdParty Services { get; set; } = null!;

    private UIElement[] ThirdPartyPage()
    {
        var details = new ContentControl();
        var list = new StackPanel();
        var itemRefreshers = new List<Action>();
        var firstChoices = new StackPanel();

        void RefreshAll()
        {
            foreach (var refresh in itemRefreshers) refresh();
            FillFirstChoices(firstChoices);
        }

        RadioButton? selected = null;
        foreach (var group in ThirdParty.Catalog.GroupBy(service => service.Group))
        {
            list.Children.Add(new TextBlock
            {
                Text = group.Key,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = (Brush)FindResource("TextMuted"),
                Margin = new Thickness(14, 12, 14, 6),
            });

            foreach (var service in group)
            {
                var (item, refresh) = ServiceItem(service);
                itemRefreshers.Add(refresh);
                item.Checked += (_, _) =>
                {
                    _selectedServiceId = service.Id;
                    var host = new StackPanel();
                    details.Content = host;
                    FillServiceDetails(host, service, RefreshAll);
                };
                if (selected is null || service.Id == _selectedServiceId) selected = item;
                list.Children.Add(item);
            }
        }

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var listPanel = new Border { BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(0, 0, 1, 0), Child = list };
        Grid.SetColumn(details, 1);
        layout.Children.Add(listPanel);
        layout.Children.Add(details);

        selected!.IsChecked = true;
        FillFirstChoices(firstChoices);

        return
        [
            Section("Services"),
            new Border { BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = layout },
            Section("First choice"),
            firstChoices,
        ];
    }

    /// <summary>Which service a workflow uses for each job, when it isn't told one.</summary>
    private void FillFirstChoices(StackPanel host)
    {
        host.Children.Clear();
        var rows = new List<FrameworkElement>();
        foreach (var use in Enum.GetValues<ServiceUse>())
        {
            var ready = Services.For(use);
            const string description = "Used unless a workflow picks another.";
            if (ready.Count == 0)
            {
                var none = Value("Nothing set up");
                none.Foreground = (Brush)FindResource("TextMuted");
                rows.Add(Row(ThirdParty.UseName(use), description, none));
                continue;
            }

            var first = Services.FirstFor(use)!;
            var choice = Choice(ready.Select(service => service.Name).ToArray(), first.Name, name =>
            {
                if (ready.FirstOrDefault(service => service.Name == name) is { } picked) Services.SetFirst(use, picked);
            });
            AutomationProperties.SetName(choice, $"First choice for {ThirdParty.UseName(use)}");
            rows.Add(Row(ThirdParty.UseName(use), description, choice));
        }
        host.Children.Add(Card(rows.ToArray()));
    }

    private string ServiceLine(ThirdPartyService service)
    {
        if (service.NoApi is not null) return "No public API yet";
        if (!Services.IsSetUp(service)) return "Not set up";
        if (_serviceChecks.GetValueOrDefault(service.Id) is { } check) return check.Works ? "Connected" : "Key refused";
        var uses = Services.UsesOf(service);
        return uses.Count == 0 ? "Set up · used for nothing" : string.Join(", ", uses.Select(ThirdParty.UseName));
    }

    private (RadioButton Item, Action Refresh) ServiceItem(ThirdPartyService service)
    {
        var detail = new TextBlock { FontSize = 12.5, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Margin = new Thickness(0, 6, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = service.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextStrong"), TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(detail);

        void Refresh()
        {
            detail.Text = ServiceLine(service);
            var ready = Services.IsSetUp(service) && _serviceChecks.GetValueOrDefault(service.Id) is not { Works: false };
            dot.Background = ready ? (Brush)FindResource("Accent") : (Brush)FindResource("Line");
        }
        Refresh();

        var row = new DockPanel();
        row.Children.Add(dot);
        row.Children.Add(text);

        var item = new RadioButton { Style = (Style)FindResource("ListItem"), GroupName = "ThirdPartyServices", Content = row };
        AutomationProperties.SetName(item, service.Name);
        return (item, Refresh);
    }

    private void FillServiceDetails(StackPanel host, ThirdPartyService service, Action refreshAll)
    {
        void Redraw()
        {
            refreshAll();
            host.Children.Clear();
            FillServiceDetails(host, service, refreshAll);
        }

        var header = new StackPanel { Margin = new Thickness(16, 14, 16, 12) };
        header.Children.Add(new TextBlock { Text = service.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextStrong") });
        header.Children.Add(new TextBlock { Text = service.Summary, FontSize = 12.5, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        host.Children.Add(header);
        host.Children.Add(Divider());

        if (service.NoApi is not null)
        {
            host.Children.Add(Row("Can't connect yet", service.NoApi, LinkButton("Their site", service.Docs)));
            return;
        }

        var setUp = Services.IsSetUp(service);
        host.Children.Add(ServiceStatusRow(service, setUp, Redraw));

        foreach (var field in service.Fields)
        {
            host.Children.Add(Divider());
            host.Children.Add(ServiceFieldRow(service, field, Redraw));
        }

        if (setUp)
        {
            host.Children.Add(Divider());
            host.Children.Add(new TextBlock { Text = "Use it for", FontSize = 13, FontWeight = FontWeights.Medium, Foreground = (Brush)FindResource("TextStrong"), Margin = new Thickness(16, 14, 16, 2) });
            var uses = Services.UsesOf(service);
            foreach (var use in service.Uses)
            {
                var toggle = Toggle(uses.Contains(use));
                AutomationProperties.SetName(toggle, $"Use {service.Name} for {ThirdParty.UseName(use)}");
                toggle.Checked += (_, _) => { Services.SetUse(service, use, true); refreshAll(); };
                toggle.Unchecked += (_, _) => { Services.SetUse(service, use, false); refreshAll(); };
                var line = Row(ThirdParty.UseName(use), null, toggle);
                line.Margin = new Thickness(16, 6, 16, 6);
                host.Children.Add(line);
            }

            host.Children.Add(Divider());
            host.Children.Add(Row("Remove", "Forgets its key on this PC. The account and anything already made stay as they are.", TwoClickAction("Remove", () =>
            {
                Services.Forget(service);
                _serviceChecks.Remove(service.Id);
                Redraw();
                return Task.CompletedTask;
            }, service.Id)));
        }
        else
        {
            host.Children.Add(Divider());
            host.Children.Add(Row("Can be used for", string.Join(", ", service.Uses.Select(ThirdParty.UseName))));
        }
    }

    private Grid ServiceStatusRow(ThirdPartyService service, bool setUp, Action redraw)
    {
        if (!setUp)
        {
            var status = Status("Not set up", false);
            return service.ViaCodex
                ? Row("Status", "Uses the ChatGPT plan of a Codex login, with no key.", status)
                : Row("Status", "Every workflow works without it.", status, LinkButton("Get a key", service.KeyPage));
        }

        var check = _serviceChecks.GetValueOrDefault(service.Id);
        var checkButton = ActionButton("Check");
        AutomationProperties.SetName(checkButton, $"Check {service.Name}");
        checkButton.Click += async (_, _) =>
        {
            checkButton.IsEnabled = false;
            checkButton.Content = "Checking…";
            _serviceChecks[service.Id] = await CheckServiceAsync(service);
            redraw();
        };

        return check is null
            ? Row("Status", "Saved. Check makes a free call to make sure it works.", Status("Set up", true), checkButton)
            : Row("Status", check.Detail, Status(check.Works ? "Connected" : "Not working", check.Works), checkButton);
    }

    private Task<ServiceCheck> CheckServiceAsync(ThirdPartyService service)
    {
#if DEBUG
        if (Headless.Active) return Task.FromResult(new ServiceCheck(false, "Not checked in headless runs."));
#endif
        return service.ViaCodex
            ? CheckCodexImagesAsync(service)
            : Services.CheckAsync(service);
    }

    /// <summary>The Codex route works when the chosen login is signed in with ChatGPT.</summary>
    private async Task<ServiceCheck> CheckCodexImagesAsync(ThirdPartyService service)
    {
        var id = Services.Read(service.Id, "login");
        var login = Providers.List().FirstOrDefault(provider => provider.Id == id);
        if (login is null) return new ServiceCheck(false, "That Codex login was removed. Pick another.");
        var status = await Providers.CheckAsync(login);
        return status.SignedIn
            ? new ServiceCheck(true, $"{login.Label} is signed in · {status.Detail}")
            : new ServiceCheck(false, $"{login.Label} isn't signed in. Sign in under Providers.");
    }

    private FrameworkElement ServiceFieldRow(ThirdPartyService service, ServiceField field, Action redraw)
    {
        // The ChatGPT route picks one of the Codex logins rather than taking a key.
        if (service.ViaCodex)
        {
            var logins = Providers.List().Where(provider => provider.Kind == ProviderKind.Codex).ToList();
            if (logins.Count == 0) return Row(field.Label, "Add a Codex login under Providers first.");
            var current = logins.FirstOrDefault(provider => provider.Id == Services.Read(service.Id, field.Key));
            const string none = "None";
            var labels = logins.Select(provider => provider.Label).Prepend(none).ToArray();
            var choice = Choice(labels, current?.Label ?? none, label =>
            {
                Services.Write(service.Id, field.Key, logins.FirstOrDefault(provider => provider.Label == label)?.Id ?? "");
                _serviceChecks.Remove(service.Id);
                redraw();
            });
            AutomationProperties.SetName(choice, field.Label);
            return Row(field.Label, field.Hint, choice);
        }

        if (field.Options is { } options)
        {
            var picked = Services.Read(service.Id, field.Key);
            if (picked is null)
            {
                // A choice always has an answer, so the service isn't left half set up for want of one.
                picked = options[0];
                Services.Write(service.Id, field.Key, picked);
            }
            var choice = Choice(options, picked, value =>
            {
                Services.Write(service.Id, field.Key, value);
                _serviceChecks.Remove(service.Id);
                redraw();
            });
            AutomationProperties.SetName(choice, field.Label);
            return Row(field.Label, field.Hint, choice);
        }

        var saved = Services.Read(service.Id, field.Key);
        if (saved is { Length: > 0 } && field.Secret)
        {
            var change = ActionButton("Change");
            AutomationProperties.SetName(change, $"Change {field.Label}");
            var row = Row(field.Label, field.Hint, Value(Masked(saved)), change);
            change.Click += (_, _) =>
            {
                var index = ((StackPanel)row.Parent).Children.IndexOf(row);
                var parent = (StackPanel)row.Parent;
                parent.Children.RemoveAt(index);
                parent.Children.Insert(index, ServiceEntryRow(service, field, redraw, editing: true));
            };
            return row;
        }

        return ServiceEntryRow(service, field, redraw, editing: false);
    }

    private FrameworkElement ServiceEntryRow(ThirdPartyService service, ServiceField field, Action redraw, bool editing)
    {
        var save = ActionButton("Save");
        AutomationProperties.SetName(save, $"Save {field.Label}");
        Control input;
        Func<string> text;
        if (field.Secret)
        {
            var box = new PasswordBox { Style = (Style)FindResource("SecretField"), Width = 240 };
            input = box;
            text = () => box.Password.Trim();
        }
        else
        {
            var box = Field(Services.Read(service.Id, field.Key) ?? "");
            box.Width = 240;
            input = box;
            text = () => box.Text.Trim();
        }
        AutomationProperties.SetName(input, field.Label);

        void Save()
        {
            var value = text();
            if (value.Length == 0 && !editing) return;
            if (value.Length > 0) Services.Write(service.Id, field.Key, value);
            _serviceChecks.Remove(service.Id);
            redraw();
        }

        save.Click += (_, _) => Save();
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Save();
            if (e.Key == Key.Escape && editing)
            {
                e.Handled = true;
                redraw();
            }
        };

        var row = Row(field.Label, field.Hint, input, save);
        if (editing) input.Loaded += (_, _) => input.Focus();
        return row;
    }

    /// <summary>Enough of a key to tell which one it is, never the whole thing.</summary>
    private static string Masked(string secret) =>
        secret.Length <= 8 ? new string('•', secret.Length) : $"{secret[..4]}••••{secret[^4..]}";

    private static Button LinkButton(string label, string address)
    {
        var button = ActionButton(label);
        button.ToolTip = address;
        button.Click += (_, _) =>
        {
#if DEBUG
            if (Headless.Active) return; // It would open a browser on someone's screen.
#endif
            Process.Start(new ProcessStartInfo(address) { UseShellExecute = true })?.Dispose();
        };
        return button;
    }
}
