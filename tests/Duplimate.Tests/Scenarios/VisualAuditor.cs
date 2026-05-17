using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Duplimate.Tests.Scenarios;

/// <summary>
/// Heuristic sanity checks against rendered scenario screenshots.
/// These don't replace human review — they catch the obvious-but-
/// noisy failures (entirely-blank frames, single-colour washes) so
/// the human reviewing SUMMARY.md can focus on layout / aesthetic
/// issues instead of "why is screenshot 7 just white".
///
/// Returns a short anomaly description, or null if the frame looks
/// fine. The runner appends every non-null result to the scenario's
/// <see cref="ScenarioReport.Anomalies"/> list.
///
/// Uses ImageSharp (already a test-project dependency) so we don't
/// need /unsafe blocks to read raw pixels — the auditor stays in
/// safe code, callable from the same dispatcher thread the
/// renderer ran on.
/// </summary>
public static class VisualAuditor
{
    /// <summary>
    /// Audit a freshly-saved PNG. Suspicious distributions get
    /// flagged:
    ///   • &gt;99% of sampled pixels share one colour bucket → blank
    ///     / washed-out frame (layout broke or the render never ran).
    ///   • Image is too small to be a real window render.
    /// </summary>
    public static string? Audit(string pngPath)
    {
        try
        {
            var info = new FileInfo(pngPath);
            if (!info.Exists)        return $"screenshot file missing: {pngPath}";
            if (info.Length < 1024)  return $"screenshot suspiciously small ({info.Length} bytes): {pngPath}";

            using var img = Image.Load<Rgba32>(pngPath);
            int w = img.Width, h = img.Height;
            if (w < 200 || h < 100) return $"screenshot too small ({w}×{h}): {pngPath}";

            // Two checks paired together so we don't false-positive
            // on perfectly normal "header at top, lots of cream
            // whitespace below" pages (the user's Settings + empty
            // Destinations views were tripping the old 20×20 / 99%
            // heuristic):
            //
            //   1) Whole frame, 50×50 grid (2500 samples), threshold
            //      99.5%. A truly blank frame still sits at 100%; a
            //      page with a single 80×80 icon + a couple of
            //      headlines drops to 95-97%.
            //   2) Top third only, 30×30 grid. The header band is
            //      where every page has SOMETHING (page-title,
            //      navbar, banner). If that band reads as one
            //      colour, layout almost certainly broke even when
            //      the body below is calm whitespace.
            //
            // Both checks must trip for an anomaly to get reported.
            int wholeFrameMax = SampleDominantBucket(img, 0, 0, w, h, 50, 50, out int wholeTotal);
            bool wholeBlank = wholeTotal > 0 && wholeFrameMax >= wholeTotal * 0.995;

            int topThirdH = Math.Max(50, h / 3);
            int topMax = SampleDominantBucket(img, 0, 0, w, topThirdH, 30, 30, out int topTotal);
            bool topBlank = topTotal > 0 && topMax >= topTotal * 0.995;

            if (wholeBlank && topBlank)
                return $"frame appears blank ({wholeFrameMax}/{wholeTotal} whole-frame, {topMax}/{topTotal} top-third samples in one bucket)";
        }
        catch (Exception ex)
        {
            return $"auditor threw on {Path.GetFileName(pngPath)}: {ex.Message}";
        }
        return null;
    }

    /// <summary>
    /// Sample a coarse rect of pixels and return the size of the
    /// largest colour bucket (4 bits per channel = 4096 buckets).
    /// </summary>
    private static int SampleDominantBucket(Image<Rgba32> img, int x0, int y0, int w, int h, int cols, int rows, out int total)
    {
        var buckets = new int[4096];
        total = 0;
        for (int yi = 0; yi < rows; yi++)
        {
            int y = rows == 1 ? y0 + h / 2 : y0 + (yi * (h - 1)) / (rows - 1);
            for (int xi = 0; xi < cols; xi++)
            {
                int x = cols == 1 ? x0 + w / 2 : x0 + (xi * (w - 1)) / (cols - 1);
                var px = img[x, y];
                int idx = ((px.R >> 4) << 8) | ((px.G >> 4) << 4) | (px.B >> 4);
                buckets[idx]++;
                total++;
            }
        }
        int max = 0;
        for (int i = 0; i < buckets.Length; i++)
            if (buckets[i] > max) max = buckets[i];
        return max;
    }
}
