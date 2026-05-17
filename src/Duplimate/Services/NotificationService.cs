using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Duplimate.Models;
using Duplimate.Services.Platform;
using Serilog;

namespace Duplimate.Services;

public enum NotificationSeverity { Info, Success, Warning, Failure }

/// <summary>
/// User-facing alerts. Three channels, used together according to severity and user preference:
///
///   1. Windows toast (respects Focus Assist / Do Not Disturb)
///   2. Topmost app alert window (bypasses DND; optional via BypassDndOnFailure/Success)
///   3. Sound (bypasses DND)
///
/// We don't take a dependency on Microsoft.Toolkit.Uwp.Notifications or WinRT
/// ToastNotificationManager here for portability — we use a simple tray-style
/// popup window that matches the app's Archival Calm theme. The DND-bypass
/// path is our primary alert channel anyway for failures.
/// </summary>
public sealed class NotificationService
{
    private static ILogger _log => AppLogger.For<NotificationService>();
    private readonly ConfigStore _config;
    public NotificationService(ConfigStore config) => _config = config;

    /// <summary>
    /// Backwards-compatible overload for existing call sites that don't
    /// pass an onClick action. Forwards to the click-aware overload
    /// with onClick=null (the toast body still dismisses, but doesn't
    /// navigate).
    /// </summary>
    public Task NotifyAsync(string title, string detail, NotificationSeverity severity)
        => NotifyAsync(title, detail, severity, onClick: null, dismissAfter: null);

    public Task NotifyAsync(string title, string detail, NotificationSeverity severity, Action? onClick)
        => NotifyAsync(title, detail, severity, onClick, dismissAfter: null);

    public Task NotifyAsync(string title, string detail, NotificationSeverity severity, TimeSpan? dismissAfter)
        => NotifyAsync(title, detail, severity, onClick: null, dismissAfter: dismissAfter);

    /// <summary>
    /// Show a notification. If <paramref name="onClick"/> is non-null,
    /// the toast body becomes clickable and invokes the action when the
    /// user clicks anywhere on it (in addition to the always-present ✕
    /// dismiss button). The orchestrator wires this to "open the main
    /// window → Activity & logs → select this backup's last run" so the
    /// user can jump straight to the failure detail from the toast.
    ///
    /// <paramref name="dismissAfter"/> overrides the default 8s tray
    /// auto-dismiss for callers whose toast carries a list users will
    /// want to actually read (the test-restore per-destination
    /// summary is one — 8s wasn't enough). Topmost alerts ignore this
    /// (they're sticky-by-design until the user clicks ✕).
    /// </summary>
    public async Task NotifyAsync(string title, string detail, NotificationSeverity severity, Action? onClick, TimeSpan? dismissAfter)
    {
        // Always log the notification, even when no UI is available — the
        // log is the canonical record of "what did the app tell the user".
        var level = severity switch
        {
            NotificationSeverity.Failure => Serilog.Events.LogEventLevel.Warning,
            NotificationSeverity.Warning => Serilog.Events.LogEventLevel.Warning,
            _ => Serilog.Events.LogEventLevel.Information,
        };
        _log.Write(level, "Notify {Severity}: {Title} — {Detail}", severity, title, detail);

        var s = _config.Current.Notifications;

        if (s.PlaySound)
            PlaySoundFor(severity);

        // Pop-up windows only make sense under a real desktop lifetime.
        // Unattended CLI runs (`--run`) and the test harness both use a
        // headless/console lifetime where creating a Window would crash —
        // fall back to a silent notification in those contexts.
        if (!IsDesktopUi()) return;

        // Failure is always shown as a sticky topmost alert: it bypasses
        // DND unconditionally (a failed backup is the one notification
        // class users actually need to see immediately) and never auto-
        // dismisses, so the user can't miss it because they were AFK
        // when the 8-second tray timer ran out. The Settings toggles
        // around DND-on-failure are honored ONLY for the taskbar-flash
        // suppression decision; the alert itself always appears.
        //
        // Other severities follow the user's notification settings:
        //   • Success/Info/Warning: tray popup IFF ToastsEnabled.
        //   • Bypass-DND on Success: tray escalates to topmost.
        if (severity == NotificationSeverity.Failure)
        {
            await ShowTopmostAlertAsync(title, detail, severity, onClick);
            return;
        }

        var bypass = severity switch
        {
            NotificationSeverity.Success => s.BypassDndOnSuccess,
            _ => false,
        };

        if (bypass)
            await ShowTopmostAlertAsync(title, detail, severity, onClick);
        else if (s.ToastsEnabled)
            ShowTrayPopup(title, detail, severity, onClick, dismissAfter);
    }

    private static bool IsDesktopUi() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime;

    // ---- sound ----

    private static void PlaySoundFor(NotificationSeverity s)
    {
        // System.Media.SystemSounds is a Windows-only assembly — its
        // type is forwarded to System.Windows.Extensions, which we only
        // reference on the Windows TFM. Cross-platform desktop tone-
        // playback is surprisingly ugly (no portable BCL surface). Skip
        // silently off-Windows; the visual notification still fires,
        // and the alert window's accent-bar plus optional sticky-
        // topmost behaviour cover the same "demand attention" intent.
        if (!PlatformInfo.IsWindows) return;
#if WINDOWS
        Platform.Windows.WindowsSystemSounds.PlayFor(s);
#endif
    }

    // ---- toasts / tray popups ----

    private void ShowTrayPopup(string title, string detail, NotificationSeverity s, Action? onClick, TimeSpan? dismissAfter = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var w = BuildAlertWindow(title, detail, s, topmost: false,
                    autoDismissAfter: dismissAfter ?? TimeSpan.FromSeconds(8), onClick: onClick);
                if (w is null) return;  // ctor failed — already logged
                PositionBottomRight(w);
                w.Show();
            }
            catch (Exception ex)
            {
                AppLogger.For<NotificationService>().Warning(ex, "ShowTrayPopup threw");
            }
        });
    }

    private Task ShowTopmostAlertAsync(string title, string detail, NotificationSeverity s, Action? onClick)
    {
        var tcs = new TaskCompletionSource();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var w = BuildAlertWindow(title, detail, s, topmost: true, autoDismissAfter: null, onClick: onClick);
                if (w is null) { tcs.TrySetResult(); return; }
                PositionBottomRight(w);
                w.Closed += (_, __) => tcs.TrySetResult();
                w.Show();

                // Flash the taskbar so users notice even on a second monitor.
                // No-op on macOS / Linux — there's no portable equivalent
                // to FlashWindowEx; the topmost+sticky behaviour above
                // already keeps the alert visible and the dock icon
                // bouncing on macOS comes from being a foreground
                // application, which the topmost window triggers.
                if (PlatformInfo.IsWindows)
                {
#if WINDOWS
                    try
                    {
                        if (w.TryGetPlatformHandle()?.Handle is { } h && h != IntPtr.Zero)
                            Platform.Windows.WindowsTaskbarFlash.Flash(h);
                    }
                    catch { }
#endif
                }
            }
            catch (Exception ex)
            {
                AppLogger.For<NotificationService>().Warning(ex, "ShowTopmostAlertAsync threw");
                tcs.TrySetResult();
            }
        });
        return tcs.Task;
    }

    private static Window? BuildAlertWindow(string title, string detail, NotificationSeverity s, bool topmost, TimeSpan? autoDismissAfter, Action? onClick)
    {
        var accent = s switch
        {
            NotificationSeverity.Failure => Color.Parse("#B5634B"),
            NotificationSeverity.Warning => Color.Parse("#C49A6C"),
            NotificationSeverity.Success => Color.Parse("#5B8E6E"),
            _ => Color.Parse("#9A9890"),
        };

        var titleBlock = new TextBlock
        {
            Text = title,
            FontFamily = new FontFamily("Fraunces, Cambria, Georgia, serif"),
            FontSize = 18,
            Foreground = new SolidColorBrush(Color.Parse("#E8E6DF")),
            Margin = new Thickness(0, 0, 0, 6),
        };

        var detailBlock = new TextBlock
        {
            Text = detail,
            FontFamily = new FontFamily("Geist, Segoe UI, system-ui, sans-serif"),
            FontSize = 13,
            LineHeight = 20,
            Foreground = new SolidColorBrush(Color.Parse("#9A9890")),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
        };

        var accentBar = new Border
        {
            Width = 3,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(accent),
        };

        var stack = new StackPanel
        {
            Margin = new Thickness(22, 20, 24, 22),
            Orientation = Orientation.Vertical,
            Spacing = 6,
            Children = { titleBlock, detailBlock },
        };

        // Dismiss × — needed because Failure alerts are sticky (no auto-
        // close) and otherwise the only way to get rid of them is to
        // kill the app. Lives in the top-right of the content column.
        var dismissBtn = new Button
        {
            Content = "✕",
            Width = 28,
            Height = 28,
            FontSize = 14,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.Parse("#9A9890")),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 6, 0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("3,*,Auto") };
        grid.Children.Add(accentBar);
        Grid.SetColumn(accentBar, 0);
        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);
        Grid.SetColumn(dismissBtn, 2);
        grid.Children.Add(dismissBtn);

        var host = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#171A17")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2A2D29")),
            BorderThickness = new Thickness(1),
            Child = grid,
        };

        // Window construction itself can fail when Avalonia tries to
        // copy the App's default Icon onto the new window via
        // Win32Icon..ctor — observed crashing the entire process when
        // GDI handles were exhausted (the syncing animation used to
        // reallocate WindowIcon every 160ms, leaking ICON handles
        // until CreateIcon returned NULL with LastError 0). Caught
        // here so a transient platform failure becomes a missed
        // notification instead of an app-killing FATAL. We immediately
        // null out Icon below for the same defensive reason.
        Window window;
        try
        {
            window = new Window
            {
                Title = "Duplimate",
                SystemDecorations = SystemDecorations.None,
                CanResize = false,
                ShowInTaskbar = topmost,
                Topmost = topmost,
                Background = Brushes.Transparent,
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
                SizeToContent = SizeToContent.WidthAndHeight,
                Content = host,
            };
            // Drop the inherited default icon — this notification window
            // doesn't need one (no taskbar entry when topmost=true), and
            // re-reading the App's Icon every popup is what triggered
            // the GDI exhaustion crash. Best-effort.
            try { window.Icon = null; } catch { }
        }
        catch (Exception ex)
        {
            // Last-ditch: build the window without any icon-bearing
            // initial properties. Avalonia still copies the App icon
            // on the property-change side, but at least the throw
            // here would be from the property assignment, not the
            // ctor itself. Surface the failure via the regular log
            // and return null — caller short-circuits.
            AppLogger.For<NotificationService>().Warning(ex,
                "BuildAlertWindow: Window ctor threw — dropping toast for «{Title}»", title);
            return null!;
        }

        // Wire the × button to close the alert. Use a closure on the
        // outer `window` since the button was created before the window.
        dismissBtn.Click += (_, __) => { try { window.Close(); } catch { } };

        // Click-to-action: when onClick is provided, the toast body
        // becomes a clickable target. Used by the orchestrator to wire
        // failure toasts → "open Activity & logs with this run picked".
        // The dismiss button stops bubble so clicking ✕ doesn't also
        // navigate. Cursor changes to Hand so the affordance is
        // discoverable.
        if (onClick is not null)
        {
            host.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            host.PointerPressed += (sender, args) =>
            {
                if (args.Source is Control c)
                {
                    // Walk up from the source — if the user clicked the
                    // dismiss button, it'll handle close itself.
                    for (var node = c; node is not null; node = node.Parent as Control)
                        if (node == dismissBtn) return;
                }
                try { onClick(); } catch { }
                try { window.Close(); } catch { }
            };
        }

        if (autoDismissAfter is { } ad)
        {
            var timer = new System.Threading.Timer(_ =>
            {
                Dispatcher.UIThread.Post(() => { try { window.Close(); } catch { } });
            }, null, ad, System.Threading.Timeout.InfiniteTimeSpan);
            window.Closed += (_, __) => timer.Dispose();
        }

        return window;
    }

    private static void PositionBottomRight(Window w)
    {
        w.Opened += (_, __) =>
        {
            var screen = w.Screens.Primary;
            if (screen is null) return;
            var wa = screen.WorkingArea;
            var size = w.Bounds.Size;
            w.Position = new PixelPoint(
                (int)(wa.Right  - size.Width  * screen.Scaling - 24),
                (int)(wa.Bottom - size.Height * screen.Scaling - 24));
        };
    }
}

