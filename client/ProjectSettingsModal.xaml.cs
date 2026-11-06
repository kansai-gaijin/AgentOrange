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
/// slow repo check can't overwrite newer edits or bring back a deleted project. Each
/// operation remembers which project it's for, because the modal can be showing a
/// different one by the time a slow step finishes.
/// </summary>
public partial class ProjectSettingsModal : UserControl
{
    private readonly Dictionary<TextBox, string> _invalidFields = [];
    private readonly Dictionary<TextBox, string> _saved = [];
    private LocalDatabase _database = null!;
    private string _projectId = "";
    private TextBlock _problem = ProblemText();
    private Task _saves = Task.CompletedTask;
    private bool _closing;
    private bool _problemWhileClosing;
    private int _session;

    public ProjectSettingsModal()
    {
        InitializeComponent();
    }

    /// <summary>Something was saved, so anything showing the project needs refreshing.</summary>
    public event Action? Changed;

    public event Action? Closed;

    /// <summary>Whether a long operation is running for this project.</summary>
    public Func<string, bool> ProjectIsBusy { get; set; } = _ => false;

    public event Action? Deleted;

    public void Open(LocalDatabase database, Project project)
    {
        _database = database;
        _projectId = project.Id;
        _session++;
        _invalidFields.Clear();
        _saved.Clear();
        _saves = Task.CompletedTask;
        _closing = false;
        _problemWhileClosing = false;
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

        var repo = Field(project.RepoUrl);
        repo.Width = 300;
        var folder = Field(_database.GetProjectFolder(project.Id) ?? "");
        folder.Width = 140;
        folder.IsReadOnly = true;
        folder.ToolTip = folder.Text.Length > 0 ? folder.Text : null;
        SaveOnLeave(repo, project.RepoUrl, value => SaveRepo(projectId, value));

        var browse = ActionButton("Browse");
        browse.Click += async (_, _) => await ChooseFolderAsync(projectId, folder);

        // Only forgets where it is on this PC. The folder itself isn't touched.
        var disconnect = ActionButton("Disconnect");
        disconnect.Visibility = folder.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        folder.TextChanged += (_, _) => disconnect.Visibility = folder.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        disconnect.Click += (_, _) => QueueWork(() =>
        {
            _database.SetProjectFolder(projectId, "");
            folder.Text = "";
            folder.ToolTip = null;
            Changed?.Invoke();
            return Task.CompletedTask;
        });

        // Read from the project and can't be edited. The latest is read on closing.
        const string CheckedOnClosing = "Checked when this closes";
        var version = new TextBlock
        {
            Text = project.EngineVersion.Length > 0 ? project.EngineVersion : "Not found",
            FontSize = 13.5,
            Foreground = (Brush)FindResource("TextNormal"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        folder.TextChanged += (_, _) => version.Text = CheckedOnClosing;
        var engine = Choice(ProjectChoices.Engines, project.Engine, value =>
        {
            version.Text = CheckedOnClosing;
            QueueUpdate(projectId, current => current with { Engine = value });
        });

        var details = new StackPanel
        {
            Children =
            {
                Section("Project"),
                Card(
                    Row("Name", null, name),
                    Row("Engine", null, engine),
                    Row("Engine version", "Read from the project's files.", version),
                    Row("Game type", null, Choice(ProjectChoices.GameTypes, project.GameType, value => QueueUpdate(projectId, current => current with { GameType = value }))),
                    Row("Repo URL", null, repo),
                    Row("Folder on this PC", "Doesn't sync, because it's different on each PC.", folder, browse, disconnect)),
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
        delete.Click += (_, _) =>
        {
            // Creating a repo can finish after this, and nothing would be left to report it on.
            if (ProjectIsBusy(projectId))
            {
                ReportProblem("This project is busy. Delete it when that finishes.");
                return;
            }

            delete.IsEnabled = false;

            // Queued, so a slow save can't bring the project back afterward.
            QueueWork(() =>
            {
                _database.DeleteProject(projectId);
                _closing = true;
                Visibility = Visibility.Collapsed;
                Deleted?.Invoke();
                return Task.CompletedTask;
            });
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
            if (Dialogs.PickFile($"Choose a {title.ToLowerInvariant()}", "Images (*.png;*.jpg;*.jpeg;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.webp") is not { } file) return;

            var session = _session;
            QueueWork(async () =>
            {
                var bytes = await Task.Run(() => ProjectImages.Prepare(file, kind));
                if (bytes is null)
                {
                    if (session == _session) ReportProblem("That file couldn't be used as an image.");
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

    // A folder left on the old repo stays linked. Opening the project then offers to connect
    // it, because it may hold work that isn't pushed.
    private Task<string?> SaveRepo(string projectId, string url)
    {
        if (url.Length == 0) return Task.FromResult<string?>("A project needs a repo.");
        if (GitTools.RepoUrlProblem(url) is { } problem) return Task.FromResult<string?>(problem);
        if (_database.ListProjects().FirstOrDefault(other => other.Id != projectId && GitTools.SameRepo(other.RepoUrl, url)) is { } taken)
        {
            return Task.FromResult<string?>($"{taken.Name} already uses that repo. A repo can only be one project.");
        }

        Update(projectId, current => current with { RepoUrl = url });
        return Task.FromResult<string?>(null);
    }

    private async Task ChooseFolderAsync(string projectId, TextBox field)
    {
        var session = _session;
        var previous = field.Text;
        var startIn = await GitTools.FolderExistsAsync(previous) ? previous : null;
        if (session != _session || Dialogs.PickFolder("Choose this project's folder", startIn) is not { } chosen) return;

        QueueWork(async () =>
        {
            if (Find(projectId) is not { } current) return;

            await GitTools.AskAboutUnreadableSshSettingsAsync(chosen);
            var problem = await GitTools.FolderProblemAsync(chosen, current.RepoUrl);
            if (problem is null && await _database.ClaimFolderAsync(chosen, current) is { } other)
            {
                problem = $"{other.Name} already uses that folder, or one inside it. A folder can only be one project.";
            }
            if (problem is not null)
            {
                if (session == _session) ReportProblem(problem);
                return;
            }

            // Sync can have moved the project to another repo while this folder was being checked.
            var latest = Find(projectId);
            if (latest is null || latest.RepoUrl != current.RepoUrl)
            {
                _database.ReleaseFolder(chosen);
                if (latest is not null && session == _session) ReportProblem("This project's repo changed while that folder was being checked, so it wasn't linked.");
                return;
            }

            _database.SetProjectFolder(projectId, chosen);
            _database.ReleaseFolder(chosen);
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

    // One failed save can't be allowed to stop the ones after it, or Done, or the whole app.
    private async Task RunAfter(Task earlier, Func<Task> work)
    {
        await earlier;
        try
        {
            await work();
        }
        catch (Exception error)
        {
            AppLog.Write($"Couldn't save a project setting: {error.Message}");
            ReportProblem("That change couldn't be saved. Try it again.");
        }
    }

    // Done waits for work to finish, so this keeps the modal open long enough for a late problem to be read.
    private void ReportProblem(string problem)
    {
        ShowProblem(_problem, problem);
        _problemWhileClosing |= _closing;
    }

    private void QueueUpdate(string projectId, Func<Project, Project> change) => QueueWork(() =>
    {
        Update(projectId, change);
        return Task.CompletedTask;
    });

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
        _saved[field] = initial;
        var session = _session;

        void Queue()
        {
            // The value is fixed now. It may have been edited again by the time this runs.
            var value = field.Text.Trim();
            QueueWork(async () =>
            {
                if (session != _session || Current() is null) return;

                if (value == _saved[field])
                {
                    if (_invalidFields.Remove(field)) ClearGeneralProblem();
                    return;
                }

                // Removed first, so a notice from the save itself isn't cleared afterward.
                _invalidFields.Remove(field);
                string? problem;
                try
                {
                    problem = await save(value);
                }
                catch (Exception error)
                {
                    // Held as a field problem, so Done won't close and lose it. Leaving the field again retries.
                    AppLog.Write($"Couldn't save a project setting: {error.Message}");
                    problem = "That change couldn't be saved. Try it again.";
                }
                if (session != _session) return;

                if (problem is not null)
                {
                    _invalidFields[field] = problem;
                    ShowProblem(_problem, problem);
                    return;
                }

                _saved[field] = value;
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

        if (_problemWhileClosing)
        {
            _problemWhileClosing = false;
            _closing = false;
            return;
        }

        // Closing would quietly lose what they typed, so it's fixed or explicitly undone first.
        if (_invalidFields.Count > 0 && Current() is not null)
        {
            _closing = false;
            var (field, problem) = _invalidFields.First();
            ShowProblem(_problem, $"{problem} Press Esc to go back to what was saved.");
            field.Focus();
            return;
        }

        Visibility = Visibility.Collapsed;
        Closed?.Invoke();
    }

    private void Modal_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        foreach (var field in _invalidFields.Keys) field.Text = _saved[field];
        _invalidFields.Clear();
        Close();
    }

    private void Backdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
