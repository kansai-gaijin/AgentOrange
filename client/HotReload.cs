#if DEBUG
using System.Reflection.Metadata;
using System.Windows;

[assembly: MetadataUpdateHandler(typeof(AgentOrange.HotReload))]

namespace AgentOrange;

/// <summary>
/// Dev builds only. Code changed while the app is running doesn't reach what's already drawn,
/// and a button drawn before the change can call code that's since been removed, which hangs the
/// app. So what's on screen is drawn again after every change.
/// </summary>
internal static class HotReload
{
    public static void UpdateApplication(Type[]? changed) =>
        Application.Current?.Dispatcher.InvokeAsync(() => (Application.Current.MainWindow as MainWindow)?.RedrawAfterCodeChange());
}
#endif
