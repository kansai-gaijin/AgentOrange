using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgentOrange;

/// <summary>
/// T3 Code's git actions, copied from its code: which one fits where the branch stands (its mz
/// rules), the menu beside it (pz, with Oz's reasons), the commit dialog, the warning before
/// acting on the default branch, and the steps as T3's server runs them, with commit messages,
/// branch names and pull requests written by the conversation's own login when left blank.
/// </summary>
public sealed partial class AgentsWindow
{
    private enum GitAction { None, Commit, CommitPush, CommitPushPr, Push, PushPr, Pull, CreatePr }

    private sealed record GitPlan(string Label, GitAction Action, string? Hint);

    // T3's mz: the one action that fits.
    private static GitPlan PlanFor(RepoState? state, bool busy)
    {
        if (busy) return new("Commit", GitAction.None, "Git action in progress.");
        if (state is null || state.Problem is not null && state.Branch is null && !state.HasChanges) return new("Commit", GitAction.None, "Git status is unavailable.");
        if (state.Branch is null) return new("Commit", GitAction.None, "Create and checkout a ref before pushing or opening a pull request.");
        var prOpen = state.PullRequestOpen;
        var ahead = state.Ahead > 0;
        var behind = state.Behind > 0;
        if (state.HasChanges)
        {
            if (behind || state.Upstream is null && !state.HasOrigin) return new("Commit", GitAction.Commit, null);
            return prOpen ? new("Commit & push", GitAction.CommitPush, null) : new("Commit, push & PR", GitAction.CommitPushPr, null);
        }
        if (state.Upstream is not null)
        {
            if (ahead && behind) return new("Sync ref", GitAction.None, "Branch has diverged from upstream. Rebase/merge first.");
            if (behind) return new("Pull", GitAction.Pull, null);
            if (ahead) return prOpen ? new("Push", GitAction.Push, null) : new("Push & create PR", GitAction.PushPr, null);
            if (prOpen) return new("Commit", GitAction.None, "Branch is up to date. No action needed.");
            if (state.AheadOfDefault > 0) return new("Create PR", GitAction.CreatePr, null);
            return new("Commit", GitAction.None, "Branch is up to date. No action needed.");
        }
        if (!state.HasOrigin) return new("Commit", GitAction.None, "Add an \"origin\" remote before pushing.");
        if (ahead) return prOpen ? new("Push", GitAction.Push, null) : new("Push & create PR", GitAction.PushPr, null);
        if (prOpen) return new("Commit", GitAction.None, "Branch is up to date. No action needed.");
        return new("Push", GitAction.None, "No local commits to push.");
    }

    // T3's pz and Oz: the menu, and why each can't be done when it can't.
    private static List<(string Label, string Icon, GitAction Action, string? Why)> GitMenu(RepoState? state, bool busy)
    {
        string? Unavailable(string? reason) => busy ? "Git action in progress." : state is null ? "Git status is unavailable." : reason;
        var onRef = state?.Branch is not null;
        var behind = state?.Behind > 0;
        var canPublish = state?.Upstream is null && state?.HasOrigin == true;
        string? commitWhy = state?.HasChanges == true ? null : "Worktree is clean. Make changes before committing.";
        string? pushWhy = null;
        if (!(onRef && !behind && state!.Ahead > 0 && (state.Upstream is not null || canPublish)))
        {
            pushWhy = !onRef ? "Detached HEAD: check out a branch before pushing."
                : state!.HasChanges ? "Commit or stash local changes before pushing."
                : behind ? "Branch is behind upstream. Pull/rebase before pushing."
                : state.Upstream is null && !state.HasOrigin ? "Add an \"origin\" remote before pushing."
                : state.Ahead > 0 ? "Push is currently unavailable."
                : "No local commits to push.";
        }
        string? prWhy = null;
        if (!(onRef && state!.AheadOfDefault > 0 && !behind && (state.Upstream is not null || canPublish)))
        {
            prWhy = !onRef ? "Check out a branch before opening a pull request."
                : state!.HasChanges ? "Commit local changes before creating a pull request."
                : behind ? "Branch is behind upstream. Pull/rebase before creating a pull request."
                : state.Upstream is null && !state.HasOrigin ? "Add an \"origin\" remote before creating a pull request."
                : state.AheadOfDefault <= 0 ? "No local commits to include in a pull request."
                : "Create PR is currently unavailable.";
        }
        List<(string, string, GitAction, string?)> items =
        [
            ("Commit", "git-commit-horizontal", GitAction.Commit, Unavailable(commitWhy)),
            ("Push", "cloud-upload", GitAction.Push, Unavailable(pushWhy)),
        ];
        if (state?.PullRequestOpen != true) items.Add(("Create PR", "git-pull-request", state?.Ahead > 0 || state?.Upstream is null ? GitAction.PushPr : GitAction.CreatePr, Unavailable(prWhy)));
        return items;
    }

    private static bool Pushes(GitAction action) => action is GitAction.CommitPush or GitAction.CommitPushPr or GitAction.Push or GitAction.PushPr or GitAction.CreatePr;

    private static bool Commits(GitAction action) => action is GitAction.Commit or GitAction.CommitPush or GitAction.CommitPushPr;

    private static bool OpensPr(GitAction action) => action is GitAction.CommitPushPr or GitAction.PushPr or GitAction.CreatePr;

    private void StartGitAction(AgentConversation conversation, string folder, GitAction action, string? message, bool featureBranch, bool fromMenu, IReadOnlyList<string>? only = null)
    {
        RepoStates.TryGetValue(folder, out var state);
        if (fromMenu && action == GitAction.Commit)
        {
            ShowCommitDialog(conversation, folder, state);
            return;
        }
        if (!featureBranch && Pushes(action) && state is { OnDefaultBranch: true, Branch: { } branch })
        {
            ShowDefaultBranchDialog(conversation, folder, action, message, only, branch, state.HasChanges);
            return;
        }
        _ = RunGitActionAsync(conversation, folder, action, message, featureBranch, only);
    }

    // The steps as T3's server runs them, each with the words it shows.
    private async Task RunGitActionAsync(AgentConversation conversation, string folder, GitAction action, string? message, bool featureBranch, IReadOnlyList<string>? only)
    {
        RepoStates.TryGetValue(folder, out var state);
        var run = new GitRun();
        _gitRuns[folder] = run;
        void Phase(string status)
        {
            run.Status = status;
            run.PhaseStarted = DateTimeOffset.UtcNow;
            _redrawDetails?.Invoke();
        }
        void Fail(string why)
        {
            _gitRuns.Remove(folder);
            Toast(action == GitAction.Pull ? "Pull failed" : "Action failed", why, error: true);
            _redrawDetails?.Invoke();
        }

        Phase(action == GitAction.Pull ? "Pulling latest changes..." : "Starting source control action...");
        var login = _main.AgentProviderFor(conversation);
        var branch = state?.Branch ?? "";
        string subject = "", body = "";
        string? suggestedBranch = null;

        if (Commits(action))
        {
            if (message is { Length: > 0 } written)
            {
                var lines = written.Split('\n', 2);
                subject = lines[0].Trim();
                body = lines.Length > 1 ? lines[1].Trim() : "";
            }
            else
            {
                Phase("Generating commit message...");
                var (files, patch) = await GitTools.CommitContextAsync(folder, only);
                var prompt = "You write concise git commit messages.\n"
                    + $"Return a JSON object with keys: subject, body{(featureBranch ? ", branch" : "")}.\n"
                    + "Rules:\n- subject must be imperative, 72 characters or fewer, and have no trailing period\n- body can be empty, or short bullet points\n- capture the main visible change\n"
                    + (featureBranch ? "- branch is a short git branch name for this change\n" : "")
                    + $"\nBranch: {branch}\n\nStaged files:\n{files}\n\nStaged patch:\n{patch}";
                var answer = login is null ? null : (await AgentText.AskAsync(login, prompt, folder)).Text;
                if (AgentText.JsonIn(answer) is { } json)
                {
                    subject = Str(json, "subject") ?? "";
                    body = Str(json, "body") ?? "";
                    suggestedBranch = Str(json, "branch");
                }
            }
            subject = subject.Split('\n')[0].Trim().TrimEnd('.');
            if (subject.Length > 72) subject = subject[..72].TrimEnd();
            if (subject.Length == 0) subject = "Update project files";
        }

        if (featureBranch)
        {
            Phase("Preparing feature branch...");
            var name = await GitTools.FeatureBranchNameAsync(folder, suggestedBranch, subject.Length > 0 ? subject : "update");
            var made = await GitTools.CreateBranchAsync(folder, name);
            if (!made.Worked)
            {
                Fail(made.Value);
                return;
            }
            branch = name;
        }

        var sha = "";
        if (Commits(action))
        {
            Phase("Committing...");
            var committed = await GitTools.CommitAsync(folder, subject, body, only);
            if (!committed.Worked)
            {
                Fail(committed.Value);
                return;
            }
            sha = committed.Value;
        }

        if (Pushes(action) && (action != GitAction.CreatePr || state?.Upstream is null))
        {
            Phase("Pushing...");
            var pushed = await GitTools.PushAsync(folder);
            if (!pushed.Worked)
            {
                Fail(pushed.Value);
                return;
            }
        }

        if (OpensPr(action))
        {
            Phase("Preparing PR...");
            var baseRef = state?.DefaultBranch ?? "origin/main";
            Phase("Generating PR content...");
            var (commits, stat, patch) = await GitTools.PullRequestContextAsync(folder, baseRef);
            var prompt = "You write GitHub pull request content.\nReturn a JSON object with keys: title, body.\n"
                + "Rules:\n- title is concise and specific\n- body is markdown with the sections \"## Summary\" and \"## Testing\"\n"
                + $"\nBase branch: {baseRef}\nHead branch: {branch}\n\nCommits:\n{commits}\n\nDiff stat:\n{stat}\n\nPatch:\n{patch}";
            var answer = login is null ? null : (await AgentText.AskAsync(login, prompt, folder)).Text;
            var json = AgentText.JsonIn(answer);
            var title = json is { } titled ? Str(titled, "title") : null;
            var description = json is { } described ? Str(described, "body") : null;
            if (title is not { Length: > 0 }) title = "Update project changes";
            Phase("Creating pull request...");
            var opened = await GitTools.CreatePullRequestAsync(folder, branch, baseRef, title.Split('\n')[0].Trim(), description ?? "");
            if (!opened.Worked)
            {
                Fail(opened.Value);
                return;
            }
            Succeed(run, "Created PR", null);
        }
        else if (action == GitAction.Pull)
        {
            var before = await GitTools.HeadAsync(folder);
            var pulled = await GitTools.PullAsync(folder);
            if (!pulled.Worked)
            {
                Fail(pulled.Value);
                return;
            }
            var after = await GitTools.HeadAsync(folder);
            if (before.Sha == after.Sha) Succeed(run, "Already up to date", $"{branch} is already synchronized.");
            else Succeed(run, "Pulled", $"Updated {branch} from {state?.Upstream ?? "origin"}");
        }
        else if (Pushes(action))
        {
            var head = await GitTools.HeadAsync(folder);
            Succeed(run, $"Pushed {head.Sha} to origin/{branch}", head.Subject);
        }
        else
        {
            Succeed(run, $"Committed {sha}", subject);
        }
        await RefreshRepoAsync(conversation.ProjectId, fetch: false);
    }

    private void Succeed(GitRun run, string title, string? description)
    {
        run.SuccessTitle = title;
        run.SuccessDescription = description is { Length: > 72 } ? description[..72] : description;
        run.SuccessUntil = DateTimeOffset.UtcNow.AddSeconds(10);
        _redrawDetails?.Invoke();
    }

    private static string? Str(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Dialogs, over the whole window, as T3's: a glass card on a dimmed window.

    private void ShowDialog(UIElement card, Color? shade = null)
    {
        _dialogs.Children.Clear();
        var dim = new Border { Background = new SolidColorBrush(shade ?? Color.FromArgb(0x80, 0x00, 0x00, 0x00)) };
        // A click that began on it, so the release of the click that opened the dialog doesn't close it.
        var pressedHere = false;
        dim.MouseLeftButtonDown += (_, _) => pressedHere = true;
        dim.MouseLeftButtonUp += (_, _) =>
        {
            if (pressedHere) CloseDialog();
            pressedHere = false;
        };
        _dialogs.Children.Add(dim);
        _dialogs.Children.Add(card);
        _dialogs.Visibility = Visibility.Visible;
        CloseDetails();
    }

    private void CloseDialog()
    {
        _dialogs.Children.Clear();
        _dialogs.Visibility = Visibility.Collapsed;
    }

    private static Border DialogCard(double maxWidth, string title, string description, UIElement? panel, Action close, params UIElement[] buttons)
    {
        var closer = Ocean.IconButton("x", close, "Close", 28, 16, Ocean.MutedText);
        closer.HorizontalAlignment = HorizontalAlignment.Right;
        closer.VerticalAlignment = VerticalAlignment.Top;
        closer.Margin = new Thickness(0, 8, 8, 0);
        var heading = Ocean.Wrapped(title, Ocean.Foreground, 20, 26);
        heading.FontWeight = FontWeights.SemiBold;
        var header = new StackPanel { Margin = new Thickness(24, 24, 24, 12), Children = { heading } };
        var said = Ocean.Wrapped(description, Ocean.MutedText, 14, 20);
        said.Margin = new Thickness(0, 8, 0, 0);
        header.Children.Add(said);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 16, 24, 16) };
        foreach (var button in buttons)
        {
            if (button is FrameworkElement each) each.Margin = new Thickness(8, 0, 0, 0);
            footer.Children.Add(button);
        }
        var stack = new StackPanel { Children = { header } };
        if (panel is not null) stack.Children.Add(panel);
        stack.Children.Add(footer);
        return new Border
        {
            MaxWidth = maxWidth,
            Margin = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Ocean.Glass,
            BorderBrush = Ocean.GlassBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Child = new Grid { Children = { stack, closer } },
        };
    }

    // T3's buttons: outline, and primary.
    private static Button DialogButton(string words, Action click, bool primary, bool enabled = true)
    {
        var text = Ocean.Text(words, primary ? Ocean.OnPrimary : Ocean.Foreground, 14, FontWeights.Medium);
        var button = Ocean.Ghost(text, click, 8, new Thickness(12, 0, 12, 0), primary ? Ocean.PrimaryHover : Ocean.RowHover, primary ? Ocean.Primary : null);
        button.Height = 32;
        if (!primary)
        {
            button.BorderBrush = Ocean.Border;
            button.BorderThickness = new Thickness(1);
        }
        button.IsEnabled = enabled;
        button.Opacity = enabled ? 1 : 0.64;
        AutomationProperties.SetName(button, words);
        return button;
    }

    // T3's commit dialog: the branch, the files (each can be left out while editing), and an
    // optional message, which is written for it when left empty.
    private void ShowCommitDialog(AgentConversation conversation, string folder, RepoState? state)
    {
        var excluded = new HashSet<string>();
        var editing = false;
        List<GitTools.ChangedFile> files = [];
        var filesArea = new StackPanel();
        Button? commit = null, newBranch = null;

        var message = new TextBox
        {
            Background = new SolidColorBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)),
            BorderBrush = Ocean.Border,
            BorderThickness = new Thickness(1),
            Foreground = Ocean.Foreground,
            CaretBrush = Ocean.Foreground,
            SelectionBrush = Ocean.Focus,
            FontSize = 14,
            Padding = new Thickness(10, 6, 10, 6),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 36,
            MaxHeight = 256,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        AutomationProperties.SetName(message, "Commit message");
        var hint = Ocean.Text("Leave empty to auto-generate", Ocean.Placeholder, 14);
        hint.IsHitTestVisible = false;
        hint.Margin = new Thickness(11, 7, 0, 0);
        hint.VerticalAlignment = VerticalAlignment.Top;
        message.TextChanged += (_, _) => hint.Visibility = message.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        FrameworkElement Tick(bool on, Action flip)
        {
            var box = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(4),
                BorderBrush = on ? Ocean.Primary : Ocean.Border,
                BorderThickness = new Thickness(1),
                Background = on ? Ocean.Primary : Brushes.Transparent,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 8, 0),
                Child = on ? AgentIcons.Line("check", 12, Ocean.OnPrimary, 2.5) : null,
            };
            box.MouseLeftButtonUp += (_, click) =>
            {
                click.Handled = true;
                flip();
            };
            return box;
        }

        void Draw()
        {
            filesArea.Children.Clear();
            var included = files.Where(file => !excluded.Contains(file.Path)).ToList();
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            if (files.Count > 0)
            {
                var edit = Ocean.Ghost(Ocean.Text(editing ? "Done" : "Edit", Ocean.Foreground, 12, FontWeights.Medium), () =>
                {
                    editing = !editing;
                    Draw();
                }, 6, new Thickness(8, 2, 8, 2), Ocean.RowHover);
                AutomationProperties.SetName(edit, editing ? "Done" : "Edit");
                DockPanel.SetDock(edit, Dock.Right);
                header.Children.Add(edit);
            }
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (editing && files.Count > 0)
            {
                left.Children.Add(Tick(excluded.Count == 0, () =>
                {
                    if (excluded.Count == 0) excluded.UnionWith(files.Select(file => file.Path));
                    else excluded.Clear();
                    Draw();
                }));
            }
            left.Children.Add(Ocean.Text("Files", Ocean.MutedText, 14));
            if (excluded.Count > 0 && !editing) left.Children.Add(Ocean.Text($" ({included.Count} of {files.Count})", Ocean.MutedText, 14));
            header.Children.Add(left);
            filesArea.Children.Add(header);

            if (files.Count == 0)
            {
                filesArea.Children.Add(Ocean.Text("none", Ocean.Foreground, 14, FontWeights.Medium));
            }
            else
            {
                var rows = new StackPanel { Margin = new Thickness(4) };
                foreach (var file in files)
                {
                    var left_out = excluded.Contains(file.Path);
                    var line = new DockPanel();
                    var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
                    if (left_out)
                    {
                        right.Children.Add(new TextBlock { Text = "Excluded", Foreground = Ocean.MutedText, FontFamily = Ocean.Mono, FontSize = 13 });
                    }
                    else
                    {
                        right.Children.Add(new TextBlock { Text = $"+{file.Insertions:N0}", Foreground = Addition, FontFamily = Ocean.Mono, FontSize = 13 });
                        right.Children.Add(new TextBlock { Text = " / ", Foreground = Ocean.MutedText, FontFamily = Ocean.Mono, FontSize = 13 });
                        right.Children.Add(new TextBlock { Text = $"-{file.Deletions:N0}", Foreground = Deletion, FontFamily = Ocean.Mono, FontSize = 13 });
                    }
                    DockPanel.SetDock(right, Dock.Right);
                    line.Children.Add(right);
                    if (editing)
                    {
                        var path = file.Path;
                        var tick = Tick(!left_out, () =>
                        {
                            if (!excluded.Remove(path)) excluded.Add(path);
                            Draw();
                        });
                        DockPanel.SetDock(tick, Dock.Left);
                        line.Children.Add(tick);
                    }
                    line.Children.Add(new TextBlock { Text = file.Path, Foreground = left_out ? Ocean.MutedText : Ocean.Foreground, FontFamily = Ocean.Mono, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = file.Path });
                    rows.Children.Add(new Border { Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(6), Child = line });
                }
                filesArea.Children.Add(new Border
                {
                    Height = 176,
                    CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush(Color.FromArgb(0x06, 0xFF, 0xFF, 0xFF)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1),
                    Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = rows },
                });
                var totals = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
                totals.Children.Add(new TextBlock { Text = $"+{included.Sum(file => file.Insertions):N0}", Foreground = Addition, FontFamily = Ocean.Mono, FontSize = 13 });
                totals.Children.Add(new TextBlock { Text = " / ", Foreground = Ocean.MutedText, FontFamily = Ocean.Mono, FontSize = 13 });
                totals.Children.Add(new TextBlock { Text = $"-{included.Sum(file => file.Deletions):N0}", Foreground = Deletion, FontFamily = Ocean.Mono, FontSize = 13 });
                filesArea.Children.Add(totals);
            }
            var any = included.Count > 0;
            foreach (var button in new[] { commit, newBranch })
            {
                if (button is null) continue;
                button.IsEnabled = any;
                button.Opacity = any ? 1 : 0.64;
            }
        }

        // The branch, with a warning when it's the default one.
        var branchRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        branchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        branchRow.ColumnDefinitions.Add(new ColumnDefinition());
        var branchLabel = Ocean.Text("Branch", Ocean.MutedText, 14);
        branchLabel.Margin = new Thickness(0, 0, 8, 0);
        branchRow.Children.Add(branchLabel);
        var branchLine = new DockPanel();
        if (state?.OnDefaultBranch == true)
        {
            var warning = Ocean.Text("Default branch", Ocean.Warning, 14);
            DockPanel.SetDock(warning, Dock.Right);
            branchLine.Children.Add(warning);
        }
        branchLine.Children.Add(Ocean.Text(state?.Branch ?? "(detached HEAD)", Ocean.Foreground, 14, FontWeights.Medium));
        Grid.SetColumn(branchLine, 1);
        branchRow.Children.Add(branchLine);

        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0x09, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Child = new StackPanel { Children = { branchRow, filesArea } },
        };
        var messageLabel = Ocean.Text("Commit message (optional)", Ocean.Foreground, 14, FontWeights.Medium);
        messageLabel.Margin = new Thickness(0, 16, 0, 4);
        var panel = new StackPanel { Margin = new Thickness(24, 4, 24, 4), Children = { card, messageLabel, new Grid { Children = { message, hint } } } };

        IReadOnlyList<string>? Only() => excluded.Count == 0 ? null : [.. files.Select(file => file.Path).Where(path => !excluded.Contains(path))];
        var cancel = DialogButton("Cancel", CloseDialog, primary: false);
        newBranch = DialogButton("Commit on new branch", () =>
        {
            CloseDialog();
            _ = RunGitActionAsync(conversation, folder, GitAction.Commit, message.Text.Trim(), featureBranch: true, Only());
        }, primary: false);
        commit = DialogButton("Commit", () =>
        {
            CloseDialog();
            _ = RunGitActionAsync(conversation, folder, GitAction.Commit, message.Text.Trim(), featureBranch: false, Only());
        }, primary: true);
        Draw();
        ShowDialog(DialogCard(512, "Commit changes", "Review and confirm your commit. Leave the message blank to auto-generate one.", panel, CloseDialog, cancel, newBranch, commit));
        Dispatcher.InvokeAsync(() => Keyboard.Focus(message), DispatcherPriority.Input);
        _ = GitTools.ChangedFilesAsync(folder).ContinueWith(task => Dispatcher.Invoke(() =>
        {
            files = task.Result;
            Draw();
        }), TaskScheduler.Default);
    }

    // T3's hz: before pushing or opening a pull request from the default branch.
    private void ShowDefaultBranchDialog(AgentConversation conversation, string folder, GitAction action, string? message, IReadOnlyList<string>? only, string branch, bool hasChanges)
    {
        var tail = $" on \"{branch}\". You can continue on this ref or create a feature ref and run the same action there.";
        var withCommit = Commits(action);
        var (title, description, go) = OpensPr(action)
            ? withCommit
                ? ("Commit, push & create PR from default ref?", $"This action will commit, push, and create a pull request{tail}", "Commit, push & create PR")
                : ("Push & create PR from default ref?", $"This action will push local commits and create a pull request{tail}", "Push & create PR")
            : withCommit
                ? ("Commit & push to default ref?", $"This action will commit and push changes{tail}", $"Commit & push to {branch}")
                : ("Push to default ref?", $"This action will push local commits{tail}", $"Push to {branch}");
        UIElement? note = null;
        if (!hasChanges)
        {
            var words = Ocean.Wrapped($"There are no changes to commit, so a feature branch can't be named. Create a branch yourself or continue on {branch}.", Ocean.MutedText, 12, 16);
            words.Margin = new Thickness(24, 0, 24, 0);
            note = words;
        }
        var abort = DialogButton("Abort", CloseDialog, primary: false);
        var proceed = DialogButton(go, () =>
        {
            CloseDialog();
            _ = RunGitActionAsync(conversation, folder, action, message, featureBranch: false, only);
        }, primary: false);
        var feature = DialogButton("Check out feature branch & continue", () =>
        {
            CloseDialog();
            _ = RunGitActionAsync(conversation, folder, action, message, featureBranch: true, only);
        }, primary: true, enabled: hasChanges);
        ShowDialog(DialogCard(576, title, description, note, CloseDialog, abort, proceed, feature));
    }
}
