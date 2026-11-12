#if DEBUG
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgentOrange;

/// <summary>
/// Dev builds only. Runs the real app off-screen and steps through a script, so a change
/// can be tried without touching the computer it's running on: the window can't take focus,
/// nothing uses the mouse or keyboard, data stays in its own folder, screenshots are drawn
/// to files, and real GitHub repos are never created. Git still runs for real in any folder a
/// script names, so scripts should only ever name throwaway ones.
///
///   AgentOrange.exe --headless data-folder script-file output-folder
///
/// Script lines, with "quotes" around anything containing spaces, and # for comments:
///   click "Button"      set 0 "text"       password "text"     select "Radio"
///   choose 0 "Item"     answer "path"      wait "text"         waitgone "text"
///   has "text"          say "note"         texts               shot name
///   sleep 500
/// </summary>
internal static partial class Headless
{
    private const int ExStyleIndex = -20;
    private const int NoActivate = 0x08000000;
    private const int ToolWindow = 0x00000080;
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RunLimit = TimeSpan.FromMinutes(5);

    private static readonly Queue<string> Answers = new();
    private static string _script = "";
    private static string _output = "";
    private static StreamWriter? _log;

    public static bool Active { get; private set; }

    public static string DataFolder { get; private set; } = "";

    public static void Configure(string[] args)
    {
        var at = Array.IndexOf(args, "--headless");
        if (at < 0) return;

        // Carrying on would open the normal window, on the real dev data.
        if (at + 3 >= args.Length) Environment.Exit(2);

        DataFolder = Path.GetFullPath(args[at + 1]);
        _script = Path.GetFullPath(args[at + 2]);
        _output = Path.GetFullPath(args[at + 3]);
        Directory.CreateDirectory(DataFolder);
        Directory.CreateDirectory(_output);
        _log = new StreamWriter(Path.Combine(_output, "headless.log"), append: false) { AutoFlush = true };
        Active = true;

        // Whatever happens in a script, this can't be left running.
        _ = Task.Delay(RunLimit).ContinueWith(_ =>
        {
            _log.WriteLine("STOPPED: ran past the time limit");
            Environment.Exit(1);
        }, TaskScheduler.Default);
    }

    public static void Say(string message) => _log?.WriteLine(message);

    /// <summary>What a file or folder dialog would have returned. Null is the same as Cancel.</summary>
    public static string? NextAnswer() => Answers.TryDequeue(out var answer) ? answer : null;

    public static void Attach(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            SetWindowLongPtrW(handle, ExStyleIndex, GetWindowLongPtrW(handle, ExStyleIndex) | NoActivate | ToolWindow);
        };
        window.Loaded += async (_, _) => await RunAsync(window);
    }

    private static async Task RunAsync(Window window)
    {
        var failed = false;
        try
        {
            await Task.Delay(500);
            foreach (var line in await File.ReadAllLinesAsync(_script))
            {
                var parts = Tokens().Matches(line).Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Value).ToArray();
                if (parts.Length == 0 || parts[0].StartsWith('#')) continue;
                await RunCommandAsync(window, parts);
            }
        }
        catch (Exception error)
        {
            failed = true;
            _log?.WriteLine($"FAILED: {error.Message}");
        }

        _log?.WriteLine("done");
        _log?.Dispose();
        Application.Current.Shutdown(failed ? 1 : 0);
    }

    private static async Task RunCommandAsync(Window window, string[] parts)
    {
        switch (parts[0])
        {
            case "click":
                (await FindButtonAsync(window, parts[1])).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                break;
            case "set":
                var field = await UntilThereAsync(window, () => Visible<TextBox>(window).ElementAtOrDefault(int.Parse(parts[1])), $"field {parts[1]}");
                field.Text = parts[2];

                // The window can't take focus, so leaving the field is done by hand.
                field.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, field, null) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
                break;
            case "password":
                (await UntilThereAsync(window, () => Visible<PasswordBox>(window).FirstOrDefault(), "password field")).Password = parts[1];
                break;
            case "select":
                (await UntilThereAsync(window, () => Visible<RadioButton>(window).LastOrDefault(radio => Matches(radio, parts[1])), parts[1])).IsChecked = true;
                break;
            case "choose":
                var box = await UntilThereAsync(window, () => Visible<ComboBox>(window).ElementAtOrDefault(int.Parse(parts[1])), $"list {parts[1]}");
                box.SelectedItem = box.Items.Cast<object>().First(item => (item as ContentControl)?.Content?.ToString() == parts[2] || item.ToString() == parts[2]);
                break;
            case "answer":
                Answers.Enqueue(parts[1]);
                break;
            case "wait":
                await WaitForAsync(window, parts[1], true);
                break;
            case "waitgone":
                await WaitForAsync(window, parts[1], false);
                break;
            case "has":
                _log?.WriteLine($"{parts[1]}: {(Texts(window).Any(text => text.Contains(parts[1], StringComparison.Ordinal)) ? "yes" : "NO")}");
                break;
            case "say":
                _log?.WriteLine(parts[1]);
                break;
            case "last":
                _log?.WriteLine(Texts(window).LastOrDefault() ?? "");
                break;
            case "texts":
                _log?.WriteLine(string.Join(" | ", Texts(window)));
                break;
            case "shot":
                await Task.Delay(150);
                SaveImage(window, Path.Combine(_output, $"{parts[1]}.png"));
                break;
            case "sleep":
                await Task.Delay(int.Parse(parts[1]));
                break;
            default:
                throw new InvalidOperationException($"Unknown command: {parts[0]}");
        }
        await Task.Delay(100);
    }

    // A card's settings button is hidden until hovered, and there's no mouse here.
    private static Task<Button> FindButtonAsync(Window window, string name) =>
        UntilThereAsync(window, () => Descendants(window).OfType<Button>().LastOrDefault(candidate =>
            candidate.IsEnabled
            && Matches(candidate, name)
            && (candidate.IsVisible || (candidate.Visibility == Visibility.Hidden && VisualTreeHelper.GetParent(candidate) is UIElement { IsVisible: true }))), name);

    // Waits as a person would, because what's asked for may not have been drawn yet.
    private static async Task<T> UntilThereAsync<T>(Window window, Func<T?> find, string what) where T : class
    {
        var limit = DateTime.UtcNow + WaitLimit;
        while (true)
        {
            if (find() is { } found) return found;
            if (DateTime.UtcNow > limit) throw new InvalidOperationException($"Couldn't find {what}. On screen: {string.Join(" | ", Texts(window))}");
            await Task.Delay(100);
        }
    }

    private static async Task WaitForAsync(Window window, string text, bool present)
    {
        var limit = DateTime.UtcNow + WaitLimit;
        while (Texts(window).Any(shown => shown.Contains(text, StringComparison.Ordinal)) != present)
        {
            if (DateTime.UtcNow > limit) throw new InvalidOperationException($"Waited for {text} to {(present ? "appear" : "go")}. On screen: {string.Join(" | ", Texts(window))}");
            await Task.Delay(100);
        }
    }

    private static bool Matches(ContentControl control, string name) =>
        AutomationProperties.GetName(control) == name
        || control.Content as string == name
        || Descendants(control).OfType<TextBlock>().Any(block => block.Text == name);

    private static List<string> Texts(Window window) =>
        Visible<TextBlock>(window).Select(block => block.Text).Where(text => !string.IsNullOrWhiteSpace(text)).ToList();

    private static IEnumerable<T> Visible<T>(DependencyObject root) where T : FrameworkElement =>
        Descendants(root).OfType<T>().Where(element => element.IsVisible);

    // A menu that opens over the window, such as the list of nodes to add, isn't inside it: it's a
    // small window of its own. Looking through the window looks through those too.
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root) =>
        root is Window
            ? Below(root).Concat(PresentationSource.CurrentSources.OfType<HwndSource>()
                .Select(source => source.RootVisual)
                .Where(over => over is not null and not Window)
                .SelectMany(over => Below(over!)))
            : Below(root);

    private static IEnumerable<DependencyObject> Below(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Below(child)) yield return deeper;
        }
    }

    // Drawn from the window itself, so nothing on the real screen is ever captured.
    private static void SaveImage(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var size = new Size(content.ActualWidth, content.ActualHeight);

        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(window.Background, null, new Rect(size));

            // Measured from the window's corner. Left to itself, a brush crops to whatever was
            // painted and stretches that, which loses the margins of a nearly empty screen.
            var brush = new VisualBrush(content)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(size),
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
            context.DrawRectangle(brush, null, new Rect(size));
        }

        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    [GeneratedRegex(@"""([^""]*)""|\S+")]
    private static partial Regex Tokens();

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(nint window, int index);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
}
#endif
