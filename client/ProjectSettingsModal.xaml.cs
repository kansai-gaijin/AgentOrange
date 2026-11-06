using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>
/// A project's settings. Everything syncs except its folder, which is different on each
/// PC. Saves run one at a time and always start from the latest stored project, so a
/// slow repo check can't overwrite newer edits or bring back a deleted project.
/// </summary>
public partial class ProjectSettingsModal : UserControl
{
    private readonly HashSet<TextBox> _invalidFields = [];
    private LocalDatabase _database = null!;
    private string _projectId = "";
    private TextBlock _problem = ProblemText();
    private Task _saves = Task.CompletedTask;
    private bool _closing;

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
        _projectId = project.Id;
        _invalidFields.Clear();
        _saves = Task.CompletedTask;
        _closing = false;
        Visibility = Visibility.Visible;
        Build(project);
        Focus();
    }

    /// <summary>The project may have been deleted on another PC.</summary>
    public void ProjectsChanged()
    {
        if (Visibility == Visibility.Visible && !_closing && Current() is null)
        {
            Visibility = Visibility.Collapsed;
            Closed?.Invoke();
        }
    }

    private Project? Current() => _database.ListProjects().FirstOrDefault(project => project.Id == _projectId);

    private void Build(Project project)
    {
        _problem = ProblemText();
        _problem.TextWrapping = TextWrapping.Wrap;

        var name = Field(project.Name);
        name.Width = 300;
        SaveOnLeave(name, project.Name, value =>
        {
            if (value.Length == 0) return Task.FromResult<string?>("A project needs a name.");
            Update(current => current with { Name = value });
            return Task.FromResult<string?>(null);
        });

        var version = Field(project.EngineVersion);
        version.Width = 300;
        SaveOnLeave(version, project.EngineVersion, value =>
        {
            Update(current => current with { EngineVersion = value });
            return Task.FromResult<string?>(null);
        });

        var versionRow = Row("Engine version", null, version);
        versionRow.Visibility = ProjectChoices.HasEngineVersion(project.Engine) ? Visibility.Visible : Visibility.Collapsed;

        var repo = Field(project.RepoUrl);
        repo.Width = 300;
        var folder = Field(_database.GetProjectFolder(project.Id) ?? "");
        folder.Width = 230;
        folder.IsReadOnly = true;
        folder.ToolTip = folder.Text.Length > 0 ? folder.Text : null;
        SaveOnLeave(repo, project.RepoUrl, value => SaveRepoAsync(value, folder));

        var browse = ActionButton("Browse");
        browse.Click += async (_, _) => await ChooseFolderAsync(folder);

        var engine = Choice(ProjectChoices.Engines, project.Engine, value =>
        {
            var hasVersion = ProjectChoices.HasEngineVersion(value);
            versionRow.Visibility = hasVersion ? Visibility.Visible : Visibility.Collapsed;
            if (!hasVersion) version.Text = "";
            Update(current => current with { Engine = value, EngineVersion = hasVersion ? current.EngineVersion : "" });
        });

        var details = new StackPanel
        {
            Children =
            {
                Section("Project"),
                Card(
                    Row("Name", null, name),
                    Row("Engine", null, engine),
                    versionRow,
                    Row("Game type", null, Choice(ProjectChoices.GameTypes, project.GameType, value => Update(current => current with { GameType = value }))),
                    Row("Repo URL", null, repo),
                    Row("Folder on this PC", "Doesn't sync, because it's different on each PC.", folder, browse)),
                Section("Images"),
                Card(
                    ImageRow("Thumbnail", "Shown on the project list.", ProjectImageKind.Thumbnail, 44, 66),
                    ImageRow("Header", "Shown at the top of the project's page.", ProjectImageKind.Header, 124, 40)),
                Section("Danger zone"),
                DangerZone(),
            },
            Margin = new Thickness(16, 0, 16, 16),
        };

        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 440, Content = details };

        // Outside the scrolling part, so a problem is always in view when Done refuses to close.
        _problem.Margin = new Thickness(24, 8, 24, 10);
        DockPanel.SetDock(_problem, Dock.Bottom);
        var body = new DockPanel { Children = { _problem, scroller } };

        Card.Child = WizardShell(
            Close,
            "Project settings",
            "Changes save as you make them.",
            null,
            body,
            PrimaryButton("Done", Close));
    }

    private Border DangerZone()
    {
        var confirmation = Field("");
        confirmation.Width = 220;
        AutomationProperties.SetName(confirmation, "Type the project name to confirm");

        var delete = new Button { Style = (Style)FindResource("DangerButton"), Content = "Delete project", IsEnabled = false };
        AutomationProperties.SetName(delete, "Delete project");
        confirmation.TextChanged += (_, _) => delete.IsEnabled = confirmation.Text.Trim() == Current()?.Name;
        delete.Click += async (_, _) =>
        {
            delete.IsEnabled = false;
            await _saves; // So a slow save can't bring the project back afterward.
            _database.DeleteProject(_projectId);
            _closing = true;
            Visibility = Visibility.Collapsed;
            Deleted?.Invoke();
        };

        var card = Card(Row(
            "Delete this project",
            "Removes it from Agent Orange on every PC. Its folder and repo aren't touched. Type the project's name to confirm.",
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
            var image = ProjectImages.ToImage(_database.GetProjectImage(_projectId, kind));
            preview.Background = image is null ? (Brush)FindResource("ControlBackground") : new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            remove.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        }
        ShowCurrent();

        var choose = ActionButton("Choose");
        choose.Click += async (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = $"Choose a {title.ToLowerInvariant()}", Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.webp" };
            if (dialog.ShowDialog() != true) return;

            if (ProjectImages.Prepare(dialog.FileName, kind) is not { } bytes)
            {
                ShowProblem(_problem, "That file couldn't be used as an image.");
                return;
            }

            _problem.Visibility = Visibility.Collapsed;
            await _saves;
            if (Current() is null) return;
            _database.SetProjectImage(_projectId, kind, bytes);
            ShowCurrent();
            Changed?.Invoke();
        };

        remove.Click += async (_, _) =>
        {
            await _saves;
            if (Current() is null) return;
            _database.SetProjectImage(_projectId, kind, null);
            ShowCurrent();
            Changed?.Invoke();
        };

        return Row(title, description, preview, choose, remove);
    }

    private async Task<string?> SaveRepoAsync(string url, TextBox folderField)
    {
        if (url.Length == 0) return "A project needs a repo.";
        if (GitTools.RepoUrlProblem(url) is { } problem) return problem;

        // A folder that's really from another repo can't stay linked. Forgetting it, without
        // touching the files, lets a project move repos instead of getting stuck between the
        // two. A folder that's just missing right now, such as on an unplugged drive, stays.
        var linked = _database.GetProjectFolder(_projectId);
        var unlink = linked is not null
            && await Task.Run(() => Directory.Exists(linked))
            && await GitTools.FolderProblemAsync(linked, url) is not null;

        Update(current => current with { RepoUrl = url });
        if (!unlink) return null;

        _database.SetProjectFolder(_projectId, "");
        folderField.Text = "";
        folderField.ToolTip = null;
        Changed?.Invoke();
        ShowProblem(_problem, "This project's folder used a different repo, so it's no longer linked. Its files weren't changed. Choose or clone the right folder.");
        return null;
    }

    private async Task ChooseFolderAsync(TextBox field)
    {
        var dialog = new OpenFolderDialog { Title = "Choose this project's folder" };
        if (Directory.Exists(field.Text)) dialog.InitialDirectory = field.Text;
        if (dialog.ShowDialog() != true) return;

        await _saves;
        if (Current() is not { } current) return;

        if (await GitTools.FolderProblemAsync(dialog.FolderName, current.RepoUrl) is { } problem)
        {
            ShowProblem(_problem, problem);
            return;
        }

        _problem.Visibility = Visibility.Collapsed;
        _database.SetProjectFolder(_projectId, dialog.FolderName);
        field.Text = dialog.FolderName;
        field.ToolTip = dialog.FolderName;
        Changed?.Invoke();
    }

    private void Update(Func<Project, Project> change)
    {
        if (Current() is not { } current) return;
        _problem.Visibility = Visibility.Collapsed;
        _database.SaveProject(change(current));
        Changed?.Invoke();
    }

    private void SaveOnLeave(TextBox field, string initial, Func<string, Task<string?>> save)
    {
        var saved = initial;
        var openedFor = _projectId;

        async Task Apply(Task earlier)
        {
            await earlier;
            if (_projectId != openedFor || Current() is null) return;

            var value = field.Text.Trim();
            if (value == saved)
            {
                _invalidFields.Remove(field);
                return;
            }

            if (await save(value) is { } problem)
            {
                _invalidFields.Add(field);
                ShowProblem(_problem, problem);
                return;
            }

            _invalidFields.Remove(field);
            saved = value;
            field.Text = value;
        }

        void Queue() => _saves = Apply(_saves);

        field.LostKeyboardFocus += (_, _) => Queue();
        field.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Queue();
        };
    }

    private async void Close()
    {
        if (_closing) return;
        _closing = true;

        Keyboard.ClearFocus(); // Saves a field that's still being edited.
        await _saves; // So whatever opens next sees the saved values.

        // Closing would quietly lose what they typed, so the problem has to be fixed first.
        if (_invalidFields.Count > 0)
        {
            _closing = false;
            return;
        }

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
