using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>RepoConfirmed means GitHub just said this PC can work on the repo, rather than it not being asked.</summary>
public sealed record NewProject(string Name, string Engine, string EngineVersion, string GameType, string Folder, string RepoUrl, bool RepoConfirmed = false);

public static class ProjectChoices
{
    public static readonly string[] Engines = ["Unreal Engine", "Godot", "Web", "Other"];
    public static readonly string[] GameTypes = ["3D", "2D", "2.5D", "Other"];

    /// <summary>Web and Other don't have an engine version to speak of.</summary>
    public static bool HasEngineVersion(string engine) => engine is "Unreal Engine" or "Godot";

    /// <summary>The newest Unreal installed here, as a starting point.</summary>
    public static string SuggestedVersion(string engine)
    {
        if (engine != "Unreal Engine") return "";
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games");
        if (!Directory.Exists(folder)) return "";

        return Directory.GetDirectories(folder, "UE_*")
            .Select(path => Path.GetFileName(path)["UE_".Length..])
            .Where(name => Version.TryParse(name, out _))
            .OrderByDescending(Version.Parse)
            .FirstOrDefault() ?? "";
    }
}

/// <summary>Creating a project: what it is, then where its files are.</summary>
public partial class NewProjectWizard : UserControl
{
    private static readonly string[] Steps = ["Project", "Location"];

    private string _name = "";
    private string _engine = ProjectChoices.Engines[0];
    private string _engineVersion = "";
    private string _gameType = ProjectChoices.GameTypes[0];
    private string _folder = "";
    private string _repoUrl = "";
    private string _autoFilledRepo = "";
    private bool _working;

    public NewProjectWizard()
    {
        InitializeComponent();
    }

    public event Action<NewProject>? Created;

    /// <summary>Gives the name of the project already using a folder, if there is one.</summary>
    public Func<string, string?> FolderTakenBy { get; set; } = _ => null;

    public event Action? Closed;

    public void Open()
    {
        _name = "";
        _engine = ProjectChoices.Engines[0];
        _engineVersion = ProjectChoices.SuggestedVersion(_engine);
        _gameType = ProjectChoices.GameTypes[0];
        _folder = "";
        _repoUrl = "";
        _autoFilledRepo = "";
        _working = false;
        Visibility = Visibility.Visible;
        ShowProjectStep();
    }

    private void ShowProjectStep()
    {
        var name = Field(_name);
        name.Width = 300;
        var version = Field(_engineVersion);
        version.Width = 300;
        var problem = ProblemText();
        var versionRow = Row("Engine version", "Like 5.8.", version);
        versionRow.Visibility = ProjectChoices.HasEngineVersion(_engine) ? Visibility.Visible : Visibility.Collapsed;

        void Next()
        {
            _name = name.Text.Trim();
            _engineVersion = version.Text.Trim();
            if (_name.Length == 0)
            {
                ShowProblem(problem, "Give the project a name.");
                return;
            }
            ShowLocationStep();
        }

        name.TextChanged += (_, _) => _name = name.Text;
        version.TextChanged += (_, _) => _engineVersion = version.Text;
        name.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Next();
        };

        var engines = TileChoice("Engine", ProjectChoices.Engines, _engine, 4, engine =>
        {
            _engine = engine;
            version.Text = ProjectChoices.SuggestedVersion(engine);
            versionRow.Visibility = ProjectChoices.HasEngineVersion(engine) ? Visibility.Visible : Visibility.Collapsed;
        });
        var types = TileChoice("Game type", ProjectChoices.GameTypes, _gameType, 4, type => _gameType = type);

        var body = new StackPanel
        {
            Margin = new Thickness(8, 8, 8, 4),
            Children =
            {
                Row("Name", "You can change this later.", name),
                engines,
                versionRow,
                types,
                problem,
            },
        };

        Card.Child = WizardShell(
            Close,
            "New project",
            "Set up a new game.",
            Stepper(Steps, 1),
            body,
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
        var folder = Field(_folder);
        folder.Width = 300;
        var repo = Field(_repoUrl);
        repo.Width = 300;
        var problem = ProblemText();
        problem.TextWrapping = TextWrapping.Wrap;
        var progress = new TextBlock { FontSize = 12.5, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(16, 8, 0, 0), Visibility = Visibility.Collapsed };

        folder.TextChanged += (_, _) => _folder = folder.Text;
        repo.TextChanged += (_, _) => _repoUrl = repo.Text;

        var browse = ActionButton("Browse");
        browse.Click += async (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "Choose the project folder" };
            if (await GitTools.FolderExistsAsync(_folder)) dialog.InitialDirectory = _folder;
            if (dialog.ShowDialog() != true) return;

            folder.Text = dialog.FolderName;

            // A URL filled in from the previous folder doesn't belong to this one.
            var typedItself = repo.Text.Trim().Length > 0 && repo.Text != _autoFilledRepo;
            if (typedItself) return;
            var before = repo.Text;
            var state = await GitTools.InspectFolderAsync(dialog.FolderName);
            if (folder.Text != dialog.FolderName || repo.Text != before) return;
            _autoFilledRepo = state?.Origin ?? "";
            repo.Text = _autoFilledRepo;
        };

        var back = ControlButton("Back", ShowProjectStep);
        var create = PrimaryButton("Create project", () => { });

        void SetWorking(bool working, string? status = null)
        {
            _working = working;
            create.IsEnabled = !working;
            back.IsEnabled = !working;
            browse.IsEnabled = !working;
            folder.IsEnabled = !working;
            repo.IsEnabled = !working;
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
            if (FolderTakenBy(path) is { } otherProject)
            {
                ShowProblem(problem, $"{otherProject} already uses that folder. A folder can only be one project.");
                return;
            }

            // What was entered is fixed here, and the wizard can't close until this finishes.
            var typedUrl = repo.Text.Trim();
            var submission = new NewProject(_name, _engine, ProjectChoices.HasEngineVersion(_engine) ? _engineVersion : "", _gameType, path, typedUrl);

            SetWorking(true, "Checking…");
            string? failure;
            string url;
            bool confirmed;
            try
            {
                (url, confirmed, failure) = await GitTools.FolderExistsAsync(path)
                    ? await ResolveRepoAsync(submission, status => progress.Text = status)
                    : ("", false, "That folder doesn't exist.");
            }
            finally
            {
                SetWorking(false);
            }

            if (failure is not null)
            {
                ShowProblem(problem, failure);
                return;
            }

            Created?.Invoke(submission with { RepoUrl = url, RepoConfirmed = confirmed });
            Close();
        };

        var body = new StackPanel
        {
            Margin = new Thickness(8, 8, 8, 12),
            Children =
            {
                Row("Project folder", "Where this game's files are on this PC.", folder, browse),
                Row("Repo URL", "Leave blank to create a new private repo on GitHub.", repo),
                progress,
                problem,
            },
        };

        Card.Child = WizardShell(
            Close,
            "New project",
            "Set up a new game.",
            Stepper(Steps, 2),
            body,
            back,
            create);
    }

    /// <summary>
    /// Works out the project's repo, so a project is never created that can't then be opened.
    /// Returns the URL, or a problem to show. Being offline doesn't block this, because
    /// nothing says the repo is unusable, and opening the project checks again.
    /// </summary>
    private static async Task<(string Url, bool Confirmed, string? Failure)> ResolveRepoAsync(NewProject project, Action<string> status)
    {
        var state = await GitTools.InspectFolderAsync(project.Folder);
        if (state is null) return ("", false, "Git wasn't found on this PC.");
        if (state.Problem is not null) return ("", false, state.Problem);
        if (state.IsInsideAnotherRepo) return ("", false, "That folder is inside a repo. Choose the repo's top folder.");

        var url = project.RepoUrl;
        if (url.Length == 0)
        {
            // Blank promises a new repo, which this folder can't have without losing its remote.
            if (state.Origin is not null) return ("", false, $"That folder already uses {state.Origin}. Enter that URL to use it, or choose a folder with no repo to create a new one.");

            status("Creating a private repo on GitHub…");
            var created = await GitTools.CreatePrivateRepoAsync(project.Name, project.Folder);
            return created.Worked ? (created.Value, true, null) : ("", false, created.Value);
        }

        if (GitTools.RepoUrlProblem(url) is { } urlProblem) return ("", false, urlProblem);
        if (state.Origin is not null && !GitTools.SameRepo(state.Origin, url))
        {
            return ("", false, "That folder is connected to a different repo than that URL.");
        }

        var check = await GitTools.CheckRepoAccessAsync(url);
        switch (check.Access)
        {
            case RepoAccess.Allowed or RepoAccess.Offline:
                break;
            case RepoAccess.SignedOut:
                return ("", false, "This PC isn't signed in to GitHub. Run gh auth login in a terminal, then try again.");
            case RepoAccess.OtherAccount:
                return ("", false, $"Your active GitHub account can't work on this repo, but {check.Account} can. Run gh auth switch --user {check.Account} in a terminal, then try again.");
            default:
                var (heading, description) = check.Describe();
                return ("", false, $"{heading}. {description}");
        }

        if (state.Origin is null)
        {
            status("Connecting the folder to the repo…");
            var connected = await GitTools.ConnectFolderAsync(project.Folder, url);
            if (!connected.Worked) return ("", false, connected.Value);
        }

        return (url, check.Access == RepoAccess.Allowed, null);
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
