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
///   sleep 500           window "Title"     (the window the next lines act on, by its title)
///   scroll top          scroll end         (every scrolling area in the window)
///   usage               (each login's limits as last heard, without opening the Usage page)
///   seed "Name" 300 12 300   (a made-up project with that many tasks, milestones and assets; a fifth word names its engine)
///   file "path" "text"  (a file in the data folder)
///   tool blender "program" "reference"   (sets a tool up, its paths in the data folder; a blank reference is where it usually keeps one)
///   briefing "Project" "text"   briefingnot "Project" "text"   (what the project's agents are told says that, or doesn't)
///   skillsays "Project" skill-name "text"   (the project's agents get that skill, and it says that)
///   open "Name"         (its page, as if its repo's check had found everything in order)
///   timed 500 click "X" (any command, then how long until the page settles, failing past 500 ms)
///   expect "text"       expectnot "text"   (fails unless something shown says exactly that, or nothing does)
///   inview "text"       (fails unless it's in sight, not scrolled away)
///   migrate             (gives assets without a status the one they have in effect, as starting does)
///   testsfile "folder" "text"   (its .agent-orange/tests.json's time limits, or why it isn't valid, say that)
///   testrun "folder" "Passed" "text"   (a test run's steps here, from the folder committed as a repo; the verdict and log say that)
///   mcp "Project" update_asset card "Asset 1" status "Needs review"   (an agent's MCP tool; mcprefused expects a refusal)
/// </summary>
internal static partial class Headless
{
    private const int ExStyleIndex = -20;
    private const int NoActivate = 0x08000000;
    private const int ToolWindow = 0x00000080;
    private static TimeSpan WaitLimit = TimeSpan.FromSeconds(60);
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

    /// <summary>
    /// The drive tool said beforehand what the next dialog should get, so the live dev app answers
    /// that instead of showing a real dialog on the person's screen.
    /// </summary>
    public static bool Answering => Active || Answers.Count > 0;

    public static void Attach(Window window)
    {
        KeepOffScreen(window);
        window.Loaded += async (_, _) => await RunAsync(window);
    }

    /// <summary>Another window the app opens, kept off the screen and from taking focus as the main one is.</summary>
    public static void KeepOffScreen(Window window)
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
    }

    private static async Task RunAsync(Window window)
    {
        var failed = false;
        try
        {
            await Task.Delay(500);
            var target = window;
            foreach (var line in await File.ReadAllLinesAsync(_script))
            {
                var parts = Tokens().Matches(line).Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Value).ToArray();
                if (parts.Length == 0 || parts[0].StartsWith('#')) continue;
                if (parts[0] == "window")
                {
                    target = await UntilThereAsync(window, () => Application.Current.Windows.OfType<Window>().FirstOrDefault(each => each.Title == parts[1] && each.IsLoaded), $"the window {parts[1]}");
                    continue;
                }
                await RunCommandAsync(target, parts, line => _log?.WriteLine(line), _output);
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

    private static async Task RunCommandAsync(Window window, string[] parts, Action<string> say, string output)
    {
        await RunOneAsync(window, parts, say, output);
        await Task.Delay(100);
    }

    private static MainWindow Main() =>
        Application.Current.Windows.OfType<MainWindow>().FirstOrDefault() ?? throw new InvalidOperationException("The main window isn't open.");

    // How long a command takes until the page has settled, and the longest the window couldn't
    // answer meanwhile, which is what feels like a freeze.
    private static async Task TimedAsync(Window window, int limit, string[] command, Action<string> say, string output)
    {
        var longest = 0.0;
        using var stop = new CancellationTokenSource();
        var dispatcher = window.Dispatcher;
        var watcher = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var waited = System.Diagnostics.Stopwatch.StartNew();
                await dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Input);
                longest = Math.Max(longest, waited.Elapsed.TotalMilliseconds);
                await Task.Delay(5);
            }
        });
        var took = System.Diagnostics.Stopwatch.StartNew();
        await RunOneAsync(window, command, say, output);
        var main = Main();
        do await dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        while (main.PageIsLoading && took.Elapsed < WaitLimit && await Task.Delay(10).ContinueWith(_ => true));
        took.Stop();
        stop.Cancel();
        await watcher;
        say($"{string.Join(' ', command)}: settled in {took.ElapsedMilliseconds} ms, the longest the window didn't answer was {longest:0} ms");
        if (took.ElapsedMilliseconds > limit || longest > limit) throw new InvalidOperationException($"{string.Join(' ', command)} took longer than {limit} ms.");
    }

    private static async Task RunOneAsync(Window window, string[] parts, Action<string> say, string output)
    {
        switch (parts[0])
        {
            case "click":
                var clicked = await FindButtonAsync(window, parts[1]);
                if (clicked is ToggleButton toggle) toggle.IsChecked = toggle.IsChecked != true; // As a click flips it.
                else clicked.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                break;
            case "set":
                // By its place among the fields shown, or by its name, such as "Commit message".
                var field = await UntilThereAsync(window, () => int.TryParse(parts[1], out var place)
                    ? Visible<TextBox>(window).ElementAtOrDefault(place)
                    : Visible<TextBox>(window).FirstOrDefault(box => System.Windows.Automation.AutomationProperties.GetName(box) == parts[1]), $"field {parts[1]}");
                field.Text = parts[2].Replace("\\n", "\n"); // A command is one line, so \n stands for a line break.

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
            case "wait" when parts.Length < 2:
                await Task.Delay(500);
                break;
            case "wait":
                await WaitForAsync(window, parts[1], true);
                break;
            case "waitgone":
                await WaitForAsync(window, parts[1], false);
                break;
            case "has":
                say($"{parts[1]}: {(Texts(window).Any(text => text.Contains(parts[1], StringComparison.Ordinal)) ? "yes" : "NO")}");
                break;
            case "say":
                say(parts[1]);
                break;
            case "last":
                say(Texts(window).LastOrDefault() ?? "");
                break;
            case "texts":
                say(string.Join(" | ", Texts(window)));
                break;
            // Each login's limits as the app last heard them, without opening the Usage page.
            case "usage":
                if (Application.Current.Windows.OfType<MainWindow>().FirstOrDefault() is not { } main) throw new InvalidOperationException("The main window isn't open.");
                foreach (var login in main.AllAgentLogins())
                {
                    var limits = main.Usage.For(login.Id).Where(limit => limit.Used is not null).ToList();
                    var known = limits.Select(limit =>
                        $"{limit.Name} {100 - limit.Used:0}% left" + (limit.ResetsAt is { } at ? $", resets {at.ToLocalTime():M/d HH:mm}" : "") + (limit.Blocked ? ", blocked" : ""));
                    say($"{login.Label}: " + (limits.Count > 0 ? string.Join("; ", known) : main.Usage.CheckedAt(login.Id) is null ? "not checked yet" : "it didn't say"));
                }
                break;
            // The words in each visible document editor, whole, as a person reads by scrolling it.
            case "read":
                foreach (var editor in Visible<RichTextBox>(window))
                {
                    say(new System.Windows.Documents.TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text);
                    // Pictures aren't text, so say where each one is, to open it.
                    var pending = new Stack<object>([editor.Document]);
                    while (pending.Count > 0)
                    {
                        var item = pending.Pop();
                        if (item is FrameworkElement { Tag: MarkdownText.PictureMark mark }) say($"Picture: {mark.Address}");
                        else if (item is Image { Source: System.Windows.Media.Imaging.BitmapImage { UriSource: { IsFile: true } file } }) say($"Picture: {file.LocalPath}");
                        if (item is DependencyObject parent)
                            foreach (var child in LogicalTreeHelper.GetChildren(parent).Cast<object>().Reverse()) pending.Push(child);
                    }
                }
                // And the words in boxes for more than a line, such as a task's notes, which show only in part.
                foreach (var notes in Visible<TextBox>(window).Where(each => each.AcceptsReturn && each.Text.Length > 0)) say(notes.Text);
                break;
            case "shot":
                await Task.Delay(150);
                // A name for it in the output folder, or a whole path; "shot" alone names it by the time.
                var named = parts.Length > 1 ? parts[1] : $"shot-{DateTime.Now:HHmmss}";
                var shot = Path.Combine(output, named.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? named : $"{named}.png");
                SaveImage(window, shot);
                say(shot);
                break;
            case "sleep":
                await Task.Delay(int.Parse(parts[1]));
                break;
            // A made-up project of this many tasks, milestones and assets, to try big pages on.
            case "seed":
                if (parts.Length > 5) Main().SeedForChecks(parts[1], int.Parse(parts[2]), int.Parse(parts[3]), int.Parse(parts[4]), parts[5]);
                else Main().SeedForChecks(parts[1], int.Parse(parts[2]), int.Parse(parts[3]), int.Parse(parts[4]));
                say($"Made {parts[1]}");
                break;
            // A file in the check's data folder, with this text in it.
            case "file":
                var made = Path.Combine(DataFolder, parts[1]);
                Directory.CreateDirectory(Path.GetDirectoryName(made)!);
                await File.WriteAllTextAsync(made, parts[2]);
                break;
            // An engine or tool set up with its program and the API reference the person chose, both in the data folder.
            case "tool":
                Main().SetUpToolForChecks(parts[1], parts[2], parts[3]);
                break;
            // Fails unless the briefing a project's agents get says this, or, with briefingnot, unless it doesn't.
            case "briefing" or "briefingnot":
                var briefing = Main().BriefForChecks(parts[1]).Briefing;
                if (briefing.Contains(parts[2], StringComparison.Ordinal) != (parts[0] == "briefing"))
                {
                    throw new InvalidOperationException($"The briefing for {parts[1]} {(parts[0] == "briefing" ? "doesn't say" : "says")} {parts[2]}.");
                }
                say($"{parts[0]} {parts[2]}: yes");
                break;
            // Fails unless a project's agents get the skill, and its SKILL.md says this.
            case "skillsays":
                var skills = Main().BriefForChecks(parts[1]).Skills;
                if (!skills.TryGetValue(parts[2], out var skillText)) throw new InvalidOperationException($"{parts[1]}'s agents don't get {parts[2]}.");
                if (!skillText.Contains(parts[3], StringComparison.Ordinal)) throw new InvalidOperationException($"{parts[2]} doesn't say {parts[3]}.");
                say($"{parts[2]} says {parts[3]}: yes");
                break;
            // Its page, without a check of its repo, which would ask GitHub.
            case "open":
                Main().OpenForChecks(parts[1]);
                break;
            case "timed":
                await TimedAsync(window, int.Parse(parts[1]), parts[2..], say, output);
                break;
            // Fails unless something shown says exactly this, or, with expectnot, unless nothing does.
            case "expect":
                await WaitUntilAsync(() => Texts(window).Contains(parts[1]), $"{parts[1]} to be shown", window);
                break;
            case "expectnot":
                await WaitUntilAsync(() => !Texts(window).Contains(parts[1]), $"{parts[1]} to go", window);
                break;
            // Fails unless it's in sight: not scrolled away, nor under anything that scrolls.
            case "inview":
                var seen = Visible<TextBlock>(window).Where(block => block.Text == parts[1]).Any(block => InSight(window, block));
                say($"{parts[1]} in view: {(seen ? "yes" : "NO")}");
                if (!seen) throw new InvalidOperationException($"{parts[1]} isn't in view.");
                break;
            // One of the MCP tools agents get, with its arguments in pairs: mcp "Project" update_asset card "Asset 1" status "To do".
            // mcp fails if the tool refuses, and mcprefused fails unless it does.
            case "mcp" or "mcprefused":
                var (answer, refused) = await Main().CallMcpForChecks(parts[1], parts[2], parts[3..]);
                say($"{parts[2]}{(refused ? " refused" : "")}: {answer}");
                if (refused != (parts[0] == "mcprefused")) throw new InvalidOperationException(refused ? $"{parts[2]} refused: {answer}" : $"{parts[2]} wasn't refused.");
                break;
            // Gives assets without a status the one they have in effect, as starting the app does.
            case "migrate":
                say($"Gave {Main().GiveAssetsAStatusForChecks()} assets a status");
                break;
            // What a folder's .agent-orange/tests.json gives, its time limits or why it isn't valid: fails unless that says this.
            case "testsfile":
                var (testSetup, testsProblem, _) = TestsFile.Read(Path.Combine(DataFolder, parts[1]));
                var testsSay = testSetup?.LimitsText ?? testsProblem ?? $"no {TestsFile.RelativePath}";
                say($"{parts[1]}: {testsSay}");
                if (!testsSay.Contains(parts[2], StringComparison.Ordinal)) throw new InvalidOperationException($"{parts[1]}'s tests file doesn't say {parts[2]}.");
                break;
            // A test run's steps on this PC, from a folder in the data folder committed as a repo, in
            // PowerShell: testrun "repo" "Passed" "what the log says" ... Fails unless the verdict and every text match.
            case "testrun":
                var runRepo = Path.Combine(DataFolder, parts[1]);
                var runCommit = await GitTools.CommitAllForChecksAsync(runRepo) ?? throw new InvalidOperationException($"{parts[1]} couldn't be committed.");
                var runSettings = new TestMachineSettings(true, "Check PC", [], Path.Combine(DataFolder, "test-runs"));
                var (runVerdict, runNote, runLog) = await TestRunner.RunStepsForChecksAsync(runSettings, runRepo, runCommit, "Check project");
                say(runLog.TrimEnd());
                say($"Verdict: {TestRunner.VerdictLabel(runVerdict)}{(runNote is null ? "" : $". {runNote}")}");
                if (TestRunner.VerdictLabel(runVerdict) != parts[2]) throw new InvalidOperationException($"The run was {TestRunner.VerdictLabel(runVerdict)}, not {parts[2]}.");
                if (parts.Skip(3).FirstOrDefault(text => !runLog.Contains(text, StringComparison.Ordinal)) is { } unsaid) throw new InvalidOperationException($"The run's log doesn't say {unsaid}.");
                break;
            case "scroll":
                foreach (var area in Visible<ScrollViewer>(window))
                {
                    if (parts[1] == "top") area.ScrollToTop();
                    else area.ScrollToEnd();
                }
                break;
            default:
                throw new InvalidOperationException($"Unknown command: {parts[0]}");
        }
    }

    // A card's settings button is hidden until hovered, and there's no mouse here.
    private static Task<ButtonBase> FindButtonAsync(Window window, string name) =>
        UntilThereAsync(window, () => Descendants(window).OfType<ButtonBase>().Where(candidate => candidate is Button or ToggleButton).LastOrDefault(candidate =>
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

    private static async Task WaitUntilAsync(Func<bool> done, string what, Window window)
    {
        var limit = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!done())
        {
            if (DateTime.UtcNow > limit) throw new InvalidOperationException($"Waited for {what}. On screen: {string.Join(" | ", Texts(window))}");
            await Task.Delay(100);
        }
    }

    // Inside the window, and inside every scrolling area it's in.
    private static bool InSight(Window window, FrameworkElement element)
    {
        if (window.Content is not FrameworkElement content) return false;
        var bounds = element.TransformToVisual(content).TransformBounds(new Rect(element.RenderSize));
        if (!new Rect(content.RenderSize).Contains(bounds)) return false;
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ScrollViewer scroll) continue;
            var view = scroll.TransformToVisual(content).TransformBounds(new Rect(new Size(scroll.ViewportWidth, scroll.ViewportHeight)));
            if (!view.Contains(bounds)) return false;
        }
        return true;
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

    // A text written as runs, such as a count with smaller words after it, is read whole.
    private static List<string> Texts(Window window) =>
        Visible<TextBlock>(window)
            .Select(block => block.Text is { Length: > 0 } text ? text : new System.Windows.Documents.TextRange(block.ContentStart, block.ContentEnd).Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();

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

    // Drawn from the window itself, so nothing on the real screen is ever captured. Menus open over
    // it are small windows of their own, so each is drawn where it sits, and the picture grows to fit.
    private static void SaveImage(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var size = new Size(content.ActualWidth, content.ActualHeight);
        var layers = new List<(Visual Visual, Rect Bounds)> { (content, new Rect(size)) };
        if (PresentationSource.FromVisual(content) is HwndSource own && own.CompositionTarget is { } target)
        {
            var origin = content.PointToScreen(new Point(0, 0));
            foreach (var source in PresentationSource.CurrentSources.OfType<HwndSource>())
            {
                if (source == own || source.RootVisual is not FrameworkElement over || over is Window || !over.IsVisible) continue;
                var at = over.PointToScreen(new Point(0, 0));
                var offset = target.TransformFromDevice.Transform(new Point(at.X - origin.X, at.Y - origin.Y));
                layers.Add((over, new Rect(offset, new Size(over.ActualWidth, over.ActualHeight))));
            }
        }
        var bounds = layers.Select(layer => layer.Bounds).Aggregate(Rect.Union);

        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            context.DrawRectangle(Brushes.Black, null, bounds);
            context.DrawRectangle(window.Background, null, new Rect(size));
            foreach (var (visual, place) in layers)
            {
                // Measured from each one's corner. Left to itself, a brush crops to whatever was
                // painted and stretches that, which loses the margins of a nearly empty screen.
                var brush = new VisualBrush(visual)
                {
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(place.Size),
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top,
                };
                context.DrawRectangle(brush, null, place);
            }
            context.Pop();
        }

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32);
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
