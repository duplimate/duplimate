using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Duplimate.Services;

/// <summary>
/// Single source of truth for the "hide Duplicacy DEBUG/TRACE lines
/// unless verbose diagnostic logging is on" rule. Verbose is a DISPLAY
/// concern only — every persistence path (per-cell backup logs,
/// restore wizard log file, test-restore log file, app log) writes
/// raw output. The filter is applied at the moment of rendering:
/// <list type="bullet">
///   <item>RestoreViewModel.ProgressLog (the in-wizard terminal pane)</item>
///   <item>LogsViewModel.LiveTail (the global live-tail pane)</item>
///   <item>LogsViewModel.LogText (the selected past run's body)</item>
/// </list>
/// Toggling the verbose setting re-materializes those three display
/// surfaces from their raw backing buffers, so the change is
/// retroactive and doesn't lose data.
/// </summary>
internal static class VerboseLogFilter
{
    /// <summary>
    /// Anchored to the standard Duplicacy log header
    /// <c>YYYY-MM-DD HH:MM:SS(.fff) &lt;LEVEL&gt;&#32;</c> so we don't
    /// accidentally match an INFO line whose path or message body
    /// happens to embed " DEBUG " (e.g. a folder named
    /// <c>C:\My DEBUG Stuff</c>, or an info line like
    /// <c>"Set debug level: from DEBUG to INFO"</c>).
    /// </summary>
    private static readonly Regex SingleLineDebugRegex = new(
        @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d+)? (?:DEBUG|TRACE) ",
        RegexOptions.Compiled);

    /// <summary>True iff <paramref name="line"/> is a Duplicacy DEBUG-
    /// or TRACE-level entry that should be suppressed when verbose is
    /// off.</summary>
    public static bool IsDuplicacyDebugLine(string line) =>
        !string.IsNullOrEmpty(line) && SingleLineDebugRegex.IsMatch(line);

    /// <summary>True iff the user has opted into verbose diagnostic
    /// logging via Settings → "Show verbose diagnostic logs". Reads
    /// directly from the live config so a runtime toggle takes effect
    /// on the next render without any subscription bookkeeping at
    /// each call site.</summary>
    public static bool IsVerboseOn()
    {
        var lvl = ServiceLocator.Config.Current.Logging?.MinimumLevel;
        return string.Equals(lvl, "Verbose", StringComparison.OrdinalIgnoreCase)
            || string.Equals(lvl, "Debug",   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True iff <paramref name="line"/> should be displayed
    /// given the current verbose setting. DEBUG/TRACE pass when
    /// verbose is on, are hidden otherwise; everything else always
    /// passes.</summary>
    public static bool ShouldShow(string line) =>
        !IsDuplicacyDebugLine(line) || IsVerboseOn();

    /// <summary>
    /// Apply the filter to a multi-line block, returning a new string
    /// with DEBUG/TRACE lines stripped when verbose is off. Returns
    /// <paramref name="raw"/> unchanged when verbose is on or when
    /// the input is empty — the common case in display flushes, so
    /// the early-out matters for hot-path materialization (live tail
    /// flushes ~20×/sec on a verbose run).
    /// </summary>
    public static string FilterForDisplay(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw ?? string.Empty;
        if (IsVerboseOn()) return raw;

        // Walk the buffer character-by-character to preserve the
        // original line terminators (\r\n vs \n) without splitting →
        // re-joining and choosing a single separator. Each surface's
        // buffer is hard-capped at ~400 KB, so an O(n) pass with one
        // Regex.IsMatch per non-empty line stays well under 1ms even
        // on a verbose run.
        var sb = new StringBuilder(raw.Length);
        int i = 0, n = raw.Length;
        while (i < n)
        {
            int lineStart = i;
            while (i < n && raw[i] != '\n') i++;
            int termInclusive = (i < n) ? i + 1 : i; // include the '\n' if present

            // Snippet of just this line's content (no terminator) for
            // the regex. Allocating a substring per line is fine at
            // 400 KB / ~50-byte avg line = ~8k allocations per full
            // flush, all short-lived and Gen0-friendly.
            int contentLen = i - lineStart;
            if (contentLen == 0
                || !SingleLineDebugRegex.IsMatch(raw.Substring(lineStart, contentLen)))
            {
                sb.Append(raw, lineStart, termInclusive - lineStart);
            }
            i = termInclusive;
        }
        return sb.ToString();
    }
}
