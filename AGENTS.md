# AGENT.md — development state & conventions

This file is for whoever picks up the codebase next, human or AI. Read it
top-to-bottom before making changes — it captures the conventions that
aren't obvious from the code alone.

## What the app is

A friendly desktop GUI for [Duplicacy](https://duplicacy.com/). Single
self-contained `.exe` with a portable config folder next to it. The app
shells out to a bundled `duplicacy(.exe)` for the actual backup work and
wraps it in a step-by-step UI: onboarding wizard, scheduled runs, restore
wizard, activity log, settings. Cross-platform via Avalonia (Windows
tested, macOS / Linux build but untested in practice).

The README and the project website cover the user-facing story. This file
focuses on what a contributor needs to know.

## High-level architecture

```
Program.cs
 ├── if (--run <name> / --run-all):  UnattendedRunner.RunAsync(...)   [headless]
 ├── if (--migrate <path>):          ConfigMigrator.MigrateFrom(...)  [one-shot]
 └── else:                           AppBuilder ... StartWithClassicDesktopLifetime(...)  [GUI]

Avalonia GUI
 ├── MainWindow
 │    ├── Sidebar nav → Backups | Destinations | Restore | Logs | Settings
 │    └── Content host swaps view based on NavItem (see MainWindowViewModel)
 └── Modal dialogs: OnboardingWindow, BackupEditorWindow,
     DestinationEditorWindow, DestructiveConfirmWindow,
     ChangeStoragePasswordWindow, GeneratedPasswordWindow,
     SourceTreePickerWindow, ErasingProgressWindow

Services (singletons, accessed via ServiceLocator)
 ├── ConfigStore          — config.json load/save, in-memory cache, change events
 ├── ConfigMigrator       — one-shot import from an existing Duplicacy setup
 ├── SecretsStore         — DPAPI (Windows) / AES-GCM keyfile (Unix) wrapper around secrets.bin
 ├── DuplicacyRunner      — spawns duplicacy(.exe), streams stdout, parses errors
 ├── DuplicacyEmbedder    — extracts the embedded duplicacy(.exe) on first run
 ├── StubEmbedder         — Windows-only fallback "missing main exe" stub
 ├── BackupOrchestrator   — coordinates a run across (source × destination) cells, serialises per-storage
 ├── BackupProgressService — per-backup progress snapshots driven by stdout
 ├── BackupVerifier       — test-restore (random sample → byte compare)
 ├── DestinationProbe     — verifies a destination is reachable + lists snapshots
 ├── DropboxAuthProbe     — verifies a Dropbox OAuth token before save
 ├── RestoreEngine        — orchestrates restore wizard execution + retry cascade
 ├── RevisionBrowser      — parses `duplicacy list` / `list -files` output
 ├── StorageCleaner       — erase-destination + per-backup erase
 ├── MaintenanceCoordinator — prune / check post-passes
 ├── FilterSimulator      — `backup -enum-only` + tree classification for the filter editor
 ├── RecommendedFilters   — curated "common things to exclude" preset
 ├── VolumeDetector       — drive letter + volume label match for external drives
 ├── MeteredNetworkWatchdog — Windows: kills runs that flip onto a metered link mid-flight
 ├── SourceSizeProbe      — estimates source size before first run
 ├── SavingsEstimator     — projects dedup ratio + storage savings
 ├── TaskSchedulerService — Windows Task Scheduler wrapper (COM interop)
 ├── HealthcheckService   — Healthchecks.io REST API client
 ├── MailService          — Resend API + Mailgun SMTP
 ├── NotificationService  — toasts + topmost alert + sound (DND bypass)
 ├── LogStore             — RunRecord history + per-run log files
 ├── AppLogger            — Serilog config (file + console + debug sinks)
 ├── AppPaths             — every on-disk path the app uses (portable)
 ├── AppStatus            — ref-counted "syncing" lease for the taskbar icon animation
 ├── AppNavigator         — cross-VM nav requests ("go to Logs", "open Restore for backup X")
 ├── NameGenerator        — friendly default names for new backups
 ├── KeepPolicyHumanizer  — `0:365 30:90 7:30 1:7` → "keep daily for 7 days, …"
 ├── RunActivityTracker   — recent-run cross-VM event bus
 ├── ShellIntegrationService — Windows: "Restore an older version" right-click menu
 ├── SingleInstanceCoordinator — prevents two GUIs running at once
 ├── CanonicalInstaller   — Windows: install to a known location for scheduled tasks
 ├── KillOnExitJobObject  — Windows: ensures duplicacy.exe dies if we crash
 └── Platform/            — per-OS abstractions (IScheduler, ISecretsEncryption)
                            with Windows/ and Unix/ subfolders
```

## File layout

```
Duplimate/                          ← repo root (folder name may vary locally)
├── README.md, ROADMAP.md, AGENT.md, LICENSE, CONTRIBUTING.md, .gitignore
├── Duplimate.sln                    — Visual Studio solution
├── docs/                                 — GitHub Pages site
│   ├── index.html                        — landing page
│   ├── screenshots/                      — generated PNGs (see scripts/)
│   └── icon.md                           — how the app icon is built
├── assets/brand/                         — master SVG + PNGs of the brand icon
├── scripts/regenerate-website-screenshots.ps1
├── tools/build-icon.ps1                  — generate the 26 .ico files from SVG
├── src/Duplimate/                   — main project (Avalonia desktop app)
│   ├── Duplimate.csproj             — multi-targets net10.0-windows10.0.19041.0 + net10.0
│   ├── Program.cs                        — entry point, argv dispatch
│   ├── App.axaml(.cs)                    — root application resources
│   ├── Models/                           — POCOs serialized to config.json
│   ├── Services/                         — non-UI logic (see above)
│   │   └── Platform/                     — IScheduler / ISecretsEncryption
│   │       ├── Windows/                  — Task Scheduler, DPAPI, etc.
│   │       └── Unix/                     — launchd / systemd, AES-GCM keyfile
│   ├── ViewModels/                       — one per view + dialog, MVVM (CommunityToolkit.Mvvm)
│   ├── Views/                            — Avalonia .axaml + code-behind
│   ├── Themes/                           — Theme.axaml (light/dark) + 5 Accent_*.axaml
│   ├── Assets/                           — embedded icons (per accent × syncing frame),
│   │                                       fonts (Fraunces, Inter, JetBrains Mono),
│   │                                       duplicacy(.exe) (gitignored, fetched at build)
│   ├── FetchDuplicacy.targets            — MSBuild target that downloads duplicacy on demand
│   └── BuildStub.targets                 — Windows-only: publishes the fallback stub
├── src/Duplimate.Stub/              — tiny "main exe missing" fallback (Windows scheduled-task safety)
└── tests/Duplimate.Tests/
    ├── Duplimate.Tests.csproj
    ├── Models/, Services/, ViewModels/, Layout/, Screenshots/  — unit + visual tests
    ├── Cloud/                            — interactive Dropbox/OneDrive/GDrive (gated by env vars)
    ├── E2E/                              — real local Duplicacy backup+restore round-trips
    └── Scenarios/                        — full-flow walk-throughs (see Scenarios/README.md)
```

## Storage locations (on the user's machine)

Everything is **portable**: the app keeps all state in one folder next to
the executable. There is **no** install footprint under `%APPDATA%` or
`%LOCALAPPDATA%`.

- `<exe-dir>/Duplimate.config/config.json` — app settings, backups, destinations
- `<exe-dir>/Duplimate.config/secrets.bin` — encrypted secrets (API keys, OAuth tokens, storage passwords)
- `<exe-dir>/Duplimate.config/duplicacy(.exe)` — extracted on first run
- `<exe-dir>/Duplimate.config/logs/app/` — Serilog daily-rolling app logs
- `<exe-dir>/Duplimate.config/logs/duplicacy/<backup-name>/` — raw duplicacy stdout per run
- `<exe-dir>/Duplimate.config/repos/<backup-name>/<source-slug>/.duplicacy/` — Duplicacy's per-repo pref dir (we keep them under our control rather than next to the source)

The `DUPLIMATE_CONFIG_ROOT` env var overrides the location, used by the
test harness to give each test its own isolated config dir.

## Design principles (stick to these)

1. **Duplicacy owns its own state.** We do not parse or rewrite the
   `preferences`, `keyring`, or `cache` files under `.duplicacy/`. We
   always shell out to the CLI.
2. **All destructive actions are type-to-confirm.** Erase-destination and
   prune-to-latest require typing the backup name. No "are you sure"
   button.
3. **No always-on service.** The platform scheduler is the cron. The only
   times our process runs are: user has GUI open, or the scheduler
   invoked us with `--run`.
4. **Single executable.** `duplicacy(.exe)` is embedded; no separate install step.
5. **Volume-name matching is first-class** for any local destination —
   never failed-state when an external drive is simply unmounted, always
   "skipped".
6. **No surprises in the config dir.** One `config.json`; a user can
   delete it to start over. Secrets in a separate machine-bound file so
   the JSON is shareable / diffable.
7. **Portable layout.** Everything under `Duplimate.config/` next to
   the exe — see Storage locations above. Don't reach into `%APPDATA%`.

## CLI surface

- `Duplimate`                              — launch GUI
- `Duplimate --run <backup-name>`          — unattended single-backup run (scheduler entry point)
- `Duplimate --run-all`                    — unattended, all enabled backups
- `Duplimate --migrate <path>`             — one-off import from an existing Duplicacy setup (single repo or parent-of-many)
- `Duplimate --version`
- `Duplimate --help | -h | /?`

On Windows the binary is `Duplimate.exe`; on Unix it's `Duplimate`.

## Invariants to protect during refactors

- The `--run` / `--run-all` code path must be usable **without** the GUI
  loaded (no `App.Current`-style references). It spins up only what it
  needs via `ServiceLocator.InitializeCore`.
- `DuplicacyRunner` must stream logs live, never `ReadToEnd()` (backups
  are long and produce GB of log over days).
- Config file reads and writes must be **atomic** (write to `.tmp`,
  rename) — a scheduled run losing config due to a power cut is
  unacceptable.
- Tasks registered with Windows Task Scheduler must use S4U logon and
  `StartWhenAvailable` (survive sleep, run missed tasks later).
- Cross-platform code paths under `Services/Platform/`: never call a
  Windows API directly from `src/Duplimate/Services/*.cs` — go
  through the `IScheduler` / `ISecretsEncryption` interface or the
  `PlatformInfo.IsWindows` guard. The csproj `Compile Remove` excludes
  `Services/Platform/Windows/` from the cross-platform TFM, so a
  direct call would compile on Windows but fail to load on macOS / Linux.

## Build & run

```bash
# Debug build (Windows, full feature set)
dotnet build src/Duplimate -c Debug

# Run the GUI from source
dotnet run --project src/Duplimate

# Headless run of one backup (same code path as scheduled tasks)
dotnet run --project src/Duplimate -- --run my-backup

# Release publish (what users get) — Windows
dotnet publish src/Duplimate -c Release -r win-x64 --self-contained \
  -f net10.0-windows10.0.19041.0

# Release publish — macOS (ARM) or Linux (x64) — cross-platform TFM
dotnet publish src/Duplimate -c Release -r osx-arm64 --self-contained \
  -f net10.0 -p:PublishSingleFile=true
dotnet publish src/Duplimate -c Release -r linux-x64 --self-contained \
  -f net10.0 -p:PublishSingleFile=true
```

The build auto-downloads the host-platform Duplicacy CLI from
[gilbertchen/duplicacy releases](https://github.com/gilbertchen/duplicacy/releases/latest)
into `src/Duplimate/Assets/` on first build. Pass
`-p:SkipDuplicacyDownload=true` to build offline (drop your own copy in
beforehand). Pass `-p:SkipStubBuild=true` to skip the Windows fallback
stub publish.

Convenience: `launch-debug.bat` / `launch-release.bat` (Windows),
`launch-debug.sh` / `launch-release.sh` (Unix).

## Test suite

```bash
# Full unit + visual suite (no live cloud)
dotnet test tests/Duplimate.Tests \
  --filter "FullyQualifiedName!~Cloud&FullyQualifiedName!~E2E"

# Local Duplicacy round-trip tests (runs real duplicacy.exe on a temp dir)
dotnet test tests/Duplimate.Tests --filter "FullyQualifiedName~E2E"

# Cloud tests (interactive — requires live OAuth tokens via secrets file
# or env var, gated by DUPLIMATE_TEST_INTERACTIVE=1)
dotnet test tests/Duplimate.Tests --filter "FullyQualifiedName~Cloud"
```

The visual / scenario harness writes PNGs under
`tests/Duplimate.Tests/artifacts/` and a `SUMMARY.md` after each
scenario run — see `tests/Duplimate.Tests/Scenarios/README.md` for
the auditing workflow.

To regenerate the 13 screenshots used on the website:

```powershell
pwsh scripts/regenerate-website-screenshots.ps1
```

## Visual + interaction E2E test philosophy

The standing rule: don't QA by hand every time something changes. Build
the test pipeline so an agent (or CI) can:

1. **Boot the real app** headlessly — same `App.axaml.cs`, same DI graph,
   same resources — pointing at a throwaway `DUPLIMATE_CONFIG_ROOT`.
2. **Generate dummy source data** under
   `<repo>/tests/Duplimate.Tests/artifacts/scenarios/<name>/sources/...`
   via `SyntheticFileTree.Generate(...)`. Keep totals small (≤ a few MB
   per scenario) so the suite stays fast.
3. **Drive each scenario** through scripted UI interactions — show
   MainWindow, click buttons, fill text, navigate the wizard, run the
   backup, restore, verify. Each step renders a PNG under
   `artifacts/scenarios/<name>/<NN>-<step>.png`.
4. **Auto-skip secret-gated scenarios** when a required token is missing.
   `SecretsLoader` looks under `tests/Duplimate.Tests/secrets/`
   (gitignored) and returns null + records the reason; the runner marks
   the scenario `Skipped(reason)` instead of failing.
5. **Write a markdown summary** at `artifacts/scenarios/SUMMARY.md`
   after each suite run, listing every scenario's status, screenshot
   index, and any anomalies the auditor caught.
6. **Iterate**. The agent runs the suite, opens the summary + select
   PNGs, files findings, fixes them in the source, re-runs.

Why: human stays in the loop only for the bits requiring real account
access (Dropbox OAuth round-trips against live API, OneDrive / GDrive
once they ship). Everything else is mechanically verifiable.

**Adding a scenario.** Subclass `ScenarioBase`, override `RunAsync`,
script the user flow with `ctx.Driver` helpers, and call
`ctx.Snapshot("name")` at every interesting state transition. Mark the
class with `[Fact]` so xUnit runs it. Gate secret-dependent flows on
`if (!ctx.Secrets.TryGetDropboxToken(out var token)) { ctx.Skip("…"); return; }`.

### Standing instruction for any agent that runs the scenario suite

When you run the scenarios (directly, via `dotnet test`, or as part of a
broader run), don't stop at "tests passed/failed":

1. Read `tests/Duplimate.Tests/artifacts/scenarios/SUMMARY.md`.
2. For every `❌ FAIL`: investigate, fix the underlying code in
   `src/Duplimate/`, re-run. If you can't fix in one or two passes,
   surface a clear diagnosis instead of leaving it unaddressed.
3. For every `**Anomalies:**` block: open the listed PNG. If it's a
   real visual regression (blank frame, clipped control, wrong colour,
   broken hover), fix it in source. If it's a false positive, tighten
   `tests/Duplimate.Tests/Scenarios/VisualAuditor.cs`.
4. For `⊘ SKIP` rows whose reason is a missing secret: do nothing.
   Those are user-supplied; mention them once in the turn summary.
5. After any fix, re-run and confirm the affected rows flipped to
   `✅ PASS`.

A single fix-and-rerun pulse, not an unbounded loop. Stop and report if
real issues remain after two passes.

## Theme & accent system

- `Themes/Theme.axaml` — light + dark resource dictionaries with the
  base palette (`DM.Brush.BgBase`, `DM.Brush.FgPrimary`, etc.).
- `Themes/Accent_<Name>.axaml` — one per accent: `Ocean` (default,
  `#0061FF`), `Emerald`, `Plum`, `Graphite`, `Terracotta`.
  Each defines `DM.Brush.Accent` + variants and is merged into App
  resources when the user picks an accent in Settings.
- `Themes/Controls.axaml` — control styles built on top of the palette.
- `Themes/Icons.axaml` — Lucide-style stroked path icons referenced as
  `{StaticResource Icon.<Name>}`.

The icon system: 26 .ico files under `Assets/` — one idle per accent +
4 syncing frames per accent + the default. See `docs/icon.md` for the
generation pipeline.

## Known sharp edges

- **OAuth first-run flow** for Dropbox / OneDrive / Google Drive uses
  Duplicacy's `duplicacy.com/<provider>_start` helper page; the user
  copy-pastes a token. If the helper page format changes, the auto-
  extraction may fail and the manual paste box becomes the fallback.
  `DropboxAuthProbe` verifies the token before save so a stale paste
  fails loudly.
- **`-enum-only`** is not a documented Duplicacy CLI flag but has been
  stable for years. `FilterSimulator` depends on it. If it disappears
  we'd need to diff the `backup` output of a no-op second run.
- **Healthchecks.io management API** has rate limits. We batch settings
  updates. Adding 50 backups in a loop may hit 429s; we retry with
  backoff.
- **macOS / Linux**: the cross-platform TFM compiles and the platform
  abstractions are in place, but the suite hasn't run there. Expect
  papercuts the first time someone tries.
- **`NumericUpDown.Value`** is `decimal?` in Avalonia 11; we bind it to
  plain `int` properties and rely on default numeric conversion. Works
  in practice; wrap in a converter if it ever throws.

## Where to look next

- `ROADMAP.md` — what's deferred from v1 (analytics, passphrase secrets,
  copy-between-destinations, cross-platform polish).
- `docs/icon.md` — icon generation pipeline if you're touching the
  brand or accent palette.
- `tests/Duplimate.Tests/Scenarios/README.md` — how the scenario
  harness works + how to add a new scenario.
- `CONTRIBUTING.md` — PR conventions.
