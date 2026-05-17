using Avalonia.Headless.XUnit;
using Duplimate.Services;
using Duplimate.ViewModels;
using Xunit;

namespace Duplimate.Tests.ViewModels;

/// <summary>
/// Regression coverage for the Restore wizard's step counter, Back-
/// button visibility, and multi-source revision-step routing — all
/// reported by the user as: "Restore Files starts with Step 2 even
/// though we got rid of Step 1. There is a Back button on Step 2!"
/// and "click Pick a revision and it goes straight to Step 6, no
/// revision selection. Always show revision even if there is only
/// one (helpful for user to see how old it is)."
///
/// Tests poke the VM's wizard state directly rather than driving
/// the live preselect path, because the preselect path's revision
/// listing spawns duplicacy.exe (out of scope for headless tests).
/// The contract under test is the pure VM derivation —
/// WizardStepLabel, CanGoBack, and the multi-source NextFromRevision
/// transition — which is decoupled from any process spawning.
/// </summary>
public class RestoreViewModelWizardLayoutTests
{
    [AvaloniaFact]
    public void WizardStepLabel_PickRevision_SingleSourceSingleDestination_IsStep1Of3()
    {
        // Single-source single-destination backup landing on PickRevision
        // (the user's first VISIBLE step) must read "STEP 1 OF 3" — was
        // "STEP 2 OF 4" before the fix because PickBackup was counted as
        // a real step despite being just an empty-state placeholder.
        ResetConfig();
        var vm = new RestoreViewModel();
        SeedSourcesAndDests(vm, sourceCount: 1, destCount: 1);
        vm.MultiSourceMode = false;
        vm.CurrentStep = RestoreViewModel.Step.PickRevision;

        Assert.Equal("STEP 1 OF 3", vm.WizardStepLabel);
    }

    [AvaloniaFact]
    public void WizardStepLabel_MultiSourceMultiDest_PickRevision_IsStep3Of4()
    {
        // Multi-source multi-dest landing on PickRevision must be
        // "STEP 3 OF 4" (PickSource → PickDestination → PickRevision →
        // PickTarget, 4 visible steps; PickFiles is bypassed in
        // multi-source mode). Before the fix this was 4 of 6 because
        // PickBackup was counted and PickFiles wasn't elided.
        ResetConfig();
        var vm = new RestoreViewModel();
        SeedSourcesAndDests(vm, sourceCount: 2, destCount: 2);
        vm.MultiSourceMode = true;
        vm.CurrentStep = RestoreViewModel.Step.PickRevision;

        Assert.Equal("STEP 3 OF 4", vm.WizardStepLabel);
    }

    [AvaloniaFact]
    public void CanGoBack_OnFirstVisibleStep_IsFalse_SoBackButtonHides()
    {
        // The user-reported regression "There is a Back button on Step
        // 2!" was caused by every step rendering an unconditional Back
        // button, even when going Back would land on the now-defunct
        // PickBackup empty-state. With single-source single-dest
        // landing on PickRevision, there's no earlier USER-VISIBLE
        // step — Back must hide.
        ResetConfig();
        var vm = new RestoreViewModel();
        SeedSourcesAndDests(vm, sourceCount: 1, destCount: 1);
        vm.MultiSourceMode = false;
        vm.CurrentStep = RestoreViewModel.Step.PickRevision;

        Assert.False(vm.CanGoBack);
    }

    [AvaloniaFact]
    public void CanGoBack_OnPickRevision_WithMultiSource_IsTrue()
    {
        // With >1 source picked, PickSource is shown and Back from
        // PickRevision should rewind to it. Pin the contract so a
        // future tweak can't accidentally hide Back here too.
        ResetConfig();
        var vm = new RestoreViewModel();
        SeedSourcesAndDests(vm, sourceCount: 2, destCount: 1);
        vm.MultiSourceMode = true;
        vm.CurrentStep = RestoreViewModel.Step.PickRevision;

        Assert.True(vm.CanGoBack);
    }

    [AvaloniaFact]
    public void CanGoBack_OnPickTarget_MultiSource_IsTrue_BackTargetIsPickRevision()
    {
        // Multi-source PickTarget previously rewound past PickRevision
        // (which was skipped) directly to source/dest. Now PickRevision
        // is a visible summary step in multi-source mode, so Back from
        // PickTarget must rewind to PickRevision.
        ResetConfig();
        var vm = new RestoreViewModel();
        SeedSourcesAndDests(vm, sourceCount: 2, destCount: 1);
        vm.MultiSourceMode = true;
        vm.CurrentStep = RestoreViewModel.Step.PickTarget;

        Assert.True(vm.CanGoBack);
        vm.BackCommand.Execute(null);
        Assert.Equal(RestoreViewModel.Step.PickRevision, vm.CurrentStep);
    }

    [AvaloniaFact]
    public void NextFromRevision_InMultiSourceMode_AdvancesToPickTarget_WithoutTouchingFilesPicker()
    {
        // The user reported: clicking "Next: pick a revision" on the
        // destination step jumped past the revision step entirely.
        // After the fix, the wizard ALWAYS routes through
        // PickRevision (single-source: picker; multi-source: read-
        // only summary). NextFromRevision in multi-source mode then
        // skips file picking and lands on PickTarget. Pin both halves
        // of that contract: revision step is reachable, and Next
        // advances forward by exactly one visible step.
        ResetConfig();
        var vm = new RestoreViewModel();
        SeedSourcesAndDests(vm, sourceCount: 2, destCount: 1);
        vm.MultiSourceMode = true;
        vm.CurrentStep = RestoreViewModel.Step.PickRevision;

        // No SelectedRevision needed in multi-source mode — Next must
        // still advance (the row list is read-only summary).
        Assert.Null(vm.SelectedRevision);
        vm.NextFromRevisionCommand.Execute(null);

        Assert.Equal(RestoreViewModel.Step.PickTarget, vm.CurrentStep);
    }

    /// <summary>
    /// Populate the wizard's pick-source / pick-destination
    /// collections with synthetic entries so WizardStepLabel /
    /// CanGoBack can read the counts. These are the only inputs the
    /// derivation logic looks at; we don't need real Backup or
    /// Destination instances.
    /// </summary>
    private static void SeedSourcesAndDests(RestoreViewModel vm, int sourceCount, int destCount)
    {
        vm.SourcesForBackup.Clear();
        for (int i = 0; i < sourceCount; i++)
            vm.SourcesForBackup.Add(new SourcePickRow($@"C:\src{i}", vm, isSelected: true));
        vm.DestinationsForBackup.Clear();
        for (int i = 0; i < destCount; i++)
            vm.DestinationsForBackup.Add(new Duplimate.Models.Destination
            {
                Id = $"dest-{i}",
                Name = $"Dest {i}",
                Kind = Duplimate.Models.DestinationKind.LocalFolder,
                PathOrSubpath = $@"D:\dest{i}",
            });
    }

    [AvaloniaFact]
    public void RestoreLog_HidesDebugAndTraceLines_WhenVerboseOff()
    {
        // Regression for the user's request: "in the restore log, hide
        // DEBUG level entries unless Verbose diagnostic logs is ON".
        // After the user reported TRACE leaking through too — duplicacy
        // emits both — extended to cover TRACE as well. Only INFO/WARN/
        // ERROR is useful unless the user explicitly opted into verbose.
        ResetConfig();
        ServiceLocator.Config.Update(cfg => cfg.Logging.MinimumLevel = "Information");

        Assert.False(RestoreViewModel.ShouldShowRestoreLogLine(
            "2026-04-22 15:56:29.939 DEBUG SOMETHING noisy detail"));
        Assert.False(RestoreViewModel.ShouldShowRestoreLogLine(
            "2026-05-01 15:35:27.456 TRACE RESTORE_PATTERN +launcher.20251120.log"));
        Assert.True(RestoreViewModel.ShouldShowRestoreLogLine(
            "2026-04-22 15:56:29.939 INFO RUN_BACKUP started"));
        Assert.True(RestoreViewModel.ShouldShowRestoreLogLine(
            "2026-04-22 15:56:29.939 WARN something odd"));
        Assert.True(RestoreViewModel.ShouldShowRestoreLogLine(
            "2026-04-22 15:56:29.939 ERROR FATAL kaboom"));
    }

    [AvaloniaFact]
    public void RestoreLog_DebugFilter_DoesNotMatchPathsContainingDebugSubstring()
    {
        // Audit follow-up: the bare Contains(" DEBUG ") match used to
        // false-positive on real INFO lines whose path or message body
        // happened to embed " DEBUG ". A user-named folder
        // C:\My DEBUG Stuff would silently disappear from the restore
        // pane. The fix anchors the predicate to the Duplicacy log
        // header (timestamp + " DEBUG "), so non-DEBUG lines with the
        // substring elsewhere now pass.
        ResetConfig();
        ServiceLocator.Config.Update(cfg => cfg.Logging.MinimumLevel = "Information");

        Assert.True(RestoreViewModel.ShouldShowRestoreLogLine(
            "2026-04-22 15:56:29.939 INFO RESTORE_FILE C:\\My DEBUG Stuff\\note.txt restored"));
        Assert.True(RestoreViewModel.ShouldShowRestoreLogLine(
            "2026-04-22 15:56:29.939 INFO RUN_INFO Set debug level: from DEBUG to INFO"));
        Assert.True(RestoreViewModel.ShouldShowRestoreLogLine(
            "[summary] DEBUG sanity check completed"));
        // Bare DEBUG keyword without a timestamp prefix is not a
        // duplicacy log line, so it passes through.
        Assert.True(RestoreViewModel.ShouldShowRestoreLogLine("DEBUG something"));
    }

    [AvaloniaFact]
    public void ProgressLogBuffer_TrimsAtNewlineBoundary_AndStaysUnderHardCap()
    {
        // Audit follow-up #2: the previous `ProgressLog = ProgressLog[^200_000..]`
        // truncation sliced mid-line, so the surviving head was a
        // partial fragment with no leading "[20XX-..." stamp. The new
        // buffer trims forward to the next '\n' so the head is always
        // a complete line. Pin both that invariant AND the hard cap so
        // a long restore can't blow up unbounded.
        ResetConfig();
        var vm = new RestoreViewModel();

        // Append enough lines to overflow the 400 KB hard cap. Each
        // line is ~40 chars including newline, so 12k lines = ~480 KB.
        for (int i = 0; i < 12_000; i++)
            vm.AppendProgressLineForTesting($"line-{i:D6} this is some restore output");

        var snapshot = vm.ProgressLogBufferSnapshot();
        Assert.True(vm.ProgressLogBufferLength <= 400_000,
            $"buffer exceeded hard cap: {vm.ProgressLogBufferLength}");
        Assert.True(snapshot.Length > 0);
        // First character must be the start of a complete line — never
        // mid-line. With an aligned trim, the head starts at "line-".
        Assert.StartsWith("line-", snapshot);
    }

    [AvaloniaFact]
    public void ProgressLogBuffer_AlwaysStoresRaw_RegardlessOfVerboseSetting()
    {
        // The user's invariant: "Verbose is only a display thing, not
        // a recording filter. DEBUG/TRACE lines should be filtered out
        // [from display] but still always stored." The buffer is the
        // source of truth used by PersistRestoreRunRecord to write the
        // .log file on disk — it MUST contain every line the engine
        // emitted, even when verbose is off, so a user troubleshooting
        // a past run later (or sharing the log file in a bug report)
        // gets the complete picture.
        ResetConfig();
        ServiceLocator.Config.Update(cfg => cfg.Logging.MinimumLevel = "Information");

        var vm = new RestoreViewModel();
        vm.AppendProgressLineForTesting("2026-04-22 15:56:29.939 INFO RUN_BACKUP started");
        vm.AppendProgressLineForTesting("2026-04-22 15:56:29.940 DEBUG NOISE keep this in storage");
        vm.AppendProgressLineForTesting("2026-04-22 15:56:29.941 TRACE PATTERN keep this too");
        vm.AppendProgressLineForTesting("2026-04-22 15:56:29.942 ERROR FATAL kaboom");

        var raw = vm.ProgressLogBufferSnapshot();

        // Raw buffer keeps everything regardless of verbose setting —
        // this is what the persisted .log file is written from.
        Assert.Contains("INFO RUN_BACKUP started", raw);
        Assert.Contains("DEBUG NOISE keep this in storage", raw);
        Assert.Contains("TRACE PATTERN keep this too", raw);
        Assert.Contains("ERROR FATAL kaboom", raw);
    }

    [AvaloniaFact]
    public void RestoreLog_ShowsDebugLines_WhenVerboseOn()
    {
        // The verbose toggle (Settings → Show verbose diagnostic logs)
        // sets cfg.Logging.MinimumLevel to "Verbose". When that's on
        // the restore pane should pass DEBUG lines through so a user
        // troubleshooting an issue sees the full picture.
        ResetConfig();
        ServiceLocator.Config.Update(cfg => cfg.Logging.MinimumLevel = "Verbose");

        Assert.True(RestoreViewModel.ShouldShowRestoreLogLine(
            "2026-04-22 15:56:29.939 DEBUG SOMETHING noisy detail"));
        Assert.True(RestoreViewModel.ShouldShowRestoreLogLine(
            "2026-04-22 15:56:29.939 INFO RUN_BACKUP started"));

        // "Debug" minimum level is also accepted as verbose-on (Serilog
        // writes both DEBUG and TRACE at this level).
        ServiceLocator.Config.Update(cfg => cfg.Logging.MinimumLevel = "Debug");
        Assert.True(RestoreViewModel.ShouldShowRestoreLogLine(
            "2026-04-22 15:56:29.939 DEBUG SOMETHING noisy detail"));
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
