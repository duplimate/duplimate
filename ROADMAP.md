# Roadmap

## v1

All core backup-management functionality a single user needs to set up, run,
monitor, and restore Duplicacy backups from a GUI.

### Destinations
- [x] Local folder
- [x] External drive **with volume-name matching** (preserves original "My Passport on M:" logic)
- [x] Network share / SMB path
- [x] Dropbox (app-scoped, via `dropbox://` + duplicacy.com OAuth helper)
- [x] OneDrive (personal `one://` and Business `odb://`)
- [x] Google Drive (`gcd://`)
- [x] S3 & S3-compatible (Wasabi, Cloudflare R2, MinIO, Backblaze-B2-via-S3) (`s3://`)
- [x] Re-authentication flow when tokens expire
- [x] Auth tokens stored via Duplicacy's own keyring (not by us)

### Backup management
- [x] Create / edit source→destination pairs
- [x] Common options surfaced first (threads, VSS, keep policy, schedule)
- [x] Advanced options in collapsible panel (chunk size, iv-key, etc.)
- [x] Manual "run now" trigger with live stdout stream
- [x] Filter / exclusion editor with **live simulation** (`backup -enum-only`) and color-coded tree
- [x] Import from existing Duplicacy yaml configs on first launch

### Scheduling & execution
- [x] Task Scheduler integration (create / edit / disable / run-now / pause-for-N-days)
- [x] Unattended CLI mode (`Duplimate.exe --run <backup-name>`) invoked by scheduled task
- [x] Metered-network watchdog folded in-process (no separate .NET bootstrap)
- [x] Volume-mount check with graceful "skipped, drive not mounted" (not treated as failure)

### Monitoring & alerting
- [x] Healthchecks.io integration — user provides personal API key, app auto-creates/names/tags checks and sets schedule
- [x] Local early-warning: stdout parsing for ERROR / Failed to / access denied
- [x] Toast notifications
- [x] Topmost alert window + sound for DND bypass (configurable per-notification-type, on by default for failures)
- [x] Email notifications via **Resend** (API-key-only, 3000/mo free) or **Mailgun** (SMTP, your existing path)

### Maintenance actions (destructive)
- [x] Erase destination storage (type-to-confirm)
- [x] Prune-to-latest-only (type-to-confirm)
- [x] Manual prune with configurable keep policy

### Inspection
- [x] Show last revision's new/modified files with sizes
- [x] List all revisions per backup

### Restore (guided wizard)
- [x] Pick backup + destination (auto-skips destination step when only one)
- [x] Pick revision (ID + datetime + size, sortable, default = most recent)
- [x] Pick files / folders (lazy tree, search, tri-state folder checkboxes)
- [x] Pick target (default = original source with OVERWRITE-to-confirm gate; alternative = any folder)
- [x] Options: preserve directory structure (default on), overwrite existing (default on), threads (default 4)
- [x] Live progress (overall bar + per-file list + failures list + log pane)
- [x] Summary (counts, per-file retry, "retry all failed", open target folder)
- [x] Retry cascade: per file independent, delays 1s → 5s → 10s → 30s → 60s, bounded by thread count, runs in parallel so one slow file doesn't block others
- [x] Retry policy global in Settings, overridable on the restore dialog

### Distribution
- [x] Single `.exe` (self-contained, no SDK install needed)
- [x] No separate `duplicacy.exe` — embedded as resource and extracted on first run

---

## v1.1 (deferred)


### Hot-folder analytics & exclusion suggestions
- [ ] Parse per-backup log to extract new/modified file paths + sizes
- [ ] SQLite DB under `Duplimate.config/analytics.db` accumulating churn stats per path across runs
- [ ] Suggestion engine: surface top-N hot paths not yet excluded
- [ ] Ignore-suggestion store (persistent, editable) — "don't recommend this path again"
- [ ] Whitelist wildcards — user can explicitly mark paths as "always include, never suggest"
- [ ] Analytics tab in UI: per-path stats, trend lines, suggestion queue, review/accept/ignore actions

### Passphrase-mode secrets (cross-machine portability)
- [ ] Optional master passphrase on first launch (Argon2id → AES-GCM)
- [ ] If enabled, `secrets.bin` decrypts on any machine the user types the passphrase on (not machine-bound)
- [ ] Optional DPAPI caching of the unlocked secrets per machine so the passphrase isn't needed on every launch
- [ ] First-run wizard on a new machine detects a config.json-without-matching-secrets and offers passphrase unlock
- [ ] Default remains DPAPI-only (simpler) — this is strictly opt-in for users who copy the config folder between PCs

### Migrate a backup between destinations
- [ ] Wrap Duplicacy's `copy` sub-command (`duplicacy copy -from <src-storage> -to <dst-storage>`)
- [ ] Supports replicating a full backup history onto a new destination without re-uploading from source
- [ ] UI flow: pick backup → pick "from" destination → pick "to" destination → confirm → stream progress
- [ ] After copy completes: option to swap the backup's primary destination (keeps the old one read-only or deletes it)
- [ ] Common use case: moving from Dropbox to Google Drive without losing revision history

---

## Backlog / nice-to-haves (not scheduled)

- [ ] Cross-platform: macOS + Linux (Avalonia already supports them; just need to swap Task Scheduler for `launchd`/`systemd`/`cron`)
- [ ] Additional destinations Duplicacy supports but we haven't wired yet: Azure Blob, B2 native, Storj, Swift/OpenStack, WebDAV, SFTP
- [ ] Backup-of-backup: scheduled `copy` between two destinations (on top of the v1.1 one-shot migration)
- [ ] Bandwidth throttling UI for the `-limit-rate` flag
- [ ] End-to-end-encryption: bring-your-own-RSA-key for the `-key-file` option
- [ ] Mobile companion app (read-only dashboard via Healthchecks.io API)
- [ ] i18n (English only for v1)

---

## Test-suite gaps (deferred — know where the blind spots are)

The automated suite under [`tests/Duplimate.Tests/`](tests/Duplimate.Tests/) covers layout alignment, icon rendering, view-model commands, synthetic file generation, WCAG contrast of the palette, local backup+restore roundtrip (plain and encrypted), editor-dialog interaction, the destructive-confirm gate, backup-run cancellation, prune/check post-passes, Dropbox interactive E2E, plus PNG screenshots of every view and dialog in both light and dark themes with baseline diffing. What it **doesn't** cover — ranked by "how much would this bite us":

### Medium-signal

- [ ] **OneDrive / Google Drive / S3 interactive E2E.** Copy-paste of `DropboxBackupRestoreTests` — just swap the `Kind` enum and the helper URL. Blocked only on the auth dance per provider.
- [ ] **Scheduling integration test.** Register a throwaway `\Duplimate\test-*` task with `TaskSchedulerService`, assert Windows picked it up, delete it. Needs a Windows test environment with rights to create scheduled tasks.
- [ ] **Log retention enforcement.** `LogRetentionDays` is configurable but nothing in `LogStore` actually sweeps old files today — add the sweep + a test.
- [ ] **Healthchecks.io integration test.** Hits the live API with a per-run test API key; gate behind `DUPLIMATE_TEST_INTERACTIVE=1`.
- [ ] **Email delivery test** (Resend + Mailgun). Same pattern — live send to a test inbox, gate behind the interactive flag.

### Lower priority / hardware-dependent

- [ ] **Metered-network watchdog.** `MeteredNetworkWatchdog` reads Windows' NetworkInformation API. Hard to mock without a real metered connection or an API fake.
- [ ] **`VSS` (Volume Shadow Copy) path.** Requires admin + a locked file to prove shadow-copy is used. Skip until a user reports an issue in practice.

### Known audit blind spots of the harness itself

- [ ] Dark-mode screenshot coverage is 3 views + 1 dialog — not every surface is re-captured in dark theme.
- [ ] `ContrastTests` parses Theme.axaml with a regex rather than loading it through Avalonia. If the file structure changes, the regex needs updating.
- [ ] Screenshot baseline threshold is 1% of pixels — deliberately loose to tolerate Windows font-rendering jitter across updates. Very small but real regressions (e.g. a badge that shifts 2px) can slip under it.
