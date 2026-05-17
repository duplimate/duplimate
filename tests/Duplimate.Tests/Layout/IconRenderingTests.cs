using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Duplimate.Services;
using Duplimate.ViewModels;
using Duplimate.Views;
using Xunit;

namespace Duplimate.Tests.Layout;

/// <summary>
/// Regression guard for the Lucide-stroke-vs-fill footgun: every Lucide
/// icon in Icons.axaml is stroke-based (e.g. "M12 5v14M5 12h14" for Plus).
/// If Avalonia's default PathIcon template is left in place (Fill-only),
/// those icons render as nothing — their bounds are still the reserved
/// 14×14 but no pixels show.
///
/// These tests render each view, look at every PathIcon inside it, and
/// assert that the PathIcon's template is styled to stroke (via our
/// overridden template). We inspect the rendered Path child's properties
/// directly rather than trying to rasterize and count pixels — headless
/// Avalonia doesn't give us reliable pixel output, but visual-tree
/// introspection is deterministic.
/// </summary>
public class IconRenderingTests
{
    [AvaloniaTheory]
    [InlineData(typeof(BackupsView),      typeof(BackupsViewModel))]
    [InlineData(typeof(DestinationsView), typeof(DestinationsViewModel))]
    [InlineData(typeof(LogsView),         typeof(LogsViewModel))]
    [InlineData(typeof(RestoreView),      typeof(RestoreViewModel))]
    [InlineData(typeof(SettingsView),     typeof(SettingsViewModel))]
    public void AllPathIcons_InView_RenderWithVisibleStroke(Type viewType, Type vmType)
    {
        // Empty the shared config so we're rendering the empty-state surface
        // — that's where the biggest icons (empty-state-glyph) live, and
        // populated state would hide them. Also guards against state
        // pollution from other tests in the same xUnit process.
        ServiceLocator.Config.Update(cfg => { cfg.Backups.Clear(); cfg.Destinations.Clear(); });

        var view = (Control)Activator.CreateInstance(viewType)!;
        view.DataContext = Activator.CreateInstance(vmType);

        var window = new Window { Width = 1240, Height = 820, Content = view };
        window.Show();
        window.UpdateLayout();
        window.UpdateLayout();

        // Only assert against ICONS THAT ARE ACTUALLY RENDERED. PathIcons
        // inside an IsVisible=False ancestor (e.g. the BackupsView health
        // banner when no backups have run yet) live in the visual tree
        // but never have their inner Path materialised — pathShape is
        // null and bounds are 0×0. That's correct behaviour, not a
        // template regression. Skip them so the regression check stays
        // focused on actually-rendered icons.
        var icons = view.GetVisualDescendants().OfType<PathIcon>()
            .Where(IsRenderedInTree)
            .ToList();
        foreach (var icon in icons)
        {
            // The template is expected to wrap a Path with Stroke bound to
            // Foreground. If someone swapped the template back to the
            // Fluent default (Fill-only), pathShape.Stroke would be null
            // and stroke-only Lucide geometries would render invisible.
            var pathShape = icon.GetVisualDescendants().OfType<Path>().FirstOrDefault();
            Assert.NotNull(pathShape);
            Assert.True(pathShape!.Stroke is not null,
                $"PathIcon in {viewType.Name} has no Stroke in its template. " +
                $"Lucide icons are stroke-only — without a Stroke they render invisible. " +
                $"Check the PathIcon style override in Controls.axaml.");
            Assert.True(pathShape.StrokeThickness > 0,
                $"PathIcon in {viewType.Name} has StrokeThickness=0. " +
                $"Icons will render invisible.");
            Assert.True(icon.Bounds.Width > 0 && icon.Bounds.Height > 0,
                $"PathIcon in {viewType.Name} has zero-size bounds " +
                $"({icon.Bounds.Width}×{icon.Bounds.Height}).");
        }

        window.Close();
    }

    /// <summary>
    /// True iff every ancestor (and the icon itself) is IsVisible=true.
    /// Walks the visual tree up to the window root. PathIcons whose
    /// containing element is hidden materialise into the tree but their
    /// inner Path child is never realised — for the test's purpose
    /// they're effectively absent and shouldn't be asserted against.
    /// </summary>
    private static bool IsRenderedInTree(PathIcon icon)
    {
        Visual? cursor = icon;
        while (cursor is not null)
        {
            if (cursor is Control c && !c.IsVisible) return false;
            cursor = cursor.GetVisualParent();
        }
        return true;
    }
}
