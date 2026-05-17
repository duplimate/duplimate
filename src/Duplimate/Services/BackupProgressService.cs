using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Serilog;

namespace Duplimate.Services;

/// <summary>
/// Per-backup progress tracking driven by Duplicacy stdout. The
/// orchestrator calls <see cref="ReportCellStarted"/> + <see cref="ReportLine"/> +
/// <see cref="ReportCellEnded"/> as a backup walks its source × target
/// matrix; subscribers (the standardised BackupCard view-model) listen
/// to <see cref="Updated"/> and read <see cref="Snapshot"/> at any time.
///
/// Why a service-level tracker instead of inline VM parsing:
///   • Multiple views (Backups list, future widgets) need the same data.
///   • The orchestrator owns the canonical "which source/target is in
///     flight right now" knowledge, so it's the only thing in a position
///     to label a stdout line with the correct source path.
///   • Keeping parsing in one place means the next time Duplicacy's
///     stdout format shifts, we update one regex.
///
/// Threading: <see cref="ReportLine"/> is called from the runner's
/// stdout pump (background thread); <see cref="Updated"/> fires on the
/// same thread. Subscribers must marshal to the UI thread themselves.
/// </summary>
public sealed class BackupProgressService
{
    private static ILogger _log => AppLogger.For<BackupProgressService>();

    /// <summary>Per-backup current state. Snapshot is a defensive copy.</summary>
    private readonly ConcurrentDictionary<string, BackupProgress> _state = new();

    /// <summary>Fires after any per-line update to a backup's progress.
    /// Argument is the backup id.</summary>
    public event Action<string>? Updated;

    /// <summary>
    /// Read the current snapshot for a backup, or null if it isn't running
    /// (or isn't tracked yet). Returned object is detached — mutating it
    /// won't affect the tracker.
    /// </summary>
    public BackupProgress? Snapshot(string backupId)
    {
        if (!_state.TryGetValue(backupId, out var live)) return null;
        // Clone iterates Sources / Destinations / SourceWeightBytes
        // dictionaries — those are mutated under lock(p) by every
        // ReportLine / ReportCellStarted / RegisterRunCells call from
        // runner threads. Cloning without the lock raced and threw
        // "Collection was modified" InvalidOperationException on the
        // UI thread when a card refreshed mid-run. Take the same
        // lock the writers do so the clone observes a consistent
        // dictionary snapshot.
        lock (live) return live.Clone();
    }

    /// <summary>
    /// Pre-populates the full (source × destination) cell matrix at
    /// run start so Overall reflects the entire planned workload from
    /// t=0 — not just the cells that have already started.
    ///
    /// Without this, sequential cells produced a misleading Overall:
    /// for a 2-source × 2-destination run the first cell at 21% would
    /// show Overall=21% (only one cell exists in the dictionary), even
    /// though three more cells haven't started yet. Pre-registering
    /// gives the average a denominator of 4 from the beginning, so
    /// the same case correctly shows Overall ≈ 5%.
    ///
    /// Per-source progress is then "fraction of destinations done",
    /// which matches the user's mental model of "this source has been
    /// pushed to 1 of 2 destinations → 50%". An in-flight cell at N%
    /// counts proportionally, so a source mid-upload to its first
    /// destination shows N% / D destinations.
    /// </summary>
    public void RegisterRunCells(
        string backupId,
        IEnumerable<string> sourcePaths,
        IEnumerable<string> destinationNames,
        IReadOnlyDictionary<string, long>? sourceSizeBytes = null)
    {
        var p = _state.GetOrAdd(backupId, _ => new BackupProgress(backupId));
        lock (p)
        {
            // Bump PurgeGeneration so any pending purge from a prior run
            // doesn't evict the freshly-populated state.
            p.PurgeGeneration++;
            // Materialise once so we can iterate twice + capture sizes.
            var dests = destinationNames.ToList();
            var sources = sourcePaths.ToList();

            // Seed all destination rows up front so the per-destination
            // pill bars show 0% from t=0 instead of appearing as cells
            // start.
            foreach (var d in dests)
            {
                if (!p.Destinations.ContainsKey(d))
                    p.Destinations[d] = new DestinationProgress(d);
            }
            // Seed every (source × dest) cell at 0%, IsRunning=false.
            // ReportCellStarted will flip IsRunning + set Detail per
            // cell as the orchestrator enters it; ReportLine updates
            // the in-flight cell's percent.
            foreach (var src in sources)
            {
                if (!p.Sources.TryGetValue(src, out var sp))
                {
                    sp = new SourceProgress(src);
                    p.Sources[src] = sp;
                }
                foreach (var d in dests)
                {
                    if (!sp.Cells.ContainsKey(d))
                        sp.Cells[d] = new CellProgress(d);
                }
            }
            // Capture per-source size weights so Overall reflects the
            // wall-clock progress proportionally — a 100 GB source
            // dominates a 100 MB source. Sources without a cached
            // size silently fall back to equal weight in
            // RecomputeOverall.
            if (sourceSizeBytes is not null)
            {
                foreach (var (path, bytes) in sourceSizeBytes)
                {
                    if (bytes > 0) p.SourceWeightBytes[path] = bytes;
                }
            }
            p.RecomputeOverall();
        }
        Raise(backupId);
    }

    public void ReportCellStarted(string backupId, string sourcePath, string destinationName)
    {
        var p = _state.GetOrAdd(backupId, _ => new BackupProgress(backupId));
        lock (p)
        {
            // Bump PurgeGeneration so any in-flight delayed purge from
            // a prior run lands with a stale gen and bails.
            p.PurgeGeneration++;

            // Source row.
            if (!p.Sources.TryGetValue(sourcePath, out var sp))
            {
                sp = new SourceProgress(sourcePath);
                p.Sources[sourcePath] = sp;
            }
            // Per-cell entry within the source (one per destination).
            // The cell starts at 0% and IsRunning=true; later ReportLine
            // calls update its PercentComplete; ReportCellEnded marks
            // it 100% on success. The source's PercentComplete is the
            // average of its cells, so a source-with-2-destinations
            // shows 50% when one destination has finished — matching
            // the user's mental model.
            if (!sp.Cells.TryGetValue(destinationName, out var cp))
            {
                cp = new CellProgress(destinationName);
                sp.Cells[destinationName] = cp;
            }
            cp.PercentComplete = 0;
            cp.IsRunning = true;
            cp.Detail = $"Starting → {destinationName}…";
            // Clear the Phase string left over from a PRIOR run.
            // CellProgress instances are reused across runs (the
            // dictionary keying off destinationName is stable), so a
            // cell whose last marker was "--- Prune ---" or
            // "--- Check ---" keeps that label until the new run's
            // first phase marker line arrives. The user reported:
            // "Is it normal that it shows Pruning... first when I
            // start a backup?" — that's the leak. Reset to empty so
            // the chip stays blank until duplicacy actually emits
            // "--- Backup ---" / "--- Prune ---" / "--- Check ---".
            cp.Phase = "";

            // Destination row (for the per-destination pill bar).
            if (!p.Destinations.ContainsKey(destinationName))
                p.Destinations[destinationName] = new DestinationProgress(destinationName);

            p.CurrentSourcePath = sourcePath;
            p.RecomputeOverall();
        }
        Raise(backupId);
    }

    public void ReportCellEnded(string backupId, string sourcePath, string destinationName, bool success)
    {
        if (!_state.TryGetValue(backupId, out var p)) return;
        lock (p)
        {
            if (p.Sources.TryGetValue(sourcePath, out var sp)
                && sp.Cells.TryGetValue(destinationName, out var cp))
            {
                cp.IsRunning = false;
                if (success) cp.PercentComplete = 100;
                cp.Detail = success ? "Done." : "Failed.";
            }
            p.RecomputeOverall();
        }
        Raise(backupId);
    }

    /// <summary>
    /// Backwards-compatible overload for any caller that still reports
    /// without a destination name. Marks every cell of the source as
    /// done — accurate if the run has only one destination, less so
    /// for fan-outs but at least preserves the old behaviour.
    /// </summary>
    public void ReportCellEnded(string backupId, string sourcePath, bool success)
    {
        if (!_state.TryGetValue(backupId, out var p)) return;
        lock (p)
        {
            if (p.Sources.TryGetValue(sourcePath, out var sp))
            {
                foreach (var cell in sp.Cells.Values)
                {
                    cell.IsRunning = false;
                    if (success) cell.PercentComplete = 100;
                    cell.Detail = success ? "Done." : "Failed.";
                }
            }
            p.RecomputeOverall();
        }
        Raise(backupId);
    }

    public void ReportLine(string backupId, string sourcePath, string destinationName, string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        if (!_state.TryGetValue(backupId, out var p)) return;

        // Size-hint scrape: Duplicacy emits an "Indexed N files (M
        // bytes)" line after the index pass and a "Files: N total, M
        // bytes" stats line at backup end. Either gives a strictly
        // more accurate per-source size than our cached SourceSizeProbe
        // walk (Duplicacy itself just measured it). Push the value
        // into the live BackupProgress weight map AND the global
        // SourceSizeProbe cache so the next run benefits too. Logged
        // here BEFORE the phase/percent screening below so we still
        // pick up a size hint from a line that doesn't carry a
        // percent.
        var sizeHint = TryParseSourceSizeBytes(line);
        if (sizeHint is long bytes && bytes > 0)
        {
            try { ServiceLocator.SizeProbe.UpdateCachedBytes(sourcePath, bytes); }
            catch { /* size-probe is a singleton — never throws but be defensive */ }
            lock (p)
            {
                p.SourceWeightBytes[sourcePath] = bytes;
                p.RecomputeOverall();
            }
            // Don't Raise here — if the line ALSO carries a percent
            // or phase marker, the block below will fire Raise once.
            // Otherwise we still want subscribers to see the new
            // weight, so fire here in the no-percent case.
            if (line.IndexOf('%') < 0 && TryParsePhaseMarker(line) is null)
            {
                Raise(backupId);
                return;
            }
        }

        var phaseMarker = TryParsePhaseMarker(line);
        if (phaseMarker is null && line.IndexOf('%') < 0 && !LineCarriesMilestone(line)) return;
        var pct = TryParsePercent(line);
        var detail = TryParseHumanDetail(line);
        if (phaseMarker is null && pct is null && detail is null) return;
        lock (p)
        {
            if (!p.Sources.TryGetValue(sourcePath, out var sp))
            {
                sp = new SourceProgress(sourcePath);
                p.Sources[sourcePath] = sp;
            }
            if (!sp.Cells.TryGetValue(destinationName, out var cp))
            {
                cp = new CellProgress(destinationName) { IsRunning = true };
                sp.Cells[destinationName] = cp;
            }
            if (phaseMarker is not null)
            {
                // A new phase ("Pruning", "Checking") starts at 0% as
                // far as Duplicacy's stdout is concerned, but the cell
                // as a whole has already finished the upload phase —
                // do NOT walk PercentComplete back to 0. Just record
                // the phase so the UI can show "Pruning…" while the
                // bar stays full at 100% from the upload pass.
                cp.Phase = phaseMarker;
                cp.Detail = phaseMarker + "…";
            }
            if (pct is double v)
            {
                // Within a single (source, destination) cell, duplicacy
                // emits monotonic-non-decreasing percentages within a
                // phase. We don't reset between phases (see above) so
                // post-100% percentages from prune/check are clamped
                // to the existing PercentComplete.
                cp.PercentComplete = Math.Max(cp.PercentComplete, Math.Clamp(v, 0, 100));
            }
            if (detail is not null) cp.Detail = detail;
            p.RecomputeOverall();
        }
        Raise(backupId);
    }

    /// <summary>
    /// Parse Duplicacy's various "I just measured the source size"
    /// stdout patterns and return the byte count. Returns null when
    /// the line isn't a recognised size-hint.
    ///
    /// Duplicacy emits at least these shapes during a backup:
    ///   • "Indexed N files (M bytes)" — fires AFTER the index pass,
    ///     well before chunk upload starts. Our preferred hint:
    ///     earliest accurate size.
    ///   • "Packed N files (M bytes)" — emitted periodically during
    ///     upload. Carries cumulative bytes packed; less useful as
    ///     an absolute size but gives a confirming value at the
    ///     final emission.
    ///   • "Files: N total, M bytes" — the BACKUP_STATS line at
    ///     completion. Confirms the final figure.
    ///
    /// We accept any of them — the more permissive we are, the more
    /// often we get a fresh size to update the cache with. The regex
    /// requires the byte count to follow a recognisable noun + count
    /// pattern so we don't mistake e.g. "Uploaded chunk 1234 size 5
    /// bytes" for a source-size line.
    /// </summary>
    internal static long? TryParseSourceSizeBytes(string line)
    {
        // Cheap pre-screen so the hot path skips most lines without
        // running any regex. The keywords are exclusive to size-hint
        // lines (chunk-upload lines say "size", not "files (".)
        if (line.IndexOf("files", StringComparison.OrdinalIgnoreCase) < 0
            && line.IndexOf("Files:", StringComparison.Ordinal) < 0) return null;
        var m = SourceSizeIndexedRx.Match(line);
        if (!m.Success) m = SourceSizeStatsRx.Match(line);
        if (!m.Success) return null;

        // Duplicacy's actual emitted format for BACKUP_STATS is the
        // human-readable form: "Files: 274 total, 1,219M bytes" — a
        // comma-separated decimal followed by an optional K/M/G/T
        // suffix (1024-based, NO trailing B). Earlier the parser
        // required a raw integer and only matched the rarer
        // "Indexed N files (1234567890 bytes)" lines that don't
        // appear in the user's verbose-off run. The user reported:
        // "in the sources column, once a successful backup has been
        // completed, after each Path it should indicate the size of
        // the source within parentheses" — and saw nothing because
        // the parser silently skipped every BACKUP_STATS line.
        var digits = m.Groups[1].Value;
        var unit   = m.Groups.Count > 2 ? m.Groups[2].Value : "";
        if (!ParseHumanizedBytes(digits, unit, out var bytes)) return null;
        return bytes > 0 ? bytes : null;
    }

    /// <summary>
    /// Parse the (possibly comma-grouped) decimal head + optional
    /// unit suffix Duplicacy emits in BACKUP_STATS lines.
    /// Multipliers are 1024-based to match Duplicacy's own
    /// formatter. Returns false on parse failure so the caller can
    /// drop to the next regex (or skip the line).
    /// </summary>
    private static bool ParseHumanizedBytes(string digits, string unit, out long bytes)
    {
        bytes = 0;
        // Strip commas (US grouping). Decimal point is preserved
        // for cases like "1.2G". Invariant culture so we don't fight
        // the user's regional decimal separator.
        var stripped = digits.Replace(",", "");
        if (!double.TryParse(stripped, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) || v < 0)
            return false;
        long mult = (unit ?? "").ToUpperInvariant() switch
        {
            ""  => 1L,
            "K" => 1L << 10,
            "M" => 1L << 20,
            "G" => 1L << 30,
            "T" => 1L << 40,
            _   => 0L,
        };
        if (mult == 0) return false;
        bytes = (long)Math.Round(v * mult);
        return true;
    }

    private static readonly Regex SourceSizeIndexedRx = new(
        // "Indexed N files (123456 bytes)" or "Indexed N files (1.2G bytes)"
        @"(?:Indexed|Packed)\s+\d+\s+files?\s*\(\s*([\d,.]+)\s*([KMGT])?\s+bytes?\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SourceSizeStatsRx = new(
        // "Files: 274 total, 1,219M bytes" (BACKUP_STATS form) — the
        // unit suffix is single-letter, no trailing B.
        @"Files:\s+\d+\s+total,\s+([\d,.]+)\s*([KMGT])?\s+bytes?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Detect the "--- {op} ---" phase markers the runner
    /// emits between init / backup / prune / check passes. Returns
    /// the user-facing phase label (capitalised, in -ing form) or
    /// null when the line isn't a phase marker.</summary>
    private static string? TryParsePhaseMarker(string line)
    {
        // Lines look like "13:51:33 --- Backup ---" / "--- Prune ---"
        // — the timestamp prefix is variable; match anywhere.
        if (!line.Contains("--- ", StringComparison.Ordinal)) return null;
        if (line.Contains("--- Backup ---", StringComparison.OrdinalIgnoreCase)) return "Backing up";
        if (line.Contains("--- Prune ---",  StringComparison.OrdinalIgnoreCase)) return "Pruning";
        if (line.Contains("--- Check ---",  StringComparison.OrdinalIgnoreCase)) return "Checking";
        if (line.Contains("--- Init ",      StringComparison.OrdinalIgnoreCase)) return "Initialising";
        return null;
    }

    /// <summary>
    /// Backwards-compatible overload — called from any path that
    /// doesn't yet thread the destination through. Updates every
    /// in-flight cell of the source; for single-destination runs
    /// this matches the prior behaviour.
    /// </summary>
    public void ReportLine(string backupId, string sourcePath, string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        if (!_state.TryGetValue(backupId, out var p)) return;
        if (line.IndexOf('%') < 0 && !LineCarriesMilestone(line)) return;
        var pct = TryParsePercent(line);
        var detail = TryParseHumanDetail(line);
        if (pct is null && detail is null) return;
        lock (p)
        {
            if (!p.Sources.TryGetValue(sourcePath, out var sp)) return;
            foreach (var cell in sp.Cells.Values.Where(c => c.IsRunning))
            {
                if (pct is double v)
                    cell.PercentComplete = Math.Max(cell.PercentComplete, Math.Clamp(v, 0, 100));
                if (detail is not null) cell.Detail = detail;
            }
            p.RecomputeOverall();
        }
        Raise(backupId);
    }

    public void ReportRunFinished(string backupId)
    {
        // Purge tracked state ~5s after end so a fast re-trigger doesn't
        // pick up stale 100%-ed entries. The card listens to
        // Orchestrator.RunEnded for the canonical "stop showing
        // progress" signal; this just frees memory.
        if (_state.TryGetValue(backupId, out var p))
        {
            // Mark the BackupProgress as scheduled-for-purge with the
            // current generation token, so a fast re-run that recreates
            // the same key with a NEW BackupProgress instance isn't
            // accidentally evicted by this delayed callback.
            int gen;
            lock (p) { gen = ++p.PurgeGeneration; foreach (var sp in p.Sources.Values) sp.IsRunning = false; }
            // Hold for a beat so any late stdout line doesn't recreate
            // a fresh entry with 0% right after we cleared.
            _ = System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(5))
                .ContinueWith(_ =>
                {
                    if (!_state.TryGetValue(backupId, out var stillThere)) return;
                    // Only remove if the entry hasn't been re-keyed AND
                    // the generation token matches what we observed.
                    // Same-key re-runs bump PurgeGeneration; mismatched
                    // gen means a newer ReportRunFinished superseded
                    // this one and owns the next purge.
                    lock (stillThere)
                    {
                        if (!ReferenceEquals(stillThere, p)) return;
                        if (stillThere.PurgeGeneration != gen) return;
                    }
                    _state.TryRemove(backupId, out BackupProgress? _);
                });
        }
        Raise(backupId);
    }

    private void Raise(string id)
    {
        try { Updated?.Invoke(id); }
        catch (Exception ex) { _log.Warning(ex, "Updated handler threw for {Id}", id); }
    }

    /// <summary>
    /// Pulls a percentage out of a Duplicacy stdout line. Common shapes:
    ///   "Uploaded chunk 1 size 4194304, 2.84MB/s 00:00:00 0.5%"
    ///   "All chunks have been uploaded — 100.0%"
    /// Returns null when the line isn't progress-bearing (info, error,
    /// blank). The regex is intentionally permissive: a future format
    /// change that adds "0.5 percent" still returns null silently rather
    /// than crashing the run.
    /// </summary>
    private static readonly Regex PercentRx = new(
        @"(?<![A-Za-z0-9])(\d{1,3}(?:\.\d+)?)\s*%",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static double? TryParsePercent(string line)
    {
        // Hot-path optimization: ReportLine is invoked for EVERY stdout
        // line — informational chatter, error messages, blank lines —
        // and >99% of those lines don't carry a percent. The compiled
        // regex still pays NFA setup + Match allocation per non-match,
        // adding up to milliseconds per second on a verbose run. A 1-
        // char IndexOf('%') is ~10ns and short-circuits before we even
        // touch the regex machinery.
        if (line.IndexOf('%') < 0) return null;
        var m = PercentRx.Match(line);
        if (!m.Success) return null;
        if (!double.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v)) return null;
        if (v < 0 || v > 100) return null; // not a percentage
        return v;
    }

    /// <summary>
    /// Extracts a one-line human detail from progress-bearing stdout —
    /// either an "Uploaded chunk N" line (fold to "Uploading…") or one of
    /// the milestone lines duplicacy prints at phase boundaries.
    /// </summary>
    private static string? TryParseHumanDetail(string line)
    {
        if (line.Contains("Uploaded chunk", StringComparison.OrdinalIgnoreCase))
            return "Uploading chunks…";
        if (line.Contains("Listing all chunks", StringComparison.OrdinalIgnoreCase))
            return "Listing chunks at destination…";
        if (line.Contains("Indexing", StringComparison.OrdinalIgnoreCase))
            return "Indexing files…";
        if (line.Contains("Backup for ", StringComparison.OrdinalIgnoreCase) &&
            line.Contains(" completed", StringComparison.OrdinalIgnoreCase))
            return "Backup complete.";
        return null;
    }

    /// <summary>
    /// Quick pre-screen mirroring the keyword set <see cref="TryParseHumanDetail"/>
    /// matches on. Used by <see cref="ReportLine"/>'s hot path to skip
    /// the full parsers when the line clearly carries no milestone (the
    /// vast majority of stdout chatter — ANALYZING, INDEX_*, SNAPSHOT_*
    /// lines that don't include any of these phrases). Each Contains
    /// is ordinal-ignoreCase for hot-path predictability; the keywords
    /// are ordered by descending hit frequency on a normal run.
    /// </summary>
    private static bool LineCarriesMilestone(string line) =>
        line.Contains("Uploaded chunk", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Indexing", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Listing all chunks", StringComparison.OrdinalIgnoreCase)
        || (line.Contains("Backup for ", StringComparison.OrdinalIgnoreCase)
            && line.Contains(" completed", StringComparison.OrdinalIgnoreCase));

    /// <summary>Snapshot of one backup's current progress.</summary>
    public sealed class BackupProgress
    {
        public string BackupId { get; }
        public Dictionary<string, SourceProgress> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// Per-destination aggregate progress, keyed by destination
        /// name. Each entry is the average of completed-cell percent
        /// across every source that targets that destination — used
        /// by the BackupCard's destination pills to fill from left to
        /// right as that specific destination's chunks land. Without
        /// this, two-destination runs only had a single overall bar
        /// and the user couldn't see which destination was lagging.
        /// </summary>
        public Dictionary<string, DestinationProgress> Destinations { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? CurrentSourcePath { get; set; }
        /// <summary>Aggregate 0..100. Computed as the average of
        /// every (source × destination) cell percent, so a backup with
        /// 2 sources and 2 destinations needs all four cells at 100%
        /// to show 100% — the previous "average per-source" formula
        /// reported 100% as soon as the first source finished its
        /// last destination, which read as misleading on multi-source
        /// runs.</summary>
        public double OverallPercent { get; set; }
        /// <summary>Bumped each time <see cref="ReportRunFinished"/>
        /// schedules a purge; the delayed callback compares against this
        /// to avoid evicting a freshly-restarted run that landed on the
        /// same key.</summary>
        internal int PurgeGeneration;

        /// <summary>
        /// Per-source byte-size hints used to weight the Overall
        /// percentage when sources are very different in size. Without
        /// these, equal weighting makes a 100 GB / 100 MB pair look
        /// 50% done as soon as the tiny one finishes — misleading on
        /// the bandwidth budget the user actually cares about.
        /// Populated lazily from <c>SourceSizeProbe</c>'s cached results
        /// at <see cref="RegisterRunCells"/> time; sources without a
        /// cached size fall back to equal weight.
        /// </summary>
        public Dictionary<string, long> SourceWeightBytes { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal BackupProgress(string backupId) { BackupId = backupId; }

        internal void RecomputeOverall()
        {
            // Roll per-source percent up from each source's per-cell
            // entries. PercentComplete on SourceProgress is the average
            // of its own cells, so a source pushed to 1 of 2 dests
            // reads as 50% (matches the user's expectation that the
            // bar reflects "how many of my targets have this data").
            foreach (var src in Sources.Values)
                src.RecomputeFromCells();

            // Aggregate per-destination: average of every source's cell
            // pointed at this destination. The Destinations dict was
            // seeded by ReportCellStarted, so an unstarted dest stays
            // at 0% rather than disappearing.
            foreach (var dest in Destinations.Values)
            {
                var pcs = Sources.Values
                    .SelectMany(s => s.Cells.Values)
                    .Where(c => string.Equals(c.DestinationName, dest.Name, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                dest.PercentComplete = pcs.Count == 0
                    ? 0
                    : pcs.Sum(c => c.PercentComplete) / pcs.Count;
            }

            // Overall: byte-weighted average of per-source progress
            // when we have size hints for EVERY source, otherwise
            // equal-weight average. Per-source progress is itself the
            // cell average, so a source pushed to 1 of 2 destinations
            // contributes 50% of its weight. With weights, a 100 GB
            // source dominates a 100 MB source — matching the
            // wall-clock progress the user is actually waiting on.
            //
            // Why "every source must have a weight before we go
            // weighted": the user reported a card showing
            //   • Source 1 at 13%
            //   • Source 2 at 0%
            //   • Overall at 0%   ← obviously wrong
            // Root cause was a PARTIAL-weights state: the
            // SourceSizeProbe cache had a multi-MB reading for
            // Source 2 but nothing yet for Source 1, so
            // SourceWeightBytes contained one huge weight (the source
            // sitting at 0%) and a fallback "1" for the source
            // sitting at 13%. The math 13×1 + 0×50_000_000 ÷
            // (1 + 50_000_000) collapsed to ~0%. Mixing measured
            // bytes with a synthetic "1" fallback is meaningless —
            // the units don't agree. Either go fully weighted (we
            // know all sizes) or fully equal (treat sources
            // symmetrically); never half-and-half.
            //
            // Once duplicacy has indexed every source the cache
            // catches up and we transition into byte-weighted mode
            // for the rest of the run. The monotonic floor below
            // prevents a visible bar regression at that boundary.
            if (Sources.Count == 0) { OverallPercent = 0; return; }
            bool allWeighted = Sources.Values.All(s =>
                SourceWeightBytes.TryGetValue(s.SourcePath, out var b) && b > 0);
            double weightedSum = 0;
            double totalWeight = 0;
            foreach (var src in Sources.Values)
            {
                var w = allWeighted
                    ? (double)SourceWeightBytes[src.SourcePath]
                    : 1.0;
                weightedSum += src.PercentComplete * w;
                totalWeight += w;
            }
            var raw = totalWeight > 0 ? weightedSum / totalWeight : 0;

            // Monotonic clamp: Overall must never decrease within a
            // single run. A mid-run weight refresh (we just learned
            // source A is 10× larger than we thought) would
            // mathematically pull Overall down — the user would see
            // the bar JUMP BACKWARD, which reads as a glitch even
            // though the new figure is more accurate. Pinning to a
            // monotonic floor keeps the bar advancing forward; the
            // newly-correct weight still influences the RATE of
            // progress going forward, just not the displayed number
            // at the moment of the update.
            //
            // The floor is reset to 0 at run start by RegisterRunCells
            // (which constructs a fresh BackupProgress) and on
            // ReportRunFinished's purge.
            if (raw > _overallFloor) _overallFloor = raw;
            OverallPercent = _overallFloor;
        }

        /// <summary>Highest Overall% observed in this run. Drives the
        /// monotonic clamp in <see cref="RecomputeOverall"/>; resets
        /// implicitly when a fresh BackupProgress is constructed
        /// (start of a new run) or via the purge path.</summary>
        private double _overallFloor;

        internal BackupProgress Clone()
        {
            var c = new BackupProgress(BackupId)
            {
                CurrentSourcePath = CurrentSourcePath,
                OverallPercent = OverallPercent,
            };
            foreach (var (k, v) in Sources)
            {
                var sc = new SourceProgress(v.SourcePath)
                {
                    PercentComplete = v.PercentComplete,
                    IsRunning = v.IsRunning,
                    Detail = v.Detail,
                    Phase = v.Phase,
                };
                foreach (var (ck, cv) in v.Cells)
                    sc.Cells[ck] = new CellProgress(cv.DestinationName)
                    {
                        PercentComplete = cv.PercentComplete,
                        IsRunning = cv.IsRunning,
                        Detail = cv.Detail,
                        Phase = cv.Phase,
                    };
                c.Sources[k] = sc;
            }
            foreach (var (k, v) in Destinations)
                c.Destinations[k] = new DestinationProgress(v.Name) { PercentComplete = v.PercentComplete };
            // Carry weights into the snapshot so Snapshot()-consumers
            // can reproduce the same Overall calc if they want to.
            foreach (var (k, v) in SourceWeightBytes) c.SourceWeightBytes[k] = v;
            return c;
        }
    }

    public sealed class SourceProgress
    {
        public string SourcePath { get; }
        /// <summary>Averaged across this source's cells (one per
        /// destination). 50% with two destinations means one is done.</summary>
        public double PercentComplete { get; set; }
        public bool IsRunning { get; set; }
        public string Detail { get; set; } = "";
        /// <summary>Most-recent phase label across this source's
        /// cells: "Backing up", "Pruning", "Checking", or empty.
        /// Reflects the state of any in-flight cell so the UI can
        /// surface "Pruning…" after the upload pass finishes — runs
        /// stay at 100% PercentComplete throughout the prune/check
        /// passes, and without a separate phase label the user
        /// thinks the run is hung.</summary>
        public string Phase { get; set; } = "";

        /// <summary>Per-destination cell within this source. Keyed by
        /// destination name.</summary>
        public Dictionary<string, CellProgress> Cells { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal SourceProgress(string sourcePath) { SourcePath = sourcePath; }

        internal void RecomputeFromCells()
        {
            if (Cells.Count == 0) return;
            PercentComplete = Cells.Values.Sum(c => c.PercentComplete) / Cells.Count;
            IsRunning = Cells.Values.Any(c => c.IsRunning);
            // Surface the busiest cell's detail + phase so the user
            // sees "Pruning…" while at least one cell is in the prune
            // pass.
            var live = Cells.Values.FirstOrDefault(c => c.IsRunning);
            if (live is not null)
            {
                if (!string.IsNullOrEmpty(live.Detail)) Detail = live.Detail;
                if (!string.IsNullOrEmpty(live.Phase)) Phase = live.Phase;
            }
        }
    }

    public sealed class CellProgress
    {
        public string DestinationName { get; }
        public double PercentComplete { get; set; }
        public bool IsRunning { get; set; }
        public string Detail { get; set; } = "";
        /// <summary>
        /// Which Duplicacy phase the cell is in: "Backing up",
        /// "Pruning", "Checking", or empty before the first phase
        /// marker. Surfaced in the BackupCard so the user
        /// understands why a "100%" run is still busy after the
        /// upload bar maxes out — the prune + check passes can take
        /// minutes on remote storages.
        /// </summary>
        public string Phase { get; set; } = "";
        internal CellProgress(string destinationName) { DestinationName = destinationName; }
    }

    public sealed class DestinationProgress
    {
        public string Name { get; }
        public double PercentComplete { get; set; }
        internal DestinationProgress(string name) { Name = name; }
    }
}
