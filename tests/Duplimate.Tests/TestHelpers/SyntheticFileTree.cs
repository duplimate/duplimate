using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Duplimate.Tests.TestHelpers;

/// <summary>
/// Procedurally generates a file tree that looks plausibly like a user's
/// documents folder: mixed file sizes, mixed extensions, nested directories,
/// with content that's deterministic from the seed.
///
/// Determinism matters because we want backup/restore tests to be able to
/// regenerate identical content and hash-compare before-and-after.
/// </summary>
public static class SyntheticFileTree
{
    private static readonly (string ext, double weight, FileProfile profile)[] Profiles =
    {
        // Small text-shaped files — typical note/doc content.
        ("md",   10, new FileProfile(MinBytes: 200,      MaxBytes: 20_000,      ContentKind.Text)),
        ("txt",  10, new FileProfile(MinBytes: 100,      MaxBytes: 10_000,      ContentKind.Text)),
        ("csv",   6, new FileProfile(MinBytes: 1_000,    MaxBytes: 200_000,     ContentKind.Text)),
        ("json",  6, new FileProfile(MinBytes: 500,      MaxBytes: 80_000,      ContentKind.Text)),
        ("log",   4, new FileProfile(MinBytes: 1_000,    MaxBytes: 500_000,     ContentKind.Text)),
        // Bigger binary-shaped files — represent photos, archives, etc.
        ("jpg",   6, new FileProfile(MinBytes: 200_000,  MaxBytes: 4_000_000,   ContentKind.Binary)),
        ("png",   4, new FileProfile(MinBytes: 100_000,  MaxBytes: 2_000_000,   ContentKind.Binary)),
        ("bin",   3, new FileProfile(MinBytes: 100_000,  MaxBytes: 10_000_000,  ContentKind.Binary)),
    };

    /// <summary>
    /// Generate a directory tree at <paramref name="rootPath"/> containing
    /// deterministic content whose total size is roughly <paramref name="targetBytes"/>.
    /// </summary>
    public static FileTreeSummary Generate(
        string rootPath,
        int seed,
        long targetBytes,
        int maxDepth = 4,
        int filesPerDir = 8,
        int subdirsPerDir = 3)
    {
        Directory.CreateDirectory(rootPath);
        var rng = new Random(seed);
        var summary = new FileTreeSummary();
        GenerateDir(rootPath, rng, targetBytes, maxDepth, filesPerDir, subdirsPerDir, summary, remainingBudget: new BudgetBox(targetBytes));
        return summary;
    }

    private static void GenerateDir(
        string dir,
        Random rng,
        long targetBytes,
        int maxDepth,
        int filesPerDir,
        int subdirsPerDir,
        FileTreeSummary summary,
        BudgetBox remainingBudget)
    {
        var fileCount = rng.Next(Math.Max(1, filesPerDir / 2), filesPerDir + 1);
        for (int i = 0; i < fileCount && remainingBudget.Bytes > 0; i++)
        {
            var profile = PickProfile(rng);
            var size = PickSize(rng, profile.profile, remainingBudget.Bytes);
            if (size <= 0) continue;
            var name = $"{FriendlyName(rng, i)}.{profile.ext}";
            var path = Path.Combine(dir, name);
            WriteFile(path, profile.profile.Kind, size, rng);
            summary.FileCount++;
            summary.TotalBytes += size;
            remainingBudget.Bytes -= size;
        }

        if (maxDepth <= 0 || remainingBudget.Bytes <= 0) return;

        var subdirCount = rng.Next(0, subdirsPerDir + 1);
        for (int i = 0; i < subdirCount && remainingBudget.Bytes > 0; i++)
        {
            var sub = Path.Combine(dir, $"{FolderName(rng, i)}");
            Directory.CreateDirectory(sub);
            summary.DirCount++;
            GenerateDir(sub, rng, targetBytes, maxDepth - 1, filesPerDir, subdirsPerDir, summary, remainingBudget);
        }
    }

    private static (string ext, double weight, FileProfile profile) PickProfile(Random rng)
    {
        var total = Profiles.Sum(p => p.weight);
        var roll = rng.NextDouble() * total;
        double running = 0;
        foreach (var p in Profiles)
        {
            running += p.weight;
            if (roll <= running) return p;
        }
        return Profiles[^1];
    }

    private static long PickSize(Random rng, FileProfile profile, long budget)
    {
        if (budget <= 0) return 0;
        var max = Math.Min(profile.MaxBytes, budget);
        var min = Math.Min(profile.MinBytes, max);
        if (max <= min) return max;
        return min + (long)(rng.NextDouble() * (max - min));
    }

    private static void WriteFile(string path, ContentKind kind, long size, Random rng)
    {
        if (kind == ContentKind.Text)
        {
            WriteTextFile(path, size, rng);
        }
        else
        {
            WriteBinaryFile(path, size, rng);
        }
    }

    private static void WriteTextFile(string path, long size, Random rng)
    {
        // Emit a stream of lorem-ipsum-style words so compression ratios and
        // chunking behave like real text. Stop when we cross the size budget
        // (it will be slightly over, which is fine — we just need "roughly").
        using var stream = File.Create(path);
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        long written = 0;
        int lineLen = 0;
        while (written < size)
        {
            var word = Words[rng.Next(Words.Length)];
            writer.Write(word);
            writer.Write(' ');
            written += word.Length + 1;
            lineLen += word.Length + 1;
            if (lineLen > 70)
            {
                writer.WriteLine();
                written += 2;
                lineLen = 0;
            }
        }
    }

    private static void WriteBinaryFile(string path, long size, Random rng)
    {
        // Use RNG-filled buffer rather than zero-bytes so Duplicacy's chunker
        // doesn't trivially dedupe everything to a single 0-byte chunk.
        const int bufSize = 64 * 1024;
        var buf = new byte[bufSize];
        using var stream = File.Create(path);
        long remaining = size;
        while (remaining > 0)
        {
            rng.NextBytes(buf);
            var chunk = (int)Math.Min(remaining, bufSize);
            stream.Write(buf, 0, chunk);
            remaining -= chunk;
        }
    }

    private static string FriendlyName(Random rng, int idx) =>
        $"{Adjectives[rng.Next(Adjectives.Length)]}_{Nouns[rng.Next(Nouns.Length)]}_{idx:D3}";

    private static string FolderName(Random rng, int idx) =>
        $"{Nouns[rng.Next(Nouns.Length)]}_{idx:D2}";

    private static readonly string[] Words =
    {
        "lorem", "ipsum", "dolor", "sit", "amet", "consectetur", "adipiscing", "elit",
        "sed", "do", "eiusmod", "tempor", "incididunt", "ut", "labore", "magna", "aliqua",
        "enim", "minim", "veniam", "quis", "nostrud", "exercitation", "ullamco", "laboris",
        "nisi", "aliquip", "commodo", "consequat", "duis", "aute", "irure", "reprehenderit",
        "voluptate", "velit", "esse", "cillum", "pariatur", "excepteur", "sint", "occaecat",
        "cupidatat", "proident", "culpa", "officia", "deserunt", "mollit", "anim", "laborum",
    };

    private static readonly string[] Adjectives =
    {
        "silent", "quick", "small", "large", "old", "new", "bright", "dim",
        "red", "blue", "green", "frozen", "warm", "ancient", "modern", "crisp",
    };

    private static readonly string[] Nouns =
    {
        "notes", "photos", "budget", "memo", "draft", "report", "receipt", "letter",
        "taxes", "trip", "meeting", "recipe", "journal", "design", "contract", "slides",
    };

    private enum ContentKind { Text, Binary }
    private sealed record FileProfile(long MinBytes, long MaxBytes, ContentKind Kind);

    // Box to let the recursive call decrement a shared budget.
    private sealed class BudgetBox { public long Bytes; public BudgetBox(long b) { Bytes = b; } }
}

public sealed class FileTreeSummary
{
    public int FileCount { get; internal set; }
    public int DirCount { get; internal set; } = 1; // root
    public long TotalBytes { get; internal set; }

    public override string ToString() =>
        $"{FileCount} files, {DirCount} dirs, {TotalBytes:N0} bytes";
}

/// <summary>
/// Directory hash: SHA-256 over (relative-path, content-hash) tuples sorted
/// by path. Lets us compare before-and-after directories for backup/restore
/// equivalence without trusting file metadata (timestamps, ACLs).
///
/// Skips <c>.duplicacy/</c> subtrees by default. Duplicacy's restore command
/// materializes its own repo metadata + chunk cache next to the restored
/// files, which we don't want to include when comparing user content.
/// </summary>
public static class DirectoryHash
{
    public static string Compute(string rootPath, params string[] skipSegments)
    {
        // Always skip .duplicacy/ — callers can opt in to more.
        var skipSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".duplicacy" };
        foreach (var s in skipSegments) skipSet.Add(s);

        var entries = new List<(string rel, string hash)>();
        foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
            if (rel.Split('/').Any(seg => skipSet.Contains(seg))) continue;

            using var stream = File.OpenRead(file);
            using var sha = SHA256.Create();
            var h = Convert.ToHexString(sha.ComputeHash(stream));
            entries.Add((rel, h));
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.rel, b.rel));

        using var outer = SHA256.Create();
        using var buf = new MemoryStream();
        using (var w = new StreamWriter(buf, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var (rel, hash) in entries)
                w.WriteLine($"{rel}\t{hash}");
        }
        buf.Position = 0;
        return Convert.ToHexString(outer.ComputeHash(buf));
    }
}
