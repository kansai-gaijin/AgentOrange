using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>
/// A project's settings. Everything syncs except its folder, which is different on each
/// PC. Saves run one at a time and always start from the latest stored project, so a
/// slow repo check can't overwrite newer edits or bring back a deleted project. Each
/// operation remembers which project it's for, because the modal can be showing a
/// different one by the time a slow step finishes.
/// </summary>
public partial class ProjectSettingsModal : UserControl
{
    private readonly Dictionary<TextBox, string> _invalidFields = [];
    private LocalDatabase _database = null!;
    private string _projectId = "";
    private TextBlock _problem = ProblemText();
    private Task _saves = Task.CompletedTask;
    private bool _closing;
    private int _session;

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
        _session++;
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

    private Project? Find(string projectId) => _database.ListProjects().FirstOrDefault(project => project.Id == projectId);

    private Project? Current() => Find(_projectId);

    private void Build(Project project)
    {
        _problem = ProblemText();
        _problem.TextWrapping = TextWrapping.Wrap;

        var name = Field(project.Name);
        name.Width = 300;
        var projectId = project.Id;
        SaveOnLeave(name, project.Name, value =>
        {
            if (value.Length == 0) return Task.FromResult<string?>("A project needs a name.");
            Update(projectId, current => current with { Name = value });
            return Task.FromResult<string?>(null);
        });

        var version = Field(project.EngineVersion);
        version.Width = 300;
        SaveOnLeave(version, project.EngineVersion, value =>
        {
            Update(projectId, current => current with { EngineVersion = value });
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
        SaveOnLeave(repo, project.RepoUrl, value => SaveRepoAsync(projectId, value, folder));

        var browse = ActionButton("Browse");
        browse.Click += async (_, _) => await ChooseFolderAsync(projectId, folder);

        var engine = Choice(ProjectChoices.Engines, project.Engine, value =>
        {
            var hasVersion = ProjectChoices.HasEngineVersion(value);
            versionRow.Visibility = hasVersion ? Visibility.Visible : Visibility.Collapsed;
            if (!hasVersion) version.Text = "";
            Update(projectId, current => current with { Engine = value, EngineVersion = hasVersion ? current.EngineVersion : "" });
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
                    Row("Game type", null, Choice(ProjectChoices.GameTypes, project.GameType, value => Update(projectId, current => current with { GameType = value }))),
                    Row("Repo URL", null, repo),
                    Row("Folder on this PC", "Doesn't sync, because it's different on each PC.", folder, browse)),
                Section("Images"),
                Card(
                    ImageRow(projectId, "Thumbnail", "Shown on the project list.", ProjectImageKind.Thumbnail, 44, 66),
                    ImageRow(projectId, "Header", "Shown at the top of the project's page.", ProjectImageKind.Header, 124, 40)),
                Section("Danger zone"),
                DangerZone(projectId),
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

    private Border DangerZone(string projectId)
    {
        var confirmation = Field("");
        confirmation.Width = 220;
        AutomationProperties.SetName(confirmation, "Type the project name to confirm");

        var delete = new Button { Style = (Style)FindResource("DangerButton"), Content = "Delete project", IsEnabled = false };
        AutomationProperties.SetName(delete, "Delete project");
        confirmation.TextChanged += (_, _) => delete.IsEnabled = confirmation.Text.Trim() == Find(projectId)?.Name;
        delete.Click += async (_, _) =>
        {
            delete.IsEnabled = false;
            await _saves; // So a slow save can't bring the project back afterward.
            _database.DeleteProject(projectId);
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

    private Grid ImageRow(string projectId, string title, string description, ProjectImageKind kind, int previewWidth, int previewHeight)
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
            var image = ProjectImages.ToImage(_database.GetProjectImage(projectId, kind), previewWidth * 2);
            preview.Background = image is null ? (Brush)FindResource("ControlBackground") : new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            remove.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        }
        ShowCurrent();

        var choose = ActionButton("Choose");
        choose.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = $"Choose a {title.ToLowerInvariant()}", RestoreDirectory = true, Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.webp" };
            if (dialog.ShowDialog() != true) return;

            var session = _session;
            var file = dialog.FileName;
            QueueWork(async () =>
            {
                var bytes = await Task.Run(() => ProjectImages.Prepare(file, kind));
                if (bytes is null)
                {
                    if (session == _session) ShowProblem(_problem, "That file couldn't be used as an image.");
                    return;
                }

                if (Find(projectId) is null) return;
                _database.SetProjectImage(projectId, kind, bytes);
                if (session == _session)
                {
                    ClearGeneralProblem();
                    ShowCurrent();
                }
                Changed?.Invoke();
            });
        };

        remove.Click += (_, _) => QueueWork(() =>
        {
            if (Find(projectId) is not null)
            {
                _database.SetProjectImage(projectId, kind, null);
                ShowCurrent();
                Changed?.Invoke();
            }
            return Task.CompletedTask;
        });

        return Row(title, description, preview, choose, remove);
    }

    private async Task<string?> SaveRepoAsync(string projectId, string url, TextBox folderField)
    {
        if (url.Length == 0) return "A project needs a repo.";
        if (GitTools.RepoUrlProblem(url) is { } problem) return problem;

        // A folder that's really from another repo can't stay linked. Forgetting it, without
        // touching the files, lets a project move repos instead of getting stuck between the
        // two. A folder that's just missing right now, such as on an unplugged drive, stays.
        var session = _session;
        var linked = _database.GetProjectFolder(projectId);
        var unlink = linked is not null && await GitTools.FolderUsesOtherRepoAsync(linked, url);

        Update(projectId, current => current with { RepoUrl = url });
        if (!unlink) return null;

        _database.SetProjectFolder(projectId, "");
        Changed?.Invoke();
        if (session == _session)
        {
            folderField.Text = "";
            folderField.ToolTip = null;
            ShowProblem(_problem, "This project's folder used a different repo, so it's no longer linked. Its files weren't changed. Choose or clone the right folder.");
        }
        return null;
    }

    private async Task ChooseFolderAsync(string projectId, TextBox field)
    {
        var dialog = new OpenFolderDialog { Title = "Choose this project's folder" };
        var session0 = _session;
        var previous = field.Text;
        if (await GitTools.FolderExistsAsync(previous)) dialog.InitialDirectory = previous;
        if (session0 != _session || dialog.ShowDialog() != true) return;

        var session = _session;
        var chosen = dialog.FolderName;
        QueueWork(async () =>
        {
            if (Find(projectId) is not { } current) return;

            var problem = await GitTools.FolderProblemAsync(chosen, current.RepoUrl);
            if (problem is not null)
            {
                if (session == _session) ShowProblem(_problem, problem);
                return;
            }

            if (Find(projectId) is null) return;
            _database.SetProjectFolder(projectId, chosen);
            Changed?.Invoke();
            if (session != _session) return;
            ClearGeneralProblem();
            field.Text = chosen;
            field.ToolTip = chosen;
        });
    }

    private void QueueWork(Func<Task> work)
    {
        var earlier = _saves;
        _saves = RunAfter(earlier, work);
    }

    // One failed save can't be allowed to stop the ones after it, or Done.
    private async Task RunAfter(Task earlier, Func<Task> work)
    {
        await earlier;
        try
        {
            await work();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or SqliteException)
        {
            AppLog.Write($"Couldn't save a project setting: {error.Message}");
            ShowProblem(_problem, "That change couldn't be saved. Try it again.");
        }
    }

    private void Update(string projectId, Func<Project, Project> change)
    {
        if (Find(projectId) is not { } current) return;
        _database.SaveProject(change(current));
        ClearGeneralProblem();
        Changed?.Invoke();
    }

    // A field's own problem stays on screen until that field is fixed.
    private void ClearGeneralProblem()
    {
        if (_invalidFields.Count > 0) ShowProblem(_problem, _invalidFields.Values.First());
        else _problem.Visibility = Visibility.Collapsed;
    }

    private void SaveOnLeave(TextBox field, string initial, Func<string, Task<string?>> save)
    {
        var saved = initial;
        var session = _session;

        void Queue()
        {
            // The value is fixed now. It may have been edited again by the time this runs.
            var value = field.Text.Trim();
            QueueWork(async () =>
            {
                if (session != _session || Current() is null) return;

                if (value == saved)
                {
                    if (_invalidFields.Remove(field)) ClearGeneralProblem();
                    return;
                }

                // Removed first, so a notice from the save itself isn't cleared afterward.
                _invalidFields.Remove(field);
                var problem = await save(value);
                if (session != _session) return;

                if (problem is not null)
                {
                    _invalidFields[field] = problem;
                    ShowProblem(_problem, problem);
                    return;
                }

                saved = value;
                if (field.Text.Trim() == value) field.Text = value;
            });
        }

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

        var session = _session;
        Keyboard.ClearFocus(); // Saves a field that's still being edited.
        await _saves; // So whatever opens next sees the saved values.
        if (session != _session || Visibility != Visibility.Visible) return;

        // Closing would quietly lose what they typed, so the problem has to be fixed first.
        if (_invalidFields.Count > 0 && Current() is not null)
        {
            _closing = false;
            var (field, problem) = _invalidFields.First();
            ShowProblem(_problem, problem);
            field.Focus();
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
