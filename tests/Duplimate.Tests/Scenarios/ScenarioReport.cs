using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Duplimate.Tests.Scenarios;

/// <summary>
/// One scenario's run record. Built up step-by-step by
/// <see cref="ScenarioContext"/> and serialised into the
/// <see cref="ScenarioRunSummary"/> markdown at suite end.
/// </summary>
public sealed class ScenarioReport
{
    public string Name { get; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public DateTime? EndedUtc { get; private set; }
    public ScenarioStatus Status { get; private set; } = ScenarioStatus.Pending;
    public string? StatusReason { get; private set; }
    public string ScreenshotDir { get; }
    public List<ScenarioStep> Steps { get; } = new();
    public List<string> Anomalies { get; } = new();

    public ScenarioReport(string name, string screenshotDir)
    {
        Name = name;
        ScreenshotDir = screenshotDir;
    }

    public void RecordStep(string label, string screenshotPath, string? note = null) =>
        Steps.Add(new ScenarioStep(label, screenshotPath, note));

    public void RecordAnomaly(string message) => Anomalies.Add(message);

    public void MarkPassed()                  { Status = ScenarioStatus.Passed; EndedUtc = DateTime.UtcNow; }
    public void MarkSkipped(string reason)    { Status = ScenarioStatus.Skipped; StatusReason = reason; EndedUtc = DateTime.UtcNow; }
    public void MarkFailed (string reason)    { Status = ScenarioStatus.Failed;  StatusReason = reason; EndedUtc = DateTime.UtcNow; }
}

public enum ScenarioStatus { Pending, Passed, Skipped, Failed }

public sealed record ScenarioStep(string Label, string ScreenshotPath, string? Note);

/// <summary>
/// Suite-level aggregator. One static instance for the whole xUnit
/// assembly; every scenario calls <see cref="Register"/> with its
/// report. <see cref="WriteSummary"/> is invoked from the assembly
/// fixture's Dispose so a SUMMARY.md is rebuilt every test run with
/// the current pass/skip/fail breakdown plus the missing-secrets
/// list the user needs to act on.
/// </summary>
public static class ScenarioRunSummary
{
    private static readonly object _lock = new();
    private static readonly List<ScenarioReport> _reports = new();
    private static SecretsLoader? _secrets;

    public static void Register(ScenarioReport report)
    {
        lock (_lock) _reports.Add(report);
    }

    public static void AttachSecrets(SecretsLoader secrets) => _secrets = secrets;

    public static string WriteSummary(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var summaryPath = Path.Combine(outputDir, "SUMMARY.md");
        var sb = new StringBuilder();

        IReadOnlyList<ScenarioReport> snapshot;
        lock (_lock) snapshot = _reports.OrderBy(r => r.Name).ToArray();

        var passed   = snapshot.Count(r => r.Status == ScenarioStatus.Passed);
        var skipped  = snapshot.Count(r => r.Status == ScenarioStatus.Skipped);
        var failed   = snapshot.Count(r => r.Status == ScenarioStatus.Failed);

        sb.AppendLine("# E2E scenario run summary");
        sb.AppendLine();
        sb.AppendLine($"_Generated {DateTime.Now:yyyy-MM-dd HH:mm}_  ");
        sb.AppendLine($"**{passed} passed · {skipped} skipped · {failed} failed** out of {snapshot.Count} scenarios.");
        sb.AppendLine();

        // Missing secrets table — first thing the user should see if they
        // want to unlock more coverage. Each row tells them exactly which
        // file to drop in.
        if (_secrets is not null && _secrets.MissingSecretsByName.Count > 0)
        {
            sb.AppendLine("## Missing secrets");
            sb.AppendLine();
            sb.AppendLine("Scenarios were skipped because these credentials weren't on disk. Drop the missing file at the listed path to unlock them on the next run:");
            sb.AppendLine();
            sb.AppendLine("| Secret | Where to put it |");
            sb.AppendLine("|--------|-----------------|");
            foreach (var (name, path) in _secrets.MissingSecretsByName.OrderBy(kv => kv.Key))
                sb.AppendLine($"| `{name}` | `{path}` |");
            sb.AppendLine();
        }

        // Per-scenario breakdown.
        sb.AppendLine("## Scenarios");
        sb.AppendLine();
        foreach (var r in snapshot)
        {
            var statusBadge = r.Status switch
            {
                ScenarioStatus.Passed  => "✅ PASS",
                ScenarioStatus.Skipped => "⊘ SKIP",
                ScenarioStatus.Failed  => "❌ FAIL",
                _                      => "… running",
            };
            var dur = r.EndedUtc is { } end ? (end - r.StartedUtc).TotalSeconds.ToString("F1") + "s" : "—";
            sb.AppendLine($"### {r.Name} — {statusBadge} ({dur})");
            if (!string.IsNullOrEmpty(r.StatusReason))
                sb.AppendLine($"_Reason:_ {r.StatusReason}");
            sb.AppendLine();
            if (r.Steps.Count > 0)
            {
                sb.AppendLine("| # | Step | Screenshot | Notes |");
                sb.AppendLine("|---|------|------------|-------|");
                for (int i = 0; i < r.Steps.Count; i++)
                {
                    var s = r.Steps[i];
                    var rel = Path.GetRelativePath(outputDir, s.ScreenshotPath).Replace('\\', '/');
                    sb.AppendLine($"| {i + 1} | {s.Label} | [{Path.GetFileName(s.ScreenshotPath)}]({rel}) | {s.Note ?? ""} |");
                }
                sb.AppendLine();
            }
            if (r.Anomalies.Count > 0)
            {
                sb.AppendLine("**Anomalies:**");
                foreach (var a in r.Anomalies) sb.AppendLine($"- {a}");
                sb.AppendLine();
            }
        }

        File.WriteAllText(summaryPath, sb.ToString());
        return summaryPath;
    }

    /// <summary>Reset state between independent suite runs (used by the
    /// fixture's constructor so re-running the suite doesn't accumulate
    /// stale entries).</summary>
    public static void Reset()
    {
        lock (_lock) _reports.Clear();
    }
}
