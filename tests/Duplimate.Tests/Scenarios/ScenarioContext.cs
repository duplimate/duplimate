using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Duplimate.Services;
using Duplimate.Tests.TestHelpers;

namespace Duplimate.Tests.Scenarios;

/// <summary>
/// Per-scenario lifecycle wrapper. Owns the throwaway directories,
/// the report, and the UiDriver. Used by every scenario class via
/// <see cref="ScenarioBase.RunAsync"/>; the base class instantiates
/// it, hands it to the concrete scenario, and disposes it after.
///
/// Directory layout (under tests/Duplimate.Tests/artifacts/scenarios/):
///   &lt;name&gt;/
///     sources/        — synthetic dummy file trees (one subdir per source)
///     destinations/   — local-folder destinations the scenario uses
///     restored/       — restore targets so a scenario can compare hashes
///     screenshots/    — every Snapshot() PNG, prefixed by step number
///
/// Each scenario gets a fresh dir on every run (stale data is wiped on
/// construction). Source content is regenerated deterministically from a
/// seed so the same scenario produces byte-identical fixtures across runs
/// — handy for "did this change my output?" diffs.
/// </summary>
public sealed class ScenarioContext : IDisposable
{
    public string Name { get; }
    public string RootDir { get; }
    public string SourcesDir { get; }
    public string DestinationsDir { get; }
    public string RestoredDir { get; }
    public string ScreenshotsDir { get; }
    public ScenarioReport Report { get; }
    public SecretsLoader Secrets { get; }
    public UiDriver Driver { get; }

    private int _stepCounter;
    private bool _disposed;

    public ScenarioContext(string name, SecretsLoader secrets)
    {
        Name = name;
        Secrets = secrets;

        var artifactsRoot = ResolveArtifactsRoot();
        RootDir         = Path.Combine(artifactsRoot, name);
        SourcesDir      = Path.Combine(RootDir, "sources");
        DestinationsDir = Path.Combine(RootDir, "destinations");
        RestoredDir     = Path.Combine(RootDir, "restored");
        ScreenshotsDir  = Path.Combine(RootDir, "screenshots");

        // Wipe and recreate. Stale screenshots from a previous run
        // would otherwise pile up and confuse the summary's "step 7"
        // index. The wipe is cheap (small file trees) and makes
        // every run idempotent.
        TryDelete(RootDir);
        Directory.CreateDirectory(SourcesDir);
        Directory.CreateDirectory(DestinationsDir);
        Directory.CreateDirectory(RestoredDir);
        Directory.CreateDirectory(ScreenshotsDir);

        Report = new ScenarioReport(name, ScreenshotsDir);
        ScenarioRunSummary.Register(Report);

        // Reset the in-process config so prior scenarios' Backups /
        // Destinations don't leak in. Every scenario starts from a
        // clean slate; the DUPLIMATE_CONFIG_ROOT env var still keeps
        // the JSON itself isolated from the user's real install.
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Backups.Clear();
            cfg.Destinations.Clear();
        });

        Driver = new UiDriver(this);
    }

    /// <summary>
    /// Materialise a synthetic source tree under a named subfolder of
    /// <see cref="SourcesDir"/>. Deterministic from <paramref name="seed"/>
    /// so two runs of the same scenario produce identical content.
    /// Defaults to ~2 MB so the suite stays fast.
    /// </summary>
    public string MakeSource(string name, int seed = 1, long targetBytes = 2_000_000)
    {
        var path = Path.Combine(SourcesDir, name);
        Directory.CreateDirectory(path);
        SyntheticFileTree.Generate(path, seed: seed, targetBytes: targetBytes);
        return path;
    }

    /// <summary>Allocate a named local-folder destination directory.</summary>
    public string MakeLocalDestination(string name)
    {
        var path = Path.Combine(DestinationsDir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Allocate a named restore-target directory.</summary>
    public string MakeRestoreTarget(string name)
    {
        var path = Path.Combine(RestoredDir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Render the given window to PNG and record the step in the
    /// scenario report. <paramref name="label"/> is used both as the
    /// filename suffix and the step row in SUMMARY.md.
    /// </summary>
    public string Snapshot(Window window, string label, string? note = null)
    {
        _stepCounter++;
        var safe = SafeFilename(label);
        var fileName = $"{_stepCounter:D2}-{safe}.png";
        var path = Path.Combine(ScreenshotsDir, fileName);

        // Pump + layout 6 times so bound-property cascades have a
        // chance to settle before the bitmap captures. Two passes
        // were enough for static views but missed bindings that
        // re-evaluate on the second cycle (e.g. IsEnabled gated
        // through an IsNotNull converter on a freshly-set
        // SelectedDestination — the S04 snapshot was capturing the
        // disabled-from-prior-frame state). Six is overkill for
        // static layouts but cheap, and reliably catches the
        // settle-after-binding-change case.
        for (int i = 0; i < 6; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        var size = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(window.Bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(window.Bounds.Height)));
        using (var bmp = new RenderTargetBitmap(size, new Vector(96, 96)))
        {
            bmp.Render(window);
            bmp.Save(path);
        }

        Report.RecordStep(label, path, note);

        // Audit immediately so the anomaly shows up next to the
        // step that produced it. Cheap (few hundred pixel samples).
        var anomaly = VisualAuditor.Audit(path);
        if (!string.IsNullOrEmpty(anomaly))
            Report.RecordAnomaly($"{label}: {anomaly}");

        return path;
    }

    /// <summary>Mark the scenario as skipped with a recorded reason.</summary>
    public void Skip(string reason)
    {
        Report.MarkSkipped(reason);
        throw new ScenarioSkippedException(reason);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Closing windows the driver opened — best-effort, the
        // headless lifecycle does most cleanup itself.
        Driver.Dispose();
        if (Report.Status == ScenarioStatus.Pending) Report.MarkPassed();
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort */ }
    }

    private static string SafeFilename(string label)
    {
        var sb = new System.Text.StringBuilder(label.Length);
        foreach (var ch in label)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (ch is ' ' or '_' or '-') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        if (s.Length == 0) s = "step";
        if (s.Length > 60) s = s[..60];
        return s;
    }

    public static string ResolveArtifactsRoot()
    {
        var asmDir = Path.GetDirectoryName(typeof(ScenarioContext).Assembly.Location)!;
        var dir = new DirectoryInfo(asmDir);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Duplimate.Tests.csproj")))
            dir = dir.Parent;
        var root = dir?.FullName ?? asmDir;
        return Path.Combine(root, "artifacts", "scenarios");
    }
}

/// <summary>Throw-and-catch sentinel used by <see cref="ScenarioContext.Skip"/>
/// to short-circuit a scenario body without confusing xUnit. The
/// scenario base class catches it and re-routes to xUnit's
/// SkipException so the test runner reports the scenario as skipped
/// rather than failed.</summary>
public sealed class ScenarioSkippedException : Exception
{
    public ScenarioSkippedException(string reason) : base(reason) { }
}
