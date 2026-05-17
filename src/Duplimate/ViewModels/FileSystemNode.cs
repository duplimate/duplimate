using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Duplimate.ViewModels;

/// <summary>
/// One node in the source-picker tree — a drive or a directory.
///
/// State that matters for the UI: <see cref="IsChecked"/> (tri-state —
/// true = whole subtree, false = not included, null = some descendants
/// ticked) and <see cref="IsExpanded"/> (drives lazy-populate their
/// children on first expand so we don't walk millions of files on open).
///
/// Checkbox propagation rules:
///   • Ticking a node ticks every descendant.
///   • Unticking a node unticks every descendant.
///   • Changing a child propagates up: parent becomes partial (null) if
///     siblings differ, true if all are true, false if all are false.
///
/// The propagation is driven from the view binding; mutations set by
/// descendant reconciliation skip propagation via the *WithoutPropagation
/// helpers so we don't end up in a "child updates parent which updates
/// children" loop.
/// </summary>
public sealed partial class FileSystemNode : ObservableObject
{
    public string Path { get; }
    public string DisplayName { get; }
    public bool IsDirectory { get; }
    public bool IsMounted { get; }
    public long? FreeBytes { get; set; }

    public FileSystemNode? Parent { get; set; }
    public ObservableCollection<FileSystemNode> Children { get; } = new();

    [ObservableProperty] private bool? _isChecked = false;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isLoadingChildren;

    private bool _childrenPopulated;
    private bool _suppressPropagation;

    /// <summary>
    /// True when this specific node was the target of an explicit user
    /// tick (or a preset-load that pinned exactly this path) — as
    /// opposed to having inherited True from a parent's down-propagation
    /// or from reconcile-up because every materialised child happens to
    /// be True.
    ///
    /// This is the field that finally fixes the recurring "user ticked
    /// D:\Antigravity\Shared\Notepad++ Portable, got back D:\Antigravity"
    /// bug. Reconcile-up bubbles a parent's IsChecked to True whenever
    /// all its (materialised) children are True, which is correct for
    /// tri-state visual display but wrong for cover computation: a
    /// parent that's only True because its sole child was ticked is NOT
    /// what the user picked. Tracking explicit-tick separately lets the
    /// cover algorithm prefer the deepest explicit-ticked node.
    /// </summary>
    public bool ExplicitlyTicked { get; private set; }

    /// <summary>
    /// Paths the picker was opened with that should be auto-ticked when
    /// the matching node materialises during lazy expansion. Carries
    /// down through the tree (each PopulateChildren passes the same
    /// ref into its new children). Without this, re-opening the picker
    /// on an existing source like
    /// <c>D:\Antigravity\Shared\Notepad++ Portable</c> would mark D:
    /// as partial-tri-state but the actual descendant would render
    /// unticked once the user finally drilled down to it — and on
    /// confirm the picker would either drop the selection or return
    /// a wrong (parent) path.
    /// </summary>
    internal System.Collections.Generic.HashSet<string>? PreTickedPaths { get; set; }

    /// <summary>
    /// Sentinel path for the "Loading…" placeholder child. Avalonia's
    /// TreeView only renders the expand chevron when a node has at least
    /// one item in its bound ItemsSource; we add a placeholder so the
    /// chevron shows up for every directory, then swap in real children
    /// when the user first expands. This is the classic WPF/Avalonia
    /// dummy-child pattern for lazy filesystem trees.
    /// </summary>
    internal const string PlaceholderPath = "__duplimate_placeholder__";

    public bool IsPlaceholder => Path == PlaceholderPath;

    public FileSystemNode(string path, string displayName, bool isDirectory, bool isMounted = true)
    {
        Path = path;
        DisplayName = displayName;
        IsDirectory = isDirectory;
        IsMounted = isMounted;

        // Only add the placeholder (which forces the expand chevron to
        // render) when the directory ACTUALLY has at least one subdir.
        // Earlier we added the placeholder unconditionally and the
        // chevron disappeared after the user clicked it on a leaf
        // directory — bad UX, looked like the arrow was broken.
        // EnumerateDirectories().Any() bails after the first hit, so
        // the probe is cheap on local disk; we swallow any IO errors
        // (UnauthorizedAccessException, network unreachable, etc.)
        // and treat them as "no children" — at worst the user has to
        // refresh the picker if a previously-empty folder later gains
        // children, which is not a path the picker is performance-
        // critical for.
        if (isDirectory && isMounted && !IsPlaceholder && HasAnySubdirectory(path))
            Children.Add(CreatePlaceholder());
    }

    private static bool HasAnySubdirectory(string path)
    {
        try
        {
            using var e = new DirectoryInfo(path).EnumerateDirectories().GetEnumerator();
            return e.MoveNext();
        }
        catch { return false; }
    }

    private static FileSystemNode CreatePlaceholder() =>
        new FileSystemNode(PlaceholderPath, "Loading…", isDirectory: false, isMounted: false);

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_childrenPopulated) PopulateChildren();
    }

    partial void OnIsCheckedChanged(bool? value)
    {
        if (_suppressPropagation) return;

        // Record the explicit-tick intent: this code path only runs on
        // an actual user click (the suppressed-propagation branches
        // above handle inherited / programmatic mutations). True →
        // user ticked this exact node; false → user unticked. Null is
        // unusual on a user click (tri-state cycle never lands there
        // in our usage) and we leave the flag alone.
        if (value == true) ExplicitlyTicked = true;
        else if (value == false) ExplicitlyTicked = false;

        // Propagate DOWN: parent changing to true/false sets every
        // descendant to the same concrete value. null is a read-only UI
        // state (partial), not something we propagate.
        if (value == true || value == false)
        {
            foreach (var c in Children) c.SetCheckedPropagateDown(value);
        }

        // Propagate UP: re-evaluate parent's state now that this node
        // changed.
        Parent?.ReconcileFromChildren();
    }

    /// <summary>Set this node's checked state and push it through descendants.</summary>
    private void SetCheckedPropagateDown(bool? value)
    {
        if (IsChecked == value) return;
        _suppressPropagation = true;
        IsChecked = value;
        _suppressPropagation = false;
        foreach (var c in Children) c.SetCheckedPropagateDown(value);
    }

    /// <summary>Set without either up- or down-propagation (used by picker VM seeding).</summary>
    public void SetCheckedWithoutPropagation(bool? value)
    {
        _suppressPropagation = true;
        IsChecked = value;
        _suppressPropagation = false;
    }

    /// <summary>Same as <see cref="SetCheckedWithoutPropagation"/> but
    /// also marks this node as explicitly ticked — used when the
    /// PreTickedPaths set contains this node's exact path (i.e., the
    /// node was a saved source last time, so its tick is the user's
    /// explicit pick).</summary>
    public void SetExplicitlyCheckedWithoutPropagation()
    {
        _suppressPropagation = true;
        IsChecked = true;
        ExplicitlyTicked = true;
        _suppressPropagation = false;
    }

    /// <summary>
    /// Re-compute this node's tri-state from its current children's
    /// states. No-op if children haven't been populated — we don't want
    /// collapsing a lazy subtree to flip the parent's checkbox.
    /// </summary>
    private void ReconcileFromChildren()
    {
        if (!_childrenPopulated || Children.Count == 0) return;

        bool allTrue = true, allFalse = true;
        foreach (var c in Children)
        {
            if (c.IsChecked != true) allTrue = false;
            if (c.IsChecked != false) allFalse = false;
            if (!allTrue && !allFalse) break;
        }
        bool? newState = allTrue ? true : (allFalse ? false : null);
        if (IsChecked == newState) return;
        _suppressPropagation = true;
        IsChecked = newState;
        _suppressPropagation = false;
        Parent?.ReconcileFromChildren();
    }

    private void PopulateChildren()
    {
        if (_childrenPopulated) return;
        _childrenPopulated = true;
        if (!IsDirectory || !IsMounted) return;

        IsLoadingChildren = true;
        try
        {
            // Remove the placeholder added at construction. We Clear()
            // unconditionally — anyone who manually seeded Children
            // before the first expand is exercising an edge case we
            // don't support (real consumers always go through the lazy
            // path, and the tests drive the real filesystem).
            Children.Clear();

            // Sort case-insensitively so folder order is predictable
            // across Windows versions.
            var subdirs = EnumerateSafely(Path).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase);
            var anyChildSeeded = false;
            foreach (var d in subdirs)
            {
                var child = new FileSystemNode(d.FullName, d.Name, isDirectory: true)
                {
                    Parent = this,
                    // Pass the pre-tick set down so deep paths still get
                    // restored when the user expands further.
                    PreTickedPaths = PreTickedPaths,
                };
                // Propagate the parent's current ticked state to the
                // freshly-loaded child so expanding an all-checked node
                // doesn't reveal un-checked children.
                if (IsChecked == true)
                {
                    child.SetCheckedWithoutPropagation(true);
                }
                else if (PreTickedPaths is not null)
                {
                    var childKey = NormalizeForCompare(d.FullName);
                    if (PreTickedPaths.Contains(childKey))
                    {
                        // Exact match: this child IS one of the originally-
                        // selected paths. Mark as explicitly ticked so the
                        // cover algorithm preserves it on save (without
                        // this, reconcile-up bubbles a parent to True and
                        // the cover walk picks the parent instead — the
                        // recurring "Notepad++ Portable" truncation bug).
                        child.SetExplicitlyCheckedWithoutPropagation();
                        anyChildSeeded = true;
                    }
                    else if (HasPreTickedDescendant(childKey))
                    {
                        // A pre-tick path lives somewhere DEEPER than this
                        // child. Mark partial so the tri-state cue is
                        // present immediately — without this, the user
                        // sees a fully-unticked intermediate node and has
                        // to expand further to discover the deeper tick,
                        // which felt like the selection was lost.
                        child.SetCheckedWithoutPropagation(null);
                        anyChildSeeded = true;
                    }
                }
                Children.Add(child);
            }

            // If we touched any descendants from PreTickedPaths, run a
            // reconcile so this node's tri-state correctly reflects the
            // newly-revealed children (it'll go to "true" if every
            // child was pre-ticked, "null" if mixed).
            if (anyChildSeeded)
                ReconcileFromChildren();
        }
        finally
        {
            IsLoadingChildren = false;
        }
    }

    /// <summary>
    /// Enumerate directories under <paramref name="parent"/>, swallowing
    /// <see cref="UnauthorizedAccessException"/> and other per-entry
    /// errors. Returns an empty sequence on any top-level error (e.g. an
    /// unmounted drive). Folders that are NEVER useful to back up
    /// (Recycle Bin, System Volume Information, Windows recovery /
    /// update transients) are filtered out — see <see cref="NeverBackupFolders"/>.
    /// </summary>
    private static System.Collections.Generic.IEnumerable<DirectoryInfo> EnumerateSafely(string parent)
    {
        DirectoryInfo[] entries;
        try
        {
            entries = new DirectoryInfo(parent).GetDirectories();
        }
        // Catch the same exception SET DotNet docs list for GetDirectories
        // PLUS PathTooLongException (>260 chars when long-paths is off)
        // and SecurityException (rare ACL configurations). Without these
        // catches the picker would throw on a single deep / restricted
        // dir, leaving IsLoadingChildren=true and the user staring at
        // a half-loaded tree.
        catch (UnauthorizedAccessException)  { yield break; }
        catch (DirectoryNotFoundException)   { yield break; }
        catch (PathTooLongException)         { yield break; }
        catch (System.Security.SecurityException) { yield break; }
        catch (IOException)                  { yield break; }

        foreach (var d in entries)
        {
            if (NeverBackupFolders.Contains(d.Name)) continue;
            yield return d;
        }
    }

    internal static string NormalizeForCompare(string p) =>
        string.IsNullOrEmpty(p) ? "" : p.TrimEnd('\\', '/');

    /// <summary>
    /// True iff <see cref="PreTickedPaths"/> contains a path that lives
    /// strictly UNDER <paramref name="ancestorKey"/>. Used during lazy
    /// expansion to mark intermediate ancestors as partial-tri-state
    /// even before the deep pre-ticked path is materialised — so the
    /// user sees the tri-state cue on opening the picker rather than
    /// "everything looks unticked, did I lose my selection?"
    /// </summary>
    private bool HasPreTickedDescendant(string ancestorKey)
    {
        if (PreTickedPaths is null) return false;
        // ancestorKey ends without trailing separator; build the prefix
        // we'd see for any direct or indirect descendant.
        var prefix = ancestorKey.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? ancestorKey
            : ancestorKey + System.IO.Path.DirectorySeparatorChar;
        foreach (var p in PreTickedPaths)
        {
            if (p.Length <= ancestorKey.Length) continue;
            if (p.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Folders that we hide unconditionally from the source picker.
    /// These are protected / transient / regenerated by Windows and
    /// trying to back them up either fails outright (NTFS ACLs deny
    /// non-SYSTEM read) or wastes space on contents the user doesn't
    /// own and can't usefully restore.
    /// <para>
    /// Drive-root system stuff:
    ///   $RECYCLE.BIN, $Recycle.Bin           — per-user recycle bin
    ///   System Volume Information            — VSS / index / restore points
    ///   Recovery                             — Windows recovery image
    ///   Config.Msi                           — MSI installer transient
    ///   $WinREAgent, $GetCurrent, $SysReset  — Windows update / reset temp
    ///   $WINDOWS.~BT, $WINDOWS.~WS           — in-place upgrade staging
    /// </para>
    /// <para>
    /// User-profile transients that are noise in a backup:
    ///   OneDriveTemp                         — OneDrive cache
    ///   MSOCache                             — Office installer cache
    ///   PerfLogs                             — Windows perf counter logs (almost always empty)
    /// </para>
    /// Comparison is OrdinalIgnoreCase because Windows is case-insensitive
    /// and these names appear with slightly different casings across
    /// Windows versions.
    /// </summary>
    private static readonly System.Collections.Generic.HashSet<string> NeverBackupFolders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "$RECYCLE.BIN",
            "$Recycle.Bin",
            "System Volume Information",
            "Recovery",
            "Config.Msi",
            "$WinREAgent",
            "$GetCurrent",
            "$SysReset",
            "$WINDOWS.~BT",
            "$WINDOWS.~WS",
            "OneDriveTemp",
            "MSOCache",
            "PerfLogs",
        };
}
