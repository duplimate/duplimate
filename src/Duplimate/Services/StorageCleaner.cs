using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Duplimate.Models;
using Serilog;

namespace Duplimate.Services;

/// <summary>
/// Cascading cleanup of Duplicacy storage backing a Backup or Destination,
/// invoked from the two "delete" flows in the UI.
///
///   • EraseBackupFromDestinationAsync — removes just ONE backup's
///     snapshot chain (and its now-orphaned chunks) from a destination,
///     leaving other backups sharing that storage untouched.
///
///   • WipeEntireDestinationAsync — nukes EVERYTHING at a destination.
///     Used when the user is removing the destination itself; the caller
///     refuses up-front if any backup still points at it.
///
/// For Local / External / Network kinds we use straight filesystem ops
/// — fast, obvious, no surprises. For cloud (Dropbox / OneDrive / Google
/// Drive) and S3 we drive Duplicacy itself: it has the credentials, the
/// rename/delete semantics per provider, and a tested retry path. We do
/// NOT ask the user to clean up anything manually — the user's directive
/// is that the app always finishes the job.
/// </summary>
public sealed class StorageCleaner
{
    private static ILogger _log => AppLogger.For<StorageCleaner>();
    private readonly ConfigStore _config;
    private readonly SecretsStore _secrets;
    private readonly DuplicacyRunner _runner;

    public StorageCleaner(ConfigStore config, SecretsStore secrets, DuplicacyRunner runner)
    {
        _config = config;
        _secrets = secrets;
        _runner = runner;
    }

    /// <summary>Probe used to enumerate snapshot ids on remote storages so
    /// the wipe path can iterate them. Set by ServiceLocator after both
    /// services are constructed to avoid a circular dependency at init.</summary>
    public DestinationProbe? Probe { get; set; }

    /// <summary>
    /// Activity-log sink. When set, each Erase / Wipe operation:
    ///   • opens a per-run log file under
    ///     <see cref="AppPaths.LogDirForBackup"/> for the relevant
    ///     bucket (real backup name, or the <c>[Destinations]</c>
    ///     sentinel for destination-wide wipes);
    ///   • captures every line of duplicacy stdout/stderr the
    ///     <see cref="DuplicacyRunner"/> emits while the operation
    ///     runs;
    ///   • appends a structured <see cref="RunRecord"/> to the
    ///     store on completion so the Activity &amp; Logs view's RUN
    ///     dropdown surfaces the entry alongside backup / restore
    ///     runs. Null = no log emission (used by tests).
    /// </summary>
    public LogStore? LogStore { get; set; }

    /// <summary>
    /// Bucket name used in the activity log when a wipe is for a
    /// whole destination (i.e. not tied to a specific backup). The
    /// user-facing string keeps the literal square brackets so it
    /// sorts and reads as obviously synthetic in the backup dropdown.
    /// </summary>
    public const string DestinationsBucketName = "[Destinations]";

    /// <summary>
    /// Fan-out IProgress: forwards every report to both inner
    /// channels. Used so erase/wipe progress text reaches both the
    /// user-facing modal AND the persisted activity log without
    /// having to call .Report twice at every emission site.
    /// </summary>
    private sealed class ChainedProgress<T> : IProgress<T>
    {
        private readonly IProgress<T> _a;
        private readonly IProgress<T> _b;
        public ChainedProgress(IProgress<T> a, IProgress<T> b) { _a = a; _b = b; }
        public void Report(T value)
        {
            try { _a.Report(value); } catch { }
            try { _b.Report(value); } catch { }
        }
    }

    /// <summary>
    /// One Erase / Wipe operation's transcript on disk. Hooks
    /// <see cref="DuplicacyRunner.LineWritten"/> so every line of
    /// duplicacy stdout/stderr lands in the log alongside our own
    /// status messages. Disposing detaches the hook and finalises the
    /// log header — the caller then constructs a
    /// <see cref="RunRecord"/> pointing at <see cref="Path"/> and
    /// hands it to <see cref="LogStore"/>.
    /// </summary>
    private sealed class RunLogSession : IDisposable
    {
        // _runner is nullable: unit tests construct StorageCleaner with
        // runner:null! for the local-path code (which never shells out)
        // — see StorageCleanerTests. The duplicacy-output hook is just
        // skipped in that case; our own Status() writes still land.
        private readonly DuplicacyRunner? _runner;
        private readonly System.IO.StreamWriter _writer;
        private readonly Action<string>? _hook;
        private readonly object _gate = new();

        public string Path { get; }
        public DateTime StartedUtc { get; }

        public RunLogSession(DuplicacyRunner? runner, string bucketName, string headline)
        {
            _runner = runner;
            StartedUtc = DateTime.UtcNow;
            var dir = AppPaths.LogDirForBackup(bucketName);
            System.IO.Directory.CreateDirectory(dir);
            // Filename schema mirrors DuplicacyRunner.PrepareLogFile —
            // UTC + milliseconds + 6-char random — so concurrent
            // operations and DST fall-back can't collide.
            var stamp = StartedUtc.ToString("yyyyMMddHHmmssfff");
            var rand = Guid.NewGuid().ToString("N").Substring(0, 6);
            Path = System.IO.Path.Combine(dir, $"{stamp}-erase-{rand}.log");
            _writer = new System.IO.StreamWriter(Path, append: false, System.Text.Encoding.UTF8);
            WriteLine($"=== {headline} ===");
            WriteLine($"Started:  {StartedUtc:O} UTC");
            if (_runner is not null)
            {
                _hook = line => WriteLine(line);
                _runner.LineWritten += _hook;
            }
        }

        public void Status(string message) => WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

        private void WriteLine(string line)
        {
            try
            {
                lock (_gate)
                {
                    _writer.WriteLine(line);
                    _writer.Flush();
                }
            }
            catch
            {
                // Best-effort: a failing log write must NEVER abort the
                // erase. We swallow and continue.
            }
        }

        public void Dispose()
        {
            try { if (_runner is not null && _hook is not null) _runner.LineWritten -= _hook; } catch { }
            try
            {
                lock (_gate)
                {
                    _writer.WriteLine($"Ended:    {DateTime.UtcNow:O} UTC");
                    _writer.Flush();
                    _writer.Dispose();
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Persist a <see cref="RunRecord"/> for an Erase / Wipe operation
    /// so the Activity &amp; Logs view's RUN dropdown surfaces it. No-op
    /// if <see cref="LogStore"/> isn't wired (test fixtures).
    /// </summary>
    private void PublishRunRecord(
        string bucketName, string bucketId, RunLogSession session,
        CleanupReport report, string summary)
    {
        if (LogStore is null) return;
        var status = report.Errors.Count > 0 ? BackupRunStatus.Warning : BackupRunStatus.Success;
        var record = new RunRecord
        {
            BackupId = bucketId,
            BackupName = bucketName,
            Kind = RunKind.Erase,
            StartedUtc = session.StartedUtc,
            EndedUtc = DateTime.UtcNow,
            Status = status,
            Summary = summary,
            LogPath = session.Path,
            Errors = new List<string>(report.Errors),
        };
        try { LogStore.AppendRun(record); }
        catch (Exception ex) { _log.Warning(ex, "Failed to append Erase RunRecord to log store"); }
    }

    /// <summary>
    /// Test seam for the snapshot-id listing step in
    /// <see cref="EraseSnapshotsViaDuplicacyAsync"/>. Production code
    /// leaves this null, falling through to <see cref="Probe"/>. Tests
    /// set it to a fake that returns canned data so they can drive the
    /// probe-first short-circuit (re-running Erase against an empty
    /// destination must report "nothing to remove" without spawning
    /// duplicacy). Returning null means "I don't know" — callers fall
    /// back to the historical blind-prune behaviour.
    /// </summary>
    internal Func<Destination, CancellationToken, Task<HashSet<string>?>>? TestSnapshotIdLister;

    /// <summary>
    /// Removes the data Duplimate wrote for <paramref name="backup"/>
    /// from <paramref name="destination"/>. Other backups sharing the
    /// same storage root are untouched.
    /// </summary>
    public async Task<CleanupReport> EraseBackupFromDestinationAsync(
        Backup backup, Destination destination, string storageName, CancellationToken ct,
        IProgress<string>? progress = null,
        IProgress<int?>? chunkPercent = null)
    {
        var report = new CleanupReport(destination.Name);
        using var session = new RunLogSession(_runner, backup.Name,
            $"Erase backup «{backup.Name}» from destination «{destination.Name}»");
        // Bridge progress text to the activity log so the persisted
        // transcript has the same human narrative the modal showed.
        var teeProgress = progress is null
            ? (IProgress<string>)new Progress<string>(session.Status)
            : new ChainedProgress<string>(progress, new Progress<string>(session.Status));

        // Gate on the same per-destination semaphore that
        // WipeEntireDestinationAsync uses. Without this, a "delete
        // backup" + "remove destination" issued back-to-back would race
        // each other on the SAME snapshots/<id> directory tree —
        // EraseSnapshotsLocally walks it while WipeLocalRoot is also
        // deleting it, throwing IOException mid-walk and leaving the
        // storage half-cleaned. Same gate also serialises against any
        // concurrent backup completing into the same destination.
        teeProgress.Report("Waiting for the destination to be free…");
        var gate = _wipeGates.GetOrAdd(destination.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var snapshotIds = backup.SourcePaths
                .Select(sp => DuplicacyRunner.SnapshotIdFor(backup, sp))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (destination.IsLocalLike)
            {
                teeProgress.Report($"Removing local snapshots for «{backup.Name}»…");
                EraseSnapshotsLocally(destination, snapshotIds, report);
            }
            else
            {
                teeProgress.Report($"Asking Duplicacy to drop «{backup.Name}»'s chunks at «{destination.Name}»…");
                await EraseSnapshotsViaDuplicacyAsync(backup, destination, storageName, snapshotIds, report, ct, teeProgress, chunkPercent);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Erase-backup failed at destination {Name}", destination.Name);
            report.Errors.Add(ex.Message);
        }
        finally
        {
            gate.Release();
        }
        teeProgress.Report("Done.");
        PublishRunRecord(
            bucketName: backup.Name, bucketId: backup.Id,
            session, report,
            summary: $"Erased «{backup.Name}» from «{destination.Name}» — "
                    + $"{report.DeletedSnapshotIds.Count} snapshot id(s) removed"
                    + (report.Errors.Count > 0 ? $", {report.Errors.Count} error(s)" : ""));
        return report;
    }

    /// <summary>
    /// Deletes every file Duplimate could have written at
    /// <paramref name="destination"/>'s storage root. Used by the
    /// destination-delete flow.
    /// </summary>
    public async Task<CleanupReport> WipeEntireDestinationAsync(
        Destination destination, CancellationToken ct,
        IProgress<string>? progress = null,
        IProgress<int?>? chunkPercent = null)
    {
        // Whole-destination wipes don't belong to a single backup, so
        // they log into the [Destinations] bucket — that name shows
        // up in the Activity & Logs backup dropdown as a synthetic
        // entry holding every destination-delete operation.
        using var session = new RunLogSession(_runner, DestinationsBucketName,
            $"Delete destination «{destination.Name}» ({destination.Kind})");
        var teeProgress = progress is null
            ? (IProgress<string>)new Progress<string>(session.Status)
            : new ChainedProgress<string>(progress, new Progress<string>(session.Status));

        // Per-destination semaphore. Two concurrent wipes against the same
        // destination would race in `Directory.Delete` (one sees the dir
        // disappear mid-iteration) for local kinds, and would race
        // duplicacy prune commands against the same storage URL for cloud
        // kinds — risking metadata corruption either way. Serialize them.
        teeProgress.Report("Waiting for the destination to be free…");
        var gate = _wipeGates.GetOrAdd(destination.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var report = new CleanupReport(destination.Name);
            try
            {
                if (destination.IsLocalLike)
                {
                    teeProgress.Report($"Removing Duplicacy data inside «{destination.PathOrSubpath}»…");
                    WipeLocalRoot(destination, report);
                }
                else
                {
                    teeProgress.Report($"Asking Duplicacy to clear «{destination.Name}»…");
                    await WipeRemoteRootAsync(destination, report, ct, teeProgress, chunkPercent);
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Wipe-destination failed for {Name}", destination.Name);
                report.Errors.Add(ex.Message);
            }
            teeProgress.Report("Done.");
            PublishRunRecord(
                bucketName: DestinationsBucketName, bucketId: "",
                session, report,
                summary: $"Deleted destination «{destination.Name}» — "
                        + $"{report.DeletedSnapshotIds.Count} snapshot id(s) removed"
                        + (report.Errors.Count > 0 ? $", {report.Errors.Count} error(s)" : ""));
            return report;
        }
        finally
        {
            gate.Release();
        }
    }

    // Instance-scoped (was static) so test fixtures that construct their
    // own StorageCleaner don't share gate state across the suite, and so
    // a process-lifetime singleton's gate dictionary can be cleaned up
    // when the cleaner itself is replaced (e.g. service-locator reinit
    // in tests). The semantics that matter — serialising concurrent
    // operations against the SAME destination — still hold because
    // production wires StorageCleaner as a singleton via ServiceLocator.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _wipeGates = new();

    /// <summary>
    /// Drop the per-destination wipe gate. Called by
    /// <see cref="ViewModels.DestinationsViewModel"/> after a successful
    /// destination removal so the dictionary doesn't grow unboundedly
    /// across the lifetime of a long-running app instance.
    /// Safe to call for destinations that never had a gate created.
    /// </summary>
    public void ForgetWipeGate(string destinationId)
    {
        // Just remove from the dict and let the GC reclaim the
        // SemaphoreSlim once all holders' local references go out of
        // scope. We deliberately do NOT Dispose, even when
        // CurrentCount==1 looks safe — there's a window between
        // EraseBackupFromDestinationAsync's `_wipeGates.GetOrAdd(...)`
        // returning and its `await gate.WaitAsync(ct)` actually
        // taking the count down to 0. ForgetWipeGate firing in that
        // window observes CurrentCount==1, disposes, and the holder's
        // WaitAsync then throws ObjectDisposedException. The earlier
        // "atomic-remove-first then check count" mitigated one shape
        // of the bug but couldn't cover the GetOrAdd-vs-WaitAsync
        // gap. Skipping Dispose is the only correct call: the
        // SemaphoreSlim is small (~50 bytes), and ForgetWipeGate is
        // only fired on destination removal — a manual user action,
        // not a hot path. The dictionary entry IS removed, which is
        // the actual leak we care about (otherwise the dict grows
        // unboundedly across the lifetime of a long-running app).
        _wipeGates.TryRemove(destinationId, out _);
    }

    // =====================================================================
    // Local / External / Network
    // =====================================================================

    private static void EraseSnapshotsLocally(Destination dest, IReadOnlyList<string> snapshotIds, CleanupReport report)
    {
        if (string.IsNullOrWhiteSpace(dest.PathOrSubpath))
        {
            report.Errors.Add("Destination has no folder path.");
            return;
        }
        var snapshotsRoot = Path.Combine(dest.PathOrSubpath, "snapshots");
        if (!Directory.Exists(snapshotsRoot))
        {
            report.Notes.Add($"No snapshots folder at {snapshotsRoot} — nothing to clean up.");
            return;
        }
        foreach (var id in snapshotIds)
        {
            var dir = Path.Combine(snapshotsRoot, id);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
                report.DeletedSnapshotIds.Add(id);
            }
        }

        // Orphaned chunks (data blocks) remain under {root}/chunks/ unless
        // we also clean them up. We don't try to be exhaustive here —
        // running a proper "prune -exhaustive" is expensive and needs a
        // repo. Leaving the chunks is safe: next time a backup runs to
        // the same storage, Duplicacy will garbage-collect them on a
        // scheduled prune.
        report.Notes.Add("Chunks will be garbage-collected on the next scheduled prune for this destination.");
    }

    /// <summary>
    /// Wipe only the artefacts Duplimate / Duplicacy could have put
    /// into the user's local folder. Earlier this method blanket-deleted
    /// the whole folder via <c>Directory.Delete(recursive=true)</c>,
    /// which destroyed any pre-existing user files the user happened to
    /// have stashed alongside the backup repo (the user reported losing
    /// unrelated content this way). The new contract: we only touch
    /// names we know Duplicacy creates — <c>snapshots/</c>,
    /// <c>chunks/</c>, the top-level <c>config</c> file, and the
    /// <c>.duplicacy</c> hidden metadata directory. Everything else the
    /// user keeps in that folder stays put. If the folder ends up empty
    /// after our cleanup we remove the now-empty directory; otherwise
    /// we leave it in place and surface a note.
    /// </summary>
    private static void WipeLocalRoot(Destination dest, CleanupReport report)
    {
        if (string.IsNullOrWhiteSpace(dest.PathOrSubpath))
        {
            report.Errors.Add("Destination has no folder path.");
            return;
        }
        var root = dest.PathOrSubpath;
        if (!Directory.Exists(root))
        {
            report.Notes.Add($"Folder {root} doesn't exist — nothing to clean up.");
            return;
        }

        // Only known-Duplicacy artefacts. Adding to this list is the
        // ONLY safe way to delete more — never extend this to wildcards
        // or "everything in the folder" without explicit user opt-in.
        string[] knownDirs   = { "snapshots", "chunks", ".duplicacy" };
        string[] knownFiles  = { "config" };

        var deletedNames = new List<string>();
        foreach (var name in knownDirs)
        {
            var path = Path.Combine(root, name);
            if (Directory.Exists(path))
            {
                try
                {
                    Directory.Delete(path, recursive: true);
                    deletedNames.Add(name + "/");
                }
                catch (Exception ex)
                {
                    report.Errors.Add($"Couldn't delete {name}/: {ex.Message}");
                }
            }
        }
        foreach (var name in knownFiles)
        {
            var path = Path.Combine(root, name);
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                    deletedNames.Add(name);
                }
                catch (Exception ex)
                {
                    report.Errors.Add($"Couldn't delete {name}: {ex.Message}");
                }
            }
        }

        if (deletedNames.Count == 0)
        {
            report.Notes.Add($"Folder {root} contained no Duplicacy data — nothing to remove.");
            return;
        }

        report.WipedPath = root;

        // Remove the parent directory only if it's now empty AND we
        // actually deleted at least one Duplicacy artefact (otherwise
        // the user pointed at a folder that had nothing of ours and
        // anything left is theirs). EnumerateFileSystemEntries is
        // cheap on the Win32 backing store.
        try
        {
            if (!Directory.EnumerateFileSystemEntries(root).Any())
            {
                Directory.Delete(root, recursive: false);
            }
            else
            {
                report.Notes.Add(
                    $"Left {root} in place — it still contains files that Duplimate didn't put there. " +
                    "Remove it by hand if you don't want it.");
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: the cleanup itself succeeded; we just couldn't
            // tidy the now-empty parent. Surface as a note rather than an
            // error.
            report.Notes.Add($"Couldn't probe/remove {root}: {ex.Message}");
        }
    }

    // =====================================================================
    // Cloud / S3 — delegated to duplicacy
    // =====================================================================

    private async Task EraseSnapshotsViaDuplicacyAsync(
        Backup backup, Destination destination, string storageName,
        IReadOnlyList<string> snapshotIds, CleanupReport report, CancellationToken ct,
        IProgress<string>? progress = null,
        IProgress<int?>? chunkPercent = null)
    {
        // Probe the destination FIRST so we only iterate snapshot ids
        // that still actually exist. Without this, a re-run of Erase
        // against an already-empty destination would still report
        // "Removing snapshot 1 of 2…" for every source path the
        // backup is configured with — duplicacy prune just does
        // nothing under the hood, but the dialog and the
        // DeletedSnapshotIds report come back as if we actually
        // erased something. The user reported: "If I erase a
        // dropbox destination, it shows me the first time it is
        // erasing 2 snapshots. But then if I run Erase again it
        // also shows me it is erasing 2 snapshots! Why doesn't he
        // see there are no more snapshots?"
        //
        // The probe call also tells us how many of the configured
        // ids actually exist remotely so the progress message
        // reflects reality rather than backup.SourcePaths.Count.
        var existingIds = await TryListExistingSnapshotIdsAsync(destination, ct);
        var idsToPrune = backup.SourcePaths
            .Select(sp => (Source: sp, Id: DuplicacyRunner.SnapshotIdFor(backup, sp)))
            .Where(x => existingIds is null || existingIds.Contains(x.Id))
            .ToList();

        if (idsToPrune.Count == 0)
        {
            // Either the probe returned a definitive empty list,
            // OR the user's configured snapshot ids no longer match
            // anything on the storage (renamed backup, foreign
            // shared destination, etc.). Either way, there's
            // nothing for us to do — say so explicitly so the
            // progress dialog doesn't pretend we erased something.
            progress?.Report("Nothing to remove — no snapshots for this backup at this destination.");
            report.Notes.Add("No matching snapshots at destination — nothing to remove.");
            return;
        }

        var total = idsToPrune.Count;
        var done = 0;
        // Hook the runner's LineWritten so we can surface chunk-removal
        // counts ("Deleted N/M chunks") to the progress dialog as
        // duplicacy emits them. Otherwise the dialog would freeze on
        // "Asking Duplicacy to drop chunks…" for the entire prune.
        // Track current snapshot index for the overall-progress
        // weighting math in the LineWritten handler below. Captured
        // by the closure; updated inside the loop.
        int currentSnapshotIndex = 0;
        Action<string>? handler = null;
        if (progress is not null || chunkPercent is not null)
        {
            handler = line =>
            {
                var (msg, pct) = TryFormatPruneProgressLineWithPercent(line);
                if (msg is not null) progress?.Report(msg);
                if (pct is not null && chunkPercent is not null && currentSnapshotIndex > 0)
                {
                    // Ponderate per-snapshot pct into overall pct.
                    // Snapshot i of N → base = (i-1)/N, then add
                    // pct/(100*N) for the fractional progress through
                    // the current snapshot. Cap at 99 so the bar only
                    // hits 100 from the explicit completion report
                    // below — duplicacy may emit "Deleted 99 of 100"
                    // multiple times near the end without it meaning
                    // the whole operation is done.
                    var fraction = ((currentSnapshotIndex - 1) + pct.Value / 100.0) / total;
                    var overall = (int)Math.Clamp(Math.Round(fraction * 99.0), 0, 99);
                    chunkPercent.Report(overall);
                }
            };
            _runner.LineWritten += handler;
        }
        try
        {
            foreach (var (sourcePath, id) in idsToPrune)
            {
                ct.ThrowIfCancellationRequested();
                done++;
                currentSnapshotIndex = done;
                // Anchor the bar at the floor of this snapshot's slice
                // so when a fast snapshot finishes before any "Deleted
                // X of Y" line is emitted, the bar still advances.
                if (chunkPercent is not null)
                {
                    var floor = (int)Math.Floor((done - 1) * 99.0 / total);
                    chunkPercent.Report(Math.Clamp(floor, 0, 99));
                }
                progress?.Report(total > 1
                    ? $"Removing snapshot {done} of {total} for «{backup.Name}»…"
                    : $"Removing snapshot for «{backup.Name}»…");
                try
                {
                    await _runner.PruneSnapshotIdAsync(backup, sourcePath, destination, storageName, id, ct);
                    report.DeletedSnapshotIds.Add(id);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Duplicacy prune failed for {Id}", sourcePath);
                    report.Errors.Add($"Couldn't remove snapshots: {ex.Message}");
                }
            }
            // All snapshots processed cleanly → push the bar the rest
            // of the way home. The earlier 99-cap kept the bar honest
            // about ongoing chunk-delete output; once the loop has
            // exited we're done.
            chunkPercent?.Report(100);
        }
        finally
        {
            if (handler is not null) _runner.LineWritten -= handler;
        }
    }

    /// <summary>
    /// Best-effort enumerate the snapshot ids currently stored at
    /// <paramref name="destination"/> via <see cref="Probe"/>. Returns
    /// null when the probe is unavailable OR the listing failed —
    /// callers should treat null as "I don't know, assume everything
    /// might exist" and fall back to attempting prune for every
    /// configured id (the historical pre-probe behaviour). Returns an
    /// empty set when the destination is reachable and confirmed
    /// empty — callers can short-circuit with a "nothing to remove"
    /// message in that case.
    /// </summary>
    private async Task<HashSet<string>?> TryListExistingSnapshotIdsAsync(
        Destination destination, CancellationToken ct)
    {
        // Both paths share the same error contract: cancellation
        // propagates, any other exception is swallowed and reported
        // as null ("don't know — fall back to blind prune"). The
        // earlier shape only wrapped the Probe path, so a
        // TestSnapshotIdLister throw in tests escaped to the caller
        // and surfaced as a wipe error instead of a clean "no
        // probe info" fallback. Symmetric handling keeps the test
        // hook semantically identical to the production path.
        try
        {
            if (TestSnapshotIdLister is not null)
                return await TestSnapshotIdLister(destination, ct);
            if (Probe is null) return null;
            var ids = await Probe.ListSnapshotIdsAsync(destination, ct);
            return new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Warning(ex, "Couldn't list snapshot ids at {Dest} — falling back to blind prune", destination.Name);
            return null;
        }
    }

    /// <summary>
    /// Recognise duplicacy's prune progress lines and format a tight
    /// status update for the progress dialog. Returns null for lines
    /// that aren't useful surface signal (DEBUG noise, irrelevant
    /// INFO codes). Public so the BackupEditor's PruneToLatestAsync
    /// can reuse the same parser for its own progress dialog.
    /// </summary>
    public static string? TryFormatPruneProgressLineForUi(string line) =>
        TryFormatPruneProgressLine(line);

    private static string? TryFormatPruneProgressLine(string line) =>
        TryFormatPruneProgressLineWithPercent(line).Status;

    /// <summary>
    /// Parse a single line of duplicacy prune stdout, returning
    /// (a human status, an optional percent-of-current-snapshot).
    /// The percent is only set for "Deleted X of Y chunks" lines —
    /// duplicacy emits these one per chunk, so they're the only
    /// data point that maps to a real percentage. Other recognised
    /// lines (fossil marking, removed-snapshot acks) update the
    /// status text but return null for percent, since they don't
    /// carry a total.
    /// </summary>
    private static (string? Status, int? Percent) TryFormatPruneProgressLineWithPercent(string line)
    {
        if (string.IsNullOrEmpty(line)) return (null, null);
        var m = PruneDeletedRx.Match(line);
        if (m.Success)
        {
            var done = int.Parse(m.Groups[1].Value);
            var total = int.Parse(m.Groups[2].Value);
            int? pct = total > 0
                ? (int)Math.Clamp(Math.Round(done * 100.0 / total), 0, 100)
                : null;
            return ($"Deleted {done} of {total} chunks…", pct);
        }
        m = PruneFossilRx.Match(line);
        if (m.Success)
            return ($"Marked {m.Groups[1].Value} chunks as fossils…", null);
        m = PruneRemovedSnapshotRx.Match(line);
        if (m.Success)
            return ($"Removed revision {m.Groups[1].Value}…", null);
        return (null, null);
    }

    private static readonly System.Text.RegularExpressions.Regex PruneDeletedRx = new(
        @"Deleted\s+(\d+)\s+of\s+(\d+)\s+chunks?",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex PruneFossilRx = new(
        @"Marked\s+(\d+)\s+(?:chunks?|fossils?)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex PruneRemovedSnapshotRx = new(
        @"(?:Removed|Deleted)\s+snapshot\s+\S+\s+(?:at\s+)?revision\s+(\d+)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Cloud + S3 wipe: prune ONLY the snapshot ids that Duplimate
    /// knows about (derived from current config's Backup.Name +
    /// SourcePath pairs). We deliberately do NOT touch unknown snapshot
    /// ids — the user might be sharing the same Dropbox folder with
    /// another tool / another machine running duplicacy directly, and
    /// blanket-pruning everything we found would destroy data we
    /// didn't put there.
    ///
    /// After this, our snapshots and the chunks they exclusively
    /// referenced are gone. Foreign snapshots + any chunks they keep
    /// referenced remain. The exhaustive prune at the end is GATED:
    /// it only runs if every remote snapshot id is one we recognise
    /// (no foreign data on the storage), otherwise it could remove
    /// chunks belonging to unrelated snapshots.
    /// </summary>
    private async Task WipeRemoteRootAsync(
        Destination destination, CleanupReport report, CancellationToken ct,
        IProgress<string>? progress = null,
        IProgress<int?>? chunkPercent = null)
    {
        if (Probe is null)
        {
            // Defensive: if the locator forgot to wire the probe, we
            // still want the rest of the call to succeed cleanly.
            _log.Warning("StorageCleaner.Probe is null — skipping remote enumeration");
            report.Errors.Add("Couldn't enumerate remote contents — skipping cloud wipe.");
            return;
        }

        IReadOnlyCollection<string> remoteIds;
        try
        {
            remoteIds = await Probe.ListSnapshotIdsAsync(destination, ct);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Couldn't list snapshots at remote {Name}", destination.Name);
            report.Errors.Add($"Couldn't list remote snapshots: {ex.Message}");
            return;
        }

        // Build the set of snapshot ids the app considers its own. Each
        // (backup, source-path) pair has a deterministic snapshot id
        // (see DuplicacyRunner.SnapshotIdFor). We only prune ids that
        // appear in this set AND on the remote.
        var ownedIds = new System.Collections.Generic.HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var b in _config.Current.Backups)
        {
            foreach (var src in b.SourcePaths)
                ownedIds.Add(DuplicacyRunner.SnapshotIdFor(b, src));
        }

        var toPrune = remoteIds.Where(id => ownedIds.Contains(id)).ToList();
        var foreignIds = remoteIds.Where(id => !ownedIds.Contains(id)).ToList();

        if (foreignIds.Count > 0)
        {
            // Surface what we're leaving alone. The cleanup report goes
            // to the diagnostic log; the user can review what wasn't
            // touched if they're surprised.
            _log.Information(
                "Skipping {Count} unknown snapshot id(s) at {Dest}: {Ids}",
                foreignIds.Count, destination.Name, string.Join(", ", foreignIds));
            report.Notes.Add(
                $"Skipped {foreignIds.Count} unknown snapshot id(s) — they don't belong to any backup Duplimate knows about. " +
                $"Use the provider's web UI if you want to remove them: {string.Join(", ", foreignIds)}");
        }

        var snapshotIds = toPrune;
        if (snapshotIds.Count == 0)
        {
            // Nothing of ours to remove — bail out, but still mark the
            // wipe as successful so the caller knows we finished cleanly.
            report.Notes.Add(remoteIds.Count == 0
                ? "Storage was already empty — nothing to remove."
                : "Storage contains only foreign snapshots — nothing Duplimate put there to remove.");
            report.WipedPath = string.IsNullOrWhiteSpace(destination.PathOrSubpath)
                ? "(remote root)"
                : destination.PathOrSubpath;
            return;
        }

        // We need ANY backup on file to drive duplicacy through this
        // storage — duplicacy commands need a repo dir + storage URL,
        // and we already write per-source repos at first-init. The
        // synthetic backup below has the right Name/SourcePaths so the
        // prune args are well-formed; no real source data is read. The
        // synthetic repo is cleaned up in the finally block — without
        // that, every cloud destination wipe leaves a stub repo dir
        // behind under %LOCALAPPDATA%\Duplimate\repos\.
        var synthetic = new Backup
        {
            // Prefix with a letter so the name doesn't violate the
            // canonical [A-Za-z0-9_-]+ regex in Backup.IsValidName,
            // and so SanitizeName's `Trim('_', '-')` doesn't strip
            // the leading underscore and produce a different string
            // — leaving the repo-dir cleanup at line 444's `finally`
            // unable to find what it created.
            Name = $"wipe{System.Guid.NewGuid():N}".Substring(0, 16),
            SourcePaths = { System.IO.Path.GetTempPath() },
        };
        const string storageName = "default";

        // Hook the runner's LineWritten so chunk-deletion progress
        // surfaces in the dialog while we drive each snapshot id's
        // prune. Otherwise the user sees "Asking Duplicacy to clear…"
        // for the entire wipe even though duplicacy is reporting
        // chunk counts every few seconds.
        // Overall-progress weighting for the destination-wipe path.
        // Snapshots own the first 90% of the bar (split evenly across
        // N snapshots); the exhaustive sweep at the end owns the
        // remaining 10% (or whatever PercentForExhaustiveSweep dials
        // to). User-visible win: the bar climbs smoothly to ~90%
        // through the snapshot loop, then to ~99% through the
        // exhaustive phase, and snaps to 100 only on success
        // completion — no "stuck at 100%" while duplicacy is still
        // working.
        const int PercentForSnapshotPhase = 90;
        int currentSnapshotIndex = 0;
        bool inExhaustivePhase = false;
        Action<string>? progressHandler = null;
        if (progress is not null || chunkPercent is not null)
        {
            progressHandler = line =>
            {
                var (msg, pct) = TryFormatPruneProgressLineWithPercent(line);
                if (msg is not null) progress?.Report(msg);
                if (pct is not null && chunkPercent is not null)
                {
                    double overall;
                    if (inExhaustivePhase)
                    {
                        // 90 → 99 as exhaustive's "Deleted X of Y"
                        // climbs from 0% to 100%.
                        overall = PercentForSnapshotPhase
                                + (100 - PercentForSnapshotPhase) * pct.Value / 100.0;
                    }
                    else if (currentSnapshotIndex > 0)
                    {
                        // 0 → 90 across N snapshots.
                        var fraction = ((currentSnapshotIndex - 1) + pct.Value / 100.0) / snapshotIds.Count;
                        overall = fraction * PercentForSnapshotPhase;
                    }
                    else return;
                    chunkPercent.Report((int)Math.Clamp(Math.Round(overall), 0, 99));
                }
            };
            _runner.LineWritten += progressHandler;
        }
        try
        {
            var doneCount = 0;
            foreach (var id in snapshotIds)
            {
                ct.ThrowIfCancellationRequested();
                doneCount++;
                currentSnapshotIndex = doneCount;
                // Anchor the bar at this snapshot's floor so a fast
                // snapshot still advances the bar even if duplicacy
                // emits no chunk lines for it.
                if (chunkPercent is not null)
                {
                    var floor = (int)Math.Floor((doneCount - 1) * (double)PercentForSnapshotPhase / snapshotIds.Count);
                    chunkPercent.Report(Math.Clamp(floor, 0, 99));
                }
                progress?.Report(snapshotIds.Count > 1
                    ? $"Removing snapshot {doneCount} of {snapshotIds.Count}: «{id}»…"
                    : $"Removing snapshot «{id}»…");
                try
                {
                    await _runner.PruneSnapshotIdAsync(synthetic, synthetic.SourcePaths[0], destination, storageName, id, ct);
                    report.DeletedSnapshotIds.Add(id);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Duplicacy prune failed for snapshot id {Id} on {Dest}", id, destination.Name);
                    report.Errors.Add($"Couldn't remove snapshot «{id}»: {ex.Message}");
                }
            }

            // Exhaustive sweep — picks up any chunks that weren't tied to
            // a snapshot (e.g. partial uploads from interrupted runs).
            // Anchor the bar at 90% as we enter this phase so the user
            // sees the snapshot-phase complete cleanly. The handler
            // will then push 90 → 99 as exhaustive emits its own
            // "Deleted X of Y" chunk lines.
            inExhaustivePhase = true;
            chunkPercent?.Report(PercentForSnapshotPhase);
            progress?.Report("Final pass: cleaning up any leftover chunks…");
            try
            {
                await _runner.PruneExhaustiveAsync(synthetic, synthetic.SourcePaths[0], destination, storageName, ct);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Exhaustive prune failed on {Dest}", destination.Name);
                report.Errors.Add($"Final chunk sweep failed: {ex.Message}");
            }

            report.WipedPath = string.IsNullOrWhiteSpace(destination.PathOrSubpath)
                ? "(remote root)"
                : destination.PathOrSubpath;
            // Wipe finished — push the bar the last percent home.
            chunkPercent?.Report(100);
            // Document the residue. Duplicacy's prune deletes chunks +
            // snapshot manifests but does NOT remove the storage's
            // `config` file, the 256 empty `chunks/<XX>/` hash-bucket
            // dirs, the empty per-snapshot-id dirs, or the storage
            // root folder itself — by design, since duplicacy treats
            // a storage URL as a long-lived resource ready to receive
            // a new backup. We can't clean that up from inside
            // Duplimate because our OAuth path issues refresh
            // tokens via duplicacy.com's helper, not access tokens
            // (Bearer auth against api.dropboxapi.com would 401).
            // Surfacing this in the cleanup report's Notes so the
            // activity-log transcript explains what the user is
            // seeing on Dropbox, and the success-case dialog can
            // quote it verbatim. Errors-free path only — failure
            // paths already surface their own diagnostic notes.
            if (report.Errors.Count == 0)
            {
                report.Notes.Add(
                    "A small Duplicacy `config` file (~1 KB) and an empty directory skeleton "
                    + "(`chunks/<XX>/` hash buckets + empty `snapshots/<id>/` dirs) remain at "
                    + $"`{report.WipedPath}` on the remote. Duplicacy's `prune` doesn't remove "
                    + "the storage's root or its config (the storage is designed to be re-used). "
                    + "Delete the folder via the provider's web UI if you want a fully clean state.");
            }
        }
        finally
        {
            if (progressHandler is not null)
                _runner.LineWritten -= progressHandler;
            // Clean up the synthetic repo dir so we don't leave junk
            // behind. Best-effort: a locked file should never block the
            // wipe report from coming back to the user.
            try
            {
                var repoRoot = AppPaths.RepoDirForBackup(synthetic.Name);
                if (System.IO.Directory.Exists(repoRoot))
                    System.IO.Directory.Delete(repoRoot, recursive: true);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Couldn't delete synthetic repo dir for {Name}", synthetic.Name);
            }
        }
    }
}

/// <summary>
/// Summary of what a cleanup attempt did. Reported to the diagnostic
/// log; not surfaced to the user as a notification — by user direction
/// the app should always finish the job, not delegate to manual cleanup.
/// </summary>
public sealed class CleanupReport
{
    public string DestinationName { get; }
    public List<string> DeletedSnapshotIds { get; } = new();
    public string? WipedPath { get; set; }
    public List<string> Notes { get; } = new();
    public List<string> Errors { get; } = new();

    public CleanupReport(string destinationName) => DestinationName = destinationName;

    public string Summarize()
    {
        var sb = new StringBuilder();
        sb.Append("At «").Append(DestinationName).Append("»: ");
        if (WipedPath is not null)
            sb.Append("wiped ").Append(WipedPath).Append(". ");
        if (DeletedSnapshotIds.Count > 0)
            sb.Append("removed snapshot chains: ").Append(string.Join(", ", DeletedSnapshotIds)).Append(". ");
        foreach (var n in Notes) sb.Append(n).Append(' ');
        foreach (var e in Errors) sb.Append("Error: ").Append(e).Append(' ');
        return sb.ToString().TrimEnd();
    }
}
