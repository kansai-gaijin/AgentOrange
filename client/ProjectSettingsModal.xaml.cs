using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>A project's settings. Everything syncs except its folder, which is different on each PC.</summary>
public partial class ProjectSettingsModal : UserControl
{
    private LocalDatabase _database = null!;
    private Project _project = null!;
    private TextBlock _problem = ProblemText();
    private Task _pendingSave = Task.CompletedTask;

    public ProjectSettingsModal()
    {
        InitializeComponent();
    }

    /// <summary>Something was saved, so anything showing the project needs refreshing.</summary>
    public event Action? Changed;

    public event Action? Closed;

    public event Action? Deleted;

    public void Open(LocalDatabase database, Project project)
    {
        _database = database;
        _project = project;
        Visibility = Visibility.Visible;
        Build();
        Focus();
    }

    private void Build()
    {
        _problem = ProblemText();

        var name = Field(_project.Name);
        name.Width = 300;
        SaveOnLeave(name, _project.Name, value =>
        {
            if (value.Length == 0) return "A project needs a name.";
            Save(_project with { Name = value });
            return null;
        });

        var version = Field(_project.EngineVersion);
        version.Width = 300;
        SaveOnLeave(version, _project.EngineVersion, value =>
        {
            Save(_project with { EngineVersion = value });
            return null;
        });

        var repo = Field(_project.RepoUrl);
        repo.Width = 300;
        SaveOnLeaveAsync(repo, _project.RepoUrl, SaveRepoAsync);

        var folder = Field(_database.GetProjectFolder(_project.Id) ?? "");
        folder.Width = 230;
        folder.IsReadOnly = true;
        folder.ToolTip = folder.Text.Length > 0 ? folder.Text : null;
        var browse = ActionButton("Browse");
        browse.Click += async (_, _) => await ChooseFolderAsync(folder);

        var details = new StackPanel
        {
            Children =
            {
                Section("Project"),
                Card(
                    Row("Name", null, name),
                    Row("Engine", null, Choice(ProjectChoices.Engines, _project.Engine, value => Save(_project with { Engine = value }))),
                    Row("Engine version", null, version),
                    Row("Game type", null, Choice(ProjectChoices.GameTypes, _project.GameType, value => Save(_project with { GameType = value }))),
                    Row("Repo URL", null, repo),
                    Row("Folder on this PC", "Doesn't sync, because it's different on each PC.", folder, browse)),
                Section("Images"),
                Card(
                    ImageRow("Thumbnail", "Shown on the project list.", ProjectImageKind.Thumbnail, 44, 66),
                    ImageRow("Header", "Shown at the top of the project's page.", ProjectImageKind.Header, 124, 40)),
                _problem,
                Section("Danger zone"),
                DangerZone(),
            },
            Margin = new Thickness(16, 0, 16, 16),
        };

        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 440, Content = details };

        Card.Child = WizardShell(
            Close,
            "Project settings",
            _project.Name,
            null,
            scroller,
            PrimaryButton("Done", Close));
    }

    private Border DangerZone()
    {
        var confirmation = Field("");
        confirmation.Width = 220;
        AutomationProperties.SetName(confirmation, "Type the project name to confirm");

        var delete = new Button { Style = (Style)FindResource("DangerButton"), Content = "Delete project", IsEnabled = false };
        AutomationProperties.SetName(delete, "Delete project");
        confirmation.TextChanged += (_, _) => delete.IsEnabled = confirmation.Text.Trim() == _project.Name;
        delete.Click += (_, _) =>
        {
            _database.DeleteProject(_project.Id);
            Visibility = Visibility.Collapsed;
            Deleted?.Invoke();
        };

        var card = Card(Row(
            "Delete this project",
            $"Removes it from Agent Orange on every PC. Its folder and repo aren't touched. Type {_project.Name} to confirm.",
            confirmation,
            delete));
        card.BorderBrush = (Brush)FindResource("Danger");
        return card;
    }

    private Grid ImageRow(string title, string description, ProjectImageKind kind, int previewWidth, int previewHeight)
    {
        var preview = new Border
        {
            Width = previewWidth,
            Height = previewHeight,
            CornerRadius = new CornerRadius(4),
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(1),
        };

        var remove = ActionButton("Remove");

        void ShowCurrent()
        {
            var image = ProjectImages.ToImage(_database.GetProjectImage(_project.Id, kind));
            preview.Background = image is null ? (Brush)FindResource("ControlBackground") : new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            remove.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        }
        ShowCurrent();

        var choose = ActionButton("Choose");
        choose.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = $"Choose a {title.ToLowerInvariant()}", Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.webp" };
            if (dialog.ShowDialog() != true) return;

            if (ProjectImages.Prepare(dialog.FileName, kind) is not { } bytes)
            {
                ShowProblem(_problem, "That file couldn't be used as an image.");
                return;
            }

            _problem.Visibility = Visibility.Collapsed;
            _database.SetProjectImage(_project.Id, kind, bytes);
            ShowCurrent();
            Changed?.Invoke();
        };

        remove.Click += (_, _) =>
        {
            _database.SetProjectImage(_project.Id, kind, null);
            ShowCurrent();
            Changed?.Invoke();
        };

        return Row(title, description, preview, choose, remove);
    }

    private async Task<string?> SaveRepoAsync(string url)
    {
        if (url.Length == 0) return "A project needs a repo.";
        if (GitTools.RepoUrlProblem(url) is { } problem) return problem;

        // A folder from the old repo can't stay linked. Forgetting it, without touching the
        // files, lets a project move to another repo instead of getting stuck between the two.
        var folderIsStale = _database.GetProjectFolder(_project.Id) is { } folder
            && await GitTools.FolderProblemAsync(folder, url) is not null;

        Save(_project with { RepoUrl = url });

        if (folderIsStale)
        {
            _database.SetProjectFolder(_project.Id, "");
            Changed?.Invoke();
            Build();
            ShowProblem(_problem, "This project's folder used a different repo, so it's no longer linked. Its files weren't changed. Choose or clone the right folder.");
        }
        return null;
    }

    private async Task ChooseFolderAsync(TextBox field)
    {
        var dialog = new OpenFolderDialog { Title = "Choose this project's folder" };
        if (Directory.Exists(field.Text)) dialog.InitialDirectory = field.Text;
        if (dialog.ShowDialog() != true) return;

        if (await GitTools.FolderProblemAsync(dialog.FolderName, _project.RepoUrl) is { } problem)
        {
            ShowProblem(_problem, problem);
            return;
        }

        _problem.Visibility = Visibility.Collapsed;
        _database.SetProjectFolder(_project.Id, dialog.FolderName);
        field.Text = dialog.FolderName;
        field.ToolTip = dialog.FolderName;
        Changed?.Invoke();
    }

    private void Save(Project project)
    {
        _project = project;
        _problem.Visibility = Visibility.Collapsed;
        _database.SaveProject(project);
        Changed?.Invoke();
    }

    private void SaveOnLeave(TextBox field, string saved, Func<string, string?> save) =>
        SaveOnLeaveAsync(field, saved, value => Task.FromResult(save(value)));

    private void SaveOnLeaveAsync(TextBox field, string saved, Func<string, Task<string?>> save)
    {
        async Task Apply()
        {
            var value = field.Text.Trim();
            if (value == saved) return;

            if (await save(value) is { } problem)
            {
                ShowProblem(_problem, problem);
                field.Text = saved;
                return;
            }

            saved = value;
            field.Text = value;
        }

        field.LostKeyboardFocus += (_, _) => _pendingSave = Apply();
        field.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) _pendingSave = Apply();
        };
    }

    private async void Close()
    {
        Keyboard.ClearFocus(); // Saves a field that's still being edited.
        await _pendingSave; // So whatever opens next sees the saved values.
        Visibility = Visibility.Collapsed;
        Closed?.Invoke();
    }

    private void Modal_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void Backdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
