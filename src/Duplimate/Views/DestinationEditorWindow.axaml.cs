using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Duplimate.Models;
using Duplimate.ViewModels;

namespace Duplimate.Views;

public partial class DestinationEditorWindow : Window
{
    private readonly DestinationEditorViewModel _vm;

    public DestinationEditorWindow() : this(new Destination(), true, simplifiedMode: false) { }

    public DestinationEditorWindow(Destination working, bool isNew)
        : this(working, isNew, simplifiedMode: false) { }

    /// <summary>
    /// Constructor with the simplified-mode flag. Easy-mode call sites
    /// (the onboarding wizard) pass <c>simplifiedMode: true</c>, which
    /// hides advanced fields like the Cloud subfolder (it always
    /// auto-fills cleanly, so first-time users don't need to make a
    /// decision about it). Advanced-mode call sites pass false (the
    /// default via the two-arg overload).
    /// </summary>
    public DestinationEditorWindow(Destination working, bool isNew, bool simplifiedMode)
    {
        _vm = new DestinationEditorViewModel(working, isNew, simplifiedMode);
        DataContext = _vm;
        InitializeComponent();

        // Escape cancels (matches OS dialog convention). Enter is not
        // bound globally because the auth-token input would otherwise
        // submit the form before the user finishes typing the password
        // / token if it sneaks past the focused element.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(null);
                e.Handled = true;
            }
        };

        // Dispose the VM on close — cancels any in-flight OAuth
        // token probe so duplicacy.exe spawned by a paste-into-token-
        // field doesn't keep running after the user closes the dialog.
        Closed += (_, _) =>
        {
            try { _vm.Dispose(); } catch { /* swallow shutdown errors */ }
        };
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        // async void exception trap.
        try
        {
            var result = _vm.Save();
            if (result is null) return;

            if (!string.IsNullOrEmpty(_vm.GeneratedStoragePassword))
            {
                var dlg = new GeneratedPasswordWindow(_vm.GeneratedStoragePassword!);
                await dlg.ShowDialog(this);
            }
            Close(result);
        }
        catch (Exception ex)
        {
            Duplimate.Services.AppLogger.For<DestinationEditorWindow>().Error(ex, "OnSave threw");
            _vm.SummaryError = "Save failed: " + ex.Message;
        }
    }

    /// <summary>
    /// "Change password…" button next to the locked storage-password
    /// field. Opens <see cref="ChangeStoragePasswordWindow"/> as a
    /// child of THIS window (so the modality stack stays clean — the
    /// editor itself is already a modal child of MainWindow). Pre-flight
    /// checks live on the VM (<see cref="DestinationEditorViewModel.CheckCanChangeStoragePassword"/>);
    /// the new password is persisted via
    /// <see cref="DestinationEditorViewModel.ApplyChangedStoragePassword"/>.
    /// </summary>
    private async void OnChangeStoragePassword(object? sender, RoutedEventArgs e)
    {
        try
        {
            var blockingReason = _vm.CheckCanChangeStoragePassword();
            if (blockingReason is not null)
            {
                await DestructiveConfirmWindow.ShowInfoAsync(this,
                    title: "Can't change password right now",
                    subtitle: blockingReason);
                return;
            }

            var oldPwd = _vm.GetCurrentStoragePassword();
            var dlg = new ChangeStoragePasswordWindow(_vm.Working, storageName: "default", oldPassword: oldPwd);
            var ok = await dlg.ShowDialog<bool>(this);
            if (ok && !string.IsNullOrEmpty(dlg.NewPassword))
            {
                _vm.ApplyChangedStoragePassword(dlg.NewPassword);
            }
        }
        catch (Exception ex)
        {
            Duplimate.Services.AppLogger.For<DestinationEditorWindow>().Error(ex, "OnChangeStoragePassword threw");
        }
    }

    /// <summary>
    /// Inline "Paste" button next to the OAuth token field. Reads the
    /// clipboard text and writes it to OAuthTokenInput, which triggers
    /// the auto-validate path. Saves the user a right-click → Paste
    /// dance during onboarding when the token they just copied from
    /// the helper page is the obvious next thing to put here.
    /// </summary>
    private async void OnPasteOAuthToken(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;
            var text = await clipboard.GetTextAsync();
            if (string.IsNullOrWhiteSpace(text)) return;
            // Trim — users routinely copy a trailing newline from the
            // helper page; leaving it in tanks the validate call.
            _vm.OAuthTokenInput = text.Trim();
            // Drop focus into the box so the user sees the value land.
            OAuthTokenBox.Focus();
        }
        catch { /* clipboard unavailable / denied — silent no-op */ }
    }
}
