using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Duplimate.Services.Platform;
using Serilog;

namespace Duplimate.Services;

/// <summary>
/// Single-instance coordinator for the GUI process. Cross-platform:
/// uses a per-user named <see cref="Mutex"/> for primary-detection
/// and a <see cref="NamedPipeServerStream"/> / <see cref="NamedPipeClientStream"/>
/// pair for arg-forwarding.
///
/// <para>
/// Why named pipes work on macOS / Linux: .NET's named-pipe types map
/// to Unix-domain sockets at <c>/tmp/CoreFxPipe_&lt;name&gt;</c>
/// transparently, with the same semantics as Windows AF_UNIX pipes.
/// We just have to drop the Windows-only <c>Local\</c> mutex prefix
/// (which corresponds to the Windows session-local namespace and
/// wouldn't be honored anyway off-Windows) — see <see cref="MutexName"/>.
/// </para>
///
/// <para>
/// Two responsibilities:
/// <list type="number">
///   <item>Hold a per-user named <see cref="Mutex"/> so a second launch
///         of the binary detects there's already an instance running.</item>
///   <item>Run a tiny named-pipe server in the primary instance so a
///         second-launch process can forward its CLI args (the path the
///         user right-clicked in Explorer, primarily) and exit.</item>
/// </list>
/// </para>
///
/// <para>
/// The mutex name embeds the assembly path hash so a developer running
/// two distinct builds (debug vs release in different folders) doesn't
/// see them collide.
/// </para>
/// </summary>
public sealed class SingleInstanceCoordinator : IDisposable
{
    private static ILogger _log => AppLogger.For<SingleInstanceCoordinator>();

    /// <summary>Fires on the pipe-server thread when a second-launch
    /// process forwards args. Subscribers MUST marshal back to the UI
    /// thread before touching Avalonia state.</summary>
    public event Action<string[]>? SecondInstanceForwarded;

    /// <summary>Latest forwarded args, or null if none received.
    /// Used by MainWindow to drain a forwarded-args message that
    /// arrived BEFORE the window finished loading and subscribed to
    /// <see cref="SecondInstanceForwarded"/> — the event fires once
    /// per arrival, so a fast second-launch could be silently dropped.
    /// MainWindow's Loaded handler calls <see cref="DrainPending"/>
    /// to pick up anything received during startup.</summary>
    private string[]? _pendingArgs;
    private readonly object _pendingGate = new();

    /// <summary>Returns and clears the most recent forwarded args (or
    /// null if no second-launch has arrived). Thread-safe.</summary>
    public string[]? DrainPending()
    {
        lock (_pendingGate)
        {
            var p = _pendingArgs;
            _pendingArgs = null;
            return p;
        }
    }

    private Mutex? _mutex;
    private CancellationTokenSource? _serverCts;
    private Task? _serverTask;
    private string MutexName { get; }
    private string PipeName  { get; }

    public SingleInstanceCoordinator()
    {
        var hash = HashAssemblyPath();
        // Windows uses the `Local\` namespace prefix to keep mutexes
        // per-session; Unix Mutex names are flat. The `Local\` prefix
        // would cause a name-too-long / invalid-name failure on Linux
        // and macOS, so we only include it on Windows.
        MutexName = PlatformInfo.IsWindows
            ? $@"Local\Duplimate-Single-{hash}"
            : $"Duplimate-Single-{hash}";
        // .NET maps named pipes to /tmp/CoreFxPipe_<PipeName> on Unix
        // automatically — no platform-specific path needed here.
        PipeName  = $"Duplimate-Pipe-{hash}";
    }

    /// <summary>
    /// Try to acquire the per-user single-instance mutex. Returns true
    /// if we ARE the primary (the caller should proceed with normal
    /// startup). Returns false if another instance already owns the
    /// mutex — the caller should call <see cref="ForwardArgsToPrimary"/>
    /// and exit.
    /// </summary>
    public bool TryAcquireMutex()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (!createdNew)
            {
                // Someone else owns it. Drop our handle without releasing
                // (we never owned it).
                _mutex.Dispose();
                _mutex = null;
                return false;
            }
            _log.Information("Single-instance mutex acquired ({Name})", MutexName);
            return true;
        }
        catch (Exception ex)
        {
            // If the mutex API itself fails, fall through to "we are
            // primary" rather than blocking the user from running their
            // app at all.
            _log.Warning(ex, "Single-instance mutex probe failed; proceeding as primary");
            return true;
        }
    }

    /// <summary>
    /// Starts the named-pipe listener that accepts arg-forward
    /// connections from later launches. Idempotent.
    /// </summary>
    public void StartServer()
    {
        if (_serverTask is not null) return;
        _serverCts = new CancellationTokenSource();
        _serverTask = Task.Run(() => ServerLoopAsync(_serverCts.Token));
    }

    /// <summary>
    /// Sends the args of the current process to the running primary
    /// instance via the named pipe. Returns true if the primary
    /// acknowledged. Caller should exit on success.
    /// </summary>
    public bool ForwardArgsToPrimary(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            var payload = SerializeArgs(args);
            var bytes = Encoding.UTF8.GetBytes(payload);
            client.Write(bytes, 0, bytes.Length);
            client.WaitForPipeDrain();
            _log.Information("Forwarded {Count} args to primary instance", args.Length);
            return true;
        }
        catch (Exception ex)
        {
            // Pipe didn't answer in time, or there's no primary listening
            // (race: primary just exited). Caller will fall back to
            // proceeding as primary itself.
            _log.Warning(ex, "Couldn't forward args to primary instance");
            return false;
        }
    }

    private async Task ServerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                using var ms = new MemoryStream();
                var buf = new byte[4096];
                int n;
                while ((n = await server.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                    ms.Write(buf, 0, n);

                var args = DeserializeArgs(Encoding.UTF8.GetString(ms.ToArray()));
                _log.Information("Primary received {Count} forwarded args from second instance", args.Length);
                // ONLY ONE consumer should run for any given forwarded
                // args. If a subscriber is already attached, fire the
                // event and DON'T buffer (subscriber handled it). If
                // not, buffer so DrainPending can pick it up from the
                // late-binding window.
                //
                // Read the delegate field UNDER the same lock that
                // DrainPending takes. Earlier this read happened
                // outside the lock, leaving a TOCTOU window where:
                //   1. Pipe reads handler (null, no subscriber yet).
                //   2. MainWindow ctor subscribes + Opened drains.
                //   3. Drain returns null (buffer not yet written).
                //   4. Pipe enters the else-branch and writes buffer
                //      AFTER the drain — args sit there forever
                //      because no further drain happens.
                // Reading-under-lock guarantees subscribe-then-drain
                // sees either the buffer (we wrote it under the lock
                // before the drain could run) or the event fire (we
                // saw the subscribe).
                Action<string[]>? handler;
                lock (_pendingGate)
                {
                    handler = SecondInstanceForwarded;
                    if (handler is null) _pendingArgs = args;
                }
                if (handler is not null)
                {
                    try { handler.Invoke(args); }
                    catch (Exception ex) { _log.Warning(ex, "SecondInstanceForwarded handler threw"); }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Warning(ex, "Single-instance pipe server tick failed; will reopen");
                try { await Task.Delay(500, ct); } catch { break; }
            }
        }
    }

    /// <summary>
    /// Newline-separated wire format. We use a sentinel between args
    /// instead of join-by-space to be safe against arg values that
    /// contain spaces (e.g. a folder path).
    /// </summary>
    private static string SerializeArgs(string[] args) => string.Join("\n", args);

    private static string[] DeserializeArgs(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        return text.Split('\n');
    }

    private static string HashAssemblyPath()
    {
        var path = Environment.ProcessPath
                ?? System.Reflection.Assembly.GetExecutingAssembly().Location
                ?? "Duplimate";
        var h = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(path));
        // 16 hex chars is plenty of entropy and keeps the mutex name short.
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
        return sb.ToString();
    }

    public void Dispose()
    {
        try { _serverCts?.Cancel(); } catch { }
        try { _serverTask?.Wait(500); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        try { _mutex?.Dispose(); } catch { }
    }
}
