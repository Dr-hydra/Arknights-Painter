using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace ArknightsPainter.App.Services;

internal static class ElevationService
{
    private const int UserCancelledError = 1223;

    public static bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static bool TryRestartAsAdministrator()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            throw new InvalidOperationException("无法确定当前程序路径，不能申请管理员权限。");
        }

        try
        {
            Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == UserCancelledError)
        {
            return false;
        }
    }
}
