using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>
/// Settings › Test machines (#93). On a PC that sends tests: T3 Code's "Linked environments" list
/// and its Link dialog, as T3 shows them when it already has a session on the other machine and
/// so mints the code itself (#95): every PC signed in to the same server is the owner's, so there's
/// never a code to type. On a PC that runs them: whether it takes them, which is its consent to
/// being linked, what it offers, and T3's "Authorized clients" with Revoke. Texts are T3's where
/// T3 has the same thing.
/// </summary>
public partial class SettingsModal
{
    private TestMachines _testMachines = null!;
    private Action? _refreshTestMachines;
    private Action? _refreshLinkDialog;
    private string? _startupNote;

    private static readonly Brush SuccessDot = new SolidColorBrush(Color.FromRgb(0x5F, 0xD0, 0x8A));
    private static readonly Brush WarningDot = new SolidColorBrush(Color.FromRgb(0xE8, 0xB0, 0x4B));
    private static readonly Brush DestructiveDot = new SolidColorBrush(Color.FromRgb(0xE8, 0x6B, 0x6B));

    public TestMachines TestMachines
    {
        get => _testMachines;
        set
        {
            _testMachines = value;
            _testMachines.Changed += () =>
            {
                _refreshTestMachines?.Invoke();
                _refreshLinkDialog?.Invoke();
            };
        }
    }

    private UIElement[] TestMachinesPage()
    {
        var settings = TestMachines.Settings;
        var problem = ProblemText();
        _cloneSizes = null; // Measured again each time the page opens, as runs change them.

        void Save(Func<TestMachineSettings, TestMachineSettings> change) => TestMachines.Save(change(TestMachines.Settings));

        var accepting = Toggle(settings.Accepting);
        AutomationProperties.SetName(accepting, "Let other PCs run tests here");
        accepting.Checked += (_, _) =>
        {
            Save(now => now with { Accepting = true });

            // Nobody may be at this PC to start Agent Orange again after a restart.
            if (StartWithWindows.Launcher is not null && !StartWithWindows.IsOn && StartWithWindows.Set(true) is null)
            {
                _startupNote = "Agent Orange now starts with Windows, in the tray, so this PC takes runs again after a restart. You can turn that off in Settings › General.";
                _refreshTestMachines?.Invoke();
            }
        };
        accepting.Unchecked += (_, _) => Save(now => now with { Accepting = false });

        var label = Field(settings.Label);
        label.Width = 300;
        label.MaxLength = MaxDeviceNameLength;
        AutomationProperties.SetName(label, "Test machine name");
        label.LostKeyboardFocus += (_, _) =>
        {
            if (label.Text.Trim().Length == 0) label.Text = TestMachines.Settings.Label;
            else if (label.Text.Trim() != TestMachines.Settings.Label) Save(now => now with { Label = label.Text.Trim() });
        };

        var tags = Field(string.Join(", ", settings.Tags ?? []));
        tags.Width = 300;
        AutomationProperties.SetName(tags, "Tags");
        tags.LostKeyboardFocus += (_, _) => Save(now => now with { Tags = SplitTags(tags.Text) });

        var folder = FolderField(settings.WorkFolder);
        var changeFolder = ActionButton("Change");
        changeFolder.Click += (_, _) =>
        {
            if (PickFolder(TestMachines.Settings.WorkFolder) is not { } picked) return;
            folder.Text = picked;
            folder.ToolTip = picked;
            Save(now => now with { WorkFolder = picked });
        };

        var shell = Choice(TestMachineSettings.Shells, settings.Shell, value => Save(now => now with { Shell = value }));
        AutomationProperties.SetName(shell, "Shell");

        var environment = Field(settings.Environment);
        environment.Width = 300;
        environment.AcceptsReturn = true;
        environment.TextWrapping = TextWrapping.NoWrap;
        environment.MinHeight = 64;
        environment.VerticalContentAlignment = VerticalAlignment.Top;
        AutomationProperties.SetName(environment, "Environment variables");
        environment.LostKeyboardFocus += (_, _) => Save(now => now with { Environment = environment.Text });

        var timeout = Field(settings.TimeoutMinutes?.ToString() ?? "");
        timeout.Width = 80;
        AutomationProperties.SetName(timeout, "Time limit");
        timeout.LostKeyboardFocus += (_, _) =>
        {
            var typed = timeout.Text.Trim();
            if (typed.Length == 0) Save(now => now with { TimeoutMinutes = null });
            else if (int.TryParse(typed, out var minutes) && minutes is >= 1 and <= 24 * 60) Save(now => now with { TimeoutMinutes = minutes });
            else
            {
                ShowProblem(problem, "The time limit is in minutes, from 1 to 1440, or empty to use the project's.");
                return;
            }
            problem.Visibility = Visibility.Collapsed;
        };

        // What only matters while this PC takes runs is shown only then.
        FrameworkElement[] targetRows =
        [
            Row("Name", "Shown to your other PCs.", label),
            Row("Tags", "What this PC offers, as words a project's tests can ask for, such as windows, gpu or node-22. Separate them with commas.", tags),
            Row("Work folder", "Each project gets its own checkout here. A short path keeps clear of tools that can't take long file names.", folder, changeFolder),
            Row("Shell", "What the commands run in here: PowerShell, cmd, or Git Bash.", shell),
            Row("Environment variables", "Set for every run here, one KEY=VALUE a line.", environment),
            Row("Time limit", "Minutes a whole run may take here, from checking out to the last test, in place of the project's limits. Empty uses the project's.", timeout),
        ];
        var thisPc = Card([Row("Let other PCs run tests here", "Your other PCs signed in to this server can link to this one, with no code to type, as this PC answers by itself. Then they can send a commit of a project's repo to test here, in the background, as you, with nobody needing to be here. What runs is what the repo's .agent-orange/tests.json says at that commit, so anyone who can push to the repo decides it. Each run is listed below with exactly what it ran.", accepting), .. targetRows]);
        void ShowTargetRows()
        {
            var rows = (StackPanel)thisPc.Child;
            for (var i = 1; i < rows.Children.Count; i++) ((UIElement)rows.Children[i]).Visibility = accepting.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
        accepting.Checked += (_, _) => ShowTargetRows();
        accepting.Unchecked += (_, _) => ShowTargetRows();
        ShowTargetRows();

        var linker = new StackPanel();
        var target = new StackPanel();
        void Redraw()
        {
            // Not under the person's typing, which drawing again would take away.
            foreach (var (host, parts) in new[] { (linker, (Func<List<UIElement>>)LinkerParts), (target, TargetParts) })
            {
                if (host.IsKeyboardFocusWithin) continue;
                host.Children.Clear();
                foreach (var element in parts()) host.Children.Add(element);
            }
        }
        _refreshTestMachines = Redraw;
        Redraw();
        _ = TestMachines.RefreshAsync();

        // T3's section header, drawn once: the list under it is drawn again as the server answers,
        // which mustn't take the button away between a press and its release.
        var link = PrimaryButton("Link", () => OpenLinkDialog(null));
        AutomationProperties.SetName(link, "Link");
        return
        [
            SectionWithActions("Linked machines", null, link),
            linker,
            Section("This PC"),
            thisPc,
            problem,
            target,
        ];
    }

    // Within what the server takes: 32 tags, each 40 characters at most.
    private static string[] SplitTags(string typed) =>
        [.. typed.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => tag.Length <= 40 && !tag.Any(char.IsControl))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)];

    // The parts that change as the server answers, drawn again each time. As the PC linking: T3's
    // Linked environments, with the links being made in place of the machine's row.
    private List<UIElement> LinkerParts()
    {
        var parts = new List<UIElement>();
        var latest = TestMachines.Latest;
        var me = latest?.Me;
        if (TestMachines.Problem is { } trouble && latest is not null)
        {
            var shown = ProblemText();
            ShowProblem(shown, trouble);
            parts.Add(shown);
        }
        var linked = latest?.Links.Where(each => each.Linker == me).OrderBy(each => each.MachineLabel, StringComparer.CurrentCultureIgnoreCase).ToList() ?? [];
        var linkings = TestMachines.Linkings;
        var rows = new List<FrameworkElement>();
        foreach (var each in linked)
        {
            rows.Add(linkings.FirstOrDefault(making => making.Machine.Install == each.Machine) is { } making ? LinkingRow(making) : LinkedMachineRow(each));
        }
        foreach (var making in linkings.Where(making => linked.All(each => each.Machine != making.Machine.Install))) rows.Add(LinkingRow(making));

        if (rows.Count > 0) parts.Add(Card([.. rows]));
        else if (latest is null) parts.Add(Card(Row(TestMachines.Problem is null ? "Loading linked machines…" : "Could not load linked machines", TestMachines.Problem)));
        else parts.Add(Card(Row("No linked machines", "Link another of your PCs running Agent Orange, so this PC can run a project's tests there. It needs no code: your PCs signed in to this server link to each other by themselves.")));
        if (TestMachines.JustLinked is { } note)
        {
            parts.Add(new TextBlock { Text = note, FontSize = 12.5, Foreground = (Brush)FindResource("TextNormal"), Margin = new Thickness(16, 8, 16, 0) });
        }
        return parts;
    }

    // A link being made: what's happening, with Cancel; or why it wasn't, with Try again.
    private Grid LinkingRow(Linking linking)
    {
        var label = linking.Machine.Label;
        if (linking.Problem is { } problem)
        {
            var again = ActionButton("Try again");
            again.Click += (_, _) => TestMachines.StartLink(linking.Machine, linking.Access);
            var dismiss = ActionButton("Dismiss");
            dismiss.Click += (_, _) => TestMachines.StopLinking(linking);
            return DotRow(DestructiveDot, problem, label, $"{MachineLinks.AccessLabel(linking.Access)} · Could not link. {problem}", again, dismiss);
        }
        var cancel = ActionButton("Cancel");
        cancel.Click += (_, _) => TestMachines.StopLinking(linking);
        return DotRow(WarningDot, linking.Step, label, $"{MachineLinks.AccessLabel(linking.Access)} · Linking… {linking.Step}", cancel);
    }

    // As the target: T3's Authorized clients, with its pairing links first, then what ran here.
    private List<UIElement> TargetParts()
    {
        var parts = new List<UIElement>();
        var latest = TestMachines.Latest;
        var settings = TestMachines.Settings;
        var me = latest?.Me;

        // Runs come in with nobody at this PC, so it has to be running and awake. Said, never changed.
        if (settings.Accepting)
        {
            var notes = new List<string>();
            if (_startupNote is { } started) notes.Add(started);
            else if (StartWithWindows.Launcher is not null && !StartWithWindows.IsOn) notes.Add("Agent Orange doesn't start with Windows here, so after a restart this PC takes no runs until someone opens it. Settings › General can turn that on.");
            if (PowerSettings.SleepsAfterPluggedIn() is { } sleep)
            {
                notes.Add($"This PC goes to sleep after {(int)Math.Round(sleep.TotalMinutes)} minutes without use (Windows power settings), and runs need it awake. Agent Orange doesn't change that: set sleep to Never in Windows Settings › System › Power if it should take runs while nobody's at it.");
            }
            foreach (var note in notes) parts.Add(new TextBlock { Text = note, FontSize = 12.5, Foreground = (Brush)FindResource("TextMuted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 10, 16, 0) });
        }

        // T3's Authorized clients, as it lists a link it minted the code for: a client session per
        // PC, with Revoke, and no pairing links, as there's no code to make.
        var clients = latest?.Links.Where(link => link.Machine == me).ToList() ?? [];
        if (settings.Accepting || clients.Count > 0)
        {
            parts.Add(SectionWithActions("Authorized clients", $"{clients.Count} client{(clients.Count == 1 ? "" : "s")}"));
            var rows = new List<FrameworkElement>();
            foreach (var client in clients) rows.Add(ClientRow(client));
            if (rows.Count == 0) rows.Add(Row("No client sessions.", "Your other PCs signed in to this server link to this one from their Settings › Test machines › Link. This PC answers by itself while it lets other PCs run tests here."));
            parts.Add(Card([.. rows]));
            parts.Add(new TextBlock
            {
                Text = "A PC you revoke can link again while this PC lets others run tests here, as every PC signed in to your server is yours. To keep one out, remove it under Other devices on the Server page.",
                FontSize = 12.5,
                Foreground = (Brush)FindResource("TextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16, 8, 16, 0),
            });
        }

        if (settings.Accepting) parts.AddRange(BorrowParts(settings));
        parts.AddRange(ClonesParts(settings));

        // Exactly what ran here, as any PC can change a project's synced commands.
        var runsHere = TestMachines.RunsHere();
        if (TestMachines.Running is not null || runsHere.Count > 0)
        {
            parts.Add(Section("Runs here"));
            var rows = new List<FrameworkElement>();
            if (TestMachines.Running is { } running)
            {
                var stop = ActionButton("Cancel");
                stop.Click += (_, _) => running.Cancel();
                rows.Add(Row($"Running for {running.Run.LinkerLabel}", $"Commit {ShortCommit(running.Run.Commit)}. Its log streams to the PC that sent it.", stop));
            }
            foreach (var run in runsHere.Take(10))
            {
                var commands = (run.Prepare.Length > 0 ? $"Prepare: {run.Prepare}\n" : "") + (run.Test.Length > 0 ? $"Test: {run.Test}" : "");
                rows.Add(Row(
                    $"{run.Project} · {TestRunner.VerdictLabel(run.Verdict)}{(run.ExitCode is { } exit ? $" (exit code {exit})" : "")}",
                    $"From {run.From}, {When(run.At)}, at {ShortCommit(run.Commit)}, in {run.Shell}{(run.Folder.Length > 0 ? $", in {run.Folder}" : "")}.{(commands.Length > 0 ? "\n" + commands : "")}{(run.Note is { } why ? "\n" + why : "")}"));
            }
            parts.Add(Card([.. rows]));
        }
        return parts;
    }

    // Projects whose folder on this PC is a clone of their repo, found once the page opens (#112).
    private Dictionary<string, string>? _borrowSuggestions;

    private async Task FindBorrowSuggestionsAsync()
    {
        var found = new Dictionary<string, string>();
        foreach (var project in Database.ListProjects())
        {
            if (Database.GetProjectFolder(project.Id) is not { } folder || !System.IO.Directory.Exists(folder)) continue;
            if (await GitTools.OriginForTestsAsync(folder) is ({ } origin, null) && GitTools.SameRepo(origin, project.RepoUrl)) found[project.Id] = folder;
        }
        _borrowSuggestions = found;
        _refreshTestMachines?.Invoke();
    }

    // Clones on this PC a project's first test checkout can copy from instead of downloading the
    // whole repo again. Only the person here chooses one; it's only ever read.
    private List<UIElement> BorrowParts(TestMachineSettings settings)
    {
        var parts = new List<UIElement>();
        if (_borrowSuggestions is null)
        {
            _borrowSuggestions = [];
            _ = FindBorrowSuggestionsAsync();
        }
        // Empty is chosen not to, so it isn't suggested as if new.
        void Choose(string projectId, string? folder)
        {
            var now = TestMachines.Settings;
            TestMachines.Save(now with { BorrowFrom = new Dictionary<string, string>(now.BorrowFrom ?? []) { [projectId] = folder ?? "" } });
        }

        var rows = new List<FrameworkElement>();
        foreach (var project in Database.ListProjects().OrderBy(each => each.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var chosen = settings.BorrowFor(project.Id);
            var declined = settings.BorrowFrom?.TryGetValue(project.Id, out var said) == true && said.Length == 0;
            var suggested = _borrowSuggestions.GetValueOrDefault(project.Id);
            if (chosen is null && suggested is null) continue;

            var pick = ActionButton(chosen is null ? "Choose…" : "Change");
            pick.Click += (_, _) =>
            {
                if (PickFolder(chosen ?? suggested ?? "") is { } picked) Choose(project.Id, picked);
            };
            if (chosen is not null)
            {
                var stop = ActionButton("Stop");
                stop.Click += (_, _) => Choose(project.Id, null);
                rows.Add(Row(project.Name, $"Copies from {chosen}, so a first checkout here doesn't download the whole repo again, and its large files come from there when it has them. Nothing there is changed.", pick, stop));
            }
            else
            {
                var borrow = ActionButton("Borrow from it");
                borrow.Click += (_, _) => Choose(project.Id, suggested);
                rows.Add(Row(project.Name, $"{suggested} is a clone of its repo on this PC.{(declined ? " Not borrowed from, as chosen." : " A first checkout here could copy from it instead of downloading the whole repo again.")}", borrow, pick));
            }
        }
        if (rows.Count == 0) return parts;
        parts.Add(Section("Borrow from clones here"));
        parts.Add(Card([.. rows]));
        parts.Add(new TextBlock
        {
            Text = "Test runs never work in these folders, as each run checks a commit out by force and cleans up. They only copy from them. Build caches, such as an engine's shared cache, belong in the project's own settings or its prepare command.",
            FontSize = 12.5,
            Foreground = (Brush)FindResource("TextMuted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 8, 16, 0),
        });
        return parts;
    }

    // Each test clone's size, measured a clone at a time in the background once the page opens, as a
    // big one takes a while. -1 when it couldn't be read.
    private Dictionary<string, long>? _cloneSizes;
    private bool _measuringClones;

    private async Task MeasureClonesAsync(Dictionary<string, long> sizes, List<TestClones.Clone> clones)
    {
        _measuringClones = true;
        try
        {
            foreach (var clone in clones.Where(clone => !sizes.ContainsKey(clone.Folder)))
            {
                try
                {
                    sizes[clone.Folder] = await Task.Run(() => TestClones.SizeOf(clone.Folder));
                }
                catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
                {
                    sizes[clone.Folder] = -1;
                }
                if (ReferenceEquals(sizes, _cloneSizes)) _refreshTestMachines?.Invoke();
            }
        }
        finally
        {
            _measuringClones = false;
        }
    }

    // The clones in this PC's work folder, a project each, with the disk each uses and Remove (#93).
    private List<UIElement> ClonesParts(TestMachineSettings settings)
    {
        var parts = new List<UIElement>();
        var clones = TestClones.In(settings.WorkFolder);
        if (clones.Count == 0) return parts;
        var sizes = _cloneSizes ??= [];
        if (!_measuringClones && clones.Any(clone => !sizes.ContainsKey(clone.Folder))) _ = MeasureClonesAsync(sizes, clones);

        var measured = clones.Where(clone => sizes.GetValueOrDefault(clone.Folder, -1) >= 0).Sum(clone => sizes[clone.Folder]);
        var allMeasured = clones.All(clone => sizes.ContainsKey(clone.Folder));
        parts.Add(SectionWithActions("Test clones here", $"{clones.Count} clone{(clones.Count == 1 ? "" : "s")} · {TestClones.SizeText(measured)}{(allMeasured ? "" : " so far")}"));

        var runsHere = TestMachines.RunsHere();
        var rows = new List<FrameworkElement>();
        foreach (var clone in clones)
        {
            var lastRun = runsHere.FirstOrDefault(run => string.Equals(System.IO.Path.TrimEndingDirectorySeparator(run.Folder), clone.Folder, StringComparison.OrdinalIgnoreCase));
            var title = lastRun is { Project.Length: > 0 } ? lastRun.Project : clone.Name;
            var size = sizes.TryGetValue(clone.Folder, out var bytes) ? bytes >= 0 ? TestClones.SizeText(bytes) : "Its size couldn't be read" : "Measuring…";
            var when = lastRun is not null ? $"Last run here {When(lastRun.At)}." : $"Last changed {When(clone.ChangedAt)}.";
            var removing = TestMachines.RemovingClone is { } gone && string.Equals(gone, clone.Folder, StringComparison.OrdinalIgnoreCase);
            var remove = new Button { Style = (Style)FindResource("DangerButton"), Content = removing ? "Removing…" : "Remove" };
            AutomationProperties.SetName(remove, $"Remove {clone.Name}");
            remove.IsEnabled = TestMachines.Running is null && TestMachines.RemovingClone is null;
            remove.Click += (_, _) => ConfirmRemoveClone(clone, title, size);
            rows.Add(Row(title, $"{size}. {when}\n{clone.Folder}", remove));
        }
        parts.Add(Card([.. rows]));
        parts.Add(new TextBlock
        {
            Text = TestMachines.Running is not null
                ? "A test run is going on here, so no clone can be removed until it's done."
                : "Each project's runs here check out in its clone again, so removing one frees its disk until the project's next run here, which clones it again, borrowing from a clone chosen above if there is one.",
            FontSize = 12.5,
            Foreground = (Brush)FindResource("TextMuted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 8, 16, 0),
        });
        return parts;
    }

    private void ConfirmRemoveClone(TestClones.Clone clone, string title, string size)
    {
        var problem = ProblemText();
        var body = new StackPanel { Margin = new Thickness(8, 0, 8, 12), Children = { problem } };
        Button confirm = null!;
        confirm = Clickable("DangerButton", "Remove it", async () =>
        {
            if (!confirm.IsEnabled) return;
            confirm.IsEnabled = false;
            confirm.Content = "Removing…";
            var failed = await TestMachines.RemoveCloneAsync(clone.Folder);
            _cloneSizes?.Remove(clone.Folder); // Measured again, as some of it may be left.
            if (failed is not null)
            {
                confirm.IsEnabled = true;
                confirm.Content = "Remove it";
                ShowProblem(problem, failed);
                return;
            }
            CloseWizard();
            _refreshTestMachines?.Invoke();
        });
        ShowWizard();
        WizardCard.Child = WizardShell(
            CloseWizard,
            $"Remove {title}'s test clone?",
            $"This deletes {clone.Folder} and everything in it ({size.TrimEnd('…')}) from this PC. Nothing outside it is touched: a link in it is removed, never what it points at. The project's next run here clones it again.",
            null,
            body,
            TextButton("Cancel", CloseWizard),
            confirm);
    }

    private static string ShortCommit(string commit) => commit.Length > 12 ? commit[..12] : commit;

    // A section title with its summary and buttons on the right, as T3's settings sections have them.
    private Grid SectionWithActions(string title, string? summary, params Button[] actions)
    {
        var heading = Section(title);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(heading);
        if (summary is not null) left.Children.Add(new TextBlock { Text = summary, FontSize = 12.5, Foreground = (Brush)FindResource("TextMuted"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 0, 12) });
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var action in actions)
        {
            action.Margin = new Thickness(8, 0, 0, 0);
            right.Children.Add(action);
        }
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    private static Border Dot(Brush color, string? tip) => new()
    {
        Width = 8,
        Height = 8,
        CornerRadius = new CornerRadius(4),
        Background = color,
        Margin = new Thickness(0, 0, 8, 0),
        VerticalAlignment = VerticalAlignment.Center,
        ToolTip = tip,
    };

    // A row whose title has a status dot before it.
    private Grid DotRow(Brush dot, string? tip, string title, string? description, params FrameworkElement[] controls)
    {
        var row = Row(title, description, controls);
        var text = (StackPanel)row.Children[0];
        var heading = (TextBlock)text.Children[0];
        text.Children.RemoveAt(0);
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(Dot(dot, tip));
        line.Children.Add(heading);
        text.Children.Insert(0, line);
        return row;
    }

    // A PC linked to this one, as T3 shows an authorized client. Revoke ends it, with no asking, as in T3.
    private Grid ClientRow(MachineLinkInfo client)
    {
        var revoke = new Button { Style = (Style)FindResource("DangerButton"), Content = "Revoke" };
        var problem = (string?)null;
        revoke.Click += async (_, _) =>
        {
            revoke.IsEnabled = false;
            revoke.Content = "Revoking…";
            problem = await TestMachines.ForgetAsync(client);
            if (problem is not null)
            {
                revoke.IsEnabled = true;
                revoke.Content = "Revoke";
                revoke.ToolTip = $"Could not revoke client access. {problem}";
            }
        };
        var expired = client.Status == "expired";
        return DotRow(expired ? DestructiveDot : SuccessDot, null, $"Agent Orange · {client.LinkerLabel}", $"{MachineLinks.AccessLabel(client.Access)} · {LinkExpiry(client)}", revoke);
    }

    private static string LinkExpiry(MachineLinkInfo link)
    {
        var left = DateTimeOffset.FromUnixTimeSeconds((long)link.ExpiresAt) - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero) return "Expired";
        var days = (int)Math.Ceiling(left.TotalDays);
        return $"Expires in {days} day{(days == 1 ? "" : "s")}";
    }

    // T3's status for a linked environment, with busy and another release added, as a test machine
    // can be. Renewing is by itself here, and linking again renews too, as in T3.
    private static (Brush Dot, string Text) LinkStatus(MachineLinkInfo link)
    {
        var left = DateTimeOffset.FromUnixTimeSeconds((long)link.ExpiresAt) - DateTimeOffset.UtcNow;
        if (link.Status == "expired" || left <= TimeSpan.Zero) return (DestructiveDot, "Expired. Link it again.");
        if (link.Status == "other_release") return (DestructiveDot, $"{link.MachineLabel} is on another release of Agent Orange. Update it, and it answers again.");
        if (link.Status == "unreachable") return ((Brush)Application.Current.FindResource("TextMuted"), "Not answering right now.");
        var days = (int)Math.Ceiling(left.TotalDays);
        if (days <= 5) return (WarningDot, $"Expires in {days} {(days == 1 ? "day" : "days")}. It renews by itself while both PCs are online, or link it again to renew.");
        return (SuccessDot, link.Status == "busy" ? "Running tests" : "Reachable");
    }

    // T3's row: the dot and name, the access and status, then (ours) its expiry and tags, and Forget.
    private Grid LinkedMachineRow(MachineLinkInfo link)
    {
        var (dot, status) = LinkStatus(link);
        var forget = new Button { Style = (Style)FindResource("DangerButton"), Content = "Forget" };
        forget.Click += (_, _) => ConfirmForget(link);
        var tags = link.MachineTags.Count > 0 ? $"\n{string.Join(", ", link.MachineTags)}" : "";
        var expiry = DateTimeOffset.FromUnixTimeSeconds((long)link.ExpiresAt) > DateTimeOffset.UtcNow ? $"\n{LinkExpiry(link)}" : "";
        return DotRow(dot, status, link.MachineLabel, $"{MachineLinks.AccessLabel(link.Access)} · {status}{expiry}{tags}", forget);
    }

    // T3's confirm: "Forget <label>?", Cancel and Confirm. Here it ends the link on both PCs, as the server keeps the only copy.
    private void ConfirmForget(MachineLinkInfo link)
    {
        var problem = ProblemText();
        var body = new StackPanel { Margin = new Thickness(8, 0, 8, 12), Children = { problem } };
        ShowWizard();
        WizardCard.Child = WizardShell(
            CloseWizard,
            $"Forget {link.MachineLabel}?",
            $"This PC stops running tests there. The link ends on {link.MachineLabel} too. Link it again to run tests there later.",
            null,
            body,
            TextButton("Cancel", CloseWizard),
            ForgetButton(link, problem));
    }

    private Button ForgetButton(MachineLinkInfo link, TextBlock problem)
    {
        Button confirm = null!;
        confirm = Clickable("DangerButton", "Confirm", async () =>
        {
            if (!confirm.IsEnabled) return;
            confirm.IsEnabled = false;
            confirm.Content = "Forgetting…";
            if (await TestMachines.ForgetAsync(link) is { } failed)
            {
                confirm.IsEnabled = true;
                confirm.Content = "Confirm";
                ShowProblem(problem, $"Could not forget the link. {failed}");
                return;
            }
            CloseWizard();
            _refreshTestMachines?.Invoke();
        });
        return confirm;
    }

    // A choice in the Link dialog: one of the owner's other PCs, which may not be linkable yet.
    private sealed record LinkChoice(string Label, TestMachineInfo? Machine, OtherPc? Other);

    // Why a PC can't be linked now, or null when it can. Said under the picker, so Link never seems
    // to do nothing (#95).
    private static string? CantLink(LinkChoice choice) => choice switch
    {
        { Other.Reason: "pair_again" } => $"{choice.Label} paired with your server before test machines, so the server can't tell it apart. Sign out on its Server page and pair it again, then link.",
        { Other: not null } => $"{choice.Label} is signed in to your server but hasn't said whether it takes runs, so it's on another release of Agent Orange. Update it, turn on Settings › Test machines › Let other PCs run tests here there, then link.",
        { Machine.Status: "other_release" } => $"{choice.Label} is on another release of Agent Orange. Update it, then link.",
        { Machine.Accepting: false } => MachineLinks.RefusedText("not_accepting", choice.Label),
        _ => null,
    };

    // T3's Link dialog as it is when T3 mints the code itself: the machine and the access, with no
    // code and no address, as everything goes through the server. Machines are the owner's other
    // PCs, as the server knows them. Linking carries on with the dialog closed, in Linked machines.
    private void OpenLinkDialog(string? machine)
    {
        try
        {
            ShowLinkDialog(machine);
        }
        catch (Exception error)
        {
            // Never nothing: whatever went wrong is said where the dialog would be.
            AppLog.Write($"The Link dialog couldn't open: {error}");
            var problem = ProblemText();
            ShowProblem(problem, $"Something went wrong on this PC: {error.Message}");
            ShowWizard();
            WizardCard.Child = WizardShell(CloseWizard, "Link a machine", "The dialog couldn't be shown.", null, new StackPanel { Margin = new Thickness(8, 0, 8, 12), Children = { problem } }, TextButton("Close", CloseWizard));
        }
    }

    private void ShowLinkDialog(string? machine)
    {
        var latest = TestMachines.Latest;
        var linkedTo = latest?.Links.Where(each => each.Linker == latest.Me).Select(each => each.Machine).ToHashSet() ?? [];
        List<LinkChoice> choices =
        [
            .. (latest?.Machines ?? []).Select(each => new LinkChoice(each.Label, each, null)),
            .. (latest?.Others ?? []).Select(each => new LinkChoice(each.Label, null, each)),
        ];
        choices.Sort((one, other) => StringComparer.CurrentCultureIgnoreCase.Compare(one.Label, other.Label));

        // T3's machine select: no choice made for you, and Linked or Not connected after the name.
        var picker = new ComboBox { Style = (Style)FindResource("Choice"), MinWidth = 260 };
        AutomationProperties.SetName(picker, "Machine");
        foreach (var each in choices)
        {
            var badge = each.Machine is { } known && linkedTo.Contains(known.Install) ? "Linked"
                : each.Machine is { Status: "reachable" or "busy" } && CantLink(each) is null ? null
                : "Not connected";
            picker.Items.Add(new ComboBoxItem { Content = badge is null ? each.Label : $"{each.Label}   {badge}", Tag = each });
        }
        var chosen = machine is null ? -1 : choices.FindIndex(each => each.Machine?.Install == machine);
        if (chosen >= 0) picker.SelectedIndex = chosen;

        var access = Choice([.. MachineLinks.AccessLevels.Take(1).Select(MachineLinks.AccessLabel)], MachineLinks.AccessLabel("tests"), _ => { });
        AutomationProperties.SetName(access, "What this PC may do there");

        var note = new TextBlock { FontSize = 12.5, Foreground = (Brush)FindResource("TextMuted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 8) };
        AutomationProperties.SetName(note, "About this machine");
        var step = new TextBlock { FontSize = 12.5, Foreground = (Brush)FindResource("TextNormal"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 8, 16, 0), Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(step, "What's happening");
        var problem = ProblemText();
        problem.TextWrapping = TextWrapping.Wrap;
        Button submit = null!;
        Linking? linking = null;

        LinkChoice? Picked() => (picker.SelectedItem as ComboBoxItem)?.Tag as LinkChoice;

        // The words under the picker and whether Link can go, as the chosen machine stands now.
        void ShowNote()
        {
            if (Picked() is not { } picked)
            {
                note.Text = "Choose a machine";
                if (linking is null) submit.IsEnabled = false;
                return;
            }
            var now = picked.Machine is { } known ? TestMachines.Latest?.Machines.FirstOrDefault(each => each.Install == known.Install) ?? known : null;
            var current = picked with { Machine = now };
            var cantLink = CantLink(current);
            note.Text = cantLink
                ?? (linkedTo.Contains(now!.Install) ? "Already linked. Linking again renews it with the access you pick. " : "")
                    + (now!.Status is "reachable" or "busy"
                        ? $"{now.Label} answers by itself, in a few seconds, with nobody needing to be there. There's no code to type, as it's signed in to the same server as this PC."
                        : $"{now.Label} isn't connected right now. It answers by itself once Agent Orange runs there and it's online, and this PC waits for it up to {MachineLinks.AnswerWait.TotalMinutes:0} minutes.");
            if (linking is null) submit.IsEnabled = cantLink is null;
        }

        // What the link being made is doing, as it changes, and how it ended.
        void ShowLinking()
        {
            if (linking is null) return;
            if (!TestMachines.Linkings.Contains(linking))
            {
                // Made, or stopped from its row: either way, this dialog is done.
                var made = TestMachines.JustLinked;
                linking = null;
                _refreshLinkDialog = null;
                if (WizardLayer.Visibility == Visibility.Visible && made is not null) CloseWizard();
                return;
            }
            if (linking.Problem is { } failed)
            {
                // Said here, so not in Linked machines as well.
                var ended = linking;
                linking = null;
                _refreshLinkDialog = null;
                TestMachines.StopLinking(ended);
                submit.Content = "Link";
                picker.IsEnabled = access.IsEnabled = true;
                step.Visibility = Visibility.Collapsed;
                ShowProblem(problem, $"Could not link the machine. {failed}");
                ShowNote();
                return;
            }
            step.Text = $"{linking.Step} You can close this: it carries on in Linked machines.";
            step.Visibility = Visibility.Visible;
        }

        submit = PrimaryButton("Link", () =>
        {
            if (linking is not null || Picked() is not { Machine: { } picked } choice || CantLink(choice) is not null) return;
            problem.Visibility = Visibility.Collapsed;
            submit.Content = "Linking…";
            submit.IsEnabled = picker.IsEnabled = access.IsEnabled = false;
            linking = TestMachines.StartLink(picked, "tests");
            _refreshLinkDialog = ShowLinking;
            ShowLinking();
        });
        picker.SelectionChanged += (_, _) =>
        {
            problem.Visibility = Visibility.Collapsed;
            ShowNote();
        };
        ShowNote();

        var rows = new StackPanel { Margin = new Thickness(8, 8, 8, 12) };
        if (latest is null)
        {
            rows.Children.Add(Row(TestMachines.Problem is null ? "Loading machines…" : "Could not load machines", TestMachines.Problem ?? "Asking the server for your PCs. Try again in a moment."));
        }
        else if (choices.Count == 0)
        {
            rows.Children.Add(Row("No machines to link yet", "Sign another of your PCs in to this server, on its Server page, then turn on Settings › Test machines › Let other PCs run tests here on it."));
        }
        else
        {
            rows.Children.Add(Row("Machine", null, picker));
            rows.Children.Add(note);
            rows.Children.Add(Row("What this PC may do there", $"{MachineLinks.AccessDescription("tests")} Agents and full access aren't offered yet.", access));
            rows.Children.Add(step);
        }
        if (latest is not null && TestMachines.Problem is { } trouble) ShowProblem(problem, trouble);
        rows.Children.Add(problem);

        void Close(bool stop)
        {
            if (stop && linking is not null) TestMachines.StopLinking(linking);
            linking = null;
            _refreshLinkDialog = null;
            CloseWizard();
        }

        ShowWizard();
        WizardCard.Child = WizardShell(
            () => Close(stop: false),
            "Link a machine",
            "This PC can then run a project's tests there. It appears under Authorized clients in that machine's Test machines settings, where it can be revoked.",
            null,
            rows,
            TextButton("Cancel", () => Close(stop: true)),
            submit);
        if (choices.Count > 0) Dispatcher.InvokeAsync(() => picker.Focus(), DispatcherPriority.Loaded);
    }
}
