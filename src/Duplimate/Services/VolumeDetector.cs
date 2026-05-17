using System;
using System.IO;

namespace Duplimate.Services;

/// <summary>
/// Preserves the original "external drive check" logic from run-backup-local_dropbox.bat:
///
///   for /f %%I in (
///       '2^>NUL wmic logicaldisk where "VolumeName='My Passport' and Name='M:'" get Name /value ^| find "="'
///   ) do set "%%I"
///
/// We replace WMIC (deprecated on modern Windows) with System.IO.DriveInfo, which is
/// cleaner, faster, and works on recent Windows 11 builds that may have removed WMIC.
///
/// Policy matches the original:
///   - both label AND letter must match → OK
///   - either is set and doesn't match  → skipped (not a failure)
///   - neither is set                   → OK (no volume matching requested)
/// </summary>
public static class VolumeDetector
{
    public enum Verdict
    {
        /// <summary>Checks passed (or none requested) — proceed.</summary>
        Mounted,
        /// <summary>Expected drive letter isn't attached at all.</summary>
        DriveNotAttached,
        /// <summary>Drive is present but the label doesn't match expectations.</summary>
        WrongVolumeLabel,
    }

    public readonly record struct CheckResult(Verdict Verdict, string Detail);

    public static CheckResult Check(string? expectedDriveLetter, string? expectedVolumeLabel)
    {
        // Nothing requested → proceed.
        if (string.IsNullOrWhiteSpace(expectedDriveLetter) && string.IsNullOrWhiteSpace(expectedVolumeLabel))
            return new(Verdict.Mounted, "No volume-matching requested");

        // If only a label is requested, scan all drives for it.
        if (string.IsNullOrWhiteSpace(expectedDriveLetter))
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady) continue;
                if (Equal(d.VolumeLabel, expectedVolumeLabel))
                    return new(Verdict.Mounted, $"Found label '{expectedVolumeLabel}' at {d.Name}");
            }
            return new(Verdict.DriveNotAttached, $"No drive with label '{expectedVolumeLabel}' is mounted");
        }

        // Letter was specified.
        var letter = NormalizeLetter(expectedDriveLetter!);
        try
        {
            var drive = new DriveInfo(letter);
            if (!drive.IsReady)
                return new(Verdict.DriveNotAttached, $"{letter} not mounted");

            if (!string.IsNullOrWhiteSpace(expectedVolumeLabel) && !Equal(drive.VolumeLabel, expectedVolumeLabel))
                return new(Verdict.WrongVolumeLabel,
                    $"{letter} is mounted but labelled '{drive.VolumeLabel}', expected '{expectedVolumeLabel}'");

            return new(Verdict.Mounted,
                string.IsNullOrWhiteSpace(expectedVolumeLabel)
                    ? $"{letter} is mounted"
                    : $"{letter} is mounted with label '{drive.VolumeLabel}'");
        }
        catch (ArgumentException)
        {
            return new(Verdict.DriveNotAttached, $"{letter} does not exist on this system");
        }
    }

    private static string NormalizeLetter(string letter)
    {
        // Accept "M", "m", "M:", "M:\" — return "M:\" (DriveInfo accepts that).
        var clean = letter.Trim().TrimEnd('\\', '/').TrimEnd(':');
        return clean.Substring(0, 1).ToUpperInvariant() + ":\\";
    }

    private static bool Equal(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
