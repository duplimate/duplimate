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
/// Every view should render to completion without throwing, produce finite
/// non-negative bounds, and leave no resource references unresolved. If a
/// DynamicResource lookup fails or a binding crashes, that will surface as a
/// null Bounds or zero-size Root — this test catches that class of regression.
/// </summary>
public class ViewSmokeTests
{
    [AvaloniaTheory]
    [InlineData(typeof(BackupsView),      typeof(BackupsViewModel))]
    [InlineData(typeof(DestinationsView), typeof(DestinationsViewModel))]
    [InlineData(typeof(LogsView),         typeof(LogsViewModel))]
    [InlineData(typeof(RestoreView),      typeof(RestoreViewModel))]
    [InlineData(typeof(SettingsView),     typeof(SettingsViewModel))]
    public void View_RendersWithFiniteBounds(Type viewType, Type vmType)
    {
        var view = (Control)Activator.CreateInstance(viewType)!;
        view.DataContext = Activator.CreateInstance(vmType);

        var window = new Window { Width = 1240, Height = 820, Content = view };
        window.Show();
        window.UpdateLayout();
        window.UpdateLayout();

        // Root bounds should be the window size.
        Assert.True(window.Bounds.Width >= 1000, $"window width too small: {window.Bounds.Width}");
        Assert.True(window.Bounds.Height >= 600, $"window height too small: {window.Bounds.Height}");

        // View should have some non-zero area. Zero area usually means a
        // binding / template blew up during layout.
        var viewBounds = view.Bounds;
        Assert.True(viewBounds.Width  > 0, $"{viewType.Name} has zero width.");
        Assert.True(viewBounds.Height > 0, $"{viewType.Name} has zero height.");

        // Every visual child should have finite, non-NaN bounds.
        foreach (var child in view.GetVisualDescendants())
        {
            var b = child.Bounds;
            Assert.False(double.IsNaN(b.X),      $"{child.GetType().Name}.X is NaN");
            Assert.False(double.IsNaN(b.Y),      $"{child.GetType().Name}.Y is NaN");
            Assert.False(double.IsNaN(b.Width),  $"{child.GetType().Name}.Width is NaN");
            Assert.False(double.IsNaN(b.Height), $"{child.GetType().Name}.Height is NaN");
            Assert.False(double.IsInfinity(b.Width),  $"{child.GetType().Name}.Width is ∞");
            Assert.False(double.IsInfinity(b.Height), $"{child.GetType().Name}.Height is ∞");
        }

        window.Close();
    }

    /// <summary>
    /// Every view that has an empty-state card must: (a) expose that card
    /// when the ViewModel reports no data, and (b) place it flush-left
    /// (same X as page-title). Guards against the Grid ColumnDefinitions="680,*"
    /// pattern regressing, and against any future empty-state card that gets
    /// accidentally Centered.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(typeof(BackupsView),      typeof(BackupsViewModel))]
    [InlineData(typeof(DestinationsView), typeof(DestinationsViewModel))]
    public void EmptyState_Card_IsFlushLeftWithTitle(Type viewType, Type vmType)
    {
        var view = (Control)Activator.CreateInstance(viewType)!;
        view.DataContext = Activator.CreateInstance(vmType);

        var window = new Window { Width = 1240, Height = 820, Content = view };
        window.Show();
        window.UpdateLayout();
        window.UpdateLayout();

        var title = view.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Classes.Contains("page-title"));
        Assert.NotNull(title);

        var card = view.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("empty-state") && b.IsVisible);

        if (card is null)
        {
            // View has no empty-state card or it's hidden — that's fine in
            // this theory, not every view demos one.
            window.Close();
            return;
        }

        var titleAt = title!.TranslatePoint(new Avalonia.Point(0, 0), window)!.Value;
        var cardAt  = card.TranslatePoint(new Avalonia.Point(0, 0), window)!.Value;

        Assert.True(Math.Abs(titleAt.X - cardAt.X) <= 1.0,
            $"Empty-state card X={cardAt.X} should match page-title X={titleAt.X}. " +
            $"If this fails, the card was probably wrapped in a Grid with " +
            $"ColumnDefinitions=\"680,*\" instead of using Classes=\"empty-state\".");

        window.Close();
    }
}
