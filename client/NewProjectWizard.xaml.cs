using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using static AgentOrange.Ui;

namespace AgentOrange;

public sealed record NewProject(string Name, string Engine, string Folder);

/// <summary>Creating a project: its name and engine, then where its files are on this PC.</summary>
public partial class NewProjectWizard : UserControl
{
    private static readonly string[] Engines = ["Unreal Engine", "Godot", "Web", "Other"];
    private static readonly string[] Steps = ["Project", "Folder"];

    private string _name = "";
    private string _engine = Engines[0];
    private string _folder = "";

    public NewProjectWizard()
    {
        InitializeComponent();
    }

    public event Action<NewProject>? Created;

    public event Action? Closed;

    public void Open()
    {
        _name = "";
        _engine = Engines[0];
        _folder = "";
        Visibility = Visibility.Visible;
        ShowProjectStep();
    }

    private void ShowProjectStep()
    {
        var name = Field(_name);
        name.Width = 300;
        var problem = ProblemText();

        void Next()
        {
            _name = name.Text.Trim();
            if (_name.Length == 0)
            {
                ShowProblem(problem, "Give the project a name.");
                return;
            }
            ShowFolderStep();
        }

        name.TextChanged += (_, _) => _name = name.Text;
        name.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Next();
        };

        var tiles = new UniformGrid { Columns = 2, Margin = new Thickness(-5, 0, -5, 0) };
        foreach (var engine in Engines)
        {
            var tile = new RadioButton
            {
                Style = (Style)FindResource("ProviderTile"),
                GroupName = "NewProjectEngine",
                Content = engine,
                IsChecked = engine == _engine,
                Margin = new Thickness(5),
            };
            tile.Checked += (_, _) => _engine = engine;
            tiles.Children.Add(tile);
        }

        var engineArea = new StackPanel { Margin = new Thickness(16, 6, 16, 14) };
        engineArea.Children.Add(new TextBlock { Text = "Engine", FontSize = 14, FontWeight = FontWeights.Medium, Foreground = (Brush)FindResource("TextStrong"), Margin = new Thickness(0, 0, 0, 5) });
        engineArea.Children.Add(tiles);

        var body = new StackPanel
        {
            Margin = new Thickness(8, 8, 8, 4),
            Children =
            {
                Row("Name", "You can change this later.", name),
                engineArea,
                problem,
            },
        };

        Card.Child = WizardShell(
            Close,
            "New project",
            "Set up a new game.",
            Stepper(Steps, 1),
            body,
            TextButton("Cancel", Close),
            PrimaryButton("Next", Next));

        Dispatcher.InvokeAsync(() =>
        {
            name.Focus();
            name.CaretIndex = name.Text.Length;
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ShowFolderStep()
    {
        var folder = Field(_folder);
        folder.Width = 300;
        var problem = ProblemText();
        folder.TextChanged += (_, _) => _folder = folder.Text;

        var browse = ActionButton("Browse");
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "Choose the project folder" };
            if (Directory.Exists(_folder)) dialog.InitialDirectory = _folder;
            if (dialog.ShowDialog() == true) folder.Text = dialog.FolderName;
        };

        void Create()
        {
            var path = folder.Text.Trim();
            if (path.Length > 0 && !Directory.Exists(path))
            {
                ShowProblem(problem, "That folder doesn't exist.");
                return;
            }

            Created?.Invoke(new NewProject(_name, _engine, path));
            Close();
        }

        var body = new StackPanel
        {
            Margin = new Thickness(8, 8, 8, 12),
            Children =
            {
                Row("Project folder", "Where this game's files are on this PC. You can add it later.", folder, browse),
                problem,
            },
        };

        Card.Child = WizardShell(
            Close,
            "New project",
            "Set up a new game.",
            Stepper(Steps, 2),
            body,
            ControlButton("Back", ShowProjectStep),
            PrimaryButton("Create project", Create));
    }

    private void Close()
    {
        Visibility = Visibility.Collapsed;
        Closed?.Invoke();
    }

    private void Wizard_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void Backdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
