using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Duplimate.Models;

namespace Duplimate.Services;

/// <summary>
/// The restore engine. Two phases, same contract for both:
///
///   Phase 1 — Bulk
///     Invokes `duplicacy restore` once for the selected patterns with the
///     user-configured thread count. Duplicacy handles its own parallelism
///     for the common path; we just collect failures.
///
///   Phase 2 — Per-file retry
///     Each file that failed in Phase 1 gets an independent async task.
///     Each task walks the configured delay cascade (1s/5s/10s/30s/60s by
///     default), re-invoking duplicacy per-file. All tasks run concurrently,
///     bounded by a SemaphoreSlim sized to the thread count. A slow
///     file retrying doesn't block other retries — that's the whole point.
///
/// The engine is stateless across calls; progress is reported via events
/// (consumed by the VM) and a snapshot via <see cref="RestoreProgressSnapshot"/>.
/// </summary>
public sealed class RestoreEngine
{
    private static Serilog.ILogger _log => AppLogger.For<RestoreEngine>();
    private readonly ConfigStore _config;
    private readonly DuplicacyRunner _runner;
    private readonly SecretsStore _secrets;

    public RestoreEngine(ConfigStore config, DuplicacyRunner runner, SecretsStore secrets)
    {
        _config = config;
        _runner = runner;
        _secrets = secrets;
    }

    public event Action<string>? Log;
    public event Action<RestoreProgressSnapshot>? Progress;
    public event Action<RestoredFile>? FileRestored;
    public event Action<RestoredFile>? FileFailed;

    // ---- public API ----

    public async Task<RestoreOutcome> RunAsync(RestoreRequest request, CancellationToken ct)
    {
        if (request.Files.Count == 0) throw new ArgumentException("No files selected.", nameof(request));

        // Maintenance gate (Erase / Wipe / Prune). Same contract as
        // BackupOrchestrator.RunBackupAsync: no restore may begin
        // while a sensitive op is in flight. Returns immediately when
        // no maintenance is happening — common case. Cancellation
        // is intentionally NOT swallowed here — both callers
        // (RestoreViewModel.RunSingleSourceRestoreAsync and
        // BackupVerifier.VerifyOneDestinationAsync) handle OCE at
        // their own layer; shaping a cancellation as an empty
        // RestoreOutcome would make it indistinguishable from a
        // genuine 0-file run, producing misleading "0 restored"
        // summaries and false-failure verify verdicts.
        await ServiceLocator.Maintenance.WaitForReadAsync(ct);

        using var _syncing = AppStatus.BeginSyncing();

        _log.Information(
            "Restore start: backup={Backup} source={Source} destination={Dest} revision={Rev} files={FileCount} target={Target}",
            request.Backup.Name, request.SourcePath, request.Destination.Name, request.Revision,
            request.Files.Count, request.TargetPath);

        var threads = Math.Max(1, request.Threads);
        var cascade = (request.RetryDelaysSeconds?.Count > 0
            ? request.RetryDelaysSeconds
            : _config.Current.Restore.RetryDelaysSeconds).ToList();

        // Prepare the restore-repo directory under which duplicacy will lay things out.
        var restoreRepo = request.TargetPath;
        Directory.CreateDirectory(restoreRepo);

        // Snapshot whether a .duplicacy/ already existed at this
        // target BEFORE we touched it. If it did, the user has
        // unrelated Duplicacy metadata at this path (e.g. they're
        // restoring INTO an existing duplicacy repo) and we MUST NOT
        // wipe it on completion — that's their data, not ours. If it
        // didn't, the .duplicacy/ that ReInitializeForRestoreAsync is
        // about to create is purely our restore-shim and will be
        // cleaned up in the finally below so the user doesn't see a
        // mystery .duplicacy folder inside their restored content.
        var hadPreExistingMetadata = Directory.Exists(Path.Combine(restoreRepo, ".duplicacy"));

        await _runner.ReInitializeForRestoreAsync(
            request.Backup, request.SourcePath, request.Destination, request.StorageName, restoreRepo, ct);

        // De-dup the input list (case-insensitive) before building the
        // dictionary. Two issues being fixed at once:
        //   1. ToDictionary throws ArgumentException on duplicate keys —
        //      a programmatic caller that passes the same path twice
        //      would crash the whole restore before any file copied.
        //   2. The duplicacy "Restored …" line at line ~210 matches
        //      keys with OrdinalIgnoreCase; using a default-comparer
        //      dictionary would have "Foo.txt" and "foo.txt" as
        //      separate entries with only one ever marked Restored.
        var distinctFiles = request.Files
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var outcome = new RestoreOutcome
        {
            RevisionNumber = request.Revision,
            TargetPath = request.TargetPath,
            TotalFiles = distinctFiles.Count,
            Files = distinctFiles.ToDictionary(
                f => f,
                f => new RestoredFile { Path = f, Status = RestoreFileStatus.Pending },
                StringComparer.OrdinalIgnoreCase),
        };

        try
        {
            // PHASE 1 — Bulk
            Log?.Invoke($"[phase 1] bulk restore of {request.Files.Count} file(s) with {threads} thread(s)");
            await BulkRestoreAsync(request, restoreRepo, outcome, threads, ct);

            var remaining = outcome.Files.Values.Where(f => f.Status != RestoreFileStatus.Restored).ToList();
            Log?.Invoke($"[phase 1] complete: {outcome.Files.Values.Count(f => f.Status == RestoreFileStatus.Restored)} OK, {remaining.Count} failed");

            // PHASE 2 — Per-file retry cascade, parallel
            if (remaining.Count > 0 && !ct.IsCancellationRequested)
            {
                Log?.Invoke($"[phase 2] retrying {remaining.Count} file(s) with cascade [{string.Join("s, ", cascade)}s]");
                await ParallelRetryAsync(request, restoreRepo, remaining, outcome, threads, cascade, ct);
            }

            var restored = outcome.Files.Values.Count(f => f.Status == RestoreFileStatus.Restored);
            var failed = outcome.Files.Values.Count(f => f.Status == RestoreFileStatus.Failed);
            _log.Information(
                "Restore done: backup={Backup} restored={Restored}/{Total} failed={Failed} cancelled={Cancelled}",
                request.Backup.Name, restored, outcome.Files.Count, failed, ct.IsCancellationRequested);

            return outcome;
        }
        finally
        {
            // The user's complaint that triggered this cleanup: "After
            // a folder is restored, I can see a .duplicacy folder
            // inside. It shouldn't be necessary right? If so, it
            // should be deleted to not confuse users." Honour that —
            // but only if WE created the metadata. If RetryFailedAsync
            // is going to be called next (user clicks Retry all
            // failed), it re-inits before retrying so the cache is
            // back; safe to delete here.
            if (!hadPreExistingMetadata)
                TryRemoveRestoreMetadata(restoreRepo);
        }
    }

    /// <summary>
    /// Best-effort delete of <c>&lt;target&gt;/.duplicacy/</c>. Any
    /// failure (open file handles from a stuck duplicacy.exe, NTFS
    /// permissions) is logged and swallowed — the user can remove
    /// the folder by hand if it survives. Never throws.
    /// </summary>
    internal static void TryRemoveRestoreMetadata(string restoreRepo)
    {
        try
        {
            var dir = Path.Combine(restoreRepo, ".duplicacy");
            if (!Directory.Exists(dir)) return;
            // Strip read-only flags first — duplicacy's cache files
            // are sometimes marked read-only on Windows and
            // Directory.Delete then refuses with UnauthorizedAccess.
            foreach (var path in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not remove .duplicacy/ from {Target}", restoreRepo);
        }
    }

    /// <summary>
    /// Retry only the files still failing, fresh cascade. Called by the summary's
    /// "Retry all failed" button without re-running Phase 1.
    /// </summary>
    public async Task<RestoreOutcome> RetryFailedAsync(RestoreRequest request, RestoreOutcome outcome, CancellationToken ct)
    {
        var remaining = outcome.Files.Values.Where(f => f.Status == RestoreFileStatus.Failed).ToList();
        if (remaining.Count == 0) return outcome;

        using var _syncing = AppStatus.BeginSyncing();

        var threads = Math.Max(1, request.Threads);
        var cascade = (request.RetryDelaysSeconds?.Count > 0
            ? request.RetryDelaysSeconds
            : _config.Current.Restore.RetryDelaysSeconds).ToList();

        foreach (var f in remaining)
        {
            f.Status = RestoreFileStatus.Pending;
            f.LastError = null;
            f.AttemptsMade = 0;
        }

        Log?.Invoke($"[retry-all] cascading {remaining.Count} file(s) with [{string.Join("s, ", cascade)}s]");
        var restoreRepo = request.TargetPath;

        // RunAsync's finally cleaned up .duplicacy/ on completion (the
        // user-facing fix that hides the metadata folder once a
        // restore is "done"). Re-init it here so duplicacy.exe sees a
        // valid repo for the retry loop. Sub-second; the user is
        // already on the Summary screen and won't notice.
        var hadPreExistingMetadata = Directory.Exists(Path.Combine(restoreRepo, ".duplicacy"));
        if (!hadPreExistingMetadata)
        {
            await _runner.ReInitializeForRestoreAsync(
                request.Backup, request.SourcePath, request.Destination, request.StorageName, restoreRepo, ct);
        }

        try
        {
            await ParallelRetryAsync(request, restoreRepo, remaining, outcome, threads, cascade, ct);
            return outcome;
        }
        finally
        {
            if (!hadPreExistingMetadata) TryRemoveRestoreMetadata(restoreRepo);
        }
    }

    // ---- phase 1 ----

    private async Task BulkRestoreAsync(RestoreRequest request, string restoreRepo, RestoreOutcome outcome,
                                        int threads, CancellationToken ct)
    {
        var args = BuildBulkArgs(request, threads);
        var pwd = request.Destination.StoragePasswordRef is null
            ? "" : _secrets.Get(request.Destination.StoragePasswordRef);
        var env = _runner.BuildCloudEnv(request.Destination, request.StorageName);

        await RunDuplicacyAsync(
            workingDir: restoreRepo,
            args: args,
            pwd: pwd,
            onLine: line =>
            {
                Log?.Invoke(line);
                // Mirror restore output into the global live-tail
                // feed so it shows up in Activity & logs alongside
                // backup output. Without this the user running a
                // restore or test-restore saw nothing in the Live
                // pane even though duplicacy.exe was streaming
                // hundreds of lines.
                _runner.RaiseLineWritten(line);
                ParseBulkLineForFile(line, outcome);
            },
            ct: ct,
            envVars: env);

        // Files we saw "Restored" lines for are done; everything else is candidate for Phase 2.
        foreach (var kvp in outcome.Files)
        {
            if (kvp.Value.Status == RestoreFileStatus.Pending)
            {
                // If the file exists on disk after Phase 1, treat as restored even if we
                // didn't capture a specific line (robustness against log-format changes).
                var onDisk = Path.Combine(restoreRepo, kvp.Key.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(onDisk))
                {
                    kvp.Value.Status = RestoreFileStatus.Restored;
                    FileRestored?.Invoke(kvp.Value);
                }
                else
                {
                    kvp.Value.Status = RestoreFileStatus.Failed;
                    kvp.Value.LastError ??= "Not written by bulk pass";
                }
            }
            Progress?.Invoke(outcome.Snapshot());
        }
    }

    private static List<string> BuildBulkArgs(RestoreRequest r, int threads)
    {
        var args = new List<string>
        {
            "-d", "-log", "restore",
            "-r", r.Revision.ToString(),
            "-storage", r.StorageName,
            "-threads", threads.ToString(),
            "-stats",
        };
        if (r.Overwrite) args.Add("-overwrite");
        args.Add("--");
        foreach (var pattern in r.Files) args.Add(pattern);
        return args;
    }

    // Duplicacy emits per-file lines such as:
    //   "Restored Users/me/Desktop/foo.txt"
    //   "Failed to restore Users/me/Desktop/foo.txt: ..."
    //   "ERROR  path: ..."
    private static readonly Regex RestoredLine = new(@"^\s*(?:\[[^\]]+\]\s*)?Restored\s+(.+?)\s*$",
        RegexOptions.Compiled);
    private static readonly Regex FailedLine = new(@"^\s*(?:\[[^\]]+\]\s*)?Failed\s+to\s+restore\s+(.+?)(?::\s*(.+))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private void ParseBulkLineForFile(string line, RestoreOutcome outcome)
    {
        var m = RestoredLine.Match(line);
        if (m.Success && outcome.Files.TryGetValue(m.Groups[1].Value, out var f))
        {
            f.Status = RestoreFileStatus.Restored;
            FileRestored?.Invoke(f);
            Progress?.Invoke(outcome.Snapshot());
            return;
        }
        m = FailedLine.Match(line);
        if (m.Success && outcome.Files.TryGetValue(m.Groups[1].Value, out var ff))
        {
            ff.Status = RestoreFileStatus.Failed;
            ff.LastError = m.Groups[2].Success ? m.Groups[2].Value : "unknown error";
            FileFailed?.Invoke(ff);
            Progress?.Invoke(outcome.Snapshot());
        }
    }

    // ---- phase 2 ----

    private async Task ParallelRetryAsync(RestoreRequest request, string restoreRepo,
                                          List<RestoredFile> failed, RestoreOutcome outcome,
                                          int threads, List<int> cascade, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(threads);
        var tasks = failed.Select(f => Task.Run(async () =>
        {
            await gate.WaitAsync(ct);
            try { await RetryOneAsync(request, restoreRepo, f, cascade, outcome, ct); }
            finally { gate.Release(); }
        }, ct)).ToArray();

        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { /* user cancelled */ }
    }

    private async Task RetryOneAsync(RestoreRequest request, string restoreRepo, RestoredFile f,
                                     List<int> cascade, RestoreOutcome outcome, CancellationToken ct)
    {
        var pwd = request.Destination.StoragePasswordRef is null
            ? "" : _secrets.Get(request.Destination.StoragePasswordRef);
        var env = _runner.BuildCloudEnv(request.Destination, request.StorageName);

        for (int i = 0; i < cascade.Count; i++)
        {
            if (ct.IsCancellationRequested) return;

            f.Status = RestoreFileStatus.WaitingRetry;
            f.NextRetryIn = TimeSpan.FromSeconds(cascade[i]);
            Progress?.Invoke(outcome.Snapshot());

            try { await Task.Delay(f.NextRetryIn, ct); }
            catch (TaskCanceledException) { return; }

            f.Status = RestoreFileStatus.Retrying;
            f.AttemptsMade++;
            Progress?.Invoke(outcome.Snapshot());

            var args = new List<string>
            {
                "-log", "restore",
                "-r", request.Revision.ToString(),
                "-storage", request.StorageName,
            };
            if (request.Overwrite) args.Add("-overwrite");
            args.Add("--"); args.Add(f.Path);

            var exit = await RunDuplicacyAsync(
                workingDir: restoreRepo,
                args: args,
                pwd: pwd,
                onLine: line =>
                {
                    var prefixed = $"[retry:{f.Path}] {line}";
                    Log?.Invoke(prefixed);
                    // Mirror retry output into the global live-tail
                    // feed so the Activity & logs Live pane shows the
                    // ongoing per-file retries during a slow/flaky
                    // restore.
                    _runner.RaiseLineWritten(prefixed);
                },
                ct: ct,
                envVars: env);

            var onDisk = Path.Combine(restoreRepo, f.Path.Replace('/', Path.DirectorySeparatorChar));
            if (exit == 0 && File.Exists(onDisk))
            {
                f.Status = RestoreFileStatus.Restored;
                f.NextRetryIn = TimeSpan.Zero;
                FileRestored?.Invoke(f);
                Progress?.Invoke(outcome.Snapshot());
                return;
            }

            f.LastError = exit == 0 ? "File not present after restore" : $"duplicacy exit {exit}";
        }

        // Cascade exhausted.
        f.Status = RestoreFileStatus.Failed;
        FileFailed?.Invoke(f);
        Progress?.Invoke(outcome.Snapshot());
    }

    // ---- raw process invocation ----

    private async Task<int> RunDuplicacyAsync(string workingDir, IEnumerable<string> args, string? pwd,
                                              Action<string>? onLine, CancellationToken ct,
                                              IReadOnlyDictionary<string, string>? envVars = null)
    {
        var exe = DuplicacyEmbedder.EnsureExtracted();
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) if (!string.IsNullOrEmpty(a)) psi.ArgumentList.Add(a);

        if (envVars is not null)
        {
            foreach (var (k, v) in envVars)
                if (!string.IsNullOrEmpty(v)) psi.Environment[k] = v;
        }

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine?.Invoke(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) onLine?.Invoke("[err] " + e.Data); };

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
            // Pass `ct` here so a cancellation whose Kill failed (handle
            // lost, AV holds it, access denied) still unwinds the wait
            // instead of hanging this thread forever on a zombie. The
            // OperationCanceledException is rethrown to the caller.
            await p.WaitForExitAsync(ct);
        }
        return p.ExitCode;
    }
}

// ================= request / result shapes =================

public sealed class RestoreRequest
{
    public required Backup Backup { get; init; }

    /// <summary>
    /// Which source of the backup this restore is for. Multi-source
    /// backups have independent snapshot chains per source (each is its
    /// own Duplicacy repo), so the restore wizard picks a source
    /// explicitly when more than one exists. Must equal one of
    /// <see cref="Models.Backup.SourcePaths"/>.
    /// </summary>
    public required string SourcePath { get; init; }

    public required Destination Destination { get; init; }
    public required string StorageName { get; init; }
    public required int Revision { get; init; }

    /// <summary>Relative forward-slash paths as Duplicacy reports them in `list -files`.</summary>
    public required IReadOnlyList<string> Files { get; init; }

    /// <summary>
    /// Absolute path to the restore target. Must already exist or we create it.
    /// If this is the backup's original source path, we've confirmed via the
    /// OVERWRITE dialog up-stack.
    /// </summary>
    public required string TargetPath { get; init; }

    public bool Overwrite { get; init; } = true;
    public bool PreserveStructure { get; init; } = true;
    public int Threads { get; init; } = 4;

    /// <summary>Null or empty = use AppConfig.Restore.RetryDelaysSeconds.</summary>
    public IReadOnlyList<int>? RetryDelaysSeconds { get; init; }
}

public enum RestoreFileStatus { Pending, Retrying, WaitingRetry, Restored, Failed }

public sealed class RestoredFile
{
    public required string Path { get; set; }
    public RestoreFileStatus Status { get; set; } = RestoreFileStatus.Pending;
    public int AttemptsMade { get; set; }
    public TimeSpan NextRetryIn { get; set; }
    public string? LastError { get; set; }
}

public sealed class RestoreOutcome
{
    public int RevisionNumber { get; init; }
    public string TargetPath { get; init; } = "";
    public int TotalFiles { get; init; }
    public Dictionary<string, RestoredFile> Files { get; init; } = new();

    public int RestoredCount => Files.Values.Count(f => f.Status == RestoreFileStatus.Restored);
    public int FailedCount   => Files.Values.Count(f => f.Status == RestoreFileStatus.Failed);
    public int InFlightCount => Files.Values.Count(f => f.Status is RestoreFileStatus.Retrying or RestoreFileStatus.WaitingRetry);

    public RestoreProgressSnapshot Snapshot() => new(
        TotalFiles, RestoredCount, FailedCount, InFlightCount);
}

public readonly record struct RestoreProgressSnapshot(int Total, int Restored, int Failed, int InFlight);
