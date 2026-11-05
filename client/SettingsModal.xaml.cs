using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AgentOrange;

/// <summary>
/// A mock of the settings screen. Everything here is placeholder content: only
/// moving between pages works, and nothing is saved.
/// </summary>
public partial class SettingsModal : UserControl
{
    private readonly record struct Page(string Name, string Icon, Func<UIElement[]> Build);

    private ServerConnection _server = null!;
    private SyncEngine _sync = null!;
    private Action? _refreshServerPage;

    public event EventHandler? Closed;

    public DeviceSession Devices { get; set; } = null!;

    public SyncEngine Sync
    {
        get => _sync;
        set
        {
            _sync = value;
            _sync.StatusChanged += () => _refreshServerPage?.Invoke();
        }
    }

    public ServerConnection Server
    {
        get => _server;
        set
        {
            _server = value;
            _server.StateChanged += () => _refreshServerPage?.Invoke();
        }
    }

    public SettingsModal()
    {
        InitializeComponent();

        var groups = new (string? Title, Page[] Pages)[]
        {
            (null,
            [
                new("General", "", GeneralPage),
            ]),
            ("Agents",
            [
                new("Providers", "", AgentProvidersPage),
                new("Approvals", "", ApprovalsPage),
                new("Third Party", "", ThirdPartyPage),
            ]),
            ("Editors",
            [
                new("Engines", "", EnginesPage),
                new("Tools", "", ToolsPage),
            ]),
            ("Sync",
            [
                new("Server", "", ServerPage),
                new("Local data", "", LocalDataPage),
            ]),
            (null,
            [
                new("About", "", AboutPage),
            ]),
        };

        RadioButton? first = null;
        foreach (var (title, pages) in groups)
        {
            if (title is not null) NavList.Children.Add(GroupLabel(title));
            else if (NavList.Children.Count > 0) NavList.Children.Add(new Border { Height = 12 });

            foreach (var page in pages)
            {
                var item = NavItem(page);
                first ??= item;
                NavList.Children.Add(item);
            }
        }
        first!.IsChecked = true;
    }

    private RadioButton NavItem(Page page)
    {
        var item = new RadioButton
        {
            Style = (Style)FindResource("NavItem"),
            GroupName = "SettingsPages",
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = page.Icon, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 14, Width = 26, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = page.Name },
                },
            },
        };
        item.Checked += (_, _) => ShowPage(page);
        return item;
    }

    private void ShowPage(Page page)
    {
        PageName.Text = page.Name;
        _refreshServerPage = null;
        PageContent.Children.Clear();
        foreach (var element in page.Build()) PageContent.Children.Add(element);
        PageScroller.ScrollToTop();
    }

    // Pages

    private UIElement[] GeneralPage() =>
    [
        Section("Updates"),
        Card(
            Row("Current version", "Updates are required, and they install when you choose Download update.", Value("0.1.9")),
            Row("Check for updates", "Checks on launch and every minute.", Action("Check now"))),
        Section("Notifications"),
        Card(
            Row("Agent needs you", "Alert when an agent is waiting on an answer or an approval.", Toggle(true)),
            Row("Agent finished", "Alert when an agent finishes its work.", Toggle(true)),
            Row("Generation finished", "Alert when a generation run has results to review.", Toggle(true)),
            Row("Notification sounds", "Play a sound along with the alert.", Toggle(false))),
    ];

    private sealed record AgentProvider(
        string Name, string Version, string Subscription, string Binary, string LoginFolder, bool On, bool AppManagesSignIn);

    private static readonly AgentProvider[] AgentProviders =
    [
        new("Claude Code", "v2.1.318", "Claude Max subscription", "claude", "~/.claude-agent-orange", true, false),
        new("Codex", "v0.166.0", "ChatGPT Plus subscription", "codex", "~/.codex-agent-orange", true, true),
    ];

    private UIElement[] AgentProvidersPage()
    {
        var details = new ContentControl();
        var list = new StackPanel();
        RadioButton? first = null;

        foreach (var provider in AgentProviders)
        {
            var item = AgentProviderItem(provider);
            item.Checked += (_, _) => details.Content = AgentProviderDetails(provider);
            first ??= item;
            list.Children.Add(item);
        }
        first!.IsChecked = true;

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
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(262) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var listPanel = new Border { BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(0, 0, 1, 0), Child = list };
        Grid.SetColumn(details, 1);
        layout.Children.Add(listPanel);
        layout.Children.Add(details);

        return
        [
            Section("Providers"),
            new Border { BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = layout },
        ];
    }

    private RadioButton AgentProviderItem(AgentProvider provider)
    {
        var name = new StackPanel { Orientation = Orientation.Horizontal };
        name.Children.Add(new TextBlock { Text = provider.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextStrong") });
        name.Children.Add(new TextBlock { Text = provider.Version, FontSize = 11, FontFamily = new FontFamily("Consolas"), Foreground = (Brush)FindResource("TextNormal"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });

        var text = new StackPanel { Margin = new Thickness(12, 0, 8, 0) };
        text.Children.Add(name);
        text.Children.Add(new TextBlock { Text = $"Signed in · {provider.Subscription}", FontSize = 12.5, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });

        var icon = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 15, Foreground = (Brush)FindResource("TextNormal") };
        var toggle = Toggle(provider.On);
        toggle.VerticalAlignment = VerticalAlignment.Center;

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(toggle, 2);
        row.Children.Add(icon);
        row.Children.Add(text);
        row.Children.Add(toggle);

        return new RadioButton { Style = (Style)FindResource("ListItem"), Content = row };
    }

    private StackPanel AgentProviderDetails(AgentProvider provider)
    {
        var header = new DockPanel { Margin = new Thickness(16, 14, 16, 12) };
        var version = new TextBlock { Text = provider.Version, FontSize = 11, FontFamily = new FontFamily("Consolas"), Foreground = (Brush)FindResource("TextNormal"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(version, Dock.Right);
        header.Children.Add(version);
        header.Children.Add(new TextBlock { Text = provider.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextStrong") });

        return new StackPanel
        {
            Children =
            {
                header,
                Divider(),
                provider.AppManagesSignIn
                    ? Row("Status", $"Signed in · {provider.Subscription}", Status("Connected", true), Action("Sign out"))
                    : Row("Status", $"Signed in · {provider.Subscription}", Status("Connected", true)),
                Divider(),
                Row("Display name", null, Field(provider.Name)),
                Divider(),
                Row("Program", "The command used to start this agent.", Field(provider.Binary)),
                Divider(),
                Row("Login folder", "Keeps this login separate from your normal setup.", Field(provider.LoginFolder)),
            },
        };
    }

    private UIElement[] ApprovalsPage() =>
    [
        Section("Agent actions"),
        Card(
            Row("Default mode", "How much an agent can do without asking.", Dropdown("Ask for risky actions")),
            Row("File edits", "Inside the current project.", Dropdown("Allow")),
            Row("Shell commands", "Each command is shown step by step.", Dropdown("Ask")),
            Row("Web access", "Fetching pages from the internet.", Dropdown("Ask")),
            Row("Agents can start editors", "Let an agent open an editor that isn't running.", Toggle(false)),
            Row("Paid generation", "Every paid run needs a fresh yes.", Toggle(true, enabled: false))),
    ];

    private UIElement[] ThirdPartyPage() =>
    [
        Section("Image"),
        Card(
            Row("OpenAI", "Image generation. Keys stay on the server and never reach agents.", Status("Key saved", true), Action("Change key"))),
        Section("3D"),
        Card(
            Row("Tencent Hunyuan 3D", "Image to 3D model generation.", Status("No key", false), Action("Add key"))),
        Section("More"),
        Card(
            Row("Add a provider", "Connect another generation service.", Action("Add"))),
    ];

    private UIElement[] EnginesPage() =>
    [
        Section("Engines"),
        Card(
            Row("Unreal Engine", "localhost:8666", Status("Connected", true), Action("Configure")),
            Row("Godot", "localhost:8645", Status("Not running", false), Action("Configure"))),
    ];

    private UIElement[] ToolsPage() =>
    [
        Section("Tools"),
        Card(
            Row("Blender", "localhost:9876", Status("Connected", true), Action("Configure")),
            Row("Aseprite", "localhost:8650", Status("Not running", false), Action("Configure"))),
    ];

    private UIElement[] ServerPage()
    {
        var host = new StackPanel();
        _ = FillServerPageAsync(host);
        return [host];
    }

    private async Task FillServerPageAsync(StackPanel host)
    {
        _refreshServerPage = null;
        host.Children.Clear();

        if (Server.Address.Length == 0 || !Devices.IsPaired)
        {
            ShowConnectForm(host);
            return;
        }

        var address = Field(Server.Address);
        address.Width = 300;
        var save = Action("Save");
        save.Visibility = Visibility.Collapsed;
        var problem = ProblemText();

        async Task SaveAddress()
        {
            if (!ServerConnection.TryCleanAddress(address.Text, out var cleaned) || cleaned.Length == 0)
            {
                ShowProblem(problem, "Enter a full https:// address.");
                return;
            }

            await Server.SetAddressAsync(cleaned);
            await FillServerPageAsync(host);
        }

        address.TextChanged += (_, _) =>
            save.Visibility = address.Text == Server.Address ? Visibility.Collapsed : Visibility.Visible;
        address.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) await SaveAddress();
        };
        save.Click += async (_, _) => await SaveAddress();

        var statusHost = new ContentControl();
        _refreshServerPage = () => statusHost.Content = ServerStatusView();
        _refreshServerPage();

        var reconnect = Action("Reconnect");
        reconnect.Click += async (_, _) => await Server.ReconnectAsync();

        var devicesHost = new StackPanel();
        host.Children.Add(Section("Connection"));
        host.Children.Add(Card(
            Row("Server address", "The URL of your Agent Orange server.", address, save),
            Row("Status", null, statusHost, reconnect)));
        host.Children.Add(devicesHost);
        host.Children.Add(problem);

        var lastSynced = Value("");
        var waiting = Value("");
        var previousRefresh = _refreshServerPage;
        _refreshServerPage = () =>
        {
            previousRefresh();
            lastSynced.Text = LastSyncedLabel();
            waiting.Text = Sync.WaitingToSync.ToString();
        };
        _refreshServerPage();

        var syncNow = Action("Sync");
        syncNow.Click += async (_, _) => await Sync.SyncNowAsync();

        host.Children.Add(Section("Sync"));
        host.Children.Add(Card(
            Row("Last synced", "Everything works offline and syncs when it can.", lastSynced),
            Row("Waiting to sync", "Changes made on this PC that the server doesn't have yet.", waiting),
            Row("Sync now", "Send and receive changes now.", syncNow)));

        var devices = await Devices.ListAsync();

        // The server turned this login down, because this PC was removed.
        if (!Devices.IsPaired)
        {
            await FillServerPageAsync(host);
            return;
        }

        if (devices is null)
        {
            var retry = Action("Try again");
            retry.Click += async (_, _) => await FillServerPageAsync(host);
            devicesHost.Children.Add(Section("Devices"));
            devicesHost.Children.Add(Card(Row("Can't reach the server", "Devices can't be shown right now.", retry)));
            return;
        }

        var thisDevice = devices.First(device => device.Current);
        devicesHost.Children.Add(Section("This device"));
        devicesHost.Children.Add(Card(
            RenameRow(thisDevice, problem),
            SignOutRow(thisDevice, host, problem)));
        devicesHost.Children.Add(Section("Other devices"));

        var others = devices.Where(device => !device.Current).ToList();
        devicesHost.Children.Add(others.Count == 0
            ? Card(Row("No other devices", "To add one, install Agent Orange on it and enter this server's pairing key."))
            : Card(others.Select(device => Row(device.Name, device.LastSeenLabel, RemoveButton(device, host, problem))).ToArray<FrameworkElement>()));
    }

    private string LastSyncedLabel()
    {
        if (Sync.LastSynced is not { } synced) return "Not yet";
        var ago = DateTimeOffset.UtcNow - synced;
        return ago.TotalMinutes < 1 ? "Just now" : ago.TotalHours < 1 ? $"{(int)ago.TotalMinutes} min ago" : $"{(int)ago.TotalHours} hr ago";
    }

    private StackPanel ServerStatusView() => Server.State switch
    {
        ServerState.Connected => Status("Connected", true),
        ServerState.Connecting => Status("Connecting…", false),
        ServerState.Disconnected => Status("Can't reach the server", false),
        _ => Status("No server set", false),
    };

    private void ShowConnectForm(StackPanel host)
    {
        var address = Field(Server.Address);
        address.Width = 300;
        var key = new PasswordBox { Style = (Style)FindResource("SecretField") };
        var name = Field(Environment.MachineName);
        name.Width = 300;
        var connect = Action("Connect");
        var problem = ProblemText();

        async Task ConnectNow()
        {
            if (!ServerConnection.TryCleanAddress(address.Text, out var cleaned) || cleaned.Length == 0)
            {
                ShowProblem(problem, "Enter a full https:// address.");
                return;
            }

            if (key.Password.Length == 0 || string.IsNullOrWhiteSpace(name.Text))
            {
                ShowProblem(problem, "Enter the pairing key and a name for this PC.");
                return;
            }

            problem.Visibility = Visibility.Collapsed;
            address.Text = cleaned;
            connect.IsEnabled = false;
            if (cleaned != Server.Address) await Server.SetAddressAsync(cleaned);
            var result = await Devices.PairAsync(key.Password, name.Text);
            connect.IsEnabled = true;

            switch (result)
            {
                case PairResult.Paired:
                    key.Clear();
                    await FillServerPageAsync(host);
                    break;
                case PairResult.WrongKey:
                    ShowProblem(problem, "That pairing key isn't right.");
                    break;
                case PairResult.TooManyTries:
                    ShowProblem(problem, "Too many wrong keys. Wait a minute and try again.");
                    break;
                case PairResult.PairingOff:
                    ShowProblem(problem, "This server doesn't have a pairing key yet.");
                    break;
                default:
                    ShowProblem(problem, "Can't reach the server.");
                    break;
            }
        }

        KeyEventHandler connectOnEnter = async (_, e) =>
        {
            if (e.Key == Key.Enter) await ConnectNow();
        };
        address.KeyDown += connectOnEnter;
        key.KeyDown += connectOnEnter;
        connect.Click += async (_, _) => await ConnectNow();

        host.Children.Add(Section("Connect to a server"));
        host.Children.Add(Card(
            Row("Server address", "The URL of your Agent Orange server.", address),
            Row("Pairing key", "From your server. Each PC only needs it once.", key),
            Row("Device name", "Shown to your other devices.", name),
            Row("Connect", "Pair this PC with the server.", connect)));
        host.Children.Add(problem);
    }

    private Grid RenameRow(DeviceInfo device, TextBlock problem)
    {
        var name = Field(device.Name);
        var save = Action("Save");
        save.Visibility = Visibility.Collapsed;
        var savedName = device.Name;

        async Task SaveName()
        {
            if (string.IsNullOrWhiteSpace(name.Text) || name.Text == savedName) return;
            if (await Devices.RenameAsync(device.Id, name.Text))
            {
                savedName = name.Text.Trim();
                name.Text = savedName;
                save.Visibility = Visibility.Collapsed;
                problem.Visibility = Visibility.Collapsed;
            }
            else
            {
                ShowProblem(problem, "Couldn't rename this PC.");
            }
        }

        name.TextChanged += (_, _) => save.Visibility = name.Text == savedName ? Visibility.Collapsed : Visibility.Visible;
        name.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) await SaveName();
        };
        save.Click += async (_, _) => await SaveName();
        return Row("Device name", "Shown to your other devices.", name, save);
    }

    private Grid SignOutRow(DeviceInfo device, StackPanel host, TextBlock problem) =>
        Row("Sign out", "This PC will need the pairing key to reconnect.", ConfirmButton("Sign out", device, host, problem));

    private Button RemoveButton(DeviceInfo device, StackPanel host, TextBlock problem) =>
        ConfirmButton("Remove", device, host, problem);

    // Takes two clicks, because the device then needs the pairing key again.
    private Button ConfirmButton(string label, DeviceInfo device, StackPanel host, TextBlock problem)
    {
        var button = Action(label);
        var confirming = false;
        button.Click += async (_, _) =>
        {
            if (!confirming)
            {
                confirming = true;
                button.Content = "Are you sure?";
                return;
            }

            if (await Devices.RemoveAsync(device))
            {
                await FillServerPageAsync(host);
            }
            else
            {
                confirming = false;
                button.Content = label;
                ShowProblem(problem, "Couldn't reach the server.");
            }
        };
        return button;
    }

    private static TextBlock ProblemText() => new()
    {
        FontSize = 12.5,
        Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0x6B, 0x6B)),
        Margin = new Thickness(16, 8, 0, 0),
        Visibility = Visibility.Collapsed,
    };

    private static void ShowProblem(TextBlock problem, string message)
    {
        problem.Text = message;
        problem.Visibility = Visibility.Visible;
    }

    private UIElement[] LocalDataPage() =>
    [
        Section("On this PC"),
        Card(
            Row("Database", "Your offline copy of every project. 148 MB.", Field(@"%APPDATA%\AgentOrange"), Action("Change")),
            Row("Downloads", "Images, models and other files kept for offline use. 2.1 GB.", Field(@"%APPDATA%\AgentOrange\downloads"), Action("Change"), Action("Clear"))),
    ];

    private UIElement[] AboutPage() =>
    [
        Section("Agent Orange"),
        Card(
            Row("Client version", null, Value("0.1.9")),
            Row("Server version", null, Value("0.1.9"))),
        Section("Help"),
        Card(
            Row("Logs", "Open the folder with the app's logs.", Action("Open folder"))),
    ];

    // Building blocks

    private TextBlock GroupLabel(string title) => new()
    {
        Text = title,
        FontSize = 12,
        FontWeight = FontWeights.Medium,
        Foreground = (Brush)FindResource("TextMuted"),
        Margin = new Thickness(10, 14, 0, 4),
    };

    private TextBlock Section(string title) => new()
    {
        Text = title,
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
        Foreground = (Brush)FindResource("TextStrong"),
        Margin = new Thickness(16, 24, 0, 10),
    };

    private Border Card(params FrameworkElement[] rows)
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

    private Grid Row(string title, string? description, params FrameworkElement[] controls)
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

    private Border Divider() => new() { Height = 1, Background = (Brush)FindResource("Line") };

    private TextBox Field(string value) => new()
    {
        Style = (Style)FindResource("Field"),
        Text = value,
    };

    private CheckBox Toggle(bool on, bool enabled = true) => new()
    {
        Style = (Style)FindResource("Toggle"),
        IsChecked = on,
        IsEnabled = enabled,
    };

    private Button Dropdown(string value) => new()
    {
        Style = (Style)FindResource("ControlButton"),
        MinWidth = 120,
        Content = new DockPanel
        {
            LastChildFill = false,
            Children =
            {
                new TextBlock { Text = value, Margin = new Thickness(0, 0, 14, 0) },
                DockedRight(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center }),
            },
        },
    };

    private static TextBlock DockedRight(TextBlock text)
    {
        DockPanel.SetDock(text, Dock.Right);
        return text;
    }

    private Button Action(string label) => new()
    {
        Style = (Style)FindResource("ControlButton"),
        Content = label,
    };

    private TextBlock Value(string value) => new()
    {
        Text = value,
        FontSize = 13,
        FontWeight = FontWeights.Medium,
        Foreground = (Brush)FindResource("TextNormal"),
    };

    private StackPanel Status(string label, bool good) => new()
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

    // Closing

    private void Close_Click(object sender, RoutedEventArgs e) => Closed?.Invoke(this, EventArgs.Empty);

    private void Modal_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (WizardLayer.Visibility == Visibility.Visible) CloseWizard();
        else Closed?.Invoke(this, EventArgs.Empty);
    }

    private void Backdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Closed?.Invoke(this, EventArgs.Empty);

    // Clicks on the modal itself shouldn't reach the backdrop behind it.
    private void Modal_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
