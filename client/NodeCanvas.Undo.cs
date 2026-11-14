using System.Windows.Threading;

namespace AgentOrange;

/// <summary>
/// Undo and redo for a board: Ctrl+Z, and Ctrl+Shift+Z or Ctrl+Y. Before the first change of each
/// action, the board as it was (its nodes, links and pictures, as stored) is kept, up to the last 30.
/// Undoing stores that copy again, which syncs like any other change. Each board keeps its own, for as
/// long as the app runs, so going to another board and back can still undo. Words being typed in a
/// node have the text box's own undo until the node is left.
/// </summary>
internal sealed partial class NodeCanvas
{
    private const int MostUndos = 30;

    private sealed record BoardState(Dictionary<string, string> Nodes, Dictionary<string, string> Links, Dictionary<string, string> Pictures);

    private sealed class History
    {
        public readonly LinkedList<BoardState> Back = new();
        public readonly Stack<BoardState> Forward = new();
        public bool Open; // An action's first change has been kept, and its others are part of it.
    }

    private static readonly Dictionary<string, History> Histories = [];

    // A picture can be in every copy kept, so each is held once rather than once per copy.
    private static readonly Dictionary<string, string> KeptPictures = [];

    private bool _restoring;

    private History BoardHistory
    {
        get
        {
            var key = $"{_projectId}|{_board}";
            if (!Histories.TryGetValue(key, out var history)) Histories[key] = history = new History();
            return history;
        }
    }

    private BoardState Capture()
    {
        var nodes = new Dictionary<string, string>();
        foreach (var (id, text) in _database.ListProjectRecords(LocalDatabase.NodesCollection, _projectId))
        {
            if (Parse<NodeData>(text) is { } data && data.Board == _board) nodes[id] = text;
        }
        var links = new Dictionary<string, string>();
        foreach (var (id, text) in _database.ListProjectRecords(LocalDatabase.NodeLinksCollection, _projectId))
        {
            if (Parse<LinkData>(text) is { } data && data.Board == _board) links[id] = text;
        }
        var pictures = new Dictionary<string, string>();
        foreach (var id in nodes.Keys)
        {
            if (!_storedPictures.TryGetValue(id, out var stored)) continue;
            if (KeptPictures.TryGetValue(id, out var kept) && kept == stored) stored = kept;
            else KeptPictures[id] = stored;
            pictures[id] = stored;
        }
        return new BoardState(nodes, links, pictures);
    }

    /// <summary>Called before anything on the board is stored. The first time in an action, keeps the board as it was.</summary>
    private void Changing()
    {
        if (_restoring) return;
        var history = BoardHistory;
        if (history.Open) return;

        history.Open = true;
        history.Back.AddLast(Capture());
        if (history.Back.Count > MostUndos) history.Back.RemoveFirst();
        history.Forward.Clear();

        // Everything changed before the app is idle again is the same action, such as deleting several nodes.
        Dispatcher.BeginInvoke(() => history.Open = false, DispatcherPriority.ContextIdle);
    }

    private void Undo(bool back)
    {
        // Words being typed are kept first, which is a change of its own to undo.
        StopEditing(keep: true);

        var history = BoardHistory;
        if (back ? history.Back.Count == 0 : history.Forward.Count == 0) return;

        var now = Capture();
        BoardState then;
        if (back)
        {
            then = history.Back.Last!.Value;
            history.Back.RemoveLast();
            history.Forward.Push(now);
        }
        else
        {
            then = history.Forward.Pop();
            history.Back.AddLast(now);
        }

        _restoring = true;
        try
        {
            Restore(LocalDatabase.NodesCollection, now.Nodes, then.Nodes);
            Restore(LocalDatabase.NodeLinksCollection, now.Links, then.Links);
            Restore(LocalDatabase.NodeImagesCollection, now.Pictures, then.Pictures);
        }
        finally
        {
            _restoring = false;
        }
        Reload();
        _changed();
    }

    // Only what differs is stored again, so an undo sends no more than the change did.
    private void Restore(string collection, Dictionary<string, string> now, Dictionary<string, string> then)
    {
        foreach (var id in now.Keys.Where(id => !then.ContainsKey(id))) _database.SaveRecord(collection, id, "", deleted: true);
        foreach (var (id, text) in then)
        {
            if (!now.TryGetValue(id, out var was) || was != text) _database.SaveRecord(collection, id, text);
        }
    }
}
