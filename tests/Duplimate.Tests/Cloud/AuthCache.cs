using System;
using System.IO;

namespace Duplimate.Tests.Cloud;

/// <summary>
/// On-disk cache of OAuth tokens captured during interactive runs.
///
/// Tokens live under <c>tests/Duplimate.Tests/.auth-cache/</c>, one file
/// per provider. The directory is gitignored (see tests/.gitignore). A
/// token is a single-line string as captured from duplicacy.com's OAuth
/// helper page — we don't parse it, just pass it through to duplicacy.
/// </summary>
public static class AuthCache
{
    public static string CacheDir
    {
        get
        {
            var asmDir = Path.GetDirectoryName(typeof(AuthCache).Assembly.Location)!;
            // Walk up to the test project root (bin/Debug/netX.0-windows/ -> project root).
            var dir = new DirectoryInfo(asmDir);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Duplimate.Tests.csproj")))
                dir = dir.Parent;
            var root = dir?.FullName ?? asmDir;
            return Path.Combine(root, ".auth-cache");
        }
    }

    public static string PathFor(string provider)
    {
        Directory.CreateDirectory(CacheDir);
        return Path.Combine(CacheDir, $"{provider}.txt");
    }

    public static string? Read(string provider)
    {
        var path = PathFor(provider);
        if (!File.Exists(path)) return null;
        var token = File.ReadAllText(path).Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    public static void Write(string provider, string token)
    {
        File.WriteAllText(PathFor(provider), token.Trim());
    }

    /// <summary>
    /// Returns the OAuth token for <paramref name="provider"/> from the
    /// cheapest available source, in priority order:
    /// <list type="number">
    ///   <item>The <c>DUPLIMATE_{PROVIDER}_TOKEN</c> environment variable
    ///         (e.g. <c>DUPLIMATE_DROPBOX_TOKEN</c>). Preferred for one-off
    ///         runs — the token lives only in this process's environment
    ///         block, never hits disk.</item>
    ///   <item>The cached file at <see cref="PathFor"/>, if present.</item>
    ///   <item>Interactive stdin prompt (prints the helper URL to stderr,
    ///         reads the pasted token, writes it to the cache for reuse).</item>
    /// </list>
    /// </summary>
    public static string PromptForToken(string provider, string helperUrl)
    {
        var envName = $"DUPLIMATE_{provider.ToUpperInvariant()}_TOKEN";
        var fromEnv = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();

        var cached = Read(provider);
        if (cached is not null) return cached;

        Console.Error.WriteLine();
        Console.Error.WriteLine("===========================================");
        Console.Error.WriteLine($"Interactive auth needed for {provider}.");
        Console.Error.WriteLine();
        Console.Error.WriteLine($"  1. Open: {helperUrl}");
        Console.Error.WriteLine($"  2. Sign in. Copy the token the helper displays.");
        Console.Error.WriteLine($"  3. Paste it below and press ENTER.");
        Console.Error.WriteLine();
        Console.Error.WriteLine($"  (Or, to avoid the prompt, set {envName} " +
                                $"in the environment before running the test.)");
        Console.Error.WriteLine("===========================================");
        Console.Error.Write("> ");

        var token = Console.In.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                $"No {provider} token received on stdin. Set {envName}=... " +
                $"or paste a token interactively.");

        Write(provider, token);
        Console.Error.WriteLine($"Token cached to {PathFor(provider)}");
        return token;
    }
}
