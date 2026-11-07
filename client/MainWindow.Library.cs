using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace AgentOrange;

/// <summary>The project library: its cards, their covers, and dragging them into order.</summary>
public partial class MainWindow
{
    private const int CoverWidth = 176;
    private const int CoverHeight = 264;
    private const double DragScrollEdge = 48;
    private const double DragScrollStep = 18;
    private static readonly TimeSpan ClickSuppressionAfterDrag = TimeSpan.FromMilliseconds(400);

    // Refreshes are frequent during a big sync, and most covers haven't changed.
    private readonly Dictionary<string, (byte[] Bytes, ImageSource? Image)> _covers = [];
    private (string ProjectId, byte[] Bytes, ImageSource? Image)? _header;

    private Button? _pressedCard;
    private Button? _draggedCard;
    private Point _pressPoint;
    private DateTimeOffset _dragEnded;
    private bool _refreshAfterDrag;
    private bool _gearPressed; // Not a card being pressed, as that could start a drag, but a rebuild would still remove it.

    private void RefreshProjects()
    {
        // A rebuild while a card is pressed would remove it, and the click would be lost.
        if (_draggedCard is not null || _pressedCard is not null || _gearPressed)
        {
            _refreshAfterDrag = true;
            return;
        }

        var projects = _database.ListProjects();
        foreach (var gone in _covers.Keys.Except(projects.Select(project => project.Id)).ToList()) _covers.Remove(gone);

        // Rebuilding the cards would otherwise send someone using the keyboard back to the top.
        var focusedCard = ProjectGrid.Children.OfType<Button>().FirstOrDefault(card => card.IsKeyboardFocusWithin)?.Tag as string;

        ProjectGrid.Children.Clear();
        foreach (var project in projects) ProjectGrid.Children.Add(ProjectCard(project));
        if (focusedCard is not null) ProjectGrid.Children.OfType<Button>().FirstOrDefault(card => (string)card.Tag == focusedCard)?.Focus();
        NoProjects.Visibility = projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // No one can reach a clone's Cancel button once its project is gone.
        if (_job is { } job && projects.All(project => project.Id != job.ProjectId)) job.Cancel?.Cancel();

        ProjectSettings.ProjectsChanged();
        if (_openProject is null) return;

        // The open project may have changed on another PC.
        var current = projects.FirstOrDefault(project => project.Id == _openProject.Id);
        if (current is null)
        {
            BackToProjects();
        }
        else if (!GitTools.SameRepo(current.RepoUrl, _openProject.RepoUrl))
        {
            _ = OpenProjectAsync(current);
        }
        else if (!_confirmingNewRepo)
        {
            RenderProjectKeepingFocus(current); // Its header image syncs as a separate record. Not while someone's typing.
        }
    }

    // Library

    private Button ProjectCard(Project project)
    {
        var thumbnail = CoverFor(project.Id);
        var cover = new Border
        {
            Width = CoverWidth,
            Height = CoverHeight,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Background = thumbnail is null ? PlaceholderFor(project.Id) : new ImageBrush(thumbnail) { Stretch = Stretch.UniformToFill },
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.35 },
        };

        if (thumbnail is null)
        {
            cover.Child = new TextBlock
            {
                Text = project.Name,
                Foreground = Brushes.White,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14),
            };
        }

        // Settings stay reachable from here, because a blocked project can't be opened.
        var gear = new Button
        {
            Style = (Style)FindResource("OverlayButton"),
            Content = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 13 },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 8, 0),
            Visibility = Visibility.Hidden,
        };
        AutomationProperties.SetName(gear, $"Settings for {project.Name}");
        gear.PreviewMouseLeftButtonDown += (_, _) =>
        {
            _pressedCard = null;
            _gearPressed = true;
        };
        gear.Click += (_, e) =>
        {
            e.Handled = true;
            OpenProjectSettings(project.Id);
        };

        var notHere = new TextBlock { Text = "Not on this PC", Foreground = (Brush)FindResource("Accent"), FontSize = 12.5, Margin = new Thickness(2, 2, 2, 0), Visibility = Visibility.Collapsed };
        _ = ShowIfNotOnThisPcAsync(project.Id, notHere);

        var coverArea = new Grid { Children = { cover, gear } };
        var content = new StackPanel { Children = { coverArea } };
        content.Children.Add(new TextBlock { Text = project.Name, Foreground = (Brush)FindResource("TextStrong"), FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 10, 2, 0), ToolTip = project.Name });
        content.Children.Add(new TextBlock { Text = project.Summary, Foreground = (Brush)FindResource("TextMuted"), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 2, 2, 0), ToolTip = project.Summary });
        content.Children.Add(notHere);

        // Long operations carry on after leaving the page, so the card says how it's going.
        var ownJob = IsBusyWith(project.Id) ? _job : null;
        var cardStatus = ownJob is not null ? ownJob.CardText ?? ownJob.Text : _heldProblems.ContainsKey(project.Id) ? "Open it to see what happened" : null;
        if (cardStatus is not null)
        {
            var statusLabel = new TextBlock { Text = cardStatus, Foreground = (Brush)FindResource("Accent"), FontSize = 12.5, Margin = new Thickness(2, 2, 2, 0), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = cardStatus };
            content.Children.Add(statusLabel);
            if (ownJob is not null) _busyCardLabel = statusLabel;
        }

        var card = new Button { Style = (Style)FindResource("ProjectCard"), Content = content, Tag = project.Id, AllowDrop = true };
        AutomationProperties.SetName(card, project.Name);

        // The gear has to show for the keyboard too, or Tab can never reach it.
        void ShowActive()
        {
            var active = card.IsMouseOver || card.IsKeyboardFocusWithin;
            cover.BorderBrush = active ? (Brush)FindResource("Accent") : Brushes.Transparent;
            gear.Visibility = active ? Visibility.Visible : Visibility.Hidden;
        }
        card.MouseEnter += (_, _) => ShowActive();
        card.MouseLeave += (_, _) => ShowActive();
        card.IsKeyboardFocusWithinChanged += (_, _) => ShowActive();
        card.Click += async (_, _) =>
        {
            if (DateTimeOffset.UtcNow - _dragEnded < ClickSuppressionAfterDrag) return;
            await OpenProjectAsync(project);
        };

        // Reordering has to work without a mouse too.
        card.PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            var step = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -CardsPerRow(card), Key.Down => CardsPerRow(card), _ => 0 };
            if (step == 0) return;
            e.Handled = true;
            MoveCardBy(project.Id, step);
        };

        card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _pressedCard = card;
            _pressPoint = e.GetPosition(ProjectGrid);
        };
        card.PreviewMouseLeftButtonUp += (_, _) => _pressedCard = null;
        card.PreviewMouseMove += (_, e) =>
        {
            if (_pressedCard != card || e.LeftButton != MouseButtonState.Pressed || _draggedCard is not null) return;
            var moved = e.GetPosition(ProjectGrid) - _pressPoint;
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            DragCard(card);
        };
        card.DragEnter += (_, e) =>
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            MoveDraggedCardTo(card);
        };

        return card;
    }

    private int CardsPerRow(FrameworkElement card) =>
        Math.Max(1, (int)(ProjectGrid.ActualWidth / Math.Max(1, card.ActualWidth + card.Margin.Left + card.Margin.Right)));

    private void MoveCardBy(string projectId, int step)
    {
        var order = CurrentOrder();
        var from = order.IndexOf(projectId);
        var to = Math.Clamp(from + step, 0, order.Count - 1); // A shorter last row still has a place to go.
        if (from < 0 || to == from) return;

        order.RemoveAt(from);
        order.Insert(to, projectId);
        _database.SaveProjectOrder(order);
        _ = _sync.SyncNowAsync();
        RefreshProjects();
        Dispatcher.InvokeAsync(
            () => ProjectGrid.Children.OfType<Button>().FirstOrDefault(card => (string)card.Tag == projectId)?.Focus(),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private ImageSource? CoverFor(string projectId)
    {
        var bytes = _database.GetProjectImage(projectId, ProjectImageKind.Thumbnail);
        if (bytes is null)
        {
            _covers.Remove(projectId);
            return null;
        }
        if (_covers.TryGetValue(projectId, out var cached) && cached.Bytes.AsSpan().SequenceEqual(bytes)) return cached.Image;

        var image = ProjectImages.ToImage(bytes, CoverWidth * 2);
        _covers[projectId] = (bytes, image);
        return image;
    }

    // A disconnected network drive can take a long time to answer, so this stays off the UI thread.
    private async Task ShowIfNotOnThisPcAsync(string projectId, TextBlock label)
    {
        var folder = _database.GetProjectFolder(projectId);
        var here = folder is not null && await GitTools.FolderExistsAsync(folder);
        label.Text = folder is null ? "Not on this PC" : "Folder not found"; // It can just be on a drive that's unplugged.
        label.Visibility = here ? Visibility.Collapsed : Visibility.Visible;
    }

    // Colors picked from the id, so a project keeps its look on every PC.
    private static Brush PlaceholderFor(string projectId)
    {
        (Color, Color)[] pairs =
        [
            (Color.FromRgb(0x24, 0x5B, 0x68), Color.FromRgb(0x1B, 0x2C, 0x3C)),
            (Color.FromRgb(0x3B, 0x4F, 0x7A), Color.FromRgb(0x1C, 0x26, 0x3A)),
            (Color.FromRgb(0x4D, 0x45, 0x78), Color.FromRgb(0x20, 0x20, 0x38)),
            (Color.FromRgb(0x2F, 0x62, 0x55), Color.FromRgb(0x18, 0x2E, 0x2C)),
        ];
        var total = projectId.Sum(character => character);
        var (top, bottom) = pairs[total % pairs.Length];
        return new LinearGradientBrush(top, bottom, 60);
    }

    private void DragCard(Button card)
    {
        _draggedCard = card;
        _pressedCard = null;
        card.Opacity = 0.45;
        card.ReleaseMouseCapture();

        var before = CurrentOrder();
        var result = DragDrop.DoDragDrop(card, new DataObject("AgentOrange.ProjectCard", card.Tag), DragDropEffects.Move);

        card.Opacity = 1;
        _draggedCard = null;
        _dragEnded = DateTimeOffset.UtcNow;
        Keyboard.ClearFocus(); // Or the card stays highlighted after the mouse has left it.

        // Esc, or dropping outside the library, puts everything back.
        var after = CurrentOrder();
        if (result == DragDropEffects.Move && !before.SequenceEqual(after))
        {
            _database.SaveProjectOrder(after);
            _ = _sync.SyncNowAsync();
        }
        else if (!before.SequenceEqual(after))
        {
            _refreshAfterDrag = true;
        }

        if (_refreshAfterDrag)
        {
            _refreshAfterDrag = false;
            RefreshProjects();
        }
    }

    private void MoveDraggedCardTo(Button target)
    {
        if (_draggedCard is null || _draggedCard == target) return;
        var index = ProjectGrid.Children.IndexOf(target);
        ProjectGrid.Children.Remove(_draggedCard);
        ProjectGrid.Children.Insert(index, _draggedCard);
    }

    private List<string> CurrentOrder() =>
        ProjectGrid.Children.OfType<Button>().Select(card => (string)card.Tag).ToList();

    private void ProjectGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = _draggedCard is null ? DragDropEffects.None : DragDropEffects.Move;
        e.Handled = true;

        // A long library has to scroll, or a card can't reach the far end in one move.
        var y = e.GetPosition(ProjectScroller).Y;
        if (y < DragScrollEdge) ProjectScroller.ScrollToVerticalOffset(ProjectScroller.VerticalOffset - DragScrollStep);
        else if (y > ProjectScroller.ActualHeight - DragScrollEdge) ProjectScroller.ScrollToVerticalOffset(ProjectScroller.VerticalOffset + DragScrollStep);
    }

    private void ProjectGrid_Drop(object sender, DragEventArgs e)
    {
        e.Effects = _draggedCard is null ? DragDropEffects.None : DragDropEffects.Move;
        e.Handled = true;
    }
}
