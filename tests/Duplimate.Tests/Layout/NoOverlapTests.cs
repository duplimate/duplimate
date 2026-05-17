using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Duplimate.Models;
using Duplimate.ViewModels;
using Duplimate.Views;
using Xunit;

namespace Duplimate.Tests.Layout;

/// <summary>
/// Semantic layout assertion: within the same render, a Button must not
/// overlap a TextBlock that is a sibling (or cousin) of it inside the
/// same container. This catches the "button stomping descriptive copy"
/// class of bug — e.g. the destinations empty-state callout where a
/// long-line TextBlock (NoWrap) used to render under the CTA.
///
/// Unlike screenshot tests, this isn't fooled by a refreshed baseline
/// that still has the bug: it inspects real bounds on the visual tree.
/// </summary>
public class NoOverlapTests
{
    [AvaloniaFact]
    public void BackupEditor_DestinationsCallout_ButtonDoesNotOverlapText()
    {
        // The test is named "…DestinationsCallout…" — it specifically
        // exercises the empty-state callout that appears when the user
        // has no destinations yet. A prior test in the suite may have
        // populated Duplimate.Services.ServiceLocator.Config.Current.
        // Destinations (state is process-wide, parallelisation is
        // disabled). Wipe it here so this test reliably renders the
        // callout it's named for instead of the picker, which has a
        // different layout and a different overlap profile.
        Duplimate.Services.ServiceLocator.Config.Update(cfg => cfg.Destinations.Clear());

        var working = new Backup { Name = "test-backup" };
        var window = new BackupEditorWindow(working, isNew: true) { Width = 980, Height = 840 };
        window.Show();
        window.UpdateLayout();
        window.UpdateLayout();

        AssertNoButtonTextOverlap(window);

        window.Close();
    }

    [AvaloniaFact]
    public void DestinationEditor_NoButtonOverlapsText()
    {
        var working = new Destination();
        var window = new DestinationEditorWindow(working, isNew: true) { Width = 740, Height = 760 };
        window.Show();
        window.UpdateLayout();
        window.UpdateLayout();

        AssertNoButtonTextOverlap(window);

        window.Close();
    }

    // -------------------------------------------------------------------

    private static void AssertNoButtonTextOverlap(Window window)
    {
        var buttons = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Bounds.Width > 0 && b.Bounds.Height > 0 && IsVisible(b))
            .ToList();

        var textBlocks = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => !string.IsNullOrWhiteSpace(t.Text)
                        && t.Bounds.Width > 0 && t.Bounds.Height > 0
                        && IsVisible(t))
            .ToList();

        var failures = new List<string>();

        foreach (var button in buttons)
        {
            var bRect = TranslatedRect(button, window);
            var bClip = EffectiveClip(button, window);

            foreach (var text in textBlocks)
            {
                // Ignore text that's actually inside the button (button
                // labels etc.) — descendant text is intentionally
                // overlapping by design.
                if (text.GetVisualAncestors().Contains(button)) continue;
                if (button.GetVisualAncestors().Contains(text)) continue;

                var tRect = TranslatedRect(text, window);
                var tClip = EffectiveClip(text, window);

                // Only report overlap if BOTH elements are still visible
                // after their nearest ScrollViewer clip. An element whose
                // logical bounds sit below a ScrollViewer's viewport is
                // physically not on screen, even if the logical rect
                // numerically overlaps something else in window space.
                var bVisible = Intersects(bRect, bClip);
                var tVisible = Intersects(tRect, tClip);
                if (!bVisible || !tVisible) continue;

                if (Intersects(bRect, tRect))
                {
                    failures.Add(
                        $"Button «{LabelOf(button)}» at {bRect} overlaps TextBlock «{Truncate(text.Text!)}» at {tRect}.");
                }
            }
        }

        if (failures.Count > 0)
        {
            Assert.Fail("Layout overlap detected:\n  " + string.Join("\n  ", failures));
        }
    }

    /// <summary>
    /// The on-screen rect an element is clipped to. Walks up through
    /// ScrollViewer ancestors and intersects each viewport-in-window-space;
    /// if a control has no ScrollViewer ancestor the clip is simply the
    /// window bounds.
    /// </summary>
    private static Rect EffectiveClip(Visual element, Visual window)
    {
        var clip = new Rect(new Point(0, 0), ((Control)window).Bounds.Size);
        foreach (var ancestor in element.GetVisualAncestors().OfType<ScrollViewer>())
        {
            var topLeft = ancestor.TranslatePoint(new Point(0, 0), window) ?? new Point(0, 0);
            var viewportRect = new Rect(topLeft, ancestor.Viewport);
            clip = IntersectRects(clip, viewportRect);
        }
        return clip;
    }

    private static Rect IntersectRects(Rect a, Rect b)
    {
        var x1 = System.Math.Max(a.Left, b.Left);
        var y1 = System.Math.Max(a.Top, b.Top);
        var x2 = System.Math.Min(a.Right, b.Right);
        var y2 = System.Math.Min(a.Bottom, b.Bottom);
        if (x2 <= x1 || y2 <= y1) return new Rect(0, 0, 0, 0);
        return new Rect(x1, y1, x2 - x1, y2 - y1);
    }

    private static Rect TranslatedRect(Visual v, Visual root)
    {
        var topLeft = v.TranslatePoint(new Point(0, 0), root) ?? new Point(0, 0);
        return new Rect(topLeft, v.Bounds.Size);
    }

    private static bool Intersects(Rect a, Rect b)
    {
        // Two rectangles intersect if each axis overlaps. A tiny
        // 1-pixel kiss doesn't count as overlap.
        const double epsilon = 1.0;
        return a.Right - b.Left > epsilon
            && b.Right - a.Left > epsilon
            && a.Bottom - b.Top > epsilon
            && b.Bottom - a.Top > epsilon;
    }

    private static bool IsVisible(Visual v)
    {
        foreach (var ancestor in v.GetSelfAndVisualAncestors())
            if (ancestor is Visual vv && !vv.IsVisible) return false;
        return v.IsVisible;
    }

    private static string LabelOf(Button b) => b.Content?.ToString() ?? b.Name ?? "(unnamed)";
    private static string Truncate(string s) => s.Length <= 50 ? s : s[..50] + "…";
}
