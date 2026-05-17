using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Xunit;
using Xunit.Abstractions;

namespace Duplimate.Tests.Screenshots;

/// <summary>
/// Diagnostic + screenshot harness for the warning-banner
/// `attention-pill`. The user has reported (multiple times) that the
/// glyph + text colours don't follow the pill's `*LightFg` brush —
/// "black on hover", "glyph wrong colour". This test instantiates
/// every tone × state combo, introspects the EFFECTIVE
/// <see cref="TemplatedControl.Foreground"/> on each PathIcon /
/// TextBlock descendant, and asserts the value matches the
/// `*LightFg` brush the pill style sets. Failure prints the actual
/// brush so we can pinpoint which selector is being beaten.
///
/// Also dumps PNGs to artifacts/screenshots so a human can confirm
/// the visual.
/// </summary>
public class AttentionPillScreenshotTests
{
    private static readonly string OutputDir = ResolveOutputDir();
    private readonly ITestOutputHelper _out;
    public AttentionPillScreenshotTests(ITestOutputHelper output) => _out = output;

    [AvaloniaFact]
    public void Shot_AttentionPill_LightMode_AllTones()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        RenderAndAssert("attention-pill-light.png", lightMode: true);
    }

    [AvaloniaFact]
    public void Shot_AttentionPill_DarkMode_AllTones()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        RenderAndAssert("attention-pill-dark.png", lightMode: false);
    }

    [AvaloniaFact]
    public void Shot_ActionButtons_LightMode()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        RenderActionButtonsAndAssert("action-buttons-light.png", lightMode: true);
    }

    [AvaloniaFact]
    public void Shot_ActionButtons_DarkMode()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        RenderActionButtonsAndAssert("action-buttons-dark.png", lightMode: false);
    }

    /// <summary>
    /// Renders Restore files / Delete / Prune / Stop buttons (the
    /// Mantine-light variants used in the BackupCard action shelf)
    /// with their actual PathIcon-containing content. Catches the
    /// "icon inside the button stays black because PathIcon doesn't
    /// inherit Foreground from its parent Button" regression class.
    /// </summary>
    private void RenderActionButtonsAndAssert(string fileName, bool lightMode)
    {
        var bgColor = lightMode
            ? Color.FromRgb(0xF3, 0xEF, 0xE6)   // CardBandTint (light)
            : Color.FromRgb(0x23, 0x23, 0x23);  // CardBandTint (dark)

        var window = new Window
        {
            Title = "Action buttons audit",
            Width = 1100,
            Height = 360,
            Background = new SolidColorBrush(bgColor),
            SystemDecorations = SystemDecorations.None,
        };

        var grid = new Grid
        {
            Margin = new Thickness(30),
            VerticalAlignment = VerticalAlignment.Top,
            ColumnDefinitions = new ColumnDefinitions("160,30,*,30,*"),
            // 5 button rows at indices 2, 4, 6, 8, 10 — RowDefinitions
            // needs 11 entries (header row + 5*(spacer+row)).
            RowDefinitions = new RowDefinitions("auto,16,auto,16,auto,16,auto,16,auto,16,auto"),
        };

        AddHeader(grid, 0, "Class", lightMode);
        AddHeader(grid, 2, "Resting", lightMode);
        AddHeader(grid, 4, ":pointerover", lightMode);

        int row = 2;
        foreach (var (cls, label) in new[]
                 {
                     ("restore-action",      "Restore files"),
                     ("row-action-danger",   "Delete"),
                     ("row-action-warn",     "Prune"),
                     ("row-action-critical", "Erase"),
                     ("danger",              "Stop"),
                 })
        {
            AddActionRow(grid, row, cls, label, lightMode);
            row += 2;
        }

        window.Content = grid;
        window.Show();
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        _out.WriteLine($"=== {fileName} ({(lightMode ? "Light" : "Dark")} theme) ===");
        foreach (var btn in window.GetVisualDescendants().OfType<Button>())
        {
            var cls = btn.Classes.FirstOrDefault(c =>
                c is "restore-action" or "row-action-danger" or "row-action-warn"
                  or "row-action-critical" or "danger");
            if (cls is null) continue;
            var hover = btn.Classes.Contains(":pointerover");
            _out.WriteLine($"  [{cls,-18} hover={hover}]");
            _out.WriteLine($"    Button.Foreground   = {Describe(btn.Foreground)}");
            foreach (var pi in btn.GetVisualDescendants().OfType<PathIcon>())
                _out.WriteLine($"    PathIcon.Foreground = {Describe(pi.Foreground)}");
            foreach (var tb in btn.GetVisualDescendants().OfType<TextBlock>())
                _out.WriteLine($"    TextBlock '{tb.Text}'.Foreground = {Describe(tb.Foreground)}");
        }

        Directory.CreateDirectory(OutputDir);
        var path = Path.Combine(OutputDir, fileName);
        var size = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(window.Bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(window.Bounds.Height)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(path);
        _out.WriteLine($"saved: {path}");
        window.Close();
    }

    private static Button BuildActionButton(string cls, string label, bool hover)
    {
        // Simple play-icon-ish geometry to stand in for Icon.Restore /
        // Icon.Trash / etc. Colour matters here, not shape.
        var glyph = new PathIcon
        {
            Width = 13, Height = 13,
            Data = StreamGeometry.Parse("M5 3l14 9-14 9z"),
        };

        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        stack.Children.Add(glyph);
        stack.Children.Add(text);

        var btn = new Button
        {
            Content = stack,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        btn.Classes.Add(cls);
        if (hover)
            btn.Initialized += (_, _) =>
                ((IPseudoClasses)btn.Classes).Set(":pointerover", true);
        return btn;
    }

    private static void AddActionRow(Grid grid, int row, string cls, string label, bool lightMode)
    {
        var labelBlock = new TextBlock
        {
            Text = cls,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(lightMode
                ? Color.FromRgb(0x1A, 0x19, 0x15)
                : Color.FromRgb(0xF2, 0xF1, 0xEE)),
        };
        Grid.SetRow(labelBlock, row);
        Grid.SetColumn(labelBlock, 0);
        grid.Children.Add(labelBlock);

        var resting = BuildActionButton(cls, label, hover: false);
        Grid.SetRow(resting, row);
        Grid.SetColumn(resting, 2);
        grid.Children.Add(resting);

        var hover = BuildActionButton(cls, label, hover: true);
        Grid.SetRow(hover, row);
        Grid.SetColumn(hover, 4);
        grid.Children.Add(hover);
    }

    private void RenderAndAssert(string fileName, bool lightMode)
    {
        var bgColor = lightMode
            ? Color.FromRgb(0xFB, 0xF8, 0xF2)   // cream BgSurface (light)
            : Color.FromRgb(0x1C, 0x1C, 0x1C);  // charcoal BgSurface (dark)

        var window = new Window
        {
            Title = "Attention pill audit",
            Width = 900,
            Height = 360,
            Background = new SolidColorBrush(bgColor),
            SystemDecorations = SystemDecorations.None,
        };

        var grid = new Grid
        {
            Margin = new Thickness(30),
            VerticalAlignment = VerticalAlignment.Top,
            ColumnDefinitions = new ColumnDefinitions("90,30,*,30,*"),
            RowDefinitions = new RowDefinitions("auto,16,auto,16,auto,16,auto,16,auto"),
        };

        AddHeader(grid, 0, "Tone", lightMode);
        AddHeader(grid, 2, "Resting", lightMode);
        AddHeader(grid, 4, ":pointerover", lightMode);

        int row = 2;
        foreach (var tone in new[] { "warn", "skipped", "fail", "idle" })
        {
            AddRow(grid, row, tone, lightMode);
            row += 2;
        }

        window.Content = grid;
        window.Show();
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // Introspect: walk every Button.attention-pill and print the
        // effective Foreground of every PathIcon + TextBlock descendant.
        // This is the smoking gun for "glyph wrong colour" — if the
        // resolved brush isn't *LightFg the selector lost.
        _out.WriteLine($"=== {fileName} ({(lightMode ? "Light" : "Dark")} theme) ===");
        foreach (var btn in window.GetVisualDescendants().OfType<Button>()
                     .Where(b => b.Classes.Contains("attention-pill")))
        {
            var toneClass = btn.Classes.FirstOrDefault(c =>
                c is "warn" or "skipped" or "fail" or "idle") ?? "(?)";
            var hover = btn.Classes.Contains(":pointerover");

            _out.WriteLine($"  pill[{toneClass,-8} hover={hover}]");
            _out.WriteLine($"    Button.Foreground   = {Describe(btn.Foreground)}");

            foreach (var pi in btn.GetVisualDescendants().OfType<PathIcon>())
            {
                var piClass = pi.Classes.FirstOrDefault(c =>
                    c is "status-icon" or "attention-pill-chevron") ?? "(?)";
                _out.WriteLine($"    PathIcon[{piClass,-22}].Foreground = {Describe(pi.Foreground)}");
            }
            foreach (var tb in btn.GetVisualDescendants().OfType<TextBlock>())
            {
                _out.WriteLine($"    TextBlock '{tb.Text}'.Foreground   = {Describe(tb.Foreground)}");
            }
        }

        Directory.CreateDirectory(OutputDir);
        var path = Path.Combine(OutputDir, fileName);
        var size = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(window.Bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(window.Bounds.Height)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(path);
        _out.WriteLine($"saved: {path}");
        window.Close();
    }

    private static string Describe(IBrush? brush) => brush switch
    {
        null => "null",
        ISolidColorBrush s => $"#{s.Color.A:X2}{s.Color.R:X2}{s.Color.G:X2}{s.Color.B:X2}",
        _ => brush.GetType().Name,
    };

    private static Button BuildPill(string tone, bool hover)
    {
        // Match the BackupsView.axaml structure verbatim.
        var statusIcon = new PathIcon
        {
            Width = 13, Height = 13,
            // Stub geometry — colour matters here, not shape.
            Data = StreamGeometry.Parse("M0,0 L1,1"),
        };
        statusIcon.Classes.Add("status-icon");
        statusIcon.Classes.Add(tone);

        var label = new TextBlock
        {
            Text = $"backup-{tone}",
            FontSize = 12, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 240, TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var chevron = new PathIcon
        {
            Width = 11, Height = 11,
            Data = StreamGeometry.Parse("M9 6l6 6-6 6"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        chevron.Classes.Add("attention-pill-chevron");

        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(statusIcon);
        stack.Children.Add(label);
        stack.Children.Add(chevron);

        var btn = new Button
        {
            Content = stack,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 5, 8, 5),
        };
        btn.Classes.Add("attention-pill");
        btn.Classes.Add(tone);
        if (hover)
            btn.Initialized += (_, _) =>
                ((IPseudoClasses)btn.Classes).Set(":pointerover", true);
        return btn;
    }

    private static void AddHeader(Grid grid, int col, string text, bool lightMode)
    {
        var t = new TextBlock
        {
            Text = text,
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Foreground = new SolidColorBrush(lightMode
                ? Color.FromRgb(0x4A, 0x47, 0x40)
                : Color.FromRgb(0xBF, 0xBB, 0xB2)),
        };
        Grid.SetRow(t, 0);
        Grid.SetColumn(t, col);
        grid.Children.Add(t);
    }

    private static void AddRow(Grid grid, int row, string tone, bool lightMode)
    {
        var labelBlock = new TextBlock
        {
            Text = tone,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(lightMode
                ? Color.FromRgb(0x1A, 0x19, 0x15)
                : Color.FromRgb(0xF2, 0xF1, 0xEE)),
        };
        Grid.SetRow(labelBlock, row);
        Grid.SetColumn(labelBlock, 0);
        grid.Children.Add(labelBlock);

        var resting = BuildPill(tone, hover: false);
        Grid.SetRow(resting, row);
        Grid.SetColumn(resting, 2);
        grid.Children.Add(resting);

        var hover = BuildPill(tone, hover: true);
        Grid.SetRow(hover, row);
        Grid.SetColumn(hover, 4);
        grid.Children.Add(hover);
    }

    private static string ResolveOutputDir()
    {
        var asmDir = Path.GetDirectoryName(typeof(AttentionPillScreenshotTests).Assembly.Location)!;
        var dir = new DirectoryInfo(asmDir);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Duplimate.Tests.csproj")))
            dir = dir.Parent;
        var root = dir?.FullName ?? asmDir;
        return Path.Combine(root, "artifacts", "screenshots");
    }
}
