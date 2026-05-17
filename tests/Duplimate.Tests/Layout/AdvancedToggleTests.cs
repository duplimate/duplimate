using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Duplimate.Models;
using Duplimate.Views;
using Xunit;

namespace Duplimate.Tests.Layout;

/// <summary>
/// Regression guard for the "can't scroll to bottom of BackupEditor" bug.
/// The Advanced toggle at the bottom of the Options card must:
///   (1) render with non-zero bounds,
///   (2) live INSIDE the ScrollViewer that contains the tab content, and
///   (3) sit within that ScrollViewer's measured content extent so the
///       user can actually scroll to it.
///
/// The earlier pass only checked (1). That wasn't enough — the
/// ToggleButton/CheckBox versions of the toggle were measured fine in
/// the headless harness but still invisible at runtime, because in the
/// live window the ScrollViewer's Extent stopped above them. The
/// stricter assertion below would have caught that.
/// </summary>
public class AdvancedToggleTests
{
    [AvaloniaFact]
    public void BackupEditor_AdvancedToggle_IsInsideScrollViewerExtent()
    {
        var window = new BackupEditorWindow(new Backup(), isNew: true)
        {
            Width = 980, Height = 840
        };
        window.Show();
        window.UpdateLayout();
        window.UpdateLayout();

        var toggle = window.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => b.Name == "AdvancedToggleButton");

        Assert.NotNull(toggle);
        AssertToggleReachableByScrolling(toggle!, window);

        window.Close();
    }

    [AvaloniaFact]
    public void DestinationEditor_OptionsToggle_IsInsideScrollViewerExtent()
    {
        var window = new DestinationEditorWindow(new Destination(), isNew: true)
        {
            Width = 740, Height = 760
        };
        window.Show();
        window.UpdateLayout();
        window.UpdateLayout();

        var toggle = window.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => b.Name == "OptionsToggleButton");

        Assert.NotNull(toggle);
        AssertToggleReachableByScrolling(toggle!, window);

        window.Close();
    }

    /// <summary>
    /// Walks up from the toggle to the nearest ScrollViewer ancestor and
    /// asserts that the toggle's bottom edge is within that ScrollViewer's
    /// Extent. If Extent.Height is less than the toggle's relative bottom,
    /// the user cannot scroll to the toggle — that's the bug.
    /// </summary>
    private static void AssertToggleReachableByScrolling(Control toggle, Visual root)
    {
        Assert.True(toggle.Bounds.Height > 0,
            $"Toggle rendered with zero height ({toggle.Bounds}). " +
            "The ScrollViewer won't measure it into its scrollable area.");

        var scroller = toggle.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        Assert.NotNull(scroller);

        // Toggle bottom relative to the ScrollViewer's content presenter.
        var togglePos = toggle.TranslatePoint(new Point(0, 0), scroller!) ?? new Point(0, 0);
        var toggleBottom = togglePos.Y + toggle.Bounds.Height;

        // ScrollViewer.Extent is the full measured content size (may exceed
        // Viewport; the difference is what's scrollable). If Extent.Height
        // is smaller than the toggle's bottom, the ScrollViewer literally
        // doesn't know the toggle is there — user can't scroll to reach it.
        Assert.True(
            scroller!.Extent.Height + 1 >= toggleBottom,
            $"Toggle bottom is at Y={toggleBottom:F0} relative to its " +
            $"ScrollViewer, but the ScrollViewer's Extent.Height is only " +
            $"{scroller.Extent.Height:F0}. The user cannot scroll to it. " +
            "This is exactly the regression we're guarding against — the " +
            "ScrollViewer is measuring the toggle as zero-height or " +
            "otherwise stopping its extent before the toggle.");
    }
}
