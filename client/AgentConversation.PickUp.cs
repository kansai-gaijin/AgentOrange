using System.Text.Json.Nodes;

namespace AgentOrange;

/// <summary>How a conversation stood once Agent Orange started again.</summary>
public enum PickedUp { Nothing, Picked, PickedWorking, CutOff }

/// <summary>
/// Picking a conversation's agent program back up after Agent Orange restarts (#101). Its program runs
/// in a session of its own (<see cref="SessionHost"/>), which goes on while the app is away. What the app
/// needs to take it back is saved with the conversation while one runs: where the session is, how far
/// the app had read what it said, and how the turn stood. The conversation's file is saved as a whole,
/// so what's read from there on always follows on from what it holds. A turn whose program is gone when
/// the app comes back was cut off, which the app tells the agent, so it carries on by itself.
/// </summary>
public sealed partial class AgentConversation
{
    /// <summary>What picks its program back up after a restart, kept while one runs.</summary>
    public sealed record LiveSession(string Folder, long Offset, bool Codex, bool Working, DateTimeOffset WorkingSince, int Turns,
        string? StartedWith, string? RunningMode, string? MessageId, List<string>? Confirmed, string? CodexTurn, int NextRpc,
        Dictionary<string, JsonNode>? CodexRequests, List<BackgroundWork>? Work, bool TasksTold, string? Checkpoint, List<string>? SkillFolders = null);

    /// <summary>Saved with the conversation while its program runs; null once it has ended.</summary>
    public LiveSession? Live
    {
        get => _process is not { } session ? null : new LiveSession(session.Folder, session.Offset, _codex, IsWorking, WorkingSince, _turns,
            _startedWith, _runningMode, _messageId, _messageId is { } message ? [.. _confirmed.Where(id => id.StartsWith($"{message}:", StringComparison.Ordinal))] : null,
            _codexTurn, _nextRpc, _codexRequests.Count > 0 ? new(_codexRequests) : null, Work.Count > 0 ? [.. Work] : null, _tasksTold,
            _checkpoint is { IsCompletedSuccessfully: true } before ? before.Result : null, _skillFolders.Count > 0 ? [.. _skillFolders] : null);
        set => _live = value;
    }

    private LiveSession? _live;

    /// <summary>On the window's thread: its program ended while Agent Orange was away, partway through a turn.</summary>
    public event Action? TurnCutOff;

    /// <summary>What Agent Orange sends a conversation whose turn a restart cut off, so it carries on by itself.</summary>
    public const string CutOffMessage =
        "Your turn was cut off by an Agent Orange restart. Check the working tree for what you'd done, and that the tools you were using are still open (open_tool opens one again), then carry on from where you were.";

    /// <summary>
    /// Once, as Agent Orange starts: takes back the program it left running, reading what it said since
    /// from where the app had got to. CutOff when it was partway through a turn and its program is gone.
    /// </summary>
    internal PickedUp PickUp(string? folder)
    {
        Folder ??= folder;
        var live = _live;
        _live = null;
        var session = live is null ? null : HostedSession.PickUp(live.Folder, live.Offset);
        if (live is null || session is null)
        {
            LetGoOfRequests();
            return live is { Working: true } && !Settled ? PickedUp.CutOff : PickedUp.Nothing;
        }

        _process = session;
        _pickedUp = true;
        _codex = live.Codex;
        if (_codex) _codexThread = SessionId;
        _codexTurn = live.CodexTurn;
        _nextRpc = Math.Max(1, live.NextRpc);
        foreach (var (item, id) in live.CodexRequests ?? []) _codexRequests[item] = id;
        _startedWith = live.StartedWith;
        _runningMode = live.RunningMode;
        _messageId = live.MessageId;
        foreach (var id in live.Confirmed ?? []) _confirmed.Add(id);
        _skillFolders = live.SkillFolders ?? [];
        Work.AddRange(live.Work ?? []);
        _tasksTold = live.TasksTold;
        if (live.Working)
        {
            IsWorking = true;
            WorkingSince = live.WorkingSince;
            _turns = Math.Max(1, live.Turns);
            if (live.Checkpoint is { } before) _checkpoint = Task.FromResult<string?>(before);
        }
        _ = ReadAsync(session);

        // One whose program ended meanwhile is read to its end, and if that's partway through a turn, it was cut off (TurnCutOff).
        return live.Working && session.WentOn ? PickedUp.PickedWorking : PickedUp.Picked;
    }

    // The program that was asking is gone, so nothing can answer it now, and nothing it left going goes on.
    private void LetGoOfRequests()
    {
        foreach (var item in Items)
        {
            if (item.RequestState == AgentRequestState.Waiting && item.Kind is AgentItemKind.Approval or AgentItemKind.Question or AgentItemKind.Plan) item.RequestState = AgentRequestState.Gone;
            if (item.Kind == AgentItemKind.Tool && item.ToolState == AgentToolState.Running) item.ToolState = AgentToolState.Failed;
            item.RequestInput = null;
            item.Suggestions = null;
        }
    }
}
