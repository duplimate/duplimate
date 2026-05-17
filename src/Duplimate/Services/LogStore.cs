using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Duplimate.Models;

namespace Duplimate.Services;

/// <summary>
/// Persists per-backup RunRecords next to their log files so the dashboard can show
/// recent history without parsing 100MB of log text. Also handles retention (keep
/// last N runs per backup, matches legacy "logfilecount: 30").
/// </summary>
public sealed class LogStore
{
    public int RetainLastNRuns { get; init; } = 30;

    /// <summary>
    /// Fired (on whatever thread called <see cref="AppendRun"/>) right
    /// after a record lands in the per-backup history.json. The
    /// LogsView model subscribes so its RUN dropdown refreshes the
    /// instant a Restore / Test-restore / Backup run finishes —
    /// before this hook the user reported having to manually pick a
    /// different backup and back to force the list to update.
    /// </summary>
    public event Action<RunRecord>? RunAppended;

    /// <summary>
    /// Single gate for all history-file reads and writes. Without it,
    /// <see cref="AppendRun"/> (which does load → append → atomic-replace)
    /// can race with <see cref="LoadHistory"/> / <see cref="LoadHistoryByBackupId"/>
    /// on the same JSON file — the read sees the file mid-replace and
    /// throws IOException (caught silently, the entry is dropped). Two
    /// concurrent AppendRuns can also clobber each other's writes if
    /// they target the same backup-name directory (force-finalise from
    /// the Stop-watchdog Task.Run + the natural finally is the
    /// real-world hot path). Lock granularity is process-wide because
    /// (a) history writes are bounded in time (~ms) and (b) the writer
    /// already does atomic File.Replace; the lock just serialises
    /// access to the shared JsonSerializer + file handles.
    /// </summary>
    private static readonly object _gate = new();

    /// <summary>
    /// Per-backup-name history cache. Populated lazily by
    /// <see cref="LoadHistory"/>; invalidated by <see cref="AppendRun"/>.
    /// Without this cache the LogsView re-parses every history.json on
    /// disk on every selected-backup change AND every RunEnded fanout —
    /// for 30 backups × 30 runs each that's ~1MB JSON / click. Cache
    /// keyed by backup NAME (not id) because that's what the file
    /// layout is keyed on; LoadHistoryByBackupId uses a separate union
    /// across folders and is not cached (rename-orphan stitching is
    /// already a best-effort cold-path).
    ///
    /// Instance-scoped (NOT static): tests that construct an isolated
    /// <c>new LogStore { … }</c> need their own cache so one fixture's
    /// writes don't bleed into another's reads. Production has a
    /// single LogStore instance via ServiceLocator so the user-visible
    /// behaviour is identical.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<RunRecord>> _cache = new();

    public string GetHistoryFile(string backupName)
        => Path.Combine(AppPaths.LogDirForBackup(backupName), "history.json");

    public List<RunRecord> LoadHistory(string backupName)
    {
        if (_cache.TryGetValue(backupName, out var cached))
        {
            // Return a copy so callers can't mutate the cached list
            // (defensive — most callers ToList() anyway).
            return new List<RunRecord>(cached);
        }
        var file = GetHistoryFile(backupName);
        if (!File.Exists(file)) return new();
        lock (_gate)
        {
            try
            {
                var text = File.ReadAllText(file);
                var loaded = JsonSerializer.Deserialize<List<RunRecord>>(text, JsonOpts) ?? new();
                _cache[backupName] = loaded;
                return new List<RunRecord>(loaded);
            }
            catch { return new(); }
        }
    }

    /// <summary>
    /// Load every run record across every per-backup history file
    /// whose <c>BackupId</c> matches <paramref name="backupId"/>.
    /// History files are stored in folders keyed by Backup.Name, so a
    /// rename creates a new file and orphans the old one — this method
    /// stitches them back together so the Logs view shows the complete
    /// run timeline regardless of name changes.
    /// </summary>
    public List<RunRecord> LoadHistoryByBackupId(string backupId)
    {
        if (string.IsNullOrEmpty(backupId)) return new();
        var root = AppPaths.DuplicacyLogsRoot;
        if (!Directory.Exists(root)) return new();

        var merged = new List<RunRecord>();
        lock (_gate)
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var file = Path.Combine(dir, "history.json");
                if (!File.Exists(file)) continue;
                // Try cache first by directory name (==backup name).
                var name = Path.GetFileName(dir);
                if (!string.IsNullOrEmpty(name) && _cache.TryGetValue(name, out var cached))
                {
                    foreach (var r in cached)
                        if (string.Equals(r.BackupId, backupId, StringComparison.Ordinal))
                            merged.Add(r);
                    continue;
                }
                try
                {
                    var text = File.ReadAllText(file);
                    var entries = JsonSerializer.Deserialize<List<RunRecord>>(text, JsonOpts) ?? new();
                    if (!string.IsNullOrEmpty(name)) _cache[name] = entries;
                    foreach (var r in entries)
                    {
                        if (string.Equals(r.BackupId, backupId, StringComparison.Ordinal))
                            merged.Add(r);
                    }
                }
                catch { /* skip unreadable history file — best-effort union */ }
            }
        }
        return merged.OrderByDescending(r => r.StartedUtc).ToList();
    }

    public void AppendRun(RunRecord record)
    {
        // Track the post-append list outside the lock so the orphan-log
        // prune (filesystem-level cleanup of stale .log files) can run
        // OUTSIDE the gate. Holding the gate across the prune-loop
        // serialised every reader behind a directory enumeration;
        // separating them keeps the gate confined to JSON file I/O
        // and lets concurrent reads (LoadHistory) proceed once the
        // history.json has been replaced.
        List<RunRecord>? finalList = null;
        string? historyFile = null;
        lock (_gate)
        {
            historyFile = GetHistoryFile(record.BackupName);
            var list = new List<RunRecord>();
            if (File.Exists(historyFile))
            {
                try
                {
                    var text = File.ReadAllText(historyFile);
                    list = JsonSerializer.Deserialize<List<RunRecord>>(text, JsonOpts) ?? new();
                }
                catch { /* corrupted file — start fresh */ }
            }
            list.Add(record);

            // Retention: keep last N by StartedUtc desc.
            list = list.OrderByDescending(r => r.StartedUtc).Take(RetainLastNRuns).ToList();

            Directory.CreateDirectory(Path.GetDirectoryName(historyFile)!);
            var tmp = historyFile + ".tmp";
            var json = JsonSerializer.Serialize(list, JsonOpts);
            File.WriteAllText(tmp, json);
            if (File.Exists(historyFile)) File.Replace(tmp, historyFile, destinationBackupFileName: null);
            else File.Move(tmp, historyFile);

            // Update cache from a re-deserialised copy of the JSON we
            // just wrote — NOT the in-memory `list`, which holds the
            // caller's live RunRecord reference. Sharing that reference
            // through the cache caused a hard-to-spot bug: the
            // orchestrator passes its synthetic "Running" record by
            // reference into AppendRun (same instance LogsViewModel had
            // pinned as SelectedRun while it was the in-flight entry),
            // mutates Status from Running→Success in-place, and then
            // when LogsViewModel reloaded History the matching cache
            // entry was reference-equal to the existing SelectedRun.
            // CommunityToolkit's [ObservableProperty] setter skipped
            // PropertyChanged on reference-equal assignment, so
            // OnSelectedRunChanged never fired and the terminal pane
            // kept showing the previous run's log file.
            // Re-deserialising forces fresh RunRecord instances; the
            // cache is now a snapshot of disk, never a live reference.
            // The user reported: "I just ran a backup and went to Logs
            // & runs: it showed the result of the previous run even
            // though the last run was selected in the dropdown!"
            // Re-deserialise on a best-effort basis. If the round-trip
            // somehow throws (corrupted JSON in the in-memory `json`
            // string is essentially impossible since we just produced
            // it, but a JsonSerializer change in a future runtime
            // could introduce a contract mismatch), keep the previous
            // cache entry rather than wiping it to an empty list. An
            // empty cache silently breaks the LogsView's "select the
            // newly-appended run" UX until the next process restart.
            try
            {
                var rebuilt = JsonSerializer.Deserialize<List<RunRecord>>(json, JsonOpts);
                if (rebuilt is not null) _cache[record.BackupName] = rebuilt;
                // If the deserialiser returned null (shouldn't happen
                // for non-null input that we just serialised), we
                // intentionally leave the prior cache entry alone.
            }
            catch (Exception ex)
            {
                AppLogger.For<LogStore>().Warning(ex,
                    "Failed to re-deserialise just-written history JSON for {Backup} — preserving prior cache",
                    record.BackupName);
            }
            finalList = list;
        }

        // Orphan-log prune outside the gate — best-effort filesystem
        // hygiene that doesn't need to block readers.
        if (finalList is not null)
        {
            try
            {
                // Include both the run-level LogPath AND every CellOutcome.LogPath:
                // multi-source / multi-destination runs produce one .log per cell,
                // and earlier this prune only saw the run-level path (which is the
                // first cell's promoted path), so every secondary cell's log file
                // got deleted on the next AppendRun. The Logs view's Selected Run
                // pane then rendered "log file not found" for every cell except
                // the first.
                var referenced = new HashSet<string>(
                    finalList.SelectMany(r => new[] { r.LogPath }
                        .Concat(r.Cells.Select(c => c.LogPath))),
                    StringComparer.OrdinalIgnoreCase);
                var dir = AppPaths.LogDirForBackup(record.BackupName);
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*.log"))
                        if (!referenced.Contains(f)) { try { File.Delete(f); } catch { } }
                }
            }
            catch { /* swallow — orphan prune is housekeeping, never fatal */ }
        }

        // Notify subscribers AFTER the gate releases so a handler
        // that reads back history.json doesn't deadlock against the
        // write we just finished. Best-effort: a thrown handler
        // mustn't fail the persist.
        try { RunAppended?.Invoke(record); }
        catch { /* listener bug shouldn't kill the persistence path */ }
    }

    public RunRecord? MostRecent(string backupName)
        => LoadHistory(backupName).OrderByDescending(r => r.StartedUtc).FirstOrDefault();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
