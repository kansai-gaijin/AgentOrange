using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace AgentOrange;

/// <summary>
/// The app's icon by the clock. Closing the window that has it hides the window there instead,
/// and the app carries on. Only one window of the app has it at a time, so several open windows
/// don't each add an icon, and the rest close the way windows usually do. Starting the app again
/// while that window is hidden brings it back, instead of opening a second one beside it.
/// </summary>
internal sealed class Tray : IDisposable
{
    // By where the app runs from, so a dev build and the installed app each have their own.
    private static readonly string Key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((Environment.ProcessPath ?? "").ToLowerInvariant())))[..32];
    private static readonly string OwnerName = $@"Local\AgentOrangeTray-{Key}";
    private static readonly string HiddenName = $@"Local\AgentOrangeHidden-{Key}";
    private static readonly string ShowName = $@"Local\AgentOrangeShow-{Key}";

    private readonly Mutex _owner;
    private readonly EventWaitHandle _show;
    private readonly RegisteredWaitHandle _showWait;
    private readonly Forms.NotifyIcon _icon;
    private readonly byte[] _picture;
    private Mutex? _hidden;
    private int _count = -1;

    /// <summary>True if a hidden window was there and has been asked to come back, so this copy has nothing to do.</summary>
    public static bool ShowHiddenWindow()
    {
        try
        {
            if (!Mutex.TryOpenExisting(HiddenName, out var hidden)) return false;
            hidden.Dispose();
            if (!EventWaitHandle.TryOpenExisting(ShowName, out var show)) return false;
            using (show) show.Set();
            return true;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            return false; // It's running as administrator, and can't be asked from here.
        }
    }

    /// <summary>Null if another window of the app already has the icon.</summary>
    public static Tray? TryCreate(Action open, Action quit)
    {
        Mutex owner;
        try
        {
            owner = new Mutex(false, OwnerName, out var isNew);
            if (!isNew)
            {
                owner.Dispose();
                return null;
            }
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            return null;
        }
        return new Tray(owner, open, quit);
    }

    private Tray(Mutex owner, Action open, Action quit)
    {
        _owner = owner;
        using (var stream = System.Windows.Application.GetResourceStream(new Uri("Assets/app-icon.ico", UriKind.Relative)).Stream)
        using (var copy = new MemoryStream())
        {
            stream.CopyTo(copy);
            _picture = copy.ToArray();
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => open());
        menu.Items.Add("Close", null, (_, _) => quit());
        _icon = new Forms.NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, click) =>
        {
            if (click.Button == Forms.MouseButtons.Left) open();
        };
        SetCount(0);

        // Set by a copy of the app that was started while this window was hidden.
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => dispatcher.InvokeAsync(open), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void SetHidden(bool hidden)
    {
        if (hidden) _hidden ??= new Mutex(false, HiddenName);
        else
        {
            _hidden?.Dispose();
            _hidden = null;
        }
    }

    /// <summary>How many things are waiting to be looked at, shown on the icon.</summary>
    public void SetCount(int count)
    {
        if (count == _count) return;
        _count = count;

        var old = _icon.Icon;
        _icon.Icon = Draw(count);
        _icon.Text = count > 0 ? $"Agent Orange · {count} to look at" : "Agent Orange";
        old?.Dispose();
    }

    public void Say(string title, string text) => _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None);

    private Drawing.Icon Draw(int count)
    {
        var size = Forms.SystemInformation.SmallIconSize;
        using var stream = new MemoryStream(_picture);
        var plain = new Drawing.Icon(stream, size);
        if (count <= 0) return plain;

        using (plain)
        using (var bitmap = new Drawing.Bitmap(size.Width, size.Height, Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using (var canvas = Drawing.Graphics.FromImage(bitmap))
            {
                canvas.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                canvas.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                canvas.DrawIcon(plain, new Drawing.Rectangle(0, 0, size.Width, size.Height));

                // In the corner, and big enough to read at the size of a tray icon.
                var across = size.Width * 0.66f;
                var badge = new Drawing.RectangleF(size.Width - across, size.Height - across, across, across);
                using var red = new Drawing.SolidBrush(Drawing.Color.FromArgb(0xE5, 0x39, 0x35));
                canvas.FillEllipse(red, badge);

                using var font = new Drawing.Font("Segoe UI", across * 0.62f, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
                using var centered = new Drawing.StringFormat { Alignment = Drawing.StringAlignment.Center, LineAlignment = Drawing.StringAlignment.Center };
                canvas.DrawString(count > 9 ? "9+" : count.ToString(), font, Drawing.Brushes.White, badge, centered);
            }

            // The handle's icon has to be freed by hand, so what's kept is a copy that frees itself.
            var handle = bitmap.GetHicon();
            try
            {
                using var borrowed = Drawing.Icon.FromHandle(handle);
                return (Drawing.Icon)borrowed.Clone();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }
    }

    public void Dispose()
    {
        _showWait.Unregister(null);
        _show.Dispose();
        _hidden?.Dispose();
        _icon.Visible = false; // Or it stays by the clock until the mouse passes over it.
        _icon.Icon?.Dispose();
        _icon.Dispose();
        _owner.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
