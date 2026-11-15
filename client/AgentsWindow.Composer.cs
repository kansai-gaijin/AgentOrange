using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgentOrange;

/// <summary>
/// The box's parts, as T3 Code's composer has them: pictures and files waiting to go, the controls
/// beside the send button (what it may do, which login, which model and how hard it thinks), and the
/// agent's questions, answered in a panel that docks on top of the box.
/// </summary>
public sealed partial class AgentsWindow
{
    private readonly WrapPanel _tray = new() { Margin = new Thickness(0, 0, 0, 12), Visibility = Visibility.Collapsed };
    private readonly List<AgentAttachment> _pending = [];
    private readonly Border _questionBanner = new() { Visibility = Visibility.Collapsed };

    // Pictures and files to go with the next message

    private void PickFiles()
    {
        if (_open is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Send to the agent", Multiselect = true, InitialDirectory = _main.ProjectFolderOf(_open) ?? "" };
        if (dialog.ShowDialog(this) == true) AttachFiles(dialog.FileNames);
    }

    private bool PasteAttachments()
    {
        try
        {
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            {
                AttachImage(image);
                return true;
            }
            if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList() is { Count: > 0 } files)
            {
                AttachFiles(files.Cast<string>());
                return true;
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another program has the clipboard open. The paste goes in as words, if it can.
        }
        return false;
    }

    private void AttachFiles(IEnumerable<string> files)
    {
        if (_open is null) return;
        foreach (var file in files)
        {
            if (Directory.Exists(file)) continue;
            if (AgentAttachments.FromFile(file, _main.AttachmentsFolder) is { } attachment) _pending.Add(attachment);
        }
        ShowTray();
        _input.Focus();
    }

    /// <summary>A picture for the next message, such as a screenshot.</summary>
    internal void AttachImage(BitmapSource image)
    {
        if (_open is null) return;
        if (AgentAttachments.SaveImage(image, _main.AttachmentsFolder) is { } attachment) _pending.Add(attachment);
        ShowTray();
        _input.Focus();
    }

    /// <summary>A screenshot taken with the key: for the conversation open here, or a new one about the project on screen.</summary>
    internal void AttachScreenshotHere(BitmapSource image)
    {
        if (_open is null && _main.ProjectOnScreen is { } project)
        {
            Show(_main.NewConversation(project));
            ShowConversations();
        }
        if (_open is null)
        {
            ShowNote("Open a project first, so the screenshot has a conversation to go to.");
            return;
        }
        AttachImage(image);
    }

    // The window gets out of the way for it, so what's under it can be taken.
    private async Task ShootAsync()
    {
        if (_open is null) return;
        var state = WindowState;
        Hide();
        await Task.Delay(200);
        var taken = await AgentAttachments.TakeScreenshotAsync();
        Show();
        WindowState = state;
        Activate();
        if (taken is not null) AttachImage(taken);
    }

    // T3 Code's strip: 64 px tiles with 8 px corners, each with its own dark close button.
    private void ShowTray()
    {
        _tray.Children.Clear();
        _tray.Visibility = _pending.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var attachment in _pending.ToList())
        {
            var remove = Ocean.Ghost(AgentIcons.Line("x", 14, Brushes.White), () =>
            {
                _pending.Remove(attachment);
                ShowTray();
            }, 8, hover: new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0)), background: new SolidColorBrush(Color.FromArgb(0xA6, 0, 0, 0)));
            remove.Width = remove.Height = 24;
            remove.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            remove.BorderThickness = new Thickness(1);
            remove.HorizontalAlignment = HorizontalAlignment.Right;
            remove.VerticalAlignment = VerticalAlignment.Top;
            remove.Margin = new Thickness(0, 4, 4, 0);
            remove.ToolTip = "Don't send this";
            AutomationProperties.SetName(remove, $"Don't send {attachment.Name}");
            _tray.Children.Add(new Grid { Margin = new Thickness(0, 0, 8, 0), Children = { TrayFace(attachment), remove } });
        }
        ShowSendButton();
    }

    private FrameworkElement TrayFace(AgentAttachment attachment)
    {
        if (attachment.IsImage && AgentAttachments.LoadImage(attachment.Path, 128) is { } picture)
        {
            var tile = new Border
            {
                Width = 64,
                Height = 64,
                CornerRadius = new CornerRadius(8),
                BorderBrush = Ocean.Border80,
                BorderThickness = new Thickness(1),
                Background = new ImageBrush(picture) { Stretch = Stretch.UniformToFill },
                Cursor = Cursors.Hand,
                ToolTip = attachment.Path,
            };
            tile.MouseLeftButtonUp += (_, _) => OpenFile(attachment.Path);
            return tile;
        }
        return FileCard(attachment, 64, 200);
    }

    // T3 Code's file card: an icon on a tile, then the name and what kind of file it is.
    private FrameworkElement FileCard(AgentAttachment attachment, double height, double width)
    {
        var icon = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(0x99, 0x17, 0x21, 0x2B)), Child = AgentIcons.Line(attachment.IsImage ? "image" : "file-text", 16, Ocean.MutedText) };
        icon.Margin = new Thickness(0, 0, 8, 0);
        var kind = Path.GetExtension(attachment.Name).TrimStart('.').ToUpperInvariant();
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Ocean.Text(attachment.Name, Ocean.Foreground, 14, FontWeights.Medium), Ocean.Text(kind.Length > 0 ? $"{kind} file" : "File", Ocean.MutedText, 12) } };
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(words);
        var card = new Border { Height = height, Width = width, CornerRadius = new CornerRadius(16), Background = Ocean.Message, Padding = new Thickness(8, 0, 12, 0), Child = row, Cursor = Cursors.Hand, ToolTip = attachment.Path };
        card.MouseLeftButtonUp += (_, _) => OpenFile(attachment.Path);
        return card;
    }

    // On a sent message: one picture 192 px tall, several as 160 px tiles, files as cards, all opening on a click.
    private FrameworkElement SentFace(AgentAttachment attachment, bool alone)
    {
        if (attachment.IsImage && AgentAttachments.LoadImage(attachment.Path, alone ? 384 : 256) is { } picture)
        {
            var tall = alone ? 192 : 96;
            var wide = alone ? Math.Clamp(tall * picture.PixelWidth / Math.Max(1.0, picture.PixelHeight), 96, ColumnWidth * 0.8) : 160;
            var tile = new Border
            {
                Width = wide,
                Height = tall,
                CornerRadius = new CornerRadius(16),
                Background = new ImageBrush(picture) { Stretch = Stretch.UniformToFill },
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = attachment.Path,
            };
            tile.MouseLeftButtonUp += (_, _) => OpenFile(attachment.Path);
            return tile;
        }
        return FileCard(attachment, 52, 256);
    }

    private static void OpenFile(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Write($"Couldn't open {path}: {error.Message}");
        }
    }

    // The controls beside the send button

    // T3 Code's composer control: 28 px, 8 px corners, words at 80% that brighten under the mouse.
    private Button ComposerControl(UIElement content, Action click, string name)
    {
        var button = Ocean.Ghost(content, click, 8, new Thickness(8, 0, 8, 0));
        button.Height = 28;
        button.ToolTip = name;
        AutomationProperties.SetName(button, name);
        return button;
    }

    private void ShowControls()
    {
        _controls.Children.Clear();
        ShowPendingHandoff();
        if (_open is not { } conversation) return;

        // How full its memory is, as a ring.
        if (conversation.ContextWindow > 0)
        {
            var fraction = Math.Clamp((double)conversation.ContextUsed / conversation.ContextWindow, 0, 1);
            var tone = fraction >= 0.9 ? Ocean.Error : fraction >= 0.8 ? Ocean.Warning : Ocean.MutedText;
            var words = Ocean.Text($"{fraction * 100:0}%", Ocean.Secondary, 12);
            words.Margin = new Thickness(6, 0, 0, 0);
            var ring = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Children = { Ring(fraction, tone), words }, Background = Brushes.Transparent };
            ring.ToolTip = $"Context: {AgentConversation.Tokens(conversation.ContextUsed)} of {AgentConversation.Tokens(conversation.ContextWindow)} tokens. Near the end it sums up what came before to make room.";
            AutomationProperties.SetName(ring, $"Context {fraction * 100:0}% full");
            _controls.Children.Add(ring);
        }

        // As T3 Code: the mode shows here only when it isn't the usual one. It's always in the model picker's footer.
        var mode = ModeOf(conversation);
        if (mode.Id != "auto")
        {
            var risky = mode.Id == "bypassPermissions";
            var modeRow = Ocean.IconText(mode.Icon, mode.Name, risky ? Ocean.Error : Ocean.Soft, 14, 16, FontWeights.Medium);
            ((FrameworkElement)modeRow.Children[0]).Opacity = risky ? 1 : 0.8;
            _controls.Children.Add(ComposerControl(modeRow, () => PickPermission(_controls.Children[^1]), mode.Meaning));
        }

        // The model and how hard it thinks, as one control: "✳ Opus 5.5 High".
        var model = ModelChoices(conversation).FirstOrDefault(each => each.Id == conversation.Model).Name ?? conversation.Model ?? "Default model";
        var login = _main.AgentProviderFor(conversation);
        var effort = login is null ? conversation.Effort : EffortsFor(login, conversation.Model).FirstOrDefault(each => each.Id == conversation.Effort).Name ?? conversation.Effort;
        var glyph = ProviderMark(login, IsClaude(conversation), 16, Ocean.Composer);
        glyph.Margin = new Thickness(0, 0, 6, 0);
        var modelRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { glyph, Ocean.Text(model, Ocean.Soft, 14, FontWeights.Medium) } };
        if (conversation.Effort is not null) modelRow.Children.Add(Ocean.Text($" {effort}", Ocean.MutedText, 14, FontWeights.Medium));
        var modelControl = ComposerControl(modelRow, () => { }, login is null ? "Model and effort, from the next message" : $"{login.Label}: model and effort, from the next message");
        modelControl.Click += (_, _) => PickModel(modelControl);
        _controls.Children.Add(modelControl);
    }

    private FrameworkElement Ring(double fraction, Brush brush)
    {
        const double Size = 16, Thick = 2;
        var radius = (Size - Thick) / 2;
        var grid = new Grid { Width = Size, Height = Size };
        grid.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = Ocean.Border, StrokeThickness = Thick });
        if (fraction >= 0.999)
        {
            grid.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = brush, StrokeThickness = Thick });
        }
        else if (fraction > 0.005)
        {
            var angle = fraction * 2 * Math.PI;
            var centre = Size / 2;
            var figure = new PathFigure { StartPoint = new Point(centre, centre - radius), IsClosed = false };
            figure.Segments.Add(new ArcSegment(new Point(centre + radius * Math.Sin(angle), centre - radius * Math.Cos(angle)), new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true));
            grid.Children.Add(new System.Windows.Shapes.Path { Stroke = brush, StrokeThickness = Thick, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Data = new PathGeometry([figure]) });
        }
        return grid;
    }

    // Claude's models, or the ones the Codex login listed when it started.
    private (string? Id, string Name)[] ModelChoices(AgentConversation conversation)
    {
        if (IsClaude(conversation)) return Models;
        var listed = _main.AgentProviderFor(conversation) is { } login && AgentConversation.CodexModels.TryGetValue(login.Id, out var models) ? models : [];
        return [(null, "Default model"), .. listed.Select(model => ((string?)model.Id, model.Name))];
    }

    private static FrameworkElement Tick(bool on)
    {
        var tick = AgentIcons.Line("check", 14, on ? Ocean.Foreground : Brushes.Transparent);
        tick.Margin = new Thickness(12, 0, 0, 0);
        return tick;
    }

    private static (string Id, string Name, string Icon, string Meaning) ModeOf(AgentConversation conversation)
    {
        var mode = Permissions.FirstOrDefault(each => each.Id == conversation.PermissionMode);
        return mode.Id is null ? Permissions[1] : mode;
    }

    private void PickPermission(UIElement by)
    {
        if (PermissionMenu(by, null) is { } menu) menu.IsOpen = true;
    }

    /// <summary>The modes as a menu over what opened it, which says when one's picked.</summary>
    private Popup? PermissionMenu(UIElement by, Action? picked)
    {
        if (_open is not { } conversation) return null;
        Popup? menu = null;
        var rows = new StackPanel { Width = 300 };
        foreach (var (id, name, icon, meaning) in Permissions)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var tick = Tick(id == conversation.PermissionMode);
            DockPanel.SetDock(tick, Dock.Right);
            row.Children.Add(tick);
            var glyph = AgentIcons.Line(icon, 16, id == "bypassPermissions" ? Ocean.Error : Ocean.MutedText);
            glyph.Margin = new Thickness(0, 2, 8, 0);
            glyph.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(glyph, Dock.Left);
            row.Children.Add(glyph);
            row.Children.Add(new StackPanel { Children = { Ocean.Text(name, Ocean.Foreground, 14), Ocean.Wrapped(meaning, Ocean.MutedText, 12, 16) } });
            rows.Children.Add(Ocean.MenuRow(row, () =>
            {
                menu!.IsOpen = false;
                conversation.SetPermissionMode(id);
                ShowControls();
                picked?.Invoke();
            }));
        }
        rows.Children.Add(new TextBlock { Text = "Takes effect straight away, even while it works.", Foreground = Ocean.MutedText, FontSize = 11, Margin = new Thickness(8, 6, 8, 2) });
        menu = Ocean.Menu(by, PlacementMode.Top, rows);
        return menu;
    }

    // Its questions, docked on top of the box as T3 Code's user input panel: a radio for each choice,
    // several where it says so, or the person's own words.

    private void ShowQuestions()
    {
        var item = _open?.Items.LastOrDefault(each => each.Kind == AgentItemKind.Question && each.RequestState == AgentRequestState.Waiting);
        _questionBanner.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        if (item is null) return;
        var questions = item.Questions ?? [];
        if (!_choices.TryGetValue(item.Id, out var chosen)) chosen = _choices[item.Id] = [];

        var rows = new StackPanel();
        var send = Ocean.Ghost(Ocean.Text("Send answers", Ocean.OnPrimary, 12, FontWeights.Medium), () =>
        {
            _open?.AnswerQuestions(item, new Dictionary<string, string>(chosen));
            _choices.Remove(item.Id);
            ShowQuestions();
        }, 8, new Thickness(10, 0, 10, 0), Ocean.PrimaryHover, Ocean.Primary);
        send.Height = 24;
        send.HorizontalAlignment = HorizontalAlignment.Right;
        send.Margin = new Thickness(0, 8, 0, 2);
        void ShowReady() => send.IsEnabled = questions.All(question => chosen.GetValueOrDefault(question.Question) is { Length: > 0 });

        for (var at = 0; at < questions.Count; at++)
        {
            var question = questions[at];
            var head = new DockPanel { Margin = new Thickness(0, at == 0 ? 0 : 12, 0, 4) };
            var counter = Ocean.Text(questions.Count > 1 ? $"{at + 1}/{questions.Count}" : "", Ocean.MutedText, 12);
            DockPanel.SetDock(counter, Dock.Right);
            head.Children.Add(counter);
            head.Children.Add(Ocean.Text(question.Header.Length > 0 ? question.Header : "Question", Ocean.MutedText, 12, FontWeights.Medium));
            rows.Children.Add(head);
            var asked = Ocean.Wrapped(question.Question, new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFA, 0xFF)), 14);
            asked.Margin = new Thickness(0, 0, 0, 6);
            rows.Children.Add(asked);
            if (question.MultiSelect) rows.Children.Add(Ocean.Text("Choose any", Ocean.Secondary, 11));

            var picked = (chosen.GetValueOrDefault(question.Question) ?? "").Split(", ", StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            foreach (var option in question.Options)
            {
                var on = picked.Contains(option.Label);
                var radio = new Grid { Width = 16, Height = 16, Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top };
                radio.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Fill = on ? Ocean.Primary : new SolidColorBrush(Color.FromArgb(0x52, 0x4F, 0x67, 0x7B)),
                    Stroke = on ? Ocean.Primary : new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFA, 0xFF)),
                    StrokeThickness = 1,
                });
                if (on) radio.Children.Add(new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = Ocean.OnPrimary });
                var words = new StackPanel { Children = { Ocean.Text(option.Label, on ? Ocean.Foreground : new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFA, 0xFF)), 14, FontWeights.Medium) } };
                if (option.Description.Length > 0) words.Children.Add(Ocean.Wrapped(option.Description, Ocean.Secondary, 11, 16));
                var line = new DockPanel();
                DockPanel.SetDock(radio, Dock.Left);
                line.Children.Add(radio);
                line.Children.Add(words);
                var choice = Ocean.Ghost(line, () =>
                {
                    if (question.MultiSelect)
                    {
                        if (!picked.Remove(option.Label)) picked.Add(option.Label);
                        chosen[question.Question] = string.Join(", ", question.Options.Select(each => each.Label).Where(picked.Contains));
                    }
                    else
                    {
                        chosen[question.Question] = option.Label;
                    }
                    ShowQuestions();
                }, 6, new Thickness(6, 6, 6, 6), new SolidColorBrush(Color.FromArgb(0x4D, 0x23, 0x35, 0x44)));
                choice.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                AutomationProperties.SetName(choice, on ? $"{option.Label}, chosen" : option.Label);
                rows.Children.Add(choice);
            }

            // Or in the person's own words.
            var own = new TextBox
            {
                Text = picked.Count == 0 ? chosen.GetValueOrDefault(question.Question) ?? "" : "",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Ocean.Foreground,
                CaretBrush = Ocean.Foreground,
                SelectionBrush = Ocean.Focus,
                FontSize = 14,
                Padding = new Thickness(0),
            };
            AutomationProperties.SetName(own, $"Your own answer to: {question.Question}");
            var hint = Ocean.Text("Or say something else…", Ocean.Placeholder, 14);
            hint.IsHitTestVisible = false;
            hint.Visibility = own.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            own.TextChanged += (_, _) =>
            {
                hint.Visibility = own.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (own.Text.Trim().Length > 0) chosen[question.Question] = own.Text.Trim();
                else if (picked.Count == 0) chosen.Remove(question.Question);
                ShowReady();
            };
            rows.Children.Add(new Border { Margin = new Thickness(32, 4, 0, 0), Padding = new Thickness(0, 4, 0, 4), Child = new Grid { Children = { hint, own } } });
        }
        rows.Children.Add(send);
        ShowReady();
        _questionBanner.Child = BannerFace(rows);
    }
}
