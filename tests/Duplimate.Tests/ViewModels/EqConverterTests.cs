using System.Globalization;
using Duplimate.Models;
using Duplimate.ViewModels;
using Xunit;

namespace Duplimate.Tests.ViewModels;

/// <summary>
/// The original Eq converter compared via <c>value as string</c>, which
/// returned null for any enum binding. That silently hid every
/// conditional UI block driven by an enum:
///   • The Resend / Mailgun API key panels in Settings (gated on
///     <see cref="MailProvider"/>).
///   • The "Run every N hours" field in the Backup editor + Onboarding
///     (gated on <see cref="ScheduleFrequency"/>).
/// These tests pin the type-tolerant behaviour that fixed the bug, so
/// a future "tighten types" refactor can't quietly re-break it.
/// </summary>
public class EqConverterTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [Fact]
    public void Eq_enumValue_matchesStringParameter()
    {
        // The exact failure mode that hid the Resend API-key field —
        // bound enum + XAML string parameter, both representing "Resend".
        var result = Eq.To.Convert(MailProvider.Resend, typeof(bool), "Resend", Culture);
        Assert.Equal(true, result);
    }

    [Fact]
    public void Eq_enumValue_doesNotMatchDifferentStringParameter()
    {
        var result = Eq.To.Convert(MailProvider.Resend, typeof(bool), "Mailgun", Culture);
        Assert.Equal(false, result);
    }

    [Fact]
    public void Eq_isCaseInsensitive()
    {
        // ConverterParameter casing in XAML shouldn't matter — match the
        // enum name regardless.
        Assert.Equal(true, Eq.To.Convert(ScheduleFrequency.Hourly, typeof(bool), "hourly", Culture));
        Assert.Equal(true, Eq.To.Convert(ScheduleFrequency.Hourly, typeof(bool), "HOURLY", Culture));
    }

    [Fact]
    public void Eq_string_string_matches()
    {
        // The pre-existing string-vs-string usage (StatusClass = "ok"
        // bound on the Dashboard tile dot) must keep working.
        Assert.Equal(true, Eq.To.Convert("ok", typeof(bool), "ok", Culture));
        Assert.Equal(false, Eq.To.Convert("ok", typeof(bool), "fail", Culture));
    }

    [Fact]
    public void Eq_null_value_returnsFalse()
    {
        Assert.Equal(false, Eq.To.Convert(null, typeof(bool), "Resend", Culture));
    }

    [Fact]
    public void Eq_null_parameter_returnsFalse()
    {
        Assert.Equal(false, Eq.To.Convert(MailProvider.Resend, typeof(bool), null, Culture));
    }

    [Fact]
    public void Eq_both_null_returnsTrue()
    {
        // Edge case — defensive, not load-bearing in any binding today,
        // but the contract should be reflexive for "both absent".
        Assert.Equal(true, Eq.To.Convert(null, typeof(bool), null, Culture));
    }
}
