using System;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;

namespace IslandWin;

/// <summary>
/// Manages the system tray icon with context menu for exit.
/// Uses WinForms NotifyIcon since WPF has no built-in tray support.
/// </summary>
internal sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _icon;
    private bool _disposed;

    public TrayIconManager(App app)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show / Hide", null, (_, _) => ToggleMainWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => app.Shutdown());

        _icon = new NotifyIcon
        {
            // ponytail: using SystemIcons since we don't have a custom .ico yet
            Icon = SystemIcons.Application,
            Text = "IslandWin",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => ToggleMainWindow();
    }

    private static void ToggleMainWindow()
    {
        var w = System.Windows.Application.Current.MainWindow;
        if (w is null) return;
        if (w.Visibility == Visibility.Visible)
            w.Hide();
        else
        {
            w.Show();
            w.Activate();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
