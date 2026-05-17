using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Duplimate.Views;
using Xunit;

namespace Duplimate.Tests.ViewModels;

/// <summary>
/// Tests the type-to-confirm gate that sits in front of every destructive
/// action: delete backup, delete destination, erase storage, prune to
/// latest, and the restore-OVERWRITE flow. All of these share this single
/// window, so if its confirmation logic regresses every destructive path
/// breaks at once.
///
/// We can't call <c>ShowDialog</c> headlessly in a useful way (the task
/// only completes when the window closes on the UI thread). Instead we
/// instantiate the window directly and poke the TextBox + button handler
/// through the visual tree — the same control-flow the user's keyboard
/// takes.
/// </summary>
public class DestructiveConfirmTests
{
    [AvaloniaFact]
    public void WrongText_DoesNotClose()
    {
        var (window, input, proceed) = Build("local_c", caseInsensitive: true);
        input.Text = "local-c"; // hyphen, not underscore — wrong
        InvokeClick(proceed);

        Assert.True(window.IsVisible, "Window should NOT close on wrong confirmation text.");
        Assert.Equal(Avalonia.Media.Brushes.Red, input.Foreground);

        window.Close();
    }

    [AvaloniaFact]
    public void ExactMatch_Closes()
    {
        var (window, input, proceed) = Build("local_c", caseInsensitive: false);
        input.Text = "local_c";
        InvokeClick(proceed);
        Assert.False(window.IsVisible, "Window should close on exact match.");
    }

    [AvaloniaFact]
    public void CaseInsensitive_ClosesOnDifferentCase()
    {
        var (window, input, proceed) = Build("OVERWRITE", caseInsensitive: true);
        input.Text = "overwrite";
        InvokeClick(proceed);
        Assert.False(window.IsVisible, "Case-insensitive match should close the window.");
    }

    [AvaloniaFact]
    public void CaseSensitive_DoesNotCloseOnDifferentCase()
    {
        var (window, input, proceed) = Build("OVERWRITE", caseInsensitive: false);
        input.Text = "overwrite";
        InvokeClick(proceed);
        Assert.True(window.IsVisible, "Case-sensitive mode should reject a case mismatch.");
        window.Close();
    }

    [AvaloniaFact]
    public void WhitespaceAroundText_IsTrimmed()
    {
        var (window, input, proceed) = Build("local_c", caseInsensitive: true);
        input.Text = "  local_c  ";
        InvokeClick(proceed);
        Assert.False(window.IsVisible, "Leading/trailing whitespace should be trimmed.");
    }

    // ----- plumbing -----

    private static (DestructiveConfirmWindow window, TextBox input, Button proceed) Build(
        string confirmString, bool caseInsensitive)
    {
        var w = new DestructiveConfirmWindow(
            title: "Confirm",
            subtitle: "Type the word to proceed.",
            confirmString: confirmString,
            caseInsensitive: caseInsensitive);

        // Show() is required for the window to realize its template. We
        // don't ShowDialog because that would block the test thread.
        w.Show();
        w.UpdateLayout();

        var input = w.FindControl<TextBox>("ConfirmInput")!;
        var proceed = w.FindControl<Button>("ProceedButton")!;
        return (w, input, proceed);
    }

    /// <summary>
    /// Invokes the button's click handler the same way a mouse-press
    /// would, via the bubbling ClickEvent. Avalonia's <c>Button.OnClick</c>
    /// isn't public, but raising the routed event runs the same code
    /// path (including the Click handler wired up in XAML).
    /// </summary>
    private static void InvokeClick(Button button)
    {
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }
}
