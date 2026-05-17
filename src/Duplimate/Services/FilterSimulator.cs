using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Duplimate.Models;
using Serilog;

namespace Duplimate.Services;

/// <summary>
/// Shows what Duplicacy's filter file would actually include / exclude.
///
/// Two modes:
///  • SimulateAsync — runs `duplicacy backup -enum-only -vss` and returns
///    the real tree. Authoritative but slower (walks the actual filesystem).
///  • PreviewAsync  — runs our own mini filter engine against a given list of
///    paths without touching duplicacy. Instant, useful while typing.
///
/// Duplicacy filter syntax (subset we support for preview):
///   - Lines starting with `#` are comments
///   - `i:<regex>` → include if match
///   - `e:<regex>` → exclude if match
///   - `+<glob>`   → include glob
///   - `-<glob>`   → exclude glob
///   - Blank lines ignored
///   - Last matching rule wins
/// </summary>
public sealed class FilterSimulator
{
    private static ILogger _log => AppLogger.For<FilterSimulator>();
    private readonly DuplicacyRunner _runner;

    public FilterSimulator(DuplicacyRunner runner) => _runner = runner;

    /// <summary>
    /// Runs a real enum-only pass against every source path of the
    /// backup and feeds results through the callback. Multi-source
    /// backups produce entries from each source sequentially; a header
    /// line-prefix tells the UI which source is currently streaming.
    /// </summary>
    public async Task SimulateAsync(Backup backup, Destination destination, string storageName,
                                    Action<SimulatedEntry> onLine, CancellationToken ct)
    {
        void LineHandler(string raw)
        {
            var entry = ParseEnumLine(raw);
            if (entry is not null) onLine(entry.Value);
        }

        _log.Information("Filter simulation start: backup={Backup} sources={Count} destination={Dest}",
            backup.Name, backup.SourcePaths.Count, destination.Name);

        // Pass the handler in via the per-call lineSink rather than
        // subscribing to the runner's global LineWritten. Earlier the
        // global subscription meant any concurrent backup spawned by
        // the orchestrator (scheduled task firing, user clicking Run on
        // a different card) routed its stdout into the simulation's
        // ParseEnumLine — the user saw foreign files appear in their
        // "what would be backed up" preview. Per-call lineSink is
        // scoped to exactly the EnumOnly invocations made here.
        foreach (var sourcePath in backup.SourcePaths)
        {
            if (ct.IsCancellationRequested) break;
            await _runner.EnumOnlyAsync(backup, sourcePath, destination, storageName, ct, lineSink: LineHandler);
        }
        _log.Information("Filter simulation finished for {Backup}", backup.Name);
    }

    /// <summary>
    /// Synchronous preview: runs the supplied filter text against `candidatePaths`.
    /// Useful as the user types filters.
    /// </summary>
    public IReadOnlyList<SimulatedEntry> Preview(string filtersText, IEnumerable<string> candidatePaths)
    {
        var rules = ParseRules(filtersText);
        var results = new List<SimulatedEntry>();
        foreach (var p in candidatePaths)
        {
            var (kept, rule) = Evaluate(rules, p);
            results.Add(new SimulatedEntry(p, kept, rule));
        }
        return results;
    }

    // ---- duplicacy stdout shape ----

    private static SimulatedEntry? ParseEnumLine(string line)
    {
        // Typical enum-only lines look like:
        //   Packing Users/me/Desktop/foo.txt
        //   Skipped file Users/me/Downloads/big.iso (filter rule: ...)
        //   Skipped directory Windows/ (filter rule: ...)
        var packM = PackRx.Match(line);
        if (packM.Success) return new SimulatedEntry(packM.Groups[1].Value, true, null);

        var skipM = SkipRx.Match(line);
        if (skipM.Success) return new SimulatedEntry(skipM.Groups[1].Value, false, skipM.Groups[2].Value);

        return null;
    }

    private static readonly Regex PackRx = new(@"Packing\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex SkipRx = new(@"Skipped\s+(?:file|directory)\s+(.+?)(?:\s+\(filter rule:\s*(.+?)\))?$",
        RegexOptions.Compiled);

    // ---- preview engine ----

    private enum RuleKind { IncludeRegex, ExcludeRegex, IncludeGlob, ExcludeGlob }

    private readonly record struct Rule(RuleKind Kind, string RawText, Regex Pattern);

    private static List<Rule> ParseRules(string text)
    {
        var list = new List<Rule>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;

            if (line.StartsWith("i:")) list.Add(Compile(RuleKind.IncludeRegex, line, line[2..]));
            else if (line.StartsWith("e:")) list.Add(Compile(RuleKind.ExcludeRegex, line, line[2..]));
            else if (line.StartsWith("+")) list.Add(Compile(RuleKind.IncludeGlob, line, GlobToRegex(line[1..])));
            else if (line.StartsWith("-")) list.Add(Compile(RuleKind.ExcludeGlob, line, GlobToRegex(line[1..])));
            // Unknown lines are ignored (mirrors duplicacy's forgiving parser)
        }
        return list;

        static Rule Compile(RuleKind k, string raw, string pattern)
        {
            try
            {
                return new Rule(k, raw, new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled));
            }
            catch
            {
                // Bad regex → match nothing (so user can see their typo as "no effect").
                return new Rule(k, raw, new Regex("(?!)", RegexOptions.Compiled));
            }
        }
    }

    private static (bool included, string? rule) Evaluate(IReadOnlyList<Rule> rules, string path)
    {
        // Duplicacy semantics: if any matching rule is reached, that rule decides.
        // Default = included.
        bool included = true;
        string? which = null;
        foreach (var r in rules)
        {
            if (!r.Pattern.IsMatch(path)) continue;
            included = r.Kind is RuleKind.IncludeRegex or RuleKind.IncludeGlob;
            which = r.RawText;
            // last match wins — keep iterating
        }
        return (included, which);
    }

    private static string GlobToRegex(string glob)
    {
        // Simple glob: * → [^/]*, ** → .*, ? → .
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

public readonly record struct SimulatedEntry(string Path, bool Included, string? MatchingRule);
