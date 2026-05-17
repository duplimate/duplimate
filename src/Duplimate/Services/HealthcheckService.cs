using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Duplimate.Models;
using Serilog;

namespace Duplimate.Services;

/// <summary>
/// Healthchecks.io integration. Two audiences:
///
/// 1) Management (API key): create/update checks on the user's behalf so they don't
///    have to touch the web UI — they paste their key, we handle naming/tagging and
///    setting the expected schedule from the Task Scheduler trigger.
///
/// 2) Pings (ping-key in the check's URL): called at the end of a run with /fail
///    or success. Ping doesn't need the management API key.
/// </summary>
public sealed class HealthcheckService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static ILogger _log => AppLogger.For<HealthcheckService>();
    private readonly ConfigStore _config;
    private readonly SecretsStore _secrets;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public HealthcheckService(ConfigStore config, SecretsStore secrets)
    {
        _config = config;
        _secrets = secrets;
    }

    private string BaseUrl => _config.Current.Monitoring.HealthchecksBaseUrl.TrimEnd('/');

    private bool TryGetApiKey(out string key)
    {
        var @ref = _config.Current.Monitoring.HealthchecksApiKeyRef;
        if (!string.IsNullOrEmpty(@ref) && _secrets.TryGet(@ref, out key!) && !string.IsNullOrEmpty(key))
            return true;
        key = "";
        return false;
    }

    // ---- management ----

    public async Task<HealthcheckRecord?> UpsertCheckAsync(Backup backup, CancellationToken ct)
    {
        if (!TryGetApiKey(out var apiKey))
        {
            _log.Information("Skipping Healthchecks.io upsert for {Backup} — no API key configured", backup.Name);
            return null;
        }

        // Translate the backup schedule to a (schedule, tz) Healthchecks understands.
        // Daily/Weekly/Hourly → pick either "timeout" (simple) or "cron".
        // We use cron for anything more specific than a fixed cadence.
        var body = new UpsertRequest
        {
            Name = backup.Name,
            Tags = $"duplimate backup-{backup.Name}",
            Desc = $"Auto-managed by Duplimate. Sources: {string.Join(" ; ", backup.SourcePaths)}",
            // Generous grace period: default 4h so a slow-but-OK backup doesn't page us.
            Grace = (int)TimeSpan.FromHours(4).TotalSeconds,
            UniqueBy = new[] { "name", "tags" },
        };

        var (isCron, cron) = BuildCron(backup);
        if (isCron)
        {
            body.Schedule = cron;
            body.Tz = TimeZoneInfo.Local.Id;
        }
        else
        {
            body.Timeout = (int)ComputeTimeoutSeconds(backup);
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/v3/checks/");
        req.Headers.Add("X-Api-Key", apiKey);
        req.Content = JsonContent.Create(body, options: Json);

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.Warning("Healthchecks.io upsert for {Backup} returned HTTP {Status}", backup.Name, (int)resp.StatusCode);
                return null;
            }
            var parsed = await resp.Content.ReadFromJsonAsync<HealthcheckRecord>(Json, ct);
            _log.Information("Healthchecks.io check upserted for {Backup}: {PingUrl}", backup.Name, parsed?.PingUrl);
            return parsed;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Healthchecks.io upsert failed for {Backup}", backup.Name);
            return null;
        }
    }

    public async Task<bool> DeleteCheckAsync(string checkUuid, CancellationToken ct)
    {
        if (!TryGetApiKey(out var apiKey)) return false;
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{BaseUrl}/api/v3/checks/{checkUuid}");
        req.Headers.Add("X-Api-Key", apiKey);
        using var resp = await _http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>
    /// Verifies that <paramref name="checkUuid"/> belongs to a real check
    /// in the user's Healthchecks.io project.
    ///
    /// <paramref name="inlineApiKey"/> / <paramref name="inlineBaseUrl"/>
    /// let callers (the BackupEditor's inline Healthchecks-setup card)
    /// pass credentials WITHOUT first writing them to global config —
    /// so a failed verify doesn't leak a user-pasted API key into
    /// Settings. When inline values are absent, we fall back to whatever
    /// is configured globally.
    /// </summary>
    public async Task<UuidVerificationResult> VerifyCheckUuidAsync(
        string checkUuid,
        string? inlineApiKey = null,
        string? inlineBaseUrl = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(checkUuid))
            return UuidVerificationResult.Empty();
        if (!Guid.TryParse(checkUuid.Trim(), out var parsed))
            return UuidVerificationResult.MalformedShape();

        var apiKey = !string.IsNullOrWhiteSpace(inlineApiKey)
            ? inlineApiKey.Trim()
            : (TryGetApiKey(out var globalKey) ? globalKey : "");
        if (string.IsNullOrEmpty(apiKey))
            return UuidVerificationResult.NoApiKey();

        var baseUrl = (!string.IsNullOrWhiteSpace(inlineBaseUrl) ? inlineBaseUrl.Trim() : BaseUrl).TrimEnd('/');

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v3/checks/{parsed}");
            req.Headers.Add("X-Api-Key", apiKey);
            using var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                _log.Information("Verified Healthchecks.io UUID {Uuid}", parsed);
                return UuidVerificationResult.Valid();
            }
            if ((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403)
            {
                _log.Warning("Healthchecks.io rejected API key while verifying {Uuid} (HTTP {Status})", parsed, (int)resp.StatusCode);
                return UuidVerificationResult.NotAuthorized();
            }
            if ((int)resp.StatusCode == 404)
            {
                _log.Warning("Healthchecks.io UUID {Uuid} not found in this account", parsed);
                return UuidVerificationResult.NotFound();
            }
            _log.Warning("Healthchecks.io returned HTTP {Status} verifying {Uuid}", (int)resp.StatusCode, parsed);
            return UuidVerificationResult.NetworkError($"Healthchecks.io returned HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Couldn't reach Healthchecks.io to verify {Uuid}", parsed);
            return UuidVerificationResult.NetworkError(ex.Message);
        }
    }

    public readonly record struct UuidVerificationResult(
        UuidVerificationOutcome Outcome,
        string Message)
    {
        public bool IsValid => Outcome == UuidVerificationOutcome.Valid;
        public bool IsBlockingError =>
            Outcome is UuidVerificationOutcome.MalformedShape
                       or UuidVerificationOutcome.NotFound
                       or UuidVerificationOutcome.NotAuthorized;

        public static UuidVerificationResult Empty()        => new(UuidVerificationOutcome.Empty, "");
        public static UuidVerificationResult Valid()        => new(UuidVerificationOutcome.Valid, "Verified with Healthchecks.io.");
        public static UuidVerificationResult MalformedShape()=> new(UuidVerificationOutcome.MalformedShape, "That doesn't look like a valid UUID.");
        public static UuidVerificationResult NotFound()     => new(UuidVerificationOutcome.NotFound, "Healthchecks.io says no check with this UUID exists in your project.");
        public static UuidVerificationResult NotAuthorized()=> new(UuidVerificationOutcome.NotAuthorized, "Healthchecks.io rejected the API key — check it in Settings.");
        public static UuidVerificationResult NoApiKey()     => new(UuidVerificationOutcome.NoApiKey, "No API key — set one in Settings (or fill it in below) so we can verify the UUID.");
        public static UuidVerificationResult NetworkError(string detail) => new(UuidVerificationOutcome.NetworkError, $"Couldn't reach Healthchecks.io: {detail}");
    }

    public enum UuidVerificationOutcome { Empty, Valid, MalformedShape, NotFound, NotAuthorized, NoApiKey, NetworkError }

    // ---- pings ----

    /// <summary>Send a "started" ping. Optional — some users prefer final-ping-only.</summary>
    public Task PingStartAsync(string pingUuid, CancellationToken ct) =>
        PingAsync(pingUuid, "/start", null, ct);

    public Task PingSuccessAsync(string pingUuid, string body, CancellationToken ct) =>
        PingAsync(pingUuid, "", body, ct);

    public Task PingFailureAsync(string pingUuid, string body, CancellationToken ct) =>
        PingAsync(pingUuid, "/fail", body, ct);

    private async Task PingAsync(string pingUuid, string suffix, string? body, CancellationToken ct)
    {
        var url = $"https://hc-ping.com/{pingUuid}{suffix}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrEmpty(body))
                req.Content = new StringContent(body, System.Text.Encoding.UTF8, "text/plain");
            req.Headers.UserAgent.ParseAdd($"Duplimate/{Environment.MachineName}");
            using var resp = await _http.SendAsync(req, ct);
            _log.Debug("Healthchecks.io ping {Suffix} for {Uuid}: HTTP {Status}",
                string.IsNullOrEmpty(suffix) ? "/success" : suffix, pingUuid, (int)resp.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller asked us to stop — propagate, don't disguise as
            // a network error.
            throw;
        }
        catch (Exception ex)
        {
            // Network errors shouldn't fail the backup result, but we
            // log so the user can see why their watchdog stayed silent.
            _log.Warning(ex, "Healthchecks.io ping {Suffix} for {Uuid} failed",
                string.IsNullOrEmpty(suffix) ? "/success" : suffix, pingUuid);
        }
    }

    // ---- schedule translation ----

    private static (bool isCron, string cron) BuildCron(Backup b)
    {
        var s = b.Schedule;
        return s.Frequency switch
        {
            ScheduleFrequency.Daily  => (true, $"{s.TimeOfDay.Minutes} {s.TimeOfDay.Hours} * * *"),
            ScheduleFrequency.Weekly => (true, BuildWeeklyCron(s)),
            ScheduleFrequency.Hourly => (true, $"0 */{Math.Max(1, s.HourlyInterval)} * * *"),
            _ => (false, ""),
        };
    }

    private static string BuildWeeklyCron(BackupSchedule s)
    {
        // Cron days: 0=Sun..6=Sat. We match our bitmask conventions.
        var days = new System.Collections.Generic.List<int>();
        if ((s.WeeklyDaysBitmask & 1)  != 0) days.Add(0);
        if ((s.WeeklyDaysBitmask & 2)  != 0) days.Add(1);
        if ((s.WeeklyDaysBitmask & 4)  != 0) days.Add(2);
        if ((s.WeeklyDaysBitmask & 8)  != 0) days.Add(3);
        if ((s.WeeklyDaysBitmask & 16) != 0) days.Add(4);
        if ((s.WeeklyDaysBitmask & 32) != 0) days.Add(5);
        if ((s.WeeklyDaysBitmask & 64) != 0) days.Add(6);
        var dayStr = days.Count > 0 ? string.Join(",", days) : "*";
        return $"{s.TimeOfDay.Minutes} {s.TimeOfDay.Hours} * * {dayStr}";
    }

    private static double ComputeTimeoutSeconds(Backup b) =>
        b.Schedule.Frequency switch
        {
            ScheduleFrequency.Hourly => TimeSpan.FromHours(Math.Max(1, b.Schedule.HourlyInterval) * 1.5).TotalSeconds,
            ScheduleFrequency.Daily  => TimeSpan.FromHours(30).TotalSeconds,
            ScheduleFrequency.Weekly => TimeSpan.FromDays(8).TotalSeconds,
            _ => TimeSpan.FromDays(2).TotalSeconds,
        };

    // ---- shapes ----

    private sealed class UpsertRequest
    {
        public string? Name { get; set; }
        public string? Tags { get; set; }
        public string? Desc { get; set; }
        public int? Timeout { get; set; }
        public int? Grace { get; set; }
        public string? Schedule { get; set; }
        public string? Tz { get; set; }

        [JsonPropertyName("unique")]
        public string[]? UniqueBy { get; set; }
    }

    public sealed class HealthcheckRecord
    {
        [JsonPropertyName("ping_url")]
        public string? PingUrl { get; set; }

        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        /// <summary>Convenience extractor: pull the UUID out of the ping_url.</summary>
        public string? ExtractPingUuid()
        {
            if (string.IsNullOrEmpty(PingUrl)) return null;
            var tail = PingUrl.TrimEnd('/').Split('/');
            return tail.Length > 0 ? tail[^1] : null;
        }
    }
}
