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
///   sleep 500           window "Title"     (the window the next lines act on, by its title; windowlike "Start" by the start of it)
///   scroll top          scroll end         (every scrolling area in the window)
///   usage               (each login's limits as last heard, without opening the Usage page; usage "Login" "text" fails unless its line says that)
///   login "Label" folder   (a Claude login whose program is a script in the data folder, saying what programs/folder/status.json says)
///   heard "Login" seven_day 100 30   (as if its program said a limit: percent used, hours until it resets)
///   seed "Name" 300 12 300   (a made-up project with that many tasks, milestones and assets; a fifth word names its engine)
///   file "path" "text"  (a file in the data folder)
///   tool blender "program" "reference"   (sets a tool up, its paths in the data folder; a blank reference is where it usually keeps one)
///   faketool godot      (a made-up tool as Godot, its MCP served in the app and a windowless program running; then 404 3, busy 2, program or cancelled)
///   briefing "Project" "text"   briefingnot "Project" "text"   (what the project's agents are told says that, or doesn't)
///   skillsays "Project" skill-name "text"   (the project's agents get that skill, and it says that)
///   open "Name"         (its page, as if its repo's check had found everything in order)
///   timed 500 click "X" (any command, then how long until the page settles, failing past 500 ms)
///   expect "text"       expectnot "text"   (fails unless something shown says exactly that, or nothing does)
///   inview "text"       (fails unless it's in sight, not scrolled away)
///   migrate             (gives assets without a status the one they have in effect, as starting does)
///   testsfile "folder" "text"   (its .agent-orange/tests.json's time limits, or why it isn't valid, say that)
///   testmenu "Project" running   testrunstate passed   runwindows 1   (Run tests on…, as if linked to Office PC with a run in that state)
///   testchecks "Project" failed   testrunwindow "Project" check-run   (made-up runs whose checks flip, and a run's window)
///   gitrepo "folder" "origin"   gitclone "from" "to"   borrow "folder"   fileis "path" "text"   (repos in the data folder, the clone testruns borrow from, a file's text)
///   testrun "folder" "Passed" "text"   (a test run's steps here, from the folder committed as a repo; the verdict and log say that)
///   mcp "Project" update_asset card "Asset 1" status "Needs review"   (an agent's MCP tool; mcprefused expects a refusal)
///   mcpjson "Project" add_cards "{\qcards\q:[...]}"   (the same with its arguments as JSON, for lists; mcpjsonrefused too)
///   said "text"         notsaid "text"     saidunder 8000   (what the last command said has it, hasn't, or is under so many bytes)
///   keep T1 "\(task (\w+)\)"               (the pattern's first group in what was last said, put in later lines for $T1)
///   refuses "Bash" "sed -i s/a/b/ f"       allows "Bash" "ls"   (a shell command past the agents' patch-script hook)
///   agentsetup "Project"                   (the settings, MCP servers and briefing an agent session gets)
///   writefile "Project" "path" "text"      after 1500 command...   (a file in its folder; a command run later, not waited for)
///   writepicture "Project" "path"          stored "Project"   (a small picture in its folder; its milestones and tasks as stored)
///   writedoc "Project" "lessons" "markdown"   (one of its documents, by its key, as a person's edit saves it)
///   limitstop "Project" "task id" ["Login"]   settleasks "Project"   (a conversation on the task a usage limit stops, on that login; when settling asks for a handoff note)
///   agentitems          setting "key" "value"   (the check's conversation, a line for each thing in it; a setting kept on this PC)
///   slowlogin "Label" folder 12   agentsend "Project" "message"   agentpick "Title"   crash
///                       (a login whose program takes a turn slowly; a conversation sent a message for real, in a session of its own;
///                        the conversation to go on with after the app started again; the session and the app ended at once, as a restart of the PC would)
///   detach [wmi]        quote "arg" ...   (a program started on its own, outside the app's tree and jobs; arguments as a command line, read back)
///   agent "Project" "message" [codex]      feed "recorded.jsonl" "part"      agentstate
///                       (a conversation sent a message with no program behind it, what its program says fed in from a recording beside the script, and how it stands)
///   (In said, refuses, allows and writefile, \n is a line break, \t a tab and \q a double quote.)
/// </summary>
internal static partial class Headless
{
    private const int ExStyleIndex = -20;
    private const int NoActivate = 0x08000000;
    private const int ToolWindow = 0x00000080;
    private static TimeSpan WaitLimit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RunLimit = TimeSpan.FromMinutes(5);

    private static readonly Queue<string> Answers = new();
    private static string? _borrowForRuns;
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
                // By the start of its title, for one whose title says a time.
                if (parts[0] == "windowlike")
                {
                    target = await UntilThereAsync(window, () => Application.Current.Windows.OfType<Window>().FirstOrDefault(each => each.Title.StartsWith(parts[1], StringComparison.Ordinal) && each.IsLoaded), $"a window called {parts[1]}…");
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

    // What the last command said, for said, notsaid and saidunder to look at.
    private static string _lastSaid = "";

    // What keep took from what was said, by name, put in later lines for $name.
    private static readonly Dictionary<string, string> Kept = [];

    private static async Task RunCommandAsync(Window window, string[] parts, Action<string> say, string output)
    {
        foreach (var (name, value) in Kept.OrderByDescending(each => each.Key.Length)) parts = [.. parts.Select(part => part.Replace($"${name}", value, StringComparison.Ordinal))];
        if (parts[0] is "said" or "notsaid" or "saidunder")
        {
            CheckSaid(parts, say);
            return;
        }
        // keep T1 "Block out \(task (\w+)\)": the first group of the pattern in what the last command said.
        if (parts[0] == "keep")
        {
            var found = Regex.Match(_lastSaid, Unescape(parts[2]));
            if (!found.Success) throw new InvalidOperationException($"It didn't say anything like {parts[2]}.");
            Kept[parts[1]] = found.Groups.Count > 1 ? found.Groups[1].Value : found.Value;
            say($"{parts[1]} = {Kept[parts[1]]}");
            return;
        }
        var heard = new System.Text.StringBuilder();
        await RunOneAsync(window, parts, line =>
        {
            heard.AppendLine(line);
            say(line);
        }, output);
        _lastSaid = heard.ToString();
        await Task.Delay(100);
    }

    // Fails unless what the last command said has this in it, or hasn't, or is under so many bytes.
    private static void CheckSaid(string[] parts, Action<string> say)
    {
        switch (parts[0])
        {
            // Lines end in \n here, whatever they end in as said.
            case "said" when !_lastSaid.Replace("\r\n", "\n").Contains(Unescape(parts[1]), StringComparison.Ordinal):
                throw new InvalidOperationException($"It didn't say {parts[1]}.");
            case "notsaid" when _lastSaid.Replace("\r\n", "\n").Contains(Unescape(parts[1]), StringComparison.Ordinal):
                throw new InvalidOperationException($"It said {parts[1]}.");
            case "saidunder":
                var size = System.Text.Encoding.UTF8.GetByteCount(_lastSaid);
                say($"It said {size} bytes.");
                if (size >= int.Parse(parts[1])) throw new InvalidOperationException($"It said {size} bytes, not under {parts[1]}.");
                break;
        }
    }

    // The lines of one part of a recording, the part after "# name" up to the next "# ".
    private static List<string> FeedLines(string file, string part)
    {
        var lines = new List<string>();
        var inPart = false;
        foreach (var line in File.ReadAllLines(file))
        {
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                inPart = line[2..].Trim() == part;
                continue;
            }
            if (inPart && line.Trim().Length > 0) lines.Add(line);
        }
        if (lines.Count == 0) throw new InvalidOperationException($"{Path.GetFileName(file)} has no part called {part}.");
        return lines;
    }

    // A command is one line with no double quotes in its words, so \n stands for a line break, \t for a tab and \q for a double quote.
    private static string Unescape(string text) => text.Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\q", "\"");

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
                // usage "Login" "text" fails unless that login's line says it.
                if (Application.Current.Windows.OfType<MainWindow>().FirstOrDefault() is not { } main) throw new InvalidOperationException("The main window isn't open.");
                foreach (var login in main.AllAgentLogins())
                {
                    var limits = main.Usage.For(login.Id).Where(limit => limit.Used is not null).ToList();
                    var known = limits.Select(limit =>
                        $"{limit.Name} {100 - limit.Used:0}% left" + (limit.ResetsAt is { } at ? $", resets {at.ToLocalTime():M/d HH:mm}" : "") + (limit.Blocked ? ", blocked" : ""));
                    var usageLine = $"{login.Label}: " + (limits.Count > 0 ? string.Join("; ", known) : main.Usage.CheckedAt(login.Id) is null ? "not checked yet" : "it didn't say")
                        + (limits.Count > 0 && main.Usage.AsLastHeard(login.Id) && main.Usage.HeardAt(login.Id) is { } heard ? $" (as last heard, {heard.ToLocalTime():M/d HH:mm})" : "")
                        + (main.Usage.NotSignedIn(login.Id) is { } why ? $"; {why}" : "");
                    say(usageLine);
                    if (parts.Length > 2 && login.Label == parts[1] && !usageLine.Contains(parts[2], StringComparison.Ordinal)) throw new InvalidOperationException($"{login.Label}'s usage doesn't say {parts[2]}.");
                }
                break;
            // A login whose program is a script in the data folder, saying whatever programs/<folder>/status.json
            // says about its login, and nothing about its usage: login "Claude (check)" claude-check. Checks only.
            case "login" when Active:
                Main().AddLoginForChecks(parts[1], parts[2]);
                break;
            // As if a login's program said one of its limits: heard "Login" seven_day 100 30 (percent used, hours until it resets).
            case "heard" when Active:
                var heardLogin = Main().AllAgentLogins().First(each => each.Label == parts[1]);
                var used = double.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
                Main().Usage.Update(heardLogin.Id, [new AgentLimit(parts[2], used, DateTimeOffset.UtcNow.AddHours(double.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture)), used >= 100)], complete: true);
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
            // A made-up tool, its MCP served here, set up as this engine or tool: faketool godot [404 3 | busy 2 | program | cancelled].
            case "faketool":
                say(Main().FakeToolForChecks(parts[1], parts[2..]));
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
            // The same, with the arguments as one JSON object, for lists: mcpjson "Project" add_cards "{\qcards\q:[...]}".
            case "mcpjson" or "mcpjsonrefused":
                var arguments = System.Text.Json.Nodes.JsonNode.Parse(Unescape(parts[3])) as System.Text.Json.Nodes.JsonObject ?? throw new InvalidOperationException("The arguments aren't a JSON object.");
                var (answered, turnedDown) = await Main().CallMcpForChecks(parts[1], parts[2], arguments);
                say($"{parts[2]}{(turnedDown ? " refused" : "")}: {answered}");
                if (turnedDown != (parts[0] == "mcpjsonrefused")) throw new InvalidOperationException(turnedDown ? $"{parts[2]} refused: {answered}" : $"{parts[2]} wasn't refused.");
                break;
            // A shell command as an agent's Claude Code would run it, past the hook Agent Orange gives it:
            // refuses "Bash" "sed -i s/a/b/ f.txt" fails unless it's stopped, and allows unless it isn't.
            case "refuses" or "allows":
                using (var input = new StringReader(new System.Text.Json.Nodes.JsonObject { ["tool_name"] = parts[1], ["tool_input"] = new System.Text.Json.Nodes.JsonObject { ["command"] = Unescape(parts[2]) } }.ToJsonString()))
                using (var told = new StringWriter())
                {
                    var stopped = PatchScripts.RunHook(input, told) == 2;
                    say($"{parts[2]}: {(stopped ? told.ToString().Trim() : "allowed")}");
                    if (stopped != (parts[0] == "refuses")) throw new InvalidOperationException(stopped ? $"It stopped {parts[2]}." : $"It let {parts[2]} through.");
                }
                break;
            // What an agent session of the project is given: its Claude Code settings, its MCP servers and its briefing.
            case "agentsetup":
                say(Main().AgentSetupForChecks(parts[1]));
                break;
            // Fetches ripgrep's release into the agent files folder, as the app does for sessions. It
            // downloads from GitHub, so it's for trying by hand, not for the checks.
            case "ripgrep":
                var agentFolder = Path.Combine(DataFolder, "agent");
                say((await Ripgrep.EnsureAsync(agentFolder)) is { } failed ? $"ripgrep: {failed}" : $"ripgrep: {(Ripgrep.IsIn(Ripgrep.Folder(agentFolder)) ? "in " + Ripgrep.Folder(agentFolder) : "on PATH already")}");
                break;
            // What use_tool adds to a relayed tool's error: mismatch "its_tool" "its input schema" "the arguments given", as JSON.
            case "mismatch":
                say(MainWindow.ArgumentsMismatch(parts[1], System.Text.Json.Nodes.JsonNode.Parse(Unescape(parts[2])) as System.Text.Json.Nodes.JsonObject, System.Text.Json.Nodes.JsonNode.Parse(Unescape(parts[3])) as System.Text.Json.Nodes.JsonObject ?? []) ?? "They match.");
                break;
            // Agent Orange's MCP tools, as a session lists them.
            case "mcptools":
                say(await Main().McpToolsForChecks());
                break;
            // A conversation Claude Code sums up to make room, and the next two messages to it, as they'd be sent.
            case "summary":
                var summed = new AgentConversation { Id = "check", ProjectId = "check" };
                summed.ReadForChecks("""{"type":"system","subtype":"compact_boundary","compact_metadata":{"pre_tokens":180000,"post_tokens":20000}}""");
                say($"Next: {summed.AfterSummary("first")}");
                say($"Then: {summed.AfterSummary("second")}");
                break;
            // A conversation about a project with a message sent but no program behind it, opened in the
            // agents window if it's open: agent "Project" "Build the game", or with codex after it, as Codex.
            case "agent":
                Main().AgentForChecks(parts[1], parts[2], parts.Length > 3 && parts[3] == "codex");
                break;
            // What its program says, from a part of a file of recorded lines beside the script, one event
            // a line, each part after a line "# name": feed "background-work.jsonl" "started".
            case "feed":
                foreach (var recorded in FeedLines(Path.Combine(Path.GetDirectoryName(_script)!, parts[1]), parts[2])) Main().CheckConversation.ReadForChecks(recorded);
                break;
            // What the conversation would have sent its program, and how it stands.
            case "agentstate":
                var checking = Main().CheckConversation;
                foreach (var wrote in checking.WrittenForChecks) say($"Sent: {wrote}");
                checking.WrittenForChecks.Clear();
                say($"Working: {(checking.IsWorking ? "yes" : "no")}, in the background: {checking.PendingWork.Count}, waited on: {checking.BackgroundJobs}, waiting on it: {(checking.IsWaitingOnWork ? "yes" : "no")}, unseen: {(checking.Unseen ? "yes" : "no")}");
                foreach (var work in checking.Work) say($"{work.Kind} {(work.IsRunning ? "running" : "ended")}{(work.Awaited ? ", awaited" : "")}{(work.InBackground ? "" : ", holds the turn")}: {work.Title}{(work.Command is { } line ? $" | {line}" : "")}");
                break;
            // Writes a file in the project's folder, such as one an agent waits for.
            case "writefile":
                Main().WriteFileForChecks(parts[1], parts[2], Unescape(parts[3]));
                break;
            // A project's document as a person's edit saves it: writedoc "Project" "lessons" "## Rules\n- ..."
            case "writedoc":
                Main().WriteDocumentForChecks(parts[1], parts[2], Unescape(parts[3]));
                break;
            // A conversation working on a task that a usage limit stops: limitstop "Project" "task id".
            case "limitstop":
                say(Main().LimitStopForChecks(parts[1], parts[2], parts.Length > 3 ? parts[3] : null));
                break;
            // The check's conversation, a line for each thing in it, with its login.
            case "agentitems":
                say(Main().ConversationItemsForChecks());
                break;
            // A setting kept on this PC: setting "agents_switch_login_at_limit" "1".
            case "setting":
                Main().SetSettingForChecks(parts[1], parts[2]);
                break;
            // A login whose program takes a turn slowly, as Claude Code would: slowlogin "Claude (slow)" slow 12.
            case "slowlogin" when Active:
                Main().AddSlowLoginForChecks(parts[1], parts[2], int.Parse(parts[3]));
                break;
            // A new conversation sent a message for real, its program started in a session of its own.
            case "agentsend":
                await Main().AgentSendForChecks(parts[1], parts[2]);
                break;
            // The conversation later lines are about, from those read as the app started, by its title.
            case "agentpick":
                Main().PickConversationForChecks(parts[1]);
                break;
            // As if the PC restarted: the conversation's session ended, and the app with it, without saving.
            case "crash" when Active:
                Main().CrashForChecks();
                break;
            // Ends the conversation's program, so nothing a check started is left running.
            case "agentend":
                Main().CheckConversation.End();
                break;
            // A program started on its own (DetachedProcess), with WMI if asked: detach, or detach wmi.
            case "detach":
                var viaWmi = parts.Length > 1 && parts[1] == "wmi";
                var (detached, notDetached) = await DetachedProcess.StartAsync(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c ping -n 3 127.0.0.1 >nul", DataFolder, viaWmi);
                if (detached is not { } started) throw new InvalidOperationException($"It didn't start: {notDetached}");
                say($"Started{(viaWmi ? " by WMI" : "")}: yes. A child of Agent Orange: {(ParentOf(started) == Environment.ProcessId ? "yes" : "no")}. In a job: {InAJob(started)}, as Agent Orange is: {InAJob(Environment.ProcessId)}.");
                break;
            // Arguments put on a command line as a program is started on its own, and read back as Windows reads them.
            case "quote":
                var given = parts[1..].Select(Unescape).ToArray();
                var quoted = string.Join(' ', given.Select(DetachedProcess.Quote));
                say(quoted);
                say($"Read back: {(ReadBack($"program {quoted}").Skip(1).SequenceEqual(given) ? "the same" : "DIFFERENT")}");
                break;
            // Whether settling asks a conversation for its handoff note, in the cases that decide it.
            case "settleasks":
                say(await Main().SettleAsksForChecks(parts[1]));
                break;
            // A small picture in the project's folder, such as a screenshot an agent took.
            case "writepicture":
                Main().WritePictureForChecks(parts[1], parts[2]);
                break;
            // The project's milestones and tasks as stored, for said to look at.
            case "stored":
                say(Main().StoredForChecks(parts[1]));
                break;
            // Runs a command later, without waiting for it: after 1500 writefile "Project" "out.log" "done"
            case "after":
                var later = parts[2..];
                _ = Task.Delay(int.Parse(parts[1])).ContinueWith(_ => window.Dispatcher.InvokeAsync(() => RunOneAsync(window, later, say, output)), TaskScheduler.Default);
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
                var runSettings = new TestMachineSettings(true, "Check PC", [], Path.Combine(DataFolder, "test-runs"), BorrowFrom: _borrowForRuns is { } borrowing ? new() { ["check-project"] = borrowing } : null);
                var (runVerdict, runNote, runLog) = await TestRunner.RunStepsForChecksAsync(runSettings, runRepo, runCommit, "Check project");
                say(runLog.TrimEnd());
                say($"Verdict: {TestRunner.VerdictLabel(runVerdict)}{(runNote is null ? "" : $". {runNote}")}");
                if (TestRunner.VerdictLabel(runVerdict) != parts[2]) throw new InvalidOperationException($"The run was {TestRunner.VerdictLabel(runVerdict)}, not {parts[2]}.");
                if (parts.Skip(3).FirstOrDefault(text => !runLog.Contains(text, StringComparison.Ordinal)) is { } unsaid) throw new InvalidOperationException($"The run's log doesn't say {unsaid}.");
                break;
            // A folder in the data folder made a repo with all in it committed, and its origin set if given: gitrepo "folder" "origin".
            case "gitrepo":
                if (await GitTools.CommitAllForChecksAsync(Path.Combine(DataFolder, parts[1]), parts.Length > 2 ? parts[2] : null) is null) throw new InvalidOperationException($"{parts[1]} couldn't be committed.");
                break;
            // A clone of one folder in the data folder into another, as a person's own clone: gitclone "from" "to".
            case "gitclone":
                if (!await GitTools.CloneForChecksAsync(Path.Combine(DataFolder, parts[1]), Path.Combine(DataFolder, parts[2]))) throw new InvalidOperationException($"{parts[1]} couldn't be cloned.");
                break;
            // The clone later testruns borrow from, a folder in the data folder, or "" for none.
            case "borrow":
                _borrowForRuns = parts[1].Length > 0 ? Path.Combine(DataFolder, parts[1]) : null;
                break;
            // Fails unless a file in the data folder has exactly this text.
            case "fileis":
                var fileText = File.Exists(Path.Combine(DataFolder, parts[1])) ? await File.ReadAllTextAsync(Path.Combine(DataFolder, parts[1])) : null;
                say($"{parts[1]}: {fileText ?? "not there"}");
                if (fileText != parts[2]) throw new InvalidOperationException($"{parts[1]} doesn't say {parts[2]}.");
                break;
            // Run tests on… for a project, as if linked to Office PC with a run of the commit in that state: testmenu "Project" running.
            case "testmenu" when Active:
                Main().OpenTestMenuForChecks(parts[1], parts[2]);
                break;
            // That made-up run changes state, as the server would say: testrunstate passed.
            case "testrunstate" when Active:
                Main().SetTestRunForChecks(parts[1]);
                break;
            // As if linked to Office PC and Laptop, with runs of the project's commit whose checks flip,
            // the latest by an agent in that state: testchecks "Project" failed.
            case "testchecks" when Active:
                await Main().SetUpTestChecksForChecks(parts[1], parts[2]);
                break;
            // Opens a made-up run's window, as the run list does: testrunwindow "Project" check-run.
            case "testrunwindow" when Active:
                Main().OpenTestRun(parts[2], parts[1]);
                break;
            // Fails unless that many test run windows are open.
            case "runwindows":
                say($"Run windows open: {Main().TestRunWindowsForChecks}");
                if (Main().TestRunWindowsForChecks != int.Parse(parts[1])) throw new InvalidOperationException($"{Main().TestRunWindowsForChecks} run windows are open, not {parts[1]}.");
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

    // The process that started this one, as Windows keeps it.
    private static int ParentOf(int processId)
    {
        using var process = System.Diagnostics.Process.GetProcessById(processId);
        var basic = new nint[6]; // PROCESS_BASIC_INFORMATION, whose last field is the parent's id.
        return NtQueryInformationProcess(process.Handle, 0, basic, basic.Length * nint.Size, out _) == 0 ? (int)basic[5] : -1;
    }

    private static string InAJob(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return IsProcessInJob(process.Handle, 0, out var inJob) ? (inJob ? "yes" : "no") : "can't tell";
        }
        catch (ArgumentException)
        {
            return "it's ended";
        }
    }

    // A command line split up as Windows programs split theirs.
    private static string[] ReadBack(string commandLine)
    {
        var list = CommandLineToArgvW(commandLine, out var count);
        try
        {
            return [.. Enumerable.Range(0, count).Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(list, index * nint.Size))!)];
        }
        finally
        {
            LocalFree(list);
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint process, int infoClass, nint[] info, int length, out int returned);

    [DllImport("kernel32.dll")]
    private static extern bool IsProcessInJob(nint process, nint job, out bool inJob);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(nint window, int index);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
}
#endif
