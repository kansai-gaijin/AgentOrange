using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AgentOrange;

/// <summary>
/// One line of a <see cref="LazyList"/>: what it stands for, and what it shows, which is only built
/// once it scrolls into view. Two lines with the same key and version look the same, so a line that's
/// still the same when the page is read again is left as it is.
/// </summary>
internal sealed class ListLine(string key, string version, Func<FrameworkElement> build)
{
    public string Key { get; } = key;

    public string Version { get; } = version;

    public FrameworkElement Build() => build();

    public bool Same(ListLine other) => other.Key == Key && other.Version == Version;
}

/// <summary>
/// A long page that scrolls itself and builds only the lines in view, so a page of hundreds of
/// tasks or cards costs what the few on screen do. Shown again with new lines, it changes only the
/// ones that differ, and keeps where it was scrolled to.
/// </summary>
internal sealed class LazyList : ItemsControl
{
    private readonly Lines _lines = [];

    public LazyList()
    {
        ItemsSource = _lines;
        Focusable = false;
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(this, ScrollUnit.Pixel);
        VirtualizingPanel.SetCacheLength(this, new VirtualizationCacheLength(1, 1));
        VirtualizingPanel.SetCacheLengthUnit(this, VirtualizationCacheLengthUnit.Page);
        ScrollViewer.SetCanContentScroll(this, true);
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Continue);
        ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));

        var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
        scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        scroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
        scroll.SetValue(FocusableProperty, false);
        scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        Template = new ControlTemplate(typeof(ItemsControl)) { VisualTree = scroll };
    }

    public int Count => _lines.Count;

    /// <summary>Puts these lines in place of the ones shown, changing only those that differ.</summary>
    public void Show(IReadOnlyList<ListLine> lines)
    {
        // Most changes are a line or two in place, such as a task ticked: each is swapped alone.
        if (lines.Count == _lines.Count && lines.Select((line, at) => line.Key == _lines[at].Key).All(same => same))
        {
            for (var at = 0; at < lines.Count; at++)
            {
                if (!_lines[at].Same(lines[at])) _lines[at] = lines[at];
            }
            return;
        }

        // Anything else, such as a milestone folded away, keeps the same lines at either end.
        var first = 0;
        while (first < lines.Count && first < _lines.Count && _lines[first].Same(lines[first])) first++;
        var fromEnd = 0;
        while (fromEnd < lines.Count - first && fromEnd < _lines.Count - first && _lines[^(fromEnd + 1)].Same(lines[^(fromEnd + 1)])) fromEnd++;
        var removed = _lines.Count - first - fromEnd;
        var added = lines.Skip(first).Take(lines.Count - first - fromEnd).ToList();
        if (removed + added.Count > 40)
        {
            _lines.ReplaceAll(lines);
            return;
        }
        for (var i = 0; i < removed; i++) _lines.RemoveAt(first);
        for (var i = 0; i < added.Count; i++) _lines.Insert(first + i, added[i]);
    }

    /// <summary>Scrolls a line into view, when it isn't, so what's on it can take the keyboard.</summary>
    public void BringLineIntoView(string key)
    {
        var at = _lines.Select((line, index) => (line, index)).FirstOrDefault(each => each.line.Key == key).index;
        if (_lines.Count == 0 || _lines[at].Key != key) return;
        if (ItemContainerGenerator.ContainerFromIndex(at) is FrameworkElement { IsVisible: true } shown)
        {
            shown.BringIntoView();
            return;
        }
        if (Panel(this) is { } panel) panel.BringIndexIntoViewPublic(at);
    }

    private static VirtualizingStackPanel? Panel(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is VirtualizingStackPanel panel) return panel;
            if (Panel(child) is { } inside) return inside;
        }
        return null;
    }

    protected override bool IsItemItsOwnContainerOverride(object item) => false;

    protected override DependencyObject GetContainerForItemOverride() => new ContentPresenter();

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is ContentPresenter shown && item is ListLine line) shown.Content = line.Build();
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is ContentPresenter shown) shown.Content = null;
    }

    private sealed class Lines : ObservableCollection<ListLine>
    {
        // All at once, as one change, rather than one for each line.
        public void ReplaceAll(IEnumerable<ListLine> lines)
        {
            Items.Clear();
            foreach (var line in lines) Items.Add(line);
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
