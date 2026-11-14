#if DEBUG
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace AgentOrange;

/// <summary>
/// Dev builds only. The running dev app takes the same commands as a headless script, one batch at a
/// time, from a pipe only this Windows account can open, so whoever is building it can use it as the
/// person does, in the window that's open. Nothing goes through the real mouse or keyboard, and
/// pictures are drawn from the app's own windows, with any menu open over them, never captured from
/// the screen. tools/drive.ps1 sends a batch and prints what came back.
///
/// Besides the script commands: windows (what's open), names (what can be clicked), key Enter|Escape|
/// Down|Up|Tab (to what has focus), hover "Name", focus N (the Nth text box), close (every open menu).
/// </summary>
internal static partial class Headless
{
    private const string PipeName = "AgentOrange-Drive";
    private const string EndOfBatch = "<<end>>";

    private static Window? _driveMain;
    private static Window? _driveTarget;
    private static readonly string DriveOutput = Path.Combine(Path.GetTempPath(), "AgentOrange-Drive");

    public static void Drive(Window main)
    {
        _driveMain = main;
        _driveTarget = main;
        WaitLimit = TimeSpan.FromSeconds(5); // Someone is waiting on the answer.
        Directory.CreateDirectory(DriveOutput);
        _ = Task.Run(ServeAsync);
    }

    private static async Task ServeAsync()
    {
        while (true)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync();
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                var lines = new List<string>();
                while (await reader.ReadLineAsync() is { } line && line != EndOfBatch) lines.Add(line);
                var reply = await _driveMain!.Dispatcher.InvokeAsync(() => RunBatchAsync(lines)).Task.Unwrap();
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true);
                await writer.WriteAsync(reply);
                await writer.FlushAsync();
            }
            catch (IOException error) when (error.Message.Contains("All pipe instances are busy", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Write("Another dev build is taking commands already, so this one isn't.");
                return;
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                AppLog.Write($"The drive pipe dropped a batch: {error.Message}");
            }
        }
    }

    // Runs each line in turn on the window's thread, stopping at the first that fails.
    private static async Task<string> RunBatchAsync(List<string> lines)
    {
        var said = new StringBuilder();
        void Say(string line) => said.AppendLine(line);
        foreach (var line in lines)
        {
            var parts = Tokens().Matches(line).Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Value).ToArray();
            if (parts.Length == 0 || parts[0].StartsWith('#')) continue;
            if (_driveTarget is not { IsLoaded: true }) _driveTarget = _driveMain;
            try
            {
                if (!await RunDriveCommandAsync(parts, Say)) await RunCommandAsync(_driveTarget!, parts, Say, DriveOutput);
            }
            catch (Exception error)
            {
                Say($"FAILED at {line}: {error.Message}");
                break;
            }
        }
        return said.Length == 0 ? "ok\n" : said.ToString();
    }

    // The commands only a live app has. False when it's one of the script's own.
    private static async Task<bool> RunDriveCommandAsync(string[] parts, Action<string> say)
    {
        switch (parts[0])
        {
            case "windows":
                foreach (var each in Application.Current.Windows.OfType<Window>().Where(each => each.IsLoaded))
                {
                    say($"{(each == _driveTarget ? "* " : "  ")}{(each.Title.Length > 0 ? each.Title : each.GetType().Name)}{(each.IsVisible ? "" : " (hidden)")}");
                }
                return true;
            case "window":
                _driveTarget = await UntilThereAsync(_driveMain!, () => Application.Current.Windows.OfType<Window>().FirstOrDefault(each => each.Title == parts[1] && each.IsLoaded), $"the window {parts[1]}");
                say($"now on {parts[1]}");
                return true;
            case "names":
                var names = Descendants(_driveTarget!).OfType<FrameworkElement>()
                    .Where(element => element.IsVisible && element is ButtonBase or TextBox)
                    .Select(element => AutomationProperties.GetName(element) is { Length: > 0 } name ? name
                        : element is ContentControl { Content: string words } ? words
                        : element is TextBox box ? $"[text box] {box.Text}"
                        : Descendants(element).OfType<TextBlock>().Select(block => block.Text).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)))
                    .Where(name => name is not null)
                    .Distinct();
                say(string.Join(" | ", names));
                return true;
            case "key":
                var key = Enum.Parse<Key>(parts[1], ignoreCase: true);
                var focused = Keyboard.FocusedElement as UIElement
                    ?? Descendants(_driveTarget!).OfType<TextBox>().FirstOrDefault(box => box.IsVisible)
                    ?? (UIElement)_driveTarget!;
                if (PresentationSource.FromVisual(focused) is not { } source) throw new InvalidOperationException("Nothing to send keys to.");
                var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                focused.RaiseEvent(down);
                if (!down.Handled) focused.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });
                return true;
            case "hover":
                var target = await UntilThereAsync(_driveTarget!, () => Descendants(_driveTarget!).OfType<FrameworkElement>().LastOrDefault(element => element.IsVisible
                    && (AutomationProperties.GetName(element) == parts[1] || element is ContentControl control && Matches(control, parts[1]))), parts[1]);
                target.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
                return true;
            case "close":
                // Every menu open over the app's windows, so none is left on the person's screen.
                var closed = 0;
                foreach (var window in PresentationSource.CurrentSources.OfType<System.Windows.Interop.HwndSource>().ToList())
                {
                    if (window.RootVisual is FrameworkElement { Parent: Popup { IsOpen: true } popup })
                    {
                        popup.IsOpen = false;
                        closed++;
                    }
                }
                say($"closed {closed}");
                return true;
            case "press":
                // Something clickable that isn't a button, such as a picture: pressed and let go, as a click.
                var pressedOn = await UntilThereAsync(_driveTarget!, () => Descendants(_driveTarget!).OfType<FrameworkElement>().LastOrDefault(element => element.IsVisible && AutomationProperties.GetName(element) == parts[1]), parts[1]);
                pressedOn.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                pressedOn.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                return true;
            case "focus":
                var field = await UntilThereAsync(_driveTarget!, () => Visible<TextBox>(_driveTarget!).ElementAtOrDefault(int.Parse(parts[1])), $"field {parts[1]}");
                Keyboard.Focus(field);
                return true;
            default:
                return false;
        }
    }
}
#endif
