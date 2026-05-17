using System;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Serilog;

namespace Duplimate.Services.Platform.Windows;

/// <summary>
/// Per-user (HKCU) Windows Explorer integration: registers a
/// "Restore an older version with Duplimate" right-click entry on
/// files and folders. Per-user means we don't need elevation — install
/// is a registry write under the user's own classes hive.
///
/// On click, Explorer launches Duplimate with
/// <c>--restore-prompt "&lt;path&gt;"</c>; <c>Program.cs</c> picks that up
/// and routes the user to the Restore tab pre-selecting the matching
/// backup + source.
///
/// We deliberately avoid a real shell extension (a registered COM DLL)
/// for v0:
///   - COM extensions need C++ or a packaged out-of-process .NET host;
///     both are deployment burdens disproportionate to the feature.
///   - The verb-style approach below is the same mechanism Notepad++,
///     7-Zip, Git, VS Code etc. use for "Edit with…" / "Open with…"
///     and is well-understood by Windows.
///   - It works on every modern Windows version (10/11) without
///     install-time elevation or per-machine registration.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsShellIntegration
{
    private static ILogger _log => AppLogger.For(nameof(WindowsShellIntegration));

    private const string VerbName = "DuplimateRestore";
    private const string VerbLabel = "Restore an older version (Duplimate)";

    // Per-class registration paths under HKCU\Software\Classes.
    // We register on three classes:
    //   - "*"           → all files (right-click on a file in Explorer)
    //   - "Directory"   → right-click on a folder
    //   - "Directory\\Background" → right-click on empty area of a folder
    private static readonly string[] HkcuClassPaths =
    {
        @"Software\Classes\*\shell\" + VerbName,
        @"Software\Classes\Directory\shell\" + VerbName,
        @"Software\Classes\Directory\Background\shell\" + VerbName,
    };

    /// <summary>True if the verb is currently registered for the user.</summary>
    public static bool IsInstalled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(HkcuClassPaths[0]);
            return k is not null;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "ShellIntegration probe failed");
            return false;
        }
    }

    /// <summary>
    /// Writes the registry verbs so Explorer shows the entry. Idempotent:
    /// re-running just overwrites the existing keys. Returns true on
    /// success.
    /// </summary>
    public static bool Install()
    {
        try
        {
            var exe = ResolveExePath();
            if (exe is null)
            {
                _log.Warning("ShellIntegration install skipped — could not resolve exe path");
                return false;
            }

            // The icon shown next to the verb is the app's own exe,0.
            // Background-context registration uses %V instead of %1
            // because Explorer passes the folder path that way.
            for (int i = 0; i < HkcuClassPaths.Length; i++)
            {
                using var verbKey = Registry.CurrentUser.CreateSubKey(HkcuClassPaths[i])!;
                verbKey.SetValue("", VerbLabel);
                verbKey.SetValue("Icon", exe);
                using var cmdKey = verbKey.CreateSubKey("command")!;
                var arg = i == 2 ? "%V" : "%1"; // background uses %V
                cmdKey.SetValue("", $"\"{exe}\" --restore-prompt \"{arg}\"");
            }
            _log.Information("ShellIntegration installed at HKCU for {Exe}", exe);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "ShellIntegration install failed");
            return false;
        }
    }

    /// <summary>Removes the verb registration. Safe to call when the
    /// keys don't exist (no-op).</summary>
    public static bool Uninstall()
    {
        var ok = true;
        foreach (var p in HkcuClassPaths)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(p, throwOnMissingSubKey: false); }
            catch (Exception ex)
            {
                _log.Warning(ex, "ShellIntegration uninstall failed at {Path}", p);
                ok = false;
            }
        }
        if (ok) _log.Information("ShellIntegration removed from HKCU");
        return ok;
    }

    /// <summary>
    /// The path Explorer should launch. Prefers <see cref="Environment.ProcessPath"/>
    /// (set by the runtime when the host is the actual exe); falls back
    /// to the executing assembly location if needed.
    /// </summary>
    private static string? ResolveExePath()
    {
        var p = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(p) && File.Exists(p)) return p;
        var asm = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrWhiteSpace(asm) && File.Exists(asm)) return asm;
        return null;
    }
}
