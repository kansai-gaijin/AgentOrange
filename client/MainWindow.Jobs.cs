using Microsoft.Data.Sqlite;
using System.Windows;
using System.Windows.Controls;

namespace AgentOrange;

/// <summary>The one long job this window runs at a time.</summary>
public partial class MainWindow
{
    private const string CreatingRepoText = "Creating a private repo on GitHub…";

    // Only one long operation runs at a time. It keeps going if the person leaves the page.
    private ProjectJob? _job;
    private TextBlock? _busyLabel;
    private TextBlock? _busyCardLabel;

    // Kept across jobs, as the next one can start before a look gets its turn.
    private readonly HashSet<string> _looksAfterJob = [];

    private bool IsBusy => _job is not null;

    private bool IsBusyWith(string projectId) => _job?.ProjectId == projectId;

    /// <summary>
    /// Null if one's already running, which is what stops a second click starting a second one.
    /// Disposing it ends it, so anything going wrong inside a using for it does too. Ending it
    /// twice, or after the next job has started, does nothing.
    /// </summary>
    private ProjectJob? StartJob(Project project, string text, CancellationTokenSource? cancel = null)
    {
        if (_job is not null) return null;

        var job = new ProjectJob(this, project.Id);
        _job = job;
        try
        {
            job.Say(text, cancel);
        }
        catch
        {
            job.End();
            throw;
        }
        return job;
    }

    // A job is working in its project's folder, so whatever wants to look at that waits until it's
    // done. False if nothing has to wait, because that project has no job running.
    private bool LookOnceJobEnds(string projectId)
    {
        if (!IsBusyWith(projectId)) return false;
        _looksAfterJob.Add(projectId);
        return true;
    }

    // Something about this project changed while its job ran. If its page is still open, that looks
    // again, and otherwise its folder is read the way any background read is. Left a moment, as what
    // ended the job often looks again itself, or starts the next job, whose end is then soon enough.
    private void LookAgainAfterJob(string projectId)
    {
        if (!_looksAfterJob.Contains(projectId) || IsBusy) return;
        _looksAfterJob.Remove(projectId);
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

    private sealed class ProjectJob(MainWindow window, string projectId) : IDisposable
    {
        public string ProjectId { get; } = projectId;

        public string Text { get; private set; } = "";

        public string? CardText { get; private set; } // Cards are narrow, so they get just the number.

        // Only for a job that stopping would lose something from.
        public CancellationTokenSource? Cancel { get; private set; }

        public bool DownloadIsInPlace { get; set; }

        private bool IsRunning => window._job == this;

        public void Say(string text, CancellationTokenSource? cancel = null)
        {
            if (!IsRunning) return;
            Text = text;
            CardText = null;
            Cancel = cancel ?? Cancel;
            window._pageProblem = null;
            if (window.UpdateOverlay.IsVisible && !window._downloading) window.ShowUpdateMessage();
            if (window._openProject?.Id == ProjectId) window.RenderProject(window._openProject);
            else window.RefreshProjects();
        }

        // Just the words, because drawing the page again would keep taking focus off Cancel.
        public void SayStage(string doing, string stage)
        {
            if (!IsRunning) return;
            var last = stage[(stage.LastIndexOf(' ') + 1)..];
            Text = $"{doing} {stage}";
            CardText = last.EndsWith('%') ? $"{doing} {last}" : doing;
            if (window._busyLabel is not null) window._busyLabel.Text = Text;
            if (window._busyCardLabel is not null)
            {
                window._busyCardLabel.Text = CardText;
                window._busyCardLabel.ToolTip = Text;
            }
        }

        public void End()
        {
            if (!IsRunning) return;
            window._job = null;
            Cancel = null;
            if (window.UpdateOverlay.IsVisible && !window._downloading) window.ShowUpdateMessage();
            foreach (var waiting in window._looksAfterJob) _ = window.Dispatcher.InvokeAsync(() => window.LookAgainAfterJob(waiting), System.Windows.Threading.DispatcherPriority.Background);
            if (window._openProject is null) window.RefreshProjects();
            else window.RenderProject(window._openProject);
        }

        public void Dispose() => End();

        // For when the database has gone and something still has to be said. Drawing the page needs
        // the database, and happens again once it's back, so that mustn't stop it being said.
        public void EndWithoutDatabase()
        {
            window._databaseWasLost = true;
            try
            {
                End();
            }
            catch (SqliteException)
            {
            }
        }

        // What a job came to is only said once it's over, in one of these orders.

        public void EndThenShow(string problem)
        {
            End();
            window.ShowPageProblem(ProjectId, problem);
        }

        public Task EndThenLookAgainAsync()
        {
            End();
            return window.RecheckIfStillOpenAsync(ProjectId);
        }

        public Task EndThenLookAgainAsync(string problem)
        {
            End();
            return window.RecheckThenShowAsync(ProjectId, problem);
        }

        public Task EndThenRefreshAsync()
        {
            End();
            return window.RefreshThenRecheckAsync(ProjectId);
        }

        public async Task EndWithResultAsync(GitResult result)
        {
            if (result.Worked)
            {
                await EndThenRefreshAsync();
                return;
            }
            EndThenShow(result.Value);
        }
    }
}
