using System;
using System.Collections.Generic;

namespace Duplimate.Models;

/// <summary>
/// A backup = one or more source folders + one or more destinations + policy.
///
/// Multi-source semantics: each entry in <see cref="SourcePaths"/> is an
/// independent Duplicacy repository living under its own subdirectory of
/// <c>%LOCALAPPDATA%\Duplimate\repos\&lt;backup-name&gt;\&lt;source-slug&gt;\</c>.
/// The orchestrator loops over sources × destinations, so a backup named
/// "computer" with sources <c>[C:\Users\me\Documents, C:\Users\me\Pictures]</c>
/// and one Dropbox destination produces two Duplicacy repos but one
/// scheduled run. Per-source retention applies correctly because
/// Duplicacy owns the repo root.
/// </summary>
/// <summary>
/// Which UI mode the user last opened this backup's editor in. Used by
/// the unified BackupCreationWindow to re-open in the same mode the
/// user committed in last time, so Edit doesn't toss them between Easy
/// and Advanced mid-task. Easy = the 3-step wizard; Advanced = the
/// tabbed editor with all the knobs. Default Advanced for backups
/// created before this field existed (their schedule may include
/// Advanced-only settings the Easy mode wouldn't reproduce faithfully).
/// </summary>
public enum BackupEditMode
{
    Advanced = 0,
    Easy = 1,
}

public sealed class Backup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Last edit mode used. See <see cref="BackupEditMode"/>.</summary>
    public BackupEditMode LastEditMode { get; set; } = BackupEditMode.Advanced;

    /// <summary>
    /// User-facing display name. Free-form text — the user can set
    /// this to anything (including spaces and punctuation) up to
    /// <see cref="MaxNameLength"/> chars. Filesystem-embedded usages
    /// (repo / log directory names, scheduled-task names) sanitise
    /// independently via <c>AppPaths.Sanitize</c>; the Duplicacy
    /// snapshot id is derived from <see cref="SnapshotIdSeed"/>
    /// (locked at first save) so a rename here doesn't fork the
    /// snapshot chain. The user reported that the prior regex-locked
    /// shape was replacing spaces with underscores on every save
    /// when they renamed; that normalisation was wrong for a
    /// purely-display field.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Hard cap on <see cref="Name"/> length. 255 matches the typical
    /// filesystem max-segment length on Windows / macOS / Linux —
    /// even though we sanitise into a derived path, keeping the user
    /// input under the same cap means the on-screen text never
    /// exceeds what could survive a round-trip into a directory name.
    /// </summary>
    public const int MaxNameLength = 255;

    /// <summary>
    /// Immutable seed for the Duplicacy snapshot id. Set ONCE at
    /// backup creation from the initial name (sanitised) and never
    /// updated thereafter, so a rename of <see cref="Name"/> doesn't
    /// silently fork a new snapshot chain on the storage and orphan
    /// every prior revision.
    /// <para>
    /// Empty string is the legacy/migration value: backups created
    /// before this field existed have no seed, and
    /// <c>DuplicacyRunner.SnapshotIdFor</c> falls back to the current
    /// Name for those — preserving their existing chain. The first
    /// edit of a legacy backup populates this field from the
    /// then-current Name so subsequent renames are safe.
    /// </para>
    /// </summary>
    public string SnapshotIdSeed { get; set; } = "";

    /// <summary>
    /// Returns true iff <paramref name="name"/> is acceptable as a
    /// display Name: non-empty after trimming and within
    /// <see cref="MaxNameLength"/> chars. The legacy regex
    /// (<c>^[A-Za-z0-9_-]+$</c>) was relaxed when the Name became
    /// purely-display — filesystem and snapshot-id consumers do their
    /// own sanitisation downstream.
    /// </summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= MaxNameLength;

    /// <summary>
    /// Coerce an arbitrary string into a valid Backup.Name: whitespace
    /// becomes '_', non-letter / non-digit / non-(_-) characters are
    /// dropped, leading/trailing '_' or '-' trimmed, empty result falls
    /// back to "my-backup". Idempotent for already-valid names.
    /// Use this anywhere <see cref="Name"/> is set from outside the
    /// regex-enforcing UI editor.
    /// </summary>
    public static string SanitizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "my-backup";
        var chars = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw.Trim())
        {
            if (char.IsLetterOrDigit(ch)) chars.Append(ch);
            else if (ch == '-' || ch == '_') chars.Append(ch);
            else if (char.IsWhiteSpace(ch)) chars.Append('_');
            // anything else is dropped silently
        }
        var clean = chars.ToString().Trim('_', '-');
        return clean.Length == 0 ? "my-backup" : clean;
    }

    /// <summary>
    /// Absolute paths to back up. At least one is required; the editor UI
    /// enforces that. Order matters only cosmetically (the list displays
    /// and iterates in this order). Each path becomes an independent
    /// Duplicacy snapshot id <c>&lt;backup-name&gt;_&lt;source-slug&gt;</c>.
    /// </summary>
    public List<string> SourcePaths { get; set; } = new();

    public List<BackupTarget> Targets { get; set; } = new();

    public string FiltersText { get; set; } = "";

    // ---- common options (surfaced up front in the UI) ----

    /// <summary>
    /// 0 means "auto" — the runner picks a good value per destination kind
    /// (1 for Local / External / Network, 4 for cloud/S3). Any non-zero
    /// value is treated as a user-chosen global override that applies to
    /// every target in the fan-out. Per-target granularity still lives on
    /// <see cref="BackupTarget.ThreadsOverride"/>.
    /// </summary>
    public int Threads { get; set; } = 0;

    /// <summary>Volume Shadow Copy — generally on for Windows system drives.</summary>
    public bool UseVss { get; set; } = true;

    /// <summary>
    /// Keep policy string in Duplicacy's native format, e.g. "0:700 30:365 7:90 1:14".
    /// Syntax: "interval:age" pairs. 0 = exhaustive. See duplicacy docs.
    /// </summary>
    public string KeepPolicy { get; set; } = "0:365 30:90 7:30 1:7";

    /// <summary>Run a prune pass after each backup. Matches duplicacy-util's -a behavior.</summary>
    public bool PruneAfterBackup { get; set; } = true;

    /// <summary>Run a check pass after each backup. Matches duplicacy-util's -a behavior.</summary>
    public bool CheckAfterBackup { get; set; } = true;

    /// <summary>Stop if the network becomes metered mid-run (watchdog).</summary>
    public bool AbortOnMeteredNetwork { get; set; } = true;

    // ---- advanced options (collapsed by default in the UI) ----

    /// <summary>Average chunk size in bytes. Default matches Duplicacy default.</summary>
    public int? ChunkSize { get; set; }

    /// <summary>Max chunk size in bytes.</summary>
    public int? MaxChunkSize { get; set; }

    /// <summary>Min chunk size in bytes.</summary>
    public int? MinChunkSize { get; set; }

    /// <summary>Bandwidth cap in KB/s (0 = unlimited). Surfaces as -limit-rate.</summary>
    public int BandwidthLimitKBps { get; set; } = 0;

    /// <summary>Extra CLI args appended verbatim. Escape hatch for power users.</summary>
    public string ExtraBackupArgs { get; set; } = "";

    // ---- schedule ----

    public BackupSchedule Schedule { get; set; } = new();

    // ---- monitoring ----

    /// <summary>Healthchecks.io check slug/UUID assigned to this backup. Null if none.</summary>
    public string? HealthcheckId { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastRunStartUtc { get; set; }
    public DateTime? LastRunEndUtc { get; set; }
    public BackupRunStatus LastRunStatus { get; set; } = BackupRunStatus.NeverRun;
    public string? LastRunSummary { get; set; }

    /// <summary>
    /// Per-source bytes-on-disk reported by Duplicacy after the last
    /// successful run (the BACKUP_STATS "Files: N total, M bytes" line),
    /// keyed by source path. Surfaced inline next to each source on the
    /// BackupCard so the user can see at a glance which paths are big.
    /// Cleared when a source is removed from <see cref="SourcePaths"/>;
    /// stale entries for paths the user removed don't render anywhere.
    /// </summary>
    public Dictionary<string, long> SourceLastSizeBytes { get; set; } = new();

    // ---- verify (test-restore drill) ----

    /// <summary>
    /// When the last "Test restore" verify drill ran. Null = never
    /// verified. The Dashboard surfaces a warning banner when this is
    /// older than 30 days for any backup, nudging the user to run a
    /// drill before they need to actually restore.
    /// </summary>
    public DateTime? LastVerifyUtc { get; set; }

    /// <summary>
    /// True if the last drill restored every sampled file successfully
    /// AND every byte-comparable file matched its source. Null = never
    /// verified. False = drill detected a problem; the user should
    /// re-run and inspect <see cref="LastVerifySummary"/>.
    /// </summary>
    public bool? LastVerifyPass { get; set; }

    /// <summary>
    /// One-line user-facing summary of the last drill outcome —
    /// "Restored 10 of 10 files, all byte-identical" or
    /// "2 of 10 files failed: …". Surfaced in the Dashboard tile.
    /// </summary>
    public string? LastVerifySummary { get; set; }
}

public sealed class BackupTarget
{
    /// <summary>FK into AppConfig.Destinations by Destination.Id.</summary>
    public string DestinationId { get; set; } = "";

    /// <summary>
    /// Duplicacy storage name as registered during init (default "default").
    /// When a backup has multiple targets, each needs a distinct name.
    /// Matches the "name" field in legacy yaml storage[] array.
    /// </summary>
    public string StorageName { get; set; } = "default";

    /// <summary>Per-target thread override. Null = inherit Backup.Threads.</summary>
    public int? ThreadsOverride { get; set; }
}

public enum BackupRunStatus
{
    NeverRun,
    Running,
    Success,
    Skipped,
    Warning,
    Failed,
}
