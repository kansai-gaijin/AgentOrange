using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Threading;

namespace AgentOrange;

/// <summary>
/// A project page's git actions: the agents window's, drawn the same, beside "Open in Unreal Engine",
/// acting on the project's folder, so the repo can be worked without a conversation open. With no
/// conversation, commit messages and pull requests are written by the login a new one would start with.
/// </summary>
public partial class MainWindow
{
    private GitActions? _gitActions;
    private Grid? _gitGroup; // The one on the page now, as the page is drawn again whenever anything syncs.
    private int _gitReadAt = -1; // The page it was last read for, by _navigation.

    // Made the first time a page shows them, with its dialogs and toasts over the page.
    private GitActions Git
    {
        get
        {
            if (_gitActions is not null) return _gitActions;
            var overlays = new Overlays();
            var root = (Grid)Content;

            // Over the page and under the window's own modals, such as project settings, which open over everything.
            var above = root.Children.IndexOf(MainContent) + 1;
            root.Children.Insert(above, LookOf(overlays.Dialogs));
            root.Children.Insert(above + 1, LookOf(overlays.Toasts));

            // As the agents window does: read again on coming back to the window, fetched every 30 seconds.
            Activated += (_, _) => { if (ShownGitProject() is { } shown) _ = Git.RefreshAsync(shown, fetch: false); };
            var fetcher = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            fetcher.Tick += (_, _) => { if (ShownGitProject() is { } shown) _ = Git.RefreshAsync(shown, fetch: true); };
            fetcher.Start();
            return _gitActions = new GitActions(this, overlays);
        }
    }

    // In T3's look, as the agents window is, rather than this window's.
    private static T LookOf<T>(T part) where T : FrameworkElement
    {
        part.Resources.MergedDictionaries.Add(Ocean.Resources());
        TextElement.SetFontFamily(part, AgentsWindow.Inter);
        return part;
    }

    // The project whose git actions are on screen, unless its own long job is working in its folder.
    private string? ShownGitProject() =>
        _gitGroup is { IsVisible: true } && _openProject is { } open && !IsBusyWith(open.Id) ? open.Id : null;

    private FrameworkElement GitActionsGroup(Project project, string folder)
    {
        var git = Git;
        var target = new GitTarget(project.Id, folder, DefaultAgentProvider);

        // Outlined, as T3's header buttons are, and wide enough for the menu's reasons.
        var layers = LookOf(new Grid { MinWidth = 220, VerticalAlignment = VerticalAlignment.Center });
        void Redraw()
        {
            // Never under an open menu, which drawing it again would close: a menu asks for the state as it opens.
            if (layers.Children.OfType<Popup>().Any(menu => menu.IsOpen)) return;
            layers.Children.Clear();
            layers.Children.Add(new Border { BorderBrush = Ocean.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = git.Row(target, layers) });
        }
        Redraw();

        // Shows what's done in the agents window too, for as long as it's on the page.
        layers.Loaded += (_, _) =>
        {
            GitActions.StatesChanged += Redraw;
            GitActions.RunsChanged += Redraw;
            Redraw();
        };
        layers.Unloaded += (_, _) =>
        {
            GitActions.StatesChanged -= Redraw;
            GitActions.RunsChanged -= Redraw;
        };
        _gitGroup = layers;

        // Read once each time the page opens, fetching, as opening the agents window's panel does.
        if (_gitReadAt != _navigation && !IsBusyWith(project.Id))
        {
            _gitReadAt = _navigation;
            _ = git.RefreshAsync(project.Id, fetch: true);
        }
        return layers;
    }
}
