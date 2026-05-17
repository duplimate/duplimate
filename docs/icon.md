# Icon pipeline

The Duplimate app icon is **cloud-check**: a soft, friendly cloud
with a check mark cut out of its body, on a rounded-square tile in
the current accent color. Reads as "safely stored in the sky" and
stays legible down to 16×16.

## Files

```
assets/brand/
  ├── duplimate.svg              ← master design, 1024×1024, Ocean background
  ├── duplimate-<size>.png     ← rasterized Ocean outputs at 16..1024 (generated)
  └── concepts/                     ← 36 alternative concepts (history + easy swap)

src/Duplimate/Assets/
  ├── Duplimate.ico                        ← default embedded icon (Ocean)
  ├── Duplimate-<Accent>.ico               ← idle: cloud + check, per accent
  └── Duplimate-<Accent>-Syncing-<N>.ico   ← syncing: rotated arrows, frame N
```

Accents: `Ocean`, `Emerald`, `Plum`, `Graphite`, `Terracotta`. Syncing
frames: `0..3` (see `SyncingFrameCount` in `MainWindow.axaml.cs`), each
frame rotated by `360° / SyncingFrameCount` around the tile center. That
gives **5 × (1 idle + 4 syncing) = 25 ICOs** committed to the repo, ~6 KB
each.

When the user changes the accent in Settings, `MainWindow` reloads the
matching ICO so the **taskbar, alt-tab, and window icon all re-color
with the accent**. The sidebar logo (inline in `MainWindow.axaml`)
tracks the accent directly via `DynamicResource DM.Brush.Accent`, so
it re-colors without reloading.

## Idle vs syncing

`Services/AppStatus.cs` exposes a ref-counted `BeginSyncing()` lease.
`BackupOrchestrator.RunBackupAsync` and `RestoreEngine.RunAsync` both
take a lease for the duration of their work. When the lease count goes
`0 → 1`, the shell:

- swaps the window / taskbar icon to `Duplimate-<Accent>-Syncing-0.ico`
- starts a `DispatcherTimer` at `FrameInterval` (160 ms — slow enough to read, fast enough to feel alive)
  that cycles `Syncing-0 → Syncing-1 → Syncing-2 → Syncing-3 → Syncing-0 …`
- shows the inline `SidebarSyncOverlay` in the sidebar logo and rotates
  it in lockstep with the taskbar frame index (same angle steps), so
  taskbar and sidebar move as one animation
- hides the idle check overlay (`SidebarIdleOverlay`)

When the last lease disposes (count `→ 0`), the idle overlay comes back
and `ApplyAccentIcon()` restores the static cloud-check icon. Nesting
is safe: if a backup and a restore are running in parallel, the icon
stays in the syncing state until **both** finish.

Pre-baking rotated frames instead of live-rendering keeps CPU/GPU usage
near zero during long backups — swapping a `WindowIcon` is cheap;
re-rasterizing 16..256 px PNGs every 160 ms is not.

## How to regenerate the icon

```powershell
# Default single-file (Ocean) build — overwrites Duplimate.ico
.\tools\build-icon.ps1

# Arbitrary color for experimentation
.\tools\build-icon.ps1 -Accent "#059669"

# Canonical build: all 5 idle ICOs + 5×SyncingFrameCount syncing ICOs
# + the default Ocean ICO. 25 files total.
.\tools\build-icon.ps1 -All
```

Run the `-All` variant whenever the design or the accent palette
changes. If you change `SyncingFrameCount`, update the matching
constant in `src/Duplimate/Views/MainWindow.axaml.cs` — the PS
script and the C# frame loader must agree on how many frames exist.

## How to change the design

1. Edit the geometry in `assets/brand/duplimate.svg` (1024 viewBox).
2. Mirror the change in the `Write-CloudCheck` function inside
   `tools/build-icon.ps1` — the PS script is the source-of-truth
   renderer; the SVG is the human-readable spec. Both must agree.
3. If the sidebar logo visually matches the icon, update its
   primitives in `src/Duplimate/Views/MainWindow.axaml` too (the
   shape coordinates there are scaled from the 1024 master to a 36×36
   tile — multiply each master value by `36 / 1024` ≈ `0.0352`).
4. Run `.\tools\build-icon.ps1 -All`.
5. Rebuild the app.
6. Commit the new PNGs and ICOs.

## Picking a different concept from the previews

`tools/build-concept-previews.ps1` generates ~36 candidate icons under
`assets/brand/concepts/` plus a contact-sheet PNG
(`assets/brand/concepts/_contact-sheet.png`). To swap to another
design entirely:

1. Open the contact sheet and pick a slug.
2. Copy the matching scriptblock from `build-concept-previews.ps1`
   into `build-icon.ps1`, replacing the body of `Write-CloudCheck`
   (and rename the function appropriately).
3. Update `duplimate.svg` to match.
4. Update the sidebar logo's inline geometry in `MainWindow.axaml`
   if you want the brand wordmark to stay visually consistent with
   the taskbar icon.
5. Run `.\tools\build-icon.ps1 -All` and commit.

The preview script stays in the repo as a record of what was
considered, so future redesigns can pick up where this one left off.

## Future work

The current flow is script-based. A nicer v2 would integrate into
MSBuild — a `<Target>` that runs the PS script when the SVG is newer
than the ICO. For now, manual is fine and the outputs are small and
versioned.
