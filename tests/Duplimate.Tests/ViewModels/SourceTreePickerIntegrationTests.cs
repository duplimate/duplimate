using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Duplimate.Tests.TestHelpers;
using Duplimate.ViewModels;
using Duplimate.Views;
using Xunit;
using Xunit.Abstractions;

namespace Duplimate.Tests.ViewModels;

/// <summary>
/// Integration test for the XAML + TreeView binding path of the source
/// picker — exercises the thing the user actually clicks (the TreeView's
/// expand chevron), not the model property in isolation. A previous
/// pass tested the model's tri-state propagation but didn't prove that
/// flipping a TreeViewItem open from the UI side actually drives the
/// lazy-populate hook. This test is the missing bridge.
/// </summary>
public class SourceTreePickerIntegrationTests
{
    private readonly ITestOutputHelper _out;
    public SourceTreePickerIntegrationTests(ITestOutputHelper output) => _out = output;

    [AvaloniaFact]
    public void Expanding_ARoot_PopulatesChildrenInTree()
    {
        // Spin up a real tree on disk so the production enumerate path runs.
        using var ws = new TempWorkspace("treepicker");
        var rootDir = Path.Combine(ws.Root, "root");
        Directory.CreateDirectory(Path.Combine(rootDir, "A"));
        Directory.CreateDirectory(Path.Combine(rootDir, "B"));
        Directory.CreateDirectory(Path.Combine(rootDir, "A", "Sub"));

        // Shove a synthetic root into the picker VM so we don't depend on
        // real drives showing up in the test environment.
        var vm = new SourceTreePickerViewModel(initiallyChecked: System.Array.Empty<string>());
        vm.Roots.Clear();
        vm.Roots.Add(new FileSystemNode(rootDir, "root", isDirectory: true, isMounted: true));

        var window = new SourceTreePickerWindow { DataContext = vm, Width = 640, Height = 720 };
        window.Show();
        window.UpdateLayout();

        var tree = window.GetVisualDescendants().OfType<TreeView>().Single();
        var rootContainer = tree.GetLogicalDescendants().OfType<TreeViewItem>().First();
        Assert.Equal("root", (rootContainer.DataContext as FileSystemNode)?.DisplayName);
        Assert.False(((FileSystemNode)rootContainer.DataContext!).IsExpanded,
            "Precondition: root starts collapsed.");

        // Simulate the user clicking the expand chevron.
        rootContainer.IsExpanded = true;
        window.UpdateLayout();
        window.UpdateLayout();

        var node = (FileSystemNode)rootContainer.DataContext!;
        _out.WriteLine($"After UI expand: node.IsExpanded={node.IsExpanded}, Children={node.Children.Count}");

        // The critical assertion: the model's lazy-populate ran because
        // the UI expansion flowed through to the VM.
        Assert.True(node.IsExpanded,
            "TreeViewItem.IsExpanded=true should propagate to the bound FileSystemNode.IsExpanded. " +
            "If this fails, the Style Setter binding in SourceTreePickerWindow.axaml isn't wiring " +
            "the two-way flow from the container back to the data.");
        Assert.Equal(2, node.Children.Count);

        // And the TreeView re-renders the new sub-items as their own containers.
        var tviForA = rootContainer.GetLogicalDescendants().OfType<TreeViewItem>()
            .FirstOrDefault(t => (t.DataContext as FileSystemNode)?.DisplayName == "A");
        Assert.NotNull(tviForA);

        window.Close();
    }

    [AvaloniaFact]
    public void ExpandingSecondLevel_AlsoPopulates()
    {
        using var ws = new TempWorkspace("treepicker-2");
        var rootDir = Path.Combine(ws.Root, "root");
        Directory.CreateDirectory(Path.Combine(rootDir, "A", "Sub1"));
        Directory.CreateDirectory(Path.Combine(rootDir, "A", "Sub2"));

        var vm = new SourceTreePickerViewModel(initiallyChecked: System.Array.Empty<string>());
        vm.Roots.Clear();
        vm.Roots.Add(new FileSystemNode(rootDir, "root", isDirectory: true, isMounted: true));

        var window = new SourceTreePickerWindow { DataContext = vm, Width = 640, Height = 720 };
        window.Show();
        window.UpdateLayout();

        var tree = window.GetVisualDescendants().OfType<TreeView>().Single();
        var rootTvi = tree.GetLogicalDescendants().OfType<TreeViewItem>().First();

        rootTvi.IsExpanded = true;
        window.UpdateLayout();
        window.UpdateLayout();

        var aTvi = rootTvi.GetLogicalDescendants().OfType<TreeViewItem>()
            .First(t => (t.DataContext as FileSystemNode)?.DisplayName == "A");
        aTvi.IsExpanded = true;
        window.UpdateLayout();
        window.UpdateLayout();

        var aNode = (FileSystemNode)aTvi.DataContext!;
        Assert.True(aNode.IsExpanded);
        Assert.Equal(2, aNode.Children.Count);

        window.Close();
    }
}
