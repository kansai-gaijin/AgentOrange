using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>Draws a project's page from what its check found.</summary>
public partial class MainWindow
{
    // Drawing the page again would otherwise send someone using the keyboard back to the start.
    private void RenderProjectKeepingFocus(Project project)
    {
        static IEnumerable<Button> Buttons(DependencyObject root)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is Button button) yield return button;
                foreach (var inside in Buttons(child)) yield return inside;
            }
        }

        var focused = Buttons(ProjectPage).FirstOrDefault(button => button.IsKeyboardFocusWithin)?.Content as string;
        RenderProject(project);
        if (focused is null) return;
        _ = Dispatcher.InvokeAsync(() => Buttons(ProjectPage).FirstOrDefault(button => button.Content as string == focused)?.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void RenderProject(Project project)
    {
        _openProject = project;
        _shownDownloadState = DownloadStateOf(_database.GetProjectFolder(project.Id));
        ProjectPage.Children.Clear();
        ProjectPage.Children.Add(ProjectHero(project));

        var body = new StackPanel { Margin = new Thickness(40, 20, 40, 0), MaxWidth = 840, HorizontalAlignment = HorizontalAlignment.Left };
        body.Children.Add(new TextBlock { Text = project.Summary, Foreground = (Brush)FindResource("TextNormal"), FontSize = 14 });
        body.Children.Add(new TextBlock { Text = project.RepoUrl, Foreground = (Brush)FindResource("TextMuted"), FontSize = 13, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = project.RepoUrl });

        // Two PCs can each add the same repo before they've synced, and only this can show that.
        if (project.RepoUrl.Length > 0 && _database.ListProjects().FirstOrDefault(other => other.Id != project.Id && GitTools.SameRepo(other.RepoUrl, project.RepoUrl)) is { } twin)
        {
            body.Children.Add(new TextBlock { Text = $"{twin.Name} uses this repo too, and a repo can only be one project. This can happen when two PCs add it before syncing. Delete the extra one in its project settings.", Foreground = (Brush)FindResource("Accent"), FontSize = 13, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
        }
        if (_renamedRepoHeldBy is { } holder)
        {
            body.Children.Add(new TextBlock { Text = $"GitHub says this repo has been renamed, and {holder} already uses it under its new name. A repo can only be one project, so delete the extra one in its project settings.", Foreground = (Brush)FindResource("Accent"), FontSize = 13, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
        }
        ProjectPage.Children.Add(body);

        if (_projectCheck is not { } check)
        {
            body.Children.Add(CenteredNote(_busyProjectId == project.Id ? "The repo is checked once this finishes." : "Checking the repo…"));
            AddOwnBusy(body, project);
            AddPageProblem(body);
            return;
        }

        // Getting no answer doesn't mean access was lost, so a PC that's reached this exact repo before can keep working.
        if (!CanWorkIn(project, check))
        {
            AddRepoProblem(body, project, check);
            return;
        }

        var hasHadAccess = _database.GetSetting(VerifiedSetting(project.Id)) == GitTools.RepoKey(project.RepoUrl);
        if (check.Access != RepoAccess.Allowed)
        {
            var (reason, forNewProject) = check.Access switch
            {
                RepoAccess.Offline => ("Couldn't reach GitHub, so the repo wasn't checked.", "This new project's repo hasn't been checked yet. That happens the next time it opens with a connection."),
                RepoAccess.GitPushUnchecked => ("GitHub says you have access, but Git on this PC couldn't finish checking whether it can push.", "Whether this PC can push to it hasn't been checked yet. It's checked again the next time this opens. If this keeps happening, check this PC's Git setup."),
                RepoAccess.GitOffline => ("GitHub says you have access, but Git on this PC couldn't connect to it.", "This PC hasn't reached it through Git yet. It's checked again the next time this opens. If this keeps happening, check this PC's network for Git, such as a proxy or a blocked SSH port."),
                _ => ("GitHub didn't give a clear answer, so the repo wasn't checked.", "This new project's repo hasn't been checked yet. It's checked again the next time this opens."),
            };
            body.Children.Add(new TextBlock { Text = hasHadAccess ? $"{reason} This PC has had access before." : $"{reason} {forNewProject}", Foreground = (Brush)FindResource("Accent"), FontSize = 13, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
        }

        var folder = _database.GetProjectFolder(project.Id);
        if (_folderExists && _folderProblem is null && !_folderLacksFiles)
        {
            body.Children.Add(new TextBlock { Text = folder, Foreground = (Brush)FindResource("TextMuted"), FontSize = 13, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = folder });
            if (_folderPushesTo is { } pushesTo)
            {
                body.Children.Add(new TextBlock { Text = $"This folder pushes to {pushesTo}, not this project's repo. That's normal for a fork, but check that it's what you want.", Foreground = (Brush)FindResource("Accent"), FontSize = 13, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
            }
            if (_folderEngine is { } found && found != project.Engine)
            {
                body.Children.Add(new TextBlock { Text = $"This folder looks like a {found} project, but this project is set to {project.Engine}. You can change that in project settings.", Foreground = (Brush)FindResource("Accent"), FontSize = 13, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
            }
            if (_folderMayNotMatch)
            {
                body.Children.Add(new TextBlock { Text = "This folder doesn't have the repo's latest commits. It may just be behind, but it may not be the same project, so check before pushing.", Foreground = (Brush)FindResource("Accent"), FontSize = 13, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
            }
            if (_busyProjectId != project.Id && _shownDownloadState.Held)
            {
                body.Children.Add(new TextBlock { Text = "Another window of the app is finishing this folder's download.", Foreground = (Brush)FindResource("TextMuted"), FontSize = 13, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
            }
            else if (_busyProjectId != project.Id && _shownDownloadState.Unfinished)
            {
                body.Children.Add(new TextBlock { Text = "This folder's download never finished, so some of its files or submodules may be missing, or be small placeholders. Finishing picks up where it left off.", Foreground = (Brush)FindResource("Accent"), FontSize = 13, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
                if (_settingsToAllow is not null || _finishSettingsToAllow is not null)
                {
                    // Finishing runs Git in the folder, so these have to be looked at first.
                    var rows = new List<FrameworkElement>();
                    AddFolderSettingsRow(rows, project, folder!);
                    AddFinishSettingsRow(rows, project, folder!);
                    var allow = Card(rows.ToArray());
                    allow.Margin = new Thickness(0, 8, 0, 0);
                    body.Children.Add(allow);
                }
                else
                {
                    var finish = ActionButton("Finish the download");
                    finish.HorizontalAlignment = HorizontalAlignment.Left;
                    finish.Margin = new Thickness(0, 8, 0, 0);
                    finish.IsEnabled = _busyProjectId is null;
                    finish.Click += async (_, _) => await FinishDownloadAsync(project, folder!);
                    body.Children.Add(finish);
                    if (_busyProjectId is { } busyId)
                    {
                        body.Children.Add(new TextBlock { Text = $"{FindProject(busyId)?.Name ?? "Another project"} is busy. This will be available when it finishes.", FontSize = 13, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
                    }
                }

                // Some of what can stop this only the person can sort out, and it can't say so forever.
                var sorted = TwoClickAction("I've sorted this myself", async () =>
                {
                    await GitTools.ForgetUnfinishedDownloadAsync(folder!);
                    _finishSettingsToAllow = null;
                    _pageProblem = null;
                    if (_openProject?.Id == project.Id) RenderProject(_openProject);
                });
                sorted.HorizontalAlignment = HorizontalAlignment.Left;
                sorted.Margin = new Thickness(0, 8, 0, 0);
                sorted.IsEnabled = _busyProjectId is null;
                body.Children.Add(sorted);
            }
            body.Children.Add(CenteredNote("Nothing here yet."));
            AddOwnBusy(body, project);
            AddPageProblem(body);
            return;
        }

        AddGetProjectOptions(body, project, folder);
    }

    private TextBlock CenteredNote(string text) => new()
    {
        Text = text,
        Foreground = (Brush)FindResource("TextMuted"),
        FontSize = 14,
        Margin = new Thickness(0, 60, 0, 0),
    };

    // The page is drawn again for every batch of a sync, and the picture rarely changes.
    private ImageSource? HeaderFor(string projectId)
    {
        var bytes = _database.GetProjectImage(projectId, ProjectImageKind.Header);
        if (bytes is null) return null;
        if (_header is { } cached && cached.ProjectId == projectId && cached.Bytes.AsSpan().SequenceEqual(bytes)) return cached.Image;

        var image = ProjectImages.ToImage(bytes);
        _header = (projectId, bytes, image);
        return image;
    }

    private Grid ProjectHero(Project project)
    {
        var header = HeaderFor(project.Id);
        var hero = new Grid { Height = 240 };
        hero.Children.Add(new Border { Background = header is null ? PlaceholderFor(project.Id) : new ImageBrush(header) { Stretch = Stretch.UniformToFill } });

        // Keeps the title readable on any picture.
        hero.Children.Add(new Border { Background = new LinearGradientBrush(Color.FromArgb(0, 0x17, 0x21, 0x2B), Color.FromRgb(0x17, 0x21, 0x2B), 90) });
        hero.Children.Add(new TextBlock
        {
            Text = project.Name,
            Foreground = Brushes.White,
            FontSize = 34,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(40, 0, 40, 12),
        });

        var back = OverlayButton("← Projects", () =>
        {
            BackToProjects();
            RefreshProjects(); // Cards say how long operations are going, and that may have changed.
        });
        back.HorizontalAlignment = HorizontalAlignment.Left;
        back.VerticalAlignment = VerticalAlignment.Top;
        back.Margin = new Thickness(26, 18, 0, 0);
        hero.Children.Add(back);

        var settings = OverlayButton("Project settings", () => OpenProjectSettings(project.Id));
        settings.HorizontalAlignment = HorizontalAlignment.Right;
        settings.VerticalAlignment = VerticalAlignment.Top;
        settings.Margin = new Thickness(0, 18, 26, 0);
        hero.Children.Add(settings);
        return hero;
    }

    private Button OverlayButton(string label, Action onClick)
    {
        var button = new Button { Style = (Style)FindResource("OverlayButton"), Content = label };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void AddRepoProblem(StackPanel body, Project project, RepoCheck check)
    {
        var actions = new List<FrameworkElement>();
        var (heading, description) = check.Describe();
        var busy = _busyProjectId is not null;

        if (check.Access == RepoAccess.SignedOut)
        {
            var signIn = ActionButton("Sign in");
            signIn.Click += (_, _) => GitTools.OpenGitHubSignIn();
            actions.Add(signIn);
        }

        if (check.Access == RepoAccess.OtherAccount)
        {
            var switchAccount = ActionButton($"Switch to {check.Account}");
            switchAccount.IsEnabled = !busy;
            switchAccount.Click += async (_, _) =>
            {
                if (await GitTools.SwitchAccountAsync(check.Account!))
                {
                    await RecheckIfStillOpenAsync(project.Id);
                }
                else if (_openProject?.Id == project.Id)
                {
                    _pageProblem = "Couldn't switch accounts.";
                    RenderProject(_openProject);
                }
            };
            actions.Add(switchAccount);
        }

        var canReplace = check.Access is RepoAccess.NotFound or RepoAccess.NotFoundOrNoAccess;
        if (canReplace && !_confirmingNewRepo)
        {
            var create = ActionButton("Create new repo");
            create.IsEnabled = !busy;
            create.Click += (_, _) =>
            {
                _confirmingNewRepo = true;
                _replacementRepoName = GitTools.RepoName(project.Name);
                RenderProject(project);
            };
            actions.Add(create);
        }

        var retry = ActionButton("Try again");
        retry.IsEnabled = !busy;
        retry.Click += async (_, _) => await RecheckIfStillOpenAsync(project.Id);
        actions.Add(retry);

        body.Children.Add(Section(heading));
        body.Children.Add(new TextBlock { Text = canReplace ? $"{description} You can change the URL in project settings, or create a new private repo." : description, Foreground = (Brush)FindResource("TextMuted"), FontSize = 13, Margin = new Thickness(16, -4, 16, 12), TextWrapping = TextWrapping.Wrap });

        if (canReplace && _confirmingNewRepo)
        {
            var confirm = new Button { Style = (Style)FindResource("PrimaryButton"), Content = "Yes, create it", IsEnabled = !busy };
            confirm.Click += async (_, _) => await EndBusyIfDatabaseFailsAsync(project.Id, () => CreateReplacementRepoAsync(project, check));
            var cancel = ActionButton("Cancel");
            cancel.IsEnabled = !busy;
            cancel.Click += (_, _) =>
            {
                _confirmingNewRepo = false;
                RenderProject(project);
            };
            actions = [confirm, cancel];

            var destination = check.Account is null ? "your active GitHub account" : $"the GitHub account {check.Account}";
            var mayStillExist = check.Access == RepoAccess.NotFoundOrNoAccess ? " The old repo may still exist, even though you can't see it." : "";

            body.Children.Add(new Border
            {
                BorderBrush = (Brush)FindResource("Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(16, 0, 16, 12),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"This creates a new, empty private repo under {destination} and moves this project to it on all your PCs, replacing {project.RepoUrl}.{mayStillExist} Nothing is pushed, so push your work afterward for your other PCs to get it.",
                            Foreground = (Brush)FindResource("TextNormal"),
                            FontSize = 13,
                            TextWrapping = TextWrapping.Wrap,
                        },
                        ReplacementRepoNameRow(),
                    },
                },
            });
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var action in actions)
        {
            action.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(action);
        }
        body.Children.Add(buttons);

        // A copy on this PC may reach the repo in a way the URL alone doesn't, such as its own SSH setup.
        if (check.Access is RepoAccess.GitCantReach or RepoAccess.GitReadOnly || check.Access == RepoAccess.GitOffline && _settingsToAllow is { ChangesConnection: true })
        {
            var rows = new List<FrameworkElement>();
            var linked = _database.GetProjectFolder(project.Id);
            if (linked is not null) AddFolderSettingsRow(rows, project, linked);

            if (linked is not null && _folderExists && _folderProblem is not null && GitTools.ConnectingCanFix(_folderProblem))
            {
                // The project moved repos elsewhere, and this PC may only get in the way this folder does.
                var connect = TwoClickAction("Connect it", () => EndBusyIfDatabaseFailsAsync(project.Id, () => PointFolderAtRepoAsync(project, linked)));
                connect.IsEnabled = !busy;
                rows.Add(Row("Keep using this folder", "If the project moved repos, this points the folder's remote at the new one, the same way it connects now. Its files don't change.", connect));
            }
            else if (linked is not null && _folderExists)
            {
                var repair = TwoClickAction("Repair it", () => EndBusyIfDatabaseFailsAsync(project.Id, () => RepairRemoteAsync(project, linked)));
                repair.IsEnabled = !busy;
                rows.Add(Row("Repair this folder's connection", "Switches it between HTTPS and SSH, whichever works from this PC.", repair));
            }
            rows.Add(Row("Select its folder", "If a copy on this PC can already reach this repo, that's enough.", SelectFolderButton(project)));

            var options = Card(rows.ToArray());
            options.Margin = new Thickness(0, 12, 0, 0);
            body.Children.Add(options);
        }
        AddBusyAndProblem(body, project);
    }

    private void AddBusyAndProblem(StackPanel body, Project project)
    {
        if (_busyProjectId is not null && _busyProjectId != project.Id)
        {
            var other = FindProject(_busyProjectId)?.Name ?? "Another project";
            body.Children.Add(new TextBlock { Text = $"{other} is busy. These will be available when it finishes.", FontSize = 13, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(16, 10, 0, 0), TextWrapping = TextWrapping.Wrap });
        }

        AddOwnBusy(body, project);
        AddPageProblem(body);
    }

    // On every page state, because Cancel for a clone has to stay within reach.
    private void AddOwnBusy(StackPanel body, Project project)
    {
        if (_busyProjectId != project.Id || _busyText is null) return;

        _busyLabel = new TextBlock { Text = _busyText, FontSize = 13, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(16, 10, 0, 0) };
        body.Children.Add(_busyLabel);
        if (_cloneCancel is not { } clone) return;

        var stop = ActionButton("Cancel");
        stop.HorizontalAlignment = HorizontalAlignment.Left;
        stop.Margin = new Thickness(16, 8, 0, 0);
        stop.Click += (_, _) =>
        {
            if (_cloneCancel == clone) clone.Cancel();
        };
        body.Children.Add(stop);
    }

    private void AddPageProblem(StackPanel body)
    {
        if (_pageProblem is null)
        {
            _scrolledToProblem = null;
            return;
        }

        var problem = ProblemText();
        ShowProblem(problem, _pageProblem);
        problem.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(problem);

        // It goes below everything else, which on a small window is below the fold, and whoever
        // just clicked something needs to see why nothing seemed to happen. Only once, because
        // the page is drawn again as things sync and shouldn't keep moving under someone reading.
        if (_scrolledToProblem == _pageProblem) return;
        _scrolledToProblem = _pageProblem;
        Dispatcher.InvokeAsync(problem.BringIntoView, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // A project's name may have nothing a repo's name can use, such as one in Japanese.
    private Grid ReplacementRepoNameRow()
    {
        var name = Field(_replacementRepoName ?? "");
        name.Width = 260;
        name.TextChanged += (_, _) => _replacementRepoName = name.Text;
        return Row("Repo name", "GitHub needs Latin letters and numbers. Anything else becomes a dash.", name);
    }

    private void AddFolderSettingsRow(List<FrameworkElement> rows, Project project, string folder)
    {
        if (_settingsToAllow is not { } settings) return;
        if (!settings.CanBeApproved)
        {
            rows.Add(Row("This folder's own Git settings can't be used", "There are too many of them to show here, and they can only be allowed once you've been able to read them all. Cut them down, or use another copy."));
            return;
        }

        var allow = TwoClickAction("Allow", () => AllowFolderSettingsAsync(project, folder, settings.Fingerprint));
        allow.IsEnabled = _busyProjectId is null;
        rows.Add(Row("Use this folder's own Git settings", $"They can run programs or send your login somewhere else, so only allow them if you trust where this folder came from: {settings.Shown}", allow));
    }

    private void AddFinishSettingsRow(List<FrameworkElement> rows, Project project, string folder)
    {
        if (_finishSettingsToAllow is not { } settings) return;
        if (!settings.CanBeApproved)
        {
            rows.Add(Row("This folder's own Git settings can't be used", "There are too many of them to show here, and they can only be allowed once you've been able to read them all. Cut them down, or use another copy."));
            return;
        }

        var allow = TwoClickAction("Allow", async () =>
        {
            GitTools.RememberAllowedFolderSettings(GitTools.FinishApprovalKey(folder), settings.Fingerprint);
            _finishSettingsToAllow = null;
            await FinishDownloadAsync(project, folder);
        });
        allow.IsEnabled = _busyProjectId is null;
        rows.Add(Row("Finishing would use this folder's own Git settings", $"They can run programs, so only allow them if you trust where this folder came from: {settings.Shown}", allow));
    }

    private void AddGetProjectOptions(StackPanel body, Project project, string? linkedFolder)
    {
        var busy = _busyProjectId is not null;
        var select = SelectFolderButton(project);
        var clone = ActionButton("Clone");
        clone.IsEnabled = !busy;

        clone.Click += async (_, _) =>
        {
            if (Dialogs.PickFolder("Choose where to put the project") is not { } parentFolder) return;

            // The dialog can stay open a long time, and sync keeps running behind it.
            if (FindProject(project.Id) is not { } latest || !GitTools.SameRepo(latest.RepoUrl, project.RepoUrl))
            {
                await RecheckIfStillOpenAsync(project.Id);
                return;
            }

            // An empty folder is where the project goes, not a place to put another folder in.
            // Busy while that's looked at, because a slow drive leaves time for a second click.
            if (_busyProjectId is not null) return;
            var chosen = Path.TrimEndingDirectorySeparator(parentFolder);
            var aboveIt = Path.GetDirectoryName(chosen);
            bool intoIt;
            try
            {
                StartBusy(latest, "Checking that folder…");
                intoIt = aboveIt is not null && await GitTools.FolderIsEmptyAsync(chosen) && !await GitTools.FolderIsLinkAsync(chosen);
            }
            finally
            {
                EndBusy();
            }
            await (intoIt ? CloneProjectAsync(latest, aboveIt!, chosen) : CloneProjectAsync(latest, parentFolder));
        };

        var (heading, description) = (linkedFolder, _folderProblem, _folderExists) switch
        {
            _ when _folderLacksFiles => ("This folder doesn't have the repo's files yet", $"It's at {linkedFolder} and connected to the repo, but it has no commits, and the repo does. Pull them yourself and try again, or clone a fresh copy."),
            (_, not null, _) => ("This project's folder doesn't match its repo", $"It's at {linkedFolder}. {_folderProblem}"),
            (null, _, _) => ("This project isn't on this PC yet", "Get a copy from its repo, or point to one you already have."),
            _ => ("This project's folder is missing", $"It was at {linkedFolder}."),
        };
        body.Children.Add(Section(heading));
        body.Children.Add(new TextBlock { Text = description, Foreground = (Brush)FindResource("TextMuted"), FontSize = 13, Margin = new Thickness(16, -4, 0, 10), TextWrapping = TextWrapping.Wrap });

        var rows = new List<FrameworkElement>();

        // Its SSH settings have to run before it can be told whether this is even the right repo.
        if (linkedFolder is not null && _folderProblem?.StartsWith(GitTools.CantTellSshSettings, StringComparison.Ordinal) == true)
        {
            AddFolderSettingsRow(rows, project, linkedFolder);
        }

        if (linkedFolder is not null && _folderExists && _folderProblem is not null && GitTools.ConnectingCanFix(_folderProblem))
        {
            // The project may have moved to a new repo on another PC, with this copy's work unpushed.
            var connect = TwoClickAction("Connect it", () => EndBusyIfDatabaseFailsAsync(project.Id, () => PointFolderAtRepoAsync(project, linkedFolder)));
            connect.IsEnabled = !busy;
            rows.Add(Row("Keep using this folder", "If the project moved repos, this points the folder's remote at the new one. Its files don't change.", connect));
        }
        rows.Add(Row("Clone from repo", project.RepoUrl, clone));
        rows.Add(Row("Select its folder", "It has to be connected to the same repo.", select));
        body.Children.Add(Card(rows.ToArray()));

        // A missing folder may just be on a drive that isn't plugged in.
        if (linkedFolder is not null)
        {
            var retry = ActionButton("Try again");
            retry.IsEnabled = !busy;
            retry.HorizontalAlignment = HorizontalAlignment.Left;
            retry.Margin = new Thickness(16, 10, 0, 0);
            retry.Click += async (_, _) => await RecheckIfStillOpenAsync(project.Id);
            body.Children.Add(retry);
        }
        AddBusyAndProblem(body, project);
    }

    // Only on this PC, and for the settings as they were when they were seen.
    private async Task AllowFolderSettingsAsync(Project project, string folder, string fingerprint)
    {
        _database.AllowFolderSettings(folder, fingerprint);
        await RecheckIfStillOpenAsync(project.Id);
    }
}
