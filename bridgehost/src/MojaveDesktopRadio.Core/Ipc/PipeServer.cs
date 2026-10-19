using System.IO.Pipes;
using System.Text;

namespace MojaveDesktopRadio.Ipc;

/// <summary>
/// Local named-pipe server (\\.\pipe\&lt;name&gt;). Only the current user can connect
/// (<see cref="PipeOptions.CurrentUserOnly"/>; .NET also sets PIPE_REJECT_REMOTE_CLIENTS),
/// so nothing is exposed to the network. Up to 4 simultaneous clients (game + diagnostics).
/// </summary>
public sealed class PipeServer : IAsyncDisposable
{
    private const int MaxInstances = 4;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<PipeSession> _sessions = [];
    private readonly object _gate = new();
    private Task? _acceptLoop;
    private int _nextSessionId;

    public PipeServer(string pipeName) => _pipeName = pipeName;

    public event Action<PipeSession>? Connected;
    public event Action<PipeSession>? Disconnected;
    public event Action<PipeSession, string>? LineReceived;

    public IReadOnlyList<PipeSession> Sessions
    {
        get { lock (_gate) return _sessions.ToList(); }
    }

    public void Start() => _acceptLoop = Task.Run(AcceptLoop);

    private async Task AcceptLoop()
    {
        Log.Info($"IPC listening on \\\\.\\pipe\\{_pipeName}");
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, MaxInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 64 * 1024, 64 * 1024);
                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);

                var session = new PipeSession(Interlocked.Increment(ref _nextSessionId), server);
                server = null;
                lock (_gate) _sessions.Add(session);
                _ = Task.Run(() => RunSession(session));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                // all instances busy — wait for one to free up
                Log.Debug($"pipe accept: {ex.Message}");
                await Task.Delay(500).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("pipe accept failed", ex);
                await Task.Delay(1000).ConfigureAwait(false);
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    private async Task RunSession(PipeSession session)
    {
        Connected?.Invoke(session);
        var buffer = new byte[4096];
        var line = new MemoryStream();
        try
        {
            while (!_cts.IsCancellationRequested && session.Stream.IsConnected)
            {
                var n = await session.Stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (n <= 0) break;
                session.Touch();

                var start = 0;
                for (var i = 0; i < n; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;
                    line.Write(buffer, start, i - start);
                    start = i + 1;
                    Dispatch(session, line);
                }

                line.Write(buffer, start, n - start);
                if (line.Length > Protocol.MaxLineBytes)
                {
                    await session.SendAsync(Protocol.Error(null, "line_too_long", $"lines are limited to {Protocol.MaxLineBytes} bytes")).ConfigureAwait(false);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Debug($"session {session.Id} read ended: {ex.Message}");
        }
        finally
        {
            lock (_gate) _sessions.Remove(session);
            session.Dispose();
            Disconnected?.Invoke(session);
        }
    }

    private void Dispatch(PipeSession session, MemoryStream line)
    {
        var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).Trim('\r', ' ', '\t');
        line.SetLength(0);
        if (text.Length == 0) return;
        try
        {
            LineReceived?.Invoke(session, text);
        }
        catch (Exception ex)
        {
            Log.Error("message handler failed", ex);
        }
    }

    public void Broadcast(string json)
    {
        foreach (var s in Sessions)
            _ = s.SendAsync(json);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var s in Sessions) s.Dispose();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
        }

        _cts.Dispose();
    }
}

public sealed class PipeSession : IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _lastActivityTicks = Environment.TickCount64;
    private int _disposed;

    internal PipeSession(int id, NamedPipeServerStream stream)
    {
        Id = id;
        Stream = stream;
    }

    public int Id { get; }
    internal NamedPipeServerStream Stream { get; }

    /// <summary>Set after a valid "hello".</summary>
    public string ClientName { get; set; } = "?";
    public string ClientVersion { get; set; } = "?";
    public int? GamePid { get; set; }
    public bool IsGame { get; set; }

    public long MillisecondsSinceActivity => Environment.TickCount64 - Interlocked.Read(ref _lastActivityTicks);

    internal void Touch() => Interlocked.Exchange(ref _lastActivityTicks, Environment.TickCount64);

    public async Task SendAsync(string json)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        if (!await _writeLock.WaitAsync(2000).ConfigureAwait(false)) return;
        try
        {
            using var cts = new CancellationTokenSource(2000);
            await Stream.WriteAsync(bytes, cts.Token).ConfigureAwait(false);
            await Stream.FlushAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug($"session {Id} write failed: {ex.Message}");
            Dispose();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { Stream.Dispose(); } catch { }
    }
}
