using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>
/// The Assets page is cards like the others, but each asset also says what it is (its type, such
/// as a 3D model or a sound effect), which story beat, character, level or mechanic it belongs to,
/// and what it's tagged with.
/// </summary>
public partial class MainWindow
{
    private const string AssetsPage = "Assets";
    private const int MostAssetTags = 20;
    private const int LongestAssetTag = 32;

    // What an asset is as a file, rather than what it's for, grouped the way it's made.
    private static readonly (string Group, (string Id, string Name)[] Types)[] AssetTypes =
    [
        ("2D", [("image", "Image"), ("sprite_sheet", "Sprite sheet"), ("tileset", "Tileset"), ("tilemap", "Tilemap"), ("texture", "Texture")]),
        ("3D", [("model", "3D model"), ("material", "Material"), ("rig", "Rig"), ("animation", "Animation"), ("vfx", "VFX"), ("shader", "Shader")]),
        ("Audio", [("sound_effect", "Sound effect"), ("music", "Music"), ("voice", "Voice line"), ("ambience", "Ambience")]),
        ("Other", [("font", "Font"), ("video", "Video")]),
    ];

    // The pages whose cards an asset can belong to.
    private static readonly string[] AssetOwnerPages = ["Story", "Characters", "Levels", "Mechanics"];

    private static string? AssetTypeName(string? id) => AssetTypes.SelectMany(group => group.Types).FirstOrDefault(type => type.Id == id).Name;

    // The card an asset belongs to, if it's still there, and the page it's on.
    private (BoardCard Card, string Page)? AssetOwner(string projectId, string stage, AssetDetails details)
    {
        if (details.Owner is null) return null;
        foreach (var page in AssetOwnerPages)
        {
            if (_database.ListCards(projectId, stage, page.ToLowerInvariant()).FirstOrDefault(card => card.Id == details.Owner) is { } card) return (card, page);
        }
        return null;
    }

    // Under an asset's name: what it is and what it belongs to, then its tags.
    private StackPanel AssetCardLines(Project project, BoardCard card)
    {
        var details = _database.GetAssetDetails(card.Id);
        var owner = AssetOwner(project.Id, card.Board.Split('/')[0], details);
        var said = AssetTypeName(details.Type) ?? "No type";
        if (owner is { } belongs) said += $" · {(belongs.Card.Name.Length > 0 ? belongs.Card.Name : $"Untitled {CardPages[belongs.Page].One}")}";

        var lines = new StackPanel { Margin = new Thickness(2, 2, 2, 0) };
        lines.Children.Add(new TextBlock { Text = said, Foreground = (Brush)FindResource("TextMuted"), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = said });
        if (details.Tags is { Length: > 0 } tags)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
            foreach (var tag in tags)
            {
                row.Children.Add(new Border
                {
                    BorderBrush = (Brush)FindResource("Line"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(7, 1, 7, 2),
                    Margin = new Thickness(0, 0, 4, 4),
                    Child = new TextBlock { Text = tag, FontSize = 11, Foreground = (Brush)FindResource("TextMuted") },
                });
            }
            lines.Children.Add(row);
        }
        return lines;
    }

    // Tags as they're kept: tidied, each once whatever its case, none too long, and not too many.
    private static string[] TidyTags(string typed) =>
    [
        .. typed.Split(',')
            .Select(tag => string.Join(' ', tag.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(tag => tag.Length is > 0 and <= LongestAssetTag)
            .DistinctBy(tag => tag.ToLowerInvariant())
            .Take(MostAssetTags),
    ];

    private void EditAssetDetails(UIElement by, Project project, BoardCard card)
    {
        var stage = card.Board.Split('/')[0];
        var details = _database.GetAssetDetails(card.Id);
        TextBlock Label(string text, double above = 0) => new() { Text = text, FontSize = 12, FontWeight = FontWeights.Medium, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(0, above, 0, 6) };
        ComboBoxItem Heading(string text) => new() { Content = text, IsEnabled = false, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextMuted") };
        ComboBoxItem Option(string text, string? value) => new() { Content = text, Tag = value, Padding = new Thickness(14, 3, 8, 3) };

        // What it is, by group.
        var type = new ComboBox { Style = (Style)FindResource("Choice"), Width = double.NaN };
        AutomationProperties.SetName(type, "Type");
        type.Items.Add(new ComboBoxItem { Content = "No type", Tag = null });
        foreach (var (group, types) in AssetTypes)
        {
            type.Items.Add(Heading(group));
            foreach (var (id, name) in types) type.Items.Add(Option(name, id));
        }
        type.SelectedItem = type.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.IsEnabled && (string?)item.Tag == details.Type) ?? type.Items[0];

        // What it belongs to: nothing, or a card on one of the pages it can belong to.
        var owner = new ComboBox { Style = (Style)FindResource("Choice"), Width = double.NaN };
        AutomationProperties.SetName(owner, "Belongs to");
        owner.Items.Add(new ComboBoxItem { Content = "Nothing", Tag = null });
        foreach (var page in AssetOwnerPages)
        {
            var cards = _database.ListCards(project.Id, stage, page.ToLowerInvariant());
            if (cards.Count == 0) continue;
            owner.Items.Add(Heading(page));
            foreach (var each in cards) owner.Items.Add(Option(each.Name.Length > 0 ? each.Name : $"Untitled {CardPages[page].One}", each.Id));
        }
        owner.SelectedItem = owner.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.IsEnabled && (string?)item.Tag == details.Owner) ?? owner.Items[0];

        var tags = Field(string.Join(", ", details.Tags ?? []));
        tags.Width = double.NaN;
        AutomationProperties.SetName(tags, "Tags, with commas between them");

        var rows = new StackPanel { Width = 280, Margin = new Thickness(8, 6, 8, 6) };
        var asked = NewMenu(by, atMouse: false, rows);
        void Save()
        {
            asked.IsOpen = false;
            var tidied = TidyTags(tags.Text);
            var changed = new AssetDetails((string?)(type.SelectedItem as ComboBoxItem)?.Tag, (string?)(owner.SelectedItem as ComboBoxItem)?.Tag, tidied.Length > 0 ? tidied : null);
            if (changed.Type == details.Type && changed.Owner == details.Owner && (changed.Tags ?? []).SequenceEqual(details.Tags ?? [])) return;
            _database.SetAssetDetails(card.Id, changed);
            RedrawProject(project);
            FocusCardAfterRedraw(card.Id);
        }
        tags.KeyDown += (_, pressed) =>
        {
            if (pressed.Key != Key.Enter) return;
            pressed.Handled = true;
            Save();
        };
        asked.PreviewKeyDown += (_, pressed) =>
        {
            if (pressed.Key != Key.Escape || type.IsDropDownOpen || owner.IsDropDownOpen) return;
            pressed.Handled = true;
            asked.IsOpen = false;
        };

        var accept = ControlButton("Save", Save);
        accept.Margin = new Thickness(0, 12, 0, 0);
        accept.HorizontalAlignment = HorizontalAlignment.Left;
        rows.Children.Add(Label("Type"));
        rows.Children.Add(type);
        rows.Children.Add(Label("Belongs to", above: 12));
        rows.Children.Add(owner);
        rows.Children.Add(Label("Tags, with commas between them", above: 12));
        rows.Children.Add(tags);
        rows.Children.Add(accept);

        asked.Opened += (_, _) => type.Focus();
        asked.IsOpen = true;
    }
}
