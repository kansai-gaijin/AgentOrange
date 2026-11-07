using System.Windows;
using System.Windows.Controls;

namespace AgentOrange;

/// <summary>The one long job this window runs at a time.</summary>
public partial class MainWindow
{
    // Only one long operation runs at a time. It keeps going if the person leaves the page.
    private string? _busyProjectId;
    private const string CreatingRepoText = "Creating a private repo on GitHub…";
    private string? _busyText;
    private string? _busyCardText; // Cards are narrow, so they get just the number.
    private TextBlock? _busyLabel;
    private TextBlock? _busyCardLabel;
    private CancellationTokenSource? _cloneCancel;

    private readonly HashSet<string> _recheckAfterBusy = [];
    private int _busyJob; // Goes up each time a job starts, and stays through its changes of text.

    private void StartBusy(Project project, string text)
    {
        if (_busyProjectId is null) _busyJob++;
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
        foreach (var waiting in _recheckAfterBusy) _ = Dispatcher.InvokeAsync(() => LookAgainAfterBusy(waiting), System.Windows.Threading.DispatcherPriority.Background);
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
        if (!_recheckAfterBusy.Contains(projectId) || _busyProjectId is not null) return;
        _recheckAfterBusy.Remove(projectId);
        // Closing settings looks at the page, or reads the folder of the project they're for, anyway.
        var settingsOpen = ProjectSettings.Visibility == Visibility.Visible;
        if (_openProject?.Id != projectId)
        {
            if (!(settingsOpen && _settingsProjectId == projectId)) _ = TellIfDatabaseFailsAsync(ReadEngineVersionAsync(projectId));
            return;
        }
        if (settingsOpen) return;

        // What the job just said about itself stays through that look.
        KeepProblemThroughReopen(projectId);
        _ = TellIfDatabaseFailsAsync(RecheckIfStillOpenAsync(projectId));
    }

    // The app carries on through a database error, as the database can be on a drive that's
    // dropped. Whatever this page was busy with has stopped, and the page has to say so. Anything
    // else unexpected closes the app, so its busy state ending first only matters for the moment
    // that's being reported. Only the job this work started is ended, not one started after it.
    private async Task EndBusyIfDatabaseFailsAsync(string projectId, Func<Task> work)
    {
        var before = _busyJob;
        try
        {
            await work();
        }
        catch (Exception)
        {
            if (_busyProjectId == projectId && _busyJob == before + 1) EndBusy();
            throw;
        }
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
}
