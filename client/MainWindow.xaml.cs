using System.Net.Http;
using System.Windows;
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

    private UpdateInfo? _requiredUpdate;
    private bool _updateRunning;

    public MainWindow()
    {
        InitializeComponent();
        VersionLabel.Text = _updates.IsInstalled ? $"v{_updates.CurrentVersion}" : "dev build";

        var serverTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        serverTimer.Tick += async (_, _) => await CheckServerAsync();
        serverTimer.Start();

        // Dev builds aren't installed, so there's nothing for them to update.
        if (_updates.IsInstalled)
        {
            var updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            updateTimer.Tick += async (_, _) => await ForceUpdateIfAvailableAsync();
            updateTimer.Start();
        }

        Loaded += async (_, _) =>
        {
            await CheckServerAsync();
            if (_updates.IsInstalled) await ForceUpdateIfAvailableAsync();
        };
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

    private async Task ForceUpdateIfAvailableAsync()
    {
        if (_updateRunning) return;
        _updateRunning = true;

        try
        {
            // Once an update is known it stays required, even if a later check can't reach GitHub.
            _requiredUpdate ??= await _updates.CheckForUpdatesAsync();
            if (_requiredUpdate is null) return;

            ShowUpdating(_requiredUpdate);
            await _updates.DownloadUpdatesAsync(
                _requiredUpdate,
                percent => Dispatcher.Invoke(() => UpdateProgress.Value = percent));
            UpdateMessage.Text = "Restarting…";
            _updates.ApplyUpdatesAndRestart(_requiredUpdate);
        }
        catch (Exception)
        {
            // Failing to check (offline, say) is fine; the next check tries again.
            // A required update that fails stays blocking until it works.
            if (_requiredUpdate is not null) ShowUpdateFailed();
        }
        finally
        {
            _updateRunning = false;
        }
    }

    private void ShowUpdating(UpdateInfo update)
    {
        MainContent.IsEnabled = false;
        UpdateOverlay.Visibility = Visibility.Visible;
        UpdateTitle.Text = $"Updating to v{update.TargetFullRelease.Version}";
        UpdateMessage.Text = "Downloading…";
        UpdateProgress.Value = 0;
        RetryUpdate.Visibility = Visibility.Collapsed;
    }

    private void ShowUpdateFailed()
    {
        UpdateMessage.Text = "The update didn't finish. Trying again every minute.";
        RetryUpdate.Visibility = Visibility.Visible;
    }

    private async void RetryUpdate_Click(object sender, RoutedEventArgs e) => await ForceUpdateIfAvailableAsync();
}
