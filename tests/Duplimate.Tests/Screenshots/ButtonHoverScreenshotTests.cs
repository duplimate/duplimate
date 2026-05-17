using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Xunit;
using Xunit.Abstractions;

namespace Duplimate.Tests.Screenshots;

/// <summary>
/// Renders the various button styles in their resting AND hover
/// (`:pointerover`) states so a human can audit whether the hover
/// effect is calm/elegant or "weird" (the user has flagged hover
/// styling six times and asked for a screenshot-driven audit).
///
/// Forces the hover pseudo-class via reflection on
/// <see cref="StyledElement.Classes"/>'s underlying
/// <see cref="IPseudoClasses"/> so we don't need a real cursor.
/// </summary>
public class ButtonHoverScreenshotTests
{
    private static readonly string OutputDir = ResolveOutputDir();
    private readonly ITestOutputHelper _out;
    public ButtonHoverScreenshotTests(ITestOutputHelper output) => _out = output;

    [AvaloniaFact]
    public void Shot_AllButtonStyles_RestingAndHover()
    {
        // Each row pairs a resting state with the same button forced
        // into :pointerover. Side-by-side so a reviewer can see the
        // exact hover delta for every class. The window background is
        // the cream BgBase so any halo or hover-bleed past the
        // CornerRadius pops visually.
        // Cream BgSurface background to MATCH where buttons actually
        // live in the app — earlier audits used a white window which
        // hid hover-edge issues that only show on cream. The colour
        // is the literal value of DM.Brush.BgSurface (light theme).
        var window = new Window
        {
            Title = "Button hover audit",
            Width = 1080,
            Height = 720,
            Background = new SolidColorBrush(Color.FromRgb(0xFB, 0xF8, 0xF2)),
            SystemDecorations = SystemDecorations.None,
        };

        var grid = new Grid
        {
            Margin = new Thickness(40),
            VerticalAlignment = VerticalAlignment.Top,
            ColumnDefinitions = new ColumnDefinitions("220,40,*,40,*"),
            RowDefinitions = new RowDefinitions("auto,28,auto,28,auto,28,auto,28,auto,28,auto,28,auto"),
        };

        AddHeader(grid, 0, "Class");
        AddHeader(grid, 2, "Resting");
        AddHeader(grid, 4, ":pointerover");

        int row = 2;
        AddPair(grid, row, "primary",            MakeButton("primary",            "Save"));
        row += 2;
        AddPair(grid, row, "ghost",              MakeButton("ghost",              "Edit"));
        row += 2;
        AddPair(grid, row, "danger",             MakeButton("danger",             "Stop"));
        row += 2;
        AddPair(grid, row, "row-action",         MakeButton("row-action",         "Edit"));
        row += 2;
        AddPair(grid, row, "row-action-danger",  MakeButton("row-action-danger",  "Remove"));
        row += 2;
        AddPair(grid, row, "row-action-warn",    MakeButton("row-action-warn",    "Prune"));
        row += 2;
        AddPair(grid, row, "restore-action",     MakeButton("restore-action",     "Restore files"));

        window.Content = grid;
        window.Show();
        window.UpdateLayout();
        window.UpdateLayout();
        // Give Avalonia's render tick a chance to materialise pseudo-class
        // visuals before we snapshot — without this, the captured bitmap
        // can show the resting state because the :pointerover pulse hasn't
        // applied through the visual tree yet.
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        // Diagnostic: walk every button + its visual tree, print the
        // ACTUAL Background brush of the rendered Border / ContentPresenter
        // so we can pinpoint which element is painting the wrong colour.
        foreach (var btn in window.GetVisualDescendants().OfType<Button>())
        {
            var cls = btn.Classes.FirstOrDefault(c => !c.StartsWith(":")) ?? "(unknown)";
            var hover = btn.Classes.Contains(":pointerover");
            var border = btn.GetVisualDescendants().OfType<Border>().FirstOrDefault();
            var cp = btn.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault();
            _out.WriteLine(
                $"class={cls,-22} hover={hover} btn.Bg={Describe(btn.Background)}, " +
                $"borderBg={Describe(border?.Background)}, cpBg={Describe(cp?.Background)}");
        }

        Directory.CreateDirectory(OutputDir);
        var path = Path.Combine(OutputDir, "button-hover-audit.png");
        var size = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(window.Bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(window.Bounds.Height)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(path);
        window.Close();
    }

    private static string Describe(IBrush? brush) => brush switch
    {
        null => "null",
        ISolidColorBrush s => $"#{s.Color.A:X2}{s.Color.R:X2}{s.Color.G:X2}{s.Color.B:X2}",
        _ => brush.GetType().Name,
    };

    private static Button MakeButton(string cls, string content)
    {
        var b = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Left };
        b.Classes.Add(cls);
        return b;
    }

    private static void AddHeader(Grid grid, int col, string text)
    {
        var t = new TextBlock
        {
            Text = text,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x47, 0x40)),
        };
        Grid.SetRow(t, 0);
        Grid.SetColumn(t, col);
        grid.Children.Add(t);
    }

    private static void AddPair(Grid grid, int row, string label, Button proto)
    {
        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x19, 0x15)),
        };
        Grid.SetRow(labelBlock, row);
        Grid.SetColumn(labelBlock, 0);
        grid.Children.Add(labelBlock);

        // Resting: clone of the prototype.
        var resting = MakeButton(proto.Classes[0], (string)proto.Content!);
        Grid.SetRow(resting, row);
        Grid.SetColumn(resting, 2);
        grid.Children.Add(resting);

        // Hover: same button with :pointerover forced.
        var hover = MakeButton(proto.Classes[0], (string)proto.Content!);
        Grid.SetRow(hover, row);
        Grid.SetColumn(hover, 4);
        grid.Children.Add(hover);

        // After layout, force the pseudo-class. The framework removes
        // it on layout passes if we set it before — Opened on the
        // window is the right hook.
        hover.Initialized += (_, _) => ForcePointerOver(hover);
    }

    /// <summary>Force the <c>:pointerover</c> pseudo-class on a control.
    /// Avalonia exposes pseudo-class management through
    /// <see cref="IPseudoClasses"/> (the same IClasses that holds normal
    /// classes also implements it). The runtime usually flips the flag
    /// from cursor input; here we set it explicitly so a screenshot
    /// captures the hover state.</summary>
    private static void ForcePointerOver(Control c)
    {
        var pseudo = (IPseudoClasses)c.Classes;
        pseudo.Set(":pointerover", true);
    }


    private static string ResolveOutputDir()
    {
        var asmDir = Path.GetDirectoryName(typeof(ButtonHoverScreenshotTests).Assembly.Location)!;
        var dir = new DirectoryInfo(asmDir);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Duplimate.Tests.csproj")))
            dir = dir.Parent;
        var root = dir?.FullName ?? asmDir;
        return Path.Combine(root, "artifacts", "screenshots");
    }
}
