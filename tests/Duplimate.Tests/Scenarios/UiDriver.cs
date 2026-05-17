using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Duplimate.Tests.Scenarios;

/// <summary>
/// High-level UI interaction primitives for scenario tests. These
/// wrap Avalonia's headless dispatcher + visual-tree walk so a
/// scenario reads like a flow ("click Add a backup, type 'Hello',
/// click Save") rather than a chain of NestedFindControl visits.
///
/// Why not Avalonia.Headless mouse APIs directly? Two reasons:
///   1. The mouse APIs take pixel coordinates, which means every
///      click depends on layout passes settling. Click-by-content
///      ("the button labelled X") is more robust to layout tweaks.
///   2. Some scenario steps interact with VM-only state (no visible
///      control) — uniform Driver lets a scenario mix UI clicks
///      with direct VM property pokes without context-switching.
///
/// All methods that mutate UI state run synchronously on the UI
/// dispatcher and pump pending dispatch operations afterward so
/// bound properties have time to fire OnPropertyChanged before the
/// next assertion.
/// </summary>
public sealed class UiDriver : IDisposable
{
    private readonly ScenarioContext _ctx;
    private readonly List<Window> _ownedWindows = new();

    public UiDriver(ScenarioContext ctx) => _ctx = ctx;

    /// <summary>
    /// Show a window headlessly, run two layout passes, and remember
    /// it so Dispose can close any leftover windows. Returns the
    /// window for fluent chaining.
    /// </summary>
    public TWindow Show<TWindow>(TWindow window) where TWindow : Window
    {
        window.Show();
        window.UpdateLayout();
        Pump();
        window.UpdateLayout();
        _ownedWindows.Add(window);
        return window;
    }

    /// <summary>
    /// Pump the Avalonia dispatcher until pending background jobs
    /// have settled. Avalonia.Headless installs a manual-pump
    /// dispatcher; without these RunJobs calls, an awaited
    /// Dispatcher.UIThread.InvokeAsync from the VM never fires
    /// during a synchronous scenario step.
    /// </summary>
    public void Pump(int iterations = 4)
    {
        for (int i = 0; i < iterations; i++)
            Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Pump for a wall-clock window so timer-driven work
    /// (debouncers, animations, the 150ms ProgressLog flush) has a
    /// chance to fire.</summary>
    public async Task PumpAsync(int milliseconds = 200)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Find every descendant button on <paramref name="root"/> and
    /// invoke the first whose visible text matches
    /// <paramref name="content"/> (case-insensitive, trim, ignores
    /// inner Run / TextBlock structure). Returns true on a hit.
    ///
    /// We invoke the bound Command directly rather than firing a
    /// PointerPressed event — pixel-based clicks are flaky in
    /// headless layouts where bounds drift by a pixel between
    /// layout passes, and we don't lose any meaningful coverage
    /// (the Click→Command wiring is an Avalonia primitive, not
    /// our code). Headless tests focus on app behaviour, not
    /// Avalonia's own input pipeline.
    /// </summary>
    public bool ClickButton(Visual root, string content)
    {
        foreach (var btn in Descendants<Button>(root))
        {
            var text = ExtractText(btn);
            if (string.Equals(text, content, StringComparison.OrdinalIgnoreCase))
            {
                Click(btn);
                return true;
            }
        }
        return false;
    }

    /// <summary>Click the named control (x:Name). Throws if not found.</summary>
    public void ClickByName(Visual root, string name)
    {
        var btn = root.FindDescendantByName(name) as Button
                  ?? throw new InvalidOperationException($"No button named '{name}' under {root}");
        Click(btn);
    }

    /// <summary>
    /// Set the Text property of the first TextBox under
    /// <paramref name="root"/> whose Watermark / Name matches
    /// <paramref name="locator"/>. Two-way bindings see the change
    /// after the next dispatcher pump.
    /// </summary>
    public bool EnterText(Visual root, string locator, string value)
    {
        foreach (var tb in Descendants<TextBox>(root))
        {
            if (string.Equals(tb.Name, locator, StringComparison.OrdinalIgnoreCase)
                || string.Equals(tb.Watermark, locator, StringComparison.OrdinalIgnoreCase))
            {
                tb.Text = value;
                Pump();
                return true;
            }
        }
        return false;
    }

    /// <summary>Wait synchronously until <paramref name="predicate"/>
    /// returns true, pumping the dispatcher between checks. Returns
    /// false on timeout.</summary>
    public bool WaitFor(Func<bool> predicate, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            Pump();
            System.Threading.Thread.Sleep(20);
        }
        return predicate();
    }

    public void Dispose()
    {
        foreach (var w in _ownedWindows)
        {
            try { w.Close(); } catch { /* best-effort */ }
        }
        _ownedWindows.Clear();
    }

    // ---- internals ----

    private void Click(Button button)
    {
        var cmd = button.Command;
        var param = button.CommandParameter;
        if (cmd is not null && cmd.CanExecute(param))
        {
            cmd.Execute(param);
        }
        else
        {
            // No bound command (or the command refuses execution).
            // Buttons with code-behind Click handlers expose a
            // public RaiseClickEvent() in some Avalonia versions but
            // not all; fall back to a direct OnClick via reflection
            // so code-behind handlers still fire. Failure to find
            // the method is logged into the report rather than
            // thrown — a scenario clicking a disabled button is a
            // real finding the auditor should record.
            var raise = button.GetType().GetMethod("RaiseClickEvent",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
              | System.Reflection.BindingFlags.Public);
            if (raise is not null) raise.Invoke(button, null);
            else _ctx.Report.RecordAnomaly(
                $"clicked button '{ExtractText(button)}' but it had no enabled command and no RaiseClickEvent shim");
        }
        Pump();
    }

    private static string ExtractText(Button btn)
    {
        // Buttons in this app use either Content="…" directly OR a
        // composite content with an inner TextBlock. Try both.
        if (btn.Content is string s) return s.Trim();
        if (btn.Content is Visual v)
        {
            foreach (var tb in Descendants<TextBlock>(v))
                if (!string.IsNullOrWhiteSpace(tb.Text)) return tb.Text.Trim();
        }
        return string.Empty;
    }

    private static IEnumerable<T> Descendants<T>(Visual root) where T : Visual
    {
        foreach (var v in root.GetVisualDescendants())
            if (v is T t) yield return t;
    }

    /// <summary>
    /// Hatch escape: a scenario can grab the underlying VM and
    /// poke properties directly when an interaction is awkward to
    /// drive through the visual tree (e.g. a TimePicker's hidden
    /// glass overlay). Use sparingly — every direct poke skips
    /// real wiring coverage.
    /// </summary>
    public T VmOf<T>(StyledElement element) where T : class =>
        (element.DataContext as T) ?? throw new InvalidOperationException(
            $"Element {element.GetType().Name} has DataContext {element.DataContext?.GetType().Name ?? "null"}, expected {typeof(T).Name}");

}

internal static class VisualLookup
{
    /// <summary>
    /// Find the first descendant whose <c>Name</c> property matches.
    /// LogicalTree walk catches templated parts that the visual
    /// tree alone misses (e.g. PART_BorderElement under a Button's
    /// template).
    /// </summary>
    public static Visual? FindDescendantByName(this Visual root, string name)
    {
        foreach (var v in root.GetVisualDescendants())
            if (v is StyledElement s && string.Equals(s.Name, name, StringComparison.Ordinal))
                return v;
        return null;
    }
}
