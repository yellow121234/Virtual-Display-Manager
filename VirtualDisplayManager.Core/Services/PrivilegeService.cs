using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace VirtualDisplayManager.Core.Services;

public enum ElevationResult { AlreadyAdministrator, Restarted, Denied, Failed }

public static class PrivilegeService
{
    public static bool IsRunAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static ElevationResult RestartAsAdministratorIfNeeded() =>
        IsRunAsAdministrator() ? ElevationResult.AlreadyAdministrator : TryRestartAsAdministrator();

    public static ElevationResult TryRestartAsAdministrator()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
                return ElevationResult.Failed;

            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(' ', Environment.GetCommandLineArgs().Skip(1).Select(QuoteArgument))
            });
            return ElevationResult.Restarted;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return ElevationResult.Denied;
        }
        catch
        {
            return ElevationResult.Failed;
        }
    }

    private static string QuoteArgument(string value) =>
        value.Any(char.IsWhiteSpace) || value.Contains('"')
            ? $"\"{value.Replace("\"", "\\\"")}\""
            : value;
}
