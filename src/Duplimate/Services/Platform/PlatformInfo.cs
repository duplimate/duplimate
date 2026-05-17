using System;
using System.Runtime.InteropServices;

namespace Duplimate.Services.Platform;

/// <summary>
/// Single source of truth for "what OS is this" decisions. Wraps
/// <see cref="OperatingSystem"/> and <see cref="RuntimeInformation"/>
/// behind a tiny surface so the rest of the codebase doesn't repeat
/// the same three checks in twenty places.
///
/// Keep this class boring. If a feature gate needs more nuance than
/// "is this Windows / macOS / Linux", expose a named property here
/// rather than threading raw OS checks through the call site.
/// </summary>
public static class PlatformInfo
{
    public static bool IsWindows { get; } = OperatingSystem.IsWindows();
    public static bool IsMacOS   { get; } = OperatingSystem.IsMacOS();
    public static bool IsLinux   { get; } = OperatingSystem.IsLinux();
    public static bool IsUnix    { get; } = !IsWindows;

    /// <summary>
    /// Filename of the bundled Duplicacy CLI for the current host. The
    /// upstream release names are platform-specific
    /// (<c>duplicacy_win_x64_*.exe</c>, <c>duplicacy_osx_x64_*</c>,
    /// <c>duplicacy_linux_x64_*</c>); we rename to a stable filename
    /// when bundling so the rest of the code only needs to know the
    /// canonical name.
    /// </summary>
    public static string DuplicacyBinaryFileName => IsWindows ? "duplicacy.exe" : "duplicacy";

    /// <summary>
    /// Suffix the upstream Duplicacy release uses for the current host
    /// + architecture. Used by FetchDuplicacy.targets / fetch scripts
    /// to pick the correct asset from the GitHub release feed.
    /// </summary>
    public static string DuplicacyReleaseAssetPrefix
    {
        get
        {
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64   => "x64",
                Architecture.Arm64 => "arm64",
                Architecture.X86   => "i386",
                _                  => "x64",
            };
            if (IsWindows) return $"duplicacy_win_{arch}_";
            if (IsMacOS)   return $"duplicacy_osx_{arch}_";
            return $"duplicacy_linux_{arch}_";
        }
    }

    /// <summary>
    /// Stable, human-readable RID label for logs / config / install
    /// surfaces. <see cref="RuntimeInformation.RuntimeIdentifier"/> can
    /// vary between published-self-contained ("win-x64") and live
    /// runtimes ("win10-x64", "win11-x64") in ways that make
    /// log-grepping painful.
    /// </summary>
    public static string DisplayPlatform =>
        IsWindows ? "Windows" :
        IsMacOS   ? "macOS"   :
        IsLinux   ? "Linux"   :
                    "Unknown";
}
