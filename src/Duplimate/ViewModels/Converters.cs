using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Duplimate.Models;

namespace Duplimate.ViewModels;

/// <summary>Bool → opacity. <c>true</c> emits <c>0.5</c> (so a tint
/// layer is visible behind running progress) and <c>false</c> emits
/// <c>0</c> (fully transparent). Used by the BackupCard's per-source
/// row background tint to fade in/out with the running flag.</summary>
public static class BoolToOpacity
{
    public static readonly IValueConverter HalfOrZero = new Conv();
    private sealed class Conv : IValueConverter
    {
        public object? Convert(object? v, Type t, object? p, CultureInfo c) =>
            v is bool b && b ? 0.5 : 0.0;
        public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
            Avalonia.Data.BindingOperations.DoNothing;
    }
}

/// <summary>Logical AND of any number of bool inputs, for MultiBinding.
/// Used by BackupCard's destination pill IsEnabled — needs to be
/// `pill.IsClickable AND parentCard.CanEditWhileIdle`. Anything
/// non-bool short-circuits to false (defensive).</summary>
public static class BoolAnd
{
    public static readonly IMultiValueConverter To = new Conv();
    private sealed class Conv : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values is null || values.Count == 0) return false;
            foreach (var v in values)
                if (v is not bool b || !b) return false;
            return true;
        }
    }
}

/// <summary>"Verifying…" while a verify is in flight, "Test restore"
/// otherwise. Driving the BackupCard's verify button label off a single
/// converter avoids duplicating two TextBlocks behind an IsVisible
/// toggle (used to flicker on rapid state changes).</summary>
public static class VerifyButtonLabel
{
    public static readonly IValueConverter To = new Conv();
    private sealed class Conv : IValueConverter
    {
        public object? Convert(object? v, Type t, object? p, CultureInfo c) =>
            v is bool b && b ? "Verifying…" : "Test restore";
        public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
            Avalonia.Data.BindingOperations.DoNothing;
    }
}

/// <summary>
/// Maps a <see cref="DestinationKind"/> (nullable) to the icon
/// <see cref="StreamGeometry"/> we want to show next to it in the
/// ComboBox item template + list rows. Categorical icons today —
/// Folder / HardDrive / Network / Cloud / Database — because shipping
/// per-brand SVGs for Dropbox/OneDrive/GDrive means tracking 3 brand
/// trademarks and their design rules. See ROADMAP.md if/when we want
/// to upgrade to branded glyphs.
/// </summary>
public static class DestinationKindIcon
{
    public static readonly IValueConverter To = new Converter();

    private sealed class Converter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var resourceKey = value switch
            {
                DestinationKind.LocalFolder      => "Icon.Folder",
                DestinationKind.ExternalDrive    => "Icon.HardDrive",
                DestinationKind.NetworkShare     => "Icon.Network",
                DestinationKind.DropboxAppScoped => "Icon.BrandDropbox",
                DestinationKind.DropboxFullAccess=> "Icon.BrandDropbox",
                DestinationKind.OneDrivePersonal => "Icon.BrandOneDrive",
                DestinationKind.OneDriveBusiness => "Icon.BrandOneDrive",
                DestinationKind.GoogleDrive      => "Icon.BrandGoogleDrive",
                DestinationKind.S3Compatible     => "Icon.Database",
                _                                => null,
            };
            if (resourceKey is null) return null;
            return Application.Current?.FindResource(resourceKey);
        }

        public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
    }
}

/// <summary>
/// Renders a <see cref="IEnumerable{String}"/> of source paths as a
/// single-line summary for list rows: the whole path when there's one
/// source, "N sources" otherwise. Callers still see the full list in
/// the editor chip view / tooltips.
/// </summary>
public static class SourcePathsDisplay
{
    public static readonly IValueConverter To = new SourcePathsConverter();

    private sealed class SourcePathsConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not IEnumerable seq) return "—";
            var list = seq.Cast<object>().Select(o => o?.ToString() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            return list.Count switch
            {
                0 => "—",
                1 => list[0],
                _ => $"{list.Count} sources",
            };
        }
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

/// <summary>
/// Returns true if the bound value's string representation equals the
/// parameter (case-insensitive). Compares via <c>ToString()</c> on both
/// sides so this works for enums (e.g. <c>Frequency = ScheduleFrequency.Hourly</c>
/// vs <c>ConverterParameter=Hourly</c>) as well as plain strings.
///
/// History note: an earlier version did <c>value as string</c>, which
/// returned null for any enum binding and silently hid every conditional
/// UI block driven by an enum (mail provider sub-fields, hourly-schedule
/// "every N hours" field, etc.). The converter is now type-tolerant so
/// those bindings work as written.
/// </summary>
public static class Eq
{
    public static readonly IValueConverter To = new EqConverter();

    private sealed class EqConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is null && parameter is null) return true;
            if (value is null || parameter is null) return false;
            return string.Equals(
                value.ToString(),
                parameter.ToString(),
                StringComparison.OrdinalIgnoreCase);
        }
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Avalonia.Data.BindingOperations.DoNothing;
    }
}

/// <summary>Returns true when the bound value is the same reference as the parameter.</summary>
public static class RefEq
{
    public static readonly IValueConverter To = new RefEqConverter();

    private sealed class RefEqConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => ReferenceEquals(value, parameter);
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is bool b && b ? parameter! : Avalonia.Data.BindingOperations.DoNothing;
    }
}

/// <summary>
/// Looks up a resource key (string) in <c>Application.Current.Resources</c>
/// and returns the resource. Used to let view models pass icon-key strings
/// (e.g. "Icon.Folder") that the view resolves to a <see cref="Avalonia.Media.StreamGeometry"/>.
/// </summary>
public static class ResourceLookup
{
    public static readonly IValueConverter To = new ResourceLookupConverter();

    private sealed class ResourceLookupConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string key && Avalonia.Application.Current is { } app &&
                app.Resources.TryGetResource(key, null, out var resource))
            {
                return resource;
            }
            return null;
        }
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Avalonia.Data.BindingOperations.DoNothing;
    }
}

/// <summary>Returns true when the bound string is non-null and non-empty.</summary>
public static class NotEmpty
{
    public static readonly IValueConverter To = new NotEmptyConverter();

    private sealed class NotEmptyConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => !string.IsNullOrWhiteSpace(value as string);
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Avalonia.Data.BindingOperations.DoNothing;
    }
}

/// <summary>
/// Returns true when the bound value is non-null. Use for IsEnabled
/// gates on object-typed bindings (Destination?, RevisionSummary?,
/// etc.) where NotEmpty.To would always return false because it
/// inspects strings — a real bug source historically: "Add
/// destination" / "Next: pick a revision" stayed disabled even after
/// the user picked a value because the value was an object.
/// </summary>
public static class IsNotNull
{
    public static readonly IValueConverter To = new IsNotNullConverter();

    private sealed class IsNotNullConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is not null;
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Avalonia.Data.BindingOperations.DoNothing;
    }
}

/// <summary>
/// Converts a 0..100 percent into a horizontal LinearGradientBrush
/// whose accent-coloured fill stops at that percentage and the
/// remainder is transparent — used by the BackupCard's per-destination
/// pills so each pill gradually darkens from left-to-right as that
/// destination's chunks land.
/// <para>
/// Multi-binding inputs: [PercentComplete (double), AccentBrush
/// (IBrush, supplied via a DynamicResource binding from XAML)].
/// Earlier versions read the accent from
/// <c>Application.Current.Resources</c> at convert-time, but the
/// converter only re-fires on PercentComplete changes — swapping
/// accents in Settings while a pill was idle (or mid-run between
/// progress events) left the wash in the OLD accent until the next
/// percent tick. Threading the accent through the binding makes
/// DynamicResource invalidation drive a re-convert immediately.
/// </para>
/// </summary>
public static class PercentToProgressBrush
{
    public static readonly IMultiValueConverter To = new MultiConv();

    private sealed class MultiConv : IMultiValueConverter
    {
        public object? Convert(System.Collections.Generic.IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            var raw = values.Count > 0 ? values[0] switch
            {
                double d => d,
                float f  => f,
                int i    => (double)i,
                _        => 0.0,
            } : 0.0;
            var pct = Math.Clamp(raw, 0, 100) / 100.0;
            var accent = (values.Count > 1 ? values[1] as ISolidColorBrush : null)?.Color
                         ?? Color.FromRgb(0x21, 0x6E, 0xBD);
            // 30% alpha keeps the foreground glyph + name readable
            // while still giving an unambiguous "this much is done"
            // wash over the existing AccentTint.
            var fill = Color.FromArgb(0x4D, accent.R, accent.G, accent.B);
            // Two stops at the same offset = a hard fill/transparent
            // edge. Clamp seam slightly inside [0, 1] so 0% / 100%
            // still yield a well-formed brush.
            var seam = Math.Min(0.999, Math.Max(0.001, pct));
            return new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint   = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(fill, 0),
                    new GradientStop(fill, seam),
                    new GradientStop(Colors.Transparent, seam),
                    new GradientStop(Colors.Transparent, 1),
                },
            };
        }
    }
}

/// <summary>Negates a bool. Used for "disabled when X is true" style visibility.</summary>
public static class Neg
{
    public static readonly IValueConverter To = new NegConverter();

    private sealed class NegConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is bool b ? !b : Avalonia.Data.BindingOperations.DoNothing;
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is bool b ? !b : Avalonia.Data.BindingOperations.DoNothing;
    }
}

/// <summary>Formats a bytes count (long or int) as "1.23 MB" etc.</summary>
public static class HumanSize
{
    public static readonly IValueConverter To = new HumanSizeConverter();

    private sealed class HumanSizeConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            long bytes = value switch
            {
                long l => l,
                int i => i,
                _ => 0,
            };
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes; var i2 = 0;
            while (v >= 1024 && i2 < u.Length - 1) { v /= 1024; i2++; }
            return $"{v:0.##} {u[i2]}";
        }
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Avalonia.Data.BindingOperations.DoNothing;
    }
}

/// <summary>
/// "Does the HashSet&lt;string&gt; passed as ConverterParameter contain the bound value?"
/// Used to reflect selection state from a shared set into per-row checkbox visuals.
/// </summary>
public static class SetContains
{
    public static readonly IValueConverter To = new SetContainsConverter();

    private sealed class SetContainsConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string s && parameter is IEnumerable<string> set)
            {
                if (set is HashSet<string> hs) return hs.Contains(s);
                foreach (var item in set) if (item == s) return true;
                return false;
            }
            return false;
        }
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Avalonia.Data.BindingOperations.DoNothing;
    }
}

/// <summary>
/// Renders a <see cref="MailProvider"/> enum value as the dropdown
/// label users actually see in Settings. Resend gets a "(recommended)"
/// suffix because it's the simplest path (single API key, no
/// per-domain auth dance).
/// </summary>
public static class MailProviderDisplay
{
    public static readonly IValueConverter To = new Converter();

    private sealed class Converter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
        {
            MailProvider.Disabled => "Disabled",
            MailProvider.Resend   => "Resend (recommended)",
            MailProvider.Mailgun  => "Mailgun",
            _                     => value?.ToString() ?? "",
        };
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Avalonia.Data.BindingOperations.DoNothing;
    }
}

/// <summary>
/// Onboarding step-indicator helpers. Drives the colored dot + number
/// for each step: when the bool is true (this is the current step) we
/// use the accent brush + white text; otherwise a subtle border + dim
/// text. Used in OnboardingWindow.axaml.
///
/// Resource lookup uses <c>TryGetResource(key, ActualThemeVariant)</c>
/// instead of the older <c>FindResource(key)</c> — accent brushes live
/// inside a <c>ThemeDictionaries</c> block and only resolve via the
/// theme-variant overload. The previous FindResource call returned
/// null silently, which left the step-2 / step-3 dots stuck on the
/// default fallback brush even when the user advanced past step 1.
/// </summary>
public static class BoolToAccent
{
    public static readonly IValueConverter OrSubtle = new OrSubtleConverter();
    public static readonly IValueConverter WhiteOrDim = new WhiteOrDimConverter();

    private static object? GetThemedBrush(string key)
    {
        if (Avalonia.Application.Current is { } app
            && app.TryGetResource(key, app.ActualThemeVariant, out var resource))
        {
            return resource;
        }
        return null;
    }

    private sealed class OrSubtleConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var key = value is bool b && b ? "DM.Brush.Accent" : "DM.Brush.BorderSubtle";
            return GetThemedBrush(key);
        }
        public object? ConvertBack(object? v, Type t, object? p, CultureInfo c)
            => Avalonia.Data.BindingOperations.DoNothing;
    }

    private sealed class WhiteOrDimConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b && b) return Brushes.White;
            return GetThemedBrush("DM.Brush.FgDim");
        }
        public object? ConvertBack(object? v, Type t, object? p, CultureInfo c)
            => Avalonia.Data.BindingOperations.DoNothing;
    }
}

/// <summary>
/// Renders a <see cref="RunRecord"/> as a single dropdown row. The shape
/// is "{started, yyyy-MM-dd HH:mm}  ·  {Status}  ·  {Summary}" — except
/// the in-flight synthetic record sets Summary="Running…" AND
/// Status=Running, which under the previous chained-Run template
/// rendered as "12:34 · Running · Running…" with the same word twice.
/// This converter folds the duplicate by suppressing the Summary cell
/// when it's a case-insensitive substring of (or equals) the Status
/// label, and also drops the Summary cell entirely when it's blank.
/// </summary>
public static class RunRecordRowText
{
    public static readonly IValueConverter To = new Conv();
    private sealed class Conv : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not RunRecord r) return "";
            var when = r.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
            var status = r.Status.ToString();
            var summary = (r.Summary ?? "").Trim();
            // Kind prefix: backup runs render flat (the most common
            // case, no extra noise); restore + test-restore get a
            // tagged prefix so the user can scan the dropdown and
            // tell what each entry represents — the user explicitly
            // asked for "a dedicated entry in RUN" for restore /
            // test-restore activity.
            var kindPrefix = r.Kind switch
            {
                RunKind.Restore     => "↺ Restore  ·  ",
                RunKind.TestRestore => "✓ Test restore  ·  ",
                _                   => "",
            };
            // De-dupe: if the summary is empty, OR is just the status
            // word again (e.g. "Running" vs "Running…"), don't repeat
            // it. Special-case Skipped: the cell-level reason builder
            // produces phrases like "Manually skipped after 5s" that
            // already describe the status — without this fold the row
            // reads "Skipped · Manually skipped after 5s", which the
            // user reported as redundant ("combine everything in one
            // clean sentence when words would otherwise be redundant").
            var summaryEchoesStatus = summary.Length > 0
                && (string.Equals(summary, status, StringComparison.OrdinalIgnoreCase)
                    || summary.StartsWith(status, StringComparison.OrdinalIgnoreCase)
                    || status.StartsWith(summary.TrimEnd('…', '.', ' '), StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(status, "Skipped", StringComparison.OrdinalIgnoreCase)
                        && summary.Contains("skipped", StringComparison.OrdinalIgnoreCase)));
            if (summary.Length == 0)
                return $"{kindPrefix}{when}  ·  {status}";
            // When the summary already implies the status, prefer the
            // summary alone (it's the more descriptive of the two).
            if (summaryEchoesStatus)
                return $"{kindPrefix}{when}  ·  {summary}";
            return $"{kindPrefix}{when}  ·  {status}  ·  {summary}";
        }
        public object? ConvertBack(object? v, Type t, object? p, CultureInfo c)
            => Avalonia.Data.BindingOperations.DoNothing;
    }
}
