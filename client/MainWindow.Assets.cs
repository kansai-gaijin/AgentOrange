using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>
/// The Assets page is cards like the others, but each asset also says what it is (its type, such
/// as a 3D model or a sound effect), which story beat, character, level or mechanic it belongs to,
/// and which of the project's tags it has. All of it can be given as the asset is added. Tags are
/// picked from a list on the asset, and made, renamed and deleted only in the tag editor.
/// </summary>
public partial class MainWindow
{
    private const string AssetsPage = "Assets";
    private const string AssetsPart = "assets";

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

    private static string CardName(BoardCard card, string page) => card.Name.Length > 0 ? card.Name : $"Untitled {CardPages[page].One}";

    private static string OneOf(string page) => char.ToUpperInvariant(CardPages[page].One[0]) + CardPages[page].One[1..];

    // Every card in a stage an asset could belong to, and the page each is on.
    private List<(BoardCard Card, string Page)> AssetOwnerChoices(string projectId, string stage) =>
        [.. AssetOwnerPages.SelectMany(page => _database.ListCards(projectId, stage, page.ToLowerInvariant()).Select(card => (card, page)))];

    // Under an asset's name: what it is and what it belongs to, then its tags.
    private StackPanel AssetCardLines(Project project, BoardCard card)
    {
        var details = _database.GetAssetDetails(card.Id);
        var said = AssetTypeName(details.Type) ?? "No type";
        var owner = AssetOwnerChoices(project.Id, card.Board.Split('/')[0]).FirstOrDefault(choice => choice.Card.Id == details.Owner);
        if (owner.Card is not null) said += $" · {CardName(owner.Card, owner.Page)}";

        var lines = new StackPanel { Margin = new Thickness(2, 2, 2, 0) };
        lines.Children.Add(new TextBlock { Text = said, Foreground = (Brush)FindResource("TextMuted"), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = said });

        var tags = _database.ListAssetTags(project.Id).Where(tag => details.Tags?.Contains(tag.Id) == true).ToList();
        if (tags.Count > 0)
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
                    Child = new TextBlock { Text = tag.Name, FontSize = 11, Foreground = (Brush)FindResource("TextMuted") },
                });
            }
            lines.Children.Add(row);
        }
        return lines;
    }

    // Small parts the asset form and the tag editor are made of.

    private TextBlock FormLabel(string text, double above = 0) => new() { Text = text, FontSize = 12, FontWeight = FontWeights.Medium, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(0, above, 0, 6) };

    // A row in a list to pick from, with a tick or a note on its right.
    private Button PickRow(string name, string? aside, Action chosen)
    {
        var content = new DockPanel();
        if (aside is not null)
        {
            var side = new TextBlock { Text = aside, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(14, 0, 0, 0) };
            DockPanel.SetDock(side, Dock.Right);
            content.Children.Add(side);
        }
        content.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
        var row = new Button { Style = (Style)FindResource("HeaderButton"), Content = content, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(row, aside == "✓" ? $"{name}, chosen" : name);
        row.Click += (_, _) => chosen();
        return row;
    }

    private TextBlock ListHeading(string text) => new() { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(7, 8, 7, 2) };

    private TextBlock ListNote(string text) => new() { Text = text, FontSize = 13, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(7, 4, 7, 4), TextWrapping = TextWrapping.Wrap };

    // Looks like a field, and opens a list under it.
    private Button DropButton(string name, out TextBlock shown)
    {
        shown = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        var arrow = new TextBlock { Text = "▾", Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(10, 0, 0, 0) };
        DockPanel.SetDock(arrow, Dock.Right);
        var button = new Button { Style = (Style)FindResource("ControlButton"), HorizontalAlignment = HorizontalAlignment.Stretch, Content = new DockPanel { Children = { arrow, shown } } };
        AutomationProperties.SetName(button, name);
        return button;
    }

    // A search box over a list, which typing anywhere in the list goes into.
    private (Grid Box, TextBox Search) SearchBox(string hint)
    {
        var search = Field("");
        search.Width = double.NaN;
        AutomationProperties.SetName(search, hint);
        var shownHint = new TextBlock { Text = hint, Foreground = (Brush)FindResource("TextMuted"), FontSize = 13, Margin = new Thickness(11, 6, 0, 0), IsHitTestVisible = false };
        search.TextChanged += (_, _) => shownHint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        return (new Grid { Margin = new Thickness(0, 0, 0, 4), Children = { search, shownHint } }, search);
    }

    // Up and Down move through a list's rows and back up to its search, and Enter in the search picks the first.
    private static void MoveThroughList(UIElement list, TextBox search, Panel rows)
    {
        list.PreviewTextInput += (_, typed) =>
        {
            if (search.IsKeyboardFocused || typed.Text.Length == 0 || char.IsControl(typed.Text[0]) || typed.Text == " ") return;
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
    }

    private Border OpenUnder() => new()
    {
        BorderBrush = (Brush)FindResource("Line"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(6),
        Margin = new Thickness(0, 6, 0, 0),
        Visibility = Visibility.Collapsed,
    };

    /// <summary>Adds an asset, or changes one, with its name, type, what it belongs to and its tags.</summary>
    private void OpenAssetForm(UIElement by, Project project, string stage, BoardCard? card)
    {
        var details = card is null ? new AssetDetails() : _database.GetAssetDetails(card.Id);
        var owner = details.Owner;
        var chosenTags = new HashSet<string>(details.Tags ?? []);
        var (saved, tagsChanged) = (false, false);

        var rows = new StackPanel { Width = 300, Margin = new Thickness(8, 6, 8, 6) };
        var asked = NewMenu(by, atMouse: false, rows);
        var problem = ProblemText();
        problem.Margin = new Thickness(0, 6, 0, 0);

        rows.Children.Add(new TextBlock { Text = card is null ? "New asset" : $"{CardName(card, AssetsPage)}", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextStrong"), Margin = new Thickness(0, 0, 0, 10), TextTrimming = TextTrimming.CharacterEllipsis });

        // Its name, only when it's being added, as a card is renamed on its own.
        TextBox? name = null;
        if (card is null)
        {
            name = Field("");
            name.Width = double.NaN;
            name.MaxLength = LocalDatabase.LongestCardName;
            AutomationProperties.SetName(name, "Name");
            rows.Children.Add(FormLabel("Name"));
            rows.Children.Add(name);
        }

        // What it is, by group.
        var type = new ComboBox { Style = (Style)FindResource("Choice"), Width = double.NaN };
        AutomationProperties.SetName(type, "Type");
        type.Items.Add(new ComboBoxItem { Content = "No type", Tag = null });
        foreach (var (group, types) in AssetTypes)
        {
            type.Items.Add(new ComboBoxItem { Content = group, IsEnabled = false, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextMuted") });
            foreach (var (id, typeName) in types) type.Items.Add(new ComboBoxItem { Content = typeName, Tag = id, Padding = new Thickness(14, 3, 8, 3) });
        }
        type.SelectedItem = type.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.IsEnabled && (string?)item.Tag == details.Type) ?? type.Items[0];
        rows.Children.Add(FormLabel("Type", above: card is null ? 12 : 0));
        rows.Children.Add(type);

        // What it belongs to, searched for in a list that opens under it.
        var owners = AssetOwnerChoices(project.Id, stage);
        var ownerButton = DropButton("Belongs to", out var ownerShown);
        var (ownerSearchBox, ownerSearch) = SearchBox("Search");
        var ownerRows = new StackPanel();
        var ownerList = OpenUnder();
        ownerList.Child = new StackPanel { Children = { ownerSearchBox, new ScrollViewer { MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = ownerRows } } };

        void ShowOwner()
        {
            var chosen = owners.FirstOrDefault(choice => choice.Card.Id == owner);
            ownerShown.Text = chosen.Card is null ? "Nothing" : $"{CardName(chosen.Card, chosen.Page)} · {OneOf(chosen.Page)}";
        }
        void PickOwner(string? id)
        {
            owner = id;
            ShowOwner();
            ownerList.Visibility = Visibility.Collapsed;
            ownerButton.Focus();
        }
        void ListOwners()
        {
            var typed = ownerSearch.Text.Trim();
            ownerRows.Children.Clear();
            if (typed.Length == 0) ownerRows.Children.Add(PickRow("Nothing", owner is null ? "✓" : null, () => PickOwner(null)));
            foreach (var page in AssetOwnerPages)
            {
                var matching = owners.Where(choice => choice.Page == page && CardName(choice.Card, page).Contains(typed, StringComparison.CurrentCultureIgnoreCase)).ToList();
                if (matching.Count == 0) continue;
                ownerRows.Children.Add(ListHeading(page));
                foreach (var (each, _) in matching) ownerRows.Children.Add(PickRow(CardName(each, page), each.Id == owner ? "✓" : null, () => PickOwner(each.Id)));
            }
            if (owners.Count == 0) ownerRows.Children.Add(ListNote("No story beats, characters, levels or mechanics in this stage yet."));
            else if (typed.Length > 0 && ownerRows.Children.Count == 0) ownerRows.Children.Add(ListNote("Nothing matches."));
        }
        ownerSearch.TextChanged += (_, _) => ListOwners();
        MoveThroughList(ownerList, ownerSearch, ownerRows);
        ownerButton.Click += (_, _) =>
        {
            var opening = ownerList.Visibility != Visibility.Visible;
            ownerList.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
            if (!opening) return;
            ownerSearch.Text = "";
            ListOwners();
            ownerSearch.Focus();
        };
        ShowOwner();
        rows.Children.Add(FormLabel("Belongs to", above: 12));
        rows.Children.Add(ownerButton);
        rows.Children.Add(ownerList);

        // Its tags, ticked in a list of the project's that opens under it, which can turn into the tag editor.
        var tagsButton = DropButton("Tags", out var tagsShown);
        var tagsList = OpenUnder();

        void ShowTags()
        {
            var names = _database.ListAssetTags(project.Id).Where(tag => chosenTags.Contains(tag.Id)).Select(tag => tag.Name).ToList();
            tagsShown.Text = names.Count == 0 ? "No tags" : string.Join(", ", names);
            tagsShown.Foreground = (Brush)FindResource(names.Count == 0 ? "TextMuted" : "TextStrong");
        }
        void ShowTagChoices()
        {
            var all = _database.ListAssetTags(project.Id);
            var (searchBox, search) = SearchBox("Search tags");
            var tagRows = new StackPanel();
            void ListTags()
            {
                var typed = search.Text.Trim();
                tagRows.Children.Clear();
                foreach (var tag in all.Where(tag => tag.Name.Contains(typed, StringComparison.CurrentCultureIgnoreCase)))
                {
                    tagRows.Children.Add(PickRow(tag.Name, chosenTags.Contains(tag.Id) ? "✓" : null, () =>
                    {
                        if (!chosenTags.Remove(tag.Id)) chosenTags.Add(tag.Id);
                        var at = tagRows.Children.OfType<Button>().ToList().FindIndex(row => row.IsKeyboardFocused);
                        ListTags();
                        ShowTags();
                        if (at >= 0 && tagRows.Children.OfType<Button>().ElementAtOrDefault(at) is { } again) again.Focus();
                    }));
                }
                if (all.Count == 0) tagRows.Children.Add(ListNote("No tags yet. Make them in the tag editor."));
                else if (tagRows.Children.Count == 0) tagRows.Children.Add(ListNote("No tags match."));
            }
            search.TextChanged += (_, _) => ListTags();
            ListTags();

            var edit = TextButton("Edit tags", () =>
            {
                ShowTagEditor();
            });
            edit.HorizontalAlignment = HorizontalAlignment.Left;
            edit.Margin = new Thickness(0, 4, 0, 0);
            var view = new StackPanel { Children = { searchBox, new ScrollViewer { MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = tagRows }, edit } };
            MoveThroughList(view, search, tagRows);
            tagsList.Child = view;
            Dispatcher.InvokeAsync(() => search.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }
        void ShowTagEditor()
        {
            var done = TextButton("Done", ShowTagChoices);
            done.HorizontalAlignment = HorizontalAlignment.Left;
            done.Margin = new Thickness(0, 6, 0, 0);
            tagsList.Child = new StackPanel
            {
                Children =
                {
                    TagEditor(project, () =>
                    {
                        tagsChanged = true;
                        ShowTags();
                    }),
                    done,
                },
            };
        }
        tagsButton.Click += (_, _) =>
        {
            var opening = tagsList.Visibility != Visibility.Visible;
            tagsList.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
            if (opening) ShowTagChoices();
        };
        ShowTags();
        rows.Children.Add(FormLabel("Tags", above: 12));
        rows.Children.Add(tagsButton);
        rows.Children.Add(tagsList);

        void Save()
        {
            var typedName = name?.Text.Trim();
            if (typedName is { Length: 0 })
            {
                ShowProblem(problem, "Give it a name.");
                name!.Focus();
                return;
            }

            // Only tags that are still there.
            var stillThere = _database.ListAssetTags(project.Id).Select(tag => tag.Id).ToHashSet();
            var tags = chosenTags.Where(stillThere.Contains).Order(StringComparer.Ordinal).ToArray();
            var changed = new AssetDetails((string?)(type.SelectedItem as ComboBoxItem)?.Tag, owner, tags.Length > 0 ? tags : null);
            var same = changed.Type == details.Type && changed.Owner == details.Owner && tags.SequenceEqual((details.Tags ?? []).Where(stillThere.Contains).Order(StringComparer.Ordinal));

            saved = true;
            asked.IsOpen = false;
            var asset = card ?? _database.AddCard(project.Id, stage, AssetsPart, typedName!);
            if (!same) _database.SetAssetDetails(asset.Id, changed);
            RedrawProject(project);
            FocusCardAfterRedraw(asset.Id);
        }

        var accept = ControlButton(card is null ? "Add" : "Save", Save);
        accept.Margin = new Thickness(0, 14, 0, 0);
        accept.HorizontalAlignment = HorizontalAlignment.Left;
        rows.Children.Add(accept);
        rows.Children.Add(problem);

        if (name is not null)
        {
            name.KeyDown += (_, pressed) =>
            {
                if (pressed.Key != Key.Enter) return;
                pressed.Handled = true;
                Save();
            };
        }

        // Esc closes a list that's open under a field first, then the form.
        asked.PreviewKeyDown += (_, pressed) =>
        {
            if (pressed.Key != Key.Escape || type.IsDropDownOpen) return;
            pressed.Handled = true;
            if (ownerList.IsKeyboardFocusWithin)
            {
                ownerList.Visibility = Visibility.Collapsed;
                ownerButton.Focus();
            }
            else if (tagsList.IsKeyboardFocusWithin)
            {
                tagsList.Visibility = Visibility.Collapsed;
                tagsButton.Focus();
            }
            else
            {
                asked.IsOpen = false;
            }
        };

        // Tags made or changed in the editor are kept, even if the asset isn't saved.
        asked.Closed += (_, _) =>
        {
            if (!saved && tagsChanged) RedrawProject(project);
        };
        asked.Opened += (_, _) => (name ?? (UIElement)type).Focus();
        asked.IsOpen = true;
    }

    /// <summary>The project's asset tags, to add, rename and delete. The only place they change.</summary>
    private void OpenTagEditor(UIElement by, Project project)
    {
        var changed = false;
        var rows = new StackPanel { Width = 300, Margin = new Thickness(8, 6, 8, 6) };
        rows.Children.Add(new TextBlock { Text = "Asset tags", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextStrong"), Margin = new Thickness(0, 0, 0, 10) });
        rows.Children.Add(TagEditor(project, () => changed = true));
        var editor = NewMenu(by, atMouse: false, rows);
        editor.PreviewKeyDown += (_, pressed) =>
        {
            if (pressed.Key != Key.Escape) return;
            pressed.Handled = true;
            editor.IsOpen = false;
        };
        editor.Closed += (_, _) =>
        {
            if (changed) RedrawProject(project);
        };
        editor.IsOpen = true;
    }

    // Each tag's name to change, a button to delete it, and a field to add one.
    private StackPanel TagEditor(Project project, Action changed)
    {
        var panel = new StackPanel();

        void Show(bool focusNew = false)
        {
            panel.Children.Clear();

            var tags = _database.ListAssetTags(project.Id);
            var list = new StackPanel();
            foreach (var tag in tags)
            {
                var rename = Field(tag.Name);
                rename.Width = double.NaN;
                rename.MaxLength = LocalDatabase.LongestAssetTag;
                AutomationProperties.SetName(rename, $"Name of the tag {tag.Name}");
                var renameProblem = ProblemText();

                // Not one deleted meanwhile, which would bring it back.
                void Rename()
                {
                    var typed = LocalDatabase.TidyTagName(rename.Text);
                    if (typed == tag.Name || !_database.ListAssetTags(project.Id).Any(each => each.Id == tag.Id)) return;
                    if (_database.AssetTagProblem(project.Id, typed, renaming: tag.Id) is { } wrong)
                    {
                        ShowProblem(renameProblem, wrong);
                        return;
                    }
                    _database.RenameAssetTag(tag, typed);
                    changed();
                    Show();
                }
                rename.KeyDown += (_, pressed) =>
                {
                    if (pressed.Key != Key.Enter) return;
                    pressed.Handled = true;
                    Rename();
                };
                rename.LostKeyboardFocus += (_, _) => Rename();

                // Two clicks, as the assets that have it lose it.
                var delete = new Button { Style = (Style)FindResource("HeaderButton"), Content = "Delete", Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(delete, $"Delete the tag {tag.Name}");
                var armed = false;
                delete.Click += (_, _) =>
                {
                    if (!armed)
                    {
                        armed = true;
                        delete.Content = "Sure?";
                        delete.Foreground = (Brush)FindResource("Danger");
                        return;
                    }
                    _database.DeleteAssetTag(tag);
                    changed();
                    Show();
                };
                delete.LostKeyboardFocus += (_, _) =>
                {
                    armed = false;
                    delete.Content = "Delete";
                    delete.Foreground = (Brush)FindResource("TextMuted");
                };

                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                DockPanel.SetDock(delete, Dock.Right);
                row.Children.Add(delete);
                row.Children.Add(rename);
                list.Children.Add(row);
                list.Children.Add(renameProblem);
            }
            if (tags.Count == 0) list.Children.Add(ListNote("No tags yet."));
            panel.Children.Add(new ScrollViewer { MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list });

            // A new one.
            var newName = Field("");
            newName.Width = double.NaN;
            newName.MaxLength = LocalDatabase.LongestAssetTag;
            AutomationProperties.SetName(newName, "Name of a new tag");
            var addProblem = ProblemText();
            void Add()
            {
                var typed = LocalDatabase.TidyTagName(newName.Text);
                if (_database.AssetTagProblem(project.Id, typed) is { } wrong)
                {
                    ShowProblem(addProblem, wrong);
                    return;
                }
                _database.AddAssetTag(project.Id, typed);
                changed();
                Show(focusNew: true);
            }
            newName.KeyDown += (_, pressed) =>
            {
                if (pressed.Key != Key.Enter) return;
                pressed.Handled = true;
                Add();
            };
            var add = ControlButton("Add", Add);
            add.Margin = new Thickness(6, 0, 0, 0);
            var addRow = new DockPanel();
            DockPanel.SetDock(add, Dock.Right);
            addRow.Children.Add(add);
            addRow.Children.Add(newName);
            panel.Children.Add(FormLabel("New tag", above: 6));
            panel.Children.Add(addRow);
            panel.Children.Add(addProblem);
            if (focusNew) Dispatcher.InvokeAsync(() => newName.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }

        Show();
        return panel;
    }
}
