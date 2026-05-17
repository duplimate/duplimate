using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Duplimate.ViewModels;
using Duplimate.Views;
using Xunit;

namespace Duplimate.Tests.Layout;

/// <summary>
/// Regression guard for the subtitle-centering bug: any TextBlock styled
/// page-subtitle with a MaxWidth used to render centered in the parent
/// slot because Avalonia treats HorizontalAlignment=Stretch + MaxWidth as
/// "center". These tests fail loudly if that ever sneaks back in.
///
/// For each view: render at a fixed window size, find the page-title and
/// page-subtitle by class, and assert their Bounds.Left values match (both
/// flush-left against the parent StackPanel's left padding).
/// </summary>
public class PageHeaderAlignmentTests
{
    // Tolerance: 1 device pixel. Anything more than that is a real offset.
    private const double Tolerance = 1.0;

    [AvaloniaFact]
    public void BackupsView_TitleAndSubtitle_FlushLeft()
        => AssertTitleSubtitleFlushLeft(new BackupsView { DataContext = new BackupsViewModel() });

    [AvaloniaFact]
    public void DestinationsView_TitleAndSubtitle_FlushLeft()
        => AssertTitleSubtitleFlushLeft(new DestinationsView { DataContext = new DestinationsViewModel() });

    [AvaloniaFact]
    public void LogsView_TitleAndSubtitle_FlushLeft()
        => AssertTitleSubtitleFlushLeft(new LogsView { DataContext = new LogsViewModel() });

    [AvaloniaFact]
    public void RestoreView_TitleAndSubtitle_FlushLeft()
        => AssertTitleSubtitleFlushLeft(new RestoreView { DataContext = new RestoreViewModel() });

    [AvaloniaFact]
    public void SettingsView_TitleAndSubtitle_FlushLeft()
        => AssertTitleSubtitleFlushLeft(new SettingsView { DataContext = new SettingsViewModel() });

    // -------------------------------------------------------------------

    private static void AssertTitleSubtitleFlushLeft(Control view)
    {
        var window = new Window { Width = 1240, Height = 820, Content = view };
        window.Show();
        window.UpdateLayout();
        // Second pass settles deferred arrangement on templated controls.
        window.UpdateLayout();

        var title    = FindByClass(view, "page-title");
        var subtitle = FindByClass(view, "page-subtitle");

        Assert.NotNull(title);
        Assert.NotNull(subtitle);

        var titleScreen    = title!.TranslatePoint(new Point(0, 0), window)
            ?? throw new InvalidOperationException("page-title has no position (not in visual tree?)");
        var subtitleScreen = subtitle!.TranslatePoint(new Point(0, 0), window)
            ?? throw new InvalidOperationException("page-subtitle has no position (not in visual tree?)");

        Assert.True(
            Math.Abs(titleScreen.X - subtitleScreen.X) <= Tolerance,
            $"page-title X={titleScreen.X}, page-subtitle X={subtitleScreen.X}. " +
            $"They should be flush-left relative to each other (within {Tolerance} px). " +
            $"If this fails, a MaxWidth on page-subtitle is centering it because " +
            $"HorizontalAlignment is defaulting to Stretch instead of Left.");

        window.Close();
    }

    internal static TextBlock? FindByClass(Visual root, string className) =>
        root.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(t => t.Classes.Contains(className));
}
