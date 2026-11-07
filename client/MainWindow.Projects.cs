using Microsoft.Data.Sqlite;
using System.Diagnostics;
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
    private const string CloneInProgressSetting = "clone_in_progress";
    private const double DragScrollEdge = 48;
    private const double DragScrollStep = 18;
    private static readonly TimeSpan ClickSuppressionAfterDrag = TimeSpan.FromMilliseconds(400);

    private Project? _openProject;
    private RepoCheck? _projectCheck;
    private string? _folderProblem;
    private static readonly TimeSpan AccessCheckInterval = TimeSpan.FromMinutes(5);
    private DateTimeOffset _lastAccessCheck;
    private bool _folderExists;
    private string? _renamedRepoHeldBy;
    private string? _folderEngine;
    private string? _folderPushesTo;
    private string? _checkedFolder;
    private bool _downloadIsInPlace;
    private (bool Unfinished, bool Held) _shownDownloadState;
    private bool _databaseWasLost;
    private bool _openPageNeedsCheck; // Kept until nothing's busy, as a long clone can be running.
    private FolderGitSettings? _finishSettingsToAllow;
    private (string? Folder, string? RepoUrl) _settingsOpenedWith;
    private bool _folderLacksFiles;
    private bool _folderMayNotMatch;
    private string? _scrolledToProblem;
    private string? _pageProblem;
    private bool _confirmingNewRepo;
    private string? _replacementRepoName;
    private int _navigation;

    // A long operation can finish after they've left its project, and what happened still has to reach them.
    private readonly Dictionary<string, string> _heldProblems = [];
    private string? _settingsProjectId;
    private FolderGitSettings? _settingsToAllow;
    private string? _recheckAfterBusy;
    private readonly Dictionary<string, int> _engineReads = [];

    // Only one long operation runs at a time. It keeps going if the person leaves the page.
    private string? _busyProjectId;
    private const string CreatingRepoText = "Creating a private repo on GitHub…";
    private string? _busyText;
    private string? _busyCardText; // Cards are narrow, so they get just the number.
    private TextBlock? _busyLabel;
    private TextBlock? _busyCardLabel;
    private CancellationTokenSource? _cloneCancel;

    // Refreshes are frequent during a big sync, and most covers haven't changed.
    private readonly Dictionary<string, (byte[] Bytes, ImageSource? Image)> _covers = [];
    private (string ProjectId, byte[] Bytes, ImageSource? Image)? _header;

    private Button? _pressedCard;
    private Button? _draggedCard;
    private Point _pressPoint;
    private DateTimeOffset _dragEnded;
    private bool _refreshAfterDrag;
    private bool _gearPressed; // Not a card being pressed, as that could start a drag, but a rebuild would still remove it.

    private void RefreshProjects()
    {
        // A rebuild while a card is pressed would remove it, and the click would be lost.
        if (_draggedCard is not null || _pressedCard is not null || _gearPressed)
        {
            _refreshAfterDrag = true;
            return;
        }

        var projects = _database.ListProjects();
        foreach (var gone in _covers.Keys.Except(projects.Select(project => project.Id)).ToList()) _covers.Remove(gone);

        // Rebuilding the cards would otherwise send someone using the keyboard back to the top.
        var focusedCard = ProjectGrid.Children.OfType<Button>().FirstOrDefault(card => card.IsKeyboardFocusWithin)?.Tag as string;

        ProjectGrid.Children.Clear();
        foreach (var project in projects) ProjectGrid.Children.Add(ProjectCard(project));
        if (focusedCard is not null) ProjectGrid.Children.OfType<Button>().FirstOrDefault(card => (string)card.Tag == focusedCard)?.Focus();
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
        else if (!_confirmingNewRepo)
        {
            RenderProjectKeepingFocus(current); // Its header image syncs as a separate record. Not while someone's typing.
        }
    }

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

    // Library

    private Button ProjectCard(Project project)
    {
        var thumbnail = CoverFor(project.Id);
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
        gear.PreviewMouseLeftButtonDown += (_, _) =>
        {
            _pressedCard = null;
            _gearPressed = true;
        };
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
        content.Children.Add(new TextBlock { Text = project.Summary, Foreground = (Brush)FindResource("TextMuted"), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 2, 2, 0), ToolTip = project.Summary });
        content.Children.Add(notHere);

        // Long operations carry on after leaving the page, so the card says how it's going.
        var cardStatus = _busyProjectId == project.Id ? _busyCardText ?? _busyText : _heldProblems.ContainsKey(project.Id) ? "Open it to see what happened" : null;
        if (cardStatus is not null)
        {
            var statusLabel = new TextBlock { Text = cardStatus, Foreground = (Brush)FindResource("Accent"), FontSize = 12.5, Margin = new Thickness(2, 2, 2, 0), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = cardStatus };
            content.Children.Add(statusLabel);
            if (_busyProjectId == project.Id) _busyCardLabel = statusLabel;
        }

        var card = new Button { Style = (Style)FindResource("ProjectCard"), Content = content, Tag = project.Id, AllowDrop = true };
        AutomationProperties.SetName(card, project.Name);

        // The gear has to show for the keyboard too, or Tab can never reach it.
        void ShowActive()
        {
            var active = card.IsMouseOver || card.IsKeyboardFocusWithin;
            cover.BorderBrush = active ? (Brush)FindResource("Accent") : Brushes.Transparent;
            gear.Visibility = active ? Visibility.Visible : Visibility.Hidden;
        }
        card.MouseEnter += (_, _) => ShowActive();
        card.MouseLeave += (_, _) => ShowActive();
        card.IsKeyboardFocusWithinChanged += (_, _) => ShowActive();
        card.Click += async (_, _) =>
        {
            if (DateTimeOffset.UtcNow - _dragEnded < ClickSuppressionAfterDrag) return;
            await OpenProjectAsync(project);
        };

        // Reordering has to work without a mouse too.
        card.PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            var step = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -CardsPerRow(card), Key.Down => CardsPerRow(card), _ => 0 };
            if (step == 0) return;
            e.Handled = true;
            MoveCardBy(project.Id, step);
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

    private int CardsPerRow(FrameworkElement card) =>
        Math.Max(1, (int)(ProjectGrid.ActualWidth / Math.Max(1, card.ActualWidth + card.Margin.Left + card.Margin.Right)));

    private void MoveCardBy(string projectId, int step)
    {
        var order = CurrentOrder();
        var from = order.IndexOf(projectId);
        var to = Math.Clamp(from + step, 0, order.Count - 1); // A shorter last row still has a place to go.
        if (from < 0 || to == from) return;

        order.RemoveAt(from);
        order.Insert(to, projectId);
        _database.SaveProjectOrder(order);
        _ = _sync.SyncNowAsync();
        RefreshProjects();
        Dispatcher.InvokeAsync(
            () => ProjectGrid.Children.OfType<Button>().FirstOrDefault(card => (string)card.Tag == projectId)?.Focus(),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private ImageSource? CoverFor(string projectId)
    {
        var bytes = _database.GetProjectImage(projectId, ProjectImageKind.Thumbnail);
        if (bytes is null)
        {
            _covers.Remove(projectId);
            return null;
        }
        if (_covers.TryGetValue(projectId, out var cached) && cached.Bytes.AsSpan().SequenceEqual(bytes)) return cached.Image;

        var image = ProjectImages.ToImage(bytes, CoverWidth * 2);
        _covers[projectId] = (bytes, image);
        return image;
    }

    // A disconnected network drive can take a long time to answer, so this stays off the UI thread.
    private async Task ShowIfNotOnThisPcAsync(string projectId, TextBlock label)
    {
        var folder = _database.GetProjectFolder(projectId);
        var here = folder is not null && await GitTools.FolderExistsAsync(folder);
        label.Text = folder is null ? "Not on this PC" : "Folder not found"; // It can just be on a drive that's unplugged.
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
        var result = DragDrop.DoDragDrop(card, new DataObject("AgentOrange.ProjectCard", card.Tag), DragDropEffects.Move);

        card.Opacity = 1;
        _draggedCard = null;
        _dragEnded = DateTimeOffset.UtcNow;
        Keyboard.ClearFocus(); // Or the card stays highlighted after the mouse has left it.

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
        try
        {
            await CheckAndShowProjectAsync(project);
        }
        catch (SqliteException)
        {
            _databaseWasLost = true; // So the check runs again, as nothing else will finish it.
            ProjectPage.Children.Clear();
            var note = CenteredNote("The app's database can't be reached right now. This page comes back once it can be.");
            note.Margin = new Thickness(40, 40, 40, 0);
            ProjectPage.Children.Add(note);
            throw;
        }
    }

    private async Task CheckAndShowProjectAsync(Project project)
    {
        // Its long job is working in its folder, and checking that folder can change its remote.
        // So it's checked once the job is done, and the page meanwhile shows what's running.
        if (_busyProjectId == project.Id)
        {
            _recheckAfterBusy = project.Id;
            if (_openProject?.Id == project.Id && GitTools.SameRepo(project.RepoUrl, _openProject.RepoUrl))
            {
                _openProject = project;
                RenderProject(project);
                return;
            }
        }

        // This is the look that was waiting.
        else if (_recheckAfterBusy == project.Id) _recheckAfterBusy = null;

        var navigation = ++_navigation;
        _lastAccessCheck = DateTimeOffset.UtcNow;
        _openProject = project;
        _projectCheck = null;
        _folderProblem = null;
        _folderExists = false;
        _renamedRepoHeldBy = null;
        _folderEngine = null;
        _folderPushesTo = null;
        _finishSettingsToAllow = null;
        _folderLacksFiles = false;
        _folderMayNotMatch = false;
        _pageProblem = _heldProblems.Remove(project.Id, out var held) ? held : null;
        _confirmingNewRepo = false;
        ProjectSelectView.Visibility = Visibility.Collapsed;
        ProjectView.Visibility = Visibility.Visible;
        RenderProject(project);
        if (_busyProjectId == project.Id) return;

        var folder = _database.GetProjectFolder(project.Id);
        var runnable = folder is null ? FolderGitSettings.None : await GitTools.FolderRunnableSettingsAsync(folder);
        var useFolderSettings = runnable is not null && (folder is null || GitTools.AreAllowed(folder, runnable));
        var check = await GitTools.CheckRepoAccessAsync(project.RepoUrl, folder, useFolderSettings);

        var folderExists = folder is not null && await GitTools.FolderExistsAsync(folder);
        var folderShown = folderExists && check.ShowsFolder;
        var usable = CanWorkIn(project, check);
        var folderProblem = folderShown ? await GitTools.FolderProblemAsync(folder!, project.RepoUrl) : null;

        // It may have been connected while offline, when nothing could say the repo had files.
        var askAboutRepo = usable && check.GitHubSeesRepo;
        var lacksFiles = askAboutRepo && folderExists && folderProblem is null
            && await GitTools.FolderHasNoCommitsAsync(folder!) == true && await GitTools.RepoHasContentAsync(project.RepoUrl) == true;

        // A folder with its own commits may not be this project at all, however it was connected.
        // This keeps saying so until the two share a commit, which is then remembered for that
        // folder and repo, as this asks GitHub each time until it does.
        var repoKey = GitTools.RepoKey(project.RepoUrl);
        var history = askAboutRepo && folderExists && folderProblem is null && !lacksFiles && !_database.FolderSharesRepo(folder!, repoKey)
            ? await GitTools.CompareFolderHistoryAsync(folder!, project.RepoUrl)
            : FolderHistory.CantTell;
        if (history == FolderHistory.SharesCommits) _database.RememberFolderSharesRepo(folder!, repoKey);
        var mayNotMatch = history == FolderHistory.SharesNothing;
        var folderEngine = folderShown && usable && folderProblem is null ? await EngineVersions.DetectAsync(folder!) : null;
        var pushesTo = folderShown && usable && folderProblem is null ? await GitTools.PushesElsewhereAsync(folder!, project.RepoUrl) : null;
        if (folderExists && check.IsDenied) _database.ForgetFolderSharesRepo(folder!);

        // Asked before the latest copy of the project is read below, as this can take a while.
        var renamed = check.GitHubSeesRepo
            ? await GitTools.RenamedRepoUrlAsync(project.RepoUrl)
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
            KeepProblemThroughReopen(project.Id);
            await OpenProjectAsync(latest);
            return;
        }

        // Renamed or moved on GitHub, which follows that for a while but won't forever. This is
        // the same repo, so the project and its folder just move to the new address.
        var renamedHeldBy = renamed is null ? null : GitTools.ProjectUsingRepo(renamed);
        var holdingRenamed = false;
        if (renamed is not null && renamedHeldBy is null)
        {
            var renameClaim = await _database.ClaimRepoAsync(renamed, latest);
            holdingRenamed = renameClaim is null;
            if (renameClaim is not null && !LocalDatabase.MeansNoTurn(renameClaim)) renamedHeldBy = renameClaim.Name;
        }
        if (holdingRenamed && (navigation != _navigation || FindProject(project.Id) is not { } stillOnIt || !GitTools.SameRepo(stillOnIt.RepoUrl, project.RepoUrl)))
        {
            _database.ReleaseRepo(renamed!);
            if (navigation != _navigation) return;
            if (FindProject(project.Id) is { } changed) await OpenProjectAsync(changed);
            else BackToProjects();
            return;
        }
        if (navigation != _navigation) return;
        if (holdingRenamed)
        {
            var wasVerified = _database.GetSetting(VerifiedSetting(project.Id)) == GitTools.RepoKey(latest.RepoUrl);
            var wasCreatedHere = _database.GetSetting(CreatedHereSetting(project.Id)) == GitTools.RepoKey(latest.RepoUrl);
            try
            {
                _database.UpdateProject(project.Id, current => GitTools.SameRepo(current.RepoUrl, project.RepoUrl) ? current with { RepoUrl = renamed! } : current);
            }
            finally
            {
                _database.ReleaseRepo(renamed!);
            }
            if (check.IsDenied)
            {
                _database.SetSetting(VerifiedSetting(project.Id), "");
                _database.SetSetting(CreatedHereSetting(project.Id), "");
            }
            else
            {
                if (check.Access == RepoAccess.Allowed || wasVerified) _database.SetSetting(VerifiedSetting(project.Id), GitTools.RepoKey(renamed!));
                if (check.Access == RepoAccess.Allowed) _database.SetSetting(CreatedHereSetting(project.Id), "");
                else if (wasCreatedHere) _database.SetSetting(CreatedHereSetting(project.Id), GitTools.RepoKey(renamed!));
            }

            // Checking it against the new name moves it there if it's on any older name, and leaves one on any other repo as it is.
            if (folderExists) await GitTools.FolderProblemAsync(folder!, renamed!);
            _ = _sync.SyncNowAsync();
            KeepProblemThroughReopen(project.Id);
            if (navigation == _navigation && FindProject(project.Id) is { } moved) await OpenProjectAsync(moved);
            return;
        }

        if (check.Access == RepoAccess.Allowed)
        {
            _database.SetSetting(VerifiedSetting(project.Id), GitTools.RepoKey(project.RepoUrl));
            _database.SetSetting(CreatedHereSetting(project.Id), "");
        }
        else if (check.IsDenied)
        {
            _database.SetSetting(VerifiedSetting(project.Id), "");
            _database.SetSetting(CreatedHereSetting(project.Id), "");
        }

        _projectCheck = check;
        _checkedFolder = folder;
        _settingsToAllow = useFolderSettings || runnable is null or { IsEmpty: true } ? null : runnable;
        _folderExists = folderExists;
        _folderProblem = folderProblem;
        _folderLacksFiles = lacksFiles;
        _folderMayNotMatch = mayNotMatch;
        _folderEngine = folderEngine;
        _renamedRepoHeldBy = renamedHeldBy;
        _folderPushesTo = pushesTo;
        RenderProject(latest);
        if (folderShown && usable) await ReadEngineVersionAsync(latest.Id);
    }

    // A project on this PC may have moved to a newer engine since it was last opened. This never
    // saves the project itself, because this PC's copy of it may be out of date. With no check of
    // its own, it goes by whether this PC has been let into the project, while an open page also
    // goes by what its check just found.
    private async Task ReadEngineVersionsAsync()
    {
        foreach (var project in _database.ListProjects()) await ReadEngineVersionAsync(project.Id);
    }

    // The version comes from the project's own files, and syncs so that PCs without a copy
    // can show it too. A project whose files don't say keeps what was last found, and a
    // folder that's really some other repo has nothing to say about this project.
    private async Task ReadEngineVersionAsync(string projectId)
    {
        if (FindProject(projectId) is not { } project || _database.GetProjectFolder(projectId) is not { } folder) return;
        var repoKey = GitTools.RepoKey(project.RepoUrl);
        if (_database.GetSetting(VerifiedSetting(projectId)) != repoKey && _database.GetSetting(CreatedHereSetting(projectId)) != repoKey) return;

        // A slow read of the old folder mustn't land on top of a newer one's answer.
        var read = _engineReads[projectId] = _engineReads.GetValueOrDefault(projectId) + 1;

        if (await GitTools.FolderProblemAsync(folder, project.RepoUrl) is { } problem)
        {
            // Seen to be on another repo, so what it found earlier can't speak for this project now.
            // Anything else, such as an unplugged drive, says nothing about that.
            if (GitTools.SeenOnAnotherRepo(problem) && _engineReads[projectId] == read && _database.GetProjectFolder(projectId) == folder && _database.ForgetEngineVersionHere(projectId)) RefreshProjects();
            return;
        }

        var version = await EngineVersions.FindAsync(project.Engine, folder);
        if (version is null || _engineReads[projectId] != read || _database.GetProjectFolder(projectId) != folder) return;
        if (FindProject(projectId) is not { } latest || !GitTools.SameRepo(latest.RepoUrl, project.RepoUrl)) return;
        if (latest.Engine != project.Engine) return;

        // Always noted, even if it matches what's shown, as another PC can move what's shown on.
        var changed = latest.EngineVersion != version;
        _database.SetEngineVersion(projectId, project.Engine, version);
        if (!changed) return;
        _ = _sync.SyncNowAsync();
        RefreshProjects();
    }

    private Project? FindProject(string id) => _database.ListProjects().FirstOrDefault(project => project.Id == id);

    // Allowed, or no clear answer on a PC that's been let in before or made the project here. Anything
    // else is a page that's blocked, whatever it shows.
    private bool CanWorkIn(Project project, RepoCheck check)
    {
        if (check.Access == RepoAccess.Allowed) return true;
        var repoKey = GitTools.RepoKey(project.RepoUrl);
        return check.IsUnanswered && (_database.GetSetting(VerifiedSetting(project.Id)) == repoKey || _database.GetSetting(CreatedHereSetting(project.Id)) == repoKey);
    }

    private static string VerifiedSetting(string projectId) => $"project_verified:{projectId}";

    // Created on this PC with no connection, so nothing has said it can't be used yet.
    private static string CreatedHereSetting(string projectId) => $"project_created_here:{projectId}";

    // Quietly, and only changes the page if the answer has changed. A check before each agent
    // action belongs here too, once agents work inside projects.
    private async Task RecheckAccessOnReturnAsync()
    {
        if (_openProject is not { } project || _projectCheck is not { } shown || _busyProjectId is not null) return;
        if (DateTimeOffset.UtcNow - _lastAccessCheck < AccessCheckInterval) return;
        _lastAccessCheck = DateTimeOffset.UtcNow;

        var folder = _database.GetProjectFolder(project.Id);
        var runnable = folder is null ? FolderGitSettings.None : await GitTools.FolderRunnableSettingsAsync(folder);
        var useFolderSettings = runnable is not null && (folder is null || GitTools.AreAllowed(folder, runnable));
        var latest = await GitTools.CheckRepoAccessAsync(project.RepoUrl, folder, useFolderSettings);

        // The folder may have changed too, as anything can happen to it while no one's looking.
        var folderExists = folder is not null && await GitTools.FolderExistsAsync(folder);
        var folderProblem = folderExists && latest.ShowsFolder
            ? await GitTools.FolderProblemAsync(folder!, project.RepoUrl)
            : null;
        var changed = latest != shown || folder != _checkedFolder || folderExists != _folderExists || folderProblem != _folderProblem;
        if (changed && _openProject?.Id == project.Id && _busyProjectId is null) await RecheckIfStillOpenAsync(project.Id);
    }

    private void KeepProblemThroughReopen(string projectId)
    {
        if (_pageProblem is { } shown) _heldProblems[projectId] = shown;
    }

    private (bool Unfinished, bool Held) DownloadStateOf(string? folder) =>
        folder is null ? default : (_database.DownloadIsUnfinished(folder), GitTools.DownloadIsHeld(folder));

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

    private void StartBusy(Project project, string text)
    {
        _busyProjectId = project.Id;
        _busyText = text;
        _busyCardText = null;
        _pageProblem = null;
        if (UpdateOverlay.IsVisible && !_downloading) ShowUpdateMessage();
        if (_openProject?.Id == project.Id) RenderProject(_openProject);
        else RefreshProjects();
    }

    private void EndBusy()
    {
        _busyProjectId = null;
        _busyText = null;
        _busyCardText = null;
        _downloadIsInPlace = false;
        if (UpdateOverlay.IsVisible && !_downloading) ShowUpdateMessage();
        if (_recheckAfterBusy is { } waiting) _ = Dispatcher.InvokeAsync(() => LookAgainAfterBusy(waiting), System.Windows.Threading.DispatcherPriority.Background);
        if (_openProject is null)
        {
            RefreshProjects();
            return;
        }
        RenderProject(_openProject);
    }

    // Something about this project changed while its job ran. If its page is still open, that looks
    // again, and otherwise its folder is read the way any background read is. Left a moment, as what
    // ended the job often looks again itself, or starts the next job, whose end is then soon enough.
    private void LookAgainAfterBusy(string projectId)
    {
        if (_recheckAfterBusy != projectId || _busyProjectId is not null) return;
        _recheckAfterBusy = null;
        _ = TellIfDatabaseFailsAsync(_openProject?.Id == projectId ? RecheckIfStillOpenAsync(projectId) : ReadEngineVersionAsync(projectId));
    }

    // Shows the result if the person is still looking at this project.
    private void ShowPageProblem(string projectId, string problem)
    {
        // Deleted elsewhere in the meantime, so no page will ever show this.
        if (FindProject(projectId) is null)
        {
            Dialogs.Notify(problem);
            return;
        }

        if (_openProject?.Id != projectId)
        {
            _heldProblems[projectId] = problem;
            RefreshProjects();
            return;
        }
        _pageProblem = problem;
        RenderProject(_openProject);
    }

    // The app carries on through a database error, as the database can be on a drive that's
    // dropped. Whatever this page was busy with has stopped, and the page has to say so.
    private async Task EndBusyIfDatabaseFailsAsync(string projectId, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (SqliteException)
        {
            if (_busyProjectId == projectId) EndBusy();
            throw;
        }
    }

    private async Task CreateReplacementRepoAsync(Project project, RepoCheck shownCheck)
    {
        // Busy from the first click, so a second one can't create a second repo.
        if (_busyProjectId is not null) return;
        StartBusy(project, "Checking the repo again…");

        // The page may have been open a while. Access may have come back, or a different
        // account be active, and what they agreed to named the account it would go under.
        var latestCheck = await GitTools.CheckRepoAccessAsync(project.RepoUrl, _database.GetProjectFolder(project.Id));
        if (latestCheck != shownCheck)
        {
            EndBusy();
            _confirmingNewRepo = false;
            await RecheckIfStillOpenAsync(project.Id);
            return;
        }

        // The linked folder only qualifies if it can really move to the new repo.
        var folder = _database.GetProjectFolder(project.Id);
        var folderIsUsable = folder is not null && await GitTools.FolderCanReplaceRepoAsync(folder, project.RepoUrl);
        EndBusy();

        if (!folderIsUsable)
        {
            folder = Dialogs.PickFolder($"Choose the folder for {project.Name}");
            if (folder is null || _busyProjectId is not null) return;

            // The dialog can stay open a long time, and what they agreed to named an account.
            StartBusy(project, "Checking the repo again…");
            if (await GitTools.CheckRepoAccessAsync(project.RepoUrl, _database.GetProjectFolder(project.Id)) != shownCheck)
            {
                EndBusy();
                _confirmingNewRepo = false;
                await RecheckIfStillOpenAsync(project.Id);
                return;
            }
        }

        StartBusy(project, CreatingRepoText);

        // The folder dialog can stay open a long time, and sync keeps running behind it.
        if (FindProject(project.Id) is not { } stillThere || !GitTools.SameRepo(stillThere.RepoUrl, project.RepoUrl))
        {
            EndBusy();
            _confirmingNewRepo = false;
            await RecheckIfStillOpenAsync(project.Id);
            return;
        }

        // Another project may have a copy of the repo that's being replaced, on a PC where the two synced together.
        if (await _database.ClaimFolderAsync(folder!, project) is { } owner)
        {
            EndBusy();
            ShowPageProblem(project.Id, LocalDatabase.RefusalFor(owner, $"{owner.Name} already uses that folder, or one that overlaps it. Project folders can't overlap."));
            return;
        }

        // Held until it's linked, so settings can't give it to another project meanwhile.
        try
        {
            await ReplaceRepoHoldingFolderAsync(project, folder!);
        }
        finally
        {
            _database.ReleaseFolder(folder!);
        }
    }

    private async Task ReplaceRepoHoldingFolderAsync(Project project, string folder)
    {
        var linkedBefore = _database.GetProjectFolder(project.Id);
        var result = await GitTools.CreatePrivateRepoAsync(project.Name, folder, project.RepoUrl, _replacementRepoName ?? "");

        // It exists on GitHub now, and this is the only place that says where.
        void SayAddressNotSaved(SqliteException error)
        {
            AppLog.Write($"Couldn't save a new repo's address: {error.Message}");
            Dialogs.Notify($"The repo was created at {result.Value}, but the app's database can't be reached, so {project.Name} wasn't changed. Once it's back, enter that address in its project settings.");
        }

        Project? latest;
        try
        {
            EndBusy();

            if (!result.Worked)
            {
                ShowPageProblem(project.Id, result.Value);
                return;
            }
            latest = FindProject(project.Id);
        }
        catch (SqliteException error) when (result.Worked)
        {
            SayAddressNotSaved(error);
            return;
        }

        // Saved even if they've left the page, because the repo now exists. A URL changed
        // in the meantime was chosen more recently, so it stays.
        if (latest is null || GitTools.RepoUrlProblem(result.Value) is not null)
        {
            ShowPageProblem(project.Id, $"The repo was created at {result.Value}, but the project couldn't be updated.");
            return;
        }
        if (!GitTools.SameRepo(latest.RepoUrl, project.RepoUrl))
        {
            _confirmingNewRepo = false;
            await RecheckThenShowAsync(project.Id, $"The repo was created at {result.Value}, but this project moved to a different repo in the meantime, so it wasn't changed.");
            return;
        }

        try
        {
            // Another window can have given this new repo to another project while it was being made.
            if (await _database.ClaimRepoAsync(result.Value, latest) is { } repoOwner)
            {
                ShowPageProblem(project.Id, LocalDatabase.RefusalFor(repoOwner, $"The repo was created at {result.Value}, but {repoOwner.Name} uses it now, so this project wasn't changed.") + (LocalDatabase.MeansNoTurn(repoOwner) ? $" The repo is at {result.Value}, so enter that in project settings." : ""));
                return;
            }

            if (FindProject(project.Id) is not { } stillOnOld || !GitTools.SameRepo(stillOnOld.RepoUrl, project.RepoUrl))
            {
                _confirmingNewRepo = false;
                await RecheckThenShowAsync(project.Id, $"The repo was created at {result.Value}, but this project moved to a different repo in the meantime, so it wasn't changed.");
                return;
            }

            _database.UpdateProject(project.Id, current => GitTools.SameRepo(current.RepoUrl, project.RepoUrl) ? current with { RepoUrl = result.Value } : current);
            _database.SetSetting(VerifiedSetting(project.Id), GitTools.RepoKey(result.Value)); // This PC just made it.

            // A folder chosen in settings while this ran is more recent, so it stays.
            if (_database.GetProjectFolder(project.Id) == linkedBefore) _database.SetProjectFolder(project.Id, folder);
        }
        catch (SqliteException error)
        {
            SayAddressNotSaved(error);
            return;
        }
        finally
        {
            _database.ReleaseRepo(result.Value);
        }
        _ = _sync.SyncNowAsync();
        _confirmingNewRepo = false;
        await RefreshThenRecheckAsync(project.Id);
    }

    // For a result that the page has to be up to date to make sense of.
    private async Task RecheckThenShowAsync(string projectId, string problem)
    {
        _heldProblems[projectId] = problem;
        if (_openProject?.Id != projectId) RefreshProjects(); // Its card says there's something to see.
        await RecheckIfStillOpenAsync(projectId);
    }

    private async Task RefreshThenRecheckAsync(string projectId)
    {
        var wasOpen = _openProject?.Id == projectId;

        // Already up to date, so the refresh doesn't see a changed repo and start its own check.
        if (wasOpen && FindProject(projectId) is { } latest) _openProject = latest;

        RefreshProjects();
        if (wasOpen) await RecheckIfStillOpenAsync(projectId);
    }

    // A new project whose repo already has files, and an empty folder for them. Running as an
    // ordinary clone, on its page, means it shows progress and can be cancelled.
    private async Task CloneIntoChosenFolderAsync(string projectId, string folder)
    {
        // The wizard's hold becomes this project's before its page opens, so nothing else can take the
        // folder during the check, and the project itself isn't turned away from it.
        try
        {
            if (FindProject(projectId) is not { } project) return;
            _database.TransferFolderHold(folder, project);
            await OpenProjectAsync(project);
            if (_busyProjectId is not null)
            {
                ShowPageProblem(projectId, $"Something else is running. When that finishes, choose Clone and pick {folder} again.");
                return;
            }
            if (FindProject(projectId) is not { } latest) return;

            // What the page found when it opened is what the clone goes by, as the page would only
            // have offered one if that let it.
            if (_openProject?.Id != projectId)
            {
                // Its card says there's something to see, as nothing else would rebuild it.
                _heldProblems[projectId] = $"The clone into {folder} didn't start, because this project's page was left before its repo was checked. Open it and choose Clone to try again.";
                RefreshProjects();
                return;
            }
            if (_projectCheck is not { } check)
            {
                ShowPageProblem(projectId, $"The clone into {folder} didn't start, because this project's repo was still being checked. Choose Clone and pick {folder} again.");
                return;
            }
            if (!CanWorkIn(latest, check))
            {
                ShowPageProblem(projectId, $"The clone into {folder} didn't start, because this repo can't be used from this PC right now.");
                return;
            }

            var exact = Path.TrimEndingDirectorySeparator(folder);
            await CloneProjectAsync(latest, Path.GetDirectoryName(exact)!, exact);
        }
        finally
        {
            _database.ReleaseFolder(folder);
        }
    }

    // Holds where it's going until the end, because a clone can take hours and another project
    // choosing that folder meanwhile would only find out when it finished.
    private async Task CloneProjectAsync(Project project, string parentFolder, string? exactFolder = null)
    {
        if (_busyProjectId is not null) return;
        var destination = exactFolder ?? GitTools.CloneDestination(project.RepoUrl, parentFolder);
        var isHeld = false;
        try
        {
            StartBusy(project, "Checking that folder…");
            var owner = destination is null ? null : await _database.ClaimFolderAsync(destination, project);
            isHeld = destination is not null && owner is null;
            if (owner is not null)
            {
                EndBusy();
                ShowPageProblem(project.Id, LocalDatabase.RefusalFor(owner, $"{owner.Name} already uses a folder that overlaps where this would clone to. Project folders can't overlap."));
                return;
            }

            await RunCloneAsync(project, parentFolder, exactFolder);
        }
        catch (Exception)
        {
            // Something unexpected, and the page can't be left waiting forever.
            if (_busyProjectId == project.Id)
            {
                _cloneCancel = null;
                EndBusy();
            }
            throw;
        }
        finally
        {
            if (isHeld) _database.ReleaseFolder(destination!);
        }
    }

    private async Task RunCloneAsync(Project project, string parentFolder, string? exactFolder)
    {
        var folderBefore = _database.GetProjectFolder(project.Id);

        using var cancel = new CancellationTokenSource();
        _cloneCancel = cancel;
        StartBusy(project, "Cloning…");
        string? cloneRecord = null;
        string? workingFolder = null;
        var result = await GitTools.CloneAsync(project.RepoUrl, parentFolder, cancel.Token, working =>
        {
            workingFolder = working;
            if (working is null)
            {
                // Finished, so the cleanup at the next start has to leave it alone from now on.
                var finished = cloneRecord;
                ChangeCloneRecords(records => records.Where(record => record != finished));
                cloneRecord = null;
                return;
            }
            cloneRecord = CloneRecord(working);
            ChangeCloneRecords(records => records.Append(cloneRecord));
        }, exactFolder, stage => ShowBusyStage("Cloning…", stage));

        try
        {
            await AfterCloneAsync(project, result, cancel, folderBefore, async () =>
            {
                // One that couldn't be fully removed stays on the list, to be tried again next start.
                if (cloneRecord is not null && (workingFolder is null || !await GitTools.FolderExistsAsync(workingFolder)))
                {
                    ChangeCloneRecords(records => records.Where(record => record != cloneRecord));
                }
            });
        }
        catch (SqliteException error) when (result.Worked)
        {
            // Hours of download are sitting there, and nothing's going to say so otherwise.
            AppLog.Write($"Couldn't link a finished clone: {error.Message}");
            _cloneCancel = null;
            _databaseWasLost = true;
            try
            {
                if (_busyProjectId == project.Id) EndBusy();
            }
            catch (SqliteException)
            {
                // Drawing the page needs the database, and that happens again once it's back.
            }
            Dialogs.Notify($"{project.Name} finished cloning to {result.Value}, but the app's database can't be reached, so it may not be linked. Once it's back, select that folder. If it uses large files or submodules, get them with git lfs pull and git submodule update --init --recursive.");
        }
    }

    private async Task AfterCloneAsync(Project project, GitResult result, CancellationTokenSource cancel, string? folderBefore, Func<Task> forgetCloneRecord)
    {
        await forgetCloneRecord();

        if (!result.Worked)
        {
            _cloneCancel = null;
            EndBusy();
            ShowPageProblem(project.Id, result.Value);
        }
        else if (FindProject(project.Id) is null)
        {
            _cloneCancel = null;
            EndBusy();
            Dialogs.Notify($"{project.Name} was deleted while it was cloning, so the clone wasn't linked to anything. It's still at {result.Value}.");
        }
        else if (FindProject(project.Id) is { } latest
            && (_database.GetProjectFolder(project.Id) != folderBefore
                || (!GitTools.SameRepo(latest.RepoUrl, project.RepoUrl) && !GitTools.SameRepo(await GitTools.RenamedRepoUrlAsync(project.RepoUrl) ?? "", latest.RepoUrl))))
        {
            // Changed through sync or in another window while this ran, and that's more recent.
            _cloneCancel = null;
            EndBusy();
            await RecheckThenShowAsync(project.Id, $"Cloned to {result.Value}, but this project changed while that ran, so it wasn't linked. The clone is still there.");
        }
        else
        {
            // Linked first, so an app closing during what comes next leaves a project that knows
            // about this folder, and says what's still missing from it.
            _downloadIsInPlace = true;
            string? missing = null;
            using (var hold = GitTools.TryTakeDownload(result.Value))
            {
                await UseFolderAsync(FindProject(project.Id) ?? project, result.Value, folderBefore);
                var isLinked = _database.GetProjectFolder(project.Id) == result.Value;

                // Files that never got put in place come first, and it stays unfinished until they're sorted out.
                if (isLinked && hold is not null && result.Note is null)
                {
                    var look = await GitTools.LookBeforeFinishingAsync(result.Value);
                    if (look?.Refused is { } refused) missing = refused;
                    else if (look is not null && GitTools.AreAllowed(GitTools.FinishApprovalKey(result.Value), look.Settings))
                    {
                        missing = await GitTools.FinishDownloadAsync(result.Value, cancel.Token, stage => ShowBusyStage("Cloning…", stage));
                    }
                }
            }
            _cloneCancel = null;
            EndBusy();

            var notes = new[] { result.Note, missing }.OfType<string>().ToList();
            if (notes.Count > 0) ShowPageProblem(project.Id, string.Join(' ', notes));
        }
    }

    private async Task FinishDownloadAsync(Project project, string folder)
    {
        if (_busyProjectId is not null) return;

        // These run in the folder, and it can have picked up settings since it was last looked at.
        var runnable = await GitTools.FolderRunnableSettingsAsync(folder);
        if (runnable is null)
        {
            ShowPageProblem(project.Id, "Couldn't read this folder's own Git settings, so nothing was run in it. Check that Git works in that folder, then try again.");
            return;
        }
        if (!GitTools.AreAllowed(folder, runnable))
        {
            await RecheckIfStillOpenAsync(project.Id); // That draws the row for allowing them.
            return;
        }

        var finishing = await GitTools.LookBeforeFinishingAsync(folder);
        if (finishing is null)
        {
            ShowPageProblem(project.Id, "Couldn't read this folder's own Git settings, so nothing was run in it. Check that Git works in that folder, then try again.");
            return;
        }
        if (finishing.Refused is { } refused)
        {
            ShowPageProblem(project.Id, refused);
            return;
        }
        if (!GitTools.AreAllowed(GitTools.FinishApprovalKey(folder), finishing.Settings))
        {
            _finishSettingsToAllow = finishing.Settings;
            if (_openProject?.Id == project.Id) RenderProject(_openProject);
            return;
        }
        if (_busyProjectId is not null) return;

        // Two of these at once would run into each other's locks.
        if (GitTools.TryTakeDownload(folder) is not { } hold)
        {
            if (_openProject?.Id == project.Id) RenderProject(_openProject);
            return;
        }

        using var cancel = new CancellationTokenSource();
        _cloneCancel = cancel;
        string? missing;
        try
        {
            StartBusy(project, "Finishing the download…");
            _downloadIsInPlace = true;
            missing = await GitTools.FinishDownloadAsync(folder, cancel.Token, stage => ShowBusyStage("Finishing the download…", stage));
        }
        finally
        {
            hold.Dispose();
            _cloneCancel = null;
            EndBusy();
        }
        if (missing is not null) ShowPageProblem(project.Id, missing);
    }

    // Just the words, because drawing the page again would keep taking focus off Cancel.
    private void ShowBusyStage(string doing, string stage)
    {
        var last = stage[(stage.LastIndexOf(' ') + 1)..];
        _busyText = $"{doing} {stage}";
        _busyCardText = last.EndsWith('%') ? $"{doing} {last}" : doing;
        if (_busyLabel is not null) _busyLabel.Text = _busyText;
        if (_busyCardLabel is not null)
        {
            _busyCardLabel.Text = _busyCardText;
            _busyCardLabel.ToolTip = _busyText;
        }
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

    private Button SelectFolderButton(Project project)
    {
        var select = ActionButton("Select folder");
        select.IsEnabled = _busyProjectId is null;
        select.Click += async (_, _) =>
        {
            if (Dialogs.PickFolder($"Find the folder for {project.Name}") is not { } chosen || _busyProjectId is not null) return;

            var folderBefore = _database.GetProjectFolder(project.Id);
            string? folderProblem;
            var allowedJustNow = false;
            try
            {
                StartBusy(project, "Checking that folder…");
                // Who has it comes first, as asking about it and checking it can both act on it.
                if (await _database.ProjectUsingFolderAsync(chosen, project.Id) is { } other)
                {
                    folderProblem = $"{other.Name} already uses that folder, or one that overlaps it. Project folders can't overlap.";
                }
                else
                {
                    try
                    {
                        allowedJustNow = await GitTools.AskAboutUnreadableSshSettingsAsync(chosen);
                        folderProblem = await GitTools.FolderProblemAsync(chosen, project.RepoUrl);
                    }
                    catch
                    {
                        if (allowedJustNow) GitTools.ForgetAllowedFolderSettings(chosen);
                        throw;
                    }
                }
                if (allowedJustNow && folderProblem is not null) GitTools.ForgetAllowedFolderSettings(chosen);
            }
            finally
            {
                EndBusy();
            }

            if (folderProblem is not null) ShowPageProblem(project.Id, folderProblem);
            else if (!await UseFolderAsync(project, chosen, folderBefore) && allowedJustNow) GitTools.ForgetAllowedFolderSettings(chosen);
        };
        return select;
    }

    // Only on this PC, and for the settings as they were when they were seen.
    private async Task AllowFolderSettingsAsync(Project project, string folder, string fingerprint)
    {
        _database.AllowFolderSettings(folder, fingerprint);
        await RecheckIfStillOpenAsync(project.Id);
    }

    private async Task RepairRemoteAsync(Project project, string folder)
    {
        if (_busyProjectId is not null) return;
        StartBusy(project, "Trying HTTPS and SSH…");
        var result = await GitTools.RepairRemoteAsync(folder, project.RepoUrl);
        EndBusy();

        if (result.Worked) await RefreshThenRecheckAsync(project.Id);
        else ShowPageProblem(project.Id, result.Value);
    }

    private async Task PointFolderAtRepoAsync(Project project, string folder)
    {
        if (_busyProjectId is not null) return;
        StartBusy(project, "Connecting the folder…");

        // The URL may be a typo, and the next push would send this folder's history there.
        var history = await GitTools.CompareFolderHistoryAsync(folder, project.RepoUrl);
        var why = history switch
        {
            FolderHistory.SharesNothing => $"{project.RepoUrl} has commits this folder doesn't, so they may not be the same project.",
            FolderHistory.CantTell when await GitTools.FolderHasNoCommitsAsync(folder) != true => $"Couldn't compare this folder's commits with {project.RepoUrl}, so they may not be the same project.",
            _ => null,
        };
        if (why is not null && !Dialogs.Confirm($"{why}\n\nConnecting them changes no files and pushes nothing, but the next push could send this folder's history there. Connect them anyway?"))
        {
            EndBusy();
            return;
        }

        // That can stay open a long time, and sync keeps running behind it.
        if (FindProject(project.Id) is not { } latest || !GitTools.SameRepo(latest.RepoUrl, project.RepoUrl))
        {
            EndBusy();
            await RecheckIfStillOpenAsync(project.Id);
            return;
        }

        var result = await GitTools.PointFolderAtRepoAsync(folder, project.RepoUrl);
        EndBusy();

        if (result.Worked) await RefreshThenRecheckAsync(project.Id);
        else ShowPageProblem(project.Id, result.Value);
    }

    // Says who's cloning as well as where, so another window of the app can tell it isn't abandoned.
    private static string CloneRecord(string folder)
    {
        using var self = Process.GetCurrentProcess();
        return $"{self.Id}|{self.StartTime.ToUniversalTime().Ticks}|{folder}";
    }

    // The app closed in the middle of a clone last time. There can be more than one, because
    // a folder on an unplugged drive stays on the list until it can be reached.
    private async Task RemoveLeftoverCloneAsync()
    {
        var finished = new List<string>();
        var kept = new List<string>();
        foreach (var record in CloneRecords())
        {
            var parts = record.Split('|', 3);
            var stillRunning = parts.Length == 3 && int.TryParse(parts[0], out var id) && long.TryParse(parts[1], out var started) && IsStillRunning(id, started);
            if (stillRunning) continue;

            var outcome = parts.Length == 3 ? await GitTools.RemoveLeftoverCloneAsync(parts[2]) : LeftoverClone.Gone;
            if (outcome != LeftoverClone.TryAgain) finished.Add(record);
            if (outcome == LeftoverClone.KeptFinished) kept.Add(parts[2]);
        }

        ChangeCloneRecords(records => records.Except(finished));

        // Hours of download can be in there, in a folder no one would think to look at.
        foreach (var folder in kept)
        {
            Dialogs.Notify($"A clone finished last time, but the app closed before it could be named or linked. It's at {folder}. You can rename it and select it. If it uses large files or submodules, get them with git lfs pull and git submodule update --init --recursive.");
        }
    }

    private List<string> CloneRecords() =>
        (_database.GetSetting(CloneInProgressSetting) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

    // Read and written together, as another window can be starting or finishing a clone of its own.
    private void ChangeCloneRecords(Func<IEnumerable<string>, IEnumerable<string>> change) =>
        _database.ChangeSetting(CloneInProgressSetting, current => string.Join('\n', change((current ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))));

    // The start time rules out another program that has since been given the same ID.
    private static bool IsStillRunning(int id, long startedTicks)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return process.StartTime.ToUniversalTime().Ticks == startedTicks;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access denied, as for a window running as administrator. Being wrong in this direction
            // leaves a folder for a later start, and being wrong in the other deletes a live clone.
            return true;
        }
    }

    // Saved even if they've left the page, because the clone or the choice was real.
    private async Task<bool> UseFolderAsync(Project chosenFor, string folder, string? folderBefore)
    {
        var projectId = chosenFor.Id;
        if (FindProject(projectId) is not { } project) return false;
        var heldAlready = _database.IsHoldingFolder(folder, projectId);
        if (!heldAlready && await _database.ClaimFolderAsync(folder, project) is { } other)
        {
            ShowPageProblem(projectId, LocalDatabase.RefusalFor(other, $"{other.Name} already uses {folder}. A folder can only be one project."));
            return false;
        }

        try
        {
            // Settings stay open during slow work, and sync keeps running, so this has to look again now.
            if (FindProject(projectId) is not { } latest) return false;
            if (_database.GetProjectFolder(projectId) != folderBefore || !GitTools.SameRepo(latest.RepoUrl, chosenFor.RepoUrl))
            {
                if (!heldAlready) _database.ReleaseFolder(folder);
                await RecheckThenShowAsync(projectId, $"{folder} wasn't linked, because this project's folder or repo changed while that was being checked.");
                return false;
            }

            _database.SetProjectFolder(projectId, folder);
            await GitTools.PickUpUnfinishedNoteAsync(folder);
        }
        finally
        {
            if (!heldAlready) _database.ReleaseFolder(folder);
        }

        // An open page reads it after its own check, and only if that lets this PC work in the project.
        if (_openProject?.Id != projectId) await ReadEngineVersionAsync(projectId);
        else
        {
            await RefreshThenRecheckAsync(projectId);
            if (_openProject?.Id != projectId) await ReadEngineVersionAsync(projectId);
            return true;
        }
        await RefreshThenRecheckAsync(projectId);
        return true;
    }

    private void OpenProjectSettings(string projectId)
    {
        if (FindProject(projectId) is not { } project) return;

        _settingsProjectId = projectId;
        _settingsOpenedWith = (_database.GetProjectFolder(projectId), project.RepoUrl);
        MainContent.IsEnabled = false;
        MainContent.Effect = new BlurEffect { Radius = 8 };
        ProjectSettings.Open(_database, project);
    }

    private void ProjectSettings_Changed() => _ = _sync.SyncNowAsync();

    private async void ProjectSettings_Closed()
    {
        MainContent.Effect = null;
        MainContent.IsEnabled = UpdateOverlay.Visibility != Visibility.Visible;

        // The engine or folder may have changed. A project whose long job is running is read once
        // that's done, so the read can't change its remote while the job works in its folder.
        if (_settingsProjectId is { } changedProject && changedProject != _openProject?.Id)
        {
            if (_busyProjectId == changedProject) _recheckAfterBusy = changedProject;
            else _ = TellIfDatabaseFailsAsync(ReadEngineVersionAsync(changedProject));
        }

        var openId = _openProject?.Id;
        if (openId is null)
        {
            RefreshProjects();
            return;
        }

        // The repo or folder may have changed. Work already running gets to finish first.
        if (_busyProjectId == openId)
        {
            _recheckAfterBusy = openId;
            RefreshProjects();
            return;
        }
        // What it says still stands, unless the folder or repo it was about has just been changed.
        if (_settingsOpenedWith == (_database.GetProjectFolder(openId), FindProject(openId)?.RepoUrl)) KeepProblemThroughReopen(openId);
        await RefreshThenRecheckAsync(openId);

        // Left before its check got there, so it's read the way any background read is.
        if (_openProject?.Id != openId) await ReadEngineVersionAsync(openId);
    }

    private void ProjectSettings_Deleted()
    {
        MainContent.Effect = null;
        MainContent.IsEnabled = UpdateOverlay.Visibility != Visibility.Visible;
        BackToProjects();
        RefreshProjects();
        _ = _sync.SyncNowAsync();
    }

    private void BackToProjects()
    {
        _navigation++;
        _openPageNeedsCheck = false;
        _openProject = null;
        _projectCheck = null;
        _pageProblem = null;
        ProjectView.Visibility = Visibility.Collapsed;
        ProjectSelectView.Visibility = Visibility.Visible;
    }
}
