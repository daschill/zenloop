using System.Security.Principal;

namespace ZenLoop.Core;

public static class WindowsElevation
{
    public const string NoElevateArg = "--no-elevate";

    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
