using System.IO;
using Duplimate.Tests.TestHelpers;
using Duplimate.ViewModels;
using Xunit;

namespace Duplimate.Tests.ViewModels;

/// <summary>
/// Unit tests for the tri-state checkbox propagation on
/// <see cref="FileSystemNode"/>. Checkbox propagation is the one piece
/// of the source-tree picker where a correctness bug ships silently —
/// either the user backs up more than they ticked (privacy/space) or
/// less (missing data). These tests pin every edge of the propagation
/// contract.
///
/// We build the tree on a real temp filesystem so the production
/// lazy-populate path runs end-to-end (not a reflection-poked mock).
/// </summary>
public class FileSystemNodeTests
{
    /// <summary>
    /// Build a tiny real directory tree in TEMP:
    ///   root/
    ///     A/
    ///       Sub1/
    ///       Sub2/
    ///     B/
    /// Two grandchildren under A so that unticking one leaves A in a
    /// partial state (a genuine null), which we want to test for.
    /// </summary>
    private static (TempWorkspace ws, FileSystemNode root, FileSystemNode a, FileSystemNode b, FileSystemNode sub1, FileSystemNode sub2) BuildTree()
    {
        var ws = new TempWorkspace("fsnode");
        var rootPath = Path.Combine(ws.Root, "tree");
        Directory.CreateDirectory(Path.Combine(rootPath, "A", "Sub1"));
        Directory.CreateDirectory(Path.Combine(rootPath, "A", "Sub2"));
        Directory.CreateDirectory(Path.Combine(rootPath, "B"));

        var root = new FileSystemNode(rootPath, "tree", isDirectory: true);
        root.IsExpanded = true;

        Assert.Equal(2, root.Children.Count);
        var a = root.Children[0];
        var b = root.Children[1];
        Assert.Equal("A", a.DisplayName);
        Assert.Equal("B", b.DisplayName);

        a.IsExpanded = true;
        Assert.Equal(2, a.Children.Count);
        var sub1 = a.Children[0];
        var sub2 = a.Children[1];

        return (ws, root, a, b, sub1, sub2);
    }

    [Fact]
    public void TickingParent_TicksAllDescendants()
    {
        var built = BuildTree();
        using var ws = built.ws;
        built.root.IsChecked = true;
        Assert.True(built.a.IsChecked);
        Assert.True(built.b.IsChecked);
        Assert.True(built.sub1.IsChecked);
        Assert.True(built.sub2.IsChecked);
    }

    [Fact]
    public void UntickingParent_UnticksAllDescendants()
    {
        var built = BuildTree();
        using var ws = built.ws;
        built.root.IsChecked = true;
        built.root.IsChecked = false;
        Assert.False(built.a.IsChecked);
        Assert.False(built.b.IsChecked);
        Assert.False(built.sub1.IsChecked);
        Assert.False(built.sub2.IsChecked);
    }

    [Fact]
    public void TickingOneChild_LeavesParentPartial()
    {
        var built = BuildTree();
        using var ws = built.ws;
        built.a.IsChecked = true;
        Assert.Null(built.root.IsChecked);   // partial
        Assert.False(built.b.IsChecked);
    }

    [Fact]
    public void TickingAllChildren_MakesParentFullyChecked()
    {
        var built = BuildTree();
        using var ws = built.ws;
        built.a.IsChecked = true;
        built.b.IsChecked = true;
        Assert.True(built.root.IsChecked);
    }

    [Fact]
    public void UntickingGrandchild_PropagatesPartialUpTwoLevels()
    {
        var built = BuildTree();
        using var ws = built.ws;
        built.root.IsChecked = true;
        built.sub1.IsChecked = false;
        // a now has one true child (sub2) and one false (sub1) → partial.
        // root has a partial child (a) and a true child (b) → partial.
        Assert.Null(built.a.IsChecked);
        Assert.Null(built.root.IsChecked);
    }
}
