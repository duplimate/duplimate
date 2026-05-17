using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Duplimate.Models;
using Serilog;

namespace Duplimate.Services;

/// <summary>
/// "Test restore" drill — the user-facing answer to "is this backup
/// actually restorable?". Picks ten random non-empty files from the
/// latest snapshot, restores them to a temp dir, and (when possible)
/// byte-compares each one against the live source.
///
/// Why this exists: a backup that succeeds for six months but produces
/// unrestorable bytes is the silent-killer scenario for any backup
/// product. "Run succeeded" alone isn't a trustworthy signal — the
/// only real signal is "we've actually pulled bytes back and verified
/// them." This drill turns that into a button.
///
/// Source-state handling. Sources change all the time, so a strict
/// "byte equal current source" check would be a noisy false-fail
/// machine. The drill is conservative:
///   * Restored bytes vs snapshot's recorded size — always asserted.
///     A mismatch means real corruption.
///   * Restored bytes vs current source bytes — only when the source
///     file's mtime is older than the snapshot creation time. A source
///     modified after the backup is expected to differ; we report
///     <see cref="FileVerifyStatus.OkNoCompare"/> in that case.
///
/// Cadence. The verifier is callable on demand from the Dashboard tile
/// ("Test restore" button) and the GUI flags any backup whose
/// <c>LastVerifyUtc</c> is older than 30 days. Auto-triggering would
/// silently consume bandwidth on cloud destinations; we make it the
/// user's choice.
/// </summary>
public sealed class BackupVerifier
{
    /// <summary>How many random files to sample from the latest revision.</summary>
    public const int FilesToSample = 10;

    /// <summary>Backups whose LastVerifyUtc is older than this are flagged on the Dashboard.</summary>
    public static readonly TimeSpan VerifyStaleAfter = TimeSpan.FromDays(30);

    private static ILogger _log => AppLogger.For<BackupVerifier>();

    private readonly ConfigStore _config;
    private readonly RestoreEngine _restore;
    private readonly RevisionBrowser _revisions;

    public BackupVerifier(ConfigStore config, RestoreEngine restore, RevisionBrowser revisions)
    {
        _config = config;
        _restore = restore;
        _revisions = revisions;
    }

    /// <summary>
    /// Run a verify drill against EVERY reachable destination this
    /// backup writes to. A multi-destination backup gets one verify
    /// per destination; the per-destination outcomes are combined into
    /// a single <see cref="VerifyOutcome"/> with file results
    /// concatenated and the OverallPass flag set to false if any one
    /// destination's verify failed. Each destination is independently
    /// try/caught so a Dropbox token expiry can't stop the verify of
    /// the local destination running in the same drill.
    /// </summary>
    public async Task<VerifyOutcome> VerifyAsync(Backup backup, CancellationToken ct)
    {
        // Maintenance gate (Erase / Wipe / Prune). Same as backup +
        // restore — test-restore drills also touch the storage chain
        // (read-only, but Duplicacy still acquires per-storage locks
        // we don't want to fight). Returns immediately when idle.
        // Cancellation is intentionally NOT swallowed: shaping a
        // user-cancel as `Failed(... "Cancelled")` would call
        // PersistOutcome and persist a false FAILED run record. The
        // sole caller (BackupsViewModel.TestRestore) already wraps
        // the call in `catch (Exception ex)` which handles OCE
        // quietly without polluting run history.
        await ServiceLocator.Maintenance.WaitForReadAsync(ct);

        var started = DateTime.UtcNow;
        using var _syncing = AppStatus.BeginSyncing();

        // Register a synthetic "Running" RunRecord so the LogsView's
        // RUN dropdown shows the test-restore drill in flight (just
        // like backup runs surface via Orchestrator.TryGetRunningRecord).
        // The ID stays the same across the lifetime; AppendRun at end
        // re-uses it so the persisted entry IS the same row the user
        // clicked while it was in flight. The user reported: "Doing
        // a test restore shows in the Live view instead of as a
        // Running test restore run (in RUN)."
        var runRecord = new RunRecord
        {
            BackupId   = backup.Id,
            BackupName = backup.Name,
            Kind       = RunKind.TestRestore,
            StartedUtc = started,
            Status     = BackupRunStatus.Running,
            Summary    = "Running…",
        };
        ServiceLocator.Activity.Register(runRecord);

        // Capture every stdout line the engine emits during the
        // drill so the persisted log file contains the actual
        // duplicacy output, not just the bullet-list summary
        // (writing only the summary made the terminal pane
        // replace the live log with that summary at end-of-run —
        // the user's exact complaint). The handler is hot-path
        // safe: append to a local StringBuilder, no allocation
        // per line beyond the one Append.
        var capturedLog = new System.Text.StringBuilder();
        Action<string> tee = ln =>
        {
            try { capturedLog.AppendLine(ln); } catch { /* never fail the drill on log capture */ }
        };
        ServiceLocator.Runner.LineWritten += tee;

        // Tell the Logs &amp; runs view to flip into Live mode AND
        // pre-select the in-flight Running entry on this backup so
        // the user sees the test-restore stream as it happens.
        AppNavigator.SignalLiveActivity(backup.Name);

        try
        {
            return await VerifyCoreAsync(backup, runRecord, started, capturedLog, ct);
        }
        finally
        {
            // Symmetrical cleanup: always unsubscribe + unregister
            // even on early-return / cancellation / unexpected throw.
            ServiceLocator.Runner.LineWritten -= tee;
            ServiceLocator.Activity.Unregister(runRecord.Id);
        }
    }

    private async Task<VerifyOutcome> VerifyCoreAsync(
        Backup backup,
        RunRecord runRecord,
        DateTime started,
        System.Text.StringBuilder capturedLog,
        CancellationToken ct)
    {
        var sourcePath = backup.SourcePaths.FirstOrDefault();
        if (string.IsNullOrEmpty(sourcePath))
            return Failed(backup, started, "Backup has no source paths configured.");

        var reachableDests = ResolveReachableDestinations(backup);
        if (reachableDests.Count == 0)
            return Failed(backup, started, "No reachable destination — drives unmounted, no destinations configured, or all targets unreachable.");

        // Per-destination outcomes — collected as structured items so
        // the final Summary can group successes vs failures with bullet
        // lists (replaces the previous single ` · `-joined string the
        // user reported as unreadable). Each item carries enough
        // information to render the bullet line directly.
        var allFileResults = new List<FileVerifyResult>();
        var successItems = new List<string>(); // bullet body, e.g. "«Local» — rev 6 from 2026-04-29 09:15"
        var failedItems  = new List<string>(); // bullet body, e.g. "«Dropbox» — no snapshots yet"
        var perDestErrors = new List<string>();
        int destinationsOk = 0;
        int destinationsFailed = 0;
        int latestRevisionAcrossDests = 0;
        // Captured from the FIRST successful destination so the success
        // headline can read "10 files restored, 10 byte-identical (same
        // for all destinations normally)" once instead of repeating per
        // destination — multi-destination drills sample identical file
        // sets so the counts match.
        int? successFilesPicked = null;
        int? successFilesByteIdentical = null;

        foreach (var dest in reachableDests)
        {
            ct.ThrowIfCancellationRequested();
            var target = backup.Targets.FirstOrDefault(t => t.DestinationId == dest.Id);
            if (target is null) continue; // shouldn't happen — ResolveReachable filters by Targets

            try
            {
                var (perDestResults, latest, latestCreatedUtc, err) = await VerifyOneDestinationAsync(
                    backup, sourcePath, dest, target, started, ct);
                if (err is not null)
                {
                    destinationsFailed++;
                    perDestErrors.Add($"«{dest.Name}»: {err}");
                    failedItems.Add($"«{dest.Name}» — {err}");
                    continue;
                }
                latestRevisionAcrossDests = Math.Max(latestRevisionAcrossDests, latest);
                allFileResults.AddRange(perDestResults);
                var failedHere = perDestResults.Count(r => r.Status is
                    FileVerifyStatus.SizeMismatch or
                    FileVerifyStatus.BytesMismatch or
                    FileVerifyStatus.RestoreFailed);
                // Surface the revision's local-time date in the summary
                // so the user knows WHEN the data they restored was
                // captured. "rev 7 from 2026-04-29 14:32" reads more
                // honestly than just "rev 7" — same backup at different
                // points in time is the whole point of revisions.
                var revStamp = $"rev {latest} from {latestCreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
                if (failedHere == 0)
                {
                    destinationsOk++;
                    var ok = perDestResults.Count(r => r.Status == FileVerifyStatus.OK);
                    successItems.Add($"«{dest.Name}» — {revStamp}");
                    successFilesPicked ??= perDestResults.Count;
                    successFilesByteIdentical ??= ok;
                }
                else
                {
                    destinationsFailed++;
                    failedItems.Add(
                        $"«{dest.Name}» — {revStamp} · {failedHere} of {perDestResults.Count} files FAILED verification");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Defensive: any throw escaping VerifyOneDestinationAsync
                // (an entire async-thrown exception or somehow bypassed
                // catch upstream) is recorded against THIS destination
                // and the loop continues. Earlier this would propagate
                // and fail the whole drill, including any destinations
                // the user could have gotten OK results for — that's
                // the user's "Test restore crashed the app" report.
                _log.Error(ex, "Verify of destination {Name} threw", dest.Name);
                destinationsFailed++;
                perDestErrors.Add($"«{dest.Name}»: {ex.Message}");
                failedItems.Add($"«{dest.Name}» — error: {ex.Message}");
            }
        }

        // Whole-drill verdict: OK iff every destination's verify
        // passed. A backup with two destinations and one of them
        // broken is NOT a passing verify.
        var pass = destinationsFailed == 0 && destinationsOk > 0;
        var totalDests = reachableDests.Count;
        var overallSummary = BuildOverallSummary(
            totalDests, destinationsOk, destinationsFailed,
            successItems, failedItems,
            successFilesPicked ?? 0, successFilesByteIdentical ?? 0);

        var outcome = new VerifyOutcome(
            BackupId: backup.Id,
            BackupName: backup.Name,
            StartedUtc: started,
            EndedUtc: DateTime.UtcNow,
            RevisionNumber: latestRevisionAcrossDests,
            FilesPicked: allFileResults.Count,
            FilesOk: allFileResults.Count(r => r.Status is FileVerifyStatus.OK or FileVerifyStatus.OkNoCompare),
            FilesFailed: allFileResults.Count(r => r.Status is
                FileVerifyStatus.SizeMismatch or
                FileVerifyStatus.BytesMismatch or
                FileVerifyStatus.RestoreFailed),
            OverallPass: pass,
            Files: allFileResults,
            FatalError: perDestErrors.Count > 0 ? string.Join(" · ", perDestErrors) : null,
            Summary: overallSummary);

        // Persist a RunRecord so the test restore appears in the
        // Logs & runs RUN dropdown alongside backup and restore
        // entries — the user explicitly asked: "When doing a test
        // restore or a restore operation, it should show up the
        // related logs in Activity & logs". Best-effort: a failed
        // persist must never demote a successful drill.
        try { PersistTestRestoreRunRecord(backup, outcome, runRecord.Id, capturedLog.ToString()); }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to persist test-restore RunRecord for {Backup}", backup.Name);
        }

        return outcome;
    }

    /// <summary>
    /// Append a test-restore RunRecord to the backup's per-name
    /// history.json so the LogsView surfaces it. Writes the verifier's
    /// rendered <see cref="VerifyOutcome.Summary"/> as the log file
    /// (it's already the human-readable bullet-list form the
    /// notification uses, which is what the user wants to see in
    /// the terminal pane).
    /// </summary>
    private void PersistTestRestoreRunRecord(Backup backup, VerifyOutcome outcome, string runId, string capturedLog)
    {
        string? logPath = null;
        try
        {
            var dir = AppPaths.LogDirForBackup(backup.Name);
            System.IO.Directory.CreateDirectory(dir);
            var stamp = outcome.StartedUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss");
            logPath = System.IO.Path.Combine(dir, $"{stamp}-test-restore.log");
            // Persist the actual streamed duplicacy output, then a
            // human-readable summary footer. Earlier the code wrote
            // ONLY the summary, so the LogsView's terminal pane
            // replaced the live log with that summary at end-of-run
            // — the user reported: "at the end the whole log
            // disappears and is replaced by the summary".
            var sb = new System.Text.StringBuilder();
            sb.Append(capturedLog ?? "");
            if (!string.IsNullOrWhiteSpace(outcome.Summary))
            {
                if (sb.Length > 0 && sb[^1] != '\n') sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("=== Summary ===");
                sb.Append(outcome.Summary);
            }
            System.IO.File.WriteAllText(logPath, sb.ToString());
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Couldn't write test-restore log file for {Backup}", backup.Name);
        }

        var status = outcome.OverallPass
            ? (outcome.FilesFailed == 0 ? Models.BackupRunStatus.Success : Models.BackupRunStatus.Warning)
            : Models.BackupRunStatus.Failed;

        var summary = $"rev {outcome.RevisionNumber} · {outcome.FilesOk}/{outcome.FilesPicked} byte-identical";
        if (outcome.FilesFailed > 0) summary += $" · {outcome.FilesFailed} mismatched";

        var rec = new Models.RunRecord
        {
            // Re-use the in-flight record's Id so the persisted
            // entry is the same row in the dropdown that was
            // showing as "Running" — clicking it during the run
            // and after the run lands on the same item.
            Id         = runId,
            BackupId   = backup.Id,
            BackupName = backup.Name,
            Kind       = Models.RunKind.TestRestore,
            StartedUtc = outcome.StartedUtc,
            EndedUtc   = outcome.EndedUtc,
            Status     = status,
            Summary    = summary,
            LogPath    = logPath ?? "",
        };
        ServiceLocator.Logs.AppendRun(rec);
    }

    /// <summary>
    /// Verify one destination of a backup. Returns the per-file results,
    /// the revision number tested, and an error message if the verify
    /// couldn't run against this destination at all (no snapshots, no
    /// non-empty files, etc.). Throws only on cancellation; every
    /// other failure is reported via the returned tuple.
    /// </summary>
    private async Task<(IReadOnlyList<FileVerifyResult> Results, int Revision, DateTime RevisionCreatedUtc, string? Error)>
        VerifyOneDestinationAsync(
            Backup backup, string sourcePath, Destination dest, BackupTarget target,
            DateTime started, CancellationToken ct)
    {
        // Latest revision at this destination.
        IReadOnlyList<RevisionSummary> revs;
        try
        {
            revs = await _revisions.ListRevisionsAsync(backup, sourcePath, dest, target.StorageName, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (Array.Empty<FileVerifyResult>(), 0, default,
                $"couldn't list revisions: {ex.Message}");
        }

        if (revs.Count == 0)
            return (Array.Empty<FileVerifyResult>(), 0, default,
                "no snapshots found at this destination yet — run a backup first");

        var latest = revs.OrderByDescending(r => r.Number).First();

        IReadOnlyList<RevisionFile> files;
        try
        {
            files = await _revisions.ListFilesAsync(
                backup, sourcePath, dest, target.StorageName, latest.Number, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (Array.Empty<FileVerifyResult>(), latest.Number, latest.CreatedUtc,
                $"couldn't list files in revision {latest.Number}: {ex.Message}");
        }

        var pool = files.Where(f => f.SizeBytes > 0).ToList();
        if (pool.Count == 0)
            return (Array.Empty<FileVerifyResult>(), latest.Number, latest.CreatedUtc,
                "latest snapshot has no non-empty files to verify");

        // Operator precedence pitfall: `??` binds looser than `^`, so without
        // the parens around `(dest.Id?.GetHashCode() ?? 0)` the whole xor
        // chain collapsed to null whenever dest.Id was null, and the seed
        // became deterministic 0 — every verify produced the same shuffle.
        var rng = new Random(unchecked(Environment.TickCount * 397)
                             ^ started.GetHashCode()
                             ^ (dest.Id?.GetHashCode() ?? 0));

        // Pre-filter pool to files that haven't been edited since the
        // backup captured them. Without this, sampling a recently-edited
        // file would either fail the byte comparison or fall back to
        // OkNoCompare — neither outcome actually verifies that the
        // backup is restorable to the bytes it claims. We compare the
        // CURRENT source file's mtime against the per-file snapshot
        // mtime (with the same 2-second fudge factor as VerifyOneFile),
        // so a file that was edited mid-test can be replaced with a
        // different random pick instead of polluting the result.
        // The retry cap (3× FilesToSample) guards against a pathological
        // case where every file in the pool has been touched since the
        // backup — we don't want to walk the entire pool synchronously
        // doing FileInfo on every entry. If we can't find FilesToSample
        // unchanged files within the cap, we use what we have; an
        // empty result returns a per-destination error.
        // Run the FileInfo walk on a worker thread so a slow source
        // (SMB share over VPN, slow USB drive) doesn't block the
        // awaiter chain — the user just sees the Test Restore button
        // hang otherwise. Each candidate gets a per-file timeout via
        // Task.Run + Task.Wait so a single hung handle can't stall the
        // whole drill.
        var picked = await Task.Run(() =>
        {
            var pickedLocal = new List<RevisionFile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var maxAttempts = Math.Max(FilesToSample * 3, 30);
            var poolShuffled = pool.OrderBy(_ => rng.Next()).ToList();
            var idx = 0;
            var attempts = 0;
            while (pickedLocal.Count < FilesToSample && attempts < maxAttempts && idx < poolShuffled.Count)
            {
                if (ct.IsCancellationRequested) break;
                attempts++;
                var candidate = poolShuffled[idx++];
                if (!seen.Add(candidate.Path)) continue;
                if (IsSourceUnchangedSinceSnapshot(candidate, sourcePath))
                    pickedLocal.Add(candidate);
            }
            return pickedLocal;
        }, ct);
        if (picked.Count == 0)
            return (Array.Empty<FileVerifyResult>(), latest.Number, latest.CreatedUtc,
                "every sampled file has been edited since the last backup — couldn't find any " +
                "byte-comparable file to verify within the retry cap. Try again after a fresh backup.");

        // Per-destination temp dir — including the destination id so
        // a multi-destination drill doesn't have all dests writing
        // into the same folder (which would clobber each other's
        // restored files and corrupt the per-file verify).
        var verifyRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Duplimate", "verify",
            $"{backup.Id}-{started:yyyyMMddHHmmss}-{ShortId(dest.Id)}");
        Directory.CreateDirectory(verifyRoot);
        try
        {
            var request = new RestoreRequest
            {
                Backup = backup,
                SourcePath = sourcePath,
                Destination = dest,
                StorageName = target.StorageName,
                Revision = latest.Number,
                Files = picked.Select(f => f.Path).ToList(),
                TargetPath = verifyRoot,
                Overwrite = true,
                PreserveStructure = true,
                Threads = Math.Max(1, backup.Threads == 0 ? 4 : backup.Threads),
                RetryDelaysSeconds = new[] { 2, 5 },
            };
            var outcome = await _restore.RunAsync(request, ct);
            var fileResults = picked
                .Select(f => VerifyOneFile(f, sourcePath, verifyRoot, outcome))
                .ToList();
            return (fileResults, latest.Number, latest.CreatedUtc, null);
        }
        finally
        {
            try { Directory.Delete(verifyRoot, recursive: true); }
            catch (Exception ex) { _log.Debug(ex, "Cleanup of verify temp dir failed"); }
        }
    }

    /// <summary>
    /// True iff the current source file mirrors the snapshot's recorded
    /// mtime (within the 2-second fudge factor that matches
    /// <see cref="VerifyOneFile"/>'s comparison logic). Used to skip
    /// files that have been edited since the backup ran — they'd
    /// produce <see cref="FileVerifyStatus.OkNoCompare"/> at verify
    /// time, which doesn't actually exercise the byte-compare path.
    /// Returns false when the source file is missing, unreadable, or
    /// edited after the snapshot.
    /// </summary>
    private static bool IsSourceUnchangedSinceSnapshot(RevisionFile snapshot, string sourceRoot)
    {
        try
        {
            var rel = snapshot.Path.Replace('/', Path.DirectorySeparatorChar);
            var src = Path.Combine(sourceRoot, rel);
            if (!File.Exists(src)) return false;
            var fi = new FileInfo(src);
            var snapMtimeUtc = AsUtc(snapshot.ModifiedUtc);
            return fi.LastWriteTimeUtc <= snapMtimeUtc.AddSeconds(2);
        }
        catch
        {
            // Path-too-long, permission denied, race-window deletion, etc.
            // Treat as "not safely verifiable" so we move on to another
            // candidate instead of hard-failing the drill.
            return false;
        }
    }

    private static string ShortId(string? id) =>
        string.IsNullOrEmpty(id) ? "x" : id.Substring(0, Math.Min(8, id.Length));

    /// <summary>Reachable destinations for verify — every target whose
    /// volume is mounted (local-like) or unconditionally reachable
    /// (cloud / S3). Returns the destinations in target-list order.</summary>
    private List<Destination> ResolveReachableDestinations(Backup backup)
    {
        var list = new List<Destination>();
        foreach (var t in backup.Targets)
        {
            var d = _config.Current.Destinations.FirstOrDefault(x => x.Id == t.DestinationId);
            if (d is null) continue;
            if (d.IsLocalLike)
            {
                var v = VolumeDetector.Check(d.ExpectedDriveLetter, d.ExpectedVolumeLabel);
                if (v.Verdict != VolumeDetector.Verdict.Mounted) continue;
            }
            list.Add(d);
        }
        return list;
    }

    /// <summary>
    /// Persist the outcome of a verify run onto the Backup record so the
    /// Dashboard can show "Verified 12 days ago — OK" without re-running
    /// the drill. Called by the VM after VerifyAsync returns.
    /// </summary>
    public void PersistOutcome(VerifyOutcome outcome)
    {
        _config.Update(cfg =>
        {
            var b = cfg.Backups.FirstOrDefault(x => x.Id == outcome.BackupId);
            if (b is null) return;
            b.LastVerifyUtc = outcome.EndedUtc;
            b.LastVerifyPass = outcome.OverallPass;
            b.LastVerifySummary = outcome.Summary;
        });
    }

    // ---- internals ----------------------------------------------------

    /// <summary>
    /// Normalise duplicacy-parsed timestamps (which come back as Kind=Local
    /// or Kind=Unspecified after <c>DateTime.TryParse</c>) so we can
    /// compare them against <c>FileInfo.LastWriteTimeUtc</c> without a
    /// timezone-offset false positive flagging every file as "modified
    /// after snapshot".
    /// </summary>
    private static DateTime AsUtc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Local => d.ToUniversalTime(),
        _ => DateTime.SpecifyKind(d, DateTimeKind.Local).ToUniversalTime(),
    };

    private FileVerifyResult VerifyOneFile(
        RevisionFile snapshot, string sourceRoot, string restoreRoot,
        RestoreOutcome restoreOutcome)
    {
        var rel = snapshot.Path.Replace('/', Path.DirectorySeparatorChar);
        var restoredPath = Path.Combine(restoreRoot, rel);

        var didRestore = restoreOutcome.Files.TryGetValue(snapshot.Path, out var restoredInfo)
            && restoredInfo!.Status == RestoreFileStatus.Restored;

        if (!didRestore || !File.Exists(restoredPath))
        {
            return new FileVerifyResult(
                Path: snapshot.Path,
                ExpectedBytes: snapshot.SizeBytes,
                ActualBytes: 0,
                Status: FileVerifyStatus.RestoreFailed,
                Detail: restoredInfo?.LastError ?? "File missing after restore.");
        }

        var restoredFi = new FileInfo(restoredPath);
        if (restoredFi.Length != snapshot.SizeBytes)
        {
            return new FileVerifyResult(
                Path: snapshot.Path,
                ExpectedBytes: snapshot.SizeBytes,
                ActualBytes: restoredFi.Length,
                Status: FileVerifyStatus.SizeMismatch,
                Detail: $"Snapshot recorded {snapshot.SizeBytes:N0} bytes, restored file is {restoredFi.Length:N0} bytes.");
        }

        // Byte-compare against current source when possible.
        var sourcePath = Path.Combine(sourceRoot, rel);
        if (!File.Exists(sourcePath))
        {
            return new FileVerifyResult(
                snapshot.Path, snapshot.SizeBytes, restoredFi.Length,
                FileVerifyStatus.OkNoCompare,
                "Source file no longer exists; size matches snapshot — byte comparison skipped.");
        }

        var srcFi = new FileInfo(sourcePath);
        // 2-second mtime fudge factor: filesystems don't always agree
        // sub-second between read paths. Compare against the per-file
        // recorded mtime (normalised to UTC), not the whole snapshot's
        // created time — the per-file value tracks the file we're
        // actually comparing.
        var snapMtimeUtc = AsUtc(snapshot.ModifiedUtc);
        if (srcFi.LastWriteTimeUtc > snapMtimeUtc.AddSeconds(2))
        {
            return new FileVerifyResult(
                snapshot.Path, snapshot.SizeBytes, restoredFi.Length,
                FileVerifyStatus.OkNoCompare,
                "Source modified after snapshot; size matches — byte comparison skipped.");
        }

        if (srcFi.Length != restoredFi.Length)
        {
            return new FileVerifyResult(
                snapshot.Path, snapshot.SizeBytes, restoredFi.Length,
                FileVerifyStatus.OkNoCompare,
                "Source size differs from snapshot — byte comparison skipped.");
        }

        return BytesEqual(sourcePath, restoredPath)
            ? new FileVerifyResult(
                snapshot.Path, snapshot.SizeBytes, restoredFi.Length,
                FileVerifyStatus.OK, null)
            : new FileVerifyResult(
                snapshot.Path, snapshot.SizeBytes, restoredFi.Length,
                FileVerifyStatus.BytesMismatch,
                "Restored bytes differ from source bytes — possible backup corruption.");
    }

    private static bool BytesEqual(string p1, string p2)
    {
        const int BufSize = 64 * 1024;
        using var f1 = File.OpenRead(p1);
        using var f2 = File.OpenRead(p2);
        if (f1.Length != f2.Length) return false;
        var b1 = new byte[BufSize];
        var b2 = new byte[BufSize];
        while (true)
        {
            int n1 = f1.Read(b1, 0, BufSize);
            int n2 = f2.Read(b2, 0, BufSize);
            if (n1 != n2) return false;
            if (n1 == 0) return true;
            if (!b1.AsSpan(0, n1).SequenceEqual(b2.AsSpan(0, n2))) return false;
        }
    }

    /// <summary>
    /// Build the multi-line user-facing summary for a verify drill. The
    /// shape (per user feedback 2026-04-29) is:
    /// <list type="bullet">
    ///   <item><c>"X of Y destinations FAILED:"</c> + blank line + bullet list of failures</item>
    ///   <item>blank line</item>
    ///   <item><c>"Z of Y destinations SUCCEEDED: 10 files restored, 10 byte-identical"</c> + blank line + bullet list of successes</item>
    /// </list>
    /// Replaces the previous middle-dot-joined run-on string ("...FAILED ·
    /// «Local» ... · «Dropbox» ...") which was unreadable. Single-
    /// destination drills get the simpler single-line format —
    /// the bullet structure is overkill when there's only one item.
    /// </summary>
    private static string BuildOverallSummary(
        int totalDests, int destinationsOk, int destinationsFailed,
        IReadOnlyList<string> successItems, IReadOnlyList<string> failedItems,
        int filesPicked, int filesByteIdentical)
    {
        if (destinationsOk == 0 && destinationsFailed == 0)
            return "No destinations could be verified.";

        // Single-destination short form. Doesn't need the grouped layout.
        if (totalDests == 1)
        {
            if (destinationsFailed == 0)
                return successItems.Count > 0
                    ? $"{successItems[0]} · {filesPicked} files restored, {filesByteIdentical} byte-identical"
                    : "Verified.";
            return failedItems.Count > 0 ? failedItems[0] : "Failed.";
        }

        var sb = new System.Text.StringBuilder();

        // Failures group first — they're the urgent thing to act on.
        // Single line break before each list item (was double — the
        // extra blank line between the header and the first bullet
        // looked like a missing item).
        if (destinationsFailed > 0)
        {
            sb.Append($"{destinationsFailed} of {totalDests} destinations FAILED:");
            foreach (var item in failedItems)
            {
                sb.AppendLine();
                sb.Append("  • ");
                sb.Append(item);
            }
        }

        if (destinationsOk > 0)
        {
            if (sb.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine();
            }
            sb.Append($"{destinationsOk} of {totalDests} destinations SUCCEEDED: ");
            sb.Append($"{filesPicked} files restored, {filesByteIdentical} byte-identical");
            sb.Append(" (same for all destinations normally)");
            foreach (var item in successItems)
            {
                sb.AppendLine();
                sb.Append("  • ");
                sb.Append(item);
            }
        }

        return sb.ToString();
    }

    private static VerifyOutcome Failed(Backup backup, DateTime started, string error) =>
        new(
            BackupId: backup.Id,
            BackupName: backup.Name,
            StartedUtc: started,
            EndedUtc: DateTime.UtcNow,
            RevisionNumber: 0,
            FilesPicked: 0,
            FilesOk: 0,
            FilesFailed: 0,
            OverallPass: false,
            Files: Array.Empty<FileVerifyResult>(),
            FatalError: error,
            Summary: error);
}

// =================== outcome shapes ===================

/// <summary>
/// Per-file result from a verify drill. <see cref="FileVerifyStatus.OK"/>
/// means the bytes were compared against the live source and matched;
/// <see cref="FileVerifyStatus.OkNoCompare"/> means we couldn't compare
/// (source missing or modified after the snapshot) but the restore size
/// matched the snapshot's recorded size.
/// </summary>
public enum FileVerifyStatus
{
    OK,
    OkNoCompare,
    SizeMismatch,
    BytesMismatch,
    RestoreFailed,
}

public sealed record FileVerifyResult(
    string Path,
    long ExpectedBytes,
    long ActualBytes,
    FileVerifyStatus Status,
    string? Detail);

public sealed record VerifyOutcome(
    string BackupId,
    string BackupName,
    DateTime StartedUtc,
    DateTime EndedUtc,
    int RevisionNumber,
    int FilesPicked,
    int FilesOk,
    int FilesFailed,
    bool OverallPass,
    IReadOnlyList<FileVerifyResult> Files,
    string? FatalError,
    string Summary);
