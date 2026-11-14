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
                // Where in the app it went wrong, as the message alone rarely says.
                var where = error.StackTrace?.Split('\n').FirstOrDefault(frame => frame.Contains("AgentOrange.", StringComparison.Ordinal))?.Trim();
                Say($"FAILED at {line}: {error.Message}{(where is null ? "" : $" ({where})")}");
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
                // A key, or a shortcut such as Shift+A or Ctrl+V, with Shift, Ctrl and Alt held only while it's sent.
                var keyParts = parts[1].Split('+');
                var key = Enum.Parse<Key>(keyParts[^1], ignoreCase: true);
                var held = ModifierKeys.None;
                foreach (var modifier in keyParts[..^1]) held |= Enum.Parse<ModifierKeys>(modifier.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ? "Control" : modifier, ignoreCase: true);
                var focused = Keyboard.FocusedElement as UIElement
                    ?? Descendants(_driveTarget!).OfType<TextBox>().FirstOrDefault(box => box.IsVisible)
                    ?? (UIElement)_driveTarget!;
                if (PresentationSource.FromVisual(focused) is not { } source) throw new InvalidOperationException("Nothing to send keys to.");
                KeyState.Held = held;
                try
                {
                    var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    focused.RaiseEvent(down);
                    var up = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent };
                    if (!down.Handled) focused.RaiseEvent(up);

                    // Text boxes take these through command bindings, which read the real keyboard.
                    RoutedUICommand? command = held != ModifierKeys.Control || down.Handled || up.Handled ? null : key switch
                    {
                        Key.V => ApplicationCommands.Paste,
                        Key.C => ApplicationCommands.Copy,
                        Key.X => ApplicationCommands.Cut,
                        Key.A => ApplicationCommands.SelectAll,
                        Key.Z => ApplicationCommands.Undo,
                        Key.Y => ApplicationCommands.Redo,
                        _ => null,
                    };
                    if (command?.CanExecute(null, focused) == true) command.Execute(null, focused);
                }
                finally
                {
                    KeyState.Held = null;
                }
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
                // As a real click arrives: each press and release tunnels first, then bubbles unless taken.
                foreach (var (preview, bubble) in new[] { (UIElement.PreviewMouseLeftButtonDownEvent, UIElement.MouseLeftButtonDownEvent), (UIElement.PreviewMouseLeftButtonUpEvent, UIElement.MouseLeftButtonUpEvent) })
                {
                    var first = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = preview };
                    pressedOn.RaiseEvent(first);
                    if (!first.Handled) pressedOn.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = bubble });
                }
                return true;
            case "focus" when !int.TryParse(parts[1], out _):
                // focus "Board": what has that name, such as a board, so keys go to it.
                Keyboard.Focus(await UntilThereAsync(_driveTarget!, () => Descendants(_driveTarget!).OfType<FrameworkElement>().LastOrDefault(element => element.IsVisible && AutomationProperties.GetName(element) == parts[1]), parts[1]));
                return true;
            case "focus":
                var field = await UntilThereAsync(_driveTarget!, () => Visible<TextBox>(_driveTarget!).ElementAtOrDefault(int.Parse(parts[1])), $"field {parts[1]}");
                Keyboard.Focus(field);
                return true;
            case "select":
                // select "Name": the node on the open board whose name starts so, as a click on it would.
                var board = Descendants(_driveTarget!).OfType<NodeCanvas>().FirstOrDefault(each => each.IsVisible) ?? throw new InvalidOperationException("No board is open.");
                if (!board.SelectNamed(parts[1])) throw new InvalidOperationException($"No node on the board is called {parts[1]}.");
                return true;
            case "nodes":
                // Each node on the open board: where it is, its size and its name.
                var listed = Descendants(_driveTarget!).OfType<NodeCanvas>().FirstOrDefault(each => each.IsVisible) ?? throw new InvalidOperationException("No board is open.");
                foreach (var each in listed.DescribeNodes()) say(each);
                return true;
            case "move" or "size":
                // move X Y, or size W: the selected node, as dragging it or its edge would.
                var dragged = Descendants(_driveTarget!).OfType<NodeCanvas>().FirstOrDefault(each => each.IsVisible) ?? throw new InvalidOperationException("No board is open.");
                if (parts[0] == "move") dragged.MoveSelected(double.Parse(parts[1]), double.Parse(parts[2]));
                else dragged.SizeSelected(double.Parse(parts[1]));
                return true;
            case "point":
                // point X Y: where on the board the next node goes, as the mouse would say. "point off" for no mouse.
                NodeCanvas.DrivePointer = parts[1] == "off" ? null : new Point(double.Parse(parts[1]), double.Parse(parts[2]));
                return true;
            case "focused":
                // What keys go to: its kind and name, and the window it's in.
                var has = Keyboard.FocusedElement as DependencyObject;
                say(has is null ? "nothing" : $"{has.GetType().Name} \"{(has is FrameworkElement shown ? AutomationProperties.GetName(shown) : "")}\" in {Window.GetWindow(has)?.Title ?? "a popup"}");
                return true;
            case "pick":
                // pick "Web" "Unreal": in the drop-down showing Web (or named Web), choose Unreal.
                static string ItemText(object? item) => item is ContentControl { Content: var content } ? content?.ToString() ?? "" : item?.ToString() ?? "";
                var combo = await UntilThereAsync(_driveTarget!, () => Visible<ComboBox>(_driveTarget!).FirstOrDefault(box =>
                    AutomationProperties.GetName(box) == parts[1] || ItemText(box.SelectedItem) == parts[1]), $"a drop-down showing {parts[1]}");
                var choice = combo.Items.Cast<object>().FirstOrDefault(item => ItemText(item) == parts[2])
                    ?? throw new InvalidOperationException($"{parts[1]} has no {parts[2]}. It has: {string.Join(", ", combo.Items.Cast<object>().Select(ItemText))}");
                combo.SelectedItem = choice;
                return true;
            default:
                return false;
        }
    }
}
#endif
