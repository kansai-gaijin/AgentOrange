using Microsoft.Data.Sqlite;
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
    private const string RequiredUpdateSetting = "required_update";
    private static readonly Brush ConnectedBrush = new SolidColorBrush(Color.FromRgb(0x5F, 0xD0, 0x8A));
    private static readonly Brush DisconnectedBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x6B, 0x6B));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAA));

    private readonly UpdateManager _updates = new(new SimpleWebSource(UpdateFeedUrl));
    private readonly LocalDatabase _database;
    private readonly ServerConnection _server;
    private readonly DeviceSession _devices;
    private readonly SyncEngine _sync;

    private UpdateInfo? _requiredUpdate;
    private string? _requiredVersion; // Can be known from an earlier run without the details to download it.
    private bool _downloading;
    private bool _restarting; // Already agreed to, so closing doesn't ask again.

    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        if (Headless.Active) Headless.Attach(this);
#endif
        VersionLabel.Text = _updates.IsInstalled ? $"v{_updates.CurrentVersion}" : "dev build";

        // Kept outside the install folder, which updates replace.
        var folders = new AppFolders(_updates.IsInstalled);
#if DEBUG
        if (Headless.Active && !folders.DatabaseFolderExists) folders.UseDefaultDatabaseFolder();
#endif
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
        ProjectWizard.ClaimFolder = async (folder, name) =>
            (await _database.ClaimFolderAsync(folder, new Project($"new-project-{Guid.NewGuid():N}", name, "", "", "", "", DateTimeOffset.UtcNow)))?.Name;
        ProjectWizard.ReleaseFolder = _database.ReleaseFolder;
        GitTools.ProjectUsingRepo = url => _database.ListProjects().FirstOrDefault(project => GitTools.SameRepo(project.RepoUrl, url))?.Name;
        GitTools.AllowedFolderSettings = _database.AllowedFolderSettings;
        GitTools.RememberAllowedFolderSettings = _database.AllowFolderSettings;
        GitTools.RememberUnfinishedDownload = _database.SetDownloadUnfinished;
        GitTools.IsUnfinishedNoteFromHere = _database.IsUnfinishedNoteFromHere;
        GitTools.RememberUnfinishedNote = _database.RememberUnfinishedNote;
        ProjectSettings.ProjectIsBusy = projectId => _busyProjectId == projectId;

        // A delete interrupted last time can leave parts of a project behind.
        _database.CleanUpDeletedProjects();
        _database.RetryRefused();
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

        _devices.ClientVersion = _updates.IsInstalled ? _updates.CurrentVersion?.ToString() : null;
        Settings.ConfirmStoppingWork = ConfirmStoppingClone;
        Settings.AppIsRestarting = () => _restarting = true;
        Closing += (_, closing) => closing.Cancel = !_restarting && !ConfirmStoppingClone("Closing");

        // Nothing stays compatible across a release, so a copy that has to update doesn't sync.
        _sync = new SyncEngine(_database, _server, _devices, folders.DatabasePath) { IsPaused = () => _requiredVersion is not null };

        // Another window of the app may have changed something, and only one of them syncs.
        var knownRecords = _database.RecordsSignature();
        var otherWindowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        otherWindowTimer.Tick += (_, _) =>
        {
            var wasLost = _databaseWasLost;
            _databaseWasLost = false;
            _openPageNeedsCheck |= wasLost;
            try
            {
                var latest = _database.RecordsSignature();
                if (latest != knownRecords || wasLost)
                {
                    knownRecords = latest;
                    RefreshProjects();
                }

                // Folders don't sync, and a clone there can finish at any time. A page that lost
                // the database partway through its check has nothing else that will finish it.
                if (_openProject is { } open && _busyProjectId is null && ProjectSettings.Visibility != Visibility.Visible)
                {
                    var folder = _database.GetProjectFolder(open.Id);
                    if (_openPageNeedsCheck || (_projectCheck is not null && folder != _checkedFolder))
                    {
                        _openPageNeedsCheck = false;
                        _ = RecheckIfStillOpenAsync(open.Id);
                    }
                    else if (_projectCheck is not null && DownloadStateOf(folder) != _shownDownloadState)
                    {
                        RenderProjectKeepingFocus(open);
                    }
                }
            }
            catch (SqliteException error)
            {
                _databaseWasLost = true; // So everything's looked at again once it's back.

                // Not thrown from here, because a timer whose tick throws never runs again.
                Dispatcher.BeginInvoke(new Action(() => throw error));
            }
        };
        otherWindowTimer.Start();
        _sync.RecordsChanged += () =>
        {
            _database.CleanUpDeletedProjects();
            RefreshProjects();
        };
        Settings.Sync = _sync;
        Settings.Start();

        // Dev builds aren't installed, so there's nothing for them to update.
        if (_updates.IsInstalled)
        {
            // Offline-first: a PC with no connection can't download it, so it keeps working from
            // its own data, without syncing, until the first check that gets through.
            if (RememberedUpdate() is { } remembered)
            {
                _requiredVersion = remembered;
                ShowServerState();
            }

            var updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            updateTimer.Tick += async (_, _) => await CheckForUpdateAsync();
            updateTimer.Start();
        }

        // Wherever the mouse is let go, as a card that's being pressed holds off rebuilding the cards.
        // That includes outside the window, which it only hears about by no longer being active.
        void EndCardPress()
        {
            _pressedCard = null;
            if (_refreshAfterDrag && _draggedCard is null)
            {
                _refreshAfterDrag = false;
                Dispatcher.InvokeAsync(RefreshProjects, DispatcherPriority.Background);
            }
        }
        PreviewMouseLeftButtonUp += (_, _) => EndCardPress();
        Deactivated += (_, _) => EndCardPress();

        // Coming back to a page that's been open a while is when access may have changed.
        Activated += async (_, _) => await RecheckAccessOnReturnAsync();

        Loaded += async (_, _) =>
        {
            await _server.CheckAsync();
            if (_updates.IsInstalled) await CheckForUpdateAsync();

            // Last, so a dead network drive can't hold up the server or update checks.
            await RemoveLeftoverCloneAsync();
            await ReadEngineVersionsAsync();
        };
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        MainContent.IsEnabled = false;
        MainContent.Effect = new BlurEffect { Radius = 8 };
        ProjectWizard.Open();
    }

    private void ProjectWizard_Closed()
    {
        MainContent.Effect = null;
        MainContent.IsEnabled = UpdateOverlay.Visibility != Visibility.Visible;
    }

    private void ProjectWizard_Created(NewProject project)
    {
        var id = _database.CreateProject(project.Name, project.Engine, project.GameType, project.RepoUrl);

        // Folders differ between PCs, so this stays here and only the rest syncs.
        if (!project.CloneIntoFolder)
        {
            _database.SetProjectFolder(id, project.Folder);
            _ = GitTools.PickUpUnfinishedNoteAsync(project.Folder);
        }
        if (project.RepoConfirmed) _database.SetSetting(VerifiedSetting(id), GitTools.RepoKey(project.RepoUrl));
        else _database.SetSetting(CreatedHereSetting(id), GitTools.RepoKey(project.RepoUrl));

        RefreshProjects();
        _ = _sync.SyncNowAsync();
        _ = project.CloneIntoFolder ? CloneIntoChosenFolderAsync(id, project.Folder) : ReadEngineVersionAsync(id);
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
        MainContent.IsEnabled = UpdateOverlay.Visibility != Visibility.Visible;
    }

    private void ShowServerState()
    {
        if (_requiredVersion is not null && UpdateOverlay.Visibility != Visibility.Visible)
        {
            (ServerStatus.Text, ServerStatus.Foreground) = ("Sync is paused until this PC can update", MutedBrush);
            return;
        }
        if (_server.State == ServerState.NoAddress || !_devices.IsPaired)
        {
            (ServerStatus.Text, ServerStatus.Foreground) = ("Not connected to a server", MutedBrush);
            return;
        }

        // Only known from the server's last answer, so it says nothing once that can't be reached.
        if (_devices.VersionsDiffer && _server.State == ServerState.Connected)
        {
            (ServerStatus.Text, ServerStatus.Foreground) = ("Sync is paused until this PC and the server are on the same version", MutedBrush);
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
            // Offline, say, so it can't be downloaded. This PC keeps working from its own data with
            // sync paused, and the next check that gets through brings this back.
            if (!_downloading && UpdateOverlay.Visibility == Visibility.Visible)
            {
                HideUpdateOverlay();
                ShowServerState();
            }
            return null;
        }

        if (update is null)
        {
            StopRequiringUpdate();
            return false;
        }
        if (_downloading) return true;

        var version = update.TargetFullRelease.Version.ToString();
        var isNewVersion = _requiredVersion != version || UpdateOverlay.Visibility != Visibility.Visible;
        _requiredUpdate = update;
        _requiredVersion = version;
        _database.SetSetting(RequiredUpdateSetting, version);
        if (isNewVersion) ShowUpdateRequired(version);
        return true;
    }

    // Only one newer than this copy still counts, which also clears it once it's installed.
    private string? RememberedUpdate() =>
        SemanticVersion.TryParse(_database.GetSetting(RequiredUpdateSetting) ?? "", out var version) && _updates.CurrentVersion is { } current && version > current
            ? version.ToString()
            : null;

    // The feed answered and has nothing newer, such as a release having been withdrawn.
    private void StopRequiringUpdate()
    {
        if (_requiredVersion is null) return;
        _requiredVersion = null;
        _requiredUpdate = null;
        _database.SetSetting(RequiredUpdateSetting, "");
        HideUpdateOverlay();
        ShowServerState();
    }

    private void HideUpdateOverlay()
    {
        UpdateOverlay.Visibility = Visibility.Collapsed;
        ProjectWizard.IsEnabled = true;
        ProjectSettings.IsEnabled = true;
        Settings.IsEnabled = true;
        MainContent.IsEnabled = ProjectWizard.Visibility != Visibility.Visible && ProjectSettings.Visibility != Visibility.Visible && Settings.Visibility != Visibility.Visible;
    }

    private void ShowUpdateRequired(string version)
    {
        // The keyboard can reach what's under the overlay, and that includes an open modal's Delete.
        MainContent.IsEnabled = false;
        ProjectWizard.IsEnabled = false;
        ProjectSettings.IsEnabled = false;
        Settings.IsEnabled = false;
        UpdateOverlay.Visibility = Visibility.Visible;
        UpdateTitle.Text = $"Update to v{version}";
        ShowUpdateMessage();
        UpdateProgress.Visibility = Visibility.Collapsed;
        DownloadUpdate.Content = "Download update";
        DownloadUpdate.Visibility = Visibility.Visible;
    }

    // The overlay covers the page that says a clone is running, so it has to say so itself.
    private void ShowUpdateMessage()
    {
        const string required = "This update is required to keep using Agent Orange.";
        UpdateMessage.Text = CloningProjectName() is { } name
            ? $"{required} {name} is still cloning, and updating stops that, so you can wait for it to finish first."
            : required;
    }

    private string? CloningProjectName() =>
        _cloneCancel is null ? null : (_busyProjectId is { } id ? FindProject(id)?.Name : null) ?? "A project";

    // A clone can take hours, and whatever stops it means starting again from nothing.
    private bool ConfirmStoppingClone(string action)
    {
#if DEBUG
        if (Headless.Active) return true;
#endif
        if (_downloadIsInPlace && CloningProjectName() is { } finishing)
        {
            return Dialogs.Confirm($"{finishing} is still downloading its large files and submodules. {action} stops that. What's arrived is kept, and its page will offer to finish the rest. Do it anyway?");
        }
        if (CloningProjectName() is { } name)
        {
            return Dialogs.Confirm($"{name} is still cloning. {action} stops that, and what's been downloaded so far is thrown away. Do it anyway?");
        }
        if (ProjectWizard.IsCreatingRepo || _busyText == CreatingRepoText)
        {
            return Dialogs.Confirm($"A repo is being created on GitHub. {action} could leave it there with no project pointing at it. Do it anyway?");
        }
        return true;
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading || _requiredVersion is null) return;
        if (!ConfirmStoppingClone("Updating")) return;
        _downloading = true;

        DownloadUpdate.Visibility = Visibility.Collapsed;
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateMessage.Text = "Downloading…";

        try
        {
            // The feed answered, and there's nothing newer now, such as a release having been withdrawn.
            _requiredUpdate = await _updates.CheckForUpdatesAsync();
            if (_requiredUpdate is null)
            {
                StopRequiringUpdate();
                return;
            }

            _requiredVersion = _requiredUpdate.TargetFullRelease.Version.ToString();
            _database.SetSetting(RequiredUpdateSetting, _requiredVersion);
            UpdateTitle.Text = $"Update to v{_requiredVersion}";
            await _updates.DownloadUpdatesAsync(
                _requiredUpdate,
                percent => Dispatcher.Invoke(() => UpdateProgress.Value = percent));
            UpdateMessage.Text = "Restarting…";
            _restarting = true;
            _updates.ApplyUpdatesAndRestart(_requiredUpdate);
        }
        catch (Exception)
        {
            _restarting = false;
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
