using System.Windows;

namespace IslandWin;

public partial class App : Application
{
    private TrayIconManager? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _trayIcon = new TrayIconManager(this);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
