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

/// <summary>The project library and each project's page.</summary>
public partial class MainWindow
{
    private const int CoverWidth = 176;
    private const int CoverHeight = 264;
    private static readonly TimeSpan ClickSuppressionAfterDrag = TimeSpan.FromMilliseconds(400);

    private Project? _openProject;
    private RepoCheck? _projectCheck;
    private string? _folderProblem;
    private bool _pageBusy;
    private Button? _pressedCard;
    private Button? _draggedCard;
    private Point _pressPoint;
    private DateTimeOffset _dragEnded;

    private void RefreshProjects()
    {
        if (_draggedCard is not null) return;

        var projects = _database.ListProjects();
        ProjectGrid.Children.Clear();
        foreach (var project in projects) ProjectGrid.Children.Add(ProjectCard(project));
        NoProjects.Visibility = projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_openProject is null || _pageBusy || ProjectSettings.Visibility == Visibility.Visible) return;
        var current = projects.FirstOrDefault(project => project.Id == _openProject.Id);
        if (current is not null) RenderProject(current);
    }

    private bool IsOnThisPc(Project project) =>
        _database.GetProjectFolder(project.Id) is { } folder && Directory.Exists(folder);

    // Library

    private Button ProjectCard(Project project)
    {
        var thumbnail = ProjectImages.ToImage(_database.GetProjectImage(project.Id, ProjectImageKind.Thumbnail));
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

        var coverArea = new Grid { Children = { cover, gear } };
        var content = new StackPanel { Children = { coverArea } };
        content.Children.Add(new TextBlock { Text = project.Name, Foreground = (Brush)FindResource("TextStrong"), FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 10, 2, 0), ToolTip = project.Name });
        content.Children.Add(new TextBlock { Text = project.Summary, Foreground = (Brush)FindResource("TextMuted"), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 2, 2, 0) });
        if (!IsOnThisPc(project))
        {
            content.Children.Add(new TextBlock { Text = "Not on this PC", Foreground = (Brush)FindResource("Accent"), FontSize = 12.5, Margin = new Thickness(2, 2, 2, 0) });
        }

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
        DragDrop.DoDragDrop(card, new DataObject(typeof(string), card.Tag), DragDropEffects.Move);

        card.Opacity = 1;
        _draggedCard = null;
        _dragEnded = DateTimeOffset.UtcNow;

        var after = CurrentOrder();
        if (before.SequenceEqual(after)) return;
        _database.SaveProjectOrder(after);
        _ = _sync.SyncNowAsync();
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

    // A project's page. Opening one first checks this PC is allowed to work on its repo.

    private async Task OpenProjectAsync(Project project)
    {
        _openProject = project;
        _projectCheck = null;
        _folderProblem = null;
        ProjectSelectView.Visibility = Visibility.Collapsed;
        ProjectView.Visibility = Visibility.Visible;
        RenderProject(project);

        var check = project.RepoUrl.Length > 0
            ? await GitTools.CheckRepoAccessAsync(project.RepoUrl)
            : new RepoCheck(RepoAccess.NotFound);

        string? folderProblem = null;
        if (check.Access is RepoAccess.Allowed or RepoAccess.CantCheck
            && _database.GetProjectFolder(project.Id) is { } folder
            && Directory.Exists(folder))
        {
            folderProblem = await GitTools.FolderProblemAsync(folder, project.RepoUrl);
        }

        if (_openProject?.Id != project.Id) return; // They moved on while this was checking.

        if (check.Access == RepoAccess.Allowed) _database.SetSetting(VerifiedSetting(project.Id), "1");
        _projectCheck = check;
        _folderProblem = folderProblem;
        RenderProject(project);
    }

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
            body.Children.Add(new TextBlock { Text = "Checking the repo…", Foreground = (Brush)FindResource("TextMuted"), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 60, 0, 0) });
            return;
        }

        // Being offline doesn't mean access was lost, so a PC that's been allowed before can keep working.
        var workingOffline = check.Access == RepoAccess.CantCheck && _database.GetSetting(VerifiedSetting(project.Id)) == "1";
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
        if (IsOnThisPc(project) && _folderProblem is null)
        {
            body.Children.Add(new TextBlock { Text = folder, Foreground = (Brush)FindResource("TextMuted"), FontSize = 13, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = folder });
            body.Children.Add(new TextBlock { Text = "Nothing here yet.", Foreground = (Brush)FindResource("TextMuted"), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 60, 0, 0) });
            return;
        }

        AddGetProjectOptions(body, project, folder);
    }

    private void AddRepoProblem(StackPanel body, Project project, RepoCheck check)
    {
        var problem = ProblemText();
        var progress = new TextBlock { FontSize = 13, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(16, 10, 0, 0), Visibility = Visibility.Collapsed };
        var actions = new List<FrameworkElement>();

        var (heading, description) = check.Access switch
        {
            RepoAccess.NoGitHubCli => ("The GitHub CLI is needed", "Agent Orange uses it to check your access. Install it from cli.github.com, then try again."),
            RepoAccess.SignedOut => ("Sign in to GitHub", "This PC isn't signed in, so your access to this project's repo can't be checked."),
            RepoAccess.OtherAccount => ("Wrong GitHub account", $"Your active GitHub account can't see this repo, but {check.Account} can."),
            RepoAccess.ReadOnly => ("You don't have access to this repo", $"{check.Account ?? "Your GitHub account"} can only read it, so you can't work on this project. Get write access, or change the repo in project settings."),
            RepoAccess.NotFound => ("This project's repo doesn't exist", "It may have been deleted or renamed. Create a new private repo, or change the URL in project settings."),
            RepoAccess.NotFoundOrNoAccess => ("Can't find this project's repo", "It was deleted, or your GitHub account doesn't have access. GitHub doesn't say which. If you should have access, ask the owner. Otherwise you can create a new private repo."),
            RepoAccess.GitCantReach => ("Git on this PC can't reach this repo", "Check this PC's Git setup, such as its SSH keys, or run gh auth setup-git."),
            _ => ("Couldn't check this project's repo", "This PC couldn't reach GitHub, and it hasn't had access to this project before."),
        };

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
                if (await GitTools.SwitchAccountAsync(check.Account!)) await OpenProjectAsync(project);
                else ShowProblem(problem, "Couldn't switch accounts.");
            };
            actions.Add(switchAccount);
        }

        if (check.Access is RepoAccess.NotFound or RepoAccess.NotFoundOrNoAccess)
        {
            var create = ActionButton("Create new repo");
            create.Click += async (_, _) => await CreateReplacementRepoAsync(project, create, problem, progress);
            actions.Add(create);
        }

        var retry = ActionButton("Try again");
        retry.Click += async (_, _) => await OpenProjectAsync(project);
        actions.Add(retry);

        body.Children.Add(Section(heading));
        body.Children.Add(new TextBlock { Text = description, Foreground = (Brush)FindResource("TextMuted"), FontSize = 13, Margin = new Thickness(16, -4, 16, 12), TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var action in actions)
        {
            action.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(action);
        }
        body.Children.Add(buttons);
        body.Children.Add(progress);
        body.Children.Add(problem);
    }

    private async Task CreateReplacementRepoAsync(Project project, Button create, TextBlock problem, TextBlock progress)
    {
        var folder = _database.GetProjectFolder(project.Id);
        if (folder is null || !Directory.Exists(folder))
        {
            var dialog = new OpenFolderDialog { Title = $"Choose the folder for {project.Name}" };
            if (dialog.ShowDialog() != true) return;
            folder = dialog.FolderName;
        }

        _pageBusy = true;
        create.IsEnabled = false;
        problem.Visibility = Visibility.Collapsed;
        progress.Text = "Creating a private repo on GitHub…";
        progress.Visibility = Visibility.Visible;

        var result = await GitTools.CreatePrivateRepoAsync(project.Name, folder);

        progress.Visibility = Visibility.Collapsed;
        create.IsEnabled = true;
        _pageBusy = false;

        if (!result.Worked)
        {
            ShowProblem(problem, result.Value);
            return;
        }

        var updated = project with { RepoUrl = result.Value };
        _database.SaveProject(updated);
        _database.SetProjectFolder(project.Id, folder);
        _ = _sync.SyncNowAsync();
        RefreshProjects();
        await OpenProjectAsync(updated);
    }

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

    private void AddGetProjectOptions(StackPanel body, Project project, string? missingFolder)
    {
        var problem = ProblemText();
        var progress = new TextBlock { FontSize = 13, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(16, 10, 0, 0), Visibility = Visibility.Collapsed };

        var select = ActionButton("Select folder");
        var clone = ActionButton("Clone");

        void SetBusy(bool busy)
        {
            _pageBusy = busy;
            select.IsEnabled = !busy;
            clone.IsEnabled = !busy && project.RepoUrl.Length > 0;
        }
        SetBusy(false);

        select.Click += async (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = $"Find the folder for {project.Name}" };
            if (dialog.ShowDialog() != true) return;

            SetBusy(true);
            var folderProblem = await GitTools.FolderProblemAsync(dialog.FolderName, project.RepoUrl);
            SetBusy(false);
            if (folderProblem is not null)
            {
                ShowProblem(problem, folderProblem);
                return;
            }

            UseFolder(project, dialog.FolderName);
        };

        clone.Click += async (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "Choose where to put the project" };
            if (dialog.ShowDialog() != true) return;

            SetBusy(true);
            problem.Visibility = Visibility.Collapsed;
            progress.Text = "Cloning…";
            progress.Visibility = Visibility.Visible;
            var result = await GitTools.CloneAsync(project.RepoUrl, dialog.FolderName);
            progress.Visibility = Visibility.Collapsed;
            SetBusy(false);

            if (result.Worked) UseFolder(project, result.Value);
            else ShowProblem(problem, result.Value);
        };

        var (heading, description) = (missingFolder, _folderProblem) switch
        {
            (_, not null) => ("This project's folder doesn't match its repo", _folderProblem),
            (null, _) => ("This project isn't on this PC yet", "Get a copy from its repo, or point to one you already have."),
            _ => ("This project's folder is missing", $"It was at {missingFolder}."),
        };
        body.Children.Add(Section(heading));
        body.Children.Add(new TextBlock { Text = description, Foreground = (Brush)FindResource("TextMuted"), FontSize = 13, Margin = new Thickness(16, -4, 0, 10), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(Card(
            Row("Clone from repo", project.RepoUrl.Length > 0 ? project.RepoUrl : "This project has no repo.", clone),
            Row("Select its folder", "It has to be connected to the same repo.", select)));
        body.Children.Add(progress);
        body.Children.Add(problem);
    }

    private void UseFolder(Project project, string folder)
    {
        _database.SetProjectFolder(project.Id, folder);
        _folderProblem = null;
        RefreshProjects();
        RenderProject(project);
    }

    private void OpenProjectSettings(string projectId)
    {
        var project = _database.ListProjects().FirstOrDefault(candidate => candidate.Id == projectId);
        if (project is null) return;

        MainContent.IsEnabled = false;
        MainContent.Effect = new BlurEffect { Radius = 8 };
        ProjectSettings.Open(_database, project);
    }

    private void ProjectSettings_Changed() => _ = _sync.SyncNowAsync();

    private async void ProjectSettings_Closed()
    {
        MainContent.Effect = null;
        MainContent.IsEnabled = _requiredUpdate is null;
        RefreshProjects();

        // The repo or folder may have changed, so check again.
        if (_openProject is not null
            && _database.ListProjects().FirstOrDefault(project => project.Id == _openProject.Id) is { } current)
        {
            await OpenProjectAsync(current);
        }
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
        _openProject = null;
        _projectCheck = null;
        ProjectView.Visibility = Visibility.Collapsed;
        ProjectSelectView.Visibility = Visibility.Visible;
    }
}
