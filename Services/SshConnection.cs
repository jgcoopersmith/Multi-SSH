using System.IO;
using System.Reflection;
using MultiSSH.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MultiSSH.Services;

/// <summary>
/// Wraps an SSH.NET <see cref="SshClient"/> and an interactive shell channel.
/// Raises <see cref="DataReceived"/> as bytes arrive; write user keystrokes
/// with <see cref="Send"/>.
/// </summary>
public class SshConnection : ITerminalBackend
{
    private readonly SessionConfig _cfg;
    private SshClient? _client;
    private ShellStream? _shell;
    private Thread? _readThread;
    private volatile bool _disposed;
    private volatile bool _connectionError;
    private int _lostRaised;
    private readonly object _life = new();
    private bool _connectInFlight;   // guarded by _life

    public event Action<byte[]>? DataReceived;
    public event Action<string>? StatusChanged;
    public event Action<string>? Closed;
    /// <summary>Raised once when the remote shell ends (EOF on the channel).</summary>
    public event Action? ShellExited;

    public bool IsConnected => _client?.IsConnected ?? false;

    public SshConnection(SessionConfig cfg) => _cfg = cfg;

    public async Task ConnectAsync(int cols, int rows)
    {
        StatusChanged?.Invoke($"Connecting to {_cfg.Host}:{_cfg.Port} …");

        // Build the connection info off the UI thread — resolving a forced IPv4/IPv6
        // host does a DNS lookup, which we don't want to block the UI with.
        var info = await Task.Run(() => RemoteAuth.BuildConnectionInfo(_cfg));

        var client = new SshClient(info);
        client.KeepAliveInterval = _cfg.KeepAliveSeconds > 0
            ? TimeSpan.FromSeconds(_cfg.KeepAliveSeconds)
            : Timeout.InfiniteTimeSpan;
        client.ErrorOccurred += (_, e) => ConnectionLost(e.Exception.Message);

        lock (_life)
        {
            // Disposed while resolving (tab closed): don't open a connection nobody will close.
            if (_disposed) { client.Dispose(); throw new ObjectDisposedException(nameof(SshConnection)); }
            _client = client;
            _connectInFlight = true;
        }

        // Dispose leaves the client alone while Connect runs — tearing an SSH.NET client down
        // mid-Connect leaks the session it is still building — so clean it up here instead.
        bool disposed;
        try { await Task.Run(() => client.Connect()); }
        catch
        {
            lock (_life) { _connectInFlight = false; disposed = _disposed; }
            if (disposed) DisposeQuietly(client);
            throw;
        }
        lock (_life) { _connectInFlight = false; disposed = _disposed; }
        if (disposed)
        {
            DisposeQuietly(client);
            throw new ObjectDisposedException(nameof(SshConnection));
        }
        RemoteAuth.ApplySocketOptions(client, _cfg);   // TCP_NODELAY / SO_KEEPALIVE

        StatusChanged?.Invoke($"Connected — {_cfg.Username}@{_cfg.Host}");

        var modes = new Dictionary<Renci.SshNet.Common.TerminalModes, uint>();
        _shell = client.CreateShellStream(
            _cfg.TerminalType,
            (uint)cols, (uint)rows,
            (uint)(cols * 8), (uint)(rows * 16),
            8192, modes);

        _shell.ErrorOccurred += (_, e) => ConnectionLost(e.Exception.Message);

        // Read on a background thread: a blocking Read returns 0 at EOF when the
        // remote shell exits, which lets us tear the window down automatically.
        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "ssh-read" };
        _readThread.Start();
    }

    private void ReadLoop()
    {
        var buf = new byte[8192];
        var shell = _shell;
        var client = _client;
        string? failure = null;
        try
        {
            while (!_disposed && shell != null)
            {
                int n = shell.Read(buf, 0, buf.Length);
                if (n <= 0) break; // EOF — the channel closed
                var slice = new byte[n];
                Array.Copy(buf, slice, n);
                DataReceived?.Invoke(slice);
            }
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }
        finally
        {
            // Nothing may escape this background thread — an unhandled exception here would
            // terminate the whole app (e.g. IsConnected throws if Dispose runs concurrently).
            try
            {
                if (!_disposed)
                {
                    // Only a clean EOF on a still-live session means the remote shell exited
                    // (which closes the pane). A dropped link keeps the pane so the user keeps
                    // the scrollback and can Reconnect.
                    if (failure == null && !_connectionError && (client?.IsConnected ?? false))
                    {
                        StatusChanged?.Invoke("Shell closed");
                        ShellExited?.Invoke();
                    }
                    else
                    {
                        ConnectionLost(failure);
                    }
                }
            }
            catch { /* disposed concurrently — the pane is closing anyway */ }
        }
    }

    /// <summary>Report a dead session once, and wake the read thread.</summary>
    private void ConnectionLost(string? reason)
    {
        _connectionError = true;
        if (_disposed || Interlocked.Exchange(ref _lostRaised, 1) != 0) return;
        Closed?.Invoke("Connection lost" + (string.IsNullOrEmpty(reason) ? "" : ": " + reason));
        // SSH.NET never completes a pending ShellStream.Read when the session dies, so the
        // read thread would hang forever; disposing the stream wakes it. Off this thread —
        // this is SSH.NET's message-listener thread, and a channel close waits on it.
        var shell = _shell;
        if (shell != null) Task.Run(() => { try { shell.Dispose(); } catch { } });
    }

    private static void DisposeQuietly(SshClient client)
    {
        try { client.Dispose(); } catch { /* best effort */ }
    }

    public void Send(byte[] data)
    {
        if (_shell == null) return;
        try
        {
            _shell.Write(data, 0, data.Length);
            _shell.Flush();
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("Send failed: " + ex.Message);
        }
    }

    public void Send(string text) => Send(System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Best-effort PTY resize. SSH.NET doesn't expose window-change on ShellStream
    /// publicly, so we reach the underlying channel via reflection.
    /// </summary>
    public void Resize(int cols, int rows)
    {
        if (_shell == null) return;
        try
        {
            var field = typeof(ShellStream).GetField("_channel",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var channel = field?.GetValue(_shell);
            var method = channel?.GetType().GetMethod("SendWindowChangeRequest",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            method?.Invoke(channel, new object[]
            {
                (uint)cols, (uint)rows, (uint)(cols * 8), (uint)(rows * 16)
            });
        }
        catch
        {
            // Non-fatal: the remote keeps the original size.
        }
    }

    public void Dispose()
    {
        bool clientConnecting;
        lock (_life)
        {
            if (_disposed) return;
            _disposed = true;
            clientConnecting = _connectInFlight;   // ConnectAsync disposes it when Connect returns
        }
        try
        {
            _shell?.Dispose();   // unblocks the read loop
            if (!clientConnecting)
            {
                _client?.Disconnect();
                _client?.Dispose();
            }
        }
        catch { /* ignore teardown errors */ }
        finally
        {
            // Null the handles so any late Send/Resize/IsConnected call no-ops
            // cleanly instead of touching a disposed stream. ReadLoop keeps its own
            // local reference to the shell, so this doesn't disturb it.
            _shell = null;
            _client = null;
        }
        Closed?.Invoke("Disconnected");
    }
}
