using System.Security.Principal;

namespace ZenLoop.Core;

public static class WindowsElevation
{
    public const string NoElevateArg = "--no-elevate";

    public static bool IsAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
