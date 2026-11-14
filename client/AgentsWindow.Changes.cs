using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AgentOrange;

/// <summary>
/// T3 Code's turn diff card, under a turn's last reply: "N changed files" with the lines added and
/// taken out, buttons to open every folder and the full diff, and the files as a tree. Folders
/// with a single folder in them are merged ("a/b/c"), and folders start closed.
/// </summary>
public sealed partial class AgentsWindow
{
    private readonly HashSet<string> _openFolders = [];
    private readonly HashSet<string> _allFoldersOpen = [];

    private sealed class ChangeFolder
    {
        public string Name = "";
        public string Key = "";
        public readonly SortedDictionary<string, ChangeFolder> Folders = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<TurnChange> Files = [];
        public int Insertions => Files.Sum(file => file.Insertions) + Folders.Values.Sum(folder => folder.Insertions);
        public int Deletions => Files.Sum(file => file.Deletions) + Folders.Values.Sum(folder => folder.Deletions);
    }

    private static ChangeFolder ChangeTree(IEnumerable<TurnChange> changes)
    {
        var root = new ChangeFolder();
        foreach (var change in changes)
        {
            var parts = change.Path.Split('/');
            var folder = root;
            for (var at = 0; at < parts.Length - 1; at++)
            {
                if (!folder.Folders.TryGetValue(parts[at], out var inner))
                {
                    inner = new ChangeFolder { Name = parts[at], Key = string.Join('/', parts[..(at + 1)]) };
                    folder.Folders[parts[at]] = inner;
                }
                folder = inner;
            }
            folder.Files.Add(change);
        }
        Merge(root);
        return root;

        // A folder holding only one folder becomes one row, "a/b/c".
        static void Merge(ChangeFolder folder)
        {
            foreach (var inner in folder.Folders.Values.ToList())
            {
                var merged = inner;
                while (merged.Files.Count == 0 && merged.Folders.Count == 1)
                {
                    var only = merged.Folders.Values.First();
                    only.Name = $"{merged.Name}/{only.Name}";
                    merged = only;
                }
                if (merged != inner)
                {
                    folder.Folders.Remove(inner.Name);
                    folder.Folders[merged.Name] = merged;
                }
                Merge(merged);
            }
        }
    }

    private FrameworkElement ChangesCard(AgentConversation conversation, AgentItem reply)
    {
        var changes = reply.Changes!;
        var cardKey = reply.Id;
        var tree = ChangeTree(changes);
        var hasFolders = changes.Any(change => change.Path.Contains('/'));
        var body = new StackPanel { Margin = new Thickness(8) };

        void DrawTree()
        {
            body.Children.Clear();
            AddRows(tree, 0);
        }

        void AddRows(ChangeFolder folder, int depth)
        {
            foreach (var inner in folder.Folders.Values)
            {
                var key = $"{cardKey}|{inner.Key}";
                var open = _allFoldersOpen.Contains(cardKey) ? !_openFolders.Contains(key) : _openFolders.Contains(key);
                var chevron = AgentIcons.Line("chevron-right", 14, Ocean.MutedText);
                chevron.RenderTransformOrigin = new Point(0.5, 0.5);
                chevron.RenderTransform = new RotateTransform(open ? 90 : 0);
                chevron.Margin = new Thickness(0, 0, 4, 0);
                var icon = AgentIcons.Line(open ? "folder" : "folder-closed", 14, Ocean.MutedText);
                icon.Margin = new Thickness(0, 0, 6, 0);
                var name = new TextBlock { Text = inner.Name, FontFamily = Ocean.Mono, FontSize = 11, Foreground = Ocean.MutedText, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                var row = ChangeRow(depth, [chevron, icon], name, inner.Insertions, inner.Deletions, () =>
                {
                    if (!_openFolders.Remove(key)) _openFolders.Add(key);
                    DrawTree();
                }, inner.Name);
                body.Children.Add(row);
                if (open) AddRows(inner, depth + 1);
            }
            foreach (var file in folder.Files)
            {
                var icon = AgentIcons.Line("file", 14, Ocean.MutedText);
                icon.Margin = new Thickness(18, 0, 6, 0);
                var name = new TextBlock { Text = file.Path.Split('/')[^1], FontFamily = Ocean.Mono, FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFA, 0xFF)), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = file.Path };
                body.Children.Add(ChangeRow(depth, [icon], name, file.Insertions, file.Deletions, () => ShowDiffPanel(conversation, reply, file.Path), file.Path));
            }
        }

        // The header: "3 changed files +12 -4", and on the right expand-all and Open diff.
        var header = new DockPanel { Margin = new Thickness(12, 8, 8, 8) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        if (hasFolders)
        {
            Button? expand = null;
            void ShowExpand()
            {
                var all = _allFoldersOpen.Contains(cardKey) && !_openFolders.Any(key => key.StartsWith($"{cardKey}|", StringComparison.Ordinal));
                expand!.Content = AgentIcons.Line(all ? "chevrons-down-up" : "chevrons-up-down", 14, Ocean.MutedText);
                expand.ToolTip = all ? "Collapse all folders" : "Expand all folders";
                AutomationProperties.SetName(expand, (string)expand.ToolTip);
            }
            expand = Ocean.Ghost(new Border(), () =>
            {
                var all = _allFoldersOpen.Contains(cardKey) && !_openFolders.Any(key => key.StartsWith($"{cardKey}|", StringComparison.Ordinal));
                _openFolders.RemoveWhere(key => key.StartsWith($"{cardKey}|", StringComparison.Ordinal));
                if (all) _allFoldersOpen.Remove(cardKey);
                else _allFoldersOpen.Add(cardKey);
                ShowExpand();
                DrawTree();
            }, 6, null, Ocean.RowHover);
            expand.Width = 24;
            expand.Height = 24;
            ShowExpand();
            tools.Children.Add(expand);
        }
        var openDiff = Ocean.Ghost(Ocean.IconText("file-diff", "Open diff", Ocean.MutedText, 12, 12, FontWeights.Medium, 4), () => ShowDiffPanel(conversation, reply, null), 6, new Thickness(6, 0, 6, 0), Ocean.RowHover);
        openDiff.Height = 24;
        openDiff.ToolTip = "Open the full diff";
        AutomationProperties.SetName(openDiff, "Open diff");
        tools.Children.Add(openDiff);
        DockPanel.SetDock(tools, Dock.Right);
        header.Children.Add(tools);
        var summary = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        summary.Children.Add(Ocean.Text($"{changes.Count} changed {(changes.Count == 1 ? "file" : "files")}", Ocean.Foreground, 12, FontWeights.Medium));
        summary.Children.Add(new TextBlock { Text = $"  +{Count(changes.Sum(change => change.Insertions))}", FontFamily = Ocean.Mono, FontSize = 12, Foreground = Addition, VerticalAlignment = VerticalAlignment.Center });
        summary.Children.Add(new TextBlock { Text = $" -{Count(changes.Sum(change => change.Deletions))}", FontFamily = Ocean.Mono, FontSize = 12, Foreground = Deletion, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(summary);

        DrawTree();
        return new Border
        {
            Margin = new Thickness(4, 16, 4, 0),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0x4F, 0x67, 0x7B)), // input/20
            Child = new StackPanel { Children = { header, body } },
        };
    }

    // A row of the tree: 28 high, indented 8 + 14 a level, its counts at the right in two narrow columns.
    private static FrameworkElement ChangeRow(int depth, FrameworkElement[] icons, TextBlock name, int added, int removed, Action click, string label)
    {
        var counts = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        counts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        counts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        var plus = new TextBlock { Text = $"+{Count(added)}", FontFamily = Ocean.Mono, FontSize = 10, Foreground = added > 0 ? Addition : Ocean.MutedText, HorizontalAlignment = HorizontalAlignment.Right };
        var minus = new TextBlock { Text = $"-{Count(removed)}", FontFamily = Ocean.Mono, FontSize = 10, Foreground = removed > 0 ? Deletion : Ocean.MutedText, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(minus, 1);
        counts.Children.Add(plus);
        counts.Children.Add(minus);
        var line = new DockPanel();
        DockPanel.SetDock(counts, Dock.Right);
        line.Children.Add(counts);
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var icon in icons)
        {
            icon.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(icon);
        }
        left.Children.Add(name);
        line.Children.Add(left);
        var button = Ocean.Ghost(line, click, 6, new Thickness(8 + depth * 14, 0, 8, 0), new SolidColorBrush(Color.FromArgb(0x99, 0x32, 0x4E, 0x66)));
        button.Height = 28;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(button, label);
        return button;
    }
}
