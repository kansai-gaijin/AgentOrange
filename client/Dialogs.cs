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

    public static string? PickFile(string title, string filter)
    {
#if DEBUG
        if (Headless.Active) return Headless.NextAnswer();
#endif
        var dialog = new OpenFileDialog { Title = title, RestoreDirectory = true, Filter = filter };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
