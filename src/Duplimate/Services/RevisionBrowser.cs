using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Duplimate.Models;
using Serilog;

namespace Duplimate.Services;

/// <summary>
/// Reads revision list + file list for a backup's destination. Thin wrapper
/// around `duplicacy list` plus parsers for its stdout. Everything here is
/// read-only — we never modify the storage.
/// </summary>
public sealed class RevisionBrowser
{
    private static ILogger _log => AppLogger.For<RevisionBrowser>();
    private readonly DuplicacyRunner _runner;
    private readonly SecretsStore _secrets;

    public RevisionBrowser(DuplicacyRunner runner, SecretsStore secrets)
    {
        _runner = runner;
        _secrets = secrets;
    }

    public async Task<List<RevisionSummary>> ListRevisionsAsync(
        Backup backup, string sourcePath, Destination destination, string storageName, CancellationToken ct)
    {
        var result = new List<RevisionSummary>();
        await EnsureInitializedAsync(backup, sourcePath, destination, storageName, ct);
        var repo = AppPaths.RepoDirForBackupSource(backup.Name, sourcePath);

        var lines = new List<string>();
        var args = new[] { "-log", "list", "-storage", storageName };
        await RunCaptureAsync(repo, args, destination, storageName, lines, ct);

        foreach (var raw in lines)
        {
            var m = RevisionRx.Match(raw);
            if (!m.Success) continue;
            var rev = int.Parse(m.Groups["rev"].Value);
            var time = DateTime.TryParse(m.Groups["time"].Value, out var t) ? t : DateTime.MinValue;
            var files = m.Groups["files"].Success && int.TryParse(m.Groups["files"].Value, out var f) ? f : 0;
            var bytes = m.Groups["bytes"].Success && long.TryParse(m.Groups["bytes"].Value, out var b) ? b : 0L;
            result.Add(new RevisionSummary(rev, time, files, bytes));
        }
        result.Sort((a, b) => b.Number.CompareTo(a.Number));
        _log.Information("Listed {Count} revisions for {Backup} at {Dest}", result.Count, backup.Name, destination.Name);
        return result;
    }

    public async Task<List<RevisionFile>> ListFilesAsync(
        Backup backup, string sourcePath, Destination destination, string storageName, int revision, CancellationToken ct)
    {
        var result = new List<RevisionFile>();
        await EnsureInitializedAsync(backup, sourcePath, destination, storageName, ct);
        var repo = AppPaths.RepoDirForBackupSource(backup.Name, sourcePath);

        var lines = new List<string>();
        var args = new[] { "-log", "list", "-files", "-r", revision.ToString(), "-storage", storageName };
        await RunCaptureAsync(repo, args, destination, storageName, lines, ct);

        foreach (var raw in lines)
        {
            var m = FileRx.Match(raw);
            if (!m.Success) continue;
            var path = m.Groups["path"].Value;
            var size = long.TryParse(m.Groups["size"].Value, out var sz) ? sz : 0L;
            var time = DateTime.TryParse(m.Groups["time"].Value, out var t) ? t : DateTime.MinValue;
            result.Add(new RevisionFile(path, size, time));
        }
        _log.Information("Listed {Count} files for {Backup} revision {Rev}", result.Count, backup.Name, revision);
        return result;
    }

    // ---- private helpers ----

    private async Task EnsureInitializedAsync(Backup backup, string sourcePath, Destination destination, string storageName,
                                              CancellationToken ct)
    {
        // Runner owns initialization — it creates the pref dir + runs `duplicacy init` if needed.
        await _runner.EnsureInitializedAsync(backup, sourcePath, destination, storageName, ct);
    }

    private async Task RunCaptureAsync(string repo, IReadOnlyList<string> args, Destination destination,
                                       string storageName, List<string> lines, CancellationToken ct)
    {
        var exe = DuplicacyEmbedder.EnsureExtracted();
        var pwd = destination.StoragePasswordRef is null ? "" : _secrets.Get(destination.StoragePasswordRef);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = repo,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) if (!string.IsNullOrEmpty(a)) psi.ArgumentList.Add(a);

        // Same env-var pipeline the runner uses for backup/restore. Listing
        // a cloud storage needs the OAuth token or S3 creds just like any
        // other operation.
        foreach (var (k, v) in _runner.BuildCloudEnv(destination, storageName))
            if (!string.IsNullOrEmpty(v)) psi.Environment[k] = v;

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var gate = new object();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (gate) lines.Add(e.Data);
            // Mirror revision-listing output into the global live-tail
            // feed so the Activity & logs Live pane shows the
            // browse activity (the user otherwise sees a frozen UI
            // while listing thousands of files in a remote chain).
            _runner.RaiseLineWritten(e.Data);
        };
        p.ErrorDataReceived  += (_, __) => { /* surface stderr only if no stdout — uncommon */ };

        p.Start();
        KillOnExitJobObject.TryAssign(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        if (!string.IsNullOrEmpty(pwd))
        {
            try { await p.StandardInput.WriteLineAsync(pwd); } catch { }
        }
        try { p.StandardInput.Close(); } catch { }

        using (ct.Register(() => { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { } }))
        {
            // Pass `ct` so a cancellation whose Kill failed still
            // unwinds the wait — without this, a stuck duplicacy
            // process pegged a thread forever.
            await p.WaitForExitAsync(ct);
        }
    }

    // ---- duplicacy output shapes ----

    // `duplicacy -log list` emits lines like:
    //   "2026-04-22 15:56:29.939 INFO SNAPSHOT_INFO Snapshot foo revision 1 created at 2026-04-22 15:56 -hash"
    //
    // Older versions may use a cleaner non-log format. We anchor on the
    // SNAPSHOT_INFO event code when present (it's stable across versions),
    // falling through the tolerant Snapshot-matcher otherwise.
    private static readonly Regex RevisionRx = new(
        @"Snapshot\s+\S+\s+revision\s+(?<rev>\d+)\s+created\s+(?:at\s+)?(?<time>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}(?::\d{2})?)(?:\s+(?<files>\d+)\s+files?)?(?:\s+(?<bytes>\d+)\s+bytes?)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // `duplicacy -log list -files` emits per-file lines like:
    //   "2026-04-22 15:56:29.992 INFO SNAPSHOT_FILE 33907 2026-04-22 15:56:29 <sha256hex> path/to/file.ext"
    //
    // We anchor on the SNAPSHOT_FILE event code so we don't have to care about
    // the timestamp/log-level prefix. Fields: size (bytes), file mtime,
    // content hash (hex), path. Path may contain spaces.
    private static readonly Regex FileRx = new(
        @"SNAPSHOT_FILE\s+(?<size>\d+)\s+(?<time>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}(?::\d{2})?)\s+[a-f0-9]+\s+(?<path>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
}

public readonly record struct RevisionSummary(int Number, DateTime CreatedUtc, int FileCount, long TotalBytes);
public readonly record struct RevisionFile(string Path, long SizeBytes, DateTime ModifiedUtc);
