using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using MultiSSH.Models;

namespace MultiSSH.Services;

/// <summary>
/// Wire format between the app and its elevated helper over one duplex named
/// pipe: [type:1][length:int32 LE][payload].
/// </summary>
internal static class ElevatedFrames
{
    public const byte Data = 1;     // either direction: terminal bytes
    public const byte Resize = 2;   // app → helper: cols:int16, rows:int16
    public const byte Close = 3;    // app → helper: end the shell
    public const byte Exited = 4;   // helper → app: the shell ended

    public const string HelperArg = "--elevated-shell";

    public static void Write(Stream s, object gate, byte type, ReadOnlySpan<byte> payload)
    {
        var buf = new byte[5 + payload.Length];
        buf[0] = type;
        BitConverter.TryWriteBytes(buf.AsSpan(1, 4), payload.Length);
        payload.CopyTo(buf.AsSpan(5));
        lock (gate)
        {
            s.Write(buf, 0, buf.Length);
            s.Flush();
        }
    }

    /// <summary>Read one frame; null at end of stream.</summary>
    public static (byte Type, byte[] Payload)? Read(Stream s)
    {
        var head = new byte[5];
        if (!ReadExact(s, head)) return null;
        int len = BitConverter.ToInt32(head, 1);
        if (len < 0 || len > 16 * 1024 * 1024) return null;   // corrupt stream
        var payload = new byte[len];
        if (!ReadExact(s, payload)) return null;
        return (head[0], payload);
    }

    private static bool ReadExact(Stream s, byte[] buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = s.Read(buf, got, buf.Length - got);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }
}

/// <summary>
/// A PowerShell / CMD session started as Administrator. A pseudoconsole can't
/// be handed across the UAC boundary, so an elevated copy of Multi-SSH
/// (<see cref="ElevatedShellHost"/>) owns the ConPTY and relays its bytes
/// over a named pipe; this side looks like any other terminal backend.
/// </summary>
public class ElevatedShellConnection : ITerminalBackend
{
    private readonly SessionConfig _cfg;
    private readonly bool _elevate;
    private readonly object _writeGate = new();

    private NamedPipeServerStream? _pipe;
    private Process? _helper;
    private Thread? _readThread;
    private volatile bool _disposed;
    private volatile bool _running;
    private int _exitedRaised;

    public event Action<byte[]>? DataReceived;
    public event Action<string>? StatusChanged;
    public event Action<string>? Closed;
    public event Action? ShellExited;

    public bool IsConnected => _running;

    /// <param name="elevate">false only for tests: run the helper un-elevated.</param>
    public ElevatedShellConnection(SessionConfig cfg, bool elevate = true)
    {
        _cfg = cfg;
        _elevate = elevate;
    }

    public async Task ConnectAsync(int cols, int rows)
    {
        if (cols < 1) cols = 80;
        if (rows < 1) rows = 24;
        StatusChanged?.Invoke($"Starting {_cfg.LocalShellName} as Administrator … (approve the UAC prompt)");

        string name = "MultiSSH.Elevated." + Guid.NewGuid().ToString("N");
        _pipe = CreatePipe(name);

        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate Multi-SSH.exe"),
            Arguments = $"{ElevatedFrames.HelperArg} {name} {(int)_cfg.Kind} {cols} {rows} {Environment.ProcessId}",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (_elevate) psi.Verb = "runas";

        try
        {
            _helper = Process.Start(psi) ?? throw new InvalidOperationException("The elevated helper did not start.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)   // ERROR_CANCELLED
        {
            throw new OperationCanceledException("Administrator launch was cancelled at the UAC prompt.");
        }

        // Wait for the helper to dial in; give up if it dies first.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var connect = _pipe.WaitForConnectionAsync(cts.Token);
        var died = _helper.WaitForExitAsync(cts.Token);
        if (await Task.WhenAny(connect, died) != connect || !connect.IsCompletedSuccessfully)
        {
            cts.Cancel();
            throw new IOException("The elevated helper exited before connecting.");
        }

        // Only the process we just launched may drive this pipe.
        if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle.DangerousGetHandle(), out uint pid) ||
            pid != (uint)_helper.Id)
            throw new IOException("Unexpected process connected to the elevated shell pipe.");

        _running = true;
        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "elevated-read" };
        _readThread.Start();
        StatusChanged?.Invoke($"{_cfg.LocalShellName} (Admin) — local");
    }

    /// <summary>
    /// Only an elevated Administrators token can open this pipe, so ordinary
    /// (un-elevated) programs can't grab the connection meant for the helper.
    /// </summary>
    private NamedPipeServerStream CreatePipe(string name)
    {
        var sec = new PipeSecurity();
        var who = _elevate
            ? new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
            : WindowsIdentity.GetCurrent().User!;
        sec.AddAccessRule(new PipeAccessRule(who, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, sec);
    }

    private void ReadLoop()
    {
        try
        {
            while (!_disposed && _pipe != null)
            {
                var f = ElevatedFrames.Read(_pipe);
                if (f == null) break;
                if (f.Value.Type == ElevatedFrames.Data) DataReceived?.Invoke(f.Value.Payload);
                else if (f.Value.Type == ElevatedFrames.Exited) break;
            }
        }
        catch { /* pipe broken — same as the shell ending */ }

        _running = false;
        if (_disposed || Interlocked.Exchange(ref _exitedRaised, 1) == 1) return;
        StatusChanged?.Invoke($"{_cfg.LocalShellName} (Admin) exited");
        ShellExited?.Invoke();
    }

    public void Send(byte[] data)
    {
        var p = _pipe;
        if (p == null || !_running) return;
        try { ElevatedFrames.Write(p, _writeGate, ElevatedFrames.Data, data); }
        catch (Exception ex) { StatusChanged?.Invoke("Send failed: " + ex.Message); }
    }

    public void Send(string text) => Send(Encoding.UTF8.GetBytes(text));

    public void Resize(int cols, int rows)
    {
        var p = _pipe;
        if (p == null || !_running || cols < 1 || rows < 1) return;
        var b = new byte[4];
        BitConverter.TryWriteBytes(b.AsSpan(0, 2), (short)cols);
        BitConverter.TryWriteBytes(b.AsSpan(2, 2), (short)rows);
        try { ElevatedFrames.Write(p, _writeGate, ElevatedFrames.Resize, b); }
        catch { /* non-fatal */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        try
        {
            if (_pipe is { IsConnected: true } p)
                try { ElevatedFrames.Write(p, _writeGate, ElevatedFrames.Close, ReadOnlySpan<byte>.Empty); } catch { }
            _pipe?.Dispose();   // the helper also ends its shell when the pipe breaks
            _readThread?.Join(1000);
            _helper?.Dispose();
        }
        catch { /* ignore teardown errors */ }
        _pipe = null;
        Closed?.Invoke("Closed");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);
}

/// <summary>
/// The elevated side: <c>Multi-SSH.exe --elevated-shell &lt;pipe&gt; &lt;kind&gt; &lt;cols&gt; &lt;rows&gt; &lt;parentPid&gt;</c>.
/// Hosts the shell in a ConPTY and relays it to the app that launched it. It
/// never opens a window, and the shell ends as soon as the app's pipe goes away.
/// </summary>
public static class ElevatedShellHost
{
    public static int Run(string[] args)
    {
        // args[0] is HelperArg.
        if (args.Length < 6 ||
            !int.TryParse(args[2], out int kind) || !SessionConfig.CanRunAsAdmin((SessionKind)kind) ||
            !int.TryParse(args[3], out int cols) || !int.TryParse(args[4], out int rows) ||
            !int.TryParse(args[5], out int parentPid))
            return 2;

        // Asynchronous (overlapped): one thread blocks reading while another writes
        // shell output. On a synchronous handle Windows serializes the two and the
        // writes stall behind the pending read.
        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        try { pipe.Connect(15000); } catch { return 3; }

        // Refuse to serve an admin shell to anything but the Multi-SSH that asked for it.
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint serverPid) ||
            serverPid != (uint)parentPid || !SameExe(parentPid))
            return 4;

        var gate = new object();
        using var done = new ManualResetEventSlim();
        using var shell = new LocalShellConnection(new SessionConfig { Kind = (SessionKind)kind });
        shell.DataReceived += d => { try { ElevatedFrames.Write(pipe, gate, ElevatedFrames.Data, d); } catch { done.Set(); } };
        shell.ShellExited += () =>
        {
            try { ElevatedFrames.Write(pipe, gate, ElevatedFrames.Exited, ReadOnlySpan<byte>.Empty); } catch { }
            done.Set();
        };

        try { shell.ConnectAsync(cols, rows).GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            try
            {
                ElevatedFrames.Write(pipe, gate, ElevatedFrames.Data,
                    Encoding.UTF8.GetBytes($"\r\n\x1b[31m*** {ex.Message} ***\x1b[0m\r\n"));
                ElevatedFrames.Write(pipe, gate, ElevatedFrames.Exited, ReadOnlySpan<byte>.Empty);
            }
            catch { }
            return 5;
        }

        var reader = new Thread(() =>
        {
            try
            {
                while (ElevatedFrames.Read(pipe) is { } f)
                {
                    if (f.Type == ElevatedFrames.Data) shell.Send(f.Payload);
                    else if (f.Type == ElevatedFrames.Resize && f.Payload.Length == 4)
                        shell.Resize(BitConverter.ToInt16(f.Payload, 0), BitConverter.ToInt16(f.Payload, 2));
                    else if (f.Type == ElevatedFrames.Close) break;
                }
            }
            catch { }
            done.Set();   // app closed the session or went away: end the admin shell
        }) { IsBackground = true, Name = "elevated-host-read" };
        reader.Start();

        done.Wait();
        return 0;   // disposing the shell closes the pseudoconsole and ends the process
    }

    private static bool SameExe(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return string.Equals(p.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);
}
