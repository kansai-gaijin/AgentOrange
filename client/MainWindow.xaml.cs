using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgentOrange;

public partial class MainWindow : Window
{
    private const string DefaultServerUrl = "http://127.0.0.1:47100";
    private const string HealthyAnswer = "🍊";
    private static readonly Brush ConnectedBrush = new SolidColorBrush(Color.FromRgb(0x5F, 0xD0, 0x8A));
    private static readonly Brush DisconnectedBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x6B, 0x6B));

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly string _serverUrl =
        Environment.GetEnvironmentVariable("AGENT_ORANGE_SERVER")?.TrimEnd('/') ?? DefaultServerUrl;

    public MainWindow()
    {
        InitializeComponent();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += async (_, _) => await CheckServerAsync();
        timer.Start();
        Loaded += async (_, _) => await CheckServerAsync();
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
}
