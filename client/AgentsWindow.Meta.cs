using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgentOrange;

/// <summary>
/// T3 Code's reply meta row, copied from its code: under a reply, the copy button, "Fork from
/// this response" and the time, which show when the reply is pointed at, and always under a
/// turn's last reply. Also its fork rows ("Forked from conversation" / "Conversation fork", each
/// opening the other), and the menu on a picture: Copy full path, Save image, Copy image.
/// </summary>
public sealed partial class AgentsWindow
{
    private FrameworkElement ReplyMeta(AgentConversation conversation, AgentItem reply)
    {
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Opacity = 0 };
        Button? copy = null;
        copy = Ocean.Ghost(AgentIcons.Line("copy", 12, Ocean.MutedText), () =>
        {
            try
            {
                Clipboard.SetText(reply.Text);
                copy!.Content = AgentIcons.Line("check", 12, Ocean.MutedText);
                copy.ToolTip = "Copied";
                var back = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
                back.Tick += (_, _) =>
                {
                    back.Stop();
                    copy.Content = AgentIcons.Line("copy", 12, Ocean.MutedText);
                    copy.ToolTip = "Copy message";
                };
                back.Start();
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Another program has the clipboard.
            }
        }, 6, null, Ocean.RowHover);
        copy.Width = 24;
        copy.Height = 24;
        copy.ToolTip = "Copy message";
        AutomationProperties.SetName(copy, "Copy message");
        if (!reply.Streaming && reply.Text.Trim().Length > 0) meta.Children.Add(copy);

        if (!reply.Streaming)
        {
            var fork = Ocean.Ghost(AgentIcons.Line("git-fork", 12, Ocean.MutedText), () => Fork(conversation, reply), 6, null, Ocean.RowHover);
            fork.Width = 24;
            fork.Height = 24;
            fork.Margin = new Thickness(8, 0, 0, 0);
            fork.ToolTip = "Fork from this response";
            ToolTipService.SetPlacement(fork, PlacementMode.Top);
            AutomationProperties.SetName(fork, "Fork from this response");
            meta.Children.Add(fork);

            var at = reply.At.ToLocalTime();
            var when = Ocean.Text(at.ToString("h:mm", System.Globalization.CultureInfo.InvariantCulture), Ocean.MutedText, 12);
            when.VerticalAlignment = VerticalAlignment.Center;
            when.Margin = new Thickness(8, 0, 0, 0);
            when.ToolTip = at.ToString("dddd, d MMMM yyyy h:mm:ss tt", System.Globalization.CultureInfo.InvariantCulture);
            meta.Children.Add(when);
        }
        return meta;
    }

    // A new conversation with everything up to this reply, which T3 opens straight away.
    private void Fork(AgentConversation conversation, AgentItem reply)
    {
        if (_main.AgentProjects().FirstOrDefault(project => project.Id == conversation.ProjectId) is not { } project) return;
        var fork = _main.NewConversation(project);
        conversation.ForkInto(fork, reply);
        conversation.Unseen = false; // Nothing new happened in it, only a fork made from it.
        Show(fork);
        ShowConversations();
    }

    // T3's fork row: a line either side of a pill that opens the other conversation.
    private FrameworkElement ForkRow(AgentItem item)
    {
        var from = item.ForkDirection == "from";
        var label = from ? "Forked from conversation" : "Conversation fork";
        var action = from ? "Open source conversation" : "Open fork";
        var pill = Ocean.Ghost(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { WithGap(AgentIcons.Line("git-fork", 12, Ocean.MutedText), 6), Ocean.Text(label, Ocean.MutedText, 11, FontWeights.Medium) },
        }, () =>
        {
            if (_main.Conversations.FirstOrDefault(each => each.Id == item.ForkConversation) is { } other)
            {
                Show(other);
                ShowConversations();
            }
            else
            {
                Toast(action, "That conversation isn't on this PC any more.", error: false);
            }
        }, 999, new Thickness(10, 4, 10, 4), Ocean.Accent, Ocean.Canvas);
        pill.BorderBrush = Ocean.Border70;
        pill.BorderThickness = new Thickness(1);
        pill.ToolTip = action;
        AutomationProperties.SetName(pill, $"{label}: {action}");
        var row = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new Border { Height = 1, Background = Ocean.Border70, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var right = new Border { Height = 1, Background = Ocean.Border70, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(pill, 1);
        Grid.SetColumn(right, 2);
        row.Children.Add(left);
        row.Children.Add(pill);
        row.Children.Add(right);
        return row;
    }

    // T3's menu on a picture.
    private void PictureMenu(string path, UIElement by)
    {
        var rows = new StackPanel();
        System.Windows.Controls.Primitives.Popup? menu = null;
        void Item(string words, Action act) => rows.Children.Add(Ocean.MenuRow(Ocean.Text(words, Ocean.Foreground, 14), () =>
        {
            menu!.IsOpen = false;
            act();
        }));
        Item("Copy full path", () => Clipboard.SetText(path));
        Item("Save image", () =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = Path.GetFileName(path),
                DefaultExt = Path.GetExtension(path),
                Filter = $"Image|*{Path.GetExtension(path)}|All files|*.*",
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                File.Copy(path, dialog.FileName, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Toast("Couldn't save the image", error.Message, error: true);
            }
        });
        Item("Copy image", () =>
        {
            try
            {
                var picture = new BitmapImage();
                picture.BeginInit();
                picture.CacheOption = BitmapCacheOption.OnLoad;
                picture.UriSource = new Uri(path);
                picture.EndInit();
                Clipboard.SetImage(picture);
            }
            catch (Exception error) when (error is IOException or NotSupportedException or UriFormatException or System.Runtime.InteropServices.COMException)
            {
                Toast("Couldn't copy the image", error.Message, error: true);
            }
        });
        menu = Ocean.Menu(by, PlacementMode.MousePoint, rows, 180, 0);
        menu.IsOpen = true;
    }
}
