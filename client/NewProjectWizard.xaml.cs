using Microsoft.Data.Sqlite;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>
/// RepoConfirmed means GitHub just said this PC can work on the repo, rather than it not being asked.
/// CloneIntoFolder means the folder is empty and the repo isn't, so the folder hasn't been connected and still needs a clone.
/// </summary>
public sealed record NewProject(string Name, string Engine, string GameType, string Folder, string RepoUrl, bool RepoConfirmed = false, bool CloneIntoFolder = false);

public static class ProjectChoices
{
    public static readonly string[] Engines = ["Unreal Engine", "Godot", "Unity", "Web", "Other"];
    public static readonly string[] GameTypes = ["3D", "2D", "2.5D", "Other"];
    public const int MaxNameLength = 100;
}

/// <summary>Creating a project: what it is, then where its files are.</summary>
public partial class NewProjectWizard : UserControl
{
    private static readonly string[] Steps = ["Project", "Location"];

    private string _name = "";
    private string _engine = ProjectChoices.Engines[0];
    private string? _heldRepo;
    private string _gameType = ProjectChoices.GameTypes[0];
    private string _folder = "";
    private string _repoUrl = "";
    private string _wantedRepoName = "";
    private string _autoFilledRepo = "";
    private bool _working;
    private string? _replacingUrl;
    private bool _offeringFolderSettings;
    private string _shownFolderSettings = "";
    private RepoCheck? _replacementOfferedFor;
    private bool _cloneIntoFolder;
    private bool _allowedForThisTry;

    public NewProjectWizard()
    {
        InitializeComponent();
    }

    public event Action<NewProject>? Created;

    /// <summary>Holds a folder for a new project of this name. Returns what's stopping that, if anything.</summary>
    public Func<string, string, Task<string?>> ClaimFolder { get; set; } = (_, _) => Task.FromResult<string?>(null);

    public Action<string> ReleaseFolder { get; set; } = _ => { };

    /// <summary>The same for a repo.</summary>
    public Func<string, string, Task<string?>> ClaimRepo { get; set; } = (_, _) => Task.FromResult<string?>(null);

    public Action<string> ReleaseRepo { get; set; } = _ => { };

    public event Action? Closed;

    /// <summary>Stopping now could leave a repo on GitHub with no project pointing at it.</summary>
    public bool IsCreatingRepo { get; private set; }

    public void Open()
    {
        _name = "";
        _engine = ProjectChoices.Engines[0];
        _gameType = ProjectChoices.GameTypes[0];
        _folder = "";
        _repoUrl = "";
        _wantedRepoName = "";
        _autoFilledRepo = "";
        _working = false;
        Visibility = Visibility.Visible;
        ShowProjectStep();
    }

    private void ShowProjectStep()
    {
        var name = Field(_name);
        name.Width = 300;
        name.MaxLength = ProjectChoices.MaxNameLength;
        var problem = ProblemText();

        void Next()
        {
            _name = name.Text.Trim();
            if (_name.Length == 0)
            {
                ShowProblem(problem, "Give the project a name.");
                Dispatcher.InvokeAsync(problem.BringIntoView, System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }
            ShowLocationStep();
        }

        name.TextChanged += (_, _) => _name = name.Text;
        name.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Next();
        };

        var engines = TileChoice("Engine", ProjectChoices.Engines, _engine, 5, engine => _engine = engine);
        var types = TileChoice("Game type", ProjectChoices.GameTypes, _gameType, 4, type => _gameType = type);

        var body = new StackPanel
        {
            Margin = new Thickness(8, 8, 8, 4),
            Children =
            {
                Row("Name", "You can change this later.", name),
                engines,
                types,
                problem,
            },
        };

        Card.Child = WizardShell(
            Close,
            "New project",
            "Set up a new game.",
            Stepper(Steps, 1),
            new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body },
            TextButton("Cancel", Close),
            PrimaryButton("Next", Next));

        Dispatcher.InvokeAsync(() =>
        {
            name.Focus();
            name.CaretIndex = name.Text.Length;
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private StackPanel TileChoice(string title, string[] options, string selected, int columns, Action<string> changed)
    {
        var tiles = new UniformGrid { Columns = columns, Margin = new Thickness(-4, 0, -4, 0) };
        foreach (var option in options)
        {
            var tile = new RadioButton
            {
                Style = (Style)FindResource("ChoiceTile"),
                GroupName = $"NewProject{title}",
                Content = option,
                IsChecked = option == selected,
                Margin = new Thickness(4),
            };
            tile.Checked += (_, _) => changed(option);
            tiles.Children.Add(tile);
        }

        var area = new StackPanel { Margin = new Thickness(16, 4, 16, 10) };
        area.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.Medium, Foreground = (Brush)FindResource("TextStrong"), Margin = new Thickness(0, 0, 0, 4) });
        area.Children.Add(tiles);
        return area;
    }

    private void ShowLocationStep()
    {
        _replacingUrl = null;
        _offeringFolderSettings = false;
        var folder = Field(_folder);
        folder.Width = 300;
        var repo = Field(_repoUrl);
        repo.Width = 300;
        var problem = ProblemText();
        problem.TextWrapping = TextWrapping.Wrap;
        var repoName = Field(_wantedRepoName);
        repoName.Width = 300;
        repoName.TextChanged += (_, _) => _wantedRepoName = repoName.Text;

        // Only used when creating a repo, so it can't look like it does something otherwise.
        void ShowRepoNameFor(string url) => repoName.IsEnabled = !_working && (url.Trim().Length == 0 || _replacingUrl is not null);
        repo.TextChanged += (_, _) => ShowRepoNameFor(repo.Text);
        ShowRepoNameFor(repo.Text);
        var progress = new TextBlock { FontSize = 12.5, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(16, 8, 0, 0), Visibility = Visibility.Collapsed };

        folder.TextChanged += (_, _) =>
        {
            _folder = folder.Text;

            // A URL filled in from the previous folder doesn't belong to this one.
            if (_autoFilledRepo.Length > 0 && repo.Text == _autoFilledRepo) repo.Text = "";
            _autoFilledRepo = "";
        };
        repo.TextChanged += (_, _) => _repoUrl = repo.Text;

        var browse = ActionButton("Browse");
        browse.Click += async (_, _) =>
        {
            var startIn = Path.IsPathFullyQualified(_folder) && await GitTools.FolderExistsAsync(_folder) ? _folder : null;
            if (Dialogs.PickFolder("Choose the project folder", startIn) is not { } chosen) return;

            folder.Text = chosen;

            // A URL filled in from the previous folder doesn't belong to this one.
            var typedItself = repo.Text.Trim().Length > 0 && repo.Text != _autoFilledRepo;
            if (typedItself) return;
            var before = repo.Text;
            var state = await GitTools.InspectFolderAsync(chosen);
            if (folder.Text != chosen || repo.Text != before) return;
            _autoFilledRepo = state?.OriginToCompare ?? "";
            repo.Text = _autoFilledRepo;
        };

        var back = ControlButton("Back", ShowProjectStep);
        var create = PrimaryButton("Create project", () => { });

        // Offered once a folder's repo turns out to be gone, and only for what's on screen.
        void ForgetOffer()
        {
            _replacingUrl = null;
            _offeringFolderSettings = false;
            create.Content = "Create project";
            ShowRepoNameFor(repo.Text);
        }
        folder.TextChanged += (_, _) => ForgetOffer();
        repo.TextChanged += (_, _) => ForgetOffer();

        void SetWorking(bool working, string? status = null)
        {
            _working = working;
            if (!working) IsCreatingRepo = false;
            create.IsEnabled = !working;
            back.IsEnabled = !working;
            browse.IsEnabled = !working;
            folder.IsEnabled = !working;
            repo.IsEnabled = !working;
            ShowRepoNameFor(repo.Text);
            progress.Text = status ?? "";
            progress.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
        }

        create.Click += async (_, _) =>
        {
            if (_working) return;
            problem.Visibility = Visibility.Collapsed;

            var path = folder.Text.Trim();
            if (path.Length == 0)
            {
                ShowProblem(problem, "Choose the project folder.");
                return;
            }
            if (!Path.IsPathFullyQualified(path))
            {
                ShowProblem(problem, "Enter the folder's full path, or use Browse.");
                return;
            }

            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) is { Length: > 3 } tidied ? tidied : Path.GetFullPath(path);

            // What was entered is fixed here, and the wizard can't close until this finishes.
            var typedUrl = repo.Text.Trim();
            var submission = new NewProject(_name, _engine, _gameType, path, typedUrl);

            SetWorking(true, "Checking…");
            _cloneIntoFolder = false;
            string? failure;
            string url;
            bool confirmed;
            var claimed = false;
            string? madeRepo = null; // It's on GitHub from then on, and this may be all that says where.
            try
            {
                if (!await GitTools.FolderExistsAsync(path))
                {
                    (url, confirmed, failure) = ("", false, "That folder doesn't exist.");
                }
                else if (await ClaimFolder(path, _name) is { } folderTaken)
                {
                    (url, confirmed, failure) = ("", false, folderTaken);
                }
                else if (_replacingUrl is { } replacing)
                {
                    claimed = true;

                    // Time has passed since they were offered this, and a different account may be active now.
                    progress.Text = "Checking the repo again…";
                    var latest = await GitTools.CheckRepoAccessAsync(replacing, path);
                    if (latest != _replacementOfferedFor)
                    {
                        var (heading, description) = latest.Describe();
                        _replacingUrl = null;
                        (url, confirmed, failure) = ("", false, $"GitHub's answer has changed. {heading}. {description}");
                    }
                    else
                    {
                        IsCreatingRepo = true;
                        progress.Text = "Creating a private repo on GitHub…";
                        var created = await GitTools.CreatePrivateRepoAsync(_name, path, replacing, _wantedRepoName);
                        (url, confirmed, failure) = created.Worked ? (created.Value, true, (string?)null) : ("", false, created.Value);
                    }
                }
                else
                {
                    claimed = true;
                    var allowedJustNow = await GitTools.AskAboutUnreadableSshSettingsAsync(path);

                    // Now its settings can be read, an address filled in from the folder may turn out to be GitHub.
                    if (typedUrl.Length > 0 && typedUrl == _autoFilledRepo && await GitTools.InspectFolderAsync(path) is { OriginToCompare: { } known } && known != typedUrl)
                    {
                        _autoFilledRepo = known;
                        repo.Text = known;
                        submission = submission with { RepoUrl = known };
                    }

                    _allowedForThisTry = false;
                    (url, confirmed, failure) = await ResolveRepoAsync(submission, status => progress.Text = status);

                    // An approval only stands if the folder then gets used.
                    if ((allowedJustNow || _allowedForThisTry) && failure is not null) GitTools.ForgetAllowedFolderSettings(path);
                }

                // One that was just made couldn't be held any sooner, and another window could have got to it.
                if (failure is null && _heldRepo is null)
                {
                    if (IsCreatingRepo) madeRepo = url;
                    if (await ClaimRepo(url, _name) is { } repoTaken) (url, confirmed, failure) = ("", false, madeRepo is null ? repoTaken : $"The repo was created at {madeRepo}, but it couldn't be used here. {repoTaken}");
                    else _heldRepo = url;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A drive can drop at any point in all of that.
                AppLog.Write($"Couldn't use a project folder: {error.Message}");
                (url, confirmed, failure) = ("", false, $"That folder stopped being available: {error.Message}");
            }
            catch (SqliteException error)
            {
                AppLog.Write($"Couldn't check a new project: {error.Message}");
                (url, confirmed, failure) = ("", false, madeRepo is null
                    ? "The app's database can't be reached, so nothing was saved. Try again once it's back."
                    : $"The repo was created at {madeRepo}, but the app's database can't be reached, so the project wasn't saved. Once it's back, enter that address here and try again.");
            }
            finally
            {
                SetWorking(false);
            }

            if (failure is not null)
            {
                ReleaseHeldRepo();
                if (claimed) ReleaseFolder(path);
                ShowProblem(problem, failure);
                create.Focus(); // Being disabled took it away, and Esc needs it to be inside the wizard.
                _ = Dispatcher.InvokeAsync(problem.BringIntoView, System.Windows.Threading.DispatcherPriority.Loaded);
                create.Content = _replacingUrl is not null ? "Create a new repo" : _offeringFolderSettings ? "Use its Git settings" : "Create project";
                return;
            }

            // Where a clone is going stays held, by whoever runs it.
            try
            {
                Created?.Invoke(submission with { RepoUrl = url, RepoConfirmed = confirmed, CloneIntoFolder = _cloneIntoFolder });
            }
            catch (SqliteException error)
            {
                AppLog.Write($"Couldn't save a new project: {error.Message}");
                ReleaseFolder(path);
                ShowProblem(problem, madeRepo is null
                    ? "The app's database can't be reached, so the project may not have been saved. Once it's back, check your projects before trying again."
                    : $"The repo was created at {madeRepo}, but the app's database can't be reached, so the project may not have been saved. Once it's back, check your projects, and use that address if it isn't there.");
                return;
            }
            finally
            {
                ReleaseHeldRepo();
            }
            if (!_cloneIntoFolder) ReleaseFolder(path);
            Close();
        };

        var body = new StackPanel
        {
            Margin = new Thickness(8, 8, 8, 12),
            Children =
            {
                Row("Project folder", "Where this game's files are on this PC.", folder, browse),
                Row("Repo URL", "Leave blank to create a new, empty private repo on GitHub. Nothing is pushed, so push your work for your other PCs to get it.", repo),
                Row("Repo name", "For a new repo. Leave blank to use the project's name. GitHub needs Latin letters and numbers.", repoName),
                progress,
                problem,
            },
        };

        Card.Child = WizardShell(
            Close,
            "New project",
            "Set up a new game.",
            Stepper(Steps, 2),
            new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body },
            back,
            create);

        // Rebuilding the step took focus away, and Esc only works with it inside the wizard.
        Dispatcher.InvokeAsync(() => folder.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>
    /// Works out the project's repo, so a project is never created that can't then be opened.
    /// Returns the URL, or a problem to show. Being offline doesn't block this, because
    /// nothing says the repo is unusable, and opening the project checks again.
    /// </summary>
    private async Task<(string Url, bool Confirmed, string? Failure)> ResolveRepoAsync(NewProject project, Action<string> status)
    {
        var state = await GitTools.InspectFolderAsync(project.Folder);
        if (state is null) return ("", false, "Git wasn't found on this PC.");
        if (state.Problem is not null) return ("", false, state.Problem);
        if (state.IsInsideAnotherRepo) return ("", false, "That folder is inside a repo. Choose the repo's top folder.");

        var url = project.RepoUrl;
        if (url.Length == 0)
        {
            // Blank promises a new repo, which this folder can't have without losing its remote.
            if (state.GitHubOrigin is not null) return ("", false, $"That folder already uses {state.GitHubOrigin}. Enter that URL to use it, or choose a folder with no repo to create a new one.");
            if (state.Origin is not null) return ("", false, $"That folder uses {state.Origin}, which isn't on GitHub. Choose a folder with no repo, or one that's on GitHub.");

            IsCreatingRepo = true;
            status("Creating a private repo on GitHub…");
            var created = await GitTools.CreatePrivateRepoAsync(project.Name, project.Folder, null, _wantedRepoName);
            return created.Worked ? (created.Value, true, null) : ("", false, created.Value);
        }

        if (GitTools.RepoUrlProblem(url) is { } urlProblem) return ("", false, urlProblem);

        // GitHub still answers to a repo's old names, so the address isn't enough to tell them apart.
        var (current, holder, underAnotherName) = await GitTools.WhoHasRepoAsync(url);
        if (holder is not null)
        {
            return ("", false, underAnotherName
                ? $"{holder} already uses that repo, under another name it's had on GitHub. A repo can only be one project."
                : $"{holder} already uses that repo. A repo can only be one project.");
        }
        url = current;

        // Before anything is done to the folder, as nothing gets undone if this is refused later.
        if (await ClaimRepo(url, project.Name) is { } repoTaken) return ("", false, repoTaken);
        _heldRepo = url;

        if (state.Origin is not null && !GitTools.SameRepo(state.OriginToCompare!, url))
        {
            var (moved, moveProblem) = await GitTools.MoveIfRepoRenamedAsync(project.Folder, state, url);
            if (moveProblem is not null) return ("", false, moveProblem);
            if (moved)
            {
                state = await GitTools.InspectFolderAsync(project.Folder);
                if (state is null) return ("", false, "Git wasn't found on this PC.");
                if (state.Problem is not null) return ("", false, state.Problem);
            }

            else
            {
                return ("", false, "That folder is connected to a different repo than that URL.");
            }
        }

        // The same check as selecting a folder for an existing project.
        if (state.Origin is not null && await GitTools.FolderProblemAsync(project.Folder, url) is { } folderProblem)
        {
            return ("", false, folderProblem);
        }

        // A folder's own Git settings can run programs, so they're only used once they've been seen.
        var runnable = state.Origin is not null ? await GitTools.FolderRunnableSettingsAsync(project.Folder) : FolderGitSettings.None;

        // Only what was actually shown gets approved. If it has changed since, it's shown again.
        if (_offeringFolderSettings)
        {
            if (runnable?.Fingerprint == _shownFolderSettings)
            {
                GitTools.RememberAllowedFolderSettings(project.Folder, _shownFolderSettings);
                _allowedForThisTry = true;
            }
            _offeringFolderSettings = false;
        }
        var useFolderSettings = state.Origin is not null && runnable is not null && GitTools.AreAllowed(project.Folder, runnable);

        var check = await GitTools.CheckRepoAccessAsync(url, state.Origin is not null ? project.Folder : null, useFolderSettings);
        switch (check.Access)
        {
            case RepoAccess.Allowed or RepoAccess.Offline:
                break;
            case RepoAccess.GitCantReach or RepoAccess.GitReadOnly when !useFolderSettings && runnable is { CanBeApproved: false }:
                return ("", false, "Git can't reach this repo without this folder's own Git settings, and there are too many of them to show here for you to check. Cut them down, or choose another copy.");
            case RepoAccess.GitCantReach or RepoAccess.GitReadOnly when !useFolderSettings && runnable is { IsEmpty: false }:
                _offeringFolderSettings = true;
                _shownFolderSettings = runnable.Fingerprint;
                return ("", false, $"Git can't reach this repo without this folder's own Git settings, which can run programs or send your login somewhere else:\n{runnable.Shown}\nOnly use them if you trust where this folder came from.");
            case RepoAccess.Archived:
                return ("", false, "That repo is archived, so GitHub won't accept changes to it. Unarchive it on GitHub, or use a different repo.");
            case RepoAccess.SignedOut:
                return ("", false, "This PC isn't signed in to GitHub. Run gh auth login in a terminal, then try again.");
            case RepoAccess.OtherAccount:
                return ("", false, $"Your active GitHub account can't work on this repo, but {check.Account} can. Run gh auth switch --user {check.Account} in a terminal, then try again.");
            case RepoAccess.NotFound or RepoAccess.NotFoundOrNoAccess when state.Origin is not null:
                var (gone, why) = check.Describe();
                _replacingUrl = url;
                _replacementOfferedFor = check;
                var where = check.Account is null ? "your active GitHub account" : $"the GitHub account {check.Account}";
                var newName = GitTools.ChosenRepoName(_wantedRepoName, project.Name);
                var called = newName.Length > 0 ? $"called {newName} " : "";
                return ("", false, $"{gone}. {why} This folder is connected to it, so you can create a new, empty private repo {called}for the folder under {where} instead. You can set its name above. Nothing is pushed.");
            default:
                var (heading, description) = check.Describe();
                return ("", false, $"{heading}. {description}");
        }

        if (state.Origin is null)
        {
            // Connecting a folder to a repo that already has work in it doesn't bring that work
            // over, so the two would just look like the same project without being it.
            var hasContent = check.Access == RepoAccess.Allowed ? await GitTools.RepoHasContentAsync(url) : false;
            if (hasContent is null) return ("", false, "Couldn't check whether that repo already has files, so nothing was changed. Try again.");
            if (hasContent == true)
            {
                if (await GitTools.FolderIsEmptyAsync(project.Folder))
                {
                    if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(project.Folder)) is null) return ("", false, "That's a whole drive, and a repo's files can't be cloned onto it. Choose a folder on it.");
                    if (await GitTools.FolderIsLinkAsync(project.Folder)) return ("", false, "That folder is a link, and a clone would replace it. Choose the real folder it points to.");
                    if (!await GitTools.CanWorkBesideAsync(project.Folder)) return ("", false, "A clone is prepared beside the folder it's going into, and this PC can't write there. Make a new folder inside this one and choose that.");

                    _cloneIntoFolder = true;
                    return (url, true, null);
                }

                var question = await GitTools.FolderHoldsOnlyGitAsync(project.Folder)
                    ? "That repo already has files, and this folder has Git set up but nothing in it yet.\n\nConnecting them doesn't bring the files over, so you'd need to pull them yourself. Connect them anyway?"
                    : "That repo already has files, and this folder has its own and isn't connected to it, so they may not be the same project.\n\nConnecting them changes no files and pushes nothing, but Git may then treat their histories as unrelated. Connect them anyway?";
                if (!Dialogs.Confirm(question))
                {
                    return ("", false, "Nothing was changed. To get a copy of that repo, choose an empty folder. Or choose a folder that's already connected to it.");
                }
            }

            status("Connecting the folder to the repo…");
            var form = check.Access == RepoAccess.Allowed ? await GitTools.ReachableFormAsync(url) ?? url : url;
            var connected = await GitTools.ConnectFolderAsync(project.Folder, form);
            if (!connected.Worked) return ("", false, connected.Value);
        }

        return (url, check.Access == RepoAccess.Allowed, null);
    }

    private void ReleaseHeldRepo()
    {
        if (_heldRepo is { } held) ReleaseRepo(held);
        _heldRepo = null;
    }

    private void Close()
    {
        if (_working) return;
        Visibility = Visibility.Collapsed;
        Closed?.Invoke();
    }

    private void Wizard_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void Backdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
