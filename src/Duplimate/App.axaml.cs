using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Duplimate.Models;
using Duplimate.Services;
using Duplimate.ViewModels;
using Duplimate.Views;

namespace Duplimate;

public partial class App : Application
{
    /// <summary>
    /// The currently-merged accent resource dictionary, kept as a field so
    /// <see cref="ApplyAccentFromConfig"/> can swap it out without removing
    /// the other merged dictionaries (Theme, Icons).
    /// </summary>
    private IResourceProvider? _currentAccent;

    /// <summary>
    /// Last-applied values, so the Config.Changed handler can skip the
    /// (relatively expensive) resource-dictionary swap when the user
    /// touched some unrelated setting and accent / theme didn't actually
    /// move. This also avoids the visual flicker caused by removing then
    /// re-adding the SAME merged dictionary.
    /// </summary>
    private AccentColor? _lastAppliedAccent;
    private AppTheme? _lastAppliedTheme;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        ServiceLocator.InitializeCore();

        // Only do UI-thread work when we're actually running as a desktop
        // app with a window. Under the headless test lifetime we still load
        // App.axaml's resources (so views render), but skip the live-update
        // Config.Changed subscription — otherwise a background-thread config
        // mutation during a test would try to touch Application.Resources
        // from the wrong thread and crash.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ApplyAccentFromConfig();
            ApplyThemeFromConfig();
            // Defer via Dispatcher.UIThread.Post: Config.Changed fires
            // synchronously from the call site that mutated config, which
            // is typically a binding setter on the Settings combobox.
            // Doing the resource-dictionary surgery synchronously inside
            // that setter can leave the visual tree in an inconsistent
            // state (DynamicResource lookups for accent brushes briefly
            // return null between the Remove and Add), and any exception
            // raised by a downstream subscriber cascades back into the
            // binding system as a hard error. Posting puts the work
            // after the current binding callback completes.
            ServiceLocator.Config.Changed += (_, __) => Dispatcher.UIThread.Post(() =>
            {
                try { ApplyAccentFromConfig(); }
                catch (Exception ex) { AppLogger.For<App>().Warning(ex, "Accent re-apply failed"); }
                try { ApplyThemeFromConfig(); }
                catch (Exception ex) { AppLogger.For<App>().Warning(ex, "Theme re-apply failed"); }
            });

            desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel() };
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            // Post-init startup: broken-install probe + deferred
            // housekeeping (stub extract, scheduler re-upsert,
            // missed-run check). Invoked HERE — not from Program.RunGui
            // — because both the install probe and the stub extract
            // resolve avares:// URIs, which need Avalonia's
            // IAssetLoader service. That service is registered by the
            // time this callback fires; calling them earlier silently
            // returned "not bundled" on debug builds where the
            // binaries are only present as embedded AvaloniaResources.
            Program.RunPostInitStartup();

            // If the secrets vault failed to decrypt at startup
            // (DPAPI on Windows / AES-GCM keyfile on Unix can't open
            // it on this machine/account), surface a one-shot dialog
            // so the user knows their saved tokens/passwords are
            // gone and they need to re-enter them. Without this the
            // first failed backup would be the user's only signal —
            // and by then the app may have already overwritten the
            // unreadable file.
            var secretsErr = ServiceLocator.Secrets.LoadErrorMessage;
            if (!string.IsNullOrEmpty(secretsErr))
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        // Informational acknowledgement — no typing gate.
                        await DestructiveConfirmWindow.ShowInfoAsync(
                            desktop.MainWindow!,
                            title: "Saved tokens couldn't be opened",
                            subtitle: secretsErr);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.For<App>().Warning(ex, "Failed to surface secrets-load warning dialog");
                    }
                });
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyThemeFromConfig()
    {
        var theme = ServiceLocator.Config.Current.Preferences.Theme;
        if (_lastAppliedTheme == theme) return;

        RequestedThemeVariant = theme switch
        {
            AppTheme.Dark   => ThemeVariant.Dark,
            AppTheme.System => ThemeVariant.Default,
            _               => ThemeVariant.Light,
        };
        _lastAppliedTheme = theme;
    }

    /// <summary>
    /// Swaps in the user's chosen accent palette. One dictionary owns
    /// the DM.Brush.Accent / AccentHover / AccentDeep / AccentTint brushes;
    /// switching replaces just that dictionary so we don't churn icons or
    /// the core theme.
    /// </summary>
    private void ApplyAccentFromConfig()
    {
        var accent = ServiceLocator.Config.Current.Preferences.Accent;
        if (_lastAppliedAccent == accent && _currentAccent is not null) return;

        var uri = accent switch
        {
            AccentColor.Emerald     => new Uri("avares://Duplimate/Themes/Accent_Emerald.axaml"),
            AccentColor.Plum        => new Uri("avares://Duplimate/Themes/Accent_Plum.axaml"),
            AccentColor.Graphite    => new Uri("avares://Duplimate/Themes/Accent_Graphite.axaml"),
            AccentColor.Terracotta  => new Uri("avares://Duplimate/Themes/Accent_Terracotta.axaml"),
            _                       => new Uri("avares://Duplimate/Themes/Accent_Ocean.axaml"),
        };

        var include = new ResourceInclude(uri) { Source = uri };

        // Add-then-remove (instead of remove-then-add) so DynamicResource
        // lookups for accent brushes never see a moment where neither
        // dictionary is present — Avalonia walks dictionaries in reverse
        // order, so the new one wins immediately and the old one becomes
        // unreachable before we drop it.
        Resources.MergedDictionaries.Add(include);
        if (_currentAccent is not null)
            Resources.MergedDictionaries.Remove(_currentAccent);
        _currentAccent = include;
        _lastAppliedAccent = accent;
    }
}
