using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>
/// Settings › Test machines (#93). On a PC that runs tests: whether it takes them, what it offers,
/// and T3 Code's "Authorized clients" with its pairing links. On a PC that sends them: T3's
/// "Linked environments" list and its Link dialog. Texts are T3's where T3 has the same thing.
/// </summary>
public partial class SettingsModal
{
    private TestMachines _testMachines = null!;
    private Action? _refreshTestMachines;
    private DispatcherTimer? _codeClock;
    private string? _linkedNote;

    private static readonly Brush SuccessDot = new SolidColorBrush(Color.FromRgb(0x5F, 0xD0, 0x8A));
    private static readonly Brush WarningDot = new SolidColorBrush(Color.FromRgb(0xE8, 0xB0, 0x4B));
    private static readonly Brush DestructiveDot = new SolidColorBrush(Color.FromRgb(0xE8, 0x6B, 0x6B));

    public TestMachines TestMachines
    {
        get => _testMachines;
        set
        {
            _testMachines = value;
            _testMachines.Changed += () => _refreshTestMachines?.Invoke();
        }
    }

    private UIElement[] TestMachinesPage()
    {
        var settings = TestMachines.Settings;
        var problem = ProblemText();

        void Save(Func<TestMachineSettings, TestMachineSettings> change) => TestMachines.Save(change(TestMachines.Settings));

        var accepting = Toggle(settings.Accepting);
        AutomationProperties.SetName(accepting, "Let other PCs run tests here");
        accepting.Checked += (_, _) => Save(now => now with { Accepting = true });
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
            Row("Time limit", "Minutes a run may take here, over what the project asks. Empty uses the project's.", timeout),
        ];
        var thisPc = Card([Row("Let other PCs run tests here", "Your other PCs linked to this one can send a project's tests to run here, in the background. Only the project's own prepare and test commands run, and each run here is listed below with exactly what it ran.", accepting), .. targetRows]);
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

        return
        [
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
    // Linked environments.
    private List<UIElement> LinkerParts()
    {
        var parts = new List<UIElement>();
        var latest = TestMachines.Latest;
        var me = latest?.Me;
        var link = ActionButton("Link");
        link.Click += (_, _) => OpenLinkDialog(null);
        parts.Add(SectionWithActions("Linked machines", null, link));
        if (TestMachines.Problem is { } trouble && latest is not null)
        {
            var shown = ProblemText();
            ShowProblem(shown, trouble);
            parts.Add(shown);
        }
        var linked = latest?.Links.Where(each => each.Linker == me).OrderBy(each => each.MachineLabel, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (latest is null)
        {
            parts.Add(Card(Row(TestMachines.Problem is null ? "Loading linked machines…" : "Could not load linked machines", TestMachines.Problem)));
        }
        else if (linked!.Count == 0)
        {
            parts.Add(Card(Row("No linked machines", "Link another of your PCs running Agent Orange, so this PC can run a project's tests there.")));
        }
        else
        {
            parts.Add(Card([.. linked.Select(LinkedMachineRow)]));
        }
        if (_linkedNote is { } note)
        {
            parts.Add(new TextBlock { Text = note, FontSize = 12.5, Foreground = (Brush)FindResource("TextNormal"), Margin = new Thickness(16, 8, 16, 0) });
        }
        return parts;
    }

    // As the target: T3's Authorized clients, with its pairing links first, then what ran here.
    private List<UIElement> TargetParts()
    {
        var parts = new List<UIElement>();
        var latest = TestMachines.Latest;
        var settings = TestMachines.Settings;
        var me = latest?.Me;

        if (settings.Accepting)
        {
            var clients = latest?.Links.Where(link => link.Machine == me).ToList() ?? [];
            var code = TestMachines.Code;
            var summary = $"{clients.Count} client{(clients.Count == 1 ? "" : "s")}{(code is null ? "" : " · 1 pairing link")}";
            var create = ActionButton("Create link");
            create.Click += (_, _) => TestMachines.MakeCode();
            parts.Add(SectionWithActions("Authorized clients", summary, create));

            var rows = new List<FrameworkElement>();
            if (code is not null) rows.Add(PairingLinkRow(code));
            foreach (var client in clients) rows.Add(ClientRow(client));
            if (rows.Count == 0) rows.Add(Row("No pairing links or client sessions.", "Create link makes a one-time pairing code. Enter it on another of your PCs, in its Settings › Test machines › Link, so it can run tests here."));
            parts.Add(Card([.. rows]));
            if (TestMachines.CodeProblem is { } codeProblem)
            {
                var shown = ProblemText();
                ShowProblem(shown, codeProblem);
                parts.Add(shown);
            }
            StartCodeClock(code is not null);
        }
        else
        {
            StartCodeClock(false);
        }


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

    // T3's pairing link row: a warning dot, a countdown, and the code to enter on the other PC.
    private Grid PairingLinkRow(LinkCode code)
    {
        var show = ActionButton("Show code");
        show.Click += (_, _) => ShowCodeDialog(code);
        var copy = ActionButton("Copy code");
        copy.Click += (_, _) => CopyCode(code);
        var revoke = new Button { Style = (Style)FindResource("DangerButton"), Content = "Revoke" };
        revoke.Click += (_, _) => TestMachines.RevokeCode();
        var row = DotRow(WarningDot, $"Link created at {code.MadeAt.ToLocalTime():g}", "Pairing link", code.ExpiresText, show, copy, revoke);
        var countdown = (TextBlock)((StackPanel)row.Children[0]).Children[1];
        countdown.ToolTip = $"{code.ExpiresAt.ToLocalTime():g}";
        _codeCountdown = (countdown, code);
        return row;
    }

    private (TextBlock Text, LinkCode Code)? _codeCountdown;

    // T3 updates the countdown every second.
    private void StartCodeClock(bool on)
    {
        if (!on)
        {
            _codeClock?.Stop();
            _codeCountdown = null;
            return;
        }
        if (_codeClock is null)
        {
            _codeClock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _codeClock.Tick += (_, _) =>
            {
                if (_codeCountdown is not { } shown || _currentPage.Name != "Test machines") return;
                shown.Text.Text = shown.Code.ExpiresText;
            };
        }
        _codeClock.Start();
    }

    private void CopyCode(LinkCode code)
    {
        try
        {
            Clipboard.SetText(code.Code);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            ShowCodeDialog(code); // T3 shows the value instead when it can't copy.
        }
    }

    // T3's "Pairing code" dialog: the value, Done and Copy code.
    private void ShowCodeDialog(LinkCode code)
    {
        var value = new TextBlock
        {
            Text = code.Code,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 26,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)FindResource("TextStrong"),
        };
        AutomationProperties.SetName(value, "Pairing code");
        var body = new StackPanel { Margin = new Thickness(24, 16, 24, 20), Children = { value } };
        ShowWizard();
        WizardCard.Child = WizardShell(
            CloseWizard,
            "Pairing code",
            $"Enter it on the other PC, in its Settings › Test machines › Link. It works once. {code.ExpiresText}.",
            null,
            body,
            TextButton("Done", CloseWizard),
            PrimaryButton("Copy code", () => CopyCode(code)));
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

    // T3's status for a linked environment, with busy added, as a test machine can be.
    private static (Brush Dot, string Text) LinkStatus(MachineLinkInfo link)
    {
        var left = DateTimeOffset.FromUnixTimeSeconds((long)link.ExpiresAt) - DateTimeOffset.UtcNow;
        if (link.Status == "expired" || left <= TimeSpan.Zero) return (DestructiveDot, "Expired. Link it again with a new pairing code.");
        if (link.Status == "unreachable") return ((Brush)Application.Current.FindResource("TextMuted"), "Not answering right now.");
        var days = (int)Math.Ceiling(left.TotalDays);
        if (days <= 5) return (WarningDot, $"Expires in {days} {(days == 1 ? "day" : "days")}. Link it again to renew.");
        return (SuccessDot, link.Status == "busy" ? "Running tests" : "Reachable");
    }

    private Grid LinkedMachineRow(MachineLinkInfo link)
    {
        var (dot, status) = LinkStatus(link);
        var forget = new Button { Style = (Style)FindResource("DangerButton"), Content = "Forget" };
        forget.Click += (_, _) => ConfirmForget(link);
        var tags = link.MachineTags.Count > 0 ? $"\n{string.Join(", ", link.MachineTags)}" : "";
        return DotRow(dot, status, link.MachineLabel, $"{MachineLinks.AccessLabel(link.Access)} · {status}{tags}", forget);
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
            $"This PC stops running tests there. The link ends on {link.MachineLabel} too, and it'll need a new pairing code to link again.",
            null,
            body,
            TextButton("Cancel", CloseWizard),
            Clickable("DangerButton", "Confirm", async () =>
            {
                if (await TestMachines.ForgetAsync(link) is { } failed)
                {
                    ShowProblem(problem, $"Could not forget the link. {failed}");
                    return;
                }
                CloseWizard();
                _refreshTestMachines?.Invoke();
            }));
    }

    // T3's Link dialog: pick a machine, enter its code, pick the access. Machines are those of your
    // PCs that let others run tests on them, as the server knows them. There are no addresses to
    // type, as everything goes through the server.
    private void OpenLinkDialog(string? machine)
    {
        var latest = TestMachines.Latest;
        var machines = latest?.Machines ?? [];
        var linkedTo = latest?.Links.Where(each => each.Linker == latest.Me).Select(each => each.Machine).ToHashSet() ?? [];

        var picker = new ComboBox { Style = (Style)FindResource("Choice"), MinWidth = 260 };
        AutomationProperties.SetName(picker, "Machine");
        foreach (var each in machines)
        {
            var badge = linkedTo.Contains(each.Install) ? "Linked" : each.Status is "reachable" or "busy" ? null : "Not connected";
            picker.Items.Add(new ComboBoxItem { Content = badge is null ? each.Label : $"{each.Label}   {badge}", Tag = each });
        }
        var chosen = machines.FindIndex(each => each.Install == machine);
        picker.SelectedIndex = chosen >= 0 ? chosen : machines.Count == 1 ? 0 : -1;

        var code = Field("");
        code.Width = 260;
        code.FontFamily = new FontFamily("Consolas");
        AutomationProperties.SetName(code, "Pairing code");

        var access = Choice([.. MachineLinks.AccessLevels.Take(1).Select(MachineLinks.AccessLabel)], MachineLinks.AccessLabel("tests"), _ => { });
        AutomationProperties.SetName(access, "What this PC may do there");

        var note = new TextBlock { FontSize = 12.5, Foreground = (Brush)FindResource("TextMuted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 0) };
        var problem = ProblemText();
        void ShowNote()
        {
            var picked = (picker.SelectedItem as ComboBoxItem)?.Tag as TestMachineInfo;
            note.Text = picked is null
                ? "Choose a machine"
                : (linkedTo.Contains(picked.Install) ? "Already linked. Linking again renews it with the access you pick. " : "")
                    + $"Create a link on {picked.Label}: Settings → Test machines → Create link, then enter its code here.";
        }
        picker.SelectionChanged += (_, _) => ShowNote();
        ShowNote();

        var rows = new StackPanel { Margin = new Thickness(8, 8, 8, 12) };
        if (machines.Count == 0)
        {
            rows.Children.Add(Row("No machines to link yet", "On the PC that should run the tests, turn on Settings › Test machines › Let other PCs run tests here."));
        }
        else
        {
            rows.Children.Add(Row("Machine", null, picker));
            rows.Children.Add(note);
            rows.Children.Add(Row("Pairing code", "Paste the code", code));
            rows.Children.Add(Row("What this PC may do there", $"{MachineLinks.AccessDescription("tests")} Agents and full access aren't offered yet.", access));
        }
        rows.Children.Add(problem);

        CancellationTokenSource? linking = null;
        Button submit = null!;
        submit = PrimaryButton("Link", async () =>
        {
            if (linking is not null || (picker.SelectedItem as ComboBoxItem)?.Tag is not TestMachineInfo picked || code.Text.Trim().Length == 0) return;
            linking = new CancellationTokenSource();
            submit.Content = "Linking…";
            submit.IsEnabled = picker.IsEnabled = code.IsEnabled = false;
            problem.Visibility = Visibility.Collapsed;
            var failed = await TestMachines.LinkAsync(picked, code.Text, "tests", linking.Token);
            var cancelled = linking.IsCancellationRequested;
            linking = null;
            if (cancelled) return;
            if (failed is not null)
            {
                submit.Content = "Link";
                submit.IsEnabled = picker.IsEnabled = code.IsEnabled = true;
                ShowProblem(problem, $"Could not link the machine. {failed}");
                return;
            }
            _linkedNote = $"Linked {picked.Label}";
            CloseWizard();
            _refreshTestMachines?.Invoke();
        });
        submit.IsEnabled = machines.Count > 0;
        code.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };

        ShowWizard();
        WizardCard.Child = WizardShell(
            () =>
            {
                linking?.Cancel();
                CloseWizard();
            },
            "Link a machine",
            "This PC can then run a project's tests there. It appears in that machine's Test machines settings, where it can be revoked.",
            null,
            rows,
            TextButton("Cancel", () =>
            {
                linking?.Cancel();
                CloseWizard();
            }),
            submit);
        if (machines.Count > 0) Dispatcher.InvokeAsync(() => (picker.SelectedIndex >= 0 ? code : (Control)picker).Focus(), DispatcherPriority.Loaded);
    }
}
