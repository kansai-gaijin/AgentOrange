using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>
/// An Unreal game's project on its page: made from Unreal's Blank C++ template when its folder has
/// none yet, and opened in its engine, with Unreal's MCP server on, once it has.
/// </summary>
public partial class MainWindow
{
    private void AddUnrealRow(StackPanel body, Project project, string folder)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        body.Children.Add(row);

        if (UnrealProject.Find(folder) is { } uproject)
        {
            var open = ActionButton("Open in Unreal Engine");
            open.IsEnabled = !IsBusyWith(project.Id);
            open.Click += (_, _) =>
            {
                if (UnrealProject.Open(uproject) is { } problem) ShowPageProblem(project.Id, problem);
            };
            row.Children.Add(open);
            return;
        }

        if (IsBusyWith(project.Id)) return; // Its job says how it's going.
        var engines = UnrealProject.Installed();
        if (engines.Count == 0)
        {
            row.Children.Add(new TextBlock { Text = "There's no Unreal project in this folder yet, and no Unreal Engine is installed on this PC to make one with. Install it from the Epic Games launcher.", Foreground = (Brush)FindResource("TextMuted"), FontSize = 13, TextWrapping = TextWrapping.Wrap });
            return;
        }

        row.Children.Add(new TextBlock { Text = "There's no Unreal project in this folder yet.", Foreground = (Brush)FindResource("TextNormal"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });

        // The newest, unless they pick another.
        var version = new ComboBox { Style = (Style)FindResource("Choice"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var engine in engines) version.Items.Add(new ComboBoxItem { Content = $"Unreal {engine.Version}", Tag = engine });
        version.SelectedIndex = 0;
        System.Windows.Automation.AutomationProperties.SetName(version, "Unreal version");
        if (engines.Count > 1) row.Children.Add(version);

        var create = ActionButton("Create the Unreal project");
        create.IsEnabled = !IsBusy;
        create.Click += async (_, _) => await CreateUnrealProjectAsync(project, folder, (UnrealInstall)((ComboBoxItem)version.SelectedItem).Tag);
        row.Children.Add(create);
    }

    private async Task CreateUnrealProjectAsync(Project project, string folder, UnrealInstall engine)
    {
        var name = UnrealProject.NameFor(project.Name);
        using var cancel = new CancellationTokenSource();
        string? problem;
        if (StartJob(project, $"Creating {name} for Unreal {engine.Version}…", cancel) is not { } job) return;
        using (job)
        {
            try
            {
                problem = await UnrealProject.CreateAsync(engine, folder, name, said => Dispatcher.InvokeAsync(() => job.SayStage(said, "")), cancel.Token);
            }
            catch (OperationCanceledException)
            {
                problem = "Stopped. What was already made is still in the folder, so Unreal can build it when it opens it.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                problem = $"Couldn't make the Unreal project: {error.Message}";
            }
        }
        if (problem is not null) ShowPageProblem(project.Id, problem);
        else await RecheckIfStillOpenAsync(project.Id); // Its engine version, from the new project.
    }
}
