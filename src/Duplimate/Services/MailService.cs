using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Duplimate.Models;
using Serilog;

namespace Duplimate.Services;

/// <summary>
/// Email notifications. Both providers use HTTP APIs (no SMTP):
///
/// • Resend (recommended): single API key, 3000/mo free.
///   Endpoint: <c>POST https://api.resend.com/emails</c> with
///   <c>Authorization: Bearer &lt;api-key&gt;</c>. For first-time users
///   the default "onboarding@resend.dev" from-address works until they
///   verify a domain.
///
/// • Mailgun: also HTTP API (NOT SMTP — earlier versions used SMTP via
///   System.Net.Mail.SmtpClient, which is obsolete per Microsoft and
///   doesn't support port 465 implicit TLS anyway). Single API key
///   plus the verified sending domain.
///   Endpoint: <c>POST https://api.mailgun.net/v3/{domain}/messages</c>
///   (or api.eu.mailgun.net for EU accounts) with HTTP Basic auth —
///   username <c>"api"</c>, password = API key.
///
/// Both are strictly optional — if disabled, this service is a no-op.
/// Send failures are logged but never fail the backup result.
/// </summary>
public sealed class MailService
{
    private static ILogger _log => AppLogger.For<MailService>();
    private readonly ConfigStore _config;
    private readonly SecretsStore _secrets;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public MailService(ConfigStore config, SecretsStore secrets)
    {
        _config = config;
        _secrets = secrets;
    }

    public bool IsEnabled => _config.Current.Mail.Provider != MailProvider.Disabled;

    public async Task SendAsync(string subject, string plainBody, CancellationToken ct)
    {
        var m = _config.Current.Mail;
        if (m.Provider == MailProvider.Disabled)
        {
            _log.Debug("Mail send skipped — provider disabled");
            return;
        }
        var recipients = ResolveRecipients(m);
        if (recipients.Count == 0)
        {
            _log.Debug("Mail send skipped — no recipients configured");
            return;
        }

        try
        {
            switch (m.Provider)
            {
                case MailProvider.Resend:
                    await SendViaResendAsync(m, recipients, subject, plainBody, ct);
                    _log.Information("Mail sent via Resend to {Count} recipient(s): {Subject}",
                        recipients.Count, subject);
                    break;
                case MailProvider.Mailgun:
                    await SendViaMailgunAsync(m, recipients, subject, plainBody, ct);
                    _log.Information("Mail sent via Mailgun to {Count} recipient(s): {Subject}",
                        recipients.Count, subject);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation is the caller's signal, not a send failure.
            // Re-throw so the orchestrator's cancellation flow handles it
            // instead of being silently swallowed by the catch below.
            throw;
        }
        catch (Exception ex)
        {
            // Never fail the backup because of a notification failure —
            // but make sure the user can find out why no email arrived.
            _log.Warning(ex, "Mail send failed via {Provider}", m.Provider);
        }
    }

    /// <summary>
    /// Settings-test entry point used by the "Send test email" button:
    /// sends a hard-coded test message right now using the in-memory
    /// settings the user has typed (which may not yet be saved). Returns
    /// (success, detail) — the caller surfaces detail in a banner.
    /// </summary>
    public async Task<(bool ok, string detail)> SendTestAsync(
        MailSettings testSettings, IReadOnlyList<string> recipients, CancellationToken ct)
    {
        if (testSettings.Provider == MailProvider.Disabled)
            return (false, "Pick a provider first.");
        if (recipients.Count == 0)
            return (false, "Add at least one TO address.");

        var subject = "Duplimate test — your alerts are wired up correctly";
        var body =
            "If you're reading this, your Duplimate email notifications are configured correctly. " +
            $"\n\nProvider: {testSettings.Provider}\n" +
            $"From: {testSettings.FromAddress}\n" +
            $"To: {string.Join(", ", recipients)}\n" +
            $"Sent at: {DateTime.UtcNow:o}\n";

        try
        {
            switch (testSettings.Provider)
            {
                case MailProvider.Resend:
                    await SendViaResendAsync(testSettings, recipients, subject, body, ct);
                    break;
                case MailProvider.Mailgun:
                    await SendViaMailgunAsync(testSettings, recipients, subject, body, ct);
                    break;
            }
            return (true, $"Sent. Check the inbox of: {string.Join(", ", recipients)}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return (false, $"Send failed: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// One-shot migration: prefer the new <see cref="MailSettings.ToAddresses"/>
    /// list; fall back to the legacy single <c>ToAddress</c> for configs
    /// that pre-date multi-recipient support. Empties / whitespace are
    /// dropped, the result is capped at <see cref="MailSettings.MaxRecipients"/>.
    /// </summary>
    internal static List<string> ResolveRecipients(MailSettings m)
    {
        var picked = new List<string>();
        foreach (var s in m.ToAddresses ?? new())
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            picked.Add(s.Trim());
            if (picked.Count >= MailSettings.MaxRecipients) return picked;
        }
        if (picked.Count == 0 && !string.IsNullOrWhiteSpace(m.ToAddress))
            picked.Add(m.ToAddress.Trim());
        return picked;
    }

    // ---- Resend ----

    private async Task SendViaResendAsync(
        MailSettings m, IReadOnlyList<string> recipients,
        string subject, string plainBody, CancellationToken ct)
    {
        var apiKey = m.ResendApiKeyRef is null ? null : _secrets.Get(m.ResendApiKeyRef);
        if (string.IsNullOrEmpty(apiKey)) throw new InvalidOperationException(
            "Resend API key is not set. Paste your re_… key in Settings → Email.");

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var from = string.IsNullOrWhiteSpace(m.FromAddress)
            ? "Duplimate <onboarding@resend.dev>"
            : m.FromAddress;

        req.Content = JsonContent.Create(new
        {
            from,
            to = recipients,
            subject,
            text = plainBody,
        });

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            // Cap the failure body at 4 KB. A misbehaving / hostile
            // provider returning a multi-MB error page would otherwise
            // be streamed entirely into the log + the InvalidOpException
            // message + the user-visible toast. 4 KB is plenty for any
            // real "your key is wrong" / "rate-limited" diagnostic.
            var body = await ReadBodyCappedAsync(resp.Content, 4096, ct);
            _log.Warning("Resend rejected send: HTTP {Status} {Body}", (int)resp.StatusCode, body);
            throw new InvalidOperationException($"Resend HTTP {(int)resp.StatusCode}: {body}");
        }
    }

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> from the response
    /// stream and returns the resulting string. Used by every HTTP
    /// failure path so a hostile / buggy provider returning a giant
    /// body can't blow up logs or in-memory error messages.
    /// </summary>
    private static async Task<string> ReadBodyCappedAsync(System.Net.Http.HttpContent content, int maxBytes, CancellationToken ct)
    {
        using var s = await content.ReadAsStreamAsync(ct);
        var buf = new byte[maxBytes];
        int read = 0;
        while (read < maxBytes)
        {
            int n = await s.ReadAsync(buf.AsMemory(read, maxBytes - read), ct);
            if (n <= 0) break;
            read += n;
        }
        var truncated = read == maxBytes;
        var text = System.Text.Encoding.UTF8.GetString(buf, 0, read);
        return truncated ? text + " [truncated]" : text;
    }

    // ---- Mailgun (HTTP API) ----

    private async Task SendViaMailgunAsync(
        MailSettings m, IReadOnlyList<string> recipients,
        string subject, string plainBody, CancellationToken ct)
    {
        var apiKey = m.MailgunApiKeyRef is null ? null : _secrets.Get(m.MailgunApiKeyRef);
        if (string.IsNullOrEmpty(apiKey)) throw new InvalidOperationException(
            "Mailgun API key is not set. Paste your private API key in Settings → Email.");
        var domain = NormaliseMailgunDomain(m.MailgunDomain);
        if (string.IsNullOrWhiteSpace(domain))
            throw new InvalidOperationException(
                "Mailgun sending domain is not set. Enter your verified domain (or sandbox) in Settings → Email.");

        var apiHost = m.MailgunRegion == MailgunRegion.EU
            ? "https://api.eu.mailgun.net"
            : "https://api.mailgun.net";
        var url = $"{apiHost}/v3/{Uri.EscapeDataString(domain)}/messages";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        // HTTP Basic, username "api", password = the project API key.
        // The "api:" username is fixed by Mailgun and is NOT the user's
        // email address.
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes("api:" + apiKey));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        var from = string.IsNullOrWhiteSpace(m.FromAddress)
            ? $"Duplimate <postmaster@{domain}>"
            : m.FromAddress;

        // Mailgun's send-message endpoint is form-encoded, not JSON.
        // Multiple "to" entries are accepted as repeated keys; that's
        // also how Mailgun's docs show batch sends.
        var form = new List<KeyValuePair<string, string>>
        {
            new("from", from),
            new("subject", subject),
            new("text", plainBody),
        };
        foreach (var to in recipients) form.Add(new KeyValuePair<string, string>("to", to));
        req.Content = new FormUrlEncodedContent(form);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await ReadBodyCappedAsync(resp.Content, 4096, ct);
            _log.Warning("Mailgun rejected send: HTTP {Status} {Body}", (int)resp.StatusCode, body);
            throw new InvalidOperationException($"Mailgun HTTP {(int)resp.StatusCode}: {body}");
        }
    }

    /// <summary>
    /// Coerce whatever the user pasted in the "Sending domain" field into
    /// the bare host portion that Mailgun's API URL expects. Tolerates:
    ///   • Full URLs ("https://mg.example.com/", "http://mg.example.com")
    ///   • Trailing slashes / paths ("mg.example.com/")
    ///   • Leading/trailing whitespace
    /// Without this, pasting "https://mg.example.com" produced a request
    /// to <c>api.mailgun.net/v3/https%3A%2F%2Fmg.example.com/messages</c>
    /// which Mailgun returned 404 for.
    /// Internal so the unit test can pin the normalisation contract.
    /// </summary>
    internal static string NormaliseMailgunDomain(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();

        // Strip any "scheme://" prefix.
        var schemeIdx = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0) s = s[(schemeIdx + 3)..];

        // Drop any path / query / fragment after the host.
        var pathIdx = s.IndexOfAny(new[] { '/', '?', '#' });
        if (pathIdx >= 0) s = s[..pathIdx];

        return s.Trim().TrimEnd('.');
    }
}
