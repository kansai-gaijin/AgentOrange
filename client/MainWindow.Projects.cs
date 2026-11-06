using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;
using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>
/// The project library and each project's page. Checks and clones finish whenever
/// they finish, so everything that continues after one re-reads the project and
/// compares _navigation first, in case the person has moved on or the project changed.
/// </summary>
public partial class MainWindow
{
    private const int CoverWidth = 176;
    private const int CoverHeight = 264;
    private const double DragScrollEdge = 48;
    private const double DragScrollStep = 18;
    private static readonly TimeSpan ClickSuppressionAfterDrag = TimeSpan.FromMilliseconds(400);

    private Project? _openProject;
    private RepoCheck? _projectCheck;
    private string? _folderProblem;
    private bool _folderExists;
    private string? _pageProblem;
    private bool _confirmingNewRepo;
    private int _navigation;

    // Only one long operation runs at a time. It keeps going if the person leaves the page.
    private string? _busyProjectId;
    private string? _busyText;
    private CancellationTokenSource? _cloneCancel;

    private Button? _pressedCard;
    private Button? _draggedCard;
    private Point _pressPoint;
    private DateTimeOffset _dragEnded;
    private bool _refreshAfterDrag;

    private void RefreshProjects()
    {
        if (_draggedCard is not null)
        {
            _refreshAfterDrag = true;
            return;
        }

        var projects = _database.ListProjects();
        ProjectGrid.Children.Clear();
        foreach (var project in projects) ProjectGrid.Children.Add(ProjectCard(project));
        NoProjects.Visibility = projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // No one can reach a clone's Cancel button once its project is gone.
        if (_busyProjectId is not null && projects.All(project => project.Id != _busyProjectId)) _cloneCancel?.Cancel();

        ProjectSettings.ProjectsChanged();
        if (_openProject is null) return;

        // The open project may have changed on another PC.
        var current = projects.FirstOrDefault(project => project.Id == _openProject.Id);
        if (current is null)
        {
            BackToProjects();
        }
        else if (!GitTools.SameRepo(current.RepoUrl, _openProject.RepoUrl))
        {
            _ = OpenProjectAsync(current);
        }
        else
        {
            RenderProject(current); // Its header image syncs as a separate record.
        }
    }

    // Library

    private Button ProjectCard(Project project)
    {
        var thumbnail = ProjectImages.ToImage(_database.GetProjectImage(project.Id, ProjectImageKind.Thumbnail), CoverWidth * 2);
        var cover = new Border
        {
            Width = CoverWidth,
            Height = CoverHeight,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Background = thumbnail is null ? PlaceholderFor(project.Id) : new ImageBrush(thumbnail) { Stretch = Stretch.UniformToFill },
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.35 },
        };

        if (thumbnail is null)
        {
            cover.Child = new TextBlock
            {
                Text = project.Name,
                Foreground = Brushes.White,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14),
            };
        }

        // Settings stay reachable from here, because a blocked project can't be opened.
        var gear = new Button
        {
            Style = (Style)FindResource("OverlayButton"),
            Content = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 13 },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 8, 0),
            Visibility = Visibility.Hidden,
        };
        AutomationProperties.SetName(gear, $"Settings for {project.Name}");
        gear.PreviewMouseLeftButtonDown += (_, _) => _pressedCard = null;
        gear.Click += (_, e) =>
        {
            e.Handled = true;
            OpenProjectSettings(project.Id);
        };

        var notHere = new TextBlock { Text = "Not on this PC", Foreground = (Brush)FindResource("Accent"), FontSize = 12.5, Margin = new Thickness(2, 2, 2, 0), Visibility = Visibility.Collapsed };
        _ = ShowIfNotOnThisPcAsync(project.Id, notHere);

        var coverArea = new Grid { Children = { cover, gear } };
        var content = new StackPanel { Children = { coverArea } };
        content.Children.Add(new TextBlock { Text = project.Name, Foreground = (Brush)FindResource("TextStrong"), FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 10, 2, 0), ToolTip = project.Name });
        content.Children.Add(new TextBlock { Text = project.Summary, Foreground = (Brush)FindResource("TextMuted"), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 2, 2, 0) });
        content.Children.Add(notHere);

        var card = new Button { Style = (Style)FindResource("ProjectCard"), Content = content, Tag = project.Id, AllowDrop = true };
        AutomationProperties.SetName(card, project.Name);

        card.MouseEnter += (_, _) =>
        {
            cover.BorderBrush = (Brush)FindResource("Accent");
            gear.Visibility = Visibility.Visible;
        };
        card.MouseLeave += (_, _) =>
        {
            cover.BorderBrush = Brushes.Transparent;
            gear.Visibility = Visibility.Hidden;
        };
        card.Click += async (_, _) =>
        {
            if (DateTimeOffset.UtcNow - _dragEnded < ClickSuppressionAfterDrag) return;
            await OpenProjectAsync(project);
        };

        card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _pressedCard = card;
            _pressPoint = e.GetPosition(ProjectGrid);
        };
        card.PreviewMouseLeftButtonUp += (_, _) => _pressedCard = null;
        card.PreviewMouseMove += (_, e) =>
        {
            if (_pressedCard != card || e.LeftButton != MouseButtonState.Pressed || _draggedCard is not null) return;
            var moved = e.GetPosition(ProjectGrid) - _pressPoint;
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            DragCard(card);
        };
        card.DragEnter += (_, e) =>
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            MoveDraggedCardTo(card);
        };

        return card;
    }

    // A disconnected network drive can take a long time to answer, so this stays off the UI thread.
    private async Task ShowIfNotOnThisPcAsync(string projectId, TextBlock label)
    {
        var folder = _database.GetProjectFolder(projectId);
        var here = folder is not null && await GitTools.FolderExistsAsync(folder);
        label.Visibility = here ? Visibility.Collapsed : Visibility.Visible;
    }

    // Colors picked from the id, so a project keeps its look on every PC.
    private static Brush PlaceholderFor(string projectId)
    {
        (Color, Color)[] pairs =
        [
            (Color.FromRgb(0x24, 0x5B, 0x68), Color.FromRgb(0x1B, 0x2C, 0x3C)),
            (Color.FromRgb(0x3B, 0x4F, 0x7A), Color.FromRgb(0x1C, 0x26, 0x3A)),
            (Color.FromRgb(0x4D, 0x45, 0x78), Color.FromRgb(0x20, 0x20, 0x38)),
            (Color.FromRgb(0x2F, 0x62, 0x55), Color.FromRgb(0x18, 0x2E, 0x2C)),
        ];
        var total = projectId.Sum(character => character);
        var (top, bottom) = pairs[total % pairs.Length];
        return new LinearGradientBrush(top, bottom, 60);
    }

    private void DragCard(Button card)
    {
        _draggedCard = card;
        _pressedCard = null;
        card.Opacity = 0.45;
        card.ReleaseMouseCapture();

        var before = CurrentOrder();
        var result = DragDrop.DoDragDrop(card, new DataObject(typeof(string), card.Tag), DragDropEffects.Move);

        card.Opacity = 1;
        _draggedCard = null;
        _dragEnded = DateTimeOffset.UtcNow;

        // Esc, or dropping outside the library, puts everything back.
        var after = CurrentOrder();
        if (result == DragDropEffects.Move && !before.SequenceEqual(after))
        {
            _database.SaveProjectOrder(after);
            _ = _sync.SyncNowAsync();
        }
        else if (!before.SequenceEqual(after))
        {
            _refreshAfterDrag = true;
        }

        if (_refreshAfterDrag)
        {
            _refreshAfterDrag = false;
            RefreshProjects();
        }
    }

    private void MoveDraggedCardTo(Button target)
    {
        if (_draggedCard is null || _draggedCard == target) return;
        var index = ProjectGrid.Children.IndexOf(target);
        ProjectGrid.Children.Remove(_draggedCard);
        ProjectGrid.Children.Insert(index, _draggedCard);
    }

    private List<string> CurrentOrder() =>
        ProjectGrid.Children.OfType<Button>().Select(card => (string)card.Tag).ToList();

    private void ProjectGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = _draggedCard is null ? DragDropEffects.None : DragDropEffects.Move;
        e.Handled = true;

        // A long library has to scroll, or a card can't reach the far end in one move.
        var y = e.GetPosition(ProjectScroller).Y;
        if (y < DragScrollEdge) ProjectScroller.ScrollToVerticalOffset(ProjectScroller.VerticalOffset - DragScrollStep);
        else if (y > ProjectScroller.ActualHeight - DragScrollEdge) ProjectScroller.ScrollToVerticalOffset(ProjectScroller.VerticalOffset + DragScrollStep);
    }

    private void ProjectGrid_Drop(object sender, DragEventArgs e)
    {
        e.Effects = _draggedCard is null ? DragDropEffects.None : DragDropEffects.Move;
        e.Handled = true;
    }

    // A project's page. Opening one first checks this PC is allowed to work on its repo.

    private async Task OpenProjectAsync(Project project)
    {
        var navigation = ++_navigation;
        _openProject = project;
        _projectCheck = null;
        _folderProblem = null;
        _folderExists = false;
        _pageProblem = null;
        _confirmingNewRepo = false;
        ProjectSelectView.Visibility = Visibility.Collapsed;
        ProjectView.Visibility = Visibility.Visible;
        RenderProject(project);

        var check = await GitTools.CheckRepoAccessAsync(project.RepoUrl);

        var folder = _database.GetProjectFolder(project.Id);
        var folderExists = folder is not null && await GitTools.FolderExistsAsync(folder);
        var folderProblem = folderExists && check.Access is RepoAccess.Allowed or RepoAccess.Offline
            ? await GitTools.FolderProblemAsync(folder!, project.RepoUrl)
            : null;

        if (navigation != _navigation) return; // They moved on, or something newer started.

        var latest = FindProject(project.Id);
        if (latest is null)
        {
            BackToProjects();
            return;
        }
        if (!GitTools.SameRepo(latest.RepoUrl, project.RepoUrl))
        {
            await OpenProjectAsync(latest);
            return;
        }

        if (check.Access == RepoAccess.Allowed) _database.SetSetting(VerifiedSetting(project.Id), GitTools.RepoKey(project.RepoUrl));
        else if (check.IsDenied) _database.SetSetting(VerifiedSetting(project.Id), "");

        _projectCheck = check;
        _folderExists = folderExists;
        _folderProblem = folderProblem;
        RenderProject(latest);
    }

    private Project? FindProject(string id) => _database.ListProjects().FirstOrDefault(project => project.Id == id);

    private static string VerifiedSetting(string projectId) => $"project_verified:{projectId}";

    private void RenderProject(Project project)
    {
        _openProject = project;
        ProjectPage.Children.Clear();
        ProjectPage.Children.Add(ProjectHero(project));

        var body = new StackPanel { Margin = new Thickness(40, 20, 40, 0), MaxWidth = 840, HorizontalAlignment = HorizontalAlignment.Left };
        body.Children.Add(new TextBlock { Text = project.Summary, Foreground = (Brush)FindResource("TextNormal"), FontSize = 14 });
        body.Children.Add(new TextBlock { Text = project.RepoUrl.Length > 0 ? project.RepoUrl : "No repo", Foreground = (Brush)FindResource("TextMuted"), FontSize = 13, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = project.RepoUrl });
        ProjectPage.Children.Add(body);

        if (_projectCheck is not { } check)
        {
            body.Children.Add(CenteredNote("Checking the repo…"));
            return;
        }

        // Being offline doesn't mean access was lost, so a PC that's reached this exact repo before can keep working.
        var workingOffline = check.Access == RepoAccess.Offline
            && _database.GetSetting(VerifiedSetting(project.Id)) == GitTools.RepoKey(project.RepoUrl);
        if (check.Access != RepoAccess.Allowed && !workingOffline)
        {
            AddRepoProblem(body, project, check);
            return;
        }

        if (workingOffline)
        {
            body.Children.Add(new TextBlock { Text = "Couldn't reach GitHub, so the repo wasn't checked. This PC has had access before.", Foreground = (Brush)FindResource("Accent"), FontSize = 13, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
        }

        var folder = _database.GetProjectFolder(project.Id);
        if (_folderExists && _folderProblem is null)
        {
            body.Children.Add(new TextBlock { Text = folder, Foreground = (Brush)FindResource("TextMuted"), FontSize = 13, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = folder });
            body.Children.Add(CenteredNote("Nothing here yet."));
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

    private Grid ProjectHero(Project project)
    {
        var header = ProjectImages.ToImage(_database.GetProjectImage(project.Id, ProjectImageKind.Header));
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

        var back = OverlayButton("← Projects", BackToProjects);
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
            confirm.Click += async (_, _) => await CreateReplacementRepoAsync(project);
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
                Child = new TextBlock
                {
                    Text = $"This creates a new, empty private repo under {destination} and moves this project to it on all your PCs, replacing {project.RepoUrl}.{mayStillExist} Nothing is pushed, so push your work afterward for your other PCs to get it.",
                    Foreground = (Brush)FindResource("TextNormal"),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
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
        AddBusyAndProblem(body, project);
    }

    private async Task RecheckIfStillOpenAsync(string projectId)
    {
        if (_openProject?.Id != projectId) return;
        if (FindProject(projectId) is { } latest) await OpenProjectAsync(latest);
        else BackToProjects();
    }

    private void AddBusyAndProblem(StackPanel body, Project project)
    {
        if (_busyProjectId is not null && _busyProjectId != project.Id)
        {
            var other = FindProject(_busyProjectId)?.Name ?? "Another project";
            body.Children.Add(new TextBlock { Text = $"{other} is busy. These will be available when it finishes.", FontSize = 13, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(16, 10, 0, 0), TextWrapping = TextWrapping.Wrap });
        }

        if (_busyProjectId == project.Id && _busyText is not null)
        {
            body.Children.Add(new TextBlock { Text = _busyText, FontSize = 13, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(16, 10, 0, 0) });
            if (_cloneCancel is { } clone)
            {
                var stop = ActionButton("Cancel");
                stop.HorizontalAlignment = HorizontalAlignment.Left;
                stop.Margin = new Thickness(16, 8, 0, 0);
                stop.Click += (_, _) => clone.Cancel();
                body.Children.Add(stop);
            }
        }

        if (_pageProblem is not null)
        {
            var problem = ProblemText();
            ShowProblem(problem, _pageProblem);
            problem.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(problem);
        }
    }

    private void StartBusy(Project project, string text)
    {
        _busyProjectId = project.Id;
        _busyText = text;
        _pageProblem = null;
        if (_openProject?.Id == project.Id) RenderProject(_openProject);
    }

    private void EndBusy()
    {
        _busyProjectId = null;
        _busyText = null;
        if (_openProject is not null) RenderProject(_openProject);
    }

    // Shows the result if the person is still looking at this project.
    private void ShowPageProblem(string projectId, string problem)
    {
        if (_openProject?.Id != projectId) return;
        _pageProblem = problem;
        RenderProject(_openProject);
    }

    private async Task CreateReplacementRepoAsync(Project project)
    {
        // Busy from the first click, so a second one can't create a second repo.
        if (_busyProjectId is not null) return;
        StartBusy(project, "Checking the project's folder…");

        // The linked folder only qualifies if it's still connected to the repo being replaced.
        var folder = _database.GetProjectFolder(project.Id);
        var folderIsUsable = folder is not null
            && await GitTools.FolderExistsAsync(folder)
            && !await GitTools.FolderUsesOtherRepoAsync(folder, project.RepoUrl);
        EndBusy();

        if (!folderIsUsable)
        {
            var dialog = new OpenFolderDialog { Title = $"Choose the folder for {project.Name}" };
            if (dialog.ShowDialog() != true) return;
            folder = dialog.FolderName;
        }

        StartBusy(project, "Creating a private repo on GitHub…");
        var result = await GitTools.CreatePrivateRepoAsync(project.Name, folder!, project.RepoUrl);
        EndBusy();

        if (!result.Worked)
        {
            ShowPageProblem(project.Id, result.Value);
            return;
        }

        // Saved even if they've left the page, because the repo now exists. A URL changed
        // in the meantime was chosen more recently, so it stays.
        if (FindProject(project.Id) is not { } latest || GitTools.RepoUrlProblem(result.Value) is not null)
        {
            ShowPageProblem(project.Id, "The repo was created, but the project couldn't be updated.");
            return;
        }
        if (!GitTools.SameRepo(latest.RepoUrl, project.RepoUrl))
        {
            ShowPageProblem(project.Id, $"The repo was created at {result.Value}, but this project moved to a different repo in the meantime, so it wasn't changed.");
            return;
        }

        var updated = latest with { RepoUrl = result.Value };
        _database.SaveProject(updated);
        _database.SetProjectFolder(project.Id, folder!);
        _ = _sync.SyncNowAsync();
        _confirmingNewRepo = false;
        await RefreshThenRecheckAsync(updated.Id);
    }

    private async Task RefreshThenRecheckAsync(string projectId)
    {
        var wasOpen = _openProject?.Id == projectId;

        // Already up to date, so the refresh doesn't see a changed repo and start its own check.
        if (wasOpen && FindProject(projectId) is { } latest) _openProject = latest;

        RefreshProjects();
        if (wasOpen) await RecheckIfStillOpenAsync(projectId);
    }

    private void AddGetProjectOptions(StackPanel body, Project project, string? linkedFolder)
    {
        var busy = _busyProjectId is not null;
        var select = ActionButton("Select folder");
        select.IsEnabled = !busy;
        var clone = ActionButton("Clone");
        clone.IsEnabled = !busy && project.RepoUrl.Length > 0;

        select.Click += async (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = $"Find the folder for {project.Name}" };
            if (dialog.ShowDialog() != true) return;

            StartBusy(project, "Checking that folder…");
            var folderProblem = await GitTools.FolderProblemAsync(dialog.FolderName, project.RepoUrl);
            EndBusy();

            if (folderProblem is null) await UseFolderAsync(project.Id, dialog.FolderName);
            else ShowPageProblem(project.Id, folderProblem);
        };

        clone.Click += async (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "Choose where to put the project" };
            if (dialog.ShowDialog() != true) return;

            using var cancel = new CancellationTokenSource();
            _cloneCancel = cancel;
            StartBusy(project, "Cloning…");
            var result = await GitTools.CloneAsync(project.RepoUrl, dialog.FolderName, cancel.Token);
            _cloneCancel = null;
            EndBusy();

            if (result.Worked) await UseFolderAsync(project.Id, result.Value);
            else ShowPageProblem(project.Id, result.Value);
        };

        var (heading, description) = (linkedFolder, _folderProblem, _folderExists) switch
        {
            (_, not null, _) => ("This project's folder doesn't match its repo", _folderProblem),
            (null, _, _) => ("This project isn't on this PC yet", "Get a copy from its repo, or point to one you already have."),
            _ => ("This project's folder is missing", $"It was at {linkedFolder}."),
        };
        body.Children.Add(Section(heading));
        body.Children.Add(new TextBlock { Text = description, Foreground = (Brush)FindResource("TextMuted"), FontSize = 13, Margin = new Thickness(16, -4, 0, 10), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(Card(
            Row("Clone from repo", project.RepoUrl.Length > 0 ? project.RepoUrl : "This project has no repo.", clone),
            Row("Select its folder", "It has to be connected to the same repo.", select)));
        AddBusyAndProblem(body, project);
    }

    // Saved even if they've left the page, because the clone or the choice was real.
    private async Task UseFolderAsync(string projectId, string folder)
    {
        if (FindProject(projectId) is null) return;
        _database.SetProjectFolder(projectId, folder);
        await RefreshThenRecheckAsync(projectId);
    }

    private void OpenProjectSettings(string projectId)
    {
        if (FindProject(projectId) is not { } project) return;

        MainContent.IsEnabled = false;
        MainContent.Effect = new BlurEffect { Radius = 8 };
        ProjectSettings.Open(_database, project);
    }

    private void ProjectSettings_Changed() => _ = _sync.SyncNowAsync();

    private async void ProjectSettings_Closed()
    {
        MainContent.Effect = null;
        MainContent.IsEnabled = _requiredUpdate is null;

        var openId = _openProject?.Id;
        if (openId is null)
        {
            RefreshProjects();
            return;
        }

        // The repo or folder may have changed. Work already running gets to finish first.
        if (_busyProjectId == openId)
        {
            RefreshProjects();
            return;
        }
        await RefreshThenRecheckAsync(openId);
    }

    private void ProjectSettings_Deleted()
    {
        MainContent.Effect = null;
        MainContent.IsEnabled = _requiredUpdate is null;
        BackToProjects();
        RefreshProjects();
        _ = _sync.SyncNowAsync();
    }

    private void BackToProjects()
    {
        _navigation++;
        _openProject = null;
        _projectCheck = null;
        _pageProblem = null;
        ProjectView.Visibility = Visibility.Collapsed;
        ProjectSelectView.Visibility = Visibility.Visible;
    }
}
