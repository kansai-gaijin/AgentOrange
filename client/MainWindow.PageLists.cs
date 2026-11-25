using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Data.Sqlite;

namespace AgentOrange;

/// <summary>
/// What the long pages share, such as Tasks and the pages of cards: each is read away from the
/// window, then shown in a list that builds only what's in view and fills the page under its header.
/// </summary>
public partial class MainWindow
{
    // Pages read what they show away from the window, and are drawn once it's read.
    private int _pageLoads;
    private readonly Dictionary<string, int> _pageReads = [];

    internal bool PageIsLoading => _pageLoads > 0;

    /// <summary>
    /// Reads a page away from the window, then shows it. A page read again before that finishes
    /// shows only the newest, as what's changed since is in that one.
    /// </summary>
    private async void LoadPage<T>(string page, Func<T> read, Action<T> show)
    {
        var reading = _pageReads[page] = _pageReads.GetValueOrDefault(page) + 1;
        _pageLoads++;
        try
        {
            T data;
            try
            {
                data = await Task.Run(read);
            }
            catch (SqliteException)
            {
                _databaseWasLost = true; // So it's all looked at again once it's back.
                return;
            }
            if (_pageReads[page] == reading) show(data);
        }
        finally
        {
            _pageLoads--;
        }
    }

    // The list that fills the page under the header, scrolling itself, so the page around it doesn't.
    private FrameworkElement? _fittedList;
    private bool _listsFollowWindow;

    private void AddFittedList(StackPanel body, FrameworkElement list)
    {
        (list.Parent as Panel)?.Children.Remove(list);

        // A list never measured without a height, or it would build every line. Short of the room
        // there is, until it's fitted, so the page around it never has to scroll.
        if (double.IsNaN(list.Height)) list.Height = Math.Max(240, ProjectScroll.ActualHeight - 240);
        body.Children.Add(list);
        _fittedList = list;
        ProjectScroll.ScrollToVerticalOffset(0);
        _ = Dispatcher.InvokeAsync(() => FitList(list), System.Windows.Threading.DispatcherPriority.Loaded);

        if (_listsFollowWindow) return;
        _listsFollowWindow = true;
        ProjectScroll.SizeChanged += (_, _) =>
        {
            if (_fittedList is { } shown) FitList(shown);
        };
        ProjectPage.SizeChanged += (_, _) =>
        {
            if (_fittedList is { } shown) _ = Dispatcher.InvokeAsync(() => FitList(shown), System.Windows.Threading.DispatcherPriority.Loaded);
        };
    }

    // As tall as the room left under what's above it, such as the page's title and filters.
    private void FitList(FrameworkElement list)
    {
        if (!ReferenceEquals(_fittedList, list) || PresentationSource.FromVisual(list) is null) return;
        var top = list.TranslatePoint(new Point(0, 0), ProjectPage).Y;
        var height = Math.Max(240, ProjectScroll.ActualHeight - top);
        if (Math.Abs(list.Height - height) > 0.5) list.Height = height;
        if (ProjectScroll.VerticalOffset > 0 && top + height <= ProjectScroll.ActualHeight + 0.5) ProjectScroll.ScrollToVerticalOffset(0);
    }

    // A line of a list, in the middle of the page at the page's width, with the page's margins.
    private static Border InPage(FrameworkElement content, double width) => new()
    {
        MaxWidth = width + 80,
        Padding = new Thickness(40, 0, 40, 0),
        Child = content,
    };

    private ListLine NoteLine(string key, string text, double width) => new(key, text, () => InPage(new TextBlock
    {
        Text = text,
        Foreground = (Brush)FindResource("TextMuted"),
        FontSize = 13.5,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 0),
    }, width));

    // Room under the last line, as the page has below it.
    private static ListLine EndLine() => new("end", "", () => new Border { Height = 30 });

    // Records come from the server in batches, so what's here may be half of something, such as an
    // asset without its details, until the rest arrives. Nothing may be filled in here, as it would
    // be newer than the real one that follows, and win on every PC (#129).
    private async Task RecordsArrivedAsync(IReadOnlyList<SyncChange> arrived)
    {
        await Task.Run(() => _database.CleanUpDeletedProjects(arrived));
        RefreshProjects();
    }

    private static IEnumerable<Button> Buttons(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button) yield return button;
            foreach (var inside in Buttons(child)) yield return inside;
        }
    }
}
