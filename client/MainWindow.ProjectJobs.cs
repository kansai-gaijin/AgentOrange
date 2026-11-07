using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>The long jobs themselves: replacing a repo, cloning, finishing a download, and connecting a folder.</summary>
public partial class MainWindow
{
    private const string CloneInProgressSetting = "clone_in_progress";

    private async Task CreateReplacementRepoAsync(Project project, RepoCheck shownCheck)
    {
        // Busy from the first click, so a second one can't create a second repo.
        if (_busyProjectId is not null) return;
        StartBusy(project, "Checking the repo again…");

        // The page may have been open a while. Access may have come back, or a different
        // account be active, and what they agreed to named the account it would go under.
        var latestCheck = await GitTools.CheckRepoAccessAsync(project.RepoUrl, _database.GetProjectFolder(project.Id));
        if (latestCheck != shownCheck)
        {
            EndBusy();
            _confirmingNewRepo = false;
            await RecheckIfStillOpenAsync(project.Id);
            return;
        }

        // The linked folder only qualifies if it can really move to the new repo.
        var folder = _database.GetProjectFolder(project.Id);
        var folderIsUsable = folder is not null && await GitTools.FolderCanReplaceRepoAsync(folder, project.RepoUrl);

        // Still busy while the folder dialog is open, so nothing looks at the page behind it and
        // forgets what they agreed to.
        if (!folderIsUsable)
        {
            StartBusy(project, "Choosing a folder…");
            folder = Dialogs.PickFolder($"Choose the folder for {project.Name}");
            if (folder is null)
            {
                EndBusy();
                return;
            }

            // The dialog can stay open a long time, and what they agreed to named an account.
            StartBusy(project, "Checking the repo again…");
            if (await GitTools.CheckRepoAccessAsync(project.RepoUrl, _database.GetProjectFolder(project.Id)) != shownCheck)
            {
                EndBusy();
                _confirmingNewRepo = false;
                await RecheckIfStillOpenAsync(project.Id);
                return;
            }
        }

        StartBusy(project, CreatingRepoText);

        // The folder dialog can stay open a long time, and sync keeps running behind it.
        if (FindProject(project.Id) is not { } stillThere || !GitTools.SameRepo(stillThere.RepoUrl, project.RepoUrl))
        {
            EndBusy();
            _confirmingNewRepo = false;
            await RecheckIfStillOpenAsync(project.Id);
            return;
        }

        // Another project may have a copy of the repo that's being replaced, on a PC where the two synced together.
        if (await _database.ClaimFolderAsync(folder!, project) is { } owner)
        {
            EndBusy();
            ShowPageProblem(project.Id, LocalDatabase.RefusalFor(owner, $"{owner.Name} already uses that folder, or one that overlaps it. Project folders can't overlap."));
            return;
        }

        // Held until it's linked, so settings can't give it to another project meanwhile.
        try
        {
            await ReplaceRepoHoldingFolderAsync(project, folder!);
        }
        finally
        {
            _database.ReleaseFolder(folder!);
        }
    }

    private async Task ReplaceRepoHoldingFolderAsync(Project project, string folder)
    {
        var linkedBefore = _database.GetProjectFolder(project.Id);
        var result = await GitTools.CreatePrivateRepoAsync(project.Name, folder, project.RepoUrl, _replacementRepoName ?? "");

        // It exists on GitHub now, and this is the only place that says where.
        void SayAddressNotSaved(SqliteException error)
        {
            AppLog.Write($"Couldn't save a new repo's address: {error.Message}");
            Dialogs.Notify($"The repo was created at {result.Value}, but the app's database can't be reached, so {project.Name} wasn't changed. Once it's back, enter that address in its project settings.");
        }

        // Busy until the project has its new repo, or it's given up on, so it can't be asked for a
        // second time meanwhile, and nothing looks at the page in between. It ends just before
        // whatever that comes to is shown, and only this job, not one started after it.
        var job = _busyJob;
        void Done()
        {
            if (_busyProjectId == project.Id && _busyJob == job) EndBusy();
        }

        try
        {
            Project? latest;
            try
            {
                if (!result.Worked)
                {
                    Done();
                    ShowPageProblem(project.Id, result.Value);
                    return;
                }
                latest = FindProject(project.Id);
            }
            catch (SqliteException error) when (result.Worked)
            {
                Done();
                SayAddressNotSaved(error);
                return;
            }

            // Saved even if they've left the page, because the repo now exists. A URL changed
            // in the meantime was chosen more recently, so it stays.
            if (latest is null || GitTools.RepoUrlProblem(result.Value) is not null)
            {
                Done();
                ShowPageProblem(project.Id, $"The repo was created at {result.Value}, but the project couldn't be updated.");
                return;
            }
            if (!GitTools.SameRepo(latest.RepoUrl, project.RepoUrl))
            {
                Done();
                _confirmingNewRepo = false;
                await RecheckThenShowAsync(project.Id, $"The repo was created at {result.Value}, but this project moved to a different repo in the meantime, so it wasn't changed.");
                return;
            }

            try
            {
                // Another window can have given this new repo to another project while it was being made.
                if (await _database.ClaimRepoAsync(result.Value, latest) is { } repoOwner)
                {
                    Done();
                    ShowPageProblem(project.Id, LocalDatabase.RefusalFor(repoOwner, $"The repo was created at {result.Value}, but {repoOwner.Name} uses it now, so this project wasn't changed.") + (LocalDatabase.MeansNoTurn(repoOwner) ? $" The repo is at {result.Value}, so enter that in project settings." : ""));
                    return;
                }

                if (FindProject(project.Id) is not { } stillOnOld || !GitTools.SameRepo(stillOnOld.RepoUrl, project.RepoUrl))
                {
                    Done();
                    _confirmingNewRepo = false;
                    await RecheckThenShowAsync(project.Id, $"The repo was created at {result.Value}, but this project moved to a different repo in the meantime, so it wasn't changed.");
                    return;
                }

                _database.UpdateProject(project.Id, current => GitTools.SameRepo(current.RepoUrl, project.RepoUrl) ? current with { RepoUrl = result.Value } : current);
                _database.SetSetting(VerifiedSetting(project.Id), GitTools.RepoKey(result.Value)); // This PC just made it.

                // A folder chosen in settings while this ran is more recent, so it stays.
                if (_database.GetProjectFolder(project.Id) == linkedBefore) _database.SetProjectFolder(project.Id, folder);
            }
            catch (SqliteException error)
            {
                Done();
                SayAddressNotSaved(error);
                return;
            }
            finally
            {
                _database.ReleaseRepo(result.Value);
            }
            Done();
            _ = _sync.SyncNowAsync();
            _confirmingNewRepo = false;
            await RefreshThenRecheckAsync(project.Id);
        }
        finally
        {
            Done();
        }
    }

    // A new project whose repo already has files, and an empty folder for them. Running as an
    // ordinary clone, on its page, means it shows progress and can be cancelled.
    private async Task CloneIntoChosenFolderAsync(string projectId, string folder)
    {
        // The wizard's hold becomes this project's before its page opens, so nothing else can take the
        // folder during the check, and the project itself isn't turned away from it.
        try
        {
            if (FindProject(projectId) is not { } project) return;
            _database.TransferFolderHold(folder, project);
            await OpenProjectAsync(project);
            if (_busyProjectId is not null)
            {
                ShowPageProblem(projectId, $"Something else is running. When that finishes, choose Clone and pick {folder} again.");
                return;
            }
            if (FindProject(projectId) is not { } latest) return;

            // What the page found when it opened is what the clone goes by, as the page would only
            // have offered one if that let it.
            if (_openProject?.Id != projectId)
            {
                // Its card says there's something to see, as nothing else would rebuild it.
                _heldProblems[projectId] = $"The clone into {folder} didn't start, because this project's page was left before its repo was checked. Open it and choose Clone to try again.";
                RefreshProjects();
                return;
            }
            if (_projectCheck is not { } check)
            {
                ShowPageProblem(projectId, $"The clone into {folder} didn't start, because this project's repo was still being checked. Choose Clone and pick {folder} again.");
                return;
            }
            if (!CanWorkIn(latest, check))
            {
                ShowPageProblem(projectId, $"The clone into {folder} didn't start, because this repo can't be used from this PC right now.");
                return;
            }

            var exact = Path.TrimEndingDirectorySeparator(folder);
            await CloneProjectAsync(latest, Path.GetDirectoryName(exact)!, exact);
        }
        finally
        {
            _database.ReleaseFolder(folder);
        }
    }

    // Holds where it's going until the end, because a clone can take hours and another project
    // choosing that folder meanwhile would only find out when it finished.
    private async Task CloneProjectAsync(Project project, string parentFolder, string? exactFolder = null)
    {
        if (_busyProjectId is not null) return;
        var destination = exactFolder ?? GitTools.CloneDestination(project.RepoUrl, parentFolder);
        var isHeld = false;
        var job = 0;
        try
        {
            job = _busyJob + 1; // Nothing's busy here, so this is the number starting it gives it.
            StartBusy(project, "Checking that folder…");
            var owner = destination is null ? null : await _database.ClaimFolderAsync(destination, project);
            isHeld = destination is not null && owner is null;
            if (owner is not null)
            {
                EndBusy();
                ShowPageProblem(project.Id, LocalDatabase.RefusalFor(owner, $"{owner.Name} already uses a folder that overlaps where this would clone to. Project folders can't overlap."));
                return;
            }

            await RunCloneAsync(project, parentFolder, exactFolder);
        }
        catch (Exception)
        {
            // Something unexpected, and the page can't be left waiting forever. Only this clone, though.
            if (_busyProjectId == project.Id && _busyJob == job)
            {
                _cloneCancel = null;
                EndBusy();
            }
            throw;
        }
        finally
        {
            if (isHeld) _database.ReleaseFolder(destination!);
        }
    }

    private async Task RunCloneAsync(Project project, string parentFolder, string? exactFolder)
    {
        var folderBefore = _database.GetProjectFolder(project.Id);

        using var cancel = new CancellationTokenSource();
        _cloneCancel = cancel;
        StartBusy(project, "Cloning…");
        string? cloneRecord = null;
        string? workingFolder = null;
        var result = await GitTools.CloneAsync(project.RepoUrl, parentFolder, cancel.Token, working =>
        {
            workingFolder = working;
            if (working is null)
            {
                // Finished, so the cleanup at the next start has to leave it alone from now on.
                var finished = cloneRecord;
                ChangeCloneRecords(records => records.Where(record => record != finished));
                cloneRecord = null;
                return;
            }
            cloneRecord = CloneRecord(working);
            ChangeCloneRecords(records => records.Append(cloneRecord));
        }, exactFolder, stage => ShowBusyStage("Cloning…", stage));

        try
        {
            await AfterCloneAsync(project, result, cancel, folderBefore, async () =>
            {
                // One that couldn't be fully removed stays on the list, to be tried again next start.
                if (cloneRecord is not null && (workingFolder is null || !await GitTools.FolderExistsAsync(workingFolder)))
                {
                    ChangeCloneRecords(records => records.Where(record => record != cloneRecord));
                }
            });
        }
        catch (SqliteException error) when (result.Worked)
        {
            // Hours of download are sitting there, and nothing's going to say so otherwise.
            AppLog.Write($"Couldn't link a finished clone: {error.Message}");
            _cloneCancel = null;
            _databaseWasLost = true;
            try
            {
                if (_busyProjectId == project.Id) EndBusy();
            }
            catch (SqliteException)
            {
                // Drawing the page needs the database, and that happens again once it's back.
            }
            Dialogs.Notify($"{project.Name} finished cloning to {result.Value}, but the app's database can't be reached, so it may not be linked. Once it's back, select that folder. If it uses large files or submodules, get them with git lfs pull and git submodule update --init --recursive.");
        }
    }

    private async Task AfterCloneAsync(Project project, GitResult result, CancellationTokenSource cancel, string? folderBefore, Func<Task> forgetCloneRecord)
    {
        await forgetCloneRecord();

        if (!result.Worked)
        {
            _cloneCancel = null;
            EndBusy();
            ShowPageProblem(project.Id, result.Value);
        }
        else if (FindProject(project.Id) is null)
        {
            _cloneCancel = null;
            EndBusy();
            Dialogs.Notify($"{project.Name} was deleted while it was cloning, so the clone wasn't linked to anything. It's still at {result.Value}.");
        }
        else if (FindProject(project.Id) is { } latest
            && (_database.GetProjectFolder(project.Id) != folderBefore
                || (!GitTools.SameRepo(latest.RepoUrl, project.RepoUrl) && !GitTools.SameRepo(await GitTools.RenamedRepoUrlAsync(project.RepoUrl) ?? "", latest.RepoUrl))))
        {
            // Changed through sync or in another window while this ran, and that's more recent.
            _cloneCancel = null;
            EndBusy();
            await RecheckThenShowAsync(project.Id, $"Cloned to {result.Value}, but this project changed while that ran, so it wasn't linked. The clone is still there.");
        }
        else
        {
            // Linked first, so an app closing during what comes next leaves a project that knows
            // about this folder, and says what's still missing from it.
            _downloadIsInPlace = true;
            string? missing = null;
            using (var hold = GitTools.TryTakeDownload(result.Value))
            {
                await UseFolderAsync(FindProject(project.Id) ?? project, result.Value, folderBefore);
                var isLinked = _database.GetProjectFolder(project.Id) == result.Value;

                // Files that never got put in place come first, and it stays unfinished until they're sorted out.
                if (isLinked && hold is not null && result.Note is null)
                {
                    var look = await GitTools.LookBeforeFinishingAsync(result.Value);
                    if (look?.Refused is { } refused) missing = refused;
                    else if (look is not null && GitTools.AreAllowed(GitTools.FinishApprovalKey(result.Value), look.Settings))
                    {
                        missing = await GitTools.FinishDownloadAsync(result.Value, cancel.Token, stage => ShowBusyStage("Cloning…", stage));
                    }
                }
            }
            _cloneCancel = null;
            EndBusy();

            var notes = new[] { result.Note, missing }.OfType<string>().ToList();
            if (notes.Count > 0) ShowPageProblem(project.Id, string.Join(' ', notes));
        }
    }

    private async Task FinishDownloadAsync(Project project, string folder)
    {
        if (_busyProjectId is not null) return;

        // These run in the folder, and it can have picked up settings since it was last looked at.
        var runnable = await GitTools.FolderRunnableSettingsAsync(folder);
        if (runnable is null)
        {
            ShowPageProblem(project.Id, "Couldn't read this folder's own Git settings, so nothing was run in it. Check that Git works in that folder, then try again.");
            return;
        }
        if (!GitTools.AreAllowed(folder, runnable))
        {
            await RecheckIfStillOpenAsync(project.Id); // That draws the row for allowing them.
            return;
        }

        var finishing = await GitTools.LookBeforeFinishingAsync(folder);
        if (finishing is null)
        {
            ShowPageProblem(project.Id, "Couldn't read this folder's own Git settings, so nothing was run in it. Check that Git works in that folder, then try again.");
            return;
        }
        if (finishing.Refused is { } refused)
        {
            ShowPageProblem(project.Id, refused);
            return;
        }
        if (!GitTools.AreAllowed(GitTools.FinishApprovalKey(folder), finishing.Settings))
        {
            _finishSettingsToAllow = finishing.Settings;
            if (_openProject?.Id == project.Id) RenderProject(_openProject);
            return;
        }
        if (_busyProjectId is not null) return;

        // Two of these at once would run into each other's locks.
        if (GitTools.TryTakeDownload(folder) is not { } hold)
        {
            if (_openProject?.Id == project.Id) RenderProject(_openProject);
            return;
        }

        using var cancel = new CancellationTokenSource();
        _cloneCancel = cancel;
        string? missing;
        try
        {
            StartBusy(project, "Finishing the download…");
            _downloadIsInPlace = true;
            missing = await GitTools.FinishDownloadAsync(folder, cancel.Token, stage => ShowBusyStage("Finishing the download…", stage));
        }
        finally
        {
            hold.Dispose();
            _cloneCancel = null;
            EndBusy();
        }
        if (missing is not null) ShowPageProblem(project.Id, missing);
    }

    private Button SelectFolderButton(Project project)
    {
        var select = ActionButton("Select folder");
        select.IsEnabled = _busyProjectId is null;
        select.Click += async (_, _) =>
        {
            if (Dialogs.PickFolder($"Find the folder for {project.Name}") is not { } chosen || _busyProjectId is not null) return;

            var folderBefore = _database.GetProjectFolder(project.Id);
            string? folderProblem;
            var allowedJustNow = false;
            try
            {
                StartBusy(project, "Checking that folder…");
                // Who has it comes first, as asking about it and checking it can both act on it.
                if (await _database.ProjectUsingFolderAsync(chosen, project.Id) is { } other)
                {
                    folderProblem = $"{other.Name} already uses that folder, or one that overlaps it. Project folders can't overlap.";
                }
                else
                {
                    try
                    {
                        allowedJustNow = await GitTools.AskAboutUnreadableSshSettingsAsync(chosen);
                        folderProblem = await GitTools.FolderProblemAsync(chosen, project.RepoUrl);
                    }
                    catch
                    {
                        if (allowedJustNow) GitTools.ForgetAllowedFolderSettings(chosen);
                        throw;
                    }
                }
                if (allowedJustNow && folderProblem is not null) GitTools.ForgetAllowedFolderSettings(chosen);
            }
            finally
            {
                EndBusy();
            }

            if (folderProblem is not null) ShowPageProblem(project.Id, folderProblem);
            else if (!await UseFolderAsync(project, chosen, folderBefore) && allowedJustNow) GitTools.ForgetAllowedFolderSettings(chosen);
        };
        return select;
    }

    private async Task RepairRemoteAsync(Project project, string folder)
    {
        if (_busyProjectId is not null) return;
        StartBusy(project, "Trying HTTPS and SSH…");
        var result = await GitTools.RepairRemoteAsync(folder, project.RepoUrl);
        EndBusy();

        if (result.Worked) await RefreshThenRecheckAsync(project.Id);
        else ShowPageProblem(project.Id, result.Value);
    }

    private async Task PointFolderAtRepoAsync(Project project, string folder)
    {
        if (_busyProjectId is not null) return;
        StartBusy(project, "Connecting the folder…");

        // The URL may be a typo, and the next push would send this folder's history there.
        var history = await GitTools.CompareFolderHistoryAsync(folder, project.RepoUrl);
        var why = history switch
        {
            FolderHistory.SharesNothing => $"{project.RepoUrl} has commits this folder doesn't, so they may not be the same project.",
            FolderHistory.CantTell when await GitTools.FolderHasNoCommitsAsync(folder) != true => $"Couldn't compare this folder's commits with {project.RepoUrl}, so they may not be the same project.",
            _ => null,
        };
        if (why is not null && !Dialogs.Confirm($"{why}\n\nConnecting them changes no files and pushes nothing, but the next push could send this folder's history there. Connect them anyway?"))
        {
            EndBusy();
            return;
        }

        // That can stay open a long time, and sync keeps running behind it.
        if (FindProject(project.Id) is not { } latest || !GitTools.SameRepo(latest.RepoUrl, project.RepoUrl))
        {
            EndBusy();
            await RecheckIfStillOpenAsync(project.Id);
            return;
        }

        var result = await GitTools.PointFolderAtRepoAsync(folder, project.RepoUrl);
        EndBusy();

        if (result.Worked) await RefreshThenRecheckAsync(project.Id);
        else ShowPageProblem(project.Id, result.Value);
    }

    // Says who's cloning as well as where, so another window of the app can tell it isn't abandoned.
    private static string CloneRecord(string folder)
    {
        using var self = Process.GetCurrentProcess();
        return $"{self.Id}|{self.StartTime.ToUniversalTime().Ticks}|{folder}";
    }

    // The app closed in the middle of a clone last time. There can be more than one, because
    // a folder on an unplugged drive stays on the list until it can be reached.
    private async Task RemoveLeftoverCloneAsync()
    {
        var finished = new List<string>();
        var kept = new List<string>();
        foreach (var record in CloneRecords())
        {
            var parts = record.Split('|', 3);
            var stillRunning = parts.Length == 3 && int.TryParse(parts[0], out var id) && long.TryParse(parts[1], out var started) && IsStillRunning(id, started);
            if (stillRunning) continue;

            var outcome = parts.Length == 3 ? await GitTools.RemoveLeftoverCloneAsync(parts[2]) : LeftoverClone.Gone;
            if (outcome != LeftoverClone.TryAgain) finished.Add(record);
            if (outcome == LeftoverClone.KeptFinished) kept.Add(parts[2]);
        }

        ChangeCloneRecords(records => records.Except(finished));

        // Hours of download can be in there, in a folder no one would think to look at.
        foreach (var folder in kept)
        {
            Dialogs.Notify($"A clone finished last time, but the app closed before it could be named or linked. It's at {folder}. You can rename it and select it. If it uses large files or submodules, get them with git lfs pull and git submodule update --init --recursive.");
        }
    }

    private List<string> CloneRecords() =>
        (_database.GetSetting(CloneInProgressSetting) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

    // Read and written together, as another window can be starting or finishing a clone of its own.
    private void ChangeCloneRecords(Func<IEnumerable<string>, IEnumerable<string>> change) =>
        _database.ChangeSetting(CloneInProgressSetting, current => string.Join('\n', change((current ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))));

    // The start time rules out another program that has since been given the same ID.
    private static bool IsStillRunning(int id, long startedTicks)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return process.StartTime.ToUniversalTime().Ticks == startedTicks;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access denied, as for a window running as administrator. Being wrong in this direction
            // leaves a folder for a later start, and being wrong in the other deletes a live clone.
            return true;
        }
    }

    // Saved even if they've left the page, because the clone or the choice was real.
    private async Task<bool> UseFolderAsync(Project chosenFor, string folder, string? folderBefore)
    {
        var projectId = chosenFor.Id;
        if (FindProject(projectId) is not { } project) return false;
        var heldAlready = _database.IsHoldingFolder(folder, projectId);
        if (!heldAlready && await _database.ClaimFolderAsync(folder, project) is { } other)
        {
            ShowPageProblem(projectId, LocalDatabase.RefusalFor(other, $"{other.Name} already uses {folder}. A folder can only be one project."));
            return false;
        }

        try
        {
            // Settings stay open during slow work, and sync keeps running, so this has to look again now.
            if (FindProject(projectId) is not { } latest) return false;
            if (_database.GetProjectFolder(projectId) != folderBefore || !GitTools.SameRepo(latest.RepoUrl, chosenFor.RepoUrl))
            {
                if (!heldAlready) _database.ReleaseFolder(folder);
                await RecheckThenShowAsync(projectId, $"{folder} wasn't linked, because this project's folder or repo changed while that was being checked.");
                return false;
            }

            _database.SetProjectFolder(projectId, folder);
            await GitTools.PickUpUnfinishedNoteAsync(folder);
        }
        finally
        {
            if (!heldAlready) _database.ReleaseFolder(folder);
        }

        // An open page reads it after its own check, and only if that lets this PC work in the project.
        if (_openProject?.Id != projectId) await ReadEngineVersionAsync(projectId);
        else
        {
            await RefreshThenRecheckAsync(projectId);
            if (_openProject?.Id != projectId) await ReadEngineVersionAsync(projectId);
            return true;
        }
        await RefreshThenRecheckAsync(projectId);
        return true;
    }
}
