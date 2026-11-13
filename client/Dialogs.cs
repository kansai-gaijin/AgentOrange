using Microsoft.Win32;

namespace AgentOrange;

/// <summary>The file and folder pickers. Each returns null if the person cancels.</summary>
internal static class Dialogs
{
    public static string? PickFolder(string title, string? initialFolder = null)
    {
#if DEBUG
        if (Headless.Active) return Headless.NextAnswer();
#endif
        var dialog = new OpenFolderDialog { Title = title };
        if (initialFolder is not null) dialog.InitialDirectory = initialFolder;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    /// <summary>For something the person has to know, when there's no page left to show it on.</summary>
    public static void Notify(string message)
    {
#if DEBUG
        if (Headless.Active)
        {
            Headless.Say(message);
            return;
        }
#endif
        System.Windows.MessageBox.Show(message, "Agent Orange", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    public static bool Confirm(string message)
    {
#if DEBUG
        if (Headless.Active)
        {
            Headless.Say(message);
            return Headless.NextAnswer() == "yes";
        }
#endif
        return System.Windows.MessageBox.Show(message, "Agent Orange", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes;
    }

    public static string? PickFile(string title, string filter, string? startIn = null)
    {
#if DEBUG
        if (Headless.Active) return Headless.NextAnswer();
#endif
        var dialog = new OpenFileDialog { Title = title, RestoreDirectory = true, Filter = filter, InitialDirectory = startIn ?? "" };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
