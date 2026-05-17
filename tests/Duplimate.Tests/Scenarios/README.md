# Scenario tests — visual + interaction E2E suite

Headless walk-throughs of every important user flow. Each scenario:

1. Creates throwaway source / destination / restore directories under
   `tests/Duplimate.Tests/artifacts/scenarios/<Sxx-name>/`.
2. Boots the real Avalonia views (same DI graph as the GUI).
3. Drives a scripted user flow (`ctx.Driver.ClickButton(...)`,
   `ctx.Driver.EnterText(...)`, or direct VM pokes for UI-awkward bits).
4. Renders a PNG at every interesting step via `ctx.Snapshot(window, "label")`.
5. Auto-skips with a recorded reason if a required secret is missing.

The suite-level `ScenarioSuiteFixture` writes
`artifacts/scenarios/SUMMARY.md` after every test run with the
pass/skip/fail breakdown, missing-secret table, and per-step
screenshot index.

## Running

```bash
# Run every scenario:
dotnet test tests/Duplimate.Tests --filter "FullyQualifiedName~Scenarios"

# One scenario:
dotnet test tests/Duplimate.Tests --filter "FullyQualifiedName~S03_TwoSourcesLocalAndDropbox"

# Open the report:
start tests/Duplimate.Tests/artifacts/scenarios/SUMMARY.md
```

## Adding a scenario

1. Create `Sxx_DescriptiveName.cs` in this folder.
2. Subclass `ScenarioBase`. Override `ScenarioName` and `RunAsync`.
3. Mark with `[Collection(ScenarioCollection.Name)]` so xUnit groups
   it under the assembly fixture.
4. Add a single `[AvaloniaFact] public async Task Run_Sxx() => await Run();`
   so xUnit discovers it.
5. Inside `RunAsync`:
   - Use `ctx.MakeSource(...)`, `ctx.MakeLocalDestination(...)` to
     materialise throwaway directories.
   - Drive UI via `ctx.Driver.ClickButton(view, "Save")`,
     `ctx.Driver.EnterText(view, "MyTextBox", "value")`, etc.
   - Call `ctx.Snapshot(window, "label", "optional human note")` at
     each interesting moment.
   - For secret-gated flows: `if (!ctx.Secrets.TryGetDropboxToken(out var t)) { ctx.Skip("…"); return; }`.

## Secrets

Drop plain-text credential files in
`tests/Duplimate.Tests/secrets/` (gitignored). One file per
credential; the loader trims whitespace + BOM. See
`secrets/README.md` (auto-generated) for the filename → service
mapping.

Missing secrets do not fail the suite — they are recorded in
`SUMMARY.md` so the user knows exactly which file to drop in to
unlock more coverage on the next run.

## Iterating with /loop

The user's standing rule: *they should not be the QA loop*. The
agent runs the suite, opens SUMMARY.md + select PNGs, identifies
issues, fixes them in the source, re-runs the suite. Recommended
`/loop` prompt:

```
/loop Run the scenario tests
(`dotnet test tests/Duplimate.Tests --filter Scenarios`),
read the SUMMARY.md, view at least the screenshots flagged by the
auditor as anomalous, identify any visual or behavioural issues
(blank frames, clipped controls, wrong colours, broken hover
states), fix them in src/Duplimate, and run again. Stop when
SUMMARY.md is clean (zero anomalies, zero failures).
```

Use sparingly — each iteration spends real time + cost. The
intention is "polish a specific area to perfection", not "run
forever".

## What's NOT in this suite (and why)

- **Live duplicacy.exe runs** — already covered by
  `tests/Duplimate.Tests/E2E/LocalBackupRestoreTests.cs` and
  friends. Scenarios stay focused on the user-visible flow.
- **OAuth round-trips** — opening a WebView for Dropbox / Google
  is interactive by definition. We accept a pre-acquired token via
  the secrets file instead.
- **Cross-process scheduled-task firing** — the COM-interop
  scheduling is exercised by `TaskSchedulerTests`; reproducing the
  Windows Task Scheduler in a unit test would be more code than
  it's worth.
