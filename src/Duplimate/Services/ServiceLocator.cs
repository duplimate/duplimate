using System;
using Duplimate.Services.Platform;

namespace Duplimate.Services;

/// <summary>
/// Light static DI: one-line construction, no container. The app creates all
/// services once (either from the GUI entry point or the unattended CLI) and
/// ViewModels ask for what they need. Good enough for an app this size.
/// </summary>
public static class ServiceLocator
{
    public static ConfigStore Config { get; private set; } = default!;
    public static SecretsStore Secrets { get; private set; } = default!;
    public static DuplicacyRunner Runner { get; private set; } = default!;
    public static LogStore Logs { get; private set; } = default!;
    public static HealthcheckService Healthcheck { get; private set; } = default!;
    public static MailService Mail { get; private set; } = default!;
    public static NotificationService Notifier { get; private set; } = default!;
    public static BackupOrchestrator Orchestrator { get; private set; } = default!;
    public static IScheduler Scheduler { get; private set; } = default!;
    public static FilterSimulator Filters { get; private set; } = default!;
    public static ConfigMigrator Migrator { get; private set; } = default!;
    public static RestoreEngine Restore { get; private set; } = default!;
    public static RevisionBrowser Revisions { get; private set; } = default!;
    public static DropboxAuthProbe DropboxProbe { get; private set; } = default!;
    public static DestinationProbe DestProbe { get; private set; } = default!;
    public static StorageCleaner Cleaner { get; private set; } = default!;
    public static SourceSizeProbe SizeProbe { get; private set; } = default!;
    public static SavingsEstimator Savings { get; private set; } = default!;
    public static BackupVerifier Verifier { get; private set; } = default!;
    public static BackupProgressService Progress { get; private set; } = default!;
    public static RunActivityTracker Activity { get; private set; } = default!;
    public static MaintenanceCoordinator Maintenance { get; private set; } = default!;

    public static bool Initialized { get; private set; }

    public static void InitializeCore()
    {
        if (Initialized) return;
        AppPaths.EnsureAll();

        Config = new ConfigStore();
        Config.Load();

        // Initialize structured logging now that we have LoggingSettings.
        AppLogger.Initialize(Config.Current.Logging);
        Config.Changed += (_, __) => AppLogger.Initialize(Config.Current.Logging);

        Secrets = new SecretsStore();
        Runner = new DuplicacyRunner(Config, Secrets);
        Logs = new LogStore();
        Healthcheck = new HealthcheckService(Config, Secrets);
        Mail = new MailService(Config, Secrets);
        Notifier = new NotificationService(Config);
        Progress = new BackupProgressService();
        Activity = new RunActivityTracker();
        Maintenance = new MaintenanceCoordinator();
        Orchestrator = new BackupOrchestrator(Config, Secrets, Runner, Logs, Healthcheck, Mail, Notifier, Progress);
        Scheduler = TaskSchedulerService.Create();
        Filters = new FilterSimulator(Runner);
        Migrator = new ConfigMigrator(Config);
        Restore = new RestoreEngine(Config, Runner, Secrets);
        Revisions = new RevisionBrowser(Runner, Secrets);
        DropboxProbe = new DropboxAuthProbe(Secrets);
        DestProbe = new DestinationProbe(Secrets);
        Cleaner = new StorageCleaner(Config, Secrets, Runner) { Probe = DestProbe, LogStore = Logs };
        SizeProbe = new SourceSizeProbe();
        Savings = new SavingsEstimator();
        Verifier = new BackupVerifier(Config, Restore, Revisions);

        Initialized = true;
    }
}
