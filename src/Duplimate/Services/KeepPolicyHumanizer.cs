using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Duplimate.Services;

/// <summary>
/// Parses a Duplicacy keep-policy string (space-separated <c>n:m</c>
/// pairs) and renders it in plain English for the Backup editor.
///
/// Policy syntax (from duplicacy-cli -keep docs): each pair is
/// <c>n:m</c>, meaning "keep one revision every n days for revisions
/// older than m days." <c>n = 0</c> means "delete revisions older than
/// m days." Rules are applied in descending <c>m</c> order — the oldest
/// window wins.
///
/// This is purely cosmetic — the string is still passed through to
/// duplicacy verbatim by <see cref="DuplicacyRunner"/>. The humanizer
/// exists so a user can tell at a glance what <c>0:365 30:90 7:30 1:7</c>
/// actually means without decoding it in their head.
/// </summary>
public static class KeepPolicyHumanizer
{
    public static readonly string Example = "e.g. 0:365 30:90 7:30 1:7";

    public static KeepPolicyDescription Describe(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy))
        {
            return new KeepPolicyDescription(
                Valid: true,
                Summary: "No policy set — every revision is kept forever.",
                Lines: Array.Empty<string>(),
                Error: null);
        }

        var rules = new List<(int Every, int OlderThan, string Raw)>();
        foreach (var part in policy.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = part.Split(':');
            if (bits.Length != 2
                || !int.TryParse(bits[0], out var n)
                || !int.TryParse(bits[1], out var m)
                || n < 0 || m < 0)
            {
                return new KeepPolicyDescription(
                    Valid: false,
                    Summary: $"“{part}” isn't a valid rule. Use space-separated n:m pairs where n and m are whole numbers (n = keep one per N days, m = age in days).",
                    Lines: Array.Empty<string>(),
                    Error: $"invalid token: {part}");
            }
            rules.Add((n, m, part));
        }

        if (rules.Count == 0)
        {
            return new KeepPolicyDescription(
                Valid: false,
                Summary: "Policy is empty. Add at least one n:m rule.",
                Lines: Array.Empty<string>(),
                Error: "no rules");
        }

        // Render oldest-first so the bullet list reads like a timeline
        // from the past back to now.
        var ordered = rules.OrderByDescending(r => r.OlderThan).ToList();
        var lines = new List<string>();
        foreach (var (every, olderThan, _) in ordered)
        {
            var when = olderThan == 0
                ? "From day 0 onward"
                : $"Older than {olderThan} day{(olderThan == 1 ? "" : "s")}";
            var what = every == 0
                ? "delete all revisions"
                : every switch
                {
                    1   => "keep one revision per day",
                    7   => "keep one revision per week",
                    30  => "keep one revision per month",
                    365 => "keep one revision per year",
                    _   => $"keep one revision every {every} days",
                };
            lines.Add($"• {when}: {what}.");
        }

        var summary = string.Join("\n", lines);
        return new KeepPolicyDescription(Valid: true, Summary: summary, Lines: lines, Error: null);
    }
}

public readonly record struct KeepPolicyDescription(
    bool Valid,
    string Summary,
    IReadOnlyList<string> Lines,
    string? Error);
