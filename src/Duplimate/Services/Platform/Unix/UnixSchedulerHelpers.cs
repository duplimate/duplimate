using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Serilog;

namespace Duplimate.Services.Platform.Unix;

/// <summary>
/// Shared helpers for the macOS launchd and Linux systemd schedulers.
/// Both implementations follow the same shape — slug a backup name
/// into a filesystem-safe identifier, atomically write a unit-file
/// next to the existing one, then invoke a CLI tool to (re)load it —
/// so the boilerplate lives here.
/// </summary>
internal static class UnixSchedulerHelpers
{
    /// <summary>
    /// Slug a backup name into something safe for use as a filename
    /// component / unit-name segment. Keep the contract tight:
    /// alphanumerics + dash + underscore, lowercase, fall back to a
    /// hash if the cleaned form is empty (launchd labels and systemd
    /// unit names MUST be unique, so an empty slug would collide).
    /// </summary>
    public static string Slug(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.ToLowerInvariant())
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_') sb.Append(ch);
            else if (ch == ' ') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length == 0)
        {
            using var sha = System.Security.Cryptography.SHA1.Create();
            slug = "x" + Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(name)))[..8].ToLowerInvariant();
        }
        return slug;
    }

    /// <summary>
    /// Write <paramref name="content"/> to <paramref name="path"/> via
    /// a sibling .tmp + rename so a crash mid-write can't leave a
    /// half-written unit file that launchd / systemd would reject on
    /// its next reload.
    /// </summary>
    public static void AtomicWriteText(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: null);
        else File.Move(tmp, path);
    }

    /// <summary>
    /// Spawn a CLI tool with each argv element passed as a discrete
    /// token via <see cref="ProcessStartInfo.ArgumentList"/>. Avoids
    /// the quoting hazards of the single-string
    /// <see cref="ProcessStartInfo.Arguments"/> when paths or unit
    /// names contain a space or quote.
    ///
    /// <para>
    /// <paramref name="tolerateFailure"/> demotes non-zero exits from
    /// Warning to Debug — useful for "best-effort" calls like
    /// <c>launchctl bootout</c> on a label that isn't loaded yet
    /// (which legitimately exits 5).
    /// </para>
    /// </summary>
    public static int Run(ILogger log, string fileName, string[] argv, bool tolerateFailure)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in argv) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            var argLine = string.Join(' ', argv);
            if (p.ExitCode != 0 && !tolerateFailure)
            {
                log.Warning("{File} {Args} exited {Code}: {Stdout} | {Stderr}", fileName, argLine, p.ExitCode, stdout, stderr);
            }
            else if (p.ExitCode != 0)
            {
                log.Debug("{File} {Args} exited {Code} (tolerated)", fileName, argLine, p.ExitCode);
            }
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            if (!tolerateFailure) log.Warning(ex, "{File} {Args} threw", fileName, string.Join(' ', argv));
            return -1;
        }
    }
}
