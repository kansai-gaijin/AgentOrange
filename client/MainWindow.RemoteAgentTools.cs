using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>
/// Starting an agent on another PC (#93), as the PC linking: "Start an agent on…" on a project's
/// page, beside Run tests on…, once this PC links to a machine with Agents access (or one grants
/// it, to link again for); and AO's start_agent_on tool, so an agent here can hand a long task, or
/// a part of a split milestone, to an agent there. Either way the job goes as a test run's does,
/// signed over the link, from a pushed commit and the folder's own origin, and the run's window,
/// read_test_run and cancel_test_run follow and stop it.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<string, RemoteAgentWindow> _remoteAgentWindows = [];

    // Shown only to a PC some machine gives Agents access, or now grants it.
    private void AddRemoteAgentButton(Panel row, Project project, string folder)
    {
        if (_testMachines.Latest is not { } latest || !latest.Links.Any(link => link.Linker == latest.Me && (MachineLinks.Allows(link.Access, "agents") || MachineLinks.Allows(link.Offered, "agents")))) return;
        var start = ActionButton("Start an agent on…");
        start.VerticalAlignment = VerticalAlignment.Center;
        start.Margin = new Thickness(row.Children.Count > 0 ? 8 : 0, 0, 0, 0);
        start.IsEnabled = !IsBusyWith(project.Id);
        start.Click += (_, _) => OpenRemoteAgentWindow(project, folder);
        row.Children.Add(start);
    }

    /// <summary>The project's Start an agent on… window: the one it has, brought to the front, or a new one.</summary>
    internal RemoteAgentWindow OpenRemoteAgentWindow(Project project, string folder)
    {
        if (_remoteAgentWindows.TryGetValue(project.Id, out var open))
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return open;
        }
        var window = new RemoteAgentWindow(this, _testMachines, project, folder) { Owner = null };
        _remoteAgentWindows[project.Id] = window;
        window.Closed += (_, _) => _remoteAgentWindows.Remove(project.Id);
        window.Show();
        return window;
    }

    private List<McpTool> RemoteAgentTools() =>
    [
        new("start_agent_on", "Starts an agent conversation on another of the owner's PCs that this PC links to with Agents access, for this project, which that PC has too: for a long task, or a part of a split milestone, to work on there while this one goes on. It starts from a commit pushed to origin (the one checked out in the project's folder, or the one given), works in that PC's own clone of the repo on a new branch, with that PC's own login and settings, and once done pushes its commits to origin as that branch. Give it everything it needs in task, as it sees nothing of this conversation. Then read_test_run with the run id follows its conversation and gives the branch, and cancel_test_run stops it.",
            new JsonObject
            {
                ["machine"] = Text("The PC, by its name as list_test_runs gives it. Left out, the one linked with Agents access, when there's only one."),
                ["task"] = Text($"What the agent there should do, in full: it sees nothing else of this conversation. {TestJob.MostTaskLength} characters at most."),
                ["title"] = Text("A few words to call it by, on both PCs. The task's first line if not given."),
                ["commit"] = Text("A commit id, the start of one, or a branch or tag, pushed to origin, for it to start from. The commit checked out in the project's folder if not given."),
            },
            ["task"], (_, _) => "", StartAgentOnForAgentAsync),
    ];

    /// <summary>
    /// A conversation another PC started here never uses this PC's own links, as T3 fences work a
    /// link started from this machine's own links: what it was granted ends here, and doesn't pass on
    /// to the PCs this one links to.
    /// </summary>
    private void RefuseFromAnotherPc(string what)
    {
        if (CallingConversation is { FromPc: { } from }) throw new McpException(-32602, $"This conversation was started on this PC by {from}, over a link, so it can't {what} over this PC's own links. Say in your reply what should be run, for {from} to do it.");
    }

    private async Task<object> StartAgentOnForAgentAsync(Project project, JsonObject given)
    {
        RefuseFromAnotherPc("start an agent on another PC");
        var task = Required(given, "task");
        var links = LinksThisPcSendsOver();
        var withAgents = links.Where(link => _testMachines.AgentsProblem(link) is null).ToList();
        MachineLinkInfo link;
        if (Given(given, "machine") is { Length: > 0 } asked)
        {
            link = links.FirstOrDefault(each => each.MachineLabel.Equals(asked, StringComparison.OrdinalIgnoreCase))
                ?? links.FirstOrDefault(each => each.MachineLabel.StartsWith(asked, StringComparison.OrdinalIgnoreCase))
                ?? throw new McpException(-32602, $"machine: this PC isn't linked to a PC called {asked}.{(links.Count > 0 ? $" It links to {string.Join(", ", links.Select(each => each.MachineLabel))}." : "")}");
            if (_testMachines.AgentsProblem(link) is { } cant) throw new McpException(-32602, $"machine: {cant}");
        }
        else
        {
            link = withAgents.Count switch
            {
                1 => withAgents[0],
                0 => throw new McpException(-32602, links.Count == 0
                    ? _testMachines.Problem ?? "This PC isn't linked to another PC. The person links one with Agents access in Settings › Test machines."
                    : $"No PC this PC links to gives it Agents access. {string.Join(" ", links.Select(each => _testMachines.AgentsProblem(each)))}"),
                _ => throw new McpException(-32602, $"machine: name one of the PCs this PC links to with Agents access: {string.Join(", ", withAgents.Select(each => each.MachineLabel))}."),
            };
        }
        if (link.Status is "unreachable" or "other_release" or "expired") throw new McpException(-32602, $"machine: {link.MachineLabel} is {MachineState(link)}.");

        var (folder, folderProblem) = await GitFolderAsync(project.Id);
        if (folder is null) throw new McpException(-32602, folderProblem!);
        var commit = Given(given, "commit") is { Length: > 0 } named ? named : null;
        var title = Given(given, "title") is { Length: > 0 } called ? called : null;
        var (runId, problem) = await _testMachines.StartAgentAsync(link, project, folder, task, title, commit, TestJob.ByAgent);
        if (runId is null) throw new McpException(-32602, problem ?? "The task couldn't be sent.");
        return $"Sent the task to {link.MachineLabel} as run {runId}. It {(link.Status == "busy" ? "waits its turn there, as that PC is busy with a run" : "starts within a few seconds")}, on the new branch {TestRunner.AgentBranch(runId)}, and pushes its commits there once done. read_test_run with run {runId} and seconds follows its conversation; cancel_test_run stops it.";
    }
}
