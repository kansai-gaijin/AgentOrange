using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace AgentOrange;

/// <summary>
/// T3 Code's model picker (its ProviderModelPicker), read from the T3 Code build on this PC: a column
/// of logins down the left with Favorites at the top, the models of the one picked with a search box
/// above them, and how hard it thinks in a column of its own on the right. Picking another login's
/// model switches the conversation to that login, so there's no separate login control.
/// </summary>
public sealed partial class AgentsWindow
{
    private const double PickerHeight = 346; // h-86.5
    private const string FavoritesTab = "favorites";

    // Claude Code's efforts as T3 Code lists them, Medium its default.
    private static readonly (string Id, string Name)[] ClaudeEfforts = [("low", "Low"), ("medium", "Medium"), ("high", "High"), ("xhigh", "Extra High"), ("max", "Max")];
    private const string ClaudeDefaultEffort = "medium";

    private static readonly SolidColorBrush RailBack = Freeze(new SolidColorBrush(Color.FromArgb(0x4D, 0x23, 0x35, 0x44))); // muted/30
    private static readonly SolidColorBrush ColumnBack = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0x23, 0x35, 0x44))); // muted/40
    private static readonly SolidColorBrush Wash10 = Freeze(new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFA, 0xFF))); // foreground/10
    private static readonly SolidColorBrush Wash9 = Freeze(new SolidColorBrush(Color.FromArgb(0x17, 0xFF, 0xFA, 0xFF))); // foreground/[0.09]
    private static readonly SolidColorBrush Warning = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xB1, 0x00)));

    private HashSet<string>? _favorites;

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private sealed record PickerModel(AgentProvider Login, string Id, string Name);

    // A login's models: Claude's own list, or what the Codex login last listed.
    private static List<PickerModel> ModelsOf(AgentProvider login) => login.Kind == ProviderKind.Codex
        ? AgentConversation.CodexModels.TryGetValue(login.Id, out var listed) ? [.. listed.Select(model => new PickerModel(login, model.Id, model.Name))] : []
        : [.. Models.Where(model => model.Id is not null).Select(model => new PickerModel(login, model.Id!, model.Name))];

    /// <summary>How hard a model can think, with the one it uses unless told: Claude's from T3 Code's list, Codex's as it lists them.</summary>
    private static List<(string Id, string Name, bool IsDefault)> EffortsFor(AgentProvider login, string? model)
    {
        if (login.Kind != ProviderKind.Codex) return [.. ClaudeEfforts.Select(effort => (effort.Id, effort.Name, effort.Id == ClaudeDefaultEffort))];
        var listed = AgentConversation.CodexModels.TryGetValue(login.Id, out var models)
            ? models.FirstOrDefault(each => each.Id == model) ?? models.FirstOrDefault(each => each.IsDefault)
            : null;
        return listed is null ? [] : [.. listed.Efforts.Select(effort => (effort, EffortName(effort), effort == listed.DefaultEffort))];
    }

    private static string EffortName(string effort) => effort switch
    {
        "xhigh" => "Extra High",
        { Length: > 0 } => char.ToUpperInvariant(effort[0]) + effort[1..],
        _ => effort,
    };

    // More than one login of a kind: each one's icon carries a badge to tell them apart, as T3 Code's do.
    private bool NeedsBadge(AgentProvider login) => _main.AllAgentLogins().Count(each => each.Kind == login.Kind) > 1;

    // The login's own part of its name, such as "WO" for "Codex Work" or "WORK CLAUDE": the provider's name isn't what tells them apart.
    private static readonly string[] ProviderWords = ["claude", "code", "codex", "openai", "chatgpt"];

    private string Badge(AgentProvider login)
    {
        static string Short(AgentProvider each)
        {
            var words = each.Label.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var own = words.FirstOrDefault(word => !ProviderWords.Contains(word.ToLowerInvariant())) ?? words.LastOrDefault() ?? "?";
            return (own.Length > 2 ? own[..2] : own).ToUpperInvariant();
        }

        // Two that would still look alike are numbered instead.
        var same = _main.AllAgentLogins().Where(each => each.Kind == login.Kind).ToList();
        var mine = Short(login);
        return same.Count(each => Short(each) == mine) > 1 ? (same.FindIndex(each => each.Id == login.Id) + 1).ToString() : mine;
    }

    /// <summary>A provider's mark, with its login's badge in the corner when there's more than one of its kind.</summary>
    private FrameworkElement ProviderMark(AgentProvider? login, bool claude, double size, Brush ring)
    {
        var mark = AgentIcons.Provider(claude, size, Ocean.Foreground);
        if (login is null || !NeedsBadge(login)) return mark;
        var badge = new Border
        {
            Height = 12,
            MinWidth = 12,
            Padding = new Thickness(2, 0, 2, 0),
            CornerRadius = new CornerRadius(6),
            Background = Ocean.Control,
            BorderBrush = ring,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -4, -3),
            Child = new TextBlock { Text = Badge(login), FontSize = 7, FontWeight = FontWeights.Bold, Foreground = Ocean.Foreground, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            IsHitTestVisible = false,
        };
        return new Grid { Width = size, Height = size, Children = { mark, badge } };
    }

    // Favourites stay on this PC, in the agent files folder, as "login/model".
    private HashSet<string> Favorites()
    {
        if (_favorites is not null) return _favorites;
        try
        {
            var file = Path.Combine(_main.AgentFolder, "model-favorites.json");
            _favorites = File.Exists(file) ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(file)) ?? [] : [];
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't read the favourite models: {error.Message}");
            _favorites = [];
        }
        return _favorites;
    }

    private void ToggleFavorite(PickerModel model)
    {
        var favorites = Favorites();
        var key = $"{model.Login.Id}/{model.Id}";
        if (!favorites.Remove(key)) favorites.Add(key);
        try
        {
            File.WriteAllText(Path.Combine(_main.AgentFolder, "model-favorites.json"), JsonSerializer.Serialize(favorites));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Couldn't save the favourite models: {error.Message}");
        }
    }

    private bool IsFavorite(PickerModel model) => Favorites().Contains($"{model.Login.Id}/{model.Id}");

    private void PickModel(UIElement by)
    {
        if (_open is not { } conversation) return;
        var current = _main.AgentProviderFor(conversation);
        var all = _main.AllAgentLogins();
        var usable = _main.AgentLogins(conversation).Select(login => login.Id).ToHashSet();
        var tab = current?.Id ?? all.FirstOrDefault()?.Id ?? FavoritesTab;
        var highlighted = 0;
        List<PickerModel> shown = [];
        Popup? menu = null;

        var rail = new StackPanel { Margin = new Thickness(4) };
        var list = new StackPanel { Margin = new Thickness(4) };
        var efforts = new StackPanel { Margin = new Thickness(8, 10, 8, 6) };
        var effortHeader = new DockPanel();
        var search = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Ocean.Foreground,
            CaretBrush = Ocean.Foreground,
            SelectionBrush = Ocean.Focus,
            FontSize = 14,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(search, "Search models");

        // The model it's set to, or the login's own choice when none was picked.
        bool IsSelected(PickerModel model)
        {
            if (model.Login.Id != current?.Id) return false;
            if (conversation.Model is { } chosen) return model.Id == chosen;
            return model.Login.Kind == ProviderKind.Codex && AgentConversation.CodexModels.TryGetValue(model.Login.Id, out var listed) && listed.FirstOrDefault(each => each.IsDefault)?.Id == model.Id;
        }

        void Pick(PickerModel model)
        {
            menu!.IsOpen = false;
            if (model.Login.Id != current?.Id) _main.SwitchLogin(conversation, model.Login);
            conversation.Model = model.Id;
            if (conversation.Effort is { } effort && !EffortsFor(model.Login, model.Id).Any(each => each.Id == effort)) conversation.Effort = null;
            ShowControls();
            ShowUsage();
        }

        void DrawRail()
        {
            rail.Children.Clear();
            FrameworkElement Item(string id, FrameworkElement icon, string tip, bool enabled)
            {
                var button = Ocean.Ghost(icon, () =>
                {
                    if (!enabled) return;
                    tab = id;
                    highlighted = 0;
                    DrawRail();
                    DrawList();
                }, 6, null, enabled ? Wash10 : Brushes.Transparent);
                button.Width = 36;
                button.Height = 36;
                button.ToolTip = tip;
                ToolTipService.SetPlacement(button, PlacementMode.Right);
                ToolTipService.SetShowOnDisabled(button, true);
                button.Opacity = enabled ? 1 : 0.5;
                button.Cursor = enabled ? Cursors.Hand : Cursors.No;
                AutomationProperties.SetName(button, tip);
                var item = new Grid { Margin = new Thickness(0, 0, 0, 4), Children = { button } };
                if (id == tab)
                {
                    item.Children.Add(new Border { Width = 3, Height = 20, CornerRadius = new CornerRadius(1.5), Background = Ocean.Primary, HorizontalAlignment = HorizontalAlignment.Right, IsHitTestVisible = false });
                }
                return item;
            }

            rail.Children.Add(Item(FavoritesTab, AgentIcons.Line("star", 20, Ocean.Foreground, fill: Ocean.Foreground), "Favorites", true));
            rail.Children.Add(new Border { Height = 1, Background = Ocean.Border70, Margin = new Thickness(0, 0, 0, 4) });
            foreach (var login in all)
            {
                var enabled = usable.Contains(login.Id);
                var tip = enabled ? login.Label : $"{login.Label} can't take over this conversation. Start a new one to use it.";
                rail.Children.Add(Item(login.Id, ProviderMark(login, login.Kind != ProviderKind.Codex, 20, Ocean.Glass), tip, enabled));
            }
        }

        void DrawList()
        {
            list.Children.Clear();
            var words = search.Text.Trim();
            IEnumerable<PickerModel> models;
            if (tab == FavoritesTab) models = all.Where(login => usable.Contains(login.Id)).SelectMany(ModelsOf).Where(IsFavorite);
            else if (all.FirstOrDefault(login => login.Id == tab) is { } login) models = ModelsOf(login).OrderBy(model => IsFavorite(model) ? 0 : 1);
            else models = [];
            if (words.Length > 0) models = models.Where(model => model.Name.Contains(words, StringComparison.OrdinalIgnoreCase) || model.Id.Contains(words, StringComparison.OrdinalIgnoreCase));
            shown = [.. models];
            highlighted = Math.Clamp(highlighted, 0, Math.Max(0, shown.Count - 1));

            if (shown.Count == 0)
            {
                var empty = tab == FavoritesTab && words.Length == 0 ? "No favorites yet"
                    : all.FirstOrDefault(login => login.Id == tab) is { Kind: ProviderKind.Codex } && words.Length == 0 ? "Asking Codex for its models…"
                    : "No models found";
                list.Children.Add(new TextBlock { Text = empty, Foreground = Ocean.MutedText, FontSize = 14, Margin = new Thickness(8, 8, 8, 8) });
                return;
            }

            for (var at = 0; at < shown.Count; at++)
            {
                var model = shown[at];
                var favorite = IsFavorite(model);
                var row = new DockPanel { LastChildFill = true };

                // On the right: the star (only on hover unless it's a favourite) and its Ctrl+number.
                var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
                DockPanel.SetDock(right, Dock.Right);
                var star = Ocean.Ghost(AgentIcons.Line("star", 12, favorite ? Warning : Ocean.MutedText, fill: favorite ? Warning : null), () =>
                {
                    ToggleFavorite(model);
                    DrawList();
                }, 4);
                star.Click += (_, e) => e.Handled = true; // Not a pick as well.
                star.Width = 20;
                star.Height = 20;
                star.ToolTip = favorite ? "Remove from favorites" : "Add to favorites";
                star.Opacity = favorite ? 1 : 0;
                AutomationProperties.SetName(star, (string)star.ToolTip);
                right.Children.Add(star);
                if (at < 9)
                {
                    right.Children.Add(new Border
                    {
                        Margin = new Thickness(6, 0, 0, 0),
                        Padding = new Thickness(4, 1, 4, 1),
                        CornerRadius = new CornerRadius(4),
                        Background = Ocean.Control,
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock { Text = $"Ctrl+{at + 1}", Foreground = Ocean.MutedText, FontSize = 11 },
                    });
                }
                row.Children.Add(right);

                var mark = ProviderMark(model.Login, model.Login.Kind != ProviderKind.Codex, 16, Ocean.Glass);
                mark.Margin = new Thickness(0, 0, 8, 0);
                DockPanel.SetDock(mark, Dock.Left);
                row.Children.Add(mark);
                var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var label = Ocean.Text(model.Name, Ocean.Foreground, 14);
                label.TextTrimming = TextTrimming.CharacterEllipsis;
                name.Children.Add(label);
                if (IsSelected(model))
                {
                    var tick = AgentIcons.Line("check", 14, Ocean.Foreground);
                    tick.Margin = new Thickness(8, 0, 0, 0);
                    name.Children.Add(tick);
                }
                row.Children.Add(name);

                var index = at;
                var button = Ocean.MenuRow(row, () => Pick(model));
                button.MinHeight = 32;
                button.Background = index == highlighted ? Ocean.Accent : Brushes.Transparent;
                button.ToolTip = model.Login.Label == model.Name ? null : $"{model.Login.Label} · {model.Name}";
                ToolTipService.SetInitialShowDelay(button, 800);
                AutomationProperties.SetName(button, $"{model.Name}, {model.Login.Label}");
                button.MouseEnter += (_, _) => star.Opacity = 1;
                button.MouseLeave += (_, _) => star.Opacity = IsFavorite(model) ? 1 : 0;
                list.Children.Add(button);
            }
        }

        // How hard the conversation's own model thinks, picked without closing, as in T3 Code.
        void DrawEfforts()
        {
            efforts.Children.Clear();
            effortHeader.Children.Clear();
            string? label = null;
            if (current is not null && conversation.Model is { } picked) label = ModelsOf(current).FirstOrDefault(each => each.Id == picked)?.Name ?? picked;
            else if (current is { Kind: ProviderKind.Codex } codex && AgentConversation.CodexModels.TryGetValue(codex.Id, out var listed)) label = listed.FirstOrDefault(each => each.IsDefault)?.Name;
            label ??= "Default model";
            var title = Ocean.Text("Reasoning", Ocean.MutedText, 14);
            DockPanel.SetDock(title, Dock.Left);
            effortHeader.Children.Add(title);
            var modelName = Ocean.Text(label, Ocean.MutedText, 12);
            modelName.HorizontalAlignment = HorizontalAlignment.Right;
            modelName.TextTrimming = TextTrimming.CharacterEllipsis;
            modelName.Margin = new Thickness(8, 0, 0, 0);
            effortHeader.Children.Add(modelName);

            List<(string Id, string Name, bool IsDefault)> choices = current is null ? [] : EffortsFor(current, conversation.Model);
            if (choices.Count == 0)
            {
                efforts.Children.Add(new TextBlock { Text = "No effort levels", Foreground = Ocean.MutedText, FontSize = 14, Margin = new Thickness(8, 6, 8, 6) });
                return;
            }
            foreach (var (id, name, isDefault) in choices)
            {
                var chosen = conversation.Effort is { } effort ? effort == id : isDefault;
                var row = new DockPanel();
                var tick = AgentIcons.Line("check", 14, chosen ? Ocean.Foreground : Brushes.Transparent);
                tick.Margin = new Thickness(8, 0, 0, 0);
                DockPanel.SetDock(tick, Dock.Right);
                row.Children.Add(tick);
                if (isDefault)
                {
                    var tag = Ocean.Text("Default", Ocean.MutedText, 12);
                    tag.Opacity = 0.7;
                    DockPanel.SetDock(tag, Dock.Right);
                    row.Children.Add(tag);
                }
                row.Children.Add(Ocean.Text(name, Ocean.Foreground, 14));
                var button = Ocean.Ghost(row, () =>
                {
                    conversation.Effort = id;
                    DrawEfforts();
                    ShowControls();
                }, 4, new Thickness(8, 0, 8, 0), Wash9);
                button.Height = 32;
                button.Margin = new Thickness(0, 0, 0, 2);
                button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                AutomationProperties.SetName(button, $"{name}{(isDefault ? ", default" : "")}");
                efforts.Children.Add(button);
            }
        }

        // The search box, with its icon, across the top of the models.
        var hint = Ocean.Text("Search models...", Ocean.Placeholder, 14);
        hint.IsHitTestVisible = false;
        search.TextChanged += (_, _) =>
        {
            hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            highlighted = 0;
            DrawList();
        };
        var searchIcon = AgentIcons.Line("search", 16, Ocean.MutedText);
        searchIcon.Margin = new Thickness(0, 0, 8, 0);
        var searchRow = new DockPanel();
        DockPanel.SetDock(searchIcon, Dock.Left);
        searchRow.Children.Add(searchIcon);
        searchRow.Children.Add(new Grid { Children = { hint, search } });
        var searchBar = new Border { Height = 40, Padding = new Thickness(12, 0, 12, 0), BorderBrush = Ocean.Border70, BorderThickness = new Thickness(0, 0, 0, 1), Child = searchRow };

        var listScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = list };
        var listColumn = new DockPanel { Background = ColumnBack };
        DockPanel.SetDock(searchBar, Dock.Top);
        listColumn.Children.Add(searchBar);
        listColumn.Children.Add(listScroll);

        var railColumn = new Border
        {
            Width = 44,
            Background = RailBack,
            CornerRadius = new CornerRadius(7, 0, 0, 7),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = rail },
        };

        var effortTop = new Border { Margin = new Thickness(12, 10, 12, 0), Height = 33, Padding = new Thickness(0, 0, 0, 6), BorderBrush = Ocean.Border70, BorderThickness = new Thickness(0, 0, 0, 1), Child = effortHeader };
        var effortColumn = new DockPanel();
        DockPanel.SetDock(effortTop, Dock.Top);
        effortColumn.Children.Add(effortTop);
        effortColumn.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = efforts });
        var effortSide = new Border
        {
            Width = 192,
            Background = ColumnBack,
            BorderBrush = Ocean.Border70,
            BorderThickness = new Thickness(1, 0, 0, 0),
            CornerRadius = new CornerRadius(0, 7, 7, 0),
            Child = effortColumn,
        };

        var layout = new Grid { Height = PickerHeight };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(316) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(listColumn, 1);
        Grid.SetColumn(effortSide, 2);
        layout.Children.Add(railColumn);
        layout.Children.Add(listColumn);
        layout.Children.Add(effortSide);

        // Keys, as in T3 Code: up and down move through the models, Enter picks, Ctrl+1 to 9 pick straight away.
        layout.PreviewKeyDown += (_, e) =>
        {
            if (KeyState.Modifiers == ModifierKeys.Control && e.Key is >= Key.D1 and <= Key.D9 && e.Key - Key.D1 < shown.Count)
            {
                e.Handled = true;
                Pick(shown[e.Key - Key.D1]);
                return;
            }
            switch (e.Key)
            {
                case Key.Down when shown.Count > 0:
                    highlighted = (highlighted + 1) % shown.Count;
                    DrawList();
                    e.Handled = true;
                    break;
                case Key.Up when shown.Count > 0:
                    highlighted = (highlighted - 1 + shown.Count) % shown.Count;
                    DrawList();
                    e.Handled = true;
                    break;
                case Key.Enter when shown.Count > 0:
                    Pick(shown[highlighted]);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    menu!.IsOpen = false;
                    e.Handled = true;
                    break;
            }
        };

        // T3 Code's footer, under all three: what it may do, as a menu of its own.
        var footer = new Grid();
        var modeWords = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button? modeButton = null;
        void DrawMode()
        {
            var mode = ModeOf(conversation);
            var risky = mode.Id == "bypassPermissions";
            modeWords.Children.Clear();
            var icon = AgentIcons.Line(mode.Icon, 14, risky ? Ocean.Error : Ocean.MutedText);
            icon.Margin = new Thickness(0, 0, 6, 0);
            modeWords.Children.Add(icon);
            modeWords.Children.Add(Ocean.Text(mode.Name, risky ? Ocean.Error : Ocean.MutedText, 12));
            var chevron = AgentIcons.Line("chevron-up", 12, Ocean.MutedText);
            chevron.Margin = new Thickness(6, 0, 0, 0);
            modeWords.Children.Add(chevron);
            if (modeButton is null) return;
            modeButton.ToolTip = mode.Meaning;
            AutomationProperties.SetName(modeButton, $"Permissions: {mode.Name}");
        }
        modeButton = Ocean.Ghost(modeWords, () =>
        {
            // Inside the picker, so opening it doesn't count as clicking away from the picker.
            if (PermissionMenu(modeButton!, DrawMode) is not { } modes) return;
            footer.Children.Add(modes);
            modes.Closed += (_, _) => footer.Children.Remove(modes);
            modes.IsOpen = true;
        }, 6, new Thickness(8, 0, 8, 0), Wash9);
        modeButton.Height = 28;
        modeButton.HorizontalAlignment = HorizontalAlignment.Right;
        DrawMode();
        footer.Children.Add(modeButton);
        var footerBar = new Border
        {
            Background = ColumnBack,
            BorderBrush = Ocean.Border70,
            BorderThickness = new Thickness(0, 1, 0, 0),
            CornerRadius = new CornerRadius(0, 0, 7, 7),
            Padding = new Thickness(12, 6, 12, 6),
            Child = footer,
        };
        railColumn.CornerRadius = new CornerRadius(7, 0, 0, 0);
        effortSide.CornerRadius = new CornerRadius(0, 7, 0, 0);
        var whole = new DockPanel();
        DockPanel.SetDock(footerBar, Dock.Bottom);
        whole.Children.Add(footerBar);
        whole.Children.Add(layout);

        DrawRail();
        DrawList();
        DrawEfforts();
        menu = Ocean.Menu(by, PlacementMode.Top, whole);
        ((Border)menu.Child).Padding = new Thickness(0);
        menu.Opened += (_, _) => Dispatcher.InvokeAsync(() => Keyboard.Focus(search), System.Windows.Threading.DispatcherPriority.Input);
        menu.IsOpen = true;
    }
}
