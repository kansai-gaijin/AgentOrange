using Microsoft.Data.Sqlite;
using System.Windows;
using System.Windows.Media.Effects;

namespace AgentOrange;

/// <summary>
/// Each project's page and what it knows. Checks and clones finish whenever they finish, so
/// everything that continues after one re-reads the project and compares _navigation first, in
/// case the person has moved on or the project changed.
/// </summary>
public partial class MainWindow
{
    private Project? _openProject;
    private PageCheck? _pageCheck; // Null until its check is done.
    private static readonly TimeSpan AccessCheckInterval = TimeSpan.FromMinutes(5);
    private DateTimeOffset _lastAccessCheck;
    private (bool Unfinished, bool Held) _shownDownloadState;
    private bool _databaseWasLost;
    private bool _openPageNeedsCheck; // Kept until nothing's busy, as a long clone can be running.
    private FolderGitSettings? _finishSettingsToAllow;
    private (string? Folder, string? RepoUrl) _settingsOpenedWith;
    private string? _scrolledToProblem;
    private string? _pageProblem;
    private bool _confirmingNewRepo;
    private string? _replacementRepoName;
    private int _navigation;

    // A long operation can finish after they've left its project, and what happened still has to reach them.
    private readonly Dictionary<string, string> _heldProblems = [];
    private string? _settingsProjectId;
    private readonly Dictionary<string, int> _engineReads = [];

    // A project's page. Opening one first checks this PC is allowed to work on its repo.

    private async Task OpenProjectAsync(Project project)
    {
        try
        {
            await CheckAndShowProjectAsync(project);
        }
        catch (SqliteException)
        {
            _databaseWasLost = true; // So the check runs again, as nothing else will finish it.
            ProjectPage.Children.Clear();
            var note = CenteredNote("The app's database can't be reached right now. This page comes back once it can be.");
            note.Margin = new Thickness(40, 40, 40, 0);
            ProjectPage.Children.Add(note);
            throw;
        }
    }

    private async Task CheckAndShowProjectAsync(Project project)
    {
        // Its long job is working in its folder, and checking that folder can change its remote.
        // So it's checked once the job is done, and the page meanwhile shows what's running.
        if (LookOnceJobEnds(project.Id))
        {
            if (_openProject?.Id == project.Id && GitTools.SameRepo(project.RepoUrl, _openProject.RepoUrl))
            {
                _openProject = project;
                RenderProject(project);
                return;
            }
        }

        // This is the look that was waiting.
        else _looksAfterJob.Remove(project.Id);

        var navigation = ++_navigation;
        _lastAccessCheck = DateTimeOffset.UtcNow;
        _openProject = project;
        _pageCheck = null;
        _finishSettingsToAllow = null;
        _pageProblem = _heldProblems.Remove(project.Id, out var held) ? held : null;
        _confirmingNewRepo = false;
        ProjectSelectView.Visibility = Visibility.Collapsed;
        ProjectView.Visibility = Visibility.Visible;
        RenderProject(project);
        if (IsBusyWith(project.Id)) return;

        var folder = _database.GetProjectFolder(project.Id);
        var runnable = folder is null ? FolderGitSettings.None : await GitTools.FolderRunnableSettingsAsync(folder);
        var useFolderSettings = runnable is not null && (folder is null || GitTools.AreAllowed(folder, runnable));
        var check = await GitTools.CheckRepoAccessAsync(project.RepoUrl, folder, useFolderSettings);

        var folderExists = folder is not null && await GitTools.FolderExistsAsync(folder);
        var folderShown = folderExists && check.ShowsFolder;
        var usable = CanWorkIn(project, check);
        var folderProblem = folderShown ? await GitTools.FolderProblemDetailAsync(folder!, project.RepoUrl) : null;

        // It may have been connected while offline, when nothing could say the repo had files.
        var askAboutRepo = usable && check.GitHubSeesRepo;
        var lacksFiles = askAboutRepo && folderExists && folderProblem is null
            && await GitTools.FolderHasNoCommitsAsync(folder!) == true && await GitTools.RepoHasContentAsync(project.RepoUrl) == true;

        // A folder with its own commits may not be this project at all, however it was connected.
        // This keeps saying so until the two share a commit, which is then remembered for that
        // folder and repo, as this asks GitHub each time until it does. Along with GitHub's ID for the
        // repo, as one deleted and made again under the same name is a different repo.
        var repoKey = check.RepoId is { } repoId ? $"{GitTools.RepoKey(project.RepoUrl)}|{repoId}" : GitTools.RepoKey(project.RepoUrl);
        var history = askAboutRepo && folderExists && folderProblem is null && !lacksFiles && !_database.FolderSharesRepo(folder!, repoKey)
            ? await GitTools.CompareFolderHistoryAsync(folder!, project.RepoUrl)
            : FolderHistory.CantTell;
        if (history == FolderHistory.SharesCommits) _database.RememberFolderSharesRepo(folder!, repoKey);
        var mayNotMatch = history == FolderHistory.SharesNothing;
        var folderEngine = folderShown && usable && folderProblem is null ? await EngineVersions.DetectAsync(folder!) : null;
        var pushesTo = folderShown && usable && folderProblem is null ? await GitTools.PushesElsewhereAsync(folder!, project.RepoUrl) : null;
        if (folderExists && check.IsDenied) _database.ForgetFolderSharesRepo(folder!);

        // Asked before the latest copy of the project is read below, as this can take a while.
        var renamed = check.GitHubSeesRepo
            ? await GitTools.RenamedRepoUrlAsync(project.RepoUrl)
            : null;

        if (navigation != _navigation) return; // They moved on, or something newer started.

        var latest = FindProject(project.Id);
        if (latest is null)
        {
            BackToProjects();
            return;
        }
        if (!GitTools.SameRepo(latest.RepoUrl, project.RepoUrl))
        {
            KeepProblemThroughReopen(project.Id);
            await OpenProjectAsync(latest);
            return;
        }

        // Renamed or moved on GitHub, which follows that for a while but won't forever. This is
        // the same repo, so the project and its folder just move to the new address.
        var renamedHeldBy = renamed is null ? null : GitTools.ProjectUsingRepo(renamed);
        var holdingRenamed = false;
        if (renamed is not null && renamedHeldBy is null)
        {
            var renameClaim = await _database.ClaimRepoAsync(renamed, latest);
            holdingRenamed = renameClaim is null;
            if (renameClaim is not null && !LocalDatabase.MeansNoTurn(renameClaim)) renamedHeldBy = renameClaim.Name;
        }
        if (holdingRenamed && (navigation != _navigation || FindProject(project.Id) is not { } stillOnIt || !GitTools.SameRepo(stillOnIt.RepoUrl, project.RepoUrl)))
        {
            _database.ReleaseRepo(renamed!);
            if (navigation != _navigation) return;
            if (FindProject(project.Id) is { } changed) await OpenProjectAsync(changed);
            else BackToProjects();
            return;
        }
        if (navigation != _navigation) return;
        if (holdingRenamed)
        {
            var wasVerified = _database.GetSetting(VerifiedSetting(project.Id)) == GitTools.RepoKey(latest.RepoUrl);
            var wasCreatedHere = _database.GetSetting(CreatedHereSetting(project.Id)) == GitTools.RepoKey(latest.RepoUrl);
            try
            {
                _database.UpdateProject(project.Id, current => GitTools.SameRepo(current.RepoUrl, project.RepoUrl) ? current with { RepoUrl = renamed! } : current);
            }
            finally
            {
                _database.ReleaseRepo(renamed!);
            }
            if (check.IsDenied)
            {
                _database.SetSetting(VerifiedSetting(project.Id), "");
                _database.SetSetting(CreatedHereSetting(project.Id), "");
            }
            else
            {
                if (check.Access == RepoAccess.Allowed || wasVerified) _database.SetSetting(VerifiedSetting(project.Id), GitTools.RepoKey(renamed!));
                if (check.Access == RepoAccess.Allowed) _database.SetSetting(CreatedHereSetting(project.Id), "");
                else if (wasCreatedHere) _database.SetSetting(CreatedHereSetting(project.Id), GitTools.RepoKey(renamed!));
            }

            // Checking it against the new name moves it there if it's on any older name, and leaves one on any other repo as it is.
            if (folderExists) await GitTools.FolderProblemAsync(folder!, renamed!);
            _ = _sync.SyncNowAsync();
            KeepProblemThroughReopen(project.Id);
            if (navigation == _navigation && FindProject(project.Id) is { } moved) await OpenProjectAsync(moved);
            return;
        }

        if (check.Access == RepoAccess.Allowed)
        {
            _database.SetSetting(VerifiedSetting(project.Id), GitTools.RepoKey(project.RepoUrl));
            _database.SetSetting(CreatedHereSetting(project.Id), "");
        }
        else if (check.IsDenied)
        {
            _database.SetSetting(VerifiedSetting(project.Id), "");
            _database.SetSetting(CreatedHereSetting(project.Id), "");
        }

        _pageCheck = new PageCheck(
            check,
            folder,
            useFolderSettings || runnable is null or { IsEmpty: true } ? null : runnable,
            folderExists,
            folderProblem,
            lacksFiles,
            mayNotMatch,
            folderEngine,
            renamedHeldBy,
            pushesTo);
        RenderProject(latest);
        if (folderShown && usable) await ReadEngineVersionAsync(latest.Id, new FolderAnswer(folder!, project.RepoUrl, folderProblem));
    }

    // A project on this PC may have moved to a newer engine since it was last opened. This never
    // saves the project itself, because this PC's copy of it may be out of date. With no check of
    // its own, it goes by whether this PC has been let into the project, while an open page also
    // goes by what its check just found.
    private async Task ReadEngineVersionsAsync()
    {
        foreach (var project in _database.ListProjects()) await ReadEngineVersionAsync(project.Id);
    }

    // The version comes from the project's own files, and syncs so that PCs without a copy
    // can show it too. A project whose files don't say keeps what was last found, and a
    // folder that's really some other repo has nothing to say about this project.
    private async Task ReadEngineVersionAsync(string projectId, FolderAnswer? pageAnswer = null)
    {
        // Its long job is working in its folder, so it's read once that's done.
        if (LookOnceJobEnds(projectId)) return;
        if (FindProject(projectId) is not { } project || _database.GetProjectFolder(projectId) is not { } folder) return;
        var repoKey = GitTools.RepoKey(project.RepoUrl);
        if (_database.GetSetting(VerifiedSetting(projectId)) != repoKey && _database.GetSetting(CreatedHereSetting(projectId)) != repoKey) return;

        // A slow read of the old folder mustn't land on top of a newer one's answer.
        var read = _engineReads[projectId] = _engineReads.GetValueOrDefault(projectId) + 1;

        // A page's check has just asked this, which can take a few calls to GitHub. It only speaks for
        // the folder and repo it asked about, which can have changed since.
        var problem = pageAnswer is { } answer && answer.Folder == folder && GitTools.SameRepo(answer.RepoUrl, project.RepoUrl)
            ? answer.Problem
            : await GitTools.FolderProblemDetailAsync(folder, project.RepoUrl);
        if (problem is not null)
        {
            // Seen to be on another repo, so what it found earlier can't speak for this project now.
            // Anything else, such as an unplugged drive, says nothing about that.
            if (problem.SeenOnAnotherRepo && _engineReads[projectId] == read && _database.GetProjectFolder(projectId) == folder && _database.ForgetEngineVersionHere(projectId)) RefreshProjects();
            return;
        }

        var version = await EngineVersions.FindAsync(project.Engine, folder);
        if (version is null || _engineReads[projectId] != read || _database.GetProjectFolder(projectId) != folder) return;
        if (FindProject(projectId) is not { } latest || !GitTools.SameRepo(latest.RepoUrl, project.RepoUrl)) return;
        if (latest.Engine != project.Engine) return;

        // Always noted, even if it matches what's shown, as another PC can move what's shown on.
        var changed = latest.EngineVersion != version;
        _database.SetEngineVersion(projectId, project.Engine, version);
        if (!changed) return;
        _ = _sync.SyncNowAsync();
        RefreshProjects();
    }

    private sealed record FolderAnswer(string Folder, string RepoUrl, FolderProblem? Problem);

    // What a page's check found, all from one look.
    private sealed record PageCheck(
        RepoCheck Repo,
        string? Folder,
        FolderGitSettings? SettingsToAllow,
        bool FolderExists,
        FolderProblem? FolderProblem,
        bool FolderLacksFiles,
        bool FolderMayNotMatch,
        string? FolderEngine,
        string? RenamedRepoHeldBy,
        string? FolderPushesTo);

    private Project? FindProject(string id) => _database.ListProjects().FirstOrDefault(project => project.Id == id);

    // Allowed, or no clear answer on a PC that's been let in before or made the project here. Anything
    // else is a page that's blocked, whatever it shows.
    private bool CanWorkIn(Project project, RepoCheck check)
    {
        if (check.Access == RepoAccess.Allowed) return true;
        var repoKey = GitTools.RepoKey(project.RepoUrl);
        return check.IsUnanswered && (_database.GetSetting(VerifiedSetting(project.Id)) == repoKey || _database.GetSetting(CreatedHereSetting(project.Id)) == repoKey);
    }

    private static string VerifiedSetting(string projectId) => $"project_verified:{projectId}";

    // Created on this PC with no connection, so nothing has said it can't be used yet.
    private static string CreatedHereSetting(string projectId) => $"project_created_here:{projectId}";

    // Quietly, and only changes the page if the answer has changed. A check before each agent
    // action belongs here too, once agents work inside projects.
    // Coming back to the window, and every few minutes while it's in use. Anything an agent does
    // in a project will need its own check just before, as this can be minutes out of date.
    private async Task RecheckAccessOnReturnAsync()
    {
        if (_openProject is not { } project || _pageCheck is not { } shown || IsBusy) return;
        if (DateTimeOffset.UtcNow - _lastAccessCheck < AccessCheckInterval) return;
        _lastAccessCheck = DateTimeOffset.UtcNow;

        var folder = _database.GetProjectFolder(project.Id);
        var runnable = folder is null ? FolderGitSettings.None : await GitTools.FolderRunnableSettingsAsync(folder);
        var useFolderSettings = runnable is not null && (folder is null || GitTools.AreAllowed(folder, runnable));
        var latest = await GitTools.CheckRepoAccessAsync(project.RepoUrl, folder, useFolderSettings);

        // The folder may have changed too, as anything can happen to it while no one's looking.
        var folderExists = folder is not null && await GitTools.FolderExistsAsync(folder);
        var folderProblem = folderExists && latest.ShowsFolder
            ? await GitTools.FolderProblemDetailAsync(folder!, project.RepoUrl)
            : null;
        var changed = latest != shown.Repo || folder != shown.Folder || folderExists != shown.FolderExists || folderProblem != shown.FolderProblem;
        if (changed && _openProject?.Id == project.Id && !IsBusy) await RecheckIfStillOpenAsync(project.Id);
    }

    private void KeepProblemThroughReopen(string projectId)
    {
        if (_pageProblem is { } shown) _heldProblems[projectId] = shown;
    }

    private (bool Unfinished, bool Held) DownloadStateOf(string? folder) =>
        folder is null ? default : (_database.DownloadIsUnfinished(folder), GitTools.DownloadIsHeld(folder));

    private async Task RecheckIfStillOpenAsync(string projectId)
    {
        if (_openProject?.Id != projectId) return;
        if (FindProject(projectId) is { } latest) await OpenProjectAsync(latest);
        else BackToProjects();
    }

    // Shows the result if the person is still looking at this project.
    private void ShowPageProblem(string projectId, string problem)
    {
        // Deleted elsewhere in the meantime, so no page will ever show this.
        if (FindProject(projectId) is null)
        {
            Dialogs.Notify(problem);
            return;
        }

        if (_openProject?.Id != projectId)
        {
            _heldProblems[projectId] = problem;
            RefreshProjects();
            return;
        }
        _pageProblem = problem;
        RenderProject(_openProject);
    }

    // For a result that the page has to be up to date to make sense of.
    private async Task RecheckThenShowAsync(string projectId, string problem)
    {
        _heldProblems[projectId] = problem;
        if (_openProject?.Id != projectId) RefreshProjects(); // Its card says there's something to see.
        await RecheckIfStillOpenAsync(projectId);
    }

    private async Task RefreshThenRecheckAsync(string projectId)
    {
        var wasOpen = _openProject?.Id == projectId;

        // Already up to date, so the refresh doesn't see a changed repo and start its own check.
        if (wasOpen && FindProject(projectId) is { } latest) _openProject = latest;

        RefreshProjects();
        if (wasOpen) await RecheckIfStillOpenAsync(projectId);
    }

    private void OpenProjectSettings(string projectId)
    {
        if (FindProject(projectId) is not { } project) return;

        _settingsProjectId = projectId;
        _settingsOpenedWith = (_database.GetProjectFolder(projectId), project.RepoUrl);
        MainContent.IsEnabled = false;
        MainContent.Effect = new BlurEffect { Radius = 8 };
        ProjectSettings.Open(_database, project);
    }

    private void ProjectSettings_Changed() => _ = _sync.SyncNowAsync();

    private async void ProjectSettings_Closed()
    {
        MainContent.Effect = null;
        MainContent.IsEnabled = UpdateOverlay.Visibility != Visibility.Visible;

        // The engine or folder may have changed. A project whose long job is running is read once
        // that's done, so the read can't change its remote while the job works in its folder.
        if (_settingsProjectId is { } changedProject && changedProject != _openProject?.Id)
        {
            if (!LookOnceJobEnds(changedProject)) _ = TellIfDatabaseFailsAsync(ReadEngineVersionAsync(changedProject));
        }

        var openId = _openProject?.Id;
        if (openId is null)
        {
            RefreshProjects();
            return;
        }

        // The repo or folder may have changed. Work already running gets to finish first.
        if (LookOnceJobEnds(openId))
        {
            RefreshProjects();
            return;
        }
        // What it says still stands, unless the folder or repo it was about has just been changed.
        if (_settingsOpenedWith == (_database.GetProjectFolder(openId), FindProject(openId)?.RepoUrl)) KeepProblemThroughReopen(openId);
        await RefreshThenRecheckAsync(openId);

        // Left before its check got there, so it's read the way any background read is.
        if (_openProject?.Id != openId) await ReadEngineVersionAsync(openId);
    }

    private void ProjectSettings_Deleted()
    {
        MainContent.Effect = null;
        MainContent.IsEnabled = UpdateOverlay.Visibility != Visibility.Visible;
        BackToProjects();
        RefreshProjects();
        _ = _sync.SyncNowAsync();
    }

    private void BackToProjects()
    {
        _navigation++;
        _openPageNeedsCheck = false;
        _openSection = null;
        _openProject = null;
        _pageCheck = null;
        _pageProblem = null;
        ProjectView.Visibility = Visibility.Collapsed;
        ProjectSelectView.Visibility = Visibility.Visible;
    }
}
