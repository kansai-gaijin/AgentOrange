using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>Finding a setting by name. Results replace the sidebar while there's a search.</summary>
public partial class SettingsModal
{
    private static readonly (string Title, string Page)[] SearchEntries =
    [
        ("Current version", "General"),
        ("Check for updates", "General"),
        ("Agent needs you", "General"),
        ("Agent finished", "General"),
        ("Generation finished", "General"),
        ("Notification sounds", "General"),
        ("Add provider", "Providers"),
        ("Claude Code", "Providers"),
        ("Codex", "Providers"),
        ("Login folder", "Providers"),
        ("Default mode", "Approvals"),
        ("File edits", "Approvals"),
        ("Shell commands", "Approvals"),
        ("Web access", "Approvals"),
        ("Agents can start editors", "Approvals"),
        ("Paid generation", "Approvals"),
        ("OpenAI", "Third Party"),
        ("Tencent Hunyuan 3D", "Third Party"),
        ("Unreal Engine", "Engines"),
        ("Godot", "Engines"),
        ("Blender", "Tools"),
        ("Aseprite", "Tools"),
        ("Server address", "Server"),
        ("Pairing key", "Server"),
        ("Device name", "Server"),
        ("Sign out", "Server"),
        ("Other devices", "Server"),
        ("Last synced", "Server"),
        ("Waiting to sync", "Server"),
        ("Sync now", "Server"),
        ("Database", "Local data"),
        ("Downloads", "Local data"),
        ("Agent files", "Local data"),
        ("Client version", "About"),
        ("Server version", "About"),
        ("Logs", "About"),
    ];

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        var searching = query.Length > 0;
        SearchHint.Visibility = SearchBox.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        SearchKeyHint.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
        ClearSearch.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        NavList.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
        SearchResults.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;

        SearchResults.Children.Clear();
        if (!searching) return;

        // Page names count as results too, so "tools" finds the Tools page.
        var matches = _pages.Keys.Select(page => (Title: page, Page: page))
            .Concat(SearchEntries)
            .Where(entry => entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Page.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .OrderByDescending(entry => entry.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            SearchResults.Children.Add(new TextBlock
            {
                Text = "No settings found",
                FontSize = 13,
                Foreground = (Brush)FindResource("TextMuted"),
                Margin = new Thickness(10, 8, 0, 0),
            });
            return;
        }

        foreach (var match in matches) SearchResults.Children.Add(SearchResultItem(match.Title, match.Page));
    }

    private Button SearchResultItem(string title, string pageName)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.Medium, Foreground = (Brush)FindResource("TextStrong"), TextTrimming = TextTrimming.CharacterEllipsis });
        if (title != pageName)
        {
            text.Children.Add(new TextBlock { Text = pageName, FontSize = 11.5, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(0, 1, 0, 0) });
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = _pages[pageName].Icon, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 14, Width = 26, Foreground = (Brush)FindResource("TextNormal") });
        row.Children.Add(text);

        var item = new Button { Style = (Style)FindResource("SearchResult"), Content = row };
        item.Click += (_, _) => OpenSearchResult(title, pageName);
        return item;
    }

    private void OpenSearchResult(string title, string pageName)
    {
        var item = _navItems[pageName];
        if (item.IsChecked == true) ShowPage(_pages[pageName]);
        else item.IsChecked = true;

        Dispatcher.InvokeAsync(() => ScrollToText(PageContent, title), DispatcherPriority.Loaded);
    }

    private static bool ScrollToText(DependencyObject parent, string text)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is TextBlock block && block.Text == text)
            {
                block.BringIntoView();
                return true;
            }
            if (ScrollToText(child, text)) return true;
        }
        return false;
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || SearchResults.Children.Count == 0) return;
        if (SearchResults.Children[0] is Button first) first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        e.Handled = true;
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e) => SearchBox.Clear();

    private void Modal_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var typing = Keyboard.FocusedElement is TextBox or PasswordBox;
        if (e.Key == Key.OemQuestion && !typing && WizardLayer.Visibility != Visibility.Visible)
        {
            SearchBox.Focus();
            e.Handled = true;
        }
    }
}
