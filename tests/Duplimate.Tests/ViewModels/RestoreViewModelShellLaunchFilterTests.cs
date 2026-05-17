using System;
using Avalonia.Headless.XUnit;
using Duplimate.Services;
using Duplimate.ViewModels;
using Xunit;

namespace Duplimate.Tests.ViewModels;

/// <summary>
/// The shell-launch flow pre-fills a sticky path filter so the user
/// lands on the file they right-clicked. Earlier this was just a
/// pre-fill of the editable FileFilter textbox — clearing it
/// (intentionally or not) lost the narrow.
///
/// These tests pin the new behaviour: the sticky path lives on
/// <see cref="RestoreViewModel.ShellLaunchFilterPath"/>, only the
/// chip's Clear button removes it, and editing FileFilter narrows
/// further without disturbing the sticky filter.
/// </summary>
public class RestoreViewModelShellLaunchFilterTests
{
    [AvaloniaFact]
    public void ShellLaunchFilter_isSticky_FileFilterEdit_doesNotClearIt()
    {
        ResetConfig();
        var vm = new RestoreViewModel();

        SeedFiles(vm,
            "Documents/letter.docx",
            "Documents/notes.txt",
            "Pictures/cat.jpg");

        // Simulate a right-click "Restore older version" landing on
        // letter.docx — the sticky filter narrows to that path.
        vm.ShellLaunchFilterPath = "Documents/letter.docx";

        Assert.True(vm.HasShellLaunchFilter);
        Assert.Single(vm.FilteredFiles);
        Assert.Equal("Documents/letter.docx", vm.FilteredFiles[0].Path);

        // User types in the search box. Sticky filter must survive,
        // and the typed text narrows on top of it (here: typed text
        // doesn't match → empty result, not "back to all files").
        vm.FileFilter = "nothing-matches-this";
        Assert.True(vm.HasShellLaunchFilter);
        Assert.Equal("Documents/letter.docx", vm.ShellLaunchFilterPath);
        Assert.Empty(vm.FilteredFiles);

        // User clears the search box. Sticky filter still applied.
        vm.FileFilter = "";
        Assert.True(vm.HasShellLaunchFilter);
        Assert.Single(vm.FilteredFiles);
    }

    [AvaloniaFact]
    public void ClearShellLaunchFilterCommand_clearsStickyOnly_leavesFileFilterAlone()
    {
        ResetConfig();
        var vm = new RestoreViewModel();
        SeedFiles(vm,
            "Documents/letter.docx",
            "Documents/notes.txt",
            "Pictures/cat.jpg");

        vm.ShellLaunchFilterPath = "Documents/letter.docx";
        vm.FileFilter = "Documents";

        // Clear chip → sticky drops, FileFilter survives, list now
        // reflects only the typed narrow.
        vm.ClearShellLaunchFilterCommand.Execute(null);

        Assert.False(vm.HasShellLaunchFilter);
        Assert.Null(vm.ShellLaunchFilterPath);
        Assert.Equal("Documents", vm.FileFilter);
        Assert.Equal(2, vm.FilteredFiles.Count); // letter.docx + notes.txt
    }

    [AvaloniaFact]
    public void StartOver_dropsBothFilters_soNextRunStartsClean()
    {
        ResetConfig();
        var vm = new RestoreViewModel();
        SeedFiles(vm, "a", "b");

        vm.ShellLaunchFilterPath = "a";
        vm.FileFilter = "b";

        vm.StartOverCommand.Execute(null);

        Assert.False(vm.HasShellLaunchFilter);
        Assert.Null(vm.ShellLaunchFilterPath);
        Assert.Equal("", vm.FileFilter);
    }

    /// <summary>
    /// Drop a small set of synthetic files into the VM's AllFiles
    /// collection so we can exercise <c>ApplyFileFilter</c> directly
    /// without touching <c>ServiceLocator.Revisions</c>. Mirrors what
    /// <see cref="RestoreViewModel.LoadFilesAsync"/> does after the
    /// revision browser returns. Each test sets a filter property
    /// after seeding, which is what re-runs the filter.
    /// </summary>
    private static void SeedFiles(RestoreViewModel vm, params string[] paths)
    {
        foreach (var p in paths)
            vm.AllFiles.Add(new RevisionFileRow(
                new RevisionFile(p, SizeBytes: 1, ModifiedUtc: DateTime.UtcNow), vm));
    }

    private static void ResetConfig()
    {
        if (!ServiceLocator.Initialized) ServiceLocator.InitializeCore();
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Backups.Clear();
            cfg.Destinations.Clear();
        });
    }
}
