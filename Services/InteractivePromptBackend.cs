using System.Collections.Concurrent;
using System.Text;
using MultiSSH.Models;

namespace MultiSSH.Services;

/// <summary>
/// Base for the file-transfer backends (SFTP / SCP / WebDAV) that present a
/// simple command prompt in the terminal. Handles line editing/echo and runs
/// each command on a worker thread; subclasses connect and execute commands.
/// </summary>
public abstract class InteractivePromptBackend : ITerminalBackend
{
    protected readonly SessionConfig Cfg;

    public event Action<byte[]>? DataReceived;
    public event Action<string>? StatusChanged;
    public event Action<string>? Closed;
    public event Action? ShellExited;

    private readonly StringBuilder _line = new();
    private readonly BlockingCollection<string> _queue = new();
    private readonly object _outLock = new();

    // Decoder for typed input: multi-byte UTF-8 chars (accents, CJK, emoji) arrive
    // one byte at a time, so we feed bytes through a stateful decoder rather than
    // casting each byte straight to a char (which would corrupt anything non-ASCII).
    private readonly System.Text.Decoder _inDecoder = Encoding.UTF8.GetDecoder();
    private readonly byte[] _inByte = new byte[1];
    private readonly char[] _inChars = new char[2];
    private Thread? _worker;
    private volatile bool _disposed;
    private bool _connected;

    protected InteractivePromptBackend(SessionConfig cfg) => Cfg = cfg;

    public bool IsConnected => _connected;

    // ---- subclass contract ----
    protected abstract void ConnectClient();            // throws on failure
    protected abstract void WriteWelcome();
    protected abstract string PromptText { get; }
    protected abstract void ExecuteCommand(string line);
    protected abstract void DisposeClient();

    protected string LocalDir { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // ---- ITerminalBackend ----

    public async Task ConnectAsync(int cols, int rows)
    {
        StatusChanged?.Invoke($"Connecting to {Cfg.Host}:{Cfg.Port} …");
        await Task.Run(ConnectClient);   // exceptions bubble to the connect loop
        if (_disposed)
        {
            // Closed while connecting: Dispose may have run before the client existed.
            try { DisposeClient(); } catch { /* best effort */ }
            throw new ObjectDisposedException(GetType().Name);
        }
        _connected = true;
        StatusChanged?.Invoke($"Connected — {SessionConfig.KindName(Cfg.Kind)} {Cfg.Username}@{Cfg.Host}");
        WriteWelcome();
        WritePrompt();

        _worker = new Thread(Worker) { IsBackground = true, Name = SessionConfig.KindName(Cfg.Kind) + "-worker" };
        _worker.Start();
    }

    private void Worker()
    {
        try
        {
            foreach (var cmd in _queue.GetConsumingEnumerable())
            {
                if (_disposed) break;
                var trimmed = cmd.Trim();
                if (trimmed.Length == 0) { WritePrompt(); continue; }
                try { ExecuteCommand(trimmed); }
                catch (Exception ex) { Line("error: " + ex.Message); }
                if (!_disposed) WritePrompt();
            }
        }
        catch { /* queue completed */ }
    }

    public void Send(byte[] data)
    {
        foreach (var b in data)
        {
            if (b is 0x0d or 0x0a)          // Enter
            {
                Output("\r\n");
                var cmd = _line.ToString();
                _line.Clear();
                if (IsExit(cmd)) { Line("bye"); ExitSession(); return; }
                _queue.Add(cmd);
            }
            else if (b is 0x7f or 0x08)     // Backspace / DEL
            {
                if (_line.Length > 0) { _line.Length--; Output("\b \b"); }
            }
            else if (b == 0x03)             // Ctrl+C
            {
                _line.Clear();
                Output("^C\r\n");
                WritePrompt();
            }
            else if (b >= 0x20)             // printable (possibly one byte of a UTF-8 char)
            {
                _inByte[0] = b;
                int n = _inDecoder.GetChars(_inByte, 0, 1, _inChars, 0);
                if (n > 0)                  // 0 while a multi-byte sequence is still incomplete
                {
                    var s = new string(_inChars, 0, n);
                    _line.Append(s);
                    Output(s);
                }
            }
        }
    }

    public void Send(string text) => Send(Encoding.UTF8.GetBytes(text));

    public void Resize(int cols, int rows) { /* no PTY */ }

    // ---- helpers for subclasses ----

    protected void Status(string s) => StatusChanged?.Invoke(s);
    protected void WritePrompt() => Output(PromptText);
    protected void Line(string s = "") => Output(s + "\r\n");

    protected void Output(string s)
    {
        if (_disposed || string.IsNullOrEmpty(s)) return;
        lock (_outLock)
            DataReceived?.Invoke(Encoding.UTF8.GetBytes(s));
    }

    protected virtual bool IsExit(string cmd)
    {
        var c = cmd.Trim().ToLowerInvariant();
        return c is "exit" or "quit" or "bye";
    }

    private void ExitSession()
    {
        _connected = false;
        Status("Disconnected");
        ShellExited?.Invoke();
    }

    /// <summary>Split a command line into whitespace-separated tokens, honoring "quotes".</summary>
    protected static List<string> Tokenize(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuote = false;
        foreach (char c in line)
        {
            if (c == '"') inQuote = !inQuote;
            else if (char.IsWhiteSpace(c) && !inQuote)
            {
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
            }
            else sb.Append(c);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connected = false;
        try { _queue.CompleteAdding(); } catch { }

        // Tear down on a background thread: never block the UI, and let an in-flight file
        // transfer on the worker finish before the client it is using gets disposed. The
        // old inline 500 ms join disposed the client mid-copy on any transfer longer than
        // that, truncating the downloaded/uploaded file. If the worker is idle the queue
        // completes and it exits at once; a running transfer gets a generous cap so normal
        // copies complete, then the client (and the queue) are disposed.
        var worker = _worker;
        Task.Run(() =>
        {
            try { worker?.Join(TimeSpan.FromSeconds(30)); } catch { }
            try { DisposeClient(); } catch { }
            try { _queue.Dispose(); } catch { }
        });

        Closed?.Invoke("Disconnected");
    }
}
