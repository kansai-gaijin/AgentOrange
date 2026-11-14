using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AgentOrange;

/// <summary>
/// The Frame: a named box behind other nodes that groups them, as in Blender. Ctrl+J puts one around
/// what's selected, or Shift+A adds an empty one. What sits inside a frame moves with it, and dragging
/// a node out of it takes it out of the group, so what's grouped is only where things are, with
/// nothing more to store or sync. Deleting a frame leaves what was in it.
/// </summary>
internal sealed partial class NodeCanvas
{
    private const string FrameKind = "frame";
    private const double FrameWidth = 480;
    private const double FrameHeight = 320;
    private const double FramePadding = 40;
    private const double FrameNameRoom = 56; // Above what's in it, for its name.
    private const double LargestFrame = 40_000;

    private static bool IsFrame(NodeView node) => node.Data.Kind == FrameKind;

    private void AddFrameParts(NodeView view, Grid parts)
    {
        view.Box.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x5B, 0x7A, 0x99));
        view.Box.CornerRadius = new CornerRadius(14);
        view.Box.Width = Math.Clamp(view.Data.Width ?? FrameWidth, NarrowestText, LargestFrame);
        view.Box.MinHeight = Math.Clamp(view.Data.Height ?? FrameHeight, ShortestText, LargestFrame);
        Panel.SetZIndex(view.Box, -1); // Behind every other node, and the links between them.

        view.Inside.HorizontalAlignment = HorizontalAlignment.Left;
        view.Inside.VerticalAlignment = VerticalAlignment.Top;
        view.Inside.Padding = new Thickness(16, 10, 16, 10);
        view.Title = new TextBlock
        {
            Text = view.Data.Title.Length > 0 ? view.Data.Title : "Frame",
            Foreground = (Brush)FindResource(view.Data.Title.Length > 0 ? "TextNormal" : "TextMuted"),
            FontSize = TitleSize,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        view.Inside.Child = view.Title;
        AddGrips(parts);
    }

    // Wholly inside it. A node only touching its edge isn't in the group.
    private static bool Holds(NodeView frame, NodeView node) =>
        !ReferenceEquals(frame, node)
        && new Rect(frame.Data.X, frame.Data.Y, frame.Box.ActualWidth, frame.Box.ActualHeight)
            .Contains(new Rect(node.Data.X, node.Data.Y, node.Box.ActualWidth, node.Box.ActualHeight));

    // What moves along with the frames being dragged: everything in them, and in frames in those.
    private IEnumerable<NodeView> CarriedBy(IEnumerable<NodeView> moving)
    {
        var carried = new HashSet<NodeView>(moving);
        var frames = new Queue<NodeView>(carried.Where(IsFrame));
        while (frames.TryDequeue(out var frame))
        {
            foreach (var inside in _nodes.Values.Where(node => Holds(frame, node) && carried.Add(node)))
            {
                if (IsFrame(inside)) frames.Enqueue(inside);
            }
        }
        return carried;
    }

    // Ctrl+J: a frame around the selected nodes, named straight away.
    private void FrameSelected()
    {
        var chosen = _selection.Where(_nodes.ContainsKey).Select(id => _nodes[id]).ToList();
        if (chosen.Count == 0) return;

        var around = Rect.Empty;
        foreach (var node in chosen) around.Union(new Rect(node.Data.X, node.Data.Y, node.Box.ActualWidth, node.Box.ActualHeight));
        var frame = AddNodeView(NewId(), new NodeData(
            _board,
            Math.Round(around.X - FramePadding),
            Math.Round(around.Y - FramePadding - FrameNameRoom),
            "",
            FrameKind,
            Math.Round(around.Width + 2 * FramePadding),
            Height: Math.Round(around.Height + 2 * FramePadding + FrameNameRoom)));
        SaveNode(frame);
        Select(frame.Id);
        StartEditing(frame);
    }
}
