using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Duplimate.Models;

namespace Duplimate.Services;

/// <summary>
/// Process-wide registry of in-flight non-backup runs (Restore +
/// Test-restore). Backups have their own equivalent inside
/// <see cref="BackupOrchestrator"/> (the <c>_runningRuns</c>
/// dictionary surfaced via <see cref="BackupOrchestrator.TryGetRunningRecord"/>);
/// this service is the symmetrical home for the other two
/// <see cref="RunKind"/>s so the LogsView can synthesize a "Running"
/// entry for them in the RUN dropdown while they're in flight.
///
/// Without this, a test-restore drill or a restore wizard run that
/// hadn't yet persisted a final RunRecord was invisible in the RUN
/// dropdown — the user only saw it via the global Live tail. The
/// user reported: "Doing a test restore shows in the Live view
/// instead of as a Running test restore run (in RUN)."
/// </summary>
public sealed class RunActivityTracker
{
    private readonly ConcurrentDictionary<string, RunRecord> _byId = new();

    /// <summary>Fires whenever an in-flight record is registered or
    /// unregistered. LogsViewModel subscribes so the RUN dropdown
    /// re-synthesizes its row list (the synthetic Running entry
    /// appears / disappears).</summary>
    public event Action? RunningChanged;

    /// <summary>
    /// Mark a run as in flight. The caller MUST eventually call
    /// <see cref="Unregister"/> with the same record id; the
    /// registry is process-wide and a leak would leave a phantom
    /// "Running" entry forever.
    /// </summary>
    public void Register(RunRecord record)
    {
        if (string.IsNullOrEmpty(record.Id)) return;
        _byId[record.Id] = record;
        RunningChanged?.Invoke();
    }

    public void Unregister(string runId)
    {
        if (string.IsNullOrEmpty(runId)) return;
        _byId.TryRemove(runId, out _);
        RunningChanged?.Invoke();
    }

    /// <summary>Snapshot of every in-flight run currently associated
    /// with the given backup id. Most calls return 0 or 1 entries —
    /// a user can occasionally have a Test restore + Restore wizard
    /// flow running back-to-back with overlap.</summary>
    public IReadOnlyList<RunRecord> GetRunningForBackup(string backupId)
    {
        if (string.IsNullOrEmpty(backupId)) return Array.Empty<RunRecord>();
        return _byId.Values
            .Where(r => string.Equals(r.BackupId, backupId, StringComparison.Ordinal))
            .OrderByDescending(r => r.StartedUtc)
            .ToArray();
    }
}
