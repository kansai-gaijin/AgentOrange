using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgentOrange;

/// <summary>
/// Nodes that stand for one of the project's cards: a story beat, character, level, mechanic or
/// asset, in the same stage as the board. Each shows its card's picture and name, which change
/// with the card, and has a pin in and a pin out. Adding one, or double-clicking it, picks the card.
/// </summary>
internal sealed partial class NodeCanvas
{
    private const string BeatReferenceKind = "beat_ref";
    private const string CharacterReferenceKind = "character_ref";
    private const string LevelReferenceKind = "level_ref";
    private const string MechanicReferenceKind = "mechanic_ref";
    private const string AssetReferenceKind = "asset_ref";
    private const double ReferenceWidth = 200;
    private const double ReferencePictureHeight = 112;

    // The page whose cards a kind stands for: where they're kept, what one and many are called.
    private static (string Part, string One, string Many)? ReferenceTo(string? kind) => kind switch
    {
        BeatReferenceKind => ("story", "story beat", "story beats"),
        CharacterReferenceKind => ("characters", "character", "characters"),
        LevelReferenceKind => ("levels", "level", "levels"),
        MechanicReferenceKind => ("mechanics", "mechanic", "mechanics"),
        AssetReferenceKind => ("assets", "asset", "assets"),
        _ => null,
    };

    // Each card's picture is opened once, and again only when it changes.
    private readonly Dictionary<string, (string Stored, ImageSource? Picture)> _cardPictures = [];

    // The stage this board is in. Its key starts with it, whether it's a page's board or a card's.
    private string BoardStage => _board.Split('/')[0];

    private ImageSource? CardPictureOf(string cardId)
    {
        if (_database.GetCardImage(cardId) is not { } stored)
        {
            _cardPictures.Remove(cardId);
            return null;
        }
        if (_cardPictures.TryGetValue(cardId, out var opened) && opened.Stored == stored) return opened.Picture;

        ImageSource? picture = null;
        try
        {
            picture = ProjectImages.ToImage(Convert.FromBase64String(stored), (int)(ReferenceWidth * 2));
        }
        catch (FormatException)
        {
            // It arrived from another PC, and isn't a picture.
        }
        _cardPictures[cardId] = (stored, picture);
        return picture;
    }

    private void AddReferenceParts(NodeView view, (string Part, string One, string Many) to)
    {
        var card = view.Data.Card is { } cardId ? _database.ListCards(_projectId, BoardStage, to.Part).FirstOrDefault(each => each.Id == cardId) : null;
        var rows = new StackPanel();
        if (card is not null && CardPictureOf(card.Id) is { } picture)
        {
            rows.Children.Add(new Border
            {
                Height = ReferencePictureHeight,
                CornerRadius = new CornerRadius(6.5, 6.5, 0, 0),
                Background = new ImageBrush(picture) { Stretch = Stretch.UniformToFill },
            });
        }

        // Until one's picked, or when the card it was is gone, it says to pick one.
        var name = card is null ? $"Choose a {to.One}" : card.Name.Length > 0 ? card.Name : $"Untitled {to.One}";
        rows.Children.Add(new StackPanel
        {
            Margin = new Thickness(12, 9, 12, 11),
            Children =
            {
                new TextBlock { Text = char.ToUpperInvariant(to.One[0]) + to.One[1..], FontSize = 11.5, FontWeight = FontWeights.Medium, Foreground = (Brush)FindResource("TextMuted") },
                new TextBlock { Text = name, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource(card is null ? "TextMuted" : "TextStrong"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) },
            },
        });

        view.Inside.Child = rows;
        view.Box.Width = ReferenceWidth;
        view.Box.Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x27, 0x32));
    }

    // The card whose board this is, when it's one an asset can belong to: a story beat, character, level or mechanic.
    private BoardCard? BoardOwnerCard()
    {
        if (_board.Split('/') is not [var stage, var part, var name] || part is not ("story" or "characters" or "levels" or "mechanics")) return null;
        var id = $"{_projectId}:{name}";
        return _database.ListCards(_projectId, stage, part).FirstOrDefault(card => card.Id == id);
    }

    // A list of the cards it can stand for, under the node, to search and pick from.
    private void ChooseCard(NodeView node)
    {
        if (ReferenceTo(node.Data.Kind) is not { } to) return;
        var id = node.Id;
        var cards = _database.ListCards(_projectId, BoardStage, to.Part);

        // On a story beat's, character's, level's or mechanic's board, an asset is one of the ones
        // that belong to it, or a new one that does.
        var asset = node.Data.Kind == AssetReferenceKind;
        var owner = asset ? BoardOwnerCard() : null;
        if (owner is not null) cards = [.. cards.Where(card => _database.GetAssetDetails(card.Id).Owner == owner.Id)];
        var ownerName = owner is null ? "" : owner.Name.Length > 0 ? owner.Name : "this card";
        Brush muted = (Brush)FindResource("TextMuted");

        var search = new TextBox { Style = (Style)FindResource("Field"), Width = double.NaN, Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetName(search, $"Search {to.Many}");
        var searchHint = new TextBlock { Text = asset ? "Search, or name a new asset" : $"Search {to.Many}", Foreground = muted, FontSize = 13, Margin = new Thickness(11, 6, 0, 0), IsHitTestVisible = false };
        var rows = new StackPanel();
        var panel = MenuPanel(new StackPanel { Children = { new Grid { Children = { search, searchHint } }, new ScrollViewer { MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = rows } } });
        panel.Width = 240;
        var list = new Popup { PlacementTarget = node.Box, Placement = PlacementMode.Bottom, VerticalOffset = 10, StaysOpen = false, AllowsTransparency = true, Child = panel };

        void Choose(BoardCard card)
        {
            list.IsOpen = false;
            Focus();
            if (!_nodes.TryGetValue(id, out var now) || now.Data.Card == card.Id) return;
            now.Data = now.Data with { Card = card.Id };
            SaveNode(now);
            Reload();
        }

        void Show(string typed)
        {
            rows.Children.Clear();
            foreach (var card in cards.Where(card => card.Name.Contains(typed, StringComparison.OrdinalIgnoreCase)))
            {
                rows.Children.Add(MenuRow(card.Name.Length > 0 ? card.Name : $"Untitled {to.One}", card.Id == node.Data.Card ? "✓" : null, () => Choose(card)));
            }
            // What's typed can be a new asset, unless one's already called that.
            var name = typed.Length > LocalDatabase.LongestCardName ? typed[..LocalDatabase.LongestCardName] : typed;
            if (asset && name.Length > 0 && !cards.Any(card => string.Equals(card.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            {
                rows.Children.Add(MenuRow($"New asset “{name}”", owner is null ? "+" : $"+ {ownerName}", () =>
                {
                    var added = _database.AddCard(_projectId, BoardStage, to.Part, name);
                    if (owner is not null) _database.SetAssetDetails(added.Id, new AssetDetails(Owner: owner.Id));
                    Choose(added);
                }));
            }

            if (rows.Children.Count > 0) return;
            var none = asset && cards.Count == 0 ? owner is null ? "No assets yet. Type a name to add one." : $"No assets belong to {ownerName} yet. Type a name to add one."
                : cards.Count == 0 ? $"No {to.Many} yet" : $"No {to.Many} match";
            rows.Children.Add(new TextBlock { Text = none, Foreground = muted, FontSize = 13, Margin = new Thickness(7, 4, 7, 4), TextWrapping = TextWrapping.Wrap });
        }

        search.TextChanged += (_, _) =>
        {
            searchHint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Show(search.Text.Trim());
        };
        list.PreviewTextInput += (_, typed) =>
        {
            if (search.IsKeyboardFocused || typed.Text.Length == 0 || char.IsControl(typed.Text[0])) return;
            search.Focus();
            search.Text += typed.Text;
            search.CaretIndex = search.Text.Length;
            typed.Handled = true;
        };
        list.PreviewKeyDown += (_, pressed) =>
        {
            var buttons = rows.Children.OfType<Button>().ToList();
            var at = Keyboard.FocusedElement is Button focused ? buttons.IndexOf(focused) : -1;
            switch (pressed.Key)
            {
                case Key.Escape:
                    list.IsOpen = false;
                    Focus();
                    break;
                case Key.Enter when search.IsKeyboardFocused:
                    buttons.FirstOrDefault()?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    break;
                case Key.Down when buttons.Count > 0:
                    buttons[(at + 1) % buttons.Count].Focus();
                    break;
                case Key.Up when at == 0:
                    search.Focus();
                    break;
                case Key.Up when at > 0:
                    buttons[at - 1].Focus();
                    break;
                default:
                    return;
            }
            pressed.Handled = true;
        };

        Show("");
        list.Opened += (_, _) => Dispatcher.BeginInvoke(() => search.Focus(), DispatcherPriority.Input);
        list.IsOpen = true;
    }
}
