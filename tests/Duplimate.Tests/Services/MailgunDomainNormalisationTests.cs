using Duplimate.Services;
using Xunit;

namespace Duplimate.Tests.Services;

/// <summary>
/// The Mailgun "Sending domain" field is free-text — users paste
/// whatever they copied. Without normalisation, "https://mg.example.com"
/// became part of the API URL as URL-encoded form
/// (<c>api.mailgun.net/v3/https%3A%2F%2Fmg.example.com/messages</c>)
/// and Mailgun returned 404. These tests pin the cleanup contract.
/// </summary>
public class MailgunDomainNormalisationTests
{
    [Theory]
    [InlineData("mg.example.com",                    "mg.example.com")]
    [InlineData("https://mg.example.com",            "mg.example.com")]
    [InlineData("http://mg.example.com",             "mg.example.com")]
    [InlineData("HTTPS://mg.example.com",            "mg.example.com")]
    [InlineData("mg.example.com/",                   "mg.example.com")]
    [InlineData("https://mg.example.com/",           "mg.example.com")]
    [InlineData("https://mg.example.com/some/path",  "mg.example.com")]
    [InlineData("mg.example.com?query=1",            "mg.example.com")]
    [InlineData("mg.example.com#anchor",             "mg.example.com")]
    [InlineData("  mg.example.com  ",                "mg.example.com")]
    [InlineData("mg.example.com.",                   "mg.example.com")] // trailing dot
    public void NormaliseMailgunDomain_stripsFluff(string raw, string expected)
    {
        Assert.Equal(expected, MailService.NormaliseMailgunDomain(raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void NormaliseMailgunDomain_emptyInput_returnsEmpty(string? raw)
    {
        Assert.Equal("", MailService.NormaliseMailgunDomain(raw));
    }
}
