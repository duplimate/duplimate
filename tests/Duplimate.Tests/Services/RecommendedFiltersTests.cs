using System.Linq;
using Duplimate.Services;
using Xunit;

namespace Duplimate.Tests.Services;

public class RecommendedFiltersTests
{
    [Fact]
    public void RenderBlock_includesHeaderAndFooterMarkers()
    {
        var block = RecommendedFilters.RenderBlock(
            includeWindowsSystem: true,
            includeCachesAndJunk: false,
            cloudExcludes: System.Array.Empty<RecommendedFilters.CloudSyncFolder>());
        Assert.Contains(RecommendedFilters.AutoBlockHeader, block);
        Assert.Contains(RecommendedFilters.AutoBlockFooter, block);
    }

    [Fact]
    public void RenderBlock_includesAtLeastOnePagefilePattern_whenWindowsSelected()
    {
        var block = RecommendedFilters.RenderBlock(
            includeWindowsSystem: true,
            includeCachesAndJunk: false,
            cloudExcludes: System.Array.Empty<RecommendedFilters.CloudSyncFolder>());
        Assert.Contains("pagefile", block);
        Assert.Contains("hiberfil", block);
        Assert.Contains("System Volume Information", block);
    }

    [Fact]
    public void RenderBlock_omitsBroadWindowsPatterns_whenNotSelected_butKeepsEssentials()
    {
        // After the refactor, "essentials" (pagefile, recycle bin,
        // $WinREAgent, etc.) are ALWAYS emitted regardless of toggles —
        // they protect the user even if they later add a drive root as
        // a Source. The toggle now only gates the broader Windows
        // system patterns (the entire Windows/ folder, MSOCache,
        // thumbs.db).
        var block = RecommendedFilters.RenderBlock(
            includeWindowsSystem: false,
            includeCachesAndJunk: true,
            cloudExcludes: System.Array.Empty<RecommendedFilters.CloudSyncFolder>());

        // Toggleable broad-system patterns are absent.
        Assert.DoesNotContain("^Windows/", block);
        Assert.DoesNotContain("MSOCache", block);

        // Essentials remain.
        Assert.Contains("pagefile", block);
        Assert.Contains("\\$Recycle\\.Bin", block);
        Assert.Contains("\\$WinREAgent", block);

        // Caches block (from the second toggle) is present.
        Assert.Contains("temp", block);
    }

    [Fact]
    public void RenderBlock_emitsRegexEscapedCloudPaths()
    {
        var folder = new RecommendedFilters.CloudSyncFolder(
            RecommendedFilters.CloudSyncProvider.Dropbox,
            "Dropbox",
            @"C:\Users\me\Dropbox");
        var block = RecommendedFilters.RenderBlock(
            includeWindowsSystem: false,
            includeCachesAndJunk: false,
            cloudExcludes: new[] { folder });
        Assert.Contains(@"Users/me/Dropbox", block);
        // The label comment shows the original path so the user can
        // identify what was excluded.
        Assert.Contains(@"C:\Users\me\Dropbox", block);
    }

    [Fact]
    public void MergeIntoFilters_prependsBlock_whenNoMarkersExist()
    {
        var existing = "# my own rules\ne:(?i)/private/";
        var block = "AUTO-BLOCK";
        var merged = RecommendedFilters.MergeIntoFilters(existing, block);
        Assert.StartsWith("AUTO-BLOCK", merged);
        Assert.Contains("# my own rules", merged);
        Assert.Contains("e:(?i)/private/", merged);
    }

    [Fact]
    public void MergeIntoFilters_replacesExistingBlock_inPlace()
    {
        // The OLD block had only the broader Windows-system rules
        // (toggle ON, caches OFF). The NEW block has caches ON,
        // Windows-system OFF. Replacement should swap the toggleable
        // section while keeping the always-on essentials in both.
        var oldBlock = RecommendedFilters.RenderBlock(true, false, System.Array.Empty<RecommendedFilters.CloudSyncFolder>());
        var existing = oldBlock + "\n# user rules below\ne:/scratch/";

        var newBlock = RecommendedFilters.RenderBlock(false, true, System.Array.Empty<RecommendedFilters.CloudSyncFolder>());
        var merged = RecommendedFilters.MergeIntoFilters(existing, newBlock);

        // The replaced block has the new (caches) content.
        Assert.Contains("temp", merged);
        // The toggleable Windows-system patterns are gone (since the
        // new block has includeWindowsSystem=false).
        Assert.DoesNotContain("^Windows/", merged);
        Assert.DoesNotContain("MSOCache", merged);
        // Essentials remain — they're emitted in EVERY block render.
        Assert.Contains("pagefile", merged);
        // User's own rules below the markers are preserved.
        Assert.Contains("# user rules below", merged);
        Assert.Contains("e:/scratch/", merged);
        // No double-block — only one header/footer pair survives.
        Assert.Equal(1, CountOccurrences(merged, RecommendedFilters.AutoBlockHeader));
        Assert.Equal(1, CountOccurrences(merged, RecommendedFilters.AutoBlockFooter));
    }

    [Fact]
    public void MergeIntoFilters_emptyExisting_yieldsBlockPlusNewline()
    {
        var block = "AUTO";
        var merged = RecommendedFilters.MergeIntoFilters("", block);
        Assert.StartsWith("AUTO", merged);
    }

    [Fact]
    public void ToSourceRelativeRegex_stripsDriveLetterAndForwardSlashes()
    {
        var rel = RecommendedFilters.ToSourceRelativeRegex(@"C:\Users\me\Dropbox");
        Assert.Equal(@"Users/me/Dropbox", rel);
    }

    [Fact]
    public void ToSourceRelativeRegex_escapesRegexSpecials()
    {
        var rel = RecommendedFilters.ToSourceRelativeRegex(@"C:\Users\j.doe (work)\OneDrive");
        // dots and parens become escaped; '/' remains literal as our separator.
        Assert.Contains(@"j\.doe", rel);
        Assert.Contains(@"\(work\)", rel);
    }

    [Fact]
    public void ToSourceRelativeRegex_emptyOrInvalid_returnsNull()
    {
        Assert.Null(RecommendedFilters.ToSourceRelativeRegex(""));
        Assert.Null(RecommendedFilters.ToSourceRelativeRegex("   "));
        Assert.Null(RecommendedFilters.ToSourceRelativeRegex(@"C:\"));
    }

    [Fact]
    public void SuggestForSources_driveRoot_enablesWindowsSystem()
    {
        var d = RecommendedFilters.SuggestForSources(new[] { @"C:\" });
        Assert.True(d.ExcludeWindowsSystem);
        Assert.True(d.ExcludeCachesAndJunk);
    }

    [Fact]
    public void SuggestForSources_userFolderOnly_keepsCachesButSkipsWindowsSystem()
    {
        var d = RecommendedFilters.SuggestForSources(new[] { @"C:\Users\me\Documents" });
        Assert.False(d.ExcludeWindowsSystem);
        Assert.True(d.ExcludeCachesAndJunk);
    }

    [Fact]
    public void SuggestForSources_emptyOrNull_safe()
    {
        var d = RecommendedFilters.SuggestForSources(System.Array.Empty<string>());
        Assert.False(d.ExcludeWindowsSystem);
        Assert.True(d.ExcludeCachesAndJunk);
        var d2 = RecommendedFilters.SuggestForSources(null!);
        Assert.False(d2.ExcludeWindowsSystem);
        Assert.True(d2.ExcludeCachesAndJunk);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
