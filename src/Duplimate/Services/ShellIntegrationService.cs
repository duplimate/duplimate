using Duplimate.Services.Platform;

namespace Duplimate.Services;

/// <summary>
/// Cross-platform shell-integration facade. On Windows, registers the
/// "Restore an older version with Duplimate" right-click verb under
/// HKCU. On macOS / Linux the file-manager extension story is per-host
/// (Finder Quick Actions, Nautilus / Dolphin / Thunar plugins) and
/// outside the scope of v1 — this class returns false from
/// <see cref="IsInstalled"/>, <see cref="Install"/>, and
/// <see cref="Uninstall"/> so the UI naturally hides the offer.
/// </summary>
public static class ShellIntegrationService
{
    /// <summary>
    /// True iff the current platform supports installing a shell
    /// integration. Drives whether onboarding/settings show the
    /// "Add right-click restore" toggle at all.
    /// </summary>
    public static bool IsSupportedOnThisPlatform => PlatformInfo.IsWindows;

    public static bool IsInstalled()
    {
#if WINDOWS
        if (PlatformInfo.IsWindows) return Platform.Windows.WindowsShellIntegration.IsInstalled();
#endif
        return false;
    }

    public static bool Install()
    {
#if WINDOWS
        if (PlatformInfo.IsWindows) return Platform.Windows.WindowsShellIntegration.Install();
#endif
        return false;
    }

    public static bool Uninstall()
    {
#if WINDOWS
        if (PlatformInfo.IsWindows) return Platform.Windows.WindowsShellIntegration.Uninstall();
#endif
        return false;
    }
}
