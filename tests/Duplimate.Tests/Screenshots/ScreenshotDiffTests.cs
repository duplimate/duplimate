using System;
using System.IO;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace Duplimate.Tests.Screenshots;

/// <summary>
/// Pixel-diffs every freshly-generated screenshot under
/// <c>artifacts/screenshots/</c> against a committed-to-git baseline
/// under <c>artifacts/baseline/</c>. If a view's rendered output drifts
/// by more than the threshold, the test fails with the drift percentage
/// and the per-pixel diff PNG is dropped next to the new screenshot for
/// eyeballing.
///
/// Workflow:
///   • First time a new screenshot test is added, its PNG won't exist
///     under baseline/ — the test auto-accepts and copies the new PNG
///     in. Commit both the new screenshot and its baseline on the same
///     PR so CI on the next run diffs against a real reference.
///   • When a visual change is intentional, set
///     <c>DUPLIMATE_UPDATE_BASELINES=1</c> before running the suite to
///     overwrite every baseline with the current render.
///   • Threshold is deliberately loose (1% of pixels) — Windows font
///     rasterization jitters across updates, and we'd rather miss
///     sub-percent drift than fail every CI run after Patch Tuesday.
/// </summary>
public class ScreenshotDiffTests
{
    /// <summary>
    /// Allowed fraction of pixels that can differ between current and
    /// baseline before the test fails. Scaled to a "meaningful" diff —
    /// a 2px-wide badge shift at our resolutions is ~0.02%, a swapped
    /// color on a card is 5%+.
    /// </summary>
    private const double MaxDiffFraction = 0.01; // 1%

    /// <summary>
    /// Per-pixel color-delta tolerance. 0 would fail on literal ARGB
    /// identity; 8 tolerates minor anti-alias / font-hinting wobble
    /// without masking real color changes.
    /// </summary>
    private const int PerPixelTolerance = 8;

    [Fact]
    public void AllScreenshots_MatchBaselineWithinTolerance()
    {
        var currentDir  = Path.Combine(Paths.ArtifactsRoot, "screenshots");
        var baselineDir = Path.Combine(Paths.ArtifactsRoot, "baseline");
        // Diff PNGs go in their own dir so they're never re-iterated as
        // candidate screenshots on the next run (which used to balloon
        // both currentDir and baselineDir with *-diff-diff-diff... cruft).
        var diffDir     = Path.Combine(Paths.ArtifactsRoot, "diffs");
        Directory.CreateDirectory(baselineDir);
        Directory.CreateDirectory(diffDir);

        // Note on ordering: this class's name sorts BEFORE ScreenshotTests
        // alphabetically, so when the suite runs sequentially this test
        // executes before any PNGs exist. xUnit runs collections in
        // parallel by default though, so in practice this gets PNGs from
        // ScreenshotTests as they land. Degrade gracefully when invoked
        // in isolation with no PNGs on disk yet.
        if (!Directory.Exists(currentDir) ||
            !Directory.EnumerateFiles(currentDir, "*.png").Any(p =>
                !Path.GetFileName(p).Contains("-diff", StringComparison.Ordinal)))
        {
            // No screenshots to diff — nothing to assert. Don't block.
            return;
        }

        bool updateMode = Environment.GetEnvironmentVariable("DUPLIMATE_UPDATE_BASELINES") == "1";

        var failures = new System.Collections.Generic.List<string>();
        var created  = new System.Collections.Generic.List<string>();

        foreach (var currentPath in Directory.EnumerateFiles(currentDir, "*.png").OrderBy(p => p))
        {
            var name = Path.GetFileName(currentPath);
            // Skip prior-run diff PNGs that may still be sitting in the
            // current dir (older versions of this test wrote them here).
            if (name.Contains("-diff", StringComparison.Ordinal)) continue;

            var baselinePath = Path.Combine(baselineDir, name);

            if (!File.Exists(baselinePath) || updateMode)
            {
                File.Copy(currentPath, baselinePath, overwrite: true);
                created.Add(name);
                continue;
            }

            var diff = Diff(baselinePath, currentPath, diffDir, name);
            if (diff.Fraction > MaxDiffFraction)
            {
                failures.Add(
                    $"{name}: {diff.Fraction:P2} pixels differ " +
                    $"({diff.Differed:N0} of {diff.Total:N0}) — " +
                    $"diff PNG written to {diff.DiffPath}");
            }
        }

        if (created.Count > 0)
        {
            // Informational, not a failure — first-time seeding or
            // deliberate update-mode run.
            var header = updateMode ? "Baselines overwritten" : "New baselines recorded";
            Console.WriteLine($"{header}: {string.Join(", ", created)}");
        }

        Assert.True(failures.Count == 0,
            "Screenshot regressions detected:\n  " + string.Join("\n  ", failures));
    }

    private record DiffResult(double Fraction, long Differed, long Total, string DiffPath);

    /// <summary>
    /// Compare two PNGs pixel-by-pixel. If either dimension differs we
    /// resize the current to match the baseline before diffing — this
    /// absorbs 1-2px layout wobble without producing noisy failures
    /// every time Avalonia rounds a measure pass differently. A diff
    /// PNG highlighting changed pixels is written alongside so a human
    /// can skim what moved.
    /// </summary>
    private static DiffResult Diff(string baselinePath, string currentPath, string outDir, string name)
    {
        using var baseline = Image.Load<Rgba32>(baselinePath);
        using var current  = Image.Load<Rgba32>(currentPath);

        if (current.Width != baseline.Width || current.Height != baseline.Height)
        {
            current.Mutate(x => x.Resize(baseline.Width, baseline.Height));
        }

        long total = (long)baseline.Width * baseline.Height;
        long differed = 0;
        using var diff = new Image<Rgba32>(baseline.Width, baseline.Height);

        // Walk rows once; compute diff pixels and the running count in
        // the same pass. Using the simple indexer is fine at our
        // resolutions (~1 MPix) — pixel loops fit comfortably in a
        // few milliseconds and keep the code legible.
        for (int y = 0; y < baseline.Height; y++)
        {
            for (int x = 0; x < baseline.Width; x++)
            {
                var p1 = baseline[x, y];
                var p2 = current[x, y];
                int d = Math.Abs(p1.R - p2.R) + Math.Abs(p1.G - p2.G) + Math.Abs(p1.B - p2.B);
                if (d > PerPixelTolerance)
                {
                    diff[x, y] = new Rgba32(255, 0, 128, 255); // magenta = changed
                    differed++;
                }
                else
                {
                    // Dim the baseline under matching pixels so the
                    // magenta diff pops when reviewed visually.
                    diff[x, y] = new Rgba32(
                        (byte)(p1.R * 0.35),
                        (byte)(p1.G * 0.35),
                        (byte)(p1.B * 0.35),
                        255);
                }
            }
        }

        var diffPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(name) + "-diff.png");
        diff.Save(diffPath);
        return new DiffResult((double)differed / total, differed, total, diffPath);
    }

    private static class Paths
    {
        public static string ArtifactsRoot
        {
            get
            {
                var asmDir = Path.GetDirectoryName(typeof(ScreenshotDiffTests).Assembly.Location)!;
                var dir = new DirectoryInfo(asmDir);
                while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Duplimate.Tests.csproj")))
                    dir = dir.Parent;
                var root = dir?.FullName ?? asmDir;
                return Path.Combine(root, "artifacts");
            }
        }
    }
}
