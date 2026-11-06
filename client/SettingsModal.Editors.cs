using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

using static AgentOrange.Ui;

namespace AgentOrange;

/// <summary>The Engines and Tools pages.</summary>
public partial class SettingsModal
{
    private const string HttpChoice = "Standard MCP (HTTP)";
    private const string PortChoice = "Port only";

    private UIElement[] EnginesPage() => EditorPage(engines: true);

    private UIElement[] ToolsPage() => EditorPage(engines: false);

    private UIElement[] EditorPage(bool engines)
    {
        var refreshers = new List<Action>();
        var elements = new List<UIElement>();
        foreach (var editor in EditorMonitor.Definitions.Where(editor => editor.IsEngine == engines))
        {
            elements.AddRange(EditorSection(editor, refreshers));
        }

        _refreshEditorsPage = () =>
        {
            foreach (var refresh in refreshers) refresh();
        };
        _refreshEditorsPage();
        return elements.ToArray();
    }

    private UIElement[] EditorSection(EditorDefinition editor, List<Action> refreshers)
    {
        var settings = Editors.SettingsOf(editor);
        var problem = ProblemText();

        var program = Field(settings.ProgramPath);
        program.Width = 290;
        program.ToolTip = settings.ProgramPath.Length > 0 ? settings.ProgramPath : null;
        var address = Field(settings.Address);
        address.Width = 290;
        var kind = new ComboBox
        {
            Style = (Style)FindResource("Choice"),
            ItemsSource = new[] { HttpChoice, PortChoice },
            SelectedItem = settings.Kind == McpKind.Http ? HttpChoice : PortChoice,
        };

        void Apply()
        {
            var chosenKind = (string)kind.SelectedItem == HttpChoice ? McpKind.Http : McpKind.Port;
            if (!EditorMonitor.TryCleanAddress(chosenKind, address.Text, out var cleaned))
            {
                ShowProblem(problem, chosenKind == McpKind.Http
                    ? "Enter a URL on this PC, like http://127.0.0.1:8000/mcp."
                    : "Enter a port on this PC, like 127.0.0.1:9000.");
                return;
            }

            problem.Visibility = Visibility.Collapsed;
            address.Text = cleaned;
            var path = program.Text.Trim();
            program.Text = path;
            program.ToolTip = path.Length > 0 ? path : null;
            Editors.Save(editor, new EditorSettings(path, chosenKind, cleaned));
        }

        foreach (var field in new[] { program, address })
        {
            field.LostKeyboardFocus += (_, _) => Apply();
            field.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) Apply();
            };
        }
        kind.SelectionChanged += (_, _) => Apply();

        var browse = ActionButton("Browse");
        browse.Click += (_, _) =>
        {
            if (Dialogs.PickFile($"Find {editor.Name}", "Programs (*.exe)|*.exe") is not { } chosen) return;
            program.Text = chosen;
            Apply();
        };

        var statusHost = new ContentControl();
        var copies = new StackPanel { Margin = new Thickness(16, -6, 16, 12) };
        refreshers.Add(() => ShowEditorStatus(editor, statusHost, copies));

        var statusRow = new StackPanel
        {
            Children = { Row("Status", null, statusHost), copies },
        };

        return
        [
            Section(editor.Name),
            Card(
                statusRow,
                Row("Program", $"The {editor.Name} program file.", program, browse),
                Row("MCP type", "How to reach its MCP.", kind),
                Row("MCP address", "On this PC.", address)),
            problem,
        ];
    }

    private void ShowEditorStatus(EditorDefinition editor, ContentControl statusHost, StackPanel copies)
    {
        var status = Editors.StatusOf(editor.Id);
        copies.Children.Clear();
        if (status is null)
        {
            statusHost.Content = Status("Checking…", false);
            copies.Visibility = Visibility.Collapsed;
            return;
        }

        var (appLabel, appGood) = EditorAppLabel(editor, status);
        var (mcpLabel, mcpGood) = EditorMcpLabel(status);
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(Status(appLabel, appGood));
        var mcp = Status(mcpLabel, mcpGood);
        mcp.Margin = new Thickness(14, 0, 0, 0);
        line.Children.Add(mcp);
        statusHost.Content = line;

        copies.Visibility = status.Instances.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var instance in status.Instances)
        {
            var title = instance.WindowTitle.Length > 0 ? instance.WindowTitle : "(no window)";
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
            if (instance.HoldsMcp)
            {
                var holds = new TextBlock { Text = "Holds MCP", FontSize = 12.5, Foreground = (Brush)FindResource("Accent"), Margin = new Thickness(12, 0, 0, 0) };
                DockPanel.SetDock(holds, Dock.Right);
                row.Children.Add(holds);
            }
            row.Children.Add(new TextBlock { Text = title, FontSize = 12.5, Foreground = (Brush)FindResource("TextMuted"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = title });
            copies.Children.Add(row);
        }
    }

    private (string Label, bool Good) EditorAppLabel(EditorDefinition editor, EditorStatus status)
    {
        if (Editors.SettingsOf(editor).ProgramPath.Length == 0) return ("No program set", false);
        if (!status.ProgramFound) return ("Program not found", false);
        return status.Instances.Count switch
        {
            0 => ("Not open", false),
            1 => ("Open", true),
            var count => ($"{count} open", true),
        };
    }

    private static (string Label, bool Good) EditorMcpLabel(EditorStatus status)
    {
        var holder = status.McpHolder is null ? "" : $" ({status.McpHolder})";
        return status.Mcp switch
        {
            McpState.Connected => ($"MCP connected{holder}", true),
            McpState.NeedsLogin => ($"MCP answers, needs its own login{holder}", false),
            McpState.PortOpen => ($"MCP port open{holder}", true),
            McpState.NotMcp => ($"Something else on that address{holder}", false),
            McpState.NotRunning => ("MCP not running", false),
            _ => ("No MCP set", false),
        };
    }
}
