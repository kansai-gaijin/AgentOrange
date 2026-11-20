using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AgentOrange;

/// <summary>
/// A project's checks that failed in its recorded test runs (#105), each with its history: first
/// the ones that passed and failed at the same commit, which flip or differ between machines, and
/// say why they're flagged; then the ones that only ever failed there. Plain, in the look of the
/// test run window.
/// </summary>
public sealed class CheckHistoryWindow : Window
{
    private readonly StackPanel _list = new();

    public CheckHistoryWindow(string projectName)
    {
        Title = $"Check history · {projectName}";
        Width = 760;
        Height = 620;
        Background = (Brush)FindResource("ContentBackground");
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border { Padding = new Thickness(20), Child = _list },
        };
#if DEBUG
        if (Headless.Active) Headless.KeepOffScreen(this);
#endif
    }

    /// <summary>Shows the history of every check that failed, or why there's nothing to show.</summary>
    public void Fill(List<CheckHistoryRow>? history)
    {
        _list.Children.Clear();
        TextBlock Words(string text, double size, string brush, FontWeight weight, Thickness margin, string? font = null) => new()
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            Foreground = (Brush)FindResource(brush),
            TextWrapping = TextWrapping.Wrap,
            Margin = margin,
            FontFamily = font is null ? FontFamily : new FontFamily(font),
        };

        if (history is null)
        {
            _list.Children.Add(Words("The server couldn't give the project's checks. Try again once this PC reaches it.", 13, "TextMuted", FontWeights.Normal, new Thickness(0)));
            return;
        }
        var flags = TestChecks.Flags(history);
        var failing = history.GroupBy(row => row.Name, StringComparer.Ordinal).OrderBy(check => flags.ContainsKey(check.Key) ? 0 : 1).ThenBy(check => check.Key, StringComparer.Ordinal).ToList();
        _list.Children.Add(Words(flags.Count switch
        {
            0 => "No check has passed and failed at the same commit.",
            1 => "1 check passed and failed at the same commit.",
            var many => $"{many} checks passed and failed at the same commit.",
        }, 16, "TextStrong", FontWeights.SemiBold, new Thickness(0)));
        _list.Children.Add(Words(failing.Count == 0
            ? "No check has failed in the runs recorded. Checks are kept from runs whose repo names a results file in .agent-orange/tests.json."
            : TestChecks.FlagAdvice, 12.5, "TextMuted", FontWeights.Normal, new Thickness(0, 6, 0, 8)));

        foreach (var check in failing)
        {
            _list.Children.Add(Words(check.Key, 14, "TextStrong", FontWeights.SemiBold, new Thickness(0, 14, 0, 0)));
            _list.Children.Add(Words(flags.TryGetValue(check.Key, out var flag) ? $"It {flag.Why}" : "It failed, but never at a commit where it also passed.", 12.5, "TextMuted", FontWeights.Normal, new Thickness(0, 2, 0, 4)));
            foreach (var line in TestChecks.HistoryLines(check).Take(50)) _list.Children.Add(Words(line, 12, "TextNormal", FontWeights.Normal, new Thickness(0, 1, 0, 0), "Consolas"));
        }
    }
}
