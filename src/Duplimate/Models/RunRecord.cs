using System;
using System.Collections.Generic;

namespace Duplimate.Models;

/// <summary>
/// What flavour of run this record describes. Defaults to
/// <see cref="Backup"/> so existing JSON history files (that pre-date
/// this field) deserialise unchanged. <see cref="Restore"/> and
/// <see cref="TestRestore"/> are surfaced in the Logs &amp; runs RUN
/// dropdown alongside backup runs — the user explicitly asked:
/// "Restore logs should show up in Activity &amp; Logs … There should
/// be a dedicated entry in RUN."
/// </summary>
public enum RunKind
{
    Backup,
    Restore,
    TestRestore,
    /// <summary>
    /// "Erase backup at destination" or "Delete destination entirely"
    /// — covers both flavours of cleanup that drive
    /// <see cref="Duplimate.Services.StorageCleaner"/>. For
    /// destination-wide wipes the parent <see cref="RunRecord"/>'s
    /// <c>BackupName</c> is the literal <c>[Destinations]</c>
    /// sentinel; for per-backup erases it's the real backup name.
    /// </summary>
    Erase,
}

/// <summary>
/// A single backup / restore / test-restore run outcome, serialized to disk
/// next to the log file. Lets the dashboard render recent history without
/// parsing the full log.
/// </summary>
public sealed class RunRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string BackupId { get; set; } = "";
    public string BackupName { get; set; } = "";

    /// <summary>
    /// What flavour of run this is. Defaults to <see cref="RunKind.Backup"/>
    /// so existing per-backup history.json files (which never wrote a
    /// Kind field) round-trip cleanly.
    /// </summary>
    public RunKind Kind { get; set; } = RunKind.Backup;

    public DateTime StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }

    public BackupRunStatus Status { get; set; }

    /// <summary>Short human-readable summary ("1,234 new, 56 MB uploaded in 2m 11s").</summary>
    public string Summary { get; set; } = "";

    /// <summary>Absolute path to the stored log file.</summary>
    public string LogPath { get; set; } = "";

    /// <summary>For the "last revision" views in the UI.</summary>
    public int? RevisionNumber { get; set; }

    public long BytesUploaded { get; set; }
    public int FilesNew { get; set; }
    public int FilesModified { get; set; }
    public int FilesRemoved { get; set; }

    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// One outcome per (source × destination) cell in the run fan-out.
    /// A backup with two sources and two destinations produces four
    /// entries. Populated incrementally as cells complete, so the
    /// dashboard tile can show partial progress while long runs are
    /// still live.
    /// </summary>
    public List<CellOutcome> Cells { get; set; } = new();
}

/// <summary>
/// Outcome of a single (source × destination) cell within a
/// multi-source / multi-target run.
/// </summary>
public sealed class CellOutcome
{
    /// <summary>Absolute source path this cell covered.</summary>
    public string SourcePath { get; set; } = "";

    /// <summary>Human-readable destination name (e.g. "Dropbox — personal").</summary>
    public string DestinationName { get; set; } = "";

    /// <summary>Which Duplicacy storage-name was used (per-target alias).</summary>
    public string StorageName { get; set; } = "default";

    public BackupRunStatus Status { get; set; }

    /// <summary>Short one-line summary for this cell; surfaced in notifications and the tile.</summary>
    public string Summary { get; set; } = "";

    /// <summary>Number of retry attempts this cell burned before settling on Status.</summary>
    public int AttemptsMade { get; set; }

    /// <summary>Last error message captured from duplicacy's stderr, if the cell ultimately failed.</summary>
    public string? LastError { get; set; }

    /// <summary>How long this cell took, start to last attempt's end.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>Path to the per-cell duplicacy log file, when one was
    /// created. Set to the LogPath of the underlying RunRecord
    /// produced by DuplicacyRunner so that LogsView can offer it as
    /// the Selected Run's history entry — earlier this stayed empty
    /// for cells that failed during init, leaving "Selected run"
    /// blank for the user.</summary>
    public string LogPath { get; set; } = "";
}
