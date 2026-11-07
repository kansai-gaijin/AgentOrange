using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>The settings screen. Third Party is still a placeholder, until there are keys to test with.</summary>
public partial class SettingsModal : UserControl
{
    private readonly record struct Page(string Name, string Icon, Func<UIElement[]> Build, Action? Reset = null);

    private readonly Dictionary<string, Page> _pages = [];
    private readonly Dictionary<string, RadioButton> _navItems = [];
    private const int MaxDeviceNameLength = 64; // The server refuses longer ones.

    private ServerConnection _server = null!;
    private DeviceSession _devices = null!;
    private bool _pairing; // The form finishes the job, once the address has moved too.
    private SyncEngine _sync = null!;
    private EditorMonitor _editors = null!;
    private Action? _refreshServerPage;
    private Action? _refreshEditorsPage;
    private Page _currentPage;
    private bool _confirmingRestore;

    public event EventHandler? Closed;

    public AppFolders Folders { get; set; } = null!;

    public LocalDatabase Database { get; set; } = null!;

    public DeviceSession Devices
    {
        get => _devices;
        set
        {
            _devices = value;

            // The server can turn this login down at any time, and then the page has to offer pairing.
            _devices.PairingChanged += () =>
            {
                if (_currentPage.Name == "Server" && !_pairing) ShowPage(_currentPage);
            };
        }
    }

    public AgentProviders Providers { get; set; } = null!;

    public string AppVersion { get; set; } = "";

    public Func<Task<string>> CheckForUpdates { get; set; } = null!;

    public EditorMonitor Editors
    {
        get => _editors;
        set
        {
            _editors = value;
            _editors.Changed += () => _refreshEditorsPage?.Invoke();
        }
    }

    public SyncEngine Sync
    {
        get => _sync;
        set
        {
            _sync = value;
            _sync.StatusChanged += () => _refreshServerPage?.Invoke();

            // Another PC changed the approvals, so show them.
            _sync.RecordsChanged += () =>
            {
                if (_currentPage.Name == "Approvals") ShowPage(_currentPage);
            };
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
                new("General", "", GeneralPage, ResetGeneral),
            ]),
            ("Agents",
            [
                new("Providers", "", AgentProvidersPage),
                new("Approvals", "", ApprovalsPage, () => SaveApprovals(DefaultApprovals)),
                new("Third Party", "", ThirdPartyPage),
            ]),
            ("Editors",
            [
                new("Engines", "", EnginesPage, () => Editors.ResetToDefaults(true)),
                new("Tools", "", ToolsPage, () => Editors.ResetToDefaults(false)),
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

        foreach (var (title, pages) in groups)
        {
            if (title is not null) NavList.Children.Add(GroupLabel(title));
            else if (NavList.Children.Count > 0) NavList.Children.Add(new Border { Height = 12 });

            foreach (var page in pages)
            {
                var item = NavItem(page);
                _pages[page.Name] = page;
                _navItems[page.Name] = item;
                NavList.Children.Add(item);
            }
        }
    }

    /// <summary>The server page's counts can change without anything here hearing about it, such as from another window.</summary>
    public void RefreshServerPage() => _refreshServerPage?.Invoke();

    /// <summary>Shows the first page. Call once everything the pages need has been set.</summary>
    public void Start() => _navItems["General"].IsChecked = true;

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
        AutomationProperties.SetName(item, page.Name);
        item.Checked += (_, _) => ShowPage(page);
        return item;
    }

    private void ShowPage(Page page)
    {
        _currentPage = page;
        PageName.Text = page.Name;
        _refreshServerPage = null;
        _refreshEditorsPage = null;
        _refreshProviderDetails = null;
        _confirmingRestore = false;
        RestoreLabel.Text = "Restore defaults";
        RestoreDefaults.Visibility = page.Reset is null ? Visibility.Collapsed : Visibility.Visible;
        PageContent.Children.Clear();
        foreach (var element in page.Build()) PageContent.Children.Add(element);
        PageScroller.ScrollToTop();
    }

    // Pages

    private UIElement[] GeneralPage()
    {
        var checkResult = Value("");
        var check = ActionButton("Check now");
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            checkResult.Text = "Checking…";
            checkResult.Text = await CheckForUpdates();
            check.IsEnabled = true;
        };

        return
        [
            Section("Updates"),
            Card(
                Row("Current version", "Updates are required, and they install when you choose Download update.", Value(AppVersion)),
                Row("Check for updates", "Checks on launch and every minute.", checkResult, check)),
            Section("Notifications"),
            Card(
                Row("Agent needs you", "Alert when an agent is waiting on an answer or an approval.", SettingToggle("notify_agent_needs_you", true)),
                Row("Agent finished", "Alert when an agent finishes its work.", SettingToggle("notify_agent_finished", true)),
                Row("Generation finished", "Alert when a generation run has results to review.", SettingToggle("notify_generation_finished", true)),
                Row("Notification sounds", "Play a sound along with the alert.", SettingToggle("notify_sounds", false))),
        ];
    }

    private void ResetGeneral()
    {
        foreach (var key in new[] { "notify_agent_needs_you", "notify_agent_finished", "notify_generation_finished", "notify_sounds" })
        {
            Database.SetSetting(key, "");
        }
    }

    private CheckBox SettingToggle(string key, bool defaultValue)
    {
        var stored = Database.GetSetting(key);
        var toggle = Toggle(string.IsNullOrEmpty(stored) ? defaultValue : stored == "1");
        toggle.Checked += (_, _) => Database.SetSetting(key, "1");
        toggle.Unchecked += (_, _) => Database.SetSetting(key, "0");
        return toggle;
    }

    private sealed record ApprovalSettings(string DefaultMode, string FileEdits, string ShellCommands, string WebAccess, bool AgentsCanStartEditors);

    private static readonly ApprovalSettings DefaultApprovals = new("Ask for risky actions", "Allow", "Ask", "Ask", false);
    private static readonly string[] ApprovalModes = ["Ask for everything", "Ask for risky actions", "Allow everything"];
    private static readonly string[] ApprovalChoices = ["Ask", "Allow", "Block"];
    private static readonly JsonSerializerOptions ApprovalJson = new(JsonSerializerDefaults.Web);

    private ApprovalSettings LoadApprovals()
    {
        var stored = Database.GetRecord("settings", "approvals");
        if (string.IsNullOrEmpty(stored)) return DefaultApprovals;
        try
        {
            return JsonSerializer.Deserialize<ApprovalSettings>(stored, ApprovalJson) ?? DefaultApprovals;
        }
        catch (JsonException error)
        {
            AppLog.Write($"Skipped unreadable approval settings: {error.Message}");
            return DefaultApprovals;
        }
    }

    // Approvals sync, because they're how agents should behave on every PC.
    private void SaveApprovals(ApprovalSettings approvals)
    {
        Database.SaveRecord("settings", "approvals", JsonSerializer.Serialize(approvals, ApprovalJson));
        _ = Sync.SyncNowAsync();
    }

    private UIElement[] ApprovalsPage()
    {
        var approvals = LoadApprovals();
        var startEditors = Toggle(approvals.AgentsCanStartEditors);
        startEditors.Checked += (_, _) => SaveApprovals(LoadApprovals() with { AgentsCanStartEditors = true });
        startEditors.Unchecked += (_, _) => SaveApprovals(LoadApprovals() with { AgentsCanStartEditors = false });

        return
        [
            Section("Agent actions"),
            Card(
                Row("Default mode", "How much an agent can do without asking.", Choice(ApprovalModes, approvals.DefaultMode, value => SaveApprovals(LoadApprovals() with { DefaultMode = value }))),
                Row("File edits", "Inside the current project.", Choice(ApprovalChoices, approvals.FileEdits, value => SaveApprovals(LoadApprovals() with { FileEdits = value }))),
                Row("Shell commands", "Each command is shown step by step.", Choice(ApprovalChoices, approvals.ShellCommands, value => SaveApprovals(LoadApprovals() with { ShellCommands = value }))),
                Row("Web access", "Fetching pages from the internet.", Choice(ApprovalChoices, approvals.WebAccess, value => SaveApprovals(LoadApprovals() with { WebAccess = value }))),
                Row("Agents can start editors", "Let an agent open an editor that isn't running.", startEditors),
                Row("Paid generation", "Every paid run needs a fresh yes.", Toggle(true, enabled: false))),
        ];
    }

    private UIElement[] ThirdPartyPage() =>
    [
        Section("Image"),
        Card(
            Row("OpenAI", "Image generation. Keys stay on the server and never reach agents.", Status("Key saved", true), ActionButton("Change key"))),
        Section("3D"),
        Card(
            Row("Tencent Hunyuan 3D", "Image to 3D model generation.", Status("No key", false), ActionButton("Add key"))),
        Section("More"),
        Card(
            Row("Add a provider", "Connect another generation service.", ActionButton("Add"))),
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

        var address = Field(Server.ChosenAddress);
        address.Width = 300;
        var save = ActionButton("Save");
        save.Visibility = Visibility.Collapsed;
        var problem = ProblemText();
        if (Server.Address != Server.ChosenAddress) ShowProblem(problem, $"That address isn't answering, so this PC is using {Server.Address} for now.");

        async Task SaveAddress()
        {
            if (!ServerConnection.TryCleanAddress(address.Text, out var cleaned) || cleaned.Length == 0)
            {
                ShowProblem(problem, "Enter a full https:// address.");
                return;
            }

            if (cleaned == Server.ChosenAddress) return;

            // Only moves once there's a login at the new address, so a typo can't cut this PC off.
            if (Devices.HasLogin(cleaned))
            {
                await Server.SetAddressAsync(cleaned);
                ShowPage(_currentPage);
                return;
            }

            _refreshServerPage = null;
            host.Children.Clear();
            ShowConnectForm(host, cleaned);
        }

        address.TextChanged += (_, _) =>
            save.Visibility = address.Text == Server.ChosenAddress ? Visibility.Collapsed : Visibility.Visible;
        address.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) await SaveAddress();
        };
        save.Click += async (_, _) => await SaveAddress();

        var statusHost = new ContentControl();
        _refreshServerPage = () => statusHost.Content = ServerStatusView();
        _refreshServerPage();

        var reconnect = ActionButton("Reconnect");
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
        var refused = Value("");
        var previousRefresh = _refreshServerPage;
        _refreshServerPage = () =>
        {
            previousRefresh();
            lastSynced.Text = LastSyncedLabel();
            waiting.Text = Sync.WaitingToSync.ToString();
            refused.Text = Sync.RefusedChanges.ToString();
        };
        _refreshServerPage();

        var syncNow = ActionButton("Sync");
        syncNow.Click += async (_, _) => await Sync.SyncNowAsync();

        host.Children.Add(Section("Sync"));
        host.Children.Add(Card(
            Row("Last synced", "Everything works offline and syncs when it can.", lastSynced),
            Row("Waiting to sync", "Changes made on this PC that the server doesn't have yet.", waiting),
            Row("Refused by the server", "Changes it wouldn't take, which stay on this PC. The log says which. They're tried again each time the app starts.", refused),
            Row("Sync now", "Send and receive changes now.", syncNow)));

        var devices = await Devices.ListAsync();

        // The server turned this login down, because this PC was removed.
        if (!Devices.IsPaired)
        {
            await FillServerPageAsync(host);
            return;
        }

        // This PC being missing means it was removed a moment ago. Trying again finds that out.
        if (devices is null || !devices.Any(device => device.Current))
        {
            var retry = ActionButton("Try again");
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
        // Whichever is behind updates itself soon, and changes wait on this PC until then.
        if (Devices.VersionsDiffer) return "Paused until this PC and the server are on the same version";
        if (Sync.OtherWindowSyncs) return "Another window of the app is syncing";
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

    private void ShowConnectForm(StackPanel host, string? typedAddress = null)
    {
        var address = Field(typedAddress ?? Server.Address);
        address.Width = 300;
        var key = new PasswordBox { Style = (Style)FindResource("SecretField") };
        var name = Field(Environment.MachineName);
        name.Width = 300;
        name.MaxLength = MaxDeviceNameLength;
        var connect = ActionButton("Connect");
        var problem = ProblemText();
        var connecting = false;

        // Locked while pairing, so what's on screen is what the login gets saved under.
        void SetConnecting(bool value)
        {
            connecting = value;
            _pairing = value;
            address.IsEnabled = !value;
            key.IsEnabled = !value;
            name.IsEnabled = !value;
            connect.IsEnabled = !value;
        }

        async Task ConnectNow()
        {
            if (connecting) return;

            if (!ServerConnection.TryCleanAddress(address.Text, out var cleaned) || cleaned.Length == 0)
            {
                ShowProblem(problem, "Enter a full https:// address.");
                return;
            }

            // Already paired there, so the key isn't needed to go back.
            if (Devices.HasLogin(cleaned))
            {
                if (cleaned != Server.Address) await Server.SetAddressAsync(cleaned);
                ShowPage(_currentPage);
                return;
            }

            if (key.Password.Length == 0 || string.IsNullOrWhiteSpace(name.Text))
            {
                ShowProblem(problem, "Enter the pairing key and a name for this PC.");
                return;
            }

            problem.Visibility = Visibility.Collapsed;
            address.Text = cleaned;
            SetConnecting(true);
            var result = await Devices.PairAsync(cleaned, key.Password, name.Text);
            if (result == PairResult.Paired && cleaned != Server.Address) await Server.SetAddressAsync(cleaned);
            SetConnecting(false);

            switch (result)
            {
                case PairResult.Paired:
                    key.Clear();
                    ShowPage(_currentPage);
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

        if (typedAddress is not null && Devices.IsPaired)
        {
            var keep = ActionButton($"Keep using {Server.Address}");
            keep.HorizontalAlignment = HorizontalAlignment.Left;
            keep.Margin = new Thickness(16, 10, 0, 0);
            keep.Click += (_, _) => ShowPage(_currentPage);
            host.Children.Add(keep);
        }
        host.Children.Add(problem);
    }

    private Grid RenameRow(DeviceInfo device, TextBlock problem)
    {
        var name = Field(device.Name);
        name.MaxLength = MaxDeviceNameLength;
        var save = ActionButton("Save");
        save.Visibility = Visibility.Collapsed;
        var savedName = device.Name;

        async Task SaveName()
        {
            if (string.IsNullOrWhiteSpace(name.Text) || name.Text == savedName) return;

            // It may be edited again by the time this comes back, and only this was saved.
            var submitted = name.Text.Trim();
            if (await Devices.RenameAsync(device.Id, submitted))
            {
                savedName = submitted;
                problem.Visibility = Visibility.Collapsed;
                if (name.Text.Trim() != submitted) return;
                name.Text = submitted;
                save.Visibility = Visibility.Collapsed;
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
        var button = ActionButton(label);
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

    /// <summary>Asks before stopping long work that would be lost, saying what's about to stop it.</summary>
    public Func<string, bool> ConfirmStoppingWork { get; set; } = _ => true;

    public Action AppIsRestarting { get; set; } = () => { };

    private static bool AnotherWindowIsOpen()
    {
        using var current = Process.GetCurrentProcess();
        var path = Environment.ProcessPath;
        return Process.GetProcessesByName(current.ProcessName).Any(process =>
        {
            using (process)
            {
                try
                {
                    return process.Id != current.Id && process.MainModule?.FileName == path;
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    return false; // Someone else's, or just gone.
                }
            }
        });
    }

    private UIElement[] LocalDataPage()
    {
        var problem = ProblemText();

        var changeDatabase = ActionButton("Change");
        changeDatabase.Click += (_, _) =>
        {
            if (PickFolder(Folders.DatabaseFolder) is not { } folder
                || string.Equals(folder.TrimEnd('\\'), Folders.DatabaseFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // That window would keep using the old file, and whatever it saves would be lost.
            if (AnotherWindowIsOpen())
            {
                ShowProblem(problem, "Close Agent Orange's other windows first, as they're using this database.");
                return;
            }

            // Moving it restarts the app.
            if (!ConfirmStoppingWork("Moving the database")) return;

            if (Folders.MoveDatabase(folder) is { } failure)
            {
                ShowProblem(problem, failure);
                return;
            }

            // Everything open still points at the old database, so start fresh.
#if DEBUG
            if (Headless.Active)
            {
                Application.Current.Shutdown();
                return;
            }
#endif
            AppIsRestarting();
            Process.Start(Environment.ProcessPath!);
            Application.Current.Shutdown();
        };

        var openAgentFolder = ActionButton("Open");
        openAgentFolder.Click += (_, _) => OpenInExplorer(Folders.AgentFolder);

        var downloadsFolder = Folders.DownloadsFolder(Database);
        var downloadsPath = FolderField(downloadsFolder);
        var changeDownloads = ActionButton("Change");
        var downloadsRow = Row("Downloads", DownloadsDescription(downloadsFolder), downloadsPath, changeDownloads);
        changeDownloads.Click += (_, _) =>
        {
            if (PickFolder(Folders.DownloadsFolder(Database)) is not { } folder) return;
            Folders.SetDownloadsFolder(Database, folder);
            ShowPage(_currentPage);
        };

        return
        [
            Section("On this PC"),
            Card(
                Row("Database", $"Your offline copy of every project. {AppFolders.FormatSize(Folders.DatabaseSize)}.", FolderField(Folders.DatabaseFolder), changeDatabase),
                downloadsRow,
                Row("Agent files", "Tools and skills that load in agent sessions, and chat caches.", FolderField(Folders.AgentFolder), openAgentFolder)),
            problem,
        ];
    }

    private static void OpenInExplorer(string folder)
    {
#if DEBUG
        if (Headless.Active) return; // It would open a window on someone's screen.
#endif
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Process.Start(explorer, $"\"{folder}\"")?.Dispose();
    }

    private static string DownloadsDescription(string folder) =>
        $"Images, models and other files kept for offline use. {AppFolders.FormatSize(AppFolders.FolderSize(folder))}.";

    private TextBox FolderField(string folder)
    {
        var field = Field(folder);
        field.Width = 290;
        field.IsReadOnly = true;
        field.ToolTip = folder;
        return field;
    }

    private static string? PickFolder(string startIn) =>
        Dialogs.PickFolder("Choose a folder", Directory.Exists(startIn) ? startIn : null);

    private UIElement[] AboutPage()
    {
        var serverVersion = Value("Checking…");
        _ = ShowServerVersionAsync(serverVersion);

        var openLogs = ActionButton("Open folder");
        openLogs.Click += (_, _) =>
        {
            Directory.CreateDirectory(Folders.LogsFolder);
            OpenInExplorer(Folders.LogsFolder);
        };

        return
        [
            Section("Agent Orange"),
            Card(
                Row("Client version", null, Value(AppVersion)),
                Row("Server version", null, serverVersion)),
            Section("Help"),
            Card(
                Row("Logs", "Open the folder with the app's logs.", openLogs)),
        ];
    }

    private async Task ShowServerVersionAsync(TextBlock label)
    {
        var version = Devices.IsPaired ? await Devices.ServerVersionAsync() : null;
        label.Text = version ?? "Not connected";
    }

    // Closing

    private void Close_Click(object sender, RoutedEventArgs e) => Closed?.Invoke(this, EventArgs.Empty);

    // Takes two clicks, and only touches the page being shown.
    private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (!_confirmingRestore)
        {
            _confirmingRestore = true;
            RestoreLabel.Text = "Are you sure?";
            return;
        }

        _currentPage.Reset?.Invoke();
        ShowPage(_currentPage);
    }

    private void Modal_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (WizardLayer.Visibility == Visibility.Visible) CloseWizard();
        else if (SearchBox.Text.Length > 0) SearchBox.Clear();
        else Closed?.Invoke(this, EventArgs.Empty);
    }

    private void Backdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Closed?.Invoke(this, EventArgs.Empty);

    // Clicks on the modal itself shouldn't reach the backdrop behind it.
    private void Modal_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
