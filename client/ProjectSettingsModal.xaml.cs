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
        Card.IsEnabled = true;
        Visibility = Visibility.Visible;
        Build(project);
        Focus();
    }

    /// <summary>The project may have been deleted on another PC, or a clone may have just linked its folder.</summary>
    public void ProjectsChanged()
    {
        if (Visibility != Visibility.Visible || _closing) return;
        if (Current() is null)
        {
            Visibility = Visibility.Collapsed;
            Closed?.Invoke();
            return;
        }

        var linked = _database.GetProjectFolder(_projectId) ?? "";
        if (_folderField is not null && _folderField.Text != linked)
        {
            _folderField.Text = linked;
            _folderField.ToolTip = linked.Length > 0 ? linked : null;
        }

        // Another PC may have changed these. Anything being edited, or not yet saved, stays as it is.
        if (Current() is not { } current) return;
        RefreshIfUntouched(_nameField, current.Name);
        RefreshIfUntouched(_repoField, current.RepoUrl);

        // Waits for anything of this PC's that's still saving, which would otherwise look undone.
        if (_saves.IsCompleted)
        {
            foreach (var refresh in _refreshes) refresh(current);
        }
    }

    private void RefreshIfUntouched(TextBox? field, string latest)
    {
        if (field is null || field.IsKeyboardFocusWithin || _invalidFields.ContainsKey(field)) return;
        if (!_saved.TryGetValue(field, out var saved) || field.Text.Trim() != saved || saved == latest) return;
        _saved[field] = latest;
        field.Text = latest;
    }

    private TextBox? _folderField;
    private readonly List<Action<Project>> _refreshes = [];
    private bool _refreshing; // So showing another PC's change doesn't save it again as this one's.
    private Action<Project>? _showNameToType;
    private TextBox? _nameField;
    private TextBox? _repoField;
    private Button? _doneButton;

    private Project? Find(string projectId) => _database.ListProjects().FirstOrDefault(project => project.Id == projectId);

    private Project? Current() => Find(_projectId);

    private void Build(Project project)
    {
        _refreshes.Clear();
        _problem = ProblemText();
        _problem.TextWrapping = TextWrapping.Wrap;

        var name = Field(project.Name);
        name.Width = 300;
        name.MaxLength = ProjectChoices.MaxNameLength;
        _nameField = name;
        var projectId = project.Id;
        SaveOnLeave(name, project.Name, value =>
        {
            if (value.Length == 0) return Task.FromResult<string?>("A project needs a name.");
            Update(projectId, current => current with { Name = value });
            return Task.FromResult<string?>(null);
        });

        var repo = Field(project.RepoUrl);
        repo.Width = 300;
        _repoField = repo;
        var folder = Field(_database.GetProjectFolder(project.Id) ?? "");
        folder.Width = 140;
        folder.IsReadOnly = true;
        folder.ToolTip = folder.Text.Length > 0 ? folder.Text : null;
        _folderField = folder;
        SaveOnLeave(repo, project.RepoUrl, value => SaveRepo(projectId, value));

        var browse = ActionButton("Browse");
        browse.Click += async (_, _) =>
        {
            await ChooseFolderAsync(projectId, folder);

            // Checking a folder can take a while on a slow drive, with nothing else to show for it.
            browse.Content = "Checking…";
            await _saves;
            browse.Content = "Browse";
        };

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
        const string DoesNotApply = "Doesn't apply";
        var chosenEngine = project.Engine;
        var shownVersion = project.EngineVersion;
        string VersionText(Project of) => !EngineVersions.CanRead(of.Engine) ? DoesNotApply : of.EngineVersion.Length > 0 ? of.EngineVersion : folder.Text.Length > 0 ? "Not found" : "Needs a folder";
        var version = new TextBlock
        {
            Text = VersionText(project),
            FontSize = 13.5,
            Foreground = (Brush)FindResource("TextNormal"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        folder.TextChanged += (_, _) =>
        {
            // With no folder left there's nothing to read, so what's known stays.
            if (folder.Text.Length > 0 && EngineVersions.CanRead(chosenEngine)) version.Text = CheckedOnClosing;
        };
        var engine = Choice(ProjectChoices.Engines, project.Engine, value =>
        {
            if (_refreshing) return;
            chosenEngine = value;
            shownVersion = ""; // The old engine's, so it isn't taken for a change arriving from elsewhere.
            version.Text = !EngineVersions.CanRead(value) ? DoesNotApply : folder.Text.Length > 0 ? CheckedOnClosing : "Needs a folder";
            QueueUpdate(projectId, current => current with { Engine = value });
        });
        var gameType = Choice(ProjectChoices.GameTypes, project.GameType, value =>
        {
            if (!_refreshing) QueueUpdate(projectId, current => current with { GameType = value });
        });

        _refreshes.Add(latest =>
        {
            _refreshing = true;
            if (!engine.IsDropDownOpen) engine.SelectedItem = latest.Engine;
            if (!gameType.IsDropDownOpen) gameType.SelectedItem = latest.GameType;
            _refreshing = false;

            if (latest.Engine == chosenEngine && latest.EngineVersion == shownVersion) return;
            chosenEngine = latest.Engine;
            shownVersion = latest.EngineVersion;
            version.Text = VersionText(latest);
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
                    Row("Game type", null, gameType),
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

        _doneButton = PrimaryButton("Done", Close);
        Card.Child = WizardShell(
            Close,
            "Project settings",
            "Changes save as you make them.",
            null,
            body,
            _doneButton);
    }

    private Border DangerZone(string projectId)
    {
        var confirmation = Field("");
        confirmation.Width = 200;
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

            // Another PC may have renamed it since this was typed.
            if (confirmation.Text.Trim() != Find(projectId)?.Name)
            {
                delete.IsEnabled = false;
                return;
            }

            delete.IsEnabled = false;

            // Queued, so a slow save can't bring the project back afterward.
            QueueWork(() =>
            {
                try
                {
                    _database.DeleteProject(projectId);
                }
                catch
                {
                    delete.IsEnabled = true; // It says to try again, and that has to be possible.
                    throw;
                }
                _closing = true;
                Visibility = Visibility.Collapsed;
                Deleted?.Invoke();
                return Task.CompletedTask;
            });
        };

        // Right here, because the Name field has scrolled away by now, and may change as things sync.
        var nameToType = new TextBlock { FontSize = 12.5, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(0, 0, 0, 4), MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis };
        void ShowNameToType(Project of)
        {
            nameToType.Text = $"Type {of.Name} to confirm";
            nameToType.ToolTip = of.Name;
        }
        if (Find(projectId) is { } named) ShowNameToType(named);
        _refreshes.Add(ShowNameToType);
        _showNameToType = ShowNameToType;

        var card = Card(Row(
            "Delete this project",
            "Removes it from Agent Orange on every PC. Its folder and repo aren't touched.",
            new StackPanel { Children = { nameToType, confirmation } },
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

        byte[]? shown = null;
        void ShowCurrent()
        {
            var bytes = _database.GetProjectImage(projectId, kind);
            shown = bytes;
            var image = ProjectImages.ToImage(bytes, previewWidth * 2);
            preview.Background = image is null ? (Brush)FindResource("ControlBackground") : new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            remove.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        }
        ShowCurrent();

        // Decoding is slow and this runs for every batch of a sync, so only for a real change.
        _refreshes.Add(_ =>
        {
            var latest = _database.GetProjectImage(projectId, kind);
            if ((latest ?? []).AsSpan().SequenceEqual(shown ?? [])) return;
            ShowCurrent();
        });

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
    private async Task<string?> SaveRepo(string projectId, string url)
    {
        if (url.Length == 0) return "A project needs a repo.";
        if (GitTools.RepoUrlProblem(url) is { } problem) return problem;

        // GitHub still answers to a repo's old names, so the address isn't enough to tell them apart.
        var (currentUrl, holder, underAnotherName) = await GitTools.WhoHasRepoAsync(url, projectId);
        if (holder is not null)
        {
            return underAnotherName
                ? $"{holder} already uses that repo, under another name it's had on GitHub. A repo can only be one project."
                : $"{holder} already uses that repo. A repo can only be one project.";
        }

        // Another window can have got to it during all of that.
        if (Find(projectId) is not { } project) return null;
        if (await _database.ClaimRepoAsync(currentUrl, project) is { } other) return LocalDatabase.RefusalFor(other, $"{other.Name} already uses that repo. A repo can only be one project.");
        try
        {
            Update(projectId, current => current with { RepoUrl = currentUrl });
        }
        finally
        {
            _database.ReleaseRepo(currentUrl);
        }
        return null;
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

            var allowedJustNow = await GitTools.AskAboutUnreadableSshSettingsAsync(chosen);
            var problem = await GitTools.FolderProblemAsync(chosen, current.RepoUrl);
            if (problem is null && await _database.ClaimFolderAsync(chosen, current) is { } other)
            {
                problem = LocalDatabase.RefusalFor(other, $"{other.Name} already uses that folder, or one that overlaps it. Project folders can't overlap.");
            }
            if (allowedJustNow && problem is not null) GitTools.ForgetAllowedFolderSettings(chosen);
            if (problem is not null)
            {
                if (session == _session) ReportProblem(problem);
                return;
            }

            try
            {
                // Sync can have moved the project to another repo while this folder was being checked.
                var latest = Find(projectId);
                if (latest is null || !GitTools.SameRepo(latest.RepoUrl, current.RepoUrl))
                {
                    if (allowedJustNow) GitTools.ForgetAllowedFolderSettings(chosen);
                    if (latest is not null && session == _session) ReportProblem("This project's repo changed while that folder was being checked, so it wasn't linked.");
                    return;
                }

                _database.SetProjectFolder(projectId, chosen);
                await GitTools.PickUpUnfinishedNoteAsync(chosen);
            }
            finally
            {
                _database.ReleaseFolder(chosen);
            }
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
        _database.UpdateProject(projectId, change);
        if (Find(projectId) is { } saved) _showNameToType?.Invoke(saved);
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
                    if (Current() is { } latest)
                    {
                        foreach (var refresh in _refreshes) refresh(latest);
                    }
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

        // Nothing can be changed from here, or it could be queued after what this is waiting for
        // and only go wrong once there's nothing left to show it on.
        Card.IsEnabled = false;
        if (_doneButton is not null) _doneButton.Content = "Saving…";
        await _saves; // So whatever opens next sees the saved values.
        if (session != _session || Visibility != Visibility.Visible) return;

        Card.IsEnabled = true;
        if (_doneButton is not null) _doneButton.Content = "Done";

        if (_problemWhileClosing)
        {
            _problemWhileClosing = false;
            _closing = false;
            Focus(); // Being disabled took it away, and Esc needs it to be inside here.
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
