using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Duplimate.ViewModels;

/// <summary>
/// VM behind <see cref="Views.SourceTreePickerWindow"/>. Presents drives +
/// directories as a lazy-populated tree with tri-state checkboxes, and
/// produces a minimal-cover list of ticked paths on confirm.
///
/// Minimal cover: if the user ticks <c>C:\Users\me</c>, we return that
/// single path — <em>not</em> an enumeration of every child. Ticking a
/// parent implicitly includes all descendants at backup time via
/// Duplicacy's normal "repo root + filters" semantics.
/// </summary>
public sealed partial class SourceTreePickerViewModel : ViewModelBase
{
    public ObservableCollection<FileSystemNode> Roots { get; } = new();

    /// <summary>Minimal cover of ticked paths. Populated by <see cref="Confirm"/>.</summary>
    public List<string>? Result { get; private set; }

    private readonly HashSet<string> _initiallyChecked;

    public SourceTreePickerViewModel(IEnumerable<string> initiallyChecked)
    {
        // Normalise once, store as a HashSet so the per-child membership
        // check in FileSystemNode.PopulateChildren is O(1). Previously
        // only the root was seeded as partial-tri-state and the actual
        // descendant leaves stayed unticked when revealed by lazy
        // expansion — re-opening the picker on an existing source like
        // "D:\Antigravity\Shared\Notepad++ Portable" looked like the
        // previous selection had been lost. The set is now passed down
        // through every PopulateChildren call so deep matches re-tick.
        _initiallyChecked = new HashSet<string>(
            (initiallyChecked ?? Array.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(FileSystemNode.NormalizeForCompare),
            StringComparer.OrdinalIgnoreCase);
        PopulateRoots();
    }

    private void PopulateRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            // DriveInfo throws on some NetworkInformation-backed drives
            // when you read Name/IsReady/TotalSize. Guard each.
            string? root;
            string label;
            bool mounted;
            long? free;
            try
            {
                root = drive.RootDirectory.FullName;
                mounted = drive.IsReady;
                label = BuildDriveLabel(drive);
                free = mounted ? drive.AvailableFreeSpace : null;
            }
            catch { continue; }

            var node = new FileSystemNode(root, label, isDirectory: true, mounted)
            {
                FreeBytes = free,
                // Carry the pre-tick set into the tree so deep matches
                // get re-ticked on lazy expansion (see FileSystemNode
                // for why a single root-level seed wasn't enough).
                PreTickedPaths = _initiallyChecked,
            };
            SeedCheckedFromInitial(node);
            Roots.Add(node);
        }

        // Pre-expand the path leading to each preset so the user can
        // SEE their previously-selected folders without having to
        // hand-walk the tree on every re-open. Earlier we deferred
        // expansion to user click — fine for fresh state, awful UX
        // when re-opening on existing sources.
        EagerExpandToPresets();
    }

    /// <summary>
    /// For each preset path, walk down from the matching root and
    /// set IsExpanded=true on each ancestor node. PopulateChildren
    /// fires off OnIsExpandedChanged when IsExpanded flips, which
    /// materialises children + propagates the pre-tick state.
    /// </summary>
    private void EagerExpandToPresets()
    {
        foreach (var preset in _initiallyChecked)
        {
            var presetKey = FileSystemNode.NormalizeForCompare(preset);
            // Find the matching root.
            FileSystemNode? cursor = null;
            foreach (var r in Roots)
            {
                if (r.Path is not null
                    && IsUnderOrEqual(presetKey, FileSystemNode.NormalizeForCompare(r.Path)))
                {
                    cursor = r;
                    break;
                }
            }
            if (cursor is null) continue;

            // Walk down. At each step, expand the node (which
            // populates its children synchronously), then find the
            // child whose path is an ancestor-or-equal of the preset.
            while (cursor is not null)
            {
                var cursorKey = FileSystemNode.NormalizeForCompare(cursor.Path ?? "");
                if (PathEquals(cursorKey, presetKey)) break; // we're at the preset
                cursor.IsExpanded = true;
                FileSystemNode? next = null;
                foreach (var c in cursor.Children)
                {
                    if (c.IsPlaceholder || c.Path is null) continue;
                    var ck = FileSystemNode.NormalizeForCompare(c.Path);
                    if (IsUnderOrEqual(presetKey, ck))
                    {
                        next = c;
                        break;
                    }
                }
                cursor = next;
            }
        }
    }

    private static string BuildDriveLabel(DriveInfo d)
    {
        try
        {
            if (!d.IsReady) return $"{d.Name.TrimEnd('\\', '/')} (offline)";
            var volLabel = string.IsNullOrWhiteSpace(d.VolumeLabel)
                ? DescribeDriveType(d.DriveType)
                : d.VolumeLabel;
            return $"{d.Name.TrimEnd('\\', '/')} — {volLabel}";
        }
        catch { return d.Name; }
    }

    private static string DescribeDriveType(DriveType t) => t switch
    {
        DriveType.Fixed      => "Local disk",
        DriveType.Removable  => "Removable",
        DriveType.Network    => "Network",
        DriveType.CDRom      => "CD/DVD",
        _                    => "Drive",
    };

    /// <summary>
    /// If one of the initially-checked paths equals or descends from this
    /// node, tick the node. Exact matches go full-checked; descendants
    /// push us to eager-expand to reveal the checked child on open.
    /// </summary>
    private void SeedCheckedFromInitial(FileSystemNode node)
    {
        if (node.Path is null) return;
        foreach (var preset in _initiallyChecked)
        {
            if (PathEquals(preset, node.Path))
            {
                // Exact match: this node IS the saved source. Mark as
                // explicitly ticked so the cover algorithm preserves it
                // (otherwise reconcile-up bubbles to a parent, and the
                // cover walk would pick the parent path).
                node.SetExplicitlyCheckedWithoutPropagation();
                return;
            }
            if (IsUnderOrEqual(preset, node.Path))
            {
                // Descendant: show the node as partial so the user sees
                // there's something ticked inside.
                node.SetCheckedWithoutPropagation(null);
                // Don't recurse eagerly — populate on expand. The PathEquals
                // match will re-tick the right descendant when it appears.
                return;
            }
        }
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsUnderOrEqual(string maybeChild, string maybeParent)
    {
        var c = NormalizePath(maybeChild);
        var p = NormalizePath(maybeParent);
        if (p.Length == 0) return true;
        if (c.Length < p.Length) return false;
        if (!c.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return false;
        return c.Length == p.Length || c[p.Length] == Path.DirectorySeparatorChar;
    }

    private static string NormalizePath(string p) =>
        string.IsNullOrEmpty(p) ? "" : p.TrimEnd('\\', '/');

    [RelayCommand]
    private void Confirm()
    {
        var cover = new List<string>();
        foreach (var r in Roots) CollectMinimalCover(r, cover);

        // Add back any preset path that the user didn't explicitly
        // touch. Without this, re-opening the picker on an existing
        // source list and clicking Save without ever expanding the
        // tree silently dropped the deep preset paths.
        //
        // Decision per preset:
        //   • Cover already contains the preset OR an ancestor → covered, skip.
        //   • Cover already contains a DESCENDANT of the preset
        //     → the user picked something narrower under the preset
        //       (e.g. unticked "D:\Antigravity\Shared" and ticked just
        //       "D:\Antigravity\Shared\Notepad++ Portable"); don't add
        //       the parent back, that would give us BOTH and the user
        //       sees the parent in the chip list. This was the
        //       "Notepad++ Portable" truncation bug.
        //   • Materialized AND IsChecked == false  → user unticked, drop.
        //   • Materialized AND IsChecked == null   → user partial-modified
        //                                            inside this preset;
        //                                            the cover-walk already
        //                                            captured the surviving
        //                                            sub-paths. DON'T re-add
        //                                            the parent.
        //   • Materialized AND IsChecked == true   → still entirely ticked,
        //                                            keep.
        //   • Not materialized (user never expanded down to it) → keep.
        foreach (var preset in _initiallyChecked)
        {
            if (IsCoveredByAny(cover, preset)) continue;
            if (CoverHasDescendantOf(cover, preset)) continue;
            var materialized = FindMaterialized(preset);
            if (materialized is null) { cover.Add(preset); continue; }
            // Only re-add when still fully ticked.
            if (materialized.IsChecked == true) cover.Add(preset);
        }
        Result = cover;
    }

    /// <summary>
    /// True iff <paramref name="cover"/> contains a path that is a
    /// strict descendant of <paramref name="path"/>. Used to skip
    /// re-injecting a preset when the user has narrowed their pick
    /// to a child of it.
    /// </summary>
    private static bool CoverHasDescendantOf(List<string> cover, string path)
    {
        var p = NormalizePath(path);
        var prefix = p.EndsWith(Path.DirectorySeparatorChar) ? p : p + Path.DirectorySeparatorChar;
        foreach (var c in cover)
        {
            var n = NormalizePath(c);
            if (n.Length <= p.Length) continue;
            if (n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// True iff <paramref name="cover"/> contains <paramref name="path"/>
    /// OR an ancestor of it. Lets Confirm skip presets that are now
    /// covered by a parent the user ticked.
    /// </summary>
    private static bool IsCoveredByAny(List<string> cover, string path)
    {
        foreach (var c in cover)
            if (IsUnderOrEqual(path, c)) return true;
        return false;
    }

    /// <summary>
    /// Walk the materialised tree (only nodes whose parents have been
    /// expanded) looking for the node at <paramref name="path"/>.
    /// Returns null if the node hasn't been realised yet — meaning the
    /// user never expanded down to it, and we should preserve the
    /// preset instead of dropping it.
    /// </summary>
    private FileSystemNode? FindMaterialized(string path)
    {
        var target = FileSystemNode.NormalizeForCompare(path);
        foreach (var r in Roots)
        {
            var hit = FindMaterializedRec(r, target);
            if (hit is not null) return hit;
        }
        return null;
    }

    private static FileSystemNode? FindMaterializedRec(FileSystemNode node, string target)
    {
        if (node.IsPlaceholder) return null;
        if (node.Path is not null
            && string.Equals(FileSystemNode.NormalizeForCompare(node.Path), target,
                             StringComparison.OrdinalIgnoreCase))
            return node;
        foreach (var c in node.Children)
        {
            var hit = FindMaterializedRec(c, target);
            if (hit is not null) return hit;
        }
        return null;
    }

    [RelayCommand]
    private void Cancel() => Result = null;

    /// <summary>
    /// Walk the tree and return the cover of explicitly-ticked paths.
    ///
    /// Rules (in order of precedence at each node):
    ///   • <c>IsChecked == false</c>: skip the subtree.
    ///   • <c>ExplicitlyTicked &amp;&amp; IsChecked == true</c>: this is exactly
    ///     what the user picked (or a saved preset). Add it; don't
    ///     descend — the subtree is fully covered.
    ///   • <c>ExplicitlyTicked &amp;&amp; IsChecked == null</c>: user ticked
    ///     this node then narrowed by unticking some descendants.
    ///     Descend to pick up the remaining ticked descendants
    ///     individually.
    ///   • <c>IsChecked == true &amp;&amp; !ExplicitlyTicked</c>: True is
    ///     INHERITED — either down-propagation from an ancestor's tick,
    ///     OR reconcile-up because every materialised child happens to
    ///     be true. Descend looking for the explicit tick(s) that drove
    ///     this state. If no descendant has an explicit tick AND no
    ///     descendant gets added, fall back to adding this node (so a
    ///     "user ticked parent then unticked one sibling" scenario
    ///     surfaces the still-ticked sibling correctly).
    ///   • <c>IsChecked == null &amp;&amp; !ExplicitlyTicked</c>: just descend.
    ///
    /// This algorithm finally fixes the "user ticked
    /// D:\Antigravity\Shared\Notepad++ Portable, got back D:\Antigravity"
    /// regression — see <see cref="FileSystemNode.ExplicitlyTicked"/>
    /// for the why.
    /// </summary>
    private static void CollectMinimalCover(FileSystemNode node, List<string> acc)
    {
        if (node.IsChecked == false || node.Path is null) return;

        if (node.ExplicitlyTicked && node.IsChecked == true)
        {
            acc.Add(node.Path);
            return;
        }

        if (node.IsChecked == true && !node.ExplicitlyTicked)
        {
            // Inherited true. Descend; if nothing gets added, fall back
            // to this node (best granularity available — typically a
            // child that survived a sibling's untick after a parent was
            // explicitly ticked).
            var before = acc.Count;
            foreach (var c in node.Children)
            {
                if (c.IsPlaceholder) continue;
                CollectMinimalCover(c, acc);
            }
            if (acc.Count == before) acc.Add(node.Path);
            return;
        }

        // null (partial) — explicit or not, descend.
        foreach (var c in node.Children)
        {
            if (c.IsPlaceholder) continue;
            CollectMinimalCover(c, acc);
        }
    }
}
