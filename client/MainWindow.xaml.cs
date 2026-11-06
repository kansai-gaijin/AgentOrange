using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Velopack;
using Velopack.Sources;

namespace AgentOrange;

public partial class MainWindow : Window
{
    private const string UpdateFeedUrl = "https://github.com/kansai-gaijin/AgentOrange/releases/latest/download";
    private static readonly Brush ConnectedBrush = new SolidColorBrush(Color.FromRgb(0x5F, 0xD0, 0x8A));
    private static readonly Brush DisconnectedBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x6B, 0x6B));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAA));

    private readonly UpdateManager _updates = new(new SimpleWebSource(UpdateFeedUrl));
    private readonly LocalDatabase _database;
    private readonly ServerConnection _server;
    private readonly DeviceSession _devices;
    private readonly SyncEngine _sync;

    private UpdateInfo? _requiredUpdate;
    private bool _downloading;

    public MainWindow()
    {
        InitializeComponent();
        VersionLabel.Text = _updates.IsInstalled ? $"v{_updates.CurrentVersion}" : "dev build";

        // Kept outside the install folder, which updates replace.
        var folders = new AppFolders(_updates.IsInstalled);
        if (!folders.DatabaseFolderExists)
        {
            var answer = MessageBox.Show(
                $"Agent Orange can't find its database folder:\n{folders.DatabaseFolder}\n\nIf it's on a drive that isn't connected, choose No, connect the drive and open Agent Orange again.\n\nChoose Yes to start again in the default folder. Anything already synced comes back from your server.",
                "Agent Orange",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) Environment.Exit(0);
            folders.UseDefaultDatabaseFolder();
        }

        AppLog.Start(folders.LogsFolder);
        AppLog.Write($"Started {(_updates.IsInstalled ? $"v{_updates.CurrentVersion}" : "dev build")}");

        _database = new LocalDatabase(folders.DatabasePath);
        Settings.Folders = folders;
        Settings.Database = _database;
        Settings.Providers = new AgentProviders(_database, folders.AgentFolder);
        Settings.Editors = new EditorMonitor(_database);
        Settings.AppVersion = _updates.IsInstalled ? _updates.CurrentVersion?.ToString() ?? "Unknown" : "Dev build";
        Settings.CheckForUpdates = CheckForUpdatesFromSettingsAsync;
        RefreshProjects();

        _server = new ServerConnection(_database);
        _devices = new DeviceSession(_database, _server);
        _server.StateChanged += ShowServerState;
        _devices.PairingChanged += ShowServerState;
        ShowServerState();
        Settings.Server = _server;
        Settings.Devices = _devices;

        _sync = new SyncEngine(_database, _server, _devices);
        _sync.RecordsChanged += RefreshProjects;
        Settings.Sync = _sync;
        Settings.Start();

        // Dev builds aren't installed, so there's nothing for them to update.
        if (_updates.IsInstalled)
        {
            var updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            updateTimer.Tick += async (_, _) => await CheckForUpdateAsync();
            updateTimer.Start();
        }

        Loaded += async (_, _) =>
        {
            await _server.CheckAsync();
            if (_updates.IsInstalled) await CheckForUpdateAsync();
        };
    }

    private void RefreshProjects()
    {
        var projects = _database.ListProjects();
        ProjectList.ItemsSource = projects;
        NoProjects.Visibility = projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        NewProjectName.Clear();
        NewProjectForm.Visibility = Visibility.Visible;
        NewProjectName.Focus();
    }

    private void CancelNewProject_Click(object sender, RoutedEventArgs e) => HideNewProjectForm();

    private void CreateProject_Click(object sender, RoutedEventArgs e) => CreateProjectFromForm();

    private void NewProjectName_TextChanged(object sender, TextChangedEventArgs e) =>
        CreateProject.IsEnabled = !string.IsNullOrWhiteSpace(NewProjectName.Text);

    private void NewProjectName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CreateProjectFromForm();
        else if (e.Key == Key.Escape) HideNewProjectForm();
    }

    private void CreateProjectFromForm()
    {
        var name = NewProjectName.Text.Trim();
        if (name.Length == 0) return;

        _database.CreateProject(name);
        HideNewProjectForm();
        RefreshProjects();
        _ = _sync.SyncNowAsync();
    }

    private void HideNewProjectForm() => NewProjectForm.Visibility = Visibility.Collapsed;

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var project = (Project)((Button)sender).DataContext;
        OpenProjectName.Text = project.Name;
        ShowView(ProjectView);
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        MainContent.IsEnabled = false;
        MainContent.Effect = new BlurEffect { Radius = 8 };
        Settings.Visibility = Visibility.Visible;
        Settings.Focus();
    }

    private void Settings_Closed(object? sender, EventArgs e)
    {
        Settings.Visibility = Visibility.Collapsed;
        MainContent.Effect = null;
        MainContent.IsEnabled = _requiredUpdate is null;
    }

    private void BackToProjects_Click(object sender, RoutedEventArgs e) => ShowView(ProjectSelectView);

    private void ShowView(UIElement view)
    {
        ProjectSelectView.Visibility = Visibility.Collapsed;
        ProjectView.Visibility = Visibility.Collapsed;
        view.Visibility = Visibility.Visible;
    }

    private void ShowServerState()
    {
        if (_server.State == ServerState.NoAddress || !_devices.IsPaired)
        {
            (ServerStatus.Text, ServerStatus.Foreground) = ("Not connected to a server", MutedBrush);
            return;
        }

        (ServerStatus.Text, ServerStatus.Foreground) = _server.State switch
        {
            ServerState.Connected => ("● Connected to server", ConnectedBrush),
            ServerState.Connecting => ("Connecting to server…", MutedBrush),
            _ => ("● Can't reach the server", DisconnectedBrush),
        };
    }

    private async Task<string> CheckForUpdatesFromSettingsAsync()
    {
        if (!_updates.IsInstalled) return "Dev builds don't update";
        return await CheckForUpdateAsync() switch
        {
            true => "Update found",
            false => "Up to date",
            null => "Couldn't check",
        };
    }

    // Keeps running while the overlay waits, because only the newest release can be downloaded.
    // Returns whether there's an update, or null if it couldn't be checked.
    private async Task<bool?> CheckForUpdateAsync()
    {
        if (_downloading) return true;

        UpdateInfo? update;
        try
        {
            update = await _updates.CheckForUpdatesAsync();
        }
        catch (Exception)
        {
            return null; // Offline, say. The next check tries again, and a known update stays required.
        }

        if (update is null) return false;
        if (_downloading) return true;

        var isNewVersion = _requiredUpdate?.TargetFullRelease.Version != update.TargetFullRelease.Version;
        _requiredUpdate = update;
        if (isNewVersion) ShowUpdateRequired(update);
        return true;
    }

    private void ShowUpdateRequired(UpdateInfo update)
    {
        MainContent.IsEnabled = false;
        UpdateOverlay.Visibility = Visibility.Visible;
        UpdateTitle.Text = $"Update to v{update.TargetFullRelease.Version}";
        UpdateMessage.Text = "This update is required to keep using Agent Orange.";
        UpdateProgress.Visibility = Visibility.Collapsed;
        DownloadUpdate.Content = "Download update";
        DownloadUpdate.Visibility = Visibility.Visible;
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading || _requiredUpdate is null) return;
        _downloading = true;

        DownloadUpdate.Visibility = Visibility.Collapsed;
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateMessage.Text = "Downloading…";

        try
        {
            _requiredUpdate = await _updates.CheckForUpdatesAsync() ?? _requiredUpdate;
            UpdateTitle.Text = $"Update to v{_requiredUpdate.TargetFullRelease.Version}";
            await _updates.DownloadUpdatesAsync(
                _requiredUpdate,
                percent => Dispatcher.Invoke(() => UpdateProgress.Value = percent));
            UpdateMessage.Text = "Restarting…";
            _updates.ApplyUpdatesAndRestart(_requiredUpdate);
        }
        catch (Exception)
        {
            UpdateMessage.Text = "The download didn't finish. Check your connection and try again.";
            UpdateProgress.Visibility = Visibility.Collapsed;
            DownloadUpdate.Content = "Try again";
            DownloadUpdate.Visibility = Visibility.Visible;
        }
        finally
        {
            _downloading = false;
        }
    }
}
