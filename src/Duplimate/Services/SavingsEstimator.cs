using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Duplimate.Services;

/// <summary>
/// Walks the configured source paths and totals the bytes the supplied
/// Duplicacy filter rules would EXCLUDE. Used by the BackupEditor to
/// show "you're saving ~12.4 GB by skipping these" after the user
/// applies the recommended exclusions block.
///
/// Accuracy vs. speed: we walk every file under each source and apply
/// the filter rules for real (last-matching-rule wins, same as
/// Duplicacy itself). Walks are cancellable, run off the UI thread,
/// and hard-cap at <see cref="MaxScanDuration"/> per source so a
/// pathological tree (1M+ small files) can't hang the estimate
/// forever — when truncated, the result carries a flag the UI surfaces
/// as a "~" prefix to make clear the number is a lower bound.
///
/// Pattern syntax supported (mirrors <see cref="FilterSimulator"/>):
///   - Lines starting with <c>#</c> or whitespace-only: ignored.
///   - <c>e:&lt;regex&gt;</c> excludes a path that matches.
///   - <c>i:&lt;regex&gt;</c> includes a path that matches (overrides
///     a prior <c>e:</c> if it comes later in the rule list).
///   - <c>+&lt;glob&gt;</c> / <c>-&lt;glob&gt;</c> (glob includes /
///     excludes) — translated to regex via the same logic
///     <see cref="FilterSimulator"/> uses.
///   - Regex matches against the path RELATIVE to the source root,
///     with forward-slash separators (Duplicacy convention).
/// </summary>
public sealed class SavingsEstimator
{
    private static ILogger _log => AppLogger.For<SavingsEstimator>();

    /// <summary>Per-source-root cap on how long the walk runs before
    /// giving up with a partial result. The UI badge prefixes the
    /// truncated number with "~" so users know it's a lower bound.</summary>
    public TimeSpan MaxScanDuration { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Computes the total bytes that the supplied filter text would
    /// have excluded across all sourcePaths. Returns ExcludedBytes,
    /// IncludedBytes, and a Truncated flag if the time cap kicked in
    /// for any source.
    /// </summary>
    public async Task<SavingsEstimate> ComputeAsync(
        IEnumerable<string> sourcePaths,
        string filtersText,
        CancellationToken ct)
    {
        var rules = ParseRules(filtersText);
        if (rules.Count == 0)
        {
            return new SavingsEstimate(ExcludedBytes: 0, IncludedBytes: 0, Truncated: false, SourcesScanned: 0);
        }

        long excluded = 0;
        long included = 0;
        var truncated = false;
        var scanned = 0;

        foreach (var src in sourcePaths)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(src) || !Directory.Exists(src)) continue;
            scanned++;

            var perSource = await Task.Run(() => WalkOne(src, rules, ct), ct);
            excluded += perSource.Excluded;
            included += perSource.Included;
            if (perSource.Truncated) truncated = true;
        }

        _log.Debug("Savings estimate: excluded={Excl:N0} included={Incl:N0} truncated={Trunc} sources={N}",
            excluded, included, truncated, scanned);

        return new SavingsEstimate(excluded, included, truncated, scanned);
    }

    // -----------------------------------------------------------------
    // Per-source walker
    // -----------------------------------------------------------------

    private (long Excluded, long Included, bool Truncated) WalkOne(
        string sourceRoot, IReadOnlyList<Rule> rules, CancellationToken ct)
    {
        long excluded = 0;
        long included = 0;
        var deadline = DateTime.UtcNow + MaxScanDuration;
        var truncated = false;

        var rootFull = Path.GetFullPath(sourceRoot).TrimEnd('\\', '/');
        var stack = new Stack<string>();
        stack.Push(rootFull);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline) { truncated = true; break; }

            var dir = stack.Pop();
            IEnumerable<string>? files = null;
            IEnumerable<string>? subs  = null;
            try
            {
                files = Directory.EnumerateFiles(dir);
                subs  = Directory.EnumerateDirectories(dir);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException)  { continue; }
            catch (PathTooLongException)        { continue; }
            catch (IOException)                 { continue; }

            // For directories: descend if NOT excluded. If excluded,
            // sum all bytes inside as Excluded (the user wanted an
            // accurate savings number, even on large excluded trees).
            foreach (var sub in subs)
            {
                try
                {
                    var info = new DirectoryInfo(sub);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }

                var rel = MakeRelative(rootFull, sub) + "/";
                if (IsExcluded(rules, rel))
                {
                    // Bulk-count an excluded subtree without applying
                    // per-file rules inside. Honours the deadline so a
                    // huge exclusion (e.g. ^Windows/) doesn't prevent
                    // the rest of the source from being scanned.
                    excluded += SumBytesUnder(sub, deadline, ref truncated, ct);
                }
                else
                {
                    stack.Push(sub);
                }
            }

            foreach (var f in files)
            {
                long len;
                try
                {
                    var info = new FileInfo(f);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    len = info.Length;
                }
                catch { continue; }

                var rel = MakeRelative(rootFull, f);
                if (IsExcluded(rules, rel)) excluded += len;
                else included += len;
            }
        }

        return (excluded, included, truncated);
    }

    /// <summary>
    /// Recursively sums every regular-file byte under <paramref name="root"/>.
    /// Used when a directory is filter-excluded — we still want to count
    /// the bytes-saved, but per-file rule evaluation inside is moot.
    /// </summary>
    private static long SumBytesUnder(string root, DateTime deadline, ref bool truncated, CancellationToken ct)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline) { truncated = true; break; }
            var dir = stack.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    try
                    {
                        var info = new FileInfo(f);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        total += info.Length;
                    }
                    catch { /* file gone mid-walk */ }
                }
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    try
                    {
                        var info = new DirectoryInfo(sub);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch { continue; }
                    stack.Push(sub);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (DirectoryNotFoundException)  { }
            catch (PathTooLongException)        { }
            catch (IOException)                 { }
        }
        return total;
    }

    private static string MakeRelative(string root, string full)
    {
        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            full = full.Substring(root.Length).TrimStart('\\', '/');
        return full.Replace('\\', '/');
    }

    /// <summary>
    /// Last-matching-rule wins. Default verdict for a path that no rule
    /// touches is INCLUDED, matching Duplicacy's behaviour.
    /// </summary>
    internal static bool IsExcluded(IReadOnlyList<Rule> rules, string relativePath)
    {
        bool? verdict = null;
        for (int i = 0; i < rules.Count; i++)
        {
            var r = rules[i];
            if (!r.Pattern.IsMatch(relativePath)) continue;
            verdict = r.Kind switch
            {
                RuleKind.ExcludeRegex or RuleKind.ExcludeGlob => true,
                RuleKind.IncludeRegex or RuleKind.IncludeGlob => false,
                _ => verdict,
            };
        }
        return verdict ?? false;
    }

    // -----------------------------------------------------------------
    // Rule parser (deliberately matches FilterSimulator.ParseRules
    // shape; we keep our own copy so this service has no dependency
    // on the simulator's UI-facing types).
    // -----------------------------------------------------------------

    internal enum RuleKind { IncludeRegex, ExcludeRegex, IncludeGlob, ExcludeGlob }

    internal readonly record struct Rule(RuleKind Kind, Regex Pattern);

    internal static List<Rule> ParseRules(string? text)
    {
        var list = new List<Rule>();
        if (string.IsNullOrEmpty(text)) return list;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

            try
            {
                if (line.StartsWith("i:", StringComparison.Ordinal))
                    list.Add(new Rule(RuleKind.IncludeRegex, MakeRegex(line[2..])));
                else if (line.StartsWith("e:", StringComparison.Ordinal))
                    list.Add(new Rule(RuleKind.ExcludeRegex, MakeRegex(line[2..])));
                else if (line.StartsWith("+", StringComparison.Ordinal))
                    list.Add(new Rule(RuleKind.IncludeGlob, MakeRegex(GlobToRegex(line[1..]))));
                else if (line.StartsWith("-", StringComparison.Ordinal))
                    list.Add(new Rule(RuleKind.ExcludeGlob, MakeRegex(GlobToRegex(line[1..]))));
                // Unknown lines silently ignored — matches duplicacy's
                // forgiving parser, and avoids breaking on user comments
                // that don't start with #.
            }
            catch
            {
                // Malformed regex: treat as match-nothing so the rule
                // is effectively a no-op rather than crashing the walk.
                list.Add(new Rule(RuleKind.ExcludeRegex, new Regex("(?!)", RegexOptions.Compiled)));
            }
        }
        return list;
    }

    private static Regex MakeRegex(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string GlobToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*') { sb.Append(".*"); i++; }
                    else sb.Append("[^/]*");
                    break;
                case '?': sb.Append('.'); break;
                case '.': case '(': case ')': case '+': case '|':
                case '^': case '$': case '{': case '}': case '[': case ']': case '\\':
                    sb.Append('\\').Append(c); break;
                default:
                    sb.Append(c); break;
            }
        }
        sb.Append('$');
        return sb.ToString();
    }
}

/// <param name="ExcludedBytes">Total file bytes the rules would skip.</param>
/// <param name="IncludedBytes">Total file bytes the rules would keep.</param>
/// <param name="Truncated">True if any source's walk hit the time cap.</param>
/// <param name="SourcesScanned">Number of source paths actually walked.</param>
public readonly record struct SavingsEstimate(
    long ExcludedBytes,
    long IncludedBytes,
    bool Truncated,
    int SourcesScanned);
