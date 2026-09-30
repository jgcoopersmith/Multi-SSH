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
        // Disposed while resolving (tab closed): don't open a connection nobody will close.
        ObjectDisposedException.ThrowIf(_disposed, this);

        var client = new SshClient(info);
        _client = client;
        client.KeepAliveInterval = _cfg.KeepAliveSeconds > 0
            ? TimeSpan.FromSeconds(_cfg.KeepAliveSeconds)
            : Timeout.InfiniteTimeSpan;
        client.ErrorOccurred += (_, e) =>
        {
            _connectionError = true;
            StatusChanged?.Invoke("Error: " + e.Exception.Message);
        };

        await Task.Run(() => client.Connect());
        if (_disposed)
        {
            try { client.Dispose(); } catch { /* already torn down */ }
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

        _shell.ErrorOccurred += (_, e) =>
        {
            _connectionError = true;
            StatusChanged?.Invoke("Shell error: " + e.Exception.Message);
        };

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
                    Closed?.Invoke("Connection lost" + (failure != null ? ": " + failure : ""));
                }
            }
        }
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
        if (_disposed) return;
        _disposed = true;
        try
        {
            _shell?.Dispose();   // unblocks the read loop
            _client?.Disconnect();
            _client?.Dispose();
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
