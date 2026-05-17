using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Duplimate.Models;
using Serilog;

namespace Duplimate.Services;

/// <summary>
/// Verifies that a just-captured Dropbox OAuth token is live, and tells the
/// user what it maps to.
///
/// Why this exists as a service instead of just "try a backup and see":
/// non-technical users need feedback within seconds of pasting a token,
/// not minutes into their first backup. Health-checks here complete in
/// ~2–3 seconds against Dropbox.
///
/// Scope-detection caveats (read before extending this):
///   • The token returned by https://duplicacy.com/dropbox_start is a
///     Dropbox OAuth2 *refresh* token, issued under duplicacy.com's Dropbox
///     app registration (client_id=vp00fqqtagpzk0l). Direct API calls
///     (Bearer header against api.dropboxapi.com) return HTTP 401 because
///     refresh tokens are not access tokens. Exchanging refresh → access
///     requires duplicacy.com's OAuth client_secret, which we do not own
///     and cannot ethically use.
///   • As a result, we cannot call GET /2/users/get_current_account
///     ourselves to retrieve the user's email, nor probe the path
///     namespace to detect whether the app is registered as "App folder"
///     vs "Full Dropbox".
///   • duplicacy.com hosts exactly one Dropbox OAuth helper URL:
///     /dropbox_start. Verified empirically — every other variant
///     (/dropbox_full_start, /dropbox_full, /dropbox_fullaccess, …)
///     soft-302's to /home.html. That URL's OAuth request omits the
///     `scope=` parameter, so the app's registered access type is what
///     you get, which for this client_id is App folder. Files land at
///     /Apps/Duplicacy/ in the user's Dropbox.
///   • Consequence: if Duplimate ever needs full-Dropbox access, it
///     can't come through duplicacy.com — we'd have to register our own
///     Dropbox app with "Full Dropbox" access type and host the OAuth
///     helper ourselves. That's tracked via DropboxFullAccess returning
///     null from OAuthHelperUrlFor today.
///   • Health-check below uses duplicacy.exe as the bridge — it holds the
///     OAuth client_secret internally and can exchange the refresh token.
///     A successful `list` against a probe storage URL means the token
///     is valid and Dropbox is reachable. It does NOT assert the scope;
///     scope assertion is compile-time via OAuthHelperUrlFor in
///     DestinationEditorViewModel.
/// </summary>
public sealed class DropboxAuthProbe
{
    private static ILogger _log => AppLogger.For<DropboxAuthProbe>();
    private readonly SecretsStore _secrets;

    public DropboxAuthProbe(SecretsStore secrets) => _secrets = secrets;

    /// <summary>
    /// Runs a fast, side-effect-free check against Dropbox using the
    /// provided refresh token. Spawns duplicacy.exe with a throwaway
    /// storage configuration that never touches the user's real backups.
    /// </summary>
    public async Task<ProbeResult> HealthCheckAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return ProbeResult.Fail("Paste a token from the Dropbox helper first.");

        _log.Information("Dropbox token health-check starting (token length={Len})", refreshToken.Length);

        // Ephemeral repo so nothing we do here touches an existing backup's
        // .duplicacy/preferences. Clean up regardless of the outcome.
        var probeRepo = Path.Combine(Path.GetTempPath(), $"duplimate-dbx-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(probeRepo);
        try
        {
            // FIXED remote subpath (no per-call GUID). The previous design
            // used `__duplimate_probe/{Guid.NewGuid():N}` so repeated probes
            // wouldn't collide — but we never deleted the remote folder
            // afterwards, so every Add/Edit of a Dropbox destination
            // accumulated yet another probe folder in the user's
            // /Apps/Duplicacy/ directory. With a fixed path, a probe
            // against an already-init'd storage is a no-op (duplicacy
            // detects the existing config and skips re-init), and the
            // user accumulates AT MOST ONE probe folder ever. Naming
            // it with our app prefix and "token-check" makes its
            // purpose obvious to a user inspecting their Dropbox.
            //
            // Existing pre-fix folders (named __duplimate_probe/<guid>)
            // remain in the user's Dropbox until they remove them by
            // hand — we don't have a clean way to enumerate-and-delete
            // remote paths with a refresh-token-only credential.
            const string remoteSubpath = "duplimate-tokencheck";
            var storageUrl = $"dropbox://{remoteSubpath}";
            var duplicacyExe = DuplicacyEmbedder.EnsureExtracted();

            // duplicacy init — creates .duplicacy/preferences and, critically,
            // forces duplicacy to exchange the refresh token for an access
            // token against Dropbox. That round-trip is what actually
            // validates the token; init alone doesn't talk to Dropbox until
            // it needs to (it lazy-inits on first API call in list).
            var initArgs = new[]
            {
                "init",
                "-storage-name", "default",
                "-repository",   probeRepo,
                "duplimate_probe",
                storageUrl,
            };
            var (initExit, initOut) = await RunAsync(duplicacyExe, probeRepo, initArgs, refreshToken, ct);
            if (initExit != 0)
            {
                _log.Warning("Dropbox probe init failed (exit {Exit}): {Tail}",
                    initExit, SummarizeError(initOut, refreshToken, "<no output>"));
                return ProbeResult.Fail(SummarizeError(initOut, refreshToken,
                    "Dropbox refused the token. It may have been revoked, expired, or truncated in paste."));
            }

            // duplicacy list — lightweight call that forces a real Dropbox
            // API round-trip (tries to list snapshot revisions). Probe
            // storage has none, so empty output = reachable and authorized.
            var listArgs = new[] { "-log", "list", "-storage", "default" };
            var (listExit, listOut) = await RunAsync(duplicacyExe, probeRepo, listArgs, refreshToken, ct);
            if (listExit != 0)
            {
                _log.Warning("Dropbox probe list failed (exit {Exit}): {Tail}",
                    listExit, SummarizeError(listOut, refreshToken, "<no output>"));
                return ProbeResult.Fail(SummarizeError(listOut, refreshToken,
                    "Dropbox accepted the token but then refused the request. Try the token again or re-authenticate."));
            }

            _log.Information("Dropbox token validated against the API");
            return ProbeResult.Ok(
                "Connected to Dropbox. Token is valid.");
        }
        finally
        {
            try { Directory.Delete(probeRepo, recursive: true); }
            catch (Exception ex)
            {
                _log.Warning(ex, "Couldn't clean Dropbox probe repo {Path}", probeRepo);
            }
        }
    }

    private static async Task<(int exit, string output)> RunAsync(
        string exe, string cwd, string[] args, string dropboxToken, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        // Hand duplicacy the token via env var so it doesn't prompt on stdin.
        psi.Environment["DUPLICACY_DROPBOX_TOKEN"] = dropboxToken;

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var buf = new StringBuilder();
        var gate = new object();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (gate) buf.AppendLine(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) lock (gate) buf.AppendLine("[err] " + e.Data); };

        p.Start();
        KillOnExitJobObject.TryAssign(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { p.StandardInput.Close(); } catch { }

        using (ct.Register(() => { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { } }))
        {
            // Pass `ct` so a cancellation whose Kill failed still
            // unwinds the wait — without this, a stuck duplicacy
            // probe pegged the thread forever.
            await p.WaitForExitAsync(ct);
        }
        return (p.ExitCode, buf.ToString());
    }

    /// <summary>
    /// Pulls the most actionable line out of duplicacy's stdout, redacting
    /// the OAuth refresh token if it happens to appear in the output.
    ///
    /// Duplicacy emits verbose logs; most of it is noise to a non-technical
    /// user, but the ERROR lines usually contain a clear cause. We forward
    /// those — but never the token — so the user sees what went wrong
    /// without leaking credentials into log files or status messages.
    /// </summary>
    private static string SummarizeError(string raw, string secret, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // First ERROR line usually carries the user-facing cause.
            var idx = line.IndexOf(" ERROR ", StringComparison.Ordinal);
            if (idx < 0) continue;
            var msg = line[(idx + " ERROR ".Length)..].Trim();
            if (string.IsNullOrWhiteSpace(msg)) continue;
            msg = Redact(msg, secret);
            return msg.Length > 240 ? msg[..240] + "…" : msg;
        }
        return fallback;
    }

    /// <summary>
    /// Replaces the OAuth refresh token (and common token-shape patterns)
    /// with a sentinel so summarized error text is safe to log or display.
    /// Exposed as <c>internal</c> so the test suite can assert that
    /// nothing token-shaped survives.
    /// </summary>
    internal static string Redact(string text, string secret)
    {
        if (!string.IsNullOrEmpty(secret) && secret.Length >= 8)
            text = text.Replace(secret, "[REDACTED]");
        // Defensive: token-like patterns Dropbox / OAuth helpers sometimes
        // print on their own (long base64-ish strings, sl.* refresh tokens,
        // bearer tokens). Cheap belt-and-braces in case the exact secret
        // isn't echoed but a derivative of it is.
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"\bsl\.[A-Za-z0-9_\-]{20,}\b", "[REDACTED]");
        // Header-style "Bearer xyz" / "Token: xyz" / "access_token=xyz" —
        // match a separator that's space, colon, or equals.
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"(?i)\b(bearer|token|access_token|refresh_token)\s*[:=\s]\s*[A-Za-z0-9_\-\.]{16,}",
            m => m.Groups[1].Value + "=[REDACTED]");
        return text;
    }
}

public readonly record struct ProbeResult(bool Healthy, string Message)
{
    public static ProbeResult Ok(string msg)   => new(true, msg);
    public static ProbeResult Fail(string msg) => new(false, msg);
}
