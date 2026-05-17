using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Duplimate.Models;
using Duplimate.Services;

namespace Duplimate.ViewModels;

/// <summary>
/// A slim wizard-like editor for a single Destination. Per-kind field visibility
/// is driven by ShowX properties below.
/// </summary>
public sealed partial class DestinationEditorViewModel : ViewModelBase, IDisposable
{
    public Destination Working { get; }

    /// <summary>
    /// True when this editor was opened from the Easy-mode wizard
    /// (onboarding). Hides advanced fields like the Cloud subfolder
    /// (which always auto-fills cleanly), so first-time users aren't
    /// asked to make decisions they shouldn't have to. Set via the
    /// <see cref="DestinationEditorViewModel(Destination, bool, bool)"/>
    /// constructor overload.
    /// </summary>
    public bool SimplifiedMode { get; }
    /// <summary>True iff the Cloud subfolder field should render — i.e.
    /// the user is in Advanced mode and the destination is a cloud
    /// kind. Easy-mode users get the auto-fill path silently.</summary>
    public bool ShowCloudSubfolderField => !SimplifiedMode && Working.NeedsOAuth;
    public bool IsNew { get; }
    public bool Dropbox11OnRoadmap => true;  // v1.1 gate for full-access variant

    [ObservableProperty] private string _name;

    // ---- Options reveal (same pattern as BackupEditor — VM-backed toggle
    // instead of element-name binding, so the ScrollViewer reliably
    // includes the toggle row in its measured content extent). ----
    [ObservableProperty] private bool _isOptionsOpen;
    public string OptionsToggleLabel => IsOptionsOpen ? "Hide options" : "Show options";
    partial void OnIsOptionsOpenChanged(bool value) => OnPropertyChanged(nameof(OptionsToggleLabel));

    [RelayCommand] private void ToggleOptions() => IsOptionsOpen = !IsOptionsOpen;

    // ---- validation state (only shown after the user has tried to Save) ----
    [ObservableProperty] private bool _attemptedSave;
    [ObservableProperty] private string? _kindError;
    [ObservableProperty] private string? _nameError;
    [ObservableProperty] private string? _pathError;
    [ObservableProperty] private string? _bucketError;
    [ObservableProperty] private string? _summaryError;
    public bool HasAnyError => !string.IsNullOrEmpty(SummaryError);
    /// <summary>
    /// Selected destination kind. Nullable so new destinations start with
    /// the Type dropdown empty — the old "default to LocalFolder" behavior
    /// silently pushed users into the local-folder branch of the form
    /// before they'd made a choice, which was confusing when they
    /// actually wanted a cloud destination.
    /// </summary>
    [ObservableProperty] private DestinationKind? _kind;
    [ObservableProperty] private string _pathOrSubpath;
    [ObservableProperty] private string? _expectedDriveLetter;
    [ObservableProperty] private string? _expectedVolumeLabel;

    // S3
    [ObservableProperty] private string? _s3Endpoint;
    [ObservableProperty] private string? _s3Region;
    [ObservableProperty] private string? _s3Bucket;
    [ObservableProperty] private string _s3AccessKeyInput = "";
    [ObservableProperty] private string _s3SecretKeyInput = "";

    // Network
    [ObservableProperty] private string? _networkUsername;
    [ObservableProperty] private string _networkPasswordInput = "";

    // Encryption + OAuth. Default OFF; the RECOMMENDED badge in the
    // editor only fires for cloud / S3 destinations (see
    // ShowEncryptionRecommendedBadge), where the data leaves the user's
    // physical possession.
    [ObservableProperty] private bool _encrypted = false;
    [ObservableProperty] private string _storagePasswordInput = "";
    [ObservableProperty] private string _oAuthTokenInput = "";
    [ObservableProperty] private string _authStatus = "";
    [ObservableProperty] private bool _hasOAuthTokenStored;
    [ObservableProperty] private bool _authBusy;
    [ObservableProperty] private bool _authOk;

    /// <summary>
    /// True when an auth verification has finished (not in flight) and
    /// failed (AuthOk == false) and there's a status message to surface.
    /// Drives the red-highlight + error-tone styling on the OAuth token
    /// field and its status caption — earlier the failure rendered as
    /// passive grey, which made "Couldn't verify…" look like an info
    /// hint rather than an error the user needs to fix.
    /// </summary>
    public bool IsAuthError =>
        !AuthOk && !AuthBusy && !string.IsNullOrEmpty(AuthStatus);

    /// <summary>
    /// True while a token verification is currently in flight. Drives a
    /// distinct "checking…" callout under the token field so the user
    /// understands the form isn't stuck — Save is disabled until either
    /// AuthOk flips true or the verifier returns an error.
    /// </summary>
    public bool IsAuthVerifying => AuthBusy && !string.IsNullOrEmpty(AuthStatus);

    /// <summary>True when verification finished successfully. Drives the
    /// green "verified" callout under the token field.</summary>
    public bool IsAuthSuccess => AuthOk && !string.IsNullOrEmpty(AuthStatus);

    /// <summary>True iff any of the three auth status callouts (busy,
    /// success, error) should render — keeps the under-field area
    /// blank when there's nothing to say (e.g. user hasn't pasted
    /// anything yet on a brand-new destination).</summary>
    public bool HasAuthStatus => IsAuthVerifying || IsAuthSuccess || IsAuthError;

    /// <summary>Watermark for the token TextBox. For brand-new
    /// destinations this prompts "Paste the token..."; for already-
    /// saved destinations it tells the user the token is on file so
    /// they don't need to re-paste just to edit other fields.</summary>
    public string TokenInputWatermark =>
        Working.LastTokenValidatedUtc is not null
            ? "Token on file — paste a new one only if you want to replace it"
            : "Paste the token from the browser here";

    /// <summary>Watermark for the storage-password TextBox.
    /// For an already-encrypted destination the field is DISABLED (see
    /// <see cref="StoragePasswordEditable"/>) and we display an
    /// obfuscated placeholder so the user can clearly see that a
    /// password is set without us leaking the real value. For a new
    /// destination we surface the auto-generate hint.</summary>
    public string StoragePasswordWatermark =>
        StoragePasswordIsLocked
            ? "••••••••••••••"
            : "Leave blank to auto-generate on save (we'll show it to you once)";

    /// <summary>True when this destination already has an encrypted
    /// storage password on file. Duplicacy doesn't expose a clean way
    /// to rotate the storage password — the data chunks are encrypted
    /// with a master key, and that master key is itself encrypted with
    /// the user's password; both <c>config</c> and chunks become
    /// unreadable if the stored password no longer matches what was
    /// written at <c>init</c> time. The official path to change a
    /// password is to delete the destination + re-create with a new
    /// password (which loses historical revisions). The UI reflects
    /// this by locking the field rather than offering a setter we
    /// can't honour. -->
    /// </summary>
    public bool StoragePasswordIsLocked =>
        !IsNew && Encrypted && !string.IsNullOrEmpty(Working.StoragePasswordRef);

    /// <summary>Inverse of <see cref="StoragePasswordIsLocked"/>, bound
    /// to the TextBox's IsEnabled. Separate property so the XAML reads
    /// straightforwardly (`IsEnabled="{Binding StoragePasswordEditable}"`).</summary>
    public bool StoragePasswordEditable => !StoragePasswordIsLocked;

    partial void OnEncryptedChanged(bool value)
    {
        // Encryption toggle flipping changes the lock predicate.
        OnPropertyChanged(nameof(StoragePasswordIsLocked));
        OnPropertyChanged(nameof(StoragePasswordEditable));
        OnPropertyChanged(nameof(StoragePasswordWatermark));
    }

    partial void OnAuthOkChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAuthError));
        OnPropertyChanged(nameof(IsAuthSuccess));
        OnPropertyChanged(nameof(HasAuthStatus));
        // When the user fixes a previously-invalid token (auth flips
        // back to true), clear the validation summary banner so they
        // can see the form is good to save without having to click
        // Save first to dismiss the stale error.
        if (value && !string.IsNullOrEmpty(SummaryError))
        {
            SummaryError = null;
            OnPropertyChanged(nameof(HasAnyError));
        }
    }
    partial void OnAuthBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAuthError));
        OnPropertyChanged(nameof(IsAuthVerifying));
        OnPropertyChanged(nameof(HasAuthStatus));
    }
    partial void OnAuthStatusChanged(string value)
    {
        OnPropertyChanged(nameof(IsAuthError));
        OnPropertyChanged(nameof(IsAuthVerifying));
        OnPropertyChanged(nameof(IsAuthSuccess));
        OnPropertyChanged(nameof(HasAuthStatus));
    }

    /// <summary>
    /// Short, friendly description of what the OAuth grant will let
    /// Duplimate do, shown right next to the Connect button so a
    /// non-technical user reads it before visiting the browser helper.
    /// </summary>
    public string OAuthScopeExplanation => Kind switch
    {
        DestinationKind.DropboxAppScoped =>
            "Duplimate will only see the Apps/Duplicacy folder in your Dropbox — not your personal files.",
        DestinationKind.DropboxFullAccess =>
            "Duplimate will have access to your entire Dropbox. Only use this if you're backing up Dropbox-wide paths.",
        DestinationKind.OneDrivePersonal =>
            "Duplimate will access your personal OneDrive.",
        DestinationKind.OneDriveBusiness =>
            "Duplimate will access your OneDrive for Business tenant as the signed-in user.",
        DestinationKind.GoogleDrive =>
            "Duplimate will access your Google Drive.",
        _ => "",
    };

    public string OAuthConnectButtonText => Kind switch
    {
        DestinationKind.DropboxAppScoped  => "Connect to Dropbox (app folder)",
        DestinationKind.DropboxFullAccess => "Connect to Dropbox (full access)",
        DestinationKind.OneDrivePersonal  => "Connect to OneDrive",
        DestinationKind.OneDriveBusiness  => "Connect to OneDrive for Business",
        DestinationKind.GoogleDrive       => "Connect to Google Drive",
        _ => "Connect",
    };

    private CancellationTokenSource? _validationCts;

    /// <summary>Cancels any in-flight OAuth-token probe. Called from
    /// the editor window's Closed handler so a user pasting then
    /// closing doesn't leave duplicacy.exe probing in the background.
    /// Without this, each rapid paste-then-close cycle leaks one
    /// duplicacy.exe + one duplimate-dbx-probe-* TEMP folder until the
    /// process eventually finishes.</summary>
    public void Dispose()
    {
        try { _validationCts?.Cancel(); _validationCts?.Dispose(); } catch { }
        _validationCts = null;
    }

    public IReadOnlyList<DestinationKind> AvailableKinds { get; } = new[]
    {
        DestinationKind.LocalFolder,
        DestinationKind.ExternalDrive,
        DestinationKind.NetworkShare,
        DestinationKind.DropboxAppScoped,
        // DestinationKind.DropboxFullAccess,  // v1.1
        DestinationKind.OneDrivePersonal,
        DestinationKind.OneDriveBusiness,
        DestinationKind.GoogleDrive,
        DestinationKind.S3Compatible,
    };

    // Derived flags for view visibility
    public bool ShowLocalPath => Kind is DestinationKind.LocalFolder or DestinationKind.ExternalDrive;
    public bool ShowNetworkPath => Kind == DestinationKind.NetworkShare;
    public bool ShowNetworkCreds => Kind == DestinationKind.NetworkShare;
    public bool ShowVolumeMatch => Kind is DestinationKind.LocalFolder or DestinationKind.ExternalDrive;
    public bool ShowCloudSubpath => Kind is DestinationKind.DropboxAppScoped
        or DestinationKind.DropboxFullAccess
        or DestinationKind.OneDrivePersonal
        or DestinationKind.OneDriveBusiness
        or DestinationKind.GoogleDrive;
    public bool ShowOAuth => Working.NeedsOAuth;
    public bool ShowS3 => Kind == DestinationKind.S3Compatible;

    /// <summary>
    /// Cloud / S3 destinations send data outside the user's physical
    /// possession, so encryption is genuinely valuable there. Local /
    /// external / network destinations sit inside the user's home or LAN,
    /// where the privacy gain is smaller and the "lose the password →
    /// lose the data" risk usually wins. The badge nudges only where it
    /// actually matters.
    /// </summary>
    public bool ShowEncryptionRecommendedBadge =>
        Working.NeedsOAuth || Kind == DestinationKind.S3Compatible;

    public string? CompleteResult { get; private set; }  // set by Save

    /// <summary>If we auto-generated a storage password during Save,
    /// the plaintext goes here so the calling window can present a
    /// "back this up — we'll never show it again" dialog. Null means
    /// either the user supplied their own password or no encryption.</summary>
    public string? GeneratedStoragePassword { get; private set; }

    public DestinationEditorViewModel(Destination working, bool isNew)
        : this(working, isNew, simplifiedMode: false) { }

    public DestinationEditorViewModel(Destination working, bool isNew, bool simplifiedMode)
    {
        Working = working;
        IsNew = isNew;
        SimplifiedMode = simplifiedMode;
        _name = working.Name;
        // Show the Type dropdown empty for brand-new destinations; for
        // existing ones, pre-select whatever the user chose last time.
        _kind = isNew && working.Kind == 0 ? null : working.Kind;
        _pathOrSubpath = working.PathOrSubpath;
        _expectedDriveLetter = working.ExpectedDriveLetter;
        _expectedVolumeLabel = working.ExpectedVolumeLabel;
        _s3Endpoint = working.S3Endpoint;
        _s3Region = working.S3Region;
        _s3Bucket = working.S3Bucket;
        _networkUsername = working.NetworkUsername;
        _encrypted = working.Encrypted;
        _hasOAuthTokenStored = !string.IsNullOrEmpty(working.OAuthTokenRef);
        // For an already-validated existing token, treat the editor's
        // initial state as healthy — the user hasn't typed anything new
        // yet, so flagging IsAuthError red on open looks like an
        // accusation. Only flip to error if the user actually pastes a
        // new token AND validation fails. AuthOk is the "we currently
        // have credentials we trust" flag the IsAuthError computation
        // negates against.
        _authOk = working.LastTokenValidatedUtc is not null;
        _authStatus = working.LastTokenValidatedUtc is DateTime d
            ? $"Saved · last validated {d.ToLocalTime():yyyy-MM-dd}"
            : "";
    }

    partial void OnKindChanged(DestinationKind? value)
    {
        // Persist onto Working so NeedsOAuth / BuildStorageUrl etc. pick
        // it up. When the user clears the selection we leave Working.Kind
        // alone (it can't take null) — the Save path refuses if Kind is
        // null at save time anyway.
        if (value is DestinationKind k) Working.Kind = k;

        OnPropertyChanged(nameof(ShowLocalPath));
        OnPropertyChanged(nameof(ShowNetworkPath));
        OnPropertyChanged(nameof(ShowNetworkCreds));
        OnPropertyChanged(nameof(ShowVolumeMatch));
        OnPropertyChanged(nameof(ShowCloudSubpath));
        OnPropertyChanged(nameof(ShowOAuth));
        OnPropertyChanged(nameof(ShowS3));
        OnPropertyChanged(nameof(ShowCloudSubfolderField));
        OnPropertyChanged(nameof(ShowEncryptionRecommendedBadge));
        OnPropertyChanged(nameof(OAuthScopeExplanation));
        OnPropertyChanged(nameof(OAuthConnectButtonText));
        RefreshSuggestedName();
        if (AttemptedSave) RevalidateField(nameof(Kind));
    }

    partial void OnNameChanged(string value) { if (AttemptedSave) RevalidateField(nameof(Name)); }
    partial void OnPathOrSubpathChanged(string value)
    {
        RefreshSuggestedName();
        if (AttemptedSave) RevalidateField(nameof(PathOrSubpath));
    }
    partial void OnS3BucketChanged(string? value)
    {
        RefreshSuggestedName();
        if (AttemptedSave) RevalidateField(nameof(S3Bucket));
    }

    /// <summary>
    /// Computed friendly default name shown as Watermark on the Name
    /// input. As the user picks a Type and fills in identifying fields,
    /// this updates live so they can see what the auto-name will look
    /// like if they leave Name blank.
    /// </summary>
    public string SuggestedName
    {
        get
        {
            if (Kind is null) return "";
            var subfolder = ShowCloudSubpath ? PathOrSubpath : null;
            var identifier = Kind switch
            {
                DestinationKind.LocalFolder or DestinationKind.ExternalDrive
                    => PathOrSubpath,
                DestinationKind.NetworkShare
                    => PathOrSubpath,
                DestinationKind.S3Compatible
                    => S3Bucket,
                _ => null, // cloud: identifier is the account, which we don't know yet
            };
            return NameGenerator.ForDestination(
                Kind.Value, identifier, subfolder,
                _ => false /* don't collision-check the suggestion preview */);
        }
    }

    private void RefreshSuggestedName() => OnPropertyChanged(nameof(SuggestedName));

    /// <summary>
    /// Coerce a free-form string into a Duplicacy-safe cloud subpath
    /// segment. Cloud subpaths form part of a path on the remote side
    /// (Dropbox folder name, OneDrive path component, etc.); strict
    /// safe-character set avoids URL-encoding surprises and keeps the
    /// remote layout readable. Idempotent for already-safe input.
    /// </summary>
    private static string SanitizeSubpath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "duplimate-storage";
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.')
                sb.Append(ch);
            else if (char.IsWhiteSpace(ch))
                sb.Append('-');
            // Other characters (slashes, colons, brackets) silently
            // dropped — they would either confuse the remote API or
            // create unintended subdirectories.
        }
        var result = sb.ToString().Trim('-', '_', '.');
        return result.Length == 0 ? "duplimate-storage" : result;
    }

    /// <summary>
    /// URL on duplicacy.com that starts the OAuth helper flow. Kept isolated
    /// here so scope enforcement has a single source of truth: we can only
    /// guarantee the *requested* scope by controlling which URL the user
    /// is sent to. Tokens issued by a given URL carry the scope
    /// duplicacy.com registered its Dropbox app with — we cannot introspect
    /// the scope client-side (see DropboxAuthProbe for the why).
    ///
    /// Dropbox specifics:
    ///   • duplicacy.com exposes exactly one Dropbox helper — /dropbox_start.
    ///     We verified empirically that variants (/dropbox_full_start,
    ///     /dropbox_full, etc.) all 302 to /home.html; no full-access
    ///     helper is hosted.
    ///   • The OAuth request it builds omits the `scope=` parameter,
    ///     so the scope is whatever Dropbox has registered under
    ///     client_id=vp00fqqtagpzk0l, which is App folder. Files created
    ///     via this flow land under /Apps/Duplicacy/ in the user's
    ///     Dropbox.
    ///   • Consequence: <see cref="DestinationKind.DropboxFullAccess"/>
    ///     cannot be supported through duplicacy.com's helper and returns
    ///     null here. Full-access support would require Duplimate to
    ///     register its own Dropbox app with "Full Dropbox" access type.
    /// </summary>
    internal static string? OAuthHelperUrlFor(DestinationKind kind) => kind switch
    {
        DestinationKind.DropboxAppScoped  => "https://duplicacy.com/dropbox_start",
        DestinationKind.DropboxFullAccess => null, // duplicacy.com doesn't host a full-access helper
        DestinationKind.OneDrivePersonal  => "https://duplicacy.com/one_start",
        DestinationKind.OneDriveBusiness  => "https://duplicacy.com/odb_start",
        DestinationKind.GoogleDrive       => "https://duplicacy.com/gcd_start",
        _ => null,
    };

    /// <summary>
    /// Folder picker for local / external destinations. Network share
    /// (UNC) paths don't go through this — Windows' folder picker doesn't
    /// offer a good way to type one in anyway, and the UI there has its
    /// own text input for `\\server\share\path`.
    /// </summary>
    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        var owner = MainWindowOrNull();
        if (owner is null) return;
        var picks = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Kind == DestinationKind.ExternalDrive
                ? "Pick a folder on the external drive"
                : "Pick the destination folder",
            AllowMultiple = false,
            SuggestedStartLocation = await TryStartAtAsync(owner, PathOrSubpath),
        });
        if (picks?.Count > 0)
        {
            var p = picks[0].TryGetLocalPath();
            if (!string.IsNullOrEmpty(p)) PathOrSubpath = p;
        }
    }

    private static async Task<IStorageFolder?> TryStartAtAsync(Avalonia.Controls.Window owner, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return await owner.StorageProvider.TryGetFolderFromPathAsync(path); }
        catch { return null; }
    }

    private static Avalonia.Controls.Window? MainWindowOrNull() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime l
            ? l.MainWindow
            : null;

    [RelayCommand]
    private void OpenOAuthHelper()
    {
        if (Kind is null)
        {
            AuthStatus = "Pick a destination type first.";
            AuthOk = false;
            return;
        }
        var url = OAuthHelperUrlFor(Kind.Value);
        if (url is null)
        {
            // Only realistic path here today is DropboxFullAccess, which
            // duplicacy.com doesn't host a helper for. Say so clearly
            // rather than silently opening nothing.
            AuthStatus = Kind == DestinationKind.DropboxFullAccess
                ? "Full-Dropbox access isn't supported yet — only the App folder flow works today."
                : "No helper is available for this destination kind.";
            AuthOk = false;
            return;
        }
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        AuthStatus = "Browser opened. Sign in, then paste the token — it'll verify automatically.";
        AuthOk = false;
    }

    /// <summary>
    /// Called automatically whenever <see cref="OAuthTokenInput"/> changes
    /// (from the XAML binding's PropertyChanged). Debounces so a user pasting
    /// a multi-line token in chunks doesn't fire N probes, and cancels any
    /// in-flight probe so only the latest token is checked.
    /// </summary>
    partial void OnOAuthTokenInputChanged(string value)
    {
        // Cancel-AND-dispose the previous CTS before reassigning. Each
        // CancellationTokenSource owns a finalizable WaitHandle; without
        // disposing, every keystroke leaked one OS handle, accumulating
        // hundreds over a long editor session.
        var prev = _validationCts;
        _validationCts = null;
        if (prev is not null)
        {
            try { prev.Cancel(); } catch { }
            try { prev.Dispose(); } catch { }
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            AuthStatus = "";
            AuthOk = false;
            AuthBusy = false;
            return;
        }

        var cts = new CancellationTokenSource();
        _validationCts = cts;
        _ = RunValidationAsync(value.Trim(), cts.Token);
    }

    private async Task RunValidationAsync(string token, CancellationToken ct)
    {
        try
        {
            // Small debounce so paste-in-chunks doesn't fire N probes.
            await Task.Delay(400, ct);

            AuthBusy = true;
            AuthOk = false;
            AuthStatus = "Checking with Dropbox…";

            var probe = ServiceLocator.DropboxProbe;
            var result = Kind switch
            {
                DestinationKind.DropboxAppScoped or DestinationKind.DropboxFullAccess
                    => await probe.HealthCheckAsync(token, ct),
                _ => ProbeResult.Ok("Token captured. It will be validated on first backup."),
            };

            if (ct.IsCancellationRequested) return;

            AuthStatus = result.Message;
            AuthOk = result.Healthy;
            HasOAuthTokenStored = result.Healthy;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer token; caller owns the status text.
        }
        catch (Exception ex)
        {
            AuthStatus = $"Couldn't verify: {ex.Message}";
            AuthOk = false;
        }
        finally
        {
            AuthBusy = false;
        }
    }

    [RelayCommand]
    private async Task Validate()
    {
        // Manual "Validate again" button. Runs the same probe synchronously.
        if (string.IsNullOrWhiteSpace(OAuthTokenInput))
        {
            AuthStatus = "Paste a token from the browser flow first.";
            AuthOk = false;
            return;
        }
        // Same cancel-and-dispose pattern as OnOAuthTokenInputChanged so
        // a "Validate again" click doesn't leak the prior CTS.
        var prev = _validationCts;
        _validationCts = null;
        if (prev is not null)
        {
            try { prev.Cancel(); } catch { }
            try { prev.Dispose(); } catch { }
        }
        var cts = new CancellationTokenSource();
        _validationCts = cts;
        await RunValidationAsync(OAuthTokenInput.Trim(), cts.Token);
    }

    /// <summary>
    /// Pre-flight check used by <see cref="Views.DestinationEditorWindow.OnChangeStoragePassword"/>
    /// before opening the change-password dialog. Returns a non-null
    /// reason string when the change can't proceed (caller surfaces it
    /// to the user); null when it's safe to open the dialog.
    /// <para>
    /// We keep the precondition logic on the VM (which knows
    /// destination state) and the dialog-orchestration logic on the
    /// view (which knows its own window for parenting). The VM also
    /// applies the new password to the secrets store via
    /// <see cref="ApplyChangedStoragePassword"/> after the dialog
    /// returns success.
    /// </para>
    /// </summary>
    public string? CheckCanChangeStoragePassword()
    {
        if (IsNew || !Encrypted || string.IsNullOrEmpty(Working.StoragePasswordRef))
            return "There's no existing password on this destination to change yet — encryption needs to have been set up first.";

        var runningUsers = ServiceLocator.Config.Current.Backups
            .Where(b => b.Targets.Any(t => t.DestinationId == Working.Id))
            .Where(b => ServiceLocator.Orchestrator.IsRunning(b.Id))
            .Select(b => b.Name)
            .ToList();
        if (runningUsers.Count > 0)
            return $"«{Working.Name}» is currently in use by " +
                   $"{(runningUsers.Count == 1 ? "the running backup" : "running backups")}: " +
                   $"{string.Join(", ", runningUsers)}. Stop the run from the Backups page and try again.";

        if (!ServiceLocator.Secrets.TryGet(Working.StoragePasswordRef!, out var oldPwd) || string.IsNullOrEmpty(oldPwd))
            return "Duplimate couldn't read the saved password for this destination from this machine's secret store " +
                   "(DPAPI failure, or the config was copied from another user/machine). Without it we can't decrypt " +
                   "the storage's master key to re-encrypt it. Remove this destination and create a fresh one to recover.";

        return null;
    }

    /// <summary>Returns the OLD password from the secrets store so the
    /// view can pass it into the change-password dialog. Caller must
    /// have already passed <see cref="CheckCanChangeStoragePassword"/>.</summary>
    public string GetCurrentStoragePassword()
    {
        if (string.IsNullOrEmpty(Working.StoragePasswordRef)) return "";
        return ServiceLocator.Secrets.TryGet(Working.StoragePasswordRef, out var v) ? v : "";
    }

    /// <summary>
    /// Persist a successfully-rotated password into the secrets store.
    /// The storage's config has already been re-encrypted at this point
    /// (caller invokes only after `duplicacy password` returned exit
    /// code 0); this just keeps OUR keyring in sync so future runs use
    /// the new value.
    /// </summary>
    public void ApplyChangedStoragePassword(string newPassword)
    {
        if (string.IsNullOrEmpty(Working.StoragePasswordRef)) return;
        if (string.IsNullOrEmpty(newPassword)) return;
        ServiceLocator.Secrets.Set(Working.StoragePasswordRef, newPassword);
        // Clear any prior summary error — a successful rotation is the
        // user's "I fixed it" signal for any related complaint banner.
        SummaryError = null;
        OnPropertyChanged(nameof(HasAnyError));
    }

    /// <summary>
    /// Runs form validation (distinct from the OAuth token probe), populates
    /// per-field error properties, and returns true if the form is OK to
    /// save. The view re-binds its error styles every time this runs so
    /// Save-attempt → red-state feedback is atomic.
    /// </summary>
    public bool ValidateForm()
    {
        AttemptedSave = true;
        KindError = null;
        NameError = null;
        PathError = null;
        BucketError = null;
        SummaryError = null;

        var errors = new List<string>();

        if (Kind is null)
        {
            KindError = "Pick a destination type.";
            errors.Add("Destination type is required.");
        }

        // Name is optional — blank will fall back to SuggestedName. But if
        // the user typed a name, make sure it isn't already taken.
        if (!string.IsNullOrWhiteSpace(Name))
        {
            var taken = ServiceLocator.Config.Current.Destinations
                .Any(d => d.Id != Working.Id
                          && string.Equals(d.Name?.Trim(), Name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (taken)
            {
                NameError = "Another destination already uses this name.";
                errors.Add("Name is not unique.");
            }
        }

        // Type-specific required-field checks.
        if (Kind is DestinationKind.LocalFolder or DestinationKind.ExternalDrive)
        {
            if (string.IsNullOrWhiteSpace(PathOrSubpath))
            {
                PathError = "Pick a folder on this PC.";
                errors.Add("Folder path is required.");
            }
        }
        else if (Kind == DestinationKind.NetworkShare)
        {
            if (string.IsNullOrWhiteSpace(PathOrSubpath))
            {
                PathError = "Enter a UNC path like \\\\server\\share\\folder.";
                errors.Add("UNC path is required.");
            }
        }
        else if (Kind == DestinationKind.S3Compatible)
        {
            if (string.IsNullOrWhiteSpace(S3Bucket))
            {
                BucketError = "Enter a bucket name.";
                errors.Add("Bucket is required.");
            }
        }

        // Cloud subpath required for cloud destinations. Empty subpath
        // produces a `dropbox://` storage URL which Duplicacy rejects
        // with "Unrecognizable storage URL" — the previous failure
        // mode in the user's run log.
        //
        // Auto-default rule: ONLY when the user typed a meaningful
        // destination name that's different from the kind label.
        // Earlier we'd fall back to the kind name itself
        // ("Dropbox" → subpath = "Dropbox"), which the user later
        // saw as a baked-in value when re-editing and couldn't tell
        // whether it was a placeholder or a real save. Now the
        // fallback is `duplimate-{8-char Id}` — stable, unique
        // per destination, obviously generated.
        if (Working.NeedsOAuth || Working.Kind == DestinationKind.S3Compatible)
        {
            if (string.IsNullOrWhiteSpace(PathOrSubpath))
            {
                var kindLabel = Working.Kind.ToString();
                var seed = !string.IsNullOrWhiteSpace(Name)
                           && !string.Equals(Name.Trim(), kindLabel, StringComparison.OrdinalIgnoreCase)
                    ? Name
                    : $"duplimate-{Working.Id[..8]}";
                PathOrSubpath = SanitizeSubpath(seed);
            }
        }

        // Storage password length guard. Duplicacy's `init` rejects
        // passwords shorter than 8 characters with the literal error
        // "The password must be at least 8 characters" — surface that
        // requirement at form-submit time so the user fixes it before
        // we burn an init attempt against the cloud / disk.
        //
        // We enforce length whenever the user typed something —
        // independent of the Encrypted toggle. Earlier the gate also
        // required Encrypted=true, which let a user type a short
        // string into the password field with Encrypted=false and
        // save: the password was kept verbatim in the secrets store
        // for next-time, so flipping Encrypted on later inherited a
        // too-short value that init then rejected. Length gate fires
        // unconditionally now; an empty field still means
        // "auto-generate" only when Encrypted=true (the auto-generate
        // path in Save handles that case below).
        if (!string.IsNullOrEmpty(StoragePasswordInput)
            && StoragePasswordInput.Length < 8)
        {
            errors.Add(
                "Storage password must be at least 8 characters. Duplicacy refuses shorter passwords at storage init time. " +
                "Pick a longer one, or clear the field — we'll auto-generate a strong password for you.");
            SummaryError = errors[^1];
            OnPropertyChanged(nameof(HasAnyError));
            return false;
        }

        // Storage password rotation guard (encrypted destinations).
        // Duplicacy's encryption password is set at `init` time and
        // cannot be changed in place — chunks encrypted with the old
        // password become unreadable if the stored password is
        // overwritten. So if the user typed a new password into an
        // already-encrypted destination, refuse the save unless what
        // they typed exactly matches what's already stored (treating
        // a redundant re-entry as a no-op).
        if (Encrypted
            && !string.IsNullOrEmpty(StoragePasswordInput)
            && !string.IsNullOrEmpty(Working.StoragePasswordRef))
        {
            // `stored` is null/default when TryGet returns false (DPAPI
            // failed, secret missing, etc). Use the materialised `current`
            // local for the comparison — it normalises that null-or-fail
            // case to "" so the equality check below is well-defined and
            // a pre-existing-but-unreadable secret doesn't accidentally
            // match an empty input.
            var current = ServiceLocator.Secrets.TryGet(Working.StoragePasswordRef, out var stored)
                ? stored
                : "";
            if (!string.Equals(current, StoragePasswordInput, StringComparison.Ordinal))
            {
                errors.Add(
                    "The storage password can't be changed in place — Duplicacy ties encryption to the password used when the storage was first set up. " +
                    "Existing data would become unreadable. Either delete this destination and recreate it with a new password (you'll lose the historical revisions), " +
                    "or clear this field to keep using the saved password.");
                SummaryError = errors[^1];
                OnPropertyChanged(nameof(HasAnyError));
                return false;
            }
        }

        // Pre-flight: local destination pointing at a folder that already
        // contains a Duplicacy storage (config / chunks/ / snapshots/).
        // Without this guard the user discovers the problem only when the
        // first backup runs and Duplicacy refuses init with "The storage
        // is likely to have been initialized with a password before"
        // (user-reported case 2026-04-29). The pre-flight gives them a
        // concrete next step BEFORE the destination is saved:
        //   • Connect to existing storage → turn on Encryption + supply
        //     the existing password right here in the form.
        //   • Start fresh → pick a different folder (we don't auto-erase
        //     because deleting backup history is the user's call).
        // Cloud / S3 destinations skip this check; their equivalent path
        // is the StoragePreviouslyInitializedException at run time.
        if (Kind is DestinationKind.LocalFolder or DestinationKind.ExternalDrive or DestinationKind.NetworkShare
            && !string.IsNullOrWhiteSpace(PathOrSubpath))
        {
            var probeDest = new Destination
            {
                Kind = Kind.Value,
                PathOrSubpath = PathOrSubpath.Trim(),
            };
            if (DestinationProbe.LocalStorageAlreadyInitialised(probeDest))
            {
                // Inspect the existing config file to see whether the
                // storage was created with encryption. An UNENCRYPTED
                // existing storage can be safely adopted with no
                // password — Duplicacy's init will succeed without
                // any auth — so we should NOT block save in that case.
                // Only encrypted storages need the user to either
                // supply the existing password or explicitly start
                // fresh. Returns null when we can't tell (file
                // unreadable etc.) — fall back to the safe "treat as
                // encrypted" branch so we never silently adopt an
                // encrypted storage as if it were plaintext.
                var existingEncrypted = DestinationProbe.LocalStorageIsEncrypted(probeDest) ?? true;

                // If the user has both flipped Encryption on AND supplied
                // (or already stored) a password, treat that as informed
                // consent: they're connecting to an existing encrypted
                // storage with its known password. Run-time init will
                // either succeed or surface the typed
                // StoragePreviouslyInitializedException — at which point
                // they get the same UX prompt from the run failure path.
                var userOptedToConnect =
                    Encrypted &&
                    (!string.IsNullOrEmpty(StoragePasswordInput)
                     || !string.IsNullOrEmpty(Working.StoragePasswordRef));

                // Adopting an unencrypted existing storage with the
                // Encryption toggle OFF is a clean "use as-is" path —
                // the user reasonably didn't enter a password because
                // none is needed. No error needed.
                var adoptingUnencryptedAsIs = !existingEncrypted && !Encrypted;

                if (!userOptedToConnect && !adoptingUnencryptedAsIs)
                {
                    PathError = "This folder already contains a Duplicacy storage.";
                    // Structured multi-line message so the validation
                    // banner renders as a heading + two bulleted options
                    // rather than a 380-char wall of prose. The
                    // TextBlock above the banner has TextWrapping="Wrap"
                    // and respects literal '\n', so explicit newlines
                    // give us reliable visual hierarchy without
                    // introducing a richer error-detail VM type.
                    //
                    // The wording branches on the existing encryption
                    // state: an encrypted storage needs the existing
                    // password, while an unencrypted storage only needs
                    // the Encryption toggle to match (off) — the user
                    // shouldn't be told to "paste the existing password"
                    // for a storage that has none.
                    if (existingEncrypted)
                    {
                        errors.Add(
                            "This folder already contains an ENCRYPTED Duplicacy storage " +
                            "(we found a Duplicacy config / chunks / snapshots layout there).\n\n" +
                            "Two ways forward:\n" +
                            "  • USE that storage — turn on Encryption above and paste the existing storage password; the next backup will read the existing data.\n" +
                            "  • START FRESH — pick a different folder, or manually delete the existing 'config' file plus the 'chunks' and 'snapshots' folders inside this path (this destroys any backup history kept there).");
                    }
                    else
                    {
                        // Reachable when the user toggled Encryption ON
                        // for a storage that's actually unencrypted —
                        // they'd be asking us to re-init with a fresh
                        // password, which would clobber the existing
                        // chunks. Tell them to mirror the existing
                        // state instead.
                        errors.Add(
                            "This folder already contains an UNENCRYPTED Duplicacy storage " +
                            "(we found a Duplicacy config / chunks / snapshots layout there).\n\n" +
                            "Two ways forward:\n" +
                            "  • USE that storage — turn Encryption OFF (the existing storage has no password); the next backup will read the existing data.\n" +
                            "  • START FRESH — pick a different folder, or manually delete the existing 'config' file plus the 'chunks' and 'snapshots' folders inside this path (this destroys any backup history kept there).");
                    }
                    SummaryError = errors[^1];
                    OnPropertyChanged(nameof(HasAnyError));
                    return false;
                }
            }
        }

        if (Working.NeedsOAuth)
        {
            // Required-field check: a cloud destination cannot exist
            // without a successfully-validated token. Editing an
            // already-authenticated destination is fine even with an
            // empty input (the previously-validated token is kept) —
            // detected via LastTokenValidatedUtc on the working copy.
            var hasPersistedToken = Working.LastTokenValidatedUtc is not null;
            if (string.IsNullOrWhiteSpace(OAuthTokenInput) && !hasPersistedToken)
            {
                AuthStatus = "Click Connect, sign in, and paste the token before saving.";
                errors.Add("Cloud destinations need a verified token.");
            }
            else if (!string.IsNullOrWhiteSpace(OAuthTokenInput) && !AuthOk)
            {
                if (AuthBusy)
                {
                    // Distinct wording so the user reads this as
                    // "wait a second" rather than "your token is
                    // wrong". The validation banner picks up this
                    // exact string, so it must clearly signal the
                    // transient nature of the state.
                    AuthStatus = "Still checking your token with the provider — please wait a moment and click Save again.";
                    errors.Add("Token check still in progress — wait a moment and try Save again. (This isn't a real error; the token verification just hasn't finished yet.)");
                }
                else
                {
                    AuthStatus = "Token couldn't be verified. Fix the issue above, or clear the field to keep the previously-validated token.";
                    errors.Add("OAuth token is not verified.");
                }
            }
        }

        if (errors.Count > 0)
        {
            SummaryError = errors.Count == 1
                ? errors[0]
                : $"{errors.Count} things need attention:  " + string.Join("  ·  ", errors);
            OnPropertyChanged(nameof(HasAnyError));
            return false;
        }

        OnPropertyChanged(nameof(HasAnyError));
        return true;
    }

    private void RevalidateField(string fieldName)
    {
        // Cheap per-field clear: user corrected this field, drop the red
        // state immediately so the form doesn't feel passive-aggressive.
        switch (fieldName)
        {
            case nameof(Kind):           if (Kind is not null) KindError = null; break;
            case nameof(Name):           if (!string.IsNullOrWhiteSpace(Name)) NameError = null; break;
            case nameof(PathOrSubpath):  if (!string.IsNullOrWhiteSpace(PathOrSubpath)) PathError = null; break;
            case nameof(S3Bucket):       if (!string.IsNullOrWhiteSpace(S3Bucket)) BucketError = null; break;
        }
    }

    public Destination? Save()
    {
        if (!ValidateForm()) return null;

        // Pull input buffers into secrets, write refs into Working.
        var secrets = ServiceLocator.Secrets;

        // Account-or-path identifier passed to NameGenerator.ForDestination.
        // Must mirror SuggestedName's per-kind switch — earlier this branch
        // always fell through to PathOrSubpath for cloud kinds, which made
        // both `accountOrPath` and `subfolder` resolve to the same auto-
        // filled cloud subpath (e.g. "duplimate-f251402f"), producing
        // names like "Dropbox - duplimate-f251402f - duplimate-f251402f"
        // (user-reported 2026-04-29). Cloud destinations don't have a stable
        // account identifier we can read from Duplicacy, so we leave the
        // identifier null and rely on the subfolder slot for disambiguation.
        var generatorIdentifier = Kind switch
        {
            DestinationKind.LocalFolder or DestinationKind.ExternalDrive
                => PathOrSubpath,
            DestinationKind.NetworkShare
                => PathOrSubpath,
            DestinationKind.S3Compatible
                => S3Bucket,
            _ => null, // cloud: account identifier isn't available; subfolder carries the unique bit
        };
        Working.Name = string.IsNullOrWhiteSpace(Name)
            ? NameGenerator.ForDestination(
                Kind!.Value,
                generatorIdentifier,
                ShowCloudSubpath ? PathOrSubpath : null,
                candidate => ServiceLocator.Config.Current.Destinations
                    .Any(d => d.Id != Working.Id
                              && string.Equals(d.Name?.Trim(), candidate, StringComparison.OrdinalIgnoreCase)))
            : Name.Trim();
        Working.Kind = Kind!.Value;
        Working.PathOrSubpath = PathOrSubpath?.Trim() ?? "";
        Working.ExpectedDriveLetter = string.IsNullOrWhiteSpace(ExpectedDriveLetter) ? null : ExpectedDriveLetter.Trim();
        Working.ExpectedVolumeLabel = string.IsNullOrWhiteSpace(ExpectedVolumeLabel) ? null : ExpectedVolumeLabel.Trim();
        Working.Encrypted = Encrypted;

        // Storage password handling (encrypted storages only):
        //
        // Duplicacy's storage encryption is tied to the password used
        // at `init` time. Once a chunk is encrypted with password X
        // it CAN ONLY be decrypted with X — there is no Duplicacy CLI
        // affordance to "rotate" or "change" the password in place;
        // the only way is to wipe the storage and re-init with a new
        // one. So we treat the persisted password as immutable:
        //
        //   • New destination, blank input  → generate + show once.
        //   • New destination, typed input  → save as the new password.
        //   • Existing destination, blank input → keep the stored
        //                                        password verbatim
        //                                        (the "leave to keep
        //                                        existing" path).
        //   • Existing destination, typed input → IGNORE silently if
        //     it matches the stored value; if different, refuse to
        //     overwrite (would orphan existing chunks).
        //
        // The "refuse to overwrite" path lives in ValidateForm so the
        // user gets a clear error before the save commits.
        if (!string.IsNullOrEmpty(StoragePasswordInput))
        {
            // For new destinations, accept whatever the user typed.
            // For existing destinations, ValidateForm has already
            // refused the save if the input differs from stored —
            // so by the time we're here, either the input matches
            // (ignore) or there's no existing password (treat as new).
            if (string.IsNullOrEmpty(Working.StoragePasswordRef))
            {
                Working.StoragePasswordRef = $"dest:{Working.Id}:storagepw";
                secrets.Set(Working.StoragePasswordRef, StoragePasswordInput);
            }
            // else: input is a no-op confirmation of the existing
            // password (validated equal); leave the stored value alone.
        }
        else if (Encrypted && string.IsNullOrEmpty(Working.StoragePasswordRef))
        {
            Working.StoragePasswordRef = $"dest:{Working.Id}:storagepw";
            var generated = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            secrets.Set(Working.StoragePasswordRef, generated);
            GeneratedStoragePassword = generated;
        }

        // OAuth token
        if (!string.IsNullOrEmpty(OAuthTokenInput))
        {
            Working.OAuthTokenRef ??= $"dest:{Working.Id}:oauth";
            secrets.Set(Working.OAuthTokenRef, OAuthTokenInput.Trim());
            Working.LastTokenValidatedUtc = DateTime.UtcNow;
        }

        // Network credentials
        if (Kind == DestinationKind.NetworkShare)
        {
            Working.NetworkUsername = NetworkUsername;
            if (!string.IsNullOrEmpty(NetworkPasswordInput))
            {
                Working.NetworkPasswordRef ??= $"dest:{Working.Id}:netpw";
                secrets.Set(Working.NetworkPasswordRef, NetworkPasswordInput);
            }
        }

        // S3
        if (Kind == DestinationKind.S3Compatible)
        {
            Working.S3Endpoint = S3Endpoint;
            Working.S3Region = S3Region;
            Working.S3Bucket = S3Bucket;
            if (!string.IsNullOrEmpty(S3AccessKeyInput))
            {
                Working.S3AccessKeyRef ??= $"dest:{Working.Id}:s3ak";
                secrets.Set(Working.S3AccessKeyRef, S3AccessKeyInput);
            }
            if (!string.IsNullOrEmpty(S3SecretKeyInput))
            {
                Working.S3SecretKeyRef ??= $"dest:{Working.Id}:s3sk";
                secrets.Set(Working.S3SecretKeyRef, S3SecretKeyInput);
            }
        }

        CompleteResult = Working.Id;
        return Working;
    }
}
