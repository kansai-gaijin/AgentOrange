using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Velopack;
using Velopack.Sources;

namespace AgentOrange;

public partial class MainWindow : Window
{
    private const string DefaultServerUrl = "http://127.0.0.1:47100";
    private const string HealthyAnswer = "🍊";
    private const string UpdateFeedUrl = "https://github.com/kansai-gaijin/AgentOrange/releases/latest/download";
    private static readonly Brush ConnectedBrush = new SolidColorBrush(Color.FromRgb(0x5F, 0xD0, 0x8A));
    private static readonly Brush DisconnectedBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x6B, 0x6B));

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly string _serverUrl =
        Environment.GetEnvironmentVariable("AGENT_ORANGE_SERVER")?.TrimEnd('/') ?? DefaultServerUrl;
    private readonly UpdateManager _updates = new(new SimpleWebSource(UpdateFeedUrl));
    private readonly ProjectStore _projects;

    private UpdateInfo? _requiredUpdate;
    private bool _downloading;

    public MainWindow()
    {
        InitializeComponent();
        VersionLabel.Text = _updates.IsInstalled ? $"v{_updates.CurrentVersion}" : "dev build";

        // Kept outside the install folder, which updates replace. Dev builds get their
        // own folder so testing never touches real projects.
        _projects = new ProjectStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            _updates.IsInstalled ? "AgentOrange" : "AgentOrange-Dev",
            "agent-orange.db"));
        RefreshProjects();

        var serverTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        serverTimer.Tick += async (_, _) => await CheckServerAsync();
        serverTimer.Start();

        // Dev builds aren't installed, so there's nothing for them to update.
        if (_updates.IsInstalled)
        {
            var updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            updateTimer.Tick += async (_, _) => await CheckForUpdateAsync();
            updateTimer.Start();
        }

        Loaded += async (_, _) =>
        {
            await CheckServerAsync();
            if (_updates.IsInstalled) await CheckForUpdateAsync();
        };
    }

    private void RefreshProjects()
    {
        var projects = _projects.List();
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

        _projects.Create(name);
        HideNewProjectForm();
        RefreshProjects();
    }

    private void HideNewProjectForm() => NewProjectForm.Visibility = Visibility.Collapsed;

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var project = (Project)((Button)sender).DataContext;
        OpenProjectName.Text = project.Name;
        ProjectSelectView.Visibility = Visibility.Collapsed;
        ProjectView.Visibility = Visibility.Visible;
    }

    private void BackToProjects_Click(object sender, RoutedEventArgs e)
    {
        ProjectView.Visibility = Visibility.Collapsed;
        ProjectSelectView.Visibility = Visibility.Visible;
    }

    private async Task CheckServerAsync()
    {
        bool connected;
        try
        {
            connected = await _http.GetStringAsync($"{_serverUrl}/v1/health") == HealthyAnswer;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            connected = false;
        }

        ServerStatus.Text = connected ? "● Connected to server" : "● Not connected to server";
        ServerStatus.Foreground = connected ? ConnectedBrush : DisconnectedBrush;
    }

    // Keeps running while the overlay waits, because only the newest release can be downloaded.
    private async Task CheckForUpdateAsync()
    {
        if (_downloading) return;

        UpdateInfo? update;
        try
        {
            update = await _updates.CheckForUpdatesAsync();
        }
        catch (Exception)
        {
            return; // Offline, say. The next check tries again, and a known update stays required.
        }

        if (update is null || _downloading) return;

        var isNewVersion = _requiredUpdate?.TargetFullRelease.Version != update.TargetFullRelease.Version;
        _requiredUpdate = update;
        if (isNewVersion) ShowUpdateRequired(update);
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
