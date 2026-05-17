using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Duplimate.Tests.TestHelpers;
using Duplimate.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Duplimate.Tests.ViewModels;

/// <summary>
/// Pure-VM tests of <see cref="SourceTreePickerViewModel.Confirm"/> that
/// finally pin down the recurring "ticked deep folder, got the parent
/// path back" bug the user has flagged five times now. These tests use
/// REAL disk structure (under TempWorkspace) so the lazy-populate path
/// runs the same way it does in production. They do NOT spin up the
/// Avalonia window — pure model behaviour.
///
/// The shape these tests pin:
///   1. Fresh-pick of a deep folder returns the deep folder, NOT the
///      parent.
///   2. Re-open with the parent as a preset, narrow to a deep child
///      (untick parent, tick child), confirm returns ONLY the deep
///      child.
///   3. Re-open with the deep child as a preset, no edits, confirm
///      returns the deep child.
///   4. The deep folder name can contain non-trivial characters
///      ("Notepad++ Portable") without the cover walk losing it.
/// </summary>
public class SourceTreePickerCoverTests
{
    private readonly ITestOutputHelper _out;
    public SourceTreePickerCoverTests(ITestOutputHelper output) => _out = output;

    /// <summary>Invoke the picker VM's private Confirm command and
    /// return the resulting cover list (or null on Cancel). Reflection
    /// because <c>[RelayCommand]</c> generates a private invoker.</summary>
    private static List<string>? RunConfirm(SourceTreePickerViewModel vm)
    {
        var cmd = vm.GetType().GetProperty("ConfirmCommand",
            BindingFlags.Public | BindingFlags.Instance)?.GetValue(vm);
        cmd!.GetType().GetMethod("Execute")!.Invoke(cmd, new object?[] { null });
        return vm.Result;
    }

    /// <summary>
    /// Replace the default drives-root list with a single synthetic root
    /// pointing at a disk path we control. The VM was designed to read
    /// every DriveInfo from the OS, which is impractical to test;
    /// reaching into Roots is the seam the integration test already uses.
    /// </summary>
    private static FileSystemNode SeedRoot(SourceTreePickerViewModel vm, string rootPath)
    {
        vm.Roots.Clear();
        var node = new FileSystemNode(rootPath, "root", isDirectory: true, isMounted: true);
        // Re-apply the pre-tick set so seeded paths still cascade — the
        // VM constructor stored it on the original Roots[0] but we just
        // replaced that.
        var pretickedField = typeof(SourceTreePickerViewModel)
            .GetField("_initiallyChecked", BindingFlags.NonPublic | BindingFlags.Instance);
        var preticked = (HashSet<string>)pretickedField!.GetValue(vm)!;
        node.PreTickedPaths = preticked;
        // Seed the root's checked state from the preset list (mimic
        // the constructor's SeedCheckedFromInitial; we can't call the
        // private method, so replicate inline — its only behaviour is
        // to mark the root as partial when a preset descends from it).
        foreach (var p in preticked)
        {
            if (FileSystemNode.NormalizeForCompare(p)
                    .StartsWith(FileSystemNode.NormalizeForCompare(rootPath),
                                System.StringComparison.OrdinalIgnoreCase))
            {
                node.SetCheckedWithoutPropagation(null);
                break;
            }
        }
        vm.Roots.Add(node);
        return node;
    }

    /// <summary>Walk the materialised tree to find a node by path. Forces
    /// IsExpanded on each ancestor to materialise children on the way
    /// down — what EagerExpandToPresets does in production, exposed here
    /// for tests that simulate "the user clicked the chevron".</summary>
    private static FileSystemNode? Drill(FileSystemNode start, string targetPath)
    {
        var target = FileSystemNode.NormalizeForCompare(targetPath);
        var startKey = FileSystemNode.NormalizeForCompare(start.Path);
        if (string.Equals(startKey, target, System.StringComparison.OrdinalIgnoreCase))
            return start;

        // Expand to lazy-populate children, then find the child whose
        // path is an ancestor-or-equal of the target.
        if (!start.IsExpanded) start.IsExpanded = true;
        foreach (var c in start.Children)
        {
            if (c.IsPlaceholder || c.Path is null) continue;
            var ck = FileSystemNode.NormalizeForCompare(c.Path);
            if (string.Equals(ck, target, System.StringComparison.OrdinalIgnoreCase))
                return c;
            if (target.StartsWith(ck + Path.DirectorySeparatorChar,
                    System.StringComparison.OrdinalIgnoreCase))
                return Drill(c, targetPath);
        }
        return null;
    }

    [Fact]
    public void FreshPick_DeepFolder_CoverHoldsTheDeepFolder()
    {
        // The exact scenario the user reported: a fresh tree, no presets,
        // user drills down to D:\Antigravity\Shared\Notepad++ Portable
        // and ticks ONLY that node. The cover should be EXACTLY that
        // path — not the parent, not the root.
        using var ws = new TempWorkspace("picker-deep-fresh");
        var deep = Path.Combine(ws.Root, "Antigravity", "Shared", "Notepad++ Portable");
        Directory.CreateDirectory(deep);
        Directory.CreateDirectory(Path.Combine(ws.Root, "Antigravity", "Shared", "Other"));
        Directory.CreateDirectory(Path.Combine(ws.Root, "Antigravity", "Sibling"));

        var vm = new SourceTreePickerViewModel(initiallyChecked: System.Array.Empty<string>());
        var root = SeedRoot(vm, ws.Root);

        var leaf = Drill(root, deep);
        Assert.NotNull(leaf);
        // Simulate the user clicking the unchecked leaf's checkbox.
        // OnIsCheckedChanged drives the down + up propagation.
        leaf!.IsChecked = true;

        var cover = RunConfirm(vm);
        Assert.NotNull(cover);
        _out.WriteLine("cover = " + string.Join(" | ", cover!));
        Assert.Single(cover!);
        Assert.Equal(deep, cover![0], ignoreCase: true);
    }

    [Fact]
    public void Reopen_PresetIsParent_NarrowToChild_CoverHoldsOnlyTheChild()
    {
        // Re-open scenario: previously-saved source list contains the
        // parent (e.g. "D:\Antigravity\Shared"). The user opens the
        // picker, unticks the parent, drills into a deep child, ticks
        // it. The cover MUST hold only the deep child — adding the
        // parent back would either duplicate the chain OR override the
        // user's narrowing intent.
        using var ws = new TempWorkspace("picker-narrow");
        var parent = Path.Combine(ws.Root, "Antigravity", "Shared");
        var child  = Path.Combine(parent, "Notepad++ Portable");
        Directory.CreateDirectory(child);
        Directory.CreateDirectory(Path.Combine(parent, "Other"));

        var vm = new SourceTreePickerViewModel(initiallyChecked: new[] { parent });
        var root = SeedRoot(vm, ws.Root);

        // Drill to the parent first to materialise children + apply
        // the preset (parent gets ticked because it equals the preset).
        var parentNode = Drill(root, parent);
        Assert.NotNull(parentNode);
        Assert.True(parentNode!.IsChecked == true,
            $"Precondition: parent should be ticked from preset. IsChecked={parentNode.IsChecked}");

        // User unticks the parent.
        parentNode.IsChecked = false;
        // Now expand the parent so its children materialise (Drill
        // returned the matched parent node WITHOUT expanding it — its
        // Children still holds the lazy-load placeholder until the
        // user — or this line — flips IsExpanded). Then locate the
        // deep child amongst the real children.
        parentNode.IsExpanded = true;
        var childNode = parentNode.Children.First(
            c => !c.IsPlaceholder
              && string.Equals(c.Path, child, System.StringComparison.OrdinalIgnoreCase));
        // User ticks the deep child only.
        childNode.IsChecked = true;

        var cover = RunConfirm(vm);
        Assert.NotNull(cover);
        _out.WriteLine("cover = " + string.Join(" | ", cover!));
        Assert.Single(cover!);
        Assert.Equal(child, cover![0], ignoreCase: true);
    }

    private void DumpTree(FileSystemNode n, string indent = "")
    {
        _out.WriteLine($"{indent}{n.Path} IsChecked={n.IsChecked} Children={n.Children.Count}");
        foreach (var c in n.Children)
        {
            if (c.IsPlaceholder) continue;
            DumpTree(c, indent + "  ");
        }
    }

    [Fact]
    public void Reopen_DeepPresetUnchanged_CoverHoldsTheDeepPreset()
    {
        // The "I just opened the picker and clicked Save" case the user
        // also flagged in earlier rounds. Preset is the deep child;
        // user touches nothing; cover should hold the deep child path
        // exactly.
        using var ws = new TempWorkspace("picker-deep-passthrough");
        var parent = Path.Combine(ws.Root, "Antigravity", "Shared");
        var child  = Path.Combine(parent, "Notepad++ Portable");
        Directory.CreateDirectory(child);

        var vm = new SourceTreePickerViewModel(initiallyChecked: new[] { child });
        SeedRoot(vm, ws.Root);

        // Don't touch anything — emulate the constructor's
        // EagerExpandToPresets to pre-expand the chain (which the real
        // ctor runs). We can't call the private method directly, so
        // walk the chain manually with the same effect.
        Drill(vm.Roots[0], child);

        DumpTree(vm.Roots[0]);

        var cover = RunConfirm(vm);
        Assert.NotNull(cover);
        _out.WriteLine("cover = " + string.Join(" | ", cover!));
        Assert.Single(cover!);
        Assert.Equal(child, cover![0], ignoreCase: true);
    }

    [Fact]
    public void DeepFolderName_WithSpecialCharacters_SurvivesTheCoverWalk()
    {
        // Folder names with '+' or ' ' shouldn't trip path comparisons.
        // This was a hypothesis in the most recent regression; this
        // test pins it.
        using var ws = new TempWorkspace("picker-special-chars");
        var deep = Path.Combine(ws.Root, "Antigravity", "C++ Build", "Special & Folder");
        Directory.CreateDirectory(deep);

        var vm = new SourceTreePickerViewModel(initiallyChecked: System.Array.Empty<string>());
        var root = SeedRoot(vm, ws.Root);

        var leaf = Drill(root, deep);
        Assert.NotNull(leaf);
        leaf!.IsChecked = true;

        var cover = RunConfirm(vm);
        Assert.NotNull(cover);
        Assert.Single(cover!);
        Assert.Equal(deep, cover![0], ignoreCase: true);
    }
}
