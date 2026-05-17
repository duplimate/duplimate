using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Duplimate.Views;

public partial class DestructiveConfirmWindow : Window
{
    private readonly string _confirm;
    private readonly StringComparison _cmp;
    private readonly bool _informational;
    private bool _closed;

    public DestructiveConfirmWindow() : this("Confirm", "", "", caseInsensitive: false) { }

    public DestructiveConfirmWindow(string title, string subtitle, string confirmString, bool caseInsensitive = false)
    {
        InitializeComponent();
        _confirm = confirmString ?? "";
        _cmp = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // Informational mode = no typing required. Triggered when the
        // caller passes an empty confirmString (used by code paths that
        // want to surface a "can't do that, here's why" message — e.g.
        // "destination is in use by a running backup"). Showing the
        // type-OK prompt for those was awful UX: the user had to acknowledge
        // a non-action by typing into a confirm box.
        _informational = string.IsNullOrEmpty(_confirm);

        TitleBlock.Text = title;
        SubtitleBlock.Text = subtitle;

        if (_informational)
        {
            ConfirmRule.IsVisible = false;
            ConfirmInputBlock.IsVisible = false;
            // Reframe the buttons: only "OK", styled as a ghost button
            // (nothing destructive happens). Hide the Cancel button —
            // the dialog is purely informational and there's no
            // alternative action to take.
            CancelButton.IsVisible = false;
            ProceedButton.Content = "OK";
            ProceedButton.Classes.Remove("danger");
            ProceedButton.Classes.Add("primary");
        }
        else
        {
            PromptLabel.Text = $"TYPE “{confirmString}” TO CONFIRM";
        }

        // Focus the right control on open: input box in confirm mode,
        // proceed button in info mode. Hooking Opened (rather than the
        // ctor) ensures the visual tree is realized.
        Opened += (_, _) =>
        {
            if (_informational) ProceedButton.Focus();
            else ConfirmInput.Focus();
        };

        // Keyboard shortcuts. Escape always cancels/closes (matches OS-wide
        // dialog convention). Enter submits — same path as clicking the
        // primary button.
        KeyDown += OnKeyDown;

        // Mark the window as closed so the red-flash timer's dispatcher
        // post can no-op instead of touching a defunct visual tree.
        Closed += (_, _) => _closed = true;
    }

    /// <summary>
    /// Convenience factory for informational "you can't do that right
    /// now" dialogs. No type-OK gate, just a single OK button. Returns
    /// a Task<bool> for symmetry with the destructive variant; the
    /// bool is always true (user acknowledged) or false (Escape).
    /// </summary>
    public static System.Threading.Tasks.Task<bool> ShowInfoAsync(
        Window owner, string title, string subtitle)
    {
        var dlg = new DestructiveConfirmWindow(title, subtitle, confirmString: "", caseInsensitive: true);
        return dlg.ShowDialog<bool>(owner);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(false);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            OnProceed(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnProceed(object? sender, RoutedEventArgs e)
    {
        if (_informational)
        {
            // No gate to clear — single-button acknowledgement.
            Close(true);
            return;
        }

        if (string.Equals(ConfirmInput.Text?.Trim(), _confirm, _cmp))
        {
            Close(true);
        }
        else
        {
            // Visual cue: tint input red briefly then reset. The
            // _closed guard prevents the dispatcher post from touching
            // ConfirmInput after the user dismissed the window mid-flash
            // (Esc, ✕). Without it Avalonia logs first-chance exceptions
            // in debugger sessions and risks a use-after-close on
            // future Avalonia releases.
            ConfirmInput.Foreground = Brushes.Red;
            ConfirmInput.Focus();
            System.Threading.Timer? t = null;
            t = new System.Threading.Timer(_ =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_closed) return;
                    try { ConfirmInput.ClearValue(TextBox.ForegroundProperty); } catch { }
                });
                t?.Dispose();
            }, null, 500, System.Threading.Timeout.Infinite);
        }
    }
}
