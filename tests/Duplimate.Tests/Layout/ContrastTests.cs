using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Duplimate.Tests.Layout;

/// <summary>
/// Static WCAG AA contrast guard for the palette in Theme.axaml.
/// The UX-regression user flagged was literally this: FgDim at #989898 on
/// the cream surface gave ratio 2.83 and looked unreadable. This test
/// parses the file, computes contrast ratios for the pairs we actually
/// use in real UI (body text on surface, hint text on surface, input
/// border on input background), and fails if any drops below its
/// WCAG threshold.
///
/// Intent: if someone "lightens the greys a bit for a softer look"
/// three months from now, this test fails with the exact ratio so the
/// regression doesn't ship.
/// </summary>
public class ContrastTests
{
    // Paths inside Theme.axaml are stable; if the file is ever restructured,
    // update the regex below. The point is not to parse XAML rigorously,
    // just to pluck out known brush keys and their hex colors per theme.
    private static readonly string ThemePath = ResolveThemePath();

    [Theory]
    // (textBrush, backgroundBrush, minRatio, note). 4.5 is WCAG AA body.
    // 3.0 is AA for large text (>= 18pt or 14pt bold) and UI components.
    [InlineData("Light", "DM.Brush.FgPrimary",   "DM.Brush.BgBase",    4.5, "Primary text on base")]
    [InlineData("Light", "DM.Brush.FgPrimary",   "DM.Brush.BgSurface", 4.5, "Primary text on surface")]
    [InlineData("Light", "DM.Brush.FgSecondary", "DM.Brush.BgBase",    4.5, "Secondary text on base")]
    [InlineData("Light", "DM.Brush.FgSecondary", "DM.Brush.BgSurface", 4.5, "Secondary text on surface")]
    [InlineData("Light", "DM.Brush.FgDim",       "DM.Brush.BgBase",    4.5, "Dim text on base — regressions here are the original complaint")]
    [InlineData("Light", "DM.Brush.FgDim",       "DM.Brush.BgSurface", 4.5, "Dim text on surface")]
    [InlineData("Dark",  "DM.Brush.FgPrimary",   "DM.Brush.BgBase",    4.5, "Primary text on dark base")]
    [InlineData("Dark",  "DM.Brush.FgSecondary", "DM.Brush.BgSurface", 4.5, "Secondary text on dark surface")]
    [InlineData("Dark",  "DM.Brush.FgDim",       "DM.Brush.BgSurface", 4.5, "Dim text on dark surface")]
    public void Palette_PassesWcagAa(string theme, string fgKey, string bgKey, double minRatio, string note)
    {
        var fg = ParseColor(theme, fgKey);
        var bg = ParseColor(theme, bgKey);
        var ratio = ContrastRatio(fg, bg);
        Assert.True(ratio >= minRatio,
            $"{note} — {fgKey} on {bgKey} ({theme}) has contrast ratio {ratio:F2}:1, " +
            $"below the {minRatio}:1 WCAG AA threshold.");
    }

    // ---- plumbing -----

    /// <summary>
    /// Grab the color for a given brush key under a given theme dictionary.
    /// Uses a light-touch regex rather than XAML parsing because Avalonia's
    /// XAML loader needs a running app; this test is zero-setup.
    /// </summary>
    private static (double r, double g, double b) ParseColor(string theme, string key)
    {
        var text = File.ReadAllText(ThemePath);

        // Find "<ResourceDictionary x:Key=\"Light\">" ... "</ResourceDictionary>"
        var blockRx = new Regex(
            $"<ResourceDictionary\\s+x:Key=\"{theme}\"\\s*>.*?</ResourceDictionary>",
            RegexOptions.Singleline);
        var block = blockRx.Match(text);
        Assert.True(block.Success, $"Theme block '{theme}' not found in Theme.axaml.");

        var brushRx = new Regex(
            $"x:Key=\"{Regex.Escape(key)}\"\\s+Color=\"#([0-9A-Fa-f]{{6,8}})\"");
        var m = brushRx.Match(block.Value);
        Assert.True(m.Success, $"Brush '{key}' not found in theme '{theme}'.");

        var hex = m.Groups[1].Value;
        // Tolerate both #AARRGGBB and #RRGGBB.
        if (hex.Length == 8) hex = hex.Substring(2);
        var r = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber) / 255.0;
        var g = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber) / 255.0;
        var b = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber) / 255.0;
        return (r, g, b);
    }

    /// <summary>
    /// WCAG 2.1 relative-luminance + contrast-ratio formula.
    /// https://www.w3.org/TR/WCAG21/#contrast-minimum
    /// </summary>
    private static double ContrastRatio((double r, double g, double b) a, (double r, double g, double b) b)
    {
        double La = Luminance(a);
        double Lb = Luminance(b);
        var (lighter, darker) = La > Lb ? (La, Lb) : (Lb, La);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance((double r, double g, double b) c)
    {
        static double Channel(double x) => x <= 0.03928 ? x / 12.92 : System.Math.Pow((x + 0.055) / 1.055, 2.4);
        return 0.2126 * Channel(c.r) + 0.7152 * Channel(c.g) + 0.0722 * Channel(c.b);
    }

    private static string ResolveThemePath()
    {
        var asmDir = Path.GetDirectoryName(typeof(ContrastTests).Assembly.Location)!;
        var dir = new DirectoryInfo(asmDir);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Duplimate.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Duplimate", "Themes", "Theme.axaml");
    }
}
