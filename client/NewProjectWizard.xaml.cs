using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using static AgentOrange.Ui;

namespace AgentOrange;

public sealed record NewProject(string Name, string Engine, string EngineVersion, string GameType, string Folder, string RepoUrl);

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
        var newest = Directory.GetDirectories(folder, "UE_*").OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        return newest is null ? "" : Path.GetFileName(newest)["UE_".Length..];
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
    private bool _working;

    public NewProjectWizard()
    {
        InitializeComponent();
    }

    public event Action<NewProject>? Created;

    public event Action? Closed;

    public void Open()
    {
        _name = "";
        _engine = ProjectChoices.Engines[0];
        _engineVersion = ProjectChoices.SuggestedVersion(_engine);
        _gameType = ProjectChoices.GameTypes[0];
        _folder = "";
        _repoUrl = "";
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
        var progress = new TextBlock { FontSize = 12.5, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(16, 8, 0, 0), Visibility = Visibility.Collapsed };

        folder.TextChanged += (_, _) => _folder = folder.Text;
        repo.TextChanged += (_, _) => _repoUrl = repo.Text;

        var browse = ActionButton("Browse");
        browse.Click += async (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "Choose the project folder" };
            if (Directory.Exists(_folder)) dialog.InitialDirectory = _folder;
            if (dialog.ShowDialog() != true) return;

            folder.Text = dialog.FolderName;
            if (repo.Text.Trim().Length == 0 && await GitTools.OriginOfAsync(dialog.FolderName) is { } origin) repo.Text = origin;
        };

        var create = PrimaryButton("Create project", () => { });
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
            if (!Directory.Exists(path))
            {
                ShowProblem(problem, "That folder doesn't exist.");
                return;
            }

            _working = true;
            create.IsEnabled = false;
            try
            {
                var url = repo.Text.Trim();
                var origin = await GitTools.OriginOfAsync(path);
                if (url.Length == 0)
                {
                    url = origin ?? "";
                }
                else if (origin is not null && !GitTools.SameRepo(origin, url))
                {
                    ShowProblem(problem, "That folder is connected to a different repo than that URL.");
                    return;
                }

                if (url.Length == 0)
                {
                    progress.Text = "Creating a private repo on GitHub…";
                    progress.Visibility = Visibility.Visible;
                    var created = await GitTools.CreatePrivateRepoAsync(_name, path);
                    progress.Visibility = Visibility.Collapsed;
                    if (!created.Worked)
                    {
                        ShowProblem(problem, created.Value);
                        return;
                    }
                    url = created.Value;
                }

                if (GitTools.RepoUrlProblem(url) is { } urlProblem)
                {
                    repo.Text = url;
                    ShowProblem(problem, urlProblem);
                    return;
                }

                Created?.Invoke(new NewProject(_name, _engine, ProjectChoices.HasEngineVersion(_engine) ? _engineVersion : "", _gameType, path, url));
                Close();
            }
            finally
            {
                _working = false;
                create.IsEnabled = true;
            }
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
            ControlButton("Back", ShowProjectStep),
            create);
    }

    private void Close()
    {
        Visibility = Visibility.Collapsed;
        Closed?.Invoke();
    }

    private void Wizard_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_working) Close();
    }

    private void Backdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_working) Close();
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
