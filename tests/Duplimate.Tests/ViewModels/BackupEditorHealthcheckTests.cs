using Duplimate.Models;
using Duplimate.Services;
using Duplimate.ViewModels;
using Xunit;

namespace Duplimate.Tests.ViewModels;

/// <summary>
/// UUID validation lives on the BackupEditor VM so a typo in the
/// Healthchecks.io UUID gets caught at Save time instead of producing
/// silent ping failures every run. The shape check is enough — we don't
/// hit the network from tests.
/// </summary>
public class BackupEditorHealthcheckTests
{
    [Fact]
    public void ValidateForm_acceptsBlankUuid()
    {
        ResetConfig();
        var vm = NewVmWithRequiredsFilled();
        vm.HealthcheckId = "";
        Assert.True(vm.ValidateForm());
        Assert.Null(vm.HealthcheckIdError);
    }

    [Fact]
    public void ValidateForm_acceptsValidUuid()
    {
        ResetConfig();
        var vm = NewVmWithRequiredsFilled();
        vm.HealthcheckId = "11111111-2222-3333-4444-555555555555";
        Assert.True(vm.ValidateForm());
        Assert.Null(vm.HealthcheckIdError);
    }

    [Fact]
    public void ValidateForm_rejectsMalformedUuid_andSurfacesPerFieldError()
    {
        ResetConfig();
        var vm = NewVmWithRequiredsFilled();
        vm.HealthcheckId = "not-a-uuid";

        Assert.False(vm.ValidateForm());
        Assert.NotNull(vm.HealthcheckIdError);
        Assert.True(vm.HasAnyError);
    }

    private static BackupEditorViewModel NewVmWithRequiredsFilled()
    {
        var vm = new BackupEditorViewModel(new Backup(), isNew: true);
        vm.SourcePaths.Add(@"C:\src");
        // Avoid TargetsError by attaching one — won't be used in validate-only.
        vm.Targets.Add(new TargetRow(
            new Destination { Kind = DestinationKind.LocalFolder, PathOrSubpath = @"C:\dest", Name = "L" },
            "default", null));
        return vm;
    }

    private static void ResetConfig()
    {
        // ServiceLocator is initialized once globally for the test run by
        // the harness fixtures; here we just make sure the in-memory
        // backups list won't trip the "name unique" check. No-op if
        // already empty.
        if (!ServiceLocator.Initialized) ServiceLocator.InitializeCore();
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Backups.Clear();
            cfg.Destinations.Clear();
        });
    }
}
