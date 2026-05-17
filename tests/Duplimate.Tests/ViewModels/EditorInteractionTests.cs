using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Duplimate.Models;
using Duplimate.Services;
using Duplimate.ViewModels;
using Duplimate.Views;
using Xunit;

namespace Duplimate.Tests.ViewModels;

/// <summary>
/// Exercises the two most binding-dense dialogs — Backup and Destination
/// editors — the way a user would: instantiate, drive fields through
/// their bound properties (which is what XAML does under the hood), hit
/// Save, and assert the resulting domain model.
///
/// These aren't pixel-level UI tests — they're "does the two-way binding
/// + Save() flow produce the right Destination / Backup" tests. That's
/// the layer most likely to regress and the one our earlier suite didn't
/// cover.
/// </summary>
public class EditorInteractionTests
{
    [AvaloniaFact]
    public void DestinationEditor_LocalFolder_SaveWritesFieldsToModel()
    {
        ResetConfig();

        var working = new Destination();
        var vm = new DestinationEditorViewModel(working, isNew: true)
        {
            Name = "Backup drive",
            Kind = DestinationKind.ExternalDrive,
            PathOrSubpath = @"M:\Duplicacy",
            ExpectedDriveLetter = "M:",
            ExpectedVolumeLabel = "My Passport",
            Encrypted = true,
            StoragePasswordInput = "secret-phrase",
        };

        var saved = vm.Save();

        Assert.NotNull(saved);
        Assert.Equal("Backup drive", saved!.Name);
        Assert.Equal(DestinationKind.ExternalDrive, saved.Kind);
        Assert.Equal(@"M:\Duplicacy", saved.PathOrSubpath);
        Assert.Equal("M:", saved.ExpectedDriveLetter);
        Assert.Equal("My Passport", saved.ExpectedVolumeLabel);
        Assert.True(saved.Encrypted);
        Assert.NotNull(saved.StoragePasswordRef);
        // Password reference resolves to the value we typed.
        Assert.Equal("secret-phrase", ServiceLocator.Secrets.Get(saved.StoragePasswordRef!));
    }

    [AvaloniaFact]
    public void DestinationEditor_RefusesSave_WhenTypeIsUnset()
    {
        ResetConfig();
        // New destination, no Kind picked — the form is supposed to stop
        // the user (and write a status line) rather than silently saving
        // a LocalFolder destination.
        var vm = new DestinationEditorViewModel(new Destination(), isNew: true)
        {
            Name = "untyped",
        };
        // Don't set vm.Kind — it starts null for new destinations.
        Assert.Null(vm.Kind);

        var saved = vm.Save();
        Assert.Null(saved);
        // After the validation rewrite the complaint lands in KindError
        // (per-field) and SummaryError (banner) rather than AuthStatus.
        // AuthStatus is now reserved for the OAuth probe's running text.
        Assert.Contains("type", vm.KindError ?? "", System.StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.HasAnyError, "expected summary banner to light up after a bad Save.");
    }

    [AvaloniaFact]
    public void DestinationEditor_NameDefaultsToPlaceholder_WhenLeftBlank()
    {
        ResetConfig();
        var vm = new DestinationEditorViewModel(new Destination(), isNew: true)
        {
            Name = "   ",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = @"D:\backup",
            Encrypted = false,
        };

        var saved = vm.Save();
        Assert.NotNull(saved);
        // We don't silently drop the destination — fall back to a name.
        Assert.False(string.IsNullOrWhiteSpace(saved!.Name));
    }

    [AvaloniaFact]
    public void DestinationEditor_NewLocalDest_AutoGeneratesStoragePassword()
    {
        ResetConfig();
        var vm = new DestinationEditorViewModel(new Destination(), isNew: true)
        {
            Name = "auto-gen",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = @"D:\auto",
            Encrypted = true,
            StoragePasswordInput = "", // left blank → auto-generate
        };

        var saved = vm.Save();
        Assert.NotNull(saved);
        Assert.NotNull(saved!.StoragePasswordRef);
        var stored = ServiceLocator.Secrets.Get(saved.StoragePasswordRef!);
        Assert.False(string.IsNullOrWhiteSpace(stored),
            "Auto-generated storage password should not be empty.");
        Assert.True(stored.Length >= 32,
            $"Auto-generated password should be substantial; got length {stored.Length}.");
    }

    [AvaloniaFact]
    public void BackupEditor_FillAndSave_ProducesBackupWithAllFields()
    {
        ResetConfig();

        // Seed a destination so the backup can reference it.
        var dest = new Destination
        {
            Name = "local-backup",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = @"D:\backup",
        };
        ServiceLocator.Config.Update(cfg => cfg.Destinations.Add(dest));

        var working = new Backup();
        var vm = new BackupEditorViewModel(working, isNew: true)
        {
            Name = "documents",
            Threads = 4,
            UseVss = true,
            KeepPolicy = "0:365 7:30",
            PruneAfterBackup = true,
            CheckAfterBackup = false,
            Enabled = true,
            FiltersText = "- *.tmp\ne:cache",
        };
        // SourcePaths is an ObservableCollection on the VM, not an
        // init-only property — we populate it after construction.
        vm.SourcePaths.Add(@"C:\Users\me\Documents");

        // Simulate the "Add destination" picker flow.
        vm.PendingNewDestination = dest;
        vm.AddTargetCommand.Execute(null);

        Assert.Single(vm.Targets);

        // Same call the OnSave click handler makes — we skip the Window
        // lifecycle because we're not testing window disposal.
        var saved = vm.Save();

        Assert.NotNull(saved);
        Assert.Equal("documents", saved!.Name);
        Assert.Single(saved.SourcePaths);
        Assert.Equal(@"C:\Users\me\Documents", saved.SourcePaths[0]);
        Assert.Equal(4, saved.Threads);
        Assert.True(saved.UseVss);
        Assert.Equal("0:365 7:30", saved.KeepPolicy);
        Assert.True(saved.PruneAfterBackup);
        Assert.False(saved.CheckAfterBackup);
        Assert.Single(saved.Targets);
        Assert.Equal(dest.Id, saved.Targets[0].DestinationId);
    }

    /// <summary>
    /// After save, the form's state should survive a re-open — i.e. the
    /// ObservableProperty snapshots must actually land on Working, not
    /// just on the VM's own fields.
    /// </summary>
    [AvaloniaFact]
    public void DestinationEditor_SaveThenReopen_FieldsPersist()
    {
        ResetConfig();
        var model = new Destination();

        var vm1 = new DestinationEditorViewModel(model, isNew: true)
        {
            Name = "round-trip",
            Kind = DestinationKind.NetworkShare,
            PathOrSubpath = @"\\nas\backup",
            NetworkUsername = "alice",
            NetworkPasswordInput = "net-pw",
            Encrypted = false,
        };
        vm1.Save();

        // Fresh VM over the same working model — simulates reopening the
        // dialog later.
        var vm2 = new DestinationEditorViewModel(model, isNew: false);
        Assert.Equal("round-trip", vm2.Name);
        Assert.Equal(DestinationKind.NetworkShare, vm2.Kind);
        Assert.Equal(@"\\nas\backup", vm2.PathOrSubpath);
        Assert.Equal("alice", vm2.NetworkUsername);
        // Passwords are secrets; their ref persists on the model.
        Assert.NotNull(model.NetworkPasswordRef);
    }

    [AvaloniaFact]
    public void DestinationEditor_Cloud_BlankName_DoesNotDuplicateSubpathInLabel()
    {
        // Regression for user-reported 2026-04-29: creating a Dropbox
        // destination with a blank Name produced
        // "Dropbox - duplimate-f251402f - duplimate-f251402f"
        // because Save() passed PathOrSubpath as BOTH the account-or-path
        // identifier AND the subfolder when generating the name. Cloud
        // destinations have no readable account identifier, so the
        // identifier slot must be null — only the subfolder differentiates
        // the cloud destination's display label.
        ResetConfig();
        var working = new Destination
        {
            // Pre-mark the OAuth token as validated so Save's OAuth gate
            // doesn't refuse the save (this test is about naming, not auth).
            LastTokenValidatedUtc = System.DateTime.UtcNow,
        };
        var vm = new DestinationEditorViewModel(working, isNew: true)
        {
            Name = "",
            Kind = DestinationKind.DropboxAppScoped,
            PathOrSubpath = "duplimate-f251402f",
        };

        var saved = vm.Save();

        Assert.NotNull(saved);
        // Must NOT contain the subpath twice. If the test fails, surface
        // the actual generated name so the diagnosis is one read away.
        var occurrences = System.Text.RegularExpressions.Regex.Matches(
            saved!.Name, System.Text.RegularExpressions.Regex.Escape("duplimate-f251402f")).Count;
        Assert.True(occurrences == 1, $"Expected the subpath to appear once in saved.Name; actual name was '{saved.Name}'.");
        // And the resulting name should look right.
        Assert.Equal("Dropbox - duplimate-f251402f", saved.Name);
    }

    private static void ResetConfig()
    {
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Backups.Clear();
            cfg.Destinations.Clear();
        });
    }
}
