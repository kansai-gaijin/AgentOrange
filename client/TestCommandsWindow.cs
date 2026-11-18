using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>
/// Shows a person the exact repo and commands of a test run and asks yes or no: on the PC that
/// would run them, before any it hasn't allowed for that link and project, and on the PC sending
/// them, before it signs ones it hasn't sent to that machine. The commands come from synced data,
/// which the server or any paired PC can change, so a person sees them before they run. Closing
/// the window is a no. Plain, as T3 has no equivalent, in the look of the rest of the app.
/// </summary>
public sealed class TestCommandsWindow : Window
{
    private readonly TaskCompletionSource<bool> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TestCommandsWindow(string title, string heading, string intro, (string Label, string Value)[] rows, string note, string yes, string no)
    {
        Title = title;
        Width = 680;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 760;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)FindResource("ContentBackground");

        var body = new StackPanel { Margin = new Thickness(24, 20, 24, 8) };
        body.Children.Add(new TextBlock { Text = heading, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextStrong"), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = intro, FontSize = 13, Foreground = (Brush)FindResource("TextNormal"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 14) });
        foreach (var (label, value) in rows)
        {
            if (value.Length == 0) continue;
            body.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(0, 6, 0, 2) });
            body.Children.Add(new Border
            {
                Background = (Brush)FindResource("SidebarBackground"),
                BorderBrush = (Brush)FindResource("Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                Child = new TextBlock { Text = value, FontFamily = new FontFamily("Consolas"), FontSize = 13, Foreground = (Brush)FindResource("TextStrong"), TextWrapping = TextWrapping.Wrap },
            });
        }
        body.Children.Add(new TextBlock { Text = note, FontSize = 12.5, Foreground = (Brush)FindResource("TextMuted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) });

        var refuse = ControlButton(no, () => Answer(false));
        var allow = PrimaryButton(yes, () => Answer(true));
        allow.Margin = new Thickness(8, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 12, 24, 20), Children = { refuse, allow } };

        var layout = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons);
        layout.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body });
        Content = layout;
        Closed += (_, _) => _answer.TrySetResult(false);
#if DEBUG
        if (Headless.Active) Headless.KeepOffScreen(this);
#endif
    }

    /// <summary>Yes or no, once. Closing the window is a no.</summary>
    public Task<bool> Result => _answer.Task;

    private void Answer(bool yes)
    {
        _answer.TrySetResult(yes);
        Close();
    }

    /// <summary>On the PC that would run them.</summary>
    public static TestCommandsWindow ToRunHere(TestApproval request) => new(
        "Allow test commands",
        $"{request.From} wants to run these commands here",
        $"For {request.Project}. They'd run as you on this PC, with your files and logins, in a clone of this repo at this commit. Allow them only if you know what they do.",
        [("Repo", request.RepoUrl), ("Commit", request.Commit), ("Prepare", request.Prepare), ("Test", request.Test), ("Shell", request.Shell), ("Folder", request.Folder)],
        $"Allowing remembers these exact commands for {request.Project} from {request.From}, so they run without asking next time. If they change, you're asked again.",
        "Allow",
        "Refuse");

    /// <summary>On the PC sending them, before it signs them.</summary>
    public static TestCommandsWindow ToSend(TestApproval request) => new(
        "Send test commands",
        $"Run these commands on {request.From}?",
        $"For {request.Project}. They come from the project's settings, which any of your PCs can change, and they'd run as the user of {request.From}. Check they're what you expect.",
        [("Repo", request.RepoUrl), ("Commit", request.Commit), ("Prepare", request.Prepare), ("Test", request.Test)],
        $"This is asked once for these exact commands on {request.From}, and again if they change. {request.From} asks a person there too, the first time.",
        "Send",
        "Cancel");
}
