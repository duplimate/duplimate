using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Duplimate.Views;

public partial class GeneratedPasswordWindow : Window
{
    private readonly string _password;

    // Parameterless ctor exists only for the XAML compiler / designer.
    public GeneratedPasswordWindow() : this("") { }

    public GeneratedPasswordWindow(string password)
    {
        _password = password ?? "";
        InitializeComponent();
        PasswordBox.Text = _password;
        // Copy to clipboard automatically on open — minimises the chance
        // the user closes the window without saving the password. The
        // result is intentionally fire-and-forget: the body of
        // CopyToClipboard catches its own exceptions, so awaiting the
        // task wouldn't change behaviour. The discard suppresses
        // CS4014 on the unawaited Task.
        Opened += (_, _) => _ = CopyToClipboard();

        // Escape and Enter both close — there's no destructive action
        // here, just an acknowledgment dialog. The password is already
        // saved to the secrets store and on the clipboard.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || e.Key == Key.Enter)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    private async void OnCopy(object? sender, RoutedEventArgs e) => await CopyToClipboard();
    private void OnDone(object? sender, RoutedEventArgs e) => Close();

    private async System.Threading.Tasks.Task CopyToClipboard()
    {
        try
        {
            var clip = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clip is not null) await clip.SetTextAsync(_password);
        }
        catch { /* clipboard inaccessible — user can still copy manually */ }
    }
}
