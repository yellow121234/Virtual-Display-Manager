using System.Windows;
using VirtualDisplayManager.Core.Services;

namespace VirtualDisplayManager.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The manifest normally causes Windows to elevate before this code runs.
        // This fallback preserves correct behavior if a host ignores the manifest.
        if (!PrivilegeService.IsRunAsAdministrator())
        {
            var result = PrivilegeService.RestartAsAdministratorIfNeeded();
            if (result is ElevationResult.Restarted or ElevationResult.Denied or ElevationResult.Failed)
            {
                Shutdown();
                return;
            }
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
